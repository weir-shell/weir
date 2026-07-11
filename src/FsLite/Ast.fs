module FsLite.Ast

type Pos = { Line: int; Col: int }

type Span = { Start: Pos; End: Pos }

module Span =
    let union (a: Span) (b: Span) = { Start = a.Start; End = b.End }

type Expr = { Kind: ExprKind; Span: Span }

and ExprKind =
    | EInt of value: int * measure: string option
    | EStr of string
    | EBool of bool
    | EVar of string
    | ELet of name: string * value: Expr * body: Expr
    | ELambda of param: string * body: Expr
    | EApp of fn: Expr * arg: Expr
    | EPipe of arg: Expr * fn: Expr
    | EField of target: Expr * field: string * fieldSpan: Span
    | EBinOp of op: string * left: Expr * right: Expr

type Stmt =
    | SLet of name: string * value: Expr
    | SExpr of Expr
