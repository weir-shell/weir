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
          "to" ]

type Resolver =
    { IsKnown: string -> bool
      IsCommandCallable: string -> bool
      IsExternal: string -> bool
      ExternalNames: unit -> seq<string> }

let private isIdentStart c = isLetter c || c = '_'
let private isIdentCont c = isLetter c || isDigit c || c = '_'

let private ws: Parser<unit, unit> = spaces
let private str_ws s = pstring s >>. ws

let private pos (p: Position) : Pos =
    { Line = int p.Line
      Col = int p.Column }

let private spanned (p: Parser<'a, unit>) : Parser<'a * Span, unit> =
    pipe3 getPosition p getPosition (fun s x e -> x, { Start = pos s; End = pos e })

let private rawWord = many1Satisfy2 isIdentStart isIdentCont

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

let private intLit =
    spanned (
        many1Satisfy isDigit .>>. opt (attempt (pchar '<' >>. rawWord .>> pchar '>'))
        |>> fun (digits, m) -> EInt(int digits, m)
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
    spanned (pchar '(' >>. ws >>. expr .>> pchar ')')
    |>> fun (inner, span) -> { inner with Span = span }
    .>> ws

let private fieldAssign =
    identSpanned .>> str_ws "=" .>>. expr |>> fun ((n, s), v) -> n, s, v

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
        .>>. opt (attempt (pchar '<' >>. rawWord .>> pchar '>'))
        |>> fun (digits, m) -> EInt(-(int digits), m)
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
            | None -> a, { Kind = EInt(1, None); Span = a.Span }, b

        match step.Kind with
        | EInt(0, _) -> failFatally "range step is zero"
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
                  sepBy expr (str_ws ";") .>> pchar ']' |>> Choice2Of2 ]
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

let private atom =
    choice [ intLit; strLit; interpLit; unitLit; parens; recordLit; listLit; wordAtom ]

let private fieldSuffix = pchar '.' >>. spanned rawWord .>> ws

let private postfixAtom =
    atom .>>. many fieldSuffix
    |>> fun (target, fields) ->
        let applied =
            fields
            |> List.fold
                (fun t (name, fspan) ->
                    { Kind = EField(t, name, fspan)
                      Span = Span.union t.Span fspan })
                target

        match target.Kind, fields with
        | EVar "_", _ :: _ ->
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

let private lambda =
    pipe3 getPosition (keyword "fun" >>. ident .>> str_ws "->") expr (fun p param body ->
        { Kind = ELambda(param, body)
          Span = { Start = pos p; End = body.Span.End } })

let private letIn =
    pipe3
        getPosition
        (keyword "let" >>. ident .>> str_ws "=" .>>. expr .>> keyword "in")
        expr
        (fun p (name, value) body ->
            { Kind = ELet(name, value, body)
              Span = { Start = pos p; End = body.Span.End } })

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
    opp.AddOperator(InfixOperator("-", ws, 6, Associativity.Left, binOp "-"))
    opp.AddOperator(InfixOperator("*", ws, 7, Associativity.Left, binOp "*"))
    opp.AddOperator(InfixOperator("/", ws, 7, Associativity.Left, binOp "/"))
    opp

let private opp = mkOpp true
let private segOpp = mkOpp false

let private pat, private patRef = createParserForwardedToRef<Pattern, unit> ()

let private patWord =
    attempt (spanned rawWord >>= fun (w, span) -> notKeyword w >>% (w, span)) .>> ws

let private patAtom =
    choice
        [ between (str_ws "(") (str_ws ")") pat
          patWord
          |>> fun (w, span) ->
              let kind =
                  if w = "_" then PWildcard
                  elif Char.IsUpper w[0] then PCase(w, None)
                  else PVar w

              { PKind = kind; PSpan = span } ]

patRef.Value <-
    choice
        [ between (str_ws "(") (str_ws ")") pat
          patWord
          >>= fun (w, span) ->
              if w = "_" then
                  preturn { PKind = PWildcard; PSpan = span }
              elif Char.IsUpper w[0] then
                  opt patAtom
                  |>> fun arg ->
                      let e = arg |> Option.map (fun a -> a.PSpan.End) |> Option.defaultValue span.End

                      { PKind = PCase(w, arg)
                        PSpan = { Start = span.Start; End = e } }
              else
                  preturn { PKind = PVar w; PSpan = span } ]

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

let private matchArm = pat .>> str_ws "->" .>>. expr

let private matchExpr =
    pipe3
        getPosition
        (keyword "match" >>. expr .>> keyword "with")
        (opt (str_ws "|") >>. matchArm .>>. many (attempt (str_ws "|" >>. matchArm)))
        (fun p scrut (arm0, rest) ->
            let arms = arm0 :: rest
            let lastBody = snd (List.last arms)

            { Kind = EMatch(scrut, arms)
              Span =
                { Start = pos p
                  End = lastBody.Span.End } })

opp.TermParser <- choice [ lambda; letIn; matchExpr; fromExpr; toExpr; appChain ]
segOpp.TermParser <- choice [ lambda; letIn; matchExpr; fromExpr; toExpr; appChain ]
exprRef.Value <- opp.ExpressionParser

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

let private cmdArg =
    choice
        [ strLit
          singleQuoted
          interpLit
          spliceVar
          parens
          spanned (cmdWord |>> EStr) |>> mkExpr .>> ws ]

type private HeadKind =
    | ExternalHead
    | BuiltinHead

let private commandSegment (r: Resolver) : Parser<Expr, unit> =
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
            elif isIdentLike w && r.IsCommandCallable w then
                preturn (BuiltinHead, w, span)
            elif isIdentLike w && (keywords.Contains w || r.IsKnown w) then
                fail "known name; expression mode"
            elif r.IsExternal w then
                preturn (ExternalHead, w, span)
            else
                fail "not an external command"

    attempt head .>>. many cmdArg
    |>> fun ((kind, prog, span), args) ->
        let fullSpan =
            { Start = span.Start
              End =
                (match args with
                 | [] -> span.End
                 | _ -> (List.last args).Span.End) }

        match kind with
        | ExternalHead ->
            { Kind = ECmd(prog, args)
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

let private segment (r: Resolver) : Parser<Expr, unit> = choice [ commandSegment r; segExpr ]

type private Seg =
    | Stage of Expr
    | CompleteMarker of Span

let private completeMarker =
    attempt (
        spanned (pstring "complete" .>> notFollowedBy (satisfy cmdWordChar))
        .>> ws
        .>> lookAhead (choice [ pipeSep |>> ignore; eof ])
    )
    |>> fun (_, span) -> CompleteMarker span

let private cmdLine (r: Resolver) : Parser<Expr, unit> =
    commandSegment r
    .>>. many (pipeSep >>. (completeMarker <|> (segment r |>> Stage)))
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
                        | ECmd(prog, args) ->
                            let span = Span.union acc.Span mspan

                            let headVar =
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

let private tySyn, private tySynRef = createParserForwardedToRef<Ty, unit> ()

tySynRef.Value <-
    choice
        [ pchar '\'' >>. rawWord .>> ws |>> TVar
          rawWord
          >>= fun w ->
              match w with
              | "int" -> opt (attempt (pchar '<' >>. rawWord .>> pchar '>')) .>> ws |>> TInt
              | "string" -> ws >>% TStr
              | "bool" -> ws >>% TBool
              | "unit" -> ws >>% TUnit
              | "seq" -> ws >>. between (str_ws "<") (str_ws ">") tySyn |>> TSeq
              | w when keywords.Contains w -> fail $"'{w}' is a keyword"
              | w ->
                  ws >>. opt (between (str_ws "<") (str_ws ">") (sepBy1 tySyn (str_ws ",")))
                  |>> fun args -> TNamed(w, Option.defaultValue [] args) ]

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

let private topLet =
    attempt (keyword "let" >>. ident .>> str_ws "=" .>>. expr .>> eof) |>> SLet

let private stmtWith (r: Resolver) =
    ws
    >>. choice [ typeDecl .>> eof; topLet; cmdLine r .>> eof |>> SCmd; expr .>> eof |>> SExpr ]

let private noExternals =
    { IsKnown = fun _ -> true
      IsCommandCallable = fun _ -> false
      IsExternal = fun _ -> false
      ExternalNames = fun () -> Seq.empty }

let parseLine (r: Resolver) (input: string) : Result<Stmt, string> =
    match run (stmtWith r) input with
    | Success(s, _, _) -> Result.Ok s
    | Failure(msg, _, _) -> Result.Error msg

let parseStmt (input: string) : Result<Stmt, string> = parseLine noExternals input

let parseExpr (input: string) : Result<Expr, string> =
    match parseStmt input with
    | Result.Ok(SExpr e)
    | Result.Ok(SCmd e) -> Result.Ok e
    | Result.Ok _ -> Result.Error "expected an expression, got a declaration"
    | Result.Error msg -> Result.Error msg
