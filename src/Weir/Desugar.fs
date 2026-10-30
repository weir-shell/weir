module Weir.Desugar

// The grammar's shared half [D:hand-parser]: everything both parsers
// (Parser.fs's FParsec grammar and Fast.fs's hand-written one) must
// agree on byte for byte — the keyword list, the identifier classes,
// the sentinels, and the desugars that build the tree. One copy each,
// so the two can never drift apart on what a construct means.

open Weir.Ast

let keywords =
    Set
        [ "within"
          // the bare scope's teardown segment [D:within-always]
          "always"
          // the bounded-loop pair and their predicate segment [D:retry-poll]
          "retry"
          "poll"
          // the loop a stop signal ends [D:repeat]
          "repeat"
          "until"
          "let"
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
          // the purity assertion's two spellings [D:pure-stage1]: the
          // `pure` block head and the `let pure` modifier
          "pure"
          // the read-only assertion [D:pure-stage2]: a `readonly`
          // block head, one tier up from `pure`
          "readonly"
          // the plan/apply capture [D:plan-apply]: a `plan` block head —
          // the third standalone withinKinds head. Reserved so a bare
          // `plan` never resolves as an identifier or command head; with
          // plan/apply in the language, `let plan = …` is a confusing
          // shadow (the `let match =` class). apply/preview stay ordinary
          // members, no reservation.
          "plan"
          // the module system's two words [D:modules-v1]: reserved so a
          // bare `module`/`import` never resolves as an identifier or a
          // command head (keyword-domination)
          "module"
          "import"
          // the general effect loop [D:for-do]
          "for"
          "do"
          // its parallel twin over Seq.piter [D:pfor]
          "pfor"
          // the implicit-match lambda [D:function-keyword]
          "function"
          // foreign control-flow words, reserved so they teach weir's
          // spelling instead of resolving as PATH programs
          // (PLAN-dx-review D4); `def` is an ordinary name whose
          // teaching keys on the statement shape [D:def-shape]
          "while"
          "return"
          "try"
          // the inverse of try [D:orfail-form]: `e |> orFail` turns a
          // failed Result/Option back into a raise; reserved like try
          "orFail" ]

// the keyword set, exposed for tooling resolvers (weir check's
// assume-command rule must never claim a keyword head)
let isKeyword (w: string) = keywords.Contains w

type Resolver =
    { IsKnown: string -> bool
      IsCommandCallable: string -> bool
      IsExternal: string -> bool
      ExternalNames: unit -> seq<string>
      // a bare module member's home (where -> Seq) [D:bare-partition]:
      // the strict-context teaching names the qualified spelling
      BareHome: string -> string option
      // a command-head alias [D:command-head-alias], REPL-only: a short
      // name -> (real exe, fixed prefix args). Consulted only in
      // command-head position, before PATH; the `^` sigil skips it.
      // Single-hop by construction (the table stores exes, never aliases).
      // Scripts/-e supply the always-None resolver, so aliases never leak.
      AliasHead: string -> (string * string list) option }

let isIdentStart (c: char) = System.Char.IsLetter c || c = '_'

let isIdentCont (c: char) =
    System.Char.IsLetter c || (c >= '0' && c <= '9') || c = '_'

// The sibling sentinel [D:sibling-sentinel]: the assembler joins body
// statement-siblings with this instead of ';' so command mode — which
// swallows a user-typed ';' as a bareword arg (the prior-bleed
// teaching, kept) — stops at the machine boundary. Same width as
// " ; " (3 chars) so every span mapping through the segment table is
// byte-identical. Unproduceable: assemble rejects any source line
// carrying it, so it reaches the grammar only from the assembler (as
// the '|' keys do — a token user text cannot form).
[<Literal>]
let sibSep = '\u001F'

let sibSepStr = System.String(sibSep, 1)

// the district content terminator [D:district-terminates]: a district
// (`yaml`/`<<<`) that closes mid-statement — a following `|>`, `in`, or
// sibling — has the assembler append this after its content, so
// districtTail knows where content ends and the expression grammar
// resumes (heredocDistrict is already an opp term, so the pipe composes
// for free). Same unproduceability contract as sibSep: a source line
// carrying it is rejected where text becomes logical lines.
[<Literal>]
let districtClose = '\u001E'

let districtCloseStr = System.String(districtClose, 1)

// the record/list field separator [D:field-sep-sentinel]: the assembler
// joins newline-separated record fields and list elements with this, not
// a bare `;`, so a field value's own `;`-sequencing (a lambda body, a
// block) cannot swallow the separator and the next field. seqExpr does
// not sequence on it (only `;` and sibSep) — it ends the field, and the
// record/list sepBy splits on it. Same unproduceability contract as sibSep.
[<Literal>]
let fieldSep = '\u001D'

let fieldSepStr = System.String(fieldSep, 1)

let mkExpr (kind, span) = { Kind = kind; Span = span }

let binOp op l r =
    { Kind = EBinOp(op, l, r)
      Span = Span.union l.Span r.Span }

// `x |> try` folds into one node [D:try-form]: the form captures its left
// side, so it is not an application of the left's value
let pipeOp l r =
    match r.Kind with
    | ETryForm asResult ->
        { Kind = ETry(l, asResult)
          Span = Span.union l.Span r.Span }
    // and its inverse [D:orfail-form]: the form's own span is the raise site
    | EOrFailForm msg ->
        { Kind = EOrFail(l, msg, r.Span)
          Span = Span.union l.Span r.Span }
    | _ ->
        { Kind = EPipe(l, r)
          Span = Span.union l.Span r.Span }

let curryParams (ps: Pattern list) (value: Expr) : Expr =
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

// xs[i] desugars to Seq.item i xs — F# 6 dotless-indexing whitespace
// rule: no space = indexing; a space means application (f [1; 2] stays
// an application of a list). Immediacy is checked against the target's
// span end (spans record positions before trailing whitespace).
let indexDesugar (target: Expr) (idx: Expr) (endPos: Pos) : Expr =
    let span =
        { Start = target.Span.Start
          End = endPos }

    let seqItem = { Kind = EVar "|seqItem"; Span = span }

    { Kind =
        EApp(
            { Kind = EApp(seqItem, idx)
              Span = span },
            target
        )
      Span = span }

// xs[^n] — F#'s from-the-end single index [D:range-slicing]: the element
// n from the end (`^1` is the last), `|seqItemFromEnd n xs` — its own
// desugar, so an out-of-range raise names the `^n` and the length.
let fromEndIndexDesugar (target: Expr) (idx: Expr) (endPos: Pos) : Expr =
    let span =
        { Start = target.Span.Start
          End = endPos }

    let fromEnd = { Kind = EVar "|seqItemFromEnd"; Span = span }

    { Kind = EApp({ Kind = EApp(fromEnd, idx); Span = span }, target)
      Span = span }

// [a..s..b] is pure sugar for Seq.range a s b; [a; b; c] stays an eager list.
let rangeExpr (span: Span) (start: Expr) (step: Expr) (stop: Expr) : Expr =
    let rangeFn = { Kind = EVar "|seqRange"; Span = span }

    let app f a = { Kind = EApp(f, a); Span = span }
    app (app (app rangeFn start) step) stop

// a typed `~` is the home directory [D:tilde]: an unquoted word-leading
// `~` or `~/…` in a command line desugars to the run-time home plus the
// rest. Source syntax only — a quoted string, an interpolation or a
// spliced value never reaches here, so data is never re-read as syntax;
// `~user` stays literal
let isTildeWord (w: string) = w = "~" || w.StartsWith "~/"

let tildeExpr (w: string) (span: Span) : Expr =
    let at k = { Kind = k; Span = span }
    let home = at (EApp(at (EVar "|home"), at EUnit))

    if w = "~" then home else at (EBinOp("+", home, at (EStr(w.Substring 1))))

// right-nested ESeq over a non-empty element list — shared by seqExpr
// and topLet's command-first RHS so both fold identically
let foldSeqExpr (all: Expr list) : Expr =
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
