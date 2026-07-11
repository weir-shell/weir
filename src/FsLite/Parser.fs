module FsLite.Parser

open FParsec
open FsLite.Ast

let private keywords = Set [ "let"; "in"; "fun"; "true"; "false" ]

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

let private ident =
    attempt (
        rawWord
        >>= fun s ->
            if keywords.Contains s then
                fail $"'{s}' is a keyword"
            else
                preturn s
    )
    .>> ws

let private expr, private exprRef = createParserForwardedToRef<Expr, unit> ()

let private mkExpr (kind, span) = { Kind = kind; Span = span }

let private intLit =
    spanned (
        many1Satisfy isDigit .>>. opt (attempt (pchar '<' >>. rawWord .>> pchar '>'))
        |>> fun (digits, m) -> EInt(int digits, m)
    )
    |>> mkExpr
    .>> ws

let private strLit =
    spanned (between (pchar '"') (pchar '"') (manySatisfy ((<>) '"')) |>> EStr)
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

let private atom = choice [ intLit; strLit; parens; wordAtom ]

let private fieldSuffix = pchar '.' >>. spanned rawWord .>> ws

let private postfixAtom =
    atom .>>. many fieldSuffix
    |>> fun (target, fields) ->
        fields
        |> List.fold
            (fun t (name, fspan) ->
                { Kind = EField(t, name, fspan)
                  Span = Span.union t.Span fspan })
            target

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

let private opp = OperatorPrecedenceParser<Expr, unit, unit>()

let private binOp op l r =
    { Kind = EBinOp(op, l, r)
      Span = Span.union l.Span r.Span }

let private pipeOp l r =
    { Kind = EPipe(l, r)
      Span = Span.union l.Span r.Span }

opp.TermParser <- choice [ lambda; letIn; appChain ]

opp.AddOperator(InfixOperator("|>", ws, 1, Associativity.Left, pipeOp))
opp.AddOperator(InfixOperator("|", ws, 1, Associativity.Left, pipeOp))
opp.AddOperator(InfixOperator("==", ws, 4, Associativity.Left, binOp "=="))
opp.AddOperator(InfixOperator(">", ws, 4, Associativity.Left, binOp ">"))
opp.AddOperator(InfixOperator("<", ws, 4, Associativity.Left, binOp "<"))
opp.AddOperator(InfixOperator("+", ws, 6, Associativity.Left, binOp "+"))
opp.AddOperator(InfixOperator("-", ws, 6, Associativity.Left, binOp "-"))
opp.AddOperator(InfixOperator("*", ws, 7, Associativity.Left, binOp "*"))
opp.AddOperator(InfixOperator("/", ws, 7, Associativity.Left, binOp "/"))

exprRef.Value <- opp.ExpressionParser

let private topLet =
    attempt (keyword "let" >>. ident .>> str_ws "=" .>>. expr .>> eof) |>> SLet

let private stmt = ws >>. (topLet <|> (expr .>> eof |>> SExpr))

let parseStmt (input: string) : Result<Stmt, string> =
    match run stmt input with
    | Success(s, _, _) -> Result.Ok s
    | Failure(msg, _, _) -> Result.Error msg

let parseExpr (input: string) : Result<Expr, string> =
    match parseStmt input with
    | Result.Ok(SExpr e) -> Result.Ok e
    | Result.Ok(SLet _) -> Result.Error "expected an expression, got a let statement"
    | Result.Error msg -> Result.Error msg
