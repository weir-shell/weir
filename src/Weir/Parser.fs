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

// comma is the tuple constructor at F#'s precedence (2026-07-21, the
// bare-comma amendment): below `;` (weir-only cell, decided — `a, b ; c`
// is `(a, b) ; c`), above `|>` (`xs |> f, ys |> g` groups F#-identically).
// Command mode is untouched by construction: barewords keep their commas.
let private commaExpr, private commaExprRef =
    createParserForwardedToRef<Expr, unit> ()

let private intLit =
    spanned (
        many1Satisfy isDigit .>>. opt (attempt (pchar '<' >>. rawWord .>> pchar '>'))
        >>= fun (digits, m) ->
            match m, System.Int64.TryParse digits with
            | Some _, _ -> failFatally "measure literals were removed (2026-07-18); use bare int"
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

let private recordLit =
    spanned (pchar '{' >>. ws >>. sepBy1 fieldAssign (str_ws ";") .>> pchar '}' |>> ERecord)
    |>> mkExpr
    .>> ws

let private dotdot = pstring ".." .>> ws

// Range endpoints/steps are simple expressions only (literals, idents, field
// access, parenthesized anything) — reject-rather-than-guess. The attempt on
// fieldSuffix keeps the first dot of '..' out of field-access parsing. The
// negative-literal form exists for descending steps ([10.. -1 ..1]); weir has
// no unary minus elsewhere. rangeTerm is a forward ref: it needs atom, which
// needs listLit.
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
// Layer 1 (2026-07-20): sigils take an optional env slot between glyph
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

let private atom =
    choice
        [ intLit
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

let private appChain =
    many1 postfixAtom
    |>> List.reduce (fun f a ->
        { Kind = EApp(f, a)
          Span = Span.union f.Span a.Span })

// Binder patterns (2026-07-21, PLAN-pattern-binders): params are plain
// idents, `()`, or PARENTHESIZED irrefutable patterns (F# also requires
// the parens in param position). Refutability is a CHECK error.
let private binderParam, private binderParamRef =
    createParserForwardedToRef<Pattern, unit> ()

// let-binder: a full pattern, bare commas allowed at the top
// (`let x, y = ...` — the closed binder grammar makes the comma free)
let private binderPat, private binderPatRef =
    createParserForwardedToRef<Pattern, unit> ()

let private lambda =
    pipe3 getPosition (keyword "fun" >>. binderParam .>> str_ws "->") seqExpr (fun p param body ->
        let kind =
            match param.PKind with
            | PVar n -> ELambda(n, body)
            | PUnit -> ELambda("()", body)
            | _ -> ELambdaPat(param, body)

        { Kind = kind
          Span = { Start = pos p; End = body.Span.End } })

// let f x y = e desugars to nested lambdas (corpus-driven feature,
// 2026-07-20: the top mining yield — F#'s most common line shape).
// Params are plain idents OR () — the unit param pins its type in the
// checker (the name "()" is unforgeable through declarations); other
// pattern params stay rejected.

let private curryParams (ps: Pattern list) (value: Expr) : Expr =
    List.foldBack
        (fun (p: Pattern) body ->
            let kind =
                match p.PKind with
                | PVar n -> ELambda(n, body)
                | PUnit -> ELambda("()", body)
                | _ -> ELambdaPat(p, body)

            { Kind = kind; Span = value.Span })
        ps
        value

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
              (keyword "let" >>. ident .>>. many binderParam .>> str_ws "=" .>>. seqExpr
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

// literal patterns (2026-07-20 plan, session 1): int and string pin
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
    pipe4
        getPosition
        (keyword "if" >>. expr)
        (keyword "then" >>. seqExpr)
        (opt (keyword "else" >>. seqExpr))
        (fun p cond thn els ->
            let endPos = (els |> Option.defaultValue thn).Span.End

            { Kind = EIf(cond, thn, els)
              Span = { Start = pos p; End = endPos } })

opp.TermParser <- choice [ lambda; letIn; ifExpr; matchExpr; fromExpr; toExpr; appChain ]
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
                // '[' never heads a command (decided 2026-07-18): a line-head
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

let private completeMarker =
    attempt (
        spanned (pstring "complete" .>> notFollowedBy (satisfy cmdWordChar))
        .>> ws
        .>> lookAhead (choice [ pipeSep |>> ignore; pchar ')' |>> ignore; eof ])
    )
    |>> fun (_, span) -> CompleteMarker span

let private cmdLineWith
    (builtinHeads: bool)
    (argP: Parser<Expr, unit>)
    (sigilEnv: Expr option)
    (r: Resolver)
    : Parser<Expr, unit> =
    commandSegment builtinHeads argP sigilEnv r
    .>>. many (
        pipeSep
        >>. (completeMarker <|> (segment builtinHeads argP sigilEnv r |>> Stage))
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
                    | Result.Ok acc, CompleteMarker mspan ->
                        match acc.Kind with
                        | ECmd(prog, args, cenv) ->
                            let span = Span.union acc.Span mspan

                            // env sigils route through completedEnv — the
                            // same desugar family, env threaded up front
                            let headVar =
                                match cenv with
                                | Some e ->
                                    { Kind =
                                        EApp(
                                            { Kind = EVar "completedEnv"
                                              Span = mspan },
                                            e
                                        )
                                      Span = mspan }
                                | None ->
                                    { Kind = EVar "completed"
                                      Span = mspan }

                            let progArg = { Kind = EStr prog; Span = acc.Span }

                            let argList = { Kind = EList args; Span = acc.Span }

                            Result.Ok
                                { Kind =
                                    EApp(
                                        { Kind = EApp(headVar, progArg)
                                          Span = span },
                                        argList
                                    )
                                  Span = span }
                        | _ -> Result.Error "'complete' must directly follow a single external command segment")
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
                  | Some _ -> failFatally "measure literals were removed (2026-07-18); use bare int"
                  | None -> preturn TInt)
              | "string" -> ws >>% TStr
              | "bool" -> ws >>% TBool
              | "unit" -> ws >>% TUnit
              | "seq" -> ws >>. between (str_ws "<") (str_ws ">") tySyn |>> TSeq
              | w when keywords.Contains w -> fail $"'{w}' is a keyword"
              | w ->
                  ws >>. opt (between (str_ws "<") (str_ws ">") (sepBy1 tySyn (str_ws ",")))
                  |>> fun args -> TNamed(w, Option.defaultValue [] args) ]
    // t1 * t2 [* ...] is a tuple type (2026-07-21) — star was unclaimed
    // in type syntax since the measure removal
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
            // command mode never sits under a lambda (splice-soundness
            // invariant), so a param-ful let takes an expression RHS only
            // RHS takes sequenced blocks too (function bodies of effect
            // lines — the bicep-script receipt)
            let rhsP =
                if List.isEmpty ps then
                    cmdLineLetRhs r <|> seqExpr
                else
                    seqExpr

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

// Structured failure: the position travels as DATA (2026-07-20
// formalization — the runner used to regex `Ln: 1 Col: (\d+)` out of
// FParsec's message text, a silent break waiting on any FParsec
// update). Message text is unchanged; Col is Some only for the
// single-logical-line case the runner translates.
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
