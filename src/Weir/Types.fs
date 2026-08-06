module Weir.Types

// reifier desugar targets carry an un-typeable '|' prefix
// [D:drop-reify-builtins] — identifiers are [A-Za-z_].. so `| complete`
// resolves them while user code cannot name them. Suggestion/completion
// pools filter to user-typeable names.
let isUserName (n: string) =
    n.Length > 0 && (System.Char.IsLetter n[0] || n[0] = '_')

type Ty =
    | TInt
    // FINITE-only floats [D:floats]: NaN/Infinity are unrepresentable —
    // checked arithmetic raises, every boundary parse rejects them
    | TFloat
    | TStr
    | TBool
    | TUnit
    // bytes as a type [D:size]: an INTEGER of bytes (int64 — ~8 EiB
    // ceiling); decimals exist only in parse and render, the Duration
    // pattern copied
    | TSize
    // time as a type [D:duration]: an INTEGER of milliseconds; decimals
    // exist only in parsing and rendering — the no-floats law's answer
    // to the time want
    | TDur
    // a marker the renderers respect [D:secret]: a plain string the
    // rendering machinery refuses to print — show is ***, interpolation
    // and the wire boundaries refuse; Secret.reveal is the one exit. NOT
    // storage, NOT memory protection (the in-memory value is a plain
    // string) — flow control at the boundaries weir owns
    | TSecret
    | TFun of domain: Ty * codomain: Ty
    | TSeq of element: Ty
    | TTuple of elements: Ty list // arity 2+ [D:tuples-reversal]
    | TNamed of name: string * args: Ty list
    | TVar of name: string
    | TRowVar of name: string * fields: (string * Ty) list

let rec formatTy (ty: Ty) : string =
    match ty with
    | TDur -> "Duration"
    | TSize -> "Size"
    | TSecret -> "Secret"
    | TVar v -> $"'{v}"
    | TRowVar(_, []) -> "{ .. }"
    | TRowVar(_, fields) ->
        let fs =
            fields |> List.map (fun (f, t) -> $"{f}: {formatTy t}") |> String.concat "; "

        $"{{ {fs}; .. }}"
    | TInt -> "int"
    | TFloat -> "float"
    | TStr -> "string"
    | TBool -> "bool"
    | TUnit -> "unit"
    | TFun(domain, codomain) ->
        let dom =
            match domain with
            | TFun _ -> $"({formatTy domain})"
            | _ -> formatTy domain

        $"{dom} -> {formatTy codomain}"
    | TSeq element -> $"seq<{formatTy element}>"
    | TTuple elements ->
        let part (t: Ty) =
            match t with
            | TFun _
            | TTuple _ -> $"({formatTy t})"
            | _ -> formatTy t

        elements |> List.map part |> String.concat " * "
    | TNamed(name, []) -> name
    | TNamed(name, args) ->
        let argStr = args |> List.map formatTy |> String.concat ", "
        $"{name}<{argStr}>"

/// the annotated DECLARATION form for hover [D:annotated-signature]:
/// `name (p1: t1) (p2: t2) : result`, decomposing `ty` by the given
/// parameter names (the arrow tail beyond the named params is the
/// result). Valid F# declaration syntax — claims nothing false. Zero
/// names -> `name : ty`, no empty parens. The plain arrow `formatTy`
/// stays the fallback (unnamed values) and the truth for type errors.
let formatSignature (name: string) (paramNames: string list) (ty: Ty) : string =
    let rec split names t =
        match names, t with
        | n :: rest, TFun(dom, cod) ->
            let ps, result = split rest cod
            (n, dom) :: ps, result
        | _ -> [], t

    match split paramNames ty with
    | [], _ -> $"{name} : {formatTy ty}"
    | ps, result ->
        let rendered =
            ps |> List.map (fun (n, t) -> $"({n}: {formatTy t})") |> String.concat " "

        $"{name} {rendered} : {formatTy result}"

let rec tyVars (ty: Ty) : Set<string> =
    match ty with
    | TVar v -> Set.singleton v
    | TRowVar(r, fields) -> fields |> List.fold (fun acc (_, t) -> acc + tyVars t) (Set.singleton r)
    | TFun(a, b) -> tyVars a + tyVars b
    | TSeq t -> tyVars t
    | TTuple ts -> ts |> List.fold (fun acc t -> acc + tyVars t) Set.empty
    | TNamed(_, args) -> args |> List.fold (fun acc t -> acc + tyVars t) Set.empty
    | TInt
    | TFloat
    | TStr
    | TBool
    | TUnit
    | TDur
    | TSize
    | TSecret -> Set.empty

// The closed class family [D:inferred-type-classes] — fully erased
// after checking: a constraint never reaches the value domain.
[<RequireQualifiedAccess>]
type Cls =
    | Eq
    | Show
    | Ord

// Cs: constraints on quantified vars — `Eq a => a -> a -> bool` is
// { Forall = {a}; Cs = [a, {Eq}]; Ty = a -> a -> bool }.
type Scheme =
    { Forall: Set<string>
      Cs: Map<string, Set<Cls>>
      Ty: Ty
      // row-field PROVENANCE [D:row-provenance]: quantified row var ->
      // (field, physLine, physCol, len) of the access that demanded it —
      // translated to PHYSICAL at generalization (spans die at statement
      // boundaries), rehydrated at instantiation, reported by the
      // row-vs-record discharge
      RowOrigins: Map<string, (string * int * int * int) list> }

let generalize (ty: Ty) : Scheme =
    { Forall = tyVars ty
      Cs = Map.empty
      Ty = ty
      RowOrigins = Map.empty }

// generalization with the checker's constraint residue: only
// constraints on vars actually quantified ride into the scheme
let generalizeWith (cs: Map<string, Set<Cls>>) (ty: Ty) : Scheme =
    let fa = tyVars ty

    { Forall = fa
      Cs = cs |> Map.filter (fun v _ -> fa.Contains v)
      Ty = ty
      RowOrigins = Map.empty }

// generalizeWith + row origins [D:row-provenance], filtered like Cs:
// only origins for quantified row vars ride into the scheme
let generalizeWithOrigins
    (cs: Map<string, Set<Cls>>)
    (origins: Map<string, (string * int * int * int) list>)
    (ty: Ty)
    : Scheme =
    let fa = tyVars ty

    { Forall = fa
      Cs = cs |> Map.filter (fun v _ -> fa.Contains v)
      Ty = ty
      RowOrigins = origins |> Map.filter (fun v _ -> fa.Contains v) }

let mono (ty: Ty) : Scheme =
    { Forall = Set.empty
      Cs = Map.empty
      Ty = ty
      RowOrigins = Map.empty }

// attribute arguments [D:attributes]: literal-only, the splice family
type AttrArg =
    | AStr of string
    | AInt of int64
    | ABool of bool
    // a duration literal (30s, 250ms) — stored as ms [D:duration]
    | ADur of int64
    | AFloat of float
    // a size literal (10MiB) — stored as bytes [D:size]
    | ASize of int64

type RecordDef =
    { Name: string
      Params: string list
      Fields: (string * Ty) list
      // check-time data, FULLY ERASED [D:attributes] — never reaches
      // eval, Value, show, json, or equatability
      Attrs: Map<string, (string * AttrArg option) list>
      // field -> the `///` doc's FIRST line, the derived --help text
      // [D:doc-help]. `--help` reads this instead of the retired
      // [<Doc>] attribute; hover still reads the full doc out-of-band.
      // Same check-time-erased nature as Attrs.
      Docs: Map<string, string> }

type UnionDef =
    { Name: string
      Params: string list
      Cases: (string * Ty option) list }

type TypeDef =
    | Record of RecordDef
    | Union of UnionDef

type TypeEnv =
    { Values: Map<string, Scheme>
      Modules: Map<string, Map<string, Scheme>>
      Types: Map<string, TypeDef>
      // imported modules [D:modules-v1]: alias -> the type names that
      // module exported. Types themselves live flat in `Types` (plain
      // name, so signatures/field-access/bare literals resolve); this
      // records provenance so the qualified literal `Git.Ctx { .. }`
      // can confirm the module owns that type. Empty for single-file.
      ModuleTypes: Map<string, Set<string>> }

let editDistance (a: string) (b: string) : int =
    let d = Array2D.create (a.Length + 1) (b.Length + 1) 0

    for i in 0 .. a.Length do
        d[i, 0] <- i

    for j in 0 .. b.Length do
        d[0, j] <- j

    for i in 1 .. a.Length do
        for j in 1 .. b.Length do
            let cost = if a[i - 1] = b[j - 1] then 0 else 1
            d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)

    d[a.Length, b.Length]

let didYouMean (name: string) (candidates: seq<string>) : string =
    candidates
    |> Seq.map (fun c -> c, editDistance name c)
    |> Seq.filter (fun (_, d) -> d <= 2)
    |> Seq.sortBy snd
    |> Seq.tryHead
    |> Option.map (fun (c, _) -> $". Did you mean '{c}'?")
    |> Option.defaultValue ""

module Color =
    open System

    let private enabled (redirected: bool) =
        not redirected
        && isNull (Environment.GetEnvironmentVariable "NO_COLOR")
        && Environment.GetEnvironmentVariable "TERM" <> "dumb"

    let onStdout = lazy enabled Console.IsOutputRedirected
    let onStderr = lazy enabled Console.IsErrorRedirected

    let private wrap (on: bool) (code: string) (s: string) =
        if on then $"\x1b[{code}m{s}\x1b[0m" else s

    let red on s = wrap on "31" s
    let yellow on s = wrap on "33" s
    let bold on s = wrap on "1" s

// ---- Duration text [D:duration] — the boundary where decimals live.
// Storage is integer ms; these two are the ONLY places decimal text
// exists, and no float appears in either direction.

/// the Go shape: largest-unit-first compound, zero components dropped,
/// sub-second seconds as a decimal (1.5s), pure ms as Nms. Round-trips
/// through parseDurationMs (pinned).
// floats render shortest-round-trippable [D:floats], and an integral
// float keeps a visible decimal so a float never renders identically
// to an int
let formatFloat (f: float) : string =
    let s = f.ToString(System.Globalization.CultureInfo.InvariantCulture)

    if s.Contains '.' || s.Contains 'E' || s.Contains 'e' then
        s.Replace("E", "e")
    else
        s + ".0"

let parseFloat (text: string) : Result<float, string> =
    match
        System.Double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture
        )
    with
    | true, f when System.Double.IsFinite f -> Ok(if f = 0.0 then 0.0 else f)
    | true, _ -> Error $"not a finite float: '{text}'"
    | _ -> Error $"not a float: '{text}'"

// sizes render BINARY units [D:size] — KiB/MiB/GiB/TiB, one decimal
// above bytes (TRUNCATED tenths: a REPORT, not an encoding — base-1024
// decimals do not terminate, so unlike Duration's show this is lossy
// by design; toBytes is the exact exit), plain bytes with no decimal.
// Integer math throughout (no float, the grep-clean bar).
let formatSize (totalBytes: int64) : string =
    let sign = if totalBytes < 0L then "-" else ""
    let b = abs totalBytes

    let unitOf =
        [ 1024L * 1024L * 1024L * 1024L, "TiB"
          1024L * 1024L * 1024L, "GiB"
          1024L * 1024L, "MiB"
          1024L, "KiB" ]
        |> List.tryFind (fun (u, _) -> b >= u)

    match unitOf with
    | None -> $"{sign}{b} B"
    | Some(u, name) ->
        let tenths = b * 10L / u
        let whole = tenths / 10L
        let dec = tenths % 10L

        if dec = 0L then
            $"{sign}{whole} {name}"
        else
            $"{sign}{whole}.{dec} {name}"

// parse reads FOREIGN text [D:size]: binary units at 1024-powers, the
// SI spellings as powers of TEN (the writer chose the unit — unlike a
// literal, where weir would be guessing), optional space, decimals
// down to whole bytes (sub-byte precision rejects, the sub-ms rule)
let parseSize (text: string) : Result<int64, string> =
    let t = text.Trim()
    let neg = t.StartsWith "-"
    let t = if neg then (t.Substring 1).TrimStart() else t

    let units =
        [ "TiB", 1024L * 1024L * 1024L * 1024L
          "GiB", 1024L * 1024L * 1024L
          "MiB", 1024L * 1024L
          "KiB", 1024L
          "TB", 1000_000_000_000L
          "GB", 1000_000_000L
          "MB", 1000_000L
          "KB", 1000L
          "B", 1L ]

    let m =
        System.Text.RegularExpressions.Regex.Match(t, "^([0-9]+)(?:\.([0-9]+))? ?([A-Za-z]+)$")

    if not m.Success then
        Error $"not a size: '{text}' — expected digits then a unit (512B, 1.5MiB)"
    else
        match units |> List.tryFind (fun (u, _) -> u = m.Groups[3].Value) with
        | None ->
            Error
                $"not a size: '{text}' — unknown unit '{m.Groups[3].Value}' (binary KiB/MiB/GiB/TiB, SI KB/MB/GB/TB, or B)"
        | Some(_, unit) ->
            let whole = int64 m.Groups[1].Value

            let bytes =
                if m.Groups[2].Success then
                    let frac = m.Groups[2].Value
                    let fracVal = int64 frac
                    let pow10 = pown 10L frac.Length

                    if (fracVal * unit) % pow10 <> 0L then
                        Error $"not a size: '{text}' — sub-byte precision (bytes are the unit)"
                    else
                        Ok(whole * unit + fracVal * unit / pow10)
                else
                    Ok(whole * unit)

            match bytes with
            | Error e -> Error e
            | Ok b -> Ok(if neg then -b else b)

let formatDuration (totalMs: int64) : string =
    if totalMs = 0L then
        "0s"
    else
        let sign = if totalMs < 0L then "-" else ""
        let ms = abs totalMs
        let h = ms / 3600000L
        let m = (ms % 3600000L) / 60000L
        let s = (ms % 60000L) / 1000L
        let frac = ms % 1000L

        let sec =
            if s = 0L && frac = 0L then
                ""
            elif frac = 0L then
                $"{s}s"
            else
                let f = $"%03d{frac}".TrimEnd '0'
                $"{s}.{f}s"

        if h = 0L && m = 0L && s = 0L then
            $"{sign}{frac}ms"
        else
            let hPart = if h > 0L then $"{h}h" else ""
            let mPart = if m > 0L then $"{m}m" else ""
            $"{sign}{hPart}{mPart}{sec}"

/// parse "30s" / "2.5s" / "1h30m" / "-90s" to ms — compound components
/// largest-first not required; decimals convert by INTEGER math and
/// sub-millisecond precision is rejected rather than rounded.
let parseDurationMs (text: string) : Result<int64, string> =
    let t = text.Trim()
    let neg, body = (if t.StartsWith "-" then true, t.Substring 1 else false, t)

    let unitMs u =
        match u with
        | "ms" -> Some 1L
        | "s" -> Some 1000L
        | "m" -> Some 60000L
        | "h" -> Some 3600000L
        | _ -> None

    let rec go (i: int) (acc: int64) =
        if i >= body.Length then
            if i = 0 then Error $"not a duration: '{text}'" else Ok acc
        else
            let j0 = i
            let mutable j = i

            while j < body.Length && System.Char.IsDigit body[j] do
                j <- j + 1

            if j = j0 then
                Error $"not a duration: '{text}' — expected digits at position {i + 1}"
            else
                let whole = System.Int64.Parse(body.Substring(j0, j - j0))

                let fracDigits, j =
                    if j < body.Length && body[j] = '.' then
                        let f0 = j + 1
                        let mutable k = f0

                        while k < body.Length && System.Char.IsDigit body[k] do
                            k <- k + 1

                        (if k = f0 then None else Some(body.Substring(f0, k - f0))), k
                    else
                        Some "", j

                match fracDigits with
                | None -> Error $"not a duration: '{text}' — a decimal point needs digits"
                | Some frac ->
                    let u0 = j
                    let mutable k = j

                    while k < body.Length && System.Char.IsLetter body[k] do
                        k <- k + 1

                    match unitMs (body.Substring(u0, k - u0)) with
                    | None -> Error $"not a duration: '{text}' — units are ms, s, m, h"
                    | Some unit ->
                        let pow10 = pown 10L frac.Length
                        let fracVal = if frac = "" then 0L else System.Int64.Parse frac

                        if (fracVal * unit) % pow10 <> 0L then
                            Error $"not a duration: '{text}' — sub-millisecond precision (ms is the base unit)"
                        else
                            go k (acc + whole * unit + (fracVal * unit) / pow10)

    go 0 0L |> Result.map (fun v -> if neg then -v else v)
