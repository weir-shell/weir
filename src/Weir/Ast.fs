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
    | PCase of ctor: string * arg: Pattern option

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
    | ECmd of prog: string * args: Expr list * env: Expr option
    | EInterp of parts: InterpPart<Expr> list

type DeclBody =
    | DRecord of fields: (string * Ty) list
    | DUnion of cases: (string * Ty option) list

type Decl =
    { Name: string
      Params: string list
      Body: DeclBody
      Span: Span }

type Stmt =
    | SLet of name: string * value: Expr
    | SExpr of Expr
    | SCmd of Expr
    | SType of Decl
