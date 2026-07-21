module Weir.Fmt

open Weir.Ast

let private collectBareUses (e: Expr) : (Span * string) list =
    let acc = ResizeArray<Span * string>()

    let rec walk (e: Expr) =
        match e.Kind with
        | EVar name when Map.containsKey name Builtins.bareAliasHomes -> acc.Add(e.Span, name)
        | EVar _
        | EInt _
        | EStr _
        | EBool _
        | EUnit -> ()
        | ELet(_, v, b) ->
            walk v
            walk b
        | ELambda(_, b) -> walk b
        | EApp(f, a)
        | EPipe(f, a) ->
            walk f
            walk a
        | EField(t, _, _) -> walk t
        | EBinOp(_, l, r) ->
            walk l
            walk r
        | ERecord fields -> fields |> List.iter (fun (_, _, v) -> walk v)
        | EMatch(s, arms) ->
            walk s

            arms
            |> List.iter (fun (_, g, b) ->
                g |> Option.iter walk
                walk b)
        | EIf(c, t, e) ->
            walk c
            walk t
            e |> Option.iter walk
        | ESeq(a, b) ->
            walk a
            walk b
        | EList items -> items |> List.iter walk
        | ETuple items -> items |> List.iter walk
        | ELetPat(_, v, b) ->
            walk v
            walk b
        | ELambdaPat(_, b) -> walk b
        | ECmd(_, args, envO) ->
            args |> List.iter walk
            envO |> Option.iter walk
        | EInterp parts ->
            parts
            |> List.iter (function
                | IStr _ -> ()
                | IExpr e -> walk e)
        | EFrom _
        | ETo _ -> ()

    walk e
    List.ofSeq acc

let qualifyLine (r: Parser.Resolver) (line: string) : string * int =
    match Parser.parseLine r line with
    | Error _ -> line, 0
    | Ok stmt ->
        let uses =
            match stmt with
            | SExpr e
            | SCmd e -> collectBareUses e
            | SLet(_, e) -> collectBareUses e
            | SLetPat(_, e) -> collectBareUses e
            | SType _ -> []

        let applicable =
            uses
            |> List.filter (fun (span, _) ->
                let idx = span.Start.Col - 1
                idx >= line.Length || line[idx] <> '$')
            |> List.sortByDescending (fun (span, _) -> span.Start.Col)

        let rewritten =
            applicable
            |> List.fold
                (fun (l: string) (span, name) ->
                    let home = Builtins.bareAliasHomes[name]
                    let before = l.Substring(0, span.Start.Col - 1)
                    let after = l.Substring(span.Start.Col - 1 + name.Length)
                    before + $"{home}.{name}" + after)
                line

        rewritten, List.length applicable

let qualifyFile (path: string) : int =
    if not (System.IO.File.Exists path) then
        System.Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let typeEnv, _ = Prelude.extend Builtins.typeEnv Builtins.valueEnv

        let r: Parser.Resolver =
            { IsKnown = fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules
              IsCommandCallable = fun n -> Builtins.commandCallable.Contains n
              IsExternal = Extern.exists
              ExternalNames = fun () -> Extern.names () :> seq<string> }

        Extern.refresh ()
        let lines = System.IO.File.ReadAllLines path
        let mutable total = 0
        let mutable droppedLoose = false

        let output =
            lines
            |> Array.map (fun line ->
                if line.StartsWith "#!" then
                    line
                elif line.Trim() = "#loose" then
                    droppedLoose <- true
                    null
                else
                    let code = Script.stripComment line

                    if code.Trim() = "" then
                        line
                    else
                        let rewrittenCode, n = qualifyLine r code
                        total <- total + n

                        if n = 0 then
                            line
                        else
                            rewrittenCode + line.Substring(code.Length))
            |> Array.filter (fun l -> not (isNull l))

        System.IO.File.WriteAllLines(path, output)

        let looseNote = if droppedLoose then "; #loose directive removed" else ""
        System.Console.Error.WriteLine $"weir fmt: {total} name(s) qualified{looseNote}"
        0

// ---------------------------------------------------------------------------
// weir fmt <script> — the canonical formatter, v1 (2026-07-18).
// Scope: leading-indent normalization (4 spaces per structural depth, using
// the same pending-let structure the assembler tracks) and trailing-
// whitespace stripping. Comments and token spacing are preserved verbatim —
// respacing/re-flowing needs trivia-preserving parsing (parked). Pipe-headed
// lines keep the column-0 shell style if they use it. Safety: the formatted
// body must re-assemble to IDENTICAL logical lines or fmt refuses to write.

let formatLines (body: string list) : Result<string list, string> =
    // trailing whitespace is never significant (strings are single-line and
    // close with a quote), so both equivalence passes compare TrimEnd'd code
    let commentOnly (raw: string) =
        Script.classifyLine raw = Script.LineKind.CommentOnly

    let numbered =
        body
        |> List.mapi (fun i l -> i + 1, l)
        |> List.filter (fun (_, raw) -> not (commentOnly raw))
        |> List.map (fun (n, raw) -> n, (Script.stripComment raw).TrimEnd())

    match Script.assemble numbered with
    | Error e -> Error $"cannot format: {e} (fix errors first)"
    | Ok originalLogical ->

        // open indent levels, deepest first — the general structural model
        // (2026-07-20): any deeper line opens a level, a line AT a level
        // returns to it, col-0 resets. Depth preserves every relational
        // comparison the assembler makes (=, <, >), so re-assembly is
        // join-for-join identical; the old let-only stack flattened if/match
        // bodies to depth 1 and tripped the safety check on legal input.
        let mutable levels: int list = []
        // open record braces (columns in the FORMATTED text): fields
        // align at top+2 — the house style (2026-07-21; the general
        // depth model had drifted them to depth*4)
        let mutable braces: int list = []
        // district: Some(markerOrigIndent, markerDepth) while inside a ! block
        let mutable district: (int * int) option = None

        let formatted =
            body
            |> List.map (fun raw ->
                let code = Script.stripComment raw
                let content = raw.TrimStart().TrimEnd()

                if raw.Trim() = "" then
                    levels <- []
                    braces <- []
                    ""
                elif code.Trim() = "" then
                    // comment-only: transparent to assembly (2026-07-20);
                    // keep it verbatim and leave formatter state alone
                    raw.TrimEnd()
                else
                    let indent = code |> Seq.takeWhile ((=) ' ') |> Seq.length
                    let piece = code.TrimStart()

                    match district with
                    | Some(m, mDepth) when indent > m ->
                        // district lines: verbatim text at marker+1 depth
                        String.replicate ((mDepth + 1) * 4) " " + content
                    | _ ->

                        district <- None

                        let formatted =
                            match braces with
                            | top :: _ ->
                                // record continuation: align under the open
                                // brace's first field (top + 2)
                                String.replicate (top + 2) " " + content
                            | [] ->
                                let depth =
                                    if indent = 0 then
                                        levels <- []
                                        0
                                    else
                                        let kept = levels |> List.filter (fun k -> k < indent)
                                        levels <- indent :: kept
                                        List.length kept + 1

                                if ((Script.classifyPiece piece).Marker <> Script.MarkerKind.NoMarker) then
                                    district <- Some(indent, depth)

                                String.replicate (depth * 4) " " + content

                        braces <- Script.braceStack braces formatted
                        formatted)

        let renumbered =
            formatted
            |> List.mapi (fun i l -> i + 1, l)
            |> List.filter (fun (_, raw) -> not (commentOnly raw))
            |> List.map (fun (n, raw) -> n, (Script.stripComment raw).TrimEnd())

        match Script.assemble renumbered with
        | Error e -> Error $"fmt safety check failed (file left unchanged): {e}"
        | Ok formattedLogical ->
            let texts (lls: Script.LogicalLine list) = lls |> List.map (fun ll -> ll.Text)

            if texts originalLogical <> texts formattedLogical then
                Error "fmt safety check failed: reformatting would change the parse; file left unchanged"
            else
                Ok formatted

let formatFile (checkOnly: bool) (path: string) : int =
    if not (System.IO.File.Exists path) then
        System.Console.Error.WriteLine $"weir: no such script: {path}"
        2
    else
        let lines = System.IO.File.ReadAllLines path |> Array.toList

        let header, body =
            match lines with
            | first :: rest when first.StartsWith "#!" ->
                match rest with
                | second :: tail when second.Trim() = "#loose" -> [ first; second ], tail
                | _ -> [ first ], rest
            | first :: rest when first.Trim() = "#loose" -> [ first ], rest
            | _ -> [], lines

        match formatLines body with
        | Error msg ->
            System.Console.Error.WriteLine $"weir fmt: {msg}"
            3
        | Ok formattedBody ->
            let result = header @ formattedBody

            if result = lines then
                if not checkOnly then
                    System.Console.Error.WriteLine "weir fmt: already formatted"

                0
            elif checkOnly then
                System.Console.Error.WriteLine $"weir fmt: {path} would be reformatted"
                1
            else
                System.IO.File.WriteAllLines(path, result)
                System.Console.Error.WriteLine $"weir fmt: formatted {path}"
                0
