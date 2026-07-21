module Weir.Complete

open Weir.Types

let private keywords =
    [ "let"
      "fun"
      "match"
      "with"
      "type"
      "of"
      "from"
      "to"
      "true"
      "false"
      "in" ]

let private recordFields (env: TypeEnv) (ty: Ty) : (string * Ty) list option =
    match ty with
    | TNamed(n, _) ->
        match Map.tryFind n env.Types with
        | Some(Record def) -> Some def.Fields
        | _ -> None
    | _ -> None

let private pipelineElemTy (env: TypeEnv) (text: string) : Ty option =
    match text.LastIndexOf '|' with
    | -1 -> None
    | i ->
        let prefix = text.Substring(0, i).Trim()

        match Weir.Parser.parseExpr prefix with
        | Error _ -> None
        | Ok e ->
            match Weir.Check.typecheck env e with
            | Ok te ->
                match te.Ty with
                | TSeq elem -> Some elem
                | _ -> None
            | Error _ -> None

let suggest (env: TypeEnv) (text: string) (wordStart: int) : string list =
    let word =
        if wordStart >= text.Length then
            ""
        else
            text.Substring wordStart

    let before = text.Substring(0, min wordStart text.Length).TrimEnd()

    if word.Contains '.' then
        let segments = word.Split '.'
        let head = segments[0]
        let path = segments[1 .. segments.Length - 2] |> Array.toList
        let prefix = segments[segments.Length - 1]

        let moduleMembers =
            if not (Map.containsKey head env.Values) then
                Map.tryFind head env.Modules
            else
                None

        match moduleMembers with
        | Some members ->
            let prefix = word.Substring(head.Length + 1)

            members
            |> Map.keys
            |> Seq.filter (fun m -> m.StartsWith prefix)
            |> Seq.sort
            |> Seq.map (fun m -> $"{head}.{m}")
            |> List.ofSeq
        | None ->

            let headTy =
                match Map.tryFind head env.Values with
                | Some sch -> Some sch.Ty
                | None -> pipelineElemTy env (text.Substring(0, wordStart))

            let finalTy =
                path
                |> List.fold
                    (fun acc seg ->
                        acc
                        |> Option.bind (recordFields env)
                        |> Option.bind (List.tryFind (fst >> (=) seg))
                        |> Option.map snd)
                    headTy

            let render (fields: string list) =
                let stem = word.Substring(0, word.Length - prefix.Length)

                fields
                |> List.filter (fun f -> f.StartsWith prefix)
                |> List.sort
                |> List.map (fun f -> stem + f)

            match finalTy with
            | Some ty ->
                // resolved head: fields if a record, NOTHING if a known
                // non-record (the nats pin — the fallback must not fire)
                match recordFields env ty with
                | Some fields -> render (fields |> List.map fst)
                | None -> []
            | None ->
                // UNRESOLVABLE head: lambda/function params are never in
                // the env, and a mid-edit statement has no typed tree.
                // Nominal records make the fallback high-signal — offer
                // every declared record's fields (user report,
                // 2026-07-21: `t.` in a function body completed nothing)
                env.Types
                |> Map.toList
                |> List.collect (fun (_, def) ->
                    match def with
                    | Record d -> d.Fields |> List.map fst
                    | Union _ -> [])
                |> List.distinct
                |> render
    elif before.EndsWith "from json" then
        env.Types
        |> Map.toList
        |> List.choose (fun (n, def) ->
            match def with
            | Record _ when n.StartsWith word -> Some n
            | _ -> None)
        |> List.sort
    else
        (List.ofSeq (Map.keys env.Values) @ List.ofSeq (Map.keys env.Modules) @ keywords)
        |> List.filter (fun n -> n.StartsWith word && n <> word)
        |> List.distinct
        |> List.sort
