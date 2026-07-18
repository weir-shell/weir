module Weir.Script

open System
open Weir.Ast
open Weir.Types

let stripComment (line: string) : string =
    let mutable i = 0
    let mutable inDouble = false
    let mutable inSingle = false
    let mutable cut = -1

    while i < line.Length && cut < 0 do
        let c = line[i]

        if inDouble then
            if c = '\\' && i + 1 < line.Length then
                i <- i + 1
            elif c = '"' then
                inDouble <- false
        elif inSingle then
            if c = '\'' then
                inSingle <- false
        elif c = '"' then
            inDouble <- true
        elif c = '\'' then
            inSingle <- true
        elif c = '/' && i + 1 < line.Length && line[i + 1] = '/' then
            cut <- i

        i <- i + 1

    if cut >= 0 then line.Substring(0, cut) else line

type LogicalLine =
    { Text: string
      Head: int
      Segments: (int * int * int) list }

// Block lets — F# light syntax at the assembly layer, the same way F#'s own
// lexer implements it (token insertion at offside boundaries): a continuation
// line beginning with `let` opens a binding; the next line at the SAME
// indentation closes it by joining with " in " instead of " ", so the
// single-line grammar sees the explicit form. `|`-headed lines are inert to
// the stack ONLY while it is empty (statement-level pipeline continuations
// and column-0 match arms — the two customers); with a binding pending they
// follow the plain indent rules, so a dedented arm inside a block is the
// same "needs a body" error F# gives the shape. Every pending let must be
// closed before the statement ends.
let assemble (numbered: (int * string) list) : Result<LogicalLine list, string> =
    let noBody letLine =
        Error
            $"line {letLine}: this let needs a body — an expression at the same indentation must follow before the statement ends"

    let close (current: (LogicalLine * (int * int) list) option) acc =
        match current with
        | Some(_, (_, letLine) :: _) -> noBody letLine
        | Some(ll, []) ->
            Ok(
                { ll with
                    Segments = List.rev ll.Segments }
                :: acc
            )
        | None -> Ok acc

    let isLetHead (piece: string) = piece.StartsWith "let "

    let folded =
        numbered
        |> List.fold
            (fun state (lineNo, raw) ->
                match state with
                | Error e -> Error e
                | Ok(current, acc, blankSinceHead) ->
                    if raw.Trim() = "" then
                        close current acc |> Result.map (fun acc -> None, acc, true)
                    elif raw[0] = ' ' || raw[0] = '\t' || raw[0] = '|' then
                        let indent = raw |> Seq.takeWhile (fun c -> c = ' ' || c = '\t') |> Seq.length

                        if raw.Substring(0, indent).Contains '\t' then
                            Error $"line {lineNo}: tabs are not allowed in indentation"
                        else
                            match current with
                            | None ->
                                if blankSinceHead then
                                    Error
                                        $"line {lineNo}: continuation after a blank line has no statement to continue"
                                else
                                    Error $"line {lineNo}: continuation without a statement"
                            | Some(ll, stack) ->
                                let piece = raw.Substring indent

                                let closed =
                                    if piece.StartsWith "|" then
                                        match stack with
                                        | (k, letLine) :: _ when indent <= k -> noBody letLine
                                        | _ -> Ok(stack, " ")
                                    else
                                        match stack with
                                        | (k, letLine) :: _ when indent < k -> noBody letLine
                                        | (k, _) :: rest when indent = k -> Ok(rest, " in ")
                                        | _ -> Ok(stack, " ")

                                closed
                                |> Result.map (fun (stack, sep) ->
                                    let stack = if isLetHead piece then (indent, lineNo) :: stack else stack

                                    let joinedStart = ll.Text.Length + sep.Length

                                    Some(
                                        { ll with
                                            Text = ll.Text + sep + piece
                                            Segments = (joinedStart, lineNo, indent) :: ll.Segments },
                                        stack
                                    ),
                                    acc,
                                    blankSinceHead)
                    else
                        close current acc
                        |> Result.map (fun acc ->
                            Some(
                                { Text = raw
                                  Head = lineNo
                                  Segments = [ (0, lineNo, 0) ] },
                                []
                            ),
                            acc,
                            false))
            (Ok(None, [], false))

    match folded with
    | Error e -> Error e
    | Ok(current, acc, _) -> close current acc |> Result.map List.rev

let translate (ll: LogicalLine) (col: int) : int * int =
    let joinedIdx = col - 1

    let segStart, physLine, physIndent =
        ll.Segments |> List.filter (fun (js, _, _) -> js <= joinedIdx) |> List.last

    physLine, joinedIdx - segStart + physIndent + 1

type Mode =
    | Strict
    | Loose

type private CheckedStmt =
    | CLet of name: string * te: Check.TypedExpr
    | CExpr of te: Check.TypedExpr
    | CCmd of te: Check.TypedExpr
    | CType of decl: Decl
    | CNoop

let private baseEnvs (mode: Mode) (scriptArgs: string list) =
    let typeEnv =
        match mode with
        | Strict -> Builtins.typeEnvStrict
        | Loose -> Builtins.typeEnv

    let typeEnv, valueEnv = Prelude.extend typeEnv Builtins.valueEnv

    let typeEnv =
        { typeEnv with
            Values =
                typeEnv.Values
                |> Map.add "args" (generalize (TSeq TStr))
                |> Map.add "stdin" (generalize (TSeq TStr)) }

    let stdinStream =
        Eval.VSeq(
            Seq.delay (fun () ->
                seq {
                    let mutable line = Console.In.ReadLine()

                    while line <> null do
                        yield Eval.VStr line
                        line <- Console.In.ReadLine()
                })
        )

    let valueEnv =
        valueEnv
        |> Map.add "args" (Eval.VSeq(scriptArgs |> List.map Eval.VStr :> seq<Eval.Value>))
        |> Map.add "stdin" stdinStream

    typeEnv, valueEnv

let private resolver (typeEnv: TypeEnv) : Parser.Resolver =
    { IsKnown = fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules
      IsCommandCallable = fun n -> Builtins.commandCallable.Contains n
      IsExternal = Extern.exists
      ExternalNames = fun () -> Extern.names () :> seq<string> }

let private located (path: string) (lineNo: int) (msg: string) : string =
    let msg =
        if msg.StartsWith "[1:" then
            $"[{lineNo}:" + msg.Substring 3
        else
            msg

    $"{path}:{lineNo}: {msg}"

// Streaming output for command-mode statements — the single exempt form.
// The seq case goes through Eval.writeLines, the same renderer print uses.
let private printResult (v: Eval.Value) =
    match v with
    | Eval.VStr s -> Console.WriteLine s
    | Eval.VSeq items -> Eval.writeLines items
    | other -> Console.WriteLine(Eval.formatValue other)

// The statement rule: a pure expression statement must have type unit.
// Classification is the parser's mode decision alone (SCmd vs SExpr) — no
// name lookup, no type direction; the removed form-2 exemption (bare
// sh/cmd applications) must not creep back in here.
let discardError (ty: Ty) : string option =
    match ty with
    | TUnit -> None
    | TSeq TUnit ->
        Some "this statement computes a seq<unit> and discards it — a lazy effect sequence never runs; use Seq.iter"
    | TSeq(TNamed("FileRow", [])) as ty ->
        Some(
            $"this statement computes a {formatTy ty} and discards it — bind it, or pipe it to print"
            + " (for a plain listing, ^ls runs the real program)"
        )
    | ty -> Some $"this statement computes a {formatTy ty} and discards it — bind it, or pipe it to print"

let run (path: string) (scriptArgs: string list) : int =
    if not (IO.File.Exists path) then
        Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let rawLines = IO.File.ReadAllLines path |> Array.toList

        let afterShebang, shebangOffset =
            match rawLines with
            | first :: rest when first.StartsWith "#!" -> rest, 1
            | _ -> rawLines, 0

        let mode, body, bodyOffset =
            match afterShebang with
            | first :: rest when first.Trim() = "#loose" -> Loose, rest, shebangOffset + 1
            | _ -> Strict, afterShebang, shebangOffset

        let directiveError =
            body
            |> List.mapi (fun i l -> i, l.Trim())
            |> List.tryFind (fun (_, l) -> l.StartsWith "#")

        match directiveError with
        | Some(i, l) ->
            Console.Error.WriteLine(
                located path (bodyOffset + i + 1) $"unknown or misplaced directive: {l} (#loose belongs at file head)"
            )

            1
        | None ->
            let typeEnv0, valueEnv0 = baseEnvs mode scriptArgs
            Extern.refresh ()
            let r = resolver typeEnv0

            let assembled =
                body |> List.mapi (fun i l -> bodyOffset + i + 1, stripComment l) |> assemble

            match assembled with
            | Error msg ->
                Console.Error.WriteLine $"{path}: {msg}"
                1
            | Ok logicalLines ->

                let typedErr (ll: LogicalLine) (terr: Check.TypeError) =
                    let physLine, physCol = translate ll terr.Span.Start.Col
                    $"{path}:{physLine}:{physCol}: type error: {terr.Message}"

                let checkedProgram =
                    logicalLines
                    |> List.fold
                        (fun state ll ->
                            match state with
                            | Error e -> Error e
                            | Ok(tenv, acc) ->
                                match Parser.parseLine r ll.Text with
                                | Error msg -> Error(located path ll.Head msg)
                                | Ok(SType decl) ->
                                    match Check.checkDecl tenv decl with
                                    | Error terr -> Error(typedErr ll terr)
                                    | Ok tenv' -> Ok(tenv', (ll.Head, CType decl) :: acc)
                                | Ok(SLet(name, e)) ->
                                    match Check.typecheck tenv e with
                                    | Error terr -> Error(typedErr ll terr)
                                    | Ok te ->
                                        let tenv' =
                                            { tenv with
                                                Values = Map.add name (generalize te.Ty) tenv.Values }

                                        Ok(tenv', (ll.Head, CLet(name, te)) :: acc)
                                | Ok(SCmd e) ->
                                    match Check.typecheck tenv e with
                                    | Error terr -> Error(typedErr ll terr)
                                    | Ok te -> Ok(tenv, (ll.Head, CCmd te) :: acc)
                                | Ok(SExpr e) ->
                                    match Check.typecheck tenv e with
                                    | Error terr -> Error(typedErr ll terr)
                                    | Ok te ->
                                        match discardError te.Ty with
                                        | Some msg -> Error(typedErr ll { Span = e.Span; Message = msg })
                                        | None -> Ok(tenv, (ll.Head, CExpr te) :: acc))
                        (Ok(typeEnv0, []))

                match checkedProgram with
                | Error msg ->
                    Console.Error.WriteLine msg
                    1
                | Ok(_, revStmts) ->
                    let stmts = List.rev revStmts

                    let rec exec (venv: Eval.Env) (rest: (int * CheckedStmt) list) : int =
                        match rest with
                        | [] -> 0
                        | (lineNo, stmt) :: tail ->
                            match stmt with
                            | CNoop -> exec venv tail
                            | CType decl ->
                                let venv' =
                                    match decl.Body with
                                    | DUnion cases ->
                                        Eval.constructorValues cases |> List.fold (fun m (n, v) -> Map.add n v m) venv
                                    | DRecord _ -> venv

                                exec venv' tail
                            | CLet(name, te) ->
                                try
                                    exec (Map.add name (Eval.eval venv te) venv) tail
                                with ex ->
                                    Console.Error.WriteLine(located path lineNo $"error: {ex.Message}")
                                    1
                            | CCmd te ->
                                try
                                    printResult (Eval.eval venv te)
                                    exec venv tail
                                with ex ->
                                    Console.Error.WriteLine(located path lineNo $"error: {ex.Message}")
                                    1
                            | CExpr te ->
                                try
                                    Eval.eval venv te |> ignore
                                    exec venv tail
                                with ex ->
                                    Console.Error.WriteLine(located path lineNo $"error: {ex.Message}")
                                    1

                    exec valueEnv0 stmts
