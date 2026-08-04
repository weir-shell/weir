module Weir.Argv

// CLI schema POLICY, extracted from the checker: kebab derivation,
// short-flag tables, minted --no-X twins, Default/shape/collision
// validation. It runs at CHECK time by necessity (no reflection — the
// Args.load/Env.load arms read declarations), but it is policy, not
// typing; the checker keeps the arms (resolution, typed-node
// construction, error-span plumbing) and calls in here.

open Weir.Types

// short-flag resolution: a letter is owned or contested; contested
// letters derive for NOBODY and error with candidates at invocation
type ShortOwner =
    | ShortOf of longFlag: string
    | AmbiguousShort of longFlags: string list

// field name -> kebab flag: split at lower->upper boundaries and
// before the last upper of an acronym run [D:typed-argv]
let kebabFlag (name: string) : string =
    let sb = System.Text.StringBuilder()

    for i in 0 .. name.Length - 1 do
        let c = name[i]

        let boundary =
            i > 0
            && System.Char.IsUpper c
            && (System.Char.IsLower name[i - 1]
                || System.Char.IsDigit name[i - 1]
                || (i + 1 < name.Length
                    && System.Char.IsUpper name[i - 1]
                    && System.Char.IsLower name[i + 1]))

        if boundary then
            sb.Append '-' |> ignore

        sb.Append(System.Char.ToLowerInvariant c) |> ignore

    sb.ToString()

let private attrOf (def: RecordDef) (field: string) (attr: string) =
    def.Attrs
    |> Map.tryFind field
    |> Option.bind (List.tryFind (fun (n, _) -> n = attr))

// the derived --help text for a field: the `///` doc's FIRST line
// [D:doc-help]. `[<Doc>]` retired — one source, hover and --help agree
// by construction. The runner populated `def.Docs` from the source.
let docOf (def: RecordDef) (field: string) : string option = Map.tryFind field def.Docs

// [D:default-attr]: the resting-point literal, when declared
let defaultOf (def: RecordDef) (field: string) : AttrArg option =
    match attrOf def field "Default" with
    | Some(_, Some a) -> Some a
    | _ -> None

// Default-true bools mint their `--no-X` twin — the minted names
// join collision checks and did-you-mean, never short derivation
let mintedFlags (def: RecordDef) : (string * string) list =
    def.Fields
    |> List.choose (fun (f, ty) ->
        match ty, defaultOf def f with
        | TBool, Some(ABool true) -> Some(f, "no-" + kebabFlag f)
        | _ -> None)

// (flag -> short) and (letter -> owner). Explicit [<Short>] beats
// derivation: the derived short retires, --help is the truth.
// 'h' never derives (reserved; [<Short "h">] rejects at attachment)
let shortTables (def: RecordDef) : Map<string, string> * Map<string, ShortOwner> =
    let flagged = def.Fields |> List.map (fun (f, _) -> f, "--" + kebabFlag f)

    let explicits =
        flagged
        |> List.choose (fun (f, flag) ->
            match attrOf def f "Short" with
            | Some(_, Some(AStr sh)) -> Some(flag, sh)
            | _ -> None)

    let taken = explicits |> List.map snd |> Set.ofList

    let derivers =
        flagged
        |> List.filter (fun (f, _) -> (attrOf def f "Short").IsNone && (attrOf def f "NoShort").IsNone)
        |> List.map (fun (_, flag) -> flag, string flag[2])
        |> List.filter (fun (_, letter) -> letter <> "h" && not (Set.contains letter taken))

    let derivedGroups = derivers |> List.groupBy snd

    let singles =
        derivedGroups
        |> List.choose (fun (letter, group) ->
            match group with
            | [ (flag, _) ] -> Some(flag, letter)
            | _ -> None)

    let flagShorts = Map.ofList (explicits @ singles)

    let shortIndex =
        (explicits |> List.map (fun (flag, letter) -> letter, ShortOf flag))
        @ (derivedGroups
           |> List.map (fun (letter, group) ->
               match group with
               | [ (flag, _) ] -> letter, ShortOf flag
               | many -> letter, AmbiguousShort(many |> List.map fst)))
        |> Map.ofList

    flagShorts, shortIndex

// the outer record minus its subcommand slot: the shared-flags
// record [D:shared-flags]
let sharedOf (outer: RecordDef) (unionField: string) : RecordDef =
    { outer with
        Fields = outer.Fields |> List.filter (fun (f, _) -> f <> unionField)
        Attrs = outer.Attrs |> Map.remove unionField }

let explicitShorts (def: RecordDef) : (string * string) list =
    def.Fields
    |> List.choose (fun (f, _) ->
        match attrOf def f "Short" with
        | Some(_, Some(AStr sh)) -> Some(f, sh)
        | _ -> None)

// ---- the Default resting-point validators [D:default-attr] --------
// TWO rules ON PURPOSE, adjacent so the divergence reads as decided
// rather than copied wrong: Env.load ACCEPTS [<Default false>] (an
// env-backed bool genuinely rests at false), Args.load REJECTS it as
// redundant (flag PRESENCE already rests at false) — the flip cell.

let badEnvDefault (def: RecordDef) : string option =
    def.Fields
    |> List.tryPick (fun (f, ft) ->
        match defaultOf def f with
        | None -> None
        | Some a ->
            match ft, a with
            | TStr, AStr _
            | TInt, AInt _
            | TFloat, AFloat _
            | TBool, ABool _
            | TDur, ADur _ -> None
            | TNamed("Option", _), _ ->
                Some(
                    "'"
                    + f
                    + "': optional with a default IS a default — drop the Option or the attribute"
                )
            | ft, _ -> Some $"'{f}': the Default literal does not match the field, which is {formatTy ft}")

// the resting-point cells [D:default-attr]: literal matches the
// field, Option is contradictory, bool-false redundant
let private badArgsDefault (label: string) (def: RecordDef) : string option =
    def.Fields
    |> List.tryPick (fun (f, ft) ->
        match defaultOf def f with
        | None -> None
        | Some a ->
            match ft, a with
            | TStr, AStr _
            | TInt, AInt _
            | TFloat, AFloat _
            | TDur, ADur _ -> None
            | TBool, ABool true -> None
            | TBool, ABool false ->
                Some $"{label}'{f}': [<Default false>] is redundant — presence already rests at false"
            | TNamed("Option", _), _ ->
                Some(
                    $"{label}'"
                    + f
                    + "': optional with a default IS a default — drop the Option or the attribute"
                )
            | ft, _ -> Some $"{label}'{f}': the Default literal does not match the field, which is {formatTy ft}")

let private badArgsShape (label: string) (def: RecordDef) : string option =
    let badShape =
        def.Fields
        |> List.tryFind (fun (_, ft) ->
            match ft with
            | TStr
            | TInt
            | TFloat
            | TBool
            | TDur
            | TNamed("Option", [ TStr | TInt | TFloat | TDur ]) -> false
            | _ -> true)

    match badShape with
    | Some(f, TNamed("Option", [ TBool ])) ->
        Some $"{label}'{f}' is Option<bool>: a presence flag is already optional; use bool"
    | Some(f, ft) ->
        Some
            $"{label}Args.load fields must be string, int, float, bool, or Duration, or Option of string|int|float|Duration; '{f}' is {formatTy ft}"
    | None -> None

let private dupFlag (label: string) (def: RecordDef) : string option =
    let flags =
        // minted --no-X twins join the namespace [D:default-attr]
        (def.Fields |> List.map (fun (f, _) -> f, kebabFlag f)) @ mintedFlags def

    match flags |> List.groupBy snd |> List.tryFind (fun (_, g) -> g.Length > 1) with
    | Some(flag, (a, _) :: (b, _) :: _) -> Some $"{label}fields '{a}' and '{b}' derive the same flag '--{flag}'"
    | _ -> None

/// Args.load field validation, chained in the arms' original order:
/// Default cells, then field shapes, then flag collisions
let fieldProblems (label: string) (def: RecordDef) : string option =
    match badArgsDefault label def with
    | Some m -> Some m
    | None ->
        match badArgsShape label def with
        | Some m -> Some m
        | None -> dupFlag label def
