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

        match finalTy |> Option.bind (recordFields env) with
        | None -> []
        | Some fields ->
            let stem = word.Substring(0, word.Length - prefix.Length)

            fields
            |> List.map fst
            |> List.filter (fun f -> f.StartsWith prefix)
            |> List.sort
            |> List.map (fun f -> stem + f)
    elif before.EndsWith "from json" then
        env.Types
        |> Map.toList
        |> List.choose (fun (n, def) ->
            match def with
            | Record _ when n.StartsWith word -> Some n
            | _ -> None)
        |> List.sort
    else
        (List.ofSeq (Map.keys env.Values) @ keywords)
        |> List.filter (fun n -> n.StartsWith word && n <> word)
        |> List.distinct
        |> List.sort
