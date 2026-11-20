/// Command signatures: the model, loading them, flag diagnostics and signature-driven completion. One of the files Script.fs was [D:script-by-concern].
module Weir.Sigs

open System
open Weir.Ast
open Weir.Types
open Weir.Assemble
open Weir.ReplColor
open Weir.Statements
open Weir.ModuleLoader
open Weir.Diagnostics

// ---- external contracts: command signatures [D:command-signatures] --------
// a flag's value shape, read off its field type [D:sig-arity]: bool takes
// no value word; Option<_> takes an attached value only (`--color=never`,
// `-i.bak`); seq<_> takes its value and ends the tool's own options
// (python's `-m mod`); any other type takes one value, attached or the
// next word. Every older signature is all-bool — no flag consumes a word,
// the check it always had.
type SigArity =
    | ANone
    | AOpt
    | AOne
    | ARest

// one case's spellings. Longs: `--x` names; Shorts: one-letter `-x`
// (explicit [<Short>] or a two-character dash Wire — weir's own
// derivation is a convention, and a signature records a foreign tool's
// facts); Literals: dash-spelled words matched whole (`-plaintext`,
// find's `-name`) [D:sig-conventions]
type SigSurface = {
    Longs: Map<string, SigArity>
    Shorts: Map<string, SigArity>
    Literals: Map<string, SigArity>
    // what a value-taking flag's value completes to, keyed by spelling
    // as typed (`--file`, `-F`, `-name`); absent = nothing
    // [D:sig-arg-complete]
    Values: Map<string, SigValue>
}

and SigValue =
    | VPath
    | VChoices of string list

let emptySigSurface = {
    Longs = Map.empty
    Shorts = Map.empty
    Literals = Map.empty
    Values = Map.empty
}

// a loaded signature's checkable surface. Subs: kebab-cased subcommand
// -> (long-flag set, short set), the globals folded in (what completion
// offers); the "" key is the flag-only shape (Cmd declared as a record).
// Surfaces carries the same cases with arity, Globals the `type Globals`
// record checked at every level, and the module lets the tool's
// convention [D:sig-conventions].
type SigInfo = {
    Tool: string
    DeclLine: int
    Exhaustive: bool
    Subs: Map<string, Set<string> * Set<string>>
    Surfaces: Map<string, SigSurface>
    Globals: SigSurface option
    // "gnu" (default), "go", "find" [D:sig-conventions]
    Convention: string
    // GNU getopt's unique-prefix longs (`--verb` for `--verbose`)
    Abbreviations: bool
    // `--no-x` for any switch `--x` (node)
    Negatable: bool
    // a single-dash word no shorts spell is the tool's own value
    // (kill's `-<signal>`: `-TERM`) [D:sig-help-shapes]
    DashWords: bool
    // a multi-letter single-dash word is a bundle of shorts, read letter
    // by letter — what a generated signature states [D:sig-help-shapes]
    Bundles: bool
    // case keys whose first operand ends the tool's own options
    // (`docker run IMAGE …`, an interpreter's script) [D:sig-operand-stop]
    StopAtOperand: Set<string>
    // case keys whose flags no source could list (git log's revision
    // options): reaching one ends the check [D:sig-unwalked]
    Unchecked: Set<string>
    // the resolved sig file, its recorded version, and each surface's
    // record type name — what hover/definition need to reach the
    // declaration site [D:lsp-cross-file]
    SigPath: string
    Version: string
    SubRecords: Map<string, string>
    // the REPL's dynamic values: "" none, "git", "cobra"
    // [D:sig-dynamic-complete]
    Dynamic: string
}

// a value word that names a file: the field's name or a spelling has a
// path word, or its doc's first line does [D:sig-arg-complete] —
// generated sigs record `string`, and help rows say `<file>`/"read from
// file" far more reliably than they say anything typed
let private pathWords =
    set [ "file"; "files"; "path"; "paths"; "dir"; "dirs"; "directory"; "folder" ]

let private pathish (names: string list) (doc: string option) : bool =
    let words (s: string) =
        Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), "[^a-z]+")
        |> Array.filter (fun w -> w <> "")

    names |> List.exists (fun n -> words n |> Array.exists pathWords.Contains)
    || (match doc with
        | Some d -> words (d.Split('\n')[0]) |> Array.exists pathWords.Contains
        | None -> false)

let private sigSurface (choicesOf: string -> string list option) (def: RecordDef) : SigSurface =
    let positional f =
        match Map.tryFind f def.Attrs with
        | Some specs -> specs |> List.exists (fun (n, _) -> n = "Positional")
        | None -> false

    // a [<Wire "type">] field carries the flag spelling the field name
    // cannot (keywords) [D:sig-version-probe] — the language-wide Wire
    // convention, honored here
    // All Wire specs count [D:sig-version-probe]: a field may carry one
    // per accepted spelling (claude lists --allowedTools and
    // --allowed-tools; both camelize to one field)
    let wiresOf f =
        match Map.tryFind f def.Attrs with
        | Some specs ->
            specs
            |> List.choose (function
                | "Wire", Some(AStr w) -> Some w
                | _ -> None)
        | None -> []

    let arityOf ty =
        match ty with
        | TBool -> ANone
        | TNamed("Option", _) -> AOpt
        | TSeq _ -> ARest
        | _ -> AOne

    let explicit = Weir.Argv.explicitShorts def |> Map.ofList
    let shortOf f = Map.tryFind f explicit

    def.Fields
    |> List.filter (fun (f, _) -> not (positional f))
    |> List.fold
        (fun (acc: SigSurface) (f, ty) ->
            let a = arityOf ty
            let wires = wiresOf f
            // a dash-spelled Wire is the field's one spelling
            // [D:sig-conventions]: a short-only row (`-B`), a second short,
            // a Go or find word, `-h` (no [<Short "h">] exists) — the field
            // name is then a label, never a `--long`
            let dashed = wires |> List.exists (fun w -> w.StartsWith "-")

            let acc =
                if dashed then
                    acc
                else
                    {
                        acc with
                            Longs = Map.add (Weir.Argv.kebabFlag f) a acc.Longs
                    }

            let acc =
                wires
                |> List.fold
                    (fun (acc: SigSurface) (w: string) ->
                        if w.StartsWith "--" then
                            {
                                acc with
                                    Longs = Map.add (w.Substring 2) a acc.Longs
                            }
                        elif w.StartsWith "-" && w.Length = 2 then
                            {
                                acc with
                                    Shorts = Map.add (w.Substring 1) a acc.Shorts
                            }
                        elif w.StartsWith "-" then
                            {
                                acc with
                                    Literals = Map.add w a acc.Literals
                            }
                        else
                            {
                                acc with
                                    Longs = Map.add w a acc.Longs
                            })
                    acc

            let acc =
                match shortOf f with
                | Some sh -> {
                    acc with
                        Shorts = Map.add sh a acc.Shorts
                  }
                | None -> acc

            // the value's completion, under every spelling this field took
            let value =
                let rec named t =
                    match t with
                    | TNamed("Option", [ inner ])
                    | TSeq inner -> named inner
                    | TNamed(n, []) -> choicesOf n
                    | _ -> None

                // the field's TYPE name counts too, split at its word
                // parts (`ExistingDir`, `ConfigFile`) — a validated type
                // names what it holds [D:validated-types]
                let rec typeName t =
                    match t with
                    | TNamed("Option", [ inner ])
                    | TSeq inner -> typeName inner
                    | TNamed(n, []) -> [ Weir.Argv.kebabFlag n ]
                    | _ -> []

                if a = ANone then
                    None
                else
                    match named ty with
                    | Some cs -> Some(VChoices cs)
                    | None when pathish (Weir.Argv.kebabFlag f :: wires @ typeName ty) (Weir.Argv.docOf def f) ->
                        Some VPath
                    | None -> None

            match value with
            | None -> acc
            | Some v ->
                let spelled =
                    (if dashed then [] else [ "--" + Weir.Argv.kebabFlag f ])
                    @ (wires |> List.map (fun w -> if w.StartsWith "-" then w else "--" + w))
                    @ (shortOf f |> Option.map (fun sh -> "-" + sh) |> Option.toList)

                {
                    acc with
                        Values = spelled |> List.fold (fun m k -> Map.add k v m) acc.Values
                })
        emptySigSurface

// later spellings never displace earlier ones: a case's own flag keeps
// its arity over a global of the same name
let private mergeSurface (a: SigSurface) (b: SigSurface) : SigSurface =
    let into (m: Map<string, SigArity>) (n: Map<string, SigArity>) =
        n
        |> Map.fold (fun acc k v -> if Map.containsKey k acc then acc else Map.add k v acc) m

    {
        Longs = into a.Longs b.Longs
        Shorts = into a.Shorts b.Shorts
        Literals = into a.Literals b.Literals
        Values =
            b.Values
            |> Map.fold
                (fun acc k v ->
                    let ownedByA =
                        Map.containsKey k a.Literals
                        || (k.StartsWith "--" && Map.containsKey (k.Substring 2) a.Longs)
                        || (k.Length = 2 && Map.containsKey (k.Substring 1) a.Shorts)

                    if Map.containsKey k acc || ownedByA then
                        acc
                    else
                        Map.add k v acc)
                a.Values
    }

let private surfaceSets (s: SigSurface) : Set<string> * Set<string> =
    (s.Longs |> Map.keys |> Set.ofSeq), (s.Shorts |> Map.keys |> Set.ofSeq)

/// a PATH-y tool name ("./rooz/v3/lib/jp", "~/.azure/bin/bicep") maps
/// to one flat filename under .weir/sigs [D:scoped-sigs]. Never a nested
/// tree, and never Path.Combine on the raw name: given an absolute tool
/// path Combine discards the sigs dir and aims outside .weir entirely.
/// Injective [D:sig-probe-bounds]: letters, digits, '-', '_' and a
/// non-leading '.' stay; every other character (and '%') is its UTF-8
/// bytes as %XX, so `/a/b/cob`, `_a_b_cob`, `.weir` and `weir` keep
/// apart. A plain name is its own file name. A name past NAME_MAX (255
/// bytes) keeps a head of its encoding and adds `~` and the SHA-256 of
/// the tool name: the encoding never keeps a `~`, so a hashed name meets
/// no encoded one, and the hash keeps it stable and apart.
let sigFileName (tool: string) : string =
    let sb = System.Text.StringBuilder()

    tool
    |> String.iteri (fun i c ->
        if System.Char.IsLetterOrDigit c || c = '-' || c = '_' || (c = '.' && i > 0) then
            sb.Append c |> ignore
        else
            for b in System.Text.Encoding.UTF8.GetBytes(string c) do
                sb.Append($"%%{b:X2}") |> ignore)

    let encoded = sb.ToString()

    if System.Text.Encoding.UTF8.GetByteCount encoded + ".weir".Length <= 255 then
        encoded + ".weir"
    else
        // the head stops at a whole character, never inside a %XX or a
        // multi-byte letter
        let head = System.Text.StringBuilder()
        let mutable i = 0

        while i < encoded.Length && head.Length < 120 do
            let step = if encoded[i] = '%' then 3 else 1
            let piece = encoded.Substring(i, min step (encoded.Length - i))

            if System.Text.Encoding.UTF8.GetByteCount(head.ToString() + piece) <= 120 then
                head.Append piece |> ignore
                i <- i + step
            else
                i <- encoded.Length

        let hash =
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes tool)
            |> Array.map (fun b -> b.ToString "x2")
            |> String.concat ""

        head.ToString() + "~" + hash + ".weir"

// the name before it was injective: separators became '_', leading dots
// dropped — a signature the lock records under it is still found
// [D:sig-probe-bounds]
let internal legacySigFileName (tool: string) : string =
    (tool
     |> String.map (fun c ->
         if System.Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then
             c
         else
             '_'))
        .TrimStart('.')
    + ".weir"

/// the file a `#sig <tool>` names from a script in `scriptDir`: the
/// override, else the nearest .weir/sigs/ under the tool's own name, a
/// legacy name only where the lock records it [D:sig-probe-bounds]
let resolveSigFile (scriptDir: string) (decl: SigDecl) : Result<string, string> =
    match decl.Override with
    | Some p -> Ok(IO.Path.GetFullPath(IO.Path.Combine(scriptDir, p)))
    | None ->
        match Contracts.findWeirDir scriptDir with
        | Error e -> Error $"#sig {quoteData decl.Tool}: {e}"
        | Ok weirDir ->
            let at name = IO.Path.Combine(weirDir, "sigs", name)
            let current = at (sigFileName decl.Tool)
            let legacy = at (legacySigFileName decl.Tool)

            // the legacy name only where the lock records it
            // for this very tool — never another tool's file
            let lockedLegacy () =
                match Contracts.readLock weirDir with
                | Ok entries ->
                    entries
                    |> List.exists (fun e ->
                        e.Kind = "sig"
                        && e.Name = decl.Tool
                        && e.Path.Replace('\\', '/') = "sigs/" + legacySigFileName decl.Tool)
                | Error _ -> false

            // the plain name another tool's lock entry records
            // (its legacy name) is that tool's file, never this one's
            let ownedByOther () =
                match Contracts.readLock weirDir with
                | Ok entries ->
                    let rel = "sigs/" + sigFileName decl.Tool

                    if
                        entries
                        |> List.exists (fun e ->
                            e.Kind = "sig" && e.Name = decl.Tool && e.Path.Replace('\\', '/') = rel)
                    then
                        None
                    else
                        entries
                        |> List.tryFind (fun e ->
                            e.Kind = "sig" && e.Name <> decl.Tool && e.Path.Replace('\\', '/') = rel)
                        |> Option.map (fun e -> e.Name)
                | Error _ -> None

            if not (IO.File.Exists current) && IO.File.Exists legacy && lockedLegacy () then
                Ok legacy
            else
                match (if IO.File.Exists current then ownedByOther () else None) with
                | Some other ->
                    Error
                        $"#sig {decl.Tool}: sigs/{sigFileName decl.Tool} is the signature of '{Eval.sanitizeTtyEcho other}' (the lock records it under its legacy name) — regenerate that one, which moves it to its own name: weir add sig '{Eval.sanitizeTtyEcho other}'"
                | None -> Ok current

// the decl's head line carries the TYPE's doc, never a field's or a
// case's (a doc above it attaches to the type name), so it is skipped
let internal fieldDocsFor (rawLines: string list) (ll: LogicalLine) : Map<string, string> =
    let physLines = ll.Segments |> List.map (fun (_, p, _) -> p) |> Set.ofList
    let arr = List.toArray rawLines

    docAttachments rawLines
    |> List.choose (fun d ->
        match d.Doc with
        | first :: _ when
            first <> ""
            && d.Line <> ll.Head
            && Set.contains d.Line physLines
            && d.Line - 1 < arr.Length
            && d.Col - 1 + d.Len <= arr[d.Line - 1].Length
            ->
            Some(arr[d.Line - 1].Substring(d.Col - 1, d.Len), first)
        | _ -> None)
    |> Map.ofList

// the type's own `///` doc, its first paragraph — an Args target's
// program description in --help [D:help-case-docs]. Stored in the
// decl's Docs under the key "", which no field or case can spell.
let internal typeDocFor (rawLines: string list) (ll: LogicalLine) : Map<string, string> =
    docAttachments rawLines
    |> List.tryFind (fun d -> d.Line = ll.Head)
    |> Option.map (fun d -> d.Doc |> List.takeWhile (fun l -> l.Trim() <> "") |> String.concat "\n")
    |> Option.filter (fun p -> p <> "")
    |> Option.map (fun p -> Map.ofList [ "", p ])
    |> Option.defaultValue Map.empty

/// load the signatures a file declared; errors become diagnostics at
/// the declaring line. The sig file is an ordinary weir module
/// (decl-only + weak purity for free): `module X`, `let version =
/// "<--version's first line>"`, optionally `let exhaustive = true`, and
/// the surface as the type named `Cmd`.
let loadSigs (path: string) (decls: SigDecl list) : Diagnostic list * SigInfo list =
    let diags = ResizeArray<Diagnostic>()
    let infos = ResizeArray<SigInfo>()

    let mk (line: int) (msg: string) : Diagnostic = {
        File = path
        Line = line
        Col = 1
        EndLine = None
        EndCol = None
        Severity = "error"
        Code = "sig"
        Related = []
        Fix = None
        Message = msg
    }

    let scriptDir =
        try
            let full = IO.Path.GetFullPath path
            let d = IO.Path.GetDirectoryName full
            if String.IsNullOrEmpty d then "." else d
        with _ ->
            "."

    for decl in decls do
        if decl.Tool = "" then
            diags.Add(mk decl.Line "malformed #sig — usage: #sig <tool> [\"path/to/sig.weir\"]")
        // a quoted tool name would search for a file named "tool".weir,
        // quotes and all — the directive takes a bare word (only the
        // override path is quoted) [D:sig-version-probe]
        elif decl.Tool.StartsWith "\"" || decl.Tool.EndsWith "\"" then
            let bare = decl.Tool.Trim '"'
            diags.Add(mk decl.Line $"#sig takes a bare tool name — drop the quotes: #sig {quoteData bare}")
        else
            let resolved = resolveSigFile scriptDir decl

            match resolved with
            | Error e -> diags.Add(mk decl.Line e)
            | Ok sigFile when not (IO.File.Exists sigFile) ->
                // the checker can tell restore from add (matching schema)
                let locked =
                    match Contracts.findWeirDir scriptDir with
                    | Ok weirDir ->
                        match Contracts.readLock weirDir with
                        | Ok entries -> entries |> List.exists (fun e -> e.Kind = "sig" && e.Name = decl.Tool)
                        | Error _ -> false
                    | Error _ -> false

                let hint =
                    if locked then
                        "the lock records it; the file should be checked in — restore cannot regenerate a signature"
                    else
                        $"generate it: weir add sig {quoteData decl.Tool}"

                diags.Add(
                    mk
                        decl.Line
                        $"#sig {quoteData decl.Tool}: no {quoteData sigFile} (searched from {quoteData scriptDir}) — {hint}"
                )
            | Ok sigFile ->
                let absScript = IO.Path.GetFullPath path

                match loadModuleCachedWith true resolver (ModuleLoads()) [ absScript ] absScript sigFile None with
                | Error ie -> diags.Add(mk decl.Line $"#sig {quoteData decl.Tool}: {ie.Message}")
                | Ok lm ->
                    let letStr name =
                        lm.Body
                        |> List.tryPick (function
                            | _, CLet(n, te) when n = name ->
                                match te.Kind with
                                | Check.TEStr s -> Some s
                                | _ -> None
                            | _ -> None)

                    let letBool name =
                        lm.Body
                        |> List.tryPick (function
                            | _, CLet(n, te) when n = name ->
                                match te.Kind with
                                | Check.TEBool b -> Some b
                                | _ -> None
                            | _ -> None)

                    // `let version` is optional [D:sig-version-probe]: a
                    // tool that does not answer --version has no identity
                    // to record — absence is a stated fact, not a defect
                    // the `///` field docs, which a loaded module's records
                    // do not carry: a value's doc can say it names a file
                    // [D:sig-arg-complete]
                    let fieldDocs =
                        try
                            let raw = IO.File.ReadAllLines sigFile |> List.ofArray

                            let numbered =
                                raw
                                |> List.mapi (fun i l -> i + 1, l)
                                |> List.filter (fun (_, l) -> classifyLine l <> LineKind.CommentOnly)
                                |> List.map (fun (n, l) -> n, stripComment l)

                            match assemble numbered with
                            | Ok lls ->
                                lls
                                |> List.choose (fun ll ->
                                    let m = Text.RegularExpressions.Regex.Match(ll.Text, @"^type\s+(\w+)")

                                    if m.Success then
                                        Some(m.Groups[1].Value, fieldDocsFor raw ll)
                                    else
                                        None)
                                |> Map.ofList
                            | Error _ -> Map.empty
                        with _ ->
                            Map.empty

                    let recordOf name =
                        lm.TypeDefs
                        |> List.tryPick (function
                            | (n, Record rd) when n = name ->
                                match Map.tryFind name fieldDocs with
                                | Some docs when rd.Docs.IsEmpty -> Some { rd with Docs = docs }
                                | _ -> Some rd
                            | _ -> None)

                    let letStrs name =
                        lm.Body
                        |> List.tryPick (function
                            | _, CLet(n, te) when n = name ->
                                match te.Kind with
                                | Check.TEList items ->
                                    Some(
                                        items
                                        |> List.choose (fun i ->
                                            match i.Kind with
                                            | Check.TEStr s -> Some s
                                            | _ -> None)
                                    )
                                | _ -> None
                            | _ -> None)

                    // the convention lets [D:sig-conventions]: absent = GNU,
                    // no abbreviations, no negation, no operand stop — what
                    // every older signature meant. Paths in the lists are
                    // written as typed (`image prune`); "" is the top level
                    let keysOf name =
                        letStrs name
                        |> Option.defaultValue []
                        |> List.map (fun p -> p.Trim().Replace(" ", "-"))
                        |> Set.ofList

                    let convention = letStr "convention" |> Option.defaultValue "gnu"

                    if not (List.contains convention [ "gnu"; "go"; "find" ]) then
                        diags.Add(
                            mk
                                decl.Line
                                $"#sig {quoteData decl.Tool}: `let convention = \"{convention}\"` names no flag convention — use \"gnu\" (the default), \"go\" or \"find\""
                        )

                    // the mechanism the REPL's Tab may ask for values
                    // [D:sig-dynamic-complete]: a name, never a command
                    let dynamic =
                        let bad what =
                            diags.Add(
                                mk
                                    decl.Line
                                    $"#sig {quoteData decl.Tool}: {what} — use \"git\" or \"cobra\", or drop the line"
                            )

                            ""

                        match
                            lm.Body
                            |> List.tryPick (function
                                | _, CLet(n, te) when n = "dynamic" -> Some te.Kind
                                | _ -> None)
                        with
                        | None -> ""
                        | Some(Check.TEStr("git" | "cobra" as m)) -> m
                        | Some(Check.TEStr s) -> bad $"`let dynamic = \"{quoteData s}\"` names no completion mechanism"
                        | Some _ -> bad "`let dynamic` takes a mechanism's name, a string"

                    // a field typed by a union of bare cases records its
                    // choices (`type Color = | Always | Never | Auto` →
                    // always, never, auto) [D:sig-arg-complete]
                    let choicesOf name =
                        lm.TypeDefs
                        |> List.tryPick (function
                            | (n, Union ud) when n = name && ud.Cases |> List.forall (fun (_, p) -> p.IsNone) ->
                                Some(ud.Cases |> List.map (fun (c, _) -> Weir.Argv.kebabFlag c))
                            | _ -> None)

                    let sigSurface = sigSurface choicesOf

                    let globals = recordOf "Globals" |> Option.map sigSurface

                    let info surfaces subRecords =
                        let g = globals |> Option.defaultValue emptySigSurface

                        {
                            Tool = decl.Tool
                            DeclLine = decl.Line
                            Exhaustive = letBool "exhaustive" |> Option.defaultValue false
                            Subs = surfaces |> Map.map (fun _ sf -> surfaceSets (mergeSurface sf g))
                            Surfaces = surfaces
                            Globals = globals
                            Convention = convention
                            Abbreviations = letBool "abbreviations" |> Option.defaultValue false
                            Negatable = letBool "negatable" |> Option.defaultValue false
                            DashWords = letBool "dashWords" |> Option.defaultValue false
                            Bundles = letBool "bundles" |> Option.defaultValue false
                            StopAtOperand = keysOf "stopAtOperand"
                            Unchecked = keysOf "unchecked"
                            SigPath = sigFile
                            Version = letStr "version" |> Option.defaultValue ""
                            SubRecords = subRecords
                            Dynamic = dynamic
                        }

                    match lm.TypeDefs |> List.tryFind (fun (n, _) -> n = "Cmd") with
                    | None ->
                        diags.Add(
                            mk
                                decl.Line
                                $"#sig {quoteData decl.Tool}: the signature declares no type `Cmd` — the command surface is the type named Cmd (a union of subcommands, or a record of flags)"
                        )
                    | Some(_, Record rd) ->
                        infos.Add(
                            info (Map [ "", sigSurface (recordOf "Cmd" |> Option.defaultValue rd) ]) (Map [ "", "Cmd" ])
                        )
                    | Some(_, Union ud) ->
                        let surfaces =
                            ud.Cases
                            |> List.map (fun (case, payload) ->
                                let flags =
                                    match payload with
                                    | Some(TNamed(rn, [])) ->
                                        match recordOf rn with
                                        | Some rd -> sigSurface rd
                                        | None -> emptySigSurface
                                    | _ -> emptySigSurface

                                Weir.Argv.kebabFlag case, flags)
                            |> Map.ofList

                        let subRecords =
                            ud.Cases
                            |> List.choose (fun (case, payload) ->
                                match payload with
                                | Some(TNamed(rn, [])) when (recordOf rn).IsSome -> Some(Weir.Argv.kebabFlag case, rn)
                                | _ -> None)
                            |> Map.ofList

                        infos.Add(info surfaces subRecords)

    List.ofSeq diags, List.ofSeq infos

/// unknown-flag checking (v1: flags only — L2) over every command whose
/// head has a declared signature. Partial signatures warn; exhaustive
/// ones error. A subcommand word matching no declared case disables
/// flag checking for that command (L2's discipline: no operand model).
/// the source lines of a file the way imports read them (open editor
/// buffers first when the LSP is driving, else disk) — cross-file hover
/// and definition read the target file through the same channel
/// [D:lsp-cross-file]
let targetSourceLines (absPath: string) : string list option =
    // hover/definition want present-or-not; an unreadable target is
    // "no source to show", the same None as absent [D:lockfile-confinement]
    match readImportSource absPath with
    | ISource lines -> Some lines
    | IAbsent
    | IUnreadable _
    | IBadUtf8 _ -> None

/// the signatures a file's #sig head declares, loaded; quiet on load
/// errors — diagnostics are analyzeLines' job [D:lsp-cross-file]
let sigInfosForFile (path: string) (rawLines: string list) : SigInfo list =
    let _, _, decls = scriptBody rawLines
    loadSigs path decls |> snd

// the top level: the flat record, or `type Globals`; a scoped sig
// without one checks the case intersection — what rides every case is
// global — and an empty intersection (hand-written unions that never
// duplicate) checks nothing there [D:scoped-sigs]
let private sigRootSurface (si: SigInfo) : SigSurface option =
    let globals = si.Globals |> Option.defaultValue emptySigSurface

    match Map.tryFind "" si.Surfaces, si.Globals with
    | Some sf, _ -> Some(mergeSurface sf globals)
    | None, Some g -> Some g
    | None, None ->
        match si.Surfaces |> Map.toList |> List.map snd with
        | [] -> None
        | c0 :: rest ->
            let inter (pick: SigSurface -> Map<string, 'v>) =
                pick c0
                |> Map.filter (fun k _ -> rest |> List.forall (fun c -> Map.containsKey k (pick c)))

            let longs = inter (fun c -> c.Longs)

            if longs.IsEmpty then
                None
            else
                Some {
                    Longs = longs
                    Shorts = inter (fun c -> c.Shorts)
                    Literals = inter (fun c -> c.Literals)
                    Values = inter (fun c -> c.Values)
                }

/// the surface a case key checks (and completes) against: its own
/// flags and the Globals; "" is the top level
let sigSurfaceAt (si: SigInfo) (key: string) : SigSurface option =
    if key = "" then
        sigRootSurface si
    else
        let globals = si.Globals |> Option.defaultValue emptySigSurface
        Map.tryFind key si.Surfaces |> Option.map (fun sf -> mergeSurface sf globals)

/// a case key with deeper cases under it — an operand there names one
let sigHasChildren (si: SigInfo) (key: string) : bool =
    not (Map.containsKey "" si.Surfaces)
    && (key = "" || si.Surfaces |> Map.exists (fun k _ -> k.StartsWith(key + "-")))

let sigCmdDiagnostics
    (path: string)
    (infos: SigInfo list)
    (pairs: (LogicalLine * CheckedStatement) list)
    : Diagnostic list =
    if infos.IsEmpty then
        []
    else
        let byTool = infos |> List.map (fun i -> i.Tool, i) |> Map.ofList

        pairs
        |> List.collect (fun (ll, chk) ->
            let roots =
                match chk.Kind with
                | KLet(_, _, te)
                | KLetPat(_, _, te)
                | KCmd te
                | KExpr te -> [ te ]
                | KType _
                | KSig _
                | KModule _
                | KImport _ -> []

            let rec cmds (te: Check.TypedExpr) =
                (match te.Kind with
                 | Check.TECmd(Check.THeadLit prog, args, _) -> [ prog, args ]
                 | _ ->
                     // reified commands desugar the ECmd away — the chain
                     // becomes a `|succeeded`-family builtin applied to the
                     // prog string and an argv list [D:command-signatures]:
                     // recover (prog, words) from that application spine so
                     // `git … | succeeds` is checked like the bare form
                     let rec spine (e: Check.TypedExpr) (acc: Check.TypedExpr list) =
                         match e.Kind with
                         | Check.TEApp(f, a) -> spine f (a :: acc)
                         // a command reifier carries (prog, argv) [D:desugar-namespace];
                         // a library desugar does not — never recover a command from it
                         | Check.TEVar n when Weir.Effects.isCommandReifier n -> Some(n, acc)
                         | _ -> None

                     match spine te [] with
                     | Some(_, args) ->
                         let prog =
                             // orFail's spine carries (msg, prog, argv) —
                             // the prog is the last string before the list
                             args
                             |> List.choose (fun a ->
                                 match a.Kind with
                                 | Check.TEStr p -> Some p
                                 | _ -> None)
                             |> List.tryLast

                         let words =
                             // a splatted reified argv is a Seq.append fold
                             // [D:desugar-capture]: the literal chunks are
                             // TEList descendants — collect them all (the
                             // spliced seq's own words stay runtime-unknowable)
                             let rec lists (e: Check.TypedExpr) =
                                 match e.Kind with
                                 | Check.TEList items -> items
                                 | _ -> Check.childExprs e |> List.collect lists

                             match args |> List.collect lists with
                             | [] -> None
                             | ws -> Some ws

                         match prog, words with
                         | Some p, Some ws -> [ p, ws ]
                         | _ -> []
                     | None -> [])
                @ (Check.childExprs te |> List.collect cmds)

            roots
            |> List.collect cmds
            |> List.collect (fun (prog, args) ->
                match Map.tryFind prog byTool with
                | None -> []
                | Some si ->
                    // a spliced word is a placeholder: its text is unknown
                    // at check, but it still takes its place — a flag's
                    // value, or an operand naming no case
                    let words =
                        args
                        |> List.map (fun a ->
                            match a.Kind with
                            | Check.TEStr w -> Some w, a.Span
                            | _ -> None, a.Span)

                    let surfaceAt = sigSurfaceAt si
                    let hasChildren = sigHasChildren si

                    // a scoped surface names its case in the warning, as
                    // typed (`gh issue list`) [D:scoped-sigs]
                    let toolAndSub (typed: string list) =
                        quoteData (String.concat " " (si.Tool :: typed))

                    let sev = if si.Exhaustive then "error" else "warning"

                    // an older signature [D:sig-arity] [D:sig-help-shapes]: every field bool and
                    // none of the convention lets — it checks as it always
                    // did, a multi-letter single-dash word unread (`-fsSL`,
                    // `-i.bak`, `-TERM`)
                    let older =
                        si.Convention = "gnu"
                        && not si.Abbreviations
                        && not si.Negatable
                        && not si.DashWords
                        && not si.Bundles
                        && si.StopAtOperand.IsEmpty
                        && si.Unchecked.IsEmpty
                        && (Option.toList si.Globals @ (si.Surfaces |> Map.toList |> List.map snd))
                           |> List.forall (fun sf ->
                               [ sf.Longs; sf.Shorts; sf.Literals ]
                               |> List.forall (Map.forall (fun _ a -> a = ANone)))

                    let note =
                        if si.Exhaustive then
                            $"(#sig {quoteData si.Tool}, line {si.DeclLine}; exhaustive signature)"
                        else
                            $"(#sig {quoteData si.Tool}, line {si.DeclLine}; partial signature — a scraped surface may be incomplete)"

                    let mk (sp: Span) (msg: string) =
                        let l1, c1 = translate ll sp.Start.Col
                        let l2, c2 = translate ll (max sp.Start.Col (sp.End.Col - 1))

                        {
                            File = path
                            Line = l1
                            Col = c1
                            EndLine = Some l2
                            EndCol = Some(c2 + 1)
                            Severity = sev
                            Code = "sig"
                            Related = []
                            Fix =
                                (let s0 = max 0 (min ll.Text.Length (sp.Start.Col - 1))
                                 let s1 = max s0 (min ll.Text.Length (sp.End.Col - 1))
                                 replaceWithFix (ll.Text.Substring(s0, s1 - s0)) msg l1 c1)
                            Message = msg
                        }

                    // did-you-mean over the spellings the convention takes
                    // [D:sig-conventions]: shorts count (`--H` -> `-H`), a
                    // distant match is no suggestion (a one-letter name
                    // never borrows a two-letter one)
                    let dym (typed: string) (cands: string seq) =
                        let bare = typed.TrimStart '-'
                        let limit = if bare.Length <= 3 then 1 else 2

                        // names compare without their dashes: `--H` is one
                        // dash from `-H`, zero letters
                        cands
                        |> Seq.filter (fun c -> c <> typed)
                        |> Seq.map (fun c -> c, editDistance bare (c.TrimStart '-'))
                        |> Seq.filter (fun (_, d) -> d = 0 || (d <= limit && d < bare.Length))
                        |> Seq.sortBy (fun (c, d) -> d, abs (c.Length - typed.Length), c)
                        |> Seq.tryHead
                        |> Option.map (fun (c, _) -> $". Did you mean '{quoteData c}'?")
                        |> Option.defaultValue ""

                    let spellings (sf: SigSurface) =
                        Seq.concat [
                            sf.Longs |> Map.keys |> Seq.map (fun l -> "--" + l)
                            sf.Shorts |> Map.keys |> Seq.map (fun s -> "-" + s)
                            sf.Literals |> Map.keys |> Seq.map id
                        ]

                    // the value-word step after a flag: 0 none, 1 the next
                    // word, 2 the tool's options end here, 3 the next word,
                    // then they end
                    let after (a: SigArity) (attached: bool) =
                        match a with
                        | ANone
                        | AOpt -> 0
                        | AOne -> if attached then 0 else 1
                        | ARest -> if attached then 2 else 3

                    let unknown (sf: SigSurface) typed (shown: string) (sp: Span) =
                        mk
                            sp
                            $"unknown flag '{quoteData shown}' for {toolAndSub typed}{dym shown (spellings sf)} {note}"

                    // one flag word against one surface: the diagnostics and
                    // the value-word step
                    let checkFlag (sf: SigSurface) typed (w: string) (sp: Span) : Diagnostic list * int =
                        let name, attached =
                            match w.IndexOf '=' with
                            | -1 -> w, false
                            | i -> w.Substring(0, i), true

                        match si.Convention with
                        | "go" ->
                            // `-x`, `-long`, `--long` are one flag (Go's flag
                            // package); no bundles
                            let bare = name.TrimStart '-'

                            let hit =
                                [
                                    Map.tryFind bare sf.Longs
                                    Map.tryFind bare sf.Shorts
                                    Map.tryFind ("-" + bare) sf.Literals
                                    Map.tryFind ("--" + bare) sf.Literals
                                ]
                                |> List.tryPick id

                            match hit with
                            | Some a -> [], after a attached
                            | None when name.StartsWith "---" -> [ unknown sf typed name sp ], 0
                            | None ->
                                let goSpellings =
                                    spellings sf
                                    |> Seq.map (fun s ->
                                        (if name.StartsWith "--" then "--" else "-") + s.TrimStart '-')

                                [
                                    mk
                                        sp
                                        $"unknown flag '{quoteData name}' for {toolAndSub typed}{dym name goSpellings} {note}"
                                ],
                                0
                        | "find" ->
                            // whole words: a predicate (`-name`), a listed
                            // `--help`; a one-letter option may carry its
                            // value attached (`-O3`)
                            let exact =
                                [
                                    Map.tryFind name sf.Literals
                                    (if name.StartsWith "--" then
                                         Map.tryFind (name.Substring 2) sf.Longs
                                     elif name.Length = 2 then
                                         Map.tryFind (name.Substring 1) sf.Shorts
                                     else
                                         None)
                                ]
                                |> List.tryPick id

                            let glued () =
                                Seq.append
                                    (sf.Shorts |> Map.toSeq |> Seq.map (fun (k, a) -> "-" + k, a))
                                    (sf.Literals |> Map.toSeq |> Seq.filter (fun (k, _) -> k.Length = 2))
                                |> Seq.tryFind (fun (k, a) ->
                                    (a = AOne || a = AOpt) && w.StartsWith k && w.Length > 2)

                            match exact with
                            | Some a -> [], after a attached
                            | None ->
                                match glued () with
                                | Some _ -> [], 0
                                | None -> [ unknown sf typed name sp ], 0
                        | _ ->
                            if w.StartsWith "--" then
                                let long = name.Substring 2

                                let negated () =
                                    if si.Negatable && long.StartsWith "no-" then
                                        match Map.tryFind (long.Substring 3) sf.Longs with
                                        | Some(ANone | AOpt) -> Some ANone
                                        | _ -> None
                                    else
                                        None

                                // getopt_long's unique prefix [D:sig-conventions]
                                let abbreviated () =
                                    if si.Abbreviations && long.Length >= 2 then
                                        match
                                            sf.Longs |> Map.toList |> List.filter (fun (k, _) -> k.StartsWith long)
                                        with
                                        | [ (_, a) ] -> Some a
                                        | _ -> None
                                    else
                                        None

                                let hit =
                                    match Map.tryFind long sf.Longs with
                                    | Some a -> Some a
                                    | None ->
                                        match Map.tryFind name sf.Literals with
                                        | Some a -> Some a
                                        | None -> negated () |> Option.orElse (abbreviated ())

                                match hit with
                                | Some a -> [], after a attached
                                | None -> [ unknown sf typed name sp ], 0
                            else
                                match Map.tryFind name sf.Literals with
                                | Some a -> [], after a attached
                                | None when System.Char.IsDigit w[1] -> [], 0 // `-1`, `-7`: a count or a value, never a flag
                                // a surface that recorded no shorts has no
                                // evidence to warn on (BSD grep's help is
                                // usage-only — the harvest sees longs, never
                                // the short bundle) [D:sig-version-probe]
                                | None when sf.Shorts.IsEmpty -> [], 0
                                | None when older && w.Length > 2 -> [], 0
                                // a single letter is never the tool's value word
                                // (no signal is one letter): `kill -x` is a typo
                                | None when si.DashWords && w.Length > 2 && not (sf.Shorts.ContainsKey(string w[1])) ->
                                    [], 0
                                | None ->
                                    // a bundle, letter by letter: a value-taking
                                    // letter takes the rest of the word (`-n5`,
                                    // `-i.bak`) or the next one (`-xzf a.tgz`)
                                    let rec bundle i =
                                        if i >= w.Length then
                                            [], 0
                                        else
                                            let c = string w[i]

                                            match Map.tryFind c sf.Shorts with
                                            | None ->
                                                let inWord = if w.Length > 2 then $" (in '{quoteData w}')" else ""

                                                [
                                                    mk
                                                        sp
                                                        $"unknown flag '-{quoteData (string c)}'{inWord} for {toolAndSub typed} {note}"
                                                ],
                                                0
                                            | Some ANone -> bundle (i + 1)
                                            | Some a -> [], after a (i + 1 < w.Length)

                                    bundle 1

                    let isFlag (w: string) = w.Length > 1 && w[0] = '-'

                    // the longest run of operand words naming a case under key
                    let subAt key (ws: (string option * Span) list) =
                        let run =
                            ws
                            |> List.takeWhile (fun (w, _) ->
                                match w with
                                | Some w -> not (isFlag w)
                                | None -> false)
                            |> List.truncate 4
                            |> List.choose fst

                        [ List.length run .. -1 .. 1 ]
                        |> List.tryPick (fun n ->
                            let toks = run |> List.truncate n
                            let k = String.concat "-" (if key = "" then toks else key :: toks)

                            if Map.containsKey k si.Surfaces then
                                Some(n, k, toks)
                            else
                                None)

                    // where the rest of the line lands, diagnostics aside:
                    // flags step over their known values, operands descend
                    let rec reach key typed (ws: (string option * Span) list) =
                        match ws with
                        | [] -> key, typed
                        | (Some "--", _) :: _ -> key, typed
                        | (Some w, sp) :: rest when isFlag w ->
                            match surfaceAt key with
                            | Some sf ->
                                match checkFlag sf typed w sp with
                                | [], 1 ->
                                    match rest with
                                    | _ :: r -> reach key typed r
                                    | [] -> key, typed
                                | _ -> reach key typed rest
                            | None -> reach key typed rest
                        | _ ->
                            if hasChildren key then
                                match subAt key ws with
                                | Some(n, k, toks) when not (Set.contains k si.Unchecked) ->
                                    reach k (typed @ toks) (List.skip n ws)
                                | _ -> key, typed
                            else
                                key, typed

                    // the walk [D:sig-operand-stop]: flags check against the
                    // case reached so far; an operand either names a deeper
                    // case (the longest run of operand words wins —
                    // `issue list` beats `issue`), or, under a case with
                    // children, is a subcommand the signature does not know
                    // and ends the check (its flags are not its parent's);
                    // a leaf's operands are permuted GNU-style unless its
                    // case stops at the first one
                    let rec walk acc key typed (ws: (string option * Span) list) =
                        match ws with
                        | [] -> acc
                        | (Some "--", _) :: _ -> acc // end-of-flags: everything after is operands
                        | (Some w, sp) :: rest when isFlag w ->
                            match surfaceAt key with
                            | None -> walk acc key typed rest // no evidence at this level (L2 discipline)
                            | Some sf ->
                                let ds, step = checkFlag sf typed w sp

                                // a flag the level does not know, written before
                                // the subcommand, is judged by the case the line
                                // reaches — cobra parses every flag at the final
                                // command (`gh --repo o/r issue view`), its
                                // value word included [D:scoped-sigs]
                                let ds, step =
                                    if ds.IsEmpty || not (hasChildren key) then
                                        ds, step
                                    else
                                        let deeper ws =
                                            match reach key typed ws with
                                            | k, t when k <> key -> Some(k, t)
                                            | _ -> None

                                        let landed =
                                            match deeper rest with
                                            | Some r -> Some(r, false)
                                            | None ->
                                                match rest with
                                                | (Some v, _) :: tail when not (isFlag v) ->
                                                    deeper tail |> Option.map (fun r -> r, true)
                                                | _ -> None

                                        match landed with
                                        | Some((k, t), tookValue) ->
                                            match surfaceAt k with
                                            | Some deep ->
                                                match checkFlag deep t w sp with
                                                | [], st -> [], (if tookValue then 1 else st)
                                                | ds2, _ -> ds2, step
                                            | None -> ds, step
                                        | None -> ds, step

                                let acc = List.rev ds @ acc

                                match step, rest with
                                | 0, _ -> walk acc key typed rest
                                | 1, _ :: rest' -> walk acc key typed rest'
                                | 1, [] -> acc
                                | _ -> acc
                        | _ :: rest ->
                            if hasChildren key then
                                match subAt key ws with
                                | Some(_, k, _) when Set.contains k si.Unchecked -> acc
                                | Some(n, k, toks) -> walk acc k (typed @ toks) (List.skip n ws)
                                | None -> acc
                            elif Set.contains key si.StopAtOperand then
                                acc
                            else
                                walk acc key typed rest

                    // `unchecked` naming the tool itself ("") checks none of
                    // its lines [D:sig-open-tool]
                    if Set.contains "" si.Unchecked then
                        []
                    else
                        walk [] "" [] words |> List.rev))

// ---- a continuation line that starts with a command [D:continued-command] --
// An indented line continues its statement, so under a command its words
// become that command's arguments. When the continuation's first word
// names a program, the line was almost certainly meant as a command of
// its own (pasted from an indented list or doc).

/// commands whose job is to take a command: a continuation under one of
/// these is the wrapped command's own words [D:continued-command]
let commandWrappers: Set<string> =
    set [
        "sudo"
        "doas"
        "env"
        "xargs"
        "time"
        "nice"
        "nohup"
        "timeout"
        "exec"
        "command"
        "watch"
        "ssh"
        "parallel"
        "flock"
        "chroot"
        "strace"
        "stdbuf"
        "setsid"
        "unbuffer"
    ]

/// what weir resolves as a program in head position: a PATH lookup and
/// the builtin command heads (the REPL adds its alias table)
let resolvesAsProgram (w: string) : bool =
    Builtins.commandCallable.Contains w || Extern.exists w

/// one hit: the continuation word's physical (line, col, endCol) and the
/// message
type ContinuedCommand = {
    CLine: int
    CCol: int
    CEndCol: int
    CMessage: string
}

/// every command in the statement whose argv takes the first word of a
/// continuation line that names a program
let continuedCommands
    (resolves: string -> bool)
    (infos: SigInfo list)
    (ll: LogicalLine)
    (te: Check.TypedExpr)
    : ContinuedCommand list =
    let segs = List.toArray ll.Segments

    // logical index -> true when it is the first character of a physical
    // line the assembler joined with a plain space (a sibling, `in`, or
    // district join is never an argv continuation)
    let spaceJoined (idx: int) =
        ll.Ends.Length = segs.Length
        && segs
           |> Array.indexed
           |> Array.exists (fun (i, (js, _, _)) -> i > 0 && js = idx && ll.Ends[i - 1] + 1 = js)

    let byTool = infos |> List.map (fun i -> i.Tool, i) |> Map.ofList

    // a sig marking the command's (sub)command as taking a command
    // [D:sig-operand-stop]: `""` is the tool itself, else a `-`-joined
    // run of the leading operand words
    let sigTakesCommand (prog: string) (args: Check.TypedExpr list) =
        match Map.tryFind prog byTool with
        | None -> false
        | Some si ->
            let run =
                args
                |> List.map (fun a ->
                    match a.Kind with
                    | Check.TEStr w -> Some w
                    | _ -> None)
                |> List.takeWhile (fun w ->
                    match w with
                    | Some w -> not (w.StartsWith "-")
                    | None -> false)
                |> List.choose id

            Set.contains "" si.StopAtOperand
            || [ 1 .. run.Length ]
               |> List.exists (fun n -> Set.contains (String.concat "-" (List.truncate n run)) si.StopAtOperand)

    // the program a continuation word names, if it does: an explicit
    // `^cmd`, or a bare word that resolves — never a flag, a quoted
    // string, a splice or a path
    let programWord (a: Check.TypedExpr) =
        match a.Kind with
        | Check.TEStr w when w <> "" && spaceJoined (a.Span.Start.Col - 1) ->
            let first = ll.Text[a.Span.Start.Col - 1]

            if first = '"' || first = '\'' || first = '@' || first = '$' then
                None
            elif w.StartsWith "^" && w.Length > 1 then
                Some w
            elif w.StartsWith "-" || w.Contains '/' then
                None
            elif resolves w then
                Some w
            else
                None
        | _ -> None

    let hits (head: string) (exempt: bool) (headSpan: Span) (args: Check.TypedExpr list) =
        if exempt then
            []
        else
            let headLine = fst (translate ll headSpan.Start.Col)

            let shown =
                match args with
                | { Kind = Check.TEStr w; Span = sp } :: _ when fst (translate ll sp.Start.Col) = headLine ->
                    $"{head} {w} …"
                | _ -> $"{head} …"

            args
            |> List.choose (fun a ->
                programWord a
                |> Option.map (fun w ->
                    let line, col = translate ll a.Span.Start.Col
                    let word = w.TrimStart '^'

                    {
                        CLine = line
                        CCol = col
                        CEndCol = col + w.Length
                        CMessage =
                            $"this indented line continues `{shown}` — its words become its arguments, and `{word}` looks like a second command: dedent the line to run it on its own, or quote the word if it really is an argument"
                    }))

    // a reified argv: the literal chunks are TEList descendants
    // (a splatted argv is a Seq.append fold)
    let rec argvWords (e: Check.TypedExpr) =
        match e.Kind with
        | Check.TEList items -> items
        | _ -> Check.childExprs e |> List.collect argvWords

    let litHead (prog: string) (args: Check.TypedExpr list) =
        Set.contains prog commandWrappers || sigTakesCommand prog args

    let rec walk (e: Check.TypedExpr) : ContinuedCommand list =
        let own =
            match e.Kind with
            | Check.TECmd(Check.THeadLit prog, args, _) -> hits prog (litHead prog args) e.Span args
            | Check.TECmd(Check.THeadDyn(d, _), args, _) -> hits $"^{d}" false e.Span args
            | Check.TEApp _ ->
                let rec spine (x: Check.TypedExpr) acc =
                    match x.Kind with
                    | Check.TEApp(f, a) -> spine f (a :: acc)
                    | Check.TEVar v -> Some(v, acc)
                    | _ -> None

                match spine e [] with
                // a reified pipeline's stages ride (prog, argv, env) tuples
                // [D:chain-reifiers]
                | Some(v, args) when v.StartsWith "|chain" ->
                    args
                    |> List.collect (fun a ->
                        match a.Kind with
                        | Check.TEList stages ->
                            stages
                            |> List.collect (fun st ->
                                match st.Kind with
                                | Check.TETuple [ { Kind = Check.TEStr prog; Span = sp }; argv; _ ] ->
                                    let words = argvWords argv
                                    hits prog (litHead prog words) sp words
                                | _ -> [])
                        | _ -> [])
                // a single reified command carries (prog, argv) [D:exit-reifiers]
                | Some(v, args) ->
                    match reifiedProgSlot v with
                    | Some(idx, arity) when List.length args = arity ->
                        match List.item idx args with
                        | { Kind = Check.TEStr prog; Span = sp } ->
                            let words = argvWords (List.item (idx + 1) args)
                            hits prog (litHead prog words) sp words
                        | _ -> []
                    | _ -> []
                | None -> []
            | _ -> []

        own @ (Check.childExprs e |> List.collect walk)

    // no continuation, no claim: the walk runs only over joined lines
    if segs.Length < 2 then [] else walk te |> List.distinct

/// the continued-command warnings for checked statements — `weir check`,
/// the LSP and the runner [D:continued-command]
let continuedCommandDiagnostics
    (path: string)
    (infos: SigInfo list)
    (pairs: (LogicalLine * CheckedStatement) list)
    : Diagnostic list =
    pairs
    |> List.collect (fun (ll, chk) ->
        match chk.Kind with
        | KLet(_, _, te)
        | KLetPat(_, _, te)
        | KCmd te
        | KExpr te -> continuedCommands resolvesAsProgram infos ll te
        | KType _
        | KSig _
        | KModule _
        | KImport _ -> [])
    |> List.map (fun h -> {
        File = path
        Line = h.CLine
        Col = h.CCol
        EndLine = Some h.CLine
        EndCol = Some h.CEndCol
        Severity = "warning"
        Code = "continued-command"
        Related = []
        Fix = None
        Message = h.CMessage
    })

// ---- signature-driven argument completion [D:sig-arg-complete] -------------
// One completer for the REPL's Tab and the LSP: subcommand words, a
// case's flags, a flag's value by arity — walked by the checker's own
// rules (the longest case run, the globals at the top, the operand stop,
// unchecked cases), so what completes is what the check accepts.

/// what a sig says the word at the cursor is
type SigCompletion =
    /// these words, already filtered by what is typed
    | SigWords of string list
    /// the sig cannot know: the caller's path completion, unchanged
    | SigPaths
    /// a value the sig knows nothing about: no offer
    | SigNothing

/// where a word sits, for the tool's own values [D:sig-dynamic-complete]
type DynSlot =
    | DynOperand
    | DynValue
    | DynStatic

module SigComplete =
    // argv words, whitespace-split outside quotes and parens
    let private tokens (s: string) : string list =
        let out = ResizeArray<string>()
        let cur = Text.StringBuilder()
        let mutable quote = ' '
        let mutable depth = 0

        for c in s do
            if quote <> ' ' then
                cur.Append c |> ignore

                if c = quote then
                    quote <- ' '
            elif c = '"' || c = '\'' then
                quote <- c
                cur.Append c |> ignore
            elif c = '(' then
                depth <- depth + 1
                cur.Append c |> ignore
            elif c = ')' then
                depth <- max 0 (depth - 1)
                cur.Append c |> ignore
            elif (c = ' ' || c = '\t') && depth = 0 then
                if cur.Length > 0 then
                    out.Add(cur.ToString())
                    cur.Clear() |> ignore
            else
                cur.Append c |> ignore

        if cur.Length > 0 then
            out.Add(cur.ToString())

        List.ofSeq out

    let private isFlag (w: string) = w.Length > 1 && w[0] = '-'

    // a flag word (no `=value`) against one surface, by the convention:
    // its arity and the spelling its value is recorded under — the
    // checker's checkFlag lookups, diagnostics aside
    let private flagArity (si: SigInfo) (sf: SigSurface) (name: string) : (SigArity * string) option =
        let long k =
            Map.tryFind k sf.Longs |> Option.map (fun a -> a, "--" + k)

        let short k =
            Map.tryFind k sf.Shorts |> Option.map (fun a -> a, "-" + k)

        let literal k =
            Map.tryFind k sf.Literals |> Option.map (fun a -> a, k)

        match si.Convention with
        | "go" ->
            let bare = name.TrimStart '-'

            [ long bare; short bare; literal ("-" + bare); literal ("--" + bare) ]
            |> List.tryPick id
        | "find" ->
            literal name
            |> Option.orElse (
                if name.StartsWith "--" then
                    long (name.Substring 2)
                else
                    None
            )
            |> Option.orElse (if name.Length = 2 then short (name.Substring 1) else None)
        | _ when name.StartsWith "--" ->
            let k = name.Substring 2

            match long k |> Option.orElse (literal name) with
            | Some hit -> Some hit
            | None when si.Negatable && k.StartsWith "no-" ->
                match Map.tryFind (k.Substring 3) sf.Longs with
                | Some(ANone | AOpt) -> Some(ANone, name)
                | _ -> None
            | None when si.Abbreviations && k.Length >= 2 ->
                match sf.Longs |> Map.toList |> List.filter (fun (l, _) -> l.StartsWith k) with
                | [ (l, a) ] -> Some(a, "--" + l)
                | _ -> None
            | None -> None
        | _ ->
            match literal name with
            | Some hit -> Some hit
            | None when name.Length = 2 -> short (name.Substring 1)
            | None when name.Length > 1 && si.DashWords && not (sf.Shorts.ContainsKey(string name[1])) -> None
            | None when si.Bundles || not sf.Shorts.IsEmpty ->
                // a bundle: a value-taking letter takes the rest of the
                // word (attached: no value word follows) or, last, the next
                let rec bundle i =
                    if i >= name.Length then
                        Some(ANone, "")
                    else
                        match Map.tryFind (string name[i]) sf.Shorts with
                        | None -> None
                        | Some ANone -> bundle (i + 1)
                        | Some a when i = name.Length - 1 -> Some(a, "-" + string name[i])
                        | Some _ -> Some(ANone, "")

                bundle 1
            | None -> None

    // the surfaces a flag word completes from: the case's, or — at a top
    // level no rule gives a surface — every case's
    let private flagSurfaces (si: SigInfo) (key: string) : SigSurface list =
        match sigSurfaceAt si key with
        | Some sf -> [ sf ]
        | None when key = "" -> si.Surfaces |> Map.toList |> List.map snd
        | None -> []

    // the spellings a `-` word may complete to, by the convention
    let private flagWords (si: SigInfo) (word: string) (sf: SigSurface) : string list =
        let longs = sf.Longs |> Map.keys |> List.ofSeq
        let shorts = sf.Shorts |> Map.keys |> Seq.map (fun s -> "-" + s) |> List.ofSeq
        let literals = sf.Literals |> Map.keys |> List.ofSeq

        match si.Convention with
        | "go" ->
            // Go's flag package takes `-x` and `--x` alike; the single dash
            // is how Go tools document them
            let dash = if word.StartsWith "--" then "--" else "-"

            (longs |> List.map (fun l -> dash + l))
            @ shorts
            @ (literals |> List.map (fun l -> dash + l.TrimStart '-'))
        | "find" -> literals @ shorts @ (longs |> List.map (fun l -> "--" + l))
        | _ ->
            // the `--no-x` twins only once `--no-` is typed: a bare `--`
            // lists the real flags [D:sig-arg-complete]
            let negs =
                if si.Negatable && word.StartsWith "--no-" then
                    sf.Longs
                    |> Map.toList
                    |> List.filter (fun (l, a) -> (a = ANone || a = AOpt) && not (l.StartsWith "no-"))
                    |> List.map (fun (l, _) -> "--no-" + l)
                else
                    []

            shorts @ (longs |> List.map (fun l -> "--" + l)) @ negs @ literals

    // the next subcommand word under `key`: per deeper case, the shortest
    // run of its segments that is a case itself or groups two or more
    // (`container` of container-run/container-ls), else its whole rest —
    // the kebab key cannot tell `remote set-head` from `remote set head`,
    // and a lone nested case reads as one word
    let private subWords (si: SigInfo) (key: string) : string list =
        let prefix = if key = "" then "" else key + "-"

        let keys =
            si.Surfaces
            |> Map.keys
            |> Seq.filter (fun k -> k <> "" && k <> key && k.StartsWith prefix)
            |> List.ofSeq

        keys
        |> List.map (fun k ->
            let segs = k.Substring(prefix.Length).Split '-'

            [ 1 .. segs.Length ]
            |> List.pick (fun n ->
                let cand = String.concat "-" segs[.. n - 1]

                let grouped =
                    keys |> List.filter (fun o -> o.StartsWith(prefix + cand + "-")) |> List.length

                if n = segs.Length || Map.containsKey (prefix + cand) si.Surfaces || grouped >= 2 then
                    Some cand
                else
                    None))
        |> List.distinct

    // where the complete words before the cursor leave the walk
    type private At =
        | InCase of string
        | Value of SigValue option
        | Open

    let rec private walk (si: SigInfo) (key: string) (ws: string list) : At =
        match ws with
        | [] -> InCase key
        | "--" :: _ -> Open
        | w :: rest when isFlag w ->
            let name, attached =
                match w.IndexOf '=' with
                | -1 -> w, false
                | i -> w.Substring(0, i), true

            let hit =
                sigSurfaceAt si key
                |> Option.bind (fun sf -> flagArity si sf name |> Option.map (fun (a, k) -> a, Map.tryFind k sf.Values))

            match hit with
            | Some(AOne, v) when not attached ->
                match rest with
                | [] -> Value v
                | _ :: r -> walk si key r
            | Some(ARest, v) when not attached ->
                match rest with
                | [] -> Value v
                | _ -> Open
            | Some(ARest, _) -> Open
            | _ -> walk si key rest
        | _ :: rest ->
            if sigHasChildren si key then
                // the longest run of operand words naming a case
                let run = ws |> List.takeWhile (fun w -> not (isFlag w)) |> List.truncate 4

                let deeper =
                    [ run.Length .. -1 .. 1 ]
                    |> List.tryPick (fun n ->
                        let k = String.concat "-" ((if key = "" then [] else [ key ]) @ List.truncate n run)
                        if Map.containsKey k si.Surfaces then Some(n, k) else None)

                // a group no case names (`container` of container-run):
                // the cursor still sits among its words
                let group =
                    let k = String.concat "-" ((if key = "" then [] else [ key ]) @ run)

                    if
                        run.Length = ws.Length
                        && si.Surfaces |> Map.exists (fun o _ -> o.StartsWith(k + "-"))
                    then
                        Some k
                    else
                        None

                match deeper, group with
                | Some(_, k), _ when Set.contains k si.Unchecked -> Open
                | Some(n, k), _ -> walk si k (List.skip n ws)
                | None, Some k -> InCase k
                | None, None -> Open
            elif Set.contains key si.StopAtOperand then
                Open
            else
                walk si key rest

    let private value (v: SigValue option) (typed: string) : SigCompletion =
        match v with
        | Some VPath -> SigPaths
        | Some(VChoices cs) -> SigWords(cs |> List.filter (fun c -> c.StartsWith typed) |> List.sort)
        | None -> SigNothing

    /// complete at the end of `argText` — the text after the tool's name
    /// up to the cursor. `word` is the editor's word there (Complete.
    /// wordStartAt): offers come back as its replacements, so a value
    /// typed after `--flag=` completes alone
    let private completeRaw (si: SigInfo) (argText: string) (word: string) : SigCompletion =
        let ts = tokens argText

        let before, cur =
            if argText = "" || Char.IsWhiteSpace argText[argText.Length - 1] then
                ts, ""
            else
                List.truncate (ts.Length - 1) ts, List.last ts

        // the token's part before the editor's word stays typed; offers
        // that keep it are cut back to the word
        let asWord (r: SigCompletion) =
            match r with
            | SigWords cs when cur.EndsWith word ->
                let lead = cur.Length - word.Length

                SigWords(
                    cs
                    |> List.filter (fun c -> c.Length >= lead && c.StartsWith(cur.Substring(0, lead)))
                    |> List.map (fun c -> c.Substring lead)
                )
            | SigWords _ -> SigNothing
            | r -> r

        if Set.contains "" si.Unchecked then
            // an open tool [D:sig-open-tool]
            SigPaths
        else
            match walk si "" before with
            | Open -> SigPaths
            | Value v ->
                match value v cur with
                | SigWords cs -> asWord (SigWords cs)
                | r -> r
            | InCase key when isFlag cur && cur.Contains '=' ->
                let i = cur.IndexOf '='
                let name, typed = cur.Substring(0, i), cur.Substring(i + 1)

                match
                    sigSurfaceAt si key
                    |> Option.bind (fun sf ->
                        flagArity si sf name |> Option.map (fun (a, k) -> a, Map.tryFind k sf.Values))
                with
                | Some(ANone, _)
                | None -> SigNothing
                | Some(_, v) ->
                    match value v typed with
                    | SigWords cs -> asWord (SigWords(cs |> List.map (fun c -> name + "=" + c)))
                    | r -> r
            | InCase key when cur.StartsWith "-" ->
                flagSurfaces si key
                |> List.collect (flagWords si cur)
                |> List.distinct
                |> List.filter (fun f -> f.StartsWith cur)
                |> List.sort
                |> SigWords
                |> asWord
            | InCase key when sigHasChildren si key ->
                match subWords si key |> List.filter (fun t -> t.StartsWith cur) |> List.sort with
                | [] -> SigPaths
                | subs -> asWord (SigWords subs)
            | InCase _ -> SigPaths

    // an offer is inserted into the user's command line verbatim, and a
    // signature (a project's, possibly untrusted) spells it: whitespace
    // would split it into extra argv words, a control character (a
    // newline) into another statement, a quote would reopen a string —
    // such a spelling is never offered [D:sig-arg-complete]
    let private spellable (w: string) =
        w <> ""
        && w
           |> Seq.forall (fun c ->
               not (Char.IsWhiteSpace c)
               && not (Char.IsControl c)
               && c <> '"'
               && c <> '\''
               && c <> '`')

    let complete (si: SigInfo) (argText: string) (word: string) : SigCompletion =
        match completeRaw si argText word with
        | SigWords cs -> SigWords(cs |> List.filter spellable)
        | r -> r

    /// the argv words of `argText`, the complete ones and the one at its
    /// end ("" after whitespace)
    let split (argText: string) : string list * string =
        let ts = tokens argText

        if argText = "" || Char.IsWhiteSpace argText[argText.Length - 1] then
            ts, ""
        else
            List.truncate (ts.Length - 1) ts, List.last ts

    /// where the word at the end of `argText` sits, for the tool's own
    /// values [D:sig-dynamic-complete]: an operand or a flag's value may
    /// ask; a subcommand or a flag name stays the sig's (Tab spawns
    /// nothing there)
    let dynamicSlot (si: SigInfo) (argText: string) : DynSlot =
        let before, cur = split argText

        if Set.contains "" si.Unchecked then
            (if isFlag cur then DynStatic else DynOperand)
        else
            match walk si "" before with
            | Value _ -> DynValue
            | Open -> if isFlag cur then DynStatic else DynOperand
            | InCase key when isFlag cur && cur.Contains '=' ->
                let name = cur.Substring(0, cur.IndexOf '=')

                match sigSurfaceAt si key |> Option.bind (fun sf -> flagArity si sf name) with
                | Some(ANone, _) -> DynStatic
                | _ -> DynValue
            | InCase _ when isFlag cur -> DynStatic
            | InCase key when sigHasChildren si key -> DynStatic
            | InCase _ -> DynOperand

/// the sigs the REPL sees, by tool name at Tab time [D:sig-arg-complete]:
/// the nearest project .weir/sigs/ (resolved as #sig resolves it), then
/// the user dir. Loaded once per file version — path + mtime
module SigLookup =
    let private cache =
        Collections.Generic.Dictionary<string, DateTime * SigInfo option>()

    /// `$XDG_CONFIG_HOME/weir/sigs` (`~/.config/weir/sigs`; `%APPDATA%`
    /// on Windows) — `weir add sig --global` writes here
    let userDir () : string =
        IO.Path.Combine(Builtins.configDir (), "weir", "sigs")

    let private load (path: string) (tool: string) : SigInfo option =
        try
            let mtime = IO.File.GetLastWriteTimeUtc path
            let key = path + "\n" + tool

            lock cache (fun () ->
                match cache.TryGetValue key with
                | true, (m, si) when m = mtime -> si
                | _ ->
                    let si =
                        loadSigs (path + ".from") [
                            {
                                Tool = tool
                                Override = Some path
                                Line = 1
                            }
                        ]
                        |> snd
                        |> List.tryHead

                    cache[key] <- (mtime, si)
                    si)
        with _ ->
            None

    /// the signature for `tool` seen from `cwd`, the project's before
    /// `user`'s, if any loads
    let findIn (cwd: string) (user: string) (tool: string) : SigInfo option =
        let project =
            match
                resolveSigFile cwd {
                    Tool = tool
                    Override = None
                    Line = 1
                }
            with
            | Ok p when IO.File.Exists p -> load p tool
            | _ -> None

        match project with
        | Some si -> Some si
        | None ->
            let file = IO.Path.Combine(user, sigFileName tool)
            if IO.File.Exists file then load file tool else None

    let find (cwd: string) (tool: string) : SigInfo option = findIn cwd (userDir ()) tool

/// the mechanism a sig's `let dynamic` is granted [D:sig-dynamic-complete]
type DynMechanism =
    | DynOff
    | DynGit
    | DynCobra
    /// a project sig asking to run its tool: ignored, with a hint
    | DynRefused

/// "git" runs only weir's own fixed git commands, so any sig has it;
/// "cobra" runs the tool, so only a sig from the user dir (`add sig
/// --global`) has it — a project's loads unasked
let dynamicFor (userDir: string) (si: SigInfo) : DynMechanism =
    match si.Dynamic with
    | "git" -> DynGit
    | "cobra" ->
        let norm (p: string) =
            IO.Path.TrimEndingDirectorySeparator(IO.Path.GetFullPath p)

        let cmp =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        let fromUser =
            try
                String.Equals(norm (IO.Path.GetDirectoryName(IO.Path.GetFullPath si.SigPath)), norm userDir, cmp)
            with _ ->
                false

        if fromUser then DynCobra else DynRefused
    | _ -> DynOff

/// the hint a refused cobra sig gets, once per session per tool
let dynamicHint (tool: string) : string =
    let t = Eval.sanitizeTtyEcho tool
    $"dynamic completion for {t} needs a user-level sig: weir add sig --global {t}"

// the tool's own values for the word at the end of `argText` (the text
// after the head, alias-expanded) [D:sig-dynamic-complete]: None defers to
// layer 1. An answer is the tool's untrusted text: offered only as a bare
// argv word (Complete.bareArgvSafe, never needing quotes) — anything else
// is dropped, as layer 1 drops a spelling it cannot offer bare
let private dynamicAnswer
    (hooks: DynComplete.Hooks)
    (userDir: string)
    (si: SigInfo)
    (tool: string)
    (argText: string)
    (word: string)
    : string list option =
    let slot = SigComplete.dynamicSlot si argText
    let before, cur = SigComplete.split argText

    // a word weir would read as more than its text (a quote, a splice, a
    // subexpression) is not what the tool would see: no ask
    let plain (w: string) =
        w |> Seq.forall (fun c -> not (Char.IsControl c || "\"'`$()\\".IndexOf c >= 0))

    let offer (outcome: DynComplete.Outcome) (valuesOnly: bool) =
        match outcome with
        | DynComplete.Interrupted -> Some []
        | DynComplete.Fallback -> None
        | DynComplete.Answer(cands, noFiles) ->
            // cobra answers `--flag=` with the values alone
            let lead =
                if valuesOnly then
                    cur.Substring(0, cur.IndexOf '=' + 1)
                else
                    ""

            let cut = cur.Length - word.Length

            let fits =
                cands
                |> List.map (fun c -> lead + c)
                |> List.filter (fun c -> c.StartsWith cur && Complete.bareArgvSafe c)
                |> List.map (fun c -> c.Substring cut)
                |> List.distinct

            match fits with
            | [] when noFiles -> Some []
            | [] -> None
            | fs -> Some fs

    if
        slot = DynStatic
        || not (cur.EndsWith word)
        || not (List.forall plain (cur :: before))
    then
        None
    else
        match dynamicFor userDir si with
        | DynOff -> None
        | DynRefused ->
            DynComplete.hintOnce tool (fun () -> hooks.Note(dynamicHint tool))
            None
        | DynGit ->
            match slot, DynComplete.gitWant before with
            | DynOperand, Some want ->
                offer
                    (hooks.Run {
                        Mechanism = "git"
                        Exe = "git"
                        Args = DynComplete.gitArgs want
                    })
                    false
            | _ -> None
        | DynCobra ->
            offer
                (hooks.Run {
                    Mechanism = "cobra"
                    Exe = tool
                    Args = before @ [ cur ]
                })
                (cur.StartsWith "-" && cur.Contains '=')

/// a command argv word the sig of its head completes: Some offers, Some
/// [] for "nothing", None when the line machinery decides (no sig, a
/// path the sig defers to, a splice, a quoted word). With `hooks` (the
/// REPL), an operand or a flag's value the sig answers with paths or
/// nothing asks the tool first when the sig names a mechanism
/// [D:sig-dynamic-complete]
let sigArgvCompleteWith
    (hooks: DynComplete.Hooks option)
    (userDir: string)
    (aliases: Map<string, string * string list>)
    (env: TypeEnv)
    (text: string)
    (wordStart: int)
    : string list option =
    let before = text.Substring(0, min wordStart text.Length)
    let word = text.Substring(min wordStart text.Length)

    let argvSlot =
        (before = ""
         || Char.IsWhiteSpace before[before.Length - 1]
         || before[before.Length - 1] = '=')
        && not (Weir.Scan.endsInsideString before)

    // a `/` word is a path to layer 1; the tool may still know it
    // (`feature/x`, `pod/web`) — a `~` word is a home path
    let plainSlot = argvSlot && word.IndexOf '/' < 0 && not (word.StartsWith "~")

    if not argvSlot || word.StartsWith "~" then
        None
    else
        match Complete.commandArgvAt env before with
        | None -> None
        | Some(head, argText) ->
            // a session alias expands first, single-hop like the resolver
            // [D:command-head-alias]: its exe's sig, its prefix words as
            // typed; `^` skips the alias table
            let tool, argText =
                match Map.tryFind head aliases with
                | Some(exe, prefix) when not (head.StartsWith "^") ->
                    exe,
                    (if prefix.IsEmpty then
                         argText
                     else
                         " " + String.concat " " prefix + argText)
                | _ -> head.TrimStart '^', argText

            match SigLookup.findIn (Session.resolve ".") userDir tool with
            | None -> None
            | Some si ->
                let layer1 =
                    if plainSlot then
                        SigComplete.complete si (argText + word) word
                    else
                        SigPaths

                let static' =
                    match layer1 with
                    | SigWords ws -> Some ws
                    | SigNothing -> Some []
                    | SigPaths -> None

                match hooks, layer1 with
                | Some h, (SigPaths | SigNothing) ->
                    match dynamicAnswer h userDir si tool (argText + word) word with
                    | Some offers -> Some offers
                    | None -> static'
                | _ -> static'

let sigArgvComplete aliases env text wordStart =
    sigArgvCompleteWith None (SigLookup.userDir ()) aliases env text wordStart

/// the REPL's Tab over its entry [D:entry-complete]: `entry` is the
/// entry up to the cursor (dedented, its last line cut there), `text`
/// the cursor's own line up to the cursor and `wordStart` within it,
/// `aliases` the session's (name -> exe, prefix). A
/// repaired `head.`/`_.` word answers first (the LSP's precedence), the
/// sig of a command's head next (with `hooks`, the tool's own values
/// [D:sig-dynamic-complete]), the line machinery under the entry's env
/// otherwise
let suggestEntryWith
    (hooks: DynComplete.Hooks option)
    (aliases: Map<string, string * string list>)
    (values: Map<string, Eval.Value>)
    (env: TypeEnv)
    (entry: string list)
    (text: string)
    (wordStart: int)
    : string list =
    let ctxEnv, stmt = entryContext env entry
    let word = text.Substring(min wordStart text.Length)

    let ctxEnv =
        match stmt with
        | Some s when s.EndsWith word -> withSpliceLocals ctxEnv s word
        | _ -> ctxEnv

    let untilStopped =
        match List.rev entry with
        | last :: above when last.Length >= word.Length ->
            Complete.untilStoppedAt (List.rev above) (last.Substring(0, last.Length - word.Length))
        | _ -> false

    // a type position across the lines of a `type` declaration
    // [D:type-name-complete]
    let typePosition =
        match stmt with
        | Some s when s.EndsWith word -> Complete.typePositionAt (s.Substring(0, s.Length - word.Length))
        | _ -> false

    if untilStopped then
        [ "stopped" ] |> List.filter (fun w -> w.StartsWith word && w <> word)
    elif typePosition then
        Complete.typeNameOffers ctxEnv word
    else

        match stmt |> Option.bind (fun s -> repairedFields ctxEnv s "") with
        | Some(_ :: _ as fields) -> fields
        | _ ->
            // a command argv word under a sig'd head [D:sig-arg-complete]
            match sigArgvCompleteWith hooks (SigLookup.userDir ()) aliases ctxEnv text wordStart with
            | Some offers -> offers
            | None -> Complete.suggestSession (aliases |> Map.keys |> Set.ofSeq) values ctxEnv text wordStart

let suggestEntry aliases values env entry text wordStart =
    suggestEntryWith None aliases values env entry text wordStart
