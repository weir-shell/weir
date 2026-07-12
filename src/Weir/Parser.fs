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

let private listLit =
    spanned (pchar '[' >>. ws >>. sepBy expr (str_ws ";") .>> pchar ']' |>> EList)
    |>> mkExpr
    .>> ws

let private atom = choice [ intLit; strLit; parens; recordLit; listLit; wordAtom ]

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
          spliceVar
          parens
          spanned (cmdWord |>> EStr) |>> mkExpr .>> ws ]

let private commandSegment (r: Resolver) : Parser<Expr, unit> =
    let head =
        spanned (opt (pchar '^') .>>. cmdWord) .>> ws
        >>= fun ((forced, w), span) ->
            if forced.IsSome then
                if r.IsExternal w then
                    preturn (w, span)
                else
                    failFatally $"command not found: {w}{didYouMean w (r.ExternalNames())}"
            elif isIdentLike w && (keywords.Contains w || r.IsKnown w) then
                fail "known name; expression mode"
            elif r.IsExternal w then
                preturn (w, span)
            else
                fail "not an external command"

    attempt head .>>. many cmdArg
    |>> fun ((prog, span), args) ->
        { Kind = ECmd(prog, args)
          Span =
            { Start = span.Start
              End =
                (match args with
                 | [] -> span.End
                 | _ -> (List.last args).Span.End) } }

let private pipeSep = (attempt (pstring "|>") <|> pstring "|") .>> ws

let private segment (r: Resolver) : Parser<Expr, unit> = choice [ commandSegment r; segExpr ]

let private cmdLine (r: Resolver) : Parser<Expr, unit> =
    commandSegment r .>>. many (pipeSep >>. segment r)
    |>> fun (h, rest) ->
        rest
        |> List.fold
            (fun acc seg ->
                { Kind = EPipe(acc, seg)
                  Span = Span.union acc.Span seg.Span })
            h

let private tySyn, private tySynRef = createParserForwardedToRef<Ty, unit> ()

tySynRef.Value <-
    rawWord
    >>= fun w ->
        match w with
        | "int" -> opt (attempt (pchar '<' >>. rawWord .>> pchar '>')) .>> ws |>> TInt
        | "string" -> ws >>% TStr
        | "bool" -> ws >>% TBool
        | "seq" -> ws >>. between (str_ws "<") (str_ws ">") tySyn |>> TSeq
        | w when keywords.Contains w -> fail $"'{w}' is a keyword"
        | w -> ws >>% TNamed w

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

let private typeDecl =
    pipe3
        getPosition
        (keyword "type" >>. ident .>> str_ws "=" .>>. (recordBody <|> unionBody))
        getPosition
        (fun p (name, body) e ->
            SType
                { Name = name
                  Body = body
                  Span = { Start = pos p; End = pos e } })

let private topLet =
    attempt (keyword "let" >>. ident .>> str_ws "=" .>>. expr .>> eof) |>> SLet

let private stmtWith (r: Resolver) =
    ws
    >>. choice
            [ typeDecl .>> eof
              topLet
              attempt (cmdLine r .>> eof) |>> SExpr
              expr .>> eof |>> SExpr ]

let private noExternals =
    { IsKnown = fun _ -> true
      IsExternal = fun _ -> false
      ExternalNames = fun () -> Seq.empty }

let parseLine (r: Resolver) (input: string) : Result<Stmt, string> =
    match run (stmtWith r) input with
    | Success(s, _, _) -> Result.Ok s
    | Failure(msg, _, _) -> Result.Error msg

let parseStmt (input: string) : Result<Stmt, string> = parseLine noExternals input

let parseExpr (input: string) : Result<Expr, string> =
    match parseStmt input with
    | Result.Ok(SExpr e) -> Result.Ok e
    | Result.Ok _ -> Result.Error "expected an expression, got a declaration"
    | Result.Error msg -> Result.Error msg
