module Weir.Can

// `weir check --can` [D:can-report]: what a script CAN do, derived from
// the typed tree AFTER a clean check — a separate post-check walk, so
// the collector cannot touch the check's error path BY CONSTRUCTION
// (the design argument the order-insensitive Eq arm won on). This is a
// report, NOT an effect system: no annotations, no inference on
// signatures — if it ever wants one, reopen the question.
//
// CAPABILITY, NOT BEHAVIOUR: a command inside a branch that never runs
// still counts — the report is a sound over-approximation of weir-level
// actions, never a prediction. THE MODEL BOUNDARY, stated once: the
// report covers what WEIR spawns and touches; any external can itself
// spawn children, read files, or reach the network — no static report
// closes that, and the interpreter list below only marks the COMMON
// deliberate escapes (their argument is a program this report cannot
// read).

open Weir.Types
open Weir.Ast
open Weir.Check
open Weir.Script

type Site = { File: string; Line: int; Col: int }

type Fact =
    | Runs of prog: string
    | OpaqueArg of interp: string
    | FsRead of member_: string * path: string option
    | FsWrite of member_: string * path: string option
    | TempWrite of what: string
    | CwdChange
    | Network of member_: string * url: string option
    | EnvRead of what: string
    | EnvWrite of via: string * names: string list option
    | SecretLoad of what: string
    | SecretArgv of prog: string
    | ProcScope
    | ProcCtl of member_: string
    | Terminates of via: string

type Cap = { Fact: Fact; Site: Site }

// the common deliberate escapes: heads whose ARGUMENT is a program this
// report cannot read. Everything else external is still listed under
// runs — this set only adds the loud opaque marker.
let private interpreters =
    Set
        [ "sh"
          "bash"
          "zsh"
          "dash"
          "ksh"
          "python"
          "python3"
          "perl"
          "ruby"
          "node"
          "deno"
          "env"
          "xargs"
          "sudo"
          "doas"
          "nohup"
          "timeout"
          "watch"
          "eval"
          // weir itself [D:can-report]: `weir file.weir` takes a program
          // as its argument in exactly sh -c's sense — the report cannot
          // see through it, and 22 unmarked self-invocations in the
          // showcase were the receipt
          "weir" ]

// module-member classification: what a REFERENCE to each member means
// for the report. Pure members (Path.fileName, Http.get the
// constructor…) are deliberately absent — building a request is not
// sending it.
let private fsReads =
    Set [ "File.read"; "File.size"; "Dir.list"; "Dir.exists"; "Path.glob" ]

let private fsWrites =
    Set
        [ "File.write"
          "File.append"
          "File.copy"
          "Dir.create"
          "Dir.delete"
          "Dir.deleteAll"
          "Dir.copy"
          "Dir.move" ]

let private networkMembers = Set [ "Http.send"; "Http.fetch"; "Net.portOpen" ]

let private procMembers = Set [ "Proc.stop"; "Proc.wait" ]

// overlay NAMES, when literal [D:can-report]: Env.pair lists and
// Env.ofPairs tuple lists carry their keys statically; a let-bound
// overlay resolves through the binds map (top-level and expression
// lets both register). Anything else is honestly unknown.
let rec private envNamesOf
    (binds: System.Collections.Generic.Dictionary<string, string list>)
    (e: TypedExpr)
    : string list option =
    let allSome (xs: 'a option list) =
        if xs |> List.forall Option.isSome then
            Some(xs |> List.map Option.get)
        else
            None

    let pairKey (item: TypedExpr) =
        match item.Kind with
        | TEApp({ Kind = TEApp({ Kind = TEVar "Env.pair" }, { Kind = TEStr k }) }, _) -> Some k
        | TETuple [ { Kind = TEStr k }; _ ] -> Some k
        | _ -> None

    match e.Kind with
    | TEVar n ->
        match binds.TryGetValue n with
        | true, ns -> Some ns
        | _ -> None
    | TEApp({ Kind = TEVar "Env.ofPairs" }, inner) -> envNamesOf binds inner
    | TEList items -> items |> List.map pairKey |> allSome
    | _ -> None

/// walk one typed expression, mapping spans through the logical line's
/// segment table (the same translation every diagnostic uses)
let rec private walkExpr
    (binds: System.Collections.Generic.Dictionary<string, string list>)
    (site: Span -> Site)
    (acc: ResizeArray<Cap>)
    (te: TypedExpr)
    : unit =
    let add fact (span: Span) =
        acc.Add { Fact = fact; Site = site span }

    // a literal argument, one level deep — never a guess
    let literalStr (e: TypedExpr) =
        match e.Kind with
        | TEStr s -> Some s
        | _ -> None

    let mutable skipChildren = false

    (match te.Kind with
     | TECmd(prog, args, envO) ->
         add (Runs prog) te.Span

         if interpreters.Contains prog then
             add (OpaqueArg prog) te.Span

         for a in args do
             match a.Ty with
             | TSecret -> add (SecretArgv prog) a.Span
             | _ -> ()

         match envO with
         | Some ov -> add (EnvWrite("env sigil", envNamesOf binds ov)) te.Span
         | None -> ()
     | TELet(n, _, v, _) ->
         (match envNamesOf binds v with
          | Some ns -> binds[n] <- ns
          | None -> ())
     | TEWithin(kind, _, arg, _, _) ->
         (match kind with
          | "tmp" -> add (TempWrite "within tmp (a temporary directory)") te.Span
          | "proc" ->
              add ProcScope te.Span
              add (TempWrite "the proc scope's spill files") te.Span
          | "env" ->
              let names = arg |> Option.bind (envNamesOf binds)
              add (EnvWrite("within env", names)) te.Span
          | "cd" -> add CwdChange te.Span
          | _ -> ())
     | TEEnvLoad(def, _) ->
         for fname, fty in def.Fields do
             add (EnvRead $"{fname} (Env.load {def.Name})") te.Span

             match fty with
             | TSecret
             | TNamed("Option", [ TSecret ]) -> add (SecretLoad $"{fname} (Env.load {def.Name})") te.Span
             | _ -> ()
     | TEArgsLoad target ->
         let defs =
             match target with
             | ArgsRecord d -> [ d ]
             | ArgsUnion(_, payloads) -> payloads |> Map.toList |> List.map snd
             | ArgsShared(outer, _, _, payloads) -> outer :: (payloads |> Map.toList |> List.map snd)

         for d in defs do
             for fname, fty in d.Fields do
                 match fty with
                 | TSecret
                 | TNamed("Option", [ TSecret ]) -> add (SecretLoad $"{fname} (Args.load {d.Name})") te.Span
                 | _ -> ()
     // a module member is a DOTTED TEVar (the checker's resolution) —
     // classify the reference; a following literal application upgrades
     // "some path/url" to the named one
     | TEVar qual when qual.Contains "." ->
         if fsReads.Contains qual then
             add (FsRead(qual, None)) te.Span
         elif fsWrites.Contains qual then
             add (FsWrite(qual, None)) te.Span
         elif networkMembers.Contains qual then
             add (Network(qual, None)) te.Span
         elif procMembers.Contains qual then
             add (ProcCtl qual) te.Span
         elif qual = "File.readSecret" then
             add (FsRead(qual, None)) te.Span
             add (SecretLoad "File.readSecret") te.Span
         elif qual = "Env.get" then
             add (EnvRead "a named variable (Env.get)") te.Span
         elif qual = "Env.vars" then
             add (EnvRead "the entire environment (Env.vars)") te.Span
     | TEApp({ Kind = TEVar qual }, arg) when qual.Contains "." ->
         (match literalStr arg with
          | Some lit when fsReads.Contains qual -> add (FsRead(qual, Some lit)) te.Span
          | Some lit when fsWrites.Contains qual -> add (FsWrite(qual, Some lit)) te.Span
          | Some lit when qual = "Http.fetch" -> add (Network(qual, Some lit)) te.Span
          | Some lit when qual = "Env.get" -> add (EnvRead $"{lit} (Env.get)") te.Span
          | Some lit when qual = "File.readSecret" ->
              add (FsRead(qual, Some lit)) te.Span
              add (SecretLoad $"File.readSecret {lit}") te.Span
          | _ ->
              // non-literal application still counts as the bare fact
              if fsReads.Contains qual then
                  add (FsRead(qual, None)) te.Span
              elif fsWrites.Contains qual then
                  add (FsWrite(qual, None)) te.Span
              elif networkMembers.Contains qual then
                  add (Network(qual, None)) te.Span
              elif qual = "Env.get" then
                  add (EnvRead "a named variable (Env.get)") te.Span)

         // the fn side is fully handled; walk only the ARGUMENT
         walkExpr binds site acc arg
         skipChildren <- true
     | TEApp({ Kind = TEVar "exit" }, _) -> add (Terminates "exit") te.Span
     | TEApp({ Kind = TEVar "fail" }, _) -> add (Terminates "fail") te.Span
     | TEApp(f, args) ->
         // the reifier desugar [D:exit-reifiers]: the program rides as a
         // LITERAL argument to the internal |complete family — the third
         // spawn shape, still statically visible
         let rec headOf (e: TypedExpr) =
             match e.Kind with
             | TEApp(g, _) -> headOf g
             | k -> k

         match headOf te with
         | TEVar h when h.StartsWith "|" ->
             // the program's POSITION in the desugar, then literalness:
             // base twins take (prog, args); orFail's msg rides ahead;
             // the Env twins lead with the overlay; orFailedEnv has both.
             // Filtering to literal strings FIRST mis-slots whenever the
             // msg is interpolated — slot 0 once reported the MESSAGE as
             // a command
             let rec argsOf (e: TypedExpr) =
                 match e.Kind with
                 | TEApp(g, a) -> argsOf g @ [ a ]
                 | _ -> []

             let progSlot =
                 if h.StartsWith "|orFailedEnv" then 2
                 elif h.StartsWith "|orFailed" then 1
                 elif h.EndsWith "Env" then 1
                 else 0

             match argsOf te |> List.tryItem progSlot |> Option.bind literalStr with
             | Some prog ->
                 add (Runs prog) te.Span

                 if interpreters.Contains prog then
                     add (OpaqueArg prog) te.Span
             | None -> ()
         | _ -> ()

         ignore args
     | _ -> ())

    if not skipChildren then
        childExprs te |> List.iter (walkExpr binds site acc)

/// walk a checked file: the script's (line, statement) pairs plus every
/// imported module's, transitively — the import graph IS the tree
let rec private walkStmts (file: string) (acc: ResizeArray<Cap>) (pairs: (LogicalLine * CheckedStmt) list) : unit =
    let binds = System.Collections.Generic.Dictionary<string, string list>()

    for ll, stmt in pairs do
        let site (span: Span) =
            let line, col = translate ll span.Start.Col
            { File = file; Line = line; Col = col }

        match stmt with
        | CLet(n, te) ->
            (match envNamesOf binds te with
             | Some ns -> binds[n] <- ns
             | None -> ())

            walkExpr binds site acc te
        | CLetPat(_, te) -> walkExpr binds site acc te
        | CExpr te -> walkExpr binds site acc te
        | CCmd te -> walkExpr binds site acc te
        | CType _ -> ()
        | CNoop -> ()
        | CImport lm -> walkStmts lm.AbsPath acc lm.Body

let private dedupe (caps: Cap list) : Cap list = caps |> List.distinct

let collect (file: string) (pairs: (LogicalLine * CheckedStmt) list) : Cap list =
    let acc = ResizeArray()
    walkStmts file acc pairs
    dedupe (List.ofSeq acc)

// ---- rendering -------------------------------------------------------------

let private siteStr (s: Site) = $"{s.File}:{s.Line}:{s.Col}"

let private factLine (c: Cap) : string * string =
    // (section, message) — the site renders separately so identical
    // messages GROUP [D:can-report]
    match c.Fact with
    | Runs p -> "runs", p
    | OpaqueArg i -> "opaque", $"{i} takes a program as its argument — not analyzed"
    | FsRead(m, Some p) -> "reads", $"{m} {p}"
    | FsRead(m, None) -> "reads", $"{m} (path not statically known)"
    | FsWrite(m, Some p) -> "writes", $"{m} {p}"
    | FsWrite(m, None) -> "writes", $"{m} (path not statically known)"
    | TempWrite w -> "writes", w
    | CwdChange -> "filesystem", "changes the working directory (within cd)"
    | Network(m, Some u) -> "network", $"{m} {u}"
    | Network(m, None) -> "network", $"{m} (url not statically known)"
    | EnvRead w -> "environment", $"reads {w}"
    | EnvWrite(via, Some ns) -> "environment", $"""sets {ns |> String.concat ", "} for children ({via})"""
    | EnvWrite(via, None) -> "environment", $"sets variables for children ({via}; names not statically known)"
    | SecretLoad w -> "secrets", $"loads {w}"
    | SecretArgv p -> "secrets", $"a Secret reaches the argv of {p} (ps-visible — the stated non-claim)"
    | ProcScope -> "processes", "a scoped background process (within proc)"
    | ProcCtl m -> "processes", m
    | Terminates via -> "terminates", via

let private sectionOrder =
    [ "runs"
      "opaque"
      "reads"
      "writes"
      "filesystem"
      "network"
      "environment"
      "secrets"
      "processes"
      "terminates" ]

let opaqueCount (caps: Cap list) : int =
    caps
    |> List.sumBy (fun c ->
        match c.Fact with
        | OpaqueArg _ -> 1
        | _ -> 0)

let renderHuman (script: string) (caps: Cap list) : string =
    let sb = System.Text.StringBuilder()

    sb.AppendLine $"{script} can (capability, not behaviour — an untaken branch still counts):"
    |> ignore

    let opaque = opaqueCount caps

    if opaque > 0 then
        sb.AppendLine
            $"  ⚠ this report is incomplete: {opaque} opaque site(s) — an interpreter's argument cannot be analyzed"
        |> ignore

    // opacity marks its runs line INLINE (the header carries the count;
    // a separate section said the same thing twice) — the opaque section
    // itself renders only in --json [D:can-report]
    let opaqueSites =
        caps
        |> List.choose (fun c ->
            match c.Fact with
            | OpaqueArg i -> Some(i, c.Site)
            | _ -> None)
        |> Set.ofList

    let entries =
        caps
        |> List.choose (fun c ->
            match c.Fact with
            | OpaqueArg _ -> None
            | Runs p when opaqueSites.Contains(p, c.Site) -> Some(("runs", $"{p} (opaque)"), c.Site)
            | _ -> Some(factLine c, c.Site))

    // identical messages group with a count, sites kept — 22 lines of
    // `weir` carry one line of information [D:can-report]. Distinct
    // sites stay visible, so a genuine same-line pair reads as one.
    let grouped =
        entries
        |> List.groupBy fst
        |> List.map (fun ((section, msg), hits) -> section, msg, hits |> List.map snd)

    for section in sectionOrder do
        let lines = grouped |> List.filter (fun (sec, _, _) -> sec = section)

        if not lines.IsEmpty then
            sb.AppendLine $"  {section}:" |> ignore

            for _, msg, sites in lines do
                let siteList = sites |> List.map siteStr |> String.concat " "

                let line =
                    if List.length sites > 1 then
                        $"{msg} × {List.length sites}  {siteList}"
                    else
                        $"{msg}  {siteList}"

                sb.AppendLine $"    {line}" |> ignore

    if caps.IsEmpty then
        sb.AppendLine "  nothing — no commands, no filesystem, no network, no environment"
        |> ignore

    sb.ToString().TrimEnd()

let renderJson (script: string) (caps: Cap list) : string =
    let opts = System.Text.Json.JsonWriterOptions(Indented = false)
    use ms = new System.IO.MemoryStream()
    use w = new System.Text.Json.Utf8JsonWriter(ms, opts)

    let writeSite (s: Site) =
        w.WriteString("file", s.File)
        w.WriteNumber("line", s.Line)
        w.WriteNumber("col", s.Col)

    w.WriteStartObject()
    w.WriteString("script", script)
    w.WriteString("model", "capability, not behaviour")
    w.WriteNumber("opaqueSites", opaqueCount caps)
    w.WriteStartArray "capabilities"

    for c in caps do
        w.WriteStartObject()

        let kind, detail =
            match c.Fact with
            | Runs p -> "runs", p
            | OpaqueArg i -> "opaque", i
            | FsRead(m, p) -> "read", m + (p |> Option.map (fun x -> " " + x) |> Option.defaultValue "")
            | FsWrite(m, p) -> "write", m + (p |> Option.map (fun x -> " " + x) |> Option.defaultValue "")
            | TempWrite t -> "write", t
            | CwdChange -> "cwd", "within cd"
            | Network(m, u) -> "network", m + (u |> Option.map (fun x -> " " + x) |> Option.defaultValue "")
            | EnvRead x -> "env-read", x
            | EnvWrite(v, ns) ->
                "env-write",
                v
                + (ns
                   |> Option.map (fun xs -> ": " + String.concat ", " xs)
                   |> Option.defaultValue "")
            | SecretLoad x -> "secret-load", x
            | SecretArgv p -> "secret-argv", p
            | ProcScope -> "proc-scope", "within proc"
            | ProcCtl m -> "proc-ctl", m
            | Terminates v -> "terminates", v

        w.WriteString("kind", kind)
        w.WriteString("detail", detail)
        writeSite c.Site
        w.WriteEndObject()

    w.WriteEndArray()
    w.WriteEndObject()
    w.Flush()
    System.Text.Encoding.UTF8.GetString(ms.ToArray())


// ---- the command entry -----------------------------------------------------

/// analyzeLines' product adapted to the walker's pair shape
let private ofChecked (pairs: (LogicalLine * CheckedStatement) list) : (LogicalLine * CheckedStmt) list =
    pairs
    |> List.choose (fun (ll, cs) ->
        match cs.Kind with
        | KLet(n, _, te) -> Some(ll, CLet(n, te))
        | KLetPat(p, _, te) -> Some(ll, CLetPat(p, te))
        | KExpr te -> Some(ll, CExpr te)
        | KCmd te -> Some(ll, CCmd te)
        | KImport lm -> Some(ll, CImport lm)
        | KType d -> Some(ll, CType d)
        | KModule _ -> None)

/// `weir check --can` — implies the check (decision 4): reporting on a
/// script that does not typecheck would report on something that cannot
/// run, so errors print (the same rendering `check` uses) and suppress
/// the report. `--strict` exits 2 on any opaque site — CI's "treat
/// unanalysable as failure" as a flag, not a default.
let run (json: bool) (strict: bool) (path: string) : int =
    if not (System.IO.File.Exists path) then
        eprintfn $"weir: no such script: {path}"
        2
    else
        let rawLines = System.IO.File.ReadAllLines path |> Array.toList
        let diags, pairs, _, _ = analyzeLines path rawLines

        if diags |> List.exists (fun d -> d.Severity = "error") then
            printDiags json diags
            1
        else
            let caps = collect path (ofChecked pairs)

            if json then
                System.Console.WriteLine(renderJson path caps)
            else
                System.Console.WriteLine(renderHuman path caps)

            if strict && opaqueCount caps > 0 then 2 else 0
