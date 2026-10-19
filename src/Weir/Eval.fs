module Weir.Eval

open Weir.Types
open Weir.Ast
open Weir.Argv
open Weir.Check
open System.Xml.Linq

let unreachable (why: string) : 'a = failwith $"unreachable: {why}"

/// what an int argument means to the builtin narrowing it: a position
/// (an index, a start) or an amount (a count, a size, a width)
type IntArg =
    | Position
    | Amount

/// the one int64 -> int32 narrowing for user ints [D:int-narrowing]:
/// `int` is 64-bit, .NET's indexes and counts are 32-bit; past that range
/// a position is out of range and an amount too large — never wrapped
let toIntArg (who: string) (role: IntArg) (n: int64) : int =
    if n >= int64 System.Int32.MinValue && n <= int64 System.Int32.MaxValue then
        int n
    else
        match role with
        | Position -> failwith $"{who}: index {n} is out of range"
        | Amount when n > 0L -> failwith $"{who}: {n} is too large (at most {System.Int32.MaxValue})"
        | Amount -> failwith $"{who}: {n} is out of range"

// Exit.code's carrier, declared in Session (the teardown raises it too)
exception ExitRequest = Session.ExitRequest

// where a runtime raise happened [D:runtime-position]: the node being
// evaluated, per thread. The runner maps a node to file:line:col and
// installs the same-statement test: a closure whose body is in ANOTHER
// statement (a function defined elsewhere, or in a module) is a CALL, and
// a raise unwinding through it records the call site as a trace frame; an
// inline lambda or a `for` body is not. Without the runner's test (REPL,
// tests) nothing is a call.
type RunPos() =
    [<System.ThreadStatic; DefaultValue>]
    static val mutable private current: obj

    static member Current
        with get () = RunPos.current
        and set (v: obj) = RunPos.current <- v

    [<System.ThreadStatic; DefaultValue>]
    static val mutable private exact: bool

    /// Current is an application's callee while the call runs: a raise
    /// there is reported at its own column [D:runtime-position-call]
    static member Exact
        with get () = RunPos.exact
        and set (v: bool) = RunPos.exact <- v

    static member val SameStatement: System.Func<obj, bool> = null with get, set

    static member IsCall(body: obj) =
        let f = RunPos.SameStatement
        not (isNull f) && not (f.Invoke body)

/// a raise's provenance: the node it is blamed on (a command at its own
/// column, or the innermost node evaluated, line only) and the call sites
/// it unwound through, innermost first
type Blame() =
    member val Node: obj = null with get, set
    member val Exact = false with get, set
    member val Frames = System.Collections.Generic.List<obj>()

// kept beside the exception, which travels unchanged, so type-matching
// catchers (the REPL's CommandFailure) still see it; first blame wins
let private blameTable = System.Runtime.CompilerServices.ConditionalWeakTable<exn, Blame>()

let blameOf (ex: exn) : Blame option =
    match blameTable.TryGetValue ex with
    | true, b -> Some b
    | _ -> None

let private blameFor (ex: exn) : Blame =
    match blameTable.TryGetValue ex with
    | true, b -> b
    | _ ->
        let b = Blame()
        blameTable.Add(ex, b)
        b

// a seq whose raise (other than exit) is reported to `onRaise` on the
// thread that pulled it, then rethrown unchanged
let private tapRaise (onRaise: exn -> unit) (inner: seq<'a>) : seq<'a> =
    { new System.Collections.Generic.IEnumerable<'a> with
        member _.GetEnumerator() : System.Collections.Generic.IEnumerator<'a> =
            let e = inner.GetEnumerator()

            { new System.Collections.Generic.IEnumerator<'a> with
                member _.Current = e.Current
              interface System.Collections.IEnumerator with
                  member _.Current = box e.Current

                  member _.MoveNext() =
                      try
                          e.MoveNext()
                      with
                      | ExitRequest _ -> reraise ()
                      | ex ->
                          onRaise ex
                          reraise ()

                  member _.Reset() = e.Reset()
              interface System.IDisposable with
                  member _.Dispose() = e.Dispose() }
      interface System.Collections.IEnumerable with
          member this.GetEnumerator() =
              ((this :?> System.Collections.Generic.IEnumerable<'a>).GetEnumerator()) :> System.Collections.IEnumerator }

// a command's failure surfaces where its output is READ, maybe statements
// later: the seq blames the command that produced it
let private blameOn (node: obj) (inner: seq<'a>) : seq<'a> =
    if isNull node then
        inner
    else
        inner
        |> tapRaise (fun ex ->
            let b = blameFor ex

            if isNull b.Node then
                b.Node <- node
                b.Exact <- true)

/// pin a raise to the node THIS thread was evaluating [D:runtime-position]:
/// positions are per thread, so a raise caught on a worker (a parallel
/// arm, a command's stdin writer) and rethrown on the main thread would
/// otherwise be blamed on the main thread's position
let pinOnThread (ex: exn) : unit =
    match ex with
    | ExitRequest _ -> ()
    | _ ->
        let b = blameFor ex

        if isNull b.Node then
            b.Node <- RunPos.Current
            b.Exact <- RunPos.Exact

/// a command's stdin feed, pinned where the producer raised: the writer
/// thread pulls it, the main thread rethrows the raise
let pinnedFeed (inner: seq<string>) : seq<string> = tapRaise pinOnThread inner

// a scoped process handle [D:scoped-procs]: the live child plus its
// spill paths; identity is the process itself (reference equality,
// pid hash), and the scope that bound it owns the lifetime
[<ReferenceEquality>]
type ProcHandle =
    { Proc: System.Diagnostics.Process
      OutPath: string
      ErrPath: string
      SpillDir: string
      // joins the spill pumps (bounded); call before any read that
      // must see the child's final output [D:scoped-procs]
      Drain: unit -> unit }

// how many container levels Value.GetHashCode mixes [D:value-hash]
[<Literal>]
let private hashDepth = 4

[<CustomEquality; NoComparison>]
type Value =
    | VInt of int64
    | VFloat of float
    | VDur of ms: int64
    | VInstant of ms: int64
    | VUuid of System.Guid
    | VSize of bytes: int64
    // the non-text value [D:bytes]: every renderer shows a summary,
    // never the content (raw bytes wreck terminals); Bytes.toBase64 is
    // the deliberate conversion to text
    | VBytes of byte[]
    | VStr of string
    // a Secret wraps a plain string [D:secret]; the renderers show *** —
    // Secret.reveal is the only unwrap
    | VSecret of string
    | VBool of bool
    | VUnit
    // fields in declaration order [D:record-order] (wire order for an
    // anonymous shape read at a boundary): the ordered list is the
    // container itself, so order holds by construction with no
    // parallel invariant; the Map it replaced was 6-23x slower to
    // build and bought nothing at record widths. Equality is
    // order-insensitive, implemented once in the custom arm below.
    | VRecord of record: string * fields: (string * Value) list
    | VUnion of case: string * payload: Value option
    | VSeq of items: seq<Value>
    // string-keyed only [D:map-string]: every receipt has string keys
    // (JSON object keys are strings), and int keys would make Map the
    // first Ord-constrained container — widened only on a receipt
    | VMap of entries: Map<string, Value>
    | VTuple of items: Value list
    | VClosure of param: string * body: TypedExpr * env: Env
    | VClosurePat of binder: Pattern * body: TypedExpr * env: Env
    | VBuiltin of (Value -> Value)
    | VProc of handle: ProcHandle
    // a scoped listener handle [D:http-serve]: the live socket;
    // identity is the listener (reference equality), and the
    // within-serve scope owns the lifetime, closing it on every exit
    | VServer of handle: Serve.Handle

    override this.Equals(other) =
        match other with
        | :? Value as top ->
            // value equality is iterative [D:eq-depth]: a legally-built
            // recursive-record value (an Option-linked record folded
            // 100k deep, which the checker accepts) once crashed the
            // process with an uncatchable StackOverflow because the
            // walk recursed one frame per nesting level. The
            // pending-pair work-list lives on the heap instead:
            // children are queued, a mismatch ends the loop. The
            // per-arm comparison is unchanged; only the recursion is
            // gone.
            let pending = System.Collections.Generic.Stack<Value * Value>()
            pending.Push(this, top)
            let mutable eq = true

            while eq && pending.Count > 0 do
                let a, b = pending.Pop()

                match a, b with
                | VInt a, VInt b -> eq <- a = b
                // finite-only and -0.0-normalized [D:floats]: reflexive
                | VFloat a, VFloat b -> eq <- a = b
                | VDur a, VDur b -> eq <- a = b
                | VInstant a, VInstant b -> eq <- a = b
                | VUuid a, VUuid b -> eq <- a = b
                | VSize a, VSize b -> eq <- a = b
                // F# array equality is structural, so this is byte equality
                | VBytes a, VBytes b -> eq <- a = b
                | VStr a, VStr b -> eq <- a = b
                | VSecret a, VSecret b -> eq <- a = b
                | VBool a, VBool b -> eq <- a = b
                | VUnit, VUnit -> ()
                | VRecord(n1, f1), VRecord(n2, f2) ->
                    // order-insensitive [D:record-order]: order is
                    // carried but never semantic, so two spellings of
                    // one record are equal; matched field values queue
                    // as pending pairs
                    if n1 = n2 && f1.Length = f2.Length then
                        for (k, v) in f1 do
                            match f2 |> List.tryFind (fun (k2, _) -> k2 = k) with
                            | Some(_, v2) -> pending.Push(v, v2)
                            | None -> eq <- false
                    else
                        eq <- false
                | VUnion(c1, p1), VUnion(c2, p2) ->
                    if c1 = c2 then
                        match p1, p2 with
                        | Some x, Some y -> pending.Push(x, y)
                        | None, None -> ()
                        | _ -> eq <- false
                    else
                        eq <- false
                | VSeq a, VSeq b ->
                    // lockstep via enumerators — neither side is
                    // materialized; short-circuit at the first mismatch
                    // (as Seq.equal does). Element pairs queue.
                    if not (obj.ReferenceEquals(a, b)) then
                        use ea = a.GetEnumerator()
                        use eb = b.GetEnumerator()
                        let mutable go = true

                        while go do
                            let na = ea.MoveNext()
                            let nb = eb.MoveNext()

                            if na && nb then pending.Push(ea.Current, eb.Current)
                            elif na <> nb then
                                eq <- false
                                go <- false
                            else
                                go <- false
                | VMap a, VMap b ->
                    // same key set; entry values queue (keys are
                    // strings, compared by the Map itself) [D:map-string]
                    if a.Count = b.Count then
                        for kv in a do
                            match b.TryFind kv.Key with
                            | Some v2 -> pending.Push(kv.Value, v2)
                            | None -> eq <- false
                    else
                        eq <- false
                | VTuple a, VTuple b ->
                    if a.Length = b.Length then
                        List.iter2 (fun x y -> pending.Push(x, y)) a b
                    else
                        eq <- false
                | VClosure(p1, b1, e1), VClosure(p2, b2, e2) -> eq <- p1 = p2 && b1 = b2 && obj.ReferenceEquals(e1, e2)
                | VClosurePat(p1, b1, e1), VClosurePat(p2, b2, e2) ->
                    eq <- p1 = p2 && b1 = b2 && obj.ReferenceEquals(e1, e2)
                | VBuiltin f, VBuiltin g -> eq <- obj.ReferenceEquals(f, g)
                | VProc a, VProc b -> eq <- obj.ReferenceEquals(a.Proc, b.Proc)
                | VServer a, VServer b -> eq <- obj.ReferenceEquals(a.Server, b.Server)
                | _ -> eq <- false

            eq
        | _ -> false

    // consistent with Equals above and depth-bounded [D:value-hash]
    // [D:eq-depth]: containers mix their children's hashes down to
    // hashDepth levels, so a seq of tuples or same-type records spreads
    // across buckets instead of all colliding on one length or name
    override this.GetHashCode() =
        let inline mix (h: int) (x: int) = (h * 31) + x

        let rec go (depth: int) (v: Value) : int =
            let child (x: Value) = if depth > 0 then go (depth - 1) x else 0

            match v with
            | VInt n -> hash n
            | VFloat f -> hash f
            | VDur n -> hash ("dur", n)
            | VInstant n -> hash ("instant", n)
            | VUuid g -> hash ("uuid", g)
            | VSize b -> hash ("size", b)
            | VBytes b -> hash ("bytes", b.Length)
            | VStr s -> hash s
            | VSecret s -> hash ("secret", s)
            | VBool b -> hash b
            | VUnit -> 17
            // order-insensitive like record equality [D:record-order]:
            // the field mix is a commutative sum
            | VRecord(n, fields) -> fields |> List.fold (fun h (k, x) -> h + mix (hash k) (child x)) (hash n)
            | VUnion(c, p) ->
                match p with
                | Some x -> mix (hash c) (child x)
                | None -> hash c
            | VSeq _ -> 0
            | VMap m -> m |> Map.fold (fun h k x -> h + mix (hash k) (child x)) (hash ("map", m.Count))
            | VTuple items -> items |> List.fold (fun h x -> mix h (child x)) (List.length items)
            | VClosure(p, _, _) -> hash p
            | VClosurePat(p, _, _) -> hash p
            | VBuiltin f -> LanguagePrimitives.PhysicalHash f
            | VProc h -> hash ("proc", h.Proc.Id)
            | VServer h -> hash ("server", h.Port)

        go hashDepth this

and Env = Map<string, Value>

// One renderer, limits threaded [D:repl-echo]: show keeps its shipped
// constants byte-identical (20 items, "; ...", unclipped strings); the
// REPL echo runs the same core tighter (10, "; …", 120-char clip,
// depth bound). Forcing is bounded at MaxItems+1 per level either way.
type private RenderLimits =
    { MaxItems: int
      MaxStr: int option
      MaxDepth: int
      Ellipsis: string }

let private showLimits =
    // depth-bounded like the equality walk [D:eq-depth]: a legally-built
    // deeply-nested value (an Option-linked record folded 100k deep)
    // once overflowed the stack through this recursive renderer too —
    // show / interpolation / a to-json error all reach formatValue. 100
    // clears every real value (the corpus max is ~11); past it the
    // guard renders an ellipsis rather than crashing.
    { MaxItems = 20
      MaxStr = None
      MaxDepth = 100
      Ellipsis = "; ..." }

let private echoLimits =
    { MaxItems = 10
      MaxStr = Some 120
      MaxDepth = 8
      Ellipsis = "; …" }

// ordered-field access [D:record-order]: a linear scan — record widths
// are single digits and the scan beat the Map it replaced; recSet
// replaces in place, so a copy-and-update keeps the field's position
let recTryGet (name: string) (fields: (string * Value) list) : Value option =
    fields |> List.tryPick (fun (k, v) -> if k = name then Some v else None)

let recGet (name: string) (fields: (string * Value) list) : Value =
    match recTryGet name fields with
    | Some v -> v
    | None -> failwith $"unreachable: record field '{name}' missing"

let recSet (name: string) (value: Value) (fields: (string * Value) list) : (string * Value) list =
    fields |> List.map (fun (k, v) -> if k = name then (k, value) else (k, v))

let rec private formatWith (lim: RenderLimits) (depth: int) (v: Value) : string =
    if depth > lim.MaxDepth then
        "…"
    else
        let sub = formatWith lim (depth + 1)

        match v with
        | VInt n -> string n
        | VFloat f -> formatFloat f
        | VDur n -> formatDuration n
        | VInstant n -> formatInstant n
        | VUuid g -> formatUuid g
        | VSize b -> formatSize b
        // a summary, never content [D:bytes]: raw bytes must not reach
        // a terminal; Bytes.toBase64 is the deliberate conversion
        | VBytes b -> $"<{formatSize (int64 b.Length)}>"
        // always *** [D:secret]; because this is the one recursive
        // renderer, a Secret inside a shown record / union / tuple /
        // seq renders *** too (sub calls back here)
        | VSecret _ -> "***"
        | VStr s ->
            let raw, clipped =
                match lim.MaxStr with
                | Some m when CodePoints.length s > m -> CodePoints.head s m, true
                | _ -> s, false

            let escaped =
                raw
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r") // [D:tty-echo-strict]
                    .Replace("\t", "\\t")

            let tail = if clipped then "…" else ""
            $"\"{escaped}{tail}\""
        | VBool true -> "true"
        | VBool false -> "false"
        | VRecord(_, fields) ->
            let body = fields |> Seq.map (fun (k, v) -> $"{k} = {sub v}") |> String.concat "; "

            "{ " + body + " }"
        | VUnion(case, None) -> case
        | VUnion(case, Some payload) ->
            let inner = sub payload

            match payload with
            | VInt _
            | VStr _
            | VBool _
            // a tuple renders its own parens: `Some (1, 2)`, never `Some ((1, 2))`
            | VTuple _ -> $"{case} {inner}"
            | _ -> $"{case} ({inner})"
        | VMap entries ->
            // sorted by construction (F# Map iterates in key order), so
            // output is deterministic; keys render through the string
            // arm (escaped), values recurse (Map<string, Secret> masks
            // for free) [D:map-string]
            let shown = entries |> Seq.truncate (lim.MaxItems + 1) |> List.ofSeq

            let body =
                shown
                |> List.truncate lim.MaxItems
                |> List.map (fun kv -> $"({sub (VStr kv.Key)}, {sub kv.Value})")
                |> String.concat "; "

            let ellipsis = if shown.Length > lim.MaxItems then lim.Ellipsis else ""
            "map [" + body + ellipsis + "]"
        | VSeq items ->
            let shown = items |> Seq.truncate (lim.MaxItems + 1) |> List.ofSeq

            let body = shown |> List.truncate lim.MaxItems |> List.map sub |> String.concat "; "

            let ellipsis = if shown.Length > lim.MaxItems then lim.Ellipsis else ""
            $"[{body}{ellipsis}]"
        | VClosure _ -> "<fun>"
        | VClosurePat _ -> "<fun>"
        | VBuiltin _ -> "<builtin>"
        | VProc h ->
            // queryable after the scope killed it (Kill leaves HasExited
            // readable); the guard is for a disposed handle only
            let state =
                try
                    if h.Proc.HasExited then
                        $"exited {h.Proc.ExitCode}"
                    else
                        "running"
                with _ ->
                    "exited"

            $"proc(pid={h.Proc.Id}, {state})"
        | VServer h ->
            let state = if h.Closed then "closed" else "listening"
            $"server(port={h.Port}, {state})"
        | VUnit -> "()"
        | VTuple items -> "(" + (items |> List.map sub |> String.concat ", ") + ")"

let formatValue (v: Value) : string = formatWith showLimits 0 v

// ---- plan/apply capture [D:plan-apply] ------------------------------
// A `plan` block runs its body but captures external mutations as Op
// values instead of performing them. The interpreter holds the running
// Value at each mutation builtin, so the interception is a thread-local
// capture stack the mutation builtins consult: when active, append an
// Op and return VUnit; ambient reads (fs.read/env/clock/query-net)
// ignore it and run. Nested plans compose — each pushes its own frame
// and the top frame captures. The stack is per-thread so pmap arms
// cannot cross-contaminate captures. Op is a `VUnion(case, payload)`
// value (the prelude `Op` type), so equality/show/masking ride the
// existing Value machinery [D:secret].
// one node of the guard's component tries [D:plan-guard-linear]: the
// target index keys a path's components (Target: a target here; Below:
// a target under it), the link cache keys the components as written
// (Resolved: where a link at that prefix leads, null for no link)
[<AllowNullLiteral>]
type private GuardNode(comparer: System.Collections.Generic.IEqualityComparer<string>) =
    let mutable kids: System.Collections.Generic.Dictionary<string, GuardNode> = null

    /// the child on edge `k`, or null
    member _.Find(k: string) : GuardNode =
        if isNull kids then
            null
        else
            match kids.TryGetValue k with
            | true, n -> n
            | _ -> null

    member _.Add(k: string, n: GuardNode) =
        if isNull kids then
            kids <- System.Collections.Generic.Dictionary(comparer)

        kids[k] <- n

    member _.IsEmpty = isNull kids

    member val Target: string = null with get, set
    member val Below: string = null with get, set
    member val Resolved: string = null with get, set

// one node of a plan's simulated filesystem [D:plan-preconditions]: what
// the captured ops have made of a path — State 0 unknown (the layer
// below decides), 1 file, 2 directory, 3 absent; Cleared: nothing below
// it in the layer beneath counts any more (deleted, overwritten, a fresh
// copy); Mirror: the path whose layer-beneath contents back this
// subtree (a copied or moved directory)
[<AllowNullLiteral>]
type private SimNode(comparer: System.Collections.Generic.IEqualityComparer<string>) =
    let mutable kids: System.Collections.Generic.Dictionary<string, SimNode> = null

    member _.Find(k: string) : SimNode =
        if isNull kids then
            null
        else
            match kids.TryGetValue k with
            | true, n -> n
            | _ -> null

    member _.Add(k: string, n: SimNode) =
        if isNull kids then
            kids <- System.Collections.Generic.Dictionary(comparer)

        kids[k] <- n

    member _.Clear() = kids <- null

    member _.Names: string list =
        if isNull kids then [] else List.ofSeq kids.Keys

    member val State = 0 with get, set
    member val Cleared = false with get, set
    member val Mirror: string = null with get, set

    member this.Clone() : SimNode =
        let n = SimNode(comparer, State = this.State, Cleared = this.Cleared, Mirror = this.Mirror)

        for k in this.Names do
            n.Add(k, this.Find(k).Clone())

        n

type private PlanFrame =
    { Ops: ResizeArray<Value>
      // the filesystem as the captured ops leave it, over the real one
      // (or an enclosing plan's) [D:plan-preconditions]
      Sim: SimNode
      // the target paths captured mutations change [D:plan-apply]: a
      // later read at, under, or — for a listing — above one is
      // known-after-apply and refused [D:plan-stale-reads]. Indexed
      // [D:plan-guard-index] as a component trie [D:plan-guard-linear]:
      // each target's comparison keys (as written, and through symlinks
      // [D:plan-guard-links]) mark their node with the path a refusal
      // names and every ancestor node with a target below, so a read
      // costs one walk of its components, never a scan of the captured
      // ops. Session-resolved so a relative read matches an earlier
      // relative write.
      Index: GuardNode
      // the link cache [D:plan-guard-linear]: every prefix is resolved
      // once per plan
      Links: GuardNode
      // the file identities in-place writes target [D:plan-guard-inode]:
      // (device, inode) — the Windows file index — to the path a
      // refusal names
      Files: System.Collections.Generic.Dictionary<struct (uint64 * uint64), string>
      // `within tmp` dirs opened inside this plan [D:plan-within-tmp]:
      // deleted when their scope closes, before any apply
      TmpRoots: ResizeArray<string> }

// a file's identity [D:plan-guard-inode]: (device, inode) through
// statx on Linux (one layout on every architecture) and stat on macOS
// (the 64-bit-inode entry point on x64), (volume serial, file index)
// through GetFileInformationByHandle on Windows; None when the path is
// absent or unreadable. A final link is followed, as a read follows it
module private FileId =
    open System.Runtime.InteropServices

    [<DllImport("libc", SetLastError = true)>]
    extern int private statx(int dirfd, string path, int flags, uint mask, byte[] buf)

    [<DllImport("libc", EntryPoint = "stat", SetLastError = true)>]
    extern int private statMac(string path, byte[] buf)

    [<DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)>]
    extern int private statMacX64(string path, byte[] buf)

    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern nativeint private CreateFileW(
        string name,
        uint access,
        uint share,
        nativeint security,
        uint disposition,
        uint flags,
        nativeint template)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GetFileInformationByHandle(nativeint handle, byte[] info)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private CloseHandle(nativeint handle)

    let private AT_FDCWD = -100
    let private STATX_INO = 0x100u

    let private u32 (b: byte[]) (at: int) = uint64 (System.BitConverter.ToUInt32(b, at))
    let private u64 (b: byte[]) (at: int) = System.BitConverter.ToUInt64(b, at)

    let ofPath (path: string) : struct (uint64 * uint64) voption =
        try
            if System.OperatingSystem.IsWindows() then
                // no access, every share mode, OPEN_EXISTING, BACKUP_SEMANTICS
                let h = CreateFileW(path, 0u, 7u, 0n, 3u, 0x02000000u, 0n)

                if h = -1n then
                    ValueNone
                else
                    try
                        let info = Array.zeroCreate<byte> 52

                        if GetFileInformationByHandle(h, info) then
                            ValueSome(struct (u32 info 28, (u32 info 44 <<< 32) ||| u32 info 48))
                        else
                            ValueNone
                    finally
                        CloseHandle h |> ignore
            elif System.OperatingSystem.IsMacOS() then
                let buf = Array.zeroCreate<byte> 256

                let rc =
                    if RuntimeInformation.ProcessArchitecture = Architecture.X64 then
                        statMacX64 (path, buf)
                    else
                        statMac (path, buf)

                if rc = 0 then ValueSome(struct (u32 buf 0, u64 buf 8)) else ValueNone
            else
                let buf = Array.zeroCreate<byte> 256

                if statx (AT_FDCWD, path, 0, STATX_INO, buf) = 0 then
                    ValueSome(struct ((u32 buf 136 <<< 32) ||| u32 buf 140, u64 buf 32))
                else
                    ValueNone
        with _ ->
            ValueNone

module PlanMode =
    let private stack = new System.Threading.ThreadLocal<PlanFrame list>(fun () -> [])

    /// is a plan currently capturing on this thread?
    let active () = not (List.isEmpty stack.Value)

    let private norm (p: string) =
        let t = System.IO.Path.TrimEndingDirectorySeparator p
        if t = "" then p else t

    /// `p` equals `root` or lies below it, segment-wise. On Windows both
    /// separators count and case folds — the filesystem's own rules, so
    /// `C:\a/B` is under `c:\A` [D:plan-guard-windows]
    let pathWithinOn (windows: bool) (root: string) (p: string) =
        let key (s: string) =
            let s = if windows then s.Replace('/', '\\') else s
            let sep = if windows then '\\' else '/'
            if s.Length > 1 && s[s.Length - 1] = sep then s.TrimEnd sep else s

        let cmp =
            if windows then System.StringComparison.OrdinalIgnoreCase
            else System.StringComparison.Ordinal

        let r, p = key root, key p
        let sep = if windows then "\\" else "/"

        System.String.Equals(p, r, cmp)
        || p.StartsWith((if r.EndsWith sep then r else r + sep), cmp)

    let private windows = System.OperatingSystem.IsWindows()

    let private within (root: string) (p: string) = pathWithinOn windows root p

    let private sep = if windows then '\\' else '/'

    // case folds where the filesystem does [D:plan-guard-links]: Windows,
    // and macOS, whose default volumes are case-insensitive — a
    // case-sensitive macOS volume over-refuses, it never misses
    let private comparer =
        if windows || System.OperatingSystem.IsMacOS() then
            System.StringComparer.OrdinalIgnoreCase
        else
            System.StringComparer.Ordinal

    // the comparison key: one separator spelling, no trailing separator
    let private keyOf (p: string) =
        let s = if windows then p.Replace('/', '\\') else p
        if s.Length > 1 && s[s.Length - 1] = sep then s.TrimEnd sep else s

    // a key's components, the trie's edges: the root separator is the
    // empty first component, so `/` is [""] and `/a` is [""; "a"]
    let private parts (k: string) : string[] =
        if k = string sep then [| "" |] else k.Split sep

    // the symlink a path names, or None (absent, not a link, unreadable)
    let private linkOf (p: string) : string option =
        try
            match System.IO.FileInfo(p).LinkTarget with
            | null -> None
            | t -> Some t
        with _ ->
            None

    /// `p` (absolute) with the symlinks among its existing components
    /// resolved [D:plan-guard-links]; components that do not exist stay
    /// as written. `followLeaf = false` keeps the last component as is
    /// — a delete or move acts on a link itself. A loop stops resolving
    /// after 40 links (the kernel's own bound). Each prefix as written
    /// is resolved once per plan, through `cache` [D:plan-guard-linear]
    /// — a path costs one lookup per component, a lstat only for the
    /// components no earlier path reached
    let private resolveLinks (cache: GuardNode) (followLeaf: bool) (p: string) : string =
        let split (s: string) =
            s.Split([| '/'; sep |], System.StringSplitOptions.RemoveEmptyEntries)

        let hops = ref 0

        let rec walk (root: string) (comps: string[]) (followLast: bool) : string =
            let mutable node =
                match cache.Find root with
                | null ->
                    let n = GuardNode(System.StringComparer.Ordinal, Resolved = root)
                    cache.Add(root, n)
                    n
                | n -> n

            // the walk so far is `baseR` plus comps[from .. i-1]: a node
            // that is no link keeps no string, so a cached walk builds
            // one path, not one per component
            let mutable baseR = node.Resolved
            let mutable from = 0

            let upTo (i: int) =
                if i <= from then
                    baseR
                else
                    System.IO.Path.Combine(Array.append [| baseR |] comps[from .. i - 1])

            for i in 0 .. comps.Length - 1 do
                let part = comps[i]

                if i < comps.Length - 1 || followLast then
                    match node.Find part with
                    | null ->
                        let parent = upTo i

                        let resolved =
                            match linkOf (System.IO.Path.Combine(parent, part)) with
                            | Some target when hops.Value < 40 ->
                                hops.Value <- hops.Value + 1
                                let full = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent, target))
                                let r = System.IO.Path.GetPathRoot full
                                walk r (split (full.Substring r.Length)) true
                            | _ -> null

                        let n = GuardNode(System.StringComparer.Ordinal, Resolved = resolved)
                        node.Add(part, n)
                        node <- n

                        if not (isNull resolved) then
                            baseR <- resolved
                            from <- i + 1
                    | n ->
                        node <- n

                        if not (isNull n.Resolved) then
                            baseR <- n.Resolved
                            from <- i + 1

            upTo comps.Length

        let full = System.IO.Path.GetFullPath p
        let root = System.IO.Path.GetPathRoot full
        walk root (split (full.Substring root.Length)) followLeaf

    // the keys a path compares under: as written, its directory resolved,
    // and — for an op or read that follows a final link — fully resolved
    let private keysOf (cache: GuardNode) (followLeaf: bool) (p: string) : string list =
        let lexical = keyOf (norm p)

        let resolved =
            try
                [ keyOf (resolveLinks cache false p)
                  if followLeaf then
                      keyOf (resolveLinks cache true p) ]
            with _ ->
                []

        List.distinct (lexical :: resolved)

    // the outermost frame's link cache — one per plan nest
    let private linkCache () =
        match List.tryLast stack.Value with
        | Some f -> f.Links
        | None -> unreachable "PlanMode.linkCache with no active frame"

    // mark a key's node with its target and every ancestor node with a
    // target below [D:plan-guard-linear]; the first target recorded wins
    let private index (frame: PlanFrame) (k: string) (shown: string) =
        let comps = parts k
        let mutable node = frame.Index

        for i in 0 .. comps.Length - 1 do
            node <-
                match node.Find comps[i] with
                | null ->
                    let n = GuardNode(comparer)
                    node.Add(comps[i], n)
                    n
                | n -> n

            if i < comps.Length - 1 then
                if isNull node.Below then
                    node.Below <- shown
            elif isNull node.Target then
                node.Target <- shown

    /// run f while a fresh capture frame is on top; returns the captured
    /// Ops in capture order (the frame is popped on every exit)
    let capturing (f: unit -> unit) : Value list =
        let frame =
            { Ops = ResizeArray()
              Sim = SimNode(comparer)
              Index = GuardNode(comparer)
              Links = GuardNode(System.StringComparer.Ordinal)
              Files = System.Collections.Generic.Dictionary()
              TmpRoots = ResizeArray() }

        stack.Value <- frame :: stack.Value
        // mirror the frame push into Session's thread-local guard so the
        // process spawn point (compiled before Eval) can refuse an
        // indirect proc that firstPlanRefusal cannot follow (a callable
        // passed as a value) [D:plan-proc-runtime-guard]
        Session.enterPlanGuard ()

        try
            f ()
        finally
            stack.Value <-
                match stack.Value with
                | _ :: rest -> rest
                | [] -> []

            Session.exitPlanGuard ()

        List.ofSeq frame.Ops

    /// a `within tmp` dir opened while planning [D:plan-within-tmp]:
    /// every open frame records it — the dir is gone before any of
    /// those plans can apply
    let noteTmp (dir: string) =
        for frame in stack.Value do
            frame.TmpRoots.Add(norm dir)

    // ---- the simulated filesystem [D:plan-preconditions] ----------------
    // a captured op checks its preconditions against the state the
    // plan's earlier ops leave, exactly as the direct call checks the
    // real filesystem — preview == apply. Kinds: 0 absent, 1 file,
    // 2 directory, 4 another entry (a dangling link)

    /// the real filesystem's kind of a path
    let realKind (p: string) : int =
        if System.IO.File.Exists p then 1
        elif System.IO.Directory.Exists p then 2
        elif not (isNull (System.IO.FileInfo(p).LinkTarget)) then 4
        else 0

    let private compsOf (p: string) = parts (keyOf (norm p))

    let private joinComps (comps: string[]) =
        if comps = [| "" |] then string sep else System.String.Join(string sep, comps)

    // one layer's view of a path: the state its ops gave it (0 = the
    // layer below decides), the overlay node there (or null), and the
    // path to ask the layer below (null when nothing below counts)
    let private viewIn (root: SimNode) (p: string) : struct (int * SimNode * string) =
        let comps = compsOf p
        let mutable node = root
        let mutable basePath: string = null
        let mutable baseFrom = 0
        let mutable cleared = false
        let mutable result = ValueNone
        let mutable i = 0

        let below () =
            if cleared then
                null
            elif isNull basePath then
                joinComps comps
            elif baseFrom >= comps.Length then
                basePath
            else
                System.IO.Path.Combine(Array.append [| basePath |] comps[baseFrom..])

        while result.IsNone && i < comps.Length do
            let child = node.Find comps[i]
            let last = i = comps.Length - 1

            if isNull child then
                result <- ValueSome(struct ((if cleared then 3 else 0), null, below ()))
            else
                node <- child

                if child.State = 3 || (child.State = 1 && not last) then
                    result <- ValueSome(struct (3, null, null))
                elif child.State = 1 then
                    result <- ValueSome(struct (1, child, null))
                else
                    if not (isNull child.Mirror) then
                        basePath <- child.Mirror
                        baseFrom <- i + 1
                        cleared <- false
                    elif child.Cleared then
                        cleared <- true

                    if last then
                        let st = if child.State = 2 then 2 elif cleared then 3 else 0
                        result <- ValueSome(struct (st, child, below ()))

            i <- i + 1

        match result with
        | ValueSome r -> r
        | ValueNone -> struct (0, null, p)

    // `real` answers for a path no layer decided — the full kind, or
    // only as much of it as the caller asks (one stat for "a directory?")
    let rec private kindWith (real: string -> int) (frames: PlanFrame list) (p: string) : int =
        match frames with
        | [] -> real p
        | f :: outer ->
            match viewIn f.Sim p with
            | struct (3, _, _) -> 0
            | struct (0, _, below) when not (isNull below) -> kindWith real outer below
            | struct (0, _, _) -> 0
            | struct (k, _, _) -> k

    let private kindIn frames p = kindWith realKind frames p

    // the real path a view ends at, for the checks only the real
    // filesystem answers (a dangling link); None when an op made it
    let rec private realPathIn (frames: PlanFrame list) (p: string) : string option =
        match frames with
        | [] -> Some p
        | f :: outer ->
            match viewIn f.Sim p with
            | struct (0, _, below) when not (isNull below) -> realPathIn outer below
            | _ -> None

    // the entry names a directory holds, before asking each one's kind
    let rec private namesIn (frames: PlanFrame list) (p: string) : string list =
        match frames with
        | [] ->
            try
                System.IO.Directory.EnumerateFileSystemEntries p
                |> Seq.map System.IO.Path.GetFileName
                |> List.ofSeq
            with _ ->
                []
        | f :: outer ->
            let struct (_, node, below) = viewIn f.Sim p
            let mine = if isNull node then [] else node.Names
            let theirs = if isNull below then [] else namesIn outer below
            List.distinct (mine @ theirs)

    /// the plan-time kind of a path (0 absent, 1 file, 2 dir, 4 other)
    let simKind (p: string) : int = kindIn stack.Value p

    /// is the path a directory at plan time?
    let simIsDir (p: string) : bool =
        kindWith (fun p -> if System.IO.Directory.Exists p then 2 else 0) stack.Value p = 2

    /// does the plan-time directory hold an entry?
    let simEmptyDir (p: string) : bool =
        namesIn stack.Value p
        |> List.forall (fun n -> kindIn stack.Value (System.IO.Path.Combine(p, n)) = 0)

    /// a dangling link at plan time: only a path no op has touched can be
    let simDangling (isDangling: string -> bool) (p: string) : bool =
        match realPathIn stack.Value p with
        | Some r -> isDangling r
        | None -> false

    // the top layer's node for a path, created along the way
    let private ensure (root: SimNode) (p: string) : SimNode =
        let mutable node = root

        for c in compsOf p do
            node <-
                match node.Find c with
                | null ->
                    let n = SimNode(comparer)
                    node.Add(c, n)
                    n
                | n -> n

        node

    // every proper ancestor exists as a directory once an entry is made
    let private markParents (root: SimNode) (p: string) =
        let comps = compsOf p
        let mutable node = root

        for i in 0 .. comps.Length - 2 do
            node <- node.Find comps[i]

            if node.State = 0 || node.State = 3 then
                if node.State = 3 then
                    node.Cleared <- true

                node.State <- 2

    let private setLeaf (root: SimNode) (p: string) (state: int) =
        let n = ensure root p
        n.Clear()
        n.State <- state
        n.Cleared <- true
        n.Mirror <- null

        if state <> 3 then
            markParents root p

    // a directory copy: the destination is the source's view, overlay
    // cloned, the layer beneath mirrored
    let private copyTree (frames: PlanFrame list) (src: string) (dst: string) =
        let top = (List.head frames).Sim
        let struct (_, node, below) = viewIn top src

        let mirror =
            match below with
            | null -> null
            | b -> b

        let d = ensure top dst
        d.Clear()

        if not (isNull node) then
            for k in node.Names do
                d.Add(k, node.Find(k).Clone())

        d.State <- 2
        d.Cleared <- true
        d.Mirror <- mirror
        markParents top dst

    // a captured op's effect on the top layer
    let private simApply (op: Value) =
        let frames = stack.Value
        let top = (List.head frames).Sim

        match op with
        | VUnion(("WriteFile" | "WriteFileAtomic"), Some(VTuple [ VStr p; _ ])) -> setLeaf top p 1
        | VUnion("DeleteFile", Some(VStr p))
        | VUnion("DeleteDir", Some(VStr p))
        | VUnion("DeleteTree", Some(VStr p)) -> setLeaf top p 3
        | VUnion("MakeDir", Some(VStr p)) ->
            let n = ensure top p

            if n.State = 0 || n.State = 3 then
                if n.State = 3 then
                    n.Cleared <- true

                n.State <- 2

            markParents top p
        | VUnion("Copy", Some(VTuple [ VStr _; VStr d ])) -> setLeaf top d 1
        | VUnion("Move", Some(VTuple [ VStr s; VStr d ])) ->
            setLeaf top s 3
            setLeaf top d 1
        | VUnion("CopyDir", Some(VTuple [ VStr s; VStr d ])) -> copyTree frames s d
        | VUnion("MoveDir", Some(VTuple [ VStr s; VStr d ])) ->
            copyTree frames s d
            setLeaf top s 3
        | _ -> ()

    // the filesystem paths an Op names (HttpSend names none)
    let private opPaths (op: Value) : string list =
        match op with
        | VUnion("WriteFile", Some(VTuple [ VStr p; _ ]))
        | VUnion("WriteFileAtomic", Some(VTuple [ VStr p; _ ])) -> [ p ]
        | VUnion(_, Some(VStr p)) -> [ p ]
        | VUnion(_, Some(VTuple [ VStr a; VStr b ])) -> [ a; b ]
        | _ -> []

    /// append a captured Op to the top frame; record its target paths so
    /// a later read at or under one is refused (known-after-apply). An
    /// Op naming a path under a `within tmp` dir this plan opened is
    /// refused [D:plan-within-tmp]: apply would meet a deleted dir
    /// midway, after earlier ops ran.
    let capture (op: Value) (targets: string list) =
        match stack.Value with
        | frame :: _ ->
            let case =
                match op with
                | VUnion(c, _) -> c
                | _ -> "Op"

            for p in opPaths op do
                match frame.TmpRoots |> Seq.tryFind (fun root -> within root (norm p)) with
                | Some root ->
                    failwith
                        $"plan: this captures a {case} of '{quoteData p}', under the 'within tmp' directory '{quoteData root}' opened inside the plan — that directory is deleted when its scope closes, before Plan.apply runs, so apply would fail midway. Build the content in memory and capture the write to its real destination, or do the tmp work outside the plan"
                | None -> ()

            frame.Ops.Add op
            simApply op

            // a write lands where a final symlink points; a delete or
            // move changes the link itself [D:plan-guard-links]
            let followLeaf = case = "WriteFile" || case = "WriteFileAtomic"
            let cache = linkCache ()

            for p in targets do
                let shown = norm p

                for k in keysOf cache followLeaf p do
                    index frame k shown

                // an in-place write changes the file under every hard
                // link to it [D:plan-guard-inode]; an atomic write
                // replaces the inode, so the other links keep their content
                if case = "WriteFile" then
                    match FileId.ofPath p with
                    | ValueSome id -> frame.Files.TryAdd(id, shown) |> ignore
                    | ValueNone -> ()
        | [] -> unreachable "PlanMode.capture with no active frame"

    let private refuse (op: string) (resolvedPath: string) (enclosing: bool, target: string, linked: bool) =
        let where = if enclosing then "an enclosing 'plan'" else "this 'plan'"

        let what =
            if linked then $"targets '{target}', the same file (a hard link)"
            elif target = norm resolvedPath then "targets it"
            else $"targets '{target}'"

        failwith
            $"{op}: known-after-apply — this reads '{resolvedPath}', but an earlier captured mutation in {where} {what}; the read would see stale state (the mutation is a pending Op, not yet applied). Restructure so the read does not depend on a captured write, or move it out of the plan"

    // one walk of a key's components in one frame [D:plan-guard-linear]:
    // the target the key equals, else the shallowest one above it, else
    // — when `below` holds — a target under it
    let private lookup (frame: PlanFrame) (k: string) (below: unit -> bool) : string option =
        let comps = parts k
        let mutable node = frame.Index
        let mutable above: string = null
        let mutable i = 0

        while not (isNull node) && i < comps.Length do
            node <- node.Find comps[i]

            if not (isNull node) && i < comps.Length - 1 && isNull above then
                above <- node.Target

            i <- i + 1

        if isNull node then
            Option.ofObj above
        elif not (isNull node.Target) then
            Some node.Target
        elif not (isNull above) then
            Some above
        elif not (isNull node.Below) && below () then
            Some node.Below
        else
            None

    // the guard's walk [D:plan-guard-links]: every enclosing frame — an
    // inner plan's read sees an outer plan's pending ops as stale too —
    // under every key of the read path; `below` decides a hit for a
    // target below the path
    let private guard (op: string) (resolvedPath: string) (below: unit -> bool) =
        match stack.Value with
        | [] -> ()
        | frames when frames |> List.forall (fun f -> f.Index.IsEmpty) -> ()
        | frames ->
            let keys = keysOf (linkCache ()) true resolvedPath

            // the path's file identity, asked only when a frame holds one
            // [D:plan-guard-inode]
            let id =
                lazy
                    (if frames |> List.exists (fun f -> f.Files.Count > 0) then
                         FileId.ofPath resolvedPath
                     else
                         ValueNone)

            frames
            |> List.indexed
            |> List.tryPick (fun (i, frame) ->
                match keys |> List.tryPick (fun k -> lookup frame k below) with
                | Some t -> Some(i > 0, t, false)
                | None ->
                    match id.Value with
                    | ValueSome fid when frame.Files.Count > 0 ->
                        match frame.Files.TryGetValue fid with
                        | true, t -> Some(i > 0, t, true)
                        | _ -> None
                    | _ -> None)
            |> Option.iter (refuse op resolvedPath)

    /// a read builtin's known-after-apply gate [D:plan-apply]
    /// [D:plan-stale-reads]: inside a plan, reading a path an earlier
    /// captured mutation changed would see stale state (the mutation is
    /// still a pending Op). A point read (one path's content, existence
    /// or metadata) conflicts with a target it equals or lies under (a
    /// captured Dir.deleteAll/move covers the tree), and with a target
    /// below a path that does not exist yet (a captured write would
    /// create it). Other reads run. `op`/`path` name the offender.
    let checkRead (op: string) (resolvedPath: string) =
        guard op resolvedPath (fun () -> not (System.IO.Directory.Exists resolvedPath))

    /// the listing gate (Dir.list/stat, Path.glob, ls) [D:plan-stale-reads]:
    /// a listing of `root` also sees every captured target below it
    let checkTree (op: string) (resolvedRoot: string) = guard op resolvedRoot (fun () -> true)

// The REPL/-e echo [D:repl-echo]: bounded render plus the way-out
// hint. The count shows only when already known (a materialized list)
// — counting a lazy seq would force it.
// the spill tail [D:scoped-procs]: the child's final output — stderr
// first (where diagnostics live), stdout filling the remainder; opened
// shared (the pump holds the write handle and flushes per chunk)
let procTail (h: ProcHandle) : string list =
    // an exited child's spill must be complete before it is read —
    // otherwise a fast exit races the pumps and the final output is
    // missing from the watch error
    (try
        if h.Proc.HasExited then
            h.Drain()
     with _ ->
         ())

    let readLines path =
        try
            use fs =
                new System.IO.FileStream(
                    path,
                    System.IO.FileMode.Open,
                    System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite
                )

            use r = new System.IO.StreamReader(fs)
            let mutable lines = []
            let mutable line = r.ReadLine()

            while line <> null do
                lines <- line :: lines
                line <- r.ReadLine()

            List.rev lines
        with _ ->
            []

    let err = readLines h.ErrPath
    let out = readLines h.OutPath
    let take n (xs: string list) = xs |> List.skip (max 0 (xs.Length - n))
    let errT = take 100 err
    errT @ take (100 - errT.Length) out

// a one-line rendering of the tail for error messages: lines join
// with ⏎, fzf's convention for multi-line content in a one-line
// message
let procTailLine (h: ProcHandle) : string =
    let t = procTail h

    match t |> List.skip (max 0 (t.Length - 5)) with
    | [] -> ""
    | lines -> " — last output: " + String.concat " ⏎ " lines

// the echo rule [D:echo-rule]: a forced seq echoes in full (the user
// forced it; the ceiling is their own scrollback); an unforced one
// shows the first N and names Seq.freeze — the lever that works and
// renders identically. Forced-ness is the materialized-collection
// probe; the REPL's map-key completion peeks through the same probe,
// so echo and completion agree about what "forced" means
// [D:value-key-complete].
let forcedItems (items: seq<Value>) : Value list option =
    match items with
    | :? (Value list) as l -> Some l
    | :? System.Collections.Generic.ICollection<Value> as c -> Some(List.ofSeq c)
    | _ -> None

/// the footer names the cap in effect [D:echo-cap] — a hardcoded
/// count beside a configurable cap could misstate it
let unforcedHint (cap: int) =
    $"first {cap} of an unforced seq — Seq.freeze to echo everything"

// the piped/-e echo cap [D:echo-cap]: the session cap is a tty-echo
// concern (the REPL owns it, #echo moves it); the piped surface and
// -e keep this constant because their output bytes are pinned
let echoPipedCap: int option = Some 10

/// echo preparation [D:echo-once]: cache an unforced seq so the table
/// probe and the line rendering enumerate the source once — without
/// this the echoTable-then-echoValue composition re-enumerates, and a
/// bare command's child runs twice per echo
let echoPrep (v: Value) : Value =
    match v with
    | VSeq items when (forcedItems items).IsNone -> VSeq(Seq.cache items)
    | v -> v

// the unforced pull, bounded by the cap [D:echo-cap]: cap+1 when
// capped (so the echo never runs more than it shows), everything when
// uncapped (#echo all is the user's own choice; an infinite seq
// hangs, and the #help line says so)
let private cappedPull (cap: int option) (items: seq<Value>) : Value list * bool =
    match cap with
    | Some c ->
        let shown = items |> Seq.truncate (c + 1) |> List.ofSeq
        shown |> List.truncate c, shown.Length > c
    | None -> items |> List.ofSeq, false

// the data sanitizers live in Types [D:tty-echo-strict] — runtime
// messages quote data through them too [D:error-data-escape]
let sanitizeTtyData (s: string) : string = Weir.Types.sanitizeTtyData s

let sanitizeTtyEcho (s: string) : string = Weir.Types.sanitizeTtyEcho s

/// sanitize data only when the sink is a tty [D:binary-echo] —
/// redirected output (a pipe, a file) stays byte-faithful; a terminal
/// gets the safe rendering
let sanitizeIfTty (redirected: bool) (s: string) : string =
    if redirected then s else sanitizeTtyData s

/// stderr at a tty [D:tty-diag-sanitize]: every write rides the data
/// sanitizer, so a diagnostic echoing source, a path or a pattern
/// cannot drive the terminal; weir's own SGR colouring passes
type TtyErrWriter(inner: System.IO.TextWriter) =
    inherit System.IO.TextWriter()
    override _.Encoding = inner.Encoding

    override _.NewLine
        with get () = inner.NewLine
        and set v = inner.NewLine <- v

    override _.Write(c: char) = inner.Write(sanitizeTtyData (string c))

    override _.Write(s: string) =
        if not (isNull s) then
            inner.Write(sanitizeTtyData s)

    override _.Write(buffer: char[], index: int, count: int) =
        inner.Write(sanitizeTtyData (System.String(buffer, index, count)))

    override _.WriteLine() = inner.WriteLine()

    override _.WriteLine(s: string) =
        inner.WriteLine(if isNull s then s else sanitizeTtyData s)

    override _.Flush() = inner.Flush()

// binary content must not reach a terminal [D:binary-echo]: a NUL in
// the echo's pulled prefix marks the value binary, and the echo —
// weir's own rendering choice — refuses with the redirect hint;
// `print` stays the user's decision. The marker is NUL, never strict
// UTF-8, which would misdetect. The probe walks the echoPrep cache,
// so no enumeration is added; an uncapped echo probes a bounded
// prefix (101).
// The probe recurses through containers: a `| complete` record holds
// the command's stdout, so records/tuples/unions/maps need the same
// refusal the seq echo has. Secret renders *** and Bytes renders a
// summary, so neither can leak content and neither is probed.
let echoBinary (cap: int option) (v: Value) : bool =
    let bound =
        match cap with
        | Some c -> c + 1
        | None -> 101

    // depth-bounded like every other value walk [D:eq-depth]: `bound`
    // clips seq width, but a recursive record type is finite in width
    // and not in depth, so an unbounded descent stack-overflows
    // (uncatchably) on a deeply-nested value. Cap at 100, the show
    // renderer's ceiling: a NUL nested past 100 goes undetected, which
    // is strictly better than crashing the process.
    let rec has (depth: int) (v: Value) : bool =
        match v with
        | _ when depth > 100 -> false
        | VStr s -> s.Contains '\u0000'
        | VSeq items -> items |> Seq.truncate bound |> Seq.exists (has (depth + 1))
        | VRecord(_, fields) -> fields |> List.exists (snd >> has (depth + 1))
        | VTuple items -> items |> List.exists (has (depth + 1))
        | VUnion(_, Some payload) -> has (depth + 1) payload
        | VMap entries -> entries |> Map.exists (fun _ x -> has (depth + 1) x)
        | _ -> false

    has 0 v

let echoValue (cap: int option) (v: Value) : string * string option =
    match v with
    | VSeq items ->
        match forcedItems items with
        | Some all ->
            // full at the top level; each element keeps the echo's
            // inner clips (a forced outer may hold a lazy inner —
            // [nats] is forcible and must not hang the echo). The cap
            // never clips a forced seq [D:echo-cap]; forced-ness
            // outranks it.
            let body = all |> List.map (formatWith echoLimits 1) |> String.concat "; "
            $"[{body}]", None
        | None ->
            // one forcing pass — the echo must not enumerate its source
            // twice: materialize the capped prefix, render from that list
            let visible, clipped = cappedPull cap items
            let body = visible |> List.map (formatWith echoLimits 1) |> String.concat "; "

            let ellipsis = if clipped then echoLimits.Ellipsis else ""

            $"[{body}{ellipsis}]", (if clipped then Some(unforcedHint cap.Value) else None)
    | _ -> formatWith echoLimits 0 v, None

// the REPL's table rendering [D:repl-table] — presentation only: show
// stays canonical and every other consumer is untouched; the REPL's
// echo (tty-gated by the caller) renders a seq of same-shaped records
// with scalar fields as aligned columns. Cells reuse show's spellings
// except strings, which drop their quotes (a display, not a literal);
// columns are alphabetical (show's own field order); numeric-ish
// columns right-align. Widths are char counts (the wrap math assumes
// this).
/// "a week ago" — the table's rendering of an Instant [D:filerow];
/// show/interpolation keep ISO (tests must assert both to pin the
/// split)
let relativeInstant (nowMs: int64) (ms: int64) : string =
    let past = ms <= nowMs
    let s = abs (nowMs - ms) / 1000L

    let phrase =
        if s < 45L then
            "moments"
        elif s < 90L then
            "a minute"
        elif s < 2700L then
            $"{(s + 30L) / 60L} minutes"
        elif s < 5400L then
            "an hour"
        elif s < 79200L then
            $"{(s + 1800L) / 3600L} hours"
        elif s < 129600L then
            "a day"
        elif s < 604800L then
            $"{(s + 43200L) / 86400L} days"
        elif s < 907200L then
            "a week"
        elif s < 2629800L then
            $"{(s + 302400L) / 604800L} weeks"
        elif s < 3944700L then
            "a month"
        elif s < 31557600L then
            $"{(s + 1314900L) / 2629800L} months"
        elif s < 47336400L then
            "a year"
        else
            $"{(s + 15778800L) / 31557600L} years"

    if phrase = "moments" then
        (if past then "just now" else "moments away")
    elif past then
        $"{phrase} ago"
    else
        $"in {phrase}"

let rec private tableCell (v: Value) : (string * bool) option =
    // (text, numericish) — numericish drives right-alignment
    match v with
    | VStr s ->
        let clipped =
            match echoLimits.MaxStr with
            | Some m when CodePoints.length s > m -> CodePoints.head s (m - 1) + "…"
            | _ -> s

        // the table is a tty-only renderer [D:binary-echo]: a data cell's
        // hostile bytes are neutralized (the tint is added later, around
        // this sanitized text — weir's own colouring is untouched)
        Some(sanitizeTtyEcho clipped, false)
    | VInt _
    | VFloat _
    | VSize _
    | VBytes _
    | VDur _ -> Some(formatWith echoLimits 0 v, true)
    // relative in the table, ISO everywhere else [D:filerow]: the same
    // split Duration has (lossless show, abbreviated cell) — no
    // staleness, since the row carries the absolute Instant and this
    // renders at echo time
    | VInstant ms -> Some(relativeInstant (System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ms, false)
    | VBool b -> Some((if b then "true" else "false"), false)
    | VUuid g -> Some(formatUuid g, false)
    | VSecret _ -> Some("***", false)
    | VUnion("None", None) -> Some("", false)
    | VUnion("Some", Some inner) -> tableCell inner
    | VUnion(name, None) -> Some(name, false)
    | _ -> None

/// the lines form [D:echo-lines]: seq<string> presents as its lines
/// at a tty — the content arrived as lines, and the list-literal
/// render undid that. Unclipped per line (a tab renders as itself; CR
/// and escapes render visibly at the caller [D:tty-echo-strict];
/// tty-only, so the piped surface cannot change); the count clip and the forced/unforced sentence
/// ride the footer unchanged. Keyed on the type at the caller
/// (seq<string> exactly), never content-sniffing.
let echoLines (cap: int option) (v: Value) : (string list * string option) option =
    let asLine =
        function
        | VStr s -> s
        | v -> unreachable $"the caller gates on seq<string>, got {formatValue v}"

    match v with
    | VSeq items ->
        match forcedItems items with
        | Some all -> Some(all |> List.map asLine, None)
        | None ->
            let visible, clipped = cappedPull cap items

            Some(visible |> List.map asLine, (if clipped then Some(unforcedHint cap.Value) else None))
    | _ -> None

let echoTable (cap: int option) (width: int option) (v: Value) : (string list * string option) option =
    match v with
    | VSeq items ->
        let forced = forcedItems items

        let shown, clipped =
            match forced with
            | Some all -> all, false
            | None -> cappedPull cap items

        match shown with
        | VRecord(n0, f0) :: _ when shown.Length > 1 || true ->
            let visible = shown

            let keys0 = f0 |> List.map fst

            // an all-None optional column hides [D:filerow]: a column
            // that says nothing on every shown row only costs width —
            // a general table rule, not a FileRow special case; one
            // Some anywhere brings the column back
            let allNone k =
                visible
                |> List.forall (fun r ->
                    match r with
                    | VRecord(_, f) ->
                        match recTryGet k f with
                        | Some(VUnion("None", None)) -> true
                        | _ -> false
                    | _ -> false)

            let showKeys = keys0 |> List.filter (allNone >> not)

            let cellsOf (r: Value) =
                match r with
                | VRecord(n, f) when n = n0 && (f |> List.map fst) = keys0 ->
                    showKeys
                    |> List.map (fun k -> tableCell (recGet k f))
                    |> List.fold
                        (fun acc c ->
                            match acc, c with
                            | Some rows, Some cell -> Some(cell :: rows)
                            | _ -> None)
                        (Some [])
                    |> Option.map List.rev
                | _ -> None

            let rows = visible |> List.map cellsOf

            if rows |> List.exists Option.isNone then
                None
            else
                let rows = rows |> List.map Option.get
                let truncated = clipped

                let numeric =
                    [ 0 .. showKeys.Length - 1 ]
                    |> List.map (fun i ->
                        rows
                        |> List.forall (fun r ->
                            let t, num = r[i]
                            num || t = ""))

                let widths =
                    [ 0 .. showKeys.Length - 1 ]
                    |> List.map (fun i ->
                        rows
                        |> List.fold (fun w r -> max w (CodePoints.length (fst r[i]))) (CodePoints.length showKeys[i]))

                // terminal-width clamping [D:table-polish]: the widest
                // column above its floor absorbs the clip (usually the
                // path column), repeatedly until the table fits; a
                // terminal too narrow even for the floors renders
                // unclamped — the terminal wraps rather than the
                // floors being mangled
                let widths =
                    match width with
                    | Some termW ->
                        let sep = 2 * (showKeys.Length - 1)
                        let floorOf i = max 5 (CodePoints.length showKeys[i])

                        let rec shrink (ws: int list) =
                            let total = List.sum ws + sep

                            if total <= termW then
                                ws
                            else
                                let candidate =
                                    [ 0 .. ws.Length - 1 ]
                                    |> List.filter (fun i -> ws[i] > floorOf i)
                                    |> List.sortByDescending (fun i -> ws[i])
                                    |> List.tryHead

                                match candidate with
                                | None -> ws
                                | Some i ->
                                    let newW = max (floorOf i) (ws[i] - (total - termW))
                                    shrink (ws |> List.mapi (fun j w -> if j = i then newW else w))

                        shrink widths
                    | None -> widths

                // a cell longer than its (possibly clamped) column ends
                // in the ellipsis — never a silent cut
                let clip (w: int) (t: string) =
                    if CodePoints.length t > w then
                        CodePoints.head t (max 0 (w - 1)) + "…"
                    else
                        t

                let pad (i: int) (t: string) =
                    let t = clip widths[i] t

                    if numeric[i] then
                        CodePoints.padLeft widths[i] t
                    else
                        CodePoints.padRight widths[i] t

                let line (cells: string list) =
                    (cells |> List.mapi pad |> String.concat "  ").TrimEnd()

                let header = line showKeys
                let rule = line (widths |> List.map (fun w -> System.String('\u2500', w)))
                let body = rows |> List.map (fun r -> line (r |> List.map fst))
                let ellipsisRow = if truncated then [ "…" ] else []

                let hint = if truncated then Some(unforcedHint cap.Value) else None

                Some(header :: rule :: (body @ ellipsisRow), hint)
        | _ -> None
    | _ -> None

// the clipped-echo tail — one spelling for the three echo consumers
// (REPL let/expr arms, -e). No pipe-to-print suggestion [D:echo-rule]:
// that promised continuation but delivered a different rendering; the
// hint names the lever that reproduces this one.
let echoTail (hint: string option) : string =
    match hint with
    | Some h -> $" ({h})"
    | None -> ""

// The line-per-element renderer. Both consumers — the print builtin
// and the runner's command-statement streaming — must call this one
// function; the byte-identity of their output is a stated claim, not
// a coincidence. Data bound for a tty is sanitized [D:binary-echo]
// (redirected output stays byte-faithful, so the byte-identity claim
// holds for a pipe/file).
let writeLinesTo (w: System.IO.TextWriter) (items: seq<Value>) : unit =
    let redirected =
        if System.Object.ReferenceEquals(w, System.Console.Out) then
            System.Console.IsOutputRedirected
        elif System.Object.ReferenceEquals(w, System.Console.Error) then
            System.Console.IsErrorRedirected
        else
            true // a non-console writer (a file) is never a tty

    for item in items do
        match item with
        | VStr s -> w.WriteLine(sanitizeIfTty redirected s)
        | other -> w.WriteLine(sanitizeIfTty redirected (formatValue other))

let writeLines (items: seq<Value>) : unit = writeLinesTo System.Console.Out items

// Overflow policy (Part 3): int arithmetic is checked — silent
// wrapping is the classic shell-calculator bug; a raise joins the
// named runtime failure classes instead.
let private checkedInt (f: unit -> int64) : Value =
    try
        VInt(f ())
    with :? System.OverflowException ->
        failwith "integer overflow"

// non-finite results raise [D:floats], mirroring checkedInt; -0.0
// normalizes so equality and rendering never disagree
let private checkedFloat (op: string) (f: unit -> float) : Value =
    let r = f ()

    if System.Double.IsFinite r then
        VFloat(if r = 0.0 then 0.0 else r)
    else
        failwith $"'{op}' produced a non-finite float (overflow)"

// Size/Duration/Instant arithmetic is checked like int's, in weir's words
let private checkedUnit (what: string) (f: unit -> int64) : int64 =
    try
        f ()
    with :? System.OverflowException ->
        failwith $"{what} overflow"

let private unitDiv (what: string) (a: int64) (b: int64) : int64 =
    if b = 0L then failwith "division by zero" else checkedUnit what (fun () -> a / b)

let private instantShift (a: int64) (d: int64) : Value =
    VInstant(checkInstant "Instant arithmetic" (checkedUnit "Instant" (fun () -> Checked.(+) a d)))

let private binOp (op: string) (l: Value) (r: Value) : Value =
    match op, l, r with
    | "+", VInt a, VInt b -> checkedInt (fun () -> Checked.(+) a b)
    | "+", VFloat a, VFloat b -> checkedFloat "+" (fun () -> a + b)
    | "-", VFloat a, VFloat b -> checkedFloat "-" (fun () -> a - b)
    | "*", VFloat a, VFloat b -> checkedFloat "*" (fun () -> a * b)
    | "/", VFloat a, VFloat b ->
        if b = 0.0 then
            failwith "float division by zero"
        else
            checkedFloat "/" (fun () -> a / b)
    | ">", VFloat a, VFloat b -> VBool(a > b)
    | "<", VFloat a, VFloat b -> VBool(a < b)
    | ">=", VFloat a, VFloat b -> VBool(a >= b)
    | "<=", VFloat a, VFloat b -> VBool(a <= b)
    | "+", VSize a, VSize b -> VSize(checkedUnit "Size" (fun () -> Checked.(+) a b))
    | "-", VSize a, VSize b -> VSize(checkedUnit "Size" (fun () -> Checked.(-) a b))
    | "*", VSize a, VInt b -> VSize(checkedUnit "Size" (fun () -> Checked.(*) a b))
    | "*", VInt a, VSize b -> VSize(checkedUnit "Size" (fun () -> Checked.(*) a b))
    | "/", VSize a, VInt b -> VSize(unitDiv "Size" a b)
    | ">", VSize a, VSize b -> VBool(a > b)
    | "<", VSize a, VSize b -> VBool(a < b)
    | ">=", VSize a, VSize b -> VBool(a >= b)
    | "<=", VSize a, VSize b -> VBool(a <= b)
    | "+", VDur a, VDur b -> VDur(checkedUnit "Duration" (fun () -> Checked.(+) a b))
    | "-", VDur a, VDur b -> VDur(checkedUnit "Duration" (fun () -> Checked.(-) a b))
    | "*", VDur a, VInt b -> VDur(checkedUnit "Duration" (fun () -> Checked.(*) a b))
    | "*", VInt a, VDur b -> VDur(checkedUnit "Duration" (fun () -> Checked.(*) a b))
    | "/", VDur a, VInt b -> VDur(unitDiv "Duration" a b)
    | "-", VInstant a, VInstant b -> VDur(checkedUnit "Duration" (fun () -> Checked.(-) a b))
    | "+", VInstant a, VDur d -> instantShift a d
    | "+", VDur d, VInstant a -> instantShift a d
    | "-", VInstant a, VDur d -> instantShift a (checkedUnit "Instant" (fun () -> Checked.(~-) d))
    | ">", VInstant a, VInstant b -> VBool(a > b)
    | "<", VInstant a, VInstant b -> VBool(a < b)
    | ">=", VInstant a, VInstant b -> VBool(a >= b)
    | "<=", VInstant a, VInstant b -> VBool(a <= b)
    | ">", VDur a, VDur b -> VBool(a > b)
    | "<", VDur a, VDur b -> VBool(a < b)
    | ">=", VDur a, VDur b -> VBool(a >= b)
    | "<=", VDur a, VDur b -> VBool(a <= b)
    | "+", VStr a, VStr b -> VStr(a + b)
    | "-", VInt a, VInt b -> checkedInt (fun () -> Checked.(-) a b)
    | "*", VInt a, VInt b -> checkedInt (fun () -> Checked.(*) a b)
    | "/", VInt a, VInt b ->
        // weir's own message text [D:message-ownership], matching the
        // float arm above, rather than the BCL's
        if b = 0L then
            failwith "division by zero"
        else
            checkedInt (fun () -> a / b)
    | "%", VInt a, VInt b ->
        // truncated, .NET's own % [D:modulo] — the sign follows the
        // dividend (-7 % 3 = -1), matching F# so the oracle stays
        // divergence-free; same zero handling as /, with % in the
        // message
        if b = 0L then
            failwith "modulo by zero"
        else
            checkedInt (fun () -> a % b)
    | ">", VInt a, VInt b -> VBool(a > b)
    | "<", VInt a, VInt b -> VBool(a < b)
    | ">=", VInt a, VInt b -> VBool(a >= b)
    | "<=", VInt a, VInt b -> VBool(a <= b)
    | "==", a, b -> VBool(a = b)
    | "<>", a, b -> VBool(a <> b)
    | _ -> unreachable $"the checker rejects '{op}' on {formatValue l} and {formatValue r}"

// ---- declared codecs [D:wire-codecs] — the json/yaml boundaries only;
// show and == never see them

// a coded field's value as the plain wire value the writers already
// render; a codec that would drop precision raises instead
let rec private encodeCoded (where: string) (c: Codec) (v: Value) : Value =
    let whole (ms: int64) (noun: string) (exact: Codec) =
        if ms % 1000L <> 0L then
            failwith
                $"{where}: [<{codecName c}>] writes whole seconds, and this {noun} carries {abs (ms % 1000L)} ms more — round it, or declare [<{codecName exact}>]"
        else
            ms / 1000L

    match c, v with
    | _, VUnion("None", None) -> v
    | _, VUnion("Some", Some inner) -> VUnion("Some", Some(encodeCoded where c inner))
    | _, VSeq items -> VSeq(items |> Seq.map (encodeCoded where c))
    | Iso8601, VInstant ms -> VStr(formatInstant ms)
    | EpochMs, VInstant ms -> VInt ms
    | EpochSec, VInstant ms -> VInt(whole ms "instant" EpochMs)
    | Millis, VDur ms -> VInt ms
    | Seconds, VDur ms -> VInt(whole ms "duration" Millis)
    | ByteCount, VSize b -> VInt b
    | _ -> unreachable $"the declaration law fits [<{codecName c}>] to {formatValue v}"

// every coded field in a value, encoded — both writers render the
// result, so json and yaml cannot disagree on a codec
let rec private encodeTree (fmt: string) (codecs: Map<string, Map<string, Codec>>) (v: Value) : Value =
    if codecs.IsEmpty then
        v
    else
        match v with
        | VRecord(rname, fields) ->
            let cods = Map.tryFind rname codecs |> Option.defaultValue Map.empty

            VRecord(
                rname,
                fields
                |> List.map (fun (f, fv) ->
                    match Map.tryFind f cods with
                    | Some c -> f, encodeCoded $"to {fmt}: field '{f}'" c fv
                    | None -> f, encodeTree fmt codecs fv)
            )
        | VUnion(case, Some payload) -> VUnion(case, Some(encodeTree fmt codecs payload))
        | VSeq items -> VSeq(items |> Seq.map (encodeTree fmt codecs))
        | VTuple items -> VTuple(items |> List.map (encodeTree fmt codecs))
        | VMap entries -> VMap(entries |> Map.map (fun _ ev -> encodeTree fmt codecs ev))
        | v -> v

// a coded field read back: text (a JSON string, any YAML scalar) and/or
// an integer (a JSON number, an unquoted YAML int)
let private decodeCoded (where: string) (c: Codec) (text: string option) (num: int64 option) : Value =
    let needInt (what: string) =
        match num with
        | Some n -> n
        | None ->
            let got = text |> Option.map (fun t -> $"'{excerpt t}'") |> Option.defaultValue "a non-integer"
            failwith $"{where}: [<{codecName c}>] reads {what} (an integer), got {got}"

    let seconds (n: int64) =
        try
            Checked.(*) n 1000L
        with :? System.OverflowException ->
            failwith $"{where}: [<{codecName c}>] value {n} is out of range"

    match c with
    | Iso8601 ->
        match text with
        | Some t ->
            match parseInstantMs t with
            | Ok ms -> VInstant ms
            | Error e -> failwith $"{where}: {e}"
        | None -> failwith $"{where}: [<Iso8601>] reads an ISO 8601 string, got a number"
    | EpochMs -> VInstant(checkInstant where (needInt "epoch milliseconds"))
    | EpochSec -> VInstant(checkInstant where (seconds (needInt "epoch seconds")))
    | Millis -> VDur(needInt "milliseconds")
    | Seconds -> VDur(seconds (needInt "seconds"))
    | ByteCount -> VSize(needInt "a byte count")

// a JSON value as a `Yaml` node [D:json-yaml-field]: JSON is a YAML
// subset, so an opaque `Yaml` field reads any subtree whole — object
// -> YMap (document order), array -> YSeq, and a number by the YAML
// scalar rule (integer-shaped -> YInt, else YFloat). A repeated key
// last-wins at its first position, as records and Map read it
// [D:json-yaml-dupkeys] — a YMap holding both would write YAML weir
// itself refuses.
// an integer past int64 in an opaque Yaml node [D:yaml-node-bigint]
// reads as YFloat (its nearest value, for code that matches it) and
// remembers its digits by node identity, so a write of that same node
// emits the digits, not the float; a node built in weir has none
let private yamlBigDigits =
    System.Runtime.CompilerServices.ConditionalWeakTable<Value, string>()

let private integerShaped (raw: string) =
    let r = if raw.StartsWith "-" || raw.StartsWith "+" then raw.Substring 1 else raw
    r.Length > 0 && r |> Seq.forall System.Char.IsAsciiDigit

let yamlFloatNode (raw: string) (f: float) : Value =
    let node = VUnion("YFloat", Some(VFloat f))

    if integerShaped raw then
        yamlBigDigits.AddOrUpdate(node, raw.TrimStart '+')

    node

let yamlBigIntText (node: Value) : string option =
    match yamlBigDigits.TryGetValue node with
    | true, t -> Some t
    | _ -> None

let rec private jsonYamlNode (where: string) (e: System.Text.Json.JsonElement) : Value =
    match e.ValueKind with
    | System.Text.Json.JsonValueKind.Object ->
        let props = e.EnumerateObject() |> List.ofSeq
        let last = props |> List.map (fun p -> p.Name, p.Value) |> Map.ofList

        VUnion(
            "YMap",
            Some(
                VSeq(
                    props
                    |> List.map (fun p -> p.Name)
                    |> List.distinct
                    |> List.map (fun k -> VTuple [ VStr k; jsonYamlNode where last[k] ])
                )
            )
        )
    | System.Text.Json.JsonValueKind.Array ->
        VUnion("YSeq", Some(VSeq(e.EnumerateArray() |> Seq.map (jsonYamlNode where) |> List.ofSeq)))
    | System.Text.Json.JsonValueKind.String -> VUnion("YStr", Some(VStr(e.GetString())))
    | System.Text.Json.JsonValueKind.True -> VUnion("YBool", Some(VBool true))
    | System.Text.Json.JsonValueKind.False -> VUnion("YBool", Some(VBool false))
    | System.Text.Json.JsonValueKind.Number ->
        let raw = e.GetRawText()

        match System.Int64.TryParse raw with
        | true, n -> VUnion("YInt", Some(VInt n))
        | _ ->
            match parseFloat raw with
            | Ok f -> yamlFloatNode raw f
            | Error _ -> failwith $"{where}: number {quoteData raw} is out of float range"
    | _ -> VUnion("YNull", None)

// a mapping's keys are unique on the wire [D:mapping-dupkeys]: a pair
// seq or YMap holding a key twice would write a document weir reads
// last-wins (json) or refuses (yaml) — the writer refuses, naming it
let private refuseDupKeys (who: string) (keys: string seq) =
    let seen = System.Collections.Generic.HashSet<string>()

    for k in keys do
        if not (seen.Add k) then
            failwith
                $"{who}: the mapping holds the key '{quoteData k}' twice — a document's keys are unique; keep one pair per key before writing"

let private jsonLine
    (renames: Map<string, Map<string, string>>)
    (unions: Map<string, string * string * bool>)
    (v: Value)
    : string =
    let buffer = new System.Buffers.ArrayBufferWriter<byte>()
    // the CLI's encoder, not the HTML-safe default [D:json-raw-user]
    use writer = new System.Text.Json.Utf8JsonWriter(buffer, rawJsonWriterOptions)

    let rec write (v: Value) =
        match v with
        | VInt n -> writer.WriteNumberValue n
        // the show shape [D:floats-boundaries]: 1.0 emits as 1.0 (a
        // float field is not an integer field); formatFloat is always
        // a valid JSON number since non-finite values are
        // unrepresentable
        | VFloat f -> writer.WriteRawValue(formatFloat f)
        | VStr s -> writer.WriteStringValue s
        // the canonical text [D:uuid]: JSON has no uuid type
        | VUuid g -> writer.WriteStringValue(formatUuid g)
        | VBool b -> writer.WriteBooleanValue b
        // Option [D:json-option]: Some writes its scalar; a bare None
        // at the element level is a null line (a None field is
        // omitted, below)
        | VUnion("Some", Some inner) -> write inner
        | VUnion("None", None) -> writer.WriteNullValue()
        // a tagged case writes its payload with the tag reinserted
        // first [D:wire-unions] (k8s's own convention); the [<Other>]
        // case holds what was not understood, so nothing faithful can
        // be written
        | VUnion(c, payload) when unions.ContainsKey c ->
            let tagField, tagValue, isOther = unions[c]

            if isOther then
                failwith
                    $"to json: '{c}' is the [<Other>] fallback — it names a tag that matched no case, and nothing faithful can be written from it"

            writer.WriteStartObject()
            writer.WritePropertyName tagField
            writer.WriteStringValue tagValue

            (match payload with
             | Some(VRecord(rname, fields)) -> writeFields rname fields
             | None -> ()
             | Some v -> unreachable $"the declaration law rejects a tagged payload of {formatValue v}")

            writer.WriteEndObject()
        | VSeq items ->
            // a non-empty pair-seq is one object (the seq<string * _>
            // rule, `to yaml`'s twin [D:repl-infer]); any other seq is
            // a nested array [D:recursive-fields] — elements recurse;
            // an Option element writes null for None (an array slot
            // cannot be omitted; null reads back as None). An empty
            // mapping writes [] — value-directed, matching the yaml
            // writer.
            let items = List.ofSeq items

            let asPairs =
                items
                |> List.map (fun i ->
                    match i with
                    | VTuple [ VStr k; v ] -> Some(k, v)
                    | _ -> None)

            if not items.IsEmpty && asPairs |> List.forall Option.isSome then
                let pairs = asPairs |> List.map Option.get
                refuseDupKeys "to json" (List.map fst pairs)
                writer.WriteStartObject()

                for (k, v) in pairs do
                    writer.WritePropertyName k
                    write v

                writer.WriteEndObject()
            else
                writer.WriteStartArray()
                items |> Seq.iter write
                writer.WriteEndArray()
        | VMap entries ->
            // an object, keys sorted by construction [D:map-string] —
            // the round-trip's write half
            writer.WriteStartObject()

            for kv in entries do
                writer.WritePropertyName kv.Key
                write kv.Value

            writer.WriteEndObject()
        | VRecord(rname, fields) ->
            writer.WriteStartObject()
            writeFields rname fields
            writer.WriteEndObject()
        // a `Yaml` node writes as the JSON it denotes [D:json-yaml-field]
        // — after the tagged arm, which owns any user case names
        | VUnion("YMap", Some(VSeq entries)) ->
            entries
            |> Seq.choose (function
                | VTuple [ VStr k; _ ] -> Some k
                | _ -> None)
            |> refuseDupKeys "to json"

            writer.WriteStartObject()

            for e in entries do
                match e with
                | VTuple [ VStr k; v ] ->
                    writer.WritePropertyName k
                    write v
                | v -> unreachable $"a YMap entry is a pair, not {formatValue v}"

            writer.WriteEndObject()
        | VUnion("YSeq", Some(VSeq items)) ->
            writer.WriteStartArray()
            items |> Seq.iter write
            writer.WriteEndArray()
        | VUnion("YFloat", _) when (yamlBigIntText v).IsSome -> writer.WriteRawValue((yamlBigIntText v).Value)
        | VUnion(("YStr" | "YInt" | "YFloat" | "YBool"), Some scalar) -> write scalar
        | VUnion("YNull", None) -> writer.WriteNullValue()
        | v -> unreachable $"the checker rejects 'to json' on {formatValue v}"

    // the field loop, shared by records and tagged payloads
    // [D:wire-unions] — the tag comes first, the payload's fields
    // follow
    and writeFields (rname: string) (fields: (string * Value) list) =
        // the wire key comes back on write [D:wire-keys] — the
        // roundtrip's other half; the rename table rides the TETo
        // node (attrs never reach values)
        let rens = Map.tryFind rname renames |> Option.defaultValue Map.empty

        for kv in fields do
            // [D:json-option]: a None field omits its key, so a
            // weir-produced payload looks like the ecosystem's (gh /
            // kubectl / docker inspect omit rather than null). Missing
            // and null both read back as None, so the roundtrip holds.
            match snd kv with
            | VUnion("None", None) -> ()
            | _ ->
                writer.WritePropertyName(Map.tryFind (fst kv) rens |> Option.defaultValue (fst kv))
                write (snd kv)

    write v
    writer.Flush()
    System.Text.Encoding.UTF8.GetString buffer.WrittenSpan

let private jsonKindName (k: System.Text.Json.JsonValueKind) : string =
    match k with
    | System.Text.Json.JsonValueKind.Array -> "array"
    | System.Text.Json.JsonValueKind.Number -> "number"
    | System.Text.Json.JsonValueKind.String -> "string"
    | System.Text.Json.JsonValueKind.True
    | System.Text.Json.JsonValueKind.False -> "boolean"
    | System.Text.Json.JsonValueKind.Null -> "null"
    | _ -> "non-object"

// the value a field met, in weir's words beside a weir type — never
// System.Text.Json's enum names (`True`, `String`) [D:json-boundary]
let private jsonGotWord (k: System.Text.Json.JsonValueKind) : string =
    match k with
    | System.Text.Json.JsonValueKind.True
    | System.Text.Json.JsonValueKind.False -> "bool"
    | System.Text.Json.JsonValueKind.String -> "string"
    | System.Text.Json.JsonValueKind.Number -> "a number"
    | System.Text.Json.JsonValueKind.Array -> "an array"
    | System.Text.Json.JsonValueKind.Object -> "an object"
    | System.Text.Json.JsonValueKind.Null -> "null"
    | _ -> "nothing"

/// one object document/element -> one row of `def`. `who` names the
/// adapter in every message ("from json" / "from jsonl") and `shown`
/// is the input to cite — the line for jsonl, a snippet for a joined
/// document [D:from-jsonl]
/// parse one document and read it under the declared shape: wantSeq
/// demands a top-level array (one row per element), otherwise an
/// object — the type decides what the top level must be, never the
/// input [D:from-json-seq]
let private jsonDocRead
    (who: string)
    (wantSeq: bool)
    (wantMap: bool)
    (top: JsonTop)
    (defs: Map<string, RecordDef>)
    (udefs: Map<string, UnionDef>)
    (shown: string)
    (text: string)
    : Value =
    use doc =
        match parseJsonBounded text with
        | Ok d -> d
        // never System.Text.Json's words [D:json-boundary]
        | Error(Some why) -> failwith $"{who}: valid JSON, but {why}: {shown}"
        | Error None -> failwith $"{who}: not valid JSON: {shown}"

    let root = doc.RootElement

    // read a scalar of type `scalarTy` from an already-fetched, non-null
    // property [D:json-option]
    let readScalar (name: string) (scalarTy: Ty) (prop: System.Text.Json.JsonElement) =
        match scalarTy, prop.ValueKind with
        | TInt, System.Text.Json.JsonValueKind.Number ->
            match prop.TryGetInt64() with
            | true, n -> VInt n
            | _ ->
                // TryGetInt64 fails for both decimals and integer-shaped
                // overflow — the raw token tells them apart, so the message
                // never calls 99999999999999999999 "a decimal"
                // [D:format-surface-json]
                let raw = prop.GetRawText()

                if raw.Contains '.' || raw.Contains 'e' || raw.Contains 'E' then
                    failwith $"{who}: field '{name}' expected int, got a decimal number — declare it float"
                else
                    failwith $"{who}: field '{name}': number out of int range — declare it float"
        | TFloat, System.Text.Json.JsonValueKind.Number ->
            // integer-shaped numbers widen here [D:floats-boundaries]:
            // JSON has one number type, and this is a parse, not weir
            // arithmetic, so the no-implicit-widening rule does not
            // apply
            let d = prop.GetDouble()

            if System.Double.IsFinite d then
                VFloat(if d = 0.0 then 0.0 else d)
            else
                failwith $"{who}: field '{name}': number out of float range"
        | TStr, System.Text.Json.JsonValueKind.String -> VStr(prop.GetString())
        // reading into a Secret wraps on the way in [D:secret-read]
        | TSecret, System.Text.Json.JsonValueKind.String -> VSecret(prop.GetString())
        // the document holds the secret: never echo it
        | TSecret, kind -> failwith $"{who}: field '{name}' expected a string (Secret), got {jsonGotWord kind}"
        | TUuid, System.Text.Json.JsonValueKind.String ->
            match parseUuid (prop.GetString()) with
            | Ok g -> VUuid g
            | Error e -> failwith $"{who}: field '{name}': {e}"
        | TBool, System.Text.Json.JsonValueKind.True -> VBool true
        | TBool, System.Text.Json.JsonValueKind.False -> VBool false
        | ty, kind -> failwith $"{who}: field '{name}' expected {formatTy ty}, got {jsonGotWord kind} in: {shown}"

    // the recursive reader [D:recursive-fields]: nested objects
    // convert through `defs` (the check-time closure — eval has no
    // env), arrays through the element type; Option means the same
    // thing at every depth (null -> None), and paths name the location
    let rec readValue (name: string) (ty: Ty) (prop: System.Text.Json.JsonElement) : Value =
        match ty with
        | TNamed("Option", [ inner ]) ->
            if prop.ValueKind = System.Text.Json.JsonValueKind.Null then
                VUnion("None", None)
            else
                VUnion("Some", Some(readValue name inner prop))
        | TSeq(TTuple [ TStr; v ]) ->
            // the open mapping (yaml's seq<string * _> rule at the
            // json boundary [D:repl-infer]): a JSON object whose keys
            // are data reads as pairs in document order. An empty
            // array is the empty mapping — the writer's own empty
            // spelling (a pair-seq with no pairs has no object
            // evidence), so the roundtrip holds; a populated array
            // still refuses.
            if
                prop.ValueKind = System.Text.Json.JsonValueKind.Array
                && prop.GetArrayLength() = 0
            then
                VSeq Seq.empty
            elif prop.ValueKind <> System.Text.Json.JsonValueKind.Object then
                failwith
                    $"{who}: field '{name}' expected an object ({formatTy ty}), got {jsonKindName prop.ValueKind} in: {shown}"
            else
                // forced before the document disposes; a repeated key
                // last-wins at its first position [D:mapping-dupkeys]
                let props = prop.EnumerateObject() |> List.ofSeq
                let last = props |> List.map (fun p -> p.Name, p.Value) |> Map.ofList

                props
                |> List.map (fun p -> p.Name)
                |> List.distinct
                |> List.map (fun k -> VTuple [ VStr k; readValue $"{name}[\"{k}\"]" v last[k] ])
                |> List.toSeq
                |> VSeq
        | TSeq elem ->
            if prop.ValueKind <> System.Text.Json.JsonValueKind.Array then
                failwith
                    $"{who}: field '{name}' expected an array ({formatTy ty}), got {jsonKindName prop.ValueKind} in: {shown}"

            // forced before the document disposes; paths index from zero
            // [D:paths-zero-based]
            prop.EnumerateArray()
            |> Seq.mapi (fun i el -> readValue $"{name}[{i}]" elem el)
            |> List.ofSeq
            |> List.toSeq
            |> VSeq
        | TNamed("Map", [ TStr; inner ]) ->
            // the ID-keyed object [D:map-string]: every property value
            // reads as the map's value type; duplicate keys last-win
            // (System.Text.Json's own lookup, the stated boundary
            // rule)
            if prop.ValueKind <> System.Text.Json.JsonValueKind.Object then
                failwith
                    $"{who}: field '{name}' expected an object ({formatTy ty}), got {jsonKindName prop.ValueKind} in: {shown}"

            prop.EnumerateObject()
            |> Seq.fold (fun m p -> Map.add p.Name (readValue $"{name}[\"{p.Name}\"]" inner p.Value) m) Map.empty
            |> VMap
        | TNamed("Yaml", []) -> jsonYamlNode $"{who}: field '{name}'" prop
        | TNamed(n, []) when defs.ContainsKey n ->
            if prop.ValueKind <> System.Text.Json.JsonValueKind.Object then
                failwith
                    $"{who}: field '{name}' expected an object ({n}), got {jsonKindName prop.ValueKind} in: {shown}"

            objRow $"{name}." defs[n] prop
        | TNamed(n, []) when udefs.ContainsKey n ->
            if prop.ValueKind <> System.Text.Json.JsonValueKind.Object then
                failwith
                    $"{who}: field '{name}' expected an object ({n}), got {jsonKindName prop.ValueKind} in: {shown}"

            readUnion $"{name}." udefs[n] prop
        | scalarTy -> readScalar name scalarTy prop

    // the tag dispatch [D:wire-unions]: the discriminator field picks
    // the case, and the whole object reads as its payload record (the
    // tag sits among the fields — internal tagging, the ecosystem's
    // shape); an unmatched value takes the [<Other>] fallback or
    // refuses naming the cases; a missing tag field is a malformed
    // document, never Other's
    and readUnion (prefix: string) (udef: UnionDef) (root: System.Text.Json.JsonElement) : Value =
        let tagField = udef.Tag |> Option.defaultValue "?"
        let mutable prop = Unchecked.defaultof<System.Text.Json.JsonElement>

        if not (root.TryGetProperty(tagField, &prop)) then
            failwith $"{who}: missing tag field '{prefix}{tagField}' ({udef.Name}) in: {shown}"
        elif prop.ValueKind <> System.Text.Json.JsonValueKind.String then
            failwith
                $"{who}: tag field '{prefix}{tagField}' ({udef.Name}) expected a string, got {jsonKindName prop.ValueKind} in: {shown}"
        else
            let tagValue = prop.GetString()

            match
                udef.Cases
                |> List.tryFind (fun (c, _) -> Some c <> udef.OtherCase && Types.caseWire udef c = tagValue)
            with
            | Some(c, Some(TNamed(pn, []))) -> VUnion(c, Some(objRow prefix defs[pn] root))
            | Some(c, None) -> VUnion(c, None)
            | Some(c, Some ty) -> unreachable $"the declaration law rejects case '{c}' of {formatTy ty}"
            | None ->
                match udef.OtherCase with
                | Some oc ->
                    let hasPayload = udef.Cases |> List.exists (fun (c, p) -> c = oc && p.IsSome)

                    VUnion(oc, (if hasPayload then Some(VStr tagValue) else None))
                | None ->
                    let cases =
                        udef.Cases
                        |> List.map (fun (c, _) -> Types.caseWire udef c)
                        |> String.concat ", "

                    failwith
                        $"{who}: tag '{prefix}{tagField}' is '{quoteData tagValue}', which matches no case of {udef.Name} (cases: {cases}) in: {shown}"

    // one object element -> one row (the param shadows the document
    // root on purpose: the field readers below say `root` either way);
    // `prefix` is the dotted path above this object ("" at the top)
    and objRow (prefix: string) (rdef: RecordDef) (root: System.Text.Json.JsonElement) =
        let readField (name: string, ty: Ty) =
            let shownName = prefix + name
            // the wire key [D:wire-keys]: [<Wire "type">] kind reads
            // the document's "type"; paths keep the weir field name
            let wire = Types.wireName rdef name
            let mutable prop = Unchecked.defaultof<System.Text.Json.JsonElement>
            let present = root.TryGetProperty(wire, &prop)
            let isNull = present && prop.ValueKind = System.Text.Json.JsonValueKind.Null

            // a coded field reads through its codec [D:wire-codecs]; the
            // Option/missing/null rules below are unchanged
            let readV (ty: Ty) (p: System.Text.Json.JsonElement) =
                match Types.fieldCodec rdef name with
                | None -> readValue shownName ty p
                | Some c ->
                    let where = $"{who}: field '{shownName}'"

                    let one (e: System.Text.Json.JsonElement) =
                        match e.ValueKind with
                        | System.Text.Json.JsonValueKind.String -> decodeCoded where c (Some(e.GetString())) None
                        | System.Text.Json.JsonValueKind.Number ->
                            match e.TryGetInt64() with
                            | true, n -> decodeCoded where c None (Some n)
                            | _ -> failwith $"{where}: [<{codecName c}>] reads an integer, got {quoteData (e.GetRawText())}"
                        | k -> failwith $"{where}: [<{codecName c}>] expects a string or a number, got {jsonKindName k}"

                    match ty, p.ValueKind with
                    | TSeq _, System.Text.Json.JsonValueKind.Array -> VSeq(p.EnumerateArray() |> Seq.map one |> List.ofSeq)
                    | TSeq _, k -> failwith $"{where}: expected an array, got {jsonKindName k}"
                    | _ -> one p

            let value =
                match ty with
                // an Option field: missing key or explicit null -> None;
                // present -> Some (readValue keeps the rule at depth)
                | TNamed("Option", [ inner ]) ->
                    if not present || isNull then
                        VUnion("None", None)
                    else
                        VUnion("Some", Some(readV inner prop))
                // a required field: missing or null both fail, and the
                // message names the fix (a missing array is an error
                // too: absence is Option's job, [] is not guessed)
                | _ when not present ->
                    let wireNote = if wire <> name then $" (wire key \"{wire}\")" else ""

                    // the error suggests the Option repair (as
                    // from-table does): a type drafted from a sample
                    // only sees what the sample had [D:schema-types]
                    failwith
                        $"{who}: missing field '{shownName}'{wireNote} in: {shown} — if the field is sometimes absent, declare it Option<{formatTy ty}>; a type drafted from a sample only sees what the sample had"
                // a null is a `Yaml` node like any other, as `from yaml`
                // reads it [D:json-yaml-field]
                | TNamed("Yaml", []) when isNull -> VUnion("YNull", None)
                | _ when isNull ->
                    failwith
                        $"{who}: field '{shownName}' is null; declare it Option<{formatTy ty}> to allow it, in: {shown}"
                | _ -> readV ty prop

            name, value

        // fields read in declaration order; an anonymous shape takes
        // the wire's order instead [D:record-order] — the one place
        // order comes from data, which is what makes read-modify-write
        // roundtrips hold for shapes the author never declared
        let fields = rdef.Fields |> List.map readField

        let ordered =
            if rdef.Name.StartsWith "{|" then
                let wireIndex (k: string) =
                    let mutable i = 0
                    let mutable found = System.Int32.MaxValue

                    for p in root.EnumerateObject() do
                        if found = System.Int32.MaxValue then
                            (if p.Name = k then
                                 found <- i)

                            i <- i + 1

                    found

                fields |> List.sortBy (fst >> wireIndex)
            else
                fields

        VRecord(rdef.Name, ordered)

    // record and tagged-union tops read the same way at every position
    // below — one element, one dispatch [D:wire-unions]
    let topName =
        match top with
        | TopRec d -> d.Name
        | TopUnion u -> u.Name
        | TopValue ty -> formatTy ty

    let readTopEl (prefix: string) (el: System.Text.Json.JsonElement) =
        match top with
        | TopRec d -> objRow prefix d el
        | TopUnion u -> readUnion prefix u el
        | TopValue _ -> unreachable "the checker admits a value top only under Map< >"

    match top with
    | TopValue ty when wantMap ->
        // a non-record value type [D:map-values]: each property reads
        // as a Map<string, V> field's values do, paths through the key
        match root.ValueKind with
        | System.Text.Json.JsonValueKind.Object ->
            root.EnumerateObject()
            |> Seq.fold (fun m p -> Map.add p.Name (readValue $"[\"{p.Name}\"]" ty p.Value) m) Map.empty
            |> VMap
        | k ->
            failwith
                $"{who}: the top level is a JSON {jsonKindName k}, but the declared type is Map<string, {topName}>, in: {shown}"
    | _ ->

    if wantMap then
        // the ID-keyed object [D:map-string]: the top level is the map
        // — each property value reads as one row; duplicate keys
        // last-win
        match root.ValueKind with
        | System.Text.Json.JsonValueKind.Object ->
            root.EnumerateObject()
            |> Seq.fold
                (fun m p ->
                    if p.Value.ValueKind <> System.Text.Json.JsonValueKind.Object then
                        failwith
                            $"{who}: key \"{p.Name}\" expected an object ({topName}), got {jsonKindName p.Value.ValueKind} in: {shown}"

                    Map.add p.Name (readTopEl $"[\"{p.Name}\"]." p.Value) m)
                Map.empty
            |> VMap
        | System.Text.Json.JsonValueKind.Array ->
            failwith
                $"{who}: the top level is a JSON array, but the declared type is Map<string, {topName}> — declare seq<{topName}> to read an array, in: {shown}"
        | k ->
            failwith
                $"{who}: the top level is a JSON {jsonKindName k}, but the declared type is Map<string, {topName}>, in: {shown}"
    else

        match wantSeq, root.ValueKind with
        | false, System.Text.Json.JsonValueKind.Object -> readTopEl "" root
        | true, System.Text.Json.JsonValueKind.Array ->
            root.EnumerateArray()
            |> Seq.mapi (fun i el ->
                if el.ValueKind <> System.Text.Json.JsonValueKind.Object then
                    failwith
                        $"{who}: array element [{i}] is a JSON {jsonKindName el.ValueKind}, not an object, in: {shown}"
                else
                    readTopEl $"[{i}]." el)
            // forced before the document disposes; then seq for the ctor
            |> List.ofSeq
            |> List.toSeq
            |> VSeq
        | true, System.Text.Json.JsonValueKind.Object ->
            failwith
                $"{who}: expected an array (the declared type is seq<{topName}>); got an object — write from json {topName}, in: {shown}"
        | true, k ->
            failwith
                $"{who}: the top level is a JSON {jsonKindName k}, but the declared type is seq<{topName}>, in: {shown}"
        | false, System.Text.Json.JsonValueKind.Array when who = "from json" ->
            // the suggested seq<T> spelling exists, so the message can
            // point at it
            failwith
                $"{who}: the top level is a JSON array, not an object — declare seq<{topName}> to read it, in: {shown}"
        | false, k ->
            let contract =
                if who = "from json" then
                    "from json T reads one object document"
                else
                    "from jsonl T reads one object per element"

            failwith $"{who}: the top level is a JSON {jsonKindName k}, not an object — {contract}, in: {shown}"

// a string's decode is the one InvalidOperationException a parsed
// document can still raise: an escape naming a lone surrogate
// (`"\ud800"`) is valid JSON grammar but not text — named in weir's
// words, never System.Text.Json's [D:json-boundary]
let private jsonDoc who wantSeq wantMap top defs udefs (shown: string) text =
    try
        jsonDocRead who wantSeq wantMap top defs udefs shown text
    with :? System.InvalidOperationException ->
        failwith $"{who}: a string escape is a lone surrogate (\\uD800-\\uDFFF without its pair), which is not text, in: {shown}"

// a document snippet for error messages: whole if short, truncated if
// not (a joined body can be megabytes; the message stays a message)
let private jsonSnippet (text: string) : string =
    let t = text.Trim()
    if t.Length <= 120 then t else t.Substring(0, 117) + "..."

// what an error cites in place of the document when the declared shape
// holds a Secret [D:from-secret-excerpt]: any excerpt may carry it
let private secretExcerpt = "<excerpt withheld: the type holds a Secret>"

/// does the declared shape hold a Secret anywhere — fields, payloads,
/// type arguments, at any depth [D:from-secret-excerpt]
let private jsonTopHoldsSecret (top: JsonTop) (defs: Map<string, RecordDef>) (udefs: Map<string, UnionDef>) =
    let seen = System.Collections.Generic.HashSet<string>()

    let rec ty (t: Ty) =
        match t with
        | TSecret -> true
        | TSeq e -> ty e
        | TTuple ts -> List.exists ty ts
        | TFun(a, b) -> ty a || ty b
        | TRowVar(_, fs) -> fs |> List.exists (snd >> ty)
        | TNamed(n, args) -> List.exists ty args || named n
        | _ -> false

    and named (n: string) =
        seen.Add n
        && (match Map.tryFind n defs with
            | Some d -> record d
            | None ->
                match Map.tryFind n udefs with
                | Some u -> union u
                | None -> false)

    and record (d: RecordDef) = d.Fields |> List.exists (snd >> ty)
    and union (u: UnionDef) = u.Cases |> List.exists (snd >> Option.exists ty)

    match top with
    | TopRec d ->
        seen.Add d.Name |> ignore
        record d
    | TopUnion u ->
        seen.Add u.Name |> ignore
        union u
    | TopValue t -> ty t

// ---- the read-only XML boundary [D:from-xml] ------------------------------

/// read one XML document under the declared shape [D:from-xml]: the root
/// element is the top record, field names match child elements (matched
/// by local name — a default xmlns is stripped), [<Attr>] reads an attribute,
/// [<Elem "X">] a repeated child, a record a nested element. Every leaf is
/// text (an element's inner text or an attribute value) — no numbers reach
/// here, the checker admits string / Option<string> / record / seq only.
// the XML declaration's encoding name [D:xml-encoding]
let private xmlDeclEncoding =
    System.Text.RegularExpressions.Regex
        @"^\uFEFF?\s*<\?xml\s[^>]*?\bencoding\s*=\s*[""']([A-Za-z][A-Za-z0-9._:-]*)[""']"

// the encodings a declaration may name: CoreLib's alone (UTF-8/16/32,
// US-ASCII, ISO-8859-1) — no code-page provider [D:xml-encoding-builtin]
let private xmlEncoding (who: string) (name: string) : System.Text.Encoding =
    try
        System.Text.Encoding.GetEncoding name
    with _ ->
        failwith
            $"{who}: the document declares encoding '{excerpt name}', which weir cannot decode — UTF-8, UTF-16, UTF-32, US-ASCII and ISO-8859-1 read; convert others to UTF-8 first (iconv -f {excerpt name} -t UTF-8)"

/// the input of `from xml`: text (already decoded as UTF-8) or the
/// document's bytes, which decode per their BOM or declaration
type XmlInput =
    | XmlText of string
    | XmlBytes of byte[]

let private xmlDoc
    (who: string)
    (top: JsonTop)
    (defs: Map<string, RecordDef>)
    (cite: string -> string)
    (input: XmlInput)
    : Value =
    // DTDs are prohibited [D:xml-no-dtd]: no declared entity expands and
    // no external one resolves; XDocument.Parse's defaults would process
    // an internal subset
    let settings =
        System.Xml.XmlReaderSettings(
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            // XDocument.Parse's LoadOptions.None reading
            IgnoreWhitespace = true
        )

    // the declared encoding decides [D:xml-encoding]: text arrived
    // decoded as UTF-8, so a declaration of another encoding reads only
    // when the text is ASCII (the same characters in either); bytes
    // decode per their BOM, else the declaration, else UTF-8
    let text =
        match input with
        | XmlText text ->
            let m = xmlDeclEncoding.Match text

            if m.Success then
                let enc = xmlEncoding who m.Groups[1].Value

                if
                    enc.CodePage <> 65001
                    && text |> Seq.exists (fun c -> int c > 0x7F && c <> '\uFEFF')
                then
                    let tyName =
                        match top with
                        | TopRec d -> d.Name
                        | _ -> "T"

                    failwith
                        $"{who}: the document declares encoding '{excerpt m.Groups[1].Value}', but it arrived as text, which weir decodes as UTF-8 — read its bytes so the declaration decodes them: File.readBytes path |> from xml {tyName}"

            text
        | XmlBytes bytes ->
            let bomLen, bomEnc =
                if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then
                    3, Some(System.Text.UTF8Encoding(false) :> System.Text.Encoding)
                elif bytes.Length >= 4 && bytes[0] = 0xFFuy && bytes[1] = 0xFEuy && bytes[2] = 0uy && bytes[3] = 0uy then
                    4, Some(System.Text.UTF32Encoding(false, false) :> System.Text.Encoding)
                elif bytes.Length >= 4 && bytes[0] = 0uy && bytes[1] = 0uy && bytes[2] = 0xFEuy && bytes[3] = 0xFFuy then
                    4, Some(System.Text.UTF32Encoding(true, false) :> System.Text.Encoding)
                elif bytes.Length >= 2 && bytes[0] = 0xFFuy && bytes[1] = 0xFEuy then
                    2, Some(System.Text.UnicodeEncoding(false, false) :> System.Text.Encoding)
                elif bytes.Length >= 2 && bytes[0] = 0xFEuy && bytes[1] = 0xFFuy then
                    2, Some(System.Text.UnicodeEncoding(true, false) :> System.Text.Encoding)
                elif bytes.Length >= 4 && bytes[0] = 0x3Cuy && bytes[1] = 0uy && bytes[2] = 0x3Fuy && bytes[3] = 0uy then
                    0, Some(System.Text.UnicodeEncoding(false, false) :> System.Text.Encoding)
                elif bytes.Length >= 4 && bytes[0] = 0uy && bytes[1] = 0x3Cuy && bytes[2] = 0uy && bytes[3] = 0x3Fuy then
                    0, Some(System.Text.UnicodeEncoding(true, false) :> System.Text.Encoding)
                else
                    0, None

            let enc =
                match bomEnc with
                | Some e -> e
                | None ->
                    // the declaration is ASCII in every ASCII-compatible
                    // encoding; read it from the head as Latin-1
                    let head = System.Text.Encoding.Latin1.GetString(bytes, 0, min bytes.Length 512)
                    let m = xmlDeclEncoding.Match head

                    if m.Success then
                        xmlEncoding who m.Groups[1].Value
                    else
                        System.Text.UTF8Encoding(false) :> System.Text.Encoding

            // strict: an undecodable byte is an error, never U+FFFD
            // [D:encoding-law]
            let strict =
                System.Text.Encoding.GetEncoding(
                    enc.CodePage,
                    System.Text.EncoderFallback.ExceptionFallback,
                    System.Text.DecoderFallback.ExceptionFallback
                )

            try
                strict.GetString(bytes, bomLen, bytes.Length - bomLen)
            with :? System.Text.DecoderFallbackException ->
                failwith $"{who}: the document is not valid {enc.WebName} (its declared or default encoding)"

    let shown = cite (jsonSnippet text)

    let root =
        try
            use sr = new System.IO.StringReader(text)
            use reader = System.Xml.XmlReader.Create(sr, settings)
            (XDocument.Load reader).Root
        with
        // Prohibit's own error carries no position; the declaration's
        // spelling is what names it
        | :? System.Xml.XmlException when text.Contains "<!DOCTYPE" ->
            failwith
                $"{who}: a DTD (<!DOCTYPE ...>) is outside the XML subset — remove it; only the five predefined entities (&amp; &lt; &gt; &quot; &apos;) and character references read: {shown}"
        | _ ->
            // never System.Xml's words, mirroring the JSON boundary
            failwith $"{who}: not valid XML: {shown}"

    // children / attributes matched by local name — namespaces
    // (msbuild's default xmlns) are ignored so field names stay plain
    // [D:from-xml]
    let childElems (el: XElement) (name: string) =
        el.Elements() |> Seq.filter (fun e -> e.Name.LocalName = name)

    // a scalar or record field takes ONE element — a repeat is a seq
    // field's shape, never silently its first (yaml refuses a duplicate key)
    let childElem (el: XElement) (name: string) (shownName: string) (want: string) =
        match childElems el name |> Seq.truncate 2 |> List.ofSeq with
        | [] -> None
        | [ c ] -> Some c
        | _ ->
            failwith
                $"{who}: element <{name}> repeats in <{el.Name.LocalName}> for field '{shownName}' — declare {want} to read every one: {shown}"

    let attr (el: XElement) (name: string) =
        el.Attributes()
        |> Seq.filter (fun a -> a.Name.LocalName = name)
        |> Seq.tryHead
        |> Option.map (fun a -> a.Value)

    // a text leaf is the element's own text: child elements inside it
    // (mixed content) refuse rather than concatenate [D:from-xml-mixed]
    let textOf (c: XElement) (shownName: string) =
        match c.Elements() |> Seq.tryHead with
        | Some inner ->
            failwith
                $"{who}: element <{c.Name.LocalName}> for field '{shownName}' holds a child element <{inner.Name.LocalName}> — a text field reads an element with text only (mixed content is outside the XML subset): {shown}"
        | None -> c.Value

    // one element -> one row; `prefix` is the dotted path above it
    let rec objRowXml (prefix: string) (rdef: RecordDef) (el: XElement) : Value =
        let readField (name: string, ty: Ty) =
            let shownName = prefix + name

            let value =
                match Types.xmlAttr rdef name with
                // [<Attr>]: an attribute's text — present-or-none for Option
                | Some aname ->
                    match ty, attr el aname with
                    | TNamed("Option", [ TStr ]), Some s -> VUnion("Some", Some(VStr s))
                    | TNamed("Option", [ TStr ]), None -> VUnion("None", None)
                    | TStr, Some s -> VStr s
                    | TStr, None ->
                        failwith
                            $"{who}: missing attribute '{aname}' on <{el.Name.LocalName}> for field '{shownName}' in: {shown}"
                    | _ -> unreachable "the checker restricts [<Attr>] to string / Option<string>"
                | None ->
                    match ty with
                    // a child element's inner text (required / optional)
                    | TStr ->
                        match childElem el name shownName "seq<string>" with
                        | Some c -> VStr(textOf c shownName)
                        | None ->
                            failwith
                                $"{who}: missing element <{name}> in <{el.Name.LocalName}> for field '{shownName}' in: {shown}"
                    | TNamed("Option", [ TStr ]) ->
                        match childElem el name shownName "seq<string>" with
                        | Some c -> VUnion("Some", Some(VStr(textOf c shownName)))
                        | None -> VUnion("None", None)
                    // repeated children: [<Elem "X">] names them, else the
                    // element type (a record) or the field name (strings)
                    | TSeq elem ->
                        let elemName =
                            match elem with
                            | TNamed(n, []) when defs.ContainsKey n -> Types.xmlElem rdef name n
                            | _ -> Types.xmlElem rdef name name

                        // zero-based, as weir's xs[i] [D:paths-zero-based]
                        childElems el elemName
                        |> Seq.mapi (fun i c ->
                            match elem with
                            | TStr -> VStr(textOf c $"{shownName}[{i}]")
                            | TNamed(n, []) when defs.ContainsKey n -> objRowXml $"{shownName}[{i}]." defs[n] c
                            | _ -> unreachable "the checker restricts an XML seq to strings or records")
                        |> List.ofSeq
                        |> List.toSeq
                        |> VSeq
                    // a nested element read as a record
                    | TNamed(n, []) when defs.ContainsKey n ->
                        match childElem el name shownName (if n = name then $"seq<{n}>" else $"[<Elem \"{name}\">] seq<{n}>") with
                        | Some c -> objRowXml (shownName + ".") defs[n] c
                        | None ->
                            failwith
                                $"{who}: missing element <{name}> in <{el.Name.LocalName}> for field '{shownName}' in: {shown}"
                    | _ -> unreachable $"the checker rejects XML field type {formatTy ty}"

            name, value

        VRecord(rdef.Name, rdef.Fields |> List.map readField)

    match top with
    | TopRec d -> objRowXml "" d root
    | TopUnion _
    | TopValue _ -> unreachable "the checker admits only records at the XML boundary"

// ---- the aligned-table boundary [D:from-table] ----------------------------

/// read header-aligned rows (kubectl/docker style) under the declared row
/// record [D:from-table]: the first non-blank line is the header; columns
/// slice by header offsets (a header boundary is a run of 2+ spaces, so
/// `CONTAINER ID` stays one column), never whitespace runs — a cell value
/// with spaces survives. Each field reads its column: [<Wire "…">] matches
/// the raw header verbatim, otherwise the normalized names match
/// (case-insensitive, alphanumerics only). Option reads an empty or
/// `<none>` cell as None; extra columns are ignored; blank lines skip.
let private tableRows (who: string) (def: RecordDef) (lines: string list) : Value =
    match Table.parse (lines |> List.mapi (fun i l -> i + 1, l)) with
    | Error e -> failwith $"{who}: {e}"
    | Ok(cols, rows) ->
        let colArr = List.toArray cols
        let headers = cols |> List.map (fun c -> c.Header) |> String.concat ", "

        // resolve each declared field to its column once, before any row
        let fieldCols =
            def.Fields
            |> List.map (fun (fname, fty) ->
                let wire = Types.wireName def fname

                let hit =
                    if wire <> fname then
                        // the Wire rule: the attribute names the raw header
                        colArr |> Array.tryFindIndex (fun c -> c.Header = wire)
                    else
                        colArr
                        |> Array.tryFindIndex (fun c -> Table.matchKey c.Header = Table.matchKey fname)

                match hit with
                | Some i -> fname, fty, i
                | None ->
                    let want = if wire <> fname then $"'{wire}'" else $"'{fname}'"
                    failwith $"{who}: missing column {want} for field '{fname}' — headers seen: {quoteData headers}")

        let cellValue (line: int) (col: Table.Column) (ty: Ty) (raw: string) : Value =
            let at = $"{who}: line {line}, col {col.Start + 1}: column '{col.Header}'"

            let scalar (t: Ty) (s: string) =
                match t with
                | TStr -> VStr s
                | TInt ->
                    match System.Int64.TryParse s with
                    | true, n -> VInt n
                    | _ -> failwith $"{at}: expected int, got '{quoteData s}'"
                | TFloat ->
                    match parseFloat s with
                    | Ok f -> VFloat f
                    | Error _ -> failwith $"{at}: expected float, got '{quoteData s}'"
                | TBool ->
                    if s = "true" then VBool true
                    elif s = "false" then VBool false
                    else failwith $"{at}: expected bool (exactly true/false), got '{quoteData s}'"
                | t -> unreachable $"the checker restricts table cells to scalars: {formatTy t}"

            match ty with
            | TNamed("Option", [ inner ]) ->
                if Table.isAbsent raw then
                    VUnion("None", None)
                else
                    VUnion("Some", Some(scalar inner raw))
            | t ->
                if Table.isAbsent raw then
                    let shownCell = if raw = "" then "an empty cell" else $"a '{raw}' cell"
                    failwith $"{at}: {shownCell} — declare Option<{formatTy t}> to read it as None"
                else
                    scalar t raw

        VSeq(
            rows
            |> List.map (fun (n, cs) ->
                let cellArr = List.toArray cs

                VRecord(
                    def.Name,
                    fieldCols
                    |> List.map (fun (fname, fty, i) ->
                        let raw = if i < cellArr.Length then cellArr[i] else ""
                        fname, cellValue n colArr[i] fty raw)
                ))
            |> List.toSeq
        )

let private fromAdapter
    (fmt: string)
    (seqOf: bool)
    (mapOf: bool)
    (top: JsonTop)
    (defs: Map<string, RecordDef>)
    (udefs: Map<string, UnionDef>)
    : Value =
    let holdsSecret = jsonTopHoldsSecret top defs udefs
    let cite (excerpt: string) =
        if holdsSecret then secretExcerpt else quoteData excerpt

    match fmt with
    // one document -> T: join the elements back into the text they
    // came from (a pretty-printed body pipes straight in) [D:from-jsonl]
    | "json" ->
        VBuiltin(fun v ->
            match v with
            | VSeq lines ->
                let text =
                    lines
                    |> Seq.map (fun l ->
                        match l with
                        | VStr s -> s
                        | v -> unreachable $"the checker rejects 'from' on non-string elements: {formatValue v}")
                    |> String.concat "\n"

                if text.Trim() = "" then
                    failwith "from json: empty input — expected one JSON document"

                jsonDoc "from json" seqOf mapOf top defs udefs (cite (jsonSnippet text)) text
            | v -> unreachable $"the checker rejects 'from' on {formatValue v}")
    // one document per element -> seq<T> (NDJSON, `to json`'s shape)
    | "jsonl" ->
        VBuiltin(fun v ->
            match v with
            | VSeq lines ->
                // a blank line separates nothing — a trailing newline
                // (a file's last line, `printf '…\n\n'`) is not a document
                VSeq(
                    lines
                    |> Seq.choose (fun l ->
                        match l with
                        | VStr s when s.Trim() = "" -> None
                        | VStr s -> Some(jsonDoc "from jsonl" false false top defs udefs (cite s) s)
                        | v -> unreachable $"the checker rejects 'from' on non-string elements: {formatValue v}")
                )
            | v -> unreachable $"the checker rejects 'from' on {formatValue v}")
    // one document -> T, reading the root element [D:from-xml]; the
    // lines join back into the text they came from, as json does
    | "xml" ->
        VBuiltin(fun v ->
            match v with
            | VSeq lines ->
                let text =
                    lines
                    |> Seq.map (fun l ->
                        match l with
                        | VStr s -> s
                        | v -> unreachable $"the checker rejects 'from' on non-string elements: {formatValue v}")
                    |> String.concat "\n"

                if text.Trim() = "" then
                    failwith "from xml: empty input — expected one XML document"

                xmlDoc "from xml" top defs cite (XmlText text)
            // the document's bytes decode per its BOM or declaration
            // [D:xml-encoding]
            | VBytes bytes ->
                if bytes.Length = 0 then
                    failwith "from xml: empty input — expected one XML document"

                xmlDoc "from xml" top defs cite (XmlBytes bytes)
            | v -> unreachable $"the checker rejects 'from' on {formatValue v}")
    // header-aligned rows -> seq<T> [D:from-table]: the header must be
    // read before any row slices, so the input materializes here
    | "table" ->
        VBuiltin(fun v ->
            match v with
            | VSeq lines ->
                let strs =
                    lines
                    |> Seq.map (fun l ->
                        match l with
                        | VStr s -> s
                        | v -> unreachable $"the checker rejects 'from' on non-string elements: {formatValue v}")
                    |> List.ofSeq

                match top with
                | TopRec def -> tableRows "from table" def strs
                | TopUnion _
                | TopValue _ -> unreachable "the checker admits only records at the table boundary"
            | v -> unreachable $"the checker rejects 'from' on {formatValue v}")
    | f -> unreachable $"the checker rejects unknown format '{f}'"

// ---- the yaml boundary [D:yaml-v1] ----------------------------------------

// shape-directed conversion: the checker packed the resolved target
// tree; every error carries the node's line (the in-house parser's
// positions — something YamlDotNet's messages lacked)
// the typeless scalar/node rules [D:yaml-nodes] — pure and
// dependency-free, so the typed `Yaml`-field read (yamlConvert's
// SNode) and the district/Yaml.parse path share one implementation
let yamlScalarValue (raw: string) (quoted: bool) : Value =
    // the single typeless scalar rule — the district and Yaml.parse
    // agree by construction [D:yaml-nodes]
    if not quoted && raw = "" then
        VUnion("YNull", None)
    elif not quoted && (raw = "true" || raw = "false") then
        VUnion("YBool", Some(VBool(raw = "true")))
    else
        match (if quoted then (false, 0L) else System.Int64.TryParse raw) with
        | true, n -> VUnion("YInt", Some(VInt n))
        | _ ->
            // unquoted float-shaped literals self-type [D:floats-boundaries]
            // — `cpu: 1.5` must not render as "1.5" (same as int;
            // parseFloat refuses non-finite so nan/inf text stays string)
            match (if quoted then Error "" else parseFloat raw) with
            | Ok f -> yamlFloatNode raw f
            | Error _ -> VUnion("YStr", Some(VStr raw))

let rec yamlNodeValue (node: Yaml.Node) : Value =
    // the typeless read [D:yaml-nodes]: parsed structure into the
    // public Yaml union — no tombstone or patch ctor can ever come
    // from here
    match node with
    | Yaml.NNull _ -> VUnion("YNull", None)
    | Yaml.NBlock(text, _) -> VUnion("YStr", Some(VStr text))
    | Yaml.NScalar(raw, quoted, _) -> yamlScalarValue raw quoted
    | Yaml.NSeq(items, _) -> VUnion("YSeq", Some(VSeq(items |> List.map yamlNodeValue |> List.toSeq)))
    | Yaml.NMap(entries, _) ->
        VUnion(
            "YMap",
            Some(VSeq(entries |> List.map (fun (k, v) -> VTuple [ VStr k; yamlNodeValue v ]) |> List.toSeq))
        )

// `localDate line raw`: is this scalar an offset-less TOML datetime
// [D:toml-local-instant] — false for every yaml read
let rec private yamlConvertWith (localDate: int -> string -> bool) (shape: Yaml.Shape) (node: Yaml.Node) : Value =
    let yamlConvert = yamlConvertWith localDate

    match shape, node with
    | Yaml.SOpt _, Yaml.NNull _ -> VUnion("None", None)
    | Yaml.SOpt inner, n -> VUnion("Some", Some(yamlConvert inner n))
    | Yaml.SInt, Yaml.NScalar(raw, quoted, line) ->
        if quoted then
            failwith $"from yaml: line {line}: a quoted scalar is a string; this field expects int"
        else
            match System.Int64.TryParse raw with
            | true, n -> VInt n
            | _ -> failwith $"from yaml: line {line}: expected int, got '{quoteData raw}'"
    | Yaml.SBool, Yaml.NScalar(raw, quoted, line) ->
        // exactly true/false (Env.load's rule: `yes`/`on`/`1` are
        // data, not booleans — the Norway problem cannot fire)
        if quoted then
            failwith $"from yaml: line {line}: a quoted scalar is a string; this field expects bool"
        elif raw = "true" then
            VBool true
        elif raw = "false" then
            VBool false
        else
            failwith $"from yaml: line {line}: expected bool (exactly true/false), got '{quoteData raw}'"
    | Yaml.SFloat, Yaml.NScalar(raw, quoted, line) ->
        if quoted then
            failwith $"from yaml: line {line}: a quoted scalar is a string; this field expects float"
        else
            // parseFloat rejects non-finite; .inf/.nan get a dedicated
            // message — yaml can spell them, weir forbids the value
            match raw.Trim().ToLowerInvariant() with
            | ".inf"
            | "-.inf"
            | "+.inf"
            | ".nan"
            // TOML's spellings [D:from-toml]
            | "inf"
            | "-inf"
            | "nan"
            | "-nan" ->
                failwith
                    $"from yaml: line {line}: '{quoteData raw}' is not representable — weir floats are finite (non-finite results raise; there is no value to read into)"
            | _ ->
                match parseFloat raw with
                | Ok f -> VFloat f
                | Error _ -> failwith $"from yaml: line {line}: expected float, got '{quoteData raw}'"
    | Yaml.SStr, Yaml.NScalar(raw, _, _) -> VStr raw
    | Yaml.SSecret, Yaml.NScalar(raw, _, _) -> VSecret raw
    | Yaml.SCodec Iso8601, Yaml.NScalar(raw, _, line) when localDate line raw ->
        let what =
            if not (raw.Contains ':') then "a local date"
            elif not (raw.Contains '-') then "a local time"
            else "a local date-time"

        failwith
            $"from yaml: line {line}: {quoteData raw} is {what} — it has no offset, so it names no instant; write an offset date-time (2024-03-01T10:00:00Z, or +02:00 for the zone), or read it into a string field"
    | Yaml.SCodec c, Yaml.NScalar(raw, quoted, line) ->
        let num =
            match (if quoted then (false, 0L) else System.Int64.TryParse raw) with
            | true, n -> Some n
            | _ -> None

        decodeCoded $"from yaml: line {line}" c (Some raw) num
    | Yaml.SUuid, Yaml.NScalar(raw, _, line) ->
        match parseUuid raw with
        | Ok g -> VUuid g
        | Error e -> failwith $"from yaml: line {line}: {e}"
    // blockness is quotedness's sibling [D:block-scalars]: a block
    // scalar is unambiguously a string
    | Yaml.SStr, Yaml.NBlock(text, _) -> VStr text
    | Yaml.SSecret, Yaml.NBlock(text, _) -> VSecret text
    | Yaml.SInt, Yaml.NBlock(_, line) ->
        failwith $"from yaml: line {line}: a block scalar is a string; this field expects int"
    | Yaml.SFloat, Yaml.NBlock(_, line) ->
        failwith $"from yaml: line {line}: a block scalar is a string; this field expects float"
    | Yaml.SBool, Yaml.NBlock(_, line) ->
        failwith $"from yaml: line {line}: a block scalar is a string; this field expects bool"
    | Yaml.SRec(name, fields), Yaml.NMap(entries, line) ->
        // extra keys are ignored (as in from-json); a missing or null
        // required field suggests Option (matching json-option)
        let get fname =
            entries |> List.tryFind (fun (k, _) -> k = fname)

        let fieldValues =
            fields
            |> List.map (fun (fname, wire, fshape) ->
                // the wire key matches the document; the field names
                // the record (and the message cites both when they
                // differ) [D:wire-keys]
                let wireNote = if wire <> fname then $" (wire key \"{wire}\")" else ""

                match get wire, fshape with
                | None, Yaml.SOpt _ -> fname, VUnion("None", None)
                | None, Yaml.SSeq _ -> fname, VSeq Seq.empty
                | None, Yaml.SPairs _ -> fname, VSeq Seq.empty
                | None, _ ->
                    failwith
                        $"from yaml: line {line}: missing field '{fname}'{wireNote} in '{name}' — if the field is sometimes absent, declare it Option<…>; a type drafted from a sample only sees what the sample had"
                | Some(_, Yaml.NNull l), (Yaml.SInt | Yaml.SFloat | Yaml.SStr | Yaml.SSecret | Yaml.SBool | Yaml.SUuid | Yaml.SCodec _ | Yaml.SRec _) ->
                    failwith $"from yaml: line {l}: field '{fname}' is null; declare it Option<…> to allow it"
                | Some(_, v), _ -> fname, yamlConvert fshape v)

        VRecord(name, fieldValues)
    // the tag dispatch [D:wire-unions]: the discriminator entry picks
    // the case, and the whole mapping reads as its payload record;
    // unmatched values take [<Other>] or refuse naming the cases; a
    // missing tag is a malformed document, never Other's
    | Yaml.SUnion(uname, tag, ucases, other), Yaml.NMap(entries, line) ->
        match entries |> List.tryFind (fun (k, _) -> k = tag) with
        | None -> failwith $"from yaml: line {line}: missing tag field '{tag}' ({uname})"
        | Some(_, Yaml.NScalar(tagValue, _, _)) ->
            match ucases |> List.tryFind (fun (_, w, _) -> w = tagValue) with
            | Some(c, _, Some payloadShape) -> VUnion(c, Some(yamlConvert payloadShape node))
            | Some(c, _, None) -> VUnion(c, None)
            | None ->
                match other with
                | Some(oc, hasPayload) -> VUnion(oc, (if hasPayload then Some(VStr tagValue) else None))
                | None ->
                    let cases = ucases |> List.map (fun (_, w, _) -> w) |> String.concat ", "

                    failwith
                        $"from yaml: line {line}: tag '{tag}' is '{quoteData tagValue}', which matches no case of {uname} (cases: {cases})"
        | Some(_, n) ->
            failwith $"from yaml: line {Yaml.nodeLine n}: tag field '{tag}' ({uname}) expects a string scalar"
    | Yaml.SSeq inner, Yaml.NSeq(items, _) -> VSeq(items |> List.map (yamlConvert inner) |> List.toSeq)
    // a null where a seq/mapping sits is the empty collection (the
    // yaml idiom: `ports:` with nothing below)
    | Yaml.SSeq _, Yaml.NNull _ -> VSeq Seq.empty
    | Yaml.SPairs inner, Yaml.NMap(entries, _) ->
        VSeq(
            entries
            |> List.map (fun (k, v) -> VTuple [ VStr k; yamlConvert inner v ])
            |> List.toSeq
        )
    | Yaml.SPairs _, Yaml.NNull _ -> VSeq Seq.empty
    // the opaque `Yaml` field reads structure whole [D:yaml-empty-flow]
    // — an empty `{}` is YMap [], an empty `[]` is YSeq [], and any
    // node passes through
    | Yaml.SNode, n -> yamlNodeValue n
    | shape, node ->
        let want =
            match shape with
            | Yaml.SInt -> "an int scalar"
            | Yaml.SFloat -> "a float scalar"
            | Yaml.SStr -> "a string scalar"
            | Yaml.SSecret -> "a string scalar (Secret)"
            | Yaml.SBool -> "a bool scalar"
            | Yaml.SUuid -> "a uuid scalar"
            | Yaml.SCodec c -> $"a [<{codecName c}>] scalar"
            | Yaml.SRec(n, _) -> $"a mapping ({n})"
            | Yaml.SUnion(n, _, _, _) -> $"a mapping ({n})"
            | Yaml.SSeq _ -> "a sequence"
            | Yaml.SPairs _ -> "a mapping"
            | Yaml.SOpt _ -> "an optional value"
            | Yaml.SNode -> "a yaml node"

        let got =
            match node with
            | Yaml.NScalar _ -> "a scalar"
            | Yaml.NBlock _ -> "a block scalar"
            | Yaml.NNull _ -> "null"
            | Yaml.NSeq _ -> "a sequence"
            | Yaml.NMap _ -> "a mapping"

        failwith $"from yaml: line {Yaml.nodeLine node}: expected {want}, got {got}"

let private yamlConvert = yamlConvertWith (fun _ _ -> false)

let private yamlFromImpl (shape: Yaml.Shape) (stream: bool) : Value =
    VBuiltin(fun v ->
        match v with
        | VSeq lines ->
            let numbered =
                lines
                |> Seq.mapi (fun i l ->
                    match l with
                    | VStr s -> i + 1, s
                    | v -> unreachable $"the checker rejects 'from yaml' on non-string elements: {formatValue v}")
                |> List.ofSeq

            match Yaml.parseDocs numbered with
            | Error msg -> failwith $"from yaml: {msg}"
            | Ok docs when stream ->
                // the stream cardinality [D:wire-unions]: N documents,
                // each converted through the same per-document shape —
                // an empty stream is zero documents, not an error
                VSeq(docs |> List.map (yamlConvert shape) |> List.toSeq)
            | Ok [] -> failwith "from yaml: empty input — expected one document"
            | Ok [ doc ] ->
                // one document; the declared shape names the top level
                // [D:yaml-seq] — each mismatch message points at the
                // other spelling
                match shape, doc with
                | Yaml.SSeq _, Yaml.NMap _ ->
                    failwith
                        "from yaml: expected a sequence at the top level (the declared type is seq<…>); got a mapping — write from yaml T"
                | Yaml.SRec(n, _), Yaml.NSeq _ ->
                    failwith $"from yaml: the top level is a sequence, not a mapping — declare seq<{n}> to read it"
                | _ -> yamlConvert shape doc
            | Ok docs ->
                // the one-document spelling stays one-document
                // [D:yaml-seq] — the stream form is the suggested
                // repair
                failwith
                    $"from yaml: reads one document; this input has {List.length docs} documents — read a stream with 'from yaml stream T'"
        | v -> unreachable $"the checker rejects 'from yaml' on {formatValue v}")

/// does a yaml/toml shape hold a Secret anywhere [D:from-secret-excerpt]
let rec private shapeHoldsSecret (shape: Yaml.Shape) : bool =
    match shape with
    | Yaml.SSecret -> true
    | Yaml.SOpt s
    | Yaml.SSeq s
    | Yaml.SPairs s -> shapeHoldsSecret s
    | Yaml.SRec(_, fields) -> fields |> List.exists (fun (_, _, s) -> shapeHoldsSecret s)
    | Yaml.SUnion(_, _, cases, _) -> cases |> List.exists (fun (_, _, p) -> Option.exists shapeHoldsSecret p)
    | _ -> false

// from toml T [D:from-toml]: the toml parser builds the yaml node tree and
// the yaml binder converts it — the binder's messages are relabelled
let private tomlFromImpl (shape: Yaml.Shape) : Value =
    VBuiltin(fun v ->
        match v with
        | VSeq lines ->
            let text =
                lines
                |> Seq.map (fun l ->
                    match l with
                    | VStr s -> s
                    | v -> unreachable $"the checker rejects 'from toml' on non-string elements: {formatValue v}")
                |> String.concat "\n"

            match Toml.parseDocAt text with
            // a parse detail quotes the document [D:from-secret-excerpt]
            | Error(line, _) when shapeHoldsSecret shape ->
                failwith $"from toml: line {line}: not a valid TOML document (detail withheld: the type holds a Secret)"
            | Error(line, msg) -> failwith $"from toml: line {line}: {msg}"
            | Ok doc ->
                try
                    yamlConvertWith (fun line raw -> doc.LocalDates.Contains((line, raw))) shape doc.Root
                with Failure msg when msg.StartsWith "from yaml: " ->
                    failwith (
                        "from toml: "
                        + msg.Substring("from yaml: ".Length).Replace("a quoted scalar is a string", "the value is a string")
                    )
        | v -> unreachable $"the checker rejects 'from toml' on {formatValue v}")

// the renderer: value-driven (records/seqs/scalars/Option/Yaml nodes).
// Record fields render alphabetically (the VRecord representation's
// existing rule — same as to json); YMap preserves its own order (the
// user-controlled escape). A None field omits its key; a None element
// renders `null` (both per the json-option split).
type private Rendered =
    | Inline of string
    | Block of string list
    // a literal block scalar [D:block-scalars]: the header rides the
    // key/item line, content lines indent one level under it
    | BlockScalar of header: string * content: string list

// a string renders as a block scalar when it holds newlines (and no
// wilder control characters) [D:block-scalars]; the form is
// deterministic — one trailing newline is `|`, none is `|-`; strings
// with more have no form in the subset (`|+` is rejected) and fall
// back below, since dropping bytes is the one thing a renderer must
// never do
let private renderString (s: string) : Rendered =
    let tame =
        s |> Seq.forall (fun c -> not (System.Char.IsControl c) || c = '\n' || c = '\t')

    // multiple trailing newlines have no block form in the subset (`|+`
    // is rejected) — they fall back to the quoted-with-escapes spelling:
    // valid, exact, round-trips; every legal string stays renderable
    // [D:content-bytes]. A content line starting with space/tab also
    // falls back: block content indentation is detected from the first
    // non-empty line, and the explicit indentation indicator that a
    // leading-whitespace line needs is outside the subset — block form
    // there writes YAML the subset refuses to read
    let noIndentedContent =
        s.Split '\n'
        |> Array.forall (fun l -> not (l.StartsWith " " || l.StartsWith "\t"))

    if
        s.Contains '\n'
        && tame
        && s.TrimEnd '\n' <> ""
        && not (s.EndsWith "\n\n")
        && noIndentedContent
    then
        let keep = s.EndsWith "\n"
        let body = if keep then s.Substring(0, s.Length - 1) else s
        BlockScalar((if keep then "|" else "|-"), body.Split '\n' |> List.ofArray)
    else
        Inline(Yaml.renderScalar s)

// nesting ceiling [D:yaml-depth]: yamlRender recurses once per level,
// so a value built iteratively (a Seq.fold nesting a YSeq) overflows the
// native stack with an uncatchable StackOverflow — the tool must never
// crash on hostile data. 100 sits far above any real tree (kubectl nests
// ~10) and safely below the stack crash floor: yamlRender's heavy frames
// overflow a small-stack worker/test thread at ~600 nesting levels, so the
// bound matches the show renderer's MaxDepth rather than a higher number
// the stack cannot reach. The guard turns the crash into a clean error.
let private yamlMaxDepth = 100

let rec private yamlRender
    (renames: Map<string, Map<string, string>>)
    (unions: Map<string, string * string * bool>)
    (depth: int)
    (guide: Yaml.Shape option)
    (v: Value)
    : Rendered =
    if depth > yamlMaxDepth then
        failwith $"to yaml: the value nests deeper than {yamlMaxDepth} — the emitter needs finite trees"

    let indent2 (lines: string list) =
        lines |> List.map (fun l -> if l = "" then "" else "  " + l)

    // a None record field omits its key [D:json-option]'s yaml face; a
    // None pair value is the entry's data and writes null, as to json
    // does [D:yaml-pair-none]
    let renderMapWith (omitNone: bool) (entries: (string * Value * Yaml.Shape option) list) : string list =
        entries
        |> List.collect (fun (k, v, g) ->
            match v with
            | VUnion("None", None) when omitNone -> []
            | _ ->
                let key = Yaml.renderScalar k

                match yamlRender renames unions (depth + 1) g v with
                | Inline "" -> [ $"{key}:" ]
                | Inline s -> [ $"{key}: {s}" ]
                | Block lines -> $"{key}:" :: indent2 lines
                | BlockScalar(h, content) -> $"{key}: {h}" :: indent2 content)

    let renderMap = renderMapWith true

    let renderSeq (g: Yaml.Shape option) (items: Value list) : string list =
        items
        |> List.collect (fun item ->
            match yamlRender renames unions (depth + 1) g item with
            | Inline "" -> [ "- null" ]
            | Inline s -> [ $"- {s}" ]
            // a sequence item nests block-wise under a bare `-`
            // [D:yaml-empty-typed] — the compact `- - x` is outside the
            // subset the reader takes
            | Block(first :: _ as lines) when first = "-" || first.StartsWith "- " -> "-" :: indent2 lines
            | Block lines ->
                match lines with
                | [] -> [ "-" ]
                | first :: rest -> ($"- {first}") :: (rest |> List.map (fun l -> "  " + l))
            | BlockScalar(h, content) -> ($"- {h}") :: indent2 content)

    // a mapping whose every entry is omitted (an all-None record) is the
    // empty flow mapping — never no text at all, which reads back as
    // null and drops a stream document [D:yaml-empty-doc]
    let mapping (lines: string list) =
        if lines.IsEmpty then Inline "{}" else Block lines

    // the child guides [D:yaml-empty-typed]
    let fieldGuide (f: string) =
        match guide with
        | Some(Yaml.SRec(_, fs)) -> fs |> List.tryPick (fun (n, _, sh) -> if n = f then Some sh else None)
        | _ -> None

    let itemGuide, valueGuide =
        match guide with
        | Some(Yaml.SSeq g) -> Some g, None
        | Some(Yaml.SPairs g) -> None, Some g
        | _ -> None, None

    match v with
    | VInt n -> Inline(string n)
    | VFloat f -> Inline(formatFloat f)
    | VDur n -> Inline(formatDuration n)
    | VSize b -> Inline(formatSize b)
    | VBool b -> Inline(if b then "true" else "false")
    | VStr s -> renderString s
    // the canonical text [D:wire-table], quoted when YAML would misread it
    | VUuid g -> renderString (formatUuid g)
    | VUnion("YStr", Some(VStr s)) -> renderString s
    | VUnion("YInt", Some(VInt n)) -> Inline(string n)
    | VUnion("YFloat", Some(VFloat f)) -> Inline(yamlBigIntText v |> Option.defaultWith (fun () -> formatFloat f))
    | VUnion("YBool", Some(VBool b)) -> Inline(if b then "true" else "false")
    | VUnion("YNull", None) -> Inline ""
    // empty Yaml collections render as the empty flow forms so they
    // round-trip [D:yaml-empty-flow] — a bare `key:` reads back as null
    | VUnion("YSeq", Some(VSeq items)) when Seq.isEmpty items -> Inline "[]"
    | VUnion("YMap", Some(VSeq pairs)) when Seq.isEmpty pairs -> Inline "{}"
    | VUnion("YSeq", Some(VSeq items)) -> Block(renderSeq None (List.ofSeq items))
    | VUnion("YMap", Some(VSeq pairs)) ->
        let entries =
            pairs
            |> Seq.map (fun p ->
                match p with
                | VTuple [ VStr k; v ] -> k, v, None
                | v -> unreachable $"the checker rejects YMap over {formatValue v}")
            |> List.ofSeq

        refuseDupKeys "to yaml" (entries |> List.map (fun (k, _, _) -> k))
        Block(renderMap entries)
    | VUnion("Some", Some inner) ->
        let g =
            match guide with
            | Some(Yaml.SOpt g) -> Some g
            | _ -> None

        yamlRender renames unions (depth + 1) g inner
    | VUnion("None", None) -> Inline "null" // element position; fields omit above
    // a tagged case renders its payload with the tag entry first
    // [D:wire-unions]; the [<Other>] case refuses — nothing faithful
    // can be written
    | VUnion(c, payload) when unions.ContainsKey c ->
        let tagField, tagValue, isOther = unions[c]

        if isOther then
            failwith
                $"to yaml: '{c}' is the [<Other>] fallback — it names a tag that matched no case, and nothing faithful can be written from it"

        let caseGuide =
            match guide with
            | Some(Yaml.SUnion(_, _, cases, _)) -> cases |> List.tryPick (fun (n, _, p) -> if n = c then p else None)
            | _ -> None

        let payloadEntries =
            match payload with
            | Some(VRecord(rname, fields)) ->
                let rens = Map.tryFind rname renames |> Option.defaultValue Map.empty

                let g (f: string) =
                    match caseGuide with
                    | Some(Yaml.SRec(_, fs)) -> fs |> List.tryPick (fun (n, _, sh) -> if n = f then Some sh else None)
                    | _ -> None

                fields
                |> List.map (fun (f, fv) -> (Map.tryFind f rens |> Option.defaultValue f), fv, g f)
            | None -> []
            | Some v -> unreachable $"the declaration law rejects a tagged payload of {formatValue v}"

        Block(renderMap ((tagField, VStr tagValue, None) :: payloadEntries))
    | VRecord(rname, fields) ->
        // wire keys on the yaml wire too [D:wire-keys]
        let rens = Map.tryFind rname renames |> Option.defaultValue Map.empty

        mapping (
            renderMap (
                fields
                |> List.map (fun (f, fv) -> (Map.tryFind f rens |> Option.defaultValue f), fv, fieldGuide f)
            )
        )
    | VSeq items ->
        let items = List.ofSeq items

        // a pair-seq is one mapping (the seq<string * _> rule)
        let asPairs =
            items
            |> List.map (fun i ->
                match i with
                | VTuple [ VStr k; v ] -> Some(k, v)
                | _ -> None)

        if not items.IsEmpty && asPairs |> List.forall Option.isSome then
            let entries = asPairs |> List.map (fun p -> let k, v = Option.get p in k, v, valueGuide)
            refuseDupKeys "to yaml" (entries |> List.map (fun (k, _, _) -> k))
            mapping (renderMapWith false entries)
        elif items.IsEmpty then
            // an empty seq is a flow empty at any depth, so `Some []`
            // reads back `Some []`; the type picks the form
            // [D:yaml-empty-typed] — a seq<string * _> is the empty
            // mapping, any other seq the empty sequence
            match guide with
            | Some(Yaml.SPairs _) -> Inline "{}"
            | _ -> Inline "[]"
        else
            Block(renderSeq itemGuide items)
    | v -> unreachable $"the checker rejects 'to yaml' on {formatValue v}"

let private yamlToLines
    (renames: Map<string, Map<string, string>>)
    (unions: Map<string, string * string * bool>)
    (guide: Yaml.Shape option)
    (v: Value)
    : string list =
    match yamlRender renames unions 0 guide v with
    | Inline s -> [ s ]
    | Block lines -> lines
    | BlockScalar(h, content) -> h :: (content |> List.map (fun l -> if l = "" then "" else "  " + l))

let private yamlToImpl
    (renames: Map<string, Map<string, string>>)
    (unions: Map<string, string * string * bool>)
    (stream: bool)
    (guide: Yaml.Shape option)
    : Value =
    VBuiltin(fun v ->
        match v with
        // `to yaml stream` [D:yaml-seq-doc]: one document per element
        // — the bundle write. Without the keyword a seq is a single
        // sequence document (yamlRender's own VSeq arm, matching
        // json's array), and a pair-seq is a single mapping document.
        | VSeq items when stream ->
            let docs = items |> Seq.map (yamlToLines renames unions guide) |> List.ofSeq

            let lines =
                match docs with
                | [] -> []
                | first :: rest -> first @ (rest |> List.collect (fun d -> "---" :: d))

            VSeq(lines |> List.map VStr |> List.toSeq)
        | v -> VSeq(yamlToLines renames unions guide v |> List.map VStr |> List.toSeq))

let scalarString (what: string) (v: Value) : string =
    match v with
    | VStr s -> s
    // a Secret splices to argv in the clear [D:secret]: `curl -H
    // $auth` needs the real value. print/printerr reject Secret at
    // the type (printArgTy), so this arm is reached only via argv —
    // marked so argv-rendering messages mask it [D:secret-argv-mask]
    | VSecret s ->
        Proc.markSecretWord s
        s
    | VUuid g -> formatUuid g
    | VInt n -> string n
    | VBool true -> "true"
    | VBool false -> "false"
    | VFloat f -> formatFloat f
    | v -> unreachable $"the checker rejects {what} {formatValue v}"

let rec private tryBind (p: Pattern) (v: Value) : (string * Value) list option =
    match p.PKind, v with
    | PWildcard, _ -> Some []
    | PVar name, _ -> Some [ name, v ]
    | PBool b, VBool v -> if b = v then Some [] else None
    | PBool _, v -> unreachable $"the checker rejects bool patterns on {formatValue v}"
    | PInt n, VInt v -> if n = v then Some [] else None
    | PInt _, v -> unreachable $"the checker rejects int patterns on {formatValue v}"
    | PStr s, VStr v -> if s = v then Some [] else None
    | PStr _, v -> unreachable $"the checker rejects string patterns on {formatValue v}"
    | PUnit, _ -> Some []
    | PRecord fields, VRecord(_, vfields) ->
        // irrefutable by checking [D:record-patterns]: every field
        // exists (checked) and every sub-pattern binds — the fold can
        // only ever produce Some
        fields
        |> List.fold
            (fun acc ((f, _), sub) ->
                acc
                |> Option.bind (fun bs ->
                    match recTryGet f vfields with
                    | Some v -> tryBind sub v |> Option.map (fun b -> bs @ b)
                    | None -> unreachable $"the checker guarantees record-pattern field '{f}'"))
            (Some [])
    | PRecord _, v -> unreachable $"the checker rejects record patterns on {formatValue v}"
    | PTuple ps, VTuple vs when List.length ps = List.length vs ->
        List.zip ps vs
        |> List.fold
            (fun acc (subP, subV) -> acc |> Option.bind (fun bs -> tryBind subP subV |> Option.map (fun b -> bs @ b)))
            (Some [])
    | PTuple _, v -> unreachable $"the checker rejects tuple patterns on {formatValue v}"
    | PCase(ctor, None), VUnion(case, None) -> if ctor = case then Some [] else None
    | PCase(ctor, Some argPat), VUnion(case, Some payload) -> if ctor = case then tryBind argPat payload else None
    | PCase _, VUnion _ -> None
    | PCase _, v -> unreachable $"the checker rejects constructor patterns on {formatValue v}"
    | PRegex(pat, _, _, binder), VStr s ->
        // the cached instance from check time [D:regex-pattern]; group
        // i binds leaf i (an unmatched optional group binds "")
        (match compileRegex pat with
         | Error msg -> unreachable $"the checker rejects invalid regex literals: {msg}"
         | Ok rx ->
             let m = regexGuarded pat s (fun () -> rx.Match s)

             if not m.Success then
                 None
             else
                 let group (i: int) = VStr m.Groups[i].Value

                 match binder.PKind with
                 | PUnit
                 | PWildcard -> Some []
                 | PVar n -> Some [ n, group 1 ]
                 | PTuple ps ->
                     ps
                     |> List.mapi (fun i sp -> sp, group (i + 1))
                     |> List.choose (fun (sp, v) ->
                         match sp.PKind with
                         | PVar n -> Some(n, v)
                         | _ -> None)
                     |> Some
                 | _ -> unreachable "the checker constrains Regex binders to unit/name/tuple")
    | PRegex _, v -> unreachable $"the checker rejects Regex patterns on {formatValue v}"
    // seq patterns [D:seq-patterns]: probes pull from the match-site
    // cache (see TEMatch) — bounded force, memoize-once
    | PSeqNil, VSeq items -> if Seq.isEmpty items then Some [] else None
    | PCons(h, t), VSeq items ->
        (match items |> Seq.truncate 1 |> List.ofSeq with
         | [ first ] ->
             tryBind h first
             |> Option.bind (fun hb -> tryBind t (VSeq(items |> Seq.skip 1)) |> Option.map (fun tb -> hb @ tb))
         | _ -> None)
    | PSeqList ps, VSeq items ->
        let probe = items |> Seq.truncate (List.length ps + 1) |> List.ofSeq

        if List.length probe <> List.length ps then
            None
        else
            List.zip ps probe
            |> List.fold
                (fun acc (p, v) -> acc |> Option.bind (fun bs -> tryBind p v |> Option.map (fun b -> bs @ b)))
                (Some [])
    | (PSeqNil | PCons _ | PSeqList _), v -> unreachable $"the checker rejects seq patterns on {formatValue v}"


// binder patterns are irrefutable by checking, so the bind always
// succeeds — the None arm is the standard checker-guarantee marker
let bindPattern (p: Pattern) (v: Value) : (string * Value) list =
    match tryBind p v with
    | Some bs -> bs
    | None -> unreachable $"the checker guarantees binder patterns match; got {formatValue v}"

let private wrapOpt (ty: Ty) (v: Value) : Value =
    match ty with
    | TNamed("Option", _) -> VUnion("Some", Some v)
    | _ -> v

// ---- Args.load [D:typed-argv] ------------------------------------
// collect-then-raise over Session.ScriptArgs; --help short-circuits
// before validation (help must work on invalid invocations)

// the <value> hint on a value-taking flag [D:argv-help-slots]: every
// non-bool field takes a value, so every one gets a slot named for its
// type (bare and Option alike — an optional still takes a value). bool
// is presence-only, so it never shows one.
let private argvValueSlot (ty: Ty) : string =
    match ty with
    | TInt
    | TNamed("Option", [ TInt ]) -> " <int>"
    | TStr
    | TNamed("Option", [ TStr ]) -> " <string>"
    | TFloat
    | TNamed("Option", [ TFloat ]) -> " <float>"
    | TDur
    | TNamed("Option", [ TDur ]) -> " <duration>"
    | TSize
    | TNamed("Option", [ TSize ]) -> " <size>"
    | TInstant
    | TNamed("Option", [ TInstant ]) -> " <instant>"
    | TSecret
    | TNamed("Option", [ TSecret ]) -> " <secret>"
    | _ -> ""

let private argvUsageLinesWith (flagShorts: Map<string, string>) (def: RecordDef) : string list =
    def.Fields
    |> List.map (fun (f, ty) ->
        let flag = "--" + Argv.kebabFlag f

        let short =
            match Map.tryFind flag flagShorts with
            | Some sh -> $"-{sh}, "
            | None -> "    "

        let left = $"  {short}{flag}{argvValueSlot ty}"

        let need =
            match ty, Argv.defaultOf def f with
            | TBool, Some(ABool true) -> $"default: on — --no-{Argv.kebabFlag f} disables"
            | TBool, _ -> ""
            | TNamed("Option", _), _ -> "optional"
            | _, Some(AStr s) -> $"default: {s}"
            | _, Some(AInt n) -> $"default: {n}"
            | _, Some(ADur n) -> $"default: {formatDuration n}"
            | _, Some(AFloat fl) -> $"default: {formatFloat fl}"
            | _, Some(ASize b) -> $"default: {formatSize b}"
            | _, _ -> "required"

        let right =
            [ need
              match Argv.docOf def f with
              | Some d -> d
              | None -> "" ]
            |> List.filter (fun s -> s <> "")
            |> String.concat " — "

        if right = "" then left else sprintf "%-30s%s" left right)

let private argvUsageLines (def: RecordDef) : string list =
    argvUsageLinesWith (fst (Argv.shortTables def)) def

// the per-case flag scope [D:shared-flags]: shared and payload fields
// together — short derivation runs over the union of both, so a
// cross-tier contest (-q for --quiet and --query) derives for neither
// in that scope
let private scopeDef (sharedDef: RecordDef) (payloadDef: RecordDef option) : RecordDef =
    match payloadDef with
    | Some pd ->
        { sharedDef with
            Fields = sharedDef.Fields @ pd.Fields
            Attrs = pd.Attrs |> Map.fold (fun m k v -> Map.add k v m) sharedDef.Attrs
            // the two-tier help draws --help text from both tiers [D:doc-help]
            Docs = pd.Docs |> Map.fold (fun m k v -> Map.add k v m) sharedDef.Docs }
    | None -> sharedDef

// pass 1 of the shared-flags scan: shared flags float, and the first
// non-flag token anchors as the case selector (an unknown flag
// consumes no value, as everywhere else)
let private argvFindCase (sharedDef: RecordDef) (argv: string list) : (int * string) option =
    let sharedLong =
        sharedDef.Fields
        |> List.map (fun (f, ty) -> "--" + Argv.kebabFlag f, ty)
        |> Map.ofList

    let _, sharedShorts = Argv.shortTables sharedDef

    let flagTy (t: string) =
        if t.StartsWith "--" then
            Map.tryFind t sharedLong
        elif t.StartsWith "-" && t.Length = 2 then
            match Map.tryFind (t.Substring 1) sharedShorts with
            | Some(ShortOf flag) -> Map.tryFind flag sharedLong
            | _ -> None
        else
            None

    let rec go i (ts: string list) =
        match ts with
        | [] -> None
        | t :: rest ->
            match flagTy t with
            | Some ty when ty <> TBool ->
                (match rest with
                 | _ :: r -> go (i + 2) r
                 | [] -> None)
            | Some _ -> go (i + 1) rest
            | None when t.StartsWith "-" -> go (i + 1) rest
            | None -> Some(i, t)

    go 0 argv

// usage names the invoked script [D:argv-help-slots]: `usage: deploy.weir
// [flags]`, the convention every --help follows. The name is the entry
// script's basename (script-only); the REPL/-e leave it empty and usage
// falls back to the bare form.
let private usageProg () : string =
    match Session.EntryPath with
    | "" -> ""
    | p -> System.IO.Path.GetFileName p + " "

let private argvUsage (target: ArgsTarget) (argv: string list) : string =
    match target with
    | ArgsRecord def -> String.concat "\n" ($"usage: {usageProg ()}[flags]" :: argvUsageLines def)
    | ArgsUnion(udef, payloads) ->
        let caseLines = udef.Cases |> List.map (fun (c, _) -> "  " + c.ToLowerInvariant())

        let blocks =
            udef.Cases
            |> List.collect (fun (c, _) ->
                match Map.tryFind c payloads with
                | Some rdef when not rdef.Fields.IsEmpty -> $"{c.ToLowerInvariant()} flags:" :: argvUsageLines rdef
                | _ -> [])

        // case-scoped help when the case token leads [D:shared-flags],
        // as the shared-flags twin scopes it — no global flags precede it
        let scoped =
            argv
            |> List.filter (fun t -> t <> "--help" && t <> "-h")
            |> List.tryHead
            |> Option.bind (fun tok -> udef.Cases |> List.tryFind (fun (c, _) -> c.ToLowerInvariant() = tok))

        match scoped with
        | Some(c, _) ->
            let name = c.ToLowerInvariant()

            match Map.tryFind c payloads with
            | Some rdef when not rdef.Fields.IsEmpty ->
                String.concat
                    "\n"
                    ([ $"usage: {usageProg ()}{name} [flags]"; $"{name} flags:" ] @ argvUsageLines rdef)
            | _ -> $"usage: {usageProg ()}{name}"
        | None ->
            String.concat "\n" ([ $"usage: {usageProg ()}<command> [flags]"; "commands:" ] @ caseLines @ blocks)
    | ArgsShared(outer, uf, udef, payloads) ->
        let sharedDef = Argv.sharedOf outer uf

        let payloadOf c (p: Ty option) =
            if p.IsSome then Map.tryFind c payloads else None

        let scopeShortsFor c p =
            fst (Argv.shortTables (scopeDef sharedDef (payloadOf c p)))

        let caseBlock c p =
            match payloadOf c p with
            | Some rdef when not rdef.Fields.IsEmpty ->
                $"{c.ToLowerInvariant()} flags:" :: argvUsageLinesWith (scopeShortsFor c p) rdef
            | _ -> []

        // case-scoped help when a case token rides along [D:shared-flags]
        let scoped =
            argvFindCase sharedDef (argv |> List.filter (fun t -> t <> "--help" && t <> "-h"))
            |> Option.bind (fun (_, tok) -> udef.Cases |> List.tryFind (fun (c, _) -> c.ToLowerInvariant() = tok))

        match scoped with
        | Some(c, p) ->
            String.concat
                "\n"
                ([ $"usage: {usageProg ()}{c.ToLowerInvariant()} [flags]"; "global options:" ]
                 @ argvUsageLinesWith (scopeShortsFor c p) sharedDef
                 @ caseBlock c p)
        | None ->
            // the global section shows a derived short only when it holds
            // in every case scope (explicit shorts always hold)
            let sharedOwn, _ = Argv.shortTables sharedDef

            let stable =
                sharedOwn
                |> Map.filter (fun flag letter ->
                    udef.Cases
                    |> List.forall (fun (c, p) ->
                        let _, scopeIdx = Argv.shortTables (scopeDef sharedDef (payloadOf c p))

                        match Map.tryFind letter scopeIdx with
                        | Some(ShortOf f) -> f = flag
                        | _ -> false))

            let caseLines = udef.Cases |> List.map (fun (c, _) -> "  " + c.ToLowerInvariant())

            let blocks = udef.Cases |> List.collect (fun (c, p) -> caseBlock c p)

            String.concat
                "\n"
                ([ $"usage: {usageProg ()}[global flags] <command> [flags]"; "global options:" ]
                 @ argvUsageLinesWith stable sharedDef
                 @ [ "commands:" ]
                 @ caseLines
                 @ blocks)

// the three argv-boundary rules — one implementation each, shared by
// the record and shared-flags twins [D:argv-rules]. The accumulators
// arrive as parameters, never closure captures: problem order stays
// each caller's own (scan order, then declaration-order fills —
// pinned exactly in e2e).

// the resting-point fill [D:default-attr]: run-time Value construction
// lives here in Eval; the Default policy it consumes (Argv.defaultOf)
// is check-time schema, shared in Argv.fs beside the Args/Env flip —
// the check/run split is unchanged by the unification
let private argvFill
    (problems: ResizeArray<string>)
    (def: RecordDef)
    (values: System.Collections.Generic.Dictionary<string, Value>)
    : (string * Value) list =
    def.Fields
    |> List.map (fun (f, ty) ->
        match values.TryGetValue f with
        | true, v -> f, v
        | false, _ ->
            match Argv.defaultOf def f with
            | Some(AStr s) -> f, VStr s
            | Some(AInt n) -> f, VInt n
            | Some(AFloat fl) -> f, VFloat fl
            | Some(ABool b) -> f, VBool b
            | Some(ADur n) -> f, VDur n
            | Some(ASize b) -> f, VSize b
            | None ->
                match ty with
                | TBool -> f, VBool false
                | TNamed("Option", _) -> f, VUnion("None", None)
                | _ ->
                    problems.Add $"missing required flag '--{Argv.kebabFlag f}'"
                    f, VUnit)

// repeats of one spelling stay the given-twice error; opposite
// polarities name both spellings
let private argvDup
    (problems: ResizeArray<string>)
    (seen: System.Collections.Generic.HashSet<string>)
    (polarity: System.Collections.Generic.Dictionary<string, bool>)
    (f: string)
    (neg: bool)
    (flagTok: string)
    =
    if not (seen.Add f) then
        let prior =
            (match polarity.TryGetValue f with
             | true, p -> p
             | _ -> false)

        if prior <> neg then
            problems.Add $"'--{Argv.kebabFlag f}' and '--no-{Argv.kebabFlag f}' are both given"
        else
            problems.Add $"'{flagTok}' is given twice"

    polarity[f] <- neg

let private argvParseValue
    (problems: ResizeArray<string>)
    (values: System.Collections.Generic.Dictionary<string, Value>)
    (f: string)
    (ty: Ty)
    (flagTok: string)
    (raw: string)
    =
    match ty with
    | TInt
    | TNamed("Option", [ TInt ]) ->
        match System.Int64.TryParse raw with
        | true, n -> values[f] <- wrapOpt ty (VInt n)
        | _ -> problems.Add $"{flagTok} is not an int ('{raw}')"
    | TDur
    | TNamed("Option", [ TDur ]) ->
        match parseDurationMs raw with
        | Ok n -> values[f] <- wrapOpt ty (VDur n)
        | Error e -> problems.Add $"{flagTok}: {e}"
    | TInstant
    | TNamed("Option", [ TInstant ]) ->
        match parseInstantMs raw with
        | Ok n -> values[f] <- wrapOpt ty (VInstant n)
        | Error e -> problems.Add $"{flagTok}: {e}"
    | TSize
    | TNamed("Option", [ TSize ]) ->
        match parseSize raw with
        | Ok b -> values[f] <- wrapOpt ty (VSize b)
        | Error e -> problems.Add $"{flagTok}: {e}"
    | TFloat
    | TNamed("Option", [ TFloat ]) ->
        match parseFloat raw with
        | Ok fl -> values[f] <- wrapOpt ty (VFloat fl)
        | Error e -> problems.Add $"{flagTok}: {e}"
    | TSecret
    | TNamed("Option", [ TSecret ]) ->
        // the boundary wraps [D:secret]: a Secret field's token is secret
        // from the moment it enters, so it is VSecret, never a bare VStr
        values[f] <- wrapOpt ty (VSecret raw)
    | _ -> values[f] <- wrapOpt ty (VStr raw)

// an unknown long flag's message; `--name=value` for a known `--name`
// teaches the separate-argument spelling (weir's argv has no `=` form).
// A value is echoed only for a known non-Secret flag [D:args-secret-echo]:
// a Secret field's shows as ***, an unknown flag's not at all (it may be
// a misspelled Secret).
let private unknownFlagMessage (t: string) (known: string list) (tyOf: string -> Ty option) : string =
    match t.IndexOf '=' with
    | i when i > 2 && List.contains (t.Substring(0, i)) known ->
        let name, value = t.Substring(0, i), t.Substring(i + 1)

        match tyOf name with
        | Some TBool -> $"unknown flag '{t}' — '{name}' is a presence flag and takes no value"
        | Some TSecret
        | Some(TNamed("Option", [ TSecret ])) ->
            $"unknown flag '{quoteData name}=***' — a flag's value is the next argument: write '{quoteData name} <value>'"
        | _ -> $"unknown flag '{t}' — a flag's value is the next argument: write '{name} {value}'"
    | i when i > 2 ->
        let name = t.Substring(0, i)
        $"unknown flag '{quoteData name}=…'{didYouMean name known}"
    | _ -> $"unknown flag '{t}'{didYouMean t known}"

let private argvParseRecord (label: string) (def: RecordDef) (tokens: string list) : Value =
    // minted --no-X twins ride the index as negative entries
    // [D:default-attr]; they join did-you-mean via the same list
    let flagged =
        (def.Fields |> List.map (fun (f, ty) -> "--" + Argv.kebabFlag f, (f, ty, false)))
        @ (Argv.mintedFlags def |> List.map (fun (f, m) -> "--" + m, (f, TBool, true)))

    let longIndex = Map.ofList flagged
    let _, shortIndex = Argv.shortTables def
    let problems = ResizeArray<string>()
    let values = System.Collections.Generic.Dictionary<string, Value>()
    let seen = System.Collections.Generic.HashSet<string>()
    let polarity = System.Collections.Generic.Dictionary<string, bool>()

    // repeats of one spelling stay the given-twice error; opposite
    // polarities name both spellings
    let dup = argvDup problems seen polarity

    let parseValue = argvParseValue problems values

    let rec go tokens =
        match tokens with
        | [] -> ()
        | (t: string) :: rest when t.StartsWith "--" ->
            match Map.tryFind t longIndex with
            | Some(f, ty, neg) -> consume f ty neg t rest
            | None ->
                let tyOf n =
                    Map.tryFind n longIndex |> Option.map (fun (_, ty, _) -> ty)

                problems.Add(unknownFlagMessage t (flagged |> List.map fst) tyOf)
                go rest
        | t :: rest when t.StartsWith "-" && t.Length = 2 ->
            match Map.tryFind (t.Substring 1) shortIndex with
            | Some(ShortOf flag) ->
                let f, ty, neg = Map.find flag longIndex
                consume f ty neg t rest
            | Some(AmbiguousShort candidates) ->
                problems.Add $"""'{t}' is ambiguous: {String.concat ", " candidates}"""
                go rest
            | None ->
                problems.Add $"unknown flag '{t}'"
                go rest
        | t :: rest ->
            problems.Add $"unexpected argument '{t}'"
            go rest

    and consume f ty neg flagTok rest =
        dup f neg flagTok

        match ty with
        | TBool ->
            values[f] <- VBool(not neg)
            go rest
        | _ ->
            match rest with
            | raw :: rest' ->
                parseValue f ty flagTok raw
                go rest'
            | [] -> problems.Add $"flag '{flagTok}' needs a value"

    go tokens

    let fields = argvFill problems def values

    if problems.Count > 0 then
        failwith ($"{label}: " + String.concat "; " problems)

    VRecord(def.Name, fields)

// the shared-flags load [D:shared-flags]: shared flags float anywhere
// on the line; the first non-flag token anchors the case; payload
// flags bind only after it. Both tiers collect into one boundary
// error.
let private argvLoadShared
    (outer: RecordDef)
    (unionField: string)
    (udef: UnionDef)
    (payloads: Map<string, RecordDef>)
    (argv: string list)
    : Value =
    let label = $"Args.load {outer.Name}"
    let sharedDef = Argv.sharedOf outer unionField

    let sharedLong =
        (sharedDef.Fields
         |> List.map (fun (f, ty) -> "--" + Argv.kebabFlag f, (f, ty, false)))
        @ (Argv.mintedFlags sharedDef
           |> List.map (fun (f, m) -> "--" + m, (f, TBool, true)))
        |> Map.ofList

    let caseTable = udef.Cases |> List.map (fun (c, p) -> c.ToLowerInvariant(), (c, p))
    let caseAt = argvFindCase sharedDef argv

    let selected =
        caseAt
        |> Option.bind (fun (_, tok) -> caseTable |> List.tryFind (fun (w, _) -> w = tok))
        |> Option.map snd

    let payloadDef =
        selected
        |> Option.bind (fun (c, p) -> if p.IsSome then Map.tryFind c payloads else None)

    let payloadLong =
        payloadDef
        |> Option.map (fun pd ->
            (pd.Fields |> List.map (fun (f, ty) -> "--" + Argv.kebabFlag f, (f, ty, false)))
            @ (Argv.mintedFlags pd |> List.map (fun (f, m) -> "--" + m, (f, TBool, true)))
            |> Map.ofList)
        |> Option.defaultValue Map.empty

    let _, scopeShorts = Argv.shortTables (scopeDef sharedDef payloadDef)

    let caseIdx = caseAt |> Option.map fst |> Option.defaultValue System.Int32.MaxValue

    // tier-aware did-you-mean: before the case token, shared flags and
    // case names; after it, shared plus the selected payload's flags
    let beforeCandidates =
        (sharedLong |> Map.toList |> List.map fst) @ (caseTable |> List.map fst)

    let afterCandidates =
        (sharedLong |> Map.toList |> List.map fst)
        @ (payloadLong |> Map.toList |> List.map fst)

    let problems = ResizeArray<string>()
    let sharedValues = System.Collections.Generic.Dictionary<string, Value>()
    let payloadValues = System.Collections.Generic.Dictionary<string, Value>()
    let seen = System.Collections.Generic.HashSet<string>()
    let polarity = System.Collections.Generic.Dictionary<string, bool>()

    let dup = argvDup problems seen polarity

    let parseValue = argvParseValue problems

    let rec go i (ts: string list) =
        match ts with
        | [] -> ()
        | _ :: rest when i = caseIdx -> go (i + 1) rest
        | t :: rest ->
            let resolved =
                if t.StartsWith "--" then
                    match Map.tryFind t sharedLong with
                    | Some(f, ty, neg) -> Choice1Of3(sharedValues, f, ty, neg)
                    | None ->
                        match Map.tryFind t payloadLong with
                        | Some(f, ty, neg) when i > caseIdx -> Choice1Of3(payloadValues, f, ty, neg)
                        | _ -> Choice2Of3()
                elif t.StartsWith "-" && t.Length = 2 then
                    match Map.tryFind (t.Substring 1) scopeShorts with
                    | Some(ShortOf flag) ->
                        (match Map.tryFind flag sharedLong with
                         | Some(f, ty, neg) -> Choice1Of3(sharedValues, f, ty, neg)
                         | None ->
                             match Map.tryFind flag payloadLong with
                             | Some(f, ty, neg) when i > caseIdx -> Choice1Of3(payloadValues, f, ty, neg)
                             | _ -> Choice2Of3())
                    | Some(AmbiguousShort candidates) ->
                        problems.Add $"""'{t}' is ambiguous: {String.concat ", " candidates}"""
                        Choice3Of3()
                    | None -> Choice2Of3()
                else
                    problems.Add $"unexpected argument '{t}'"
                    Choice3Of3()

            match resolved with
            | Choice1Of3(values, f, ty, neg) ->
                dup f neg t

                (match ty with
                 | TBool ->
                     values[f] <- VBool(not neg)
                     go (i + 1) rest
                 | _ ->
                     match rest with
                     | raw :: rest' ->
                         parseValue values f ty t raw
                         go (i + 2) rest'
                     | [] -> problems.Add $"flag '{t}' needs a value")
            | Choice2Of3() ->
                let cands = if i < caseIdx then beforeCandidates else afterCandidates
                let tyOf n =
                    match Map.tryFind n sharedLong, Map.tryFind n payloadLong with
                    | Some(_, ty, _), _
                    | None, Some(_, ty, _) -> Some ty
                    | _ -> None

                problems.Add(unknownFlagMessage t cands tyOf)
                go (i + 1) rest
            | Choice3Of3() -> go (i + 1) rest

    go 0 argv

    (match caseAt, selected with
     | None, _ -> problems.Add("missing subcommand; one of: " + String.concat ", " (caseTable |> List.map fst))
     | Some(_, tok), None -> problems.Add $"unknown subcommand '{tok}'{didYouMean tok (caseTable |> List.map fst)}"
     | Some _, Some _ -> ())

    let collectFields def values = argvFill problems def values

    let sharedFields = collectFields sharedDef sharedValues

    let payloadValue =
        match selected with
        | Some(c, Some _) ->
            let pd = Map.find c payloads
            Some(c, Some(VRecord(pd.Name, collectFields pd payloadValues)))
        | Some(c, None) -> Some(c, None)
        | None -> None

    if problems.Count > 0 then
        failwith ($"{label}: " + String.concat "; " problems)

    let case, payload =
        match payloadValue with
        | Some(c, p) -> c, p
        | None -> failwith $"{label}: internal — no case after validation"

    VRecord(outer.Name, (unionField, VUnion(case, payload)) :: sharedFields)

let private argvLoad (target: ArgsTarget) : Value =
    let argv = Session.ScriptArgs

    if List.contains "--help" argv || List.contains "-h" argv then
        // the `///` doc text is the script's data [D:tty-help-sanitize]
        printfn "%s" (argvUsage target argv |> sanitizeIfTty System.Console.IsOutputRedirected)
        raise (ExitRequest 0)

    match target with
    | ArgsRecord def -> argvParseRecord $"Args.load {def.Name}" def argv
    | ArgsShared(outer, uf, udef, payloads) -> argvLoadShared outer uf udef payloads argv
    | ArgsUnion(udef, payloads) ->
        let table = udef.Cases |> List.map (fun (c, p) -> c.ToLowerInvariant(), (c, p))

        match argv with
        | [] ->
            failwith (
                $"Args.load {udef.Name}: missing subcommand; one of: "
                + String.concat ", " (table |> List.map fst)
            )
        | tok :: rest ->
            match table |> List.tryFind (fun (w, _) -> w = tok) with
            | Some(_, (c, None)) ->
                match rest with
                | [] -> VUnion(c, None)
                | extra ->
                    failwith (
                        $"Args.load {udef.Name} {quoteData tok}: "
                        + String.concat "; " (extra |> List.map (fun t -> $"unexpected argument '{t}'"))
                    )
            | Some(_, (c, Some _)) ->
                let payload =
                    argvParseRecord $"Args.load {udef.Name} {tok}" (Map.find c payloads) rest

                VUnion(c, Some payload)
            | None ->
                failwith $"Args.load {udef.Name}: unknown subcommand '{quoteData tok}'{didYouMean tok (table |> List.map fst)}"

// the sigil env slot evaluated to overlay pairs — inside the stream's
// delay, so Env.fromFile boundary errors keep raise-at-force semantics
let private envPairsOf (where: string) (v: Value) : (string * string) list =
    match v with
    | VSeq items ->
        items
        |> Seq.map (fun item ->
            match item with
            | VRecord(_, fields) ->
                (match recTryGet "name" fields, recTryGet "value" fields with
                 | Some(VStr n), Some(VStr value) ->
                     Proc.envKeyRefusal where n
                     n, value
                 | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
            | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
        |> List.ofSeq
    | _ -> unreachable "the checker rejects non-seq overlays"

// eval-time scope capture for the ambient bare values `ls`/`pwd`/`Env.vars`
// [D:ambient-capture] [D:ambient-capture-within]: a rebinder snapshots the
// enclosing `within` scope where the identifier is EVALUATED (the rule the
// command pipe uses), keyed by the builtin's own object so a user shadow
// (`let ls = …`) is returned untouched. Populated by Builtins at load.
let ambientRebinders =
    System.Collections.Generic.Dictionary<string, Value * (unit -> Value)>()

// the explicit sigil env only — ambient `within env` layers apply in
// Proc's starters themselves [D:within-scopes], so every spawn
// (reifier desugars included) obeys the outer-first rule,
// not just these Eval paths
let rec private overlayOf (env: Env) (cenvO: TypedExpr option) : (string * string) list =
    match cenvO with
    | None -> []
    | Some ce ->
        envPairsOf "the env capture" (eval env ce)
        |> List.map (fun (k, v) -> k, noNul $"the env value for '{k}'" v)

// a NUL cannot cross the spawn hand-off [D:encoding-law]: argv and env
// are NUL-terminated C strings, so the byte would silently truncate
// the word at the child — and silent truncation is always wrong. The
// boundary holds on its own rather than trusting every upstream
// constructor: once a Bytes type lands there will be other routes to
// a NUL.
and private noNul (what: string) (s: string) : string =
    if s.Contains '\u0000' then
        failwith
            $"{what} contains a NUL byte — it would silently truncate at the process boundary (argv and env are NUL-terminated); NUL-bearing data is binary: pass it via a file or stdin, not an argument"
    else
        s

// spawn-argv assembly [D:argv-splat]: a splat enumerates once at
// spawn (argv is finite, so the splat forces by necessity), order
// preserved, each element one word
and argvOf (env: Env) (args: Check.TypedExpr list) : string list =
    args
    |> List.collect (fun a ->
        match a.Kind with
        | Check.TESplat inner ->
            match eval env inner with
            | VSeq items ->
                items
                |> Seq.map (scalarString "splat element" >> noNul "a splat element")
                |> List.ofSeq
            | v -> unreachable $"the checker rejects '$@' on {formatValue v}"
        | _ -> [ eval env a |> scalarString "command argument" |> noNul "a command argument" ])

// a dynamic head resolves at run time [D:dynamic-head] — like
// `^literal`, on the runtime string: no glob, no word-split, no
// re-lex. Not-found raises a located error naming the value.
and private progOf (env: Env) (h: Check.TCmdHead) : string =
    match h with
    | Check.THeadLit p -> p
    | Check.THeadDyn(display, he) ->
        let s =
            match eval env he with
            | VStr s -> s
            | v -> unreachable $"the checker guarantees a string head; got {formatValue v}"

        let s = noNul "a dynamic command head" s

        if s = "" then
            failwith $"the dynamic head ^{quoteData display} is empty — nothing to run"
        elif not (Extern.exists s) then
            // a typed home path names its own spelling [D:tilde]
            if display.StartsWith "~/" then
                failwith $"command not found: {quoteData s} (from {quoteData display})"
            else
                failwith $"command not found: {quoteData s} — the dynamic head ^{quoteData display} resolves at run time"
        else
            s

and eval (env: Env) (te: TypedExpr) : Value =
    RunPos.Current <- box te
    RunPos.Exact <- false
    // a signal or closed stdout unwinds here, before the node runs
    // [D:signal-main-unwind]; any flow's cut stops it here [D:scope-tree]
    Session.evalBoundary ()

    match te.Kind with
    // a failure as a value [D:try-form]: the body runs inside the capture
    // and its value is forced there — a lazy seq would otherwise raise
    // after `try` returned. exit and weir's own internal errors pass through
    | TETry(body, asResult) ->
        let rec force (v: Value) =
            match v with
            | VSeq items -> VSeq(items |> Seq.map force |> Seq.toArray :> seq<Value>)
            | VRecord(n, fields) -> VRecord(n, fields |> List.map (fun (k, x) -> k, force x))
            | VUnion(c, Some p) -> VUnion(c, Some(force p))
            | VTuple xs -> VTuple(List.map force xs)
            | VMap m -> VMap(Map.map (fun _ x -> force x) m)
            | v -> v

        try
            let v = force (eval env body)
            if asResult then VUnion("Ok", Some v) else VUnion("Some", Some v)
        with
        | ExitRequest _ -> reraise ()
        | :? System.OperationCanceledException -> reraise ()
        | Failure m when m.StartsWith "unreachable:" -> reraise ()
        | ex ->
            if asResult then
                VUnion("Error", Some(VStr ex.Message))
            else
                VUnion("None", None)
    | TEInt n -> VInt(int64 n)
    | TEDur n -> VDur n
    | TESize b -> VSize b
    | TEFloat f -> VFloat f
    | TERetry(isPoll, optsE, watchE, body, until) ->
        let head = if isPoll then "poll" else "retry"

        let watched =
            watchE
            |> Option.map (fun w ->
                match eval env w with
                | VProc h -> h
                | v -> unreachable $"the checker rejects a watch of {formatValue v}")

        // the watched child dying is itself the answer [D:scoped-procs]:
        // fail immediately with its own output, never a blind timeout
        let watchCheck (sw: System.Diagnostics.Stopwatch) =
            match watched with
            | Some h when
                (try
                    h.Proc.HasExited
                 with _ ->
                     true)
                ->
                // elapsed is the poll's own clock — Process.StartTime
                // throws on an exited child, and the wait's duration
                // is the number the reader wants anyway
                failwith (
                    $"poll: watched process (pid {h.Proc.Id}) exited with code {h.Proc.ExitCode} "
                    + $"after {formatDuration sw.ElapsedMilliseconds}"
                    + procTailLine h
                )
            | _ -> ()

        let fields =
            match eval env optsE with
            | VRecord(_, fs) -> fs
            | v -> unreachable $"the checker rejects '{head}' options of {formatValue v}"

        let dur name =
            match recGet name fields with
            | VDur ms -> ms
            | v -> unreachable $"the checker rejects a {name} of {formatValue v}"

        // the two bounds [D:retry-poll]: retry counts attempts (with
        // an optional total-time ceiling), poll counts time — an
        // unbounded loop is unrepresentable rather than refused
        let attempts, delayMs, timeoutMs =
            if isPoll then
                System.Int32.MaxValue, dur "interval", Some(dur "timeout")
            else
                (match recGet "attempts" fields with
                 | VInt n -> toIntArg head Amount n
                 | v -> unreachable $"the checker rejects attempts of {formatValue v}"),
                dur "delay",
                (match recGet "timeout" fields with
                 | VUnion("Some", Some(VDur t)) -> Some t
                 | _ -> None)

        if not isPoll && attempts < 1 then
            failwith $"{head}: attempts must be at least 1, got {attempts}"

        if delayMs < 0L then
            let key = if isPoll then "interval" else "delay"
            failwith $"{head}: a negative {key} ({formatDuration delayMs})"

        // poll is bounded by time alone, so a zero interval re-runs the
        // body in a busy loop until the timeout — refused like timeout=0s
        // [D:retry-poll]; retry's attempts bound it, so delay=0ms stays
        if isPoll && delayMs = 0L then
            failwith "poll: interval must be positive, got 0ms — a zero interval re-runs the body in a busy loop"

        match timeoutMs with
        | Some t when t <= 0L -> failwith $"{head}: timeout must be positive, got {formatDuration t}"
        | _ -> ()

        let sw = System.Diagnostics.Stopwatch.StartNew()
        // the wait is cancellable from the start [D:retry-poll]: the
        // timeout ceiling cancels a pending delay instead of waiting
        // it out; an external token can join the source later
        use cts = new System.Threading.CancellationTokenSource()

        // .NET timers take at most Int32.MaxValue ms; a longer ceiling is
        // the stopwatch's, enforced by the wait's slices — every valid
        // Duration is honoured, as Duration.sleep honours it [D:duration]
        let maxSliceMs = int64 System.Int32.MaxValue - 1L

        match timeoutMs with
        | Some t when t <= maxSliceMs ->
            cts.CancelAfter(System.TimeSpan.FromTicks(t * System.TimeSpan.TicksPerMillisecond))
        | _ -> ()

        let pastDeadline () =
            match timeoutMs with
            | Some t -> sw.ElapsedMilliseconds >= t
            | None -> false

        // the delay in timer-sized slices, each bounded by what is left of
        // the timeout — true when the timeout ended the wait
        let waitDelay () =
            let mutable left = delayMs
            let mutable cancelled = false

            while left > 0L && not cancelled do
                let budget =
                    match timeoutMs with
                    | Some t -> t - sw.ElapsedMilliseconds
                    | None -> System.Int64.MaxValue

                if budget <= 0L then
                    cancelled <- true
                else
                    let slice = min left (min budget maxSliceMs)

                    cancelled <-
                        Session.waitOrCancel
                            (System.TimeSpan.FromTicks(slice * System.TimeSpan.TicksPerMillisecond))
                            cts.Token

                    left <- left - slice

            cancelled

        let predicate (v: Value) =
            match until with
            | Some(b, pred) ->
                match eval (Map.add b v env) pred with
                | VBool ok -> ok
                | pv -> unreachable $"the checker rejects a predicate of {formatValue pv}"
            | None ->
                match v with
                | VBool ok -> ok
                | pv -> unreachable $"the checker rejects a {head} body of {formatValue pv}"

        let exhausted (n: int) =
            if isPoll then
                // the watched state rides the exhaustion message
                // [D:scoped-procs]: an up-but-never-ready child is
                // named as such (only poll can reach this message —
                // the reason watch is a key, not a wrapper)
                let watchedNote =
                    match watched with
                    | Some h -> $"; watched process (pid {h.Proc.Id}) still running{procTailLine h}"
                    | None -> ""

                failwith $"poll: timed out after {formatDuration sw.ElapsedMilliseconds} ({n} attempt(s)){watchedNote}"
            elif n >= attempts then
                failwith $"retry: exhausted {attempts} attempt(s) over {formatDuration sw.ElapsedMilliseconds}"
            else
                // the timeout ended it: the attempts actually made
                failwith
                    $"retry: timed out after {formatDuration sw.ElapsedMilliseconds} ({n} attempt(s) made of {attempts} allowed)"

        let rec loop (n: int) =
            watchCheck sw
            // raises propagate [D:retry-poll]: retry retries on the
            // predicate, never on exceptions — command failure becomes
            // data through the reifier family
            let v = eval env body

            if predicate v then
                match until with
                | Some _ -> v
                | None -> VUnit
            elif n >= attempts || cts.IsCancellationRequested || pastDeadline () then
                exhausted n
            else
                let timedOut =
                    if delayMs > 0L then
                        // the indicator wraps only the wait between
                        // attempts — the body may own the terminal
                        // [D:waiting-indicator]
                        Waiting.during $"{head}: waiting {formatDuration delayMs}" waitDelay
                    else
                        cts.IsCancellationRequested || pastDeadline ()

                if timedOut then exhausted n else loop (n + 1)

        loop 1
    | TEStr s -> VStr s
    | TEBool b -> VBool b
    | TEUnit -> VUnit
    | TEVar name ->
        match Map.tryFind name env with
        | Some v ->
            // an ambient bare value rebinds to eval-time cwd capture, but
            // only while the name still holds the builtin's own object — a
            // shadow returns as-is [D:ambient-capture]
            match ambientRebinders.TryGetValue name with
            | true, (original, rebind) when obj.ReferenceEquals(v, original) -> rebind ()
            | _ -> v
        | None -> unreachable $"the checker rejects unbound variable '{name}'"
    | TELet(name, _, value, body) -> eval (Map.add name (eval env value) env) body
    | TELetPat(pat, value, body) ->
        let bindings = bindPattern pat (eval env value)
        eval (bindings |> List.fold (fun m (n, v) -> Map.add n v m) env) body
    | TELambdaPat(pat, body) -> VClosurePat(pat, body, env)
    | TELambda(param, _, body) -> VClosure(param, body, env)
    | TEApp(fn, arg) -> callAt env fn arg
    | TEPipe(arg, { Kind = TECmd(chead, cargs, cenvO) }) ->
        // collect the command chain left of this hop [D:byte-pipes]: a
        // command feeding a command is a raw byte hop (a hop makes no
        // value — [D:colour-inherit]'s rationale, completed); only the
        // chain's far-left value head (if any) is a text edge
        let specs = chainSpecs env arg chead cargs cenvO
        VSeq(Seq.delay (fun () -> Proc.chainLinesOf specs) |> Seq.map VStr)
    // the armed statement command inherits at a tty [D:armed-inherit]:
    // a command statement — top level or in a block body — arms as
    // `cmd |print`, and get the bare statement's terminal
    // [D:colour-inherit] — isatty true, so editors, pagers and colour
    // work. The pre-spawn flush orders weir's prints against the child's.
    // Redirected output keeps the linesOf path untouched. Reifiers and
    // captures are unaffected (| complete is in-memory capture).
    | TEPipe({ Kind = TECmd(head, args, cenvO) }, { Kind = TEVar "|print" }) when
        not (System.Console.IsOutputRedirected)
        ->
        System.Console.Out.Flush()

        Proc.runInherited
            { Prog = Proc.resolveProg (progOf env head)
              Args = argvOf env args
              Env = overlayOf env cenvO
              Input = None
              Cwd = None
              Ambient = None }

        VUnit
    // the armed chain likewise: its tail writes the terminal
    // [D:armed-inherit] — `git log | less` pages, `xs | fzf` picks
    | TEPipe({ Kind = TEPipe(arg, { Kind = TECmd(chead, cargs, cenvO) }) }, { Kind = TEVar "|print" }) when
        not (System.Console.IsOutputRedirected)
        ->
        System.Console.Out.Flush()
        Proc.runChainInherited (chainSpecs env arg chead cargs cenvO)
        VUnit
    | TEPipe(arg, fn) -> callAt env fn arg
    | TEField(target, field) ->
        match eval env target with
        | VRecord(name, fields) ->
            match recTryGet field fields with
            | Some v -> v
            | None -> unreachable $"the checker rejects unknown field '{field}' on {name}"
        | v -> unreachable $"the checker rejects field access on {formatValue v}"
    | TESlice(target, lo, hi, on, loFromEnd, hiFromEnd) ->
        // inclusive, clamping [D:range-slicing]: an absent bound is an open
        // end (lo -> 0, hi -> last), out-of-range and reversed yield the
        // empty result, never a raise. A bounded seq truncates (so an
        // infinite source does not hang); an open end skips lazily. A
        // `^`-bound counts from the end (F#'s `^n` = length - n), which
        // needs the length, so a from-end SEQ slice forces (as Seq.last does).
        // bounds stay int64 [D:int-narrowing]; clamping them to ±2^40 is
        // exact (no target is that long) and keeps `len - v` from wrapping
        let boundVal o =
            match o with
            | None -> None
            | Some e ->
                match eval env e with
                | VInt n -> Some(max -(1L <<< 40) (min (1L <<< 40) n))
                | v -> unreachable $"the checker guarantees an int slice bound; got {formatValue v}"

        let loV = boundVal lo
        let hiV = boundVal hi

        // resolve a bound to an absolute index given the target's length
        let absLo (len: int) =
            match loV with
            | None -> 0L
            | Some v -> if loFromEnd then int64 len - v else v

        // the closed (inclusive) upper index, clamped to len - 1; an absent
        // hi is the last element
        let absHi (len: int) =
            match hiV with
            | None -> int64 len - 1L
            | Some v -> min (if hiFromEnd then int64 len - v else v) (int64 len - 1L)

        match on with
        | Check.SliceStr ->
            match eval env target with
            | VStr s ->
                // code-point positions [D:str-code-points]
                let n = CodePoints.length s
                let a = max 0L (absLo n)
                let b = absHi n
                if a > b then VStr "" else VStr(CodePoints.sub s (int a) (int (b - a + 1L)))
            | v -> unreachable $"the checker guarantees a string slice target; got {formatValue v}"
        | Check.SliceBytes ->
            match eval env target with
            | VBytes arr ->
                let a = max 0L (absLo arr.Length)
                let b = absHi arr.Length
                if a > b then VBytes [||] else VBytes(Array.sub arr (int a) (int (b - a + 1L)))
            | v -> unreachable $"the checker guarantees a Bytes slice target; got {formatValue v}"
        | Check.SliceSeq ->
            match eval env target with
            | VSeq items ->
                if loFromEnd || hiFromEnd then
                    // a from-end bound needs the length — force the seq,
                    // unless it is already an array [D:seq-index-o1]
                    let arr =
                        match items with
                        | :? (Value[]) as a -> a
                        | _ -> Seq.toArray items
                    let a = max 0L (absLo arr.Length)
                    let b = absHi arr.Length
                    if a > b then VSeq Seq.empty else VSeq(Array.sub arr (int a) (int (b - a + 1L)))
                else
                    // the lazy path (infinite-safe): open end skips lazily,
                    // a bounded hi stops pulling past it; int64 positions
                    let a = max 0L (Option.defaultValue 0L loV)

                    match hiV with
                    | Some b when a > b -> VSeq Seq.empty
                    | _ ->
                        VSeq(
                            seq {
                                use e = items.GetEnumerator()
                                let mutable i = 0L

                                while (match hiV with
                                       | Some b -> i <= b
                                       | None -> true)
                                      && e.MoveNext() do
                                    if i >= a then
                                        yield e.Current

                                    i <- i + 1L
                            }
                        )
            | v -> unreachable $"the checker guarantees a sequence slice target; got {formatValue v}"
    | TEBinOp("&&", l, r) ->
        (match eval env l with
         | VBool false -> VBool false
         | VBool true -> eval env r
         | v -> unreachable $"the checker rejects '&&' on {formatValue v}")
    | TEBinOp("||", l, r) ->
        (match eval env l with
         | VBool true -> VBool true
         | VBool false -> eval env r
         | v -> unreachable $"the checker rejects '||' on {formatValue v}")
    // composition sits here, not in binOp: it needs `apply` (the
    // eval/apply knot) [D:composition-operators]
    | TEBinOp(">>", l, r) ->
        let f = eval env l
        let g = eval env r
        VBuiltin(fun x -> apply g (apply f x))
    | TEBinOp(op, l, r) -> binOp op (eval env l) (eval env r)
    | TERecord(name, fields) -> VRecord(name, fields |> List.map (fun (n, fv) -> n, eval env fv))
    | TEUpdate(src, updates) ->
        // source evaluated once [D:record-update]; nested paths overlay
        let source = eval env src

        updates
        |> List.fold
            (fun acc (path, tval) ->
                let rec go (v: Value) (path: string list) : Value =
                    match v, path with
                    // recSet replaces in place [D:record-order]: an
                    // updated field keeps its position, never moves
                    | VRecord(n, fs), [ f ] -> VRecord(n, recSet f (eval env tval) fs)
                    | VRecord(n, fs), f :: rest -> VRecord(n, recSet f (go (recGet f fs) rest) fs)
                    | v, _ -> unreachable $"the checker rejects update on {formatValue v}"

                go acc path)
            source
    | TEList items -> VSeq(items |> List.map (eval env))
    | TETuple items -> VTuple(items |> List.map (eval env))
    | TECmd(head, args, cenvO) ->
        let argv = argvOf env args

        // only the enclosing `within` scopes are captured
        // [D:ambient-capture-within]
        let spec: Proc.Spec =
            { Prog = Proc.resolveProg (progOf env head)
              Args = argv
              Env = overlayOf env cenvO
              Input = None
              Cwd = Weir.Session.scopedCwd ()
              Ambient = Weir.Session.scopedAmbient () }

        // the command takes the blame wherever it is read [D:runtime-position]
        VSeq(Seq.delay (fun () -> Proc.linesOf spec) |> Seq.map VStr |> blameOn (box te))
    | TEInterp parts ->
        let sb = System.Text.StringBuilder()

        for p in parts do
            match p with
            | IStr s -> sb.Append s |> ignore
            | IExpr e ->
                // a hole renders what show renders [D:interp-show]; a
                // bare string stays raw (the value, not its quoted form)
                sb.Append(
                    match eval env e with
                    | VStr str -> str
                    | v -> formatValue v
                )
                |> ignore

        VStr(sb.ToString())
    | TEFrom(fmt, top, defs, udefs, seqOf, mapOf) -> fromAdapter fmt seqOf mapOf top defs udefs
    | TEFromYaml(_, shape, stream) -> yamlFromImpl shape stream
    | TEFromToml(_, shape) -> tomlFromImpl shape
    | TEYaml(tpl, _, patchBy) ->
        // a patch district wraps its tree with the merge key — the
        // wrapper ctor cannot be produced from user code or from parsed
        // text ([D:yaml-nodes]; the |-prefix trick, [D:drop-reify-builtins])
        let tree = evalYamlTpl env tpl

        match patchBy with
        | None -> tree
        | Some by -> VUnion("|ypatch", Some(VTuple [ VStr(defaultArg by ""); tree ]))
    | TETo("yaml", renames, codecs, unions, stream, guide) ->
        // coded fields become plain values first [D:wire-codecs]
        let render = yamlToImpl renames unions stream guide
        VBuiltin(fun v -> apply render (encodeTree "yaml" codecs v))
    | TETo("jsonl", renames, codecs, unions, _, _) ->
        VBuiltin(fun v ->
            match v with
            | VSeq items -> VSeq(items |> Seq.map (encodeTree "jsonl" codecs >> jsonLine renames unions >> VStr))
            | v -> unreachable $"the checker rejects 'to jsonl' on {formatValue v}")
    | TETo(fmt, renames, codecs, unions, _, _) ->
        // one document [D:to-jsonl] — the whole value through the same
        // renderer, once; an array document forces its seq (one line
        // cannot stream)
        VBuiltin(fun v -> VSeq [ VStr(jsonLine renames unions (encodeTree fmt codecs v)) ])
    | TEMatch(scrutinee, arms) ->
        let v0 = eval env scrutinee

        // memoize-once rule [D:seq-patterns]: a match containing any
        // seq pattern views its scrutinee through one cache — arms
        // probe the same buffer (never re-pull), rest binds the buffer
        // suffix plus the untouched tail, and effects run once in
        // total
        let rec hasSeqPat (p: Weir.Ast.Pattern) =
            match p.PKind with
            | Weir.Ast.PSeqNil
            | Weir.Ast.PCons _
            | Weir.Ast.PSeqList _ -> true
            | Weir.Ast.PTuple ps -> ps |> List.exists hasSeqPat
            | Weir.Ast.PRecord fields -> fields |> List.exists (snd >> hasSeqPat)
            | Weir.Ast.PCase(_, Some a) -> hasSeqPat a
            | _ -> false

        let v =
            match v0 with
            | VSeq items when arms |> List.exists (fun (p, _, _) -> hasSeqPat p) -> VSeq(Seq.cache items)
            | _ -> v0

        let rec tryArms arms =
            match arms with
            | [] -> unreachable $"the checker guarantees totality; no arm matched {formatValue v}"
            | (pat, guard, body) :: rest ->
                match tryBind pat v with
                | Some bindings ->
                    let armEnv = bindings |> List.fold (fun e (n, bv) -> Map.add n bv e) env

                    let guardPasses =
                        match guard with
                        | None -> true
                        | Some g ->
                            match eval armEnv g with
                            | VBool b -> b
                            | gv -> unreachable $"the checker rejects a non-bool guard: {formatValue gv}"

                    if guardPasses then eval armEnv body else tryArms rest
                | None -> tryArms rest

        tryArms arms
    | TEArgsLoad target -> argvLoad target
    | TEEnvLoad(def, enums) ->
        // snapshot at force time; collect every problem, raise once
        let problems = ResizeArray<string>()

        let fields =
            def.Fields
            |> List.map (fun (field, ty) ->
                // the env var this field reads [D:wire-keys]: verbatim unless
                // [<Wire "NAME">] says otherwise. Env var names are not always
                // legal weir field names (a leading digit, a dash, a reserved
                // word), and the author controls the environment no more than
                // a JSON payload — the same wire-key problem as from json.
                // `name` is the wire name from here on, so every message names
                // the variable the author actually asked for.
                let name = Types.wireName def field
                // the `within env` overlay included [D:env-overlay-read]
                let raw = Session.getEnv name |> Option.toObj

                let value =
                    match ty, raw with
                    | TNamed("Option", _), null -> VUnion("None", None)
                    | _, null ->
                        // the resting point sits below the whole overlay
                        // stack [D:default-attr]: any set var wins
                        match Argv.defaultOf def field with // attributes are keyed by FIELD
                        | Some(AStr s) -> VStr s
                        | Some(AInt n) -> VInt n
                        | Some(AFloat fl) -> VFloat fl
                        | Some(ABool b) -> VBool b
                        | Some(ADur n) -> VDur n
                        | Some(ASize b) -> VSize b
                        | None ->
                            problems.Add $"{name} is missing"
                            VUnit
                    | (TStr | TNamed("Option", [ TStr ])), v -> wrapOpt ty (VStr v)
                    | (TInt | TNamed("Option", [ TInt ])), v ->
                        match System.Int64.TryParse v with
                        | true, n -> wrapOpt ty (VInt n)
                        | _ ->
                            problems.Add $"{name} is not an int ('{v}')"
                            VUnit
                    | (TBool | TNamed("Option", [ TBool ])), v ->
                        match v with
                        | "true" -> wrapOpt ty (VBool true)
                        | "false" -> wrapOpt ty (VBool false)
                        | _ ->
                            problems.Add $"{name} is not a bool ('{v}'; exactly true or false)"
                            VUnit
                    | (TDur | TNamed("Option", [ TDur ])), v ->
                        match parseDurationMs v with
                        | Ok n -> wrapOpt ty (VDur n)
                        | Error e ->
                            problems.Add $"{name}: {e}"
                            VUnit
                    | (TInstant | TNamed("Option", [ TInstant ])), v ->
                        match parseInstantMs v with
                        | Ok n -> wrapOpt ty (VInstant n)
                        | Error e ->
                            problems.Add $"{name}: {e}"
                            VUnit
                    | (TSize | TNamed("Option", [ TSize ])), v ->
                        match parseSize v with
                        | Ok b -> wrapOpt ty (VSize b)
                        | Error e ->
                            problems.Add $"{name}: {e}"
                            VUnit
                    | (TFloat | TNamed("Option", [ TFloat ])), v ->
                        match parseFloat v with
                        | Ok fl -> wrapOpt ty (VFloat fl)
                        | Error e ->
                            problems.Add $"{name}: {e}"
                            VUnit
                    // env is the secret channel [D:secret]: wrap at the boundary
                    | (TSecret | TNamed("Option", [ TSecret ])), v -> wrapOpt ty (VSecret v)
                    | (TNamed(un, []) | TNamed("Option", [ TNamed(un, []) ])), v ->
                        // the enum conversion [D:env-enums]: matching is
                        // case-insensitive against the declared names (env
                        // convention is uppercase — LOG_LEVEL=DEBUG, =debug
                        // and =Debug all select Debug); an empty value is a
                        // miss listing the candidates, as the int rule does
                        let cases = enums |> Map.tryFind un |> Option.defaultValue []

                        match
                            cases
                            |> List.tryFind (fun c ->
                                System.String.Equals(c, v, System.StringComparison.OrdinalIgnoreCase))
                        with
                        | Some c -> wrapOpt ty (VUnion(c, None))
                        | None ->
                            // the hint compares like the matcher does —
                            // case-insensitively — but names the declared
                            // spelling
                            let hint =
                                cases
                                |> List.tryPick (fun c ->
                                    if didYouMean (v.ToLowerInvariant()) [ c.ToLowerInvariant() ] <> "" then
                                        Some $". Did you mean '{c}'?"
                                    else
                                        None)
                                |> Option.defaultValue ""

                            let listed = String.concat ", " cases
                            problems.Add $"{name} is not a {un} ('{v}'; expected one of: {listed}){hint}"
                            VUnit
                    | _ -> unreachable "the checker rejects non-scalar Env.load fields"

                field, value) // the RECORD field keeps its weir name

        if problems.Count > 0 then
            failwith ($"Env.load {def.Name}: " + String.concat "; " problems)

        VRecord(def.Name, fields)
    | TESeq(a, b) ->
        eval env a |> ignore
        eval env b
    | TEAlways(body, cleanup) ->
        // the bare scope [D:within-always]; raise handling:
        //  1. normal exit + cleanup raises -> the scope raises (always
        //     is never the one place a raise disappears)
        //  2. already unwinding + cleanup raises -> the original wins;
        //     the cleanup failure goes to stderr with a marker (the
        //     diagnosis outranks its consequence)
        //  3. teardown continues outward regardless — one failed
        //     cleanup must not strand the enclosing scopes' LIFO.
        // exit n is a raise (ExitRequest) and unwinds the same way; a
        // signal unwinds the flow, which runs them LIFO [D:scope-tree].
        // a cleanup's commands start even mid-teardown [D:teardown-always-spawns]
        let hooked = Session.registerAlways ()

        // the claim is made inside the cleanup's mark, so a teardown
        // requested from here on waits for it [D:teardown-never-preempts]
        let claimAndRun () =
            Session.runningCleanup (fun () ->
                if Session.deregisterAlways hooked then
                    eval env cleanup |> ignore)

        // the main flow's pending cleanup, which the signal backstop runs
        // if it adopts a stuck main flow [D:scope-tree]
        let frame = Session.pushFrame claimAndRun

        match
            (try
                Ok(eval env body)
             with e ->
                 Error e)
        with
        | Ok v ->
            // case 1: a cleanup raise propagates as the scope's raise —
            // unless a signal's hook already claimed the cleanup
            Session.releaseFrame frame claimAndRun
            v
        | Error original ->
            // the cleanup runs weir code while the body's raise unwinds: pin
            // the raise to where it happened first [D:runtime-position]
            (match original with
             | :? ExitRequest -> ()
             | _ ->
                 let b = blameFor original

                 if isNull b.Node then
                     b.Node <- RunPos.Current
                     b.Exact <- RunPos.Exact)

            (try
                Session.releaseFrame frame claimAndRun
             with
             | :? ExitRequest ->
                 // the checker refuses exit inside always; a raise here
                 // would swap the original for a code — unreachable
                 ()
             | ce ->
                 eprintfn
                     "within/always: the cleanup ALSO failed — %s (the original error wins)"
                     (sanitizeIfTty System.Console.IsErrorRedirected ce.Message))

            raise original
    | TEWithin(kind, binder, targ, topts, body) ->
        // dispatch kind-first on the union [D:within-kind-union]: a new
        // kind must claim its runtime here or the build fails. Binder
        // and arg are extracted by name per kind, never a wildcard over
        // the tuple: a wildcard would absorb a malformed node of any
        // kind instead of reaching the unreachable guard, which is what
        // makes the guard worth having.
        let argOf (what: string) =
            match targ with
            | Some a -> a
            | None -> unreachable $"the parser gives within {what} its argument"

        let binderOf (what: string) =
            match binder with
            | Some b -> b
            | None -> unreachable $"the parser gives within {what} its binder"

        match kind with
        | WithinPure
        | WithinReadonly ->
            // the purity/read-only assertions are check-time rules
            // [D:pure-stage1] [D:pure-stage2]: by the time evaluation
            // reaches the region, the body is verified within its
            // ceiling (∅ for pure, ambient-input for readonly) — the
            // region is transparent at runtime, so ambient reads still
            // run
            eval env body
        | WithinPlan ->
            // the plan capture [D:plan-apply]: run the body while a
            // fresh capture frame is on top — the mutation builtins
            // append Ops instead of performing (ambient reads still
            // run); the body's own value is discarded (it ran for its
            // capture). The region yields a Plan (a VRecord carrying
            // the captured Ops in order, all data per ruling 6 —
            // content thunks forced at capture).
            let ops = PlanMode.capturing (fun () -> eval env body |> ignore)
            VRecord("Plan", [ "ops", VSeq ops ])
        | WithinLock ->
            // advisory file lock [D:within-lock]: an explicit flock(2) on
            // POSIX [D:lock-advisory] (per-open-file-description, so pmap
            // arms exclude each other; interoperates with flock(1))
            // and native share modes on Windows. Blocking by default,
            // timeout= bounds the wait; the kernel releases on any
            // death, kill -9 included — the one kind whose guarantee
            // survives the hard-exit carve-out. Advisory only: a
            // non-cooperating process ignores it.
            let path =
                match eval env (argOf "lock") with
                | VStr s -> s
                | v -> unreachable $"the checker rejects a lock path of {formatValue v}"

            if path = "" then
                failwith "within lock: the lock path is empty — name a lock file"

            let resolved = Session.resolveFs path

            if System.IO.Directory.Exists resolved then
                failwith $"within lock: {quoteData resolved} is a directory — the lock is a file (e.g. {quoteData resolved}/.lock)"

            let timeoutMs =
                topts
                |> Option.map (fun o ->
                    match eval env o with
                    | VDur ms when ms < 0L ->
                        failwith $"within lock: timeout must not be negative, got {formatDuration ms}"
                    | VDur ms -> ms
                    | v -> unreachable $"the checker rejects a lock timeout of {formatValue v}")

            let sw = System.Diagnostics.Stopwatch.StartNew()

            let rec acquire () =
                match
                    (try
                        Some(
                            new System.IO.FileStream(
                                resolved,
                                System.IO.FileMode.OpenOrCreate,
                                System.IO.FileAccess.ReadWrite,
                                System.IO.FileShare.None
                            )
                        )
                     with
                     // DirectoryNotFound is an IOException — a missing
                     // parent must fail immediately, not spin as "held
                     // elsewhere"
                     | :? System.IO.DirectoryNotFoundException ->
                         failwith $"within lock: no such directory for {quoteData resolved} — the lock file's parent must exist"
                     | :? System.UnauthorizedAccessException ->
                         failwith $"within lock: cannot open {quoteData resolved}: permission denied"
                     | :? System.IO.IOException -> None)
                    // POSIX: the lock is an explicit flock — .NET's own is
                    // disabled for weir [D:lock-advisory]
                    |> Option.filter (fun fs ->
                        System.OperatingSystem.IsWindows()
                        || Session.tryFlockExclusive fs
                        || (fs.Dispose()
                            false))
                with
                | Some fs -> fs
                | None ->
                    match timeoutMs with
                    | Some ms when sw.ElapsedMilliseconds >= ms ->
                        failwith
                            $"within lock: timed out after {formatDuration ms} waiting for {quoteData resolved} (held elsewhere)"
                    | _ ->
                        Session.sleepInterruptible (System.TimeSpan.FromMilliseconds 25.0)
                        acquire ()

            let fs = acquire ()
            let frame = Session.pushFrame fs.Dispose

            try
                eval env body
            finally
                Session.releaseFrame frame fs.Dispose
        | WithinCd ->
            // cd consumes a path [D:within-scopes]: resolved against
            // the current cwd (so nested relative scopes compose),
            // verified before the block runs, restored on every
            // managed exit
            let path =
                match eval env (argOf "cd") with
                | VStr s -> s
                | v -> unreachable $"the checker rejects a cd path of {formatValue v}"

            let resolved = Session.resolve path

            if not (System.IO.Directory.Exists resolved) then
                failwith $"within cd: no such directory: {quoteData resolved}"

            let saved = Session.Cwd()
            Session.setCwd resolved
            Session.enterWithinCd ()

            let restore () =
                Session.exitWithinCd ()
                Session.setCwd saved

            let frame = Session.pushFrame restore

            try
                eval env body
            finally
                Session.releaseFrame frame restore
        | WithinEnv ->
            // env pushes an ambient overlay that child spawns see;
            // weir's own Env.load is untouched [D:within-scopes]
            Session.pushEnvOverlay (envPairsOf "within env" (eval env (argOf "env")))
            let frame = Session.pushFrame Session.popEnvOverlay

            try
                eval env body
            finally
                Session.releaseFrame frame Session.popEnvOverlay
        | WithinProc ->
            // the scoped process [D:scoped-procs]: spawn with both
            // streams spilling, bind the handle, and at every exit —
            // normal and raise alike — tree-kill and reap. The scope
            // is the lifetime; the exit hook is the hard-exit
            // backstop.
            let binderName = binderOf "proc"

            let prog, argv, overlay =
                match (argOf "proc").Kind with
                | TECmd(h, args, cenvO) -> progOf env h, argvOf env args, overlayOf env cenvO
                | _ -> unreachable "the parser guarantees a command in the proc slot"

            let spill =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-proc-{System.Guid.NewGuid():N}")

            // created and registered inside the spawn gate, so no spill dir
            // appears after the teardown kill; removed when the start
            // refuses [D:teardown-stops-arms]
            Session.gatedSpawn (fun () ->
                Session.createPrivateDir spill
                Session.registerTmpDir spill)

            let p, drain =
                try
                    Proc.startSpilled
                        overlay
                        (Proc.resolveProg prog)
                        argv
                        (System.IO.Path.Combine(spill, "out.log"))
                        (System.IO.Path.Combine(spill, "err.log"))
                with _ ->
                    (try
                        System.IO.Directory.Delete(spill, true)
                     with _ ->
                         ())

                    Session.deregisterTmpDir spill
                    reraise ()

            Session.registerProc p

            let handle =
                { Proc = p
                  OutPath = System.IO.Path.Combine(spill, "out.log")
                  ErrPath = System.IO.Path.Combine(spill, "err.log")
                  SpillDir = spill
                  Drain = drain }

            let release () =
                Proc.stopTree p
                Session.deregisterProc p
                // pumps settle before the spill dir goes (Windows would
                // refuse the delete under a live write handle)
                drain ()

                (try
                    System.IO.Directory.Delete(spill, true)
                 with _ ->
                     ())

                Session.deregisterTmpDir spill

            let frame = Session.pushFrame release

            try
                eval (Map.add binderName (VProc handle) env) body
            finally
                Session.releaseFrame frame release
        | WithinServe ->
            // the scoped listener [D:http-serve]: open the socket,
            // serve on a pool of handler threads (the maxConcurrent
            // ceiling, as pmapWith bounds it), bind the handle, run
            // the body, and at every exit — normal, raise,
            // SIGINT/SIGTERM — close the listener so the port frees.
            // The signal path rides the same registerAlways backstop
            // the bare within/always uses.
            let binderName = binderOf "serve"

            let cfg =
                match eval env (argOf "serve") with
                | VRecord(_, f) -> f
                | v -> unreachable $"the checker rejects a serve config of {formatValue v}"

            let port =
                match recGet "port" cfg with
                | VInt n -> toIntArg "serve port" Amount n
                | v -> unreachable $"serve port {formatValue v}"

            let maxConcurrent =
                match recGet "maxConcurrent" cfg with
                | VInt n -> toIntArg "serve maxConcurrent" Amount n
                | v -> unreachable $"serve maxConcurrent {formatValue v}"

            if maxConcurrent < 1 then
                failwith $"serve: maxConcurrent is at least 1, got {maxConcurrent}"

            // the request-body read timeout [D:serve-body-timeout]:
            // optional in the config literal, defaulting to 30s — a
            // slow client dribbling the body cannot park a handler
            // slot forever
            let bodyTimeoutMs =
                match List.tryFind (fun (n, _) -> n = "bodyTimeout") cfg with
                | Some(_, VDur ms) -> toIntArg "serve bodyTimeout (ms)" Amount ms
                | Some(_, v) -> unreachable $"serve bodyTimeout {formatValue v}"
                | None -> 30_000

            if bodyTimeoutMs < 1 then
                failwith $"serve: bodyTimeout is at least 1ms, got {bodyTimeoutMs}ms"

            // the request-body size bound [D:serve-max-body]: optional,
            // 16 MiB, answered 413 past it
            let maxBody =
                match List.tryFind (fun (n, _) -> n = "maxBody") cfg with
                | Some(_, VSize b) -> b
                | Some(_, v) -> unreachable $"serve maxBody {formatValue v}"
                | None -> 16L * 1024L * 1024L

            if maxBody < 0L then
                failwith $"serve: maxBody cannot be negative, got {formatSize maxBody}"

            // the handler closure, evaluated once at scope entry
            let handler =
                match topts with
                | Some h -> eval env h
                | None -> unreachable "the parser gives within serve its handler"

            // request primitives -> the HttpServerRequest Value the handler sees
            let requestValue (r: Serve.SReq) : Value =
                // HttpMethod is open-world inbound [D:serve-method]:
                // the listed verbs read as themselves (QUERY included
                // — a case the shared client↔server family already
                // has), and a well-formed but unlisted verb reads as
                // `Other v` so the handler can route on it and answer
                // 405 by choice, never misreported as Get. A malformed
                // token never reaches here (refused with 400 at the
                // boundary before the handler).
                let methodValue =
                    match r.Method.ToUpperInvariant() with
                    | "GET" -> VUnion("Get", None)
                    | "POST" -> VUnion("Post", None)
                    | "PUT" -> VUnion("Put", None)
                    | "DELETE" -> VUnion("Delete", None)
                    | "PATCH" -> VUnion("Patch", None)
                    | "HEAD" -> VUnion("Head", None)
                    | "OPTIONS" -> VUnion("Options", None)
                    | "QUERY" -> VUnion("Query", None)
                    | _ -> VUnion("Other", Some(VStr r.Method))

                VRecord(
                    "HttpServerRequest",
                    [ "method", methodValue
                      "path", VStr r.Path
                      "query", VStr r.Query
                      "headers", VSeq(r.Headers |> List.map (fun (k, v) -> VTuple [ VStr k; VStr v ]))
                      "body", VStr r.Body ]
                )

            // the handler's HttpServerResponse Value -> response
            // primitives. A Stream body stays a lazy seq<Value> mapped
            // to strings, pulled by Serve.writeResponse
            // element-by-element (the incremental rule)
            let responseOf (v: Value) : Serve.SResp =
                match v with
                | VRecord("HttpServerResponse", f) ->
                    let status =
                        match recGet "status" f with
                        | VInt n -> toIntArg "serve status" Amount n
                        | bad -> unreachable $"serve status {formatValue bad}"

                    let headers =
                        match recGet "headers" f with
                        | VSeq items ->
                            [ for it in items do
                                  match it with
                                  | VTuple [ VStr k; VStr hv ] -> k, hv
                                  | _ -> () ]
                        | _ -> []

                    // refuse a response header carrying CR/LF/NUL
                    // [D:http-header-bytes]: the serve face of F3 — a
                    // handler-returned header cannot forge a second one
                    // on the wire (nor be silently dropped, F11's
                    // shape). The located message is surfaced to the
                    // script through the stream-error channel, and the
                    // response is refused without the injecting header
                    // (the accept loop 500s it).
                    Http.refuseHeaderInjection "response header" (fun m -> raise (Serve.ResponseHeaderRefused m)) headers

                    let asString v =
                        match v with
                        | VStr s -> s
                        | other -> formatValue other

                    let body =
                        match recGet "body" f with
                        | VUnion("NoBody", None) -> Serve.RNoBody
                        | VUnion("Text", Some(VStr s)) -> Serve.RText s
                        | VUnion("Json", Some(VSeq lines)) ->
                            Serve.RJson(lines |> Seq.map asString |> String.concat "\n")
                        // the lazy body [D:http-serve]: keep it a seq, so
                        // writeResponse pulls and flushes each element as
                        // produced — a slow producer streams incrementally
                        | VUnion("Stream", Some(VSeq lines)) -> Serve.RStream(lines |> Seq.map asString)
                        // the urlencoded write [D:http-dx] — encoded by the
                        // same shared escaper as the client send path
                        | VUnion("Form", Some(VSeq pairs)) ->
                            pairs
                            |> Seq.map (fun it ->
                                match it with
                                | VTuple [ VStr k; VStr v ] -> k, v
                                | bad -> unreachable $"serve form pair {formatValue bad}")
                            |> Http.formUrlEncode
                            |> Serve.RForm
                        | bad -> unreachable $"serve body {formatValue bad}"

                    { Status = status
                      Headers = headers
                      Body = body }
                | bad -> unreachable $"the checker rejects a serve response of {formatValue bad}"

            let handle =
                Serve.start
                    port
                    { BodyTimeoutMs = bodyTimeoutMs
                      MaxBody = maxBody }

            // the scope-close drain [D:serve-drain]: in-flight handlers get
            // this long, together, to finish their responses
            let drainMs = 2000

            // a live scope for the terminal-^C rule [D:sigint-live-scopes];
            // a signal unwinds the flow through the close below, and the
            // kernel closes the socket at any death [D:http-serve]
            let hooked = Session.registerAlways ()

            // the handler concurrency ceiling [D:http-serve]: N in
            // flight, excess queues — pmapWith's bound, via a
            // SemaphoreSlim here. Never disposed [D:serve-drain]: a
            // handler outliving the scope still releases its slot.
            let gate = new System.Threading.SemaphoreSlim(maxConcurrent, maxConcurrent)
            let closing = new System.Threading.CancellationTokenSource()
            let parentCwd = Session.Cwd()
            // each running handler with its node [D:serve-cut]
            let inflight =
                System.Collections.Concurrent.ConcurrentDictionary<System.Threading.Thread, Session.Node>()

            // the scope's node [D:scope-tree]: the body runs in it, each
            // handler in a node under it
            let home = Session.currentNode ()
            let serveNode = Session.openNode home Session.NodeKind.Serve

            // a handler failure the client sees as a 500 is recorded for
            // the script too [D:serve-handler-errors], on the stream-error
            // channel the header refusals already use
            let recordHandlerError (req: Serve.SReq option) (msg: string) =
                let where =
                    match req with
                    | Some r -> $" on {r.Method} {r.Path}"
                    | None -> ""

                Serve.recordStreamError handle $"serve handler{where} failed: {msg}"

            let internalError ctx =
                try
                    Serve.writeResponse
                        ctx
                        { Status = 500
                          Headers = []
                          Body = Serve.RText "internal error" }
                        ignore
                with _ ->
                    ()

            // one request on its own thread. Past the drain a handler still
            // running is cut at its next effect boundary [D:serve-cut]: the
            // scope's flow token (the pfirst loser's mechanism) and the
            // request's node, whose children die with the cut
            let runHandler (ctx: Serve.SCtx) (node: Session.Node) =
                let mutable seen = None

                try
                    Session.setCurrentNode node
                    // fork the session like a pmap arm — the
                    // handler sees the scope's cwd, worker-local
                    Session.enterWorker parentCwd

                    try
                        // the body was read before the slot
                        // [D:serve-body-timeout]: a slow body
                        // arrives here as its 408 signal
                        let req = Serve.readRequest ctx
                        seen <- Some req

                        // a malformed method token is refused
                        // at the boundary [D:serve-method]:
                        // 400 before the handler, the same
                        // byte-class refusal Bundle C's
                        // guard performs
                        if not (Serve.methodTokenOk req.Method) then
                            Serve.writeResponse
                                ctx
                                { Status = 400
                                  Headers = []
                                  Body = Serve.RText "malformed request method" }
                                ignore
                        else
                            let resp = responseOf (apply handler (requestValue req))
                            // a Stream producer raising mid-body
                            // goes through the designated
                            // channel [D:serve-stream]: record
                            // it on the handle, do not raise
                            Serve.writeResponse ctx resp (Serve.recordStreamError handle)
                    with
                    | Serve.BodyReadTimeout ->
                        // the client dribbled its body past the
                        // bound [D:serve-body-timeout] — refuse
                        // rather than park unboundedly
                        (try
                            Serve.writeResponse
                                ctx
                                { Status = 408
                                  Headers = []
                                  Body = Serve.RText "request body read timed out" }
                                ignore
                         with _ ->
                             ())
                    | Serve.BodyTooLarge ->
                        // past maxBody [D:serve-max-body]: the
                        // rest of the body is never read, so the
                        // connection closes after the 413
                        (try
                            Serve.writeResponse
                                ctx
                                { Status = 413
                                  Headers = []
                                  Body = Serve.RText $"request body exceeds maxBody ({formatSize maxBody})" }
                                ignore
                         with _ ->
                             ())
                    | Serve.BodyCharsetUnknown cs ->
                        // a body in a charset weir cannot decode
                        // [D:serve-charset]: refused before the
                        // handler, never decoded as UTF-8
                        (try
                            Serve.writeResponse
                                ctx
                                { Status = 415
                                  Headers = []
                                  Body =
                                    Serve.RText
                                        $"request body charset '{cs}' is not one weir decodes — utf-8, utf-16, utf-32, us-ascii or iso-8859-1" }
                                ignore
                         with _ ->
                             ())
                    | Serve.ResponseHeaderRefused msg ->
                        // a handler returned a header carrying
                        // CR/LF/NUL [D:http-header-bytes] or one
                        // the wire cannot carry
                        // [D:http-header-unsendable]: refuse
                        // the response without the offending
                        // header (never a silent drop) — a 500
                        // to the client, and the located message
                        // to the script via the stream-error
                        // channel so the author learns which
                        // header and byte the handler bug carried
                        Serve.recordStreamError handle msg
                        internalError ctx
                    | Serve.ResponseStatusRefused n ->
                        recordHandlerError seen $"status {n} is not an HTTP status (100 to 999)"
                        internalError ctx
                    // cut past the scope: detached, nothing to answer
                    | :? Session.Cancelled -> ()
                    // `exit n` in a handler stops the scope [D:serve-handler-exit]:
                    // no new request, the other handlers and the body cut
                    // (their children killed, their cleanups whole), the
                    // scope closes, then the exit; this connection closes
                    // unanswered
                    | :? ExitRequest as e ->
                        System.Threading.Interlocked.CompareExchange(&serveNode.Exit, (e :> exn), null) |> ignore
                        Session.cutNode serveNode 1 Session.CutReason.HandlerExit true
                        Serve.abandon ctx
                    // a REPL ^C passes through as `exit` does [D:repl-interrupt]:
                    // the evaluation ends, the connection closes unanswered
                    | :? Session.Interrupted -> Serve.abandon ctx
                    | ex ->
                        // a handler raise is a 500 — the server
                        // survives one bad request, and the
                        // script hears of it
                        recordHandlerError seen ex.Message
                        internalError ctx
                finally
                    Serve.finish ctx
                    Session.exitWorker ()
                    gate.Release() |> ignore
                    inflight.TryRemove(System.Threading.Thread.CurrentThread) |> ignore
                    Session.setCurrentNode null
                    Session.closeNode node

            // the accept loop on its own thread: it takes a slot, then
            // blocks in accept until a request is parked or stop() closes
            // the queue (accept then yields None and the loop ends) — a
            // slot is held before a request is dequeued, so nothing waits
            // dequeued while the scope closes [D:serve-drain]. Each request
            // runs the handler on a worker thread.
            let acceptLoop () =
                let rec go () =
                    let slot =
                        try
                            gate.Wait closing.Token
                            true
                        with _ ->
                            false

                    if slot then
                        match Serve.accept handle with
                        | None -> gate.Release() |> ignore // the server closed — shut down
                        // a handler's `exit n` stopped the scope: no new
                        // request runs [D:serve-handler-exit]
                        | Some ctx when Session.isCut serveNode ->
                            Serve.abandon ctx
                            Serve.finish ctx
                            gate.Release() |> ignore
                            go ()
                        | Some ctx ->
                            let node = Session.openNode serveNode Session.NodeKind.Handler

                            let worker =
                                System.Threading.Thread(fun () -> runHandler ctx node)

                            worker.IsBackground <- true
                            inflight[worker] <- node
                            worker.Start()
                            go ()

                go ()

            let loopThread =
                System.Threading.Thread(System.Threading.ThreadStart acceptLoop)

            loopThread.IsBackground <- true
            loopThread.Start()

            // close the server — frees the port, drops queued requests,
            // gives in-flight handlers the drain together; idempotent
            // [D:serve-drain]. A signal landing meanwhile holds the
            // backstop [D:serve-drain-signal]
            let release () =
                Session.closingScope (fun () ->
                    Serve.stop handle drainMs
                    closing.Cancel()
                    Session.deregisterAlways hooked |> ignore
                    loopThread.Join 500 |> ignore

                    // a handler still running past the drain is cut at its next
                    // effect boundary and joined whole, its `always` cleanup
                    // included, so none of its effects lands after the scope
                    // [D:serve-cut] [D:serve-cut-joins-cleanup]; its writes are
                    // dropped and its result discarded, never raised
                    // [D:serve-drain]. Pure compute holds the join, as a pfirst
                    // loser's does
                    for kv in inflight do
                        Session.cutNode kv.Value 1 Session.CutReason.ServeCut true

                    for kv in inflight do
                        Session.joinThread kv.Key

                    Session.closeNode serveNode)

            let frame = Session.pushFrame release

            let v =
                try
                    try
                        Session.setCurrentNode serveNode

                        try
                            eval (Map.add binderName (VServer handle) env) body
                        finally
                            Session.setCurrentNode home
                    with
                    | :? ExitRequest -> reraise ()
                    // the body cut by a handler's `exit n`: raised below
                    | _ when not (isNull serveNode.Exit) -> VUnit
                finally
                    Session.releaseFrame frame release

            match serveNode.Exit with
            | null -> v
            | e -> raise e
        | WithinTmp ->
            // kind tmp [D:within-scopes]: a fresh unique directory,
            // bound as the binder for the block; removed on every
            // exit, normal and raise alike (the raise path is the
            // important guarantee). The delete is best-effort (a
            // vanished dir is fine).
            let binderName = binderOf "tmp"

            let dir =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-tmp-{System.Guid.NewGuid():N}")

            Session.createPrivateDir dir
            // the exit hook's backstop registration [D:exit-hook]: a hard
            // exit (pfirst exit-race, Ctrl-C) sweeps what this finally
            // could not; the clean path deregisters and the hook is idle
            Session.registerTmpDir dir
            // opened inside a plan: a captured op under it is refused
            // [D:plan-within-tmp]
            PlanMode.noteTmp dir

            let release () =
                (try
                    System.IO.Directory.Delete(dir, true)
                 with _ ->
                     ())

                Session.deregisterTmpDir dir

            let frame = Session.pushFrame release

            try
                eval (Map.add binderName (VStr dir) env) body
            finally
                Session.releaseFrame frame release
    | TEIf(cond, thn, els) ->
        match eval env cond, els with
        | VBool true, _ -> eval env thn
        | VBool false, Some e -> eval env e
        | VBool false, None -> VUnit
        | v, _ -> unreachable $"the checker rejects a non-bool condition: {formatValue v}"
    // TESplat [D:argv-splat] lives only in TECmd argv, expanded by
    // argvOf; it never reaches value evaluation. Closes the match so a
    // stray splat is a clear internal error, not a MatchFailureException.
    | TESplat _ -> unreachable "$@ splat outside command arguments (checker confines it to argv)"


// the yaml district's evaluator [D:yaml-district]: builds Yaml nodes —
// the lift is value-driven (the checker already enforced the liftable
// rule), a None splice omits its entry/item, `for` instantiates its
// body per element (binder = bindPattern, a lambda param's machinery),
// and runtime duplicate keys (for-generated or key-spliced) are
// errors — invalid YAML must not render silently.
and private liftYaml (v: Value) : Value option =
    match v with
    | VStr s -> Some(VUnion("YStr", Some(VStr s)))
    | VInt n -> Some(VUnion("YInt", Some(VInt n)))
    | VFloat f -> Some(VUnion("YFloat", Some(VFloat f)))
    | VBool b -> Some(VUnion("YBool", Some(VBool b)))
    | VUnion(("YStr" | "YInt" | "YFloat" | "YBool" | "YNull" | "YSeq" | "YMap"), _) -> Some v
    | VUnion("Some", Some inner) -> liftYaml inner
    | VUnion("None", None) -> None
    | VSeq items ->
        Some(
            VUnion(
                "YSeq",
                Some(
                    VSeq(
                        items
                        |> Seq.map (fun i -> liftYaml i |> Option.defaultValue (VUnion("YNull", None)))
                        |> List.ofSeq
                        |> List.toSeq
                    )
                )
            )
        )
    | v ->
        // as with sortBy, a polymorphic splice's constraint is
        // enforced here at runtime
        failwith
            $"yaml splice: got {formatValue v}; splices take string/int/float/bool, a Yaml node, Option of one, or a seq of those"

and private evalYamlTpl (env: Env) (tpl: Check.TypedYamlTpl) : Value =
    match tpl with
    // block scalar content never self-types: it is always a string
    // [D:block-scalars]
    | Check.TYtBlock(text, _) -> VUnion("YStr", Some(VStr text))
    | Check.TYtScalar(raw, quoted, _) -> yamlScalarValue raw quoted
    | Check.TYtDrop _ -> VUnion("|ydrop", None)
    | Check.TYtSplice te -> liftYaml (eval env te) |> Option.defaultValue (VUnion("YNull", None))
    | Check.TYtSeq(items, _) -> VUnion("YSeq", Some(VSeq(evalYamlItems env items |> List.toSeq)))
    | Check.TYtMap(entries, _) ->
        let pairs = evalYamlEntries env entries
        let seen = System.Collections.Generic.HashSet<string>()

        for (k, _) in pairs do
            if not (seen.Add k) then
                failwith $"yaml: duplicate key '{quoteData k}' (generated at runtime)"

        VUnion("YMap", Some(VSeq(pairs |> List.map (fun (k, v) -> VTuple [ VStr k; v ]) |> List.toSeq)))

and private evalYamlEntries (env: Env) (entries: Check.TypedYamlTplEntry list) : (string * Value) list =
    entries
    |> List.collect (fun entry ->
        match entry with
        | Check.TYtPair(key, value) ->
            let k =
                match key with
                | Check.TYtKeyLit(s, _) -> s
                | Check.TYtKeySplice te ->
                    match eval env te with
                    | VStr s -> s
                    | v -> unreachable $"the checker rejects a non-string key splice: {formatValue v}"

            // a splice value evaluating to None omits the whole entry
            // (the json-option omit, in template form)
            match value with
            | Check.TYtSplice te ->
                match liftYaml (eval env te) with
                | None -> []
                | Some node -> [ k, node ]
            | _ -> [ k, evalYamlTpl env value ]
        | Check.TYtForEntries(binder, source, body) ->
            match eval env source with
            | VSeq items ->
                items
                |> Seq.collect (fun item ->
                    let bs = bindPattern binder item
                    let env' = bs |> List.fold (fun m (n, v) -> Map.add n v m) env
                    evalYamlEntries env' body)
                |> List.ofSeq
            | v -> unreachable $"the checker rejects a non-seq for source: {formatValue v}")

and private evalYamlItems (env: Env) (items: Check.TypedYamlTplItem list) : Value list =
    items
    |> List.collect (fun item ->
        match item with
        | Check.TYtItem(Check.TYtSplice te) ->
            // a None splice omits its item; a seq splice flattens as items
            match eval env te with
            | VUnion("None", None) -> []
            | VSeq inner ->
                inner
                |> Seq.map (fun i -> liftYaml i |> Option.defaultValue (VUnion("YNull", None)))
                |> List.ofSeq
            | v -> liftYaml v |> Option.map List.singleton |> Option.defaultValue []
        | Check.TYtItem t -> [ evalYamlTpl env t ]
        | Check.TYtDropItem(t, _) -> [ VUnion("|ydropitem", Some(evalYamlTpl env t)) ]
        | Check.TYtForItems(binder, source, body) ->
            match eval env source with
            | VSeq elems ->
                elems
                |> Seq.collect (fun item ->
                    let bs = bindPattern binder item
                    let env' = bs |> List.fold (fun m (n, v) -> Map.add n v m) env
                    evalYamlItems env' body)
                |> List.ofSeq
            | v -> unreachable $"the checker rejects a non-seq for source: {formatValue v}")

// the chain left of a command hop, as specs [D:byte-pipes]: one builder
// for the captured chain and the inherited statement chain
// [D:armed-inherit], so the two cannot drift
and private chainSpecs env (arg: TypedExpr) chead cargs cenvO : Proc.Spec list =
    // the scope snapshot [D:ambient-capture] [D:ambient-capture-within]:
    // a lazy seq escaping a `within` scope still spawns under the scope
    // it was written in; outside any scope nothing is captured
    let snapCwd = Weir.Session.scopedCwd ()

    let snapAmb = Weir.Session.scopedAmbient ()

    let rec collect (e: TypedExpr) (acc: Proc.Spec list) : TypedExpr option * Proc.Spec list =
        match e.Kind with
        | TECmd(h2, a2, e2) ->
            None,
            { Proc.Prog = Proc.resolveProg (progOf env h2)
              Proc.Args = argvOf env a2
              Proc.Env = overlayOf env e2
              Proc.Input = None
              Proc.Cwd = snapCwd
              Proc.Ambient = snapAmb }
            :: acc
        | TEPipe(l, { Kind = TECmd(h2, a2, e2) }) ->
            collect
                l
                ({ Proc.Prog = Proc.resolveProg (progOf env h2)
                   Proc.Args = argvOf env a2
                   Proc.Env = overlayOf env e2
                   Proc.Input = None
                   Proc.Cwd = snapCwd
                   Proc.Ambient = snapAmb }
                 :: acc)
        | _ -> Some e, acc

    let valueHead, leftSpecs = collect arg []

    let stdin =
        match valueHead with
        | None -> None
        | Some he ->
            match eval env he with
            | VSeq items ->
                items
                |> Seq.map (fun v ->
                    match v with
                    | VStr s -> s
                    | v -> unreachable $"the checker rejects non-string stdin: {formatValue v}")
                |> pinnedFeed
                |> Some
            | v -> unreachable $"the checker rejects piping {formatValue v} into a command"

    let specs =
        (match leftSpecs with
         | first :: rest -> { first with Proc.Input = stdin } :: rest
         | [] -> [])
        @ [ { Proc.Prog = Proc.resolveProg (progOf env chead)
              Proc.Args = argvOf env cargs
              Proc.Env = overlayOf env cenvO
              Proc.Input = (if leftSpecs.IsEmpty then stdin else None)
              Proc.Cwd = snapCwd
              Proc.Ambient = snapAmb } ]

    specs

and apply (fn: Value) (arg: Value) : Value =
    match fn with
    | VClosure(param, body, closureEnv) ->
        if RunPos.IsCall body then
            inFunction (fun () -> eval (Map.add param arg closureEnv) body)
        else
            eval (Map.add param arg closureEnv) body
    | VClosurePat(pat, body, closureEnv) ->
        let bindings = bindPattern pat arg
        let env' = bindings |> List.fold (fun m (n, v) -> Map.add n v m) closureEnv

        if RunPos.IsCall body then
            inFunction (fun () -> eval env' body)
        else
            eval env' body
    | VBuiltin f ->
        // every builtin call is a pfirst loser's boundary [D:pfirst-join]
        Session.checkCancelled ()
        f arg
    | v -> unreachable $"the checker rejects application of {formatValue v}"

// an application: callee, then argument, then the call — which runs with
// the callee as the current node, so a raise from the call itself (`fail`,
// a builtin) is reported at the callee's column, not the line's first
// [D:runtime-position-call]
and private callAt (env: Env) (fn: TypedExpr) (arg: TypedExpr) : Value =
    let f = eval env fn
    let a = eval env arg

    // a reifier's call is its command's: blamed at the command, whose
    // program is the reifier's first argument
    let rec reifiedCmd (e: TypedExpr) =
        match e.Kind with
        | TEApp({ Kind = TEVar v }, first) when v.StartsWith '|' && (Ast.reifierOfVar v).IsSome -> Some first
        | TEApp(g, _) -> reifiedCmd g
        | _ -> None

    let at = reifiedCmd fn |> Option.defaultValue fn

    if at.Span.Start.Col >= 1 then
        RunPos.Current <- box at
        RunPos.Exact <- true

    let r = apply f a
    RunPos.Exact <- false
    r

// a call of a function defined elsewhere [D:runtime-position]: a raise
// unwinding through it is blamed on the innermost node first (if nothing
// blamed it yet), then gains this call site as a trace frame
and private inFunction (f: unit -> Value) : Value =
    let callSite = RunPos.Current

    try
        f ()
    with
    | ExitRequest _ -> reraise ()
    | ex ->
        let b = blameFor ex

        if isNull b.Node then
            b.Node <- RunPos.Current
            b.Exact <- RunPos.Exact

        if not (isNull callSite) then
            b.Frames.Add callSite

        reraise ()

/// the bare-statement spec (argv, overlay) — one builder for the relay
/// and the inheriting spawn, so the two forms cannot drift
let private commandStatementSpec (env: Env) (te: Check.TypedExpr) : Proc.Spec =
    match te.Kind with
    | Check.TECmd(head, args, cenvO) ->
        { Prog = Proc.resolveProg (progOf env head)
          Args = argvOf env args
          Env = overlayOf env cenvO
          Input = None
          Cwd = None
          Ambient = None }
    | _ -> unreachable "a command-statement helper takes the bare-command statement only"

/// the REPL's streaming statement echo [D:stream-echo] — spawns the
/// bare command via the chunk relay instead of eval'ing to a VSeq (a
/// seq<string> cannot carry a partial line). The caller owns rendering;
/// this owns the spawn (argv, overlay, nonzero raise). The relay's
/// remaining statement customer is the redirected-|print form; the
/// bare statement at a tty inherits instead [D:colour-inherit].
let streamCommandStatement (env: Env) (te: Check.TypedExpr) (onText: string -> unit) (onBreak: unit -> unit) : unit =
    Proc.streamSegmentsOf (commandStatementSpec env te) onText onBreak

/// colour from the child [D:colour-inherit]: the bare statement at a
/// tty spawns with stdout inherited, so isatty is true for the child
/// the inherit gate [D:colour-inherit] — one predicate, so the runner
/// and the REPL cannot answer it differently. TECmd is built with
/// Ty = seq<string> at its single site, so the type test is currently
/// redundant but stated anyway: it is the property the spawn depends
/// on, not an incidental fact about TECmd.
let inheritsStdout (te: Check.TypedExpr) : bool =
    match te.Kind with
    | Check.TECmd _
    // a statement chain's tail inherits too [D:armed-inherit]
    | Check.TEPipe(_, { Kind = Check.TECmd _ }) -> not System.Console.IsOutputRedirected && te.Ty = TSeq TStr
    | _ -> false

let inheritCommandStatement (env: Env) (te: Check.TypedExpr) : unit =
    match te.Kind with
    | Check.TEPipe(arg, { Kind = Check.TECmd(chead, cargs, cenvO) }) ->
        Proc.runChainInherited (chainSpecs env arg chead cargs cenvO)
    | _ -> Proc.runInherited (commandStatementSpec env te)

let constructorValues (cases: (string * Ty option * AttrSpec list) list) : (string * Value) list =
    cases
    |> List.map (fun (case, payload, _) ->
        match payload with
        | None -> case, VUnion(case, None)
        | Some _ -> case, VBuiltin(fun v -> VUnion(case, Some v)))
