module Weir.Main

open System
open Weir.Ast
open Weir.Types

// -e takes a program: newlines are statement boundaries exactly as in
// a file (assemble handles blocks and comment stripping), and a lone
// declaration is still refused with its kind's teaching — the property
// is "-e evaluates something and shows you the result", a deliberate
// divergence from python -c and friends [D:e-programs]. Strict like
// files: bare module members live in the REPL session only.
let private evalOnce (input: string) : int =
    let typeEnv, valueEnv =
        Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv.Value

    let printHint (line: string) =
        Diagnose.hint
            (fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules)
            Builtins.commandCallable.Contains
            Extern.exists
            line
        |> Option.iter (fun h -> Console.Error.WriteLine $"hint: {h}")

    let normalized = (input: string).Replace("\r\n", "\n")
    let srcLines = normalized.Split('\n') |> Array.toList

    // blank or comment-only input has no statement to point at — say so,
    // never a bare caret under nothing
    if srcLines |> List.forall (fun l -> (Script.stripComment l).Trim() = "") then
        Console.Error.WriteLine "weir -e: nothing to evaluate — give it an expression: weir -e '1 + 2'"
        2
    elif srcLines |> List.exists (fun l -> l.StartsWith "#") then
        // a directive is a prompt (or script-head) thing: named, never an
        // expected-token dump [D:directive-diagnostic]
        let word =
            (srcLines |> List.find (fun l -> l.StartsWith "#"))
                .Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                .[0]

        Console.Error.WriteLine
            $"weir -e: {word} is a directive — -e evaluates weir code and takes none (the REPL's directives work at the weir prompt)"

        1
    else
        let assembled =
            if normalized.Contains '\n' then
                Script.assemble (srcLines |> List.mapi (fun i l -> i + 1, l))
            else
                // the single-line spelling keeps its exact path (stripComment
                // up front [D:trailing-comments]); multi-line strips inside
                // assembly, as files do
                Ok [ Script.singleLine (Script.stripComment input) ]

        let srcLine (n: int) =
            srcLines |> List.tryItem (n - 1) |> Option.defaultValue ""

        let showDiag (d: Script.StmtDiag) =
            let line = srcLine d.PhysLine

            (if d.Parse then
                 Console.Error.WriteLine line

                 Console.Error.WriteLine(
                     Types.Color.red Types.Color.onStderr.Value (String(' ', max 0 (d.PhysCol - 1)) + "^")
                 )

                 Console.Error.WriteLine d.Message
             else
                 match d.Span with
                 | Some _ ->
                     Console.Error.WriteLine line

                     let width =
                         match d.PhysEnd with
                         | Some(el, ec) when el = d.PhysLine -> max 1 (ec - d.PhysCol)
                         | _ -> 1

                     Console.Error.WriteLine(
                         Types.Color.red
                             Types.Color.onStderr.Value
                             (String(' ', max 0 (d.PhysCol - 1)) + String('^', width))
                     )

                     Console.Error.WriteLine $"type error: {d.Message}"
                 | None ->
                     match d.File with
                     // an import's module error, at the module's own site
                     // [D:repl-import]
                     | Some mf -> Console.Error.WriteLine $"{quoteData mf}:{d.PhysLine}:{d.PhysCol}: error: {d.Message}"
                     | None -> Console.Error.WriteLine d.Message)

            printHint line

        match assembled with
        | Error msg ->
            // the named line under a caret at its indentation
            // [D:assembly-col], as a parse error shows its line
            match Script.assemblyErrorAt (srcLines |> List.mapi (fun i l -> i + 1, l)) msg with
            | Some(l, c, _) ->
                Console.Error.WriteLine(srcLine l)
                Console.Error.WriteLine(Types.Color.red Types.Color.onStderr.Value (String(' ', c - 1) + "^"))
            | None -> ()

            Console.Error.WriteLine msg
            1
        | Ok lls ->
            let rec checkAll tenv acc rest =
                match rest with
                | [] -> Ok(List.rev acc)
                | (ll: Script.LogicalLine) :: tail ->
                    match Script.checkStatement false None Script.resolver Script.sessionImportLoader tenv ll with
                    | Error d -> Error d
                    | Ok chk -> checkAll chk.Env ((ll, chk) :: acc) tail

            match checkAll typeEnv [] lls with
            | Error d ->
                showDiag d
                1
            | Ok checked' ->
                // reading (b) [D:e-programs]: every statement may declare,
                // but the program must end in an expression — each kind
                // keeps its own specific refusal message
                let lastKindError =
                    match checked' |> List.tryLast with
                    | Some(_, chk) ->
                        match chk.Kind with
                        | Script.KType _ -> Some "-e takes an expression, not a declaration"
                        | Script.KLet _ -> Some "-e takes an expression, not a let statement"
                        | Script.KLetPat _ -> Some "-e evaluates one expression; use 'let (x, y) = ... in ...'"
                        | Script.KModule _ -> Some "-e takes an expression, not a module declaration"
                        | Script.KImport _ -> Some "-e takes an expression, not an import — follow it with one"
                        | Script.KSig _ ->
                            // unreachable: -e passes no sig context, so the
                            // form already refused [D:module-signatures]
                            Some "signatures belong to module APIs — scripts infer"
                        | Script.KExpr _
                        | Script.KCmd _ -> None
                    | None -> None

                match lastKindError with
                | Some m ->
                    Console.Error.WriteLine m
                    1
                | None ->
                    for _, chk in checked' do
                        for wl, wc, wm in chk.Warnings do
                            if List.length lls > 1 then
                                Console.Error.WriteLine $"{wl}:{wc}: warning: {wm}"
                            else
                                Console.Error.WriteLine $"warning: {wm}"

                    let lastIdx = List.length checked' - 1

                    let rec execAll (venv: Eval.Env) idx rest =
                        match rest with
                        | [] -> 0
                        | (_, (chk: Script.CheckedStatement)) :: tail ->
                            try
                                match chk.Kind with
                                | Script.KType(decl, validator) ->
                                    execAll (Eval.declare venv decl validator) (idx + 1) tail
                                | Script.KLet(name, _, te) ->
                                    execAll (Map.add name (Eval.eval venv te) venv) (idx + 1) tail
                                | Script.KLetPat(pat, _, te) ->
                                    let bindings = Eval.bindPattern pat (Eval.eval venv te)
                                    execAll (bindings |> List.fold (fun m (n, v) -> Map.add n v m) venv) (idx + 1) tail
                                | Script.KImport lm -> execAll (Script.importInto venv lm) (idx + 1) tail
                                | Script.KModule _
                                | Script.KSig _ ->
                                    // unreachable: gated above / by the
                                    // sig-context refusal [D:module-signatures]
                                    execAll venv (idx + 1) tail
                                | Script.KExpr te
                                | Script.KCmd te when idx < lastIdx ->
                                    // mid-program statements behave as in a file:
                                    // commands stream, expression values discard
                                    (match chk.Kind with
                                     | Script.KCmd _ -> Script.printResult (Eval.eval venv te)
                                     | _ -> Eval.eval venv te |> ignore)

                                    execAll venv (idx + 1) tail
                                | Script.KExpr te
                                | Script.KCmd te ->
                                    // the last statement is the result — the -e echo
                                    let v = Eval.eval venv te

                                    if v <> Eval.VUnit then
                                        // the -e echo applies the same
                                        // binary refusal as the REPL's
                                        // [D:binary-echo], so every echo
                                        // path shares it
                                        let v = Eval.echoPrep v

                                        // the -e echo shares the REPL echo's var
                                        // normalization (formatEchoTy) — the one
                                        // consumer beside the REPL meta lines
                                        // [D:repl-fn-echo]
                                        if not Console.IsOutputRedirected && Eval.echoBinary Eval.echoPipedCap v then
                                            Console.WriteLine
                                                $": {formatEchoTy te.Ty} (binary output — the echo refuses a terminal; redirect to a file, or print deliberately)"
                                        else
                                            let rendered, hint = Eval.echoValue Eval.echoPipedCap v
                                            // the REPL echo's renderer [D:echo-one-renderer]
                                            let rendered = Eval.echoRender Console.IsOutputRedirected rendered

                                            let tail' = Eval.echoTail hint

                                            Console.WriteLine $"{rendered} : {formatEchoTy te.Ty}{tail'}"

                                    0
                            with
                            | Eval.ExitRequest code -> code
                            | ex ->
                                // a command death the signal owns ends
                                // silently [D:settle-pending]
                                match Proc.fatalTeardown ex with
                                | Some code -> code
                                | None ->
                                    Console.Error.WriteLine(
                                        Types.Color.red Types.Color.onStderr.Value "error"
                                        + $": {Eval.sanitizeIfTty Console.IsErrorRedirected ex.Message}"
                                    )

                                    1

                    execAll valueEnv 0 checked'

// `weir verify`/`restore`/`add sig` lines carry lock.json fields and
// tool text — messages, so CR and escapes show visibly, piped too
// [D:lock-print-sanitize]
let private lockText (s: string) : string = Eval.sanitizeTtyEcho s

// the lock is committed: an add drops the URL's userinfo from it and
// says so [D:lock-no-userinfo]
let private userinfoNote (url: string) : unit =
    if Http.stripUserinfo url <> url then
        Console.Error.WriteLine
            "weir add: note: the URL's credentials are not written to .weir/lock.json (it is committed) — `weir restore` fetches without them"

// a script (or -e) tears down on its main thread: signals and a closed
// stdout end it with their own code [D:signal-main-unwind] [D:stdout-epipe]
let private runOnMain (run: unit -> int) : int =
    // an inherited SIG_IGN on SIGINT without a tty is reset at start, so a
    // detached poller that never spawns dies by a `kill -INT` too
    // [D:signal-teardown]
    Session.resetDetachedSigint.Force()
    Session.registerMainThread ()
    Session.armStdout ()
    // the mode a hard exit puts back [D:hard-exit]
    Session.saveTerminal ()
    run () |> Session.exitCodeOr

[<EntryPoint>]
let main argv =

    // a closed stdin, stdout or stderr is /dev/null: nothing weir opens
    // lands on fd 0-2 — first, before any console stream exists
    // [D:console-write]
    Session.nullClosedStdio ()

    // captured output is data: LF on every platform [D:lf-output] —
    // the dual of the content-bytes input ruling (a line ending is
    // not data). A tty is display and keeps the platform newline:
    // Windows raw-mode rendering needs CRLF, and bytes only matter
    // where they persist.
    if Console.IsOutputRedirected then
        Console.Out.NewLine <- "\n"

    if Console.IsErrorRedirected then
        Console.Error.NewLine <- "\n"
        // stderr is weir's diagnostic channel: a write it refuses (a full
        // disk, a closed pipe) is dropped, never a raise [D:console-write]
        Console.SetError(new Types.HangupWriter(Console.Error))
    else
        // one choke point for every stderr diagnostic [D:tty-diag-sanitize];
        // a hung-up terminal's EIO never aborts weir [D:console-hangup]
        let err = new Types.HangupWriter(Console.Error)
        Types.Waiting.control <- err
        Console.SetError(new Eval.TtyErrWriter(err))

    if not Console.IsOutputRedirected then
        Console.SetOut(new Types.HangupWriter(Console.Out))

    // WEIR_LOG validates once, before anything runs — an invalid level
    // is a loud startup error, never a silent fallback [D:log-module]
    match Builtins.initLogLevel () with
    | Error msg ->
        Console.Error.WriteLine $"weir: {msg}"
        exit 2
    | Ok() -> ()

    // top-level CLI guard [D:cli-exception-guard]: check/run/fmt/-e
    // run the front end, and a residual exception there (a parser edge
    // the located teachings miss) would otherwise abort with a raw
    // stack trace and exit 134; this guard covers that whole class.
    // Mirrors the LSP's per-document guard: turn it into a located
    // lint diagnostic with a non-zero exit. `exit`/`fail` never reach
    // here (Environment.Exit terminates; ExitRequest is caught inside
    // run/-e), and it is re-raised in case.
    let dispatch () =
        match Array.toList argv with
        | [ "-e"; input ] -> runOnMain (fun () -> evalOnce input)
        // a signal's teardown of an evaluation ends the REPL by the signal
        // [D:die-by-signal]
        | [] -> Weir.Repl.run () |> Session.exitCodeOr
        // the reference dump [D:reference] — machine surface for the site's
        // generated pages; the e2e currency gate diffs it against the
        // committed site/src/data/reference.json
        | [ "docs-json" ] ->
            // Write + "\n", never WriteLine: the trailing newline must be LF
            // on Windows too — the dump is diffed against a committed file
            Console.Out.Write(Weir.Repl.docsJson () + "\n")
            0
        // the headless doc render [D:help-find]: the exact `#help <name>`
        // text for the builtin surface — #find's fzf --preview runs it per
        // highlighted line. Read-only (no user code, no eval); deliberately
        // absent from the usage text (an internal seam, lightly documented
        // in docs/repl.md).
        | [ "--repl-doc"; name ] ->
            Console.WriteLine(Weir.Repl.replDocText name)
            0
        | [ "--version" ] ->
            // the build stamp [D:masking-mechanized] — <tag>+<hash>; the
            // harness gates read the hash (the part after the last '+') and
            // compare it against git HEAD. The same source the LSP
            // serverInfo.version reads, so the two cannot diverge.
            Console.WriteLine Weir.Version.current
            0
        // LSP clients conventionally append transport argv (languageclient
        // v10 adds --stdio/--clientProcessId to Executables) — tolerated
        // and ignored, stdio is the only transport anyway
        | "lsp" :: rest when
            rest
            |> List.forall (fun a -> a = "--stdio" || a = "--debug" || a.StartsWith "--clientProcessId")
            ->
            Lsp.run (List.contains "--debug" rest)
        | "lsp" :: _ ->
            Console.Error.WriteLine
                "usage: weir lsp — the language server, JSON-RPC over stdio (conventional client argv like --stdio is tolerated).\nWire your editor to run this command: see docs/editors.md"

            2
        // check's flags in any order, one script [D:can-report]: --can
        // implies the check; --strict (with --can) exits 2 on any opaque
        // site (CI's choice, not a default); --json for either report
        | "check" :: rest ->
            let flags, paths = rest |> List.partition (fun a -> a.StartsWith "--")
            let known = set [ "--json"; "--can"; "--strict" ]

            let usage (why: string) =
                Console.Error.WriteLine $"weir check: {why}\nusage: weir check [--json] [--can [--strict]] <script>"

                2

            match flags |> List.tryFind (fun f -> not (known.Contains f)), paths with
            | Some f, _ -> usage $"unknown flag {quoteData f}{didYouMean f known}"
            | None, [] -> usage "which script? — give its path"
            | None, _ :: _ :: _ -> usage $"""one script at a time — got {paths |> String.concat " " |> quoteData}"""
            | None, [ path ] ->
                let has f = List.contains f flags

                if has "--strict" && not (has "--can") then
                    usage "--strict applies to the capability report — add --can"
                elif has "--can" then
                    Can.run (has "--json") (has "--strict") path
                else
                    Script.checkOnly (has "--json") path

        // fmt's one flag in either position, one script — a flag is
        // never read as a script path, as check does
        | "fmt" :: rest ->
            let flags, paths = rest |> List.partition (fun a -> a.StartsWith "-" && a <> "-")

            let usage (why: string) =
                Console.Error.WriteLine $"weir fmt: {why}\nusage: weir fmt [--check] <script>"
                2

            match flags |> List.tryFind (fun f -> f <> "--check"), paths with
            | Some f, _ -> usage $"""unknown flag {quoteData f}{didYouMean f [ "--check" ]}"""
            | None, [] -> usage "which script? — give its path"
            | None, _ :: _ :: _ -> usage $"""one script at a time — got {paths |> String.concat " " |> quoteData}"""
            | None, [ path ] -> Fmt.formatFile (List.contains "--check" flags) path
        // external contracts [D:contracts-spine]: `add <kind>` is
        // kind-aware (acquiring differs per kind); `restore`/`verify`
        // operate on the lockfile and are kind-agnostic by construction.
        // All resolve .weir/ from the CWD; check never does any of this.
        | [ "add"; "schema"; url; "--as"; name ] ->
            let weirDir =
                match Contracts.findWeirDir "." with
                | Ok d -> d
                // computed, not created [D:add-validates]: a failed add must
                // leave the tree byte-identical, including no empty .weir/
                | Error _ -> IO.Path.GetFullPath ".weir"

            // the vendor-name guard at the argv crossing [D:lockfile-confinement]
            // — the name becomes the vendored file, so a separator/`..`/absolute
            // `--as` must refuse before the fetch, writing nothing (F14). Hyphens
            // are fine (k8s-configmap); only path-escaping shapes refuse.
            if not (Contracts.vendorNameSafe name) then
                Console.Error.WriteLine
                    $"weir add schema: '--as {quoteData name}' must be a plain file name — no path separators, '..', or leading dot; it becomes the vendored file .weir/schemas/{quoteData name}.json"

                1
            else
                (match Contracts.addFetched weirDir "schema" name url with
                 | Ok line ->
                     Console.WriteLine line
                     userinfoNote url
                     0
                 | Error e ->
                     Console.Error.WriteLine $"weir add: {e}"
                     1)
        | [ "add"; "sig"; tool ]
        | [ "add"; "sig"; "--global"; tool ] when not (tool.StartsWith "-") ->
            let weirDir =
                match Contracts.findWeirDir "." with
                | Ok d -> d
                | Error _ -> IO.Path.GetFullPath ".weir"

            // --global: the user dir, no lock [D:sig-arg-complete]
            let globalDir =
                if Array.contains "--global" argv then
                    Some(Script.SigLookup.userDir ())
                else
                    None

            (match Script.SigGen.generateTo globalDir weirDir tool with
             | Ok line ->
                 // the line quotes the tool's --version [D:sig-text-sanitize]
                 Console.WriteLine(lockText line)
                 0
             | Error e ->
                 Console.Error.WriteLine $"weir add sig: {lockText e}"
                 1)
        | "add" :: "sig" :: _ ->
            Console.Error.WriteLine
                "usage: weir add sig [--global] <tool>   generate a signature from the installed binary (--global: into the user dir, for the REPL's Tab everywhere)"

            2
        | "add" :: "module" :: spec :: flags when
            (match flags with
             // a bare name derives --as [D:lib-short-name]; the long form
             // keeps requiring it (its path is not a name) and falls to usage
             | []
             | [ "--path"; _ ] -> Contracts.libSpec spec <> Ok None
             | [ "--as"; _ ]
             | [ "--as"; _; "--path"; _ ]
             | [ "--path"; _; "--as"; _ ] -> true
             | _ -> false)
            ->
            let asName, entryPath =
                match flags with
                | [ "--as"; n ] -> Some n, None
                | [ "--as"; n; "--path"; p ]
                | [ "--path"; p; "--as"; n ] -> Some n, Some p
                | [ "--path"; p ] -> None, Some p
                | _ -> None, None

            // the library's short spelling [D:lib-short-name] resolves through
            // its own ref pick [D:lib-module-tags]; everything after the
            // resolve sees the shorthand's result, as if typed
            let lib, name, libError =
                match Contracts.libSpec spec with
                | Error e -> None, "", Some e
                | Ok None -> None, (asName |> Option.defaultValue ""), None
                | Ok(Some l) -> Some l, (asName |> Option.defaultValue l.Name), None

            let weirDir =
                match Contracts.findWeirDir "." with
                | Ok d -> d
                | Error _ -> IO.Path.GetFullPath ".weir"

            // R5: the alias namespace — a builtin module's name is reserved,
            // and the name must be able to derive an import alias. The
            // plain-name rule is Contracts.plainName [D:lockfile-confinement]
            // — the one spelling every `--as` kind shares
            let nameOk = Contracts.plainName name

            let derivedAlias =
                if name.Length > 0 then
                    string (System.Char.ToUpper name[0]) + name.Substring 1
                else
                    name

            if libError.IsSome then
                Console.Error.WriteLine $"weir add module: {Option.get libError}"
                1
            elif not nameOk then
                Console.Error.WriteLine
                    $"weir add module: '--as {quoteData name}' must be a plain name (a letter, then letters/digits/_) — it becomes the vendored file and the import alias"

                1
            elif Map.containsKey derivedAlias Builtins.typeEnvStrict.Modules then
                Console.Error.WriteLine
                    $"weir add module: '{name}' derives the alias '{derivedAlias}', a builtin module — pick another --as"

                1
            else
                let shortSha (h: string) = h.Substring(0, 12) + "…"

                // the single-file form, unchanged [D:add-module]
                let addLeaf (url: string) (bytes: byte[]) =
                    // validate before any .weir/ write [D:add-validates]:
                    // a module that does not check must not land
                    let tmp = IO.Path.Combine(IO.Path.GetTempPath(), $"weir-add-{name}.weir")
                    IO.File.WriteAllBytes(tmp, bytes)
                    let checked' = Script.checkVendoredModule tmp

                    (try
                        IO.File.Delete tmp
                     with _ ->
                         ())

                    match checked' with
                    | Error e ->
                        // the error names the source, not the temp
                        // file validation ran against
                        let e = e.Replace(tmp, quoteData (Http.redactUrl url))
                        Console.Error.WriteLine $"weir add module: {e}; nothing was written"
                        1
                    | Ok memberCount ->
                        match Contracts.vendorFile weirDir "module" name ("modules/" + name + ".weir") url bytes with
                        | Error e ->
                            Console.Error.WriteLine $"weir add module: {e}"
                            1
                        | Ok(hash, prior) ->
                            (match prior with
                             | Some old when old <> hash ->
                                 // a re-add is the update path: the sha
                                 // pair signals there is a diff to review
                                 Console.WriteLine
                                     $"updated module {name}: {old.Substring(0, 12)}… → {hash.Substring(0, 12)}…"
                             | Some _ -> Console.WriteLine $"module {name}: unchanged ({hash.Substring(0, 12)}…)"
                             | None ->
                                 Console.WriteLine
                                     $"added module {name} ({hash.Substring(0, 12)}…, {memberCount} member(s)) from {quoteData (Http.redactUrl url)}")

                            Console.WriteLine $"import it:  import \"weir:{name}\" as {derivedAlias}"
                            userinfoNote url
                            0

                // the closure form [D:add-module-closure]: validated whole in
                // a scratch directory, then vendored under modules/<name>/
                let addClosure (src: Contracts.ResolvedModuleSource) (rawBase: string) (files: (string * byte[]) list) =
                    let entryRepo = fst files.Head
                    let ancestor = Script.closureAncestor (files |> List.map fst)
                    let rel (p: string) = p.Substring ancestor.Length
                    let baseUrl = rawBase + ancestor
                    let scratch = IO.Directory.CreateTempSubdirectory($"weir-add-{name}-").FullName

                    let checked' =
                        try
                            for p, b in files do
                                let dest = IO.Path.Combine(scratch, rel p)
                                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName dest) |> ignore
                                IO.File.WriteAllBytes(dest, b)

                            Script.checkVendoredClosure scratch (rel entryRepo)
                        finally
                            try
                                IO.Directory.Delete(scratch, true)
                            with _ ->
                                ()

                    match checked' with
                    | Error e ->
                        // the error names the source files, not the scratch copy
                        let e =
                            e.Replace(scratch + string IO.Path.DirectorySeparatorChar, Http.redactUrl baseUrl)

                        Console.Error.WriteLine $"weir add module: {lockText e}; nothing was written"
                        1
                    | Ok memberCount ->
                        let relFiles = files |> List.map (fun (p, b) -> rel p, b)

                        match Contracts.vendorClosure weirDir name baseUrl src.Sha (rel entryRepo) relFiles with
                        | Error e ->
                            Console.Error.WriteLine $"weir add module: {lockText e}"
                            1
                        | Ok(hash, prior, changes) ->
                            let n = List.length files

                            (match prior with
                             | Some old when old = hash ->
                                 Console.WriteLine $"module {name}: unchanged ({n} files, {shortSha hash})"
                             | Some _ ->
                                 // the R4 update path, per file: the sha pairs
                                 // say where the diff to review is
                                 Console.WriteLine $"updated module {name}: {n} files, {memberCount} member(s)"

                                 for p, o, nw in changes do
                                     match o, nw with
                                     | Some a, Some b when a <> b ->
                                         Console.WriteLine $"  {quoteData p}: {shortSha a} → {shortSha b}"
                                     | None, Some b -> Console.WriteLine $"  {quoteData p}: added ({shortSha b})"
                                     | Some _, None -> Console.WriteLine $"  {quoteData p}: removed"
                                     | _ -> ()
                             | None ->
                                 Console.WriteLine
                                     $"added module {name} ({n} files, {memberCount} member(s)) from {quoteData (Http.redactUrl baseUrl)}")

                            Console.WriteLine $"import it:  import \"weir:{name}\" as {derivedAlias}"
                            userinfoNote src.Url
                            0

                let fetchFail (src: Contracts.ResolvedModuleSource) (e: string) =
                    let e =
                        match src.Host with
                        | Some h -> Contracts.hintPrivate h e
                        | None -> e

                    Console.Error.WriteLine $"weir add module: {e}; nothing was written"
                    1

                let resolved =
                    match lib with
                    | Some l ->
                        Contracts.resolveLibSpec l entryPath
                        |> Result.map (fun (src, picked) ->
                            // a bare add says which tag it picked — the lock
                            // holds the raw URL only [D:lib-module-tags]
                            if l.Tag.IsNone then
                                Console.WriteLine $"{l.Name}: latest is {picked}"

                            src)
                    | None -> Contracts.resolveModuleSpec spec entryPath

                (match resolved with
                 | Error e ->
                     Console.Error.WriteLine $"weir add module: {e}"
                     1
                 | Ok({ Root = None } as src) ->
                     match Contracts.fetchBytesWith src.FetchHeaders src.Url with
                     | Error e -> fetchFail src e
                     | Ok(bytes, _) ->
                         match Script.importsOfSource bytes with
                         | (l, _, p) :: _ ->
                             // one raw URL has no repo root to fetch an import from
                             Console.Error.WriteLine
                                 $"weir add module: {quoteData (Http.redactUrl src.Url)} imports '{quoteData p}' (line {l}), and a single raw URL has no repo root to fetch it from — name the root and the entry: weir add module <root url> --path <file> --as {name}, or vendor it as <host>/<org>/<repo>//<file>@<ref>; nothing was written"

                             1
                         | [] -> addLeaf src.Url bytes
                 | Ok({ Root = Some(rawBase, entry) } as src) ->
                     let fetch (p: string) =
                         Contracts.fetchBytesWith src.FetchHeaders (rawBase + p) |> Result.map fst

                     match Script.walkVendorClosure fetch entry with
                     | Error e -> fetchFail src e
                     | Ok [ (p, bytes) ] -> addLeaf (rawBase + p) bytes
                     | Ok files -> addClosure src rawBase files)
        | "add" :: "module" :: _ ->
            Console.Error.WriteLine
                "usage: weir add module <host>/<org>/<repo>//<file>@<ref> --as <name>   vendor a remote module, lock it\n       weir add module <raw url> --as <name>                           one file, fetched verbatim\n       weir add module <raw root url> --path <file> --as <name>        the entry <file> under the root, with its imports\n       weir add module <name>[@<tag>] [--as <name>]                    an official library module (github.com/weir-shell/lib); no tag = the latest release\n       (an explicit @ref is required on the shorthand)"

            2
        | "add" :: _ ->
            Console.Error.WriteLine
                "usage: weir add schema <url> --as <name>   fetch a JSON schema into .weir/schemas/, lock it\n       weir add sig [--global] <tool>      generate a signature from the installed binary\n       weir add module <src>//<file>@<ref> --as <name>   vendor a remote module, lock it\n       weir add module <raw url> [--path <file>] --as <name>   the raw-URL form (--path: the URL is the root)\n       weir add module <name>[@<tag>] [--as <name>]   an official library module (github.com/weir-shell/lib); no tag = the latest release"

            2
        | [ "restore" ] ->
            (match Contracts.findWeirDir "." with
             | Error e ->
                 Console.Error.WriteLine $"weir restore: {e}"
                 1
             | Ok weirDir ->
                 match Contracts.restore weirDir with
                 | Ok lines ->
                     lines |> List.iter (fun l -> Console.WriteLine(lockText l))

                     0
                 | Error e ->
                     Console.Error.WriteLine $"weir restore: {lockText e}"
                     1)
        | "restore" :: _ ->
            Console.Error.WriteLine "usage: weir restore — re-materialize everything the lock records (hash-verified)"
            2
        | [ "verify" ] ->
            (match Contracts.findWeirDir "." with
             | Error e ->
                 Console.Error.WriteLine $"weir verify: {e}"
                 1
             | Ok weirDir ->
                 match Contracts.verify Proc.resolveProg weirDir with
                 | Error e ->
                     Console.Error.WriteLine $"weir verify: {lockText e}"
                     1
                 | Ok(lines, findings) ->
                     lines |> List.iter (fun l -> Console.WriteLine(lockText l))

                     if List.isEmpty findings then 0 else 1)
        | "verify" :: _ ->
            Console.Error.WriteLine
                "usage: weir verify — vendored contracts against the lock (absent/modified are findings; exit 1)"

            2
        // the schema→types generator [D:schema-types]: locked schema →
        // a decl-only weir module the user owns. `gen`, not `add`: add's
        // invariant is artifact-plus-lock-entry together, and the generated
        // module is deliberately unlocked (user-owned after generation —
        // regeneration is an explicit re-run, as with sigs/restore).
        // Reads only the vendored file: never fetches (the offline rule).
        | "gen" :: "types" :: rest ->
            let usage =
                "usage: weir gen types --schema <name> [--as <TypeName>] [--out <path>|-]\n       generate weir type declarations from a locked schema (.weir/types/<name>.weir by default)"

            let rec walk (m: Map<string, string>) rest =
                match rest with
                | [] -> Ok m
                | f :: v :: tail when (f = "--schema" || f = "--as" || f = "--out") && not (Map.containsKey f m) ->
                    walk (Map.add f v m) tail
                | f :: _ -> Error f

            (match walk Map.empty rest with
             | Error f ->
                 Console.Error.WriteLine $"weir gen types: unexpected '{quoteData f}'\n{usage}"
                 2
             | Ok m when not (Map.containsKey "--schema" m) ->
                 Console.Error.WriteLine usage
                 2
             | Ok m ->
                 let name = m["--schema"]

                 let fail1 (msg: string) =
                     Console.Error.WriteLine $"weir gen types: {msg}"
                     1

                 match Contracts.findWeirDir "." with
                 | Error e -> fail1 $"{e} — vendor the schema first: weir add schema <url> --as {quoteData name}"
                 | Ok weirDir ->
                     match Contracts.readLock weirDir with
                     | Error e -> fail1 e
                     | Ok entries ->
                         match entries |> List.tryFind (fun e -> e.Kind = "schema" && e.Name = name) with
                         | None ->
                             fail1
                                 $"no locked schema '{quoteData name}' — add it: weir add schema <url> --as {quoteData name}"
                         | Some entry ->
                             // gen types writes weir source, so it is the
                             // highest-value consumer of the lock-read
                             // confinement [D:lockfile-confinement]: resolve the
                             // schema file through entryDest, refusing a hostile
                             // path before reading it
                             match Contracts.entryDest weirDir entry with
                             | Error refusal -> fail1 refusal
                             | Ok file ->

                                 if not (IO.File.Exists file) then
                                     fail1
                                         $"schema '{quoteData name}': no {quoteData file} — the lock records it; run `weir restore`"
                                 else
                                     let bytes = IO.File.ReadAllBytes file
                                     let actual = Contracts.sha256Hex bytes

                                     if actual <> entry.Sha256 then
                                         // generation must be reproducible from the lock:
                                         // a drifted file would stamp a hash the bytes
                                         // do not carry
                                         fail1
                                             $"schema '{quoteData name}': the vendored file hashes {actual.Substring(0, 12)}… but the lock records {entry.Sha256.Substring(0, 12)}… — `weir restore` repairs it, or re-add"
                                     else
                                         match Contracts.parseSchema name (Text.Encoding.UTF8.GetString bytes) with
                                         | Error e -> fail1 e
                                         | Ok doc ->
                                             // prelude registration populates the builtin
                                             // nominal set the taken-name guard reads
                                             Prelude.extendTypes Builtins.typeEnvStrict |> ignore
                                             let taken = Infer.takenTypeNames Check.builtinTypeNames.Keys
                                             let asName = Map.tryFind "--as" m

                                             let asNameOk =
                                                 match asName with
                                                 | None -> Ok()
                                                 | Some n when
                                                     n.Length > 0
                                                     && Char.IsUpper n[0]
                                                     && n |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_')
                                                     ->
                                                     if Set.contains n taken then
                                                         Error
                                                             $"'--as {quoteData n}' is a builtin type name — pick another"
                                                     else
                                                         Ok()
                                                 | Some n ->
                                                     Error
                                                         $"'--as {quoteData n}' must be a type name: an uppercase letter first, then letters, digits or _"

                                             match asNameOk with
                                             | Error e -> fail1 e
                                             | Ok() ->
                                                 match
                                                     SchemaTypes.moduleText
                                                         Parser.keywords
                                                         (Set.ofSeq Check.builtinTypeNames.Keys)
                                                         name
                                                         asName
                                                         entry.Sha256
                                                         entry.Url
                                                         doc
                                                 with
                                                 | Error e -> fail1 e
                                                 | Ok gen ->
                                                     // add-validates [D:add-validates]: the emitted
                                                     // module must check before anything lands — a
                                                     // schema producing unrepresentable weir refuses
                                                     // with the located reason
                                                     let tmp =
                                                         IO.Path.Combine(
                                                             IO.Path.GetTempPath(),
                                                             $"weir-gentypes-{Guid.NewGuid():N}.weir"
                                                         )

                                                     IO.File.WriteAllText(tmp, gen.Text)
                                                     let checked' = Script.checkVendoredModule tmp

                                                     (try
                                                         IO.File.Delete tmp
                                                      with _ ->
                                                          ())

                                                     match checked' with
                                                     | Error e ->
                                                         let e = e.Replace(tmp, "<generated>")

                                                         fail1
                                                             $"the generated module does not check: {e} — this is a generator bug; nothing was written"
                                                     | Ok _ ->
                                                         match Map.tryFind "--out" m with
                                                         | Some "-" ->
                                                             Console.Out.Write gen.Text
                                                             0
                                                         | out ->
                                                             let dest, importPath, confineWrite =
                                                                 match out with
                                                                 // --out is a user-chosen path (combined as
                                                                 // given, not confined — the user controls
                                                                 // it); the default .weir/types/ path is
                                                                 // vendor-directory territory and confines
                                                                 // through a symlinked `types` dir
                                                                 // [D:lockfile-symlink-confinement]
                                                                 | Some p -> IO.Path.GetFullPath p, $"\"{p}\"", false
                                                                 | None ->
                                                                     IO.Path.Combine(weirDir, "types", name + ".weir"),
                                                                     $"\"weir:{name}\"",
                                                                     true

                                                             let writeRes =
                                                                 if confineWrite then
                                                                     Contracts.writeConfined
                                                                         weirDir
                                                                         dest
                                                                         (Text.Encoding.UTF8.GetBytes gen.Text)
                                                                 else
                                                                     IO.Directory.CreateDirectory(
                                                                         IO.Path.GetDirectoryName dest
                                                                     )
                                                                     |> ignore

                                                                     IO.File.WriteAllText(dest, gen.Text)
                                                                     Ok()

                                                             match writeRes with
                                                             | Error w -> fail1 w
                                                             | Ok() ->

                                                                 let notes =
                                                                     if gen.NoteCount = 0 then
                                                                         ""
                                                                     else
                                                                         $", {gen.NoteCount} note(s) inside"

                                                                 Console.WriteLine
                                                                     $"generated {quoteData dest} — {gen.TypeCount} type(s){notes}; user-owned (re-run to regenerate)"

                                                                 Console.WriteLine
                                                                     $"import it:  import {quoteData importPath} as {gen.ModuleName}"

                                                                 Console.WriteLine
                                                                     $"read with:  from json {gen.ModuleName}.{gen.TopType} / from yaml {gen.ModuleName}.{gen.TopType}  (or the bare name, while no other import declares it)"

                                                                 0)
        | "gen" :: _ ->
            Console.Error.WriteLine
                "usage: weir gen types --schema <name> [--as <TypeName>] [--out <path>|-]   generate weir types from a locked schema"

            2
        | path :: rest when not (path.StartsWith "-") -> runOnMain (fun () -> Script.run path rest)
        // teaching arms, not dumps [D:windows-v1]: a mistyped option gets a
        // did-you-mean; a mis-quoted -e gets its arity named (on Windows a
        // one-expression intent often arrives shell-split into many argv)
        | [ "-e" ] ->
            Console.Error.WriteLine "weir -e takes exactly one argument: the program"
            2
        | "-e" :: rest ->
            Console.Error.WriteLine
                $"weir -e takes ONE program argument, got {List.length rest} — quote the program so the shell passes it whole"

            2
        | "--version" :: _ ->
            Console.Error.WriteLine "weir --version takes no arguments"
            2
        | opt :: _ when opt.StartsWith "-" && opt <> "--help" && opt <> "-h" ->
            let spellings = [
                "-e"
                "--help"
                "--version"
                "check"
                "fmt"
                "lsp"
                "add"
                "gen"
                "restore"
                "verify"
            ]

            Console.Error.WriteLine
                $"weir: unknown option '{quoteData opt}'{didYouMean opt spellings} (weir --help for usage)"

            2
        | args ->
            let usage =
                "usage: weir                                    the REPL\n"
                + "       weir <script> [args...]                 run a script\n"
                + "       weir -e <program>                       evaluate a program; the result is its last expression\n"
                + "       weir check [--json] <script>            diagnostics only (no evaluation)\n"
                + "       weir check --can [--strict] [--json] <script>  the static capability report\n"
                + "       weir fmt [--check] <script>             canonical formatter\n"
                + "       weir lsp                                language server (stdio)\n"
                + "       weir add sig [--global] <tool>          generate a command signature from the installed binary\n"
                + "       weir add schema <url> --as <name>       fetch an external contract, lock it\n"
                + "       weir add module <src>//<file>@<ref> --as <name>  vendor a remote module, lock it\n"
                + "       weir add module <url> [--path <file>] --as <name>  the raw-URL form (--path: the URL is the root)\n"
                + "       weir add module <name>[@<tag>] [--as <name>]    an official library module (github.com/weir-shell/lib); no tag = latest\n"
                + "       weir gen types --schema <name>          generate weir types from a locked schema\n"
                + "       weir restore                            re-materialize the lock's artifacts\n"
                + "       weir verify                             vendored contracts vs the lock\n"
                + "       weir --version                          the build stamp"

            match args with
            | [ "--help" ] // asked for: stdout, exit 0
            | [ "-h" ] ->
                Console.WriteLine usage
                0
            | _ ->
                Console.Error.WriteLine usage
                2

    let guarded () =
        try
            dispatch ()
        with
        | Eval.ExitRequest code -> code
        | ex ->
            // located like a lint error: file:1:1 with the class, non-zero
            // (2 — the tool's own failure, distinct from a script's exit 1)
            // ex.Message interpolates attacker-influenced data (a filename,
            // URL, or value), so it is sanitized at a tty like every other
            // error-echo site [D:binary-echo]; the class name is weir-internal.
            Console.Error.WriteLine(
                Types.Color.red Types.Color.onStderr.Value "internal error"
                + $": {ex.GetType().Name}: {Eval.sanitizeIfTty Console.IsErrorRedirected ex.Message}"
            )

            Console.Error.WriteLine
                "this is a weir bug — the front end should have produced a located diagnostic; please report it with the script"

            2

    // the front end and the runner get a stack of their own, not the
    // platform's main-thread size (1MB on Windows, `ulimit -s` on POSIX):
    // a block's statements are width, not depth, so a long function body
    // recurses once per statement through parse/check/eval [D:block-width];
    // nesting stays bounded by the counted ceiling and the stack probe
    // [D:depth-guard]. Reserved, not committed — pages are touched on use.
    let code = ref 0

    let worker =
        Threading.Thread((fun () -> code.Value <- guarded ()), 256 * 1024 * 1024)

    worker.Start()
    worker.Join()

    // the coverage counter [D:hand-parser] — its own switch, so oracle
    // runs leave every captured stream byte-identical
    if Environment.GetEnvironmentVariable "WEIR_PARSE_STATS" = "1" then
        let taken, refused = Fast.stats ()
        Console.Error.WriteLine $"parse oracle: fast path took {taken} of {taken + refused} statements"

    code.Value
