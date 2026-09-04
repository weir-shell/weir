module Weir.Eval

open Weir.Types
open Weir.Ast
open Weir.Argv
open Weir.Check

let unreachable (why: string) : 'a = failwith $"unreachable: {why}"

// Exit.code's carrier: an intentional exit, not an error — the runner
// returns the code silently instead of printing a located message.
exception ExitRequest of code: int

// a scoped process handle [D:scoped-procs]: the live child plus its
// spill paths — identity IS the process (reference equality, pid hash);
// the scope that bound it owns the lifetime
[<ReferenceEquality>]
type ProcHandle =
    { Proc: System.Diagnostics.Process
      OutPath: string
      ErrPath: string
      SpillDir: string
      // joins the spill pumps (bounded) — called before any read that
      // must see the child's LAST words [D:scoped-procs]
      Drain: unit -> unit }

[<CustomEquality; NoComparison>]
type Value =
    | VInt of int64
    | VFloat of float
    | VDur of ms: int64
    | VInstant of ms: int64
    | VSize of bytes: int64
    // the non-text value [D:bytes]: renders as a SUMMARY everywhere a
    // renderer can reach it (raw bytes wreck terminals — the gzip
    // receipt); Bytes.toBase64 is the deliberate text exit
    | VBytes of byte[]
    | VStr of string
    // a Secret wraps a plain string [D:secret]; the renderers show *** —
    // Secret.reveal is the only unwrap
    | VSecret of string
    | VBool of bool
    | VUnit
    // fields in DECLARATION order [D:record-order] (wire order for an
    // anonymous shape read at a boundary): the ordered list IS the
    // container — order by construction, no parallel invariant; the
    // Map it replaced was 6-23x slower to build and bought nothing at
    // record widths. Equality is order-INSENSITIVE by the custom arm
    // below — the one place the rule lives, where it cannot drift.
    | VRecord of record: string * fields: (string * Value) list
    | VUnion of case: string * payload: Value option
    | VSeq of items: seq<Value>
    // string-keyed only [D:map-string]: every receipt has string keys
    // (JSON object keys ARE strings) and int keys would make Map the
    // first Ord-constrained container — widened only on a receipt
    | VMap of entries: Map<string, Value>
    | VTuple of items: Value list
    | VClosure of param: string * body: TypedExpr * env: Env
    | VClosurePat of binder: Pattern * body: TypedExpr * env: Env
    | VBuiltin of (Value -> Value)
    | VProc of handle: ProcHandle

    override this.Equals(other) =
        match other with
        | :? Value as v ->
            match this, v with
            | VInt a, VInt b -> a = b
            // finite-only and -0.0-normalized [D:floats]: reflexive
            | VFloat a, VFloat b -> a = b
            | VDur a, VDur b -> a = b
            | VInstant a, VInstant b -> a = b
            | VSize a, VSize b -> a = b
            // F# array equality is structural — byte equality, the Eq law
            | VBytes a, VBytes b -> a = b
            | VStr a, VStr b -> a = b
            | VSecret a, VSecret b -> a = b
            | VBool a, VBool b -> a = b
            | VUnit, VUnit -> true
            | VRecord(n1, f1), VRecord(n2, f2) ->
                // order-insensitive [D:record-order]: order is carried,
                // never semantic — two spellings of one record are equal
                n1 = n2
                && f1.Length = f2.Length
                && f1
                   |> List.forall (fun (k, v) ->
                       match f2 |> List.tryFind (fun (k2, _) -> k2 = k) with
                       | Some(_, v2) -> v = v2
                       | None -> false)
            | VUnion(c1, p1), VUnion(c2, p2) -> c1 = c2 && p1 = p2
            | VSeq a, VSeq b -> obj.ReferenceEquals(a, b) || List.ofSeq a = List.ofSeq b
            | VMap a, VMap b -> a = b
            | VTuple a, VTuple b -> a = b
            | VClosure(p1, b1, e1), VClosure(p2, b2, e2) -> p1 = p2 && b1 = b2 && obj.ReferenceEquals(e1, e2)
            | VClosurePat(p1, b1, e1), VClosurePat(p2, b2, e2) -> p1 = p2 && b1 = b2 && obj.ReferenceEquals(e1, e2)
            | VBuiltin f, VBuiltin g -> obj.ReferenceEquals(f, g)
            | VProc a, VProc b -> obj.ReferenceEquals(a.Proc, b.Proc)
            | _ -> false
        | _ -> false

    override this.GetHashCode() =
        match this with
        | VInt n -> hash n
        | VFloat f -> hash f
        | VDur n -> hash ("dur", n)
        | VInstant n -> hash ("instant", n)
        | VSize b -> hash ("size", b)
        | VBytes b -> hash ("bytes", b.Length)
        | VStr s -> hash s
        | VSecret s -> hash ("secret", s)
        | VBool b -> hash b
        | VUnit -> 17
        | VRecord(n, _) -> hash n
        | VUnion(c, _) -> hash c
        | VSeq _ -> 0
        | VMap m -> hash ("map", m.Count)
        | VTuple items -> hash (List.length items)
        | VClosure(p, _, _) -> hash p
        | VClosurePat(p, _, _) -> hash p
        | VBuiltin f -> LanguagePrimitives.PhysicalHash f
        | VProc h -> hash ("proc", h.Proc.Id)

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
    { MaxItems = 20
      MaxStr = None
      MaxDepth = System.Int32.MaxValue
      Ellipsis = "; ..." }

let private echoLimits =
    { MaxItems = 10
      MaxStr = Some 120
      MaxDepth = 8
      Ellipsis = "; …" }

// ordered-field access [D:record-order]: linear scan — record widths
// are single digits and the scan beat the Map it replaced; recSet
// replaces IN PLACE (a copy-and-update keeps the field's position)
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
        | VSize b -> formatSize b
        // a SUMMARY, never content [D:bytes]: raw bytes on a terminal
        // is the gzip failure; Bytes.toBase64 is the deliberate exit
        | VBytes b -> $"<{formatSize (int64 b.Length)}>"
        // the load-bearing render [D:secret]: *** ALWAYS, and because this
        // is the one recursive renderer, a Secret inside a shown record /
        // union / tuple / seq renders *** too (sub calls back here)
        | VSecret _ -> "***"
        | VStr s ->
            let raw, clipped =
                match lim.MaxStr with
                | Some m when s.Length > m -> s.Substring(0, m), true
                | _ -> s, false

            let escaped =
                raw.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t")

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
            | VBool _ -> $"{case} {inner}"
            | _ -> $"{case} ({inner})"
        | VMap entries ->
            // SORTED by construction (F# Map iterates in key order) — the
            // deterministic-output law; keys render through the string
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
        | VUnit -> "()"
        | VTuple items -> "(" + (items |> List.map sub |> String.concat ", ") + ")"

let formatValue (v: Value) : string = formatWith showLimits 0 v

// The REPL/-e echo [D:repl-echo]: bounded render + the way-out hint.
// The count shows only when already known (a materialized list) —
// counting a lazy seq would force it.
// the spill tail [D:scoped-procs]: the child's last words — stderr
// first (where diagnostics live), stdout filling the remainder; read
// SHARED (the pump holds the write handle and flushes per chunk)
let procTail (h: ProcHandle) : string list =
    // an exited child's spill must be COMPLETE before it is read — the
    // fast-exit race dropped the dying words from the watch error
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

// a one-line rendering of the tail for error messages — the fzf
// display's ⏎ join (multi-line content, one-line message)
let procTailLine (h: ProcHandle) : string =
    let t = procTail h

    match t |> List.skip (max 0 (t.Length - 5)) with
    | [] -> ""
    | lines -> " — last output: " + String.concat " ⏎ " lines

// the echo RULE [D:echo-rule]: a FORCED seq echoes in full (the user
// forced it; the ceiling is scrollback, which is theirs); an UNFORCED
// one shows the first N and names the lever that WORKS and renders
// identically — Seq.force. Forced-ness is the materialized-collection
// probe (the same one that used to print real counts).
let private forcedItems (items: seq<Value>) : Value list option =
    match items with
    | :? (Value list) as l -> Some l
    | :? System.Collections.Generic.ICollection<Value> as c -> Some(List.ofSeq c)
    | _ -> None

/// the footer names the cap IN EFFECT [D:echo-cap] — a hardcoded count
/// beside a configurable cap is the lying-message class
let unforcedHint (cap: int) =
    $"first {cap} of an unforced seq — Seq.force to echo everything"

// the piped/-e echo cap [D:echo-cap]: the SESSION cap is a tty-echo
// concern (the REPL owns it, #echo moves it); the piped surface and -e
// keep the historical constant — their bytes are pinned
let echoPipedCap: int option = Some 10

/// echo preparation [D:echo-once]: cache an unforced seq so the table
/// probe and the line rendering enumerate the SOURCE once — the
/// echoTable-then-echoValue composition re-enumerated, and a bare
/// command's child ran TWICE per echo
let echoPrep (v: Value) : Value =
    match v with
    | VSeq items when (forcedItems items).IsNone -> VSeq(Seq.cache items)
    | v -> v

// the unforced pull, bounded by the cap [D:echo-cap]: cap+1 when
// capped (the laziness guarantee — the echo never runs more than it
// shows), EVERYTHING when uncapped (#echo all is the user's own
// footgun; an infinite seq hangs, and the #help line says so)
let private cappedPull (cap: int option) (items: seq<Value>) : Value list * bool =
    match cap with
    | Some c ->
        let shown = items |> Seq.truncate (c + 1) |> List.ofSeq
        shown |> List.truncate c, shown.Length > c
    | None -> items |> List.ofSeq, false

// binary content must not reach a TERMINAL [D:binary-echo]: a NUL in
// the echo's pulled prefix marks the value binary (gzip at a tty — the
// live receipt for the parked bytes item) and the echo, weir's OWN
// rendering choice, refuses with the redirect hint; `print` stays the
// user's decision. NUL, never strict-UTF-8 (the misdetection class
// stays closed). The probe walks the echoPrep CACHE — no enumeration
// added; an uncapped echo probes a bounded prefix (101).
// The probe RECURSES through containers: a `| complete` RECORD holds
// the command's stdout, and the record echo leaked the bytes the seq
// echo refused — the membership shape again (the mechanism was right;
// records/tuples/unions/maps were missing). Secret renders *** and
// Bytes renders a summary, so neither can leak content and neither is
// probed.
let echoBinary (cap: int option) (v: Value) : bool =
    let bound =
        match cap with
        | Some c -> c + 1
        | None -> 101

    let rec has (v: Value) : bool =
        match v with
        | VStr s -> s.Contains '\u0000'
        | VSeq items -> items |> Seq.truncate bound |> Seq.exists has
        | VRecord(_, fields) -> fields |> List.exists (snd >> has)
        | VTuple items -> items |> List.exists has
        | VUnion(_, Some payload) -> has payload
        | VMap entries -> entries |> Map.exists (fun _ x -> has x)
        | _ -> false

    has v

let echoValue (cap: int option) (v: Value) : string * string option =
    match v with
    | VSeq items ->
        match forcedItems items with
        | Some all ->
            // full at the TOP level; each ELEMENT keeps the echo's inner
            // clips (a forced outer may hold a lazy inner — [nats] is
            // forcible and must not hang the echo). The cap NEVER clips
            // a forced seq [D:echo-cap] — forced-ness outranks it.
            let body = all |> List.map (formatWith echoLimits 1) |> String.concat "; "
            $"[{body}]", None
        | None ->
            // ONE forcing pass — the echo must not enumerate its source
            // twice: materialize the capped prefix, render from that list
            let visible, clipped = cappedPull cap items
            let body = visible |> List.map (formatWith echoLimits 1) |> String.concat "; "

            let ellipsis = if clipped then echoLimits.Ellipsis else ""

            $"[{body}{ellipsis}]", (if clipped then Some(unforcedHint cap.Value) else None)
    | _ -> formatWith echoLimits 0 v, None

// the REPL's TABLE rendering [D:repl-table] — PRESENTATION ONLY: show
// stays canonical and every other consumer is untouched; the REPL's
// echo (tty-gated by the CALLER) renders a seq of same-shaped records
// with scalar fields as aligned columns. Cells reuse show's spellings
// except strings, which drop their quotes (a display, not a literal);
// columns are alphabetical (show's own field law); numeric-ish columns
// right-align. Widths are char counts (the wrap math's assumption).
/// "a week ago" — the table's rendering of an Instant [D:filerow];
/// show/interpolation keep ISO (assert BOTH or the split is unpinned)
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
            | Some m when s.Length > m -> s.Substring(0, m - 1) + "…"
            | _ -> s

        Some(clipped, false)
    | VInt _
    | VFloat _
    | VSize _
    | VBytes _
    | VDur _ -> Some(formatWith echoLimits 0 v, true)
    // RELATIVE in the table, ISO everywhere else [D:filerow]: the
    // Duration split (lossless show, abbreviated cell) — no staleness,
    // the row carries the absolute Instant and this renders at echo
    | VInstant ms -> Some(relativeInstant (System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ms, false)
    | VBool b -> Some((if b then "true" else "false"), false)
    | VSecret _ -> Some("***", false)
    | VUnion("None", None) -> Some("", false)
    | VUnion("Some", Some inner) -> tableCell inner
    | VUnion(name, None) -> Some(name, false)
    | _ -> None

/// the LINES form [D:echo-lines]: seq<string> presents as its lines at
/// a tty — the content arrived as lines and the literal was UNDOING
/// that. RAW and unclipped per line (print parity — a tab or an escape
/// renders as itself; tty-only, so the piped surface cannot move); the
/// COUNT clip and the forced/unforced sentence ride the footer
/// unchanged. Keyed on the TYPE at the caller (seq<string> exactly) —
/// never content-sniffing.
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

            // an all-None optional column HIDES [D:filerow]: a column
            // that says nothing on every shown row costs width saying
            // it — general table rule, not a FileRow special case; one
            // Some anywhere and the column is back
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
                    |> List.map (fun i -> rows |> List.fold (fun w r -> max w (fst r[i]).Length) showKeys[i].Length)

                // terminal-width clamping [D:table-polish]: the WIDEST
                // column above its floor absorbs the clip (path,
                // usually), repeatedly until the table fits; a terminal
                // too narrow even for the floors renders unclamped —
                // the terminal wraps, stated, never a mangled floor
                let widths =
                    match width with
                    | Some termW ->
                        let sep = 2 * (showKeys.Length - 1)
                        let floorOf i = max 5 showKeys[i].Length

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
                    if t.Length > w then
                        t.Substring(0, max 0 (w - 1)) + "…"
                    else
                        t

                let pad (i: int) (t: string) =
                    let t = clip widths[i] t

                    if numeric[i] then
                        t.PadLeft(widths[i])
                    else
                        t.PadRight(widths[i])

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
// (REPL let/expr arms, -e). The old pipe-to-print suggestion RETIRED
// [D:echo-rule]: it promised continuation and delivered a different
// rendering; the hint now names the lever that reproduces this one.
let echoTail (hint: string option) : string =
    match hint with
    | Some h -> $" ({h})"
    | None -> ""

// The line-per-element renderer. Both consumers — the print builtin and the
// runner's command-statement streaming — must call this one function; the
// byte-identity of their output is a plan-level claim, not a coincidence.
let writeLinesTo (w: System.IO.TextWriter) (items: seq<Value>) : unit =
    for item in items do
        match item with
        | VStr s -> w.WriteLine s
        | other -> w.WriteLine(formatValue other)

let writeLines (items: seq<Value>) : unit = writeLinesTo System.Console.Out items

// Overflow policy (Part 3): int arithmetic is CHECKED — wrapping silently
// is the bash-calculator bug class; a raise joins the named runtime
// failure classes instead.
let private checkedInt (f: unit -> int64) : Value =
    try
        VInt(f ())
    with :? System.OverflowException ->
        failwith "integer overflow"

// non-finite results RAISE [D:floats] — the checkedInt law applied to
// the new type; -0.0 normalizes so equality and rendering never split
let private checkedFloat (op: string) (f: unit -> float) : Value =
    let r = f ()

    if System.Double.IsFinite r then
        VFloat(if r = 0.0 then 0.0 else r)
    else
        failwith $"'{op}' produced a non-finite float (overflow)"

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
    | "+", VSize a, VSize b -> VSize(Checked.(+) a b)
    | "-", VSize a, VSize b -> VSize(Checked.(-) a b)
    | "*", VSize a, VInt b -> VSize(Checked.(*) a b)
    | "*", VInt a, VSize b -> VSize(Checked.(*) a b)
    | "/", VSize a, VInt b -> VSize(a / b)
    | ">", VSize a, VSize b -> VBool(a > b)
    | "<", VSize a, VSize b -> VBool(a < b)
    | ">=", VSize a, VSize b -> VBool(a >= b)
    | "<=", VSize a, VSize b -> VBool(a <= b)
    | "+", VDur a, VDur b -> VDur(Checked.(+) a b)
    | "-", VDur a, VDur b -> VDur(Checked.(-) a b)
    | "*", VDur a, VInt b -> VDur(Checked.(*) a b)
    | "*", VInt a, VDur b -> VDur(Checked.(*) a b)
    | "/", VDur a, VInt b -> VDur(a / b)
    | "-", VInstant a, VInstant b -> VDur(Checked.(-) a b)
    | "+", VInstant a, VDur d -> VInstant(Checked.(+) a d)
    | "+", VDur d, VInstant a -> VInstant(Checked.(+) a d)
    | "-", VInstant a, VDur d -> VInstant(Checked.(-) a d)
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
        // weir's own text [D:message-ownership] — the float twin above
        // already said it; the int side leaked the BCL's
        if b = 0L then
            failwith "division by zero"
        else
            checkedInt (fun () -> a / b)
    | "%", VInt a, VInt b ->
        // TRUNCATED, .NET's own % [D:modulo] — sign follows the
        // dividend (-7 % 3 = -1), matching F# so the oracle stays
        // divergence-free; /'s zero discipline, %'s word
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

let private jsonLine (renames: Map<string, Map<string, string>>) (v: Value) : string =
    let buffer = new System.Buffers.ArrayBufferWriter<byte>()
    use writer = new System.Text.Json.Utf8JsonWriter(buffer)

    let rec write (v: Value) =
        match v with
        | VInt n -> writer.WriteNumberValue n
        // the show shape [D:floats-boundaries]: 1.0 emits as 1.0 (a
        // float field is not an integer field); formatFloat is always a
        // valid JSON number — non-finite is unrepresentable by law
        | VFloat f -> writer.WriteRawValue(formatFloat f)
        | VStr s -> writer.WriteStringValue s
        | VBool b -> writer.WriteBooleanValue b
        // Option [D:json-option]: Some writes its scalar; a bare None at the
        // element level is a null line (a None FIELD is OMITTED, below)
        | VUnion("Some", Some inner) -> write inner
        | VUnion("None", None) -> writer.WriteNullValue()
        | VSeq items ->
            // a nested array [D:recursive-fields] — elements recurse;
            // an Option element writes null for None (an array slot
            // cannot be omitted; null reads back as None)
            writer.WriteStartArray()
            items |> Seq.iter write
            writer.WriteEndArray()
        | VMap entries ->
            // an OBJECT, keys sorted by construction [D:map-string] —
            // the round-trip's write half
            writer.WriteStartObject()

            for kv in entries do
                writer.WritePropertyName kv.Key
                write kv.Value

            writer.WriteEndObject()
        | VRecord(rname, fields) ->
            writer.WriteStartObject()

            // the wire key comes back on WRITE [D:wire-keys] — the
            // roundtrip's other half; the rename table rides the TETo
            // node (attrs never reach values)
            let rens = Map.tryFind rname renames |> Option.defaultValue Map.empty

            for kv in fields do
                // THE FORK [D:json-option]: a None field OMITS its key — a
                // weir-produced payload looks like the ecosystem's (gh /
                // kubectl / docker inspect omit rather than null). Missing
                // and null both read back as None, so the roundtrip holds.
                match snd kv with
                | VUnion("None", None) -> ()
                | _ ->
                    writer.WritePropertyName(Map.tryFind (fst kv) rens |> Option.defaultValue (fst kv))
                    write (snd kv)

            writer.WriteEndObject()
        | v -> unreachable $"the checker rejects 'to json' on {formatValue v}"

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

/// one OBJECT document/element -> one row of `def`. `who` names the
/// adapter in every message ("from json" / "from jsonl") and `shown`
/// is the input to cite — the line for jsonl, a snippet for a joined
/// document [D:from-jsonl]
/// parse one document and read it under the DECLARED shape: wantSeq
/// demands a top-level array (one row per element), otherwise an object
/// — the type decides what the top level must be, never the input
/// [D:from-json-seq]
let private jsonDoc
    (who: string)
    (wantSeq: bool)
    (wantMap: bool)
    (def: RecordDef)
    (defs: Map<string, RecordDef>)
    (shown: string)
    (text: string)
    : Value =
    use doc =
        try
            System.Text.Json.JsonDocument.Parse text
        with ex ->
            // never System.Text.Json's words [D:json-boundary]
            failwith $"{who}: not valid JSON: {shown}"

    let root = doc.RootElement

    // read a scalar of type `scalarTy` from an already-fetched, non-null
    // property [D:json-option]
    let readScalar (name: string) (scalarTy: Ty) (prop: System.Text.Json.JsonElement) =
        match scalarTy, prop.ValueKind with
        | TInt, System.Text.Json.JsonValueKind.Number ->
            match prop.TryGetInt64() with
            | true, n -> VInt n
            | _ ->
                // TryGetInt64 fails for BOTH decimals and integer-shaped
                // overflow — the raw token tells them apart, so the message
                // never calls 99999999999999999999 "a decimal"
                // [D:format-surface-json]
                let raw = prop.GetRawText()

                if raw.Contains '.' || raw.Contains 'e' || raw.Contains 'E' then
                    failwith $"{who}: field '{name}' expected int, got a decimal number — declare it float"
                else
                    failwith $"{who}: field '{name}': number out of int range — declare it float"
        | TFloat, System.Text.Json.JsonValueKind.Number ->
            // integer-shaped numbers WIDEN here [D:floats-boundaries]:
            // JSON has one number type — this is a parse, not weir
            // arithmetic, so the no-implicit-widening rule does not bite
            let d = prop.GetDouble()

            if System.Double.IsFinite d then
                VFloat(if d = 0.0 then 0.0 else d)
            else
                failwith $"{who}: field '{name}': number out of float range"
        | TStr, System.Text.Json.JsonValueKind.String -> VStr(prop.GetString())
        | TBool, System.Text.Json.JsonValueKind.True -> VBool true
        | TBool, System.Text.Json.JsonValueKind.False -> VBool false
        | ty, kind -> failwith $"{who}: field '{name}' expected {formatTy ty}, got {kind} in: {shown}"

    // the RECURSIVE reader [D:recursive-fields]: nested objects convert
    // through `defs` (the check-time closure — eval has no env), arrays
    // through the element type; Option means the SAME thing at every
    // depth (null -> None), and paths name the location
    let rec readValue (name: string) (ty: Ty) (prop: System.Text.Json.JsonElement) : Value =
        match ty with
        | TNamed("Option", [ inner ]) ->
            if prop.ValueKind = System.Text.Json.JsonValueKind.Null then
                VUnion("None", None)
            else
                VUnion("Some", Some(readValue name inner prop))
        | TSeq elem ->
            if prop.ValueKind <> System.Text.Json.JsonValueKind.Array then
                failwith
                    $"{who}: field '{name}' expected an array ({formatTy ty}), got {jsonKindName prop.ValueKind} in: {shown}"

            // forced before the document disposes
            prop.EnumerateArray()
            |> Seq.mapi (fun i el -> readValue $"{name}[{i + 1}]" elem el)
            |> List.ofSeq
            |> List.toSeq
            |> VSeq
        | TNamed("Map", [ TStr; inner ]) ->
            // the ID-keyed object [D:map-string]: every property VALUE
            // reads as the map's value type; duplicate keys LAST-WIN
            // (System.Text.Json's own lookup — the boundary's stated law)
            if prop.ValueKind <> System.Text.Json.JsonValueKind.Object then
                failwith
                    $"{who}: field '{name}' expected an object ({formatTy ty}), got {jsonKindName prop.ValueKind} in: {shown}"

            prop.EnumerateObject()
            |> Seq.fold (fun m p -> Map.add p.Name (readValue $"{name}[\"{p.Name}\"]" inner p.Value) m) Map.empty
            |> VMap
        | TNamed(n, []) when defs.ContainsKey n ->
            if prop.ValueKind <> System.Text.Json.JsonValueKind.Object then
                failwith
                    $"{who}: field '{name}' expected an object ({n}), got {jsonKindName prop.ValueKind} in: {shown}"

            objRow $"{name}." defs[n] prop
        | scalarTy -> readScalar name scalarTy prop

    // one OBJECT element -> one row (the param shadows the document root
    // on purpose: the field readers below say `root` either way);
    // `prefix` is the dotted path above this object ("" at the top)
    and objRow (prefix: string) (rdef: RecordDef) (root: System.Text.Json.JsonElement) =
        let readField (name: string, ty: Ty) =
            let shownName = prefix + name
            // the WIRE key [D:wire-keys]: [<Wire "type">] kind reads the
            // document's "type"; paths keep the weir field name
            let wire = Types.wireName rdef name
            let mutable prop = Unchecked.defaultof<System.Text.Json.JsonElement>
            let present = root.TryGetProperty(wire, &prop)
            let isNull = present && prop.ValueKind = System.Text.Json.JsonValueKind.Null

            let value =
                match ty with
                // an Option field: missing key OR explicit null -> None;
                // present -> Some (readValue keeps the rule at depth)
                | TNamed("Option", [ inner ]) ->
                    if not present || isNull then
                        VUnion("None", None)
                    else
                        VUnion("Some", Some(readValue shownName inner prop))
                // a required field: missing or null both fail — null names
                // the fix (a missing ARRAY is an error too: absence is
                // Option's job, [] is not guessed)
                | _ when not present ->
                    let wireNote = if wire <> name then $" (wire key \"{wire}\")" else ""
                    failwith $"{who}: missing field '{shownName}'{wireNote} in: {shown}"
                | _ when isNull ->
                    failwith
                        $"{who}: field '{shownName}' is null; declare it Option<{formatTy ty}> to allow it, in: {shown}"
                | _ -> readValue shownName ty prop

            name, value

        // fields read in DECLARATION order; an ANONYMOUS shape takes the
        // WIRE's order instead [D:record-order] — the one place order
        // comes from data, which is what makes read-modify-write
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

    if wantMap then
        // the ID-keyed object [D:map-string]: the top level IS the map —
        // each property value reads as one row; duplicate keys LAST-WIN
        match root.ValueKind with
        | System.Text.Json.JsonValueKind.Object ->
            root.EnumerateObject()
            |> Seq.fold
                (fun m p ->
                    if p.Value.ValueKind <> System.Text.Json.JsonValueKind.Object then
                        failwith
                            $"{who}: key \"{p.Name}\" expected an object ({def.Name}), got {jsonKindName p.Value.ValueKind} in: {shown}"

                    Map.add p.Name (objRow $"[\"{p.Name}\"]." def p.Value) m)
                Map.empty
            |> VMap
        | System.Text.Json.JsonValueKind.Array ->
            failwith
                $"{who}: the top level is a JSON array, but the declared type is Map<string, {def.Name}> — declare seq<{def.Name}> to read an array, in: {shown}"
        | k ->
            failwith
                $"{who}: the top level is a JSON {jsonKindName k}, but the declared type is Map<string, {def.Name}>, in: {shown}"
    else

        match wantSeq, root.ValueKind with
        | false, System.Text.Json.JsonValueKind.Object -> objRow "" def root
        | true, System.Text.Json.JsonValueKind.Array ->
            root.EnumerateArray()
            |> Seq.mapi (fun i el ->
                if el.ValueKind <> System.Text.Json.JsonValueKind.Object then
                    failwith
                        $"{who}: array element {i + 1} is a JSON {jsonKindName el.ValueKind}, not an object, in: {shown}"
                else
                    objRow "" def el)
            // forced BEFORE the document disposes; then seq for the ctor
            |> List.ofSeq
            |> List.toSeq
            |> VSeq
        | true, System.Text.Json.JsonValueKind.Object ->
            failwith
                $"{who}: expected an array (the declared type is seq<{def.Name}>); got an object — write from json {def.Name}, in: {shown}"
        | true, k ->
            failwith
                $"{who}: the top level is a JSON {jsonKindName k}, but the declared type is seq<{def.Name}>, in: {shown}"
        | false, System.Text.Json.JsonValueKind.Array when who = "from json" ->
            // the pointer is REAL now: the spelling exists
            failwith
                $"{who}: the top level is a JSON array, not an object — declare seq<{def.Name}> to read it, in: {shown}"
        | false, k ->
            let contract =
                if who = "from json" then
                    "from json T reads one object document"
                else
                    "from jsonl T reads one object per element"

            failwith $"{who}: the top level is a JSON {jsonKindName k}, not an object — {contract}, in: {shown}"

// a document snippet for error messages: whole if short, elided middle
// if not (a joined body can be megabytes; the message stays a message)
let private jsonSnippet (text: string) : string =
    let t = text.Trim()
    if t.Length <= 120 then t else t.Substring(0, 117) + "..."

let private fromAdapter
    (fmt: string)
    (seqOf: bool)
    (mapOf: bool)
    (def: RecordDef)
    (defs: Map<string, RecordDef>)
    : Value =
    match fmt with
    // ONE document -> T: join the elements back into the text they came
    // from (a pretty-printed body pipes straight in) [D:from-jsonl]
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

                jsonDoc "from json" seqOf mapOf def defs (jsonSnippet text) text
            | v -> unreachable $"the checker rejects 'from' on {formatValue v}")
    // one document per element -> seq<T> (NDJSON, `to json`'s shape)
    | "jsonl" ->
        VBuiltin(fun v ->
            match v with
            | VSeq lines ->
                VSeq(
                    lines
                    |> Seq.map (fun l ->
                        match l with
                        | VStr s -> jsonDoc "from jsonl" false false def defs s s
                        | v -> unreachable $"the checker rejects 'from' on non-string elements: {formatValue v}")
                )
            | v -> unreachable $"the checker rejects 'from' on {formatValue v}")
    | f -> unreachable $"the checker rejects unknown format '{f}'"

// ---- the yaml boundary [D:yaml-v1] ----------------------------------------

// shape-directed conversion: the checker packed the resolved target tree;
// every error carries the node's LINE (the owned parser's positions —
// the bar YamlDotNet's messages missed)
let rec private yamlConvert (shape: Yaml.Shape) (node: Yaml.Node) : Value =
    match shape, node with
    | Yaml.SOpt _, Yaml.NNull _ -> VUnion("None", None)
    | Yaml.SOpt inner, n -> VUnion("Some", Some(yamlConvert inner n))
    | Yaml.SInt, Yaml.NScalar(raw, quoted, line) ->
        if quoted then
            failwith $"from yaml: line {line}: a quoted scalar is a string; this field expects int"
        else
            match System.Int64.TryParse raw with
            | true, n -> VInt n
            | _ -> failwith $"from yaml: line {line}: expected int, got '{raw}'"
    | Yaml.SBool, Yaml.NScalar(raw, quoted, line) ->
        // EXACTLY true/false (the Env.load law: `yes`/`on`/`1` are data,
        // not booleans — the Norway problem never fires by construction)
        if quoted then
            failwith $"from yaml: line {line}: a quoted scalar is a string; this field expects bool"
        elif raw = "true" then
            VBool true
        elif raw = "false" then
            VBool false
        else
            failwith $"from yaml: line {line}: expected bool (exactly true/false), got '{raw}'"
    | Yaml.SFloat, Yaml.NScalar(raw, quoted, line) ->
        if quoted then
            failwith $"from yaml: line {line}: a quoted scalar is a string; this field expects float"
        else
            // parseFloat rejects non-finite; .inf/.nan additionally
            // TEACH — yaml spells them, weir's law forbids the value
            match raw.Trim().ToLowerInvariant() with
            | ".inf"
            | "-.inf"
            | "+.inf"
            | ".nan" ->
                failwith
                    $"from yaml: line {line}: '{raw}' is not representable — weir floats are finite (non-finite results raise; there is no value to read into)"
            | _ ->
                match parseFloat raw with
                | Ok f -> VFloat f
                | Error _ -> failwith $"from yaml: line {line}: expected float, got '{raw}'"
    | Yaml.SStr, Yaml.NScalar(raw, _, _) -> VStr raw
    // blockness is quotedness's sibling [D:block-scalars]: a block
    // scalar is unambiguously a string
    | Yaml.SStr, Yaml.NBlock(text, _) -> VStr text
    | Yaml.SInt, Yaml.NBlock(_, line) ->
        failwith $"from yaml: line {line}: a block scalar is a string; this field expects int"
    | Yaml.SFloat, Yaml.NBlock(_, line) ->
        failwith $"from yaml: line {line}: a block scalar is a string; this field expects float"
    | Yaml.SBool, Yaml.NBlock(_, line) ->
        failwith $"from yaml: line {line}: a block scalar is a string; this field expects bool"
    | Yaml.SRec(name, fields), Yaml.NMap(entries, line) ->
        // extra keys are IGNORED (the from-json precedent); a missing or
        // null REQUIRED field teaches Option (the json-option precedent)
        let get fname =
            entries |> List.tryFind (fun (k, _) -> k = fname)

        let fieldValues =
            fields
            |> List.map (fun (fname, wire, fshape) ->
                // the WIRE key matches the document; the FIELD names the
                // record (and the message cites both when they differ)
                // [D:wire-keys]
                let wireNote = if wire <> fname then $" (wire key \"{wire}\")" else ""

                match get wire, fshape with
                | None, Yaml.SOpt _ -> fname, VUnion("None", None)
                | None, Yaml.SSeq _ -> fname, VSeq Seq.empty
                | None, Yaml.SPairs _ -> fname, VSeq Seq.empty
                | None, _ -> failwith $"from yaml: line {line}: missing field '{fname}'{wireNote} in '{name}'"
                | Some(_, Yaml.NNull l), (Yaml.SInt | Yaml.SFloat | Yaml.SStr | Yaml.SBool | Yaml.SRec _) ->
                    failwith $"from yaml: line {l}: field '{fname}' is null; declare it Option<…> to allow it"
                | Some(_, v), _ -> fname, yamlConvert fshape v)

        VRecord(name, fieldValues)
    | Yaml.SSeq inner, Yaml.NSeq(items, _) -> VSeq(items |> List.map (yamlConvert inner) |> List.toSeq)
    // a null where a seq/mapping sits is the EMPTY collection (the yaml
    // idiom: `ports:` with nothing below)
    | Yaml.SSeq _, Yaml.NNull _ -> VSeq Seq.empty
    | Yaml.SPairs inner, Yaml.NMap(entries, _) ->
        VSeq(
            entries
            |> List.map (fun (k, v) -> VTuple [ VStr k; yamlConvert inner v ])
            |> List.toSeq
        )
    | Yaml.SPairs _, Yaml.NNull _ -> VSeq Seq.empty
    | shape, node ->
        let want =
            match shape with
            | Yaml.SInt -> "an int scalar"
            | Yaml.SFloat -> "a float scalar"
            | Yaml.SStr -> "a string scalar"
            | Yaml.SBool -> "a bool scalar"
            | Yaml.SRec(n, _) -> $"a mapping ({n})"
            | Yaml.SSeq _ -> "a sequence"
            | Yaml.SPairs _ -> "a mapping"
            | Yaml.SOpt _ -> "an optional value"

        let got =
            match node with
            | Yaml.NScalar _ -> "a scalar"
            | Yaml.NBlock _ -> "a block scalar"
            | Yaml.NNull _ -> "null"
            | Yaml.NSeq _ -> "a sequence"
            | Yaml.NMap _ -> "a mapping"

        failwith $"from yaml: line {Yaml.nodeLine node}: expected {want}, got {got}"

let private yamlFromImpl (shape: Yaml.Shape) : Value =
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
            | Ok [] -> failwith "from yaml: empty input — expected one document"
            | Ok [ doc ] ->
                // ONE document; the declared shape names the top level
                // [D:yaml-seq] — pointers cross to the other spelling
                match shape, doc with
                | Yaml.SSeq _, Yaml.NMap _ ->
                    failwith
                        "from yaml: expected a sequence at the top level (the declared type is seq<…>); got a mapping — write from yaml T"
                | Yaml.SRec(n, _), Yaml.NSeq _ ->
                    failwith $"from yaml: the top level is a sequence, not a mapping — declare seq<{n}> to read it"
                | _ -> yamlConvert shape doc
            | Ok docs ->
                // multi-document streams retired [D:yaml-seq]: weir cannot
                // type a heterogeneous stream, and homogeneous ones are rare
                failwith
                    $"from yaml: reads one document; this input has {List.length docs} documents — split on '---' and parse each"
        | v -> unreachable $"the checker rejects 'from yaml' on {formatValue v}")

// the renderer: VALUE-driven (records/seqs/scalars/Option/Yaml nodes).
// Record fields render ALPHABETICALLY (the VRecord representation's
// existing law — same as to json); YMap preserves ITS order (the
// user-controlled escape). A None FIELD omits its key; a None ELEMENT
// renders `null` (both the json-option split).
type private Rendered =
    | Inline of string
    | Block of string list
    // a literal block scalar [D:block-scalars]: the header rides the
    // key/item line, content lines indent one level under it
    | BlockScalar of header: string * content: string list

// a string renders as a block scalar when it holds newlines (and no
// wilder control characters) [D:block-scalars]; the form is
// DETERMINISTIC — one trailing newline is `|`, none is `|-`, more have
// no form in the subset (`|+` is rejected) and dropping bytes is the
// one thing a renderer must never do, so that errors
let private renderString (s: string) : Rendered =
    let tame =
        s |> Seq.forall (fun c -> not (System.Char.IsControl c) || c = '\n' || c = '\t')

    // multiple trailing newlines have no block form in the subset (`|+`
    // is rejected) — they FALL BACK to the quoted-with-escapes spelling:
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

let rec private yamlRender (renames: Map<string, Map<string, string>>) (v: Value) : Rendered =
    let indent2 (lines: string list) =
        lines |> List.map (fun l -> if l = "" then "" else "  " + l)

    let renderMap (entries: (string * Value) list) : string list =
        entries
        |> List.collect (fun (k, v) ->
            match v with
            | VUnion("None", None) -> [] // omit the key [D:json-option]'s yaml face
            | _ ->
                let key = Yaml.renderScalar k

                match yamlRender renames v with
                | Inline "" -> [ $"{key}:" ]
                | Inline s -> [ $"{key}: {s}" ]
                | Block lines -> $"{key}:" :: indent2 lines
                | BlockScalar(h, content) -> $"{key}: {h}" :: indent2 content)

    let renderSeq (items: Value list) : string list =
        items
        |> List.collect (fun item ->
            match yamlRender renames item with
            | Inline "" -> [ "- null" ]
            | Inline s -> [ $"- {s}" ]
            | Block lines ->
                match lines with
                | [] -> [ "-" ]
                | first :: rest -> ($"- {first}") :: (rest |> List.map (fun l -> "  " + l))
            | BlockScalar(h, content) -> ($"- {h}") :: indent2 content)

    match v with
    | VInt n -> Inline(string n)
    | VFloat f -> Inline(formatFloat f)
    | VDur n -> Inline(formatDuration n)
    | VSize b -> Inline(formatSize b)
    | VBool b -> Inline(if b then "true" else "false")
    | VStr s -> renderString s
    | VUnion("YStr", Some(VStr s)) -> renderString s
    | VUnion("YInt", Some(VInt n)) -> Inline(string n)
    | VUnion("YFloat", Some(VFloat f)) -> Inline(formatFloat f)
    | VUnion("YBool", Some(VBool b)) -> Inline(if b then "true" else "false")
    | VUnion("YNull", None) -> Inline ""
    | VUnion("YSeq", Some(VSeq items)) -> Block(renderSeq (List.ofSeq items))
    | VUnion("YMap", Some(VSeq pairs)) ->
        Block(
            renderMap (
                pairs
                |> Seq.map (fun p ->
                    match p with
                    | VTuple [ VStr k; v ] -> k, v
                    | v -> unreachable $"the checker rejects YMap over {formatValue v}")
                |> List.ofSeq
            )
        )
    | VUnion("Some", Some inner) -> yamlRender renames inner
    | VUnion("None", None) -> Inline "null" // element position; fields omit above
    | VRecord(rname, fields) ->
        // wire keys on the yaml wire too [D:wire-keys]
        let rens = Map.tryFind rname renames |> Option.defaultValue Map.empty

        Block(
            renderMap (
                fields
                |> List.map (fun (f, fv) -> (Map.tryFind f rens |> Option.defaultValue f), fv)
            )
        )
    | VSeq items ->
        let items = List.ofSeq items

        // a pair-seq is ONE mapping (the seq<string * _> law)
        let asPairs =
            items
            |> List.map (fun i ->
                match i with
                | VTuple [ VStr k; v ] -> Some(k, v)
                | _ -> None)

        if not items.IsEmpty && asPairs |> List.forall Option.isSome then
            Block(renderMap (asPairs |> List.map Option.get))
        else
            Block(renderSeq items)
    | v -> unreachable $"the checker rejects 'to yaml' on {formatValue v}"

let private yamlToLines (renames: Map<string, Map<string, string>>) (v: Value) : string list =
    match yamlRender renames v with
    | Inline s -> [ s ]
    | Block lines -> lines
    | BlockScalar(h, content) -> h :: (content |> List.map (fun l -> if l = "" then "" else "  " + l))

let private yamlToImpl (renames: Map<string, Map<string, string>>) : Value =
    VBuiltin(fun v ->
        match v with
        // a top-level SEQ is `---`-separated DOCUMENTS — except a
        // pair-seq, which is ONE mapping document (the check-side rule)
        | VSeq items when
            (let l = List.ofSeq items

             not l.IsEmpty
             && l
                |> List.forall (fun i ->
                    match i with
                    | VTuple [ VStr _; _ ] -> true
                    | _ -> false))
            ->
            VSeq(yamlToLines renames v |> List.map VStr |> List.toSeq)
        | VSeq items ->
            let docs = items |> Seq.map (yamlToLines renames) |> List.ofSeq

            let lines =
                match docs with
                | [] -> []
                | first :: rest -> first @ (rest |> List.collect (fun d -> "---" :: d))

            VSeq(lines |> List.map VStr |> List.toSeq)
        | v -> VSeq(yamlToLines renames v |> List.map VStr |> List.toSeq))

let scalarString (what: string) (v: Value) : string =
    match v with
    | VStr s -> s
    // a Secret splices to argv in the CLEAR [D:secret]: the argv ruling —
    // `curl -H $auth` needs the real value. print/printerr reject Secret
    // at the type (printArgTy), so this arm is reached only via argv
    | VSecret s -> s
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
        // irrefutable by checker law [D:record-patterns]: every field
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
             let m = rx.Match s

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
// BEFORE validation (help must work on invalid invocations)

let private argvValueSlot (ty: Ty) : string =
    match ty with
    | TInt
    | TNamed("Option", [ TInt ]) -> " <int>"
    | TStr
    | TNamed("Option", [ TStr ]) -> " <string>"
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
// together — short derivation runs over the UNION, so a cross-tier
// contest (-q for --quiet and --query) derives for NEITHER in that scope
let private scopeDef (sharedDef: RecordDef) (payloadDef: RecordDef option) : RecordDef =
    match payloadDef with
    | Some pd ->
        { sharedDef with
            Fields = sharedDef.Fields @ pd.Fields
            Attrs = pd.Attrs |> Map.fold (fun m k v -> Map.add k v m) sharedDef.Attrs
            // the two-tier help draws --help text from BOTH tiers [D:doc-help]
            Docs = pd.Docs |> Map.fold (fun m k v -> Map.add k v m) sharedDef.Docs }
    | None -> sharedDef

// pass 1 of the shared-flags scan: shared flags float, the FIRST
// non-flag token anchors as the case selector (an unknown flag consumes
// no value — the standing precedent)
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

let private argvUsage (target: ArgsTarget) (argv: string list) : string =
    match target with
    | ArgsRecord def -> String.concat "\n" ("usage: [flags]" :: argvUsageLines def)
    | ArgsUnion(udef, payloads) ->
        let caseLines = udef.Cases |> List.map (fun (c, _) -> "  " + c.ToLowerInvariant())

        let blocks =
            udef.Cases
            |> List.collect (fun (c, _) ->
                match Map.tryFind c payloads with
                | Some rdef when not rdef.Fields.IsEmpty -> $"{c.ToLowerInvariant()} flags:" :: argvUsageLines rdef
                | _ -> [])

        String.concat "\n" ([ "usage: <command> [flags]"; "commands:" ] @ caseLines @ blocks)
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
                ([ $"usage: {c.ToLowerInvariant()} [flags]"; "global options:" ]
                 @ argvUsageLinesWith (scopeShortsFor c p) sharedDef
                 @ caseBlock c p)
        | None ->
            // the global section shows a derived short only when it holds
            // in EVERY case scope (explicit shorts always hold)
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
                ([ "usage: [global flags] <command> [flags]"; "global options:" ]
                 @ argvUsageLinesWith stable sharedDef
                 @ [ "commands:" ]
                 @ caseLines
                 @ blocks)

// the three argv-boundary rules — ONE implementation each, shared by
// the record and shared-flags twins [D:argv-rules]. The accumulators
// arrive as PARAMETERS, never closure captures: problem ORDER stays
// each caller's own (scan order, then declaration-order fills —
// pinned exact in e2e).

// the resting-point fill [D:default-attr]: run-time Value construction
// lives HERE (Eval); the Default POLICY it consumes (Argv.defaultOf)
// is check-time schema, already shared in Argv.fs beside the Args/Env
// flip — the check/run line is unchanged by the unification
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
                problems.Add $"unknown flag '{t}'{didYouMean t (flagged |> List.map fst)}"
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

// the shared-flags load [D:shared-flags]: shared flags float anywhere on
// the line; the first non-flag token anchors the case; payload flags
// bind only AFTER it. Both tiers collect into ONE boundary error.
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
                problems.Add $"unknown flag '{t}'{didYouMean t cands}"
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
        printfn "%s" (argvUsage target argv)
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
                        $"Args.load {udef.Name} {tok}: "
                        + String.concat "; " (extra |> List.map (fun t -> $"unexpected argument '{t}'"))
                    )
            | Some(_, (c, Some _)) ->
                let payload =
                    argvParseRecord $"Args.load {udef.Name} {tok}" (Map.find c payloads) rest

                VUnion(c, Some payload)
            | None ->
                failwith $"Args.load {udef.Name}: unknown subcommand '{tok}'{didYouMean tok (table |> List.map fst)}"

// the sigil env slot evaluated to overlay pairs — inside the stream's
// delay, so Env.fromFile boundary errors keep raise-at-force semantics
let private envPairsOf (v: Value) : (string * string) list =
    match v with
    | VSeq items ->
        items
        |> Seq.map (fun item ->
            match item with
            | VRecord(_, fields) ->
                (match recTryGet "name" fields, recTryGet "value" fields with
                 | Some(VStr n), Some(VStr value) -> n, value
                 | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
            | _ -> unreachable "the checker rejects non-EnvVar overlay entries")
        |> List.ofSeq
    | _ -> unreachable "the checker rejects non-seq overlays"

// the explicit sigil env only — ambient `within env` layers apply in
// Proc's starters themselves [D:within-scopes], so EVERY spawn
// (reifier desugars, cmd/into included) obeys the outer-first law,
// not just these Eval paths
let rec private overlayOf (env: Env) (cenvO: TypedExpr option) : (string * string) list =
    match cenvO with
    | None -> []
    | Some ce ->
        envPairsOf (eval env ce)
        |> List.map (fun (k, v) -> k, noNul $"the env value for '{k}'" v)

// a NUL cannot cross the spawn hand-off [D:encoding-law]: argv and env
// are NUL-terminated C strings, so the byte would silently TRUNCATE
// the word at the child — and silent truncation is wrong under every
// answer. The boundary holds on its own rather than trusting every
// upstream constructor: once BYTES lands there will be other routes
// to a NUL.
and private noNul (what: string) (s: string) : string =
    if s.Contains '\u0000' then
        failwith
            $"{what} contains a NUL byte — it would silently truncate at the process boundary (argv and env are NUL-terminated); NUL-bearing data is binary: pass it via a file or stdin, not an argument"
    else
        s

// spawn-argv assembly [D:argv-splat]: a splat enumerates ONCE at
// spawn (argv is finite — the splat forces by necessity), order
// preserved, each element ONE word
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

and eval (env: Env) (te: TypedExpr) : Value =
    match te.Kind with
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

        // the watched child dying IS the answer [D:scoped-procs]: fail
        // NOW with its own words, never a blind timeout
        let watchCheck (sw: System.Diagnostics.Stopwatch) =
            match watched with
            | Some h when
                (try
                    h.Proc.HasExited
                 with _ ->
                     true)
                ->
                // elapsed is the POLL's clock — Process.StartTime throws
                // on an exited child (the .NET trap), and the wait's own
                // duration is the number the reader wants anyway
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

        // the two bounds [D:retry-poll]: retry counts ATTEMPTS (with an
        // optional total-time ceiling), poll counts TIME — an unbounded
        // loop is unrepresentable, not refused
        let attempts, delayMs, timeoutMs =
            if isPoll then
                System.Int32.MaxValue, dur "interval", Some(dur "timeout")
            else
                (match recGet "attempts" fields with
                 | VInt n -> int n
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

        match timeoutMs with
        | Some t when t <= 0L -> failwith $"{head}: timeout must be positive, got {formatDuration t}"
        | _ -> ()

        let sw = System.Diagnostics.Stopwatch.StartNew()
        // the wait is CANCELLABLE from the start [D:retry-poll]: the
        // timeout ceiling cancels a pending delay instead of waiting it
        // out; an external token can join the source later
        use cts = new System.Threading.CancellationTokenSource()

        match timeoutMs with
        | Some t -> cts.CancelAfter(System.TimeSpan.FromTicks(t * System.TimeSpan.TicksPerMillisecond))
        | None -> ()

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
                // the watched state rides the exhaustion [D:scoped-procs]:
                // up-but-never-ready names itself (only poll can reach
                // this message — the reason watch is a key, not a wrapper)
                let watchedNote =
                    match watched with
                    | Some h -> $"; watched process (pid {h.Proc.Id}) still running{procTailLine h}"
                    | None -> ""

                failwith $"poll: timed out after {formatDuration sw.ElapsedMilliseconds} ({n} attempt(s)){watchedNote}"
            else
                failwith $"retry: exhausted {attempts} attempt(s) over {formatDuration sw.ElapsedMilliseconds}"

        let rec loop (n: int) =
            watchCheck sw
            // raises PROPAGATE [D:retry-poll]: retry retries on the
            // predicate, never on exceptions — command failure becomes
            // data through the reifier family
            let v = eval env body

            if predicate v then
                match until with
                | Some _ -> v
                | None -> VUnit
            elif n >= attempts || cts.IsCancellationRequested then
                exhausted n
            else
                let timedOut =
                    if delayMs > 0L then
                        // the indicator wraps ONLY the wait between
                        // attempts — the body may own the terminal
                        // [D:waiting-indicator]
                        Waiting.during $"{head}: waiting {formatDuration delayMs}" (fun () ->
                            try
                                System.Threading.Tasks.Task
                                    .Delay(
                                        System.TimeSpan.FromTicks(delayMs * System.TimeSpan.TicksPerMillisecond),
                                        cts.Token
                                    )
                                    .Wait()

                                false
                            with _ ->
                                true)
                    else
                        cts.IsCancellationRequested

                if timedOut then exhausted n else loop (n + 1)

        loop 1
    | TEStr s -> VStr s
    | TEBool b -> VBool b
    | TEUnit -> VUnit
    | TEVar name ->
        match Map.tryFind name env with
        | Some v -> v
        | None -> unreachable $"the checker rejects unbound variable '{name}'"
    | TELet(name, _, value, body) -> eval (Map.add name (eval env value) env) body
    | TELetPat(pat, value, body) ->
        let bindings = bindPattern pat (eval env value)
        eval (bindings |> List.fold (fun m (n, v) -> Map.add n v m) env) body
    | TELambdaPat(pat, body) -> VClosurePat(pat, body, env)
    | TELambda(param, _, body) -> VClosure(param, body, env)
    | TEApp(fn, arg) -> apply (eval env fn) (eval env arg)
    | TEPipe(arg, { Kind = TECmd(prog, cargs, cenvO) }) ->
        let argv = argvOf env cargs

        let stdin =
            match eval env arg with
            | VSeq items ->
                items
                |> Seq.map (fun v ->
                    match v with
                    | VStr s -> s
                    | v -> unreachable $"the checker rejects non-string stdin: {formatValue v}")
            | v -> unreachable $"the checker rejects piping {formatValue v} into a command"

        VSeq(
            Seq.delay (fun () -> Proc.linesWith (overlayOf env cenvO) (Proc.resolveProg prog) argv (Some stdin))
            |> Seq.map VStr
        )
    // the armed statement command STREAMS at a tty [D:stream-echo]:
    // |print(linesOf) held a partial line (an interactive prompt) until
    // its newline — the chunk relay flushes as bytes arrive. Content is
    // byte-identical to the batched path (same segment split, trailing
    // newline ensured); ONLY timing differs, and only at a tty —
    // redirected output keeps the linesOf path untouched. Reifiers and
    // captures are unaffected by law (| complete is in-memory capture).
    | TEPipe({ Kind = TECmd(prog, args, cenvO) }, { Kind = TEVar "|print" }) when
        not (System.Console.IsOutputRedirected)
        ->
        let argv = argvOf env args

        let spec: Proc.Spec =
            { Prog = Proc.resolveProg prog
              Args = argv
              Env = overlayOf env cenvO
              Input = None }

        let mutable atLineStart = true

        Proc.streamSegmentsOf
            spec
            (fun txt ->
                System.Console.Out.Write txt
                System.Console.Out.Flush()
                atLineStart <- false)
            (fun () ->
                System.Console.Out.Write '\n'
                System.Console.Out.Flush()
                atLineStart <- true)

        if not atLineStart then
            System.Console.Out.Write '\n'

        VUnit
    | TEPipe(arg, fn) -> apply (eval env fn) (eval env arg)
    | TEField(target, field) ->
        match eval env target with
        | VRecord(name, fields) ->
            match recTryGet field fields with
            | Some v -> v
            | None -> unreachable $"the checker rejects unknown field '{field}' on {name}"
        | v -> unreachable $"the checker rejects field access on {formatValue v}"
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
    | TEBinOp("<<", l, r) ->
        let g = eval env l
        let f = eval env r
        VBuiltin(fun x -> apply g (apply f x))
    | TEBinOp(op, l, r) -> binOp op (eval env l) (eval env r)
    | TERecord(name, fields) -> VRecord(name, fields |> List.map (fun (n, fv) -> n, eval env fv))
    | TEUpdate(src, updates) ->
        // source evaluated ONCE [D:record-update]; nested paths overlay
        let source = eval env src

        updates
        |> List.fold
            (fun acc (path, tval) ->
                let rec go (v: Value) (path: string list) : Value =
                    match v, path with
                    // recSet replaces IN PLACE [D:record-order]: an
                    // updated field keeps its position, never moves
                    | VRecord(n, fs), [ f ] -> VRecord(n, recSet f (eval env tval) fs)
                    | VRecord(n, fs), f :: rest -> VRecord(n, recSet f (go (recGet f fs) rest) fs)
                    | v, _ -> unreachable $"the checker rejects update on {formatValue v}"

                go acc path)
            source
    | TEList items -> VSeq(items |> List.map (eval env))
    | TETuple items -> VTuple(items |> List.map (eval env))
    | TECmd(prog, args, cenvO) ->
        let argv = argvOf env args

        VSeq(
            Seq.delay (fun () -> Proc.linesWith (overlayOf env cenvO) (Proc.resolveProg prog) argv None)
            |> Seq.map VStr
        )
    | TEInterp parts ->
        let sb = System.Text.StringBuilder()

        for p in parts do
            match p with
            | IStr s -> sb.Append s |> ignore
            | IExpr e ->
                // a hole renders what show renders [D:interp-show]; a
                // bare string stays RAW (the value, not its quoted form)
                sb.Append(
                    match eval env e with
                    | VStr str -> str
                    | v -> formatValue v
                )
                |> ignore

        VStr(sb.ToString())
    | TEFrom(fmt, def, defs, seqOf, mapOf) -> fromAdapter fmt seqOf mapOf def defs
    | TEFromYaml(_, shape) -> yamlFromImpl shape
    | TEYaml(tpl, _) -> evalYamlTpl env tpl
    | TETo("yaml", renames) -> yamlToImpl renames
    | TETo("jsonl", renames) ->
        VBuiltin(fun v ->
            match v with
            | VSeq items -> VSeq(items |> Seq.map (jsonLine renames >> VStr))
            | v -> unreachable $"the checker rejects 'to jsonl' on {formatValue v}")
    | TETo(_, renames) ->
        // ONE document [D:to-jsonl] — the whole value through the same
        // renderer, once; an array document forces its seq (one line
        // cannot stream)
        VBuiltin(fun v -> VSeq [ VStr(jsonLine renames v) ])
    | TEMatch(scrutinee, arms) ->
        let v0 = eval env scrutinee

        // memoize-once law [D:seq-patterns]: a match containing ANY seq
        // pattern views its scrutinee through ONE cache — arms probe the
        // same buffer (never re-pull), rest binds the buffer suffix plus
        // the untouched tail, effects run once TOTAL
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
        // snapshot at force time; collect every problem, raise ONCE
        let problems = ResizeArray<string>()

        let fields =
            def.Fields
            |> List.map (fun (field, ty) ->
                // the env var this field READS [D:wire-keys]: verbatim unless
                // [<Wire "NAME">] says otherwise. Env var names are not always
                // legal weir field names either (a leading digit, a dash, a
                // reserved word), and the author controls the environment no more
                // than a JSON payload — the same wire-key problem as from json.
                // `name` is the WIRE name from here on, so every message names the
                // variable the author actually asked for.
                let name = Types.wireName def field
                let raw = System.Environment.GetEnvironmentVariable name

                let value =
                    match ty, raw with
                    | TNamed("Option", _), null -> VUnion("None", None)
                    | _, null ->
                        // the resting point sits BELOW the whole overlay
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
                    // env is THE secret channel [D:secret]: wrap at the boundary
                    | (TSecret | TNamed("Option", [ TSecret ])), v -> wrapOpt ty (VSecret v)
                    | (TNamed(un, []) | TNamed("Option", [ TNamed(un, []) ])), v ->
                        // the enum conversion [D:env-enums]: matching is
                        // CASE-INSENSITIVE against the declared names (env
                        // convention is uppercase — LOG_LEVEL=DEBUG, =debug
                        // and =Debug all select Debug); an empty value is a
                        // miss with candidates, the int rule's precedent
                        let cases = enums |> Map.tryFind un |> Option.defaultValue []

                        match
                            cases
                            |> List.tryFind (fun c ->
                                System.String.Equals(c, v, System.StringComparison.OrdinalIgnoreCase))
                        with
                        | Some c -> wrapOpt ty (VUnion(c, None))
                        | None ->
                            // the hint compares like the matcher does —
                            // case-insensitively — but names the DECLARED
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
        // the bare scope [D:within-always], the raise rulings:
        //  1. normal exit + cleanup raises -> the scope raises (always
        //     is never the one place a raise disappears)
        //  2. already unwinding + cleanup raises -> the ORIGINAL wins;
        //     the cleanup failure goes to stderr with a marker (the
        //     diagnosis outranks its consequence)
        //  3. teardown continues outward regardless — one failed
        //     cleanup must not strand the enclosing scopes' LIFO.
        // exit n is a raise (ExitRequest) and unwinds the same way;
        // signals run the pending cleanups via the exit hook, LIFO.
        let runCleanup () = eval env cleanup |> ignore
        let hooked = Session.registerAlways runCleanup

        match
            (try
                Ok(eval env body)
             with e ->
                 Error e)
        with
        | Ok v ->
            Session.deregisterAlways hooked
            // case 1: a cleanup raise propagates as the scope's raise
            runCleanup ()
            v
        | Error original ->
            Session.deregisterAlways hooked

            (try
                runCleanup ()
             with
             | :? ExitRequest ->
                 // the checker refuses exit inside always; a raise here
                 // would swap the original for a code — unreachable
                 ()
             | ce -> eprintfn "within/always: the cleanup ALSO failed — %s (the original error wins)" ce.Message)

            raise original
    | TEWithin(kind, binder, targ, topts, body) ->
        match kind, binder, targ with
        | "lock", _, Some pathE ->
            // advisory file lock [D:within-lock]: FileShare.None maps to
            // flock(2) on Unix (probe-pinned: per-open-file-description,
            // so pmap arms exclude each other; interoperates with
            // flock(1)) and native share modes on Windows. Blocking by
            // default, timeout= bounds the wait; the kernel releases on
            // ANY death, kill -9 included — the one kind whose guarantee
            // survives the hard-exit carve-out. Advisory only: a
            // non-cooperating process ignores it (stated non-claim).
            let path =
                match eval env pathE with
                | VStr s -> s
                | v -> unreachable $"the checker rejects a lock path of {formatValue v}"

            let resolved = Session.resolve path

            let timeoutMs =
                topts
                |> Option.map (fun o ->
                    match eval env o with
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
                     // DirectoryNotFound IS an IOException — a missing
                     // parent must fail NOW, not spin as "held elsewhere"
                     // (the Windows C:\tmp lesson)
                     | :? System.IO.DirectoryNotFoundException ->
                         failwith $"within lock: no such directory for {resolved} — the lock file's parent must exist"
                     | :? System.IO.IOException -> None)
                with
                | Some fs -> fs
                | None ->
                    match timeoutMs with
                    | Some ms when sw.ElapsedMilliseconds >= ms ->
                        failwith
                            $"within lock: timed out after {formatDuration ms} waiting for {resolved} (held elsewhere)"
                    | _ ->
                        System.Threading.Thread.Sleep 25
                        acquire ()

            let fs = acquire ()

            try
                eval env body
            finally
                fs.Dispose()
        | _ ->

            match kind, binder, targ with
            | "cd", _, Some pathE ->
                // cd CONSUMES a path [D:within-scopes]: resolved against the
                // current cwd (so nested relative scopes compose), verified
                // BEFORE the block runs, restored on every managed exit
                let path =
                    match eval env pathE with
                    | VStr s -> s
                    | v -> unreachable $"the checker rejects a cd path of {formatValue v}"

                let resolved = Session.resolve path

                if not (System.IO.Directory.Exists resolved) then
                    failwith $"within cd: no such directory: {resolved}"

                let saved = Session.Cwd()
                Session.setCwd resolved

                try
                    eval env body
                finally
                    Session.setCwd saved
            | "env", _, Some varsE ->
                // env pushes an ambient overlay CHILD SPAWNS see; weir's own
                // Env.load is untouched [D:within-scopes]
                Session.pushEnvOverlay (envPairsOf (eval env varsE))

                try
                    eval env body
                finally
                    Session.popEnvOverlay ()
            | "proc", Some binderName, Some cmdNode ->
                // the scoped process [D:scoped-procs]: spawn with both
                // streams spilling, bind the handle, and at EVERY exit —
                // normal and raise alike — tree-kill and reap. The scope IS
                // the lifetime; the exit hook is the hard-exit backstop.
                let prog, argv, overlay =
                    match cmdNode.Kind with
                    | TECmd(prog, args, cenvO) -> prog, argvOf env args, overlayOf env cenvO
                    | _ -> unreachable "the parser guarantees a command in the proc slot"

                let spill =
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-proc-{System.Guid.NewGuid():N}")

                System.IO.Directory.CreateDirectory spill |> ignore
                Session.registerTmpDir spill

                let p, drain =
                    Proc.startSpilled
                        overlay
                        (Proc.resolveProg prog)
                        argv
                        (System.IO.Path.Combine(spill, "out.log"))
                        (System.IO.Path.Combine(spill, "err.log"))

                Session.registerProc p

                let handle =
                    { Proc = p
                      OutPath = System.IO.Path.Combine(spill, "out.log")
                      ErrPath = System.IO.Path.Combine(spill, "err.log")
                      SpillDir = spill
                      Drain = drain }

                try
                    eval (Map.add binderName (VProc handle) env) body
                finally
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
            | "tmp", Some binderName, _ ->
                // kind "tmp" [D:within-scopes]: a fresh unique directory,
                // bound as the binder for the block; removed on EVERY exit —
                // normal and raise alike (the raise-path is the load-bearing
                // pin). The delete is best-effort (a vanished dir is fine).
                // Matched by NAME, never a wildcard: a wildcard arm absorbs a
                // malformed node of ANY kind instead of reaching the
                // unreachable guard, which is what makes the guard worth having
                let dir =
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-tmp-{System.Guid.NewGuid():N}")

                System.IO.Directory.CreateDirectory dir |> ignore
                // the exit hook's backstop registration [D:exit-hook]: a hard
                // exit (pfirst exit-race, Ctrl-C) sweeps what this finally
                // could not; the clean path deregisters and the hook is idle
                Session.registerTmpDir dir

                try
                    eval (Map.add binderName (VStr dir) env) body
                finally
                    (try
                        System.IO.Directory.Delete(dir, true)
                     with _ ->
                         ())

                    Session.deregisterTmpDir dir
            | _ -> unreachable "within kinds are closed at parse"
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


// the yaml district's evaluator [D:yaml-district]: build Yaml NODES —
// the lift is VALUE-driven (the checker already enforced the liftable
// law), a None SPLICE omits its entry/item, `for` instantiates its body
// per element (binder = bindPattern, a lambda param's machinery), and
// runtime duplicate keys (for-generated or key-spliced) are errors —
// invalid YAML must not render silently.
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
        // the sortBy posture: a polymorphic splice's law is enforced HERE
        failwith
            $"yaml splice: got {formatValue v}; splices take string/int/float/bool, a Yaml node, Option of one, or a seq of those"

and private evalYamlTpl (env: Env) (tpl: Check.TypedYamlTpl) : Value =
    match tpl with
    // block scalar content never self-types: it is a STRING, always
    // [D:block-scalars]
    | Check.TYtBlock(text, _) -> VUnion("YStr", Some(VStr text))
    | Check.TYtScalar(raw, quoted, _) ->
        if not quoted && raw = "" then
            VUnion("YNull", None)
        elif not quoted && (raw = "true" || raw = "false") then
            VUnion("YBool", Some(VBool(raw = "true")))
        else
            match (if quoted then (false, 0L) else System.Int64.TryParse raw) with
            | true, n -> VUnion("YInt", Some(VInt n))
            | _ ->
                // unquoted float-shaped literals self-type [D:floats-boundaries]
                // — `cpu: 1.5` must not render as "1.5" (the int precedent;
                // parseFloat refuses non-finite so nan/inf text stays string)
                match (if quoted then Error "" else parseFloat raw) with
                | Ok f -> VUnion("YFloat", Some(VFloat f))
                | Error _ -> VUnion("YStr", Some(VStr raw))
    | Check.TYtSplice te -> liftYaml (eval env te) |> Option.defaultValue (VUnion("YNull", None))
    | Check.TYtSeq(items, _) -> VUnion("YSeq", Some(VSeq(evalYamlItems env items |> List.toSeq)))
    | Check.TYtMap(entries, _) ->
        let pairs = evalYamlEntries env entries
        let seen = System.Collections.Generic.HashSet<string>()

        for (k, _) in pairs do
            if not (seen.Add k) then
                failwith $"yaml: duplicate key '{k}' (generated at runtime)"

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

            // a splice VALUE evaluating to None omits the whole entry
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

and apply (fn: Value) (arg: Value) : Value =
    match fn with
    | VClosure(param, body, closureEnv) -> eval (Map.add param arg closureEnv) body
    | VClosurePat(pat, body, closureEnv) ->
        let bindings = bindPattern pat arg
        eval (bindings |> List.fold (fun m (n, v) -> Map.add n v m) closureEnv) body
    | VBuiltin f -> f arg
    | v -> unreachable $"the checker rejects application of {formatValue v}"

/// the bare-statement spec (argv, overlay) — ONE builder for the relay
/// and the inheriting spawn, so the two forms cannot drift
let private commandStatementSpec (env: Env) (te: Check.TypedExpr) : Proc.Spec =
    match te.Kind with
    | Check.TECmd(prog, args, cenvO) ->
        { Prog = Proc.resolveProg prog
          Args = argvOf env args
          Env = overlayOf env cenvO
          Input = None }
    | _ -> unreachable "a command-statement helper takes the bare-command statement only"

/// the REPL's streaming statement echo [D:stream-echo] — spawns the
/// bare command via the chunk relay instead of eval'ing to a VSeq (a
/// seq<string> cannot carry a partial line). The caller owns rendering;
/// this owns the spawn (argv, overlay, nonzero raise). The relay's
/// remaining statement customer is the REDIRECTED-|print form; the
/// bare statement at a tty inherits instead [D:colour-inherit].
let streamCommandStatement (env: Env) (te: Check.TypedExpr) (onText: string -> unit) (onBreak: unit -> unit) : unit =
    Proc.streamSegmentsOf (commandStatementSpec env te) onText onBreak

/// colour from the child [D:colour-inherit]: the bare statement at a
/// tty spawns with stdout INHERITED — isatty is true for the child
/// the inherit GATE [D:colour-inherit] — one predicate, so the runner and the
/// REPL cannot answer it differently. TECmd is built with Ty = seq<string> at
/// its single site, so the type test is redundant TODAY and stated anyway: it
/// is the property the spawn depends on, not an incidental fact about TECmd.
let inheritsStdout (te: Check.TypedExpr) : bool =
    match te.Kind with
    | Check.TECmd _ -> not System.Console.IsOutputRedirected && te.Ty = TSeq TStr
    | _ -> false

let inheritCommandStatement (env: Env) (te: Check.TypedExpr) : unit =
    Proc.runInherited (commandStatementSpec env te)

let constructorValues (cases: (string * Ty option) list) : (string * Value) list =
    cases
    |> List.map (fun (case, payload) ->
        match payload with
        | None -> case, VUnion(case, None)
        | Some _ -> case, VBuiltin(fun v -> VUnion(case, Some v)))
