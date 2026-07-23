module Weir.Ast

open Weir.Types

type Pos = { Line: int; Col: int }

type Span = { Start: Pos; End: Pos }

module Span =
    let union (a: Span) (b: Span) = { Start = a.Start; End = b.End }

type Pattern = { PKind: PatternKind; PSpan: Span }

and PatternKind =
    | PWildcard
    | PVar of string
    | PBool of bool
    | PInt of int64
    | PStr of string
    | PUnit
    | PTuple of Pattern list
    | PCase of ctor: string * arg: Pattern option
    // the bespoke Regex pattern [D:regex-pattern] — one pattern kind,
    // NOT a general active-pattern mechanism. The literal is kept
    // verbatim (its backslashes belong to the regex engine); litSpan
    // aims check errors at the literal, not the whole pattern.
    | PRegex of pattern: string * litSpan: Span * raw: bool * binder: Pattern

type InterpPart<'e> =
    | IStr of string
    | IExpr of 'e

type Expr = { Kind: ExprKind; Span: Span }

and ExprKind =
    | EInt of value: int64
    | EStr of string
    | EBool of bool
    | EUnit
    | EVar of string
    | ELet of name: string * value: Expr * body: Expr
    | ELambda of param: string * body: Expr
    | EApp of fn: Expr * arg: Expr
    | EPipe of arg: Expr * fn: Expr
    | EField of target: Expr * field: string * fieldSpan: Span
    | EBinOp of op: string * left: Expr * right: Expr
    | ERecord of fields: (string * Span * Expr) list
    | EMatch of scrutinee: Expr * arms: (Pattern * Expr option * Expr) list
    | EIf of cond: Expr * thn: Expr * els: Expr option
    | ESeq of first: Expr * rest: Expr
    | EFrom of format: string * tyName: string option
    | ETo of format: string
    | EList of items: Expr list
    | ETuple of items: Expr list
    | ELetPat of binder: Pattern * value: Expr * body: Expr
    | ELambdaPat of binder: Pattern * body: Expr
    | ECmd of prog: string * args: Expr list * env: Expr option
    // copy-and-update [D:record-update]: paths carry nested sugar
    // (I.X); the checker walks them, eval overlays — source ONCE
    | EUpdate of source: Expr * updates: ((string * Span) list * Expr) list
    | EInterp of parts: InterpPart<Expr> list

// [<Name arg>] attachment [D:attributes] — check-time, fully erased
type AttrSpec =
    { AName: string
      AArg: AttrArg option
      ASpan: Span }

type DeclBody =
    | DRecord of fields: (string * Ty * AttrSpec list) list
    | DUnion of cases: (string * Ty option) list

type Decl =
    { Name: string
      Params: string list
      Body: DeclBody
      Span: Span }

// the expression tree's child list — tooling walks share this (the
// TypedExpr twin lives in Check.childExprs)
let exprChildren (e: Expr) : Expr list =
    match e.Kind with
    | EInt _
    | EStr _
    | EBool _
    | EUnit
    | EVar _
    | EFrom _
    | ETo _ -> []
    | ELet(_, v, b) -> [ v; b ]
    | ELetPat(_, v, b) -> [ v; b ]
    | ELambda(_, b) -> [ b ]
    | ELambdaPat(_, b) -> [ b ]
    | EApp(f, x) -> [ f; x ]
    | EPipe(x, f) -> [ x; f ]
    | EField(t, _, _) -> [ t ]
    | EBinOp(_, l, r) -> [ l; r ]
    | ERecord fields -> fields |> List.map (fun (_, _, v) -> v)
    | EMatch(s, arms) -> s :: (arms |> List.collect (fun (_, g, b) -> (g |> Option.toList) @ [ b ]))
    | EIf(c, t, e) -> c :: t :: Option.toList e
    | ESeq(a, b) -> [ a; b ]
    | EList items -> items
    | ETuple items -> items
    | ECmd(_, args, envO) -> args @ Option.toList envO
    | EUpdate(src, ups) -> src :: (ups |> List.map snd)
    | EInterp parts ->
        parts
        |> List.choose (function
            | IExpr e -> Some e
            | IStr _ -> None)

type Stmt =
    | SLet of name: string * value: Expr
    | SLetPat of binder: Pattern * value: Expr
    | SExpr of Expr
    | SCmd of Expr
    | SType of Decl

// Span-free sexpr rendering — the parse-SHAPE language. Two consumers:
// the test suite's parse pins and fmt's respace safety check (a
// formatted statement must sexpr-match its original) [D:fmt-respace].
let rec sexprPat (p: Pattern) : string =
    match p.PKind with
    | PWildcard -> "_"
    | PBool b -> if b then "true" else "false"
    | PInt n -> string n
    | PStr s -> $"\"{s}\""
    | PUnit -> "()"
    | PVar x -> x
    | PTuple ps -> "(" + (ps |> List.map sexprPat |> String.concat ", ") + ")"
    | PCase(c, None) -> c
    | PCase(c, Some arg) -> $"({c} {sexprPat arg})"
    | PRegex(pat, _, _, binder) -> $"(regex \"{pat}\" {sexprPat binder})"

let rec sexpr (e: Expr) : string =
    match e.Kind with
    | EInt n -> string n
    | EStr s -> $"\"{s}\""
    | EBool b -> if b then "true" else "false"
    | EVar x -> x
    | EUnit -> "()"
    | ELet(n, v, b) -> $"(let {n} {sexpr v} {sexpr b})"
    | ELetPat(p, v, b) -> $"(letpat {sexprPat p} {sexpr v} {sexpr b})"
    | ELambda(p, b) -> $"(fun {p} {sexpr b})"
    | ELambdaPat(p, b) -> $"(funpat {sexprPat p} {sexpr b})"
    | EApp(f, a) -> $"({sexpr f} {sexpr a})"
    | EPipe(a, f) -> $"({sexpr a} |> {sexpr f})"
    | EField(t, f, _) -> $"{sexpr t}.{f}"
    | EBinOp(op, l, r) -> $"({op} {sexpr l} {sexpr r})"
    | ERecord fields ->
        let body =
            fields |> List.map (fun (n, _, v) -> $"{n} = {sexpr v}") |> String.concat "; "

        "{" + body + "}"
    | EUpdate(src, ups) ->
        let body =
            ups
            |> List.map (fun (path, v) -> (path |> List.map fst |> String.concat ".") + $" = {sexpr v}")
            |> String.concat "; "

        "{" + sexpr src + " with " + body + "}"
    | EMatch(scrut, arms) ->
        let showArm (p, g, b) =
            match g with
            | None -> $"[{sexprPat p} -> {sexpr b}]"
            | Some g -> $"[{sexprPat p} when {sexpr g} -> {sexpr b}]"

        let armsStr = arms |> List.map showArm |> String.concat " "
        $"(match {sexpr scrut} {armsStr})"
    | EIf(c, t, None) -> $"(if {sexpr c} {sexpr t})"
    | EIf(c, t, Some e) -> $"(if {sexpr c} {sexpr t} {sexpr e})"
    | ESeq(a, b) -> $"(seq {sexpr a} {sexpr b})"
    | EFrom(fmt, None) -> $"(from {fmt})"
    | EFrom(fmt, Some ty) -> $"(from {fmt} {ty})"
    | ETo fmt -> $"(to {fmt})"
    | EList items ->
        let body = items |> List.map sexpr |> String.concat "; "
        $"[{body}]"
    | ETuple items -> "(tuple " + (items |> List.map sexpr |> String.concat ", ") + ")"
    | EInterp parts ->
        let body =
            parts
            |> List.map (function
                | IStr s -> $"\"{s}\""
                | IExpr e -> "{" + sexpr e + "}")
            |> String.concat ""

        $"(interp {body})"
    | ECmd(prog, [], None) -> $"(cmd {prog})"
    | ECmd(prog, args, None) ->
        let body = args |> List.map sexpr |> String.concat " "
        $"(cmd {prog} {body})"
    | ECmd(prog, args, Some envE) ->
        let body = args |> List.map sexpr |> String.concat " "
        $"(cmdenv {sexpr envE} {prog} {body})"

let sexprStmt (s: Stmt) : string =
    match s with
    | SLet(n, e) -> $"(slet {n} {sexpr e})"
    | SLetPat(p, e) -> $"(sletpat {sexprPat p} {sexpr e})"
    | SExpr e -> $"(sexpr {sexpr e})"
    | SCmd e -> $"(scmd {sexpr e})"
    | SType d -> $"(stype {d.Name})"
