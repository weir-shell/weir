module Weir.Main

open System
open Weir.Ast
open Weir.Types

let private evalOnce (input: string) : int =
    // -e agrees with scripts and the REPL [D:trailing-comments]
    let input = Script.stripComment input
    let typeEnv, valueEnv = Prelude.extend Builtins.typeEnv Builtins.valueEnv

    let resolver = Script.resolver typeEnv

    let printHint () =
        Diagnose.hint
            (fun n -> Map.containsKey n typeEnv.Values || Map.containsKey n typeEnv.Modules)
            Builtins.commandCallable.Contains
            Extern.exists
            input
        |> Option.iter (fun h -> Console.Error.WriteLine $"hint: {h}")

    let ll = Script.singleLine input

    // [D:one-pipeline]: -e is a consumer; non-expression kinds are
    // rejected AFTER checking, so an ill-typed let reports its real
    // error rather than the form message
    match Script.checkStatement false (fun _ -> resolver) Script.scriptOnlyImport typeEnv ll with
    | Error d ->
        (if d.Parse then
             Console.Error.WriteLine input

             Console.Error.WriteLine(
                 Types.Color.red Types.Color.onStderr.Value (String(' ', max 0 (d.PhysCol - 1)) + "^")
             )

             Console.Error.WriteLine d.Message
         else
             match d.Span with
             | Some _ ->
                 Console.Error.WriteLine input

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

        printHint ()
        1
    | Ok chk ->
        match chk.Kind with
        | Script.KType _ ->
            Console.Error.WriteLine "-e takes an expression, not a declaration"
            1
        | Script.KLet _ ->
            Console.Error.WriteLine "-e takes an expression, not a let statement"
            1
        | Script.KLetPat _ ->
            Console.Error.WriteLine "-e evaluates one expression; use 'let (x, y) = ... in ...'"
            1
        | Script.KModule _ ->
            Console.Error.WriteLine "-e takes an expression, not a module declaration"
            1
        | Script.KImport _ ->
            // unreachable: scriptOnlyImport rejects the import before this
            Console.Error.WriteLine "import is script-only"
            1
        | Script.KExpr te
        | Script.KCmd te ->
            for w in Check.warnings te do
                Console.Error.WriteLine(Check.formatWarning w)

            try
                let v = Eval.eval valueEnv te

                if v <> Eval.VUnit then
                    let rendered, hint = Eval.echoValue Eval.echoPipedCap v

                    let tail = Eval.echoTail hint

                    Console.WriteLine $"{rendered} : {formatTy te.Ty}{tail}"

                0
            with
            | Eval.ExitRequest code -> code
            | ex ->
                Console.Error.WriteLine(Types.Color.red Types.Color.onStderr.Value "error" + $": {ex.Message}")
                1

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
    | [ "fmt"; "--qualify"; path ] -> Fmt.qualifyFile path
    | [ "fmt"; "--check"; path ] -> Fmt.formatFile true path
    | [ "fmt"; path ] -> Fmt.formatFile false path
    | "fmt" :: _ ->
        Console.Error.WriteLine "usage: weir fmt [--check|--qualify] <script>"
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
        Console.Error.WriteLine "weir -e takes exactly one argument: the expression"
        2
    | "-e" :: rest ->
        Console.Error.WriteLine
            $"weir -e takes ONE expression argument, got {List.length rest} — quote the expression so the shell passes it whole"

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
            + "       weir -e <expression>                    evaluate one expression\n"
            + "       weir check [--json] <script>            diagnostics only (no evaluation)\n"
            + "       weir fmt [--check|--qualify] <script>   canonical formatter\n"
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
