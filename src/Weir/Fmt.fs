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
            arms |> List.iter (snd >> walk)
        | EList items -> items |> List.iter walk
        | ECmd(_, args) -> args |> List.iter walk
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
