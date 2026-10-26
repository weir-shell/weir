module Weir.Script

open System
open Weir.Ast
open Weir.Types

// the quote-aware scanner [D:one-scanner] lives in Scan.fs
let private foldOutsideStrings (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a =
    Scan.foldOutsideStrings f init s

let endsInsideString (s: string) : bool = Scan.endsInsideString s

type StringState = Scan.StringState

let stringStart: StringState = Scan.stringStart

let stringStep (st: StringState) (chunk: string) : StringState = Scan.stringStep st chunk

let isInsideString (st: StringState) : bool = Scan.isInsideString st

let stripComment (line: string) : string =
    let cut = Scan.commentAt line

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
// space runs, pad record braces, tidy `;`, and pad the unambiguous
// binary operators (`=`, `==`, `<>`, `<=`, `>=`, `&&`, `||`, `|>`,
// `+` — never `-`, whose adjacency is meaning [D:operator-values],
// and never bare `<`/`>`/`*`/`/`, which live in types, globs and
// paths). String interiors and leading indent untouched. Fmt applies
// this under a parse-shape safety check — any statement whose sexpr
// changes reverts — so a rule misfiring on a command line (argv
// `{x}`, literal `;`, an `a||b` word, `attempts=3`) can never change
// meaning, only be skipped.
let respaceLine (line: string) : string =
    let mask = inStringMask line
    let indent = line |> Seq.takeWhile ((=) ' ') |> Seq.length
    let sb = System.Text.StringBuilder()

    let lastEmitted () =
        if sb.Length = 0 then ' ' else sb[sb.Length - 1]

    let mutable i = 0

    // the operator at i, length included — two-char first, then the
    // single `=`/`+` with their neighbor guards (`=` next to another
    // operator char is that operator's own business, not a binding)
    let opAt (i: int) : int option =
        let two =
            i + 1 < line.Length
            && not mask[i + 1]
            && (match line[i], line[i + 1] with
                | '=', '='
                | '<', '>'
                | '<', '='
                | '>', '='
                | '&', '&'
                | '|', '|'
                | '|', '>' -> true
                | _ -> false)

        if two then
            Some 2
        else
            let prev = if i = 0 then ' ' else line[i - 1]
            let next = if i + 1 < line.Length then line[i + 1] else ' '
            let opChar (ch: char) = "=<>&|+-*/^!." |> Seq.contains ch

            match line[i] with
            | '=' when not (opChar prev) && not (opChar next) -> Some 1
            | '+' when not (opChar prev) && not (opChar next) -> Some 1
            | _ -> None

    while i < line.Length do
        let c = line[i]

        if i < indent || mask[i] then
            sb.Append c |> ignore
            i <- i + 1
        else
            match opAt i with
            | Some len ->
                if lastEmitted () <> ' ' then
                    sb.Append ' ' |> ignore

                sb.Append(line.Substring(i, len)) |> ignore

                if i + len < line.Length && line[i + len] <> ' ' then
                    sb.Append ' ' |> ignore

                i <- i + len
            | None ->
                (match c with
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
                 | c -> sb.Append c |> ignore)

                i <- i + 1

    sb.ToString()

// the 1-based column of the first tab in a line's leading whitespace
// [D:spaces-only-indent]
let private leadingTabCol (raw: string) : int option =
    raw
    |> Seq.takeWhile (fun c -> c = ' ' || c = '\t')
    |> Seq.tryFindIndex ((=) '\t')
    |> Option.map ((+) 1)

let private tabIndentError (lineNo: int) (raw: string) : string =
    let col = leadingTabCol raw |> Option.defaultValue 1
    $"line {lineNo}: tabs are not allowed in indentation (a tab at column {col}) — indent with spaces"

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

// the init file declares itself [D:init-marker]: `#init` as the first
// code line makes a file init-kind WHEREVER it lives — authorable in a
// dotfiles repo, applied by symlinking into <configHome>/weir/init.weir
// (the REPL loads only that path: which init runs must never depend on
// the cwd). The marker is REQUIRED — the canonical file without it
// refuses at load — so a file's kind is always in the file.
let isInitMarked (lines: string list) : bool =
    lines
    |> List.tryFind (fun l -> classifyLine l = LineKind.Code)
    |> Option.map (fun l -> (stripComment l).Trim() = "#init")
    |> Option.defaultValue false

// init.weir carries init-only directives (#session, #alias) the script
// checker does not know [D:repl-init][D:session-prompt] — rewrite them
// into an equivalent script for diagnostics AND completion: blank the
// `#init`/`#session {`/`}`/`#alias` lines, and turn each
// `<4-indent>key = value` field into `let <binder> = value`
// COLUMN-PRESERVING — 4-space indent out, `let ` in, a same-length
// binder (so the value stays at its exact column for hover/completion,
// and a distinct `_`-led binder neither self-references the value nor
// warns unused).
let sameLenBinder (key: string) : string =
    // same length as the key, distinct from it (no self-reference), no
    // unused/discard warning: `_` + the key's tail, e.g. prompt -> _rompt
    if key.Length = 0 then "_" else "_" + key.Substring 1

let stripInitDirectives (lines: string list) : string list =
    let fieldStart =
        System.Text.RegularExpressions.Regex @"^(\s{0,4})([A-Za-z_][A-Za-z0-9_]*)(\s*=\s*)(.*)$"

    let mutable inBlock = false

    [ for l in lines do
          let t = l.Trim()

          if inBlock then
              if t = "}" then
                  inBlock <- false
                  yield ""
              else
                  let m = fieldStart.Match l

                  if m.Success then
                      // `<indent>key = value` -> `let <binder> = value`; when the
                      // indent is 4, `let ` + a same-length binder keeps the value
                      // column exact (columns matter for hover/completion)
                      let indent = m.Groups.[1].Value
                      let binder = sameLenBinder m.Groups.[2].Value

                      if indent.Length = 4 then
                          yield "let " + binder + m.Groups.[3].Value + m.Groups.[4].Value
                      else
                          yield "let " + binder + " = " + m.Groups.[4].Value
                  else
                      yield l // a continuation line (list entry) is part of the value
          elif t = "#init" then
              yield ""
          elif t.StartsWith "#session" then
              inBlock <- t.EndsWith "{"
              yield ""
          elif t = "#alias" || t.StartsWith "#alias " then
              yield ""
          else
              yield l ]


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
    // covers the overlay; the $e() capture sigil covers fragments.
    | Yaml
    // line-end `<<<` / `$<<<` opens a heredoc district [D:text-block] —
    // the yaml district's sibling; identical join semantics (verbatim
    // lines, relative indentation behind the sentinel)
    | Heredoc

type PieceClass =
    { Kind: PieceKind
      Marker: MarkerKind
      OpensCompound: bool
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
        // the bare within [D:within-always] — its `always` closes the
        // body like an else, and a dedented sibling statement closes both
        || piece = "within"
        || piece.StartsWith "for "
        || piece.StartsWith "pfor "
        // the bounded-loop pair [D:retry-poll] — the machine's 5th and
        // 6th members, still no stack
        || piece.StartsWith "retry "
        || piece.StartsWith "poll "
        // and repeat [D:repeat]
        || piece.StartsWith "repeat "
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
    // repeat joins them [D:repeat]
    || t = "repeat"
    || t.StartsWith "repeat "
    || t.StartsWith "until "
    // the bare within's teardown segment dangles too [D:within-always]
    || t = "always"

/// Smart indent's question [D:repl-autoindent]: does the text before the
/// editor's cursor open an indented body? The assembler's own set
/// (dangleOpensBlock) on the comment-stripped text, minus what only
/// looks like an opener at EOL: a line ending inside a string, a
/// comparison (`==` `<=` `>=` `!=`), an `=` inside an unclosed `{`
/// (a record field), and a word ender glued to an identifier
let opensIndentedBody (before: string) : bool =
    if Scan.endsInsideString before then
        false
    else
        let t = (stripComment before).TrimEnd()

        let gluedWord =
            [ "then"; "else"; "with"; "do" ]
            |> List.exists (fun w ->
                t.EndsWith w
                && t.Length > w.Length
                && (System.Char.IsLetterOrDigit t[t.Length - w.Length - 1] || t[t.Length - w.Length - 1] = '_'))

        let comparison =
            [ "=="; "<="; ">="; "!=" ] |> List.exists t.EndsWith

        let fieldEq = t.EndsWith "=" && braceDelta t > 0

        // a within's `until stopped` takes no block: it dangles for the
        // assembler only so a deeper line reaches the parser's teaching
        // [D:held-scope]
        let held = t.TrimStart() = "until stopped"

        t <> ""
        && dangleOpensBlock (t.TrimStart())
        && not gluedWord
        && not comparison
        && not fieldEq
        && not held

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

    // the district marker ended a COMMAND line; a `!name` after an
    // expression-position token (or alone) is C negation, which the
    // parser teaches as `not` [D:left-to-right-ops]
    let before =
        let rest = piece.Substring(0, piece.Length - lastToken.Length).TrimEnd()

        match rest.LastIndexOf ' ' with
        | -1 -> rest
        | i -> rest.Substring(i + 1)

    // (a standalone `!e`, or one after then/else/->, was a district header
    // and keeps its retirement teaching)
    let negationSlot =
        List.contains before [ "="; "("; "&&"; "||"; "<-"; ","; "["; "not"; "=="; "<>" ]
        || before.EndsWith "("

    piece = "!"
    || piece.EndsWith " !"
    || (lastToken.StartsWith "!"
        && not (lastToken.Contains "(")
        && isIdentToken (lastToken.Substring 1)
        && not negationSlot)

let private markerOpener (m: MarkerKind) : (string * int * bool) option =
    match m with
    | MarkerKind.NoMarker -> None

    // the yaml district keeps its marker word; lines join verbatim with
    // relative indentation behind the sentinel [D:yaml-district]
    | MarkerKind.Yaml -> Some("", 0, true)
    // the text district joins exactly as yaml does [D:text-block]
    | MarkerKind.Heredoc -> Some("", 0, true)

// translate's per-line data rides the line itself [D:line-pos-cache]:
// a ConditionalWeakTable entry per line is a GC dependent handle, and one
// per statement made every collection walk them all. Equality is the
// line's text and segments only, as before the fields moved here.
[<CustomEquality; NoComparison>]
type LogicalLine =
    { Text: string
      Head: int
      Segments: (int * int * int) list
      // where each segment's own text ends, aligned with Segments (the
      // join text after it is no physical column); empty for a line not
      // from the assembler
      Ends: int[]
      // translate's index, built on first use; it names the Text,
      // Segments and Ends it was built from, so a copy never reads a
      // stale one
      mutable PosIndex: obj }

    override this.Equals(other) =
        match other with
        | :? LogicalLine as o -> this.Text = o.Text && this.Head = o.Head && this.Segments = o.Segments
        | _ -> false

    override this.GetHashCode() = hash (this.Text, this.Head, this.Segments)

// a one-physical-line LogicalLine — the REPL's and -e's spelling
let singleLine (text: string) : LogicalLine =
    { Text = text
      Head = 1
      Segments = [ (0, 1, 0) ]
      Ends = [||]
      PosIndex = null }

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
                i <- Scan.escapeEnd text i
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
// running stack — fmt's only caller; parens ride it too, since inside
// any open bracket fmt shifts rather than re-levels [D:fmt-bracket-shift]
// (quote-aware via the scanner family; lives here per the rule).
let braceStack (prev: (char * int) list) (line: string) : (char * int) list =
    // rides the one scanner [D:one-scanner]
    foldOutsideStrings
        (fun stack i c ->
            match c with
            | '{'
            | '['
            | '(' -> (c, i) :: stack
            // fmt drops a paren the offside rule has already closed, so a
            // `)` pops only its own kind
            | ')' ->
                (match stack with
                 | ('(', _) :: rest -> rest
                 | _ -> stack)
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
                        // source, not a field — the first field after `with`
                        // anchors, on this line or (dangling `with`) the next
                        // [D:field-alignment] [D:record-update]. Only a record
                        // brace can carry a `with`. The scan is bounded: it
                        // stops at the first top-level `=`, `;`, opener or
                        // closer, so a bracket-heavy single line stays linear
                        // [D:assemble-quadratic].
                        let withField =
                            if c <> '{' then
                                None
                            else
                                let mutable k = j
                                let mutable depth = 0
                                let mutable found = None
                                let mutable stop = false

                                while not stop && k < piece.Length do
                                    let ch = piece[k]

                                    if ch = '"' then
                                        k <- k + 1

                                        while k < piece.Length && piece[k] <> '"' do
                                            if piece[k] = '\\' then k <- k + 1
                                            k <- k + 1
                                    elif ch = '(' then
                                        depth <- depth + 1
                                    elif ch = ')' then
                                        depth <- depth - 1
                                        if depth < 0 then stop <- true
                                    elif depth = 0 && (ch = '=' || ch = ';' || ch = '{' || ch = '}' || ch = '[' || ch = ']') then
                                        stop <- true
                                    elif
                                        depth = 0
                                        && ch = 'w'
                                        && k > j
                                        && piece[k - 1] = ' '
                                        && k + 3 < piece.Length
                                        && piece[k + 1] = 'i'
                                        && piece[k + 2] = 't'
                                        && piece[k + 3] = 'h'
                                        && (k + 4 = piece.Length || piece[k + 4] = ' ')
                                    then
                                        let mutable f = k + 4

                                        while f < piece.Length && piece[f] = ' ' do
                                            f <- f + 1

                                        found <- Some f
                                        stop <- true

                                    k <- k + 1

                                found

                        match withField with
                        | Some f when f >= piece.Length -> None
                        | Some f -> Some(indent + f)
                        | None when j < piece.Length && piece[j] <> '}' && piece[j] <> ']' -> Some(indent + j)
                        | None -> None

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
      // where each segment's own text ends (aligned with Segments): the
      // synthetic join text after it is no physical column
      Ends: int list
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
      Active: int option
      // a heredoc's outdented `//` line, transparent only if the block
      // ends there — further content makes it an outdent error
      OutdentedComment: int option }

// content after an outdented heredoc comment: the comment sat inside the
// block, where every line is content — refuse like any outdent
let private heredocCommentGap (dst: District) : string option =
    match dst.Marker, dst.OutdentedComment with
    | MarkerKind.Heredoc, Some n ->
        Some
            $"line {n}: this comment line outdents below the heredoc's first line — inside a heredoc it is content: indent it to keep it, or move the comment out of the block"
    | _ -> None

// a marker-ending line arms its district, whatever kind of line it is —
// a plain head, a `| pat ->` arm, an `else` [D:arm-blocks]
let private armDistrict (marker: MarkerKind) (indent: int) (lineNo: int) : District option =
    markerOpener marker
    |> Option.map (fun (opener, strip, isYaml) ->
        { MarkerIndent = indent
          MarkerLine = lineNo
          Opener = opener
          Strip = strip
          Yaml = isYaml
          Marker = marker
          Active = None
          OutdentedComment = None })

type private Pend =
    { Buf: PendBuf
      Lets: (int * int) list
      LastIndent: int
      // sibling pipe columns, innermost first [D:pipe-alignment]: a
      // consecutive `|` line must sit exactly on a group column; the
      // first pipe after a non-pipe line opens a group. Per group:
      // (groupCol, bodyCol, isForward, parenDepth) [D:match-pipe-offside]. isForward =
      // a `|>` group vs a bare `|` (match/union arm) group; bodyCol = the
      // arm body's column (after the arm `->`; for a dangling arm resolved
      // to the body line's indent when it arrives; MaxValue until then).
      // A forward `|>` landing on a bare arm group: at the `|` it closes
      // the match, at or under the body it extends the arm, left of the
      // body it is rejected.
      // The 4th element is the paren depth at open: a line whose closers
      // drop below it ends the group — `(match … | _ -> x)` leaves no arm
      // group behind [D:compound-paren-prune]
      PipeGroups: (int * int * bool * int) list
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

// `within env A=1 …` [D:within-env-pairs]: its block joins sentineled too,
// so a first body line that is itself `B=2 cmd` stays that command's own
// prefix instead of widening the scope (the binder-head shape: an
// identifier glued to `=`; a bound `within env e` keeps the space join)
let private bufEndsInEnvHead (b: PendBuf) : bool =
    endsInBinderHeadSeg "within env " (bufLastSeg b)

// materialize a LogicalLine from a finished pending buffer (segments are
// reversed at the boundary exactly as before)
let private bufToLL (b: PendBuf) : LogicalLine =
    { Text = b.Sb.ToString()
      Head = b.Head
      Segments = List.rev b.Segments
      Ends = List.rev b.Ends |> List.toArray
      PosIndex = null }

// a line's segments re-pointed at the physical indents in `indents`
// (line -> leading spaces): assembly recovery re-indents a misaligned
// line, and every position map must read the source, not the repair
// [D:recovery-physical-indent]
let withPhysicalIndents (indents: Map<int, int>) (ll: LogicalLine) : LogicalLine =
    if ll.Segments |> List.exists (fun (_, l, _) -> indents.ContainsKey l) then
        { ll with
            Segments =
                ll.Segments
                |> List.map (fun (js, l, i) ->
                    match indents.TryFind l with
                    | Some phys -> js, l, phys
                    | None -> js, l, i)
            PosIndex = null }
    else
        ll

// a fresh single-line buffer (the assembler's per-statement seed)
let private bufNew (text: string) (lineNo: int) (indent: int) : PendBuf =
    let sb = System.Text.StringBuilder(text)
    let nonWs, segStart = scanTail sb 0 -1 0

    { Sb = sb
      Head = lineNo
      Segments = [ (0, lineNo, indent) ]
      Ends = [ text.Length ]
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

            let b =
                { b with
                    LastNonWs = nonWs
                    LastSegStart = segStart
                    Ends = b.Ends |> List.map (min b.Sb.Length) }

            bufAppend b (opener + piece + ")")
        | JDistrictSibling opener -> bufAppend b (" ; " + opener + piece + ")")
        | JDistrictPipe ->
            // only on a `|` continuation line inside a district — not a
            // quadratic path; rescan the (now one-shorter) buffer whole
            b.Sb.Remove(b.Sb.Length - 1, 1) |> ignore
            let nonWs, segStart = scanTail b.Sb 0 -1 0

            let b =
                { b with
                    LastNonWs = nonWs
                    LastSegStart = segStart
                    Ends = b.Ends |> List.map (min b.Sb.Length) }

            bufAppend b (" " + piece + ")")
        | JYamlLine rel -> bufAppend b (Parser.sibSepStr + String(' ', rel) + piece)

    // the piece ends before a district's closing ")"
    let pieceEnd =
        match j with
        | JDistrictOpen _
        | JDistrictSibling _
        | JDistrictPipe -> bufLen b - 1
        | _ -> bufLen b

    { b with
        Segments = (joinedStart, lineNo, indent) :: b.Segments
        Ends = pieceEnd :: b.Ends }

// onMisaligned (line, siblings' column) fires before a sibling-alignment
// error — the recovering assembler re-indents that line instead of
// dropping it, so its closers survive (no cascading "still open")
let assembleWith (onMisaligned: int -> int -> unit) (numbered: (int * string) list) : Result<LogicalLine list, string> =
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
            if cls.Kind = PieceKind.LetHead then
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
                    |> List.map (fun (js, l, i) -> (if js >= ts then js + 1 else js), l, i)
                Ends =
                    List.map2 (fun (js, _, _) e -> if js >= ts || e > ts then e + 1 else e) b.Segments b.Ends }

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
                            // a comment-shaped block line is content, so it
                            // obeys the content indentation rule
                            // [D:spaces-only-indent]
                            | Some { District = Some { Yaml = true; MarkerIndent = m } } when
                                (leadingTabCol raw).IsSome
                                && (raw |> Seq.takeWhile (fun c -> c = ' ' || c = '\t') |> Seq.length) > m
                                ->
                                Error(tabIndentError lineNo raw)
                            | Some({ District = Some({ Active = Some bse
                                                       Yaml = true
                                                       MarkerIndent = m } as dst) } as p) when
                                (let ind = raw |> Seq.takeWhile ((=) ' ') |> Seq.length in ind > m && ind >= bse)
                                ->
                                let ind = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                                if (heredocCommentGap dst).IsSome then
                                    Error (heredocCommentGap dst).Value
                                else
                                    Ok(
                                        Some
                                            { p with
                                                Buf = applyJoin (JYamlLine(ind - bse)) p.Buf (raw.Substring ind) lineNo ind },
                                        acc,
                                        blankSinceHead
                                    )
                            // a comment-only FIRST content line is bytes in a
                            // HEREDOC [D:block-scalars]: it arms the district
                            // and fixes the block base exactly as any first
                            // line does — transparency fired here once, and a
                            // heredoc's leading `// header` line silently
                            // vanished from the value. Heredocs only: a yaml
                            // template strips `//` lines as its own comments,
                            // so a leading one stays transparent there (an
                            // inserted comment must not fix the block base —
                            // the fuzzer's comment-neutrality invariant)
                            | Some({ District = Some({ Active = None
                                                       Yaml = true
                                                       Marker = MarkerKind.Heredoc
                                                       MarkerIndent = m } as dst) } as p) when
                                (raw |> Seq.takeWhile ((=) ' ') |> Seq.length) > m
                                ->
                                let ind = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                                Ok(
                                    Some
                                        { p with
                                            Buf = applyJoin (JYamlLine 0) p.Buf (raw.Substring ind) lineNo ind
                                            LastIndent = ind
                                            District = Some { dst with Active = Some ind } },
                                    acc,
                                    blankSinceHead
                                )
                            // an outdented `//` inside an ACTIVE heredoc: transparent
                            // only if the block ends here; content after it makes it
                            // an outdent error (pasted code lost its comment)
                            | Some({ District = Some({ Active = Some _
                                                       Yaml = true
                                                       Marker = MarkerKind.Heredoc
                                                       OutdentedComment = None } as dst) } as p) ->
                                Ok(Some { p with District = Some { dst with OutdentedComment = Some lineNo } }, acc, blankSinceHead)
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
                            let indent = raw |> Seq.takeWhile ((=) ' ') |> Seq.length

                            // indentation is spaces only, block content
                            // included: a tab is content only after the
                            // line's first non-whitespace character
                            // [D:spaces-only-indent]
                            if (leadingTabCol raw).IsSome then
                                Error(tabIndentError lineNo raw)
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
                                                        onMisaligned lineNo c

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
                                                |> Result.bind (fun brackets ->
                                                    let joinedP =
                                                        { p with
                                                            Buf = applyJoin join p.Buf piece lineNo indent
                                                            LastIndent = indent
                                                            Brackets = brackets }

                                                    if not brackets.IsEmpty then
                                                        Ok(Some joinedP, acc, blankSinceHead)
                                                    else
                                                        // the line closing the last bracket rejoins paren
                                                        // tracking: its parens move the depth, so `])` /
                                                        // `})` closes a multiline lambda exactly as a
                                                        // `)` line does, and a dangling `(fun x ->` after
                                                        // the closer opens one [D:multiline-lambda]
                                                        let depth = p.ParenDepth + parenDelta piece

                                                        let popped, kept =
                                                            p.Lambdas
                                                            |> List.partition (fun (_, _, d0, _) -> d0 >= depth)

                                                        let innerLet =
                                                            match List.tryLast popped with
                                                            | Some(_, oindent, _, _) ->
                                                                p.Lets |> List.tryFind (fun (k, _) -> k > oindent)
                                                            | None -> None

                                                        match innerLet with
                                                        | Some(_, letLine) -> noBody letLine
                                                        | None ->
                                                            let backTo =
                                                                match popped with
                                                                | [] -> joinedP.LastIndent
                                                                | ps ->
                                                                    let (_, _, _, restore) = List.last ps
                                                                    restore

                                                            let stmtLevel =
                                                                if popped.IsEmpty then p.StmtLevel else backTo

                                                            let kept =
                                                                if lambdaOpens piece then
                                                                    (lineNo, indent, depth - 1, stmtLevel) :: kept
                                                                else
                                                                    kept

                                                            Ok(
                                                                Some
                                                                    { joinedP with
                                                                        LastIndent = backTo
                                                                        StmtLevel = stmtLevel
                                                                        ParenDepth = depth
                                                                        Lambdas = kept
                                                                        Compounds =
                                                                            p.Compounds
                                                                            |> List.filter (fun (_, _, d) -> d <= depth)
                                                                        PipeGroups =
                                                                            if popped.IsEmpty then
                                                                                p.PipeGroups
                                                                            else
                                                                                p.PipeGroups
                                                                                |> List.skipWhile (fun (g, _, _, _) ->
                                                                                    g > backTo)
                                                                                |> List.filter (fun (_, _, _, d) ->
                                                                                    d <= depth)
                                                                        LastWasPipe =
                                                                            if popped.IsEmpty then p.LastWasPipe else false },
                                                                acc,
                                                                blankSinceHead
                                                            ))
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
                                                if (heredocCommentGap dst).IsSome then
                                                    Error (heredocCommentGap dst).Value
                                                elif indent < bse then
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
                                                                                 || bufEndsInEnvHead p.Buf
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
                                                                        |> List.skipWhile (fun (g, _, _, _) -> g > backTo)
                                                                        |> List.filter (fun (_, _, _, d) -> d <= depth)
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
                                                            p.PipeGroups |> List.skipWhile (fun (g, _, _, _) -> g > indent)

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
                                                                    | (_, _, false, _) :: _ -> true
                                                                    | _ -> false)
                                                            then
                                                                match groups with
                                                                | (g, _, _, _) :: _ when indent = g ->
                                                                    // at the `|` column: close the match,
                                                                    // pipe the whole
                                                                    Ok(true, groups)
                                                                | (g, bc, _, _) :: _ when indent < bc ->
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
                                                                    Ok(false, (indent, indent, true, p.ParenDepth) :: groups)
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
                                                                | (g, _, false, d) :: rest when g = indent && not isFwd ->
                                                                    // a returning arm after a non-pipe
                                                                    // body [D:match-pipe-offside]: the
                                                                    // group at this exact column is the
                                                                    // arm's own match — a deeper open
                                                                    // compound (the previous arm's
                                                                    // if/let tail) offside-closes below,
                                                                    // it is not "its match"
                                                                    Ok(false, (g, bodyCol, false, d) :: rest)
                                                                | _ ->
                                                                    match p.Compounds with
                                                                    | (h, _, _) :: _ when indent < h ->
                                                                        Error
                                                                            $"line {lineNo}: this arm sits left of its match (head at column {h}) — align arms at or right of it"
                                                                    | _ ->
                                                                        Ok(
                                                                            false,
                                                                            (indent, (if isFwd then indent else bodyCol), isFwd, p.ParenDepth)
                                                                            :: groups
                                                                        )
                                                            else
                                                                match groups with
                                                                | (g, _, false, d) :: rest when g = indent && not isFwd ->
                                                                    // a new arm aligns with its siblings:
                                                                    // reset the group's body column to this
                                                                    // arm's — each arm has its own body
                                                                    // offside [D:match-pipe-offside]
                                                                    Ok(false, (g, bodyCol, false, d) :: rest)
                                                                | (g, _, _, _) :: _ when g = indent ->
                                                                    // aligned sibling (pipeline stage)
                                                                    Ok(false, groups)
                                                                | (g, _, _, _) :: _ ->
                                                                    onMisaligned lineNo g

                                                                    Error
                                                                        $"line {lineNo}: this line is indented off its siblings (they sit at column {g}) — align the group exactly"
                                                                | [] ->
                                                                    Error
                                                                        $"line {lineNo}: this line (column {indent}) is indented off its siblings — align the group exactly"

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

                                                            let unpruned =
                                                                if closes then
                                                                    (indent, indent, true, p.ParenDepth)
                                                                    :: (groups
                                                                        |> List.filter (fun (g, _, _, _) -> g < indent))
                                                                else
                                                                    groups

                                                            let pipeGroups =
                                                                unpruned |> List.filter (fun (_, _, _, d) -> d <= depth)

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
                                                                                     || bufEndsInEnvHead buf
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
                                                                        District = armDistrict cls.Marker indent lineNo
                                                                        // a closing `|>` pops the arm
                                                                        // group and opens a fresh forward
                                                                        // group so a following `|>` aligns
                                                                        // as a pipeline [D:match-pipe-offside]
                                                                        PipeGroups = pipeGroups
                                                                        // a group this line's `)` closed ends the
                                                                        // pipe run: the next pipe opens afresh
                                                                        LastWasPipe =
                                                                            not (isUntil || isAlways)
                                                                            && pipeGroups.Length = unpruned.Length
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
                                                            | _ when bufEndsInProcHead buf || bufEndsInServeHead buf || bufEndsInEnvHead buf ->
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

                                                        let district = armDistrict cls.Marker indent lineNo

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
                                                                             | (g, bc, false, d) :: rest when
                                                                                 bc = System.Int32.MaxValue
                                                                                 && p.PrevDangles
                                                                                 && indent > g
                                                                                 ->
                                                                                 (g, indent, false, d) :: rest
                                                                             | pg -> pg)
                                                                            |> List.skipWhile (fun (g, _, _, _) ->
                                                                                g > lastIndent)
                                                                            |> List.filter (fun (_, _, _, d) -> d <= depth)
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
                                          District = armDistrict cls.Marker 0 lineNo
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
let assemble (numbered: (int * string) list) : Result<LogicalLine list, string> =
    assembleWith (fun _ _ -> ()) numbered

/// an assembly error's location [D:assembly-col]: the line its message
/// names and that line's first non-blank column, with the message's
/// `line N: ` lead removed (the location carries it). None when the
/// message names no line.
let assemblyErrorAt (numbered: (int * string) list) (msg: string) : (int * int * string) option =
    let m = Text.RegularExpressions.Regex.Match(msg, @"^line ([0-9]{1,9}): ")

    if not m.Success then
        None
    else
        let line = int m.Groups[1].Value

        let col =
            numbered
            |> List.tryFind (fun (n, _) -> n = line)
            |> Option.map (fun (_, raw: string) -> (raw |> Seq.takeWhile ((=) ' ') |> Seq.length) + 1)
            |> Option.defaultValue 1

        Some(line, col, msg.Substring m.Length)

/// the located one-line spelling of an assembly error [D:assembly-col]:
/// `<where>:<line>:<col>: msg`, the message unchanged when it names no line
let assemblyErrorLocated (where: string) (numbered: (int * string) list) (msg: string) : string =
    match assemblyErrorAt numbered msg with
    | Some(l, c, rest) -> $"{where}:{l}:{c}: {rest}"
    | None -> $"{where}: {msg}"

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
    // a column-0 `#` line is a directive, one line by definition — it
    // never continues the statement above it [D:repl-multiline]
    if classifyLine next <> LineKind.Code || next.StartsWith "#" then
        false
    else
        match statementCount buf, statementCount (buf @ [ next ]) with
        | _, None -> true
        | None, Some _ -> true
        | Some a, Some b -> b <= a

// a logical line's segment index, built once per line: translate is called
// per typed node, and a linear scan per call made a long statement
// quadratic (a 64k-line heredoc)
type private SegIndex =
    { Starts: int[]
      Lines: int[]
      Indents: int[]
      // where each segment's own text ends — the synthetic join text
      // between it and the next segment is no physical column
      Ends: int[]
      // what it was built from [D:line-pos-cache]
      ForText: string
      ForSegments: (int * int * int) list
      ForEnds: int[] }

let private buildSegIndex (ll: LogicalLine) : SegIndex =
    let segs = List.toArray ll.Segments
    let starts = segs |> Array.map (fun (js, _, _) -> js)

    // a line not from the assembler has no join text to step over
    let ends =
        if ll.Ends.Length = starts.Length then
            ll.Ends
        else
            Array.init starts.Length (fun i -> if i + 1 < starts.Length then starts[i + 1] else ll.Text.Length)

    { Starts = starts
      Lines = segs |> Array.map (fun (_, l, _) -> l)
      Indents = segs |> Array.map (fun (_, _, i) -> i)
      Ends = ends
      ForText = ll.Text
      ForSegments = ll.Segments
      ForEnds = ll.Ends }

let private segIndexOf (ll: LogicalLine) : SegIndex =
    match ll.PosIndex with
    | :? SegIndex as ix when
        obj.ReferenceEquals(ix.ForText, ll.Text)
        && obj.ReferenceEquals(ix.ForSegments, ll.Segments)
        && obj.ReferenceEquals(ix.ForEnds, ll.Ends)
        ->
        ix
    | _ ->
        let ix = buildSegIndex ll
        ll.PosIndex <- ix
        ix

let translate (ll: LogicalLine) (col: int) : int * int =
    let ix = segIndexOf ll
    let joinedIdx = col - 1

    // the last segment starting at or before the index
    let i =
        match Array.BinarySearch(ix.Starts, joinedIdx) with
        | i when i >= 0 ->
            // equal starts (an empty segment): the last of them, as before
            let mutable j = i

            while j + 1 < ix.Starts.Length && ix.Starts[j + 1] = joinedIdx do
                j <- j + 1

            j
        | i -> max 0 (~~~i - 1)

    // a position in the join text sits just past its line's last
    // character, never beyond the physical line — except on the
    // assembler's wrapping "(" directly before the next segment, which
    // opens that segment's compound: it maps to the segment's start
    let next = i + 1

    if
        joinedIdx > ix.Ends[i]
        && next < ix.Starts.Length
        && (let mutable k = joinedIdx
            while k < ix.Starts[next] && ll.Text[k] = '(' do
                k <- k + 1
            k = ix.Starts[next])
    then
        ix.Lines[next], ix.Indents[next] + 1
    else
        let at = min joinedIdx ix.Ends[i]
        ix.Lines[i], at - ix.Starts[i] + ix.Indents[i] + 1

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

// the string and hole masks of a line with `$"…{hole}…"` strings: a hole
// is code inside a string, so the colorizer paints it as code
// [D:interp-hole-complete]. The kinds mirror `closers`: plain and interp
// strings (backslash escapes), verbatim @"…" (`""` stays inside), triple
// """…""", single-quoted; `{{` in an interp string is a literal brace; a
// string inside a hole is a string again; an unclosed one runs to the end
let private interpMasks (line: string) : bool[] * bool[] =
    let str = Array.create line.Length false
    let hole = Array.create line.Length false
    let mutable stack: char list = []
    let mutable i = 0

    let mark (from: int) (until: int) =
        for j in from .. min until (line.Length - 1) do
            str[j] <- true

    while i < line.Length do
        let c = line[i]

        match stack with
        | ('"' | '$') :: rest ->
            str[i] <- true

            if c = '\\' && i + 1 < line.Length then
                let e = Scan.escapeEnd line i
                mark i e
                i <- e
            elif c = '"' then
                stack <- rest
            elif c = '{' && List.head stack = '$' then
                if i + 1 < line.Length && line[i + 1] = '{' then
                    mark i (i + 1)
                    i <- i + 1
                else
                    stack <- '}' :: stack
        | 'V' :: rest ->
            str[i] <- true

            if c = '"' then
                if i + 1 < line.Length && line[i + 1] = '"' then
                    mark i (i + 1)
                    i <- i + 1
                else
                    stack <- rest
        | 'T' :: rest ->
            str[i] <- true

            if c = '"' && i + 2 < line.Length && line[i + 1] = '"' && line[i + 2] = '"' then
                mark i (i + 2)
                i <- i + 2
                stack <- rest
        | '\'' :: rest ->
            str[i] <- true

            if c = '\'' then
                stack <- rest
        | top ->
            let inHole =
                match top with
                | '}' :: _ -> true
                | _ -> false

            if inHole && c = '}' then
                // the hole's closing brace belongs to the string
                str[i] <- true
                stack <- List.tail stack
            else
                if inHole then
                    hole[i] <- true

                if c = '"' && i + 2 < line.Length && line[i + 1] = '"' && line[i + 2] = '"' then
                    mark i (i + 2)
                    stack <- 'T' :: stack
                    i <- i + 2
                elif c = '"' then
                    str[i] <- true
                    stack <- (if i > 0 && line[i - 1] = '@' then 'V'
                              elif i > 0 && line[i - 1] = '$' then '$'
                              else '"') :: stack
                elif c = '\'' then
                    str[i] <- true
                    stack <- '\'' :: stack
                elif c = '{' && inHole then
                    stack <- '}' :: stack

        i <- i + 1

    (str, hole)

let colorizeRepl (isKnown: string -> bool) (line: string) : string =
    if line = "" then
        ""
    else
        // per-char color codes; None = plain
        let codes: string option array = Array.create line.Length None
        // a line with an interp string takes the hole-aware masks; any
        // other line keeps the shared scanner's
        let mask, holes =
            if line.Contains "$\"" then
                interpMasks line
            else
                inStringMask line, Array.create line.Length false

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
                    // an uppercase word after `|` is a module or constructor,
                    // never a program — a case-insensitive filesystem would
                    // otherwise find `Seq` as seq on PATH [D:constructors-not-heads]
                    match
                        (if holes[start] then
                             Complete.HeadSlot.No // a hole is an expression
                         else
                             match Complete.headSlotAt (line.Substring(0, start)) with
                             | Complete.HeadSlot.Stage when Char.IsUpper word[0] -> Complete.HeadSlot.No
                             | s -> s)
                    with
                    | Complete.HeadSlot.Forced ->
                        // ^head: PATH only — `^x` names a program,
                        // never a keyword, a form, or an alias
                        Some(if Extern.exists word then "34" else "31")
                    | slot ->
                        if slot <> Complete.HeadSlot.No
                           && i < line.Length
                           && line[i] = '='
                           && not (i + 1 < line.Length && line[i + 1] = '=') then
                            // an env-prefix name is not a head [D:env-prefix]
                            None
                        elif slot = Complete.HeadSlot.Stage
                             && (word = "and" || word = "or")
                             && (match Complete.lastStage (line.Substring(0, start)) with
                                 | Some(_, true) -> true
                                 | _ -> false) then
                            Some "34" // the chain words read as keywords [D:cmd-chaining]
                        elif Weir.Parser.keywords.Contains word then
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
                        elif slot = Complete.HeadSlot.Stmt || slot = Complete.HeadSlot.Stage then
                            Some "31" // unresolved statement or stage head: red
                        elif Char.IsUpper word[0] then
                            // the casing rule: types/ctors/modules — at the
                            // let-RHS an unknown uppercase head is a legal
                            // constructor application, never red
                            // [D:constructors-not-heads]
                            Some "33"
                        elif slot = Complete.HeadSlot.LetRhs then
                            Some "31" // unresolved lowercase RHS head: red
                        elif cmdMode && not holes[start] then
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
            elif line[i] = '$' || line[i] = '^' then
                // sigils, splices, force-prefix
                codes[i] <- Some "36"
                i <- i + 1

                if line[i - 1] = '$' && i < line.Length && free i && isIdentStart line[i] then
                    while i < line.Length && free i && isIdentCont line[i] do
                        codes[i] <- Some "36"
                        i <- i + 1
            elif "|><=+-*/".Contains(line[i]) then
                if line[i] = '|' && not holes[i] then
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
    // validator: a validated type's id and typed `validate` function
    // [D:validated-types] — the run registers its `create` from them
    | CType of decl: Decl * validator: (string * Check.TypedExpr) option
    | CNoop
    // an imported module's replayable body [D:modules-v1] — evaluated once
    // at exec into the module's own venv, exposed as `alias.member`
    | CImport of LoadedModule

// a checked, imported module [D:modules-v1]. Its check-time contributions
// (Members -> Modules[Alias], TypeDefs -> Types, TypeNames -> ModuleTypes)
// merge into the importer's tenv; Body replays at exec to build the
// module's values. AbsPath is the path as reached; the identity is its
// real path [D:module-once].
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
      // each TypeDefs entry's identity [D:transitive-types]
      TypeOrigins: (string * string) list
      // each statement with its logical line [D:can-report]: the line
      // carries the segment table, so a capability inside a module can
      // name its own file:line:col like any diagnostic
      Body: (LogicalLine * CheckedStmt) list
      // each signed member's /// doc, read from its signature [D:module-signatures]
      MemberDocs: Map<string, string list>
      // a prompt import may take an alias an earlier import holds — the
      // session's reload [D:repl-import]; scripts and -e never set it
      MayRebind: bool }

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

// the #session prompt's status domain [D:prompt-status-arg]: records
// are nominal and init.weir admits no signatures, so a provider's
// param types as a ROW ({ ok: bool; .. } — field access on a bare
// param) — accepted when every named field is PromptStatus's own,
// exactly typed; a bare unused param ('a) is accepted and simply
// ignores the status. One predicate, read by the loader and the LSP.
let promptStatusDomain (dom: Ty) : bool =
    match dom with
    | TNamed("PromptStatus", []) -> true
    | TVar _ -> true
    | TRowVar(_, fields) ->
        fields
        |> List.forall (fun (f, t) ->
            // an unpinned var is a field the body only forwards (show,
            // isSome) — the runtime value satisfies it; a WRONGLY pinned
            // field (exit as Option<string>, a hole-defaulted ok) refuses
            match f, t with
            | "ok", (TBool | TVar _) -> true
            | "exit", (TNamed("Option", [ TInt ]) | TNamed("Option", [ TVar _ ]) | TVar _) -> true
            | "duration", (TDur | TVar _) -> true
            | _ -> false)
    | _ -> false

let private baseEnvs (scriptArgs: string list) (scriptPath: string) =
    let typeEnv = Builtins.typeEnvStrict

    let typeEnv, valueEnv = Prelude.extend typeEnv Builtins.valueEnv

    let typeEnv =
        { typeEnv with
            Modules = typeEnv.Modules |> Map.add "Self" selfMembers }

    let stdinStream =
        // one enumeration [D:prompt]: stdin is a live stream — a second
        // GetEnumerator cannot rewind the fd, and yielding empty there
        // silently produces wrong data; raise with the repair instead.
        // 0 untouched, 1 an enumeration began, 2 it read: one cut before
        // its first read (a race's loser) read nothing, so it leaves stdin
        // untouched [D:stdin-reader]
        let consumed = ref 0

        let firstLine () =
            try
                let line = Session.Stdin.readLine ()
                consumed.Value <- 2
                line
            with _ ->
                System.Threading.Interlocked.CompareExchange(&consumed.contents, 0, 1) |> ignore
                reraise ()

        Eval.VSeq(
            Seq.delay (fun () ->
                if System.Threading.Interlocked.CompareExchange(&consumed.contents, 1, 0) <> 0 then
                    failwith
                        "Self.stdin is a live stream and was already consumed — bind ONE enumeration (let lines = Self.stdin |> Seq.freeze), or read a line per interaction with `Self.prompt`"

                // weir's one stdin reader [D:stdin-reader]
                seq {
                    let mutable line = firstLine ()

                    while line <> null do
                        yield Eval.VStr line
                        line <- Session.Stdin.readLine ()
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

    // a file name is data [D:error-data-escape]
    $"{quoteData path}:{lineNo}: {msg}"

// the runtime-error line [D:binary-echo]: an error message may interpolate
// data (a hostile filename, a tenant name), and error text is a tty-bound
// renderer — so sanitize the message when stderr is a tty, the same guard
// print's data path uses. weir's own "error" colour word is added around
// the sanitized message, so colouring is untouched; redirected stderr
// stays byte-faithful.
let private runtimeErrorLine (path: string) (lineNo: int) (message: string) : string =
    let safe = Eval.sanitizeIfTty Console.IsErrorRedirected message
    located path lineNo (Color.red Color.onStderr.Value "error" + $": {safe}")

// every node of a statement's typed tree -> its physical (line, col), via
// that statement's OWN line map [D:runtime-position] (a logical span means
// nothing through another statement's map — [D:row-provenance]). Keyed by
// node identity; iterative, so a wide tree cannot overflow the stack.
let private recordPositions
    (file: string)
    (stmt: int)
    (ll: LogicalLine)
    (root: Check.TypedExpr)
    (into: System.Collections.Generic.Dictionary<obj, string * int * int * int>)
    =
    let stack = System.Collections.Generic.Stack<Check.TypedExpr>()
    stack.Push root

    while stack.Count > 0 do
        let n = stack.Pop()
        let key = box n

        if not (into.ContainsKey key) then
            if n.Span.Start.Col >= 1 then
                try
                    let l, c = translate ll n.Span.Start.Col
                    into[key] <- (file, stmt, l, c)
                with _ ->
                    ()

            for c in Check.childExprs n do
                stack.Push c

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
// binder so far (the sig-after-impl guard); SigLines = each signature's
// line, where its /// doc lives (one doc home).
type SigContext =
    { Pending: Map<string, MemberSig>
      Signed: Set<string>
      Implemented: Set<string>
      SigLines: Map<string, int> }

module SigContext =
    let empty =
        { Pending = Map.empty
          Signed = Set.empty
          Implemented = Set.empty
          SigLines = Map.empty }

type CheckedKind =
    | KType of decl: Decl * validator: (string * Check.TypedExpr) option
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
                    // an import's type, its bare name ambiguous [D:qualified-type-names]
                    && not (tenv.ModuleTypes |> Map.exists (fun _ owned -> owned.ContainsKey n))
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
    // `!` likewise: a guard that only teaches `not` (prefix `!` is not weir)
    let bannedTokens =
        Parser.foreignKeywordWords |> List.map (fun w -> "'" + w + "'") |> Set.ofList |> Set.add "'!'"

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
                    // two alternatives can carry the same relabelled token
                    // [D:clean-parse-dump]: the separator renders `';'` from
                    // more than one branch, so the set read `';' or ';'` —
                    // collapse to distinct, first spelling kept
                    |> List.distinct

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
    // the dump quotes source text (a head, an excerpt) [D:error-data-escape]
    |> List.map quoteData
    |> String.concat "\n"

// ---- transitive types [D:transitive-types] -------------------------------
// a type reached only through another module's signatures holds its plain
// name while nothing else wants it; a local declaration or a direct
// import of another same-named type takes the name, and the transitive
// one moves to its origin-qualified name ("A.Pt") everywhere in the env,
// so its values keep their type

let rec private renameTy (m: Map<string, string>) (ty: Ty) : Ty =
    match ty with
    | TNamed(n, args) ->
        TNamed(Map.tryFind n m |> Option.defaultValue n, args |> List.map (renameTy m))
    | TFun(a, b) -> TFun(renameTy m a, renameTy m b)
    | TSeq t -> TSeq(renameTy m t)
    | TTuple ts -> TTuple(ts |> List.map (renameTy m))
    | TRowVar(r, fields) -> TRowVar(r, fields |> List.map (fun (f, t) -> f, renameTy m t))
    | t -> t

let private renameScheme (m: Map<string, string>) (sch: Scheme) : Scheme = { sch with Ty = renameTy m sch.Ty }

let private renameDef (m: Map<string, string>) (def: TypeDef) : TypeDef =
    match def with
    | Record rd ->
        Record
            { rd with
                Fields = rd.Fields |> List.map (fun (f, t) -> f, renameTy m t) }
    | Union u ->
        Union
            { u with
                Cases = u.Cases |> List.map (fun (c, p) -> c, p |> Option.map (renameTy m)) }

let private renameEnvTypes (m: Map<string, string>) (tenv: TypeEnv) : TypeEnv =
    if Map.isEmpty m then
        tenv
    else
        let key n = Map.tryFind n m |> Option.defaultValue n

        { tenv with
            Values = tenv.Values |> Map.map (fun _ sch -> renameScheme m sch)
            Modules = tenv.Modules |> Map.map (fun _ ms -> ms |> Map.map (fun _ sch -> renameScheme m sch))
            ModulePrivate = tenv.ModulePrivate |> Map.map (fun _ ms -> ms |> Map.map (fun _ sch -> renameScheme m sch))
            Types = tenv.Types |> Map.toList |> List.map (fun (n, d) -> key n, renameDef m d) |> Map.ofList
            ModuleTypes = tenv.ModuleTypes |> Map.map (fun _ owned -> owned |> Map.map (fun _ k -> key k))
            TypeOrigins = tenv.TypeOrigins |> Map.toList |> List.map (fun (n, o) -> key n, o) |> Map.ofList }

// "A.Pt@/abs/a.weir" -> "A.Pt", made unique against the env's types
let private qualifiedFree (tenv: TypeEnv) (origin: string) : string =
    let at = origin.IndexOf '@'
    let q = if at > 0 then origin.Substring(0, at) else origin

    let rec free (c: string) =
        if Map.containsKey c tenv.Types then free (c + "'") else c

    free q

let private isTransitive (tenv: TypeEnv) (n: string) =
    tenv.TypeOrigins.ContainsKey n
    && not (tenv.ModuleTypes |> Map.exists (fun _ owned -> owned |> Map.exists (fun _ k -> k = n)))

/// a transitive type named `n` gives its plain name up (see above)
let yieldTransitive (n: string) (tenv: TypeEnv) : TypeEnv =
    if isTransitive tenv n then
        renameEnvTypes (Map [ n, qualifiedFree tenv tenv.TypeOrigins[n] ]) tenv
    else
        tenv

// "A" + "T" -> "A.T", made unique against the env's types and `taken`
let private qualifiedKey (tenv: TypeEnv) (taken: Set<string>) (alias: string) (n: string) : string =
    let rec free (c: string) =
        if Map.containsKey c tenv.Types || taken.Contains c then free (c + "'") else c

    free (alias + "." + n)

/// an imported type holding the plain name `n` gives it up: a transitive
/// one to its origin-qualified name, an import's own one to its
/// alias-qualified key [D:qualified-type-names]
let yieldImported (n: string) (tenv: TypeEnv) : TypeEnv =
    let tenv = yieldTransitive n tenv

    match
        tenv.ModuleTypes
        |> Map.tryFindKey (fun _ owned -> owned |> Map.exists (fun plain k -> plain = n && k = n))
    with
    | Some holder when tenv.TypeOrigins.ContainsKey n ->
        renameEnvTypes (Map [ n, qualifiedKey tenv Set.empty holder n ]) tenv
    | _ -> tenv

// merge a loaded module into the importer's env [D:modules-v1]: values
// (and union ctors) under Modules[Alias], types under their key, and the
// alias's name -> key table under ModuleTypes. An own type keys by its
// plain name while no other import declares that name; when one does,
// both move to their qualified keys ("A.T", "B.T") and the bare name
// resolves to neither [D:qualified-type-names]; a local declaration keeps
// the bare name and the import's type is reached qualified. Its
// transitive types [D:transitive-types] join an existing entry of the
// same origin, else take the plain name if free, else the qualified one.
let private mergeModule (tenv: TypeEnv) (lm: LoadedModule) : TypeEnv =
    let originOf n =
        lm.TypeOrigins |> List.tryFind (fst >> (=) n) |> Option.map snd

    let own = Set.ofList lm.TypeNames

    // the aliases already declaring a type named n
    let ownersOf (t: TypeEnv) (n: string) =
        t.ModuleTypes |> Map.toList |> List.choose (fun (a, owned) -> Map.tryFind n owned |> Option.map (fun k -> a, k))

    // each own type claims its key, moving another import's holder of the
    // plain name to that holder's qualified key
    let tenv, ownKeys =
        lm.TypeNames
        |> List.fold
            (fun (t: TypeEnv, keys: Map<string, string>) n ->
                let taken = keys |> Map.values |> Set.ofSeq

                match originOf n |> Option.bind (fun o -> t.TypeOrigins |> Map.tryFindKey (fun _ held -> held = o)) with
                | Some existing -> t, Map.add n existing keys
                | None ->
                    let t = yieldTransitive n t

                    if Map.containsKey n t.Types then
                        match ownersOf t n |> List.tryFind (fun (_, k) -> k = n) with
                        | Some(holder, _) ->
                            let t = renameEnvTypes (Map [ n, qualifiedKey t taken holder n ]) t
                            t, Map.add n (qualifiedKey t taken lm.Alias n) keys
                        | None -> t, Map.add n (qualifiedKey t taken lm.Alias n) keys
                    elif not (List.isEmpty (ownersOf t n)) then
                        t, Map.add n (qualifiedKey t taken lm.Alias n) keys
                    else
                        t, Map.add n n keys)
            (tenv, Map.empty)

    let subst =
        lm.TypeDefs
        |> List.filter (fun (n, _) -> not (own.Contains n))
        |> List.fold
            (fun (acc: Map<string, string>, taken: Set<string>) (n, _) ->
                let o = originOf n |> Option.defaultValue n

                match tenv.TypeOrigins |> Map.tryFindKey (fun _ held -> held = o) with
                | Some existing -> Map.add n existing acc, taken
                | None when
                    not (Map.containsKey n tenv.Types)
                    && not (own.Contains n)
                    && not (taken.Contains n)
                    && List.isEmpty (ownersOf tenv n)
                    ->
                    acc, Set.add n taken
                | None ->
                    let q = qualifiedFree tenv o

                    let rec unique (c: string) =
                        if taken.Contains c then unique (c + "'") else c

                    let q = unique q
                    Map.add n q acc, Set.add q taken)
            (Map.empty, ownKeys |> Map.values |> Set.ofSeq)
        |> fst
        |> fun transitive -> Map.fold (fun m n k -> if n = k then m else Map.add n k m) transitive ownKeys

    let rename n = Map.tryFind n subst |> Option.defaultValue n

    let typeDefs =
        lm.TypeDefs
        |> List.choose (fun (n, d) ->
            let n' = rename n

            // joined an existing same-origin entry: nothing to add
            match Map.tryFind n' tenv.TypeOrigins, originOf n with
            | Some held, Some o when held = o -> None
            | _ when n' <> n && Map.containsKey n' tenv.Types && not (own.Contains n) -> None
            | _ -> Some(n', renameDef subst d))

    let origins =
        lm.TypeOrigins |> List.map (fun (n, o) -> rename n, o)

    { tenv with
        Modules = Map.add lm.Alias (lm.Members |> List.map (fun (n, s) -> n, renameScheme subst s) |> Map.ofList) tenv.Modules
        Types = typeDefs |> List.fold (fun ts (n, d) -> Map.add n d ts) tenv.Types
        ModuleTypes = Map.add lm.Alias ownKeys tenv.ModuleTypes
        TypeOrigins = origins |> List.fold (fun m (n, o) -> Map.add n o m) tenv.TypeOrigins
        ModulePrivate =
            Map.add
                lm.Alias
                (lm.PrivateMembers |> List.map (fun (n, s) -> n, renameScheme subst s) |> Map.ofList)
                tenv.ModulePrivate }

let rec private checkStatementCore
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

    // a command head not on PATH, named precisely: the parse path's dump
    // and the runner's "unbound variable" for an unknown statement head both
    // come down to a missing program [D:missing-command-diagnosis]
    let missingCommandDiagWith (exact: bool) (onlyName: (string * bool) option) : StmtDiag option =
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
                    @ (chainStageHeads e |> List.filter (fst >> Extern.exists >> not))
                    @ (reifiedHead e |> Option.filter (fst >> Extern.exists >> not) |> Option.toList)
                    @ (exprChildren e |> List.collect heads)

                e |> Option.map heads |> Option.defaultValue []

        // the runner reads a dashed head as subtraction (`no-such-tool` as
        // `no - …`) and reports its first segment unbound — no weir name has
        // a dash, so it is the program even past a near-miss suggestion
        // [D:missing-command-diagnosis]
        let namesHead (n: string, suggested: bool) (prog: string) =
            (n = prog && not suggested) || (not exact && prog.StartsWith(n + "-"))

        match missingHeads with
        | (prog, span) :: rest when (match onlyName with Some n -> namesHead n prog | None -> true) ->
            let physLine, physCol = translate ll span.Start.Col

            let others =
                match rest |> List.map fst |> List.distinct |> List.filter ((<>) prog) with
                | [] -> ""
                | more ->
                    let joined = String.concat ", " more
                    $" (also missing: {joined})"

            Some
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
                         // a misspelled reifier after `|` reads as a program [D:reifier-term]
                         let afterPipe =
                             let at = min ll.Text.Length (max 0 (span.Start.Col - 1))
                             ll.Text.Substring(0, at).TrimEnd().EndsWith "|"

                         let reifier =
                             Ast.reifierNames
                             |> List.map (fun c -> c, editDistance prog c)
                             |> List.filter (fun (_, d) -> d <= 2)
                             |> List.sortBy snd
                             |> List.tryHead

                         match reifier with
                         // an exact reifier read as a program: the position
                         // refuses the chain, not the spelling [D:statement-lets]
                         | Some(c, 0) when afterPipe ->
                             $"'{c}' is a reifier, but this position is an expression — wrap the chain: $(<command> | {c}); bare command lets belong to statement bodies (top level, if/for/match arms, within blocks)"
                         | Some(c, _) when afterPipe ->
                             $"unknown command '{prog}' — not found on PATH. Did you mean the reifier '{c}'? (`<command> | {c}`)"
                         | _ ->
                             $"unknown command '{prog}' — not found on PATH{others}. weir resolves command names before running: install the tool, or, if it only exists when the script runs, name it at run time — `let tool = \"{prog}\"` then `^$tool`")
                  File = None
                  Note = None
                  Warnings = [] }
        | _ -> None

    let missingCommandDiag = missingCommandDiagWith false

    // an "unbound variable" that is really a missing program — a let's
    // command body inside a function reads as an application under the
    // runner's hard resolver — gets the unknown-command teaching; the
    // name must be the whole head, so `let y = lenth-1` stays arithmetic
    // [D:missing-command-diagnosis]
    let typedOrMissing (tag: StmtTag) (terr: Check.TypeError) =
        let unboundName =
            if terr.Message.StartsWith "unbound variable '" then
                Some(terr.Message.Substring(18).Split('\'').[0], terr.Message.Contains "Did you mean")
            else
                None

        match unboundName |> Option.bind (fun n -> missingCommandDiagWith true (Some n)) with
        | Some d -> Error d
        | None -> Error(typed tag terr)

    try
        match Parser.parseLineFull r ll.Text with
        | Error f ->
            // FParsec's primary error is often irrelevant when the real cause
            // is an unresolvable command head (the backtrack note buries it).
            // Retry under the assume-resolver: if that parses, the failure is
            // missing commands — name them precisely instead of the dump.
            // check reads a word that is no program as a command head
            // (assume-resolver); the runner reads it as an expression, so
            // a `|>`-into-a-program refusal of it is check's alone — take
            // the runner's verdict instead, so check and run agree
            // (`xs |> contains x` is module-qualified, not a program)
            // [D:assume-resolver]
            let runnersVerdict =
                if f.Message.StartsWith "'|>' applies functions" then
                    match Parser.parseLineFull (resolver tenv) ll.Text with
                    | Ok _ ->
                        match checkStatementCore gateExprs sigCtx resolver loadImport tenv ll with
                        | Error d -> Some d
                        | Ok _ -> None
                    | Error _ -> None
                else
                    None

            match missingCommandDiag None |> Option.orElse runnersVerdict with
            | Some d -> Error d
            | None ->
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
            // a local declaration takes its name from a transitive type
            // [D:transitive-types], and in the REPL from an import's own
            // type, which stays reachable qualified [D:qualified-type-names]
            // (a script refuses the redeclaration: dup-type-decl)
            match Check.checkDecl (yieldImported decl.Name tenv) decl with
            | Error terr -> Error(typed StmtTag.Type terr)
            | Ok tenv' ->
                // the `validate` block types against the declared type
                // [D:validated-types] — its purity is the post-check layer's
                let validator =
                    match decl.Validate, Types.typeDefFor tenv' decl.Name with
                    | Some v, Some(Union { Validated = Some id }) ->
                        Check.typecheckValidator tenv' decl.Name v |> Result.map (fun te -> Some(id, te))
                    | _ -> Ok None

                match validator with
                | Error terr -> Error(typed StmtTag.Type terr)
                | Ok validator ->
                    Ok
                        { Kind = KType(decl, validator)
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
                        | Ok(tenv', ty) ->
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
                        | PCase(_, Some inner, _) -> patVars inner
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
                | Error terr -> typedOrMissing StmtTag.LetPat terr
                | Ok(te, schemes) ->
                    Ok
                        { Kind = KLetPat(pat, schemes, te)
                          Env =
                            schemes |> List.fold (fun e (n, sch) -> bindValue n sch e) tenv
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
                    | Error(Check.SigBody terr) -> typedOrMissing StmtTag.Let terr
                    | Error(Check.SigShape(implTy, span)) ->
                        // a body ending in a command returns its output
                        // where the signature says unit [D:module-signatures]
                        let rec result (t: Ty) =
                            match t with
                            | TFun(_, r) -> result r
                            | t -> t

                        let rec endsInCommand (x: Expr) =
                            match x.Kind with
                            | ELambda(_, _, b)
                            | ELambdaPat(_, b)
                            | ELet(_, _, _, b)
                            | ELetPat(_, _, b)
                            | ESeq(_, b) -> endsInCommand b
                            | ECmd _ -> true
                            | EPipe(_, stage) -> (match stage.Kind with ECmd _ -> true | _ -> false)
                            | _ -> false

                        let hint =
                            match result sd.Ty, result implTy with
                            | TUnit, TSeq TStr when endsInCommand e ->
                                " — its last line is a command, so it returns the command's output (seq<string>); end the body with `()`"
                            | _ -> ""

                        sigErrAt
                            span
                            ($"the implementation of '{name}' does not match its signature: "
                             + $"the signature (line {sd.Line}) declares {formatTy sd.Ty}, but the implementation is {formatTy implTy}"
                             + hint)
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
                                bindValue name scheme tenv
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
                    | Error terr -> typedOrMissing StmtTag.Let terr
                    | Ok(te, cs, origins, holeDefaults) ->
                        let scheme = generalizeWithOrigins cs origins holeDefaults te.Ty

                        Ok
                            { Kind = KLet(name, scheme, te)
                              Env =
                                bindValue name scheme tenv
                              Warnings = warningsOf te }
        | Ok(SCmd e) ->
            let tenv = Check.withAnonDefs tenv e

            match Check.typecheck tenv e with
            | Error terr ->
                // a near-miss binding suggestion means a likely typo of a
                // weir name: keep it; otherwise the head is a missing program
                let unboundName =
                    if terr.Message.StartsWith "unbound variable '" then
                        Some(terr.Message.Substring(18).Split('\'').[0], terr.Message.Contains "Did you mean")
                    else
                        None

                match unboundName |> Option.bind (fun n -> missingCommandDiag (Some n)) with
                | Some d -> Error d
                | None -> Error(typed StmtTag.Cmd terr)
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
            // [D:within-scopes] — reaching through scopes and let-ins.
            // The REPL (gateExprs=false) arms block tails too, so a block's
            // final command inherits the terminal as a top-level one does
            // [D:armed-inherit]; a bare top-level chain stays unarmed for
            // the REPL's own inherit path and its streamed teaching
            let e =
                match e.Kind with
                | _ when gateExprs -> Check.armTail e
                | ECmd _
                | EPipe(_, { Kind = ECmd _ }) -> e
                | _ -> Check.armTail e
            let tenv = Check.withAnonDefs tenv e

            match Check.typecheck tenv e with
            | Error terr ->
                // a near-miss binding suggestion means a likely typo of a
                // weir name: keep it; otherwise the head is a missing program
                let unboundName =
                    if terr.Message.StartsWith "unbound variable '" then
                        Some(terr.Message.Substring(18).Split('\'').[0], terr.Message.Contains "Did you mean")
                    else
                        None

                match unboundName |> Option.bind (fun n -> missingCommandDiag (Some n)) with
                | Some d -> Error d
                | None -> Error(typed StmtTag.Expr terr)
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
            | Ok lm ->
            // a session re-import [D:repl-import]: the alias an earlier
            // import holds is unbound first, then the import proceeds as
            // fresh — its types, members and privates replace the old. Its
            // own type keys leave Types/TypeOrigins too, else the new
            // module's same-origin types would join the stale defs
            // [D:qualified-type-names]; a key another import still names
            // stays
            let tenv =
                if lm.MayRebind && tenv.ModuleTypes.ContainsKey lm.Alias then
                    let others = tenv.ModuleTypes |> Map.remove lm.Alias

                    let dropped =
                        tenv.ModuleTypes[lm.Alias]
                        |> Map.values
                        |> Seq.filter (fun k ->
                            tenv.TypeOrigins.ContainsKey k
                            && not (others |> Map.exists (fun _ owned -> owned |> Map.exists (fun _ k' -> k' = k))))
                        |> Set.ofSeq

                    { tenv with
                        Types = tenv.Types |> Map.filter (fun k _ -> not (dropped.Contains k))
                        TypeOrigins = tenv.TypeOrigins |> Map.filter (fun k _ -> not (dropped.Contains k))
                        Modules = Map.remove lm.Alias tenv.Modules
                        ModuleTypes = Map.remove lm.Alias tenv.ModuleTypes
                        ModulePrivate = Map.remove lm.Alias tenv.ModulePrivate }
                else
                    tenv

            match lm with
            | lm when Map.containsKey lm.Alias tenv.Modules ->
                Error(
                    stmtErr
                        $"the name '{lm.Alias}' is already a module in scope; import it under a different name with 'as'"
                )
            // an alias spelled like a union case in scope (`as Some`) would
            // read as the constructor at every use — refused, as a second
            // module of one name is
            | lm when
                tenv.Types
                |> Map.exists (fun _ def ->
                    match def with
                    | Union u -> u.Cases |> List.exists (fun (c, _) -> c = lm.Alias)
                    | _ -> false)
                ->
                Error(
                    stmtErr
                        $"the name '{lm.Alias}' is a union case in scope; import the module under a different name with 'as'"
                )
            | lm ->
                // a type name another import or a local declaration holds is
                // no clash: mergeModule keys it [D:qualified-type-names]
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
            | KType(_, Some(_, te)) -> Some(te, StmtTag.Type)
            | KType _
            | KSig _
            | KModule _
            | KImport _ -> None

        match teTag with
        | None -> Ok st
        | Some(te, tag) ->
            // pureViolation returns the complete located message
            // (pure's "…forbids effects, but…" or readonly's
            // "…forbids external mutation, but…") [D:pure-stage2]; a
            // validator is a pure region of its own [D:validated-types]
            let violation =
                match st.Kind with
                | KType _ -> Purity.validatorViolation tenv.PureBindings te
                | _ -> Purity.pureViolation tenv.PlanRefused tenv.PureBindings te

            match violation with
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
                                    PureBindings = Map.add name (Purity.isPureExpr tenv.PureBindings te) st.Env.PureBindings
                                    PlanRefused =
                                        match Purity.planRefusalOf tenv.PlanRefused te with
                                        | Some why -> Map.add name why st.Env.PlanRefused
                                        | None -> Map.remove name st.Env.PlanRefused } }
                | _ -> Ok st)

// ---- unused bindings [D:unused-bindings]: whole-file threading ------------
// The check is a whole-file judgement (a binder's readers may sit any
// number of statements later), so it applies post-fold — beside the
// missing-implementation check — in every consumer that folds a complete file:
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
        // a validator reads like a function body [D:validated-types]
        | KType(_, Some(_, te)) ->
            let free, locals = Check.bindingUsage te
            this.Uses free
            this.Locals ll locals
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
    // top-level tracked names -> their pulls [D:reenum-indirect];
    // id -> (command, repair)
    let tracked = System.Collections.Generic.Dictionary<string, Check.ReenumCarry>()
    let info = System.Collections.Generic.Dictionary<int, string * string>()
    let counts = System.Collections.Generic.Dictionary<int, int>()
    let found = ResizeArray<ReenumFinding>()
    let mutable nextId = 0
    let mutable poisoned = false

    let fresh () =
        let id = nextId
        nextId <- nextId + 1
        id

    let tracked0 () =
        tracked |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    /// count one statement's events: enumerating uses of the tracked set
    /// (top-level and any qualifying block-locals), a warning at the
    /// second and later sites — the first pull is the binding's point,
    /// the second is where the command silently runs again
    let consume (ll: LogicalLine) (events: Check.ReenumEvent list) =
        for ev in events do
            match ev with
            | Check.ReenumBind(id, _, cmd) ->
                // a block-local binder has no clean one-line source to
                // ride the repair — the generic spelling instead
                if not (info.ContainsKey id) then
                    info[id] <- (cmd, "add '|> Seq.freeze' at the binding")

                counts[id] <- 0
            | Check.ReenumUse(id, name, note, span) ->
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
                            $"possible re-enumeration: '{name}'{note} is command-backed and unforced — each pull re-runs '{cmd}'; snapshot one run: {repair}" }

    member _.Poison() = poisoned <- true

    member _.Feed (ll: LogicalLine) (chk: CheckedStatement) =
        match chk.Kind with
        | KType _
        | KSig _
        | KModule _
        | KImport _ -> ()
        | KLet(name, _, te) ->
            // RHS first (a same-name rebind reads the outer binding — no
            // `let rec` exists)
            let carry, events = Check.reenumLet fresh (tracked0 ()) name te

            // the binding's own command: the repair rides its source when
            // it is one clean line (as streamed-it does); assembled
            // (sentinel-joined) text falls back to the generic spelling
            (match Check.commandBackedUnforced te, carry.Pulls with
             | Some cmd, (id, n) :: _ when n = name ->
                 let src = ll.Text.Trim()

                 let repair =
                     if src.StartsWith "let " && not (src |> Seq.exists System.Char.IsControl) then
                         $"{src} |> Seq.freeze"
                     else
                         "add '|> Seq.freeze' at the binding"

                 info[id] <- (cmd, repair)
             | _ -> ())

            consume ll events
            tracked.Remove name |> ignore

            if not carry.Pulls.IsEmpty || carry.Forced then
                tracked[name] <- carry
        | KLetPat(pat, _, te) ->
            consume ll (Check.reenumEvents fresh (tracked0 ()) te)

            for n, _ in Check.patNameSpans pat do
                tracked.Remove n |> ignore
        | KCmd te
        | KExpr te -> consume ll (Check.reenumEvents fresh (tracked0 ()) te)

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

// the lock as `weir:` resolution reads it [D:add-module-closure]: a
// closure module's entry file is recorded there, so the checker and the
// LSP read lock.json — once per (path, mtime, length), not per import
let private lockReads =
    System.Collections.Concurrent.ConcurrentDictionary<string, DateTime * int64 * Result<Contracts.LockEntry list, string>>()

let private readLockCached (weirDir: string) : Result<Contracts.LockEntry list, string> =
    let p = IO.Path.Combine(weirDir, "lock.json")

    let stamp =
        try
            let fi = IO.FileInfo p
            if fi.Exists then fi.LastWriteTimeUtc, fi.Length else DateTime.MinValue, -1L
        with _ ->
            DateTime.MinValue, -1L

    match lockReads.TryGetValue p with
    | true, (t, n, r) when (t, n) = stamp -> r
    | _ ->
        let r = Contracts.readLock weirDir
        lockReads[p] <- (fst stamp, snd stamp, r)
        r

// a closure module's entry file [D:add-module-closure]: the lock entry
// for a .weir/modules/<name>/ directory names it, confined like every
// lock path; a directory the lock does not record is an error, never a
// guessed entry
let private vendoredDirEntry (weirDir: string) (name: string) : Result<string, string> =
    let dir = IO.Path.Combine(weirDir, "modules", name)

    match readLockCached weirDir with
    | Error e -> Error $"'{quoteData dir}' is a vendored module directory, and the lock cannot be read: {e}"
    | Ok entries ->
        match entries |> List.tryFind (fun e -> e.Kind = "module" && e.Name = name) with
        | Some({ Closure = Some c } as e) -> Contracts.closureFileDest weirDir e c.Entry
        | Some _ ->
            Error
                $"'{quoteData dir}' is a vendored module directory, but the lock records module '{quoteData name}' as a single file — re-add it: weir add module <host>/<org>/<repo>//<file>@<ref> --as {quoteData name}"
        | None ->
            Error
                $"'{quoteData dir}' is a vendored module directory the lock has no entry for, so its entry file is unknown — re-add it (weir add module <host>/<org>/<repo>//<file>@<ref> --as {quoteData name}) or remove the directory"

let private resolveImportPathRaw (importingAbsPath: string) (path: string) : string =
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

            let modDir = IO.Path.GetFullPath(IO.Path.Combine(wd, "modules", name))

            if IO.File.Exists modPath then
                modPath
            elif IO.Directory.Exists modDir then
                // a closure module [D:add-module-closure]: its entry per
                // the lock (a fault is vendoredFault's error)
                match vendoredDirEntry wd name with
                | Ok entry -> entry
                | Error _ -> modPath
            else
                // one namespace, two homes [D:schema-types]: vendored
                // modules first, then generated types (.weir/types/ —
                // `weir gen types` output); a name in both is refused at
                // import (vendoredCollision), a missing name still errors
                // through the modules path below
                let typesPath = IO.Path.GetFullPath(IO.Path.Combine(wd, "types", name + ".weir"))

                if IO.File.Exists typesPath then typesPath else modPath
        | Error _ ->
            // no .weir/ anywhere: a display path for the not-found error
            IO.Path.GetFullPath(IO.Path.Combine(dir, ".weir", "modules", name + ".weir"))
    else
        IO.Path.GetFullPath(IO.Path.Combine(dir, path))

// a module's identity is its real path [D:module-once]: every symlinked
// component resolved, so two links to one file import one module
let rec private realPath (full: string) : string =
    match IO.Path.GetDirectoryName full with
    | null -> full
    | parent ->
        let here = IO.Path.Combine(realPath parent, IO.Path.GetFileName full)

        try
            match IO.FileInfo(here).ResolveLinkTarget true with
            | null -> here
            | target -> realPath target.FullName
        with _ ->
            here


// one `weir:` name, two vendored homes: a module under .weir/modules and a
// generated types module under .weir/types both named <name> — an error,
// never a silent precedence (the modules file would shadow the types one).
// A module's two forms, <name>.weir and <name>/, are the same error
// [D:add-module-closure], and so is a <name>/ the lock does not record
let private vendoredFault (importingAbsPath: string) (path: string) : string option =
    if path.StartsWith "weir:" then
        let name = path.Substring 5

        match Contracts.findWeirDir (IO.Path.GetDirectoryName importingAbsPath) with
        | Ok wd ->
            let m = IO.Path.GetFullPath(IO.Path.Combine(wd, "modules", name + ".weir"))
            let md = IO.Path.GetFullPath(IO.Path.Combine(wd, "modules", name))
            let t = IO.Path.GetFullPath(IO.Path.Combine(wd, "types", name + ".weir"))
            let hasDir = IO.Directory.Exists md

            if IO.File.Exists m && hasDir then
                Some
                    $"'{quoteData path}' names two vendored forms, {quoteData m} and {quoteData md} — a module is one file or one directory; re-add it (weir add module … --as {quoteData name}) to settle the form, or remove the stale one"
            elif (IO.File.Exists m || hasDir) && IO.File.Exists t then
                let m = if hasDir then md else m

                Some
                    $"'{quoteData path}' names two vendored files, {quoteData m} and {quoteData t} — a vendored module and a generated types module cannot share a name; regenerate the types under another name (weir gen types --schema <schema> --out .weir/types/<other>.weir) or re-add the module with another --as"
            elif hasDir then
                match vendoredDirEntry wd name with
                | Error e -> Some e
                | Ok _ -> None
            else
                None
        | Error _ -> None
    else
        None

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
// not an import-time effect. A module/init let refines it with
// Check.commandRunsNow (a lazy command seq passes, [D:lazy-command-lets]).
// The walk lives in Check beside the re-enumeration machinery that
// shares it [D:reenum-warning].
let runsCommandT: Check.TypedExpr -> bool = Check.runsCommandT

// a module let that calls a command-reaching helper of its own runs the
// command at import, as a direct command would [D:modules-v1]
let private helperAtImport (name: string) (h: string) =
    $"a module 'let' cannot run a command at import — '{h}' runs one, and this calls it now; wrap this in a function too (let {name} () = …), the command runs when a script calls it"

// a module let whose RHS runs a command now; a lazy command seq is a
// value and passes [D:lazy-command-lets]
let private commandAtImport (why: string) =
    $"a module 'let' cannot run a command at import: {why}"

/// the module's command-reaching functions and lazy command values so
/// far, in declaration order
let private moduleCommandHelpers (lets: (string * Check.TypedExpr) list) : Map<string, int> =
    lets |> List.fold (fun m (n, te) -> Check.recordCommandBinding m n te) Map.empty

// a source file's read, once into a result: an entry script or an import
// that is missing, a directory, unreadable or not a regular file (a device
// that never ends) is a weir-worded refusal, never a raw .NET exception
type SourceFault =
    | NotFound
    | IsDirectory
    | PermissionDenied
    | NotRegular
    | IoFault of string
    // the first invalid byte's 1-based line and column [D:source-utf8]
    | InvalidUtf8 of line: int * col: int

// a zero-length stat is a pipe, a procfs file or a device: read it with a
// ceiling, so /dev/zero refuses instead of filling memory
let private unsizedCap = 16 * 1024 * 1024

let private readSourceBytes (path: string) : byte[] =
    use fs = new IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)
    let sized = try fs.Length with _ -> 0L

    if sized > 0L then
        use ms = new IO.MemoryStream(int sized)
        fs.CopyTo ms
        ms.ToArray()
    else
        use ms = new IO.MemoryStream()
        let buf = Array.zeroCreate<byte> 65536
        let mutable n = fs.Read(buf, 0, buf.Length)

        while n > 0 do
            if ms.Length + int64 n > int64 unsizedCap then
                raise (IO.InvalidDataException "unsized")

            ms.Write(buf, 0, n)
            n <- fs.Read(buf, 0, buf.Length)

        ms.ToArray()

// a source file is UTF-8 [D:source-utf8]: strict decoding, so the first
// invalid byte is a located refusal, never a silent U+FFFD. One leading
// BOM is skipped; lines split as ReadAllLines splits (\r\n, \n, \r).
exception private BadUtf8 of line: int * col: int

let private strictUtf8 = Text.UTF8Encoding(false, true)

let private decodeSourceLines (bytes: byte[]) : string list =
    let start =
        if bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy then
            3
        else
            0

    let text =
        try
            strictUtf8.GetString(bytes, start, bytes.Length - start)
        with :? Text.DecoderFallbackException as ex ->
            // the bad byte's offset; its line is the newlines before it,
            // its column the code points before it on that line, plus one
            let at = start + max 0 ex.Index
            let lineStart = (Array.LastIndexOf(bytes, 10uy, max 0 (at - 1)) + 1) |> max start
            let line = 1 + (bytes[start .. at - 1] |> Array.sumBy (fun b -> if b = 10uy then 1 else 0))
            let prefix = Text.Encoding.UTF8.GetString(bytes, lineStart, at - lineStart)
            let col = 1 + (prefix.EnumerateRunes() |> Seq.length)
            raise (BadUtf8(line, col))

    use sr = new IO.StringReader(text)
    let acc = Collections.Generic.List<string>()
    let mutable line = sr.ReadLine()

    while not (isNull line) do
        acc.Add line
        line <- sr.ReadLine()

    List.ofSeq acc

let readSourceFile (path: string) : Result<string list, SourceFault> =
    if IO.Directory.Exists path then
        Error IsDirectory
    else
        try
            Ok(decodeSourceLines (readSourceBytes path))
        with
        | :? IO.FileNotFoundException
        | :? IO.DirectoryNotFoundException -> Error NotFound
        | :? UnauthorizedAccessException -> Error PermissionDenied
        | :? IO.InvalidDataException -> Error NotRegular
        | BadUtf8(l, c) -> Error(InvalidUtf8(l, c))
        | :? IO.IOException as ex -> Error(IoFault ex.Message)

/// the entry script's read, worded for the CLI: `weir: <message>`, exit 2
let readEntryScript (path: string) : Result<string list, string> =
    readSourceFile path
    |> Result.mapError (function
        | NotFound -> $"no such script: {quoteData path}"
        | IsDirectory -> $"{quoteData path} is a directory, not a script"
        | PermissionDenied -> $"cannot read {quoteData path}: permission denied"
        | NotRegular -> $"cannot read {quoteData path}: not a regular file — a script is a text file"
        | IoFault m -> $"cannot read {quoteData path}: {quoteData m}"
        | InvalidUtf8(l, c) -> $"{quoteData path}:{l}:{c}: invalid UTF-8 — a source file must be UTF-8; this byte is not")

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
    | IBadUtf8 of line: int * col: int

let private readImportSource (absPath: string) : ImportRead =
    match importSourceOverride.Value with
    | Some f ->
        match f absPath with
        | Some lines -> ISource lines
        | None -> IAbsent
    | None ->
        // present but unreadable — permission denied, a directory, a
        // device, an IO fault; name the path and the reason, located
        match readSourceFile absPath with
        | Ok lines -> ISource lines
        | Error NotFound -> IAbsent
        | Error IsDirectory -> IUnreadable "it is a directory, not a module"
        | Error PermissionDenied -> IUnreadable "permission denied"
        | Error NotRegular -> IUnreadable "not a regular file — a module is a text file"
        | Error(IoFault m) -> IUnreadable m
        | Error(InvalidUtf8(l, c)) -> IBadUtf8(l, c)

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
    (mkR: TypeEnv -> Parser.Resolver)
    (cache: System.Collections.Generic.Dictionary<string, LoadedModule>)
    (chain: string list)
    (importingAbsPath: string)
    (path: string)
    (importAs: string option)
    : Result<LoadedModule, ImportError> =
    // the path as the user reaches it (diagnostics, the LSP's locations)
    // and the identity [D:module-once] apart: on macOS /var is a link to
    // /private/var, and an editor must get back the path it opened
    let absPath = resolveImportPathRaw importingAbsPath path
    let key = realPath absPath

    // an import-statement error reports at the import line (File=None)
    let stmt msg =
        Error
            { File = None
              Line = 0
              Col = 0
              Message = msg }

    let notAModule =
        stmt $"{quoteData absPath} is not a module; add `module` at the top, or invoke it as a command"

    if
        path.StartsWith "weir:"
        && (let n = path.Substring 5 in n = "" || n.Contains "/" || n.Contains "\\" || n.Contains "..")
    then
        stmt "a weir: import names a vendored module, never a path — weir:<name>, no separators"
    elif (vendoredFault importingAbsPath path).IsSome then
        stmt (vendoredFault importingAbsPath path).Value
    elif key = realPath importingAbsPath then
        stmt "a file cannot import itself"
    elif List.contains key chain then
        // a cycle — same detector as self-import, different rendering
        // (decision 9): render the loop by file name
        let recent = chain |> List.takeWhile ((<>) key)

        let loop =
            (key :: (List.rev recent @ [ key ]))
            |> List.map IO.Path.GetFileName
            |> String.concat " → "

        stmt $"import cycle: {quoteData loop}"
    elif cache.ContainsKey key then
        // a diamond's shared module is checked once; re-alias per import site
        let cached = cache[key]

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
                $"no vendored module '{quoteData n}' ({quoteData absPath}) — vendor it: weir add module <host>/<org>/<repo>//<file>@<ref> --as {quoteData n} (a generated types module lands here via: weir gen types --schema {quoteData n})"
        else
            stmt $"cannot resolve import: no file at {quoteData absPath}"
    | IUnreadable reason -> stmt $"cannot read import {quoteData absPath}: {quoteData reason}"
    | IBadUtf8(l, c) ->
        Error
            { File = Some absPath
              Line = l
              Col = c
              Message = "invalid UTF-8 — a source file must be UTF-8; this byte is not" }
    | ISource rawLines ->
        let body, bodyOffset, _ = scriptBody rawLines

        let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)
        let assembled = numbered |> assemble // raw lines: assemble classifies/strips internally [D:content-bytes]

        match assembled with
        | Error msg -> stmt (assemblyErrorLocated absPath numbered msg)
        | Ok [] -> notAModule
        | Ok(first :: rest) ->
            let baseTenv, _ = moduleBaseEnvs absPath

            match Parser.parseLineFull (mkR baseTenv) first.Text with
            | Ok(SModule(declName, _)) ->
                let natural = declName |> Option.orElseWith (fun () -> deriveModuleName absPath)

                match importAs |> Option.orElse natural with
                | None ->
                    stmt
                        $"cannot derive a module name from '{quoteData (IO.Path.GetFileName absPath)}'; name it (module Name) or import it as a name (import \"…\" as Name)"
                | Some alias ->
                    // transitive: a reached module's own imports resolve, sharing
                    // the cache, with this module pushed on the chain
                    let childLoader: ImportLoader =
                        fun p _ a -> loadModuleCachedWith false mkR cache (key :: chain) absPath p a

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
                            match checkStatement true (Some sc) mkR childLoader tenv ll with
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
                                let helpers =
                                    accBody
                                    |> List.rev
                                    |> List.choose (function
                                        | _, CLet(n, t) -> Some(n, t)
                                        | _ -> None)
                                    |> moduleCommandHelpers

                                let eagerHelper =
                                    match chk.Kind with
                                    | KLet(_, _, te)
                                    | KLetPat(_, _, te) -> Check.eagerCommandHelper helpers te
                                    | _ -> None

                                let runsNow =
                                    match chk.Kind with
                                    | KLet(_, _, te)
                                    | KLetPat(_, _, te) -> Check.commandRunsNow helpers te
                                    | _ -> None

                                match chk.Kind with
                                | KType(decl, validator) -> go chk.Env sc implLines ((ll, CType(decl, validator)) :: accBody) tail
                                | KImport lm -> go chk.Env sc implLines ((ll, CImport lm) :: accBody) tail
                                | KSig(name, sd) ->
                                    // the sig binds nothing yet [D:module-signatures];
                                    // the paired implementation is the value
                                    let sc' =
                                        { sc with
                                            Pending = Map.add name sd sc.Pending
                                            Signed = Set.add name sc.Signed
                                            SigLines = Map.add name sd.Line sc.SigLines }

                                    go chk.Env sc' implLines accBody tail
                                | KLet _
                                | KLetPat _ when runsNow.IsSome -> at ll.Head 1 (commandAtImport runsNow.Value)
                                | KLet(name, _, _) when eagerHelper.IsSome -> at ll.Head 1 (helperAtImport name eagerHelper.Value)
                                | KLetPat _ when eagerHelper.IsSome -> at ll.Head 1 (helperAtImport "f" eagerHelper.Value)
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

                            // a module's own types (from its Body's decls); what it
                            // imported crosses only as reachableDefs below, never as
                            // its own [D:transitive-types]; Members already exclude
                            // imported ones (those live under Modules[·], not Values)
                            let typeDefs =
                                moduleBody
                                |> List.choose (function
                                    | _, CType(decl, _) ->
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

                            // own = declared in this body, a builtin's name
                            // included — exported, reachable qualified
                            // (`Doc.Text`); the bare name stays the builtin
                            // [D:module-builtin-names]
                            let declared =
                                moduleBody
                                |> List.collect (function
                                    | _, CLet(n, _) -> [ n ]
                                    | _, CLetPat(p, _) -> Parser.patLeafNames p
                                    | _ -> [])
                                |> Set.ofList
                                |> Set.union ctorNames

                            let ownMembers =
                                finalTenv.Values
                                |> Map.toList
                                |> List.filter (fun (n, _) -> declared.Contains n)

                            let members =
                                ownMembers
                                |> List.filter (fun (n, _) -> Set.contains n sc.Signed || Set.contains n ctorNames)

                            let privateMembers =
                                ownMembers
                                |> List.filter (fun (n, _) ->
                                    not (Set.contains n sc.Signed) && not (Set.contains n ctorNames))

                            // an imported type reachable from what this module
                            // exports (a signature, an own type's field or case)
                            // crosses with it, so an importer can read, show and
                            // compare the values it receives; it is not this
                            // module's type (TypeNames: no qualified literal, no
                            // re-export under this alias) [D:transitive-types]
                            let reachableDefs =
                                let own = typeDefs |> List.map fst |> Set.ofList

                                let rec walk (seen: Set<string>) (ty: Ty) =
                                    match ty with
                                    | TFun(a, b) -> walk (walk seen a) b
                                    | TSeq t -> walk seen t
                                    | TTuple ts -> ts |> List.fold walk seen
                                    | TRowVar(_, fields) -> fields |> List.map snd |> List.fold walk seen
                                    | TNamed(n, args) ->
                                        let seen = args |> List.fold walk seen

                                        if seen.Contains n || Map.containsKey n baseTenv.Types then
                                            seen
                                        else
                                            match Map.tryFind n finalTenv.Types with
                                            | Some def -> defTys def |> List.fold walk (Set.add n seen)
                                            | None -> seen
                                    | _ -> seen

                                and defTys (def: TypeDef) =
                                    match def with
                                    | Record rd -> rd.Fields |> List.map snd
                                    | Union u -> u.Cases |> List.choose snd

                                let roots =
                                    (members |> List.map (fun (_, sch) -> sch.Ty))
                                    @ (typeDefs |> List.collect (snd >> defTys))

                                roots
                                |> List.fold walk own
                                |> Set.filter (fun n -> not (own.Contains n))
                                |> Set.toList
                                |> List.choose (fun n -> Map.tryFind n finalTenv.Types |> Option.map (fun d -> n, d))

                            let loaded =
                                { Alias = alias
                                  NaturalName = natural |> Option.defaultValue alias
                                  AbsPath = absPath
                                  TypeDefs = typeDefs @ reachableDefs
                                  Members = members
                                  PrivateMembers = privateMembers
                                  MemberDocs =
                                      let byLine =
                                          docAttachments rawLines |> List.map (fun d -> d.Line, d.Doc) |> Map.ofList

                                      sc.SigLines
                                      |> Map.toList
                                      |> List.choose (fun (n, l) -> Map.tryFind l byLine |> Option.map (fun d -> n, d))
                                      |> Map.ofList
                                  TypeNames = typeDefs |> List.map fst
                                  TypeOrigins =
                                      let ownOrigin n =
                                          $"{natural |> Option.defaultValue alias}.{n}@{absPath}"

                                      (typeDefs |> List.map (fun (n, _) -> n, ownOrigin n))
                                      @ (reachableDefs
                                         |> List.map (fun (n, _) ->
                                             n, Map.tryFind n finalTenv.TypeOrigins |> Option.defaultValue (ownOrigin n)))
                                  Body = moduleBody
                                  MayRebind = false }

                            cache[key] <- loaded
                            Ok loaded
            | Ok _ -> notAModule
            | Error _ -> notAModule

// the module-body replayer [D:modules-v1]: evaluate a checked module's Body
// in its own clean env (Self.scriptPath is the module's; process facts ride
// from the entry), exposing a nested import's members as `alias.member` for
// this module's own lets. Returns the module's venv (bindings under bare
// names); the caller exposes this module's Members qualified.
//
// A module's body runs once per program [D:module-once]: keyed by its
// identity, and by the checked Body itself — a reloaded module (the REPL
// re-importing an edited file) is a new body and runs afresh.
let private replayed =
    System.Collections.Generic.Dictionary<string, (LogicalLine * CheckedStmt) list * Eval.Env>()

let rec replayModule (procFacts: (string * Eval.Value) list) (lm: LoadedModule) : Eval.Env =
    let key = realPath lm.AbsPath

    match replayed.TryGetValue key with
    | true, (body, venv) when obj.ReferenceEquals(body, lm.Body) -> venv
    | _ ->
        Session.enterImportGuard lm.NaturalName

        let venv =
            try
                replayModuleBody procFacts lm
            finally
                Session.exitImportGuard ()

        replayed[key] <- (lm.Body, venv)
        venv

and private replayModuleBody (procFacts: (string * Eval.Value) list) (lm: LoadedModule) : Eval.Env =
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
        | (_, CType(decl, validator)) :: t -> replay (Eval.declare mv decl validator) t
        | (_, CLet(name, te)) :: t -> replay (Map.add name (Eval.eval mv te) mv) t
        | (_, CLetPat(pat, te)) :: t ->
            let bs = Eval.bindPattern pat (Eval.eval mv te)
            replay (bs |> List.fold (fun m (n, v) -> Map.add n v m) mv) t
        | (_, CImport nested) :: t ->
            let nestedVenv = replayModule procFacts nested
            replay (expose nested.Alias nested.Members nestedVenv mv) t
        | _ :: t -> replay mv t

    replay mBase lm.Body

// a session import's values [D:repl-import]: replay the module with the
// session's process facts and bind `Alias.member`, as the runner does
let importInto (venv: Eval.Env) (lm: LoadedModule) : Eval.Env =
    let procFacts =
        [ "Self.pid"; "Self.args"; "Self.stdin"; "Self.entryPath" ]
        |> List.choose (fun k -> Map.tryFind k venv |> Option.map (fun v -> k, v))

    let moduleVenv = replayModule procFacts lm

    lm.Members
    |> List.fold
        (fun acc (n, _) ->
            match Map.tryFind n moduleVenv with
            | Some v -> Map.add $"{lm.Alias}.{n}" v acc
            | None -> acc)
        venv

/// add-time validation for a single fetched module file [D:add-module]:
/// the same loader imports use — is-a-module, the module purity rules, a
/// full typecheck — with imports refused first: a single file vendors as
/// a leaf (a file with imports vendors its closure, [D:add-module-closure]).
/// Returns the member count for the add's report line.
let checkVendoredModule (absPath: string) : Result<int, string> =
    match readImportSource absPath with
    | IAbsent -> Error $"cannot read {absPath}"
    | IUnreadable reason -> Error $"cannot read {absPath}: {reason}"
    | IBadUtf8(l, c) -> Error $"{absPath}:{l}:{c}: invalid UTF-8 — a source file must be UTF-8; this byte is not"
    | ISource raws ->
        if raws |> List.exists (fun l -> l.StartsWith "import ") then
            Error
                "the module imports — a single file vendors as a leaf; vendor a module with imports by its repo path, <host>/<org>/<repo>//<file>@<ref>, so its imports are fetched with it"
        else
            let cache = Collections.Generic.Dictionary()
            // a phantom importer in the same directory: never equal to
            // absPath, so the self-import guard stays quiet
            let phantom = absPath + ".add"

            match loadModuleCachedWith false resolver cache [] phantom absPath None with
            | Ok lm -> Ok(List.length lm.Members)
            | Error e ->
                let where =
                    match e.File with
                    | Some f -> $"{quoteData f}:{e.Line}:{e.Col}: "
                    | None -> ""

                Error $"{where}{e.Message}"

// ---- vendoring a module's closure [D:add-module-closure] ------------------

/// the import statements of a module source, located (line, col, path),
/// read by the real assembler and parser; a source that does not decode
/// or assemble yields none here, and validation reports it located
let importsOfSource (bytes: byte[]) : (int * int * string) list =
    try
        let body, off, _ = scriptBody (decodeSourceLines bytes)
        let numbered = body |> List.mapi (fun i l -> off + i + 1, l)

        match assemble numbered with
        | Error _ -> []
        | Ok lls ->
            let r = resolver (fst (moduleBaseEnvs "/vendored.weir"))

            lls
            |> List.choose (fun ll ->
                if ll.Text.TrimStart().StartsWith "import" then
                    match Parser.parseLineFull r ll.Text with
                    | Ok(SImport(p, span, _)) ->
                        let l, c = translate ll span.Start.Col
                        Some(l, c, p)
                    | _ -> None
                else
                    None)
    with _ ->
        []

let private repoSegments (p: string) = p.Split '/' |> List.ofArray

// '.'/'..'/empty segments folded; None when `..` climbs past the root
let private normRepoPath (segs: string list) : string list option =
    (Some [], segs)
    ||> List.fold (fun acc seg ->
        match acc, seg with
        | None, _ -> None
        | Some st, ("" | ".") -> Some st
        | Some [], ".." -> None
        | Some(_ :: t), ".." -> Some t
        | Some st, s -> Some(s :: st))
    |> Option.map List.rev

/// walk a module's imports from its repo path, fetching each file at the
/// same commit: relative imports (every spelling that resolves relative
/// today) join the closure; a `weir:` import, an absolute path and a path
/// escaping the repo root refuse, located at file:line:col. A file is
/// fetched once (a cycle is the loader's error at validation). Returns
/// (repo path, bytes) in discovery order, the entry first.
let walkVendorClosure (fetch: string -> Result<byte[], string>) (entry: string) : Result<(string * byte[]) list, string> =
    match normRepoPath (repoSegments entry) with
    | None
    | Some [] -> Error $"'{quoteData entry}' is not a file inside the repo"
    | Some entrySegs ->
        let entryPath = String.concat "/" entrySegs
        let seen = Collections.Generic.HashSet<string>([ entryPath ])

        let rec go (acc: (string * byte[]) list) (queue: (string * string option) list) =
            match queue with
            | [] -> Ok(List.rev acc)
            | (file, from) :: rest ->
                match fetch file with
                | Error e ->
                    match from with
                    | Some at -> Error $"{quoteData file} (imported at {quoteData at}): {e}"
                    | None -> Error e
                | Ok bytes ->
                    let dirSegs = repoSegments file |> List.take (List.length (repoSegments file) - 1)

                    let rec classify (found: (string * string) list) imports =
                        match imports with
                        | [] -> Ok(List.rev found)
                        | (l, c, p: string) :: t ->
                            let at = $"{file}:{l}:{c}"

                            if p.StartsWith "weir:" then
                                Error
                                    $"{quoteData at}: imports '{quoteData p}' — a weir: import inside a vendored module is not vendored with it (cross-remote vendoring is a current boundary, not a refusal of the idea): inline the dependency, or vendor both and import each from your own code"
                            elif
                                p.StartsWith "/"
                                || p.StartsWith "\\"
                                || (p.Length >= 2 && Char.IsLetter p[0] && p[1] = ':')
                            then
                                Error
                                    $"{quoteData at}: imports the absolute path '{quoteData p}' — a vendored module reaches only files of its own repo, by relative path"
                            else
                                match normRepoPath (dirSegs @ repoSegments p) with
                                | None
                                | Some [] ->
                                    Error
                                        $"{quoteData at}: imports '{quoteData p}', which resolves outside the repo root — a vendored module reaches only files inside its repo"
                                | Some segs -> classify ((String.concat "/" segs, at) :: found) t

                    match classify [] (importsOfSource bytes) with
                    | Error e -> Error e
                    | Ok targets ->
                        let fresh = targets |> List.filter (fun (t, _) -> seen.Add t)
                        go ((file, bytes) :: acc) (rest @ (fresh |> List.map (fun (t, at) -> t, Some at)))

        go [] [ entryPath, None ]

/// the closure's layout [D:add-module-closure]: the common ancestor
/// directory of every file ("" or "a/b/"), so files keep their relative
/// positions under modules/<name>/ and imports need no rewriting
let closureAncestor (paths: string list) : string =
    let dirs =
        paths
        |> List.map (fun p ->
            let segs = repoSegments p
            List.take (List.length segs - 1) segs)

    let rec common (a: string list) (b: string list) =
        match a, b with
        | x :: xs, y :: ys when x = y -> x :: common xs ys
        | _ -> []

    match dirs with
    | [] -> ""
    | d :: ds ->
        match List.fold common d ds with
        | [] -> ""
        | segs -> String.concat "/" segs + "/"

/// add-time validation for a closure written under `dir` [D:add-module-closure]:
/// the entry loads through the normal loader, which checks every file it
/// reaches transitively with the module purity rules (a cycle is its
/// cycle error). Returns the entry's member count.
let checkVendoredClosure (dir: string) (entryRel: string) : Result<int, string> =
    let entryAbs = IO.Path.GetFullPath(IO.Path.Combine(dir, entryRel))
    let cache = Collections.Generic.Dictionary()

    match loadModuleCachedWith false resolver cache [] (entryAbs + ".add") entryAbs None with
    | Ok lm -> Ok(List.length lm.Members)
    | Error e ->
        let where =
            match e.File with
            | Some f -> $"{quoteData f}:{e.Line}:{e.Col}: "
            | None -> ""

        Error $"{where}{e.Message}"

// where a file's imports resolve from: its real location, through a
// symlink [D:init-import] — a linked dotfile or ~/bin script finds its
// neighbours. Self.scriptPath keeps the link [D:script-path]
let importAnchor (path: string) : string =
    let full = IO.Path.GetFullPath path

    try
        match IO.FileInfo(full).ResolveLinkTarget true with
        | null -> full
        | target -> target.FullName
    with _ ->
        full

// a file's import loader [D:modules-v1]: imports resolve against the
// importing file; one cache per loader dedups a diamond, the chain catches
// a cycle. The REPL init file uses it too [D:init-import]
// mkR is the importer's own resolver: a check reads an imported module
// under assumeResolver exactly as it reads the importing file, the runner
// under the hard one [D:assume-resolver]
let fileImportLoader (mkR: TypeEnv -> Parser.Resolver) (importingAbsPath: string) : ImportLoader =
    let cache = System.Collections.Generic.Dictionary<string, LoadedModule>()
    fun p _ alias -> loadModuleCachedWith false mkR cache [ importingAbsPath ] importingAbsPath p alias

// the session import loader [D:repl-import]: the prompt and -e resolve an
// import against the session cwd at the moment it runs — a phantom
// importer in that directory (never a file, so the self-import guard stays
// quiet). A fresh cache per import, so a re-import re-reads the file
let sessionImportLoader: ImportLoader =
    fun p _ alias ->
        let phantom = IO.Path.Combine(Session.Cwd(), "(session)")
        let cache = System.Collections.Generic.Dictionary<string, LoadedModule>()
        loadModuleCachedWith false resolver cache [] phantom p alias

// the no-import loader: a form with no place for an import statement (an
// #infer source, a completion repair, a test harness) refuses it
let scriptOnlyImport: ImportLoader =
    fun _ _ _ ->
        Error
            { File = None
              Line = 0
              Col = 0
              Message = "import is a statement of its own — not available here" }

// ---- completion in context [D:entry-complete] ---------------------------
// The REPL's multi-line entry and the LSP's document share one repair:
// the cursor's whole logical statement (every physical line of it up to
// the cursor) is repaired and typed [D:repair-completion], so a binder
// on an earlier line of the statement — a for/fun/let/match binder —
// types the head, and an earlier statement's bindings join the env.
// Checked, never run (the never-executes law).
let private completeParam = "weirCompleteParam"
let private completeHole = "weirCompleteHole"

/// `head.prefix` fields for the word ending `pre` (the statement text up
/// to the cursor; `post` the rest of it, "" when cut at the cursor).
/// A lowercase head the env does not bind repairs to a blank filler; the
/// `_` shorthand repairs to a lambda whose param is the element — a seq
/// flowing in unwraps to its element, the pipeline-elem rule — except
/// in a bare `|> _.` stage, where a seq never type-checks and offers
/// nothing. None when the word is no such head or no repair types to a
/// record.
let repairedFields (env: TypeEnv) (pre: string) (post: string) : string list option =
    let ws = Complete.wordStartAt pre pre.Length
    let word = pre.Substring ws
    let dot = word.IndexOf '.'

    if dot <= 0 || word.IndexOf('.', dot + 1) >= 0 then
        None
    else
        let head = word.Substring(0, dot)
        let prefix = word.Substring(dot + 1)
        let shorthand = head = "_"

        if not shorthand && not (Char.IsLower head[0] && not (Map.containsKey head env.Values)) then
            None
        else
            let before = pre.Substring(0, ws)
            let parse t = Parser.parseLine (assumeResolver env) t

            // the neutral `""` first (a bare row-typed head left behind
            // breaks positions with scalar rules — printerr); a hole
            // (a fresh type var [D:hole-completion]) where a string
            // cannot stand (a predicate's bool)
            let fillers =
                if shorthand then
                    [ $"(fun {completeParam} -> {completeHole})" ]
                else
                    [ "\"\"" + String(' ', max 0 (word.Length - 2)); completeHole ]

            let target = if shorthand then completeParam else head
            let unwrap = shorthand && not (Complete.bareStageBefore before)

            // two repair candidates per filler: close dangling delimiters
            // at the cursor (mid-statement edits — the suffix stays
            // outside the string), else at the end (last-line edits)
            fillers
            |> List.collect (fun filler ->
                let prefixDone = before + filler
                let blanked = before + filler + post
                [ prefixDone + closers prefixDone + post; blanked + closers blanked ])
            |> List.tryPick (fun cand ->
                Complete.tyAtRepaired parse env cand target
                |> Option.map (fun ty ->
                    match ty with
                    | TSeq elem when unwrap -> elem
                    | t -> t)
                |> Option.bind (Complete.fieldsOfTy env))
            |> Option.map (fun fields ->
                fields
                |> List.filter (fun f -> f.StartsWith prefix)
                |> List.distinct
                |> List.sort
                |> List.map (fun f -> head + "." + f))

/// a multi-line entry's context at the cursor: `lines` is the entry up
/// to the cursor (its last line cut there). The statements before the
/// cursor's extend `env` (checked, never run; one that fails to check is
/// skipped); returns that env and the cursor statement's text, None when
/// the entry does not assemble (the caller falls back to the line)
let entryContext (env: TypeEnv) (lines: string list) : TypeEnv * string option =
    let numbered =
        lines
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> classifyLine raw <> LineKind.CommentOnly)
        |> List.map (fun (n, raw) -> n, stripComment raw)

    // a statement still open at the cursor (a lambda's `(` across lines)
    // refuses to assemble: close it at the cursor, then cut the closers
    // back off the statement's text
    let assembled =
        match assemble numbered with
        | Ok lls -> Some(lls, "")
        | Error _ ->
            let suffix = closers (numbered |> List.map snd |> String.concat "\n")

            match List.rev numbered with
            | (n, last) :: earlierRev when suffix <> "" ->
                match assemble (List.rev ((n, last + suffix) :: earlierRev)) with
                | Ok lls -> Some(lls, suffix)
                | Error _ -> None
            | _ -> None

    match assembled with
    | None -> env, None
    | Some(lls, suffix) ->
        match List.rev lls with
        | last :: earlier when
            last.Segments |> List.exists (fun (_, phys, _) -> phys = lines.Length)
            && last.Text.EndsWith suffix
            ->
            let env' =
                earlier
                |> List.rev
                |> List.fold
                    (fun te ll ->
                        match checkStatement false None assumeResolver scriptOnlyImport te ll with
                        // an anonymous record it minted completes at once
                        // [D:anon-literals], as between REPL statements
                        | Ok chk -> Check.withPendingAnonDefs chk.Env
                        | Error _ -> te)
                    env

            env', Some(last.Text.Substring(0, last.Text.Length - suffix.Length))
        | _ -> env, None


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
        "lowercase-binding"
    elif msg.Contains "discards it" then
        "discard"
    elif msg.StartsWith "a sequenced expression must be unit" then
        "non-unit-statement"
    elif msg.StartsWith "this pattern can fail" then
        "refutable-binder"
    elif msg.StartsWith "match is not exhaustive" || msg.Contains "needs a catch-all" then
        "non-exhaustive"
    elif msg.Contains "unreachable" then
        "unreachable-arm"
    elif msg.StartsWith "invalid regex" then
        "regex"
    elif msg.StartsWith "this regex has" then
        "regex-binder-count"
    elif msg.StartsWith "cannot sort by this key" then
        "unsortable-key"
    elif msg.Contains "equatable" || msg.Contains "cannot be compared" then
        "not-equatable"
    elif msg.Contains "cannot render functions" then
        "unrenderable-function"
    elif msg.StartsWith "unbound variable" then
        "unbound"
    elif msg.Contains "nothing determines" then
        "undetermined-type"
    else
        "check"

// AOT-safe JSON writing: Utf8JsonWriter (reflection-free, the write
// twin of the JsonDocument reader) — escaping is the library's job,
// never string interpolation's; the encoder is Types.RawBmpEncoder
// [D:json-relaxed-escaping]
let jsonBuild (build: System.Text.Json.Utf8JsonWriter -> unit) : string =
    use ms = new IO.MemoryStream()
    use w = new System.Text.Json.Utf8JsonWriter(ms, Types.rawJsonWriterOptions)
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
type SigSurface =
    { Longs: Map<string, SigArity>
      Shorts: Map<string, SigArity>
      Literals: Map<string, SigArity>
      // what a value-taking flag's value completes to, keyed by spelling
      // as typed (`--file`, `-F`, `-name`); absent = nothing
      // [D:sig-arg-complete]
      Values: Map<string, SigValue> }

and SigValue =
    | VPath
    | VChoices of string list

let emptySigSurface =
    { Longs = Map.empty
      Shorts = Map.empty
      Literals = Map.empty
      Values = Map.empty }

// a loaded signature's checkable surface. Subs: kebab-cased subcommand
// -> (long-flag set, short set), the globals folded in (what completion
// offers); the "" key is the flag-only shape (Cmd declared as a record).
// Surfaces carries the same cases with arity, Globals the `type Globals`
// record checked at every level, and the module lets the tool's
// convention [D:sig-conventions].
type SigInfo =
    { Tool: string
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
      Dynamic: string }

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
                    { acc with
                        Longs = Map.add (Weir.Argv.kebabFlag f) a acc.Longs }

            let acc =
                wires
                |> List.fold
                    (fun (acc: SigSurface) (w: string) ->
                        if w.StartsWith "--" then
                            { acc with
                                Longs = Map.add (w.Substring 2) a acc.Longs }
                        elif w.StartsWith "-" && w.Length = 2 then
                            { acc with
                                Shorts = Map.add (w.Substring 1) a acc.Shorts }
                        elif w.StartsWith "-" then
                            { acc with
                                Literals = Map.add w a acc.Literals }
                        else
                            { acc with Longs = Map.add w a acc.Longs })
                    acc

            let acc =
                match shortOf f with
                | Some sh -> { acc with Shorts = Map.add sh a acc.Shorts }
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

                { acc with
                    Values = spelled |> List.fold (fun m k -> Map.add k v m) acc.Values })
        emptySigSurface

// later spellings never displace earlier ones: a case's own flag keeps
// its arity over a global of the same name
let private mergeSurface (a: SigSurface) (b: SigSurface) : SigSurface =
    let into (m: Map<string, SigArity>) (n: Map<string, SigArity>) =
        n |> Map.fold (fun acc k v -> if Map.containsKey k acc then acc else Map.add k v acc) m

    { Longs = into a.Longs b.Longs
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

                if Map.containsKey k acc || ownedByA then acc else Map.add k v acc)
            a.Values }

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
let private legacySigFileName (tool: string) : string =
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

                    if entries |> List.exists (fun e -> e.Kind = "sig" && e.Name = decl.Tool && e.Path.Replace('\\', '/') = rel) then
                        None
                    else
                        entries
                        |> List.tryFind (fun e -> e.Kind = "sig" && e.Name <> decl.Tool && e.Path.Replace('\\', '/') = rel)
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
let private fieldDocsFor (rawLines: string list) (ll: LogicalLine) : Map<string, string> =
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
let private typeDocFor (rawLines: string list) (ll: LogicalLine) : Map<string, string> =
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

                diags.Add(mk decl.Line $"#sig {quoteData decl.Tool}: no {quoteData sigFile} (searched from {quoteData scriptDir}) — {hint}")
            | Ok sigFile ->
                let cache = System.Collections.Generic.Dictionary<string, LoadedModule>()
                let absScript = IO.Path.GetFullPath path

                match loadModuleCachedWith true resolver cache [ absScript ] absScript sigFile None with
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
                                    if m.Success then Some(m.Groups[1].Value, fieldDocsFor raw ll) else None)
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

                        { Tool = decl.Tool
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
                          Dynamic = dynamic }

                    match lm.TypeDefs |> List.tryFind (fun (n, _) -> n = "Cmd") with
                    | None ->
                        diags.Add(
                            mk
                                decl.Line
                                $"#sig {quoteData decl.Tool}: the signature declares no type `Cmd` — the command surface is the type named Cmd (a union of subcommands, or a record of flags)"
                        )
                    | Some(_, Record rd) ->
                        infos.Add(info (Map [ "", sigSurface (recordOf "Cmd" |> Option.defaultValue rd) ]) (Map [ "", "Cmd" ]))
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
                pick c0 |> Map.filter (fun k _ -> rest |> List.forall (fun c -> Map.containsKey k (pick c)))

            let longs = inter (fun c -> c.Longs)

            if longs.IsEmpty then
                None
            else
                Some
                    { Longs = longs
                      Shorts = inter (fun c -> c.Shorts)
                      Literals = inter (fun c -> c.Literals)
                      Values = inter (fun c -> c.Values) }

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
                    let toolAndSub (typed: string list) = quoteData (String.concat " " (si.Tool :: typed))

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

                        { File = path
                          Line = l1
                          Col = c1
                          EndLine = Some l2
                          EndCol = Some(c2 + 1)
                          Severity = sev
                          Code = "sig"
                          Message = msg }

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
                        Seq.concat
                            [ sf.Longs |> Map.keys |> Seq.map (fun l -> "--" + l)
                              sf.Shorts |> Map.keys |> Seq.map (fun s -> "-" + s)
                              sf.Literals |> Map.keys |> Seq.map id ]

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
                        mk sp $"unknown flag '{quoteData shown}' for {toolAndSub typed}{dym shown (spellings sf)} {note}"

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
                                [ Map.tryFind bare sf.Longs
                                  Map.tryFind bare sf.Shorts
                                  Map.tryFind ("-" + bare) sf.Literals
                                  Map.tryFind ("--" + bare) sf.Literals ]
                                |> List.tryPick id

                            match hit with
                            | Some a -> [], after a attached
                            | None when name.StartsWith "---" -> [ unknown sf typed name sp ], 0
                            | None ->
                                let goSpellings =
                                    spellings sf |> Seq.map (fun s -> (if name.StartsWith "--" then "--" else "-") + s.TrimStart '-')

                                [ mk sp $"unknown flag '{quoteData name}' for {toolAndSub typed}{dym name goSpellings} {note}" ], 0
                        | "find" ->
                            // whole words: a predicate (`-name`), a listed
                            // `--help`; a one-letter option may carry its
                            // value attached (`-O3`)
                            let exact =
                                [ Map.tryFind name sf.Literals
                                  (if name.StartsWith "--" then
                                       Map.tryFind (name.Substring 2) sf.Longs
                                   elif name.Length = 2 then
                                       Map.tryFind (name.Substring 1) sf.Shorts
                                   else
                                       None) ]
                                |> List.tryPick id

                            let glued () =
                                Seq.append
                                    (sf.Shorts |> Map.toSeq |> Seq.map (fun (k, a) -> "-" + k, a))
                                    (sf.Literals |> Map.toSeq |> Seq.filter (fun (k, _) -> k.Length = 2))
                                |> Seq.tryFind (fun (k, a) -> (a = AOne || a = AOpt) && w.StartsWith k && w.Length > 2)

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
                                        match sf.Longs |> Map.toList |> List.filter (fun (k, _) -> k.StartsWith long) with
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
                                | None when si.DashWords && w.Length > 2 && not (sf.Shorts.ContainsKey(string w[1])) -> [], 0
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

                                                [ mk sp $"unknown flag '-{quoteData (string c)}'{inWord} for {toolAndSub typed} {note}" ], 0
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
                            if Map.containsKey k si.Surfaces then Some(n, k, toks) else None)

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
                                | Some(n, k, toks) when not (Set.contains k si.Unchecked) -> reach k (typed @ toks) (List.skip n ws)
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
                                                | (Some v, _) :: tail when not (isFlag v) -> deeper tail |> Option.map (fun r -> r, true)
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
    set
        [ "sudo"
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
          "unbuffer" ]

/// what weir resolves as a program in head position: a PATH lookup and
/// the builtin command heads (the REPL adds its alias table)
let resolvesAsProgram (w: string) : bool =
    Builtins.commandCallable.Contains w || Extern.exists w

/// one hit: the continuation word's physical (line, col, endCol) and the
/// message
type ContinuedCommand =
    { CLine: int
      CCol: int
      CEndCol: int
      CMessage: string }

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

            if first = '"' || first = '\'' || first = '@' || first = '$' then None
            elif w.StartsWith "^" && w.Length > 1 then Some w
            elif w.StartsWith "-" || w.Contains '/' then None
            elif resolves w then Some w
            else None
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

                    { CLine = line
                      CCol = col
                      CEndCol = col + w.Length
                      CMessage =
                        $"this indented line continues `{shown}` — its words become its arguments, and `{word}` looks like a second command: dedent the line to run it on its own, or quote the word if it really is an argument" }))

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
    |> List.map (fun h ->
        { File = path
          Line = h.CLine
          Col = h.CCol
          EndLine = Some h.CLine
          EndCol = Some h.CEndCol
          Severity = "warning"
          Code = "continued-command"
          Message = h.CMessage })

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
                if c = quote then quote <- ' '
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
        let long k = Map.tryFind k sf.Longs |> Option.map (fun a -> a, "--" + k)
        let short k = Map.tryFind k sf.Shorts |> Option.map (fun a -> a, "-" + k)
        let literal k = Map.tryFind k sf.Literals |> Option.map (fun a -> a, k)

        match si.Convention with
        | "go" ->
            let bare = name.TrimStart '-'

            [ long bare; short bare; literal ("-" + bare); literal ("--" + bare) ]
            |> List.tryPick id
        | "find" ->
            literal name
            |> Option.orElse (if name.StartsWith "--" then long (name.Substring 2) else None)
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
                let grouped = keys |> List.filter (fun o -> o.StartsWith(prefix + cand + "-")) |> List.length

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

                    if run.Length = ws.Length && si.Surfaces |> Map.exists (fun o _ -> o.StartsWith(k + "-")) then
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
                    |> Option.bind (fun sf -> flagArity si sf name |> Option.map (fun (a, k) -> a, Map.tryFind k sf.Values))
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
        && w |> Seq.forall (fun c -> not (Char.IsWhiteSpace c) && not (Char.IsControl c) && c <> '"' && c <> '\'' && c <> '`')

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
                        loadSigs
                            (path + ".from")
                            [ { Tool = tool
                                Override = Some path
                                Line = 1 } ]
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
                resolveSigFile
                    cwd
                    { Tool = tool
                      Override = None
                      Line = 1 }
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
        w
        |> Seq.forall (fun c -> not (Char.IsControl c || "\"'`$()\\".IndexOf c >= 0))

    let offer (outcome: DynComplete.Outcome) (valuesOnly: bool) =
        match outcome with
        | DynComplete.Interrupted -> Some []
        | DynComplete.Fallback -> None
        | DynComplete.Answer(cands, noFiles) ->
            // cobra answers `--flag=` with the values alone
            let lead = if valuesOnly then cur.Substring(0, cur.IndexOf '=' + 1) else ""
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

    if slot = DynStatic || not (cur.EndsWith word) || not (List.forall plain (cur :: before)) then
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
                    (hooks.Run
                        { Mechanism = "git"
                          Exe = "git"
                          Args = DynComplete.gitArgs want })
                    false
            | _ -> None
        | DynCobra ->
            offer
                (hooks.Run
                    { Mechanism = "cobra"
                      Exe = tool
                      Args = before @ [ cur ] })
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
        (before = "" || Char.IsWhiteSpace before[before.Length - 1] || before[before.Length - 1] = '=')
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
                    exe, (if prefix.IsEmpty then argText else " " + String.concat " " prefix + argText)
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

    match stmt |> Option.bind (fun s -> repairedFields ctxEnv s "") with
    | Some(_ :: _ as fields) -> fields
    | _ ->
        // a command argv word under a sig'd head [D:sig-arg-complete]
        match sigArgvCompleteWith hooks (SigLookup.userDir ()) aliases ctxEnv text wordStart with
        | Some offers -> offers
        | None -> Complete.suggestSession (aliases |> Map.keys |> Set.ofSeq) values ctxEnv text wordStart

let suggestEntry aliases values env entry text wordStart =
    suggestEntryWith None aliases values env entry text wordStart

// ---- `weir add sig <tool>`: generation [D:command-signatures] --------------
// Sources in fidelity order [D:sig-sources], the chosen one recorded in the
// provenance comment and the lock's Url slot ("generated:<source>"): a
// cobra tool's `__complete` endpoint, git's own command list and
// completion helper, a completion endpoint or shipped fish file, a
// full-help switch the help names (`--help all`, `--dump-options`,
// `--help-all`, `--v8-options`), then `--help` rows (and `-h`/`-help`
// when `--help` refuses). The help pages always ride along: they carry
// the arities and the subcommand tables. Subcommands are walked from the
// tables only — a word the tool did not list as a command is never run.
// The generated file is validated (loads as a signature) before anything
// persists.
module SigGen =
    // the probes whose output passed the byte bound, for the generated
    // file's note [D:sig-probe-bounds]
    let private cutProbes = System.Collections.Concurrent.ConcurrentQueue<string>()

    // the probes stopped at the time bound: what they printed is read, and
    // the file and the added line say so [D:sig-probe-bounds]
    let private slowProbes = System.Collections.Concurrent.ConcurrentQueue<string>()

    // one run per argv and cwd in a generation: git's builtins ask the
    // same helper and `-h` from two places, the refused-help harvest asks
    // `--help` again
    let private probed =
        System.Collections.Concurrent.ConcurrentDictionary<string option * string * string list, Lazy<Contracts.ProbeOutput option>>()

    let private runIn (cwd: string option) (anyExit: bool) (tool: string) (args: string list) : string option =
        // the bounded probe [D:sig-probe-bounds]: empty stdin (a
        // stdin-reader must not hang generation), a fresh temp cwd, a
        // time and a byte bound, the probe's process group killed
        let run () =
            match (try Contracts.runProbe (Proc.resolveProg tool) args cwd with _ -> None) with
            | Some r as res ->
                if r.Cut then
                    cutProbes.Enqueue(String.concat " " args)
                elif r.Exit.IsNone then
                    slowProbes.Enqueue(String.concat " " args)

                res
            | None -> None

        let once = probed.GetOrAdd((cwd, tool, args), (fun _ -> lazy (run ())))

        match once.Force() with
        | Some r when r.Exit = Some 0 || r.Cut || r.Exit.IsNone || (anyExit && r.Exit.IsSome) ->
            match Contracts.stripAnsi (if r.Out.Trim() <> "" then r.Out else r.Err) with
            | blank when blank.Trim() = "" -> None
            | text -> Some(text.Replace("\r", ""))
        | _ -> None

    let private runTool = runIn None false

    // BSD grep's --help exits 2 with the usage on stderr — a nonzero
    // exit's dump is harvest-only material [D:sig-version-probe]:
    // never structured rows, never the walk, never a bare-word gate
    let private runToolAnyExit = runIn None true

    // one discovered flag: its spellings (a `--long`, one-letter shorts,
    // whole dash words), the value it takes when known, a doc
    type private Flag =
        { Long: string
          Shorts: string list
          Literals: string list
          Arity: SigArity option
          Doc: string option
          // the value is a boolean (`--legend=BOOL`): `--no-x` is the
          // tool's too [D:sig-help-shapes]
          BoolValue: bool }

    let private noFlag =
        { Long = ""
          Shorts = []
          Literals = []
          Arity = None
          Doc = None
          BoolValue = false }

    let private legalLong (l: string) =
        l.Length > 0
        && System.Char.IsLetterOrDigit l[0]
        && l |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '-' || c = '_')

    let private legalShort (s: string) =
        s.Length = 1 && (System.Char.IsLetterOrDigit s[0] || s = "?" || s = "#" || s = "@")

    let private keyOf (f: Flag) =
        if f.Long <> "" then "--" + f.Long
        else
            match f.Shorts, f.Literals with
            | s :: _, _ -> "-" + s
            | [], l :: _ -> l
            | [], [] -> ""

    // one flag per spelling key, first source wins on arity and doc;
    // spellings union
    let private mergeFlags (lists: Flag list list) : Flag list =
        let byKey = System.Collections.Generic.Dictionary<string, Flag>()
        let order = ResizeArray<string>()
        // a short or literal already owned by a long never starts its own flag
        let owner = System.Collections.Generic.Dictionary<string, string>()

        for f in List.concat lists do
            let k0 = keyOf f

            let k =
                if f.Long = "" then
                    match owner.TryGetValue k0 with
                    | true, o -> o
                    | _ -> k0
                else
                    k0

            if k <> "" then
                match byKey.TryGetValue k with
                | true, g ->
                    byKey[k] <-
                        { g with
                            Shorts = g.Shorts @ (f.Shorts |> List.filter (fun s -> not (List.contains s g.Shorts)))
                            Literals = g.Literals @ (f.Literals |> List.filter (fun s -> not (List.contains s g.Literals)))
                            Arity = g.Arity |> Option.orElse f.Arity
                            Doc = g.Doc |> Option.orElse f.Doc
                            BoolValue = g.BoolValue || f.BoolValue }
                | _ ->
                    byKey[k] <- f
                    order.Add k

                let g = byKey[k]

                for s in g.Shorts do
                    owner["-" + s] <- k

                for l in g.Literals do
                    owner[l] <- k

        // a short-only entry recorded before its long arrived folds in
        [ for k in order do
              let f = byKey[k]

              if f.Long <> "" || not (owner.ContainsKey k && owner[k] <> k) then
                  yield f ]

    // ---- the parsers ----------------------------------------------------

    let private allCaps (t: string) =
        t.Length > 0
        && System.Char.IsUpper t[0]
        && t |> Seq.forall (fun c -> System.Char.IsUpper c || System.Char.IsDigit c || "_-|.:[]".Contains c)

    // the head of a help row: its spellings and the value shape, then the
    // doc. `-o, --outfile <x>  doc`, `--file=ARCHIVE`, `-i[SUFFIX],
    // --in-place[=SUFFIX]`, `-E, -r, --regexp-extended`, cobra's
    // `-a, --attach list   doc`, python's `-c cmd : doc`, Go's `-H value`.
    // None when the line does not start with a spelling.
    let private parseRow (line: string) : Flag option =
        // git's `--[no-]quiet` is the long `--quiet` (its helper lists
        // the `--no-` form apart)
        let s = line.TrimStart().Replace("--[no-]", "--")

        if not (s.StartsWith "-") || s.StartsWith "--- " || s = "--" || s.StartsWith "-- " then
            None
        else
            let mutable pos = 0
            let mutable longs = []
            let mutable shorts = []
            let mutable literals = []
            let mutable arity: SigArity option = None
            let mutable boolValue = false
            let mutable go = true
            let n = s.Length
            let at i = if i < n then s[i] else '\000'

            let readWhile (i: int) (pred: char -> bool) =
                let mutable j = i

                while j < n && pred s[j] do
                    j <- j + 1

                j

            while go do
                // one spelling, `+x` toggles skipped (fzf)
                if at pos = '+' then
                    pos <- readWhile pos (fun c -> c <> ' ' && c <> ',')
                elif at pos = '-' then
                    let dashes = if at (pos + 1) = '-' then 2 else 1
                    let st = pos + dashes
                    let e = readWhile st (fun c -> System.Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '?' || c = '#' || c = '@')
                    let name = s.Substring(st, e - st)
                    let letters = System.Text.RegularExpressions.Regex.Match(s.Substring st, "^\\[([A-Za-z]{2,})\\]")

                    // the spelling's end, or -1 when this is no spelling
                    let spelled =
                        if dashes = 1 && at st = '<' && s.IndexOf('>', st) > 0 then
                            // kill's `-<signal>`: any dash word, no spelling of
                            // its own (the help states it [D:sig-help-shapes])
                            s.IndexOf('>', st) + 1
                        elif dashes = 1 && letters.Success then
                            // perl's `-[mM]`: each letter a short
                            for c in letters.Groups[1].Value do
                                shorts <- shorts @ [ string c ]

                            st + letters.Length
                        elif name = "" || (name.Length > 1 && not (System.Char.IsLetterOrDigit name[0])) then
                            -1
                        elif dashes = 1 && System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Z][a-z]{2,}$") then
                            // perl's `-Idirectory`: the letter, its value glued
                            shorts <- shorts @ [ name.Substring(0, 1) ]
                            arity <- Some AOne
                            e
                        else
                            if dashes = 2 then
                                if legalLong name then longs <- longs @ [ name ]
                            elif name.Length = 1 then
                                if legalShort name then shorts <- shorts @ [ name ]
                            elif legalLong name then
                                literals <- literals @ [ "-" + name ]

                            e

                    if spelled < 0 then
                        go <- false
                    else
                        let oneLetter = dashes = 1 && name.Length = 1 && spelled = e
                        pos <- spelled
                        // the value shape glued to the spelling
                        let glued = s.Substring(pos, readWhile pos (fun c -> c <> ' ' && c <> ',') - pos)

                        if System.Text.RegularExpressions.Regex.IsMatch(glued, "^\\[?=(BOOL|BOOLEAN|<bool>|<boolean>)\\]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) then
                            boolValue <- true

                        if at pos = '[' then
                            arity <- Some AOpt
                            pos <- readWhile pos (fun c -> c <> ' ' && c <> ',')
                        elif at pos = '=' then
                            arity <- Some AOne
                            pos <- readWhile pos (fun c -> c <> ' ' && c <> ',')
                        elif oneLetter && "/:%@<".Contains(at pos) then
                            // perl's `-F/pattern/`: a value glued to the letter
                            arity <- Some AOne
                            pos <- readWhile pos (fun c -> c <> ' ' && c <> ',')
                        elif at pos = ' ' && at (pos + 1) <> ' ' && at (pos + 1) <> '-' && at (pos + 1) <> '\000' then
                            // ` <x>`, ` VALUE`, ` [X]`, ` cmd :`, cobra's
                            // ` string   doc` — one word, then a gap,
                            // a comma, a colon or the end of the line
                            let ts = pos + 1
                            let te = readWhile ts (fun c -> c <> ' ' && c <> ',')
                            let tok = s.Substring(ts, te - ts)
                            let tail = s.Substring te
                            let gapAfter = tail = "" || tail.StartsWith "  " || tail.StartsWith ", " || tail.StartsWith "," || tail.StartsWith " :" || tail.StartsWith ":"

                            let placeholder =
                                (tok.StartsWith "<" && tok.TrimEnd(':').EndsWith ">")
                                || (tok.StartsWith "[" && tok.EndsWith "]")
                                || allCaps (tok.TrimEnd(':', ','))
                                || tok = "..."
                                || (gapAfter
                                    && tok.Length > 0
                                    && System.Char.IsLower tok[0]
                                    && tok.TrimEnd(':', ',') |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || "|-_.[]<>=".Contains c))

                            // az's `[Required]` marks the flag, not a value
                            if tok = "[Required]" then
                                pos <- te
                            elif placeholder then
                                arity <- Some(if tok.StartsWith "[" then AOpt else AOne)
                                pos <- te
                            elif System.Text.RegularExpressions.Regex.IsMatch(tok, "^<[^<>]+>\\.\\.\\.$") then
                                // fd's `--exec <cmd>...`: the values run to the
                                // end — the tool's options end here [D:sig-arity]
                                arity <- Some ARest
                                pos <- te

                        // the next spelling: `, -x`, ` | -x`, `|-x`, `, `
                        let rest = s.Substring pos

                        if rest.StartsWith ", -" || rest.StartsWith ",-" then
                            pos <- pos + (if rest.StartsWith ", " then 2 else 1)
                        elif System.Text.RegularExpressions.Regex.IsMatch(rest, "^, +-") then
                            // shfmt's aligned `-w,     --write`
                            pos <- pos + (rest.Length - rest.Substring(1).TrimStart().Length)
                        elif rest.StartsWith " | -" then
                            pos <- pos + 3
                        elif rest.StartsWith "|-" then
                            pos <- pos + 1
                        elif rest.StartsWith " -" && not (rest.StartsWith "  ") then
                            // journalctl's `-h --help`, az's `--uri --url -u`
                            // (no comma)
                            pos <- pos + 1
                        elif longs.IsEmpty && not shorts.IsEmpty && rest.StartsWith "  " && rest.TrimStart().StartsWith "--" then
                            // a box table's `-d    --dates` (the column gap)
                            pos <- pos + (rest.Length - rest.TrimStart().Length)
                        else
                            go <- false
                else
                    go <- false

            if longs.IsEmpty && shorts.IsEmpty && literals.IsEmpty then
                None
            else
                let doc = s.Substring(min pos n).Trim().TrimStart(':').Trim()

                // the tool's own options end at this one (python's `-c cmd`,
                // `-m mod`) [D:sig-arity]
                let arity =
                    if doc.Contains "terminates option list" then Some ARest else arity

                Some
                    { Long = List.tryHead longs |> Option.defaultValue ""
                      Shorts = shorts
                      Literals = literals
                      Arity = arity
                      Doc = (if doc = "" then None else Some doc)
                      BoolValue = boolValue }

    // every long a row names beyond its first is an alias of it (`-n,
    // --quiet, --silent`; az's `--name --alias -n`): they become their own
    // flags with the row's arity
    let private rowAliases (line: string) : Flag list =
        let s = line.TrimStart().Replace("--[no-]", "--")

        if not (s.StartsWith "-") then
            []
        else
            // spellings up to the doc gap only
            let head =
                match s.IndexOf "  " with
                | -1 -> s
                | i -> s.Substring(0, i)

            System.Text.RegularExpressions.Regex.Matches(head, "(?:^|[\\s,|])--([A-Za-z0-9][A-Za-z0-9_-]*)")
            |> Seq.map (fun m -> m.Groups[1].Value)
            |> Seq.distinct
            |> List.ofSeq
            |> function
                | _ :: aliases -> aliases |> List.map (fun l -> { noFlag with Long = l })
                | [] -> []

    let private parseHelp (text: string) : Flag list =
        let flags = ResizeArray<Flag>()
        // a docless row adopts the next deeper prose line (Go's flag
        // package, git's -h)
        let mutable docless = -1
        let mutable rowIndent = 0

        // a two-column table (unzip's `-p  doc     -l  doc`): the right
        // column is a row of its own
        let columns (raw: string) =
            match System.Text.RegularExpressions.Regex.Match(raw, "^(\\s*-.*?\\S)\\s{3,}(-[A-Za-z][A-Za-z]?\\s+\\S.*)$") with
            | m when m.Success && (parseRow m.Groups[1].Value).IsSome ->
                let ind = raw.Length - raw.TrimStart().Length
                [ m.Groups[1].Value; String.replicate ind " " + m.Groups[2].Value ]
            | _ -> [ raw ]

        for raw in text.Split '\n' |> Array.collect (columns >> Array.ofList) do
            // broot draws its options as a box table — the border and
            // column glyphs (U+2500..U+257F) become spaces and the rows
            // collapse into the standard `-d  --dates  desc` shape
            let line = System.Text.RegularExpressions.Regex.Replace(raw, "[─-╿]", " ")
            let indent = line.Length - line.TrimStart().Length

            match parseRow line with
            | None when docless >= 0 && line.Trim() <> "" && indent > rowIndent ->
                flags[docless] <- { flags[docless] with Doc = Some(line.Trim()) }
                docless <- -1
            | None -> if line.Trim() <> "" then docless <- -1
            | Some f ->
                docless <- (if f.Doc.IsNone then flags.Count else -1)
                rowIndent <- indent
                let aliases = rowAliases line |> List.map (fun a -> { a with Arity = f.Arity; Doc = f.Doc })
                flags.Add f
                flags.AddRange aliases

                // python's `-h : … (also -? or --help)`: the doc names more
                // spellings of the same flag
                match f.Doc with
                | Some d ->
                    for m in System.Text.RegularExpressions.Regex.Matches(d, "\\(also ([^)]*)\\)") do
                        for t in m.Groups[1].Value.Split([| ' '; ',' |], StringSplitOptions.RemoveEmptyEntries) do
                            if t.StartsWith "--" && legalLong (t.Substring 2) then
                                flags.Add { noFlag with Long = t.Substring 2; Arity = f.Arity }
                            elif t.Length = 2 && t[0] = '-' && legalShort (t.Substring 1) then
                                flags.Add { noFlag with Shorts = [ t.Substring 1 ]; Arity = f.Arity }
                | None -> ()

        mergeFlags [ List.ofSeq flags ]

    // usage brackets: `[-w|--workspace <name>]`, `[--json]`, git's
    // `[-C <path>] [--exec-path[=<path>]] [-p | --paginate | -P]`
    let private parseUsage (text: string) : Flag list =
        let flags = ResizeArray<Flag>()

        for m in System.Text.RegularExpressions.Regex.Matches(text, "\\[((?:[^\\[\\]]|\\[[^\\[\\]]*\\])*)\\]") do
            let inner = m.Groups[1].Value.Trim()

            if inner.StartsWith "-" then
                for alt in inner.Split '|' do
                    let a = alt.Trim()

                    if a.StartsWith "-" then
                        // a nested `[-w|--workspace <x> ...]` repeat is the same flag
                        match parseRow ("  " + a.Replace("--[no-]", "--")) with
                        | Some f -> flags.Add { f with Doc = None }
                        | None -> ()

        mergeFlags [ List.ofSeq flags ]

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
        |> Seq.map (fun long -> { noFlag with Long = long })
        |> List.ofSeq

    // fish `complete` lines, both spellings: `-c tool -s x -l long -o old
    // -d desc -r` and `--command tool --short-option x --long-option long
    // --old-option old --description desc --require-parameter`; `-r`/`-x`
    // (and their long forms) mark a value
    let private parseFish (text: string) (tool: string) : Flag list =
        let flags = ResizeArray<Flag>()

        for line in text.Split '\n' do
            let t = line.Trim()

            if t.StartsWith "complete " then
                let tokens =
                    System.Text.RegularExpressions.Regex.Matches(t, "'(?:[^'\\\\]|\\\\.)*'|\"[^\"]*\"|\\S+")
                    |> Seq.map (fun m -> m.Value.Trim([| '\''; '"' |]))
                    |> List.ofSeq

                let rec walk (cmd: string option) (f: Flag) toks =
                    match toks with
                    | ("-c" | "--command") :: v :: rest -> walk (Some v) f rest
                    | ("-l" | "--long-option") :: v :: rest -> walk cmd { f with Long = (if f.Long = "" then v else f.Long) } rest
                    | ("-s" | "--short-option") :: v :: rest -> walk cmd { f with Shorts = f.Shorts @ [ v ] } rest
                    | ("-o" | "--old-option") :: v :: rest -> walk cmd { f with Literals = f.Literals @ [ "-" + v ] } rest
                    | ("-d" | "--description") :: v :: rest -> walk cmd { f with Doc = Some v } rest
                    | ("-r" | "--require-parameter" | "-x" | "--exclusive") :: rest -> walk cmd { f with Arity = Some AOne } rest
                    | ("-n" | "--condition" | "-a" | "--arguments" | "-w" | "--wraps" | "-k" | "--keep-order") :: _ :: rest ->
                        walk cmd f rest
                    | _ :: rest -> walk cmd f rest
                    | [] -> cmd, f

                match walk None noFlag tokens with
                | Some c, f when c = tool ->
                    let f =
                        { f with
                            Shorts = f.Shorts |> List.filter legalShort
                            Literals = f.Literals |> List.filter (fun l -> legalLong (l.Substring 1)) }

                    if (f.Long <> "" && legalLong f.Long) || not f.Shorts.IsEmpty || not f.Literals.IsEmpty then
                        flags.Add(if legalLong f.Long then f else { f with Long = "" })
                | _ -> ()

        mergeFlags [ List.ofSeq flags ]

    // cobra's `__complete <path> -`: `--flag<TAB>doc` lines, a flag's
    // short right after it with the same doc, then the `:N` directive
    let private parseCobraComplete (text: string) : Flag list =
        let rows =
            text.Split '\n'
            |> Array.choose (fun l ->
                let name, doc =
                    match l.IndexOf '\t' with
                    | -1 -> l.Trim(), ""
                    | i -> l.Substring(0, i).Trim(), l.Substring(i + 1).Trim()

                if name.StartsWith "-" && not (name.StartsWith ":") then Some(name, doc) else None)
            |> List.ofArray

        let rec go (acc: Flag list) rows =
            match rows with
            | [] -> List.rev acc
            | (n: string, d) :: rest when n.StartsWith "--" ->
                let long = n.Substring 2

                match rest with
                | (s: string, d2) :: rest' when s.Length = 2 && not (s.StartsWith "--") && d2 = d ->
                    go ({ noFlag with Long = long; Shorts = [ s.Substring 1 ]; Doc = (if d = "" then None else Some d) } :: acc) rest'
                | _ -> go ({ noFlag with Long = long; Doc = (if d = "" then None else Some d) } :: acc) rest
            | (s, d) :: rest when s.Length = 2 ->
                go ({ noFlag with Shorts = [ s.Substring 1 ]; Doc = (if d = "" then None else Some d) } :: acc) rest
            | _ :: rest -> go acc rest

        go [] rows
        |> List.map (fun f -> { f with Shorts = f.Shorts |> List.filter legalShort })
        |> List.filter (fun f -> (f.Long = "" || legalLong f.Long) && (f.Long <> "" || not f.Shorts.IsEmpty))
        |> fun fs -> mergeFlags [ fs ]

    // git's `--git-completion-helper`: one line of `--x`, `--y=` (a
    // value), then `--` and the `--no-x` forms
    let private parseGitHelper (text: string) : Flag list =
        text.Split([| ' '; '\n'; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun t ->
            if t.StartsWith "--" && t.Length > 2 then
                let v = t.EndsWith "="
                let name = t.Substring(2).TrimEnd '='

                if legalLong name then
                    Some { noFlag with Long = name; Arity = Some(if v then AOne else ANone) }
                else
                    None
            else
                None)
        |> List.ofArray
        |> fun fs -> mergeFlags [ fs ]

    // V8's `--v8-options`: `  --name (doc)` then `type: bool  default: …`
    let private parseV8 (text: string) : Flag list =
        let lines = text.Split '\n'

        [ for i in 0 .. lines.Length - 1 do
              let m = System.Text.RegularExpressions.Regex.Match(lines[i], "^  --([a-z0-9][a-z0-9_-]*) \\((.*)\\)\\s*$")

              if m.Success then
                  let ty =
                      if i + 1 < lines.Length then
                          let t = System.Text.RegularExpressions.Regex.Match(lines[i + 1], "type: (\\S+)")
                          if t.Success then t.Groups[1].Value else ""
                      else
                          ""

                  yield
                      { noFlag with
                          Long = m.Groups[1].Value.Replace('_', '-')
                          Arity = Some(if ty = "bool" || ty = "maybe_bool" then ANone else AOne)
                          Doc = Some m.Groups[2].Value } ]
        |> fun fs -> mergeFlags [ fs ]

    // gpg's `--dump-options`: one `--name` per line
    let private parseDump (text: string) : Flag list =
        text.Split '\n'
        |> Array.choose (fun l ->
            let t = l.Trim()

            if t.StartsWith "--" && legalLong (t.Substring 2) then
                Some { noFlag with Long = t.Substring 2 }
            else
                None)
        |> List.ofArray
        |> fun fs -> mergeFlags [ fs ]

    // find's predicates: single-dash words, several per line, a value
    // when an all-caps word or N follows (`-name PATTERN`, `-mtime N`)
    let private parsePredicates (text: string) : Flag list =
        [ for line in text.Split '\n' do
              let toks = line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

              for i in 0 .. toks.Length - 1 do
                  let t = toks[i]

                  if t.Length > 2 && t[0] = '-' && t[1] <> '-' && legalLong (t.Substring 1) && System.Char.IsLower t[1] then
                      let next = if i + 1 < toks.Length then toks[i + 1] else ""

                      let arity =
                          if next = "COMMAND" then ARest
                          elif next <> "" && (allCaps next || next.StartsWith "[" || next = "N" || next.StartsWith "N[") then AOne
                          else ANone

                      yield { noFlag with Literals = [ t ]; Arity = Some arity } ]
        |> fun fs -> mergeFlags [ fs ]

    // a heading names a command table: non-indented, mentions commands,
    // never options/flags/usage, ends with a colon or is an all-caps banner
    let private isCommandHeading (line: string) =
        let low = line.ToLowerInvariant()

        line.Length > 0
        && not (System.Char.IsWhiteSpace line[0])
        && not (line.StartsWith "-")
        && low.Contains "command"
        && not (low.Contains "option" || low.Contains "flag" || low.Contains "usage")
        && (line.TrimEnd().EndsWith ":" || line = line.ToUpperInvariant())

    // the phantom filter [D:sig-sources]: the words a help page lists as
    // its commands — rows at the table's own indent whose first word
    // starts with a letter and ends at a gap, a colon (gh's `auth:`), an
    // argument shape (systemctl's `list-units [PATTERN...]`) or the line;
    // a prose line, a description column, a deeper continuation, a flag
    // row never count. npm's comma-separated table counts word by word.
    let private commandTokens (helpText: string) : string list =
        let subs = ResizeArray<string>()
        let mutable inSection = false
        let mutable indent = -1

        let word (t: string) =
            t.Length > 1
            && System.Char.IsLower t[0]
            && t |> Seq.forall (fun c -> System.Char.IsLower c || System.Char.IsDigit c || c = '-')

        for raw in helpText.Split '\n' do
            let line = raw.TrimEnd()

            if isCommandHeading line then
                inSection <- true
                indent <- -1
            elif line.Length > 0 && not (System.Char.IsWhiteSpace line[0]) then
                inSection <- false
            elif inSection && line.Trim() <> "" then
                let ind = line.Length - line.TrimStart().Length

                if indent < 0 then
                    indent <- ind

                if ind = indent then
                    let body = line.TrimStart()

                    if body.Contains ", " && body.Split(',') |> Array.forall (fun p -> word (p.Trim()) || p.Trim() = "") then
                        for p in body.Split ',' do
                            let w = p.Trim()

                            if word w && not (subs.Contains w) then
                                subs.Add w
                    else
                        let m =
                            System.Text.RegularExpressions.Regex.Match(body, "^([a-z][a-z0-9-]*)\\*?:?(?:$|  |: | \\[| <| \\{| [A-Z][A-Z0-9_.]*(?:\\.\\.\\.)?(?: |$))")

                        if m.Success then
                            let w = m.Groups[1].Value

                            if word w && not (subs.Contains w) then
                                subs.Add w

        List.ofSeq subs

    // the usage ends the tool's own options at the first operand
    // [D:sig-operand-stop]: `docker run [OPTIONS] IMAGE [COMMAND] [ARG...]`,
    // `python3 [option] ... [-c cmd | -m mod | file | -] [arg] ...`,
    // `node [options] [ script.js ] [arguments]`, `ssh … destination
    // [command [argument ...]]` — an operand followed by the arguments it
    // hands on; a wrapper's `COMMAND...`, `<program> [<argument>...]`, a
    // usage ending at its command after its options (`stdbuf [OPTION]...
    // COMMAND` — bat's bare `bat <COMMAND>` is a subcommand slot), bash's
    // `script-file ...`; sudo's `[command [arg ...]]`, systemd-run's
    // `COMMAND [ARGUMENTS...]`, chrt's `<priority> <command> [<arg>...]`,
    // strace's `PROG [ARGS]`, a usage ending at an optional `[COMMAND]`
    // after its options (uvx; a tool with a commands table never stops at
    // the top), bunx's `[flags and arguments for the package]`. Every usage
    // line counts (flock, bash, sudo list several); a help with none reads
    // its synopsis lines, `<tool> [`/`<tool> <`/`<tool> COMMAND`
    // (systemd-run, chrt) [D:sig-help-shapes]
    let private stopsAtOperand (toolName: string) (helpText: string) =
        let lines = helpText.Split '\n'

        let usageAt (i: int) =
            let l = lines[i]
            let after = l.Substring(l.ToLowerInvariant().IndexOf "usage" + 5).TrimStart(':', ' ', '\t')

            let more =
                lines
                |> Array.skip (i + 1)
                |> Array.takeWhile (fun l ->
                    l.Trim() <> ""
                    && not (l.TrimStart().ToLowerInvariant().StartsWith "usage")
                    && (System.Char.IsWhiteSpace l[0] || l.TrimStart().StartsWith "or:"))
                |> List.ofArray

            (if after.Trim() = "" then [] else [ after ]) @ more

        let usage =
            match
                lines
                |> Array.indexed
                |> Array.filter (fun (_, l) -> l.TrimStart().ToLowerInvariant().StartsWith "usage")
                |> Array.map fst
                |> List.ofArray
            with
            | [] ->
                let synopsis = "^" + System.Text.RegularExpressions.Regex.Escape toolName + " +(\\[|<|[A-Z])"

                lines
                |> Array.filter (fun l -> System.Text.RegularExpressions.Regex.IsMatch(l.Trim(), synopsis))
                |> Array.map (fun l -> l.Trim())
                |> List.ofArray
            | starts -> starts |> List.collect usageAt

        usage
        |> List.exists (fun u ->
            System.Text.RegularExpressions.Regex.IsMatch(
                u.TrimEnd(),
                "(?i)(\\b(command|program|cmd|prog)>?\\]?\\s+\\[?<?[\\w-]*arg(s|uments?)?\\b|\\[arg\\]\\s*\\.\\.\\.|\\[arguments\\]|\\[args\\.\\.\\.\\]|\\bcommand\\.\\.\\.|\\]\\S*\\s+\\[?<?\\b(command|program)>?\\]*$|\\bscript(-file)?\\s*(\\.\\.\\.|\\[)|\\[(flags|options) and arg(s|uments)\\b)"
            ))

    // a listed switch `--no-x` has its `--x` (node's `--no-deprecation`:
    // `--deprecation` is valid), a listed `--x=BOOL` its `--no-x`
    // (systemctl's `--legend=BOOL`: `--no-legend`); a tool that lacks it
    // costs a missed typo, never a false warning [D:sig-help-shapes]
    let private withPositives (fs: Flag list) : Flag list =
        let have = fs |> List.map (fun f -> f.Long) |> Set.ofList

        let positives =
            fs
            |> List.choose (fun f ->
                if f.Long.StartsWith "no-" && f.Long.Length > 3 && (f.Arity = None || f.Arity = Some ANone) && not (Set.contains (f.Long.Substring 3) have) then
                    Some { noFlag with Long = f.Long.Substring 3; Arity = Some ANone }
                elif f.BoolValue && f.Long <> "" && not (f.Long.StartsWith "no-") && not (Set.contains ("no-" + f.Long) have) then
                    Some { noFlag with Long = "no-" + f.Long; Arity = Some ANone }
                else
                    None)

        mergeFlags [ fs; positives ]

    // ---- the walk -------------------------------------------------------

    // one walked case: its path words, own flags, and whether its first
    // operand ends the tool's options
    type private Node =
        { Path: string list
          Flags: Flag list
          Stop: bool
          // other spellings of the same command (cobra's Aliases:
          // `eval-all, ea`; `docker container run, docker run`) — recorded
          // as cases of the same record, never probed
          Aliases: string list list }

    let private aliasesOf (toolName: string) (path: string list) (page: string) : string list list =
        let lines = page.Split '\n'

        match lines |> Array.tryFindIndex (fun l -> let t = l.Trim() in t = "Aliases:" || t = "ALIASES") with
        | None -> []
        | Some i ->
            lines
            |> Array.skip (i + 1)
            |> Array.takeWhile (fun l -> l.StartsWith " " && l.Trim() <> "")
            |> Array.collect (fun l -> l.Split ',')
            |> Array.map (fun e -> e.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
            |> Array.choose (fun ws ->
                match ws with
                | [ w ] -> Some((List.truncate (path.Length - 1) path) @ [ w ])
                | t :: rest when t = toolName && not rest.IsEmpty -> Some rest
                | _ -> None)
            |> Array.filter (fun a -> a <> path && a |> List.forall (fun w -> w.Length > 0 && System.Char.IsLetter w[0]))
            |> Array.distinct
            |> List.ofArray

    // depth-first per top-level command, each with its own budget, so a
    // tool with forty commands still reaches `gh pr create` and `docker
    // compose up` [D:sig-sources]. A page identical to its parent's is the
    // tool ignoring the word (systemctl, whose verbs share one help): no
    // case, no descent. `flagsAt` reads one node's flags from its page.
    let private walkTree
        (toolName: string)
        (topHelp: string)
        (probeHelp: string list -> string option)
        (flagsAt: string list -> string -> Flag list)
        : Node list * int =
        // help/completion are listed everywhere and walk nowhere
        let noise k = k = "help" || k = "completion"

        // one top-level command's subtree, depth-first, on its own budget
        let walkTop (top: string) : Node list * int =
            let nodes = ResizeArray<Node>()
            let budget = ref 40
            let probed = ref 0

            let rec visit (path: string list) (parentPage: string) =
                if budget.Value > 0 && path.Length <= 4 then
                    budget.Value <- budget.Value - 1
                    probed.Value <- probed.Value + 1

                    match probeHelp path with
                    | Some page when page.Trim() <> parentPage.Trim() && page.Trim() <> topHelp.Trim() ->
                        let kids = commandTokens page |> List.filter (fun k -> not (noise k) && not (List.contains k path))

                        nodes.Add
                            { Path = path
                              Flags = flagsAt path page
                              Stop = kids.IsEmpty && stopsAtOperand toolName page
                              Aliases = aliasesOf toolName path page }

                        for k in kids do
                            visit (path @ [ k ]) page
                    | _ -> ()

            visit [ top ] topHelp
            List.ofSeq nodes, probed.Value

        // the subtrees are independent: eight at a time (gh and docker
        // spend seconds per hundred probes), joined in the tables' order,
        // so the signature is the same however the probes interleave
        let tops = commandTokens topHelp |> List.filter (noise >> not) |> List.truncate 100 |> Array.ofList
        let results = Array.create tops.Length ([], 0)

        System.Threading.Tasks.Parallel.For(
            0,
            tops.Length,
            System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = 8),
            fun i -> results[i] <- walkTop tops[i]
        )
        |> ignore

        (results |> Array.toList |> List.collect fst), (results |> Array.sumBy snd)

    let private fieldName (long: string) =
        // inverse kebab: dry-run -> dryRun
        long.Split([| '-'; '_' |])
        |> Array.mapi (fun i part ->
            if i = 0 || part = "" then
                part
            else
                string (System.Char.ToUpper part[0]) + part.Substring 1)
        |> String.concat ""

    // a tool's --version and help text are data [D:sig-text-sanitize]:
    // control bytes and escapes render visibly in the generated file
    let private escape (s: string) =
        (Eval.sanitizeTtyEcho (s.Replace("\r", ""))).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")

    // where a tool's own fish completion sits: beside its install prefix
    // (`<prefix>/bin/tool` -> `<prefix>/share/fish/…`), the PATH entry's
    // and the real file's [D:sig-sources] — never another install's
    let private shippedFish (tool: string) : string option =
        let name = IO.Path.GetFileName tool

        let resolved =
            if tool.Contains '/' then
                Some(IO.Path.GetFullPath tool)
            else
                Extern.resolveFile tool

        let real (p: string) =
            try
                match IO.File.ResolveLinkTarget(p, true) with
                | null -> p
                | t -> t.FullName
            with _ ->
                p

        match resolved with
        | None -> None
        | Some bin ->
            [ bin; real bin ]
            |> List.distinct
            |> List.collect (fun b ->
                match IO.Path.GetDirectoryName b |> Option.ofObj |> Option.bind (IO.Path.GetDirectoryName >> Option.ofObj) with
                | Some prefix ->
                    [ IO.Path.Combine(prefix, "share", "fish", "vendor_completions.d", name + ".fish")
                      IO.Path.Combine(prefix, "share", "fish", "completions", name + ".fish") ]
                | None -> [])
            |> List.tryFind IO.File.Exists

    let generateTo (globalDir: string option) (weirDir: string) (tool: string) : Result<string, string> =
        cutProbes.Clear()
        slowProbes.Clear()
        probed.Clear()
        // Flag probes are safe on any tool; a bare word is not — `code
        // completion fish` opens VS Code on two files
        // [D:sig-version-probe]. Bare-word probes (completion fish, the
        // version word, a subcommand's help) run only for words the tool's
        // own --help lists as commands.
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
            let topHelp = runTool tool [ "--help" ]

            // exit-0 help only gates and walks; a refused --help still
            // yields its usage dump to the harvest below, and a tool that
            // answers `-h` or `-help` instead gets those rows
            let harvestOnly =
                match topHelp with
                | Some _ -> None
                | None -> runToolAnyExit tool [ "--help" ]

            let topHelp =
                match topHelp with
                | Some t -> Some t
                | None ->
                    [ [ "-h" ]; [ "-help" ] ]
                    |> List.tryPick (fun a ->
                        match runTool tool a with
                        | Some t when (parseHelp t).Length > 0 -> Some t
                        | _ -> None)

            let advertised = topHelp |> Option.map commandTokens |> Option.defaultValue []

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
                    runTool tool [ "completion"; "fish" ]
                else
                    None

            let help = topHelp |> Option.defaultValue ""
            let helpRows = parseHelp help

            // the cobra endpoint: its fish script — printed, or the one it
            // ships — calls `<tool> __complete`
            let cobra =
                match fishText with
                | Some t -> t.Contains "__complete"
                | None ->
                    match shippedFish tool with
                    | Some f ->
                        try
                            (IO.File.ReadAllText f).Contains "__complete"
                        with _ ->
                            false
                    | None -> false

            let isGit =
                match version with
                | Some v -> v.StartsWith "git version "
                | None -> false

            // the full-help switches a help page names [D:sig-sources]
            // (GnuPG's tools answer --dump-options without naming it)
            let gnupg =
                match version with
                | Some v -> v.Contains "(GnuPG)"
                | None -> false

            let fullHelp =
                [ "--help all", [ "--help"; "all" ], parseHelp
                  "--help-all", [ "--help-all" ], parseHelp
                  "--dump-options", [ "--dump-options" ], parseDump
                  "--v8-options", [ "--v8-options" ], parseV8 ]
                |> List.filter (fun (mention, _, _) -> help.Contains mention || (gnupg && mention = "--dump-options"))
                |> List.choose (fun (mention, argv, parse) ->
                    runTool tool argv |> Option.map (fun t -> mention, parse t))

            // the convention [D:sig-conventions]: find's predicates (lines
            // listing several single-dash words), Go's flag package (rows
            // that are single-dash words, one per line, no `--long` rows)
            let isGoRow (l: string) =
                System.Text.RegularExpressions.Regex.IsMatch(l, "^\\s{0,4}-[A-Za-z][\\w.-]*( [A-Za-z][\\w.]*)?\\s*$")

            let goRows = help.Split '\n' |> Array.filter isGoRow |> Array.length

            let predicateLines =
                help.Split '\n'
                |> Array.filter (fun l ->
                    l.StartsWith " "
                    && not (isGoRow l)
                    && (l.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        |> Array.filter (fun t -> t.Length > 2 && t[0] = '-' && System.Char.IsLower t[1])
                        |> Array.length) >= 2)
                |> Array.length

            let longRows = helpRows |> List.filter (fun f -> f.Long <> "") |> List.length

            let convention =
                if isGit || cobra then "gnu"
                elif goRows >= 2 && longRows <= 2 then "go"
                elif predicateLines >= 3 && longRows <= 3 then "find"
                else "gnu"

            // a Go row is a long under another dash count: one spelling per
            // flag, single-letter ones too
            let goFlags (fs: Flag list) =
                fs
                |> List.map (fun f ->
                    let words = (f.Literals |> List.map (fun l -> l.TrimStart '-')) @ f.Shorts @ (if f.Long <> "" then [ f.Long ] else [])
                    { f with Long = ""; Shorts = []; Literals = words |> List.distinct |> List.map (fun w -> "-" + w) })

            let gitCwd () =
                // git's helper needs a repository: an empty one in a temp
                // dir, discarded after the probes
                let d = IO.Path.Combine(IO.Path.GetTempPath(), $"weir-siggit-{System.Guid.NewGuid():N}")
                Contracts.ProbeGuard.trackPath d
                IO.Directory.CreateDirectory(IO.Path.Combine(d, ".git", "objects")) |> ignore
                IO.Directory.CreateDirectory(IO.Path.Combine(d, ".git", "refs")) |> ignore
                IO.File.WriteAllText(IO.Path.Combine(d, ".git", "HEAD"), "ref: refs/heads/main\n")
                d

            let source, flags, nodes, unchecked, walkNote =
                if isGit then
                    // git [D:sig-sources]: its own builtin list; per builtin
                    // the completion helper (longs, `=` a value) and `-h`
                    // (shorts and arities). A builtin that passes an
                    // unknown option on (log, show: revision options
                    // outside parse-options) cannot be listed — unchecked
                    let d = gitCwd ()

                    try
                        let run args = runIn (Some d) false tool args
                        let runAny args = runIn (Some d) true tool args

                        let builtins =
                            run [ "--list-cmds=builtins" ]
                            |> Option.map (fun t -> t.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.Trim()) |> List.ofArray)
                            |> Option.defaultValue []
                            |> List.filter (fun c -> c.Length > 1 && System.Char.IsLower c[0] && c |> Seq.forall (fun ch -> System.Char.IsLower ch || System.Char.IsDigit ch || ch = '-'))
                            |> List.truncate 200

                        let mutable opened = []

                        // one parse-options command: unchecked when it passes
                        // an unknown option on, else its helper's longs and
                        // its -h's shorts
                        let probeCmd (parentHelper: string option) (path: string list) : Node option =
                            match run (path @ [ "--weir-probe-unknown"; "--git-completion-helper-all" ]) with
                            | Some _ ->
                                opened <- String.concat " " path :: opened
                                Some { Path = path; Flags = []; Stop = false; Aliases = [] }
                            | None ->
                                // `-all` lists the hidden options too (--allow-empty)
                                // a sub whose helper is its parent's is the
                                // parent ignoring the word (`rebase options`)
                                match run (path @ [ "--git-completion-helper-all" ]) with
                                | Some h when (parseGitHelper h).Length > 0 && Some h <> parentHelper ->
                                    let dashText = runAny (path @ [ "-h" ])

                                    // its usage hands diff or log options on
                                    // (`git stash show [<diff-options>]`): no
                                    // list holds them [D:sig-unwalked]
                                    let passesOn =
                                        let at = path |> List.map System.Text.RegularExpressions.Regex.Escape |> String.concat "\\s+"

                                        (dashText |> Option.defaultValue "").Split '\n'
                                        |> Array.exists (fun l ->
                                            let m = System.Text.RegularExpressions.Regex.Match(l, "^\\s*(?:usage|or):\\s+git\\s+" + at + "(\\s.*)?$")

                                            m.Success
                                            && not (System.Text.RegularExpressions.Regex.IsMatch(m.Groups[1].Value, "^\\s+[a-z][a-z0-9-]*(\\s|$)"))
                                            && System.Text.RegularExpressions.Regex.IsMatch(m.Groups[1].Value, "<[a-z-]*(diff|log)-options>"))

                                    let dashH =
                                        dashText
                                        |> Option.map (fun t -> mergeFlags [ parseHelp t; parseUsage t ])
                                        |> Option.defaultValue []
                                    let byLong = dashH |> List.filter (fun f -> f.Long <> "") |> List.map (fun f -> f.Long, f) |> Map.ofList

                                    let helper =
                                        parseGitHelper h
                                        |> List.map (fun f ->
                                            match Map.tryFind f.Long byLong with
                                            | Some r -> { f with Shorts = r.Shorts; Doc = r.Doc; Arity = (if f.Arity = Some AOne then Some AOne else r.Arity |> Option.orElse f.Arity) }
                                            | None -> f)

                                    if passesOn then
                                        opened <- String.concat " " path :: opened
                                        Some { Path = path; Flags = []; Stop = false; Aliases = [] }
                                    else
                                        Some { Path = path; Flags = mergeFlags [ helper; dashH ]; Stop = false; Aliases = [] }
                                | _ -> None

                        // a builtin's own subcommands (remote add, notes add,
                        // stash push): the words its usage lines name right
                        // after `git <c>` and its bracketed options; only
                        // those whose helper answers become cases
                        // [D:sig-help-shapes]
                        let subsOf (c: string) : string list =
                            match runAny [ c; "-h" ] with
                            | None -> []
                            | Some t ->
                                [ for l in t.Split '\n' do
                                      let m = System.Text.RegularExpressions.Regex.Match(l, "^\\s*(?:usage|or):\\s+git " + System.Text.RegularExpressions.Regex.Escape c + "((?:\\s+\\[(?:[^\\[\\]]|\\[[^\\[\\]]*\\])*\\])*)\\s+([a-z][a-z0-9-]*)\\b(?![=>])")

                                      if m.Success then
                                          yield m.Groups[2].Value

                                      // an optional one: `git stash [push [-p] …`
                                      let o = System.Text.RegularExpressions.Regex.Match(l, "^\\s*(?:usage|or):\\s+git " + System.Text.RegularExpressions.Regex.Escape c + "\\s+\\[([a-z][a-z0-9-]*)\\b(?![=>])")

                                      if o.Success then
                                          yield o.Groups[1].Value ]
                                |> List.distinct
                                |> List.truncate 30

                        let nodes =
                            builtins
                            |> List.collect (fun c ->
                                let own = probeCmd None [ c ]

                                let subs =
                                    match own with
                                    | Some _ when List.contains c opened -> []
                                    | _ ->
                                        let parent = run [ c; "--git-completion-helper-all" ]
                                        subsOf c |> List.choose (fun sub -> probeCmd (Some(parent |> Option.defaultValue "")) [ c; sub ])

                                // a builtin whose helper lists only its
                                // subcommands (config, stash, worktree) still
                                // gets a case [D:sig-help-shapes]: its own flags
                                // are its subcommands' and, for an older form
                                // (`git config --global k v`), the table its
                                // `-h` prints after one of them
                                let parent =
                                    match own with
                                    | Some _ -> None
                                    | None ->
                                        let union = subs |> List.map (fun n -> n.Flags) |> mergeFlags

                                        match union |> List.tryFind (fun f -> f.Long <> "" && (f.Arity = None || f.Arity = Some ANone)) with
                                        | None -> None
                                        | Some first ->
                                            let rows (t: string option) =
                                                t |> Option.map (fun t -> mergeFlags [ parseHelp t; parseUsage t ]) |> Option.defaultValue []

                                            let older = rows (runAny [ c; "--" + first.Long; "-h" ])

                                            Some
                                                { Path = [ c ]
                                                  Flags = mergeFlags [ union; older; rows (runAny [ c; "-h" ]) ]
                                                  Stop = false
                                                  Aliases = [] }

                                Option.toList own @ Option.toList parent @ subs)

                        let globals = mergeFlags [ parseUsage help; helpRows ]
                        "git-helper", globals, nodes, List.rev opened, ""
                    finally
                        try
                            IO.Directory.Delete(d, true)
                        with _ ->
                            ()

                        Contracts.ProbeGuard.untrackPath d
                elif cobra then
                    // cobra [D:sig-sources]: per command, `__complete <path> -`
                    // lists its flags with shorts (plugins answer too), the
                    // help page its arities and its subcommands
                    let complete path = runTool tool ("__complete" :: path @ [ "-" ]) |> Option.map parseCobraComplete |> Option.defaultValue []

                    let arityFrom (rows: Flag list) (fs: Flag list) =
                        let byKey = rows |> List.map (fun f -> keyOf f, f) |> Map.ofList

                        fs
                        |> List.map (fun f ->
                            match Map.tryFind (keyOf f) byKey with
                            | Some r -> { f with Arity = f.Arity |> Option.orElse r.Arity; Doc = f.Doc |> Option.orElse r.Doc }
                            | None -> f)

                    let flagsAt path page =
                        let rows = parseHelp page
                        mergeFlags [ arityFrom rows (complete path); rows ]

                    let nodes, probed = walkTree (IO.Path.GetFileName tool) help (fun p -> runTool tool (p @ [ "--help" ])) flagsAt
                    let root = mergeFlags [ arityFrom helpRows (complete []); helpRows ]
                    "cobra-complete", root, nodes, [], (if nodes.IsEmpty && probed > 0 then $" (walked {probed} subcommand help(s), none yielded a case)" else "")
                else
                    let fish =
                        match fishText with
                        | Some t when t.Contains "complete " ->
                            match parseFish t (IO.Path.GetFileName tool) with
                            | [] -> None
                            | fs -> Some("completion-fish", fs)
                        | _ ->
                            shippedFish tool
                            |> Option.bind (fun f ->
                                match parseFish (IO.File.ReadAllText f) (IO.Path.GetFileName tool) with
                                | [] -> None
                                | fs -> Some("fish-file", fs))

                    let full = fullHelp |> List.collect snd

                    let source, top =
                        match fish, fullHelp with
                        | _, (mention, _) :: _ when not full.IsEmpty ->
                            // `--help all` and `--help-all` are one source
                            let label = mention.TrimStart('-').Replace(" ", "-")
                            (if label = "help-all" then label else "help+" + label), mergeFlags [ full; helpRows ]
                        | Some(label, fs), _ -> label, mergeFlags [ fs; helpRows ]
                        | None, _ ->
                            match topHelp with
                            | Some _ when not helpRows.IsEmpty -> "help", helpRows
                            | Some t ->
                                match parseUsage t with
                                | [] -> "help-scan", harvestHelp t
                                | fs -> "help-scan", fs
                            | None ->
                                match harvestOnly with
                                | Some dump ->
                                    match harvestHelp dump with
                                    | [] -> "help", []
                                    | fs -> "help-scan", fs
                                | None -> "help", []

                    let top =
                        match convention with
                        | "go" ->
                            // a Go tool's help may show a flag only in its
                            // examples (mkcert's `$ mkcert -install`)
                            let prompt = "$ " + IO.Path.GetFileName tool + " "

                            let examples =
                                help.Split '\n'
                                |> Array.map (fun l -> l.Trim())
                                |> Array.filter (fun l -> l.StartsWith prompt)
                                |> Array.collect (fun l ->
                                    let piped = (l.Substring prompt.Length).Split '|'
                                    piped[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                |> Array.filter (fun w -> System.Text.RegularExpressions.Regex.IsMatch(w, "^-[A-Za-z][\\w-]*$"))
                                |> Array.distinct
                                |> Array.map (fun w -> { noFlag with Literals = [ w ]; Arity = Some ANone })
                                |> List.ofArray

                            goFlags (mergeFlags [ top; examples ])
                        | "find" ->
                            // the usage's options: `-L` whole, `-Olevel` a
                            // letter with its value glued
                            let usage =
                                parseUsage help
                                |> List.map (fun f ->
                                    let glued, whole = f.Literals |> List.partition (fun l -> System.Text.RegularExpressions.Regex.IsMatch(l, "^-[A-Z][a-z]+$"))

                                    { f with
                                        Literals = whole @ (f.Shorts |> List.map (fun s -> "-" + s)) @ (glued |> List.map (fun l -> l.Substring(0, 2)))
                                        Shorts = []
                                        Arity = (if glued.IsEmpty then f.Arity else Some AOpt) })

                            mergeFlags [ parsePredicates help; usage; top ]
                        | _ -> withPositives top

                    // a refused --help still lists its commands (npm exits 1
                    // on a full page): the walk reads a recognized command
                    // table from either [D:sig-sources]
                    match topHelp |> Option.orElse harvestOnly with
                    // an open surface [D:sig-open-tool]: the help says any
                    // config key is a flag (npm's `npm <command>
                    // --key=value`) — no list can be complete, so the tool
                    // is recorded unchecked and its commands are not walked
                    | Some t when t.Contains "--key=value" || System.Text.RegularExpressions.Regex.IsMatch(t, "(?m)^\\s*npm [a-z]") ->
                        // npx's help is `npm exec …`: npm's config keys too
                        source, top, [], [ "" ], ""
                    | Some t when convention = "gnu" ->
                        // a sub page's rows: its own help, or its usage brackets (npm)
                        let flagsAt _ page =
                            match parseHelp page with
                            | [] -> parseUsage page
                            | fs -> withPositives (mergeFlags [ fs; parseUsage page ])

                        let nodes, probed = walkTree (IO.Path.GetFileName tool) t (fun p -> runTool tool (p @ [ "--help" ])) flagsAt

                        let note =
                            if nodes.IsEmpty && probed > 0 then
                                $" (walked {probed} subcommand help(s), none yielded a case)"
                            else
                                ""

                        source, top, nodes, [], note
                    | _ -> source, top, [], [], ""

            // GNU getopt's unique-prefix longs [D:sig-conventions]: the
            // tool answers `--versio` exactly as `--version`. Asked only of
            // a help that spells values getopt_long's way (`--file=ARCHIVE`)
            // — a tool that launches on an unknown flag (an editor) is
            // never handed one
            let abbreviations =
                convention = "gnu"
                && not cobra
                && not isGit
                && System.Text.RegularExpressions.Regex.IsMatch(help, "--[a-z][a-z0-9-]*=[A-Z<\\[]")
                && (match version with
                    | Some v ->
                        match runTool tool [ "--versio" ] with
                        | Some out ->
                            let first = out.Split('\n') |> Array.tryFind (fun l -> l.Trim() <> "") |> Option.defaultValue ""
                            System.Text.RegularExpressions.Regex.Replace(first.Trim(), "\\s+", " ") = v
                        | None -> false
                    | None -> false)

            // V8 documents `--no-flag` for every bool flag, node for its own
            let negatable = fullHelp |> List.exists (fun (m, _) -> m = "--v8-options")

            // a row spelled `-<signal>` (kill): any dash word is a value
            let dashWords =
                convention = "gnu" && System.Text.RegularExpressions.Regex.IsMatch(help, "(?m)^\\s*-<[A-Za-z][\\w-]*>")

            // a refused help still states its usage (bunx exits 1 on --help)
            let topStop =
                advertised.IsEmpty
                && stopsAtOperand (IO.Path.GetFileName tool) (topHelp |> Option.orElse harvestOnly |> Option.defaultValue "")

            match flags, nodes with
            | [], [] ->
                // the file #sig reads, as it names it [D:sig-probe-bounds]
                let slow =
                    match slowProbes |> Seq.distinct |> List.ofSeq with
                    | [] -> ""
                    | ps ->
                        let named = ps |> List.map (fun a -> $"'{tool} {a}'") |> String.concat ", "
                        $"; stopped at {Contracts.probeTimeoutMs / 1000} s: {named}"

                Error
                    $"'{tool}': found no flags to record (probed: completion fish, shipped fish files, --help{slow}) — write .weir/sigs/{sigFileName tool} by hand"
            | flags, nodes ->
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

                // one record body [D:sig-arity]: a field per spelling group.
                // The long names the field (Wire when its camel cannot
                // round-trip, Flag suffix on a keyword), the first short
                // rides [<Short>]; every further spelling — a second
                // short, `-h`, a short-only row, a Go or find word — is its
                // own field with a dash Wire. Shorts and words dedup within
                // the record (per case — docker's -a on all and all-tags
                // live apart); the arity is the field's type.
                let renderRecord (name: string) (flags: Flag list) =
                    line $"type {name} = {{"
                    let names = System.Collections.Generic.HashSet<string>()
                    let taken = System.Collections.Generic.HashSet<string>()

                    let uniq (n: string) =
                        let n = if Set.contains n Weir.Parser.keywords then n + "Flag" else n
                        let mutable c = n
                        let mutable i = 2

                        while not (names.Add c) do
                            c <- $"{n}{i}"
                            i <- i + 1

                        c

                    let ty =
                        function
                        | Some AOne -> "string"
                        | Some AOpt -> "Option<string>"
                        | Some ARest -> "seq<string>"
                        | _ -> "bool"

                    let field (doc: string option) (attrs: string list) (fname: string) (arity: SigArity option) =
                        match doc with
                        | Some d -> line $"    /// {escape d}"
                        | None -> ()

                        if not attrs.IsEmpty then
                            let joined = String.concat "; " attrs
                            line $"    [<{joined}>]"

                        line $"    {fname}: {ty arity}"

                    let spellingName (sp: string) =
                        let bare = sp.TrimStart '-'

                        if bare.Length = 1 then
                            if System.Char.IsLetterOrDigit bare[0] then "short" + bare else $"shortX{int bare[0]:X}"
                        else
                            let camel = fieldName bare
                            if camel <> "" && System.Char.IsLetter camel[0] then camel else "dashWord"

                    // same-camel spellings share a field (claude's
                    // --allowedTools and --allowed-tools): the kebab one is
                    // the field's own, the other rides its one Wire
                    let twins =
                        flags
                        |> List.filter (fun f -> f.Long <> "")
                        |> List.groupBy (fun f -> fieldName f.Long)
                        |> List.choose (fun (camel, g) ->
                            match g with
                            | [ _; _ ] when not (Set.contains camel Weir.Parser.keywords) ->
                                match g |> List.partition (fun f -> f.Long = Weir.Argv.kebabFlag camel) with
                                | [ own ], [ other ] -> Some(own.Long, other)
                                | _ -> None
                            | _ -> None)
                        |> Map.ofList

                    let twinned = twins |> Map.values |> Seq.map (fun f -> f.Long) |> Set.ofSeq

                    let ordered =
                        flags
                        |> List.filter (fun f -> not (Set.contains f.Long twinned))
                        |> List.sortBy (fun f -> (if f.Long <> "" then f.Long else keyOf f).TrimStart('-').ToLowerInvariant(), keyOf f)

                    for f in ordered do
                        let twin = Map.tryFind f.Long twins

                        let f =
                            match twin with
                            | Some t ->
                                { f with
                                    Shorts = f.Shorts @ t.Shorts
                                    Literals = f.Literals @ t.Literals
                                    Doc = f.Doc |> Option.orElse t.Doc }
                            | None -> f

                        let shorts = f.Shorts |> List.filter (fun s -> taken.Add("-" + s))
                        let words = f.Literals |> List.filter (fun w -> taken.Add w)

                        let extra =
                            if f.Long <> "" && taken.Add("--" + f.Long) then
                                let camel = fieldName f.Long
                                let fname = uniq (if camel <> "" && System.Char.IsLetter camel[0] then camel else "flag" + camel)
                                let covered = Weir.Argv.kebabFlag fname

                                let attrs =
                                    match twin with
                                    | Some t when taken.Add("--" + t.Long) -> [ $"Wire \"{escape t.Long}\"" ]
                                    | _ -> if covered <> f.Long then [ $"Wire \"{escape f.Long}\"" ] else []

                                match shorts with
                                | s :: rest when s <> "h" && s.Length = 1 && System.Char.IsLetterOrDigit s[0] ->
                                    field f.Doc (attrs @ [ $"Short \"{s}\"" ]) fname f.Arity
                                    rest
                                | _ ->
                                    field f.Doc attrs fname f.Arity
                                    shorts
                            else
                                shorts

                        let docOnce = if f.Long = "" then f.Doc else None

                        for s in extra do
                            field docOnce [ $"Wire \"-{escape s}\"" ] (uniq (spellingName s)) f.Arity

                        for w in words do
                            field docOnce [ $"Wire \"{escape w}\"" ] (uniq (spellingName w)) f.Arity

                    line "}"

                // a case name from a sub path ("issue list" ->
                // IssueList): PascalCase of the camel of the kebab-joined
                // tokens — kebabFlag round-trips it to the key the
                // checker builds from the line's own words
                let caseName (path: string) =
                    let camel = fieldName (path.Replace(" ", "-"))
                    let pascal = string (System.Char.ToUpper camel[0]) + camel.Substring 1
                    if pascal = "Cmd" || pascal = "Globals" then pascal + "Cmd" else pascal

                line $"module {moduleName}"

                line (
                    "/// generated from '"
                    + (Eval.sanitizeTtyEcho tool).Replace("\n", "\\x0a")
                    + "' on "
                    + System.DateTime.UtcNow.ToString "yyyy-MM-dd"
                )

                let fullSource = source + (if nodes.IsEmpty then "" else "+subs") + walkNote
                line $"/// source: {fullSource} — a scraped surface may be incomplete (partial"
                line "/// by default; add `let exhaustive = true` once verified by hand)"

                // a probe whose output passed the byte bound was read in
                // part [D:sig-probe-bounds]
                let cutNote =
                    match cutProbes |> Seq.distinct |> List.ofSeq with
                    | [] -> ""
                    | cuts ->
                        let mib = Contracts.probeMaxBytes / (1024 * 1024)
                        let named = cuts |> List.map (fun a -> $"'{tool} {a}'") |> String.concat ", "
                        $"output cut at {mib} MiB per stream: {named}"

                if cutNote <> "" then
                    line $"/// {escape cutNote} — the rest was not read"

                // a probe stopped at the time bound was read up to the stop
                // [D:sig-probe-bounds]
                let slowNote =
                    match slowProbes |> Seq.distinct |> List.ofSeq with
                    | [] -> ""
                    | slow ->
                        let secs = Contracts.probeTimeoutMs / 1000
                        let named = slow |> List.map (fun a -> $"'{tool} {a}'") |> String.concat ", "
                        $"stopped at {secs} s: {named}"

                if slowNote <> "" then
                    line $"/// {escape slowNote} — what it printed by then was read"

                if nodes.IsEmpty then
                    line "/// flat surface: flags checked across the whole line — split into"
                    line "/// subcommand records by hand if the tool warrants it"
                else
                    line "/// scoped surface [D:scoped-sigs]: the LONGEST matching subcommand"
                    line "/// path picks the case (issue list beats issue); a case checks its"
                    line "/// own flags and the Globals; an unlisted subcommand is not checked"

                match version with
                | Some v -> line $"let version = \"{escape v}\""
                | None ->
                    line
                        "// no `let version`: the tool answers neither --version nor the version subcommand, so there is no identity to record"

                // the REPL's Tab may ask the tool for values
                // [D:sig-dynamic-complete]; the line is the user's to drop
                match source with
                | "cobra-complete" ->
                    line "/// the REPL's Tab asks the tool for values (`__complete`); honored from"
                    line "/// the user sig dir only (weir add sig --global) — delete to turn off"
                    line "let dynamic = \"cobra\""
                | "git-helper" ->
                    line "/// the REPL's Tab lists refs and remotes (weir's own read-only git"
                    line "/// commands) — delete to turn off"
                    line "let dynamic = \"git\""
                | _ -> ()

                // the convention lets [D:sig-conventions]
                if convention <> "gnu" then
                    line $"let convention = \"{convention}\""

                if abbreviations then
                    line "/// the tool takes a unique prefix of a long flag (`--verb` for `--verbose`)"
                    line "let abbreviations = true"

                if negatable then
                    line "/// `--no-x` turns off any switch `--x`"
                    line "let negatable = true"

                if dashWords then
                    line "/// a dash word no short spells is a value (the help's `-<signal>`)"
                    line "let dashWords = true"

                // the marker that this is no older all-bool signature
                // [D:sig-help-shapes]: its bool fields were read as switches
                if convention = "gnu" then
                    line "/// a multi-letter single-dash word is a bundle of shorts (`-xzf`)"
                    line "let bundles = true"

                let caseKey (path: string list) = String.concat " " path

                let stops =
                    [ if topStop && nodes.IsEmpty then
                          yield ""
                      for n in nodes do
                          if n.Stop then
                              yield caseKey n.Path

                              for a in n.Aliases do
                                  yield caseKey a ]
                    // an alias can repeat a case or another alias
                    |> List.distinct

                if not stops.IsEmpty then
                    line "/// the first operand of these ends the tool's own options (what follows is"
                    line "/// handed on — a container's command, a script's arguments)"
                    let joined = stops |> List.map (fun s -> $"\"{escape s}\"") |> String.concat "; "
                    line $"let stopAtOperand = [ {joined} ]"

                if unchecked = [ "" ] then
                    line "/// the tool takes any config key as a flag (its help: `--key=value`):"
                    line "/// no flag on its lines is checked [D:sig-open-tool]"
                    line "let unchecked = [ \"\" ]"
                elif not unchecked.IsEmpty then
                    line "/// these pass options the source cannot list (git's revision options):"
                    line "/// reaching one ends the check"
                    let joined = unchecked |> List.map (fun s -> $"\"{escape s}\"") |> String.concat "; "
                    line $"let unchecked = [ {joined} ]"

                if nodes.IsEmpty then
                    renderRecord "Cmd" flags
                else
                    // globals once [D:sig-sources]: the top level's flags
                    // are checked at every level, so no case repeats them;
                    // a help-walked case also carries its ancestors' own
                    // flags (`jira issue list` takes issue's), a cobra case
                    // lists its inherited ones itself
                    let globalKeys = flags |> List.map keyOf |> Set.ofList
                    let byPath = nodes |> List.map (fun n -> n.Path, n.Flags) |> Map.ofList

                    let walked = nodes |> List.map (fun n -> caseKey n.Path) |> Set.ofList

                    let cases =
                        nodes
                        |> List.distinctBy (fun n -> caseName (caseKey n.Path))
                        |> List.map (fun n ->
                            let ancestors =
                                if cobra || isGit then
                                    []
                                else
                                    [ for k in n.Path.Length - 1 .. -1 .. 1 do
                                          match Map.tryFind (List.truncate k n.Path) byPath with
                                          | Some fs -> yield! fs
                                          | None -> () ]

                            let own =
                                mergeFlags [ n.Flags; ancestors ]
                                |> List.filter (fun f -> not (Set.contains (keyOf f) globalKeys))

                            n, caseName (caseKey n.Path), own)

                    if not flags.IsEmpty then
                        renderRecord "Globals" flags

                    for _, cn, own in cases do
                        if not own.IsEmpty then
                            renderRecord $"{cn}Flags" own

                    line "type Cmd ="

                    let aliasCases =
                        [ for n, cn, own in cases do
                              for a in n.Aliases do
                                  if not (Set.contains (caseKey a) walked) then
                                      yield caseName (caseKey a), cn, own ]
                        |> List.distinctBy (fun (an, _, _) -> an)
                        |> List.filter (fun (an, _, _) -> cases |> List.forall (fun (_, cn, _) -> cn <> an))

                    for an, cn, own in aliasCases do
                        if own.IsEmpty then
                            line $"    | {an}"
                        else
                            line $"    | {an} of {cn}Flags"

                    for _, cn, own in cases do
                        // the matching key is the kebab of the case — it
                        // must round-trip to the sub token the user types;
                        // a case with no flags of its own takes the Globals
                        if own.IsEmpty then
                            line $"    | {cn}"
                        else
                            line $"    | {cn} of {cn}Flags"

                let text = sb.ToString()

                // validate before write [D:add-validates]: the generated
                // signature must itself load as one; nothing persists if
                // it does not
                let tmp =
                    IO.Path.Combine(IO.Path.GetTempPath(), $"weir-siggen-{System.Guid.NewGuid():N}.weir")

                Contracts.ProbeGuard.trackPath tmp

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
                        let versionNote =
                            match version with
                            | Some v -> v
                            | None -> $"none — '{tool}' answers neither --version nor version"

                        let surfaceNote =
                            if nodes.IsEmpty then
                                $"{flags.Length} flag(s)"
                            else
                                let total =
                                    (flags @ (nodes |> List.collect (fun n -> n.Flags)))
                                    |> List.map keyOf
                                    |> List.distinct
                                    |> List.length

                                $"{total} flag(s) across {nodes.Length} subcommand case(s)"

                        match globalDir with
                        | Some dir ->
                            // the user dir [D:sig-arg-complete]: no lock — a
                            // user-level sig serves the REPL's Tab only, so
                            // verify/restore never see it and no #sig reads it
                            let dest = IO.Path.Combine(dir, sigFileName tool)
                            IO.Directory.CreateDirectory dir |> ignore
                            Contracts.AtomicFile.replaceFile dest (Text.UTF8Encoding(false).GetBytes text)

                            let cutTail =
                                [ cutNote; slowNote ] |> List.filter ((<>) "") |> List.map (fun n -> "; " + n) |> String.concat ""

                            Ok
                                $"added sig {tool} to {dest} ({surfaceNote}, source: {fullSource}, version: {versionNote}{cutTail}) — user-level: the REPL's Tab reads it in every directory; no lock entry"
                        | None ->
                        let rel = "sigs/" + sigFileName tool

                        Contracts.AtomicFile.withWeirDirLock weirDir (fun () ->
                            match Contracts.readLock weirDir with
                            | Error e -> Error e
                            | Ok entries0 ->
                            // a file another lock entry records under this name
                            // (a legacy name: `a b` wrote sigs/a_b.weir) is that
                            // tool's signature — never overwritten: it moves to
                            // its owner's own name first, its lock entry with it
                            // [D:sig-probe-bounds]
                            let moved =
                                match
                                    entries0
                                    |> List.tryFind (fun e -> e.Kind = "sig" && e.Name <> tool && e.Path.Replace('\\', '/') = rel)
                                with
                                | None -> Ok(entries0, "")
                                | Some owner ->
                                    let ownName = sigFileName owner.Name
                                    let src = IO.Path.Combine(weirDir, "sigs", sigFileName tool)
                                    let dst = IO.Path.Combine(weirDir, "sigs", ownName)
                                    let shown = Eval.sanitizeTtyEcho owner.Name

                                    if IO.File.Exists dst then
                                        Error
                                            $"{rel} is the signature of '{shown}' (the lock records it under the name it had before file names were injective), and sigs/{ownName} exists too — move or remove one by hand"
                                    else
                                        try
                                            if IO.File.Exists src then
                                                IO.File.Move(src, dst)

                                            Ok(
                                                entries0
                                                |> List.map (fun e ->
                                                    if e = owner then
                                                        { e with Path = IO.Path.Combine("sigs", ownName) }
                                                    else
                                                        e),
                                                $"; moved the signature of '{shown}' from {rel} to sigs/{ownName}"
                                            )
                                        with ex ->
                                            Error $"{rel} is the signature of '{shown}', and moving it to sigs/{ownName} failed: {ex.Message}"

                            match moved with
                            | Error e -> Error e
                            | Ok(entries, movedNote) ->
                                let dest = IO.Path.Combine(weirDir, "sigs", sigFileName tool)
                                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName dest) |> ignore
                                Contracts.AtomicFile.replaceFile dest (Text.UTF8Encoding(false).GetBytes text)
                                let bytes = IO.File.ReadAllBytes dest

                                let entry: Contracts.LockEntry =
                                    { Kind = "sig"
                                      Name = tool
                                      Url = "generated:" + source + (if nodes.IsEmpty then "" else "+subs")
                                      Sha256 = Contracts.sha256Hex bytes
                                      Path = IO.Path.Combine("sigs", sigFileName tool)
                                      Version = version
                                      Closure = None }

                                let others = entries |> List.filter (fun e -> not (e.Kind = "sig" && e.Name = tool))

                                Contracts.writeLock weirDir (others @ [ entry ])

                                // this tool's file under the legacy name is
                                // superseded — unless another entry owns that
                                // name [D:sig-probe-bounds]
                                let legacy = "sigs/" + legacySigFileName tool

                                if legacy <> "sigs/" + sigFileName tool
                                   && entries |> List.exists (fun e -> e.Kind = "sig" && e.Name = tool && e.Path.Replace('\\', '/') = legacy)
                                   && not (others |> List.exists (fun e -> e.Path.Replace('\\', '/') = legacy)) then
                                    try
                                        IO.File.Delete(IO.Path.Combine(weirDir, legacy))
                                    with _ ->
                                        ()

                                let cutTail =
                                    ([ cutNote; slowNote ] |> List.filter ((<>) "") |> List.map (fun n -> "; " + n) |> String.concat "")
                                    + movedNote

                                Ok
                                    $"added sig {tool} ({surfaceNote}, source: {fullSource}, version: {versionNote}{cutTail}) — partial by default; verify and mark exhaustive by hand")
                finally
                    try
                        IO.File.Delete tmp
                    with _ ->
                        ()

                    Contracts.ProbeGuard.untrackPath tmp

    /// generate into the project's .weir/sigs/ and lock it
    let generate (weirDir: string) (tool: string) : Result<string, string> = generateTo None weirDir tool

// ---- the init file [D:repl-init] ------------------------------------------
// ONE home for every static init rule [D:init-check-one-home] — `weir
// check`, the LSP and the REPL loader all run checkInit, so check refuses
// exactly what the loader refuses. The loader keeps only what needs
// evaluation (a cwd that exists, a logLevel that parses, an echoCap in
// range).

// a command-head alias [D:command-head-alias]: (real exe, fixed prefix
// args). Single-hop by construction — the exe is a real program name,
// never another alias (define-time resolution rejects alias-of-alias).
type CommandAlias = { Exe: string; Prefix: string list }

/// parse a `#alias name = cmd [args...]` directive line [D:command-head-alias].
/// The head token maps to (exe, prefix args); argv is split on whitespace
/// (a fixed prefix is literal words — no quoting/typed-argv machinery: an
/// alias is a resolution-table entry, not a script line). Returns the name
/// and its target, or a message. Single-hop is enforced later against the
/// whole table.
let parseAliasLine (body: string) : Result<string * CommandAlias, string> =
    // body is the text after `#alias`
    match
        body.Trim()
        |> fun s -> System.Text.RegularExpressions.Regex.Match(s, @"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$")
    with
    | m when m.Success && Parser.isKeyword m.Groups.[1].Value ->
        // a keyword heads its own statement form, never a command — the
        // alias could never resolve
        let name = m.Groups.[1].Value
        Error $"#alias {name}: '{name}' is a keyword — a line starting with it is never a command; pick another name"
    | m when m.Success ->
        let name = m.Groups.[1].Value

        let rhsWords =
            m.Groups.[2].Value.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        // an alias target is one program plus literal words — a chaining
        // glyph there would reach the program as an argument, never chain
        // [D:alias-no-chain]
        let chainGlyph =
            rhsWords |> List.tryFind (fun w -> List.contains w [ "|"; "|>"; "||"; "&&"; ";" ])

        match rhsWords with
        | [] -> Error "#alias needs a target: '#alias name = command [args...]'"
        | _ when chainGlyph.IsSome ->
            let g = chainGlyph.Value

            Error
                $"#alias {name}: '{g}' in an alias target is a literal argument, not a chain — an alias is one program plus fixed words; for a pipeline or a chain write a function: let {name} () = …"
        | exe :: prefix ->
            // `#alias ls = ls` (or `= ls --color`) is a legal shadow (bash
            // allows it), bypassable with `^ls` — register as-is
            Ok(name, { Exe = exe; Prefix = prefix })
    | _ -> Error "malformed #alias — usage: '#alias name = command [args...]' (e.g. '#alias k = kubectl')"

/// one wording for every consumer [D:repl-init]
let sessionDeclRefMessage (name: string) : string =
    $"a #session value cannot use the file's own declarations ('{name}') — settings are read before the declarations bind; only prompt may name one. Write the value inline"

let knownSessionKeys = Set [ "cwd"; "env"; "logLevel"; "echoCap"; "prompt" ]

let expectedFieldTypes (key: string) : Ty list =
    match key with
    | "cwd"
    | "logLevel" -> [ TStr ]
    | "echoCap" -> [ TInt ]
    | "env" -> [ TSeq(TTuple [ TStr; TStr ]) ]
    | "prompt" -> [ TStr; TFun(TUnit, TStr); TFun(TNamed("PromptStatus", []), TStr) ]
    | _ -> []

/// the init file split by role, physical line numbers kept: the #session
/// fields as synthesized lets (the prompt field apart — it checks after the
/// declarations [D:session-prompt]), the declarations with every directive
/// line blanked (transparent, so positions stay real), the #alias bodies,
/// and the lines the #session block spans
type InitSplit =
    { Fields: (int * string) list
      PromptFields: (int * string) list
      Decls: (int * string) list
      Aliases: (int * string) list
      BlockLines: Set<int> }

/// the checked init: every static error (Diags — init rules and the
/// #session values' own errors; DeclDiags — the declarations' own statement
/// errors, kept apart because check already reports those from the full
/// file), plus what the loader applies
type InitCheck =
    { Diags: Diagnostic list
      DeclDiags: Diagnostic list
      Split: InitSplit
      Settings: (LogicalLine * string * Check.TypedExpr) list
      Decls: (LogicalLine * CheckedStatement) list
      DeclEnv: TypeEnv
      Prompt: (LogicalLine * Check.TypedExpr) option
      Aliases: (int * string * CommandAlias) list }

let private initError (file: string) (line: int) (col: int) (endCol: int option) (code: string) (msg: string) =
    { File = file
      Line = line
      Col = col
      EndLine = endCol |> Option.map (fun _ -> line)
      EndCol = endCol
      Severity = "error"
      Code = code
      Message = msg }

let splitInit (file: string) (lines: string list) : InitSplit * Diagnostic list =
    let mutable fields: (int * string) list = []
    // the prompt field rides separately [D:session-prompt]: its value
    // names init declarations, so it checks after they bind — every
    // other field checks before them (settings before names)
    let mutable promptFields: (int * string) list = []
    let mutable inPromptField = false
    let mutable rest: (int * string) list = []
    // #alias directive lines, collected with their (1-based) line number;
    // the text is what follows `#alias` [D:command-head-alias]
    let mutable aliases: (int * string) list = []
    let mutable blockLines: Set<int> = Set.empty
    let mutable inBlock = false
    let mutable seen = false
    let diags = ResizeArray<Diagnostic>()
    let arr = List.toArray lines

    for i in 0 .. arr.Length - 1 do
        let l = arr.[i]
        let t = l.Trim()

        if not inBlock && t = "#init" then
            rest <- (i + 1, "") :: rest
        elif not inBlock && (t = "#alias" || t.StartsWith "#alias ") then
            // a top-level directive line; kept out of the declaration
            // stream and replaced by a blank so declaration diagnostics keep
            // their real positions (the #session discipline)
            aliases <- (i + 1, t.Substring("#alias".Length)) :: aliases
            rest <- (i + 1, "") :: rest
        elif not inBlock && t.StartsWith "#session" then
            if seen then
                diags.Add(initError file (i + 1) 1 None "session-shape" "a second #session block — the init takes one")

            seen <- true

            if t = "#session {" then
                inBlock <- true
                blockLines <- Set.add (i + 1) blockLines
            else
                diags.Add(
                    initError
                        file
                        (i + 1)
                        1
                        None
                        "session-shape"
                        "malformed #session — usage: '#session {' then 'key = value' lines then '}'"
                )

            rest <- (i + 1, "") :: rest
        elif inBlock && t = "}" then
            inBlock <- false
            blockLines <- Set.add (i + 1) blockLines
            rest <- (i + 1, "") :: rest
        elif inBlock then
            blockLines <- Set.add (i + 1) blockLines

            if t <> "" then
                // a field-start (`key = …`) dedents 4 and gains a 'let '
                // prefix — 4 chars out, 4 in, columns survive. Every
                // other line (list entries, closers) passes through
                // untouched: still indented, still a continuation of the
                // synthesized let, columns exact
                let fieldStart =
                    System.Text.RegularExpressions.Regex.Match(t, "^([A-Za-z_][A-Za-z0-9_]*)\s*=")

                // a continuation line belongs to the field it follows
                if fieldStart.Success then
                    inPromptField <- fieldStart.Groups.[1].Value = "prompt"

                let synthesized =
                    if fieldStart.Success then
                        "let " + (if l.StartsWith "    " then l.Substring 4 else t)
                    else
                        l

                if inPromptField then
                    promptFields <- (i + 1, synthesized) :: promptFields
                else
                    fields <- (i + 1, synthesized) :: fields

            rest <- (i + 1, "") :: rest
        else
            rest <- (i + 1, l) :: rest

    if inBlock then
        diags.Add(
            initError file (max 1 arr.Length) 1 None "session-shape" "unclosed #session block — a lone '}' line ends it"
        )

    { Fields = List.rev fields
      PromptFields = List.rev promptFields
      Decls = List.rev rest
      Aliases = List.rev aliases
      BlockLines = blockLines },
    List.ofSeq diags

let checkInit (mkR: TypeEnv -> Parser.Resolver) (file: string) (lines: string list) : InitCheck =
    let split, shapeDiags = splitInit file lines
    // the init file is a script, not interactive use: bare module members
    // are refused here as in any script — the session gets them, the file
    // does not [D:init-check-one-home]
    let strictTenv, _ = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv
    let diags = ResizeArray<Diagnostic>(shapeDiags)
    let declDiags = ResizeArray<Diagnostic>()

    // the marker is required [D:init-marker]
    if not (isInitMarked lines) then
        diags.Add(initError file 1 1 None "init-marker" "the init file declares itself — put #init on the first line")

    // imports resolve against the init file's real location [D:init-import]
    let importer = fileImportLoader mkR (importAnchor file)

    let stmtDiag (d: StmtDiag) =
        { File = d.File |> Option.defaultValue file
          Line = d.PhysLine
          Col = d.PhysCol
          EndLine = d.PhysEnd |> Option.map fst
          EndCol = d.PhysEnd |> Option.map snd
          Severity = "error"
          Code = codeOf d.Parse d.Message
          Message = d.Message }

    let assembleOrReport (numbered: (int * string) list) (into: ResizeArray<Diagnostic>) =
        match assemble numbered with
        | Ok lls -> Some lls
        | Error msg ->
            match assemblyErrorAt numbered msg with
            | Some(line, col, rest) -> into.Add(initError file line col None "layout" rest)
            | None ->
                let line = numbered |> List.tryHead |> Option.map fst |> Option.defaultValue 1
                into.Add(initError file line 1 None "layout" msg)

            None

    // the #alias table [D:command-head-alias] — single-hop: a target naming
    // a different alias is refused (a self-shadow, `#alias ls = ls --color`,
    // targets the real binary and is legal)
    let parsedAliases =
        split.Aliases
        |> List.choose (fun (lineNo, body) ->
            match parseAliasLine body with
            | Error msg ->
                diags.Add(initError file lineNo 1 None "init-alias" msg)
                None
            | Ok(name, alias) -> Some(lineNo, name, alias))

    let aliasNames = parsedAliases |> List.map (fun (_, n, _) -> n) |> Set.ofList

    for lineNo, name, alias in parsedAliases do
        if alias.Exe <> name && Set.contains alias.Exe aliasNames then
            diags.Add(
                initError
                    file
                    lineNo
                    1
                    None
                    "init-alias"
                    $"#alias {name} = {alias.Exe} …: an alias resolves to a program, not to another alias ('{alias.Exe}' is itself an alias) — aliases are single-hop"
            )

    // the declarations — the module rule, applied to the prompt; checking
    // stops at the first statement error (later ones would echo it)
    let declsChecked, declEnv, declsComplete =
        match
            (if shapeDiags.IsEmpty then
                 assembleOrReport split.Decls declDiags
             else
                 None)
        with
        | None -> [], strictTenv, false
        | Some lls ->
            let rec go env acc rest =
                match rest with
                | [] -> List.rev acc, env, true
                | (ll: LogicalLine) :: tail ->
                    match checkStatement false None mkR importer env ll with
                    | Error d ->
                        declDiags.Add(stmtDiag d)
                        List.rev acc, env, false
                    | Ok chk -> go chk.Env ((ll, chk) :: acc) tail

            go strictTenv [] lls

    // a lazy command let binds and spawns nothing; forcing one at load is
    // refused [D:lazy-command-lets]
    let mutable cmdBindings: Map<string, int> = Map.empty

    for ll, chk in declsChecked do
        let refuse msg =
            diags.Add(initError file ll.Head 1 None "init-decl" msg)

        match chk.Kind with
        | KType _
        | KImport _
        | KSig _ -> ()
        | KLet(_, _, te)
        | KLetPat(_, _, te) ->
            match Check.commandRunsNow cmdBindings te, chk.Kind with
            | Some why, _ -> refuse $"an init 'let' cannot run a command at load: {why}"
            | None, KLet(name, _, _) -> cmdBindings <- Check.recordCommandBinding cmdBindings name te
            | None, _ -> ()
        | KCmd _
        | KExpr _ -> refuse "the init file is declaration-only — 'type' and 'let'; it configures the prompt, it cannot run"
        | KModule _ -> refuse "the init file is not a module — no 'module' marker; its names are the prompt's"

    let declNames =
        declsChecked
        |> List.collect (fun (_, chk) ->
            match chk.Kind with
            | KLet(n, _, _) -> [ n ]
            | KLetPat(p, _, _) -> Check.patNameSpans p |> List.map fst
            | _ -> [])
        |> Set.ofList

    // an alias naming a declaration is dead on arrival
    // [D:alias-binding-shadow]: the binding shadows the table
    for lineNo, name, _ in parsedAliases do
        if Set.contains name declNames then
            diags.Add(
                initError
                    file
                    lineNo
                    1
                    None
                    "init-alias"
                    $"#alias {name}: '{name}' is also declared in this file — the binding shadows the alias, so it would never resolve; pick another name"
            )

    let rawArr = List.toArray lines

    let keyCol (lineNo: int) =
        if lineNo >= 1 && lineNo <= rawArr.Length then
            rawArr[lineNo - 1].Length - rawArr[lineNo - 1].TrimStart().Length + 1
        else
            1

    // the value's column: past `key =` on the field's head line
    let valCol (lineNo: int) (key: string) =
        if lineNo >= 1 && lineNo <= rawArr.Length then
            let m =
                System.Text.RegularExpressions.Regex.Match(rawArr[lineNo - 1], @"^\s*[A-Za-z_][A-Za-z0-9_]*\s*=\s*")

            if m.Success then m.Length + 1 else keyCol lineNo + key.Length
        else
            1

    // the #session fields — settings before names: each checks in the bare
    // env, so naming a declaration is unbound; say why
    let settings =
        match
            (if shapeDiags.IsEmpty then
                 assembleOrReport split.Fields diags
             else
                 None)
        with
        | None -> []
        | Some lls ->
            let mutable env = strictTenv
            let mutable seen: Set<string> = Set.empty
            let acc = ResizeArray<LogicalLine * string * Check.TypedExpr>()

            for ll in lls do
                match checkStatement false None mkR importer env ll with
                | Error d ->
                    let d' = stmtDiag d

                    let unboundDecl =
                        System.Text.RegularExpressions.Regex.Match(d.Message, @"^unbound variable '([^']+)'")

                    if unboundDecl.Success && Set.contains unboundDecl.Groups[1].Value declNames then
                        let n = unboundDecl.Groups[1].Value

                        diags.Add
                            { d' with
                                Code = "session-names-declaration"
                                EndLine = Some d'.Line
                                EndCol = Some(d'.Col + n.Length)
                                Message = sessionDeclRefMessage n }
                    else
                        diags.Add d'
                | Ok chk ->
                    env <- chk.Env

                    match chk.Kind with
                    | KLet(key, _, te) ->
                        let kc = keyCol ll.Head
                        let vc = valCol ll.Head key

                        if not (Set.contains key knownSessionKeys) then
                            let hint = didYouMean key knownSessionKeys

                            diags.Add(
                                initError
                                    file
                                    ll.Head
                                    kc
                                    (Some(kc + key.Length))
                                    "session-key"
                                    $"unknown #session key '{key}'{hint} (the keys: cwd, env, logLevel, echoCap, prompt)"
                            )
                        elif Set.contains key seen then
                            diags.Add(
                                initError
                                    file
                                    ll.Head
                                    kc
                                    (Some(kc + key.Length))
                                    "session-key"
                                    $"duplicate #session key '{key}'"
                            )
                        elif Check.runsCommandT te then
                            diags.Add(
                                initError
                                    file
                                    ll.Head
                                    vc
                                    None
                                    "session-command"
                                    "a #session value cannot run a command — settings are read, not computed by running things"
                            )
                        else
                            seen <- Set.add key seen

                            let typeMsg =
                                match key, te.Ty with
                                | ("cwd" | "logLevel"), TStr
                                | "echoCap", TInt
                                | "env", TSeq(TTuple [ TStr; TStr ]) -> None
                                // a generic value (an empty list -> seq<'a>)
                                // unifies at load — not a mismatch
                                | _, ty when (formatTy ty).Contains "'" -> None
                                | "cwd", ty -> Some $"cwd expects a string, got {formatTy ty}"
                                | "logLevel", ty -> Some $"logLevel expects a string, got {formatTy ty}"
                                | "echoCap", ty -> Some $"echoCap expects an int, got {formatTy ty}"
                                | _, ty -> Some $"env expects seq<string * string> — pairs, order kept; got {formatTy ty}"

                            match typeMsg with
                            | Some msg -> diags.Add(initError file ll.Head vc None "session-type" msg)
                            | None -> acc.Add(ll, key, te)
                    | _ ->
                        diags.Add(
                            initError file ll.Head 1 None "session-shape" "a #session block takes 'key = value' lines only"
                        )

            List.ofSeq acc

    // the prompt field last [D:session-prompt]: its value names the
    // declarations, so it checks in their env — only once they all checked
    // (a broken declaration would echo here as unbound)
    let prompt =
        if not declsComplete || split.PromptFields.IsEmpty then
            None
        else
            match assembleOrReport split.PromptFields diags with
            | None -> None
            | Some lls ->
                let checkedPrompt =
                    lls
                    |> List.map (fun ll -> ll, checkStatement false None mkR importer declEnv ll)

                checkedPrompt
                |> List.iteri (fun i (ll, r) ->
                    match r with
                    | Error d -> diags.Add(stmtDiag d)
                    | Ok _ when i > 0 ->
                        let kc = keyCol ll.Head

                        diags.Add(
                            initError file ll.Head kc (Some(kc + 6)) "session-key" "duplicate #session key 'prompt'"
                        )
                    | Ok chk ->
                        match chk.Kind with
                        | KLet("prompt", _, te) when Check.runsCommandT te ->
                            diags.Add(
                                initError
                                    file
                                    ll.Head
                                    (valCol ll.Head "prompt")
                                    None
                                    "session-command"
                                    "a #session value cannot run a command — name a function here (prompt = f); the command runs when the prompt calls f"
                            )
                        | KLet("prompt", _, te) ->
                            match te.Ty with
                            | TStr
                            | TFun(TUnit, TStr) -> ()
                            | TFun(dom, TStr) when promptStatusDomain dom -> ()
                            | ty ->
                                diags.Add(
                                    initError
                                        file
                                        ll.Head
                                        (valCol ll.Head "prompt")
                                        None
                                        "session-type"
                                        $"prompt expects a string, a unit -> string, or a PromptStatus -> string function (ok, a bare command's exit code, the entry's duration), got {formatTy ty}"
                                )
                        | _ ->
                            diags.Add(
                                initError
                                    file
                                    ll.Head
                                    1
                                    None
                                    "session-shape"
                                    "a #session block takes 'key = value' lines only"
                            ))

                match checkedPrompt with
                | (ll, Ok { Kind = KLet("prompt", _, te) }) :: _ -> Some(ll, te)
                | _ -> None

    let byPosition (ds: seq<Diagnostic>) =
        ds
        |> List.ofSeq
        |> List.sortWith (fun a b ->
            let byLine = compare a.Line b.Line
            if byLine <> 0 then byLine else compare a.Col b.Col)

    { Diags = byPosition diags
      DeclDiags = byPosition declDiags
      Split = split
      Settings = settings
      Decls = declsChecked
      DeclEnv = declEnv
      Prompt = prompt
      Aliases = parsedAliases }

/// the record types a file hands to `Args.load`/`Env.load` (type ->
/// loader), read off its parsed statements — an unbound field name's
/// hint teaches loading the record first. A line that does not parse
/// contributes nothing (its own error is reported where it is checked).
let loadTargetsOf (tenv: TypeEnv) (lines: LogicalLine list) : Map<string, string> =
    let rec walk (e: Expr) : (string * string) list =
        (match e.Kind with
         | EApp({ Kind = EField({ Kind = EVar(("Args" | "Env") as m) }, "load", _) }, { Kind = EVar t }) when
             t.Length > 0 && System.Char.IsUpper t[0]
             ->
             [ t, m + ".load" ]
         | _ -> [])
        @ (exprChildren e |> List.collect walk)

    lines
    |> List.collect (fun ll ->
        match Parser.parseLine (assumeResolver tenv) ll.Text with
        | Ok(SLet(_, e))
        | Ok(SLetPat(_, e))
        | Ok(SExpr e)
        | Ok(SCmd e) -> walk e
        | _ -> [])
    |> List.distinctBy fst
    |> Map.ofList

// ---- effects before the CLI parse [D:effect-before-args] ------------------
// A top-level statement BEFORE the entry script's first top-level
// `Args.load`/`Env.load` that writes the filesystem, spawns or touches the
// network still runs on `--help` or a bad argument. Advisory (a warning).
// The classification is Effects.effectClass's mutation side — console,
// exit and cd excluded — plus the network members; a function body is a
// definition (it does not run here), and the statement holding the load
// is never judged (a `within` wrapping the load is deliberate). A load
// only inside a function or a branch is a runtime fact: no judgement.

/// the effect a statement runs, as the message names it, and its span
let private firstRunEffect (isLetRhs: bool) (te: Check.TypedExpr) : (string * Span) option =
    let quiet =
        Set [ "print"; "printerr"; "Self.prompt"; "Term.title"; "exit"; "cd"; "|print" ]

    let memberEffect (n: string) =
        match n with
        | "Http.send"
        | "Http.expect"
        | "Net.portOpen" -> Some $"'{n}'"
        | _ when quiet.Contains n || n.StartsWith "Log." -> None
        | _ when Effects.isCommandReifier n -> None // the command is named at its program slot
        | _ ->
            match Effects.effectClass n with
            | Some Effects.Mutation -> Some $"'{n}'"
            | _ -> None

    let rec headOf (e: Check.TypedExpr) =
        match e.Kind with
        | Check.TEApp(g, _) -> headOf g
        | k -> k

    let rec argsOf (e: Check.TypedExpr) =
        match e.Kind with
        | Check.TEApp(g, a) -> argsOf g @ [ a ]
        | _ -> []

    let rec go (top: bool) (e: Check.TypedExpr) : (string * Span) option =
        match e.Kind with
        // a binding's lazy command spawns nothing until read
        // [D:lazy-command-lets]
        | Check.TECmd _ when top && isLetRhs -> None
        | Check.TECmd(Check.THeadLit prog, _, _) -> Some($"the command '{prog}'", e.Span)
        | Check.TECmd(Check.THeadDyn(d, _), _, _) -> Some($"the command '^{d}'", e.Span)
        // a reified command: the program rides the desugar's arguments
        | Check.TEApp _ when
            (match headOf e with
             | Check.TEVar h -> Effects.isCommandReifier h && h <> "|print"
             | _ -> false)
            ->
            // the program's slot in the desugar, as --can reads it
            let h =
                match headOf e with
                | Check.TEVar h -> h
                | _ -> ""

            let progArg =
                if h.StartsWith "|chain" then
                    match argsOf e |> List.tryItem (if h = "|chainOrFailed" then 1 else 0) with
                    | Some { Kind = Check.TEList({ Kind = Check.TETuple(p :: _) } :: _) } -> Some p
                    | _ -> None
                else
                    let slot =
                        if h.StartsWith "|orFailedEnv" then 2
                        elif h.StartsWith "|orFailed" then 1
                        elif h.EndsWith "Env" then 1
                        else 0

                    argsOf e |> List.tryItem slot

            let prog =
                progArg
                |> Option.bind (fun a ->
                    match a.Kind with
                    | Check.TEStr s -> Some(s, a.Span)
                    | _ -> None)

            match prog with
            | Some(p, span) -> Some($"the command '{p}'", span)
            | None -> Some("a command", e.Span)
        | Check.TEVar n -> memberEffect n |> Option.map (fun m -> m, e.Span)
        | Check.TELambda _
        | Check.TELambdaPat _ -> None
        | Check.TEWithin(kind, _, _, _, body) ->
            match kind with
            | WithinTmp -> Some("'within tmp'", e.Span)
            | WithinLock -> Some("'within lock'", e.Span)
            | WithinProc -> Some("'within proc'", e.Span)
            | WithinServe -> Some("'within serve'", e.Span)
            // a plan captures its mutations instead of running them
            | WithinPlan -> None
            | WithinCd
            | WithinEnv
            | WithinPure
            | WithinReadonly -> go false body
        | _ -> Check.childExprs e |> List.tryPick (go false)

    go true te

/// does a top-level statement run a typed boundary load unconditionally
/// (outside a function body or a branch)? Its message name if so.
let private topLevelLoad (te: Check.TypedExpr) : (string * Span) option =
    let named (prefix: string) (name: string) =
        if name.StartsWith "{" then prefix else $"{prefix} {name}"

    let rec go (e: Check.TypedExpr) =
        match e.Kind with
        | Check.TEArgsLoad target ->
            let name =
                match target with
                | Check.ArgsRecord d -> d.Name
                | Check.ArgsUnion(u, _) -> u.Name
                | Check.ArgsShared(o, _, _, _) -> o.Name

            Some(named "Args.load" name, e.Span)
        | Check.TEEnvLoad(def, _) -> Some(named "Env.load" def.Name, e.Span)
        | Check.TELambda _
        | Check.TELambdaPat _ -> None
        | Check.TEIf(cond, _, _) -> go cond
        | Check.TEMatch(scrut, _) -> go scrut
        | _ -> Check.childExprs e |> List.tryPick go

    go te

/// the warnings over a script's checked statements, in file order
let effectBeforeArgs (path: string) (stmts: (LogicalLine * CheckedKind) list) : Diagnostic list =
    let teOf (k: CheckedKind) =
        match k with
        | KLet(_, _, te) -> Some(true, te)
        | KLetPat(_, _, te) -> Some(true, te)
        | KCmd te
        | KExpr te -> Some(false, te)
        | _ -> None

    let indexed = stmts |> List.indexed

    let load =
        indexed
        |> List.tryPick (fun (i, (ll, k)) ->
            teOf k
            |> Option.bind (fun (_, te) -> topLevelLoad te)
            |> Option.map (fun (name, span) -> i, name, fst (translate ll span.Start.Col)))

    match load with
    | None -> []
    | Some(at, loadName, loadLine) ->
        let consequence =
            if loadName.StartsWith "Env.load" then
                "on a missing or bad variable it has already run; load the environment first"
            else
                "on --help or a bad argument it still runs; load the arguments first"

        indexed
        |> List.takeWhile (fun (i, _) -> i < at)
        |> List.choose (fun (_, (ll, k)) ->
            teOf k
            |> Option.bind (fun (isLet, te) -> firstRunEffect isLet te)
            |> Option.map (fun (what, span) ->
                let l, c = translate ll span.Start.Col
                let el, ec = translate ll span.End.Col

                { File = path
                  Line = l
                  Col = c
                  EndLine = Some el
                  EndCol = Some(if el = l then max (c + 1) ec else ec)
                  Severity = "warning"
                  Code = "effect-before-args"
                  Message = $"{what} runs before '{loadName}' (line {loadLine}) — {consequence}" }))

// the recovery pass [D:block-recovery], tooling-only: an errored
// statement is re-checked once for its tree, so the editor keeps
// answering inside it. The diagnostic stays the first pass's; the
// binder schemes are the hole schemes the Error branch binds, so the
// partial's kind and its env agree
let private recoverStatement
    (tenv: TypeEnv)
    (holeScheme: Scheme)
    (stmt: Stmt)
    : CheckedKind option * bool =
    let recovered (e: Expr) (wrap: Check.TypedExpr -> CheckedKind) =
        match Check.typecheckRecovering tenv e with
        | Check.RecoveredExpr(te, _) -> Some(wrap te), false
        | Check.NoRecovery -> None, false
        | Check.RecoveryBudgetExhausted -> None, true

    match stmt with
    | SLet(name, e) -> recovered e (fun te -> KLet(name, holeScheme, te))
    | SLetPat(pat, e) ->
        recovered e (fun te -> KLetPat(pat, Check.patNameSpans pat |> List.map (fun (n, _) -> n, holeScheme), te))
    | SCmd e -> recovered e KCmd
    // statement position demands unit — checkStatementCore's arming
    | SExpr e -> recovered (Check.armTail e) KExpr
    | SType _
    | SSig _
    | SModule _
    | SImport _ -> None, false

/// analyzeLines, with `asInit` forcing the init-file reading (the LSP's
/// canonical init path, marked or not [D:init-marker]). The fifth
/// product is the partials [D:block-recovery]: statements that did NOT
/// check, with their recovered trees — never among `stmts`, so every
/// diagnostic consumer sees exactly the checked ones
let analyzeLinesAs
    (asInit: bool)
    (path: string)
    (rawLines: string list)
    : Diagnostic list *
      (LogicalLine * CheckedStatement) list *
      TypeEnv *
      LogicalLine list *
      (LogicalLine * CheckedStatement) list
    =
    // an #init-marked file checks as the init it is [D:init-marker]:
    // directives rewritten for the full-file pass (hover, completion,
    // statement errors), its declarations are session exports — the
    // unused-binding judgement does not apply (the loader never judges
    // it) — and checkInit adds the loader's own rules
    let initMarked = asInit || isInitMarked rawLines

    // the #session lines are checkInit's: the full-file pass checks their
    // rewritten lets in file order, which is not the loader's (settings
    // before names, prompt after the declarations) — its errors there give
    // way
    let withInitRules (ds: Diagnostic list) : Diagnostic list =
        if initMarked then
            let ic = checkInit assumeResolver path rawLines

            (ds
             |> List.filter (fun d ->
                 not (d.Severity = "error" && d.File = path && Set.contains d.Line ic.Split.BlockLines)))
            @ ic.Diags
        else
            ds

    let rawLines =
        if initMarked then stripInitDirectives rawLines else rawLines

    let body, bodyOffset, sigDecls = scriptBody rawLines
    let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)

    let typeEnv0, _ = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv

    let typeEnv0 =
        { typeEnv0 with
            Modules = typeEnv0.Modules |> Map.add "Self" selfMembers }

    // imports resolve relative to the file being checked [D:modules-v1];
    // one cache per check dedups a diamond, one chain catches a cycle
    let analyzeImport: ImportLoader = fileImportLoader assumeResolver (importAnchor path)

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

    let physicalIndent =
        numbered
        |> List.map (fun (n, raw) -> n, raw |> Seq.takeWhile ((=) ' ') |> Seq.length)
        |> Map.ofList

    // the lines a recovery attempt re-indented, at their source indent
    let reindented = ref Map.empty

    let rec assembleRecovering (attempts: int) (input: (int * string) list) =
        let misaligned = ref None

        match assembleWith (fun l c -> misaligned.Value <- Some(l, c)) input with
        | Ok lls -> lls
        | Error msg when attempts > 0 ->
            let line, message =
                match assemblyErrorAt input msg with
                | Some(l, _, rest) -> l, rest
                | None -> -1, msg

            // the named line's content start is the column — in the
            // source, not in an earlier attempt's re-indent
            // [D:recovery-physical-indent]
            let col =
                physicalIndent.TryFind line
                |> Option.map (fun i -> i + 1)
                |> Option.defaultValue 1

            assemblyDiags.Add
                { File = path
                  Line = (if line > 0 then line else 1)
                  Col = col
                  EndLine = None
                  EndCol = None
                  Severity = "error"
                  Code = "layout"
                  Message = message }

            match misaligned.Value with
            | Some(l, c) when l = line ->
                // re-indent onto the siblings' column: the line's closers
                // still close, so the statement checks on
                let fix (n, raw: string) =
                    if n = line then n, String(' ', c) + raw.TrimStart(' ') else n, raw

                reindented.Value <- reindented.Value |> Map.add line physicalIndent[line]

                assembleRecovering (attempts - 1) (input |> List.map fix)
            | _ when line > 0 && input |> List.exists (fun (n, _) -> n = line) ->
                assembleRecovering (attempts - 1) (input |> List.filter (fun (n, _) -> n <> line))
            | _ -> []
        | Error _ -> []

    let logicalLines =
        numbered
        |> assembleRecovering 10 // raw lines: assemble classifies/strips internally
        |> List.map (withPhysicalIndents reindented.Value)

    (let diags0 = List.ofSeq assemblyDiags
     diags0 |> ignore)

    match Some logicalLines with
    | None -> [], [], typeEnv0, [], []
    | Some logicalLines ->
        let diags = ResizeArray<Diagnostic>()
        let stmts = ResizeArray<LogicalLine * CheckedStatement>()
        let partials = ResizeArray<LogicalLine * CheckedStatement>()
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

        let mutable tenv =
            { typeEnv0 with
                LoadTargets = loadTargetsOf typeEnv0 logicalLines }

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
             // a typed home-path head [D:tilde]: this machine's home
             | Check.TECmd(Check.THeadDyn(d, _), _, _) when
                 d.StartsWith "~/"
                 && not (
                     System.IO.File.Exists((try Builtins.homeDir () with _ -> "") + d.Substring 1)
                 )
                 ->
                 [ d, te.Span ]
             // a reified pipeline's stages [D:chain-reifiers]
             | Check.TEApp(f, { Kind = Check.TEList stages }) when
                 (let rec chainVar (g: Check.TypedExpr) =
                     match g.Kind with
                     | Check.TEVar v -> v.StartsWith "|chain"
                     | Check.TEApp(h, _) -> chainVar h
                     | _ -> false

                  chainVar f)
                 ->
                 stages
                 |> List.choose (fun st ->
                     match st.Kind with
                     | Check.TETuple({ Kind = Check.TEStr prog; Span = sp } :: _) when not (Extern.exists prog) ->
                         Some(prog, sp)
                     | _ -> None)
             // a single reified command (`$(cmd | complete)`): the program
             // rides as the desugar's literal argument, warned like a
             // plain head [D:exit-reifiers]
             | Check.TEApp _ ->
                 let rec spine (e: Check.TypedExpr) acc =
                     match e.Kind with
                     | Check.TEApp(g, a) -> spine g (a :: acc)
                     | Check.TEVar v -> Some(v, acc)
                     | _ -> None

                 match spine te [] with
                 | Some(v, args) ->
                     match reifiedProgSlot v with
                     | Some(idx, arity) when List.length args = arity ->
                         match List.item idx args with
                         | { Kind = Check.TEStr prog; Span = sp } when not (Extern.exists prog) -> [ prog, sp ]
                         | _ -> []
                     | _ -> []
                 | None -> []
             | _ -> [])
            @ (Check.childExprs te |> List.collect cmdHeads)

        // module-signature pairing state [D:module-signatures] — module
        // files only; scripts pass None so the form refuses with its
        // explanation. The whole-file rules (orphan, doc-on-impl) are
        // judged after the fold, mirroring the module loader.
        let mutable sigState = if isModule then Some SigContext.empty else None
        // the module's command-reaching functions so far [D:modules-v1]
        let mutable moduleHelpers: Map<string, int> = Map.empty
        // aliases of imports that failed: a later "no module named" for
        // one echoes the import's own error, so it is not reported
        let mutable failedImports: Set<string> = Set.empty
        let sigImplLines = ResizeArray<string * int>()

        // unused bindings [D:unused-bindings]: fed per statement, judged
        // after the fold with the module's Signed set
        let unusedTracker = UnusedTracker()

        // possible re-enumeration [D:reenum-warning]: fed per statement,
        // flushed after the fold; modules skip (a command-running module
        // `let` is already the module-declares-only error)
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
                         | None when prog.StartsWith "~/" ->
                             $"no executable at {prog} — weir resolves commands at check time; the script runs once it exists"
                         | None ->
                             $"command not found on PATH: {prog}{hint} — weir resolves commands at check time; the script runs once it is installed") }

            match checkStatement true sigState assumeResolver analyzeImport tenv ll with
            | Ok chk ->
                // the statement's own minted anonymous shapes join its env
                // now, not at the next statement's drain [D:anon-literals]:
                // the editor reads a statement's env (completion after the
                // last good line), and the pending table is per-thread
                // state that would otherwise leak into the next analysis
                // [D:lsp-analysis-cache]
                let chk = { chk with Env = Check.withPendingAnonDefs chk.Env }
                chk.Warnings |> List.iter warn

                // a duplicate type declaration is an error, not a silent
                // replacement [D:dup-type-decl] — replacement would be
                // retroactive (code above the redeclaration re-resolves
                // against the winner). Scripts only; the REPL replaces
                // by design.
                (match chk.Kind with
                 | KType(decl, _) ->
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
                         | KLetPat(_, _, te) when (Check.commandRunsNow moduleHelpers te).IsSome ->
                             Some(commandAtImport (Check.commandRunsNow moduleHelpers te).Value)
                         | KLet(name, _, te) -> Check.eagerCommandHelper moduleHelpers te |> Option.map (helperAtImport name)
                         | KLetPat(_, _, te) -> Check.eagerCommandHelper moduleHelpers te |> Option.map (helperAtImport "f")
                         | _ -> None

                     (match chk.Kind with
                      | KLet(name, _, te) ->
                          moduleHelpers <- Check.recordCommandBinding moduleHelpers name te
                      | _ -> ())

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
                               Code = "module-declares-only"
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
                         @ (chainStageHeads e |> List.filter (fst >> Extern.exists >> not))
                         @ (reifiedHead e |> Option.filter (fst >> Extern.exists >> not) |> Option.toList)
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
                         | PCase(_, Some inner, _) -> patVars inner
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

                     (match stmt with
                      | SImport(_, _, Some(alias, _)) -> failedImports <- Set.add alias failedImports
                      | SImport(ipath, _, None) ->
                          deriveModuleName ipath
                          |> Option.iter (fun alias -> failedImports <- Set.add alias failedImports)
                      | _ -> ())

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

                     // the tree, against the env the statement checked in;
                     // its env is the hole-bound one below. A budget failure
                     // is not paid twice [D:budget-stop-first]
                     let recovered, recoveryBudget =
                         if Check.isBudgetMessage d.Message then
                             None, false
                         else
                             recoverStatement tenv holeScheme stmt

                     for n in bound do
                         tenv <- bindValue n holeScheme tenv

                     recovered
                     |> Option.iter (fun kind ->
                         partials.Add(
                             ll,
                             { Kind = kind
                               Env = tenv
                               Warnings = [] }
                         ))

                     // a spent recovery budget stops the fold exactly as a
                     // published budget diagnostic does [D:budget-stop-first]:
                     // one burn bounds the file, not one burn per statement
                     if recoveryBudget then
                         budgetStop <- true
                 | Error _ ->
                     // a statement that does not parse still names its
                     // binder [D:parse-error-binds]: harvested lexically, a
                     // `let NAME` at the statement's start only (never argv),
                     // so its later uses don't echo as "unbound"
                     let m =
                         System.Text.RegularExpressions.Regex.Match(ll.Text, @"^\s*let\s+([A-Za-z_][A-Za-z0-9_']*)")

                     if m.Success && not (Parser.keywords.Contains m.Groups[1].Value) then
                         tenv <-
                             bindValue
                                 m.Groups[1].Value
                                 { Forall = Set.singleton "__hole"
                                   Cs = Map.empty
                                   Ty = TVar "__hole"
                                   RowOrigins = Map.empty
                                   HoleDefaults = [] }
                                 tenv)

                let echoesFailedImport =
                    failedImports
                    |> Set.exists (fun a -> d.Message.StartsWith $"no module named '{a}'")

                // multi-file [D:modules-v1]: a module error carries File +
                // PhysLine/Col into that other file; its Note is the "imported
                // here" pointer at the import line in this file
                if not echoesFailedImport then
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
                       Code = "missing-implementation"
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
                           Code = "doc-on-implementation"
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

        // effects before the CLI parse [D:effect-before-args]: the entry
        // script only (a module or init file has no Args.load), silent
        // when a statement errored — one real error beats advice
        (if
            not isModule
            && not initMarked
            && not (diags |> Seq.exists (fun d -> d.Severity = "error"))
         then
             for d in effectBeforeArgs path (stmts |> Seq.map (fun (ll, chk) -> ll, chk.Kind) |> List.ofSeq) do
                 diags.Add d)

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
         @ continuedCommandDiagnostics path sigInfos (List.ofSeq stmts)
         |> withInitRules
         |> List.sortWith (fun a b ->
             let byLine = compare a.Line b.Line
             if byLine <> 0 then byLine else compare a.Col b.Col)
         // init declarations are session exports [D:init-marker]: the
         // unused-binding judgement does not apply to a marked file
         |> List.filter (fun d -> not (initMarked && d.Code = "unused-binding"))),
        List.ofSeq stmts,
        typeEnv0,
        logicalLines,
        List.ofSeq partials

let analyzeLines (path: string) (rawLines: string list) =
    analyzeLinesAs false path rawLines

/// diagnostic rendering shared by `check` and `check --can`
/// [D:can-report] — one spelling for both consumers
/// `parsedAny` is "did any statement parse" — the conservative half of the
/// not-weir heuristic (rider D4). Without it a real script with a broken
/// first line would be told it is not weir at all.
let printDiags (json: bool) (parsedAny: bool) (diags: Diagnostic list) : unit =
    if json then
        // the encoder is unchanged; a tty sink gets the safe rendering
        // [D:tty-bidi-sanitize]
        Console.WriteLine(
            jsonBuild (fun w ->
                w.WriteStartArray()
                diags |> List.iter (writeDiag w)
                w.WriteEndArray())
            |> Eval.sanitizeIfTty Console.IsOutputRedirected
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
        // a bare-member teaching is weir's own diagnosis of weir code —
        // the file is weir, so no preamble [D:bare-partition]
        let notWeir =
            not parsedAny
            && diags
               |> List.exists (fun d -> d.Line = 1 && d.Code = "parse" && d.Severity = "error")
            && not (
                diags
                |> List.exists (fun d -> d.Line = 1 && d.Message.Contains "is a bare module member")
            )

        if notWeir then
            match diags with
            | d :: _ ->
                Console.WriteLine(
                    Color.bold c $"{quoteData d.File}"
                    + ": this does not look like a weir script — the first statement could not be parsed"
                    |> Eval.sanitizeIfTty Console.IsOutputRedirected
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

            // a message echoes source, paths, patterns [D:tty-diag-sanitize]
            Console.WriteLine(
                Color.bold c $"{quoteData d.File}:{d.Line}:{d.Col}" + $": {sev}: {d.Message}"
                |> Eval.sanitizeIfTty Console.IsOutputRedirected
            )

let checkOnly (json: bool) (path: string) : int =
    match readEntryScript path with
    | Error msg ->
        Console.Error.WriteLine $"weir: {msg}"
        2
    | Ok rawLines ->
        let diags, stmts, _, _, _ = analyzeLines path rawLines
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
let run (path: string) (scriptArgs: string list) : int =
    match readEntryScript path with
    | Error msg ->
        Console.Error.WriteLine $"weir: {msg}"
        2
    | Ok rawLines ->

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
                    (if l.TrimStart().StartsWith "#init" then
                         "an init file is not runnable — the REPL loads it; author it anywhere with #init on the first line and symlink it to the config dir (weir/init.weir)"
                     elif l.TrimStart().StartsWith "#session" then
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
            let numbered = body |> List.mapi (fun i l -> bodyOffset + i + 1, l)
            let assembled = numbered |> assemble // raw lines: assemble classifies/strips internally

            match assembled with
            | Error msg ->
                Console.Error.WriteLine(assemblyErrorLocated path numbered msg)
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
                let entryImport: ImportLoader = fileImportLoader resolver (importAnchor absScriptPath)

                // signatures load once, before the check fold; a load
                // failure (missing/malformed sig) is a check error —
                // absence is loud, never a silent fallback
                // [D:command-signatures]
                let sigLoadDiags, runSigInfos = loadSigs path runSigDecls
                let runDeclaredTypes = System.Collections.Generic.Dictionary<string, int * bool>()

                // unused bindings gate the run too [D:unused-bindings] —
                // check error = zero side effects, this check included
                let runUnused = UnusedTracker()

                // node -> physical position, for runtime errors [D:runtime-position]
                let runPositions =
                    System.Collections.Generic.Dictionary<obj, string * int * int * int>(HashIdentity.Reference)

                // a module's statements, with ITS file, so a function defined
                // there reports its own lines [D:runtime-position]
                let rec recordModule (lm: LoadedModule) =
                    for ll, stmt in lm.Body do
                        match stmt with
                        | CLet(_, te)
                        | CLetPat(_, te)
                        | CExpr te
                        | CCmd te -> recordPositions lm.AbsPath ll.Head ll te runPositions
                        | CImport nested -> recordModule nested
                        | _ -> ()

                // the checked kinds, for the whole-file effect-before-args
                // warning [D:effect-before-args]
                let runKinds = ResizeArray<LogicalLine * CheckedKind>()

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
                                            $"{quoteData path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    let sameFileMsg =
                                        if d.Parse then
                                            if d.HasCol then
                                                // the original source line + caret — never
                                                // the assembled text
                                                let src = rawByLine |> Map.tryFind d.PhysLine |> Option.defaultValue ""

                                                let caret = Color.red c (String(' ', max 0 (d.PhysCol - 1)) + "^")

                                                Color.bold c $"{quoteData path}:{d.PhysLine}:{d.PhysCol}"
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

                                            Color.bold c $"{quoteData path}:{d.PhysLine}:{d.PhysCol}"
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
                                                    "\n" + Color.bold c $"{quoteData path}:{nl}:{nc}" + ": note: " + nmsg
                                                | None -> ""

                                            Color.bold c $"{quoteData mf}:{d.PhysLine}:{d.PhysCol}"
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
                                            $"{quoteData path}:{wl}:{wc}: " + Color.yellow c "warning" + $": {wm}"
                                        )

                                    runKinds.Add(ll, chk.Kind)

                                    // signature warnings print, signature errors
                                    // gate exactly as schemas do
                                    // [D:command-signatures]
                                    let sigDs = sigCmdDiagnostics path runSigInfos [ (ll, chk) ]

                                    // a continued command warns and runs, as every
                                    // check warning does here [D:continued-command]
                                    for d in
                                        (sigDs |> List.filter (fun d -> d.Severity = "warning"))
                                        @ continuedCommandDiagnostics path runSigInfos [ (ll, chk) ] do
                                        Console.Error.WriteLine(
                                            $"{quoteData path}:{d.Line}:{d.Col}: " + Color.yellow c "warning" + $": {d.Message}"
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
                                            Color.bold c $"{quoteData path}:{d.Line}:{d.Col}"
                                            + ": "
                                            + Color.red c "schema error"
                                            + $":\n{src}\n{underline}\n{d.Message}"
                                        )
                                    | [] ->

                                        // duplicate type declarations gate the
                                        // run too [D:dup-type-decl]
                                        let dupError =
                                            match chk.Kind with
                                            | KType(decl, _) ->
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
                                                | KType(decl, validator) -> CType(decl, validator)
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

                                            // enrich a record's (or union's) Docs from
                                            // the `///` field (case) docs, so --help
                                            // reads them [D:doc-help]
                                            // [D:help-case-docs]; the Args.load arm (checked
                                            // later, the type comes first) captures it
                                            let env' =
                                                match chk.Kind with
                                                | KType(decl, _) ->
                                                    let docs =
                                                        Map.fold
                                                            (fun m k v -> Map.add k v m)
                                                            (fieldDocsFor rawLines ll)
                                                            (typeDocFor rawLines ll)

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
                                                                        | Union ud -> Union { ud with Docs = docs })) }
                                                | _ -> chk.Env

                                            match stmt with
                                            | CLet(_, te)
                                            | CLetPat(_, te)
                                            | CExpr te
                                            | CCmd te -> recordPositions path ll.Head ll te runPositions
                                            | CImport lm -> recordModule lm
                                            | _ -> ()

                                            Ok(env', (ll.Head, stmt) :: acc))
                        (Ok(
                            { typeEnv0 with
                                LoadTargets = loadTargetsOf typeEnv0 logicalLines },
                            []
                        ))

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
                            Color.bold c $"{quoteData path}:{f.ULine}:{f.UCol}"
                            + ": "
                            + Color.red c "check error"
                            + $":\n{src}\n{underline}\n{f.UMessage}"
                        )

                    1
                | Ok _ when not (docMisalignments path rawLines).IsEmpty ->
                    // a check error means nothing runs [D:check-error-no-run],
                    // the doc-alignment lint included
                    let c = Color.onStderr.Value

                    for d in docMisalignments path rawLines do
                        let src = rawByLine |> Map.tryFind d.Line |> Option.defaultValue ""
                        let caret = Color.red c (String(' ', max 0 (d.Col - 1)) + "^")

                        Console.Error.WriteLine(
                            Color.bold c $"{quoteData path}:{d.Line}:{d.Col}"
                            + ": "
                            + Color.red c "check error"
                            + $":\n{src}\n{caret}\n{d.Message}"
                        )

                    1
                | Ok(_, revStmts) ->
                    let stmts = List.rev revStmts

                    // advisory, before any line runs [D:effect-before-args]
                    (let c = Color.onStderr.Value

                     for d in effectBeforeArgs path (List.ofSeq runKinds) do
                         Console.Error.WriteLine(
                             $"{quoteData path}:{d.Line}:{d.Col}: " + Color.yellow c "warning" + $": {d.Message}"
                         ))

                    // a runtime error's position [D:runtime-position]: the
                    // blamed node (a command at its own column, any other
                    // node at its line's first non-blank column), then one
                    // `called from` line per call of a function defined
                    // elsewhere, innermost first; the statement's line when
                    // nothing is known
                    let sourceLines = System.Collections.Generic.Dictionary<string, string[]>()

                    let firstCol (file: string) (l: int) =
                        let text =
                            if file = path then
                                Map.tryFind l rawByLine
                            else
                                let lines =
                                    match sourceLines.TryGetValue file with
                                    | true, ls -> ls
                                    | _ ->
                                        let ls = (try IO.File.ReadAllLines file with _ -> [||])
                                        sourceLines[file] <- ls
                                        ls

                                if l >= 1 && l <= lines.Length then Some lines[l - 1] else None

                        match text with
                        | Some t -> t.Length - t.TrimStart().Length + 1
                        | None -> 1

                    // the entry as the user wrote it; a module relative to the
                    // working directory when it is below it
                    // a file name is data [D:error-data-escape]
                    let shownFile (file: string) =
                        quoteData (
                            if file = path then
                                file
                            else
                                let rel = IO.Path.GetRelativePath(Environment.CurrentDirectory, file)
                                if rel.StartsWith ".." || IO.Path.IsPathRooted rel then file else rel
                        )

                    let posOf (exact: bool) (n: obj) =
                        if isNull n then
                            None
                        else
                            match runPositions.TryGetValue n with
                            | true, (file, _, l, c) -> Some(shownFile file, l, (if exact then c else firstCol file l))
                            | _ -> None

                    let runtimeErrorAt (lineNo: int) (ex: exn) : string =
                        let origin, frames =
                            match Eval.blameOf ex with
                            | Some b -> posOf b.Exact b.Node, b.Frames |> Seq.choose (posOf false) |> List.ofSeq
                            | None -> posOf Eval.RunPos.Exact Eval.RunPos.Current, []

                        // an unmapped origin falls back to the innermost frame
                        let origin, frames =
                            match origin, frames with
                            | Some o, fs -> Some o, fs
                            | None, f :: fs -> Some f, fs
                            | None, [] -> None, []

                        match origin with
                        | Some(file, l, c) ->
                            let safe = Eval.sanitizeIfTty Console.IsErrorRedirected ex.Message
                            let head = $"{file}:{l}:{c}: " + Color.red Color.onStderr.Value "error" + $": {safe}"
                            let shown = frames |> List.truncate 10

                            let more =
                                if frames.Length > shown.Length then
                                    [ $"  … {frames.Length - shown.Length} more" ]
                                else
                                    []

                            head
                            :: (shown |> List.map (fun (f, fl, fc) -> $"  called from {f}:{fl}:{fc}"))
                            @ more
                            |> String.concat Environment.NewLine
                        | None -> runtimeErrorLine path lineNo ex.Message

                    // statements are width, not depth: a loop, each step in
                    // its own try [D:block-width]
                    let fail (lineNo: int) (ex: exn) : Result<Eval.Env, int> =
                        match ex with
                        | Eval.ExitRequest code -> Error code
                        // the script user's usage error: no weir location,
                        // exit 2 [D:args-usage-error]
                        | :? Eval.ArgsUsageError ->
                            Console.Error.WriteLine(Eval.sanitizeIfTty Console.IsErrorRedirected ex.Message)
                            Error 2
                        | ex ->
                            // a command death the signal owns ends silently
                            // [D:settle-pending]
                            match Proc.fatalTeardown ex with
                            | Some code -> Error code
                            | None ->
                                Console.Error.WriteLine(runtimeErrorAt lineNo ex)
                                Error 1

                    let step (venv: Eval.Env) (lineNo: int) (stmt: CheckedStmt) : Result<Eval.Env, int> =
                        // each top-level statement starts at its own root; a
                        // closure from this statement is not a call
                        Eval.RunPos.SameStatement <-
                            System.Func<obj, bool>(fun body ->
                                match runPositions.TryGetValue body with
                                | true, (f, s, _, _) -> f = path && s = lineNo
                                | _ -> false)

                        match stmt with
                        | CLet(_, te)
                        | CLetPat(_, te)
                        | CExpr te
                        | CCmd te -> Eval.RunPos.Current <- box te
                        // an import's module code has no positions here:
                        // the statement's own line, never a stale node
                        | _ -> Eval.RunPos.Current <- null

                        Eval.RunPos.Exact <- false

                        match stmt with
                        | CNoop -> Ok venv
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

                                lm.Members
                                |> List.fold
                                    (fun acc (n, _) ->
                                        match Map.tryFind n moduleVenv with
                                        | Some v -> Map.add $"{lm.Alias}.{n}" v acc
                                        | None -> acc)
                                    venv
                                |> Ok
                            with ex ->
                                fail lineNo ex
                        | CType(decl, validator) ->
                            try
                                Ok(Eval.declare venv decl validator)
                            with ex ->
                                fail lineNo ex
                        | CLetPat(pat, te) ->
                            try
                                let bindings = Eval.bindPattern pat (Eval.eval venv te)
                                Ok(bindings |> List.fold (fun m (n, v) -> Map.add n v m) venv)
                            with ex ->
                                fail lineNo ex
                        | CLet(name, te) ->
                            try
                                Ok(Map.add name (Eval.eval venv te) venv)
                            with ex ->
                                fail lineNo ex
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

                                Ok venv
                            with ex ->
                                fail lineNo ex
                        | CExpr te ->
                            try
                                Eval.eval venv te |> ignore
                                Ok venv
                            with ex ->
                                fail lineNo ex

                    let mutable venv = valueEnv0
                    let mutable rest = stmts
                    let mutable code = None

                    while code.IsNone && not (List.isEmpty rest) do
                        let (lineNo, stmt) = List.head rest
                        rest <- List.tail rest

                        match step venv lineNo stmt with
                        | Ok venv' -> venv <- venv'
                        | Error c -> code <- Some c

                    defaultArg code 0
