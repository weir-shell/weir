module Weir.Script

open System
open Weir.Ast
open Weir.Types

// The quote-aware scanner, the single string-state primitive
// [D:one-scanner]. Folds f over the characters outside string
// literals: double quotes honor backslash escapes, single quotes close
// at the next single quote.
// The outside-string fold and the end-state question share this one
// machine [D:one-scanner] — a second inline quote machine is a review
// flag. stringScan returns the fold result and whether the line ends
// inside a string.
let private stringScan (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a * bool =
    let mutable st = init
    let mutable i = 0
    let mutable inDouble = false
    let mutable inSingle = false
    // raw kinds [D:raw-strings]: verbatim @"..." ("" = one quote, no
    // escapes) and triple-quoted """...""" (no escapes at all,
    // closes at the first triple [D:raw-strings])
    let mutable inVerbatim = false
    let mutable inTriple = false

    while i < s.Length do
        let c = s[i]

        if inDouble then
            if c = '\\' && i + 1 < s.Length then
                i <- i + 1
            elif c = '"' then
                inDouble <- false
        elif inSingle then
            if c = '\'' then
                inSingle <- false
        elif inVerbatim then
            if c = '"' then
                if i + 1 < s.Length && s[i + 1] = '"' then
                    i <- i + 1
                else
                    inVerbatim <- false
        elif inTriple then
            if c = '"' && i + 2 < s.Length && s[i + 1] = '"' && s[i + 2] = '"' then
                i <- i + 2
                inTriple <- false
        elif c = '@' && i + 1 < s.Length && s[i + 1] = '"' then
            inVerbatim <- true
            i <- i + 1
        elif c = '"' && i + 2 < s.Length && s[i + 1] = '"' && s[i + 2] = '"' then
            inTriple <- true
            i <- i + 2
        elif c = '"' then
            inDouble <- true
        elif c = '\'' then
            inSingle <- true
        else
            st <- f st i c

        i <- i + 1

    st, (inDouble || inSingle || inVerbatim || inTriple)

let private foldOutsideStrings (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a = fst (stringScan f init s)

// All four weir string kinds are single-line, so a line ending inside
// one can never be completed by more input — the multiline REPL submits
// such a buffer instead of growing it [D:repl-multiline]
let endsInsideString (s: string) : bool =
    snd (stringScan (fun () _ _ -> ()) () s)

let stripComment (line: string) : string =
    // comment only at line start or after whitespace: bareword URLs
    // (https://...) live in command lines (nuget-script receipt)
    let cut =
        foldOutsideStrings
            (fun cut i c ->
                if
                    cut < 0
                    && c = '/'
                    && i + 1 < line.Length
                    && line[i + 1] = '/'
                    && (i = 0 || System.Char.IsWhiteSpace line[i - 1])
                then
                    i
                else
                    cut)
            -1
            line

    // TrimEnd only when a comment was cut [D:trailing-comments]: the
    // code before a comment never needs its gap (district heads match
    // EndsWith), and an untouched line stays byte-equal
    if cut >= 0 then line.Substring(0, cut).TrimEnd() else line

// ---- `///` doc comments [D:doc-comments] -------------------------
// Docs are out-of-band metadata about a source location, never part of
// the program's meaning: Value/Eval/Check never see one, so runtime is
// byte-identical by construction (nothing to erase, nothing to pin).
// The key is the physical (line, col, len) of the documented name,
// never the name itself (shadowing / inner lets / duplicate field
// names). Hover and completion look attachments up by that position.

/// The doc lines attached to one declaration, with the physical
/// (1-based line, 1-based col, length) of the name they document.
type DocAttach =
    { Line: int
      Col: int
      Len: int
      Doc: string list }

let private isDocLine (raw: string) : bool = raw.TrimStart().StartsWith "///"

/// the text of a `///` line: after the three slashes, one optional
/// leading space consumed (so `/// x` and `///x` both yield "x")
let private docText (raw: string) : string =
    let t = raw.TrimStart().Substring 3
    if t.StartsWith " " then t.Substring 1 else t

/// the documented name's (1-based col, len) on a declaration line: the
/// identifier after `let`/`type`, after `|` (union case), or leading
/// (a record field). None when the line has no such name.
let private declName (raw: string) : (int * int) option =
    let code = stripComment raw
    let trimmed = code.TrimStart()
    let indent = code.Length - trimmed.Length

    let identAt (from: int) : (int * int) option =
        let mutable i = from

        while i < code.Length && (code[i] = ' ' || code[i] = '\t') do
            i <- i + 1

        if i < code.Length && (System.Char.IsLetter code[i] || code[i] = '_') then
            let s = i

            while i < code.Length && (System.Char.IsLetterOrDigit code[i] || code[i] = '_') do
                i <- i + 1

            Some(s + 1, i - s)
        else
            None

    if trimmed.StartsWith "let " then identAt (indent + 3)
    elif trimmed.StartsWith "type " then identAt (indent + 4)
    elif trimmed.StartsWith "|" then identAt (indent + 1)
    elif trimmed = "" then None
    else identAt indent

// In-string mask over a line — true where a char sits inside any
// string kind (plain/single/verbatim/triple). The scanner family's
// third consumer [D:fmt-respace]: respacing must never touch
// string interiors.
let inStringMask (s: string) : bool[] =
    let mask = Array.create s.Length false
    let mutable outside = System.Collections.Generic.HashSet<int>()

    foldOutsideStrings
        (fun () i _ ->
            outside.Add i |> ignore
            ())
        ()
        s

    for i in 0 .. s.Length - 1 do
        mask[i] <- not (outside.Contains i)

    mask

// Canonical intra-line spacing [D:fmt-respace], bounded: collapse
// space runs, pad record braces, tidy `;`. String interiors and
// leading indent untouched. Fmt applies this under a parse-shape
// safety check — any statement whose sexpr changes reverts — so a
// rule misfiring on a command line (argv `{x}`, literal `;`) can
// never change meaning, only be skipped.
let respaceLine (line: string) : string =
    let mask = inStringMask line
    let indent = line |> Seq.takeWhile ((=) ' ') |> Seq.length
    let sb = System.Text.StringBuilder()

    let lastEmitted () =
        if sb.Length = 0 then ' ' else sb[sb.Length - 1]

    let mutable i = 0

    while i < line.Length do
        let c = line[i]

        if i < indent || mask[i] then
            sb.Append c |> ignore
        else
            match c with
            | ' ' when lastEmitted () = ' ' -> () // collapse runs
            | '{' when
                i + 1 < line.Length
                && line[i + 1] <> ' '
                && line[i + 1] <> '{'
                && lastEmitted () <> '{'
                ->
                sb.Append "{ " |> ignore
            | '}' when lastEmitted () <> ' ' -> sb.Append " }" |> ignore
            | ';' ->
                // no space before, one after
                while sb.Length > indent && lastEmitted () = ' ' do
                    sb.Remove(sb.Length - 1, 1) |> ignore

                sb.Append ';' |> ignore

                if i + 1 < line.Length && line[i + 1] <> ' ' then
                    sb.Append ' ' |> ignore
            | c -> sb.Append c |> ignore

        i <- i + 1

    sb.ToString()

// net { vs } outside strings — interpolation holes sit inside the
// quotes, so their braces never count; clamping to >= 0 happens at the
// consumer (stray command-arg } must not poison the statement)
let private braceDelta (s: string) : int =
    foldOutsideStrings
        (fun d _ c ->
            (if c = '{' then d + 1
             elif c = '}' then d - 1
             else d))
        0
        s

// --- the line classifier: one derivation, three consumers -----------
// (assembler fold, fmt's block logic, the oracle's weirVerdict mirror
// — sharing the derivation is what makes them agree)

/// Whole-line classification, pre-assembly: the statement filter.
[<RequireQualifiedAccess>]
type LineKind =
    | Blank
    | CommentOnly
    | Code

let classifyLine (raw: string) : LineKind =
    if raw.Trim() = "" then
        LineKind.Blank
    elif (stripComment raw).Trim() = "" then
        LineKind.CommentOnly
    else
        LineKind.Code

/// a col-0 `else`/`elif` line continues its open `if` [D:toplevel-if-else]
/// — the top-level block form (`if c then <block>` then a dedented
/// `else`/`elif <block>`). `else`/`elif` are keywords, so a line whose
/// leading word is one can only continue an open if, never head a fresh
/// statement; checked on the trimmed text so an identifier such as
/// `elsewhere` is untouched. Hoisted out of the assembler's col-0 gate so
/// that giant function carries no inline `let … in`.
let continuesOpenIf (raw: string) : bool =
    let lw = raw.TrimEnd()
    lw = "else" || lw.StartsWith "else " || lw.StartsWith "elif "

/// Piece classification, inside assembly: the join/structure decisions.
/// Kind is exclusive; Marker and OpensCompound are orthogonal fields —
/// `let d = yaml` is a let head and also arms the yaml district.
[<RequireQualifiedAccess>]
type PieceKind =
    | PipeHead
    | ElseHead
    | LetHead
    | Plain

/// Line-end district markers: `yaml` (with an optional schema= suffix)
/// is the only surviving marker — the `!`/`!name` command districts
/// were retired [D:district-retirement].
[<RequireQualifiedAccess>]
type MarkerKind =
    | NoMarker
    // line-end `yaml` opens a yaml district [D:yaml-district] — except
    // `to yaml` / `from yaml`, which are the boundary adapters.
    // The `!` and `!ev` districts are retired [D:district-retirement]:
    // the arming rule made their mode gate unnecessary and `within env`
    // covers the overlay; the $e()/!e() sigil forms stay (fragment and
    // single-command uses have no block spelling).
    | Yaml
    // line-end `<<<` / `$<<<` opens a heredoc district [D:text-block] —
    // the yaml district's sibling; identical join semantics (verbatim
    // lines, relative indentation behind the sentinel)
    | Heredoc

type PieceClass =
    { Kind: PieceKind
      Marker: MarkerKind
      OpensCompound: bool
      IsBangSigil: bool
      ClosesBrace: bool
      ClosesParen: bool
      StartsField: bool
      StartsTypeField: bool
      BraceDelta: int }

let private isIdentToken (t: string) =
    t.Length > 0
    && (System.Char.IsLetter t[0] || t[0] = '_')
    && t |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

/// the marker predicate lives in Parser (shared with completion);
/// this alias keeps Script's callers and pins stable
let isYamlMarkerPiece = Parser.isYamlMarkerPiece
let isHeredocMarkerPiece = Parser.isHeredocMarkerPiece

/// the arm arrow `->` at bracket-depth 0, outside strings
/// [D:match-pipe-offside] — the match arm's body offside. Patterns carry
/// no arrow and a guard lambda's arrow nests in parens, so the first such
/// `->` is the arm's; a string literal in the pattern is skipped.
let armArrowIndex (s: string) : int option =
    let mutable depth = 0
    let mutable inStr = false
    let mutable i = 0
    let mutable found = -1

    while found < 0 && i < s.Length - 1 do
        let c = s[i]

        if inStr then
            if c = '\\' then i <- i + 1
            elif c = '"' then inStr <- false
        else
            match c with
            | '"' -> inStr <- true
            | '(' | '[' | '{' -> depth <- depth + 1
            | ')' | ']' | '}' -> depth <- depth - 1
            | '-' when depth = 0 && s[i + 1] = '>' -> found <- i
            | _ -> ()

        i <- i + 1

    if found >= 0 then Some found else None

let classifyPiece (piece: string) : PieceClass =
    let lastToken =
        match piece.LastIndexOf ' ' with
        | -1 -> piece
        | i -> piece.Substring(i + 1)

    { Kind =
        if piece.StartsWith "|" then
            PieceKind.PipeHead
        elif
            piece = "else"
            || piece.StartsWith "else "
            // elif extends a compound exactly as else does [D:elif]
            || piece = "elif"
            || piece.StartsWith "elif "
            // `until` extends retry/poll the same way [D:retry-poll]
            || piece = "until"
            || piece.StartsWith "until "
            // `always` extends a bare within the same way [D:within-always]
            || piece = "always"
        then
            PieceKind.ElseHead
        elif piece.StartsWith "let " then
            PieceKind.LetHead
        else
            PieceKind.Plain
      Marker =
        if isYamlMarkerPiece piece then MarkerKind.Yaml
        elif isHeredocMarkerPiece piece then MarkerKind.Heredoc
        else MarkerKind.NoMarker
      OpensCompound =
        // within/for block heads close-and-wrap exactly like the
        // conditionals [D:dedent-join] — same machine, two more
        // members, not a fifth alignment stack (let-prefixed forms
        // stay Lets-owned, the if/match convention)
        piece.StartsWith "if "
        || piece.StartsWith "match "
        || piece.StartsWith "within "
        || piece.StartsWith "for "
        // the bounded-loop pair [D:retry-poll] — the machine's 5th and
        // 6th members, still no stack
        || piece.StartsWith "retry "
        || piece.StartsWith "poll "
      IsBangSigil =
        piece.StartsWith "!("
        || (piece.StartsWith "!"
            && (match piece.IndexOf '(' with
                | i when i > 1 -> isIdentToken (piece.Substring(1, i - 1))
                | _ -> false))
      ClosesBrace = piece.StartsWith "}"
      ClosesParen = piece.StartsWith ")"
      StartsField =
        (match piece.IndexOf '=' with
         | i when i > 0 -> isIdentToken (piece.Substring(0, i).TrimEnd())
         | _ -> false)
      // type-declaration fields carry `:` not `=`; attribute lines
      // start their field [D:multiline-brackets]
      StartsTypeField =
        piece.StartsWith "[<"
        || (match piece.IndexOf ':' with
            | i when i > 0 -> isIdentToken (piece.Substring(0, i).TrimEnd())
            | _ -> false)
      BraceDelta = braceDelta piece }

/// The multiline-lambda opener [D:multiline-lambda]: a line ends with
/// `->` while its innermost unclosed paren opens a `fun` — `(fun r ->`
/// dangling at EOL opens a body block closed by its own `)`. A same-line
/// `(fun r -> body)` never arms (its parens balance at EOL).
let lambdaOpens (piece: string) : bool =
    let trimmed = piece.TrimEnd()

    if not (trimmed.EndsWith "->") then
        false
    else
        let opens =
            foldOutsideStrings
                (fun stack i c ->
                    match c with
                    | '(' -> i :: stack
                    | ')' ->
                        (match stack with
                         | _ :: t -> t
                         | [] -> [])
                    | _ -> stack)
                []
                trimmed

        match opens with
        | top :: _ -> trimmed.Substring(top + 1).TrimStart().StartsWith "fun "
        | [] -> false

/// A piece that dangles a block open at EOL: the next deeper line
/// starts a statement of that block (the lambda restore level rides
/// this) [D:multiline-lambda].
let dangleEnders = [| "="; "then"; "else"; "with"; "->" |]

// a within head [D:within-scopes]: `within <kind> <args…>` (optionally
// behind `let <name> =`) opens its block — the head ends with arbitrary
// argument words, so the classifier keys on the keyword, as the yaml
// marker does (a lexical rule shared by assembler and REPL, never a parse)
let isWithinHead (piece: string) : bool =
    let t = piece.Trim()

    let afterLet =
        if t.StartsWith "let " then
            match t.IndexOf '=' with
            | -1 -> t
            | i -> t.Substring(i + 1).TrimStart()
        else
            t

    afterLet = "within"
    || afterLet.StartsWith "within " && not (afterLet.Contains ";")

// a standalone district head [D:pure-stage1]: a bare district keyword
// (or one behind `let <name> =`) opens its block exactly as a within
// head does — the same lexical rule, one word shorter (no kind, no args)
let private isDistrictHead (keyword: string) (piece: string) : bool =
    let t = piece.Trim()

    let afterLet =
        if t.StartsWith "let " then
            match t.IndexOf '=' with
            | -1 -> t
            | i -> t.Substring(i + 1).TrimStart()
        else
            t

    afterLet = keyword
    || afterLet.StartsWith(keyword + " ") && not (afterLet.Contains ";")

let isPureHead (piece: string) : bool = isDistrictHead "pure" piece

// the standalone readonly head [D:desugar-namespace]: readonly opens a
// statement block exactly as pure does — a body may sequence unit
// statements before its result value, so it needs the sibling sentinel
// between them (treating the body as value-shaped space-joined a
// multi-statement body and mis-parsed it)
let isReadonlyHead (piece: string) : bool = isDistrictHead "readonly" piece

// the standalone plan head [D:plan-apply]: `plan` (bare, or behind
// `let <name> =`) opens its block exactly as pure/readonly do — a plan
// body is a statement sequence (bare consecutive mutations, captured as
// Ops), so it needs the sibling sentinel between its statements
let isPlanHead (piece: string) : bool = isDistrictHead "plan" piece

// the binder-head scanner [D:scoped-procs] [D:http-serve]: a `within
// proc <b> =` / `within serve <b> =` head whose tail is a resource spec
// (a command for proc, `{config} handler` atoms for serve), so the
// first block statement must join at the machine boundary too — a space
// join would feed it to the tail (proc: the command's argv; serve: the
// handler application). This scan is `;`-blind (it finds the marker
// outside strings and checks only the binder-`=` shape), so an inline
// record `;` in serve's config tail does not hide the head — the reason
// serve routes through here and not isWithinHead's `;`-guarded path.
// The test takes an already-extracted last segment
// [D:assemble-quadratic]: the whole-text `LastIndexOf sibSep` slice moved
// to the caller so the assembler can hand its incrementally-tracked last
// segment (short) instead of re-slicing the whole growing text per join.
// A fast reject — Contains is allocation-free and vectorized — skips the
// per-char foldOutsideStrings scan on the vast majority of segments,
// which carry no marker at all.
let private endsInBinderHeadSeg (marker: string) (lastSeg: string) : bool =
    if not (lastSeg.Contains marker) then
        false
    else

    // the head may sit mid-segment (behind a let, a lambda arrow, a
    // pipe) — find its last occurrence outside strings (the one
    // scanner), then require the binder-`=` shape; the tail then runs to
    // the segment's end, which is the thing a space join would feed
    let lastStart =
        foldOutsideStrings
            (fun acc i _ ->
                if
                    i + marker.Length <= lastSeg.Length
                    && lastSeg.Substring(i, marker.Length) = marker
                    // word-bounded: an identifier merely ending in the
                    // letters must not trip it (the `do` rule's argument)
                    && (i = 0
                        || not (System.Char.IsLetterOrDigit lastSeg[i - 1] || lastSeg[i - 1] = '_'))
                then
                    i
                else
                    acc)
            -1
            lastSeg

    lastStart >= 0
    && (let rest = lastSeg.Substring(lastStart + marker.Length)

        match rest.IndexOf '=' with
        | -1 -> false
        | eq ->
            rest.Substring(0, eq).Trim()
            |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_'))

// the last segment of a full logical-line text (after the last sibling
// sentinel) — the whole-text callers (dangleOpensBlock on a short piece)
let private lastSegOf (text: string) : string =
    match text.LastIndexOf Parser.sibSep with
    | -1 -> text
    | i -> text.Substring(i + 1)

let private endsInBinderHead (marker: string) (text: string) : bool =
    endsInBinderHeadSeg marker (lastSegOf text)

let endsInProcHead (text: string) : bool = endsInBinderHead "within proc " text

// the serve head [D:http-serve]: same shape as proc's — a `within serve
// <b> =` binder head whose block joins sentineled — routed through the
// same `;`-blind scanner so an inline config record `{ a; b }` on the
// head line does not defeat the body attach
let endsInServeHead (text: string) : bool = endsInBinderHead "within serve " text

let dangleOpensBlock (piece: string) : bool =
    let t = piece.TrimEnd()

    dangleEnders |> Array.exists t.EndsWith
    // `do` needs a word boundary — `sudo` at EOL must not dangle a
    // block open [D:for-do] (the existing enders keep their exact
    // suffix behavior)
    || t = "do"
    || t.EndsWith " do"
    // a line-end `function` dangles its arm block open, the match-head
    // shape one word shorter [D:function-keyword]; word-bounded like
    // `do` (an identifier merely ending in the letters must not dangle)
    || t = "function"
    || t.EndsWith " function"
    || t.EndsWith "(function"
    || isWithinHead t
    // a serve head with an inline config `;` is `;`-blind here
    // [D:http-serve] — isWithinHead's `;`-guard would hide it
    || endsInServeHead t
    || isPureHead t
    // readonly opens a statement block too [D:desugar-namespace] — a
    // multi-statement body sentinels between its statements, like pure
    || isReadonlyHead t
    // the plan head opens a statement block [D:plan-apply]
    || isPlanHead t
    // retry/poll heads and the until binder line open their blocks
    // [D:retry-poll]
    || t = "retry"
    || t.StartsWith "retry "
    || t = "poll"
    || t.StartsWith "poll "
    || t.StartsWith "until "
    // the bare within's teardown segment dangles too [D:within-always]
    || t = "always"

/// A line-end district marker of any kind — the mask below and the
/// REPL share classifyPiece's marker rules through these predicates.
let isMarkerPiece (piece: string) =
    (classifyPiece piece).Marker <> MarkerKind.NoMarker

/// True for physical lines that are district content (deeper than an
/// arming marker line) [D:content-bytes]: the byte-preserving passes —
/// doc attachment, the doc-align lint, fmt's doc canonicalization —
/// must neither read nor move them; content is bytes.
let districtContentMask (lines: string list) : bool[] =
    let arr = List.toArray lines
    let mask = Array.create arr.Length false
    let mutable armed: int option = None // the marker line's indent

    for i in 0 .. arr.Length - 1 do
        let raw = arr[i]
        let code = stripComment raw
        let indent = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

        if raw.Trim() = "" then
            () // blank: transparent, stays armed, unmasked
        elif code.Trim() = "" then
            match armed with
            | Some m when indent > m -> mask[i] <- true
            | _ -> ()
        else
            match armed with
            | Some m when indent > m -> mask[i] <- true
            | _ ->
                armed <- None

                if isMarkerPiece (code.TrimStart()) then
                    armed <- Some indent

    mask

/// Pure pass: a contiguous run of `///` lines attaches to the
/// declaration on the next code line; a blank or a plain `//` line
/// breaks the run (the contiguity rule). An attribute-only line
/// (`[<...>]`) is transparent: the doc rides through to the
/// declaration below, so F#'s canonical doc-then-attribute order and
/// the attribute-then-doc order both attach.
let isAttributeOnlyLine (raw: string) =
    let t = (stripComment raw).Trim()
    t.StartsWith "[<" && t.EndsWith ">]"

let docAttachments (lines: string list) : DocAttach list =
    let mutable pending: string list = []
    let acc = System.Collections.Generic.List<DocAttach>()

    let masked = districtContentMask lines

    lines
    |> List.iteri (fun idx raw ->
        let ln = idx + 1

        if masked[idx] then
            () // district content is bytes — never doc syntax [D:content-bytes]
        elif isDocLine raw then
            // Cons, not `@ [x]`: a contiguous /// run of N lines appended
            // one at a time is O(N^2) (each append copies the whole list),
            // pinning a core on a heavily documented module. Cons is O(1);
            // the run is reversed once at the single consumer (Doc below).
            pending <- docText raw :: pending
        elif isAttributeOnlyLine raw then
            ()
        elif raw.Trim() = "" then
            pending <- []
        elif (stripComment raw).Trim() = "" then
            pending <- [] // a plain // comment-only line breaks contiguity
        else
            if not (List.isEmpty pending) then
                match declName raw with
                | Some(col, len) ->
                    acc.Add
                        { Line = ln
                          Col = col
                          Len = len
                          Doc = List.rev pending } // reverse the consed run
                | None -> ()

            pending <- [])

    List.ofSeq acc

/// The marker's district wrap: opener text and how many trailing
/// characters of the armed line the first district line strips.
/// the retired district spellings [D:district-retirement] — detected
/// so their removal error explains itself instead of dumping an
/// expecting-list (a documented feature's removal is the one case a
/// reader has a right to be confused about)
let retiredDistrictMarker (piece: string) : bool =
    let lastToken =
        match piece.LastIndexOf ' ' with
        | -1 -> piece
        | i -> piece.Substring(i + 1)

    piece = "!"
    || piece.EndsWith " !"
    || (lastToken.StartsWith "!"
        && not (lastToken.Contains "(")
        && isIdentToken (lastToken.Substring 1))

let private markerOpener (m: MarkerKind) : (string * int * bool) option =
    match m with
    | MarkerKind.NoMarker -> None

    // the yaml district keeps its marker word; lines join verbatim with
    // relative indentation behind the sentinel [D:yaml-district]
    | MarkerKind.Yaml -> Some("", 0, true)
    // the text district joins exactly as yaml does [D:text-block]
    | MarkerKind.Heredoc -> Some("", 0, true)

type LogicalLine =
    { Text: string
      Head: int
      Segments: (int * int * int) list }

// a one-physical-line LogicalLine — the REPL's and -e's spelling
let singleLine (text: string) : LogicalLine =
    { Text = text
      Head = 1
      Segments = [ (0, 1, 0) ] }

// Block lets — F# light syntax at the assembly layer, the same way F#'s own
// lexer implements it (token insertion at offside boundaries): a continuation
// line beginning with `let` opens a binding; the next line at the same
// indentation closes it by joining with " in " instead of " ", so the
// single-line grammar sees the explicit form. `|`-headed lines are inert to
// the stack only while it is empty (statement-level pipeline continuations
// and column-0 match arms — the two customers); with a binding pending they
// follow the plain indent rules, so a dedented arm inside a block is the
// same "needs a body" error F# gives the shape. Every pending let must be
// closed before the statement ends.
// Unbalanced ( and { closers for a text fragment — the completion
// repair path appends these so a mid-edit dangling line parses.
// Quote-aware via its own stack machine, not stringScan: interp `$"`
// holes reopen code land mid-string, which a flag-based scanner cannot
// model — the four string-kind close rules are re-implemented here by
// necessity (and stringScan reads `$"…"` as a plain string).
let closers (text: string) : string =
    // one stack of expected closers models the full nesting: brackets
    // in code, strings ('"' plain, '$' interp — closes with '"' but
    // '{' opens a hole back into code land), single quotes, holes.
    // Mid-edit dangling text closes correctly at any depth.
    let mutable stack: char list = []
    let mutable i = 0

    while i < text.Length do
        let c = text[i]

        match stack with
        | ('"' | '$') :: rest ->
            if c = '\\' && i + 1 < text.Length then
                i <- i + 1
            elif c = '"' then
                stack <- rest
            elif c = '{' && List.head stack = '$' then
                if i + 1 < text.Length && text[i + 1] = '{' then
                    i <- i + 1
                else
                    stack <- '}' :: stack
        // raw kinds [D:raw-strings]: 'V' verbatim ("" stays inside),
        // 'T' triple (closes only at """)
        | 'V' :: rest ->
            if c = '"' then
                if i + 1 < text.Length && text[i + 1] = '"' then
                    i <- i + 1
                else
                    stack <- rest
        | 'T' :: rest ->
            if c = '"' && i + 2 < text.Length && text[i + 1] = '"' && text[i + 2] = '"' then
                i <- i + 2
                stack <- rest
        | '\'' :: rest ->
            if c = '\'' then
                stack <- rest
        | _ ->
            if c = '"' && i + 2 < text.Length && text[i + 1] = '"' && text[i + 2] = '"' then
                stack <- 'T' :: stack
                i <- i + 2
            elif c = '"' then
                stack <-
                    (if i > 0 && text[i - 1] = '@' then 'V'
                     elif i > 0 && text[i - 1] = '$' then '$'
                     else '"')
                    :: stack
            elif c = '\'' then
                stack <- '\'' :: stack
            elif c = '(' then
                stack <- ')' :: stack
            elif c = '{' then
                stack <- '}' :: stack
            elif (c = ')' || c = '}') then
                match stack with
                | top :: rest when top = c -> stack <- rest
                | _ -> ()

        i <- i + 1

    stack
    |> List.map (fun c ->
        match c with
        | '$'
        | 'V' -> "\""
        | 'T' -> "\"\"\""
        | c -> string c)
    |> String.concat ""

// Still-open brackets (kind, column) after folding a line into the
// running stack — fmt aligns record fields at top+2 and list elements
// at top+1, under the first field/element either way
// (quote-aware via the scanner family; lives here per the rule).
let braceStack (prev: (char * int) list) (line: string) : (char * int) list =
    // rides the one scanner [D:one-scanner]
    foldOutsideStrings
        (fun stack i c ->
            match c with
            | '{'
            | '[' -> (c, i) :: stack
            | '}'
            | ']' ->
                (match stack with
                 | _ :: rest -> rest
                 | [] -> [])
            | _ -> stack)
        prev
        line

// Net ( / ) delta of a piece (scanner-aware) — compounds opened
// inside parens are pruned when the user's own closer buries them
// [D:compound-paren-prune]
let parenDelta (s: string) : int =
    foldOutsideStrings
        (fun d _ c ->
            (if c = '(' then d + 1
             elif c = ')' then d - 1
             else d))
        0
        s

// Fold a piece's brackets into the pending statement's open-bracket
// stack (kind, line, firstEntryCol) [D:multiline-brackets]. A bracket
// with content after its opener on the same line records that content's
// physical column as the sibling-entry anchor [D:field-alignment]; a
// dangling opener records None (the first continuation entry sets it).
// A mismatched closer is an error naming both sides; over-closing stays
// permissive (the parser owns that message). Parens are not tracked.
let bracketFold
    (lineNo: int)
    (indent: int)
    (stack: (char * int * int option) list)
    (piece: string)
    : Result<(char * int * int option) list, string> =
    foldOutsideStrings
        (fun acc i c ->
            match acc with
            | Error _ -> acc
            | Ok stack ->
                match c with
                | '{'
                | '[' ->
                    let entryCol =
                        // `{|` is one opener token [D:anon-literals] — the
                        // `|` is not entry content, and `|}` closes from
                        // the entry-content side too
                        let mutable j = i + 1

                        if c = '{' && j < piece.Length && piece[j] = '|' then
                            j <- j + 1

                        while j < piece.Length && piece[j] = ' ' do
                            j <- j + 1

                        // an update header's opener-line content is the
                        // source, not a field — the first continuation entry
                        // anchors instead [D:record-update]. Only a record
                        // brace can carry a `with`; and the check is
                        // index-based — no per-opener `piece.Substring(j)`
                        // allocation, so a bracket-heavy single line stays
                        // linear [D:assemble-quadratic].
                        let isWithHeader =
                            c = '{'
                            && (let mutable e = piece.Length - 1

                                while e >= j && piece[e] = ' ' do
                                    e <- e - 1
                                // does piece[j..e] end with the word "with"?
                                // (either the whole trimmed tail is "with", or
                                // it ends " with" — a space precedes it)
                                e >= j + 3
                                && piece[e] = 'h'
                                && piece[e - 1] = 't'
                                && piece[e - 2] = 'i'
                                && piece[e - 3] = 'w'
                                && (e - 3 = j || piece[e - 4] = ' '))

                        if j < piece.Length && piece[j] <> '}' && piece[j] <> ']' && not isWithHeader then
                            Some(indent + j)
                        else
                            None

                    Ok((c, lineNo, entryCol) :: stack)
                | '}'
                | ']' ->
                    let expected = if c = '}' then '{' else '['

                    (match stack with
                     | (o, _, _) :: rest when o = expected -> Ok rest
                     | (o, oline, _) :: _ -> Error $"line {lineNo}: '{c}' closes the '{o}' opened at line {oline}"
                     | [] -> Ok [])
                | _ -> Ok stack)
        (Ok stack)
        piece

// Pending-statement state. Compounds is the offside stack: each open
// if/match-headed piece as (headIndent, textStart). A sibling arriving
// at or left of a head closes that compound by paren-wrapping it — a
// balanced, line-structural unit — so same-level siblings sequence
// after the conditional while deeper lines still join into its body,
// where greedy `;` grouping is exactly right. `else` and `|` pieces
// extend a compound instead of closing it. BraceDepth > 0 puts the
// assembler in record-continuation mode: line breaks separate fields,
// every other joining rule is inert (records are expressions).

// The pending statement's growing text [D:assemble-quadratic]: a single
// StringBuilder mutated in place, so a continuation-line flood joins in
// amortized-linear time (the old `ll.Text + sep + piece` rebuilt the whole
// string per join → O(N²) on hundreds-of-KB inputs). The immutable
// LogicalLine.Text is materialized once at statement close (`bufToLL`);
// every join site threads a PendBuf instead. Span arithmetic is byte-
// identical: `joinedStart` derives from `Sb.Length` at the same point the
// old code read `ll.Text.Length`, and the separators are the same literals.
//
// The incremental indexes answer the assembler's hot queries in O(1)/
// O(suffix) instead of a full-text rescan per line:
//   LastNonWs   — index of the last non-whitespace char (or -1): the
//                 bracket-continuation `prev.TrimEnd()` predicates read it
//                 (last char, EndsWith, Length) without slicing.
//   LastSegStart — index just past the last sibling sentinel (or 0): the
//                 proc/serve binder-head scan runs over the last segment
//                 only, never the whole growing text (LastIndexOf was O(N)).
type private PendBuf =
    { Sb: System.Text.StringBuilder
      // the statement's first physical line (LogicalLine.Head)
      Head: int
      Segments: (int * int * int) list
      // index of the last non-whitespace char in Sb, or -1
      LastNonWs: int
      // index just past the last sibling sentinel (Parser.sibSep) in Sb, or 0
      LastSegStart: int }

type private District =
    { MarkerIndent: int
      MarkerLine: int
      Opener: string
      Strip: int
      Yaml: bool
      // the marker kind that armed this district — errors name its word
      Marker: MarkerKind
      Active: int option }

type private Pend =
    { Buf: PendBuf
      Lets: (int * int) list
      LastIndent: int
      // sibling pipe columns, innermost first [D:pipe-alignment]: a
      // consecutive `|` line must sit exactly on a group column; the
      // first pipe after a non-pipe line opens a group. Per group:
      // (groupCol, bodyCol, isForward) [D:match-pipe-offside]. isForward =
      // a `|>` group vs a bare `|` (match/union arm) group; bodyCol = the
      // arm body's column (after the arm `->`; for a dangling arm resolved
      // to the body line's indent when it arrives; MaxValue until then).
      // A forward `|>` landing on a bare arm group: at the `|` it closes
      // the match, at or under the body it extends the arm, left of the
      // body it is rejected.
      PipeGroups: (int * int * bool) list
      LastWasPipe: bool
      District: District option
      // (headIndent, textStart, parenDepthAtOpen) [D:compound-paren-prune]
      Compounds: (int * int * int) list
      ParenDepth: int
      // the indent where the current statement started (statement = a
      // sibling/`in` join, or the first line after a dangling head) —
      // the lambda pop restores to this level, so a block sibling after
      // the `)` sequences while a fold's init still applies
      // [D:multiline-lambda]
      StmtLevel: int
      PrevDangles: bool
      // open multiline lambdas (opening line, opener indent, paren depth
      // before the open, statement level to restore on pop), innermost
      // first [D:multiline-lambda] — popped by paren balance; the user's
      // `)` is the closer
      Lambdas: (int * int * int * int) list
      // still-open brackets (kind, opening line, sibling-entry column)
      // [D:multiline-brackets] [D:field-alignment]
      Brackets: (char * int * int option) list }

// The join algebra: every way a continuation line attaches to the
// pending statement, its inserted text in one place. joinedStart
// derives from the same strings, so span arithmetic cannot drift from
// the insertion.
type private Join =
    | JIn // let-close: text + " in " + piece
    | JSibling // bracket field/element separators [D:field-sep-sentinel]
    | JStmtSibling // statement-sibling sequencing [D:sibling-sentinel]:
    // " <sentinel> " — same width as " ; " so span arithmetic is
    // identical, but command mode stops at it (a user ';' does not)
    | JSpace // plain continuation: " "
    | JDistrictOpen of strip: int * opener: string // strip the armed marker, wrap
    | JDistrictSibling of opener: string // text + " ; " + opener + piece + ")"
    | JDistrictPipe // reopen the wrap: stem + " " + piece + ")"
    | JYamlLine of rel: int // sentinel + rel spaces + VERBATIM line [D:yaml-district]

// recompute LastNonWs / LastSegStart for the chars appended from `at`
// onward (the buffer below `at` is unchanged, so the old indexes carry
// unless the tail overwrote them — callers pass the smaller of the two)
let private scanTail (sb: System.Text.StringBuilder) (from: int) (seedNonWs: int) (seedSegStart: int) =
    let mutable nonWs = seedNonWs
    let mutable segStart = seedSegStart

    for i in from .. sb.Length - 1 do
        let c = sb[i]

        if c = Parser.sibSep then
            segStart <- i + 1

        if not (System.Char.IsWhiteSpace c) then
            nonWs <- i

    nonWs, segStart

// append `s` to the buffer, maintaining the incremental indexes over the
// newly-added region (the existing buffer is untouched, so its indexes stand)
let private bufAppend (b: PendBuf) (s: string) : PendBuf =
    let from = b.Sb.Length
    b.Sb.Append s |> ignore
    let nonWs, segStart = scanTail b.Sb from b.LastNonWs b.LastSegStart

    { b with
        LastNonWs = nonWs
        LastSegStart = segStart }

let private bufLen (b: PendBuf) : int = b.Sb.Length

// the TrimEnd'd length of the buffer (LastNonWs + 1), and whether the
// TrimEnd'd buffer ends with `suffix` — both answered from the tracked
// last-non-white index, so the bracket-continuation predicates cost
// O(suffix) instead of a full-text TrimEnd allocation per line
// [D:assemble-quadratic]
let private bufTrimEndLen (b: PendBuf) : int = b.LastNonWs + 1

let private bufTrimEndEndsWith (b: PendBuf) (suffix: string) : bool =
    let sb = b.Sb
    let last = b.LastNonWs
    let n = suffix.Length

    last + 1 >= n
    && (let mutable ok = true
        let mutable i = 0

        while ok && i < n do
            if sb[last - n + 1 + i] <> suffix[i] then
                ok <- false

            i <- i + 1

        ok)

// the last non-white char of the buffer (the caller guards emptiness via
// bufTrimEndLen > 0)
let private bufLastNonWsChar (b: PendBuf) : char = b.Sb[b.LastNonWs]

// does the buffer, from index `at`, start with `prefix`? (the within-tail
// wrap check, O(prefix) with no Substring allocation)
let private bufStartsWithAt (b: PendBuf) (at: int) (prefix: string) : bool =
    let sb = b.Sb

    at >= 0
    && sb.Length - at >= prefix.Length
    && (let mutable ok = true
        let mutable i = 0

        while ok && i < prefix.Length do
            if sb[at + i] <> prefix[i] then
                ok <- false

            i <- i + 1

        ok)

// does the buffer start with `prefix`? (O(prefix), no allocation) — the
// `type ` / `[<` head checks
let private bufStartsWith (b: PendBuf) (prefix: string) : bool =
    let sb = b.Sb

    sb.Length >= prefix.Length
    && (let mutable ok = true
        let mutable i = 0

        while ok && i < prefix.Length do
            if sb[i] <> prefix[i] then
                ok <- false

            i <- i + 1

        ok)

// the buffer's last segment (after the last sibling sentinel), O(segment)
// via the tracked LastSegStart — the proc/serve binder-head scan reads
// this instead of LastIndexOf over the whole growing text [D:assemble-quadratic]
let private bufLastSeg (b: PendBuf) : string =
    b.Sb.ToString(b.LastSegStart, b.Sb.Length - b.LastSegStart)

let private bufEndsInProcHead (b: PendBuf) : bool =
    endsInBinderHeadSeg "within proc " (bufLastSeg b)

let private bufEndsInServeHead (b: PendBuf) : bool =
    endsInBinderHeadSeg "within serve " (bufLastSeg b)

// materialize a LogicalLine from a finished pending buffer (segments are
// reversed at the boundary exactly as before)
let private bufToLL (b: PendBuf) : LogicalLine =
    { Text = b.Sb.ToString()
      Head = b.Head
      Segments = List.rev b.Segments }

// a fresh single-line buffer (the assembler's per-statement seed)
let private bufNew (text: string) (lineNo: int) (indent: int) : PendBuf =
    let sb = System.Text.StringBuilder(text)
    let nonWs, segStart = scanTail sb 0 -1 0

    { Sb = sb
      Head = lineNo
      Segments = [ (0, lineNo, indent) ]
      LastNonWs = nonWs
      LastSegStart = segStart }

let private applyJoin (j: Join) (b: PendBuf) (piece: string) (lineNo: int) (indent: int) : PendBuf =
    // joinedStart derives from bufLen at the same point the old code read
    // ll.Text.Length — byte-identical span arithmetic [D:assemble-quadratic]
    let joinedStart =
        match j with
        | JIn -> bufLen b + " in ".Length
        | JSibling -> bufLen b + (" " + Parser.fieldSepStr + " ").Length
        | JStmtSibling -> bufLen b + (" " + Parser.sibSepStr + " ").Length
        | JSpace -> bufLen b + " ".Length
        | JDistrictOpen(strip, opener) -> (bufLen b - strip) + opener.Length
        | JDistrictSibling opener -> bufLen b + (" ; " + opener).Length
        | JDistrictPipe -> (bufLen b - 1) + " ".Length
        | JYamlLine rel -> bufLen b + (Parser.sibSepStr + String(' ', rel)).Length

    // apply the text mutation (identical bytes to the old string algebra)
    let b =
        match j with
        | JIn -> bufAppend b (" in " + piece)
        | JSibling -> bufAppend b (" " + Parser.fieldSepStr + " " + piece)
        | JStmtSibling -> bufAppend b (" " + Parser.sibSepStr + " " + piece)
        | JSpace -> bufAppend b (" " + piece)
        | JDistrictOpen(strip, opener) ->
            // strip the armed marker's trailing chars, then wrap. This arm
            // fires once per district (the first content line), so the full
            // rescan-after-remove is not on any quadratic path.
            if strip > 0 then
                b.Sb.Remove(b.Sb.Length - strip, strip) |> ignore

            let nonWs, segStart = scanTail b.Sb 0 -1 0
            let b = { b with LastNonWs = nonWs; LastSegStart = segStart }
            bufAppend b (opener + piece + ")")
        | JDistrictSibling opener -> bufAppend b (" ; " + opener + piece + ")")
        | JDistrictPipe ->
            // only on a `|` continuation line inside a district — not a
            // quadratic path; rescan the (now one-shorter) buffer whole
            b.Sb.Remove(b.Sb.Length - 1, 1) |> ignore
            let nonWs, segStart = scanTail b.Sb 0 -1 0
            let b = { b with LastNonWs = nonWs; LastSegStart = segStart }
            bufAppend b (" " + piece + ")")
        | JYamlLine rel -> bufAppend b (Parser.sibSepStr + String(' ', rel) + piece)

    { b with Segments = (joinedStart, lineNo, indent) :: b.Segments }

let assemble (numbered: (int * string) list) : Result<LogicalLine list, string> =
    // trailing comments strip here, per physical line [D:trailing-comments]:
    // stripComment is the whitespace-preceded rule (glued // — http://a,
    // --format=a//b — stays data). Skipped: yaml district content (bytes)
    // and comment-only lines (their class carries transparency semantics
    // a blanked line would lose — blankSinceHead is the difference)
    let numbered =
        // the mask must see stripped heads (a commented district head
        // still opens its district), so the cut runs twice: once to
        // find the content regions, once — content excluded — for real.
        // TrimEnd only on actual cuts: untouched lines stay byte-equal
        let mask = districtContentMask (numbered |> List.map (snd >> stripComment))

        numbered
        |> List.mapi (fun i (n, raw) ->
            if (i < mask.Length && mask[i]) || (stripComment raw).Trim() = "" then
                n, raw
            else
                n, stripComment raw)

    // the retired ! districts get an explanatory error
    // [D:district-retirement] — checked up front over every non-content
    // line (yaml district bodies are bytes, never read:
    // districtContentMask)
    let retiredHit =
        let mask = districtContentMask (numbered |> List.map snd)

        numbered
        |> List.mapi (fun i (n, raw) -> i, n, raw)
        |> List.tryPick (fun (i, n, raw) ->
            if i < mask.Length && mask[i] then
                None
            else
                let t = (stripComment raw).TrimEnd()
                if t <> "" && retiredDistrictMarker t then Some n else None)

    match retiredHit with
    | Some n ->
        Error
            $"line {n}: a line-end '!' is not a district marker — commands are ordinary statements (drop the !); for an env overlay over a block, use `within env vars`"
    | None ->
        let noBody letLine =
            Error
                $"line {letLine}: this let needs a body — an expression at the same indentation must follow before the statement ends"

        let braceOpen (p: Pend) =
            match p.Brackets with
            | ('{', line, _) :: _ when bufStartsWith p.Buf "type " ->
                Error $"line {line}: this record type's {{ is still open when the statement ends — close the brace"
            | ('{', line, _) :: _ ->
                Error $"line {line}: this record literal's {{ is still open when the statement ends — close the brace"
            | (kind, line, _) :: _ ->
                Error $"line {line}: this list's {kind} is still open when the statement ends — close the bracket"
            | [] -> Error "unreachable: bracketOpen on an empty stack"

        let close (current: Pend option) acc =
            match current with
            | Some p when not p.Brackets.IsEmpty -> braceOpen p
            | Some { Lambdas = (oline, _, _, _) :: _ } ->
                Error $"line {oline}: this lambda's '(' is still open when the statement ends — close the paren"
            | Some { District = Some { Active = None
                                       MarkerLine = mLine
                                       Marker = m } } ->
                let what =
                    match m with
                    | MarkerKind.Heredoc -> "'<<<'"
                    | _ -> "'yaml'"

                Error $"line {mLine}: line-end {what} needs an indented block below it"
            | Some { Lets = (_, letLine) :: _ } -> noBody letLine
            | Some p -> Ok(bufToLL p.Buf :: acc)
            | None -> Ok acc

        let districtLineCheck lineNo (cls: PieceClass) =
            if cls.IsBangSigil then
                Error $"line {lineNo}: already inside a command block; drop the !(...)"
            elif cls.Kind = PieceKind.LetHead then
                Error $"line {lineNo}: district lines are commands; bind values outside the block"
            else
                Ok()

        // paren-wrap the compound starting at textStart; later segment
        // starts shift by the inserted "(" (remaining compounds all start
        // earlier — pops run deepest-first — so they never shift). The buffer
        // Insert/Append is byte-identical to the old Substring algebra; the
        // trailing ")" becomes the last non-white char [D:assemble-quadratic]
        let wrapFrom (b: PendBuf) (ts: int) : PendBuf =
            b.Sb.Insert(ts, "(") |> ignore
            b.Sb.Append ")" |> ignore

            { b with
                LastNonWs = b.Sb.Length - 1
                LastSegStart = (if b.LastSegStart >= ts then b.LastSegStart + 1 else b.LastSegStart)
                Segments =
                    b.Segments
                    |> List.map (fun (js, l, i) -> (if js >= ts then js + 1 else js), l, i) }

        let folded =
            numbered
            |> List.fold
                (fun state (lineNo, raw) ->
                    match state with
                    | Error e -> Error e
                    | Ok _ when
                        raw.Contains Parser.sibSep
                        || raw.Contains Parser.districtClose
                        || raw.Contains Parser.fieldSep
                        ->
                        // unproduceability [D:sibling-sentinel] [D:district-terminates]:
                        // the machine sibling token and the district-close
                        // terminator can never come from source — reject both
                        // at the one place text becomes logical lines
                        Error $"line {lineNo}: illegal control character in source"
                    | Ok(current, acc, blankSinceHead) ->
                        let inOpenBrace =
                            match current with
                            | Some p -> not p.Brackets.IsEmpty
                            | None -> false

                        // the col-0 rule suspends while a lambda's paren is
                        // open [D:multiline-lambda]: the closer (or the leak
                        // guard) owns those lines
                        let inOpenLambda =
                            match current with
                            | Some p -> not p.Lambdas.IsEmpty
                            | None -> false

                        if raw.Trim() = "" then
                            match current with
                            // inside an active yaml district a blank line is
                            // bytes [D:block-scalars] — a block scalar's
                            // content keeps it, so it rides as an empty
                            // verbatim line; the template parser skips blanks
                            // everywhere outside a block scalar's content
                            | Some({ District = Some { Active = Some _; Yaml = true } } as p) ->
                                Ok(
                                    Some
                                        { p with
                                            Buf = applyJoin (JYamlLine 0) p.Buf "" lineNo 0 },
                                    acc,
                                    blankSinceHead
                                )
                            // transparency is total while a statement pends
                            // [D:body-blanks] — the comment-line class, second
                            // member; the col-0 rule (plus EOF) is the sole
                            // statement boundary, so every error the blank
                            // boundary produced still fires at close
                            | Some p -> Ok(Some p, acc, blankSinceHead)
                            | None -> Ok(None, acc, true)
                        elif (stripComment raw).Trim() = "" then
                            // comment-only: transparent [D:comment-transparency] —
                            // except inside an active yaml district, where the
                            // line is bytes (`// x` in a block scalar is data)
                            match current with
                            | Some({ District = Some { Active = Some bse
                                                       Yaml = true
                                                       MarkerIndent = m } } as p) when
                                (let ind = raw |> Seq.takeWhile ((=) ' ') |> Seq.length in ind > m && ind >= bse)
                                ->
                                let ind = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                                Ok(
                                    Some
                                        { p with
                                            Buf = applyJoin (JYamlLine(ind - bse)) p.Buf (raw.Substring ind) lineNo ind },
                                    acc,
                                    blankSinceHead
                                )
                            | Some p -> Ok(Some p, acc, blankSinceHead)
                            | None -> Ok(None, acc, blankSinceHead)
                        elif
                            raw[0] = ' '
                            || raw[0] = '\t'
                            || raw[0] = '|'
                            // a col-0 `until` continues its retry/poll —
                            // matching the col-0 `|` arm [D:retry-poll]
                            || raw.StartsWith "until "
                            || raw.TrimEnd() = "until"
                            // a col-0 `always` continues its bare within
                            // [D:within-always]
                            || raw.TrimEnd() = "always"
                            // a col-0 `else`/`elif` continues its `if` — the
                            // top-level block form; the ElseHead join below
                            // already handles it, only this gate excluded a
                            // dedented else/elif [D:toplevel-if-else]
                            || continuesOpenIf raw
                            || inOpenBrace
                            || inOpenLambda
                        then
                            let wsRun = raw |> Seq.takeWhile (fun c -> c = ' ' || c = '\t') |> Seq.length
                            let indent = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                            // content is bytes: inside an active yaml district a
                            // tab after the (space) indentation is content — the
                            // structure-level rejection must not reach it
                            let inYamlContent =
                                match current with
                                | Some { District = Some { Active = Some _
                                                           Yaml = true
                                                           MarkerIndent = m } } -> indent > m
                                | _ -> false

                            if indent < wsRun && not inYamlContent then
                                Error $"line {lineNo}: tabs are not allowed in indentation"
                            else
                                match current with
                                | None ->
                                    if blankSinceHead then
                                        Error
                                            $"line {lineNo}: continuation after a blank line has no statement to continue"
                                    else
                                        Error $"line {lineNo}: continuation without a statement"
                                | Some p ->
                                    // structure decisions read the stripped text;
                                    // yaml-district joins carry the raw bytes
                                    let rawPiece = raw.Substring indent
                                    let piece = (stripComment raw).Substring indent
                                    let cls = classifyPiece piece

                                    let rec go (p: Pend) =
                                        match p.Brackets with
                                        // the statement-head guard [D:blank-in-brackets]:
                                        // keywords cannot be entries, so a col-0
                                        // let/type bounds a runaway unclosed bracket
                                        | (kind, bline, _) :: _ when
                                            indent = 0 && (piece.StartsWith "let " || piece.StartsWith "type ")
                                            ->
                                            Error
                                                $"line {lineNo}: statement at column 0 while the '{kind}' opened at line {bline} is still open — close the bracket"
                                        | (kind, _, entryCol) :: _ ->
                                            // bracket continuation: a line break after a
                                            // field/element is a separator. The predicates
                                            // below read the pending buffer's tracked
                                            // last-non-white index — no full-text TrimEnd per
                                            // line [D:assemble-quadratic]
                                            let isTypeDecl = bufStartsWith p.Buf "type "

                                            // the separator goes before an entry-start line,
                                            // never before a value continuation — a field's
                                            // value may open on the next line.
                                            // Lists have no entry marker: every line starts an
                                            // element unless the previous line dangles an
                                            // opener/separator/operator [D:multiline-brackets]
                                            let startsEntry =
                                                // a closer line ends its bracket, never
                                                // starts an entry (Stroustrup closers)
                                                // [D:multiline-brackets]
                                                if piece.StartsWith "]" || piece.StartsWith "}" then false
                                                elif kind = '[' then true
                                                elif isTypeDecl then cls.StartsTypeField
                                                else cls.StartsField

                                            let prevEndsWith (s: string) = bufTrimEndEndsWith p.Buf s

                                            let danglesOpen =
                                                prevEndsWith "{"
                                                // the anon opener is one token [D:anon-literals] —
                                                // named here because the operator clause below
                                                // excludes `type` lines, where a nested anon
                                                // type may dangle it too
                                                || prevEndsWith "{|"
                                                || prevEndsWith "["
                                                || prevEndsWith ";"
                                                // an update header ends at `with`; the
                                                // first field after it is not a sibling
                                                // [D:record-update]
                                                || prevEndsWith " with"
                                                // a preceding-line attribute binds to its
                                                // field: no separator between them
                                                || prevEndsWith ">]"
                                                // a dangling operator/comma continues the
                                                // same element (wrapped elements) — but not in
                                                // type declarations, where a generic closer
                                                // (`Option<string>`) legitimately ends a field
                                                || (not (kind = '{' && isTypeDecl)
                                                    && bufTrimEndLen p.Buf > 0
                                                    && "+-*/,(<>=|&" |> Seq.contains (bufLastNonWsChar p.Buf))

                                            let join = if startsEntry && not danglesOpen then JSibling else JSpace

                                            // sibling entries align exactly [D:field-alignment]:
                                            // the first entry (opener-line content, or the first
                                            // continuation entry of a dangling opener) sets the
                                            // column; every later entry must hit it
                                            // an attribute and its field are one entry on two
                                            // lines — the `>]` dangle suppresses the separator,
                                            // never the alignment [D:field-alignment]
                                            let attrField = startsEntry && prevEndsWith ">]"

                                            let alignment =
                                                if
                                                    join = JSibling || attrField || (startsEntry && entryCol.IsNone)
                                                then
                                                    match entryCol with
                                                    | Some c when indent <> c ->
                                                        Error
                                                            $"line {lineNo}: this field/element is indented off its siblings (they sit at column {c}) — align the group exactly"
                                                    | Some _ -> Ok p.Brackets
                                                    | None ->
                                                        // dangling opener: this entry anchors it
                                                        (match p.Brackets with
                                                         | (k, l, None) :: rest -> Ok((k, l, Some indent) :: rest)
                                                         | other -> Ok other)
                                                else
                                                    Ok p.Brackets

                                            match alignment with
                                            | Error e -> Error e
                                            | Ok anchored ->
                                                bracketFold lineNo indent anchored piece
                                                |> Result.map (fun brackets ->
                                                    Some
                                                        { p with
                                                            Buf = applyJoin join p.Buf piece lineNo indent
                                                            LastIndent = indent
                                                            Brackets = brackets },
                                                    acc,
                                                    blankSinceHead)
                                        | [] ->
                                            match p.District with
                                            | Some({ Active = None; Yaml = true } as dst) when
                                                indent > dst.MarkerIndent
                                                ->
                                                // the first yaml line fixes the block base; it rides
                                                // at relative indent 0 [D:yaml-district]
                                                Ok(
                                                    Some
                                                        { p with
                                                            Buf = applyJoin (JYamlLine 0) p.Buf rawPiece lineNo indent
                                                            LastIndent = indent
                                                            District = Some { dst with Active = Some indent } },
                                                    acc,
                                                    blankSinceHead
                                                )
                                            | Some({ Active = Some bse; Yaml = true } as dst) when
                                                indent > dst.MarkerIndent
                                                ->
                                                if indent < bse then
                                                    let noun =
                                                        match dst.Marker with
                                                        | MarkerKind.Heredoc -> "heredoc"
                                                        | _ -> "yaml"

                                                    Error
                                                        $"line {lineNo}: this {noun} line outdents below the block's first line"
                                                else
                                                    Ok(
                                                        Some
                                                            { p with
                                                                Buf =
                                                                    applyJoin
                                                                        (JYamlLine(indent - bse))
                                                                        p.Buf
                                                                        rawPiece
                                                                        lineNo
                                                                        indent
                                                                LastIndent = indent },
                                                        acc,
                                                        blankSinceHead
                                                    )
                                            | Some({ Active = None } as dst) when indent > dst.MarkerIndent ->
                                                districtLineCheck lineNo cls
                                                |> Result.map (fun () ->
                                                    Some
                                                        { p with
                                                            Buf =
                                                                applyJoin
                                                                    (JDistrictOpen(dst.Strip, dst.Opener))
                                                                    p.Buf
                                                                    piece
                                                                    lineNo
                                                                    indent
                                                            LastIndent = indent
                                                            District = Some { dst with Active = Some indent } },
                                                    acc,
                                                    blankSinceHead)
                                            | Some { Active = None
                                                     MarkerLine = mLine
                                                     Marker = m } ->
                                                let what =
                                                    match m with
                                                    | MarkerKind.Heredoc -> "'<<<'"
                                                    | _ -> "'yaml'"

                                                Error $"line {mLine}: line-end {what} needs an indented block below it"
                                            | Some({ Active = Some d } as dst) when indent > dst.MarkerIndent ->
                                                if cls.Kind = PieceKind.PipeHead then
                                                    Ok(
                                                        Some
                                                            { p with
                                                                Buf = applyJoin JDistrictPipe p.Buf piece lineNo indent
                                                                LastIndent = indent },
                                                        acc,
                                                        blankSinceHead
                                                    )
                                                elif indent = d then
                                                    districtLineCheck lineNo cls
                                                    |> Result.map (fun () ->
                                                        Some
                                                            { p with
                                                                Buf =
                                                                    applyJoin
                                                                        (JDistrictSibling dst.Opener)
                                                                        p.Buf
                                                                        piece
                                                                        lineNo
                                                                        indent
                                                                LastIndent = indent },
                                                        acc,
                                                        blankSinceHead)
                                                else
                                                    Error
                                                        $"line {lineNo}: district lines are commands, one per line (use a leading | to continue a pipeline)"
                                            | Some dst ->
                                                // at or left of the marker while the statement still
                                                // pends [D:district-terminates]: the district closes and
                                                // the line reprocesses under the normal rules — a
                                                // following `|>`, `in`, or sibling composes with the
                                                // block's value. The flattened logical line carries no
                                                // content terminator, so without a marker the
                                                // continuation would glue into the last content line
                                                // (silent corruption); appending districtClose marks
                                                // the extent, and districtTail stops there so the
                                                // expression grammar resumes on the reprocessed line.
                                                go
                                                    { p with
                                                        District = None
                                                        Buf = bufAppend p.Buf Parser.districtCloseStr
                                                        LastIndent = dst.MarkerIndent }
                                            // the multiline lambda's closer and leak guard
                                            // [D:multiline-lambda]: a `)`-headed line continues
                                            // the statement at any indent; any other line at or
                                            // left of the opener is a leak (the error names the
                                            // opener)
                                            | None when
                                                (match p.Lambdas with
                                                 | (_, oindent, _, _) :: _ -> cls.ClosesParen || indent < oindent
                                                 | [] -> false)
                                                ->
                                                let (oline, oindent, _, _) = List.head p.Lambdas

                                                if not cls.ClosesParen then
                                                    // F#-parity: FS0058 is an error left of the
                                                    // opener; at the opener's indent the line is a
                                                    // body continuation (handled below)
                                                    Error
                                                        $"line {lineNo}: this line sits left of the lambda '(' opened at line {oline} — close the paren first"
                                                else
                                                    // a body let still needs its body before the paren
                                                    match p.Lets |> List.tryFind (fun (k, _) -> k > oindent) with
                                                    | Some(_, letLine) -> noBody letLine
                                                    | None ->
                                                        let depth = p.ParenDepth + parenDelta piece

                                                        let popped, kept =
                                                            p.Lambdas
                                                            |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                        let kept =
                                                            if lambdaOpens piece then
                                                                (lineNo, indent, depth - 1, p.StmtLevel) :: kept
                                                            else
                                                                kept

                                                        // restore the level the popped lambda's own
                                                        // statement started at
                                                        let backTo =
                                                            match popped with
                                                            | [] -> indent
                                                            | ps ->
                                                                let (_, _, _, restore) = List.last ps
                                                                restore

                                                        bracketFold lineNo indent [] piece
                                                        |> Result.map (fun brackets ->
                                                            Some
                                                                { p with
                                                                    Buf =
                                                                        applyJoin
                                                                            (if
                                                                                 bufEndsInProcHead p.Buf
                                                                                 || bufEndsInServeHead p.Buf
                                                                             then
                                                                                 JStmtSibling
                                                                             else
                                                                                 JSpace)
                                                                            p.Buf
                                                                            piece
                                                                            lineNo
                                                                            indent
                                                                    LastIndent = backTo
                                                                    StmtLevel = backTo
                                                                    PrevDangles = dangleOpensBlock piece
                                                                    ParenDepth = depth
                                                                    Lambdas = kept
                                                                    Compounds =
                                                                        p.Compounds
                                                                        |> List.filter (fun (_, _, d) -> d <= depth)
                                                                    PipeGroups =
                                                                        p.PipeGroups
                                                                        |> List.skipWhile (fun (g, _, _) -> g > backTo)
                                                                    LastWasPipe = false
                                                                    Brackets = brackets },
                                                            acc,
                                                            blankSinceHead)
                                            | None ->
                                                if cls.Kind = PieceKind.PipeHead || cls.Kind = PieceKind.ElseHead then
                                                    // arms, pipeline stages, and else extend the
                                                    // current piece: no sibling `;` — but siblings
                                                    // must align, and a shallower arm offside-closes
                                                    // deeper compounds [D:pipe-alignment]
                                                    let isUntil = piece = "until" || piece.StartsWith "until "
                                                    let isAlways = piece = "always"
                                                    let isFwd = piece.StartsWith "|>"

                                                    match p.Lets with
                                                    | (k, letLine) :: _ when indent <= k && not isUntil ->
                                                        noBody letLine
                                                    | _ ->
                                                        // deeper groups close at this line's column
                                                        let groups =
                                                            p.PipeGroups |> List.skipWhile (fun (g, _, _) -> g > indent)

                                                        // the arm body's column [D:match-pipe-offside]: the
                                                        // token after the arm `->` for an inline body;
                                                        // MaxValue for a dangling arm (resolved to the body
                                                        // line's indent when it arrives) or a `|`-decl with
                                                        // no arrow
                                                        let bodyCol =
                                                            match armArrowIndex piece with
                                                            | Some i ->
                                                                let after = piece.Substring(i + 2)
                                                                let t = after.TrimStart()

                                                                if t = "" then
                                                                    System.Int32.MaxValue
                                                                else
                                                                    indent + (i + 2) + (after.Length - t.Length)
                                                            | None -> System.Int32.MaxValue

                                                        // `closes` rides through to the wrap; `extends`
                                                        // joins the arm body and opens a forward group
                                                        let aligned =
                                                            if cls.Kind = PieceKind.ElseHead then
                                                                // else/elif keep their standing rules
                                                                Ok(false, groups)
                                                            elif
                                                                // a forward `|>` landing on a bare `|`
                                                                // (arm/union) group follows F#'s offside
                                                                // [D:match-pipe-offside], regardless of
                                                                // whether the arm body was inline or dangled
                                                                isFwd
                                                                && (match groups with
                                                                    | (_, _, false) :: _ -> true
                                                                    | _ -> false)
                                                            then
                                                                match groups with
                                                                | (g, _, _) :: _ when indent = g ->
                                                                    // at the `|` column: close the match,
                                                                    // pipe the whole
                                                                    Ok(true, groups)
                                                                | (g, bc, _) :: _ when indent < bc ->
                                                                    // left of the arm body: neither closes
                                                                    // nor continues
                                                                    let bodyDesc =
                                                                        if bc = System.Int32.MaxValue then
                                                                            "the arm body"
                                                                        else
                                                                            $"the arm body (column {bc})"

                                                                    Error
                                                                        $"line {lineNo}: this pipe sits left of {bodyDesc} — put it at the '|' (column {g}) to pipe the whole match, or under the body to continue the arm"
                                                                | _ ->
                                                                    // at or under the body: extend the arm
                                                                    // body, opening a forward group
                                                                    Ok(false, (indent, indent, true) :: groups)
                                                            elif not p.LastWasPipe || p.PrevDangles then
                                                                // first pipe after a non-pipe line
                                                                // opens a group — anchored at or
                                                                // right of the innermost open
                                                                // compound head (F#'s offside).
                                                                // A dangling pipe line (ending
                                                                // with/->/function) opens one too:
                                                                // its arms sit deeper by design
                                                                // [D:function-keyword]
                                                                match groups with
                                                                | (g, _, false) :: rest when g = indent && not isFwd ->
                                                                    // a returning arm after a non-pipe
                                                                    // body [D:match-pipe-offside]: the
                                                                    // group at this exact column is the
                                                                    // arm's own match — a deeper open
                                                                    // compound (the previous arm's
                                                                    // if/let tail) offside-closes below,
                                                                    // it is not "its match"
                                                                    Ok(false, (g, bodyCol, false) :: rest)
                                                                | _ ->
                                                                    match p.Compounds with
                                                                    | (h, _, _) :: _ when indent < h ->
                                                                        Error
                                                                            $"line {lineNo}: this arm sits left of its match (head at column {h}) — align arms at or right of it"
                                                                    | _ ->
                                                                        Ok(
                                                                            false,
                                                                            (indent, (if isFwd then indent else bodyCol), isFwd)
                                                                            :: groups
                                                                        )
                                                            else
                                                                match groups with
                                                                | (g, _, false) :: rest when g = indent && not isFwd ->
                                                                    // a new arm aligns with its siblings:
                                                                    // reset the group's body column to this
                                                                    // arm's — each arm has its own body
                                                                    // offside [D:match-pipe-offside]
                                                                    Ok(false, (g, bodyCol, false) :: rest)
                                                                | (g, _, _) :: _ when g = indent ->
                                                                    // aligned sibling (pipeline stage)
                                                                    Ok(false, groups)
                                                                | (g, _, _) :: _ ->
                                                                    Error
                                                                        $"line {lineNo}: this line is indented off its siblings (they sit at column {g}) — align the group exactly"
                                                                | [] ->
                                                                    Error
                                                                        $"line {lineNo}: this line is indented off its siblings — align the group exactly"

                                                        match aligned with
                                                        | Error e -> Error e
                                                        | Ok(closes, groups) ->
                                                            // a shallower arm closes compounds whose
                                                            // heads sit deeper (the nested-match
                                                            // return F# reads from the columns); a
                                                            // closing `|>` also wraps the head it sits
                                                            // on — `(match …) |> f` [D:match-pipe-offside]
                                                            let closeAt = if closes then indent - 1 else indent

                                                            let rec closeDeeper (b: PendBuf) compounds =
                                                                match compounds with
                                                                | (h, ts, _) :: rest when
                                                                    h > closeAt
                                                                    // a within head has no arm at its
                                                                    // column [D:within-tail-pipe]: a pipe
                                                                    // at the head closes the scope and
                                                                    // pipes the whole — `(within …) |> f`,
                                                                    // the match-close wrap one keyword over
                                                                    || (h = indent
                                                                        && cls.Kind = PieceKind.PipeHead
                                                                        && bufStartsWithAt b ts "within ")
                                                                    ->
                                                                    closeDeeper (wrapFrom b ts) rest
                                                                | _ -> b, compounds

                                                            let buf, compounds = closeDeeper p.Buf p.Compounds
                                                            let depth = p.ParenDepth + parenDelta piece

                                                            let poppedL, keptL =
                                                                p.Lambdas
                                                                |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                            let lambdas =
                                                                if lambdaOpens piece then
                                                                    (lineNo, indent, depth - 1, p.StmtLevel) :: keptL
                                                                else
                                                                    keptL

                                                            let lastIndent, stmtLevel =
                                                                match poppedL with
                                                                | [] -> indent, p.StmtLevel
                                                                | ps ->
                                                                    let (_, _, _, restore) = List.last ps
                                                                    restore, restore

                                                            Ok(
                                                                Some
                                                                    { p with
                                                                        Buf =
                                                                            applyJoin
                                                                                // until/always join at the sentinel:
                                                                                // a space would feed the keyword to a
                                                                                // command body's argv (cmdWord stops
                                                                                // at the sentinel) [D:until-argv-join]
                                                                                (if
                                                                                     bufEndsInProcHead buf
                                                                                     || bufEndsInServeHead buf
                                                                                     || isUntil
                                                                                     || isAlways
                                                                                 then
                                                                                     JStmtSibling
                                                                                 else
                                                                                     JSpace)
                                                                                buf
                                                                                piece
                                                                                lineNo
                                                                                indent
                                                                        LastIndent = lastIndent
                                                                        StmtLevel = stmtLevel
                                                                        PrevDangles = dangleOpensBlock piece
                                                                        ParenDepth = depth
                                                                        Lambdas = lambdas
                                                                        // a closing `|>` pops the arm
                                                                        // group and opens a fresh forward
                                                                        // group so a following `|>` aligns
                                                                        // as a pipeline [D:match-pipe-offside]
                                                                        PipeGroups =
                                                                            if closes then
                                                                                (indent, indent, true)
                                                                                :: (groups
                                                                                    |> List.filter (fun (g, _, _) -> g < indent))
                                                                            else
                                                                                groups
                                                                        LastWasPipe = not (isUntil || isAlways)
                                                                        Compounds =
                                                                            compounds
                                                                            |> List.filter (fun (_, _, d) ->
                                                                                d <= depth) },
                                                                acc,
                                                                blankSinceHead
                                                            )
                                                else
                                                    match p.Lets with
                                                    | (k, letLine) :: _ when indent < k -> noBody letLine
                                                    | _ ->
                                                        // the offside close: siblings at or left of an
                                                        // open if/match head wrap it shut
                                                        let rec closeCompounds b compounds closedHead =
                                                            match compounds with
                                                            | (h, ts, _) :: rest when indent <= h ->
                                                                closeCompounds (wrapFrom b ts) rest (Some h)
                                                            | _ -> b, compounds, closedHead

                                                        let buf, compounds, closedHead =
                                                            closeCompounds p.Buf p.Compounds None

                                                        // the sibling floor is the statement's start column,
                                                        // not the last line's [D:continuation-siblings]: a
                                                        // deeper continuation line must not hoist the floor,
                                                        // or the second argument line of a multi-line
                                                        // application sequences as a statement
                                                        let siblingLevel =
                                                            match closedHead with
                                                            | Some h -> h
                                                            | None -> p.StmtLevel

                                                        // while a lambda's paren is open, lines at (or
                                                        // right of) its opener that would close a let or
                                                        // sequence a sibling outside it are body
                                                        // continuations instead — the `in`/`;` joins wait
                                                        // for the `)` [D:multiline-lambda]
                                                        let lambdaFloor =
                                                            match p.Lambdas with
                                                            | (_, oi, _, _) :: _ -> oi
                                                            | [] -> -1

                                                        let lets, join =
                                                            match p.Lets with
                                                            // a `)`-headed line while a plain paren is
                                                            // open closes a multi-line application — a
                                                            // continuation at any body indent, never a
                                                            // sibling; the `in`/`;` joins wait for the
                                                            // `)` exactly as the lambda floor rules
                                                            // [D:paren-close-continuation]
                                                            | _ when cls.ClosesParen && p.ParenDepth > 0 ->
                                                                p.Lets, JSpace
                                                            | (k, _) :: rest when indent = k && k > lambdaFloor ->
                                                                rest, JIn
                                                            // a proc head's block joins sentineled even in
                                                            // the dangle position [D:scoped-procs]
                                                            | _ when bufEndsInProcHead buf || bufEndsInServeHead buf ->
                                                                p.Lets, JStmtSibling
                                                            // the first line after a dangling head opens its
                                                            // body — a stale statement level from an earlier
                                                            // block must not sibling-capture it
                                                            // [D:continuation-siblings]
                                                            | _ when p.PrevDangles && indent > p.LastIndent ->
                                                                p.Lets, JSpace
                                                            // same-indent sibling = block sequencing
                                                            // [D:sibling-sentinel]: the machine boundary,
                                                            // not a user ';' — command mode stops here
                                                            | _ when indent = siblingLevel && indent > lambdaFloor ->
                                                                p.Lets, JStmtSibling
                                                            | _ -> p.Lets, JSpace

                                                        let lets =
                                                            if cls.Kind = PieceKind.LetHead then
                                                                (indent, lineNo) :: lets
                                                            else
                                                                lets

                                                        let district =
                                                            markerOpener cls.Marker
                                                            |> Option.map (fun (opener, strip, isYaml) ->
                                                                { MarkerIndent = indent
                                                                  MarkerLine = lineNo
                                                                  Opener = opener
                                                                  Strip = strip
                                                                  Yaml = isYaml
                                                                  Marker = cls.Marker
                                                                  Active = None })

                                                        let joined = applyJoin join buf piece lineNo indent

                                                        let depth = p.ParenDepth + parenDelta piece

                                                        // a net-negative piece closed parens the
                                                        // compounds were opened inside: those are
                                                        // balanced units already — prune, never wrap
                                                        // [D:compound-paren-prune]
                                                        let compounds =
                                                            compounds |> List.filter (fun (_, _, d) -> d <= depth)

                                                        let compounds =
                                                            if cls.OpensCompound then
                                                                // the piece starts where the join put it:
                                                                // its segment is the newest entry
                                                                let (js, _, _) = List.head joined.Segments
                                                                (indent, js, p.ParenDepth) :: compounds
                                                            else
                                                                compounds

                                                        // a statement starts at a sibling/`in` join or on
                                                        // the first line after a dangling head; the level
                                                        // rides into the lambda entry as its pop restore
                                                        let stmtLevel =
                                                            if join = JStmtSibling || join = JIn || p.PrevDangles then
                                                                indent
                                                            else
                                                                p.StmtLevel

                                                        // an attached closer pops its lambda and restores
                                                        // the statement level — the next sibling must
                                                        // join with `;`, never as an application
                                                        let poppedL, keptL =
                                                            p.Lambdas
                                                            |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                        let lambdas =
                                                            if lambdaOpens piece then
                                                                (lineNo, indent, depth - 1, stmtLevel) :: keptL
                                                            else
                                                                keptL

                                                        let lastIndent, stmtLevel =
                                                            match poppedL with
                                                            | [] -> indent, stmtLevel
                                                            | ps ->
                                                                let (_, _, _, restore) = List.last ps
                                                                restore, restore

                                                        // the dedent floor [D:district-retirement]: a line
                                                        // that dedents below the open block but aligns with
                                                        // no enclosing level would space-join — silently
                                                        // absorbed as argv when the previous line is a
                                                        // command (a legal parse, wrong meaning). Error instead.
                                                        if
                                                            join = JSpace
                                                            && indent < p.LastIndent
                                                            // open lambdas/brackets/parens legitimately take
                                                            // dedented body/element continuations
                                                            && List.isEmpty p.Lambdas
                                                            && List.isEmpty p.Brackets
                                                            && p.ParenDepth = 0
                                                        then
                                                            Error
                                                                $"line {lineNo}: this line dedents below the open block but aligns with no enclosing statement — align it with the statement it continues, or with the block level it should follow"
                                                        else

                                                            bracketFold lineNo indent [] piece
                                                            |> Result.map (fun brackets ->
                                                                Some
                                                                    { p with
                                                                        Buf = joined
                                                                        Lets = lets
                                                                        LastIndent = lastIndent
                                                                        StmtLevel = stmtLevel
                                                                        PrevDangles = dangleOpensBlock piece
                                                                        District = district
                                                                        Compounds = compounds
                                                                        Lambdas = lambdas
                                                                        Brackets = brackets
                                                                        ParenDepth = depth
                                                                        PipeGroups =
                                                                            // the first body line of a
                                                                            // dangling arm resolves its body
                                                                            // column [D:match-pipe-offside]
                                                                            (match p.PipeGroups with
                                                                             | (g, bc, false) :: rest when
                                                                                 bc = System.Int32.MaxValue
                                                                                 && p.PrevDangles
                                                                                 && indent > g
                                                                                 ->
                                                                                 (g, indent, false) :: rest
                                                                             | pg -> pg)
                                                                            |> List.skipWhile (fun (g, _, _) ->
                                                                                g > lastIndent)
                                                                        LastWasPipe = false },
                                                                acc,
                                                                blankSinceHead)

                                    go p
                        else
                            let cls = classifyPiece (raw.TrimEnd())

                            // a statement-level attribute line binds to the
                            // declaration below it [D:attr-positions] — a pend
                            // that is only a complete attr list joins the next
                            // col-0 line instead of closing (the field-attr `>]`
                            // dangle, one level up); the joined text re-parses
                            // as one statement, so a non-decl follower gets the
                            // parser's located error about attribute position
                            let attrOnlyPend =
                                match current with
                                | Some p when
                                    p.Brackets.IsEmpty
                                    && p.Lambdas.IsEmpty
                                    && p.District.IsNone
                                    && bufStartsWith p.Buf "[<"
                                    && bufTrimEndEndsWith p.Buf ">]"
                                    ->
                                    Some p
                                | _ -> None

                            let joinedBuf, accR =
                                match attrOnlyPend with
                                | Some p -> Some p.Buf, Ok acc
                                | None -> None, close current acc

                            accR
                            |> Result.bind (fun acc ->
                                bracketFold lineNo 0 [] (raw.TrimEnd())
                                |> Result.map (fun brackets ->
                                    Some
                                        { Buf =
                                            match joinedBuf with
                                            | Some b -> applyJoin JSpace b (raw.TrimEnd()) lineNo 0
                                            | None -> bufNew raw lineNo 0
                                          Lets = []
                                          LastIndent = 0
                                          District =
                                            markerOpener cls.Marker
                                            |> Option.map (fun (opener, strip, isYaml) ->
                                                { MarkerIndent = 0
                                                  MarkerLine = lineNo
                                                  Opener = opener
                                                  Strip = strip
                                                  Yaml = isYaml
                                                  Marker = cls.Marker
                                                  Active = None })
                                          // a fresh logical-line head that opens a
                                          // compound is tracked too [D:match-pipe-offside]
                                          // — so a later dedented `|>` can wrap it
                                          // `(match …) |> f`; a sibling head already is
                                          Compounds =
                                              if cls.OpensCompound && joinedBuf.IsNone then
                                                  [ (0, 0, 0) ]
                                              else
                                                  []
                                          ParenDepth = parenDelta (raw.TrimEnd())
                                          StmtLevel = 0
                                          PrevDangles = dangleOpensBlock (raw.TrimEnd())
                                          Lambdas =
                                            (if lambdaOpens (raw.TrimEnd()) then
                                                 [ (lineNo, 0, parenDelta (raw.TrimEnd()) - 1, 0) ]
                                             else
                                                 [])
                                          PipeGroups = []
                                          LastWasPipe = false
                                          Brackets = brackets },
                                    acc,
                                    false)))
                (Ok(None, [], false))

        match folded with
        | Error e -> Error e
        | Ok(current, acc, _) -> close current acc |> Result.map List.rev

// how many logical statements a raw physical buffer assembles to
// [D:repl-multiline] — comment-only lines filtered, comments stripped
// exactly as the REPL preprocesses. An assembly error (a still-open or
// pending statement) is "not yet countable" -> None. The piped REPL's
// continuation oracle: an added line that keeps the count merged into the
// pending statement; one that raises it started a new statement.
let statementCount (bufLines: string list) : int option =
    let numbered =
        bufLines
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> classifyLine raw <> LineKind.CommentOnly)
        |> List.map (fun (n, raw) -> n, stripComment raw)

    match assemble numbered with
    | Ok lls -> Some(List.length lls)
    | Error _ -> None

/// does `next` continue the already-complete statement in `buf`?
/// [D:repl-multiline] The assembler's own answer, no second parser: a
/// blank/comment line breaks a completed statement (the blank-boundary
/// rule) and does not attach; otherwise `next` attaches iff appending it
/// does not raise the assembled statement count (a `|>` tail, an offside
/// `else`, a district body keep it; still-pending buffers always want
/// more). Drives the piped REPL's read-ahead so a multi-line statement
/// assembles the way a script does.
let pipedAttaches (buf: string list) (next: string) : bool =
    if classifyLine next <> LineKind.Code then
        false
    else
        match statementCount buf, statementCount (buf @ [ next ]) with
        | _, None -> true
        | None, Some _ -> true
        | Some a, Some b -> b <= a

let translate (ll: LogicalLine) (col: int) : int * int =
    let joinedIdx = col - 1

    let segStart, physLine, physIndent =
        ll.Segments |> List.filter (fun (js, _, _) -> js <= joinedIdx) |> List.last

    physLine, joinedIdx - segStart + physIndent + 1

// ANSI color for interactive diagnostics: gated per stream on TTY,
// NO_COLOR, and TERM=dumb — pipes and CI capture always get plain
// text, so pinned messages never see escape codes.
// Color moved to Types.fs (shared with the Log builtins); the alias
// keeps Script.Color consumers (Repl) and bare Color.* sites stable
module Color = Weir.Types.Color

// ---- the REPL input-line colorizer [D:repl-color] -----------------
// Rides the one scanner (inStringMask), stripComment, and the parser's
// keyword set — no re-derived string states, so this is the one
// highlighter that is correct by construction. Lexical grade only;
// the head word additionally colors by the session resolver's verdict
// (the fish trick). Fixed palette, no theming.

let stripAnsi (s: string) : string =
    System.Text.RegularExpressions.Regex.Replace(s, "\x1b\[[0-9;]*m", "")

let colorizeRepl (isKnown: string -> bool) (line: string) : string =
    if line = "" then
        ""
    else
        // per-char color codes; None = plain
        let codes: string option array = Array.create line.Length None
        let mask = inStringMask line

        for i in 0 .. line.Length - 1 do
            if mask[i] then
                codes[i] <- Some "32" // strings, all three kinds

        let commentCut =
            // paint from the '//' itself, not from the cut (the cut
            // TrimEnds [D:trailing-comments] — the gap stays uncolored)
            let code = stripComment line

            if code.Length = line.Length then
                line.Length
            else
                line.IndexOf("//", code.Length)

        for i in commentCut .. line.Length - 1 do
            codes[i] <- Some "90" // comments override to EOL

        let isIdentStart c = Char.IsLetter c || c = '_'
        let isIdentCont c = Char.IsLetterOrDigit c || c = '_'
        let free i = i < commentCut && not mask[i]

        // token pass over the code region
        let mutable i = 0
        // the within kind paints as part of the form [D:within-kinds]
        let mutable prevWord = ""
        // the mode tint [D:semantic-tokens]: an external head arms
        // command mode; argv words render dim until a '|' hands the
        // chain to an expression stage — the same three-way the LSP
        // tokens carry (head / argv / splice), from the same resolver
        let mutable cmdMode = false

        while i < line.Length do
            if not (free i) then
                i <- i + 1
            elif isIdentStart line[i] then
                let start = i

                while i < line.Length && free i && isIdentCont line[i] do
                    i <- i + 1

                let word = line.Substring(start, i - start)

                let code =
                    // the head slot is Complete's one predicate
                    // [D:let-rhs-head]: the statement head and the
                    // let-RHS take the same verdict — tint and Tab
                    // cannot disagree about where a head stands
                    match Complete.headSlotAt (line.Substring(0, start)) with
                    | Complete.HeadSlot.Forced ->
                        // ^head: PATH only — `^x` names a program,
                        // never a keyword, a form, or an alias
                        Some(if Extern.exists word then "34" else "31")
                    | slot ->
                        if Weir.Parser.keywords.Contains word then
                            // keywords: blue — the red family (31/35 render
                            // near-identically in some themes) is reserved for
                            // exactly one signal: a head that would fail
                            Some "34"
                        elif prevWord = "within" && Ast.withinKinds |> List.exists (fun k -> k.Name = word) then
                            Some "34" // the kind is form, not a name [D:within-kinds]
                        elif (prevWord = "from" || prevWord = "to") && Builtins.allAdapterNames.Contains word then
                            Some "34" // the adapter is form, not a name [D:form-word-hover]
                        elif slot <> Complete.HeadSlot.No && isKnown word then
                            // the fish trick: the head resolves live
                            Some "1" // known: bold
                        elif slot <> Complete.HeadSlot.No && Extern.exists word then
                            cmdMode <- true
                            Some "1;34" // PATH: bold blue
                        elif slot = Complete.HeadSlot.Stmt then
                            Some "31" // unresolved statement head: red
                        elif Char.IsUpper word[0] then
                            // the casing rule: types/ctors/modules — at the
                            // let-RHS an unknown uppercase head is a legal
                            // constructor application, never red
                            // [D:constructors-not-heads]
                            Some "33"
                        elif slot = Complete.HeadSlot.LetRhs then
                            Some "31" // unresolved lowercase RHS head: red
                        elif cmdMode then
                            Some "2" // argv words: dim (inert data)
                        else
                            None

                prevWord <- word

                match code with
                | Some c ->
                    for j in start .. i - 1 do
                        codes[j] <- Some c
                | None -> ()
            elif Char.IsDigit line[i] then
                let start = i

                while i < line.Length && free i && Char.IsDigit line[i] do
                    i <- i + 1

                for j in start .. i - 1 do
                    codes[j] <- Some "36" // numbers: cyan
            elif line[i] = '$' || line[i] = '^' || line[i] = '!' then
                // sigils, splices, markers, force-prefix
                codes[i] <- Some "36"
                i <- i + 1

                if line[i - 1] = '$' && i < line.Length && free i && isIdentStart line[i] then
                    while i < line.Length && free i && isIdentCont line[i] do
                        codes[i] <- Some "36"
                        i <- i + 1
            elif "|><=+-*/".Contains(line[i]) then
                if line[i] = '|' then
                    // a stage boundary: what follows is expression land
                    cmdMode <- false

                codes[i] <- Some "1" // operators: bold
                i <- i + 1
            else
                i <- i + 1

        // the yaml district marker [D:yaml-district]: the line-end word
        // tints like the `!` markers do; district body lines stay
        // per-line lexical — the block treatment is the static grammars'
        // and semantic tokens' job, not a line colorizer's
        let codeTrimmed = (line.Substring(0, commentCut)).TrimEnd()

        if codeTrimmed.Length >= 4 && isYamlMarkerPiece codeTrimmed then
            // marker + modifiers (patch/by=/schema=) — one strip loop
            // shared with the predicate, so tint and the marker rule
            // cannot disagree [D:yaml-nodes]
            let markerLen = Parser.yamlMarkerLen codeTrimmed

            for j in codeTrimmed.Length - markerLen .. codeTrimmed.Length - 1 do
                codes[j] <- Some "36"

        // emit: group adjacent same-code chars into spans
        let sb = System.Text.StringBuilder()
        let mutable j = 0

        while j < line.Length do
            let code = codes[j]
            let start = j

            while j < line.Length && codes[j] = code do
                j <- j + 1

            let span = line.Substring(start, j - start)

            match code with
            | Some c ->
                sb.Append("\x1b[").Append(c).Append('m').Append(span).Append("\x1b[0m")
                |> ignore
            | None -> sb.Append span |> ignore

        sb.ToString()

// a `#sig` head directive [D:command-signatures]: tool + optional
// override path + its physical line (so errors can name the declaration)
type SigDecl =
    { Tool: string
      Override: string option
      Line: int }

// shebang/#sig peeling — one derivation for the runner and the
// check-side analyzeLines. The head block takes #sig lines; a #-line
// past it stays the misplaced-directive error.
let private scriptBody (rawLines: string list) : string list * int * SigDecl list =
    let afterShebang, shebangOffset =
        match rawLines with
        | first :: rest when first.StartsWith "#!" -> rest, 1
        | _ -> rawLines, 0

    let mutable rest = afterShebang
    let mutable off = shebangOffset
    let sigs = ResizeArray<SigDecl>()
    let mutable go = true

    while go do
        match rest with
        | first :: tail when first.TrimStart().StartsWith "#sig" ->
            let t = (stripComment first).Trim()

            let decl =
                match t.Split(' ', System.StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
                | [ "#sig"; tool ] ->
                    { Tool = tool
                      Override = None
                      Line = off + 1 }
                | [ "#sig"; tool; path ] when path.StartsWith "\"" && path.EndsWith "\"" && path.Length > 1 ->
                    { Tool = tool
                      Override = Some(path.Substring(1, path.Length - 2))
                      Line = off + 1 }
                | _ ->
                    // malformed: carry the raw for the diagnostic
                    { Tool = ""
                      Override = Some t
                      Line = off + 1 }

            sigs.Add decl
            rest <- tail
            off <- off + 1
        | _ -> go <- false

    rest, off, List.ofSeq sigs

type CheckedStmt =
    | CLet of name: string * te: Check.TypedExpr
    | CLetPat of binder: Weir.Ast.Pattern * te: Check.TypedExpr
    | CExpr of te: Check.TypedExpr
    | CCmd of te: Check.TypedExpr
    | CType of decl: Decl
    | CNoop
    // an imported module's replayable body [D:modules-v1] — evaluated once
    // at exec into the module's own venv, exposed as `alias.member`
    | CImport of LoadedModule

// a checked, imported module [D:modules-v1]. Its check-time contributions
// (Members -> Modules[Alias], TypeDefs -> Types, TypeNames -> ModuleTypes)
// merge into the importer's tenv; Body replays at exec to build the
// module's values. AbsPath is the normalized identity (symlinks unresolved).
and LoadedModule =
    { Alias: string
      // the module's own name (declared or filename-derived), independent
      // of a site's `as` — a cached module re-aliases from this [D:modules-v1]
      NaturalName: string
      AbsPath: string
      TypeDefs: (string * TypeDef) list
      Members: (string * Scheme) list
      // unsigned members [D:module-signatures]: never resolvable from an
      // importer — carried so the import-of-private error can suggest
      // the signature to add (type included)
      PrivateMembers: (string * Scheme) list
      TypeNames: string list
      // each statement with its logical line [D:can-report]: the line
      // carries the segment table, so a capability inside a module can
      // name its own file:line:col like any diagnostic
      Body: (LogicalLine * CheckedStmt) list }

// an import failure [D:modules-v1]. File=Some for a module-content error —
// reported at the module's own site (Line/Col into that file) plus an
// "imported here" note at the import line; File=None for an import-statement
// error (self-import, missing file, not-a-module) reported at the import line.
and ImportError =
    { File: string option
      Line: int
      Col: int
      Message: string }

// resolves an `import` to a checked module, or an ImportError [D:modules-v1].
// The caller binds it to the importing file's directory; the script-only
// (-e/REPL) and nested-import variants just return Error. Args: the literal
// path, its span (for error placement), the optional `as` alias.
and ImportLoader = string -> Span -> string option -> Result<LoadedModule, ImportError>

// the Self module [D:self-module]: script/process introspection grouped
// under one name, freeing the bare `args`/`stdin`/`scriptPath` for users.
// Members are per-run, so they inject here (not in static Builtins); the
// type side is a Modules entry, the value side mangled "Self.member" keys
// — module access checks `EField {EVar "Self"} field` -> TEVar "Self.field"
let selfMembers: Map<string, Scheme> =
    Map
        [ "pid", generalize TInt
          "args", generalize (TSeq TStr)
          "stdin", generalize (TSeq TStr)
          // scriptPath = the file's own path (a module sees its own);
          // entryPath = the invoked script's, a process fact like args/stdin
          // — the same for every file in the run [D:modules-v1] (decision 12)
          "scriptPath", generalize TStr
          "entryPath", generalize TStr
          // the interactive read [D:prompt] — static (Builtins holds the
          // value under the mangled key); grouped here, not bare
          "prompt", generalize (TFun(TStr, TStr)) ]

let private baseEnvs (scriptArgs: string list) (scriptPath: string) =
    let typeEnv = Builtins.typeEnvStrict

    let typeEnv, valueEnv = Prelude.extend typeEnv Builtins.valueEnv

    let typeEnv =
        { typeEnv with
            Modules = typeEnv.Modules |> Map.add "Self" selfMembers }

    let stdinStream =
        // one enumeration [D:prompt]: stdin is a live stream — a second
        // GetEnumerator cannot rewind the fd, and yielding empty there
        // silently produces wrong data; raise with the repair instead
        let consumed = ref false

        Eval.VSeq(
            Seq.delay (fun () ->
                if consumed.Value then
                    failwith
                        "Self.stdin is a live stream and was already consumed — bind ONE enumeration (let lines = Self.stdin |> Seq.freeze), or read a line per interaction with `Self.prompt`"

                consumed.Value <- true

                seq {
                    let mutable line = Console.In.ReadLine()

                    while line <> null do
                        yield Eval.VStr line
                        line <- Console.In.ReadLine()
                })
        )

    Session.ScriptArgs <- scriptArgs
    Session.EntryPath <- scriptPath

    let valueEnv =
        valueEnv
        |> Map.add "Self.pid" (Eval.VInt(int64 System.Environment.ProcessId))
        |> Map.add "Self.args" (Eval.VSeq(scriptArgs |> List.map Eval.VStr :> seq<Eval.Value>))
        |> Map.add "Self.stdin" stdinStream
        // the entry is the invoked script, so its own path and the entry
        // path coincide here; a module later overrides scriptPath with its
        // own while entryPath rides along as a process fact
        |> Map.add "Self.scriptPath" (Eval.VStr scriptPath)
        |> Map.add "Self.entryPath" (Eval.VStr scriptPath)

    typeEnv, valueEnv

// the base resolver over a type env — one constructor behind the
// script/fmt/REPL/CLI call sites
let resolver (typeEnv: TypeEnv) : Parser.Resolver =
    { IsKnown = fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules
      IsCommandCallable = fun n -> Builtins.commandCallable.Contains n
      IsExternal = Extern.exists
      ExternalNames = (fun () -> Extern.names () :> seq<string>)
      BareHome = (fun n -> Map.tryFind n Builtins.bareAliasHomes)
      // scripts/fmt/CLI carry no aliases (REPL-only) — the REPL overrides
      // this field with its session alias table [D:command-head-alias]
      AliasHead = fun _ -> None }

let private located (path: string) (lineNo: int) (msg: string) : string =
    let msg =
        if msg.StartsWith "[1:" then
            $"[{lineNo}:" + msg.Substring 3
        else
            msg

    $"{path}:{lineNo}: {msg}"

// the runtime-error line [D:binary-echo]: an error message may interpolate
// data (a hostile filename, a tenant name), and error text is a tty-bound
// renderer — so sanitize the message when stderr is a tty, the same guard
// print's data path uses. weir's own "error" colour word is added around
// the sanitized message, so colouring is untouched; redirected stderr
// stays byte-faithful.
let private runtimeErrorLine (path: string) (lineNo: int) (message: string) : string =
    let safe = Eval.sanitizeIfTty Console.IsErrorRedirected message
    located path lineNo (Color.red Color.onStderr.Value "error" + $": {safe}")

// Streaming output for command-mode statements — the single exempt form.
// The seq case goes through Eval.writeLines, the same renderer print uses.
let printResult (v: Eval.Value) =
    match v with
    | Eval.VStr s -> Console.WriteLine s
    | Eval.VSeq items -> Eval.writeLines items
    // unit-valued command statements (| orFail) print nothing —
    // the assert idiom is silent on success [D:exit-reifiers]
    | Eval.VUnit -> ()
    | other -> Console.WriteLine(Eval.formatValue other)

// The statement rule: a pure expression statement must have type unit.
// Classification is the parser's mode decision alone (SCmd vs SExpr) — no
// name lookup, no type direction; the removed form-2 exemption (bare
// sh/cmd applications) must not creep back in here.
let discardError (ty: Ty) : string option =
    match ty with
    | TUnit -> None
    // a hole-typed statement descends from an already-reported failed
    // let — silent, not a second complaint [PLAN-diagnostics-arc B6]
    | TVar v when v.StartsWith "__hole" -> None
    | TSeq TUnit ->
        Some "this statement computes a seq<unit> and discards it — a lazy effect sequence never runs; use Seq.iter"
    | TSeq(TNamed("FileRow", [])) as ty ->
        Some(
            $"this statement computes a {formatTy ty} and discards it — bind it, or pipe it to print"
            + " (for a plain listing, ^ls runs the real program)"
        )
    // an unresolved statement type [D:exit-polymorphic]: nothing
    // determines it — almost always a helper whose body ends in
    // exit/fail (diverging, so polymorphic — never unit). A bare
    // "computes a 'a1" names no cause; this names it and the repair.
    | TVar _ as ty ->
        Some(
            $"this statement computes a {formatTy ty} and discards it — an unresolved type here usually means the helper ends in exit or fail, which makes it polymorphic, not unit: return the exit code and exit at the call site (exit (helper …)), or bind the value"
        )
    | ty -> Some $"this statement computes a {formatTy ty} and discards it — bind it, or pipe it to print"

// ---------------------------------------------------------------------------
// The checked-statement pipeline — one owner [D:one-pipeline]:
// parse -> statement dispatch -> check -> statement-rule gate,
// physical spans computed inside. Every consumer (runner, REPL, -e,
// check/LSP, the oracle mirror) calls this and only renders.

// a module member signature [D:module-signatures] — the export
// declaration. Line/Col are the sig's own physical site (mismatch and
// orphan errors name it); Scheme is the sig type generalized (the
// paired implementation's check refines its Cs).
type MemberSig =
    { Ty: Ty
      Scheme: Scheme
      Line: int
      Col: int }

// the module-signature context a module check threads [D:module-signatures]:
// None = script/REPL/-e, where the form refuses with the teaching.
// Pending = declared, not yet implemented; Signed = every name ever
// signed (re-implementation guard); Implemented = every module-own
// binder so far (the sig-after-impl guard).
type SigContext =
    { Pending: Map<string, MemberSig>
      Signed: Set<string>
      Implemented: Set<string> }

module SigContext =
    let empty =
        { Pending = Map.empty
          Signed = Set.empty
          Implemented = Set.empty }

type CheckedKind =
    | KType of Decl
    | KLet of name: string * scheme: Scheme * te: Check.TypedExpr
    | KLetPat of binder: Pattern * schemes: (string * Scheme) list * te: Check.TypedExpr
    | KCmd of te: Check.TypedExpr
    | KExpr of te: Check.TypedExpr
    // the module marker [D:modules-v1] — makes the file a module (decl-only,
    // not runnable); the runner turns it into the running-a-module error
    | KModule of name: string option * kwSpan: Span
    // a resolved import [D:modules-v1] — its Env merge already applied
    | KImport of LoadedModule
    // a member signature [D:module-signatures] — binds nothing yet; the
    // caller pairs it with its implementation (and errors the orphan)
    | KSig of name: string * decl: MemberSig

[<RequireQualifiedAccess>]
type StmtTag =
    | Type
    | Let
    | LetPat
    | Cmd
    | Expr

type StmtDiag =
    { PhysLine: int
      PhysCol: int
      PhysEnd: (int * int) option // physical end of the span, when known
      Tag: StmtTag option // None for parse failures (kind unknown)
      HasCol: bool // false only for col-less parse failures
      Span: Span option // None for parse failures
      Parse: bool // parse error (FParsec text) vs type error (message)
      Message: string
      // multi-file [D:modules-v1]: File=Some when PhysLine/PhysCol point
      // into another file (a module's own error site); Note carries an
      // extra (line, col, message) in the current file (the import-line
      // "imported here"). Both default to None (single-file).
      File: string option
      Note: (int * int * string) option
      // the runner prints warnings even when the discard gate then
      // errors (standing behavior) — they travel with the diag
      Warnings: (int * int * string) list }

type CheckedStatement =
    { Kind: CheckedKind
      Env: TypeEnv // the env AFTER this statement (bindings added)
      Warnings: (int * int * string) list } // physical line, col, message

// gateExprs: scripts apply the statement rule (values must be bound or
// printed); the REPL and -e echo values instead — the same pipeline,
// one explicit switch, never a re-derivation
// assume only command-shaped words (letter-initial, ident chars +
// dashes; never keywords, never dotted): the expression grammar must
// keep claiming Env.load, from-adapters, and punctuation heads
let assumeResolver (tenv: TypeEnv) : Parser.Resolver =
    { resolver tenv with
        IsExternal =
            fun n ->
                Extern.exists n
                || (n.Length > 0
                    && System.Char.IsLetter n[0]
                    && not (Parser.isKeyword n)
                    // a declared type or module is never a command — else the
                    // named literal `Ctx { .. }` / `Paths.Ctx { .. }` mis-parses
                    // under check's assume-command rule [D:modules-v1]
                    && not (Map.containsKey n tenv.Types)
                    && not (Map.containsKey n tenv.Modules)
                    && n |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_' || c = '-')) }

// mkR builds the resolver from the current env per statement, so
// script-defined names are known at parse time — bindings shadow PATH
// commands by construction (`let cat = ...` then `cat x` is an
// application; ^cat forces the binary) [D:assume-resolver].
// FParsec dumps embed the assembled logical line — never what the user
// wrote. Strip every snippet+caret block, keep the diagnostic text,
// and translate embedded positions to physical line/col
// [D:clean-parse-dump].
let private cleanParseDump (ll: LogicalLine) (msg: string) : string =
    // no-leak [D:sibling-sentinel]: FParsec may echo the assembled line or
    // list the sentinel as an expected token; the machine sentinel must
    // never surface — render both the raw char and FParsec's  escape
    // (its expected-set rendering) as ';'
    let msg =
        msg
            .Replace(Parser.sibSepStr, ";")
            .Replace("\\u001f", ";")
            .Replace(Parser.fieldSepStr, ";")
            .Replace("\\u001d", ";")
    let lines = msg.Replace("\r\n", "\n").Split('\n') |> Array.toList

    let isCaret (l: string) =
        l.Trim() <> "" && l.Trim() |> Seq.forall ((=) '^')

    let translateErrorLine (l: string) : string option =
        let m = System.Text.RegularExpressions.Regex.Match(l, @"Error in Ln: 1 Col: (\d+)")

        if m.Success then
            let pl, pc = translate ll (int m.Groups[1].Value)
            Some($"at line {pl}, col {pc}:")
        else
            None

    let rec go (first: bool) (acc: string list) (rest: string list) =
        match rest with
        | [] -> List.rev acc
        | l :: tail ->
            match translateErrorLine l with
            | Some pos ->
                // drop the snippet rows up to and including the caret
                let indent = l |> Seq.takeWhile ((=) ' ') |> Seq.length

                let rec dropSnippet r =
                    match r with
                    | [] -> []
                    | x :: xs when isCaret x -> xs
                    | _ :: xs -> dropSnippet xs

                // the first position is the diag's own — consumers render
                // it with the source line; only backtrack positions stay
                let acc' = if first then acc else (String(' ', indent) + pos) :: acc
                go false acc' (dropSnippet tail)
            | None -> go first (l :: acc) tail

    // internal backtrack labels never surface [D:label-leaks]: drop any
    // line carrying the marker (raw or FParsec-escaped), then a heading
    // whose whole section emptied
    // openers (header/position lines ending with ':') whose section
    // emptied are dropped too — bottom-up: an opener survives only if
    // the next surviving line is its content (>= indent, non-opener) or
    // a nested opener (> indent)
    let isOpener (l: string) = l.TrimEnd().EndsWith ":"

    let indentOf (l: string) =
        l |> Seq.takeWhile ((=) ' ') |> Seq.length

    let rec dropEmptyOpeners (ls: string list) =
        match ls with
        | [] -> []
        | h :: tail ->
            let kept = dropEmptyOpeners tail

            if isOpener h then
                match kept with
                | next :: _ when
                    (isOpener next && indentOf next > indentOf h)
                    || (not (isOpener next) && indentOf next >= indentOf h)
                    ->
                    h :: kept
                | _ -> kept
            else
                h :: kept

    // rider D3: the foreign words (`while`/`return`/`try`/`def`) are reserved
    // only to explain that weir lacks them, so offering them as tokens the
    // parser expects is backwards. Strip them, consulting Parser's one list.
    // FParsec wraps a long list across lines, so rejoin first — otherwise a
    // token straddling the break is missed, and the list reads better whole.
    let bannedTokens =
        Parser.foreignKeywordWords |> List.map (fun w -> "'" + w + "'") |> Set.ofList

    let rebuildExpecting (ls: string list) : string list =
        // FParsec wraps the list at whatever width it likes — sometimes
        // comma-first, sometimes token-first — so a prefix test misses
        // continuations and the banned words survive on the tail line.
        // A continuation is instead any following line that is not a
        // section opener: openers end with ':'.
        let isCont (l: string) =
            l.Trim() <> "" && not (l.TrimEnd().EndsWith ":")

        let rec walk acc rest =
            match rest with
            | [] -> List.rev acc
            | (l: string) :: tail when l.Contains "Expecting:" ->
                let conts = tail |> List.takeWhile isCont
                let after = tail |> List.skip (List.length conts)

                let joined =
                    String.concat " " (l :: (conts |> List.map (fun (c: string) -> c.Trim())))
                // normalise the seam so ", " splitting works wherever it wrapped
                let whole =
                    System.Text.RegularExpressions.Regex.Replace(joined, @"\s+", " ").Replace(" ,", ",")

                let idx = whole.IndexOf "Expecting:"
                let head = whole.Substring(0, idx + "Expecting:".Length)
                let body = whole.Substring(idx + "Expecting:".Length)

                let toks =
                    body.Split([| ", "; " or " |], System.StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun (t: string) -> t.Trim())
                    |> Array.filter (fun t -> t <> "" && not (bannedTokens.Contains t))
                    |> List.ofArray

                let rendered =
                    match toks with
                    | [] -> head
                    | [ one ] -> head + " " + one
                    | many ->
                        head
                        + " "
                        + String.concat ", " (List.truncate (many.Length - 1) many)
                        + " or "
                        + List.last many

                walk (rendered :: acc) after
            | l :: tail -> walk (l :: acc) tail

        walk [] ls

    // rider D5: FParsec's backtracking trace is the parser talking to its
    // author. `go` already dropped the diagnostic's own position (consumers
    // render that with the source line), so any position line still standing
    // is a backtrack position — drop those and FParsec's EOF note, and the
    // "The parser backtracked after:" opener empties and dropEmptyOpeners
    // takes it.
    //
    // Kept under WEIR_LOG=debug: backtrack positions tell weir's own
    // developers where the grammar gave up — the wrong part was the
    // audience, not the information, so this hides rather than deletes.
    // `Unknown Error(s)` is FParsec claiming an error while having
    // nothing to say — dropped too, since that content alone would keep
    // the backtrack section (and its opener) alive after the position
    // lines go.
    let isBacktrackNoise (l: string) =
        let t = l.Trim()

        t.StartsWith "Note: The error occurred"
        || t = "Unknown Error(s)"
        || System.Text.RegularExpressions.Regex.IsMatch(t, @"^at line \d+, col \d+:$")

    go true [] lines
    |> rebuildExpecting
    |> List.filter (fun l ->
        not (l.Contains Parser.internalLabelMarker || l.Contains "\\u0006")
        && not (not (Builtins.debugEnabled ()) && isBacktrackNoise l)
        && l.Trim() <> "")
    |> dropEmptyOpeners
    |> String.concat "\n"

// merge a loaded module into the importer's env [D:modules-v1]: values
// (and union ctors) under Modules[Alias], types flat under their plain
// name, and the provenance for the qualified literal under ModuleTypes.
let private mergeModule (tenv: TypeEnv) (lm: LoadedModule) : TypeEnv =
    { tenv with
        Modules = Map.add lm.Alias (Map.ofList lm.Members) tenv.Modules
        Types = lm.TypeDefs |> List.fold (fun ts (n, d) -> Map.add n d ts) tenv.Types
        ModuleTypes = Map.add lm.Alias (Set.ofList lm.TypeNames) tenv.ModuleTypes
        ModulePrivate = Map.add lm.Alias (Map.ofList lm.PrivateMembers) tenv.ModulePrivate }

let private checkStatementCore
    (gateExprs: bool)
    (sigCtx: SigContext option)
    (mkR: TypeEnv -> Parser.Resolver)
    (loadImport: ImportLoader)
    (tenv: TypeEnv)
    (ll: LogicalLine)
    : Result<CheckedStatement, StmtDiag> =
    let r = mkR tenv

    let typed (tag: StmtTag) (terr: Check.TypeError) =
        // an Origin is already physical, recorded by the statement that
        // owned the access [D:row-provenance] — it bypasses translate
        let physLine, physCol, physEnd =
            match terr.Origin with
            | Some(ol, oc, len) -> ol, oc, Some(ol, oc + len)
            | None ->
                let pl, pc = translate ll terr.Span.Start.Col
                pl, pc, Some(translate ll terr.Span.End.Col)

        { PhysLine = physLine
          PhysCol = physCol
          PhysEnd = physEnd
          Tag = Some tag
          HasCol = true
          Span = Some terr.Span
          Parse = false
          Message = terr.Message
          File = None
          Note = None
          Warnings = [] }

    let warningsOf te =
        [ for w in Check.warnings te do
              let physLine, physCol = translate ll w.Span.Start.Col
              physLine, physCol, w.Message ]

    // physical translator for the checker's boundary-crossing
    // positions [D:row-provenance]; reset so non-statement consumers
    // (Complete, tests) never see a stale statement
    Check.toPhys.Value <- Some(translate ll)

    try
        match Parser.parseLineFull r ll.Text with
        | Error f ->
            // FParsec's primary error is often irrelevant when the real cause
            // is an unresolvable command head (the backtrack note buries it).
            // Retry under the assume-resolver: if that parses, the failure is
            // missing commands — name them precisely instead of the dump.
            let missingHeads =
                match Parser.parseLineFull (assumeResolver tenv) ll.Text with
                | Error _ -> []
                | Ok stmt ->
                    let e =
                        match stmt with
                        | SLet(_, e)
                        | SLetPat(_, e)
                        | SExpr e
                        | SCmd e -> Some e
                        | SType _
                        | SSig _
                        | SModule _
                        | SImport _ -> None

                    let rec heads (e: Expr) =
                        (match e.Kind with
                         | ECmd(HeadLit prog, _, _) when not (Extern.exists prog) -> [ prog, e.Span ]
                         | _ -> [])
                        @ (exprChildren e |> List.collect heads)

                    e |> Option.map heads |> Option.defaultValue []

            match missingHeads with
            | (prog, span) :: rest ->
                let physLine, physCol = translate ll span.Start.Col

                let others =
                    match rest |> List.map fst |> List.distinct |> List.filter ((<>) prog) with
                    | [] -> ""
                    | more ->
                        let joined = String.concat ", " more
                        $" (also missing: {joined})"

                Error
                    { PhysLine = physLine
                      PhysCol = physCol
                      PhysEnd = Some(translate ll span.End.Col)
                      Tag = None
                      HasCol = true
                      Span = Some span
                      Parse = true
                      Message =
                        (match Map.tryFind prog Builtins.bareAliasHomes with
                         | Some home ->
                             $"'{prog}' is a bare module member, not a program — spell it '{home}.{prog}' (bare names live in the REPL session)"
                         | None ->
                             $"unknown command '{prog}' — not found on PATH{others}. weir resolves command names before running: install the tool, or run it through sh -c")
                      File = None
                      Note = None
                      Warnings = [] }
            | [] ->
                let physLine, physCol, hasCol =
                    match f.Col with
                    | Some col ->
                        let l, c = translate ll col
                        l, c, true
                    | None -> ll.Head, 1, false

                Error
                    { PhysLine = physLine
                      PhysCol = physCol
                      PhysEnd = None
                      Tag = None
                      HasCol = hasCol
                      Span = None
                      Parse = true
                      Message = cleanParseDump ll f.Message
                      File = None
                      Note = None
                      Warnings = [] }
        | Ok(SType decl) ->
            match Check.checkDecl tenv decl with
            | Error terr -> Error(typed StmtTag.Type terr)
            | Ok tenv' ->
                Ok
                    { Kind = KType decl
                      Env = tenv'
                      Warnings = [] }
        | Ok(SSig(name, ty, nameSpan)) ->
            let sigErr msg =
                Error(
                    typed
                        StmtTag.Let
                        { Span = nameSpan
                          Message = msg
                          Origin = None }
                )

            match sigCtx with
            | None ->
                // scripts infer [D:module-signatures] — the API-surface rule
                // is a module rule; a script's lets need no export marker
                sigErr
                    $"signatures belong to module APIs — scripts infer: drop the signature (let {name} = …), or move this into a module"
            | Some sc ->
                match Check.checkBinderName nameSpan name with
                | Error terr -> Error(typed StmtTag.Let terr)
                | Ok() ->
                    if Map.containsKey name sc.Pending then
                        sigErr $"duplicate signature for '{name}'"
                    elif Set.contains name sc.Signed then
                        sigErr $"'{name}' already has a signature and an implementation — one signature per member"
                    elif Set.contains name sc.Implemented then
                        sigErr
                            $"the signature for '{name}' comes after its implementation — declare the signature above it"
                    else
                        match Check.validateSigTy tenv nameSpan ty with
                        | Error terr -> Error(typed StmtTag.Let terr)
                        | Ok tenv' ->
                            let line, col = translate ll nameSpan.Start.Col

                            Ok
                                { Kind =
                                    KSig(
                                        name,
                                        { Ty = ty
                                          Scheme = generalize ty
                                          Line = line
                                          Col = col }
                                    )
                                  Env = tenv'
                                  Warnings = [] }
        | Ok(SLetPat({ PKind = PWildcard } as pat, _)) when gateExprs ->
            // a bare `_` binder discards anonymously — binding is how
            // weir silences raise-on-nonzero, so the discard gets a name
            // [D:unused-bindings]; gateExprs scopes the refusal with the
            // discard family (the REPL and -e keep their echo regime)
            Error(
                typed
                    StmtTag.LetPat
                    { Span = pat.PSpan
                      Message =
                        "a bare '_' binder discards its value anonymously — name the discard (let _r = …) so the intent survives a grep"
                      Origin = None }
            )
        | Ok(SLetPat(pat, e)) ->
            // a destructuring let cannot implement a signature
            // [D:module-signatures] — pairing is by the plain name
            let sigClash =
                sigCtx
                |> Option.bind (fun sc ->
                    let rec patVars (p: Pattern) =
                        match p.PKind with
                        | PVar n -> [ n ]
                        | PTuple ps -> ps |> List.collect patVars
                        | PRecord fields -> fields |> List.map snd |> List.collect patVars
                        | PCase(_, Some inner) -> patVars inner
                        | _ -> []

                    patVars pat
                    |> List.tryFind (fun n -> Map.containsKey n sc.Pending || Set.contains n sc.Signed))

            match sigClash with
            | Some n ->
                Error(
                    typed
                        StmtTag.LetPat
                        { Span = pat.PSpan
                          Message =
                            $"'{n}' has a signature, and a signature pairs with a plain implementation — write 'let {n} … = …'"
                          Origin = None }
                )
            | None ->
                // anonymous adapter shapes persist their defs [D:anon-records]
                let tenv = Check.withAnonDefs tenv e

                match Check.typecheckBinder tenv pat e with
                | Error terr -> Error(typed StmtTag.LetPat terr)
                | Ok(te, schemes) ->
                    Ok
                        { Kind = KLetPat(pat, schemes, te)
                          Env =
                            { tenv with
                                Values = schemes |> List.fold (fun vs (n, sch) -> Map.add n sch vs) tenv.Values }
                          Warnings = warningsOf te }
        | Ok(SLet(name, e)) ->
            // SLet carries the name as a bare string, so re-derive its own
            // columns from the statement text (grammar: ws `let` ws name)
            // [D:squiggle-on-binder]
            let nameSpan =
                let m = Text.RegularExpressions.Regex.Match(ll.Text, @"^\s*let\s+")

                if m.Success then
                    { Start = { Line = 1; Col = m.Length + 1 }
                      End =
                        { Line = 1
                          Col = m.Length + 1 + name.Length } }
                else
                    e.Span

            let tenv = Check.withAnonDefs tenv e

            match sigCtx |> Option.bind (fun sc -> Map.tryFind name sc.Pending) with
            | Some sd ->
                // check-against-sig [D:module-signatures]: the signature's
                // types flow into the implementation's checking; the
                // exported scheme is the signature's (plus the
                // implementation's constraint residue)
                let sigErrAt (span: Span) msg =
                    Error(
                        typed
                            StmtTag.Let
                            { Span = span
                              Message = msg
                              Origin = None }
                    )

                match Check.checkBinderName nameSpan name with
                | Error terr -> Error(typed StmtTag.Let terr)
                | Ok() ->
                    match Check.typecheckAgainstSig tenv sd.Ty e with
                    | Error(Check.SigBody terr) -> Error(typed StmtTag.Let terr)
                    | Error(Check.SigShape(implTy, span)) ->
                        sigErrAt
                            span
                            ($"the implementation of '{name}' does not match its signature: "
                             + $"the signature (line {sd.Line}) declares {formatTy sd.Ty}, but the implementation is {formatTy implTy}")
                    | Error(Check.SigPinned(v, pinnedTy, span)) ->
                        sigErrAt
                            span
                            ($"the implementation of '{name}' is less general than its signature: "
                             + $"'{v} (line {sd.Line}) is pinned to {formatTy pinnedTy} here — generalize the implementation, or narrow the signature")
                    | Error(Check.SigMerged(v1, v2, span)) ->
                        sigErrAt
                            span
                            ($"the implementation of '{name}' is less general than its signature: "
                             + $"'{v1} and '{v2} (line {sd.Line}) are forced to one type here — the signature promises they stay independent")
                    | Ok(te, scheme) ->
                        Ok
                            { Kind = KLet(name, scheme, te)
                              Env =
                                { tenv with
                                    Values = Map.add name scheme tenv.Values }
                              Warnings = warningsOf te }
            | None ->
                match sigCtx with
                | Some sc when Set.contains name sc.Signed ->
                    // paired already — a second implementation would let the
                    // last binding win at replay while the export keeps the
                    // signature's, so the split is refused
                    Error(
                        typed
                            StmtTag.Let
                            { Span = nameSpan
                              Message =
                                $"'{name}' already implements its signature — one implementation per signed member"
                              Origin = None }
                    )
                | _ ->
                    match
                        Check.checkBinderName nameSpan name
                        |> Result.bind (fun () -> Check.typecheckWith tenv e)
                    with
                    | Error terr -> Error(typed StmtTag.Let terr)
                    | Ok(te, cs, origins, holeDefaults) ->
                        let scheme = generalizeWithOrigins cs origins holeDefaults te.Ty

                        Ok
                            { Kind = KLet(name, scheme, te)
                              Env =
                                { tenv with
                                    Values = Map.add name scheme tenv.Values }
                              Warnings = warningsOf te }
        | Ok(SCmd e) ->
            let tenv = Check.withAnonDefs tenv e

            match Check.typecheck tenv e with
            | Error terr -> Error(typed StmtTag.Cmd terr)
            | Ok te ->
                // a bool-valued chain (| succeeds) as a bare statement is a
                // discarded value, not a stream — the discard family
                // [D:exit-reifiers]. `| complete` (a Completed record) joins
                // it: same mistake, keyed on the distinctive value type.
                let rec exitCodeSpine (t: Check.TypedExpr) =
                    match t.Kind with
                    | Check.TEVar("|exitCoded" | "|exitCodedEnv") -> true
                    | Check.TEApp(f, _) -> exitCodeSpine f
                    | _ -> false

                match te.Ty with
                | TBool ->
                    Error(
                        typed
                            StmtTag.Cmd
                            { Span = te.Span
                              Message =
                                "this statement computes a bool and discards it — bind it "
                                + "(let ok = ... | succeeds) or use it in a condition"
                              Origin = None }
                    )
                // `set +e; cmd; rc=$?` muscle memory lands here [D:exit-reifiers]
                | TInt when exitCodeSpine te ->
                    Error(
                        typed
                            StmtTag.Cmd
                            { Span = te.Span
                              Message =
                                "this statement computes the exit code and discards it — bind it "
                                + "(let rc = <command> | exitCode), match on it, or drop '| exitCode' if you don't need the code"
                              Origin = None }
                    )
                // `| complete` captures a Completed record — reading it is
                // the point, so discarding it is the family mistake
                | TNamed("Completed", _) ->
                    Error(
                        typed
                            StmtTag.Cmd
                            { Span = te.Span
                              Message =
                                "this statement computes a Completed record and discards it — bind it "
                                + "(let r = ... | complete) or read a field (.exitCode, .stdout)"
                              Origin = None }
                    )
                | _ ->
                    Ok
                        { Kind = KCmd te
                          Env = tenv
                          Warnings = warningsOf te }
        | Ok(SExpr e) ->
            // statement position demands unit, so a commandish tail arms
            // [D:within-scopes] — reaching through scopes and let-ins;
            // the REPL (gateExprs=false) keeps its echo instead
            let e = if gateExprs then Check.armTail e else e
            let tenv = Check.withAnonDefs tenv e

            match Check.typecheck tenv e with
            | Error terr -> Error(typed StmtTag.Expr terr)
            | Ok te ->
                // the likeliest intent behind a discarded $(cmd) is "run
                // it" — the wrapper is what is in the way, so the error
                // names the drop [D:district-retirement] (the wrap-it
                // hint's principle, inverted)
                let dropClause =
                    match e.Kind with
                    | ECapture { Kind = ECmd _ }
                    | ECapture { Kind = EPipe(_, { Kind = ECmd _ }) } -> ", or drop the $( ) to run it as a command"
                    | _ -> ""

                // a diverging tail makes no value to discard
                // [D:fail-bottom]: its fresh var passes the statement gate
                let gated =
                    match te.Ty with
                    | TVar _ when Check.divergesTo e -> None
                    | ty -> if gateExprs then discardError ty else None

                match gated with
                | Some msg ->
                    let msg = msg + dropClause

                    Error
                        { typed
                              StmtTag.Expr
                              { Span = e.Span
                                Message = msg
                                Origin = None } with
                            Warnings = warningsOf te }
                | None ->
                    Ok
                        { Kind = KExpr te
                          Env = tenv
                          Warnings = warningsOf te }
        | Ok(SModule(nameOpt, kwSpan)) ->
            // the marker adds no bindings; decl-only enforcement and the
            // running-a-module error are the caller's (loadModule / run)
            Ok
                { Kind = KModule(nameOpt, kwSpan)
                  Env = tenv
                  Warnings = [] }
        | Ok(SImport(path, pathSpan, aliasOpt)) ->
            let importLine, importCol = translate ll pathSpan.Start.Col

            // a module-content error (File=Some) reports at the module's own
            // site [D:modules-v1], with an "imported here" note at the import
            // line; an import-statement error reports at the import line
            let importDiag (e: ImportError) =
                match e.File with
                | Some mf ->
                    { PhysLine = e.Line
                      PhysCol = e.Col
                      PhysEnd = None
                      Tag = None
                      HasCol = true
                      Span = None
                      Parse = false
                      Message = e.Message
                      File = Some mf
                      // point at this level's import; the outermost (entry)
                      // level runs last, so its note (in the entry file) wins
                      Note = Some(importLine, importCol, "imported here")
                      Warnings = [] }
                | None ->
                    { PhysLine = importLine
                      PhysCol = importCol
                      PhysEnd = Some(translate ll pathSpan.End.Col)
                      Tag = None
                      HasCol = true
                      Span = Some pathSpan
                      Parse = false
                      Message = e.Message
                      File = None
                      Note = None
                      Warnings = [] }

            let stmtErr msg =
                importDiag
                    { File = None
                      Line = 0
                      Col = 0
                      Message = msg }

            match loadImport path pathSpan (aliasOpt |> Option.map fst) with
            | Error e -> Error(importDiag e)
            | Ok lm when Map.containsKey lm.Alias tenv.Modules ->
                Error(
                    stmtErr
                        $"the name '{lm.Alias}' is already a module in scope; import it under a different name with 'as'"
                )
            | Ok lm ->
                match lm.TypeNames |> List.tryFind (fun n -> Map.containsKey n tenv.Types) with
                | Some clash ->
                    Error(
                        stmtErr
                            $"import '{lm.Alias}' declares a type '{clash}' that is already declared here; rename one (cross-module same-name types are not yet distinguishable)"
                    )
                | None ->
                    Ok
                        { Kind = KImport lm
                          Env = mergeModule tenv lm
                          Warnings = [] }
    finally
        Check.toPhys.Value <- None

// [D:pure-stage1]: the pure region's rule, enforced as a post-check
// layer here — the classifier needs the closed builtin surface, so it
// lives after Builtins (Purity.fs), out of Check.fs's compile reach;
// and every consumer — runner, REPL, -e, modules — flows through this
// pipeline [D:one-pipeline], so the layer covers them all. A KLet
// registers its purity into the env the next statement threads:
// transitivity through earlier bindings is one forward pass (no
// `let rec` exists).
let checkStatement
    (gateExprs: bool)
    (sigCtx: SigContext option)
    (mkR: TypeEnv -> Parser.Resolver)
    (loadImport: ImportLoader)
    (tenv: TypeEnv)
    (ll: LogicalLine)
    : Result<CheckedStatement, StmtDiag> =
    checkStatementCore gateExprs sigCtx mkR loadImport tenv ll
    |> Result.bind (fun st ->
        let teTag =
            match st.Kind with
            | KLet(_, _, te) -> Some(te, StmtTag.Let)
            | KLetPat(_, _, te) -> Some(te, StmtTag.LetPat)
            | KCmd te -> Some(te, StmtTag.Cmd)
            | KExpr te -> Some(te, StmtTag.Expr)
            | KType _
            | KSig _
            | KModule _
            | KImport _ -> None

        match teTag with
        | None -> Ok st
        | Some(te, tag) ->
            // pureViolation returns the complete located message
            // (pure's "…forbids effects, but…" or readonly's
            // "…forbids external mutation, but…") [D:pure-stage2]
            match Purity.pureViolation tenv.PureBindings te with
            | Some(span, message) ->
                let physLine, physCol = translate ll span.Start.Col

                Error
                    { PhysLine = physLine
                      PhysCol = physCol
                      PhysEnd = Some(translate ll span.End.Col)
                      Tag = Some tag
                      HasCol = true
                      Span = Some span
                      Parse = false
                      Message = message
                      File = None
                      Note = None
                      Warnings = [] }
            | None ->
                match st.Kind with
                | KLet(name, _, te) ->
                    Ok
                        { st with
                            Env =
                                { st.Env with
                                    PureBindings = Map.add name (Purity.isPureExpr tenv.PureBindings te) st.Env.PureBindings } }
                | _ -> Ok st)

// ---- unused bindings [D:unused-bindings]: whole-file threading ------------
// The check is a whole-file judgement (a binder's readers may sit any
// number of statements later), so it applies post-fold — beside the
// sig-orphan check — in every consumer that folds a complete file:
// analyzeLines (check / --can / LSP), the runner's check phase, the
// module loader, and the fidelity mirror. Per-statement consumers
// (REPL, -e, Complete) never see it. An errored statement poisons the
// pass for the whole file — one real error beats N echoes (matching
// the hole scheme).

type UnusedFinding =
    { ULine: int
      UCol: int
      UEndCol: int
      UMessage: string }

type UnusedTracker() =
    // pending top-level binders: name -> (physLine, physCol, physEndCol, used)
    let pending = System.Collections.Generic.Dictionary<string, int * int * int * bool>()
    let found = ResizeArray<UnusedFinding>()
    let mutable poisoned = false

    let plainMsg (n: string) =
        $"'{n}' is bound but never used — read it, or name it '_{n}' to keep it deliberately"

    let bareMsg =
        "a bare '_' binder discards its value anonymously — name the discard (let _r = …) so the intent survives a grep"

    member _.Poison() = poisoned <- true

    member _.Uses(names: Set<string>) =
        for n in names do
            match pending.TryGetValue n with
            | true, (l, c, e, false) -> pending[n] <- (l, c, e, true)
            | _ -> ()

    member _.Locals (ll: LogicalLine) (locals: Check.UnusedLocal list) =
        for u in locals do
            let l, c = translate ll u.USpan.Start.Col
            let _, ec = translate ll u.USpan.End.Col

            found.Add
                { ULine = l
                  UCol = c
                  UEndCol = max (c + 1) ec
                  UMessage = if u.UBareWildcard then bareMsg else plainMsg u.UName }

    /// register a top-level binder; an unread pending binder of the
    /// same name errors at the earlier binder (the shadow rule —
    /// it catches the classic copy-paste bug)
    member _.Binder (ll: LogicalLine) (name: string) (startCol: int) (endCol: int) =
        if not (Check.unusedExempt name) then
            let l, c = translate ll startCol
            let _, ec = translate ll endCol

            (match pending.TryGetValue name with
             | true, (ol, oc, oe, false) ->
                 found.Add
                     { ULine = ol
                       UCol = oc
                       UEndCol = oe
                       UMessage =
                         $"'{name}' is bound but never used before being rebound at line {l} — read it, or name it '_{name}' to keep it deliberately" }
             | _ -> ())

            pending[name] <- (l, c, max (c + 1) ec, false)

    /// the KLet binder's own span, re-derived from the statement text
    /// exactly as the squiggle does [D:squiggle-on-binder]
    member this.LetBinder (ll: LogicalLine) (name: string) =
        let m = Text.RegularExpressions.Regex.Match(ll.Text, @"^\s*let\s+(?:pure\s+)?")

        if m.Success then
            this.Binder ll name (m.Length + 1) (m.Length + 1 + name.Length)
        else
            this.Binder ll name 1 (1 + name.Length)

    /// one checked statement, in file order: mark its uses, collect its
    /// block-local findings, then register its own binders (a
    /// same-statement read of the same name reads the outer binding —
    /// no `let rec` exists)
    member this.Feed (ll: LogicalLine) (chk: CheckedStatement) =
        match chk.Kind with
        | KType _
        | KSig _
        | KModule _
        | KImport _ -> ()
        | KLet(name, _, te) ->
            let free, locals = Check.bindingUsage te
            this.Uses free
            this.Locals ll locals
            this.LetBinder ll name
        | KLetPat(pat, _, te) ->
            let free, locals = Check.bindingUsage te
            this.Uses free
            this.Locals ll locals

            for n, sp in Check.patNameSpans pat do
                this.Binder ll n sp.Start.Col sp.End.Col
        | KCmd te
        | KExpr te ->
            let free, locals = Check.bindingUsage te
            this.Uses free
            this.Locals ll locals

    /// end of file: every still-unread binder errors — signed module
    /// members exempt (the signature is the use); an unsigned module
    /// member unread at home is dead private code
    member _.Flush (signedNames: Set<string>) (isModule: bool) : UnusedFinding list =
        if poisoned then
            []
        else
            let flushed =
                [ for KeyValue(name, (l, c, e, used)) in pending do
                      if not used && not (Set.contains name signedNames) then
                          { ULine = l
                            UCol = c
                            UEndCol = e
                            UMessage =
                              if isModule then
                                  $"'{name}' is bound but never used — an unsigned module member is private dead code: use it, export it with a signature (let {name} : …), or name it '_{name}'"
                              else
                                  plainMsg name } ]

            (List.ofSeq found) @ flushed |> List.sortBy (fun f -> f.ULine, f.UCol)

// ---- possible re-enumeration [D:reenum-warning]: whole-file threading -----
// The hazard is a whole-file judgement like the unused check (a second
// pull may sit any number of statements later), so it applies
// post-fold in analyzeLines — check / --json / --can / the LSP all
// inherit it; the REPL states the same hazard on its binding echo
// instead. Warning severity: advisory, never a gate — check still
// exits 0. Poisoned (any errored statement) = silent, the unused
// check's rule: one real error beats advisory noise.

type ReenumFinding =
    { RLine: int
      RCol: int
      REndCol: int
      RMessage: string }

type ReenumTracker() =
    // top-level tracked binders: name -> id; id -> (command, repair)
    let tracked = System.Collections.Generic.Dictionary<string, int>()
    let info = System.Collections.Generic.Dictionary<int, string * string>()
    let counts = System.Collections.Generic.Dictionary<int, int>()
    let found = ResizeArray<ReenumFinding>()
    let mutable nextId = 0
    let mutable poisoned = false

    let fresh () =
        let id = nextId
        nextId <- nextId + 1
        id

    /// walk one statement tree: count enumerating uses of the tracked
    /// set (top-level and any qualifying block-locals), a warning at
    /// the second and later sites — the first pull is the binding's
    /// point, the second is where the command silently runs again
    let consume (ll: LogicalLine) (te: Check.TypedExpr) =
        let tracked0 =
            tracked |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

        for ev in Check.reenumEvents fresh tracked0 te do
            match ev with
            | Check.ReenumBind(id, _, cmd) ->
                // a block-local binder has no clean one-line source to
                // ride the repair — the generic spelling instead
                info[id] <- (cmd, "add '|> Seq.freeze' at the binding")
                counts[id] <- 0
            | Check.ReenumUse(id, name, span) ->
                let n =
                    (match counts.TryGetValue id with
                     | true, c -> c
                     | _ -> 0)
                    + 1

                counts[id] <- n

                if n >= 2 then
                    let cmd, repair = info[id]
                    let l, c = translate ll span.Start.Col
                    let _, ec = translate ll span.End.Col

                    found.Add
                        { RLine = l
                          RCol = c
                          REndCol = max (c + 1) ec
                          RMessage =
                            $"possible re-enumeration: '{name}' is command-backed and unforced — each pull re-runs '{cmd}'; snapshot one run: {repair}" }

    member _.Poison() = poisoned <- true

    member _.Feed (ll: LogicalLine) (chk: CheckedStatement) =
        match chk.Kind with
        | KType _
        | KSig _
        | KModule _
        | KImport _ -> ()
        | KLet(name, _, te) ->
            // RHS uses first (a same-name rebind reads the outer binding
            // — no `let rec` exists); the alias rule, classified: a
            // whole-RHS bare name neither pulls nor carries the
            // tracking onto the alias
            (match te.Kind with
             | Check.TEVar _ -> ()
             | _ -> consume ll te)

            tracked.Remove name |> ignore

            match Check.commandBackedUnforced te with
            | Some cmd ->
                let id = fresh ()

                // the repair rides the binding's own source when it is
                // one clean line (as streamed-it does); assembled
                // (sentinel-joined) text falls back to the generic
                // spelling
                let repair =
                    let src = ll.Text.Trim()

                    if src.StartsWith "let " && not (src |> Seq.exists System.Char.IsControl) then
                        $"{src} |> Seq.freeze"
                    else
                        "add '|> Seq.freeze' at the binding"

                info[id] <- (cmd, repair)
                counts[id] <- 0
                tracked[name] <- id
            | None -> ()
        | KLetPat(pat, _, te) ->
            consume ll te

            for n, _ in Check.patNameSpans pat do
                tracked.Remove n |> ignore
        | KCmd te
        | KExpr te -> consume ll te

    member _.Flush() : ReenumFinding list =
        if poisoned then [] else List.ofSeq found

// ---- the newTempDir footgun [D:newtempdir-lint]: whole-file threading -----
// A newTempDir binding and its delete can sit any number of statements
// apart (`let d = Path.newTempDir ()` … work … `Dir.deleteAll d`), so the
// pairing is a whole-file judgement — the re-enumeration tracker's shape
// exactly. Fed post-check per statement, flushed after the fold; warning
// severity (advisory, check still exits 0); poisoned on any errored
// statement (one real error beats advisory noise). Modules never bind a
// command/effect `let`, so the pairing cannot arise there — the tracker is
// only fed for scripts.

type TempDirFinding =
    { TLine: int
      TCol: int
      TEndCol: int
      TMessage: string }

type TempDirTracker() =
    // top-level newTempDir binders: name -> id
    let tracked = System.Collections.Generic.Dictionary<string, int>()
    // every bind id seen (top-level + block-local) still awaiting a delete
    let openIds = System.Collections.Generic.HashSet<int>()
    let found = ResizeArray<TempDirFinding>()
    let mutable nextId = 0
    let mutable poisoned = false

    let fresh () =
        let id = nextId
        nextId <- nextId + 1
        id

    /// walk one statement tree: a delete of an open binder (top-level or a
    /// qualifying block-local) warns once at the delete site
    let consume (ll: LogicalLine) (te: Check.TypedExpr) =
        let tracked0 =
            tracked |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

        for ev in Check.tempDirEvents fresh tracked0 te do
            match ev with
            | Check.TempBind(id, _) -> openIds.Add id |> ignore
            | Check.TempDelete(id, name, span, isAll) ->
                if openIds.Remove id then
                    // a matched top-level binder is resolved — drop it so a
                    // later stray delete of a reused name does not re-warn
                    for kv in tracked |> Seq.filter (fun kv -> kv.Value = id) |> Seq.toList do
                        tracked.Remove kv.Key |> ignore

                    let l, c = translate ll span.Start.Col
                    let _, ec = translate ll span.End.Col
                    let deleteCall = if isAll then "Dir.deleteAll" else "Dir.delete"

                    found.Add
                        { TLine = l
                          TCol = c
                          TEndCol = max (c + 1) ec
                          TMessage =
                            $"'{name}' is a Path.newTempDir directory later removed with {deleteCall} — "
                            + $"use a 'within tmp {name}' block instead: it removes the directory on scope exit "
                            + "AND on Ctrl+C/kill, which a manual delete misses (newTempDir is for a directory "
                            + "that must OUTLIVE the scope)" }

    member _.Poison() = poisoned <- true

    member _.Feed (ll: LogicalLine) (chk: CheckedStatement) =
        match chk.Kind with
        | KType _
        | KSig _
        | KModule _
        | KImport _ -> ()
        | KLet(name, _, te) ->
            consume ll te
            tracked.Remove name |> ignore

            if Check.isNewTempDirCall te then
                let id = fresh ()
                tracked[name] <- id
                openIds.Add id |> ignore
        | KLetPat(pat, _, te) ->
            consume ll te

            for n, _ in Check.patNameSpans pat do
                tracked.Remove n |> ignore
        | KCmd te
        | KExpr te -> consume ll te

    member _.Flush() : TempDirFinding list =
        if poisoned then [] else List.ofSeq found

// ---- the module loader [D:modules-v1] ------------------------------------
// A module's own base env: builtins (strict) + prelude + Self, with
// Self.scriptPath = the module's own path. Pure — no stdin/args/Session
// wiring, so loading a module never disturbs the entry's process facts.
let private moduleBaseEnvs (absPath: string) : TypeEnv * Eval.Env =
    let te, ve = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv

    { te with
        Modules = te.Modules |> Map.add "Self" selfMembers },
    ve |> Map.add "Self.scriptPath" (Eval.VStr absPath)

// the one import path resolver [D:modules-v1]: absolute + normalized (for
// identity and, later, caching); symlinks stay unresolved — as in
// Path.glob, two links to one file are two files.
let private resolveImportPath (importingAbsPath: string) (path: string) : string =
    let dir = IO.Path.GetDirectoryName importingAbsPath

    if path.StartsWith "weir:" then
        // the vendored namespace [D:add-module] — shape-scoped, never a
        // fallback: `weir:` always resolves via the .weir/ walk (a file
        // literally containing a colon keeps the ./ spelling). Both bare
        // import shapes were already meaningful, so the vendored
        // spelling had to be distinct.
        let name = path.Substring 5

        match Contracts.findWeirDir dir with
        | Ok wd ->
            let modPath = IO.Path.GetFullPath(IO.Path.Combine(wd, "modules", name + ".weir"))

            if IO.File.Exists modPath then
                modPath
            else
                // one namespace, two homes [D:schema-types]: vendored
                // modules first, then generated types (.weir/types/ —
                // `weir gen types` output); a missing name still errors
                // through the modules path below
                let typesPath = IO.Path.GetFullPath(IO.Path.Combine(wd, "types", name + ".weir"))

                if IO.File.Exists typesPath then typesPath else modPath
        | Error _ ->
            // no .weir/ anywhere: a display path for the not-found error
            IO.Path.GetFullPath(IO.Path.Combine(dir, ".weir", "modules", name + ".weir"))
    else
        IO.Path.GetFullPath(IO.Path.Combine(dir, path))

// F#'s filename fallback for a bare `module`: capitalize the base name. A
// non-identifier filename has no derivation (name it, or import `as`).
let private deriveModuleName (absPath: string) : string option =
    let bare = IO.Path.GetFileNameWithoutExtension absPath

    if
        bare.Length > 0
        && System.Char.IsLetter bare[0]
        && bare |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')
    then
        Some(string (System.Char.ToUpper bare[0]) + bare.Substring 1)
    else
        None

// does evaluating this RHS at import run a command? — the weak purity rule
// [D:modules-v1]. Eager positions only, stops at lambdas: a command in a
// lambda body is deferred, so a param-ful `let f r = git …` is a function,
// not an import-time effect; only a paramless `let x = git …` is rejected.
// The walk lives in Check beside the re-enumeration machinery that
// shares it [D:reenum-warning].
let runsCommandT: Check.TypedExpr -> bool = Check.runsCommandT

// buffer-over-disk [D:modules-v1]: when the LSP sets this, an imported file's
// content is read through it (open editor buffers first, then disk) so an
// unsaved dependency checks against what the user sees; None -> CLI, plain
// disk (decision 14).
let importSourceOverride: (string -> string list option) option ref = ref None

// the import read, once into a Result [D:lockfile-confinement]: an
// unguarded Exists-then-ReadAllLines crashes on an unreadable file
// (mode 000), and the two-call shape (isNone then get) races a file
// deleted between them. Absent -> None (the "no file at …" message);
// present-but-unreadable -> Error naming the path (a located
// diagnostic, never a crash). The buffer override (LSP) is absent-or-present
// by its own contract, so it maps to Ok.
type private ImportRead =
    | ISource of string list
    | IAbsent
    | IUnreadable of string

let private readImportSource (absPath: string) : ImportRead =
    match importSourceOverride.Value with
    | Some f ->
        match f absPath with
        | Some lines -> ISource lines
        | None -> IAbsent
    | None ->
        try
            ISource(IO.File.ReadAllLines absPath |> Array.toList)
        with
        | :? IO.FileNotFoundException
        | :? IO.DirectoryNotFoundException -> IAbsent
        | ex ->
            // present but unreadable — permission denied, a directory in
            // the path, an IO fault; name the path and the reason, located
            IUnreadable ex.Message

// resolve + check an imported module to a LoadedModule, or an ImportError.
// The graph [D:modules-v1]: `cache` (normalized abs path -> module) checks a
// shared module once (diamonds); `chain` is the current DFS path of importing
// files, so a repeat is a cycle. Transitive: a reached module's own imports
// resolve too, sharing the cache with this module pushed on the chain.
// sigContract [D:unused-bindings]: a #sig file is a module whose
// members (version, exhaustive, Cmd) are read by the sig loader, not by
// the module's own text — the loader is the use, so the unused check
// stays off for that one consumer
let rec loadModuleCachedWith
    (sigContract: bool)
    (cache: System.Collections.Generic.Dictionary<string, LoadedModule>)
    (chain: string list)
    (importingAbsPath: string)
    (path: string)
    (importAs: string option)
    : Result<LoadedModule, ImportError> =
    let absPath = resolveImportPath importingAbsPath path

    // an import-statement error reports at the import line (File=None)
    let stmt msg =
        Error
            { File = None
              Line = 0
              Col = 0
              Message = msg }

    let notAModule =
        stmt $"{absPath} is not a module; add `module` at the top, or invoke it as a command"

    if
        path.StartsWith "weir:"
        && (let n = path.Substring 5 in n = "" || n.Contains "/" || n.Contains "\\" || n.Contains "..")
    then
        stmt "a weir: import names a vendored module, never a path — weir:<name>, no separators"
    elif absPath = importingAbsPath then
        stmt "a file cannot import itself"
    elif List.contains absPath chain then
        // a cycle — same detector as self-import, different rendering
        // (decision 9): render the loop by file name
        let recent = chain |> List.takeWhile ((<>) absPath)

        let loop =
            (absPath :: (List.rev recent @ [ absPath ]))
            |> List.map IO.Path.GetFileName
            |> String.concat " → "

        stmt $"import cycle: {loop}"
    elif cache.ContainsKey absPath then
        // a diamond's shared module is checked once; re-alias per import site
        let cached = cache[absPath]

        Ok
            { cached with
                Alias = importAs |> Option.defaultValue cached.NaturalName }
    else

    // read the source once [D:lockfile-confinement] — no Exists/read race,
    // and present-but-unreadable is its own located diagnostic, not a crash
    match readImportSource absPath with
    | IAbsent ->
        if path.StartsWith "weir:" then
            let n = path.Substring 5

            stmt
                $"no vendored module '{n}' ({absPath}) — vendor it: weir add module <host>/<org>/<repo>//<file>@<ref> --as {n} (a generated types module lands here via: weir gen types --schema {n})"
        else
            stmt $"cannot resolve import: no file at {absPath}"
    | IUnreadable reason -> stmt $"cannot read import {absPath}: {reason}"
    | ISource rawLines ->
        let body, bodyOffset, _ = scriptBody rawLines

        let assembled = body |> List.mapi (fun i l -> bodyOffset + i + 1, l) |> assemble // raw lines: assemble classifies/strips internally [D:content-bytes]

        match assembled with
        | Error msg -> stmt $"{absPath}: {msg}"
        | Ok [] -> notAModule
        | Ok(first :: rest) ->
            let baseTenv, _ = moduleBaseEnvs absPath

            match Parser.parseLineFull (resolver baseTenv) first.Text with
            | Ok(SModule(declName, _)) ->
                let natural = declName |> Option.orElseWith (fun () -> deriveModuleName absPath)

                match importAs |> Option.orElse natural with
                | None ->
                    stmt
                        $"cannot derive a module name from '{IO.Path.GetFileName absPath}'; name it (module Name) or import it as a name (import \"…\" as Name)"
                | Some alias ->
                    // transitive: a reached module's own imports resolve, sharing
                    // the cache, with this module pushed on the chain
                    let childLoader: ImportLoader =
                        fun p _ a -> loadModuleCachedWith false cache (absPath :: chain) absPath p a

                    // a module-content error reports at the module's own site
                    let at line col msg : Result<_, ImportError> =
                        Error
                            { File = Some absPath
                              Line = line
                              Col = col
                              Message = msg }

                    // unused bindings in the module's own text
                    // [D:unused-bindings]: signed members exempt at flush
                    let moduleUnused = UnusedTracker()

                    let rec go
                        (tenv: TypeEnv)
                        (sc: SigContext)
                        (implLines: (string * int) list)
                        (accBody: (LogicalLine * CheckedStmt) list)
                        (stmts: LogicalLine list)
                        =
                        match stmts with
                        | [] -> Ok(tenv, sc, List.rev implLines, List.rev accBody)
                        | (ll: LogicalLine) :: tail ->
                            match checkStatement true (Some sc) resolver childLoader tenv ll with
                            | Error d ->
                                // a deeper module's error (File already set)
                                // propagates unchanged; this module's own error
                                // takes this module's site
                                match d.File with
                                | Some _ ->
                                    Error
                                        { File = d.File
                                          Line = d.PhysLine
                                          Col = d.PhysCol
                                          Message = d.Message }
                                | None -> at d.PhysLine d.PhysCol d.Message
                            | Ok chk ->
                                match chk.Kind with
                                | KType decl -> go chk.Env sc implLines ((ll, CType decl) :: accBody) tail
                                | KImport lm -> go chk.Env sc implLines ((ll, CImport lm) :: accBody) tail
                                | KSig(name, sd) ->
                                    // the sig binds nothing yet [D:module-signatures];
                                    // the paired implementation is the value
                                    let sc' =
                                        { sc with
                                            Pending = Map.add name sd sc.Pending
                                            Signed = Set.add name sc.Signed }

                                    go chk.Env sc' implLines accBody tail
                                | KLet(_, _, te) when runsCommandT te ->
                                    at
                                        ll.Head
                                        1
                                        "a module 'let' cannot run a command at import — wrap it in a function (let f () = …), the command runs when a script calls it"
                                | KLetPat(_, _, te) when runsCommandT te ->
                                    at ll.Head 1 "a module 'let' cannot run a command at import"
                                | KLet(name, _, te) ->
                                    let sc' =
                                        { sc with
                                            Pending = Map.remove name sc.Pending
                                            Implemented = Set.add name sc.Implemented }

                                    let implLines' =
                                        if Set.contains name sc.Signed then
                                            (name, ll.Head) :: implLines
                                        else
                                            implLines

                                    moduleUnused.Feed ll chk
                                    go chk.Env sc' implLines' ((ll, CLet(name, te)) :: accBody) tail
                                | KLetPat(pat, schemes, te) ->
                                    let sc' =
                                        { sc with
                                            Implemented =
                                                schemes |> List.fold (fun s (n, _) -> Set.add n s) sc.Implemented }

                                    moduleUnused.Feed ll chk
                                    go chk.Env sc' implLines ((ll, CLetPat(pat, te)) :: accBody) tail
                                | KModule _ -> at ll.Head 1 "a file has at most one 'module' marker, and it comes first"
                                | KCmd _
                                | KExpr _ ->
                                    at
                                        ll.Head
                                        1
                                        "a module declares only — 'type' and 'let', no commands or bare expressions"

                    match go baseTenv SigContext.empty [] [] rest with
                    | Error e -> Error e
                    | Ok(finalTenv, sc, implLines, moduleBody) ->
                        // sig without impl = check error at the sig
                        // [D:module-signatures]; impl without sig = private
                        let orphan =
                            sc.Pending |> Map.toList |> List.sortBy (fun (_, sd) -> sd.Line) |> List.tryHead

                        // a signed member's /// doc belongs on the signature
                        // (one home) [D:module-signatures]
                        let docOnImpl =
                            lazy
                                (docAttachments rawLines
                                 |> List.tryPick (fun d ->
                                     implLines
                                     |> List.tryFind (fun (_, implLine) -> d.Line = implLine)
                                     |> Option.map (fun (n, _) -> n, d)))

                        let lawError =
                            match orphan with
                            | Some(name, sd) ->
                                Some(
                                    sd.Line,
                                    sd.Col,
                                    $"signature without implementation — no 'let {name} … = …' follows in this module"
                                )
                            | None ->
                                match docOnImpl.Value with
                                | Some(name, d) ->
                                    Some(
                                        max 1 (d.Line - List.length d.Doc),
                                        1,
                                        $"'{name}' is signed, and its /// doc belongs on the signature — move it above 'let {name} : …' (one doc home)"
                                    )
                                | None when not sigContract ->
                                    // an unsigned member the module never reads is
                                    // dead private code [D:unused-bindings]
                                    match moduleUnused.Flush sc.Signed true |> List.tryHead with
                                    | Some f -> Some(f.ULine, f.UCol, f.UMessage)
                                    | None -> None
                                | None -> None

                        match lawError with
                        | Some(l, c, msg) -> at l c msg
                        | None ->

                            // a module exports only its own types (from its Body's
                            // decls), not what it transitively imported (no re-export,
                            // decision 3); Members already exclude imported ones
                            // (those live under Modules[·], not Values)
                            let typeDefs =
                                moduleBody
                                |> List.choose (function
                                    | _, CType decl ->
                                        Map.tryFind decl.Name finalTenv.Types |> Option.map (fun d -> decl.Name, d)
                                    | _ -> None)

                            // the export split [D:module-signatures]: the signature
                            // is the export — signed members and declared types'
                            // constructors cross; unsigned members stay private
                            // (carried for the import-of-private error only)
                            let ctorNames =
                                typeDefs
                                |> List.collect (fun (_, d) ->
                                    match d with
                                    | Union u -> u.Cases |> List.map fst
                                    | Record _ -> [])
                                |> Set.ofList

                            let ownMembers =
                                finalTenv.Values
                                |> Map.toList
                                |> List.filter (fun (n, _) -> not (Map.containsKey n baseTenv.Values))

                            let members =
                                ownMembers
                                |> List.filter (fun (n, _) -> Set.contains n sc.Signed || Set.contains n ctorNames)

                            let privateMembers =
                                ownMembers
                                |> List.filter (fun (n, _) ->
                                    not (Set.contains n sc.Signed) && not (Set.contains n ctorNames))

                            let loaded =
                                { Alias = alias
                                  NaturalName = natural |> Option.defaultValue alias
                                  AbsPath = absPath
                                  TypeDefs = typeDefs
                                  Members = members
                                  PrivateMembers = privateMembers
                                  TypeNames = typeDefs |> List.map fst
                                  Body = moduleBody }

                            cache[absPath] <- loaded
                            Ok loaded
            | Ok _ -> notAModule
            | Error _ -> notAModule

// the module-body replayer [D:modules-v1]: evaluate a checked module's Body
// in its own clean env (Self.scriptPath is the module's; process facts ride
// from the entry), exposing a nested import's members as `alias.member` for
// this module's own lets. Returns the module's venv (bindings under bare
// names); the caller exposes this module's Members qualified.
let rec replayModule (procFacts: (string * Eval.Value) list) (lm: LoadedModule) : Eval.Env =
    let _, mBase0 = moduleBaseEnvs lm.AbsPath
    let mBase = procFacts |> List.fold (fun m (k, v) -> Map.add k v m) mBase0

    let expose (alias: string) (members: (string * Scheme) list) (from: Eval.Env) (into: Eval.Env) =
        members
        |> List.fold
            (fun acc (n, _) ->
                match Map.tryFind n from with
                | Some v -> Map.add $"{alias}.{n}" v acc
                | None -> acc)
            into

    let rec replay (mv: Eval.Env) body =
        match body with
        | [] -> mv
        | (_, CType decl) :: t ->
            let mv' =
                match decl.Body with
                | DUnion cases -> Eval.constructorValues cases |> List.fold (fun m (n, v) -> Map.add n v m) mv
                | DRecord _ -> mv

            replay mv' t
        | (_, CLet(name, te)) :: t -> replay (Map.add name (Eval.eval mv te) mv) t
        | (_, CLetPat(pat, te)) :: t ->
            let bs = Eval.bindPattern pat (Eval.eval mv te)
            replay (bs |> List.fold (fun m (n, v) -> Map.add n v m) mv) t
        | (_, CImport nested) :: t ->
            let nestedVenv = replayModule procFacts nested
            replay (expose nested.Alias nested.Members nestedVenv mv) t
        | _ :: t -> replay mv t

    replay mBase lm.Body

// the -e / REPL import loader [D:modules-v1]: there is no file to resolve
// relative paths against, so import is script-only (decision 12)
/// add-time validation for a fetched module file [D:add-module]: the
/// same loader imports use — is-a-module, the module purity rules, a
/// full typecheck — with imports refused first: vendored modules are
/// leaves for now (a current boundary, not a permanent one). Returns
/// the member count for the add's report line.
let checkVendoredModule (absPath: string) : Result<int, string> =
    match readImportSource absPath with
    | IAbsent -> Error $"cannot read {absPath}"
    | IUnreadable reason -> Error $"cannot read {absPath}: {reason}"
    | ISource raws ->
        if raws |> List.exists (fun l -> l.StartsWith "import ") then
            Error
                "the module imports — vendored modules are leaves for now (transitive vendoring is a current boundary, not a refusal of the idea): inline the dependency, or vendor both and import each"
        else
            let cache = Collections.Generic.Dictionary()
            // a phantom importer in the same directory: never equal to
            // absPath, so the self-import guard stays quiet
            let phantom = absPath + ".add"

            match loadModuleCachedWith false cache [] phantom absPath None with
            | Ok lm -> Ok(List.length lm.Members)
            | Error e ->
                let where =
                    match e.File with
                    | Some f -> $"{f}:{e.Line}:{e.Col}: "
                    | None -> ""

                Error $"{where}{e.Message}"

let scriptOnlyImport: ImportLoader =
    fun _ _ _ ->
        Error
            { File = None
              Line = 0
              Col = 0
              Message =
                "import is script-only — it needs a file to resolve its path against (not available with -e or in the REPL)" }

// ---------------------------------------------------------------------------
// weir check [--json] [D:check-lsp-chain]. Check-everything, no
// evaluation by construction (this function cannot reach Eval).
// Statement-level error recovery: a failed statement records its diag
// and checking continues with the env unchanged, so a multi-error file
// reports every independent error. Codes are seeded from the message
// families (structured codes at error origin are the parked upgrade).

type Diagnostic =
    { File: string
      Line: int
      Col: int
      EndLine: int option
      EndCol: int option
      Severity: string // "error" | "warning"
      Code: string
      Message: string }

let private codeOf (parse: bool) (msg: string) : string =
    if parse then
        "parse"
    elif msg.StartsWith "binding names start lowercase" then
        "casing-law"
    elif msg.Contains "discards it" then
        "discard"
    elif msg.StartsWith "a sequenced expression must be unit" then
        "seq-unit"
    elif msg.StartsWith "this pattern can fail" then
        "refutable-binder"
    elif msg.StartsWith "match is not exhaustive" || msg.Contains "needs a catch-all" then
        "non-exhaustive"
    elif msg.Contains "unreachable" then
        "unreachable-arm"
    elif msg.StartsWith "invalid regex" then
        "regex"
    elif msg.StartsWith "this regex has" then
        "regex-arity"
    elif msg.StartsWith "cannot sort by this key" then
        "ord-key"
    elif msg.Contains "equatable" || msg.Contains "cannot be compared" then
        "eq"
    elif msg.Contains "cannot render functions" then
        "show-fn"
    elif msg.StartsWith "unbound variable" then
        "unbound"
    elif msg.Contains "nothing determines" then
        "ambiguous-constraint"
    else
        "check"

// AOT-safe JSON writing: Utf8JsonWriter (reflection-free, the write
// twin of the JsonDocument reader) — escaping is the library's job,
// never string interpolation's. UnsafeRelaxedJsonEscaping: "unsafe"
// means HTML-embedding only — these payloads are LSP/CLI, never HTML;
// the default encoder's \u0022-style quote escaping is valid but
// trips naive clients [D:json-relaxed-escaping]
let private jsonWriterOptions =
    System.Text.Json.JsonWriterOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let jsonBuild (build: System.Text.Json.Utf8JsonWriter -> unit) : string =
    use ms = new IO.MemoryStream()
    use w = new System.Text.Json.Utf8JsonWriter(ms, jsonWriterOptions)
    build w
    w.Flush()
    Text.Encoding.UTF8.GetString(ms.ToArray())

let writeDiag (w: System.Text.Json.Utf8JsonWriter) (d: Diagnostic) =
    w.WriteStartObject()
    // one file identity per document [D:ci-matrix-triage round 26]: an
    // imported module's diag carried the resolved path while the
    // importer's carried argv's spelling verbatim — on Windows the same
    // dir under two spellings (8.3 vs long, / vs \), breaking any
    // consumer that groups by file. GetFullPath is the one spelling.
    w.WriteString(
        "file",
        (try
            IO.Path.GetFullPath d.File
         with _ ->
             d.File)
    )

    w.WriteNumber("line", d.Line)
    w.WriteNumber("col", d.Col)

    match d.EndLine, d.EndCol with
    | Some el, Some ec ->
        w.WriteNumber("endLine", el)
        w.WriteNumber("endCol", ec)
    | _ -> ()

    w.WriteString("severity", d.Severity)
    w.WriteString("code", d.Code)
    w.WriteString("message", d.Message)
    w.WriteEndObject()

// doc-comment alignment lint [D:doc-comments]: a `///` attaches to the
// declaration below it, so it must sit at that declaration's indent —
// the entry anchor, exactly as an attribute line does. Docs are inert
// (dropped before assembly), so a misaligned one cannot mis-parse: this
// is a lint, not the parse-time attribute machinery.
let private docMisalignments (path: string) (lines: string list) : Diagnostic list =
    let indent (s: string) = s.Length - s.TrimStart().Length
    let arr = List.toArray lines
    let diags = ResizeArray<Diagnostic>()
    let mutable runStart = -1 // index of the first `///` in the pending run

    let masked = districtContentMask lines

    lines
    |> List.iteri (fun idx raw ->
        if masked[idx] then
            () // district content is bytes — the lint has no claim [D:content-bytes]
        elif isDocLine raw then
            (if runStart < 0 then
                 runStart <- idx)
        elif isAttributeOnlyLine raw then
            () // transparent: the doc rides through to the declaration below
        elif raw.Trim() = "" || (stripComment raw).Trim() = "" then
            runStart <- -1 // a blank / plain-// breaks the run — no attachment, no claim
        else
            (if runStart >= 0 then
                 match declName raw with
                 | Some _ ->
                     let anchor = indent raw

                     for k in runStart .. idx - 1 do
                         let di = indent arr[k]

                         if di <> anchor then
                             diags.Add
                                 { File = path
                                   Line = k + 1
                                   Col = di + 1
                                   EndLine = None
                                   EndCol = None
                                   Severity = "error"
                                   Code = "doc-align"
                                   Message =
                                     $"this /// doc sits at column {di + 1}, but the declaration it documents is at column {anchor + 1} — a doc aligns with what it describes" }
                 | None -> ())

            runStart <- -1)

    List.ofSeq diags

// full analysis for tooling (the LSP re-frames this): diagnostics and
// the successfully-checked statements with their logical lines — plus
// the initial env, so consumers can pick the in-scope env per position
// external contracts: schema validation [D:yaml-schemas]. Walks every
// typed district carrying a `schema=` declaration; structural checks
// always, value checks where the splice's type permits. Check-time
// only, and it reads vendored files exclusively — check never fetches
// from the network.
/// resolve a schema name from a script's path to Ok (weirDir, vendored
/// file) — the walk the checker uses, with its restore-vs-add guidance
/// on a miss; the schema= hover and definition resolve through this
/// function so the editor and the checker cannot diverge [D:schema-hover]
let resolveSchemaFile (path: string) (name: string) : Result<string * string, string> =
    let fromDir =
        try
            let full = IO.Path.GetFullPath path
            let d = IO.Path.GetDirectoryName full
            if String.IsNullOrEmpty d then "." else d
        with _ ->
            "."

    match Contracts.findWeirDir fromDir with
    | Error e -> Error $"schema '{name}': {e}"
    | Ok weirDir ->
        let file = IO.Path.Combine(weirDir, "schemas", name + ".json")

        if not (IO.File.Exists file) then
            // the checker can tell restore from add: a lock
            // entry means the artifact was declared (a fresh
            // clone restores); no entry means it never was
            let locked =
                match Contracts.readLock weirDir with
                | Ok entries -> entries |> List.exists (fun e -> e.Kind = "schema" && e.Name = name)
                | Error _ -> false

            if locked then
                Error $"schema '{name}': no {file} (searched from {weirDir}) — the lock records it; run `weir restore`"
            else
                Error
                    $"schema '{name}': no {file} (searched from {weirDir}) — add it: weir add schema <url> --as {name}"
        else
            Ok(weirDir, file)

let schemaDiagnostics (path: string) (pairs: (LogicalLine * CheckedStatement) list) : Diagnostic list =
    let cache =
        System.Collections.Generic.Dictionary<string, Result<Contracts.SchemaDoc, string>>()

    let loadSchema (name: string) : Result<Contracts.SchemaDoc, string> =
        match cache.TryGetValue name with
        | true, r -> r
        | _ ->
            let r =
                match resolveSchemaFile path name with
                | Error e -> Error e
                | Ok(_, file) -> Contracts.parseSchema name (IO.File.ReadAllText file)

            cache[name] <- r
            r

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

        let rec districts (te: Check.TypedExpr) =
            (match te.Kind with
             | Check.TEYaml(tpl, Some name, _) -> [ te.Span, name, tpl ]
             | _ -> [])
            @ (Check.childExprs te |> List.collect districts)

        roots
        |> List.collect districts
        |> List.collect (fun (dspan, name, tpl) ->
            let mk (sp: Span) (msg: string) =
                let l1, c1 = translate ll sp.Start.Col
                let l2, c2 = translate ll (max sp.Start.Col (sp.End.Col - 1))

                { File = path
                  Line = l1
                  Col = c1
                  EndLine = Some l2
                  EndCol = Some(c2 + 1)
                  Severity = "error"
                  Code = "schema"
                  Message = msg }

            match loadSchema name with
            | Error e -> [ mk dspan e ]
            | Ok doc ->
                Contracts.validateTpl name doc.Defs "" doc.Root tpl
                |> List.map (fun (sp, m) -> mk sp m)))

// ---- external contracts: command signatures [D:command-signatures] --------
// a loaded signature's checkable surface. Subs: kebab-cased subcommand
// -> (long-flag set, explicit-short set); the "" key is the flag-only
// shape (Cmd declared as a record). Shorts are explicit [<Short>] only —
// weir's own derivation is a convention, and a signature records a
// foreign tool's facts.
type SigInfo =
    { Tool: string
      DeclLine: int
      Exhaustive: bool
      Subs: Map<string, Set<string> * Set<string>>
      // the resolved sig file, its recorded version, and each surface's
      // record type name — what hover/definition need to reach the
      // declaration site [D:lsp-cross-file]
      SigPath: string
      Version: string
      SubRecords: Map<string, string> }

let private sigFlagSets (def: RecordDef) : Set<string> * Set<string> =
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

    let longs =
        def.Fields
        |> List.filter (fun (f, _) -> not (positional f))
        |> List.collect (fun (f, _) -> Weir.Argv.kebabFlag f :: wiresOf f)
        |> Set.ofList

    let shorts = Weir.Argv.explicitShorts def |> List.map snd |> Set.ofList
    longs, shorts

/// a PATH-y tool name ("./rooz/v3/lib/jp", "~/.azure/bin/bicep") maps
/// to one flat filename under .weir/sigs — separators become '_'
/// [D:scoped-sigs]. Never a nested tree, and never Path.Combine on the
/// raw name: given an absolute tool path Combine discards the sigs dir
/// and aims outside .weir entirely.
let sigFileName (tool: string) : string =
    (tool
     |> String.map (fun c ->
         if System.Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' then
             c
         else
             '_'))
        .TrimStart('.')
    + ".weir"

/// load the signatures a file declared; errors become diagnostics at
/// the declaring line. The sig file is an ordinary weir module
/// (decl-only + weak purity for free): `module X`, `let version =
/// "<--version's first line>"`, optionally `let exhaustive = true`, and
/// the surface as the type named `Cmd`.
let loadSigs (path: string) (decls: SigDecl list) : Diagnostic list * SigInfo list =
    let diags = ResizeArray<Diagnostic>()
    let infos = ResizeArray<SigInfo>()

    let mk (line: int) (msg: string) : Diagnostic =
        { File = path
          Line = line
          Col = 1
          EndLine = None
          EndCol = None
          Severity = "error"
          Code = "sig"
          Message = msg }

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
            diags.Add(mk decl.Line $"#sig takes a bare tool name — drop the quotes: #sig {bare}")
        else
            let resolved =
                match decl.Override with
                | Some p -> Ok(IO.Path.GetFullPath(IO.Path.Combine(scriptDir, p)))
                | None ->
                    match Contracts.findWeirDir scriptDir with
                    | Error e -> Error $"#sig {decl.Tool}: {e}"
                    | Ok weirDir -> Ok(IO.Path.Combine(weirDir, "sigs", sigFileName decl.Tool))

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
                        $"generate it: weir add sig {decl.Tool}"

                diags.Add(mk decl.Line $"#sig {decl.Tool}: no {sigFile} (searched from {scriptDir}) — {hint}")
            | Ok sigFile ->
                let cache = System.Collections.Generic.Dictionary<string, LoadedModule>()
                let absScript = IO.Path.GetFullPath path

                match loadModuleCachedWith true cache [ absScript ] absScript sigFile None with
                | Error ie -> diags.Add(mk decl.Line $"#sig {decl.Tool}: {ie.Message}")
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
                    let recordOf name =
                        lm.TypeDefs
                        |> List.tryPick (function
                            | (n, Record rd) when n = name -> Some rd
                            | _ -> None)

                    match lm.TypeDefs |> List.tryFind (fun (n, _) -> n = "Cmd") with
                    | None ->
                        diags.Add(
                            mk
                                decl.Line
                                $"#sig {decl.Tool}: the signature declares no type `Cmd` — the command surface is the type named Cmd (a union of subcommands, or a record of flags)"
                        )
                    | Some(_, Record rd) ->
                        infos.Add
                            { Tool = decl.Tool
                              DeclLine = decl.Line
                              Exhaustive = letBool "exhaustive" |> Option.defaultValue false
                              Subs = Map [ "", sigFlagSets rd ]
                              SigPath = sigFile
                              Version = letStr "version" |> Option.defaultValue ""
                              SubRecords = Map [ "", "Cmd" ] }
                    | Some(_, Union ud) ->
                        let subs =
                            ud.Cases
                            |> List.map (fun (case, payload) ->
                                let flags =
                                    match payload with
                                    | Some(TNamed(rn, [])) ->
                                        match recordOf rn with
                                        | Some rd -> sigFlagSets rd
                                        | None -> Set.empty, Set.empty
                                    | _ -> Set.empty, Set.empty

                                Weir.Argv.kebabFlag case, flags)
                            |> Map.ofList

                        let subRecords =
                            ud.Cases
                            |> List.choose (fun (case, payload) ->
                                match payload with
                                | Some(TNamed(rn, [])) when (recordOf rn).IsSome -> Some(Weir.Argv.kebabFlag case, rn)
                                | _ -> None)
                            |> Map.ofList

                        infos.Add
                            { Tool = decl.Tool
                              DeclLine = decl.Line
                              Exhaustive = letBool "exhaustive" |> Option.defaultValue false
                              Subs = subs
                              SigPath = sigFile
                              Version = letStr "version" |> Option.defaultValue ""
                              SubRecords = subRecords }

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
    | IUnreadable _ -> None

/// the signatures a file's #sig head declares, loaded; quiet on load
/// errors — diagnostics are analyzeLines' job [D:lsp-cross-file]
let sigInfosForFile (path: string) (rawLines: string list) : SigInfo list =
    let _, _, decls = scriptBody rawLines
    loadSigs path decls |> snd

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
                    let words =
                        args
                        |> List.choose (fun a ->
                            match a.Kind with
                            | Check.TEStr w -> Some(w, a.Span)
                            | _ -> None)

                    let surface =
                        match Map.tryFind "" si.Subs with
                        | Some fs -> Some(None, fs)
                        | None ->
                            // the longest run of leading sub words wins
                            // [D:scoped-sigs]: `issue list` matches the
                            // IssueList case before falling back to Issue —
                            // tokens join kebab-style, the loader's own key
                            let run =
                                words
                                |> List.skipWhile (fun (w, _) -> w.StartsWith "-")
                                |> List.takeWhile (fun (w, _) -> not (w.StartsWith "-"))
                                |> List.truncate 4
                                |> List.map fst

                            let hit =
                                [ List.length run .. -1 .. 1 ]
                                |> List.tryPick (fun n ->
                                    let toks = run |> List.truncate n
                                    let key = String.concat "-" toks

                                    Map.tryFind key si.Subs
                                    |> Option.map (fun fs -> Some(String.concat " " toks), fs))

                            match hit with
                            | Some hit -> Some hit
                            | None ->
                                // a sub-less line on a scoped sig checks the
                                // globals: what rides every case is global,
                                // so the intersection is the global set
                                // [D:scoped-sigs]. Empty intersection
                                // (hand-written unions that never duplicate)
                                // keeps the L2 skip — claude's flag-only
                                // lines check, git's sub-less lines stay
                                // silent
                                match si.Subs |> Map.toList |> List.map snd with
                                | [] -> None
                                | (l0, s0) :: rest ->
                                    let longs = rest |> List.fold (fun acc (l, _) -> Set.intersect acc l) l0

                                    let shorts = rest |> List.fold (fun acc (_, sh) -> Set.intersect acc sh) s0

                                    if longs.IsEmpty then None else Some(None, (longs, shorts))

                    match surface with
                    | None -> [] // no matching subcommand, no shared globals: L2 stops here
                    | Some(subName, (longs, shorts)) ->
                        // a scoped surface names its case in the warning
                        // [D:scoped-sigs]
                        let toolAndSub =
                            match subName with
                            | Some sub -> $"{si.Tool} {sub}"
                            | None -> si.Tool

                        let sev = if si.Exhaustive then "error" else "warning"

                        let note =
                            if si.Exhaustive then
                                $"(#sig {si.Tool}, line {si.DeclLine}; exhaustive signature)"
                            else
                                $"(#sig {si.Tool}, line {si.DeclLine}; partial signature — a scraped surface may be incomplete)"

                        let mk (sp: Span) (msg: string) =
                            let l1, c1 = translate ll sp.Start.Col
                            let l2, c2 = translate ll (max sp.Start.Col (sp.End.Col - 1))

                            { File = path
                              Line = l1
                              Col = c1
                              EndLine = Some l2
                              EndCol = Some(c2 + 1)
                              Severity = sev
                              Code = "sig"
                              Message = msg }

                        let rec check acc ws =
                            match ws with
                            | [] -> acc
                            | ("--", _) :: _ -> acc // end-of-flags: everything after is operands
                            | (w: string, sp) :: rest when w.StartsWith "--" ->
                                let name = w.Substring(2).Split('=')[0]

                                let acc =
                                    if longs.Contains name then
                                        acc
                                    else
                                        let dym =
                                            Weir.Types.didYouMean ("--" + name) (longs |> Seq.map (fun l -> "--" + l))

                                        mk sp $"unknown flag '--{name}' for {toolAndSub}{dym} {note}" :: acc

                                check acc rest
                            | (w, sp) :: rest when w.StartsWith "-" && w.Length = 2 && not (System.Char.IsDigit w[1]) ->
                                // a surface that recorded no shorts has no
                                // evidence to warn on (BSD grep's help is
                                // usage-only — the harvest sees longs, never
                                // the short bundle) [D:sig-version-probe]
                                let acc =
                                    if shorts.IsEmpty || shorts.Contains(w.Substring 1) then
                                        acc
                                    else
                                        mk sp $"unknown flag '{w}' for {toolAndSub} {note}" :: acc

                                check acc rest
                            | _ :: rest -> check acc rest

                        check [] words |> List.rev))

// ---- `weir add sig <tool>`: generation [D:command-signatures] --------------
// Three sources in fidelity order, the chosen one recorded in the
// provenance comment and the lock's Url slot ("generated:<source>"):
// a completion endpoint (`<tool> completion fish` — Cobra/clap emit
// parseable `complete` lines), a shipped fish completion file, then
// `--help` scraping. v1 generates a flat surface (one Cmd record —
// flags across the whole line); splitting into subcommand records is
// the hand edit the provenance comment invites. The generated file
// is validated (loads as a signature) before anything persists.
module SigGen =
    let private runToolWith (anyExit: bool) (tool: string) (args: string) : string option =
        // probeRung's guards [D:sig-version-probe]: null stdin (a
        // stdin-reader must not hang generation), temp cwd, async reads
        // ahead of a bounded wait, kill on expiry
        try
            let psi = System.Diagnostics.ProcessStartInfo(Proc.resolveProg tool, args)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.RedirectStandardInput <- true
            psi.UseShellExecute <- false
            psi.WorkingDirectory <- IO.Path.GetTempPath()
            use p = System.Diagnostics.Process.Start psi
            p.StandardInput.Close()
            let outTask = p.StandardOutput.ReadToEndAsync()
            let errTask = p.StandardError.ReadToEndAsync()

            if not (p.WaitForExit 15000) then
                (try
                    p.Kill true
                 with _ ->
                     ())

                None
            elif p.ExitCode = 0 || anyExit then
                let out = outTask.Result

                match Contracts.stripAnsi (if out.Trim() <> "" then out else errTask.Result) with
                | blank when blank.Trim() = "" -> None
                | text -> Some text
            else
                None
        with _ ->
            None

    let private runTool = runToolWith false

    // BSD grep's --help exits 2 with the usage on stderr — a nonzero
    // exit's dump is harvest-only material [D:sig-version-probe]:
    // never structured rows, never the walk, never a bare-word gate
    let private runToolAnyExit = runToolWith true

    // one discovered flag: long name (kebab), optional short, optional doc
    type private Flag =
        { Long: string
          Short: string option
          Doc: string option }

    let private legalLong (l: string) =
        l.Length > 0
        && System.Char.IsLetter l[0]
        && l |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '-')

    let private parseFish (text: string) (tool: string) : Flag list =
        // `complete -c <tool> … -l long [-s x] [-d 'desc']` — token walk,
        // quote-aware enough for the -d payload
        let flags = System.Collections.Generic.Dictionary<string, Flag>()

        for line in text.Split '\n' do
            let t = line.Trim()

            if t.StartsWith "complete " && t.Contains $"-c {tool}" then
                let tokens =
                    System.Text.RegularExpressions.Regex.Matches(t, "'[^']*'|\"[^\"]*\"|\\S+")
                    |> Seq.map (fun m -> m.Value.Trim([| '\''; '"' |]))
                    |> List.ofSeq

                let rec walk (long: string option) (short: string option) (doc: string option) toks =
                    match toks with
                    | "-l" :: v :: rest -> walk (Some v) short doc rest
                    | "-s" :: v :: rest -> walk long (Some v) doc rest
                    | "-d" :: v :: rest -> walk long short (Some v) rest
                    | _ :: rest -> walk long short doc rest
                    | [] -> long, short, doc

                match walk None None None tokens with
                | Some l, sh, d when legalLong l ->
                    if not (flags.ContainsKey l) then
                        flags[l] <- { Long = l; Short = sh; Doc = d }
                | _ -> ()

        flags.Values |> List.ofSeq

    let private parseHelp (text: string) : Flag list =
        // `  -o, --outfile <x>  desc` / `  --stdout  desc` — the usual
        // help shapes; unreliable by design, a starting point
        let flags = System.Collections.Generic.Dictionary<string, Flag>()

        // Go-flag rows (micro): `-clean` / `-config-dir dir`, description
        // on the next line — single-dash multi-char is a long (Go accepts
        // --clean too); the last flag with no doc adopts a following
        // deeper prose line
        let mutable lastDocless: string option = None

        for raw in text.Split '\n' do
            // broot draws its options as a box table — the border and
            // column glyphs (U+2500..U+257F) become spaces and the rows
            // collapse into the standard `-d  --dates  desc` shape
            let line = System.Text.RegularExpressions.Regex.Replace(raw, "[\u2500-\u257F]", " ")

            let goRow =
                System.Text.RegularExpressions.Regex.Match(line, "^\\s*-([a-zA-Z][a-zA-Z0-9-]+)( \\S+)?\\s*$")

            let m =
                System.Text.RegularExpressions.Regex.Match(
                    line,
                    // `+s, --no-sort` — fzf-style off toggles: the +x is
                    // skipped, never recorded as a short. The tail is
                    // walked procedurally: az spells rows as
                    // `--flag --alias -s [Required] : doc`
                    "^\\s+(?:(?:-(\\w)|\\+\\w),?\\s+)?--([a-zA-Z][a-zA-Z0-9-]*)(.*)$"
                )

            if goRow.Success && not m.Success then
                let long = goRow.Groups[1].Value

                if legalLong long && not (flags.ContainsKey long) then
                    flags[long] <-
                        { Long = long
                          Short = None
                          Doc = None }

                    lastDocless <- Some long
            elif
                not m.Success
                && line.StartsWith "        "
                && line.Trim() <> ""
                && not (line.TrimStart().StartsWith "-")
            then
                match lastDocless with
                | Some l when flags.ContainsKey l && flags[l].Doc.IsNone ->
                    flags[l] <-
                        { flags[l] with
                            Doc = Some(line.Trim()) }

                    lastDocless <- None
                | _ -> ()
            elif m.Success then
                let long = m.Groups[2].Value

                let mutable short = if m.Groups[1].Success then Some m.Groups[1].Value else None

                let aliases = ResizeArray<string>()

                // walk the tail: aliases and shorts join the flag, arg
                // shapes (=X, <x>, [X], bare all-caps) are skipped, and the
                // doc starts at az's `:` or the first prose word
                let mutable rest = m.Groups[3].Value.TrimStart()
                let mutable walking = true

                while walking && rest <> "" do
                    let tok, tail =
                        match rest.IndexOf ' ' with
                        | -1 -> rest, ""
                        | i -> rest.Substring(0, i), rest.Substring(i + 1).TrimStart()

                    if tok = "," then
                        // claude spells `--allowedTools, --allowed-tools <x>` —
                        // the bare comma between aliases is separator noise
                        rest <- tail
                    elif tok.StartsWith "--" && legalLong (tok.TrimEnd(',').TrimStart '-') then
                        aliases.Add(tok.TrimEnd(',').TrimStart '-')
                        rest <- tail
                    elif tok.Length = 2 && tok[0] = '-' && System.Char.IsLetterOrDigit tok[1] then
                        if short.IsNone then
                            short <- Some(string tok[1])

                        rest <- tail
                    elif
                        tok.StartsWith "="
                        || (tok.StartsWith "<" && tok.EndsWith ">")
                        || (tok.StartsWith "[" && tok.EndsWith "]")
                        || (tok.Length > 1 && tok |> Seq.forall (fun c -> System.Char.IsUpper c || c = '_'))
                    then
                        rest <- tail
                    elif tok = ":" then
                        rest <- tail
                        walking <- false
                    else
                        walking <- false

                let doc = rest.TrimStart(':', ' ')

                let record l =
                    if legalLong l && not (flags.ContainsKey l) then
                        flags[l] <-
                            { Long = l
                              Short = (if l = long then short else None)
                              Doc = (let d = doc.Trim() in if d = "" then None else Some d) }

                record long

                for a in aliases do
                    record a

                lastDocless <- None

        flags.Values |> List.ofSeq

    // the last-resort pass [D:sig-version-probe]: a usage-table help
    // (weir's own: `weir check [--json] <script>`) has no flag rows, so
    // the structured scrape finds nothing — harvest every --flag token
    // instead, docless. Only ever consulted at zero structured hits;
    // partial by default covers the imprecision.
    let private harvestHelp (text: string) : Flag list =
        System.Text.RegularExpressions.Regex.Matches(text, "--([a-zA-Z][a-zA-Z0-9-]*)")
        |> Seq.map (fun m -> m.Groups[1].Value)
        |> Seq.filter legalLong
        |> Seq.distinct
        |> Seq.map (fun long ->
            { Long = long
              Short = None
              Doc = None })
        |> List.ofSeq

    // subcommand tokens advertised by a help page: indented first words
    // under any heading ending in "commands:" (Cobra's "Available
    // Commands:", kubectl's grouped "Basic Commands (Beginner):").
    // `help`/`completion` are noise on every such tool and are skipped.
    let private subcommandTokens (helpText: string) : string list =
        let subs = ResizeArray<string>()
        let mutable inSection = false

        for raw in helpText.Split '\n' do
            let line = raw.TrimEnd()

            // a heading is non-indented and names commands — with a
            // colon (Cobra's "Available Commands:") or as an all-caps
            // banner (jira's "MAIN COMMANDS" / "OTHER COMMANDS")
            if
                line.Length > 0
                && not (System.Char.IsWhiteSpace line[0])
                && line.ToLowerInvariant().Contains "command"
                && (line.EndsWith ":" || line = line.ToUpperInvariant())
            then
                inSection <- true
            elif line = "" || (line.Length > 0 && not (System.Char.IsWhiteSpace line[0])) then
                inSection <- false

            if inSection && line.StartsWith "  " then
                // gh spells its command tables `auth:  Authenticate…` —
                // the trailing colon is punctuation, not the token
                let tok = (line.TrimStart().Split(' ') |> Array.head).TrimEnd ':'

                if
                    tok.Length > 1
                    && tok
                       |> Seq.forall (fun c -> System.Char.IsLower c || System.Char.IsDigit c || c = '-')
                    && not (subs.Contains tok)
                then
                    subs.Add tok

        List.ofSeq subs

    // the subcommand walk [D:sig-version-probe]: Cobra-family tools
    // (jira, kubectl) keep the real flags under `tool sub --help` — the
    // flat surface unions them (flags checked across the whole line is
    // the flat model's own charter). Depth 2 (`jira issue list`),
    // budget-capped; structured rows only, never the harvest (a
    // sub-page harvest over-collects).
    // provenance kept [D:scoped-sigs]: flags group under their level-1
    // subcommand (deeper levels flatten into their root's group), so
    // the emitter can scope. Depth 4 (`kustomize edit add resource`),
    // breadth-first under the budget — levels complete in order, so a
    // big tool degrades to shallow-but-wide, never deep-but-lopsided
    let private walkSubFlags (tool: string) (topHelp: string) : (string * Flag list) list * int * int =
        let groups = System.Collections.Generic.Dictionary<string, ResizeArray<Flag>>()
        let order = ResizeArray<string>()
        let mutable budget = 60
        let mutable probed = 0
        let mutable answered = 0

        // breadth-first: level 1 completes before level 2 spends a
        // probe — depth-first lets an early subcommand's children starve
        // the rest of docker's forty top-level commands
        // help/completion are advertised everywhere and walk nowhere
        let noise sub = sub = "help" || sub = "completion"

        let mutable level =
            [ for sub in subcommandTokens topHelp do
                  if not (noise sub) then
                      "", sub ]

        for _ in 1..4 do
            let next = ResizeArray<string * string>()

            for prefix, sub in level do
                if budget > 0 then
                    budget <- budget - 1
                    probed <- probed + 1

                    match runTool tool $"{prefix}{sub} --help" with
                    | None -> ()
                    | Some subHelp ->
                        answered <- answered + 1
                        let path = $"{prefix}{sub}"

                        if not (groups.ContainsKey path) then
                            groups[path] <- ResizeArray<Flag>()
                            order.Add path

                        groups[path].AddRange(parseHelp subHelp)

                        for deeper in subcommandTokens subHelp do
                            if not (noise deeper) then
                                next.Add($"{prefix}{sub} ", deeper)

            level <- List.ofSeq next

        [ for root in order do
              if groups[root].Count > 0 then
                  root, List.ofSeq groups[root] ],
        probed,
        answered

    // the source label rides the sig comment — a harvested surface is
    // weaker lineage than parsed rows, and says so
    let private helpSurface
        (tool: string)
        (topHelp: string option)
        (harvestOnly: string option)
        : string * Flag list * (string * Flag list) list =
        match topHelp with
        | None ->
            match harvestOnly with
            | Some dump ->
                match harvestHelp dump with
                | [] -> "help", [], []
                | fs -> "help-scan", fs, []
            | None -> "help", [], []
        | Some text ->
            let source, top =
                match parseHelp text with
                | [] -> "help-scan", harvestHelp text
                | fs -> "help", fs

            match walkSubFlags tool text with
            // the walk's outcome is observable either way [D:sig-version-probe]:
            // "help" alone cannot say whether subcommands were never
            // advertised, never answered, or answered nothing — the counts do
            | [], 0, _ -> source, top, []
            | [], probed, answered ->
                $"{source} (walked {probed} subcommand help(s), {answered} answered, none yielded flags)", top, []
            | groups, _, _ -> source + "+subs", top, groups

    let private fieldName (long: string) =
        // inverse kebab: dry-run -> dryRun
        long.Split '-'
        |> Array.mapi (fun i part ->
            if i = 0 || part = "" then
                part
            else
                string (System.Char.ToUpper part[0]) + part.Substring 1)
        |> String.concat ""

    let private escape (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n")

    let generate (weirDir: string) (tool: string) : Result<string, string> =
        // Flag probes are safe on any tool; a bare word is not — `code
        // completion fish` opens VS Code on two files
        // [D:sig-version-probe]. Bare-word probes (completion fish, the
        // version word) run only when the tool's own --help advertises
        // that subcommand.
        match Contracts.probeVersionFlag Proc.resolveProg tool with
        | Contracts.ToolAbsent ->
            // a file that exists but would not run is a different wrong
            // turn than a missing tool — `add sig rooz/v3/xr.yaml` is a
            // data file, not a binary
            if IO.File.Exists tool then
                Error $"'{tool}' exists but did not run — a signature describes a runnable tool; is this a data file?"
            else
                Error $"'{tool}' is not on PATH — generation probes the installed binary"
        | rung1 ->
            let topHelp = runTool tool "--help"

            // exit-0 help only gates and walks; a refused --help still
            // yields its usage dump to the harvest below
            let harvestOnly =
                match topHelp with
                | Some _ -> None
                | None -> runToolAnyExit tool "--help"

            let advertised = topHelp |> Option.map subcommandTokens |> Option.defaultValue []

            // a tool that refuses --version has no recorded identity
            // [D:sig-version-probe] — the refusal's usage dump is not a
            // version, and recording it leaks paths into the sig
            let version =
                match rung1 with
                | Contracts.ToolVersion raw -> Some(raw.Trim())
                | _ when List.contains "version" advertised ->
                    match Contracts.probeVersionWord Proc.resolveProg tool with
                    | Contracts.ToolVersion raw -> Some(raw.Trim())
                    | _ -> None
                | _ -> None

            let fishText =
                if List.contains "completion" advertised then
                    runTool tool "completion fish"
                else
                    None

            let source, flags, subGroups =
                match fishText with
                | Some text when text.Contains "complete " ->
                    match parseFish text tool with
                    | [] -> helpSurface tool topHelp harvestOnly
                    | fs -> "completion-fish", fs, []
                | _ ->
                    let shipped =
                        [ $"/usr/share/fish/completions/{tool}.fish"
                          $"/usr/share/fish/vendor_completions.d/{tool}.fish" ]
                        |> List.tryFind IO.File.Exists

                    match shipped with
                    | Some f ->
                        match parseFish (IO.File.ReadAllText f) tool with
                        | [] -> helpSurface tool topHelp harvestOnly
                        | fs -> "fish-file", fs, []
                    | None -> helpSurface tool topHelp harvestOnly

            match flags, subGroups with
            | [], [] ->
                Error
                    $"'{tool}': found no flags to record (probed: completion fish, shipped fish files, --help) — write .weir/sigs/{tool}.weir by hand"
            | flags, subGroups ->
                // a path-y tool name must still mint a legal module
                // name: letters and digits only, letter-first (an
                // absolute path would otherwise mint `module /Users…`)
                let moduleName =
                    let core = tool |> String.filter System.Char.IsLetterOrDigit

                    if core = "" then "Sig"
                    elif System.Char.IsDigit core[0] then "Sig" + core
                    else string (System.Char.ToUpper core[0]) + core.Substring 1

                let sb = System.Text.StringBuilder()
                let line (l: string) = sb.AppendLine l |> ignore

                // one record body, shared by the flat shape and every
                // union case: same-camel spellings merge (the field
                // carries one Wire for the spelling its kebab does not
                // cover), shorts dedup within the record (per case —
                // docker's -a on all and all-tags live apart), and
                // keyword longs take the Flag suffix
                let renderRecord (name: string) (flags: Flag list) =
                    line $"type {name} = {{"

                    let flags =
                        let taken = System.Collections.Generic.HashSet<string>()

                        flags
                        |> List.sortBy (fun f -> f.Long)
                        |> List.map (fun f ->
                            match f.Short with
                            | Some sh when not (taken.Add sh) -> { f with Short = None }
                            | _ -> f)

                    let grouped = flags |> List.groupBy (fun f -> fieldName f.Long) |> List.sortBy fst

                    for fname, group in grouped do
                        match group |> List.tryPick (fun g -> g.Doc) with
                        | Some d -> line $"    /// {escape d}"
                        | None -> ()

                        let fname =
                            if Set.contains fname Weir.Parser.keywords then
                                fname + "Flag"
                            else
                                fname

                        let covered = Weir.Argv.kebabFlag fname

                        let attrs =
                            match group |> List.tryFind (fun g -> g.Long <> covered) with
                            | Some g -> [ $"Wire \"{g.Long}\"" ]
                            | None -> []

                        let attrs =
                            match group |> List.tryPick (fun g -> g.Short) with
                            | Some sh when sh.Length = 1 && sh <> "h" -> attrs @ [ $"Short \"{sh}\"" ]
                            | _ -> attrs

                        if not attrs.IsEmpty then
                            let joined = String.concat "; " attrs
                            line $"    [<{joined}>]"

                        line $"    {fname}: bool"

                    line "}"

                // a case name from a sub path ("issue list" ->
                // IssueList): PascalCase of the camel of the kebab-joined
                // tokens — kebabFlag round-trips it to the key the
                // checker builds from the line's own words
                let caseName (path: string) =
                    let camel = fieldName (path.Replace(" ", "-"))
                    let pascal = string (System.Char.ToUpper camel[0]) + camel.Substring 1
                    if pascal = "Cmd" then "CmdCmd" else pascal

                line $"module {moduleName}"

                line (
                    "/// generated from '"
                    + tool
                    + "' on "
                    + System.DateTime.UtcNow.ToString "yyyy-MM-dd"
                )

                line $"/// source: {source} — a scraped surface may be incomplete (partial"
                line "/// by default; add `let exhaustive = true` once verified by hand)"

                if subGroups.IsEmpty then
                    line "/// flat surface: flags checked across the whole line — split into"
                    line "/// subcommand records by hand if the tool warrants it"
                else
                    line "/// scoped surface [D:scoped-sigs]: the LONGEST matching subcommand"
                    line "/// path picks the case (issue list beats issue); a case checks its"
                    line "/// own flags plus its ancestors' and the globals"

                match version with
                | Some v -> line $"let version = \"{escape v}\""
                | None ->
                    line
                        "// no `let version`: the tool answers neither --version nor the version subcommand, so there is no identity to record"

                if subGroups.IsEmpty then
                    renderRecord "Cmd" flags
                else
                    // load-bearing: no global merge exists in the
                    // checker, so top-level flags join every case —
                    // and so do each ancestor path's flags (`jira issue
                    // list` carries issue's own flags too); nearest
                    // definition wins on a field collision
                    let globalByField = flags |> List.map (fun f -> fieldName f.Long, f)
                    let byPath = subGroups |> Map.ofList

                    let cases =
                        subGroups
                        |> List.distinctBy (fun (path, _) -> caseName path)
                        |> List.map (fun (path, gflags) ->
                            let parts = path.Split ' '

                            let ancestors =
                                [ for n in parts.Length - 1 .. -1 .. 1 do
                                      let anc = String.concat " " (Array.truncate n parts)

                                      match Map.tryFind anc byPath with
                                      | Some fs -> yield! fs
                                      | None -> () ]

                            let mutable have = gflags |> List.map (fun f -> fieldName f.Long) |> Set.ofList

                            let inherited =
                                (ancestors @ (globalByField |> List.map snd))
                                |> List.filter (fun f ->
                                    let fn = fieldName f.Long

                                    if Set.contains fn have then
                                        false
                                    else
                                        have <- Set.add fn have
                                        true)

                            path, caseName path, gflags @ inherited)

                    for _, cn, cflags in cases do
                        renderRecord $"{cn}Flags" cflags

                    line "type Cmd ="

                    for sub, cn, _ in cases do
                        // the matching key is the kebab of the case — it
                        // must round-trip to the sub token the user types
                        line $"    | {cn} of {cn}Flags"

                let text = sb.ToString()

                // validate before write [D:add-validates]: the generated
                // signature must itself load as one; nothing persists if
                // it does not
                let tmp =
                    IO.Path.Combine(IO.Path.GetTempPath(), $"weir-siggen-{System.Guid.NewGuid():N}.weir")

                try
                    IO.File.WriteAllText(tmp, text)

                    let probe =
                        // a synthetic importer path — the sig file must
                        // not read as importing itself
                        loadSigs
                            (tmp + ".from")
                            [ { Tool = tool
                                Override = Some tmp
                                Line = 1 } ]

                    match probe with
                    | d :: _, _ ->
                        // name the offending line — without it a generator
                        // bug reports blind [D:sig-version-probe]
                        let atLine =
                            match text.Split '\n' |> Array.tryItem (d.Line - 1) with
                            | Some l when l.Trim() <> "" -> $" at line {d.Line}: {l.Trim()}"
                            | _ -> ""

                        Error $"generated signature does not validate: {d.Message}{atLine} — this is a generator bug"
                    | [], _ ->
                        let dest = IO.Path.Combine(weirDir, "sigs", sigFileName tool)
                        IO.Directory.CreateDirectory(IO.Path.GetDirectoryName dest) |> ignore
                        IO.File.WriteAllText(dest, text)
                        let bytes = IO.File.ReadAllBytes dest

                        let entry: Contracts.LockEntry =
                            { Kind = "sig"
                              Name = tool
                              Url = $"generated:{source}"
                              Sha256 = Contracts.sha256Hex bytes
                              Path = IO.Path.Combine("sigs", sigFileName tool)
                              Version = version }

                        match Contracts.readLock weirDir with
                        | Error e -> Error e
                        | Ok entries ->
                            let others = entries |> List.filter (fun e -> not (e.Kind = "sig" && e.Name = tool))

                            Contracts.writeLock weirDir (others @ [ entry ])

                            let versionNote =
                                match version with
                                | Some v -> escape v
                                | None -> $"none — '{tool}' answers neither --version nor version"

                            let surfaceNote =
                                if subGroups.IsEmpty then
                                    $"{flags.Length} flag(s)"
                                else
                                    let total =
                                        (flags @ (subGroups |> List.collect snd))
                                        |> List.distinctBy (fun f -> f.Long)
                                        |> List.length

                                    $"{total} flag(s) across {subGroups.Length} subcommand case(s)"

                            Ok
                                $"added sig {tool} ({surfaceNote}, source: {source}, version: {versionNote}) — partial by default; verify and mark exhaustive by hand"
                finally
                    try
                        IO.File.Delete tmp
                    with _ ->
                        ()

let analyzeLines
    (path: string)
    (rawLines: string list)
    : Diagnostic list * (LogicalLine * CheckedStatement) list * TypeEnv * LogicalLine list =
    let body, bodyOffset, sigDecls = scriptBody rawLines
    let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)

    let typeEnv0, _ = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv

    let typeEnv0 =
        { typeEnv0 with
            Modules = typeEnv0.Modules |> Map.add "Self" selfMembers }

    // imports resolve relative to the file being checked [D:modules-v1];
    // one cache per check dedups a diamond, one chain catches a cycle
    let analyzeImport: ImportLoader =
        let absPath = IO.Path.GetFullPath path
        let cache = System.Collections.Generic.Dictionary<string, LoadedModule>()
        fun p _ alias -> loadModuleCachedWith false cache [ absPath ] absPath p alias

    Extern.refresh ()

    // Check-only consumers assume unknown heads are commands, so a
    // script for uninstalled tools still parses; each head missing
    // from PATH becomes a warning (cmd-not-found). The runner keeps
    // hard resolution — same pipeline, explicitly different resolver
    // input (the gateExprs pattern) [D:assume-resolver].

    // Assembly recovery [D:assembly-recovery]: drop the line the
    // error names and retry, keeping each drop as a diagnostic. The
    // runner keeps hard assembly failure; tooling-only.
    let assemblyDiags = ResizeArray<Diagnostic>()

    let rec assembleRecovering (attempts: int) (input: (int * string) list) =
        match assemble input with
        | Ok lls -> lls
        | Error msg when attempts > 0 ->
            let line =
                match msg.Split(' ') |> Array.tryItem 1 with
                | Some tok ->
                    tok.TrimEnd(':')
                    |> fun t ->
                        (match System.Int32.TryParse t with
                         | true, n -> n
                         | false, _ -> -1)
                | None -> -1

            assemblyDiags.Add
                { File = path
                  Line = (if line > 0 then line else 1)
                  Col = 1
                  EndLine = None
                  EndCol = None
                  Severity = "error"
                  Code = "assembly"
                  Message = msg }

            if line > 0 && input |> List.exists (fun (n, _) -> n = line) then
                assembleRecovering (attempts - 1) (input |> List.filter (fun (n, _) -> n <> line))
            else
                []
        | Error _ -> []

    let logicalLines = numbered |> assembleRecovering 10 // raw lines: assemble classifies/strips internally

    (let diags0 = List.ofSeq assemblyDiags
     diags0 |> ignore)

    match Some logicalLines with
    | None -> [], [], typeEnv0, []
    | Some logicalLines ->
        let diags = ResizeArray<Diagnostic>()
        let stmts = ResizeArray<LogicalLine * CheckedStatement>()
        // first declaration per type name: line * came-from-import
        // [D:dup-type-decl]
        let declaredTypes = System.Collections.Generic.Dictionary<string, int * bool>()

        let warn (wl, wc, wm) =
            diags.Add
                { File = path
                  Line = wl
                  Col = wc
                  EndLine = None
                  EndCol = None
                  Severity = "warning"
                  Code = "warning"
                  Message = wm }

        let mutable tenv = typeEnv0

        // a module file [D:modules-v1] is checkable in isolation: direct
        // `weir check` enforces the same decl-only + weak-purity rules an
        // import would (decision 10)
        let isModule =
            match logicalLines with
            | first :: _ ->
                let t = first.Text.TrimStart()
                t = "module" || t.StartsWith "module "
            | [] -> false

        let rec cmdHeads (te: Check.TypedExpr) =
            (match te.Kind with
             | Check.TECmd(Check.THeadLit prog, _, _) when not (Extern.exists prog) -> [ prog, te.Span ]
             | _ -> [])
            @ (Check.childExprs te |> List.collect cmdHeads)

        // module-signature pairing state [D:module-signatures] — module
        // files only; scripts pass None so the form refuses with its
        // explanation. The whole-file rules (orphan, doc-on-impl) are
        // judged after the fold, mirroring the module loader.
        let mutable sigState = if isModule then Some SigContext.empty else None
        let sigImplLines = ResizeArray<string * int>()

        // unused bindings [D:unused-bindings]: fed per statement, judged
        // after the fold with the module's Signed set
        let unusedTracker = UnusedTracker()

        // possible re-enumeration [D:reenum-warning]: fed per statement,
        // flushed after the fold; modules skip (a command-running module
        // `let` is already the module-rule error)
        let reenumTracker = ReenumTracker()

        // the newTempDir footgun [D:newtempdir-lint]: same per-statement
        // feed / post-fold flush; scripts only (modules cannot pair a
        // newTempDir bind with a delete)
        let tempDirTracker = TempDirTracker()

        // budget stop-at-first [D:budget-stop-first]: the per-statement
        // inference budget is a whole-file DoS when the multi-error fold
        // multiplies it across independent burning statements. A budget
        // diagnostic is a stop-and-fix (like the runner/module-loader
        // aborting on first error), so once one is seen the fold reports
        // it and processes no further statement — one burn bounds the file.
        // Ordinary type errors still all report.
        let mutable budgetStop = false

        for ll in logicalLines do
          if not budgetStop then
            // one spelling for the head warning, shared by the Ok walk
            // (typed) and the Error walk (parse-level) below
            let warnMissingHead (prog: string) (startCol: int) =
                let wl, wc = translate ll startCol

                // a near-miss binding bridges the check/run
                // verdict split: the runner reads this head in
                // expression mode and errors "unbound 'xx' —
                // did you mean 'xr'?"; check's command reading
                // must surface the same candidate
                // a missing program suggests programs — externals and
                // lowercase user bindings; a constructor (YMap) is never
                // a plausible command (PLAN-dx-review D5)
                let hint =
                    didYouMean
                        prog
                        (Seq.append
                            (Extern.names () :> seq<string>)
                            (Map.keys tenv.Values
                             |> Seq.filter Types.isUserName
                             |> Seq.filter (fun n -> n.Length > 0 && System.Char.IsLower n[0])))

                diags.Add
                    { File = path
                      Line = wl
                      Col = wc
                      // the full head word squiggles, not one
                      // char [PLAN-diagnostics-arc A4]
                      EndLine = Some wl
                      EndCol = Some(wc + prog.Length)
                      Severity = "warning"
                      Code = "cmd-not-found"
                      Message =
                        (match Map.tryFind prog Builtins.bareAliasHomes with
                         | Some home ->
                             $"'{prog}' is a bare module member, not a program — spell it '{home}.{prog}' (bare names live in the REPL session)"
                         | None ->
                             $"command not found on PATH: {prog}{hint} — weir resolves commands at check time; the script runs once it is installed") }

            match checkStatement true sigState assumeResolver analyzeImport tenv ll with
            | Ok chk ->
                chk.Warnings |> List.iter warn

                // a duplicate type declaration is an error, not a silent
                // replacement [D:dup-type-decl] — replacement would be
                // retroactive (code above the redeclaration re-resolves
                // against the winner). Scripts only; the REPL replaces
                // by design.
                (match chk.Kind with
                 | KType decl ->
                     match declaredTypes.TryGetValue decl.Name with
                     | true, (firstLine, viaImport) ->
                         diags.Add
                             { File = path
                               Line = ll.Head
                               Col = 1
                               EndLine = None
                               EndCol = None
                               Severity = "error"
                               Code = "dup-type"
                               Message =
                                 if viaImport then
                                     $"type '{decl.Name}' is already provided by the import at line {firstLine}; rename one"
                                 else
                                     $"type '{decl.Name}' is already declared at line {firstLine}" }
                     | _ -> declaredTypes[decl.Name] <- (ll.Head, false)
                 | KImport lm ->
                     for tn in lm.TypeNames do
                         if not (declaredTypes.ContainsKey tn) then
                             declaredTypes[tn] <- (ll.Head, true)
                 | _ -> ())

                (match chk.Kind with
                 | KType _
                 | KSig _
                 | KModule _
                 | KImport _ -> ()
                 | KLet(_, _, te)
                 | KLetPat(_, _, te)
                 | KCmd te
                 | KExpr te ->
                     for prog, span in cmdHeads te do
                         warnMissingHead prog span.Start.Col)

                (if isModule then
                     let violation =
                         match chk.Kind with
                         | KCmd _
                         | KExpr _ -> Some "a module declares only — 'type' and 'let', no commands or bare expressions"
                         | KLet(_, _, te)
                         | KLetPat(_, _, te) when runsCommandT te ->
                             Some "a module 'let' cannot run a command at import — wrap it in a function (let f () = …)"
                         | _ -> None

                     match violation with
                     | Some msg ->
                         let wl, wc = translate ll 1

                         diags.Add
                             { File = path
                               Line = wl
                               Col = wc
                               EndLine = None
                               EndCol = None
                               Severity = "error"
                               Code = "module-rule"
                               Message = msg }
                     | None -> ())

                (match sigState, chk.Kind with
                 | Some sc, KSig(name, sd) ->
                     sigState <-
                         Some
                             { sc with
                                 Pending = Map.add name sd sc.Pending
                                 Signed = Set.add name sc.Signed }
                 | Some sc, KLet(name, _, _) ->
                     if Set.contains name sc.Signed then
                         sigImplLines.Add(name, ll.Head)

                     sigState <-
                         Some
                             { sc with
                                 Pending = Map.remove name sc.Pending
                                 Implemented = Set.add name sc.Implemented }
                 | Some sc, KLetPat(_, schemes, _) ->
                     sigState <-
                         Some
                             { sc with
                                 Implemented = schemes |> List.fold (fun st (n, _) -> Set.add n st) sc.Implemented }
                 | _ -> ())

                unusedTracker.Feed ll chk

                if not isModule then
                    reenumTracker.Feed ll chk
                    tempDirTracker.Feed ll chk

                stmts.Add(ll, chk)
                tenv <- chk.Env
            | Error d ->
                unusedTracker.Poison()
                reenumTracker.Poison()
                tempDirTracker.Poison()
                d.Warnings |> List.iter warn

                // [PLAN-diagnostics-arc B5+B6]: an errored statement
                // still (a) surfaces its command-head warnings — no
                // typed tree exists, so the walk is parse-level — and
                // (b) binds its let names to hole schemes so downstream
                // uses don't cascade as "unbound". Suppression with
                // deferral, deliberately: a hole unifies with anything,
                // so a later genuine mismatch against the real type may
                // surface only after this error is fixed — one real
                // error beats N echoes. (The poison-type alternative —
                // suppressing downstream errors that mention the name —
                // needs a new type node through unify; declined as
                // disproportionate.)
                (match Parser.parseLine (assumeResolver tenv) ll.Text with
                 | Ok stmt ->
                     let rec eheads (e: Expr) =
                         (match e.Kind with
                          | ECmd(HeadLit prog, _, _) when not (Extern.exists prog) -> [ prog, e.Span ]
                          | _ -> [])
                         @ (exprChildren e |> List.collect eheads)

                     let exprs =
                         match stmt with
                         | SLet(_, v)
                         | SLetPat(_, v)
                         | SExpr v
                         | SCmd v -> [ v ]
                         | SType _
                         | SSig _
                         | SModule _
                         | SImport _ -> []

                     for prog, span in exprs |> List.collect eheads do
                         warnMissingHead prog span.Start.Col

                     let rec patVars (p: Pattern) =
                         match p.PKind with
                         | PVar n -> [ n ]
                         | PTuple ps -> ps |> List.collect patVars
                         | PRecord fields -> fields |> List.map snd |> List.collect patVars
                         | PCase(_, Some inner) -> patVars inner
                         | _ -> []

                     let holeScheme =
                         { Forall = Set.singleton "__hole"
                           Cs = Map.empty
                           Ty = TVar "__hole"
                           RowOrigins = Map.empty
                           HoleDefaults = [] }

                     let bound =
                         match stmt with
                         | SLet(name, _) -> [ name ]
                         | SLetPat(pat, _) -> patVars pat
                         | _ -> []

                     // an errored implementation still discharges its
                     // pending signature — one real error beats a trailing
                     // orphan echo [D:module-signatures]
                     (match sigState with
                      | Some sc when bound |> List.exists (fun n -> Map.containsKey n sc.Pending) ->
                          sigState <-
                              Some
                                  { sc with
                                      Pending = bound |> List.fold (fun m n -> Map.remove n m) sc.Pending
                                      Implemented = bound |> List.fold (fun st n -> Set.add n st) sc.Implemented }
                      | _ -> ())

                     for n in bound do
                         tenv <-
                             { tenv with
                                 Values = Map.add n holeScheme tenv.Values }
                 | Error _ -> ())

                // multi-file [D:modules-v1]: a module error carries File +
                // PhysLine/Col into that other file; its Note is the "imported
                // here" pointer at the import line in this file
                diags.Add
                    { File = d.File |> Option.defaultValue path
                      Line = d.PhysLine
                      Col = d.PhysCol
                      EndLine = d.PhysEnd |> Option.map fst
                      EndCol = d.PhysEnd |> Option.map snd
                      Severity = "error"
                      Code = codeOf d.Parse d.Message
                      Message = d.Message }

                (match d.Note with
                 | Some(nl, nc, nmsg) ->
                     diags.Add
                         { File = path
                           Line = nl
                           Col = nc
                           EndLine = None
                           EndCol = None
                           Severity = "note"
                           Code = "imported-here"
                           Message = nmsg }
                 | None -> ())

                // budget stop-at-first [D:budget-stop-first]: a budget
                // diagnostic ends the fold — no further statement is
                // checked, so one exhaustion cannot multiply across the file
                if Check.isBudgetMessage d.Message then
                    budgetStop <- true

        // the whole-file signature rules [D:module-signatures]: every
        // remaining pending sig is an orphan; a /// doc on a signed
        // member's implementation names its one home
        (match sigState with
         | Some sc ->
             for name, sd in sc.Pending |> Map.toList |> List.sortBy (fun (_, sd) -> sd.Line) do
                 diags.Add
                     { File = path
                       Line = sd.Line
                       Col = sd.Col
                       EndLine = None
                       EndCol = None
                       Severity = "error"
                       Code = "sig-orphan"
                       Message = $"signature without implementation — no 'let {name} … = …' follows in this module" }

             let attaches = docAttachments rawLines

             for name, implLine in sigImplLines do
                 match attaches |> List.tryFind (fun d -> d.Line = implLine) with
                 | Some d ->
                     diags.Add
                         { File = path
                           Line = max 1 (d.Line - List.length d.Doc)
                           Col = 1
                           EndLine = None
                           EndCol = None
                           Severity = "error"
                           Code = "sig-doc"
                           Message =
                             $"'{name}' is signed, and its /// doc belongs on the signature — move it above 'let {name} : …' (one doc home)" }
                 | None -> ()
         | None -> ())

        // the unused-binding check [D:unused-bindings]: whole-file, so it
        // judges after the fold; poisoned (any errored statement) = silent.
        // A file under .weir/sigs/ is a sig contract — its members
        // (version, exhaustive, Cmd) are read by the sig loader, so the
        // loader is the use and a direct `weir check` agrees with it
        (let isSigContract =
            let norm = path.Replace('\\', '/')
            norm.Contains "/.weir/sigs/" || norm.StartsWith ".weir/sigs/"

         let signedNames =
             sigState |> Option.map (fun sc -> sc.Signed) |> Option.defaultValue Set.empty

         for f in (if isSigContract then [] else unusedTracker.Flush signedNames isModule) do
             diags.Add
                 { File = path
                   Line = f.ULine
                   Col = f.UCol
                   EndLine = Some f.ULine
                   EndCol = Some f.UEndCol
                   Severity = "error"
                   Code = "unused-binding"
                   Message = f.UMessage })

        // the re-enumeration warning lands on the second and later
        // enumerating uses [D:reenum-warning] — warning severity, so
        // check still exits 0
        (for f in reenumTracker.Flush() do
            diags.Add
                { File = path
                  Line = f.RLine
                  Col = f.RCol
                  EndLine = Some f.RLine
                  EndCol = Some f.REndCol
                  Severity = "warning"
                  Code = "re-enumeration"
                  Message = f.RMessage })

        // the newTempDir footgun [D:newtempdir-lint] lands on the delete
        // site — warning severity, so check still exits 0
        (for f in tempDirTracker.Flush() do
            diags.Add
                { File = path
                  Line = f.TLine
                  Col = f.TCol
                  EndLine = Some f.TLine
                  EndCol = Some f.TEndCol
                  Severity = "warning"
                  Code = "temp-dir-cleanup"
                  Message = f.TMessage })

        (let sigLoadDiags, sigInfos = loadSigs path sigDecls

         // position order, not accumulation order (rider D1): assembly
         // diagnostics are gathered before parsing, so unsorted output leads
         // with "line 4" for a file that was not plausible from byte one.
         // Sorted at the one return, so check / --json / --can / the LSP
         // all inherit it [D:one-pipeline]; stable, so same-position
         // diagnostics keep their emission order.
         List.ofSeq assemblyDiags
         @ List.ofSeq diags
         @ docMisalignments path rawLines
         @ schemaDiagnostics path (List.ofSeq stmts)
         @ sigLoadDiags
         @ sigCmdDiagnostics path sigInfos (List.ofSeq stmts)
         |> List.sortWith (fun a b ->
             let byLine = compare a.Line b.Line
             if byLine <> 0 then byLine else compare a.Col b.Col)),
        List.ofSeq stmts,
        typeEnv0,
        logicalLines

/// diagnostic rendering shared by `check` and `check --can`
/// [D:can-report] — one spelling for both consumers
/// `parsedAny` is "did any statement parse" — the conservative half of the
/// not-weir heuristic (rider D4). Without it a real script with a broken
/// first line would be told it is not weir at all.
let printDiags (json: bool) (parsedAny: bool) (diags: Diagnostic list) : unit =
    if json then
        Console.WriteLine(
            jsonBuild (fun w ->
                w.WriteStartArray()
                diags |> List.iter (writeDiag w)
                w.WriteEndArray())
        )
    else
        let c = Color.onStdout.Value

        // rider D4: a file that failed at its first statement and produced no
        // parsed statement anywhere is probably not a weir script — say so
        // once instead of leaving the reader to infer it from a list. It
        // precedes the detail rather than replacing it (a reader who wants
        // the parse errors keeps them), and never appears in --json, where a
        // gate wants real diagnostics and not a human affordance. No
        // extension rule: this also covers shebang files, stdin and
        // extensionless fixtures, which an extension check cannot.
        let notWeir =
            not parsedAny
            && diags
               |> List.exists (fun d -> d.Line = 1 && d.Code = "parse" && d.Severity = "error")

        if notWeir then
            match diags with
            | d :: _ ->
                Console.WriteLine(
                    Color.bold c $"{d.File}"
                    + ": this does not look like a weir script — the first statement could not be parsed"
                )
            | [] -> ()

        for d in diags do
            let sev =
                if d.Severity = "warning" then
                    Color.yellow c $"warning [{d.Code}]"
                elif d.Severity = "note" then
                    Color.bold c $"note [{d.Code}]"
                else
                    Color.red c $"error [{d.Code}]"

            Console.WriteLine(Color.bold c $"{d.File}:{d.Line}:{d.Col}" + $": {sev}: {d.Message}")

let checkOnly (json: bool) (path: string) : int =
    if not (IO.File.Exists path) then
        Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let rawLines = IO.File.ReadAllLines path |> Array.toList
        let diags, stmts, _, _ = analyzeLines path rawLines
        printDiags json (not (List.isEmpty stmts)) diags

        if diags |> List.exists (fun d -> d.Severity = "error") then
            1
        else
            0

// [D:doc-help] the `///` first-line help for the fields of the type decl
// on `ll`, keyed by field name. A field's DocAttach sits at the field's own
// physical line, so scope by the decl's physical lines (its Segments); a
// field name is unique within a decl. An empty first line -> no help entry
// (silence beats a mystery), same as no doc.
let private fieldDocsFor (rawLines: string list) (ll: LogicalLine) : Map<string, string> =
    let physLines = ll.Segments |> List.map (fun (_, p, _) -> p) |> Set.ofList
    let arr = List.toArray rawLines

    docAttachments rawLines
    |> List.choose (fun d ->
        match d.Doc with
        | first :: _ when
            first <> ""
            && Set.contains d.Line physLines
            && d.Line - 1 < arr.Length
            && d.Col - 1 + d.Len <= arr[d.Line - 1].Length
            ->
            Some(arr[d.Line - 1].Substring(d.Col - 1, d.Len), first)
        | _ -> None)
    |> Map.ofList

let run (path: string) (scriptArgs: string list) : int =
    if not (IO.File.Exists path) then
        Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let rawLines = IO.File.ReadAllLines path |> Array.toList

        let body, bodyOffset, runSigDecls = scriptBody rawLines

        // Column-0 only: a directive is a statement-position thing.
        // Indented `#` lines are continuations — inside a yaml district
        // they are content (`#!/bin/sh` in a block scalar, `#` comments)
        // [D:block-scalars]; anywhere else they fail the parse
        let directiveError =
            body
            |> List.mapi (fun i l -> i, l.TrimEnd())
            |> List.tryFind (fun (_, l) -> l.StartsWith "#")

        match directiveError with
        | Some(i, l) ->
            Console.Error.WriteLine(
                located
                    path
                    (bodyOffset + i + 1)
                    (if l.TrimStart().StartsWith "#session" then
                         "#session lives in the REPL init file (config dir, weir/init.weir) — a script takes its settings from flags and env"
                     else
                         $"unknown or misplaced directive: {l} (directives belong at the file head)")
            )

            1
        | None ->
            // the script's own absolute path [D:script-path]: resolved
            // against the startup cwd, before any cd; symlinks stay
            // unresolved (the bash-$0 behavior)
            let absScriptPath = IO.Path.GetFullPath path

            let typeEnv0, valueEnv0 = baseEnvs scriptArgs absScriptPath
            Extern.refresh ()

            let rawByLine = body |> List.mapi (fun i l -> bodyOffset + i + 1, l) |> Map.ofList

            // comment-only lines are transparent [D:comment-transparency]
            let assembled = body |> List.mapi (fun i l -> bodyOffset + i + 1, l) |> assemble // raw lines: assemble classifies/strips internally

            match assembled with
            | Error msg ->
                Console.Error.WriteLine $"{path}: {msg}"
                1
            | Ok logicalLines ->

                // a module file is not runnable [D:modules-v1] — the marker
                // is what makes this message possible (an empty script is a
                // different, "nothing to run", situation)
                let moduleMarker =
                    logicalLines
                    |> List.tryFind (fun ll ->
                        let t = ll.Text.TrimStart()
                        t = "module" || t.StartsWith "module ")

                // the entry's import loader, bound to its directory; one cache
                // per run dedups a diamond, one chain catches a cycle
                let entryImport: ImportLoader =
                    let cache = System.Collections.Generic.Dictionary<string, LoadedModule>()
                    fun p _ alias -> loadModuleCachedWith false cache [ absScriptPath ] absScriptPath p alias

                // signatures load once, before the check fold; a load
                // failure (missing/malformed sig) is a check error —
                // absence is loud, never a silent fallback
                // [D:command-signatures]
                let sigLoadDiags, runSigInfos = loadSigs path runSigDecls
                let runDeclaredTypes = System.Collections.Generic.Dictionary<string, int * bool>()

                // unused bindings gate the run too [D:unused-bindings] —
                // check error = zero side effects, this check included
                let runUnused = UnusedTracker()

                let checkedProgram =
                    logicalLines
                    |> List.fold
                        (fun state ll ->
                            match state with
                            | Error e -> Error e
                            | Ok(tenv, acc) ->
                                match checkStatement true None resolver entryImport tenv ll with
                                | Error d ->
                                    let c = Color.onStderr.Value

                                    for wl, wc, wm in d.Warnings do
                                        Console.Error.WriteLine(
                                            $"{path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    let sameFileMsg =
                                        if d.Parse then
                                            if d.HasCol then
                                                // the original source line + caret — never
                                                // the assembled text
                                                let src = rawByLine |> Map.tryFind d.PhysLine |> Option.defaultValue ""

                                                let caret = Color.red c (String(' ', max 0 (d.PhysCol - 1)) + "^")

                                                Color.bold c $"{path}:{d.PhysLine}:{d.PhysCol}"
                                                + ": "
                                                + Color.red c "parse error"
                                                + $":\n{src}\n{caret}\n{d.Message}"
                                            else
                                                located path d.PhysLine d.Message
                                        else
                                            // same source-line treatment as parse
                                            // errors, with the span underlined
                                            let src = rawByLine |> Map.tryFind d.PhysLine |> Option.defaultValue ""

                                            let width =
                                                match d.PhysEnd with
                                                | Some(el, ec) when el = d.PhysLine -> max 1 (ec - d.PhysCol)
                                                | _ -> 1

                                            let underline =
                                                Color.red c (String(' ', max 0 (d.PhysCol - 1)) + String('^', width))

                                            Color.bold c $"{path}:{d.PhysLine}:{d.PhysCol}"
                                            + ": "
                                            + Color.red c "type error"
                                            + $":\n{src}\n{underline}\n{d.Message}"

                                    // multi-file [D:modules-v1]: a module error
                                    // renders at its own file + an "imported here"
                                    // note at the import line
                                    let locatedMsg =
                                        match d.File with
                                        | Some mf ->
                                            let note =
                                                match d.Note with
                                                | Some(nl, nc, nmsg) ->
                                                    "\n" + Color.bold c $"{path}:{nl}:{nc}" + ": note: " + nmsg
                                                | None -> ""

                                            Color.bold c $"{mf}:{d.PhysLine}:{d.PhysCol}"
                                            + ": "
                                            + Color.red c "error"
                                            + $": {d.Message}"
                                            + note
                                        | None -> sameFileMsg

                                    Error locatedMsg
                                | Ok chk ->
                                    let c = Color.onStderr.Value

                                    for wl, wc, wm in chk.Warnings do
                                        Console.Error.WriteLine(
                                            $"{path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    // signature warnings print, signature errors
                                    // gate exactly as schemas do
                                    // [D:command-signatures]
                                    let sigDs = sigCmdDiagnostics path runSigInfos [ (ll, chk) ]

                                    for d in sigDs |> List.filter (fun d -> d.Severity = "warning") do
                                        Console.Error.WriteLine(
                                            $"{path}:{d.Line}:{d.Col}: " + Color.yellow c "warning" + $": {d.Message}"
                                        )

                                    // schema contracts gate the run too — check
                                    // before effects [D:yaml-schemas]
                                    match
                                        (sigDs |> List.filter (fun d -> d.Severity = "error"))
                                        @ schemaDiagnostics path [ (ll, chk) ]
                                    with
                                    | d :: _ ->
                                        let src = rawByLine |> Map.tryFind d.Line |> Option.defaultValue ""

                                        let width =
                                            match d.EndLine, d.EndCol with
                                            | Some el, Some ec when el = d.Line -> max 1 (ec - d.Col)
                                            | _ -> 1

                                        let underline =
                                            Color.red c (String(' ', max 0 (d.Col - 1)) + String('^', width))

                                        Error(
                                            Color.bold c $"{path}:{d.Line}:{d.Col}"
                                            + ": "
                                            + Color.red c "schema error"
                                            + $":\n{src}\n{underline}\n{d.Message}"
                                        )
                                    | [] ->

                                        // duplicate type declarations gate the
                                        // run too [D:dup-type-decl]
                                        let dupError =
                                            match chk.Kind with
                                            | KType decl ->
                                                match runDeclaredTypes.TryGetValue decl.Name with
                                                | true, (firstLine, viaImport) ->
                                                    Some(
                                                        located
                                                            path
                                                            ll.Head
                                                            (if viaImport then
                                                                 $"type '{decl.Name}' is already provided by the import at line {firstLine}; rename one"
                                                             else
                                                                 $"type '{decl.Name}' is already declared at line {firstLine}")
                                                    )
                                                | _ ->
                                                    runDeclaredTypes[decl.Name] <- (ll.Head, false)
                                                    None
                                            | KImport lm ->
                                                for tn in lm.TypeNames do
                                                    if not (runDeclaredTypes.ContainsKey tn) then
                                                        runDeclaredTypes[tn] <- (ll.Head, true)

                                                None
                                            | _ -> None

                                        match dupError with
                                        | Some e -> Error e
                                        | None ->
                                            runUnused.Feed ll chk

                                            let stmt =
                                                match chk.Kind with
                                                | KType decl -> CType decl
                                                | KLet(name, _, te) -> CLet(name, te)
                                                | KLetPat(pat, _, te) -> CLetPat(pat, te)
                                                | KCmd te -> CCmd te
                                                | KExpr te -> CExpr te
                                                | KImport lm -> CImport lm
                                                // unreachable: the marker is caught before the fold,
                                                // and a script's KSig never checks (scripts refuse
                                                // the form) [D:module-signatures]
                                                | KModule _
                                                | KSig _ -> CNoop

                                            // enrich a record's Docs from the `///`
                                            // field docs, so --help reads them
                                            // [D:doc-help]; the Args.load arm (checked
                                            // later, the type comes first) captures it
                                            let env' =
                                                match chk.Kind with
                                                | KType decl ->
                                                    let docs = fieldDocsFor rawLines ll

                                                    if Map.isEmpty docs then
                                                        chk.Env
                                                    else
                                                        { chk.Env with
                                                            Types =
                                                                chk.Env.Types
                                                                |> Map.change
                                                                    decl.Name
                                                                    (Option.map (function
                                                                        | Record rd -> Record { rd with Docs = docs }
                                                                        | u -> u)) }
                                                | _ -> chk.Env

                                            Ok(env', (ll.Head, stmt) :: acc))
                        (Ok(typeEnv0, []))

                match
                    (match moduleMarker with
                     | Some ll ->
                         Error(
                             located
                                 path
                                 ll.Head
                                 "a module declares; it does not run. To run a script from a script, invoke it as a command"
                         )
                     | None ->
                         // a signature that fails to load is a check
                         // error — nothing runs [D:command-signatures]
                         match sigLoadDiags with
                         | d :: _ -> Error(located path d.Line d.Message)
                         | [] -> checkedProgram)
                with
                | Error msg ->
                    Console.Error.WriteLine msg
                    1
                | Ok _ when not (runUnused.Flush Set.empty false |> List.isEmpty) ->
                    // the unused-binding check [D:unused-bindings]: judged after
                    // the whole-file fold, before any line runs
                    let c = Color.onStderr.Value

                    for f in runUnused.Flush Set.empty false do
                        let src = rawByLine |> Map.tryFind f.ULine |> Option.defaultValue ""

                        let underline =
                            Color.red c (String(' ', max 0 (f.UCol - 1)) + String('^', max 1 (f.UEndCol - f.UCol)))

                        Console.Error.WriteLine(
                            Color.bold c $"{path}:{f.ULine}:{f.UCol}"
                            + ": "
                            + Color.red c "check error"
                            + $":\n{src}\n{underline}\n{f.UMessage}"
                        )

                    1
                | Ok(_, revStmts) ->
                    let stmts = List.rev revStmts

                    let rec exec (venv: Eval.Env) (rest: (int * CheckedStmt) list) : int =
                        match rest with
                        | [] -> 0
                        | (lineNo, stmt) :: tail ->
                            match stmt with
                            | CNoop -> exec venv tail
                            | CImport lm ->
                                try
                                    // replay the module (its Body, including any
                                    // nested imports) and expose its members as
                                    // `alias.member` [D:modules-v1]
                                    // process facts (incl. entryPath) ride from
                                    // the entry; the module keeps its own scriptPath
                                    let procFacts =
                                        [ "Self.pid"; "Self.args"; "Self.stdin"; "Self.entryPath" ]
                                        |> List.choose (fun k -> Map.tryFind k venv |> Option.map (fun v -> k, v))

                                    let moduleVenv = replayModule procFacts lm

                                    let venv' =
                                        lm.Members
                                        |> List.fold
                                            (fun acc (n, _) ->
                                                match Map.tryFind n moduleVenv with
                                                | Some v -> Map.add $"{lm.Alias}.{n}" v acc
                                                | None -> acc)
                                            venv

                                    exec venv' tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        runtimeErrorLine path lineNo ex.Message
                                    )

                                    1
                            | CType decl ->
                                let venv' =
                                    match decl.Body with
                                    | DUnion cases ->
                                        Eval.constructorValues cases |> List.fold (fun m (n, v) -> Map.add n v m) venv
                                    | DRecord _ -> venv

                                exec venv' tail
                            | CLetPat(pat, te) ->
                                try
                                    let bindings = Eval.bindPattern pat (Eval.eval venv te)
                                    exec (bindings |> List.fold (fun m (n, v) -> Map.add n v m) venv) tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        runtimeErrorLine path lineNo ex.Message
                                    )

                                    1
                            | CLet(name, te) ->
                                try
                                    exec (Map.add name (Eval.eval venv te) venv) tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        runtimeErrorLine path lineNo ex.Message
                                    )

                                    1
                            | CCmd te ->
                                try
                                    // the bare command statement at a tty
                                    // inherits stdout [D:colour-inherit]: the
                                    // child sees the terminal (isatty true,
                                    // colour on) and weir never holds the
                                    // bytes; redirected output keeps the
                                    // batched path — byte-identical, decided
                                    // at spawn
                                    (match te.Kind with
                                     | _ when Eval.inheritsStdout te ->
                                         // no mid-line tidy: a DSR query
                                         // hangs under a non-answering
                                         // terminal — bash's posture, wart
                                         // and all
                                         Console.Out.Flush()
                                         Eval.inheritCommandStatement venv te
                                     | _ -> printResult (Eval.eval venv te))

                                    exec venv tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        runtimeErrorLine path lineNo ex.Message
                                    )

                                    1
                            | CExpr te ->
                                try
                                    Eval.eval venv te |> ignore
                                    exec venv tail
                                with
                                | Eval.ExitRequest code -> code
                                | ex ->
                                    Console.Error.WriteLine(
                                        runtimeErrorLine path lineNo ex.Message
                                    )

                                    1

                    exec valueEnv0 stmts
