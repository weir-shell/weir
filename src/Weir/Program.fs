module Weir.Main

open System
open Weir.Ast
open Weir.Types

// -e takes a PROGRAM: newlines are statement boundaries exactly as in
// a file (assemble handles blocks and comment stripping), and a LONE
// declaration is still refused with its kind's teaching — the property
// is "-e evaluates something and shows you the result", a deliberate
// divergence from python -c and friends [D:e-programs]. Strict like
// files: bare module members live in the REPL session only.
let private evalOnce (input: string) : int =
    let typeEnv, valueEnv = Prelude.extend Builtins.typeEnvStrict Builtins.valueEnv

    let printHint (line: string) =
        Diagnose.hint
            (fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules)
            Builtins.commandCallable.Contains
            Extern.exists
            line
        |> Option.iter (fun h -> Console.Error.WriteLine $"hint: {h}")

    let normalized = (input: string).Replace("\r\n", "\n")
    let srcLines = normalized.Split('\n') |> Array.toList

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
             | None -> Console.Error.WriteLine d.Message)

        printHint line

    match assembled with
    | Error msg ->
        Console.Error.WriteLine msg
        1
    | Ok lls ->
        let rec checkAll tenv acc rest =
            match rest with
            | [] -> Ok(List.rev acc)
            | (ll: Script.LogicalLine) :: tail ->
                match Script.checkStatement false Script.resolver Script.scriptOnlyImport tenv ll with
                | Error d -> Error d
                | Ok chk -> checkAll chk.Env ((ll, chk) :: acc) tail

        match checkAll typeEnv [] lls with
        | Error d ->
            showDiag d
            1
        | Ok checked' ->
            // reading (b) [D:e-programs]: every statement may declare,
            // but the PROGRAM must end in an expression — the four kind
            // teachings survive, pointed at exactly the case they were
            // written for
            let lastKindError =
                match checked' |> List.tryLast with
                | Some(_, chk) ->
                    match chk.Kind with
                    | Script.KType _ -> Some "-e takes an expression, not a declaration"
                    | Script.KLet _ -> Some "-e takes an expression, not a let statement"
                    | Script.KLetPat _ -> Some "-e evaluates one expression; use 'let (x, y) = ... in ...'"
                    | Script.KModule _ -> Some "-e takes an expression, not a module declaration"
                    | Script.KImport _ ->
                        // unreachable: scriptOnlyImport rejects the import before this
                        Some "import is script-only"
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
                            | Script.KType decl ->
                                let venv' =
                                    match decl.Body with
                                    | DUnion cases ->
                                        Eval.constructorValues cases |> List.fold (fun m (n, v) -> Map.add n v m) venv
                                    | DRecord _ -> venv

                                execAll venv' (idx + 1) tail
                            | Script.KLet(name, _, te) -> execAll (Map.add name (Eval.eval venv te) venv) (idx + 1) tail
                            | Script.KLetPat(pat, _, te) ->
                                let bindings = Eval.bindPattern pat (Eval.eval venv te)
                                execAll (bindings |> List.fold (fun m (n, v) -> Map.add n v m) venv) (idx + 1) tail
                            | Script.KModule _
                            | Script.KImport _ ->
                                // unreachable: gated above / by scriptOnlyImport
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
                                // the LAST statement is the result — the -e echo
                                let v = Eval.eval venv te

                                if v <> Eval.VUnit then
                                    // the -e echo wears the same binary
                                    // refusal as the REPL's [D:binary-echo]
                                    // — it was the one echo path without it
                                    let v = Eval.echoPrep v

                                    if
                                        not Console.IsOutputRedirected
                                        && Eval.echoBinary Eval.echoPipedCap v
                                    then
                                        Console.WriteLine
                                            $": {formatTy te.Ty} (binary output — the echo refuses a terminal; redirect to a file, or print deliberately)"
                                    else
                                        let rendered, hint = Eval.echoValue Eval.echoPipedCap v

                                        let tail' = Eval.echoTail hint

                                        Console.WriteLine $"{rendered} : {formatTy te.Ty}{tail'}"

                                0
                        with
                        | Eval.ExitRequest code -> code
                        | ex ->
                            Console.Error.WriteLine(
                                Types.Color.red Types.Color.onStderr.Value "error" + $": {ex.Message}"
                            )

                            1

                execAll valueEnv 0 checked'

[<EntryPoint>]
let main argv =

    // captured output is DATA: LF on every platform [D:lf-output] — the
    // content-bytes input ruling's dual (a line ending is not data). A
    // tty is DISPLAY and keeps the platform newline: Windows raw-mode
    // rendering needs CRLF, and bytes only matter where they persist.
    if Console.IsOutputRedirected then
        Console.Out.NewLine <- "\n"

    if Console.IsErrorRedirected then
        Console.Error.NewLine <- "\n"

    // WEIR_LOG validates ONCE, before anything runs — an invalid level
    // is a loud startup error, never a silent fallback [D:log-module]
    match Builtins.initLogLevel () with
    | Error msg ->
        Console.Error.WriteLine $"weir: {msg}"
        exit 2
    | Ok() -> ()

    match Array.toList argv with
    | [ "-e"; input ] -> evalOnce input
    | [] -> Weir.Repl.run ()
    | [ "--version" ] ->
        // the build stamp [D:masking-mechanized] — harness gates
        // compare this against git HEAD
        let v =
            match
                System.Reflection.Assembly
                    .GetEntryAssembly()
                    .GetCustomAttributes(typeof<System.Reflection.AssemblyInformationalVersionAttribute>, false)
            with
            | [| :? System.Reflection.AssemblyInformationalVersionAttribute as a |] -> a.InformationalVersion
            | _ -> "dev"

        Console.WriteLine v
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
    | [ "check"; path ] -> Script.checkOnly false path
    | [ "check"; "--json"; path ] -> Script.checkOnly true path
    // the capability report [D:can-report]: --can implies the check;
    // --strict exits 2 on any opaque site (CI's choice, not a default)
    | [ "check"; "--can"; path ] -> Can.run false false path
    | [ "check"; "--can"; "--json"; path ] -> Can.run true false path
    | [ "check"; "--can"; "--strict"; path ] -> Can.run false true path
    | [ "check"; "--can"; "--strict"; "--json"; path ] -> Can.run true true path
    | [ "check"; "--can"; "--json"; "--strict"; path ] -> Can.run true true path

    | [ "fmt"; "--check"; path ] -> Fmt.formatFile true path
    | [ "fmt"; path ] -> Fmt.formatFile false path
    | "fmt" :: _ ->
        Console.Error.WriteLine "usage: weir fmt [--check] <script>"
        2
    // external contracts [D:contracts-spine]: `add <kind>` is
    // KIND-AWARE (acquiring differs per kind); `restore`/`verify`
    // operate on the LOCKFILE and are kind-agnostic by construction.
    // All resolve .weir/ from the CWD; check never does any of this.
    | [ "add"; "schema"; url; "--as"; name ] ->
        let weirDir =
            match Contracts.findWeirDir "." with
            | Ok d -> d
            // computed, NOT created [D:add-validates]: a failed add must
            // leave the tree byte-identical, including no empty .weir/
            | Error _ -> IO.Path.GetFullPath ".weir"

        (match Contracts.addFetched weirDir "schema" name url with
         | Ok line ->
             Console.WriteLine line
             0
         | Error e ->
             Console.Error.WriteLine $"weir add: {e}"
             1)
    | [ "add"; "sig"; tool ] ->
        let weirDir =
            match Contracts.findWeirDir "." with
            | Ok d -> d
            | Error _ -> IO.Path.GetFullPath ".weir"

        (match Script.SigGen.generate weirDir tool with
         | Ok line ->
             Console.WriteLine line
             0
         | Error e ->
             Console.Error.WriteLine $"weir add sig: {e}"
             1)
    | "add" :: "sig" :: _ ->
        Console.Error.WriteLine "usage: weir add sig <tool>   generate a signature from the installed binary"
        2
    | "add" :: "module" :: _ ->
        Console.Error.WriteLine
            "weir add module: remote modules are the spine's third customer — not built yet (DESIGN-external-contracts.md); `add module <repo> --ref <sha>` will clone at a ref"

        2
    | "add" :: _ ->
        Console.Error.WriteLine
            "usage: weir add schema <url> --as <name>   fetch a JSON schema into .weir/schemas/, lock it\n       weir add sig <tool>                (next customer — generates from the installed binary)\n       weir add module <repo> --ref <sha> (third customer — clones at a ref)"

        2
    | [ "restore" ] ->
        (match Contracts.findWeirDir "." with
         | Error e ->
             Console.Error.WriteLine $"weir restore: {e}"
             1
         | Ok weirDir ->
             match Contracts.restore weirDir with
             | Ok lines ->
                 lines |> List.iter Console.WriteLine
                 0
             | Error e ->
                 Console.Error.WriteLine $"weir restore: {e}"
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
                 Console.Error.WriteLine $"weir verify: {e}"
                 1
             | Ok(lines, findings) ->
                 lines |> List.iter Console.WriteLine
                 if List.isEmpty findings then 0 else 1)
    | "verify" :: _ ->
        Console.Error.WriteLine
            "usage: weir verify — vendored contracts against the lock (absent/modified are findings; exit 1)"

        2
    | "run" :: path :: rest -> Script.run path rest
    | path :: rest when not (path.StartsWith "-") -> Script.run path rest
    // teaching arms, not dumps [D:windows-v1]: a mistyped option gets a
    // did-you-mean; a mis-quoted -e gets its arity named (on Windows a
    // ONE-expression intent often arrives shell-split into many argv)
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
        let spellings =
            [ "-e"; "--version"; "check"; "fmt"; "lsp"; "add"; "restore"; "verify"; "run" ]

        Console.Error.WriteLine $"weir: unknown option '{opt}'{didYouMean opt spellings} (weir --help for usage)"
        2
    | args ->
        let usage =
            "usage: weir                                    the REPL\n"
            + "       weir <script> [args...]                 run a script\n"
            + "       weir -e <program>                       evaluate a program; the result is its last expression\n"
            + "       weir check [--json] <script>            diagnostics only (no evaluation)\n"
            + "       weir fmt [--check] <script>   canonical formatter\n"
            + "       weir lsp                                language server (stdio)\n"
            + "       weir add schema <url> --as <name>       fetch an external contract, lock it\n"
            + "       weir restore                            re-materialize the lock's artifacts\n"
            + "       weir verify                             vendored contracts vs the lock"

        match args with
        | [ "--help" ] // asked for: stdout, exit 0
        | [ "-h" ] ->
            Console.WriteLine usage
            0
        | _ ->
            Console.Error.WriteLine usage
            2
