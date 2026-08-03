module Weir.Parser

open System
open FParsec
open Weir.Types
open Weir.Ast

// public: the REPL colorizer reuses THIS set [D:repl-color] — one
// keyword source, no drift
let keywords =
    Set
        [ "let"
          "in"
          "fun"
          "true"
          "false"
          "match"
          "with"
          "type"
          "of"
          "from"
          "to"
          "if"
          "then"
          "else"
          "when"
          "elif"
          "rec"
          "mutable"
          // the module system's two words [D:modules-v1]: reserved so a
          // bare `module`/`import` never resolves as an identifier or a
          // command head (keyword-domination)
          "module"
          "import"
          // the general effect loop [D:for-do]
          "for"
          "do"
          // reserved for the parked match-lambda sugar [D:block-let-cmd
          // rider]: the future form breaks nothing
          "function" ]

// the keyword set, exposed for tooling resolvers (weir check's
// assume-command rule must never claim a keyword head)
let isKeyword (w: string) = keywords.Contains w

type Resolver =
    { IsKnown: string -> bool
      IsCommandCallable: string -> bool
      IsExternal: string -> bool
      ExternalNames: unit -> seq<string> }

// Sigil interiors ($(...) / !(...)) need the resolver inside the
// expression grammar, which is otherwise resolver-free. parseLine sets
// this per call; ThreadLocal keeps parallel test runs isolated.
let private ambientResolver =
    new System.Threading.ThreadLocal<Resolver>(fun () ->
        { IsKnown = (fun _ -> true)
          IsCommandCallable = (fun _ -> false)
          IsExternal = (fun _ -> false)
          ExternalNames = fun () -> Seq.empty })

// Block-let command RHS [D:block-let-cmd]: TRUE only along the
// statement spine a block assembles into (topLet RHS + its let-in
// chain) — parens, lambda bodies, and the bare single-line let-in
// (REPL/-e) stay expression-only, holding the original in-swallow
// park's boundary by construction.
let private letCmdOk = new System.Threading.ThreadLocal<bool>(fun () -> false)

// forwarded: the let-in value position sits above the command grammar
let private letRhsCmd, private letRhsCmdRef =
    createParserForwardedToRef<Expr, unit> ()

let private withLetCmd (v: bool) (p: Parser<'a, unit>) : Parser<'a, unit> =
    fun stream ->
        let saved = letCmdOk.Value
        letCmdOk.Value <- v

        try
            p stream
        finally
            letCmdOk.Value <- saved

// the param-ful law one scope deeper [D:block-let-cmd]: a block-let
// name shadows PATH for every later parse in its body
let private withAmbientName (name: string) (p: Parser<'a, unit>) : Parser<'a, unit> =
    fun stream ->
        let saved = ambientResolver.Value

        ambientResolver.Value <-
            { saved with
                IsKnown = fun n -> n = name || saved.IsKnown n }

        try
            p stream
        finally
            ambientResolver.Value <- saved

let private isIdentStart c = isLetter c || c = '_'
let private isIdentCont c = isLetter c || isDigit c || c = '_'

let private ws: Parser<unit, unit> = spaces
let private str_ws s = pstring s >>. ws

// The sibling sentinel [D:sibling-sentinel]: the assembler joins body
// statement-siblings with THIS instead of ';' so command mode — which
// swallows a user-typed ';' as a bareword arg (the prior-bleed
// teaching, kept) — STOPS at the machine boundary. Same width as
// " ; " (3 chars) so every span mapping through the segment table is
// byte-identical. Unproduceable: assemble rejects any source line
// carrying it, so it reaches the grammar ONLY from the assembler (the
// '|'-key precedent — a token user text cannot form).
[<Literal>]
let sibSep = '\u001F'

let sibSepStr = System.String(sibSep, 1)

/// Line-end `yaml` arms a district; `to yaml` / `from yaml` are the
/// boundary adapters [D:yaml-district]. One predicate, shared with the
/// REPL colorizer's marker tint — never a second classifier.
let isYamlMarkerPiece (piece: string) =
    // a `schema=<name>` suffix declares the district's contract
    // [D:yaml-schemas]; strip it, then apply the marker law
    let core =
        let lastTok =
            match piece.LastIndexOf ' ' with
            | -1 -> piece
            | i -> piece.Substring(i + 1)

        if lastTok.StartsWith "schema=" && lastTok.Length > 7 then
            piece.Substring(0, piece.Length - lastTok.Length).TrimEnd()
        else
            piece

    (core = "yaml" || core.EndsWith " yaml")
    && not (core.EndsWith "to yaml")
    && not (core.EndsWith "from yaml")


let private pos (p: Position) : Pos =
    { Line = int p.Line
      Col = int p.Column }

let private spanned (p: Parser<'a, unit>) : Parser<'a * Span, unit> =
    pipe3 getPosition p getPosition (fun s x e -> x, { Start = pos s; End = pos e })

let private rawWord = many1Satisfy2L isIdentStart isIdentCont "identifier"

// the two-pipe cliff [D:pipe-hint]: a bare `|` after a completed
// EXPRESSION is not an operator — name the spelling instead of
// dumping the token set (`||` and `|>` belong to the expression
// grammar and never reach this check)
// failFatally, ANCHORED [D:anchor-before-read]: raise the fatal at a
// captured position, not wherever the stream drifted after consuming the
// trigger. Consuming the trigger first is what clears the competing
// "expected" errors at that spot (a plain lookAhead-restore keeps the
// fatal non-consuming, so <|> merges them back in); seeking to the anchor
// then reports ON the trigger. `run` reads err.Position, so the seek is
// the whole mechanism.
let private failFatallyAt (anchor: Position) (msg: string) : Parser<'a, unit> =
    fun stream ->
        stream.Seek anchor.Index
        Reply(ReplyStatus.FatalError, messageError msg)

// same anchor by COLUMN, for sites whose token is already captured as a
// Span (parse runs on one assembled logical line, so Index = Col - 1)
let private failFatallyAtCol (col: int) (msg: string) : Parser<'a, unit> =
    fun stream ->
        stream.Seek(int64 (col - 1))
        Reply(ReplyStatus.FatalError, messageError msg)

let private barePipeHint: Parser<unit, unit> =
    // a bare `|` after a completed EXPRESSION [D:pipe-hint]; anchor the
    // caret ON the `|`, not the ws after it [D:anchor-before-read]
    (attempt (getPosition .>> pchar '|' .>> notFollowedBy (anyOf "|>"))
     >>= fun pipePos -> failFatallyAt pipePos "'|' chains commands; pipe expressions with '|>'")
    <|> preturn ()

// non-field positions name the scope decision [D:attributes]
let private attrsRejectHere: Parser<unit, unit> =
    followedBy (pstring "[<") >>. failFatally "attributes attach to record fields"

let private keyword s =
    attempt (pstring s .>> notFollowedBy (satisfy isIdentCont)) .>> ws

let private notKeyword (w: string) =
    if w = "function" then
        fail "'function' is reserved; write 'fun x -> match x with'"
    elif keywords.Contains w then
        fail $"'{w}' is a keyword"
    else
        preturn w

let private identSpanned =
    attempt (spanned rawWord >>= fun (w, span) -> notKeyword w >>% (w, span)) .>> ws

let private ident = identSpanned |>> fst

let private expr, private exprRef = createParserForwardedToRef<Expr, unit> ()

let private mkExpr (kind, span) = { Kind = kind; Span = span }

// e1 ; e2 — block sequencing. Deployed at BODY positions (then/else,
// arm and lambda bodies, let-in bodies, parens, statements) and GREEDY
// there: `if c then a ; b` sequences INSIDE the then-branch, matching
// the block-shaped source it assembles from. This diverges from F#
// VERBOSE grouping (named divergence row) — the alternative made
// assembled if-blocks silently unconditional (see the Session-2
// stop-and-report in NOTES).
let private seqExpr, private seqExprRef = createParserForwardedToRef<Expr, unit> ()

// comma is the tuple constructor at F#'s precedence [D:bare-comma]:
// below `;` (`a, b ; c` is `(a, b) ; c`), above `|>`
// (`xs |> f, ys |> g` groups F#-identically).
// Command mode is untouched by construction: barewords keep their commas.
let private commaExpr, private commaExprRef =
    createParserForwardedToRef<Expr, unit> ()

// update-source expressions [D:record-update]: compound-free (a bare
// match/if source is rejected — parens required [D:record-update]);
// assigned once the operator table exists
let private updateSource, private updateSourceRef =
    createParserForwardedToRef<Expr, unit> ()

let private intLit =
    spanned (
        // anchor at the literal's start [D:anchor-before-read]: the fail
        // fires after consuming the digits (and the measure), so seek back
        getPosition
        .>>. (many1Satisfy isDigit .>>. opt (attempt (pchar '<' >>. rawWord .>> pchar '>')))
        >>= fun (at, (digits, m)) ->
            match m, System.Int64.TryParse digits with
            | Some _, _ -> failFatallyAt at "units of measure are not supported; use bare int"
            | None, (true, n) -> preturn (EInt n)
            | None, (false, _) -> failFatallyAt at $"int literal out of range (64-bit): {digits}"
    )
    |>> mkExpr
    .>> ws

// the one escape decoder — plain strings and interp text share it
let private escapedChar =
    pchar '\\'
    >>. (anyOf "\"\\nt"
         |>> function
             | 'n' -> '\n'
             | 't' -> '\t'
             | c -> c)

let private stringChar =
    choice [ satisfy (fun c -> c <> '"' && c <> '\\'); escapedChar ]

let private strLit =
    spanned (between (pchar '"') (pchar '"') (manyChars stringChar) |>> EStr)
    |>> mkExpr
    .>> ws

// raw strings [D:raw-strings] — F#'s two kinds, single-line, oracle
// probe-pinned BEFORE implementation: @"..." verbatim (backslashes
// literal, "" = one embedded quote) and """...""" (no escapes at
// all; closes at the FIRST triple, so a trailing extra quote is an
// error — FCS's verdict on the quad-closer edge).
let private verbatimBody =
    manyStrings (choice [ many1Satisfy (fun c -> c <> '"'); attempt (pstring "\"\"") >>% "\"" ])

let private tripleBody =
    manyStrings (
        choice
            [ many1Satisfy (fun c -> c <> '"')
              attempt (pchar '"' .>> notFollowedBy (pstring "\"\"")) >>% "\"" ]
    )

let private verbatimLit =
    spanned (pstring "@\"" >>. verbatimBody .>> pchar '"' |>> EStr) |>> mkExpr
    .>> ws

let private tripleLit =
    spanned (pstring "\"\"\"" >>. tripleBody .>> pstring "\"\"\"" |>> EStr)
    |>> mkExpr
    .>> ws

let private wordAtom =
    attempt (
        spanned rawWord
        >>= fun (s, span) ->
            match s with
            | "true" -> preturn { Kind = EBool true; Span = span }
            | "false" -> preturn { Kind = EBool false; Span = span }
            | s when keywords.Contains s -> fail $"'{s}' is a keyword"
            | s -> preturn { Kind = EVar s; Span = span }
    )
    .>> ws

let private unitLit =
    spanned (attempt (pchar '(' >>. ws >>. pchar ')') >>% EUnit) |>> mkExpr .>> ws

let private parens =
    // (e) groups; tuples come from the comma INSIDE seqExpr (the
    // bare-comma amendment moved the comma into the expression grammar)
    spanned (pchar '(' >>. ws >>. seqExpr .>> pchar ')')
    |>> fun (inner, span) -> { inner with Span = span }
    .>> ws

let private fieldAssign =
    identSpanned .>> str_ws "=" .>>. commaExpr |>> fun ((n, s), v) -> n, s, v

// record literal OR copy-and-update [D:record-update]: after `{`,
// try the field-assign head; else parse a (compound-free) source and
// expect `with` — the bounded backtrack. Paths carry the nested
// I.X sugar; the checker walks them.
let private updatePath = sepBy1 (spanned rawWord .>> ws) (pchar '.' >>. ws)

let private updateAssign =
    (attrsRejectHere >>% Unchecked.defaultof<_>)
    <|> (updatePath .>> str_ws "=" .>>. commaExpr)

// `{ <keyword> = ` is a field-assign with a reserved name — DOMINATE
// [D:anchor-before-read] BEFORE the literal-vs-update commit-check
// [D:arm-commit], which would otherwise bury it in the update path. The
// decision is inside the attempt (a real field backtracks, no consume);
// only the fatal escapes, and `{` has already committed the atom.
let private keywordFieldGuard: Parser<ExprKind, unit> =
    attempt (
        getPosition .>>. spanned rawWord .>> ws .>> followedBy (str_ws "=")
        >>= fun (at, (w, _)) ->
            if w = "function" || keywords.Contains w then
                preturn (at, w)
            else
                fail "real field"
    )
    >>= fun (at, w) ->
        if w = "function" then
            failFatallyAt at "'function' is reserved; write 'fun x -> match x with'"
        else
            failFatallyAt at $"'{w}' is a keyword"

let private recordLit =
    spanned (
        pchar '{'
        >>. ws
        >>. (keywordFieldGuard
             <|> choice
                     [ // the consumed-separator law's record instance
                       // [D:arm-commit]: the literal COMMITS on its head
                       // (`ident =`, not `==`) — a deep field failure reports
                       // at ITS site instead of rewinding the whole literal
                       // into the update alternative's shallower dump
                       attempt (lookAhead (identSpanned .>> str_ws "=" .>> notFollowedBy (pchar '=')))
                       >>. (sepBy1 fieldAssign (str_ws ";") .>> pchar '}')
                       |>> ERecord
                       (updateSource .>> keyword "with") .>>. sepBy1 updateAssign (str_ws ";")
                       .>> pchar '}'
                       |>> EUpdate ])
    )
    |>> mkExpr
    .>> ws

let private dotdot = pstring ".." .>> ws

// Range endpoints/steps are simple expressions only (literals, idents, field
// access, parenthesized anything) — reject-rather-than-guess. The attempt on
// fieldSuffix keeps the first dot of '..' out of field-access parsing. The
// negative-literal form predates general prefix minus [D:prefix-minus] and stays:
// range steps allow the SPACED form ([10.. -1 ..1]) that adjacency rejects.
// rangeTerm is a forward ref: it needs atom, which needs listLit.
let private negIntLit =
    spanned (
        pchar '-' >>. many1Satisfy isDigit
        >>= fun digits ->
            match System.Int64.TryParse digits with
            | true, n -> preturn (EInt(-n))
            // NOT anchored [D:anchor-before-read]: seeking to the '-' would
            // drop the fatal into the unary-minus operator's contested spot
            // and merge its expecting-list — a message-domination FINDING,
            // coupled to that separate class; the clean message wins here
            | false, _ -> failFatally $"int literal out of range (64-bit): -{digits}"
    )
    |>> mkExpr
    .>> ws

let private rangeTerm, private rangeTermRef =
    createParserForwardedToRef<Expr, unit> ()

// Fail the range probe fast on a list/record opener [D:range-probe]:
// a range endpoint is never `[`/`{`, so committing to the list path
// here is safe — and it stops rangeTerm from descending into nested
// brackets that the backtrack would then re-parse (O(2^n) on `[[[…]]]`).
let private rangeBody =
    notFollowedBy (anyOf "[{") >>. attempt (rangeTerm .>> dotdot)
    .>>. rangeTerm
    .>>. opt (dotdot >>. rangeTerm)
    >>= fun ((a, b), c) ->
        let start, step, stop =
            match c with
            | Some stop -> a, b, stop
            | None -> a, { Kind = EInt 1L; Span = a.Span }, b

        match step.Kind with
        // anchor on the step, not the range's end [D:anchor-before-read]
        | EInt 0L -> failFatallyAtCol step.Span.Start.Col "range step is zero"
        | _ -> preturn (start, step, stop)

// [a..s..b] is pure sugar for Seq.range a s b; [a; b; c] stays an eager list.
let private buildBracket (content, span) : Expr =
    match content with
    | Choice1Of2(start, step, stop) ->
        let rangeFn =
            { Kind = EField({ Kind = EVar "Seq"; Span = span }, "range", span)
              Span = span }

        let app f a = { Kind = EApp(f, a); Span = span }
        app (app (app rangeFn start) step) stop
    | Choice2Of2 items -> { Kind = EList items; Span = span }

let private listLit =
    spanned (
        pchar '['
        >>. ws
        >>. choice
                [ rangeBody
                  .>> (pchar ']' <?> "']' (complex range endpoints need parentheses: [a..(f x)])")
                  |>> Choice1Of2
                  sepBy commaExpr (str_ws ";") .>> pchar ']' |>> Choice2Of2 ]
    )
    |>> buildBracket
    .>> ws

let private interpChar =
    choice
        [ satisfy (fun c -> c <> '"' && c <> '\\' && c <> '{' && c <> '}')
          escapedChar ]

let private interpPart =
    choice
        [ pstring "{{" >>% IStr "{"
          pstring "}}" >>% IStr "}"
          pchar '{' >>. ws >>. expr .>> pchar '}' |>> IExpr
          many1Chars interpChar |>> IStr ]

let private interpLit =
    spanned (pstring "$\"" >>. many interpPart .>> pchar '"' |>> EInterp) |>> mkExpr
    .>> ws

// Command-mode sigils: explicit, delimited guest entry for command
// chains in expression position. Interior grammar is IDENTICAL to a
// statement-level chain (cmdLine — same segments, splices, pipes,
// | complete, bareword heads incl. command-callables; the sigil makes
// the intent unambiguous, unlike the bare let-RHS which excludes
// builtins). $(chain) captures the value; !(chain) desugars to
// (chain) |> print — eager, streaming, raising, unit.
// [D:env-sugar-layers]: sigils take an optional env slot between glyph
// and paren — $e(...) / !e(...), e : seq<EnvVar>, applied to EVERY
// spawn in the interior chain (segments and | complete alike, threaded
// at construction). The ident must be GLUED to both glyph and paren;
// with a space the parse falls back ($name splice, plain paren).
let mutable private sigilChainImpl: Expr option -> Parser<Expr, unit> =
    fun _ -> fail "sigilChain not initialized"

let private sigilChain (envO: Expr option) : Parser<Expr, unit> =
    fun stream -> (sigilChainImpl envO) stream

// value-headed pipelines [D:value-headed-pipe]: after an expression, a
// bare `|` whose head resolves EXTERNAL feeds the value as stdin
// (`snips | sha256sum` ≡ `snips |> feed "sha256sum" []`). A known/library
// head keeps the barePipeHint teaching. Forward-declared (needs the
// command grammar below); set after cmdLineWith.
let mutable private valueHeadedTailImpl: Expr -> Parser<Expr, unit> =
    fun _ -> fail "valueHeadedTail not initialized"

let private valueHeadedTail (lhs: Expr) : Parser<Expr, unit> =
    fun stream -> (valueHeadedTailImpl lhs) stream

// after seqExpr, EITHER a value-headed pipeline OR the barePipeHint
// (which fatals on a bare `|` into an expression, else passes)
let private pipeOrHint (lhs: Expr) : Parser<Expr, unit> =
    valueHeadedTail lhs <|> (barePipeHint >>% lhs)

let private sigilOpen (glyph: char) : Parser<Expr option, unit> =
    attempt (
        pchar glyph >>. spanned (opt rawWord) .>> pchar '('
        |>> fun (nameO, span) -> nameO |> Option.map (fun n -> { Kind = EVar n; Span = span })
    )

// `| exitCode` STREAMS; capture/discard contexts are destination
// conflicts [D:exit-reifiers] — reject at parse with the teaching text
let rec private exitCodeSpine (e: Expr) : bool =
    match e.Kind with
    | EVar("|exitCoded" | "|exitCodedEnv") -> true
    | EApp(f, _) -> exitCodeSpine f
    | _ -> false

let private captureSigil =
    spanned (
        sigilOpen '$'
        >>= fun envO ->
            ws >>. sigilChain envO
            >>= fun chain ->
                (if exitCodeSpine chain then
                     failFatally
                         "exitCode streams; $() captures — use '| complete' inside $() and read .exitCode, or move the exitCode chain to a let RHS"
                 else
                     preturn chain)
                .>> (pchar ')'
                     <?> "')' — close the sigil on this line, or bind with 'let x = <command>' at statement level")
    )
    |>> fun (chain, span) -> { chain with Span = span }
    .>> ws

let private effectSigil =
    spanned (
        sigilOpen '!'
        >>= fun envO ->
            ws >>. sigilChain envO
            >>= fun chain ->
                (if exitCodeSpine chain then
                     failFatally
                         "this discards the exit code — bind it (let rc = <command> | exitCode), match on it, or drop '| exitCode'"
                 else
                     preturn chain)
                .>> (pchar ')'
                     <?> "')' — close the sigil on this line, or use line-end '!' for a block of commands")
    )
    |>> (fun (chain, span) ->
        { Kind = EPipe(chain, { Kind = EVar "print"; Span = span })
          Span = span })
    .>> ws

// prefix minus [D:prefix-minus] — F#'s adjacency rule: `-` is prefix
// when the previous char cannot end an operand (start, space, `(`,
// `[`, `{`, `=`, ...) AND the operand is glued to the glyph. In an
// application chain `f -1` means `f (-1)`; `x-1` and `x - 1` stay
// infix. Desugars to `0 - e`, so typing and eval are untouched.
let private postfixAtomFwd, private postfixAtomFwdRef =
    createParserForwardedToRef<Expr, unit> ()

let private negAtom =
    attempt (
        // the trailing '-': `--` is ONE operator token in F# (unknown,
        // rejected) — prefix minus never rides a preceding minus, which
        // also keeps `tool --flag` lines parse-failing into the
        // missing-command diagnosis instead of silently typechecking
        previousCharSatisfiesNot (fun c ->
            Char.IsLetterOrDigit c
            || c = '_'
            || c = ')'
            || c = ']'
            || c = '}'
            || c = '"'
            || c = '\''
            || c = '-')
        >>. getPosition
        .>> pchar '-'
        .>> notFollowedBy (anyOf " \t>")
    )
    // the attempt covers only the prefix DETECTION [D:anchor-before-read]:
    // once committed to prefix-minus the operand parses OUTSIDE it, so a
    // failing operand (an out-of-range literal) propagates its fatal
    // instead of being swallowed by the attempt (a fatal inside an attempt
    // is not a fatal) and merged into a dump
    >>= fun p ->
        postfixAtomFwd
        |>> fun e ->
            { Kind = EBinOp("-", { Kind = EInt 0L; Span = e.Span }, e)
              Span = { Start = pos p; End = e.Span.End } }

// Depth guard [D:depth-guard]: unbounded expression depth blows the
// native stack — the recursive-descent parser on deep NESTING (parens/
// brackets), and check/eval's tree-walk on a deep left-spine (a long
// `a + a + …` chain parses shallow but builds a deep AST). One ceiling,
// two enforcement points: `deepen` stops nesting DURING the parse
// (before the parser itself overflows); the post-parse gate in
// parseLineFull catches the spine. The limit sits far above any real
// program (corpus max nesting is ~11) and well below the crash floor
// (~6000). "Margin for smaller stacks" cannot be a constant — macOS
// test-host threads overflowed at ~420 of the 500 — so deepen ALSO
// probes the actual stack; the ceiling bounds cost, the probe bounds
// the resource, and capacity between them is platform-dependent by
// design [D:depth-guard].
let private maxDepth = 500

let private parseDepth = new System.Threading.ThreadLocal<int>(fun () -> 0)

// Thrown past FParsec's error protocol [D:depth-guard]: a failFatally
// here gets swallowed by the surrounding attempt/choice backtracking
// (a shallower "expecting expression" wins the merge); an exception
// unwinds straight to parseLineFull with the exact position and
// message. Only fires above the ceiling, which no legitimate program
// reaches, so it never perturbs a real parse.
exception private DepthExceeded of Pos

let private deepen (p: Parser<'a, unit>) : Parser<'a, unit> =
    fun stream ->
        parseDepth.Value <- parseDepth.Value + 1

        try
            if
                parseDepth.Value > maxDepth
                || not (System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            then
                raise (DepthExceeded(pos stream.Position))
            else
                p stream
        finally
            parseDepth.Value <- parseDepth.Value - 1

// [for p in xs -> e] — forward-declared: the comprehension needs the
// pattern grammar, which is defined after atom [D:for-do]
let private comprehensionLit, private comprehensionLitRef =
    createParserForwardedToRef<Expr, unit> ()

let private atom =
    deepen (
        choice
            [ attrsRejectHere >>% Unchecked.defaultof<Expr>
              negAtom
              intLit
              tripleLit
              verbatimLit
              strLit
              interpLit
              captureSigil
              effectSigil
              unitLit
              parens
              recordLit
              attempt comprehensionLit
              listLit
              wordAtom ]
    )

let private fieldSuffix = pchar '.' >>. spanned rawWord .>> ws

// xs[i] desugars to Seq.item i xs — F# 6 dotless-indexing whitespace
// rule: NO space = indexing; a space means application (f [1; 2] stays
// an application of a list). Immediacy is checked against the target's
// span end (spans record positions before trailing whitespace).
let private indexDesugar (target: Expr) (idx: Expr) (endPos: Pos) : Expr =
    let span =
        { Start = target.Span.Start
          End = endPos }

    let seqItem =
        { Kind = EField({ Kind = EVar "Seq"; Span = span }, "item", span)
          Span = span }

    { Kind =
        EApp(
            { Kind = EApp(seqItem, idx)
              Span = span },
            target
        )
      Span = span }

let private postfixAtom =
    let rec suffixes (target: Expr) : Parser<Expr, unit> =
        let fieldNext =
            attempt fieldSuffix
            >>= fun (name, fspan) ->
                suffixes
                    { Kind = EField(target, name, fspan)
                      Span = Span.union target.Span fspan }

        let indexNext =
            attempt (
                getPosition
                >>= fun p ->
                    if int p.Line = target.Span.End.Line && int p.Column = target.Span.End.Col then
                        pchar '[' >>. ws >>. expr .>> pchar ']' .>>. getPosition .>> ws
                        |>> fun (idx, endP) -> indexDesugar target idx (pos endP)
                    else
                        fail "whitespace before [ means application"
            )
            >>= suffixes

        fieldNext <|> indexNext <|> preturn target

    atom
    >>= fun target ->
        suffixes target
        |>> fun applied ->
            match target.Kind with
            | EVar "_" when applied <> target ->
                { Kind = ELambda("_", target.Span, applied)
                  Span = applied.Span }
            | _ -> applied

rangeTermRef.Value <-
    choice
        [ negIntLit
          atom .>>. many (attempt fieldSuffix)
          |>> fun (target, fields) ->
              fields
              |> List.fold
                  (fun t (name, fspan) ->
                      { Kind = EField(t, name, fspan)
                        Span = Span.union t.Span fspan })
                  target ]

postfixAtomFwdRef.Value <- postfixAtom

let private appChain =
    many1 postfixAtom
    |>> List.reduce (fun f a ->
        { Kind = EApp(f, a)
          Span = Span.union f.Span a.Span })

// Binder patterns [D:pattern-binders]: params are plain
// idents, `()`, or PARENTHESIZED irrefutable patterns (F# also requires
// the parens in param position). Refutability is a CHECK error.
let private binderParam, private binderParamRef =
    createParserForwardedToRef<Pattern, unit> ()

// let-binder: a full pattern, bare commas allowed at the top
// (`let x, y = ...` — the closed binder grammar makes the comma free)
let private binderPat, private binderPatRef =
    createParserForwardedToRef<Pattern, unit> ()

// duplicate params reject in BOTH sugar positions [D:fun-sugar];
// explicit nested lambdas may still shadow.
let private rejectDupParams (ps: Pattern list) =
    let named =
        ps
        |> List.choose (fun p ->
            match p.PKind with
            | PVar n -> Some(n, p)
            | _ -> None)

    let dup =
        named
        |> List.groupBy fst
        |> List.tryPick (fun (n, g) ->
            if List.length g > 1 then
                Some(n, snd (List.item 1 g))
            else
                None)

    match dup with
    // anchor on the SECOND binder, not the '=' that closed the params
    // [D:anchor-before-read]
    | Some(n, p2) -> failFatallyAtCol p2.PSpan.Start.Col $"duplicate parameter '{n}'"
    | None -> preturn ()

let private curryParams (ps: Pattern list) (value: Expr) : Expr =
    List.foldBack
        (fun (p: Pattern) body ->
            let kind =
                match p.PKind with
                | PVar n -> ELambda(n, p.PSpan, body)
                | PUnit -> ELambda("()", p.PSpan, body)
                | _ -> ELambdaPat(p, body)

            // span covers the param — binder diagnostics point at
            // it, not at the RHS
            { Kind = kind
              Span = Span.union p.PSpan value.Span })
        ps
        value

let private lambda =
    // fun a b -> e desugars to nested lambdas [D:fun-sugar] — the
    // lambda-side twin of let-param sugar, same param set, same
    // curryParams, zero checker surface. The body INHERITS the spine
    // flag [D:multiline-lambda]: block lets in a lambda body on a
    // let-RHS spine take command RHS like any other spine position —
    // and the params extend the ambient resolver for the body, so
    // params shadow PATH exactly as let params do [D:paramful-rhs]
    getPosition
    >>= fun p ->
        (keyword "fun" >>. many1 binderParam >>= fun ps -> rejectDupParams ps >>% ps
         .>> str_ws "->")
        >>= fun ps ->
            let withParams (inner: Parser<'a, unit>) : Parser<'a, unit> =
                fun stream ->
                    let saved = ambientResolver.Value

                    let rec leafNames (pt: Pattern) =
                        match pt.PKind with
                        | PVar n -> [ n ]
                        | PTuple pts -> pts |> List.collect leafNames
                        | _ -> []

                    let names = ps |> List.collect leafNames |> Set.ofList

                    ambientResolver.Value <-
                        { saved with
                            IsKnown = fun n -> Set.contains n names || saved.IsKnown n }

                    try
                        inner stream
                    finally
                        ambientResolver.Value <- saved

            withParams seqExpr
            |>> fun body ->
                let inner = curryParams ps body

                { inner with
                    Span = { Start = pos p; End = body.Span.End } }

// let f x y = e desugars to nested lambdas [D:let-param-sugar].
// Params are plain idents OR () — the unit param pins its type in the
// checker (the name "()" is unforgeable through declarations); other
// pattern params stay rejected.

let private letIn =
    let patForm =
        attempt (
            keyword "let" >>. binderPat .>> str_ws "="
            >>= fun b ->
                match b.PKind with
                | PVar _
                | PCase(_, None) -> fail "plain binder takes the ident path"
                | _ -> preturn b
        )

    choice
        [ pipe3
              getPosition
              (patForm .>>. (seqExpr >>= pipeOrHint) .>> keyword "in")
              seqExpr
              (fun p (binder, value) body ->
                  { Kind = ELetPat(binder, value, body)
                    Span = { Start = pos p; End = body.Span.End } })
          getPosition
          >>= fun p ->
              (keyword "let" >>. spanned ident .>>. many binderParam
               >>= fun (n, ps) -> rejectDupParams ps >>% (n, ps))
              .>> str_ws "="
              >>= fun ((name, nameSpan), ps) ->
                  // the block-let command RHS [D:block-let-cmd]: the same
                  // grammar the top-level bare RHS takes (in-stop argv, one
                  // gate), live only on the assembled statement spine
                  let cmdRhs: Parser<Expr, unit> =
                      fun stream ->
                          if letCmdOk.Value then
                              letRhsCmd stream
                          else
                              fail "block-let command RHS is spine-only" stream

                  (cmdRhs <|> ((seqExpr >>= pipeOrHint))) .>> keyword "in"
                  >>= fun value ->
                      withAmbientName name seqExpr
                      |>> fun body ->
                          { Kind = ELet(name, nameSpan, curryParams ps value, body)
                            Span = { Start = pos p; End = body.Span.End } } ]

let private binOp op l r =
    { Kind = EBinOp(op, l, r)
      Span = Span.union l.Span r.Span }

let private pipeOp l r =
    { Kind = EPipe(l, r)
      Span = Span.union l.Span r.Span }

let private mkOpp (withPipe: bool) =
    let opp = OperatorPrecedenceParser<Expr, unit, unit>()

    if withPipe then
        opp.AddOperator(InfixOperator("|>", ws, 1, Associativity.Left, pipeOp))

    opp.AddOperator(InfixOperator("||", ws, 2, Associativity.Left, binOp "||"))
    opp.AddOperator(InfixOperator("&&", ws, 3, Associativity.Left, binOp "&&"))
    opp.AddOperator(InfixOperator("==", ws, 4, Associativity.Left, binOp "=="))
    opp.AddOperator(InfixOperator("<>", ws, 4, Associativity.Left, binOp "<>"))
    opp.AddOperator(InfixOperator(">=", ws, 4, Associativity.Left, binOp ">="))
    opp.AddOperator(InfixOperator("<=", ws, 4, Associativity.Left, binOp "<="))
    // composition [D:composition-operators] at the PIPE's level:
    // `xs |> f >> g` is `(xs |> f) >> g` (F#'s shared infix class) —
    // the idiom needs parens, `xs |> (f >> g)`. OPP's operator trie
    // keeps > / >= / >> apart.
    opp.AddOperator(InfixOperator(">>", ws, 1, Associativity.Left, binOp ">>"))
    opp.AddOperator(InfixOperator("<<", ws, 1, Associativity.Left, binOp "<<"))
    opp.AddOperator(InfixOperator(">", ws, 4, Associativity.Left, binOp ">"))
    opp.AddOperator(InfixOperator("<", ws, 4, Associativity.Left, binOp "<"))
    opp.AddOperator(InfixOperator("+", ws, 6, Associativity.Left, binOp "+"))
    opp.AddOperator(InfixOperator("-", notFollowedBy (pchar '>') >>. ws, 6, Associativity.Left, binOp "-"))
    opp.AddOperator(InfixOperator("*", ws, 7, Associativity.Left, binOp "*"))
    opp.AddOperator(InfixOperator("/", ws, 7, Associativity.Left, binOp "/"))
    opp

let private opp = mkOpp true
let private segOpp = mkOpp false

let private pat, private patRef = createParserForwardedToRef<Pattern, unit> ()

let private patWord =
    // the keyword check DOMINATES [D:anchor-before-read], outside the
    // word's own attempt: a keyword is never a valid pattern, so where the
    // context is committed (a match arm past its `|`, a lambda past `fun`)
    // the fatal surfaces the teaching; where an outer attempt encloses it
    // (params, destructure) it is swallowed as before — no worse.
    (attempt (spanned rawWord)
     >>= fun (w, span) ->
         if w = "true" || w = "false" then
             preturn (w, span)
         elif w = "function" then
             failFatallyAtCol span.Start.Col "'function' is reserved; write 'fun x -> match x with'"
         elif keywords.Contains w then
             failFatallyAtCol span.Start.Col $"'{w}' is a keyword"
         else
             preturn (w, span))
    .>> ws

// literal patterns [D:literal-patterns]: int and string pin
// the scrutinee; () is the irrefutable unit pattern
let private patLit =
    choice
        [ attempt (spanned (pstring "()") .>> ws)
          |>> fun (_, span) -> { PKind = PUnit; PSpan = span }
          attempt (getPosition .>>. spanned (opt (pchar '-') .>>. many1Satisfy isDigit) .>> ws)
          >>= fun (at, ((neg, digits), span)) ->
              match System.Int64.TryParse((if neg.IsSome then "-" else "") + digits) with
              | true, n -> preturn { PKind = PInt n; PSpan = span }
              // anchor at the literal start [D:anchor-before-read]
              | false, _ -> failFatallyAt at $"int literal out of range (64-bit): {digits}"
          spanned (pstring "\"\"\"" >>. tripleBody .>> pstring "\"\"\"") .>> ws
          |>> fun (s, span) -> { PKind = PStr s; PSpan = span }
          spanned (pstring "@\"" >>. verbatimBody .>> pchar '"') .>> ws
          |>> fun (s, span) -> { PKind = PStr s; PSpan = span }
          spanned (between (pchar '"') (pchar '"') (manyChars stringChar)) .>> ws
          |>> fun (s, span) -> { PKind = PStr s; PSpan = span } ]

// one-or-tuple over comma-separated patterns — shared by paren
// interiors and binder positions (was written twice)
let private commaPats =
    sepBy1 pat (str_ws ",")
    |>> function
        | [ one ] -> one
        | many ->
            { PKind = PTuple many
              PSpan =
                { Start = (List.head many).PSpan.Start
                  End = (List.last many).PSpan.End } }

let private patParens = between (str_ws "(") (str_ws ")") commaPats

// seq patterns [D:seq-patterns]: [] and fixed-arity [p; q]
let private patSeq =
    spanned (str_ws "[" >>. sepBy pat (str_ws ";") .>> pchar ']') .>> ws
    |>> fun (ps, span) ->
        match ps with
        | [] -> { PKind = PSeqNil; PSpan = span }
        | ps -> { PKind = PSeqList ps; PSpan = span }

let private patAtom =
    choice
        [ patLit
          patParens
          patSeq
          patWord
          |>> fun (w, span) ->
              let kind =
                  if w = "_" then PWildcard
                  elif w = "true" then PBool true
                  elif w = "false" then PBool false
                  elif Char.IsUpper w[0] then PCase(w, None)
                  else PVar w

              { PKind = kind; PSpan = span } ]

// The Regex position parses any string kind and tags it; the
// checker enforces raw-only there [D:raw-strings].
let private regexPatternLit =
    choice
        [ spanned (pstring "\"\"\"" >>. tripleBody .>> pstring "\"\"\"")
          |>> fun (p, sp) -> p, sp, true
          spanned (pstring "@\"" >>. verbatimBody .>> pchar '"')
          |>> fun (p, sp) -> p, sp, true
          spanned (between (pchar '"') (pchar '"') (manyChars stringChar))
          |>> fun (p, sp) -> p, sp, false ]
    .>> ws

let private patCore =
    choice
        [ patLit
          patParens
          patSeq
          patWord
          >>= fun (w, span) ->
              if w = "_" then
                  preturn { PKind = PWildcard; PSpan = span }
              elif w = "true" then
                  preturn { PKind = PBool true; PSpan = span }
              elif w = "false" then
                  preturn { PKind = PBool false; PSpan = span }
              elif w = "Regex" then
                  // bespoke pattern form, literal-only [D:regex-pattern]
                  choice
                      [ regexPatternLit .>>. patAtom
                        |>> fun ((pat, litSpan, raw), binder) ->
                            { PKind = PRegex(pat, litSpan, raw, binder)
                              PSpan =
                                { Start = span.Start
                                  End = binder.PSpan.End } }
                        failFatally
                            "Regex patterns take a LITERAL string; computed patterns live on the expression side (Str.isMatch / Str.rmatch)" ]
              elif Char.IsUpper w[0] then
                  opt patAtom
                  |>> fun arg ->
                      let e = arg |> Option.map (fun a -> a.PSpan.End) |> Option.defaultValue span.End

                      { PKind = PCase(w, arg)
                        PSpan = { Start = span.Start; End = e } }
              else
                  preturn { PKind = PVar w; PSpan = span } ]

// cons is the pattern grammar's one infix: right-associative, tighter
// than comma, looser than constructor application [D:seq-patterns]
patRef.Value <-
    sepBy1 patCore (str_ws "::")
    |>> fun ps ->
        let rec build =
            function
            | [ last ] -> last
            | h :: rest ->
                let t = build rest

                { PKind = PCons(h, t)
                  PSpan =
                    { Start = h.PSpan.Start
                      End = t.PSpan.End } }
            | [] -> failwith "sepBy1 never yields empty"

        build ps


binderParamRef.Value <-
    choice
        [ attrsRejectHere >>% Unchecked.defaultof<Pattern>
          spanned (pstring "()") .>> ws
          |>> fun (_, span) -> { PKind = PUnit; PSpan = span }
          identSpanned |>> fun (n, span) -> { PKind = PVar n; PSpan = span }
          patParens ]

binderPatRef.Value <- commaPats

let private fromExpr =
    spanned (
        keyword "from" >>. ident
        .>>. opt (
            attempt (
                identSpanned
                >>= fun (w, _) -> if Char.IsUpper w[0] then preturn w else fail "type name"
            )
        )
    )
    |>> fun ((fmt, tyName), span) ->
        { Kind = EFrom(fmt, tyName)
          Span = span }

let private toExpr =
    spanned (keyword "to" >>. ident)
    |>> fun (fmt, span) -> { Kind = ETo fmt; Span = span }

let private matchArm =
    // bare-comma tuple PATTERNS [D:bare-comma]: the arm rides the same
    // one-or-tuple production as binder positions — `when`/`->` are not
    // commas, so the guard sits OUTSIDE the tuple by construction
    commaPats .>>. opt (keyword "when" >>. expr) .>> str_ws "->" .>>. seqExpr
    |>> fun ((p, guard), body) -> p, guard, body

let private matchExpr =
    pipe3
        getPosition
        // the scrutinee is an ordinary expression at the let-RHS's
        // precedence [D:bare-comma]: `match a, b with` builds the tuple;
        // `with` is reserved, so it terminates the comma chain cleanly
        (keyword "match" >>. commaExpr .>> keyword "with")
        // a consumed '|' COMMITS to its arm [D:arm-commit] — the
        // consumed-separator law's second instance (seq-commit's twin):
        // a failing arm RHS reports at ITS OWN site instead of silently
        // ending the arm list, whose leftover '|' then counterfeited
        // the bare-pipe fatal's "completed expression" customer
        (opt (str_ws "|") >>. matchArm .>>. many (str_ws "|" >>. matchArm))
        (fun p scrut (arm0, rest) ->
            let arms = arm0 :: rest
            let lastBody = List.last arms |> fun (_, _, b) -> b

            { Kind = EMatch(scrut, arms)
              Span =
                { Start = pos p
                  End = lastBody.Span.End } })

let private ifExpr =
    // elif is SPELLING [D:elif]: `elif c then e` desugars at parse to
    // `else if c then e` — zero checker surface; the trailing else
    // stays optional under the unit rule, F#'s chain exactly
    pipe5
        getPosition
        (keyword "if" >>. expr)
        (keyword "then" >>. seqExpr)
        (many ((keyword "elif" >>. expr) .>>. (keyword "then" >>. seqExpr)))
        (opt (keyword "else" >>. seqExpr))
        (fun p cond thn elifs els ->
            let rec build clauses =
                match clauses with
                | [] -> els
                | (c: Expr, t: Expr) :: rest ->
                    let inner = build rest
                    let e = (inner |> Option.defaultValue t).Span.End

                    Some
                        { Kind = EIf(c, t, inner)
                          Span = { Start = c.Span.Start; End = e } }

            let chained = build elifs
            let endPos = (chained |> Option.defaultValue thn).Span.End

            { Kind = EIf(cond, thn, chained)
              Span = { Start = pos p; End = endPos } })

// for/do [D:for-do]: the general effect loop -- F#'s own statement form,
// desugared AT PARSE to `xs |> Seq.iter (fun p -> body)` (the reifier
// precedent: the typed tree never sees `for`, so checking, warnings,
// hover, and eval all ride the existing machinery). A BARE COMMAND body
// is implicit `!(...)` -- `for f in files do git add $f` streams and
// raises per iteration, the natural shell shape; known heads fall
// through to the expression body exactly as the statement classifier
// decides.
let private forExpr =
    let cmdBody =
        attempt (
            spanned (sigilChain None)
            >>= fun (chain, span) ->
                if exitCodeSpine chain then
                    failFatally
                        "this discards the exit code -- bind it (let rc = <command> | exitCode), match on it, or drop '| exitCode'"
                else
                    preturn
                        { Kind = EPipe(chain, { Kind = EVar "print"; Span = span })
                          Span = span }
        )

    pipe4
        (getPosition .>> keyword "for")
        (binderPat .>> keyword "in")
        (expr .>> keyword "do")
        (cmdBody <|> seqExpr)
        (fun p binder source body ->
            let span = { Start = pos p; End = body.Span.End }
            let mk k = { Kind = k; Span = span }
            let iter = mk (EField(mk (EVar "Seq"), "iter", span))
            mk (EApp(mk (EApp(iter, mk (ELambdaPat(binder, body)))), source)))

// [for p in xs -> e] [D:for-do]: F#'s list comprehension, desugared to
// `xs |> Seq.map (fun p -> e) |> Seq.force` -- Seq.force keeps the list
// literal's EAGERNESS contract. The desugar bypasses EList entirely, so
// list-literal inference (the empty-list fresh var, element unification)
// is untouched -- the session finding: same path as the statement form.
comprehensionLitRef.Value <-
    spanned (
        attempt (pchar '[' >>. ws >>. keyword "for") >>. binderPat .>> keyword "in"
        .>>. expr
        .>> str_ws "->"
        .>>. expr
        .>> (pchar ']' <?> "']' to close the comprehension")
    )
    |>> (fun (((binder, source), elem), span) ->
        let mk k = { Kind = k; Span = span }

        let field name =
            mk (EField(mk (EVar "Seq"), name, span))

        let mapped =
            mk (EApp(mk (EApp(field "map", mk (ELambdaPat(binder, elem)))), source))

        mk (EApp(field "force", mapped)))
    .>> ws

// ---- the yaml district [D:yaml-district] ---------------------------------
// `yaml` followed by the machine sentinel can ONLY come from the
// assembler's wrap (the sentinel is unproduceable), so `yaml` needs no
// reservation — a binding named yaml never collides. The tail is
// sentinel-separated VERBATIM block lines with indentation RELATIVE to
// the block's first line; the template parser reconstructs the 2D
// structure, and fragment parses run PADDED so every span lands at its
// true logical column (translate then maps it physically).

// run a weir sub-parser on a fragment at logical column `col` — the
// padding aligns FParsec's columns with the logical line, so no span
// shifting is ever needed
let private runFragment (col: int) (frag: string) (p: Parser<'a, unit>) : Result<'a, string> =
    match run (ws >>. p .>> eof) (System.String(' ', col - 1) + frag) with
    | Success(v, _, _) -> Result.Ok v
    | Failure(msg, _, _) ->
        let firstLine =
            (msg.Split('\n') |> Array.filter (fun l -> l.Trim() <> "") |> Array.tryLast)
            |> Option.defaultValue msg

        Result.Error(firstLine.Trim())

let private isIdentWord (w: string) =
    w.Length > 0
    && (System.Char.IsLetter w[0] || w[0] = '_')
    && w |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

// a template VALUE slot: a whole-slot splice ($name / $(expr)), or a
// subset scalar. Mid-text `$` is LITERAL (compute with $(...) instead).
let private tplValueSlot (col: int) (text: string) : Result<YamlTpl, string * int> =
    let lead = text.Length - text.TrimStart().Length
    let t = text.Trim()
    let tCol = col + lead

    let mkSpan len =
        { Start = { Line = 1; Col = tCol }
          End = { Line = 1; Col = tCol + len } }

    if t.StartsWith "$" then
        let inner = t.Substring 1

        if inner.StartsWith "(" then
            match runFragment (tCol + 1) inner expr with
            | Result.Ok e -> Result.Ok(YtSplice e)
            | Result.Error m -> Result.Error($"in this splice: {m}", tCol)
        elif isIdentWord inner then
            Result.Ok(
                YtSplice
                    { Kind = EVar inner
                      Span = mkSpan t.Length }
            )
        else
            Result.Error("a $ splice is $name or $(expr)", tCol)
    else
        match Yaml.scalarCore t with
        | Result.Error m -> Result.Error(m, tCol)
        // the empty slot is null — carried as an empty PLAIN scalar,
        // constructed as YNull at eval
        | Result.Ok None -> Result.Ok(YtScalar("", false, mkSpan 0))
        | Result.Ok(Some(txt, q)) -> Result.Ok(YtScalar(txt, q, mkSpan t.Length))

let private tplForHeader (col: int) (text: string) : Result<Pattern * Expr, string * int> =
    match runFragment col text (keyword "for" >>. binderPat .>> keyword "in" .>>. expr) with
    | Result.Ok(b, src) -> Result.Ok(b, src)
    | Result.Error m -> Result.Error($"in this for: {m}", col)

// block scalar content in a district [D:block-scalars]: the lines are
// BYTES — consumed here, before the splice/for scanners ever see them,
// so `$name`, `$(expr)`, and `for x in xs` survive verbatim. Blank
// district lines ride the sentinel as empty verbatim lines (the
// assembler's yaml-blank join) and become newlines here.
let private tplBlockScalar
    (lines: (int * int * string)[])
    (start: int)
    (fin: int)
    (keep: bool)
    (headerCol: int)
    : Result<YamlTpl, string * int> =
    let content = ResizeArray<int * string>() // (col, reconstructed line)

    for k in start .. fin - 1 do
        let c, r, t = lines[k]

        content.Add(c, (if t.Trim() = "" then "" else String.replicate r " " + t))

    while content.Count > 0 && snd content[content.Count - 1] = "" do
        content.RemoveAt(content.Count - 1)

    if content.Count = 0 then
        Result.Error("a block scalar header needs an indented block below it", headerCol)
    else
        let cIndent =
            content
            |> Seq.filter (fun (_, l) -> l <> "")
            |> Seq.head
            |> snd
            |> Yaml.indentOf

        match content |> Seq.tryFind (fun (_, l) -> l <> "" && Yaml.indentOf l < cIndent) with
        | Some(c, _) -> Result.Error("this line sits left of the block scalar's content indentation", c)
        | None ->
            let body =
                content
                |> Seq.map (fun (_, l) -> if l = "" then "" else l.Substring cIndent)
                |> String.concat "\n"

            Result.Ok(
                YtBlock(
                    (if keep then body + "\n" else body),
                    { Start = { Line = 1; Col = headerCol }
                      End = { Line = 1; Col = headerCol + 1 } }
                )
            )

// structure-transparency for template lines [D:district-hash]: blanks
// and full-line comment shapes are BYTES inside block-scalar content
// (consumed before these loops) and invisible to STRUCTURE everywhere
// else — one predicate, consumed by the units skip, the extent scan,
// the nested-content counter, and the nested-indent probe (the fourth
// site was found by the fuzz production's comment-insertion transform)
let private tplTransparent (t: string) =
    t.Trim() = "" || t.TrimStart().StartsWith "#" || t.TrimStart().StartsWith "//"

// one block-level unit of a template
type private TplUnit =
    | UPair of YamlTplKey * YamlTpl
    | UItem of YamlTplItem
    | UFor of Pattern * Expr * YamlTpl // body block, context-checked later

let rec private parseTplBlock
    (lines: (int * int * string)[]) // (logical col of text start, rel indent, text)
    (start: int)
    (fin: int)
    (indent: int)
    : Result<YamlTpl, string * int> =
    if start >= fin then
        Result.Ok(
            YtScalar(
                "",
                false,
                { Start = { Line = 1; Col = 1 }
                  End = { Line = 1; Col = 1 } }
            )
        )
    else
        let firstCol, _, _ = lines[start]

        let spanAt col =
            { Start = { Line = 1; Col = col }

              End = { Line = 1; Col = col + 1 } }

        // the first non-blank line's rel in a range (nested-block indent)
        let firstContentRel a b fallback =
            let mutable k = a
            let mutable found = fallback

            while k < b do
                let (_, r, t) = lines[k]

                // comment-shaped lines are structure-transparent here
                // exactly as in the units loop — a shallow `// noise`
                // between a key and its block must not set the block's
                // indent (the yaml fuzz production's first catch)
                if found = fallback && not (tplTransparent t) then
                    found <- r
                    k <- b
                else
                    k <- k + 1

            found

        // collect the units at exactly this indent; blank lines are
        // structure-transparent (they are BYTES only inside a block
        // scalar's content [D:block-scalars])
        let rec units i acc =
            if i >= fin then
                Result.Ok(List.rev acc)
            else
                let col, rel, textRaw = lines[i]

                // mid-line ` #` on a STRUCTURE line is a comment — YAML's
                // own rule, the read side's rule, now the district's too
                // [D:district-hash]; quoted regions and $() holes are
                // data, and block content was consumed as bytes before
                // this loop ever ran
                let text = Yaml.stripDistrictComment textRaw

                if tplTransparent text then
                    // blanks and full-line `#` comments are structure-
                    // transparent; both are BYTES inside a block scalar's
                    // content, which consumed its lines before this loop
                    units (i + 1) acc
                elif rel < indent then
                    Result.Ok(List.rev acc)
                elif rel > indent then
                    Result.Error("unexpected indentation in this yaml block", col)
                else
                    // the extent of this unit's nested block (blanks ride along)
                    let mutable j = i + 1

                    while j < fin && (let (_, r, t) = lines[j] in tplTransparent t || r > indent) do
                        j <- j + 1

                    // blanks and `#` lines ride the extent but are not
                    // structure — nested-block decisions count CONTENT
                    let hasNested =
                        seq { i + 1 .. j - 1 }
                        |> Seq.exists (fun k ->
                            let (_, _, t) = lines[k]

                            not (tplTransparent t))

                    if text.StartsWith "for " then
                        match tplForHeader col text with
                        | Result.Error e -> Result.Error e
                        | Result.Ok(binder, src) ->
                            match
                                parseTplBlock
                                    lines
                                    (i + 1)
                                    j
                                    (indent
                                     + (if hasNested then
                                            (firstContentRel (i + 1) j (indent + 4)) - indent
                                        else
                                            4))
                            with
                            | Result.Error e -> Result.Error e
                            | Result.Ok body -> units j (UFor(binder, src, body) :: acc)
                    elif text.StartsWith "- " || text.TrimEnd() = "-" then
                        let inlinePart = if text.TrimEnd() = "-" then "" else text.Substring 2
                        let inlineCol = col + 2

                        let itemR =
                            match Yaml.blockHeader inlinePart with
                            | Some(Result.Error msg) -> Result.Error(msg, inlineCol)
                            | Some(Result.Ok keep) -> tplBlockScalar lines (i + 1) j keep inlineCol |> Result.map YtItem
                            | None ->

                                if inlinePart.Trim() = "" then
                                    if hasNested then
                                        let r1 = firstContentRel (i + 1) j (indent + 2)
                                        parseTplBlock lines (i + 1) j r1 |> Result.map YtItem
                                    else
                                        Result.Ok(YtItem(YtScalar("", false, spanAt col)))
                                else
                                    match Yaml.splitKey 0 inlinePart with
                                    | Some _ ->
                                        // compact map item: the first entry lives on
                                        // this line at a VIRTUAL rel of item+2
                                        let shifted =
                                            Array.append [| (inlineCol, indent + 2, inlinePart) |] lines[i + 1 .. j - 1]

                                        parseTplBlock shifted 0 shifted.Length (indent + 2) |> Result.map YtItem
                                    | None ->
                                        if hasNested then
                                            Result.Error("a scalar sequence item cannot have a nested block", col)
                                        else
                                            tplValueSlot inlineCol inlinePart |> Result.map YtItem

                        match itemR with
                        | Result.Error e -> Result.Error e
                        | Result.Ok item -> units j (UItem item :: acc)
                    else
                        match Yaml.splitKey 0 text with
                        | None -> Result.Error("expected 'key:', '- ', or 'for … in …' in this yaml block", col)
                        | Some(rawKey, rest) ->
                            let keyR =
                                if rawKey.StartsWith "$" then
                                    let kIdent = rawKey.Substring 1

                                    if isIdentWord kIdent then
                                        Result.Ok(
                                            YtKeySplice
                                                { Kind = EVar kIdent
                                                  Span = spanAt col }
                                        )
                                    else
                                        Result.Error("a key splice is $name (string-typed)", col)
                                else
                                    // key span: text start through the key's
                                    // width — schema validation anchors here
                                    Result.Ok(
                                        YtKeyLit(
                                            rawKey,
                                            { Start = { Line = 1; Col = col }
                                              End = { Line = 1; Col = col + rawKey.Length } }
                                        )
                                    )

                            match keyR with
                            | Result.Error e -> Result.Error e
                            | Result.Ok key ->
                                let valueR =
                                    match Yaml.blockHeader rest with
                                    | Some(Result.Error msg) -> Result.Error(msg, col + text.Length - rest.Length)
                                    | Some(Result.Ok keep) ->
                                        tplBlockScalar lines (i + 1) j keep (col + text.Length - rest.Length)
                                    | None ->

                                        if rest.Trim() = "" then
                                            if hasNested then
                                                let r1 = firstContentRel (i + 1) j (indent + 4)
                                                parseTplBlock lines (i + 1) j r1
                                            else
                                                Result.Ok(YtScalar("", false, spanAt col))
                                        elif hasNested then
                                            Result.Error("this key has both an inline value and a nested block", col)
                                        else
                                            tplValueSlot (col + text.Length - rest.Length) rest

                                match valueR with
                                | Result.Error e -> Result.Error e
                                | Result.Ok v -> units j (UPair(key, v) :: acc)

        match units start [] with
        | Result.Error e -> Result.Error e
        | Result.Ok us ->
            let hasPair =
                us
                |> List.exists (function
                    | UPair _ -> true
                    | _ -> false)

            let hasItem =
                us
                |> List.exists (function
                    | UItem _ -> true
                    | _ -> false)

            if hasPair && hasItem then
                Result.Error("this yaml block mixes 'key:' entries and '- ' items", firstCol)
            elif hasPair then
                us
                |> List.fold
                    (fun acc u ->
                        acc
                        |> Result.bind (fun es ->
                            match u with
                            | UPair(k, v) -> Result.Ok(YtPair(k, v) :: es)
                            | UFor(b, src, body) ->
                                match body with
                                | YtMap(entries, _) -> Result.Ok(YtForEntries(b, src, entries) :: es)
                                | _ -> Result.Error("a for under a mapping must yield 'key: value' entries", firstCol)
                            | UItem _ -> Result.Error("unreachable: mixed block", firstCol)))
                    (Result.Ok [])
                |> Result.map (fun es -> YtMap(List.rev es, spanAt firstCol))
            elif hasItem then
                us
                |> List.fold
                    (fun acc u ->
                        acc
                        |> Result.bind (fun its ->
                            match u with
                            | UItem it -> Result.Ok(it :: its)
                            | UFor(b, src, body) ->
                                let bodyItems =
                                    match body with
                                    | YtSeq(items, _) -> items
                                    | other -> [ YtItem other ]

                                Result.Ok(YtForItems(b, src, bodyItems) :: its)
                            | UPair _ -> Result.Error("unreachable: mixed block", firstCol)))
                    (Result.Ok [])
                |> Result.map (fun its -> YtSeq(List.rev its, spanAt firstCol))
            else
                // only for-units (or a single scalar/splice line)
                match us with
                | [ UFor(b, src, body) ] ->
                    match body with
                    | YtMap(entries, _) -> Result.Ok(YtMap([ YtForEntries(b, src, entries) ], spanAt firstCol))
                    | YtSeq(items, _) -> Result.Ok(YtSeq([ YtForItems(b, src, items) ], spanAt firstCol))
                    | other -> Result.Ok(YtSeq([ YtForItems(b, src, [ YtItem other ]) ], spanAt firstCol))
                | [] ->
                    // a single non-key non-item line: a scalar/splice document
                    let col, _, text = lines[start]

                    if fin > start + 1 then
                        Result.Error("expected 'key:', '- ', or 'for … in …' in this yaml block", col)
                    else
                        tplValueSlot col text
                | _ -> Result.Error("multiple for blocks need a surrounding mapping or sequence context", firstCol)

let private yamlDistrict: Parser<Expr, unit> =
    attempt (
        getPosition .>> pstring "yaml"
        .>>. opt (
            pstring " schema="
            >>. many1Satisfy (fun c -> System.Char.IsLower c || System.Char.IsDigit c || c = '-')
        )
        .>> followedBy (pstring sibSepStr)
    )
    >>= fun (startP, schemaName) ->
        getPosition .>>. manyChars anyChar
        >>= fun (tailP, tail) ->
            let parts = tail.Split sibSep
            // parts[0] is the empty prefix before the first sentinel
            let lineList = System.Collections.Generic.List<int * int * string>()
            let mutable colCursor = int tailP.Column

            for i in 1 .. parts.Length - 1 do
                let part = parts[i]
                let partStart = colCursor + 1 // past the sentinel char
                let rel = Yaml.indentOf part
                let content = part.Substring rel

                if content.TrimEnd() <> "" then
                    // UNTRIMMED: trailing whitespace is bytes inside a
                    // block scalar; structure decisions Trim on their own
                    lineList.Add((partStart + rel, rel, content))
                else
                    // a blank district line is BYTES inside a block
                    // scalar [D:block-scalars]; the structure loops skip it
                    lineList.Add((partStart, 0, ""))

                colCursor <- partStart + part.Length

            let lines = lineList.ToArray()

            if lines |> Array.forall (fun (_, _, t) -> t = "") then
                failFatallyAtCol (int startP.Column) "this yaml block is empty"
            else
                let baseRel = let (_, r, _) = lines[0] in r

                match parseTplBlock lines 0 lines.Length baseRel with
                | Result.Error(msg, col) -> failFatallyAtCol col msg
                | Result.Ok tpl ->
                    let endCol = colCursor

                    preturn
                        { Kind = EYaml(tpl, schemaName)
                          Span =
                            { Start = pos startP
                              End = { Line = int startP.Line; Col = endCol } } }

opp.TermParser <-
    choice
        [ lambda
          letIn
          ifExpr
          matchExpr
          forExpr
          yamlDistrict
          fromExpr
          toExpr
          appChain ]

updateSourceRef.Value <-
    (let u = mkOpp true
     u.TermParser <- appChain
     u.ExpressionParser)

segOpp.TermParser <- choice [ lambda; letIn; ifExpr; matchExpr; fromExpr; toExpr; appChain ]
exprRef.Value <- opp.ExpressionParser

commaExprRef.Value <-
    expr .>>. many (attempt (str_ws "," >>. expr))
    |>> fun (first, rest) ->
        match rest with
        | [] -> first
        | _ ->
            let all = first :: rest

            { Kind = ETuple all
              Span = Span.union first.Span (List.last all).Span }

// right-nested ESeq over a non-empty element list — shared by seqExpr
// and topLet's command-first RHS so both fold identically
let private foldSeqExpr (all: Expr list) : Expr =
    List.foldBack
        (fun e acc ->
            match acc with
            | None -> Some e
            | Some tail ->
                Some
                    { Kind = ESeq(e, tail)
                      Span = Span.union e.Span tail.Span })
        all
        None
    |> Option.defaultWith (fun () -> failwith "foldSeqExpr: empty")

// the sequencing separator [D:seq-commit][D:sibling-sentinel]: a
// user-typed ';' OR the machine sibling sentinel. Both COMMIT (no
// attempt) — a failing element must not un-consume the separator.
// the sentinel is unproduceable [D:sibling-sentinel], so it must never
// surface in an expected-set — relabel the whole separator as ';', the
// only form a user can type
let private seqSep = (str_ws ";" <|> str_ws sibSepStr) <?> "';'"

seqExprRef.Value <-
    // a consumed separator COMMITS to its element [D:seq-commit]: a
    // failing element must not un-consume it — the backtrack would
    // re-parse the tail OUTSIDE its let-in scope, where check's
    // assume-resolver claims the then-unknown binding as a phantom command
    commaExpr .>>. many (seqSep >>. commaExpr)
    |>> fun (first, rest) ->
        match rest with
        | [] -> first
        | _ -> foldSeqExpr (first :: rest)

let private segExpr = segOpp.ExpressionParser


let private cmdWordChar c =
    not (System.Char.IsWhiteSpace c)
    && c <> '|'
    && c <> '('
    && c <> ')'
    && c <> '"'
    && c <> '\''
    && c <> '$'
    // command mode STOPS at the machine sibling boundary
    // [D:sibling-sentinel]; a user ';' is still a bareword (prior-bleed)
    && c <> sibSep

let private cmdWord = many1Satisfy cmdWordChar

let private isIdentLike (w: string) =
    isIdentStart w[0] && w |> Seq.forall isIdentCont

let private singleQuoted =
    spanned (between (pchar '\'') (pchar '\'') (manySatisfy ((<>) '\'')) |>> EStr)
    |>> mkExpr
    .>> ws

// A splice glued into a word under construction is fatal [D:argv-splat]:
// the glued prefix would silently drop (`--flag=$x` → `["--flag="; x]`),
// so name the two honest spellings. Shared by `$x` and `$@xs` — the
// scalar path teaches its own fix, the splat path teaches per-element.
let private notMidWord (teach: string) : Parser<unit, unit> =
    // consume the '$' then anchor back [D:anchor-before-read]: the sibling
    // cmdArg alternatives all fail at the '$', so a non-consuming fatal
    // merges their expected-set into a dump — consuming clears it
    (previousCharSatisfiesNot (fun c -> c = ' ' || c = '\t') >>. getPosition
     >>= fun at -> pchar '$' >>. failFatallyAt at teach)
    <|> preturn ()

let private spliceVar =
    // gate the mid-word check behind the `$` (as splat gates behind `$@`)
    // so it fires only on an actual splice, never on a plain bareword
    lookAhead (pchar '$')
    >>. notMidWord
            "a splice cannot join a word under construction — spell it with a space (`--flag $x`) or an interpolated arg (`$\"--flag={x}\"`)"
    >>. spanned (pchar '$' >>. rawWord |>> EVar)
    |>> mkExpr
    .>> ws

// $@name / $@(expr) — the argv splat [D:argv-splat]: N words, never
// re-split. `$@"` stays the parked interpolated-verbatim opener's
// cell (lookahead-decided, pinned); mid-word adjacency is fatal — N
// words cannot live inside one word under construction.
let private spliceSplat: Parser<Expr, unit> =
    lookAhead (attempt (pstring "$@" .>> notFollowedBy (pchar '"')))
    >>. notMidWord
            "a splat cannot join a word under construction — map the prefix onto the elements, or pass it as a separate argument"
    >>. spanned (
        pstring "$@"
        >>. (choice
                 [ rawWord |>> Choice1Of2
                   (pchar '(' >>. ws >>. seqExpr .>> ws .>> pchar ')') |>> Choice2Of2 ]
             <?> "a name or (expr) after '$@' — the argv splat")
    )
    |>> (fun (c, span) ->
        let inner =
            match c with
            | Choice1Of2 n -> { Kind = EVar n; Span = span }
            | Choice2Of2 e -> e

        { Kind = ESplat inner; Span = span })
    .>> ws

let private cmdArgWith (stopAtIn: bool) =
    let bareword =
        // the machine boundary, arg face [D:yaml-district]: a bareword
        // GLUED to the sentinel can only be the assembler's yaml wrap
        // (`yaml schema=x` + glued sentinel) — statement joins SPACE it.
        // Refusing here makes the command path fall through to the
        // district arm, exactly as the head guard does for bare `yaml`.
        if stopAtIn then
            // In a let RHS, a bareword `in` would silently become argv (the
            // let...in cliff). Stop instead: the parse falls through to the
            // expression grammar and surfaces a check error. Quote "in" to
            // pass it to a command from a let RHS.
            notFollowedBy (attempt (pstring "in" .>> notFollowedBy (satisfy cmdWordChar)))
            >>. (spanned (cmdWord |>> EStr) |>> mkExpr .>> notFollowedBy (pchar sibSep) .>> ws)
        else
            spanned (cmdWord |>> EStr) |>> mkExpr .>> notFollowedBy (pchar sibSep) .>> ws

    choice
        [ strLit
          singleQuoted
          interpLit
          captureSigil
          spliceSplat
          spliceVar
          parens
          bareword ]

let private cmdArg = cmdArgWith false

type private HeadKind =
    | ExternalHead
    | BuiltinHead

let private commandSegment
    (builtinHeads: bool)
    (argP: Parser<Expr, unit>)
    (sigilEnv: Expr option)
    (r: Resolver)
    : Parser<Expr, unit> =
    let head =
        spanned (opt (pchar '^') .>>. cmdWord)
        // the machine boundary [D:yaml-district]: a head GLUED to the
        // sentinel can only be the assembler's yaml-district wrap
        // (statement joins always space the sentinel) — never a command
        .>> notFollowedBy (pchar sibSep)
        // …and its marker+schema face [D:yaml-schemas]: a ` schema=<name>`
        // suffix GLUED to the sentinel is the same wrap (`yaml schema=x`);
        // no user argv is ever glued, so the whole segment refuses here
        // and the parse falls through to the district arm
        .>> notFollowedBy (
            attempt (
                pstring " schema="
                >>. many1Satisfy (fun c -> System.Char.IsLower c || System.Char.IsDigit c || c = '-')
                >>. pchar sibSep
            )
        )
        .>> ws
        >>= fun ((forced, w), span) ->
            if w[0] = '[' then
                // '[' never heads a command [D:bracket-heads-expression]: a line-head
                // string list would otherwise resolve to /usr/bin/[. The
                // external is still reachable as cmd "[" [...].
                if forced.IsSome then
                    failFatally "'[' cannot begin a command; use cmd \"[\" [...] to run the external"
                else
                    // [D:value-headed-pipe] this fail is DISCARDED at statement
                    // level (the expression grammar takes the list); it only
                    // SURFACES in command-only contexts — a district or sigil
                    // interior — where it should teach the value-headed spelling
                    fail
                        "'[' is command mode here (a district or sigil interior takes command lines); feed a value into a command with a value-headed pipeline bound outside the block — `let out = xs | prog`"
            elif forced.IsSome then
                if r.IsExternal w then
                    preturn (ExternalHead, w, span)
                else
                    failFatally $"command not found: {w}{didYouMean w (r.ExternalNames())}"
            elif builtinHeads && isIdentLike w && r.IsCommandCallable w then
                preturn (BuiltinHead, w, span)
            elif isIdentLike w && (keywords.Contains w || r.IsKnown w) then
                fail "known name; expression mode"
            elif r.IsExternal w then
                preturn (ExternalHead, w, span)
            else
                fail "not an external command"

    // consume the trigger, then anchor back [D:anchor-before-read]: a
    // non-consuming fatal here merges the head alternative's expected-set
    (getPosition .>> pstring "$@"
     >>= fun at ->
         failFatallyAt
             at
             "a splat cannot head a command (N words would be N heads); a command head is a literal — branch the whole command line")
    <|> (attempt head .>>. many argP)
    |>> fun ((kind, prog, span), args) ->
        let fullSpan =
            { Start = span.Start
              End =
                (match args with
                 | [] -> span.End
                 | _ -> (List.last args).Span.End) }

        match kind with
        | ExternalHead ->
            { Kind = ECmd(prog, args, sigilEnv)
              Span = fullSpan }
        | BuiltinHead ->
            let headVar = { Kind = EVar prog; Span = span }

            let effectiveArgs =
                match args with
                | [] -> [ { Kind = EStr "~"; Span = span } ]
                | _ -> args

            effectiveArgs
            |> List.fold
                (fun acc arg ->
                    { Kind = EApp(acc, arg)
                      Span = Span.union acc.Span arg.Span })
                headVar

let private pipeSep = (attempt (pstring "|>") <|> pstring "|") .>> ws


let private segment
    (builtinHeads: bool)
    (argP: Parser<Expr, unit>)
    (sigilEnv: Expr option)
    (r: Resolver)
    : Parser<Expr, unit> =
    choice [ commandSegment builtinHeads argP sigilEnv r; segExpr ]

type private Seg =
    | Stage of Expr
    | CompleteMarker of Span
    // the exit-code reifiers [D:exit-reifiers] — complete's family,
    // ONE rule (single external segment, nothing follows)
    | SucceedsMarker of Span
    | ExitCodeMarker of Span
    | OrFailMarker of Expr * Span

let private reifierEnd =
    // the let-RHS chain also ends at bare `in` [D:block-let-cmd] —
    // without this, `| succeeds in body` demotes the reifier to a
    // bareword stage
    let inStop: Parser<unit, unit> =
        fun stream ->
            if letCmdOk.Value then
                (attempt (pstring "in" .>> notFollowedBy (satisfy cmdWordChar)) |>> ignore) stream
            else
                fail "no in-stop here" stream

    lookAhead (choice [ pipeSep |>> ignore; pchar ')' |>> ignore; eof; inStop ])

let private completeMarker =
    attempt (
        spanned (pstring "complete" .>> notFollowedBy (satisfy cmdWordChar))
        .>> ws
        .>> reifierEnd
    )
    |>> fun (_, span) -> CompleteMarker span

let private succeedsMarker =
    attempt (
        spanned (pstring "succeeds" .>> notFollowedBy (satisfy cmdWordChar))
        .>> ws
        .>> reifierEnd
    )
    |>> fun (_, span) -> SucceedsMarker span

let private orFailMarker =
    attempt (
        spanned (pstring "orFail" .>> notFollowedBy (satisfy cmdWordChar)) .>> ws
        .>>. postfixAtom
        .>> reifierEnd
    )
    |>> fun ((_, span), msg) -> OrFailMarker(msg, span)

let private exitCodeMarker =
    attempt (
        spanned (pstring "exitCode" .>> notFollowedBy (satisfy cmdWordChar))
        .>> ws
        .>> reifierEnd
    )
    |>> fun (_, span) -> ExitCodeMarker span

// fold a parsed pipeline — an initial head expression plus piped stages
// and reifier markers — into one Expr. Shared by the command-headed
// chain and the value-headed chain [D:value-headed-pipe]: the ONLY
// difference is the head (a command segment vs an expression value), so
// the stage/reifier desugar is one function. A reifier's segment must be
// a single external command; a value threaded into it (`xs | grep |
// complete`) carries as the ECmd's stdin position.
// the error carries the OFFENDING segment's span [D:anchor-before-read]
// so the caller anchors on it, not the chain's drifted end
// the RIGHT-HAND SIDE decides the glyph [D:pipe-rhs-decides]: `|` when the
// stage is a program or a reifier (command grammar), `|>` when it is a
// function. A mismatch is the teaching error, anchored on the glyph.
let private requiredPipeOp (seg: Seg) : string =
    match seg with
    | Stage { Kind = ECmd _ } -> "|" // a program
    | Stage _ -> "|>" // a function/value stage
    | _ -> "|" // a reifier terminates the command chain

let private pipeOpError (got: string) : string =
    if got = "|" then
        "'|' chains commands; pipe expressions with '|>'"
    else
        "'|>' applies functions; feed a program with '|'"

let private foldChain (h: Expr) (rest: ((string * Span) * Seg) list) : Result<Expr, string * Span> =
    rest
    |> List.fold
        (fun acc ((op, opSpan), seg) ->
            match acc with
            | Result.Error m -> Result.Error m
            | Result.Ok _ when op <> requiredPipeOp seg -> Result.Error(pipeOpError op, opSpan)
            | Result.Ok acc ->
                match seg with
                | Stage seg ->
                    Result.Ok
                        { Kind = EPipe(acc, seg)
                          Span = Span.union acc.Span seg.Span }
                | (CompleteMarker _ | SucceedsMarker _ | ExitCodeMarker _ | OrFailMarker _ as marker) ->
                    let stageName, mspan, plainVar, envVar, stdinVar, extraArgs =
                        match marker with
                        | CompleteMarker sp -> "complete", sp, "|completed", "|completedEnv", "|completedIn", []
                        | SucceedsMarker sp -> "succeeds", sp, "|succeeded", "|succeededEnv", "|succeededIn", []
                        | ExitCodeMarker sp -> "exitCode", sp, "|exitCoded", "|exitCodedEnv", "|exitCodedIn", []
                        | OrFailMarker(msg, sp) -> "orFail", sp, "|orFailed", "|orFailedEnv", "|orFailedIn", [ msg ]
                        | Stage _ -> "", acc.Span, "", "", "", []

                    // a chain head is command-ish (an external segment or a
                    // command→command pipe); a VALUE head is anything else
                    let isCommandish (e: Expr) =
                        match e.Kind with
                        | ECmd _
                        | EPipe(_, { Kind = ECmd _ }) -> true
                        | _ -> false

                    // a reified segment's mixed literal+splat argv denotes a
                    // seq VALUE [D:splat-reifier-chains]: contiguous non-splat
                    // args chunk into list literals, each splat splices its
                    // interior seq WHOLE, folded with Seq.append. Element =
                    // one word carries through the builtin's argv (the same
                    // boundary as spawn-argv-build); ESplat never leaves argv
                    // in the AST. Splat-free argv keeps the plain list node.
                    let argvExpr (args: Expr list) : Expr =
                        let hasSplat =
                            args
                            |> List.exists (fun a ->
                                match a.Kind with
                                | ESplat _ -> true
                                | _ -> false)

                        if not hasSplat then
                            { Kind = EList args; Span = acc.Span }
                        else
                            let seqAppend (a: Expr) (b: Expr) =
                                let span = Span.union a.Span b.Span

                                let f =
                                    { Kind = EField({ Kind = EVar "Seq"; Span = span }, "append", span)
                                      Span = span }

                                { Kind = EApp({ Kind = EApp(f, a); Span = span }, b)
                                  Span = span }

                            let listOf (chunk: Expr list) =
                                { Kind = EList chunk
                                  Span = Span.union (List.head chunk).Span (List.last chunk).Span }

                            let flush chunk parts =
                                match chunk with
                                | [] -> parts
                                | c -> listOf (List.rev c) :: parts

                            let parts, chunk =
                                args
                                |> List.fold
                                    (fun (parts, chunk) a ->
                                        match a.Kind with
                                        // the interior keeps the full `$@...`
                                        // span (diagnostics + tokens)
                                        | ESplat inner -> ({ inner with Span = a.Span } :: flush chunk parts, [])
                                        | _ -> (parts, a :: chunk))
                                    ([], [])

                            match List.rev (flush chunk parts) with
                            | [] -> { Kind = EList []; Span = acc.Span }
                            | first :: rest -> rest |> List.fold seqAppend first

                    match acc.Kind with
                    | ECmd(prog, args, cenv) ->
                        let span = Span.union acc.Span mspan

                        // env sigils route through the *Env twins — the same
                        // desugar family, env threaded up front
                        let headVar =
                            match cenv with
                            | Some e ->
                                { Kind = EApp({ Kind = EVar envVar; Span = mspan }, e)
                                  Span = mspan }
                            | None -> { Kind = EVar plainVar; Span = mspan }

                        let progArg = { Kind = EStr prog; Span = acc.Span }
                        let argList = argvExpr args

                        let applied =
                            (extraArgs @ [ progArg; argList ])
                            |> List.fold (fun f a -> { Kind = EApp(f, a); Span = span }) headVar

                        Result.Ok applied
                    // a VALUE-headed single external segment [D:value-headed-pipe]:
                    // `xs | grep foo | complete` reifies grep WITH xs as stdin —
                    // the stdin-carrying twin, value appended. Only when the LHS
                    // is a value: a command→command LHS is the multi-external
                    // case below, rejected as always (the family's single-segment
                    // rule, unchanged).
                    | EPipe(stdinE, { Kind = ECmd(prog, args, None) }) when not (isCommandish stdinE) ->
                        let span = Span.union acc.Span mspan
                        let headVar = { Kind = EVar stdinVar; Span = mspan }
                        let progArg = { Kind = EStr prog; Span = acc.Span }
                        let argList = argvExpr args

                        let applied =
                            (extraArgs @ [ progArg; argList; stdinE ])
                            |> List.fold (fun f a -> { Kind = EApp(f, a); Span = span }) headVar

                        Result.Ok applied
                    // a reifier needs a SINGLE external segment [D:exit-reifiers]:
                    // a multi-external chain is rejected as always (no new law)
                    | _ -> Result.Error($"'{stageName}' must directly follow a single external command segment", mspan))
        (Result.Ok h)

// the pipe glyph, captured with its span [D:pipe-rhs-decides] — foldChain
// checks it against the RIGHT-HAND stage kind (| for a program/reifier, |>
// for a function) and anchors the teaching error ON the glyph
let private pipeSepSpanned: Parser<string * Span, unit> =
    spanned (attempt (pstring "|>") <|> pstring "|") .>> ws

let private pipedStages (builtinHeads: bool) (argP: Parser<Expr, unit>) (sigilEnv: Expr option) (r: Resolver) =
    many (
        pipeSepSpanned
        .>>. (completeMarker
              <|> succeedsMarker
              <|> exitCodeMarker
              <|> orFailMarker
              <|> (segment builtinHeads argP sigilEnv r |>> Stage))
    )

let private cmdLineWith
    (builtinHeads: bool)
    (argP: Parser<Expr, unit>)
    (sigilEnv: Expr option)
    (r: Resolver)
    : Parser<Expr, unit> =
    commandSegment builtinHeads argP sigilEnv r
    .>>. pipedStages builtinHeads argP sigilEnv r
    >>= fun (h, rest) ->
        match foldChain h rest with
        | Result.Ok e -> preturn e
        | Result.Error(m, sp) -> failFatallyAtCol sp.Start.Col m

let private cmdLine (r: Resolver) : Parser<Expr, unit> = cmdLineWith true cmdArg None r

sigilChainImpl <- fun envO -> fun stream -> (cmdLineWith true cmdArg envO ambientResolver.Value) stream

// a single bare pipe `|` — NOT `|>` (expression pipe) or `||` (or)
let private singlePipe: Parser<unit, unit> =
    attempt (pchar '|' .>> notFollowedBy (anyOf "|>")) >>. ws

valueHeadedTailImpl <-
    fun lhs ->
        fun stream ->
            let r = ambientResolver.Value

            // gate [D:value-headed-pipe]: a single `|` then a head that
            // resolves to an EXTERNAL command (an ECmd — a known/library
            // head fails commandSegment and falls through to barePipeHint).
            // The lookAhead consumes nothing; the stages re-parse from `|`.
            let externalHeaded =
                commandSegment true cmdArg None r
                >>= fun seg ->
                    match seg.Kind with
                    | ECmd _ -> preturn ()
                    | _ -> fail "value-headed pipe needs an external command head"

            let p =
                lookAhead (singlePipe >>. externalHeaded) >>. pipedStages true cmdArg None r
                >>= fun stages ->
                    match foldChain lhs stages with
                    | Result.Ok e -> preturn e
                    | Result.Error(m, sp) -> failFatallyAtCol sp.Start.Col m

            p stream

// let-RHS command lines stop at a bareword `in` (see cmdArgWith), and
// command-callable builtins (cd) stay ordinary functions there —
// `let workdir = cd target` must apply the BINDING target, never read
// it as a bareword.
let private cmdLineLetRhs (r: Resolver) : Parser<Expr, unit> =
    cmdLineWith false (cmdArgWith true) None r

letRhsCmdRef.Value <- fun stream -> (cmdLineLetRhs ambientResolver.Value) stream

let private tySyn, private tySynRef = createParserForwardedToRef<Ty, unit> ()

tySynRef.Value <-
    choice
        [ pchar '\'' >>. rawWord .>> ws |>> TVar
          rawWord
          >>= fun w ->
              match w with
              | "int" ->
                  // anchor at the measure's '<' [D:anchor-before-read]
                  getPosition .>>. opt (attempt (pchar '<' >>. rawWord .>> pchar '>')) .>> ws
                  >>= (fun (at, m) ->
                      match m with
                      | Some _ -> failFatallyAt at "units of measure are not supported; use bare int"
                      | None -> preturn TInt)
              | "string" -> ws >>% TStr
              | "bool" -> ws >>% TBool
              | "unit" -> ws >>% TUnit
              | "seq" -> ws >>. between (str_ws "<") (str_ws ">") tySyn |>> TSeq
              | w when keywords.Contains w -> fail $"'{w}' is a keyword"
              | w ->
                  ws >>. opt (between (str_ws "<") (str_ws ">") (sepBy1 tySyn (str_ws ",")))
                  |>> fun args -> TNamed(w, Option.defaultValue [] args) ]
    // t1 * t2 [* ...] is a tuple type [D:tuples-reversal]
    |> fun atom ->
        sepBy1 atom (attempt (str_ws "*"))
        |>> function
            | [ one ] -> one
            | many -> TTuple many

// literal-only attribute arguments [D:attributes]
let private attrArgLit =
    choice
        [ between (pchar '"') (pchar '"') (manyChars stringChar) |>> AStr
          many1Satisfy isDigit |>> (int64 >> AInt)
          keyword "true" >>% ABool true
          keyword "false" >>% ABool false ]
    .>> ws

let private attrSpec =
    spanned (ident .>>. opt attrArgLit)
    |>> fun ((name, arg), sp) -> { AName = name; AArg = arg; ASpan = sp }

let private attrList = str_ws "[<" >>. sepBy1 attrSpec (str_ws ";") .>> str_ws ">]"

// a record-decl field name DOMINATES on a keyword [D:anchor-before-read]:
// typeDecl is committed past `type T = {`, so the fatal propagates (no
// enclosing attempt to swallow it, unlike the shared `ident`)
let private fieldNameDecl: Parser<string, unit> =
    getPosition .>>. spanned rawWord .>> ws
    >>= fun (at, (w, _)) ->
        if keywords.Contains w then
            failFatallyAt at $"'{w}' is a keyword"
        else
            preturn w

let private fieldDecl =
    opt attrList .>>. (fieldNameDecl .>> str_ws ":") .>>. tySyn
    |>> fun ((attrs, name), ty) -> name, ty, defaultArg attrs []

let private recordBody =
    str_ws "{" >>. sepBy1 fieldDecl (str_ws ";") .>> str_ws "}" |>> DRecord

let private caseDecl =
    // peek-validate-then-consume: a post-consumption fail reports past
    // the word (its trailing ws even crosses physical lines) — lookAhead
    // restores the position so the error lands ON the constructor
    (attrsRejectHere >>% Unchecked.defaultof<_>)
    <|> (lookAhead rawWord
         >>= fun w ->
             if keywords.Contains w then
                 failFatally $"'{w}' is a keyword"
             elif not (Char.IsUpper w[0]) then
                 failFatally "constructor names must start with an uppercase letter"
             else
                 rawWord .>> ws >>= fun w -> opt (keyword "of" >>. tySyn) |>> fun ty -> w, ty)

let private unionBody = opt (str_ws "|") >>. sepBy1 caseDecl (str_ws "|") |>> DUnion

let private typeParams =
    opt (between (str_ws "<") (str_ws ">") (sepBy1 (pchar '\'' >>. rawWord .>> ws) (str_ws ",")))
    |>> Option.defaultValue []

let private typeDecl =
    pipe4
        getPosition
        (keyword "type" >>. ident .>>. typeParams .>> str_ws "=")
        (recordBody <|> unionBody)
        getPosition
        (fun p (name, tps) body e ->
            SType
                { Name = name
                  Params = tps
                  Body = body
                  Span = { Start = pos p; End = pos e } })

// A top-level let RHS admits command mode (agent-dogfooding finding, two
// independent hits): the RHS occupies the rest of the logical line, so
// commit-to-command semantics carry over. Expression-level `let ... in`
// stays expression-only — a greedy command grammar would eat `in x` as
// barewords.
let private topLet (r: Resolver) =
    attempt (
        keyword "let" >>. ident .>>. many binderParam .>> str_ws "="
        >>= fun (name, ps) ->
            rejectDupParams ps
            >>= fun () ->
                // RHS takes sequenced blocks too, and commands
                // [D:paramful-rhs] — param splices are boundary-safe.
                // params shadow PATH in their own RHS
                // [D:paramful-rhs] — bindings-beat-PATH; ^x still
                // reaches the binary
                let rec leafNames (p: Pattern) =
                    match p.PKind with
                    | PVar n -> [ n ]
                    | PTuple ps -> ps |> List.collect leafNames
                    | _ -> []

                let paramNames = ps |> List.collect leafNames |> Set.ofList

                let r' =
                    { r with
                        IsKnown = fun n -> Set.contains n paramNames || r.IsKnown n }

                // command-first body [D:sibling-sentinel]: the command
                // is ONE statement; the sentinel-separated tail (inner
                // block-lets and expressions) sequences AFTER it, so
                // `cmd ⟨sib⟩ let x = … in body` parses as a real ESeq
                // instead of command mode over-running the boundary. A
                // user ';' after the command is still a bareword arg
                // (eaten by cmdArg), so only the machine sentinel splits.
                let rhsCmd =
                    cmdLineLetRhs r' .>>. many (str_ws sibSepStr >>. commaExpr)
                    |>> fun (h, rest) -> foldSeqExpr (h :: rest)

                let rhsP = rhsCmd <|> ((seqExpr >>= pipeOrHint))

                // the RHS spine carries the flag + the param-extended
                // resolver, so interior block lets parse commands with
                // params AND earlier block names known [D:block-let-cmd]
                let withSpine (p: Parser<'a, unit>) : Parser<'a, unit> =
                    fun stream ->
                        let saved = ambientResolver.Value
                        ambientResolver.Value <- r'

                        try
                            (withLetCmd true p) stream
                        finally
                            ambientResolver.Value <- saved

                withSpine (rhsP .>> eof) |>> fun rhs -> SLet(name, curryParams ps rhs)
    )

// `let <keyword>` [D:anchor-before-read]: a keyword in the binder-name
// slot is always an error — DOMINATE at the word so its teaching is not
// buried under the let-parsers' merged backtrack. Must fire OUTSIDE any
// attempt (topLet's attempt would swallow the fatal); the peek engages
// ONLY when the name is reserved, so every real binder falls through.
let private letKeywordGuard: Parser<Stmt, unit> =
    // scan the whole binder region for a keyword [D:anchor-before-read]:
    // the name, its params, AND any destructure/param PATTERN. Finding a
    // reserved word in an identifier slot between `let` and the top-level
    // `=` is a LEXICAL question, so the scan collects barewords while
    // skipping pattern delimiters ( ) [ ] { } , ; _ — no pattern parse,
    // no nesting logic. Fires OUTSIDE any attempt (a stmtWith alternative
    // before topLet/SLetPat, whose attempts would swallow the fatal); the
    // scan STOPS at `=`, so a keyword in the RHS never counts as a binder.
    let binderTok =
        choice
            [ getPosition .>>. spanned rawWord .>> ws |>> Some
              anyOf "()[]{},;_" .>> ws >>% None ]

    attempt (
        keyword "let" >>. many binderTok
        >>= fun toks ->
            match
                toks
                |> List.choose id
                |> List.tryPick (fun (at, (w, _)) ->
                    // true/false are LITERAL patterns, not keyword names
                    // (patWord's rule) — a refutable binder, not a parse error
                    if (w = "function" || keywords.Contains w) && w <> "true" && w <> "false" then
                        Some(at, w)
                    else
                        None)
            with
            | Some hit -> preturn hit
            | None -> fail "real binder(s)"
    )
    >>= fun (at, w) ->
        if w = "function" then
            failFatallyAt at "'function' is reserved; write 'fun x -> match x with'"
        else
            failFatallyAt at $"'{w}' is a keyword"

// module + import statements [D:modules-v1] — top-level, no `=`, no body.
// A module/alias name is uppercase (the casing law: uppercase declares).
let private upperName (role: string) : Parser<string * Span, unit> =
    spanned rawWord .>> ws
    >>= fun (w, sp) ->
        if System.Char.IsUpper w[0] then
            preturn (w, sp)
        else
            fail $"{role} must be uppercase"

// `module` (name derived from the filename) or `module Name`
let private moduleDecl: Parser<Stmt, unit> =
    pipe2
        (spanned (pstring "module" .>> notFollowedBy (satisfy isIdentCont)) .>> ws)
        (opt (upperName "a module name") .>> eof)
        (fun (_, kwSpan) nameOpt -> SModule(Option.map fst nameOpt, kwSpan))

// `import "path"` / `import "path" as Name` — the path is a LITERAL string
// (resolution is check-time); anything else gets the teaching error
let private importDecl: Parser<Stmt, unit> =
    keyword "import"
    >>. (spanned (between (pchar '"') (pchar '"') (manyChars stringChar)) .>> ws
         <|> failFatally "import takes a literal string path, e.g. import \"./lib/paths.weir\"")
    .>>. (opt (keyword "as" >>. upperName "an import alias") .>> eof)
    |>> fun ((path, pathSpan), aliasOpt) -> SImport(path, pathSpan, aliasOpt)

let private stmtWith (r: Resolver) =
    ws
    >>. choice
            [ moduleDecl
              importDecl
              typeDecl .>> eof
              // destructuring let statement (pattern binder, expression RHS);
              // fully attempt-wrapped so `let (x, y) = v in body` backtracks
              // to the expression grammar's letIn form
              attempt (
                  (keyword "let" >>. binderPat .>> str_ws "="
                   >>= fun b ->
                       match b.PKind with
                       | PVar _
                       | PCase(_, None) -> fail "plain binder takes the ident path"
                       | _ -> preturn b)
                  .>>. ((seqExpr >>= pipeOrHint))
                  .>> eof
              )
              |>> SLetPat
              letKeywordGuard
              topLet r
              cmdLine r .>> eof |>> SCmd
              (seqExpr >>= pipeOrHint) .>> eof |>> SExpr ]

let private noExternals =
    { IsKnown = fun _ -> true
      IsCommandCallable = fun _ -> false
      IsExternal = fun _ -> false
      ExternalNames = fun () -> Seq.empty }

// Structured failure: the position travels as DATA
// [D:structured-parse-failure]. Message text is unchanged; Col is
// Some only for the single-logical-line case the runner translates.
type ParseFailure = { Message: string; Col: int option }

// Iterative max-depth probe [D:depth-guard] — the checker/evaluator
// walk the tree recursively, so a spine past the ceiling would overflow
// THEIR stack; this measures depth WITHOUT recursing (an explicit
// stack), then early-exits at the first over-limit node. Catches the
// operator/application/pipe/sequencing spines that parse shallow.
let private exprTooDeep (root: Expr) : Span option =
    let stack = System.Collections.Generic.Stack<Expr * int>()
    stack.Push(root, 1)
    let mutable hit = None

    while hit.IsNone && stack.Count > 0 do
        let node, d = stack.Pop()

        if d > maxDepth then
            hit <- Some node.Span
        else
            for c in exprChildren node do
                stack.Push(c, d + 1)

    hit

let private stmtExprs (s: Stmt) : Expr list =
    match s with
    | SLet(_, v)
    | SLetPat(_, v)
    | SExpr v
    | SCmd v -> [ v ]
    | SType _
    | SModule _
    | SImport _ -> []

// expr |> cmd [D:pipe-rhs-decides]: the `|>` OPERATOR fed a value into a
// PROGRAM (its RHS is headed by an external command). foldChain catches the
// command-CHAIN mismatches; this catches the value-headed operator form,
// anchored on the offending program name.
let private pipeToCommand (r: Resolver) (root: Expr) : Span option =
    let rec cmdHead (e: Expr) =
        match e.Kind with
        | EVar n when r.IsExternal n && not (r.IsKnown n) -> Some e.Span
        | EApp(f, _) -> cmdHead f
        | _ -> None

    let rec walk (e: Expr) =
        match e.Kind with
        | EPipe(_, rhs) ->
            match cmdHead rhs with
            | Some sp -> Some sp
            | None -> exprChildren e |> List.tryPick walk
        | _ -> exprChildren e |> List.tryPick walk

    walk root

let parseLineFull (r: Resolver) (input: string) : Result<Stmt, ParseFailure> =
    ambientResolver.Value <- r
    parseDepth.Value <- 0

    try
        try
            match run (stmtWith r) input with
            | Success(s, _, _) ->
                match s |> stmtExprs |> List.tryPick exprTooDeep with
                | Some span ->
                    let col = if span.Start.Line = 1 then Some span.Start.Col else None

                    Result.Error
                        { Message = $"expression nested too deeply (limit {maxDepth})"
                          Col = col }
                | None ->
                    match s |> stmtExprs |> List.tryPick (pipeToCommand r) with
                    | Some span ->
                        let col = if span.Start.Line = 1 then Some span.Start.Col else None

                        Result.Error
                            { Message = "'|>' applies functions; feed a program with '|'"
                              Col = col }
                    | None -> Result.Ok s
            | Failure(msg, err, _) ->
                let col =
                    if err.Position.Line = 1L then
                        Some(int err.Position.Column)
                    else
                        None

                Result.Error { Message = msg; Col = col }
        with DepthExceeded p ->
            let col = if p.Line = 1 then Some p.Col else None

            Result.Error
                { Message = $"expression nested too deeply (limit {maxDepth}, less on small stacks)"
                  Col = col }
    finally
        ambientResolver.Value <- noExternals

let parseLine (r: Resolver) (input: string) : Result<Stmt, string> =
    parseLineFull r input |> Result.mapError _.Message

let parseStmt (input: string) : Result<Stmt, string> = parseLine noExternals input

let parseExpr (input: string) : Result<Expr, string> =
    match parseStmt input with
    | Result.Ok(SExpr e)
    | Result.Ok(SCmd e) -> Result.Ok e
    | Result.Ok _ -> Result.Error "expected an expression, got a declaration"
    | Result.Error msg -> Result.Error msg
