module Weir.Parser

open System
open FParsec
open Weir.Types
open Weir.Ast

let private keywords =
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
          "mutable" ]

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

let private isIdentStart c = isLetter c || c = '_'
let private isIdentCont c = isLetter c || isDigit c || c = '_'

let private ws: Parser<unit, unit> = spaces
let private str_ws s = pstring s >>. ws

let private pos (p: Position) : Pos =
    { Line = int p.Line
      Col = int p.Column }

let private spanned (p: Parser<'a, unit>) : Parser<'a * Span, unit> =
    pipe3 getPosition p getPosition (fun s x e -> x, { Start = pos s; End = pos e })

let private rawWord = many1Satisfy2L isIdentStart isIdentCont "identifier"

let private keyword s =
    attempt (pstring s .>> notFollowedBy (satisfy isIdentCont)) .>> ws

let private notKeyword (w: string) =
    if keywords.Contains w then
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
// match/if source is rejected — parens required, FCS-verdict-pinned);
// assigned once the operator table exists
let private updateSource, private updateSourceRef =
    createParserForwardedToRef<Expr, unit> ()

let private intLit =
    spanned (
        many1Satisfy isDigit .>>. opt (attempt (pchar '<' >>. rawWord .>> pchar '>'))
        >>= fun (digits, m) ->
            match m, System.Int64.TryParse digits with
            | Some _, _ -> failFatally "units of measure are not supported; use bare int"
            | None, (true, n) -> preturn (EInt n)
            | None, (false, _) -> failFatally $"int literal out of range (64-bit): {digits}"
    )
    |>> mkExpr
    .>> ws

let private stringChar =
    choice
        [ satisfy (fun c -> c <> '"' && c <> '\\')
          pchar '\\'
          >>. (anyOf "\"\\nt"
               |>> function
                   | 'n' -> '\n'
                   | 't' -> '\t'
                   | c -> c) ]

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

let private updateAssign = updatePath .>> str_ws "=" .>>. commaExpr

let private recordLit =
    spanned (
        pchar '{'
        >>. ws
        >>. choice
                [ attempt (sepBy1 fieldAssign (str_ws ";") .>> pchar '}') |>> ERecord
                  (updateSource .>> keyword "with") .>>. sepBy1 updateAssign (str_ws ";")
                  .>> pchar '}'
                  |>> EUpdate ]
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
            | false, _ -> failFatally $"int literal out of range (64-bit): -{digits}"
    )
    |>> mkExpr
    .>> ws

let private rangeTerm, private rangeTermRef =
    createParserForwardedToRef<Expr, unit> ()

let private rangeBody =
    attempt (rangeTerm .>> dotdot) .>>. rangeTerm .>>. opt (dotdot >>. rangeTerm)
    >>= fun ((a, b), c) ->
        let start, step, stop =
            match c with
            | Some stop -> a, b, stop
            | None -> a, { Kind = EInt 1L; Span = a.Span }, b

        match step.Kind with
        | EInt 0L -> failFatally "range step is zero"
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
          pchar '\\'
          >>. (anyOf "\"\\nt"
               |>> function
                   | 'n' -> '\n'
                   | 't' -> '\t'
                   | c -> c) ]

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

let private sigilOpen (glyph: char) : Parser<Expr option, unit> =
    attempt (
        pchar glyph >>. spanned (opt rawWord) .>> pchar '('
        |>> fun (nameO, span) -> nameO |> Option.map (fun n -> { Kind = EVar n; Span = span })
    )

let private captureSigil =
    spanned (
        sigilOpen '$'
        >>= fun envO ->
            ws >>. sigilChain envO
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
        .>>. postfixAtomFwd
        |>> fun (p, e) ->
            { Kind = EBinOp("-", { Kind = EInt 0L; Span = e.Span }, e)
              Span = { Start = pos p; End = e.Span.End } }
    )

let private atom =
    choice
        [ negAtom
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
          listLit
          wordAtom ]

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
                { Kind = ELambda("_", applied)
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

// duplicate params reject in BOTH sugar positions (let and fun) —
// FCS-verdict-pinned; explicit nested lambdas may still shadow
// [D:fun-sugar]. The probe also caught let-sugar ACCEPTING dups (a
// latent divergence, fixed here by the one-rule-two-positions law).
let private rejectDupParams (ps: Pattern list) =
    let names =
        ps
        |> List.choose (fun p ->
            match p.PKind with
            | PVar n -> Some n
            | _ -> None)

    match names |> List.groupBy id |> List.tryFind (fun (_, g) -> List.length g > 1) with
    | Some(n, _) -> failFatally $"duplicate parameter '{n}'"
    | None -> preturn ()

let private curryParams (ps: Pattern list) (value: Expr) : Expr =
    List.foldBack
        (fun (p: Pattern) body ->
            let kind =
                match p.PKind with
                | PVar n -> ELambda(n, body)
                | PUnit -> ELambda("()", body)
                | _ -> ELambdaPat(p, body)

            // span covers the param (binder diagnostics point at it,
            // not at the RHS the old value-only span implied)
            { Kind = kind
              Span = Span.union p.PSpan value.Span })
        ps
        value

let private lambda =
    // fun a b -> e desugars to nested lambdas [D:fun-sugar] — the
    // lambda-side twin of let-param sugar, same param set, same
    // curryParams, zero checker surface
    pipe3
        getPosition
        (keyword "fun" >>. many1 binderParam >>= fun ps -> rejectDupParams ps >>% ps
         .>> str_ws "->")
        seqExpr
        (fun p ps body ->
            let inner = curryParams ps body

            { inner with
                Span = { Start = pos p; End = body.Span.End } })

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
        [ pipe3 getPosition (patForm .>>. seqExpr .>> keyword "in") seqExpr (fun p (binder, value) body ->
              { Kind = ELetPat(binder, value, body)
                Span = { Start = pos p; End = body.Span.End } })
          pipe3
              getPosition
              ((keyword "let" >>. ident .>>. many binderParam
                >>= fun (n, ps) -> rejectDupParams ps >>% (n, ps))
               .>> str_ws "="
               .>>. seqExpr
               .>> keyword "in")
              seqExpr
              (fun p ((name, ps), value) body ->
                  { Kind = ELet(name, curryParams ps value, body)
                    Span = { Start = pos p; End = body.Span.End } }) ]

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
    // composition [D:composition-operators] at the PIPE's level — the
    // oracle refuted the tighter-than-pipe folklore: F# parses
    // `xs |> f >> g` as `(xs |> f) >> g` (shared infix class), so the
    // idiom needs parens: `xs |> (f >> g)`. OPP's operator trie keeps
    // > / >= / >> apart.
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
    attempt (
        spanned rawWord
        >>= fun (w, span) ->
            if w = "true" || w = "false" then
                preturn (w, span)
            else
                notKeyword w >>% (w, span)
    )
    .>> ws

// literal patterns [D:literal-patterns]: int and string pin
// the scrutinee; () is the irrefutable unit pattern
let private patLit =
    choice
        [ attempt (spanned (pstring "()") .>> ws)
          |>> fun (_, span) -> { PKind = PUnit; PSpan = span }
          attempt (spanned (opt (pchar '-') .>>. many1Satisfy isDigit) .>> ws)
          >>= fun ((neg, digits), span) ->
              match System.Int64.TryParse((if neg.IsSome then "-" else "") + digits) with
              | true, n -> preturn { PKind = PInt n; PSpan = span }
              | false, _ -> failFatally $"int literal out of range (64-bit): {digits}"
          spanned (pstring "\"\"\"" >>. tripleBody .>> pstring "\"\"\"") .>> ws
          |>> fun (s, span) -> { PKind = PStr s; PSpan = span }
          spanned (pstring "@\"" >>. verbatimBody .>> pchar '"') .>> ws
          |>> fun (s, span) -> { PKind = PStr s; PSpan = span }
          spanned (between (pchar '"') (pchar '"') (manyChars stringChar)) .>> ws
          |>> fun (s, span) -> { PKind = PStr s; PSpan = span } ]

let private patParens =
    between (str_ws "(") (str_ws ")") (sepBy1 pat (str_ws ","))
    |>> function
        | [ one ] -> one
        | many ->
            { PKind = PTuple many
              PSpan =
                { Start = (List.head many).PSpan.Start
                  End = (List.last many).PSpan.End } }

let private patAtom =
    choice
        [ patLit
          patParens
          patWord
          |>> fun (w, span) ->
              let kind =
                  if w = "_" then PWildcard
                  elif w = "true" then PBool true
                  elif w = "false" then PBool false
                  elif Char.IsUpper w[0] then PCase(w, None)
                  else PVar w

              { PKind = kind; PSpan = span } ]

// The positional raw-regex lexer RETIRED with PLAN-raw-strings
// (2026-07-22): rawness is a property of the literal KIND, never of
// position. The Regex position parses any string kind and tags it;
// the checker enforces raw-only there [D:raw-strings].
let private regexPatternLit =
    choice
        [ spanned (pstring "\"\"\"" >>. tripleBody .>> pstring "\"\"\"")
          |>> fun (p, sp) -> p, sp, true
          spanned (pstring "@\"" >>. verbatimBody .>> pchar '"')
          |>> fun (p, sp) -> p, sp, true
          spanned (between (pchar '"') (pchar '"') (manyChars stringChar))
          |>> fun (p, sp) -> p, sp, false ]
    .>> ws

patRef.Value <-
    choice
        [ patLit
          patParens
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


binderParamRef.Value <-
    choice
        [ spanned (pstring "()") .>> ws
          |>> fun (_, span) -> { PKind = PUnit; PSpan = span }
          identSpanned |>> fun (n, span) -> { PKind = PVar n; PSpan = span }
          patParens ]

binderPatRef.Value <-
    sepBy1 pat (str_ws ",")
    |>> function
        | [ one ] -> one
        | many ->
            { PKind = PTuple many
              PSpan =
                { Start = (List.head many).PSpan.Start
                  End = (List.last many).PSpan.End } }

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
    pat .>>. opt (keyword "when" >>. expr) .>> str_ws "->" .>>. seqExpr
    |>> fun ((p, guard), body) -> p, guard, body

let private matchExpr =
    pipe3
        getPosition
        (keyword "match" >>. expr .>> keyword "with")
        (opt (str_ws "|") >>. matchArm .>>. many (attempt (str_ws "|" >>. matchArm)))
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

opp.TermParser <- choice [ lambda; letIn; ifExpr; matchExpr; fromExpr; toExpr; appChain ]

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

seqExprRef.Value <-
    commaExpr .>>. many (attempt (str_ws ";" >>. commaExpr))
    |>> fun (first, rest) ->
        match rest with
        | [] -> first
        | _ ->
            let all = first :: rest

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
            |> Option.get

let private segExpr = segOpp.ExpressionParser


let private cmdWordChar c =
    not (System.Char.IsWhiteSpace c)
    && c <> '|'
    && c <> '('
    && c <> ')'
    && c <> '"'
    && c <> '\''
    && c <> '$'

let private cmdWord = many1Satisfy cmdWordChar

let private isIdentLike (w: string) =
    isIdentStart w[0] && w |> Seq.forall isIdentCont

let private singleQuoted =
    spanned (between (pchar '\'') (pchar '\'') (manySatisfy ((<>) '\'')) |>> EStr)
    |>> mkExpr
    .>> ws

let private spliceVar = spanned (pchar '$' >>. rawWord |>> EVar) |>> mkExpr .>> ws

let private cmdArgWith (stopAtIn: bool) =
    let bareword =
        if stopAtIn then
            // In a let RHS, a bareword `in` would silently become argv (the
            // let...in cliff). Stop instead: the parse falls through to the
            // expression grammar and surfaces a check error. Quote "in" to
            // pass it to a command from a let RHS.
            notFollowedBy (attempt (pstring "in" .>> notFollowedBy (satisfy cmdWordChar)))
            >>. (spanned (cmdWord |>> EStr) |>> mkExpr .>> ws)
        else
            spanned (cmdWord |>> EStr) |>> mkExpr .>> ws

    choice [ strLit; singleQuoted; interpLit; captureSigil; spliceVar; parens; bareword ]

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
        spanned (opt (pchar '^') .>>. cmdWord) .>> ws
        >>= fun ((forced, w), span) ->
            if w[0] = '[' then
                // '[' never heads a command [D:bracket-heads-expression]: a line-head
                // string list would otherwise resolve to /usr/bin/[. The
                // external is still reachable as cmd "[" [...].
                if forced.IsSome then
                    failFatally "'[' cannot begin a command; use cmd \"[\" [...] to run the external"
                else
                    fail "list literal; expression mode"
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

    attempt head .>>. many argP
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
    | OrFailMarker of Expr * Span

let private reifierEnd =
    lookAhead (choice [ pipeSep |>> ignore; pchar ')' |>> ignore; eof ])

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

let private cmdLineWith
    (builtinHeads: bool)
    (argP: Parser<Expr, unit>)
    (sigilEnv: Expr option)
    (r: Resolver)
    : Parser<Expr, unit> =
    commandSegment builtinHeads argP sigilEnv r
    .>>. many (
        pipeSep
        >>. (completeMarker
             <|> succeedsMarker
             <|> orFailMarker
             <|> (segment builtinHeads argP sigilEnv r |>> Stage))
    )
    >>= fun (h, rest) ->
        let folded =
            rest
            |> List.fold
                (fun acc seg ->
                    match acc, seg with
                    | Result.Error m, _ -> Result.Error m
                    | Result.Ok acc, Stage seg ->
                        Result.Ok
                            { Kind = EPipe(acc, seg)
                              Span = Span.union acc.Span seg.Span }
                    | Result.Ok acc, (CompleteMarker _ | SucceedsMarker _ | OrFailMarker _ as marker) ->
                        let stageName, mspan, plainVar, envVar, extraArgs =
                            match marker with
                            | CompleteMarker sp -> "complete", sp, "completed", "completedEnv", []
                            | SucceedsMarker sp -> "succeeds", sp, "succeeded", "succeededEnv", []
                            | OrFailMarker(msg, sp) -> "orFail", sp, "orFailed", "orFailedEnv", [ msg ]
                            | Stage _ -> "", acc.Span, "", "", []

                        match acc.Kind with
                        | ECmd(prog, args, cenv) ->
                            let span = Span.union acc.Span mspan

                            // env sigils route through the *Env twins — the
                            // same desugar family, env threaded up front
                            let headVar =
                                match cenv with
                                | Some e ->
                                    { Kind = EApp({ Kind = EVar envVar; Span = mspan }, e)
                                      Span = mspan }
                                | None -> { Kind = EVar plainVar; Span = mspan }

                            let progArg = { Kind = EStr prog; Span = acc.Span }
                            let argList = { Kind = EList args; Span = acc.Span }

                            let applied =
                                (extraArgs @ [ progArg; argList ])
                                |> List.fold (fun f a -> { Kind = EApp(f, a); Span = span }) headVar

                            Result.Ok applied
                        | _ -> Result.Error $"'{stageName}' must directly follow a single external command segment")
                (Result.Ok h)

        match folded with
        | Result.Ok e -> preturn e
        | Result.Error m -> failFatally m

let private cmdLine (r: Resolver) : Parser<Expr, unit> = cmdLineWith true cmdArg None r

sigilChainImpl <- fun envO -> fun stream -> (cmdLineWith true cmdArg envO ambientResolver.Value) stream

// let-RHS command lines stop at a bareword `in` (see cmdArgWith), and
// command-callable builtins (cd) stay ordinary functions there — found as a
// silent meaning change of `let workdir = cd target` (target became a
// bareword) when the example script was modernized.
let private cmdLineLetRhs (r: Resolver) : Parser<Expr, unit> =
    cmdLineWith false (cmdArgWith true) None r

let private tySyn, private tySynRef = createParserForwardedToRef<Ty, unit> ()

tySynRef.Value <-
    choice
        [ pchar '\'' >>. rawWord .>> ws |>> TVar
          rawWord
          >>= fun w ->
              match w with
              | "int" ->
                  opt (attempt (pchar '<' >>. rawWord .>> pchar '>')) .>> ws
                  >>= (function
                  | Some _ -> failFatally "units of measure are not supported; use bare int"
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

let private fieldDecl = ident .>> str_ws ":" .>>. tySyn

let private recordBody =
    str_ws "{" >>. sepBy1 fieldDecl (str_ws ";") .>> str_ws "}" |>> DRecord

let private caseDecl =
    spanned rawWord .>> ws
    >>= fun (w, _) ->
        if keywords.Contains w then
            fail $"'{w}' is a keyword"
        elif not (Char.IsUpper w[0]) then
            fail "constructor names must start with an uppercase letter"
        else
            opt (keyword "of" >>. tySyn) |>> fun ty -> w, ty

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
                // RHS takes sequenced blocks too (function bodies of effect
                // lines — the bicep-script receipt). The old "no command
                // mode under a lambda" bar RETIRED with [D:paramful-rhs]:
                // splice-default-last made param splices boundary-safe.
                // params shadow PATH in their own RHS — bindings-beat-
                // PATH reaching a scope commands could not previously
                // occupy [D:paramful-rhs]; ^x still reaches the binary
                let rec leafNames (p: Pattern) =
                    match p.PKind with
                    | PVar n -> [ n ]
                    | PTuple ps -> ps |> List.collect leafNames
                    | _ -> []

                let paramNames = ps |> List.collect leafNames |> Set.ofList

                let r' =
                    { r with
                        IsKnown = fun n -> Set.contains n paramNames || r.IsKnown n }

                let rhsP = cmdLineLetRhs r' <|> seqExpr

                rhsP .>> eof |>> fun rhs -> SLet(name, curryParams ps rhs)
    )

let private stmtWith (r: Resolver) =
    ws
    >>. choice
            [ typeDecl .>> eof
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
                  .>>. seqExpr
                  .>> eof
              )
              |>> SLetPat
              topLet r
              cmdLine r .>> eof |>> SCmd
              seqExpr .>> eof |>> SExpr ]

let private noExternals =
    { IsKnown = fun _ -> true
      IsCommandCallable = fun _ -> false
      IsExternal = fun _ -> false
      ExternalNames = fun () -> Seq.empty }

// Structured failure: the position travels as DATA
// [D:structured-parse-failure]. Message text is unchanged; Col is
// Some only for the single-logical-line case the runner translates.
type ParseFailure = { Message: string; Col: int option }

let parseLineFull (r: Resolver) (input: string) : Result<Stmt, ParseFailure> =
    ambientResolver.Value <- r

    try
        match run (stmtWith r) input with
        | Success(s, _, _) -> Result.Ok s
        | Failure(msg, err, _) ->
            let col =
                if err.Position.Line = 1L then
                    Some(int err.Position.Column)
                else
                    None

            Result.Error { Message = msg; Col = col }
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
