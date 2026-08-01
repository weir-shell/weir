module Weir.Complete

open Weir.Types

// keyword suggestions DERIVE from the parser's set — one source of
// truth [D:keyword-completion]; the hand-kept copy here predated
// if/then/else/elif/when and silently under-offered them for six
// weeks. The exclusions, each with its reason:
//   rec, mutable — reserved words with NO meaning (offering them
//     suggests a spelling whose only outcome is the reserved-word
//     teaching error)
//   function — reserved for the parked match-lambda sugar; same fate
let unsuggestedKeywords = Set [ "rec"; "mutable"; "function" ]

let private keywords = Weir.Parser.keywords - unsuggestedKeywords |> Set.toList

let private recordFields (env: TypeEnv) (ty: Ty) : (string * Ty) list option =
    match ty with
    | TNamed(n, _) ->
        match Map.tryFind n env.Types with
        | Some(Record def) -> Some def.Fields
        | _ -> None
    | _ -> None

// unbound lowercase names in the prefix become HOLES (fresh type
// vars): the pipe source often mentions enclosing params
// (`targetEnv t |> ...`) whose VALUES are unknown but irrelevant —
// a known function's result type falls out of unification anyway
// [D:hole-completion]
let private holeNames (env: TypeEnv) (e: Weir.Ast.Expr) : string list =
    let acc = System.Collections.Generic.HashSet<string>()

    let rec walk (e: Weir.Ast.Expr) =
        (match e.Kind with
         | Weir.Ast.EVar n when n.Length > 0 && System.Char.IsLower n[0] && not (Map.containsKey n env.Values) ->
             acc.Add n |> ignore
         | _ -> ())

        Weir.Ast.exprChildren e |> List.iter walk

    walk e
    List.ofSeq acc

// bind every hole name as a fresh TVar — the shared setup of the
// pipeline-elem and repaired-statement typing paths
let private withHoles (env: TypeEnv) (e: Weir.Ast.Expr) : TypeEnv =
    holeNames env e
    |> List.mapi (fun i n -> n, mono (TVar $"__hole{i}"))
    |> List.fold
        (fun (te: TypeEnv) (n, sch) ->
            { te with
                Values = Map.add n sch te.Values })
        env

let private pipelineElemTy (env: TypeEnv) (text: string) : Ty option =
    match text.LastIndexOf '|' with
    | -1 -> None
    | i ->
        let prefix = text.Substring(0, i).Trim()

        match Weir.Parser.parseExpr prefix with
        | Error _ -> None
        | Ok e ->
            let envWithHoles = withHoles env e

            match Weir.Check.typecheck envWithHoles e with
            | Ok te ->
                match te.Ty with
                | TSeq elem -> Some elem
                | _ -> None
            | Error _ -> None

/// text ENDS AT THE CURSOR (both callers truncate — the LSP's `upto`,
/// the REPL's Substring): the word runs from wordStart to the end
// filesystem completion [D:repl-quality] for an explicit path word (has a
// `/` or leads with `~`): list the directory, keep prefix matches, expand
// `~`, trailing `/` on directories. NEVER runs anything — a directory read
// only. Callers pass words that already look like paths.
let private filesystemComplete (word: string) : string list =
    let expanded =
        if word.StartsWith "~" then
            System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile
            + word.Substring 1
        else
            word

    let slash = expanded.LastIndexOf '/'

    let dir =
        if slash < 0 then "."
        elif slash = 0 then "/"
        else expanded.Substring(0, slash)

    let prefix = expanded.Substring(slash + 1)

    try
        System.IO.Directory.GetFileSystemEntries dir
        |> Array.filter (fun e -> (System.IO.Path.GetFileName e).StartsWith prefix)
        |> Array.map (fun e -> if System.IO.Directory.Exists e then e + "/" else e)
        |> Array.sort
        |> Array.toList
    with _ ->
        []

let suggest (env: TypeEnv) (text: string) (wordStart: int) : string list =
    let word =
        if wordStart >= text.Length then
            ""
        else
            text.Substring wordStart

    let before = text.Substring(0, min wordStart text.Length).TrimEnd()

    if word.StartsWith "~" || word.Contains '/' then
        // an explicit path word — filesystem entries [D:repl-quality]
        filesystemComplete word
    elif word.Contains '.' then
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

            // bespoke ARMS (`Args.load`/`Env.load`) are not in the member
            // map — offer them too, from the one source the checker uses
            let special =
                Weir.Check.specialModuleMembers |> Map.tryFind head |> Option.defaultValue []

            Seq.append (Map.keys members) special
            |> Seq.filter (fun m -> m.StartsWith prefix)
            |> Seq.distinct
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
                // every declared record's fields
                // [D:declared-fields-fallback]
                env.Types
                |> Map.toList
                |> List.collect (fun (_, def) ->
                    match def with
                    | Record d -> d.Fields |> List.map fst
                    | Union _ -> [])
                |> List.distinct
                |> render
    elif
        before.EndsWith "schema="
        && Weir.Parser.isYamlMarkerPiece (before.Substring(0, before.Length - 7).TrimEnd())
    then
        // the vendored schema NAMES [D:yaml-schemas] — `schema` itself is
        // MARKER-LOCAL, deliberately not a Parser.keywords member (that
        // would reserve the identifier); the district marker context
        // offers it and its completions instead
        match Weir.Contracts.findWeirDir "." with
        | Ok weirDir ->
            let dir = System.IO.Path.Combine(weirDir, "schemas")

            if System.IO.Directory.Exists dir then
                System.IO.Directory.GetFiles(dir, "*.json")
                |> Array.map System.IO.Path.GetFileNameWithoutExtension
                |> Array.filter (fun n -> n.StartsWith word && n <> word)
                |> Array.sort
                |> Array.toList
            else
                []
        | Error _ -> []
    elif word = "" && Weir.Parser.isYamlMarkerPiece (before.TrimEnd()) then
        [ "schema=" ]
    elif before.EndsWith "from json" then
        env.Types
        |> Map.toList
        |> List.choose (fun (n, def) ->
            match def with
            | Record _ when n.StartsWith word -> Some n
            | _ -> None)
        |> List.sort
    else
        // command HEADS at a statement head (before is empty): PATH
        // executables + command-callable builtins join the name pool; in
        // argv position (before non-empty) cwd files join instead — the
        // two interactive contexts completion could not serve before
        // [D:repl-quality]
        let cwdEntries () =
            try
                System.IO.Directory.GetFileSystemEntries "."
                |> Array.map (fun e ->
                    let n = System.IO.Path.GetFileName e
                    if System.IO.Directory.Exists e then n + "/" else n)
                |> Array.toList
            with _ ->
                []

        let extra =
            if before = "" then
                (Extern.names () |> Set.toList) @ (Builtins.commandCallable |> Set.toList)
            elif word <> "" then
                cwdEntries ()
            else
                []

        (List.ofSeq (Map.keys env.Values |> Seq.filter Types.isUserName)
         @ List.ofSeq (Map.keys env.Modules)
         @ keywords
         @ extra)
        |> List.filter (fun n -> n.StartsWith word && n <> word)
        |> List.distinct
        |> List.sort

// Error-recovery completion [D:repair-completion]: the caller
// REPAIRS the broken statement (dangling
// `.prefix` blanked, closers appended) and this types the repaired
// text — holes for stragglers — then reads the head identifier's
// INFERRED type at its column. Row types from the statement's other
// uses of the param surface here.
let fieldsAtRepaired
    (parse: string -> Result<Weir.Ast.Stmt, string>)
    (env: TypeEnv)
    (repaired: string)
    (head: string)
    : string list option =
    match parse repaired with
    | Error _ -> None
    | Ok stmt ->
        let exprOf =
            match stmt with
            | Weir.Ast.SLet(_, e) -> Some e
            | Weir.Ast.SLetPat(_, e) -> Some e
            | Weir.Ast.SExpr e
            | Weir.Ast.SCmd e -> Some e
            | Weir.Ast.SType _
            | Weir.Ast.SModule _
            | Weir.Ast.SImport _ -> None

        exprOf
        |> Option.bind (fun e ->
            let envH = withHoles env e

            match Weir.Check.typecheck envH e with
            | Error _ -> None
            | Ok te ->
                // ANY occurrence serves: a param's uses share one type,
                // and the cursor's own occurrence was blanked away
                let rec find (best: Ty option) (node: Weir.Check.TypedExpr) =
                    let best =
                        match node.Kind with
                        | Weir.Check.TEVar n when n = head -> Some node.Ty
                        | _ -> best

                    Weir.Check.childExprs node |> List.fold find best

                find None te)
        |> Option.bind (fun ty ->
            match ty with
            | TRowVar(_, fields) ->
                // an OPEN row (the `..` tail) is compatible with any
                // declared record it fits inside — offer those records'
                // FULL field sets too, so editing the one line that
                // demanded a field does not hide it [D:open-row-compat]
                let known = fields |> List.map fst

                let compatible =
                    env.Types
                    |> Map.toList
                    |> List.collect (fun (_, def) ->
                        match def with
                        | Record d ->
                            let fits =
                                fields
                                |> List.forall (fun (f, ft) ->
                                    match d.Fields |> List.tryFind (fst >> (=) f) with
                                    | None -> false
                                    | Some(_, rt) ->
                                        (match ft with
                                         | TVar _ -> true
                                         | _ -> formatTy ft = formatTy rt))

                            if fits then d.Fields |> List.map fst else []
                        | Union _ -> [])

                Some(known @ compatible |> List.distinct)
            | _ -> recordFields env ty |> Option.map (List.map fst))
