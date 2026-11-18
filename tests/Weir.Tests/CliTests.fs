module CliTests

// The cli suites. A suite sits in the file of the area it mostly
// exercises; Tests.fs lists every suite for the runner.

open System.Diagnostics
open System.IO
open Expecto
open Weir.Ast
open Weir.Types
open Weir.Argv
open Weir.Check
open Weir.Eval
open TestPrelude
open ParserTests
open CheckerTests
open EvalTests
open BuiltinsTests
open AdaptersTests

let diagnoseTests =
    testList "Cliff diagnostic" [
        test "ls -la gets the hint" {
            match
                Weir.Diagnose.hint (fun n -> Map.containsKey n env.Values) (fun _ -> false) (fun _ -> true) "ls -la"
            with
            | Some h ->
                Expect.stringContains h "'ls' is a weir binding" ""
                Expect.stringContains h "^ls -la" ""
            | None -> failtest "expected a hint"
        }
        test "binding shadowing a PATH name gets the hint on bareword tail" {
            let isKnown n = n = "git"
            let isExternal n = n = "git"

            match Weir.Diagnose.hint isKnown (fun _ -> false) isExternal "git status" with
            | Some h -> Expect.stringContains h "'git' is a weir binding" ""
            | None -> failtest "expected a hint"
        }
        test "plain unbound tail without PATH presence stays quiet" {
            Expect.isNone (Weir.Diagnose.hint (fun n -> n = "where") (fun _ -> false) (fun _ -> false) "where p") ""
        }
        test "operator tails stay quiet" {
            let isKnown n = Map.containsKey n env.Values
            Expect.isNone (Weir.Diagnose.hint isKnown (fun _ -> false) (fun _ -> true) "ls |> take 5") ""
            Expect.isNone (Weir.Diagnose.hint isKnown (fun _ -> false) (fun _ -> true) "x + 1") ""
        }
        test "path tails hint even without PATH presence" {
            match Weir.Diagnose.hint (fun n -> n = "mybinding") (fun _ -> false) (fun _ -> false) "mybinding ../x" with
            | Some _ -> ()
            | None -> failtest "expected a hint for path-like tail"
        }
    ]

let lockfileConfinementTests =
    // Bundle A+B [D:lockfile-confinement]: a path from outside (a lock
    // entry, an --as name) must not reach a write/read outside .weir/.
    // The confining join and the plain-name validator are pure — probe
    // them directly; the import-unreadable path (F2) goes through
    // analyzeLines.
    let mkEntry path : Weir.Contracts.LockEntry = {
        Kind = "schema"
        Name = "n"
        Url = "http://x"
        Sha256 = "0"
        Path = path
        Version = None
        Closure = None
    }

    testList "lockfile path confinement [D:lockfile-confinement]" [
        test "confineUnder admits an inside path, refuses absolute and traversal" {
            let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weir-conf-root")

            match Weir.Contracts.confineUnder root "schemas/x.json" with
            | Ok dest -> Expect.stringContains dest "schemas" "an inside path resolves under the base"
            | Error e -> failtest $"an inside path must confine: {e}"

            // a legitimately-inside `a/b/../c` stays under
            match Weir.Contracts.confineUnder root "a/b/../c.json" with
            | Ok _ -> ()
            | Error e -> failtest $"a normalising-but-inside path must confine: {e}"

            // absolute, traversal-past-base, and (the classic bug) a
            // sibling that shares the base as a string prefix all refuse
            for bad in [ "/etc/passwd"; "../escape.json"; "../root-evil/x" ] do
                match Weir.Contracts.confineUnder root bad with
                | Ok d -> failtest $"'{bad}' must escape, got {d}"
                | Error _ -> ()
        }
        test "vendorNameSafe: a file-name segment (hyphens ok), separators/traversal/absolute refuse" {
            // the schema-name floor — k8s-configmap is legitimate
            for ok in [ "benign"; "k8s-configmap"; "a_b-2"; "v1.2"; "X" ] do
                Expect.isTrue (Weir.Contracts.vendorNameSafe ok) $"'{ok}' is a safe file name"

            // only path-escaping shapes refuse
            for bad in [ ""; "a/b"; "a\\b"; "../x"; "/abs"; ".hidden"; "a b"; "a..b" ] do
                Expect.isFalse (Weir.Contracts.vendorNameSafe bad) $"'{bad}' escapes or is unsafe"
        }
        test "plainName: the stricter ALIAS rule — a letter then letters/digits/_ ; hyphen and dot refuse" {
            for ok in [ "benign"; "myMod"; "a_b_2"; "X" ] do
                Expect.isTrue (Weir.Contracts.plainName ok) $"'{ok}' is a plain identifier"

            // an import alias admits no hyphen/dot/separator
            for bad in [ ""; "1x"; "a/b"; "../x"; "/abs"; "a.b"; "a-b"; "a b" ] do
                Expect.isFalse (Weir.Contracts.plainName bad) $"'{bad}' is not an alias-shaped name"
        }
        test "entryDest confines PER ENTRY — a hostile entry is a located refusal naming it" {
            let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weir-conf-root")

            match Weir.Contracts.entryDest root (mkEntry "schemas/ok.json") with
            | Ok _ -> ()
            | Error e -> failtest $"a benign entry resolves: {e}"

            match Weir.Contracts.entryDest root (mkEntry "../ESCAPE.json") with
            | Ok d -> failtest $"a hostile entry must refuse, got {d}"
            | Error msg ->
                Expect.stringContains msg "escapes .weir/" "the refusal names the escape"
                Expect.stringContains msg "../ESCAPE.json" "and the offending path"
        }
        test "entryDest refuses the lockfile and .weir/ itself — restore never overwrites lock.json (H5)" {
            let root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "weir-conf-root")

            for bad in
                [
                    "lock.json"
                    "./lock.json"
                    "schemas/../lock.json"
                    "LOCK.JSON"
                    "."
                    "schemas/.."
                ] do
                match Weir.Contracts.entryDest root (mkEntry bad) with
                | Ok d -> failtest $"'{bad}' must refuse, got {d}"
                | Error msg -> Expect.stringContains msg "lockfile" $"'{bad}': the refusal names the lockfile"
        }
        test "a malformed lock entry is a weir-worded refusal naming the entry and field (H12)" {
            let dir = tempDir "weir-h12"

            try
                let sha = System.String('a', 64)

                let entry (fields: string) =
                    "{ \"artifacts\": [ { " + fields + " } ] }"

                let full =
                    $"\"kind\": \"schema\", \"name\": \"n\", \"url\": \"http://x\", \"sha256\": \"{sha}\", \"path\": \"schemas/n.json\""

                let cases = [
                    entry (full.Replace(sha, "abc")), "\"sha256\" must be 64 hex digits"
                    entry (full.Replace("\"path\": \"schemas/n.json\"", "\"path\": null")), "\"path\" must be a string"
                    entry (full.Replace("\"url\": \"http://x\", ", "")), "missing \"url\""
                    entry (full.Replace("schemas/n.json", "")), "\"path\" is empty"
                    "{ \"artifacts\": [ 3 ] }", "artifacts[0] must be an object"
                    // [D:lock-print-sanitize]: weir writes schema, module and sig only
                    entry (full.Replace("\"kind\": \"schema\"", "\"kind\": \"evil\"")), "unknown \"kind\" 'evil'"
                    "[]", "must be a JSON object"
                ]

                for text, want in cases do
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "lock.json"), text)

                    match Weir.Contracts.readLock dir with
                    | Ok _ -> failtest $"{text} must refuse"
                    | Error msg ->
                        Expect.stringContains msg "malformed lockfile" "weir-worded"
                        Expect.stringContains msg want $"names the field: {text}"

                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "lock.json"), entry full)

                match Weir.Contracts.readLock dir with
                | Ok [ e ] -> Expect.equal e.Path "schemas/n.json" "a well-formed entry reads"
                | other -> failtest $"a well-formed lock reads: {other}"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "a module re-fetch carries the WEIR_TOKEN_<host> credential add sent (H13)" {
            let var = Weir.Contracts.hostTokenVar "modules.example.test"
            let gl = Weir.Contracts.hostTokenVar "gitlab.com"
            let gh = Weir.Contracts.hostTokenVar "github.com"

            let saved =
                [ var; gl; gh ]
                |> List.map (fun v -> v, System.Environment.GetEnvironmentVariable v)

            try
                System.Environment.SetEnvironmentVariable(var, "t1")
                System.Environment.SetEnvironmentVariable(gl, "t2")
                System.Environment.SetEnvironmentVariable(gh, "t3")

                let auth url =
                    fst (Weir.Contracts.moduleFetchHeaders url)
                    |> List.filter (fun (k, _) -> k <> "User-Agent")

                Expect.equal (auth "https://modules.example.test/m.weir") [ "Authorization", "token t1" ] "raw URL host"
                Expect.equal (auth "https://gitlab.com/g/r/-/raw/abc/m.weir") [ "PRIVATE-TOKEN", "t2" ] "gitlab"

                Expect.equal
                    (auth "https://raw.githubusercontent.com/o/r/abc/m.weir")
                    [ "Authorization", "token t3" ]
                    "github raw reads the github.com token"

                Expect.equal (auth "https://other.example.test/m.weir") [] "no token, no header"
            finally
                for v, old in saved do
                    System.Environment.SetEnvironmentVariable(v, old)
        }
        test "restore reports a locally modified generated sig as MODIFIED, never present" {
            let dir = tempDir "weir-genmod"

            try
                let sigs = System.IO.Path.Combine(dir, "sigs")
                System.IO.Directory.CreateDirectory sigs |> ignore
                let original = "module\n"B
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(sigs, "ls.weir"), original)

                let sha = Weir.Contracts.sha256Hex original

                Weir.Contracts.writeLock dir [
                    {
                        Kind = "sig"
                        Name = "ls"
                        Url = "generated:help"
                        Sha256 = sha
                        Path = "sigs/ls.weir"
                        Version = None
                        Closure = None
                    }
                ]

                match Weir.Contracts.restore dir with
                | Ok lines -> Expect.equal lines [ "sig ls: present" ] "an untouched sig is present"
                | Error e -> failtest e

                System.IO.File.WriteAllText(System.IO.Path.Combine(sigs, "ls.weir"), "module\n// drift\n")

                let actual =
                    Weir.Contracts.sha256Hex (System.IO.File.ReadAllBytes(System.IO.Path.Combine(sigs, "ls.weir")))

                match Weir.Contracts.restore dir with
                | Ok lines -> failtest $"a modified generated sig must not be present: {lines}"
                | Error e ->
                    Expect.equal
                        e
                        $"sig ls: MODIFIED — sha256 {actual.Substring(0, 12)}…, lock records {sha.Substring(0, 12)}… — a generated file is not refetched; restore the checked-in copy, or regenerate deliberately with `weir add sig ls`"
                        "reported as verify reports it"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "a lockfile that is not JSON is refused in weir's words, with its position" {
            let dir = tempDir "weir-lockjson"

            try
                let p = System.IO.Path.Combine(dir, "lock.json")
                System.IO.File.WriteAllText(p, "{ bad")

                match Weir.Contracts.readLock dir with
                | Ok _ -> failtest "invalid JSON must refuse"
                | Error msg ->
                    Expect.equal
                        msg
                        $"{p}: malformed lockfile — not valid JSON (line 1, column 3); fix the file or restore it from version control"
                        "no System.Text.Json text"
            finally
                System.IO.Directory.Delete(dir, true)
        }
        test "a module's restore hash mismatch names `weir add module`, not schema (H56)" {
            let dir = tempDir "weir-h56c"
            let l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)
            l.Start()
            let port = (l.LocalEndpoint :?> System.Net.IPEndPoint).Port

            let t =
                System.Threading.Thread(fun () ->
                    try
                        use c = l.AcceptTcpClient()
                        use st = c.GetStream()
                        let buf = Array.zeroCreate<byte> 8192
                        st.Read(buf, 0, buf.Length) |> ignore
                        let r = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi"B
                        st.Write(r, 0, r.Length)
                    with _ ->
                        ())

            t.IsBackground <- true
            t.Start()

            try
                let url = $"http://127.0.0.1:{port}/m.weir"

                Weir.Contracts.writeLock dir [
                    {
                        Kind = "module"
                        Name = "m"
                        Url = url
                        Sha256 = System.String('a', 64)
                        Path = "modules/m.weir"
                        Version = None
                        Closure = None
                    }
                ]

                match Weir.Contracts.restore dir with
                | Ok lines -> failtest $"a hash mismatch must refuse: {lines}"
                | Error e -> Expect.stringContains e $"weir add module {url} --as m" e
            finally
                l.Stop()
                System.IO.Directory.Delete(dir, true)
        }
        test "entryDest refuses a leaf symlinked to the lockfile (H5)" {
            if not (System.OperatingSystem.IsWindows()) then
                let dir = tempDir "weir-h5"

                try
                    let weirDir = System.IO.Path.Combine(dir, ".weir")

                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(weirDir, "schemas"))
                    |> ignore

                    System.IO.File.WriteAllText(System.IO.Path.Combine(weirDir, "lock.json"), "{}")

                    System.IO.File.CreateSymbolicLink(
                        System.IO.Path.Combine(weirDir, "schemas", "x.json"),
                        System.IO.Path.Combine(weirDir, "lock.json")
                    )
                    |> ignore

                    match Weir.Contracts.entryDest weirDir (mkEntry "schemas/x.json") with
                    | Ok d -> failtest $"a leaf linked to lock.json must refuse, got {d}"
                    | Error msg -> Expect.stringContains msg "lockfile" "the refusal names the lockfile"
                finally
                    System.IO.Directory.Delete(dir, true)
        }
        test "an unreadable import is a LOCATED diagnostic, never a crash (F2)" {
            // mode-000 on the imported file: check must give a located
            // 'cannot read import' error, not throw (the old crash)
            if System.OperatingSystem.IsWindows() then
                ()
            else
                let dir = tempDir "weir-f2"

                try
                    let modP = System.IO.Path.Combine(dir, "mod.weir")
                    System.IO.File.WriteAllLines(modP, [ "module M"; "let f : int -> int"; "let f x = x + 1" ])
                    let mainP = System.IO.Path.Combine(dir, "main.weir")
                    System.IO.File.WriteAllLines(mainP, [ "import \"./mod.weir\" as M"; "print (show (M.f 1))" ])
                    System.IO.File.SetUnixFileMode(modP, System.IO.UnixFileMode.None)

                    let ds = diagsIn mainP (List.ofArray (System.IO.File.ReadAllLines mainP))

                    let errs = ds |> List.filter (fun d -> d.Severity = "error")

                    Expect.isNonEmpty errs "an unreadable import produces a diagnostic"

                    Expect.exists
                        errs
                        (fun d -> d.Message.Contains "cannot read import")
                        "the located 'cannot read import' diagnostic, not a crash"
                finally
                    try
                        System.IO.File.SetUnixFileMode(
                            System.IO.Path.Combine(dir, "mod.weir"),
                            System.IO.UnixFileMode.UserRead ||| System.IO.UnixFileMode.UserWrite
                        )

                        System.IO.Directory.Delete(dir, true)
                    with _ ->
                        ()
        }
    ]

let libSpecTests =
    // [D:lib-short-name]: the bare-name expansion is pure — every arm
    // pinned here; the host chain behind it is the live e2e probe's.
    // The tag is optional and per module [D:lib-module-tags]
    let expand spec =
        match Weir.Contracts.libSpec spec with
        | Ok(Some l) -> l
        | Ok None -> failtest $"'{spec}' must expand, not pass through"
        | Error e -> failtest $"'{spec}' must expand: {e}"

    let refuse spec =
        match Weir.Contracts.libSpec spec with
        | Error e -> e
        | Ok _ -> failtest $"'{spec}' must refuse"

    let passes spec =
        match Weir.Contracts.libSpec spec with
        | Ok None -> ()
        | Ok(Some l) -> failtest $"'{spec}' must pass through, not expand to {l.Name}"
        | Error e -> failtest $"'{spec}' must pass through, not refuse: {e}"

    let sha = String.replicate 40 "a"

    testList "lib short name [D:lib-short-name] [D:lib-module-tags]" [
        test "the three spellings: a bare name, a version, a full sha" {
            Expect.equal (expand "semver") { Name = "semver"; Tag = None } "bare: the resolver picks the latest"
            Expect.equal (expand "semver@v0.1.0") { Name = "semver"; Tag = Some "v0.1.0" } "the version as typed"
            Expect.equal (expand $"semver@{sha}") { Name = "semver"; Tag = Some sha } "a sha passes through"
            Expect.equal (expand "a_b2@v1.0.0").Name "a_b2" "underscores and digits are name characters"
        }
        test "the source the resolver builds names the per-module tag" {
            Expect.equal
                (Weir.Contracts.libSource "semver" "semver/v0.1.0")
                "github.com/weir-shell/lib//modules/semver.weir@semver/v0.1.0"
                "the shorthand at the tag"
        }
        test "a trailing empty @ refuses naming both spellings" {
            let e = refuse "semver@"
            Expect.stringContains e "has nothing after the @" "the refusal"
            Expect.stringContains e "weir add module semver)" "the bare spelling"
            Expect.stringContains e "weir add module semver@v0.1.0" "the pinned spelling"
        }
        test "a version without its v refuses naming the tag" {
            let e = refuse "semver@0.1.0"
            Expect.stringContains e "without its v" "the refusal"
            Expect.stringContains e "the tag is semver/v0.1.0" "the tag"
            Expect.stringContains e "weir add module semver@v0.1.0" "the spelling"
            Expect.stringContains (refuse "semver@1.0.0-rc.1+b") "semver@v1.0.0-rc.1+b" "a prerelease too"
        }
        test "a name that cannot be a file and an alias refuses" {
            for spec in [ "semver.weir@v1"; "my-mod@v1"; "@v1"; "1x@v1"; "my-mod" ] do
                Expect.stringContains (refuse spec) "is not a library module name" $"{spec}: the refusal"
        }
        test "every spec with a / or : passes through untouched — the long grammar's teaches stand" {
            for spec in
                [
                    "github.com/acme/lib//x.weir@v1"
                    "https://h/x.weir"
                    "weir:semver"
                    "./x.weir"
                    "sr.ht/a/b//x@v1"
                ] do
                passes spec
        }
        test "an expansion never re-expands" { passes (Weir.Contracts.libSource "semver" "semver/v0.1.0") }
    ]

let semverTagTests =
    // [D:lib-module-tags]: semver precedence in weir's own code — pinned to
    // the spec's ordered example sequence (semver.org §11) — and the pick
    // over a ref listing
    let key v =
        match Weir.Contracts.semverKey v with
        | Some k -> k
        | None -> failtest $"'{v}' must parse"

    let shaA = String.replicate 40 "a"
    let shaB = String.replicate 40 "b"
    let shaC = String.replicate 40 "c"
    let shaD = String.replicate 40 "d"

    testList "library module tags [D:lib-module-tags]" [
        test "semverKey: core, prerelease, build dropped" {
            Expect.equal (key "1.2.3") (1, 2, 3, None) "core"
            Expect.equal (key "1.0.0-alpha.1") (1, 0, 0, Some "alpha.1") "prerelease"
            Expect.equal (key "1.0.0+20130313144700") (1, 0, 0, None) "build dropped"
            Expect.equal (key "1.0.0-beta+exp.sha.5114f85") (1, 0, 0, Some "beta") "both"
            Expect.equal (key "0.0.0") (0, 0, 0, None) "zeros"
        }
        test "semverKey: not a version" {
            for v in
                [
                    "v1.0.0"
                    "1.0"
                    "01.0.0"
                    "1.0.0-"
                    "1.0.0-01"
                    "1.0.0-a..b"
                    "1.0.0 "
                    ""
                    "main"
                    "99999999999.0.0"
                ] do
                Expect.equal (Weir.Contracts.semverKey v) None $"'{v}' is not a version"
        }
        test "semverCompare: the spec's example sequences, in order" {
            let seq1 = [
                "1.0.0-alpha"
                "1.0.0-alpha.1"
                "1.0.0-alpha.beta"
                "1.0.0-beta"
                "1.0.0-beta.2"
                "1.0.0-beta.11"
                "1.0.0-rc.1"
                "1.0.0"
            ]

            let seq2 = [ "1.0.0"; "2.0.0"; "2.1.0"; "2.1.1" ]

            for s in [ seq1; seq2 ] do
                for a, b in List.pairwise s do
                    Expect.isLessThan (Weir.Contracts.semverCompare (key a) (key b)) 0 $"{a} < {b}"
                    Expect.isGreaterThan (Weir.Contracts.semverCompare (key b) (key a)) 0 $"{b} > {a}"

                let shuffled =
                    s
                    |> List.rev
                    |> List.sortWith (fun a b -> Weir.Contracts.semverCompare (key a) (key b))

                Expect.equal shuffled s "sorts into the spec's order"

            Expect.equal (Weir.Contracts.semverCompare (key "1.0.0+a") (key "1.0.0+b")) 0 "build metadata is ignored"
            Expect.equal (Weir.Contracts.semverCompare (key "1.0.0-rc.1") (key "1.0.0-rc.1")) 0 "equal"
        }
        test "latestModuleTag: the highest stable tag of the named module" {
            let refs = [
                shaA, "HEAD"
                shaA, "refs/heads/main"
                shaB, "refs/tags/semver/v0.1.0"
                shaC, "refs/tags/semver/v0.2.0"
                shaD, "refs/tags/semver/v0.10.0"
                shaA, "refs/tags/yaml/v3.0.0"
                shaA, "refs/tags/v0.1.0"
            ]

            Expect.equal
                (Weir.Contracts.latestModuleTag refs "semver")
                (Some("semver/v0.10.0", shaD))
                "numeric, not lexical"

            Expect.equal
                (Weir.Contracts.latestModuleTag refs "yaml")
                (Some("yaml/v3.0.0", shaA))
                "another module's tags are its own"

            Expect.equal (Weir.Contracts.latestModuleTag refs "retry") None "no tag for the module"
            Expect.equal (Weir.Contracts.latestModuleTag refs "v0") None "a repo-wide tag belongs to no module"
        }
        test "latestModuleTag: a prerelease is skipped while a stable tag exists, else the highest prerelease" {
            let refs = [
                shaB, "refs/tags/semver/v1.0.0"
                shaC, "refs/tags/semver/v2.0.0-rc.1"
                shaD, "refs/tags/semver/v2.0.0-beta.11"
            ]

            Expect.equal
                (Weir.Contracts.latestModuleTag refs "semver")
                (Some("semver/v1.0.0", shaB))
                "stable wins over a higher prerelease"

            Expect.equal
                (Weir.Contracts.latestModuleTag
                    (refs |> List.filter (fun (_, r) -> r <> "refs/tags/semver/v1.0.0"))
                    "semver")
                (Some("semver/v2.0.0-rc.1", shaC))
                "nothing stable: the highest prerelease"
        }
        test "latestModuleTag: the peeled commit of an annotated tag is preferred; a non-version tag is no candidate" {
            let refs = [
                shaB, "refs/tags/semver/v0.1.0"
                shaC, "refs/tags/semver/v0.1.0^{}"
                shaA, "refs/tags/semver/vnext"
                shaA, "refs/tags/semver/v01.0.0"
                shaD, "refs/tags/semver/latest"
            ]

            Expect.equal (Weir.Contracts.latestModuleTag refs "semver") (Some("semver/v0.1.0", shaC)) "peeled"

            Expect.equal
                (Weir.Contracts.latestModuleTag (refs |> List.filter (fun (_, r) -> not (r.EndsWith "^{}"))) "semver")
                (Some("semver/v0.1.0", shaB))
                "a lightweight tag"

            Expect.equal (Weir.Contracts.latestModuleTag [] "semver") None "an empty listing"
        }
    ]

let refListingTests =
    // [D:ref-resolve-smart-http]: the ref→sha step is a pure parse of
    // git's own advertisement plus a pure pick — every arm pinned here;
    // the fetch behind it is the live e2e probe's
    let pkt (s: string) =
        let body = System.Text.Encoding.UTF8.GetBytes(s + "\n")

        System.Text.Encoding.ASCII.GetBytes(sprintf "%04x" (body.Length + 4))
        |> Array.append
        <| body

    let adv (lines: string list) =
        Array.concat (
            [ pkt "# service=git-upload-pack"; System.Text.Encoding.ASCII.GetBytes "0000" ]
            @ (lines |> List.map pkt)
            @ [ System.Text.Encoding.ASCII.GetBytes "0000" ]
        )

    let shaA = String.replicate 40 "a"
    let shaB = String.replicate 40 "b"
    let shaC = String.replicate 40 "c"

    let parse bytes =
        match Weir.Contracts.parseRefListing bytes with
        | Ok refs -> refs
        | Error e -> failtest $"must parse: {e}"

    testList "ref listing [D:ref-resolve-smart-http]" [
        test "the GitHub shape: service line, flush, HEAD with its NUL capability list, heads and tags" {
            let refs =
                parse (
                    adv [
                        shaA
                        + " HEAD\000multi_ack thin-pack side-band symref=HEAD:refs/heads/main agent=git/github"
                        shaA + " refs/heads/main"
                        shaB + " refs/heads/feat/lib"
                        shaC + " refs/tags/v0.1.0"
                    ]
                )

            Expect.equal
                refs
                [
                    shaA, "HEAD"
                    shaA, "refs/heads/main"
                    shaB, "refs/heads/feat/lib"
                    shaC, "refs/tags/v0.1.0"
                ]
                "sha/refname pairs, the capabilities dropped"
        }
        test "an annotated tag lists the tag object and its peeled commit" {
            let refs =
                parse (adv [ shaA + " HEAD\000caps"; shaB + " refs/tags/v1"; shaC + " refs/tags/v1^{}" ])

            Expect.equal refs [ shaA, "HEAD"; shaB, "refs/tags/v1"; shaC, "refs/tags/v1^{}" ] "both lines kept"
        }
        test "a flush-only body lists nothing" {
            Expect.equal (parse (System.Text.Encoding.ASCII.GetBytes "0000")) [] "no refs"
            Expect.equal (parse [||]) [] "empty"
        }
        test "garbage is not an advertisement" {
            for body in
                [
                    System.Text.Encoding.UTF8.GetBytes "<!DOCTYPE html><html>…"
                    System.Text.Encoding.ASCII.GetBytes "0010abc"
                    System.Text.Encoding.ASCII.GetBytes("0040" + shaA)
                    adv [ "not a sha at all, just prose" ]
                    adv [ shaA + "refs/heads/nospace" ]
                ] do
                match Weir.Contracts.parseRefListing body with
                | Error _ -> ()
                | Ok refs -> failtest $"'{System.Text.Encoding.UTF8.GetString body}' must refuse, got {refs}"
        }
        test "pick: the peeled commit beats the tag object beats the branch" {
            let refs = [
                shaA, "refs/heads/v1"
                shaB, "refs/tags/v1"
                shaC, "refs/tags/v1^{}"
                shaA, "refs/heads/main"
            ]

            Expect.equal (Weir.Contracts.pickRef refs "v1") (Some shaC) "peeled"

            Expect.equal
                (Weir.Contracts.pickRef (refs |> List.filter (fun (_, n) -> n <> "refs/tags/v1^{}")) "v1")
                (Some shaB)
                "the tag object (a lightweight tag)"

            Expect.equal (Weir.Contracts.pickRef [ shaA, "refs/heads/v1" ] "v1") (Some shaA) "the branch"
            Expect.equal (Weir.Contracts.pickRef refs "main") (Some shaA) "a branch by name"
        }
        test "pick: a full 40-hex sha is its own answer, with or without a listing; a missing name is None" {
            Expect.equal (Weir.Contracts.pickRef [] shaB) (Some shaB) "no listing needed"
            Expect.equal (Weir.Contracts.pickRef [ shaA, "refs/heads/main" ] shaB) (Some shaB) "not looked up"
            Expect.equal (Weir.Contracts.pickRef [ shaA, "refs/heads/main" ] "v0") None "missing"
            Expect.equal (Weir.Contracts.pickRef [] "0123abcd") None "a sha prefix is not a ref"
        }
    ]

let vendorClosureTests =
    // [D:add-module-closure]: the closure walk, its refusals, the layout,
    // the lock round-trip, per-file confinement and weir: resolution order
    let fetchFrom (files: (string * string) list) =
        let m = Map.ofList files

        fun (p: string) ->
            match Map.tryFind p m with
            | Some t -> Ok(System.Text.Encoding.UTF8.GetBytes(t: string))
            | None -> Error $"{p} answered 404"

    let modText name (imports: string list) =
        String.concat
            "\n"
            ([ $"module {name}"; "" ]
             @ imports
             @ [ ""; "/// x"; "let f : int -> int"; ""; "let f x = x" ])

    let sha (c: char) = System.String(c, 64)

    let closureEntry (files: (string * string) list) : Weir.Contracts.LockEntry = {
        Kind = "module"
        Name = "m"
        Url = "http://h/r/"
        Sha256 = Weir.Contracts.treeHash files
        Path = "modules/m"
        Version = None
        Closure =
            Some {
                Commit = Some(System.String('c', 40))
                Entry = fst files.Head
                Files = files
            }
    }

    testList "vendor closure [D:add-module-closure]" [
        test "the walk follows relative imports at every spelling, dedups a diamond, entry first" {
            let fetch =
                fetchFrom [
                    "s/entry.weir",
                    modText "Entry" [
                        "import \"./lib/a.weir\" as A"
                        "import \"lib/sub/b.weir\" as B"
                        "import \"sibnoext\" as S"
                    ]
                    "s/lib/a.weir", modText "A" []
                    "s/lib/sub/b.weir", modText "B" [ "import \"../a.weir\" as A"; "import \"./../../sibnoext\" as S" ]
                    "s/sibnoext", modText "Sib" []
                ]

            match Weir.Script.walkVendorClosure fetch "s/entry.weir" with
            | Ok files ->
                Expect.equal
                    (files |> List.map fst)
                    [ "s/entry.weir"; "s/lib/a.weir"; "s/lib/sub/b.weir"; "s/sibnoext" ]
                    "every relative spelling joins, each file once, entry first"
            | Error e -> failtest e
        }
        test "--path: the raw URL is the root, the entry joins it; without --path a `//` URL stays verbatim" {
            match Weir.Contracts.resolveModuleSpec "https://h/base" (Some "lib/x.weir") with
            | Ok s ->
                Expect.equal s.Root (Some("https://h/base/", "lib/x.weir")) "the root gains its trailing /"
                Expect.equal s.Url "https://h/base/lib/x.weir" "the entry's URL"
            | Error e -> failtest e

            match Weir.Contracts.resolveModuleSpec "https://h/base/" (Some "a/../lib/x.weir") with
            | Ok s ->
                Expect.equal s.Root (Some("https://h/base/", "a/../lib/x.weir")) "a `..` that stays inside is fine"
            | Error e -> failtest e

            match Weir.Contracts.resolveModuleSpec "https://s3.h/team//lib/retry.weir" None with
            | Ok s ->
                Expect.equal s.Url "https://s3.h/team//lib/retry.weir" "fetched verbatim"
                Expect.isNone s.Root "never split"
            | Error e -> failtest e
        }
        test "--path refuses the shorthand, empty, absolute, escaping, the root itself, and a root with ? or #" {
            let cases = [
                "github.com/o/r//x.weir@v1", "x.weir", "the shorthand already carries the path"
                "https://h/b/", "", "--path is empty"
                "https://h/b/", "/etc/x.weir", "is absolute"
                "https://h/b/", "C:/x.weir", "is absolute"
                "https://h/b/", "../x.weir", "climbs above the root URL"
                "https://h/b/", "lib/../../x.weir", "climbs above the root URL"
                "https://h/b/", "lib/..", "names the root itself"
                "https://h/b/?sig=1", "x.weir", "without a query or fragment"
                "https://h/b/#f", "x.weir", "without a query or fragment"
            ]

            for spec, p, want in cases do
                match Weir.Contracts.resolveModuleSpec spec (Some p) with
                | Ok _ -> failtest $"{spec} --path {p} must refuse"
                | Error e -> Expect.stringContains e want $"{spec} --path {p}"
        }
        test "a leaf walks to itself" {
            match Weir.Script.walkVendorClosure (fetchFrom [ "x.weir", modText "X" [] ]) "x.weir" with
            | Ok [ ("x.weir", _) ] -> ()
            | other -> failtest $"{other}"
        }
        test "a weir: import, an absolute path and a root escape refuse, located at file:line:col" {
            let cases = [
                "import \"weir:other\" as O",
                [
                    "lib/e.weir:3:8"
                    "a weir: import inside a vendored module"
                    "current boundary, not a refusal"
                ]
                "import \"/etc/x.weir\" as O", [ "lib/e.weir:3:8"; "imports the absolute path '/etc/x.weir'" ]
                "import \"C:/x.weir\" as O", [ "imports the absolute path" ]
                "import \"../../x.weir\" as O", [ "lib/e.weir:3:8"; "resolves outside the repo root" ]
            ]

            for imp, wants in cases do
                match Weir.Script.walkVendorClosure (fetchFrom [ "lib/e.weir", modText "E" [ imp ] ]) "lib/e.weir" with
                | Ok _ -> failtest $"{imp} must refuse"
                | Error e ->
                    for w in wants do
                        Expect.stringContains e w imp
        }
        test "a missing import names the importing site; the entry spec cannot escape" {
            match
                Weir.Script.walkVendorClosure
                    (fetchFrom [ "e.weir", modText "E" [ "import \"./gone.weir\" as G" ] ])
                    "e.weir"
            with
            | Error e ->
                Expect.stringContains e "gone.weir (imported at e.weir:3:8)" e
                Expect.stringContains e "404" e
            | Ok _ -> failtest "a missing import must refuse"

            match Weir.Script.walkVendorClosure (fetchFrom []) "../x.weir" with
            | Error e -> Expect.stringContains e "not a file inside the repo" e
            | Ok _ -> failtest "an escaping entry must refuse"
        }
        test "a cycle walks once per file (the loader names it at validation)" {
            let fetch =
                fetchFrom [
                    "a.weir", modText "A" [ "import \"./b.weir\" as B" ]
                    "b.weir", modText "B" [ "import \"./a.weir\" as A" ]
                ]

            match Weir.Script.walkVendorClosure fetch "a.weir" with
            | Ok files -> Expect.equal (files |> List.map fst) [ "a.weir"; "b.weir" ] "each once"
            | Error e -> failtest e
        }
        test "the layout mirrors the repo under the common ancestor directory" {
            Expect.equal
                (Weir.Script.closureAncestor [
                    "starship/starship.weir"
                    "starship/lib/a.weir"
                    "starship/lib/mods/b.weir"
                ])
                "starship/"
                "the port"

            Expect.equal (Weir.Script.closureAncestor [ "lib/mods/x.weir"; "util.weir" ]) "" "an import above the entry"
            Expect.equal (Weir.Script.closureAncestor [ "a/b/x.weir"; "a/b/c/y.weir" ]) "a/b/" "deep"
        }
        test "validation loads the closure: a module purity violation in a lib file names that file" {
            withTempDir "weir-closure-check" (fun dir ->
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, "lib"))
                |> ignore

                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "e.weir"),
                    modText "E" [ "import \"./lib/a.weir\" as A" ]
                )

                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "lib", "a.weir"),
                    "module A\n\nlet now = ls\n\nprint \"x\"\n"
                )

                match Weir.Script.checkVendoredClosure dir "e.weir" with
                | Ok n -> failtest $"a command in a lib must refuse ({n})"
                | Error e -> Expect.stringContains e "a.weir" e

                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "lib", "a.weir"), modText "A" [])

                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "e.weir"),
                    "module E\n\nimport \"./lib/a.weir\" as A\n\n/// y\nlet g : int -> int\n\nlet g x = A.f x\n"
                )

                match Weir.Script.checkVendoredClosure dir "e.weir" with
                | Ok n -> Expect.equal n 1 "the entry's member count"
                | Error e -> failtest e)
        }
        test "a closure entry round-trips through the lock at schemaVersion 2; leaf-only stays 1" {
            withTempDir "weir-closure-lock" (fun dir ->
                let files = [ "entry.weir", sha 'a'; "lib/a.weir", sha 'b' ]

                let leaf: Weir.Contracts.LockEntry = {
                    Kind = "module"
                    Name = "l"
                    Url = "http://h/l.weir"
                    Sha256 = sha 'd'
                    Path = "modules/l.weir"
                    Version = None
                    Closure = None
                }

                Weir.Contracts.writeLock dir [ leaf ]
                let text1 = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "lock.json"))
                Expect.stringContains text1 "\"schemaVersion\": 1" "leaf-only"
                Expect.isFalse (text1.Contains "files") "no closure keys on a leaf"

                Weir.Contracts.writeLock dir [ leaf; closureEntry files ]
                let text2 = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "lock.json"))
                Expect.stringContains text2 "\"schemaVersion\": 2" "a closure writes 2"

                match Weir.Contracts.readLock dir with
                | Ok [ l; c ] ->
                    Expect.equal l leaf "the leaf reads back"
                    Expect.equal c (closureEntry files) "the closure reads back"
                | other -> failtest $"{other}"

                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(dir, "lock.json"),
                    text2.Replace("\"schemaVersion\": 2", "\"schemaVersion\": 3")
                )

                match Weir.Contracts.readLock dir with
                | Error e -> Expect.stringContains e "newer than this weir understands" e
                | Ok _ -> failtest "3 must refuse"

                let bad = [
                    text2.Replace(sha 'b', "zz"), "\"sha256\" must be 64 hex digits"
                    text2.Replace("\"entry\": \"entry.weir\"", "\"entry\": \"nope.weir\""), "is not one of its files"
                    text2.Replace("\"path\": \"lib/a.weir\"", "\"path\": \"\""), "\"path\" must be a non-empty string"
                ]

                for t, want in bad do
                    System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "lock.json"), t)

                    match Weir.Contracts.readLock dir with
                    | Error e -> Expect.stringContains e want want
                    | Ok _ -> failtest $"{want} must refuse")
        }
        test "a closure file's dest is confined to its own modules/<name>/" {
            withTempDir "weir-closure-confine" (fun wd ->
                let e = closureEntry [ "entry.weir", sha 'a' ]

                match Weir.Contracts.closureFileDest wd e "lib/a.weir" with
                | Ok d ->
                    Expect.equal
                        d
                        (System.IO.Path.GetFullPath(System.IO.Path.Combine(wd, "modules", "m", "lib", "a.weir")))
                        "benign"
                | Error e -> failtest e

                for hostile in [ "../other/x.weir"; "../../lock.json"; "/etc/passwd"; "a/../../m.weir" ] do
                    match Weir.Contracts.closureFileDest wd e hostile with
                    | Ok d -> failtest $"{hostile} must refuse, got {d}"
                    | Error msg -> Expect.stringContains msg "module m" hostile

                if not (System.OperatingSystem.IsWindows()) then
                    let outside =
                        System.IO.Path.Combine(wd, "..", System.IO.Path.GetFileName wd + "-out")

                    System.IO.Directory.CreateDirectory outside |> ignore

                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(wd, "modules", "m"))
                    |> ignore

                    try
                        System.IO.Directory.CreateSymbolicLink(
                            System.IO.Path.Combine(wd, "modules", "m", "lib"),
                            outside
                        )
                        |> ignore

                        match Weir.Contracts.closureFileDest wd e "lib/a.weir" with
                        | Ok d -> failtest $"a symlinked dir out must refuse, got {d}"
                        | Error msg -> Expect.stringContains msg "escapes .weir/" msg
                    finally
                        System.IO.Directory.Delete(outside, true))
        }
        test "weir:<name> resolves <name>.weir, then <name>/ by its lock entry, then types/; faults are named" {
            withTempDir "weir-closure-resolve" (fun td ->
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(td, ".git"))
                |> ignore

                let wd = System.IO.Path.Combine(td, ".weir")
                let mdir = System.IO.Path.Combine(wd, "modules", "m")

                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(mdir, "lib"))
                |> ignore

                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(wd, "types"))
                |> ignore

                let main = System.IO.Path.Combine(td, "main.weir")

                let errs lines =
                    errorsIn main lines |> List.map (fun d -> d.Message)

                System.IO.File.WriteAllText(System.IO.Path.Combine(mdir, "lib", "a.weir"), modText "A" [])

                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(mdir, "entry.weir"),
                    "module Entry\n\nimport \"./lib/a.weir\" as A\n\n/// y\nlet g : int -> int\n\nlet g x = A.f x\n"
                )

                let usage = [ "import \"weir:m\" as M"; "print (show (M.g 1))" ]

                Expect.exists
                    (errs usage)
                    (fun m -> m.Contains "the lock has no entry for")
                    "a directory with no lock entry"

                let files = [
                    "entry.weir",
                    Weir.Contracts.sha256Hex (System.IO.File.ReadAllBytes(System.IO.Path.Combine(mdir, "entry.weir")))
                    "lib/a.weir",
                    Weir.Contracts.sha256Hex (
                        System.IO.File.ReadAllBytes(System.IO.Path.Combine(mdir, "lib", "a.weir"))
                    )
                ]

                Weir.Contracts.writeLock wd [ closureEntry files ]
                Expect.isEmpty (errs usage) "the directory resolves through the lock's entry"

                System.IO.File.WriteAllText(System.IO.Path.Combine(wd, "modules", "m.weir"), modText "M" [])

                Expect.exists
                    (errs usage)
                    (fun m -> m.Contains "names two vendored forms")
                    "<name>.weir beside <name>/"

                System.IO.File.Delete(System.IO.Path.Combine(wd, "modules", "m.weir"))

                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(wd, "types", "m.weir"),
                    "module M\ntype T = { a: int }\n"
                )

                Expect.exists
                    (errs usage)
                    (fun m -> m.Contains "names two vendored files")
                    "<name>/ beside types/<name>.weir"

                System.IO.Directory.Delete(mdir, true)
                Expect.isEmpty (errs [ "import \"weir:m\" as M"; "print (show { a = 1 })" ]) "types/ last")
        }
    ]

let lockfileSymlinkConfinementTests =
    // DA-04 [D:lockfile-symlink-confinement]: confineUnder is lexical, so a
    // lexically-clean path escapes through a symlinked component. entryDest
    // now runs the real-path gate too — probe confineRealUnder directly
    // (POSIX symlinks; skipped on Windows).
    let mkEntry path : Weir.Contracts.LockEntry = {
        Kind = "schema"
        Name = "n"
        Url = "http://x"
        Sha256 = "0"
        Path = path
        Version = None
        Closure = None
    }

    testList "lockfile symlink confinement [D:lockfile-symlink-confinement]" [
        test "entryDest refuses a symlinked INTERMEDIATE dir escaping .weir/ (DA-04)" {
            if System.OperatingSystem.IsWindows() then
                ()
            else
                let baseDir = tempPath "weir-da04"

                let weirDir = System.IO.Path.Combine(baseDir, ".weir")
                let outside = System.IO.Path.Combine(baseDir, "OUTSIDE")
                System.IO.Directory.CreateDirectory weirDir |> ignore
                System.IO.Directory.CreateDirectory outside |> ignore

                try
                    // .weir/schemas is a symlink to a dir outside .weir/
                    System.IO.Directory.CreateSymbolicLink(System.IO.Path.Combine(weirDir, "schemas"), outside)
                    |> ignore

                    // the lock path is lexically clean — no `..`, not absolute
                    match Weir.Contracts.entryDest weirDir (mkEntry "schemas/marker.json") with
                    | Ok d -> failtest $"a symlinked intermediate dir must refuse, got {d}"
                    | Error msg ->
                        Expect.stringContains msg "schema n" "the refusal names the entry"
                        Expect.stringContains msg "escapes .weir/" "and names the escape"
                finally
                    try
                        System.IO.Directory.Delete(baseDir, true)
                    with _ ->
                        ()
        }
        test "confineRealUnder refuses a symlinked FINAL component escaping out" {
            if System.OperatingSystem.IsWindows() then
                ()
            else
                let baseDir = tempPath "weir-da04b"

                let weirDir = System.IO.Path.Combine(baseDir, ".weir")
                let schemas = System.IO.Path.Combine(weirDir, "schemas")
                let outside = System.IO.Path.Combine(baseDir, "OUTSIDE")
                System.IO.Directory.CreateDirectory schemas |> ignore
                System.IO.Directory.CreateDirectory outside |> ignore

                try
                    // the leaf itself is a link pointing to a file outside
                    let leak = System.IO.Path.Combine(outside, "leak.json")
                    System.IO.File.WriteAllText(leak, "{}")
                    let leaf = System.IO.Path.Combine(schemas, "marker.json")
                    System.IO.File.CreateSymbolicLink(leaf, leak) |> ignore

                    match Weir.Contracts.entryDest weirDir (mkEntry "schemas/marker.json") with
                    | Ok d -> failtest $"a symlinked final component must refuse, got {d}"
                    | Error msg -> Expect.stringContains msg "escapes .weir/" "the refusal names the escape"
                finally
                    try
                        System.IO.Directory.Delete(baseDir, true)
                    with _ ->
                        ()
        }
        test "a REAL .weir/schemas dir + a clean path still confines (not over-refused)" {
            let baseDir = tempPath "weir-da04c"

            let weirDir = System.IO.Path.Combine(baseDir, ".weir")

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(weirDir, "schemas"))
            |> ignore

            try
                match Weir.Contracts.entryDest weirDir (mkEntry "schemas/marker.json") with
                | Ok _ -> ()
                | Error e -> failtest $"a real directory + clean path must confine: {e}"
            finally
                try
                    System.IO.Directory.Delete(baseDir, true)
                with _ ->
                    ()
        }
        test "F1 absolute/`..` traversal is still refused (A+B behavior unchanged)" {
            let baseDir = tempPath "weir-da04d"

            let weirDir = System.IO.Path.Combine(baseDir, ".weir")
            System.IO.Directory.CreateDirectory weirDir |> ignore

            try
                for bad in [ "../escape.json"; "/etc/passwd" ] do
                    match Weir.Contracts.entryDest weirDir (mkEntry bad) with
                    | Ok d -> failtest $"'{bad}' must still refuse, got {d}"
                    | Error msg -> Expect.stringContains msg "escapes .weir/" "the lexical refusal is unchanged"
            finally
                try
                    System.IO.Directory.Delete(baseDir, true)
                with _ ->
                    ()
        }
        test "writeConfined writes a READABLE file (mode is sane, not zero) [D:lockfile-symlink-confinement]" {
            // the variadic-open mode regression: on ARM64 macOS the fixed-
            // signature open(2) supplied a garbage mode_t (stack-passed
            // variadic arg), so the leaf could land unreadable and a later
            // read (verify) failed with Permission denied. fchmod on the
            // open fd makes 0o644 reliable — the file must read back.
            let baseDir = tempPath "weir-da04e"

            let weirDir = System.IO.Path.Combine(baseDir, ".weir")

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(weirDir, "schemas"))
            |> ignore

            let dest = System.IO.Path.Combine(weirDir, "schemas", "k8s-configmap.json")
            let payload = System.Text.Encoding.UTF8.GetBytes "{\"ok\":true}"

            try
                match Weir.Contracts.writeConfined weirDir dest payload with
                | Error e -> failtest $"writeConfined must succeed under a real .weir/: {e}"
                | Ok() ->
                    // the exact CI failure was File.ReadAllBytes → Permission
                    // denied; the file must read back identically
                    let back = System.IO.File.ReadAllBytes dest
                    Expect.equal back payload "the written file reads back byte-identical"

                    if not (System.OperatingSystem.IsWindows()) then
                        // the permission bits must be 0o644 (fchmod took) —
                        // owner read+write, group/other read
                        let mode = (System.IO.File.GetUnixFileMode dest)

                        let expected =
                            System.IO.UnixFileMode.UserRead
                            ||| System.IO.UnixFileMode.UserWrite
                            ||| System.IO.UnixFileMode.GroupRead
                            ||| System.IO.UnixFileMode.OtherRead

                        Expect.equal mode expected "the leaf is 0o644, not a garbage/zero mode"
            finally
                try
                    System.IO.Directory.Delete(baseDir, true)
                with _ ->
                    ()
        }
        test "writeConfined TRUNCATES a longer existing file (no stale tail) [D:lockfile-symlink-confinement]" {
            // the open(2) flag regression: the hardcoded Linux O_CREAT/O_TRUNC
            // meant the wrong bits on macOS, so O_TRUNC never set — a re-write
            // over an existing longer file (restore repairing a tampered vendored
            // file) left stale trailing bytes → hash mismatch. The result must be
            // exactly the new bytes. Passes on Linux either way; guards the flags.
            let baseDir = tempPath "weir-da04t"

            let weirDir = System.IO.Path.Combine(baseDir, ".weir")

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(weirDir, "schemas"))
            |> ignore

            let dest = System.IO.Path.Combine(weirDir, "schemas", "k8s-configmap.json")

            // a longer tampered file already on disk (the restore-repair case)
            let longer =
                System.Text.Encoding.UTF8.GetBytes
                    "{\"tampered\":true,\"padding\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}"

            let shorter = System.Text.Encoding.UTF8.GetBytes "{\"ok\":true}"

            try
                System.IO.File.WriteAllBytes(dest, longer)

                match Weir.Contracts.writeConfined weirDir dest shorter with
                | Error e -> failtest $"writeConfined must succeed over an existing file: {e}"
                | Ok() ->
                    let back = System.IO.File.ReadAllBytes dest
                    // exactly the new bytes — no truncation bug would leave the
                    // longer file's trailing bytes after the shorter write
                    Expect.equal back shorter "the re-written file is truncated to exactly the new bytes"
                    Expect.equal back.Length shorter.Length "no stale trailing bytes remain"
            finally
                try
                    System.IO.Directory.Delete(baseDir, true)
                with _ ->
                    ()
        }
        // ---- DA-05: the contract-fetch client drops credentials on a
        // cross-origin redirect [D:contract-redirect] ----
        test "fetchBytesWith drops PRIVATE-TOKEN across a cross-origin redirect; a control header crosses" {
            // origin A (127.0.0.1) 302s to origin B (localhost) — a
            // different origin by host. B records what arrived. The bare
            // HttpClient re-sent PRIVATE-TOKEN here (DA-05); the explicit
            // follow must drop it while a non-credential control crosses.
            let portA = 8621
            let portB = 8622
            let received = System.Collections.Generic.List<string>()

            let a = new System.Net.HttpListener()
            a.Prefixes.Add $"http://127.0.0.1:{portA}/"
            let b = new System.Net.HttpListener()
            b.Prefixes.Add $"http://localhost:{portB}/"

            a.Start()
            b.Start()

            let aLoop =
                System.Threading.Thread(fun () ->
                    try
                        let ctx = a.GetContext()
                        ctx.Response.StatusCode <- 302
                        ctx.Response.Headers.Add("Location", $"http://localhost:{portB}/dest")
                        ctx.Response.Close()
                    with _ ->
                        ())

            let bLoop =
                System.Threading.Thread(fun () ->
                    try
                        let ctx = b.GetContext()

                        for k in ctx.Request.Headers.AllKeys do
                            match ctx.Request.Headers.Get k with
                            | null -> ()
                            | v -> received.Add $"{k}: {v}"

                        let bytes = System.Text.Encoding.UTF8.GetBytes "landed"
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length)
                        ctx.Response.Close()
                    with _ ->
                        ())

            aLoop.IsBackground <- true
            bLoop.IsBackground <- true
            aLoop.Start()
            bLoop.Start()

            try
                let result =
                    Weir.Contracts.fetchBytesWith
                        [
                            ("PRIVATE-TOKEN", "SECRET-GITLAB-TOKEN")
                            ("Authorization", "Bearer SECRET-GH-TOKEN")
                            ("X-Control", "keepme")
                        ]
                        $"http://127.0.0.1:{portA}/start"

                match result with
                | Ok(bytes, _) ->
                    Expect.equal
                        (System.Text.Encoding.UTF8.GetString bytes)
                        "landed"
                        "the redirect is followed to origin B"
                | Error e -> failtest $"the cross-origin fetch should succeed: {e}"

                aLoop.Join 2000 |> ignore
                bLoop.Join 2000 |> ignore

                let arrived = String.concat "\n" received

                Expect.isFalse
                    (arrived.ToLowerInvariant().Contains "private-token")
                    $"PRIVATE-TOKEN must NOT reach the cross-origin target (DA-05), arrived:\n{arrived}"

                Expect.isFalse
                    (arrived.ToLowerInvariant().Contains "authorization")
                    $"Authorization must NOT reach the cross-origin target, arrived:\n{arrived}"

                Expect.stringContains
                    arrived
                    "X-Control: keepme"
                    "a non-credential control header still crosses (the drop is credential-specific)"
            finally
                a.Stop()
                b.Stop()
                (a :> System.IDisposable).Dispose()
                (b :> System.IDisposable).Dispose()
        }
    ]

let windowsV1Tests =
    // PATH is process-global — the probes mutate and restore it, so the
    // list is sequenced like every other env-mutating list in the suite
    testSequenced
    <| testList "Windows v1 platform arms" [
        test "PATHEXT: a bare name resolves through its extension on Windows ONLY" {
            let dir = weirPath (tempPath "weir-px")

            Directory.CreateDirectory dir |> ignore
            File.WriteAllText(Path.Combine(dir, "weirpxprobe.exe"), "")
            let oldPath = System.Environment.GetEnvironmentVariable "PATH"

            try
                System.Environment.SetEnvironmentVariable("PATH", dir + string Path.PathSeparator + oldPath)
                Weir.Extern.refresh ()
                Expect.isTrue (Weir.Extern.exists "weirpxprobe.exe") "the name as-given always resolves"

                Expect.equal
                    (Weir.Extern.exists "weirpxprobe")
                    (System.OperatingSystem.IsWindows())
                    "bare-name resolution is the PATHEXT arm: Windows yes, POSIX no"

                // .BAT rides the same arm (CreateProcess runs batch
                // files directly — no cmd /c synthesis) [D:windows-s2]
                File.WriteAllText(Path.Combine(dir, "weirbatprobe.bat"), "")

                Expect.equal
                    (Weir.Extern.exists "weirbatprobe")
                    (System.OperatingSystem.IsWindows())
                    "bare .bat resolution: Windows yes, POSIX no"

                // the list is read from the environment, not hardcoded
                // [D:windows-s2]: a custom extension resolves iff the
                // platform honours PATHEXT at all
                let oldExts = System.Environment.GetEnvironmentVariable "PATHEXT"

                try
                    System.Environment.SetEnvironmentVariable("PATHEXT", ".XYZ")
                    File.WriteAllText(Path.Combine(dir, "weircustomext.xyz"), "")

                    Expect.equal
                        (Weir.Extern.exists "weircustomext")
                        (System.OperatingSystem.IsWindows())
                        "a custom PATHEXT entry resolves on Windows only"
                finally
                    System.Environment.SetEnvironmentVariable("PATHEXT", oldExts)
            finally
                System.Environment.SetEnvironmentVariable("PATH", oldPath)
                Weir.Extern.refresh ()
        }
        test "PATH separator is the platform's own (Extern splits on Path.PathSeparator)" {
            let dir = weirPath (tempPath "weir-ps")

            Directory.CreateDirectory dir |> ignore
            File.WriteAllText(Path.Combine(dir, "weirpsprobe"), "")
            let oldPath = System.Environment.GetEnvironmentVariable "PATH"

            try
                // prepend, never replace: PATH is process-global and
                // `testSequenced` does not isolate it under the YoloDev
                // TestSdk (a bare `PATH := dir` raced concurrent spawns
                // into "sh not found"). The prepended probe dir still
                // forces a real Path.PathSeparator split to find the probe.
                System.Environment.SetEnvironmentVariable("PATH", dir + string Path.PathSeparator + oldPath)
                Weir.Extern.refresh ()
                Expect.isTrue (Weir.Extern.exists "weirpsprobe") "a prepended PATH entry resolves through the split"
            finally
                System.Environment.SetEnvironmentVariable("PATH", oldPath)
                Weir.Extern.refresh ()
        }
        test "env lookup carries the platform's case rule through Env.get" {
            System.Environment.SetEnvironmentVariable("WEIR_CASE_PROBE", "here")

            try
                let got = run "Env.get \"weir_case_probe\""

                let expected =
                    if System.OperatingSystem.IsWindows() then
                        VUnion("Some", Some(VStr "here"))
                    else
                        VUnion("None", None)

                Expect.equal got expected "Windows env is case-insensitive; POSIX is not"
            finally
                System.Environment.SetEnvironmentVariable("WEIR_CASE_PROBE", null)
        }
        test "a genuinely-missing command still misses (PATHEXT adds, never invents)" {
            Expect.isFalse (Weir.Extern.exists "weir-definitely-absent-xyzzy") ""
        }
        test "add's raw-URL hint rewrites blob pages; teaches generically otherwise [D:add-validates]" {
            Expect.stringContains
                (Weir.Contracts.rawUrlHint "https://github.com/acme/schemas/blob/main/svc.json")
                "https://raw.githubusercontent.com/acme/schemas/main/svc.json"
                "github rewrite"

            Expect.stringContains
                (Weir.Contracts.rawUrlHint "https://gitlab.com/grp/sub/proj/-/blob/main/svc.json")
                "https://gitlab.com/grp/sub/proj/-/raw/main/svc.json"
                "gitlab rewrite (subgroups ride the greedy group)"

            Expect.stringContains (Weir.Contracts.rawUrlHint "https://example.com/x.json") "use the raw URL" "generic"
        }
        test "add schema refuses a local path or file:// URL by usage, writing nothing [D:http-url-refused]" {
            let dir =
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weir-add-{System.Guid.NewGuid():N}")

            let weirDir = System.IO.Path.Combine(dir, ".weir")

            for u in [ "/srv/schemas/cm.json"; "file:///srv/schemas/cm.json" ] do
                Expect.equal
                    (Weir.Contracts.addFetched weirDir "schema" "cm" u)
                    (Error $"add schema takes an http(s) URL; got '{u}'")
                    u

            Expect.isFalse (System.IO.Directory.Exists dir) "nothing was written"
        }
        test "LSP uri/path ROUND-TRIPS on this platform (spaces + non-ASCII)" {
            // the acceptance is identity both ways [D:windows-s3] — a
            // one-way fix is how the mirror bug survives
            let native =
                if System.OperatingSystem.IsWindows() then
                    "C:\\a b\\wé.weir"
                else
                    "/a b/wé.weir"

            let uri = Weir.Lsp.pathToUri native
            Expect.stringContains uri "%20" "spaces percent-encode"
            Expect.isTrue (uri.StartsWith "file:///") "file scheme, empty host"
            Expect.equal (Weir.Lsp.uriToPath uri) native "round-trip identity"

            // the crash shape: a bare drive path must convert, not throw
            Expect.isTrue ((Weir.Lsp.pathToUri "C:\\x\\y.weir").StartsWith "file:///c:/") "drive lowercases on the wire"

            // the VS Code spelling with an encoded drive colon decodes
            let vp = Weir.Lsp.uriToPath "file:///c%3A/a/b.weir"

            if System.OperatingSystem.IsWindows() then
                Expect.equal vp "C:\\a\\b.weir" "encoded drive colon accepted"
            else
                Expect.equal vp "/c:/a/b.weir" "POSIX keeps the literal path"
        }
    ]

let hunt4Tests =
    let runFile (lines: string list) : int =
        let path = tempPath "weir-hunt4" + ".weir"
        File.WriteAllLines(path, lines)

        try
            Weir.Script.run path []
        finally
            File.Delete path

    let caps (lines: string list) : string =
        let _, pairs, _, _, _ = Weir.Script.analyzeLines "hunt4.weir" lines

        Weir.Can.collect "hunt4.weir" (Weir.Can.ofChecked pairs)
        |> Weir.Can.renderHuman "hunt4.weir"

    testSequenced
    <| testList "hunt 4 findings" [
        test "readonly refuses File.writeBytes [D:pure-stage2]" {
            mustSayIn
                "pin.weir"
                [
                    "let x ="
                    "    readonly"
                    "        File.writeBytes \"out.bin\" (Str.toUtf8 \"x\")"
                    "        1"
                    "print (show x)"
                ]
                "'File.writeBytes' writes the filesystem"
                "a byte write is a filesystem write"
        }
        test "every File/Dir member has a side in the partition [D:can-report]" {
            // a new member must be placed: a write joins Effects.fsWriteMembers,
            // a read this list — the report and `readonly` both read the result
            let reads =
                Set [
                    "File.exists"
                    "File.isExecutable"
                    "File.mode"
                    "File.read"
                    "File.readBytes"
                    "File.readFrom"
                    "File.readSecret"
                    "File.sha1"
                    "File.sha256"
                    "File.size"
                    "File.stat"
                    "Dir.exists"
                    "Dir.list"
                    "Dir.stat"
                ]

            let members =
                [ "File"; "Dir" ]
                |> List.collect (fun m ->
                    Weir.Builtins.typeEnv.Modules
                    |> Map.tryFind m
                    |> Option.map (fun ms -> ms |> Map.toList |> List.map (fun (n, _) -> $"{m}.{n}"))
                    |> Option.defaultValue [])

            Expect.isNonEmpty members "the File/Dir members are in the type environment"

            for m in members do
                let isWrite = Weir.Effects.effectClass m = Some Weir.Effects.Mutation
                Expect.isTrue (isWrite <> reads.Contains m) $"{m}: a write xor a listed read (write={isWrite})"
        }
        test "--can reports deletes, moves, byte writes and cd [D:can-report]" {
            let out =
                caps [
                    "File.delete \"/x/a\""
                    "File.move \"/x/b\" \"/x/c\""
                    "File.writeBytes \"/x/d\" (Str.toUtf8 \"x\")"
                    "cd \"/var\""
                    "let n = File.readBytes \"/x/e\" |> Bytes.length"
                    "print $\"{n} {ls |> Seq.length}\""
                ]

            for needle in
                [
                    "File.delete /x/a"
                    "File.move /x/b"
                    "File.writeBytes /x/d"
                    "changes the working directory (cd)"
                    "File.readBytes /x/e"
                    "ls (the working directory)"
                ] do
                Expect.stringContains out needle "the capability is reported"
        }
        test "--can renders a literal's control characters visibly [D:error-data-escape]" {
            let out = caps [ "File.write \"/tmp/ok\\n  nothing else\" [\"x\"]" ]
            Expect.stringContains out "/tmp/ok\\x0a  nothing else" "the newline is shown, not written"
        }
        test "--can reports a reified command's env overlay [D:can-report]" {
            let out =
                caps [
                    "let e = Env.ofPairs [(\"K\", \"v\")]"
                    "let z = $e(sh -c \"true\" | complete)"
                    "print (show z.ok)"
                ]

            Expect.stringContains out "sets K for children (env sigil)" "the overlay is reported"
        }
        test "a nullary case takes a guard" {
            Expect.equal
                (runFile [
                    "type L = Debug | Info | Warn"
                    "let f l n = match l with | Debug | Info when n > 0 -> 1 | Warn when n > 0 -> 2 | _ -> 3"
                    "let g = function | None when 1 == 1 -> 1 | _ -> 2"
                    "if f Info 1 <> 1 then exit 3"
                    "if f Warn 1 <> 2 then exit 4"
                    "if f Warn 0 <> 3 then exit 5"
                    "if g None <> 1 then exit 6"
                ])
                0
                "| None when …, | A | B when …, in match and function"
        }
        test "a name starting with a declaration keyword heads a statement" {
            Expect.equal
                (runFile [
                    "let modules = [\"a\"]"
                    "modules |> Seq.iter (fun m ->"
                    "    let y = <<<"
                    "        x"
                    "    if (y |> Seq.head) + m <> \"xa\" then exit 3)"
                    "let typed = 1"
                    "let imports = [typed]"
                    "imports |> Seq.iter (fun i -> if i <> 1 then exit 4)"
                ])
                0
                "modules / typed / imports are ordinary names"

            for head in [ "type-check"; "module-assistant"; "import-im6"; "try-x"; "return-code" ] do
                let ds = diagsIn "pin.weir" [ $"{head} --flag" ]

                Expect.isTrue
                    (ds |> List.forall (fun d -> d.Severity <> "error"))
                    $"{head} is a command word: {ds |> List.map _.Message}"
        }
        test "a REPL ^C stops computation at a call and in a long range [D:repl-interrupt-compute]" {
            for src in
                [
                    "Seq.initInfinite id |> Seq.length"
                    "Seq.initInfinite (fun i -> i + 1) |> Seq.length"
                    "[1..9000000000000] |> Seq.sum"
                ] do
                Weir.Session.beginReplEval ()

                try
                    let sw = System.Diagnostics.Stopwatch.StartNew()

                    System.Threading.Tasks.Task.Run(fun () ->
                        System.Threading.Thread.Sleep 200
                        Weir.Session.interruptRepl ())
                    |> ignore

                    Expect.throwsT<Weir.Session.Interrupted> (fun () -> run src |> ignore) src
                    Expect.isLessThan sw.ElapsedMilliseconds 10000L "cut, not run to the end"
                finally
                    Weir.Session.endReplEval ()
        }
        test "an ordering operator on a generic ordered type demands the instance [D:derived-ord]" {
            mustSayIn
                "pin.weir"
                [ "type K<'a> = { [<Ord 1>] f: 'a }"; "print ({ f = [1] } < { f = [2] })" ]
                "operator '<' is not defined for K<seq<int>>"
                "a seq member does not order"

            mustSayIn
                "pin.weir"
                [
                    "type B<'a> = | [<Ord 1>] Box of 'a | [<Ord 2>] Nil"
                    "print (Box (1, 2) >= Box (2, 1))"
                ]
                "operator '>=' is not defined for B<"
                "a tuple member does not order"

            Expect.equal
                (runFile [
                    "type K<'a> = { [<Ord 1>] f: 'a }"
                    "type B<'a> = | [<Ord 1>] Box of 'a | [<Ord 2>] Nil"
                    "if not ({ f = 1 } < { f = 2 }) then exit 3"
                    "if not (Box \"a\" < Nil) then exit 4"
                ])
                0
                "an instance whose members order still compares"
        }
        test "a redirect rewrites only POST to GET on 301/302 [D:secret-redirect]" {
            Expect.equal
                (runFile [
                    "let route = fun rq ->"
                    "    match rq.path with"
                    "    | \"/old\" -> Reply.redirect 302 \"/new\""
                    "    | \"/see\" -> Reply.redirect 303 \"/new\""
                    "    | _ -> Reply.text 200 $\"{rq.method} {rq.body}\""
                    "within serve srv handler=route"
                    "    let base = $\"http://127.0.0.1:{Server.port srv}\""
                    "    let seen req = Http.send req |> _.body |> Str.join \"\""
                    "    if seen (Http.delete $\"{base}/old\") <> \"Delete \" then exit 3"
                    "    if seen { Http.put $\"{base}/old\" with body = Text \"p\" } <> \"Put p\" then exit 4"
                    "    if seen { Http.patch $\"{base}/old\" with body = Text \"p\" } <> \"Patch p\" then exit 5"
                    "    if seen { Http.post $\"{base}/old\" with body = Text \"p\" } <> \"Get \" then exit 6"
                    "    if seen { Http.put $\"{base}/see\" with body = Text \"p\" } <> \"Get \" then exit 7"
                ])
                0
                "DELETE, PUT and PATCH keep their method and body; POST and a 303 become GET"
        }
        test "Yaml.merge applied twice is applied once [D:yaml-nodes]" {
            Expect.equal
                (runFile [
                    "let doc = [\"args:\"; \"  - -v\"; \"  - -v\"; \"  - x\"] |> Yaml.parse"
                    "let p = yaml patch"
                    "    args:"
                    "        - $- -v"
                    "let once = doc |> Yaml.merge p"
                    "let j1 = once |> to json |> Str.join \"\""
                    "if j1 <> \"{\\\"args\\\":[\\\"x\\\"]}\" then exit 3"
                    "if (once |> Yaml.merge p |> to json |> Str.join \"\") <> j1 then exit 4"
                    "let p2 = yaml patch by=name"
                    "    images:"
                    "        - name: app"
                    "          tag: 1"
                    "        - name: app"
                    "          tag: 2"
                    "    xs:"
                    "        - name: $-"
                    "let once2 = [\"a: 1\"] |> Yaml.parse |> Yaml.merge p2"
                    "let j2 = once2 |> to json |> Str.join \"\""
                    "if (once2 |> Yaml.merge p2 |> to json |> Str.join \"\") <> j2 then exit 5"
                ])
                0
                "a tombstone takes every match; a patch's own items meet the merge laws where the target had none"
        }
        test "a bound name glued to a dash is subtraction in check and run [D:dash-binding]" {
            // PATH never decides: `count-1` with `count` bound is the same
            // expression to both, whatever is installed
            let src = [
                "let count = 5"
                "let last = count-1"
                "if last + 1 <> 5 then exit 3"
                "if not (count-1 > 0) then exit 4"
                "let f = fun n -> n-1"
                "if f 3 <> 2 then exit 5"
                "let g n m ="
                "    let d = n-m"
                "    d"
                "if g 9 4 <> 5 then exit 6"
            ]

            cleanIn "pin.weir" src "check reads the subtraction"
            Expect.equal (runFile src) 0 "run reads the subtraction"

            // a part nothing knows leaves the word a command
            let ds =
                diagsIn "pin.weir" [ "let docker = \"docker\""; "docker-composezz up"; "print docker" ]

            Expect.exists
                ds
                (fun d -> d.Message.Contains "command not found on PATH: docker-composezz")
                $"docker-composezz stays a command: {ds |> List.map _.Message}"
        }
        test "Table.render takes rows of one type whatever their field order [D:record-order]" {
            Expect.equal
                (runFile [
                    "type R = { b: int; a: int }"
                    "let t = [{ a = 2; b = 1 }; { b = 3; a = 4 }] |> Table.render |> Seq.freeze"
                    "if (t |> Seq.length) <> 4 then exit 3"
                    "if (t |> Seq.last |> Str.fields |> Str.join \",\") <> \"4,3\" then exit 4"
                    "let js = [\"[{\\\"name\\\":\\\"a\\\",\\\"email\\\":\\\"a@x\\\"},{\\\"name\\\":\\\"b\\\"},{\\\"email\\\":\\\"c@x\\\",\\\"name\\\":\\\"c\\\"}]\"]"
                    "let u = js |> from json seq<{| name: string; email: Option<string> |}> |> Table.render |> Seq.freeze"
                    "if (u |> Seq.last |> Str.fields |> Str.join \",\") <> \"c,c@x\" then exit 5"
                    "let v = [{| s = \"a\\nb\"; n = 1 |}; {| s = Str.replicate 500 \"x\"; n = 2 |}] |> Table.render |> Seq.freeze"
                    "if (v |> Seq.length) <> 4 then exit 6"
                    "if not (v[2] |> Str.contains \"a\\\\nb\") then exit 7"
                    "if Str.length v[3] < 500 then exit 8"
                ])
                0
                "a literal's and a wire read's field order; a newline cell stays one row; no clip off a terminal"
        }
        test "in an arm a lazy command's short first line arrives when written [D:async-seq]" {
            skipOnWindows ()

            Expect.equal
                (runFile [
                    "let short () = $(sh -c \"echo ok; exec sleep 3\") |> Seq.head"
                    "let (v, took) = [1] |> Seq.pmap (fun _ -> Duration.measure short) |> Seq.head"
                    "if v <> \"ok\" then exit 3"
                    "if took > 2s then exit 4"
                ])
                0
                "a line shorter than a byte-order mark's four bytes is not held for more input"
        }
        test "in an arm a value-headed pipe feeds while its consumer reads [D:value-headed-pipe]" {
            skipOnWindows ()

            Expect.equal
                (runFile [
                    "let ticks () = Seq.initInfinite (fun i ->"
                    "    Duration.sleep 100ms"
                    "    $\"tick {i}\")"
                    "let first () ="
                    "    let l = ticks () | cat |> Seq.head"
                    "    l"
                    "let (v, took) = [1] |> Seq.pmap (fun _ -> Duration.measure first) |> Seq.head"
                    "if v <> \"tick 0\" then exit 3"
                    "if took > 5s then exit 4"
                ])
                0
                "an endless slow producer does not hold the arm that reads the command's output"
        }
        test "a byte-order mark split across writes selects the decoding [D:byte-pipes]" {
            skipOnWindows ()

            Expect.equal
                (runFile [
                    "let hex s = s |> Str.toUtf8 |> Bytes.toHex"
                    "let split1 = sh -c \"printf '\\\\357'; sleep 0.1; printf '\\\\273\\\\277id=7\\\\n'\" |> Seq.head"
                    "if hex split1 <> \"69643d37\" then exit 3"
                    "let l = sh -c \"printf '\\\\357\\\\273'; sleep 0.1; printf '\\\\277id=7\\\\n'\" | line"
                    "if hex l <> \"69643d37\" then exit 4"
                    "let c = sh -c \"printf '\\\\357'; sleep 0.1; printf '\\\\273\\\\277id=7\\\\n'\" | cat | text"
                    "if hex c <> \"69643d37\" then exit 5"
                    "let u = sh -c \"printf '\\\\377'; sleep 0.1; printf '\\\\376i\\\\000d\\\\000\\\\n\\\\000'\" |> Seq.head"
                    "if hex u <> \"6964\" then exit 6"
                    "let short = sh -c \"echo ok; exec sleep 3\" |> Seq.head"
                    "if short <> \"ok\" then exit 7"
                ])
                0
                "lazy lines, | line and a chain's | text decode a late mark as an early one; a short line does not wait"
        }
        test "a retry-family body takes a command let with a reifier [D:statement-lets]" {
            skipOnWindows ()

            let src = [
                "retry attempts=3 delay=10ms"
                "    let r = sh -c \"exit 0\" | complete"
                "    r.ok"
                "within tmp d"
                "    poll timeout=1s interval=10ms"
                "        let up = test -d $d | succeeds"
                "        up"
                "    if not (Dir.exists d) then exit 3"
                "let n = repeat interval=10ms"
                "    let t = sh -c \"echo 7\" | line"
                "    t"
                "until v"
                "    let again = sh -c \"echo 7\" | line"
                "    v == again"
                "if n <> \"7\" then exit 4"
            ]

            cleanIn "pin.weir" src "the body and the until block are statement bodies"
            Expect.equal (runFile src) 0 "and they run"
        }
        test "a block let binds a value-headed pipe [D:value-headed-pipe]" {
            skipOnWindows ()

            let src = [
                "let count xs ="
                "    let out = xs | cat"
                "    out |> Seq.length"
                "if count [\"a\"; \"b\"] <> 2 then exit 3"
                "if 1 == 1 then"
                "    let o = [\"x\"; \"in\"] | cat"
                "    if (o |> Seq.length) <> 2 then exit 4"
                "[\"q\"] |> Seq.iter (fun s ->"
                "    let o = [s] | cat"
                "    if (o |> Seq.head) <> \"q\" then exit 5)"
            ]

            cleanIn "pin.weir" src "function, if and lambda bodies"
            Expect.equal (runFile src) 0 "and they run"
        }
        test "within serve is refused inside plan [D:plan-parallel-refusal]" {
            mustSayIn
                "pin.weir"
                [
                    "let route = fun rq ->"
                    "    File.write \"out.txt\" [\"x\"]"
                    "    Reply.text 200 \"ok\""
                    "let p ="
                    "    plan"
                    "        within serve srv handler=route"
                    "            print $\"{Server.port srv}\""
                    "print $\"{Plan.isEmpty p}\""
                ]
                "'within serve' is refused inside 'plan'"
                "a handler's writes would run for real"

            mustSayIn
                "pin.weir"
                [
                    "let route = fun rq -> Reply.text 200 \"ok\""
                    "let once () ="
                    "    within serve srv handler=route"
                    "        print $\"{Server.port srv}\""
                    "let p ="
                    "    plan"
                    "        once ()"
                    "print $\"{Plan.isEmpty p}\""
                ]
                "'once' cannot run inside 'plan'"
                "through a helper too"
        }
        test "a class range ending in an astral code point is that range [D:regex-code-points]" {
            Expect.equal
                (runFile [
                    "let m pat s = Str.isMatch pat s"
                    "if not (m @\"^[a-😀]$\" \"b\") then exit 3"
                    "if not (m @\"^[a-😀]$\" \"😀\") then exit 4"
                    "if m @\"^[a-😀]$\" \"😁\" then exit 5"
                    "if m @\"^[a-😀]$\" \"-\" then exit 6"
                    "if m @\"^[^a-😀]$\" \"b\" then exit 7"
                    "if not (m @\"^[^a-😀]$\" \"😁\") then exit 8"
                    "if not (m @\"^[😀-😂]$\" \"😁\") then exit 9"
                    "if m @\"^[😀-😂]$\" \"😃\" then exit 10"
                    "if not (m @\"^[😀-🧿x]+$\" \"x🤔😀🧿\") then exit 11"
                    "if m @\"^[😀-🧿x]+$\" \"x🪀\" then exit 12"
                    "if (Str.rreplace @\"[😀😁-[😀]]\" \"x\" \"😀😁\") <> \"😀x\" then exit 13"
                    "if m @\"^[a-z😀-[aeiou]]+$\" \"ba\" then exit 14"
                    "if (m @\"^[😂-😀]$\" \"😁\" |> try) <> None then exit 15"
                    "if m @\"^[\\uFFFD]$\" \"😀\" then exit 16"
                    "if m @\"^[\\uE000-\\uFFFF]$\" \"😀\" then exit 17"
                    "if m @\"^[0-\\uFFFD]$\" \"😀\" then exit 18"
                    "if not (m @\"^[^0-\\uFFFD]$\" \"😀\") then exit 19"
                    "if not (m @\"^[\\S]$\" \"😀\") then exit 20"
                    "if m @\"^[^\\S]$\" \"😀\" then exit 21"
                ])
                0
                "ranges, negation and subtraction with astral members; a class of U+FFFD takes no emoji"
        }
        test "inferShape drafts declarations that check and read the sample back [D:repl-infer]" {
            let draft (sample: string) =
                let node =
                    match Weir.Infer.nodeOf Weir.Infer.Json [ sample ] with
                    | Ok n -> n
                    | Error e -> failtest e

                Weir.Infer.inferDecls Weir.Parser.keywords Set.empty "Root" node
                |> fst
                |> String.concat "\n"

            // the top record keeps its name when a key capitalises to it
            let d1 = draft "{\"root\": {\"id\": 1}}"
            Expect.stringContains d1 "type Root = {\n    root: RootRoot\n}" "the envelope is Root"
            Expect.stringContains d1 "type RootRoot = {\n    id: int\n}" "the nested object lands elsewhere"

            // no name is handed out twice
            let d2 =
                draft "{\"spec\": {\"a\": 1}, \"pod\": {\"spec\": {\"b\": 1}}, \"podSpec\": {\"c\": 1}}"

            let names =
                d2.Split('\n')
                |> Array.filter (fun l -> l.StartsWith "type ")
                |> Array.map (fun l -> l.Split(' ')[1])

            Expect.equal (Array.distinct names).Length names.Length $"distinct type names: {names}"

            // a null beside real elements drafts Option; a repeated key is one field
            let d3 =
                draft "{\"xs\": [1, null], \"ys\": [{\"b\": 1}, null], \"a\": 1, \"a\": \"x\"}"

            Expect.stringContains d3 "xs: seq<Option<int>>" "a null int element"
            Expect.stringContains d3 "ys: seq<Option<Y>>" "a null object element"
            Expect.stringContains d3 "a: string" "last wins"
            Expect.isFalse (d3.Contains "a: int") "one field per key"

            // a key with a non-ASCII digit is not a field name
            Expect.stringContains (draft "{\"a٣\": 2, \"b\": 1, \"c\": \"x\"}") "[<Wire \"a٣\">]" "it rides a wire key"

            for d in [ d1; d2; d3 ] do
                cleanIn "pin.weir" (d.Split('\n') |> List.ofArray) "the draft checks"
        }
        test "hunt 4 lows: closed cons chains, a leading joiner, prefixed xml names, endless walks, a hand-edited lock" {
            // `x :: []` is `[x]` to the coverage check [D:seq-patterns]
            cleanIn
                "pin.weir"
                [
                    "let f xs = match xs with | [] -> 0 | x :: [] -> x | x :: y :: _ -> x + y"
                    "print (f [1; 2; 3])"
                ]
                "a chain closed by [] is a fixed length"

            // a joiner with nothing before it joins nothing [D:str-width]
            Expect.equal
                (runFile [
                    "if Str.width \"\\u{200D}👨\" <> 2 then exit 3"
                    "if Str.width \"👨\\u{200D}👩\" <> 2 then exit 4"
                ])
                0
                "a leading ZWJ does not swallow the emoji after it"

            // [<Attr>]/[<Elem>] match by local name [D:from-xml]
            mustSayIn
                "pin.weir"
                [ "type D = { [<Attr \"xml:lang\">] a: Option<string> }"; "print \"x\"" ]
                "takes the attribute's local name"
                "a prefixed attribute name could never match"

            mustSayIn
                "pin.weir"
                [ "type D = { [<Elem \"x:item\">] e: seq<string> }"; "print \"x\"" ]
                "takes the element's local name"
                "a prefixed element name could never match"

            // the walks' budget is charged at discovery [D:structural-walk]
            Expect.equal
                (runFile [
                    "let endless n = Seq.initInfinite (fun i -> i + n + 1)"
                    "let r = Graph.reach show endless 0 |> Seq.length |> try result"
                    "match r with"
                    "| Error m when Str.contains \"exceeded 100000 steps\" m -> ()"
                    "| _ -> exit 3"
                    "if (Graph.reach show (fun n -> if n < 99999 then [n + 1] else []) 0 |> Seq.length) <> 100000 then exit 4"
                ])
                0
                "an endless child seq raises the budget error; 100000 nodes still walk"

            // a hand-edited lock is refused in weir's words [D:lockfile-confinement]
            withTempDir "weir-h4lock" (fun d ->
                let w = Path.Combine(d, ".weir")
                Directory.CreateDirectory w |> ignore

                let refusal (json: string) =
                    File.WriteAllText(Path.Combine(w, "lock.json"), json)

                    match Weir.Contracts.readLock w with
                    | Error e -> e
                    | Ok _ -> "accepted"

                Expect.stringContains
                    (refusal "{\"schemaVersion\":1.5,\"artifacts\":[]}")
                    "must be a whole number"
                    "a fractional version"

                Expect.stringContains
                    (refusal
                        "{\"schemaVersion\":1,\"artifacts\":[{\"kind\":\"schema\",\"name\":\"s\",\"url\":\"https://x/s.json\",\"sha256\":\"00\",\"path\":\"schemas/s.json\\u0000x\"}]}")
                    "holds a NUL character"
                    "a NUL in a path"

                let z = System.String(char 48, 64)

                Expect.stringContains
                    (refusal (
                        "{\"schemaVersion\":1,\"artifacts\":[{\"kind\":\"module\",\"name\":\"m\",\"url\":\"https://x/m.weir\",\"sha256\":\""
                        + z
                        + "\",\"path\":\"modules/m.weir\",\"files\":[{\"path\":\"x\\u0000y\",\"sha256\":\""
                        + z
                        + "\"}]}]}"
                    ))
                    "holds a NUL character"
                    "a NUL in a closure file's path"

                // verify spawns no file a lock names by path [D:verify-probe-path-only]
                let sigs = Path.Combine(w, "sigs")
                Directory.CreateDirectory sigs |> ignore
                File.WriteAllText(Path.Combine(sigs, "t.weir"), "x")

                let sha =
                    System.Convert
                        .ToHexString(
                            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes "x")
                        )
                        .ToLowerInvariant()

                File.WriteAllText(
                    Path.Combine(w, "lock.json"),
                    "{\"schemaVersion\":1,\"artifacts\":[{\"kind\":\"sig\",\"name\":\"tools/setup.sh\",\"url\":\"probe\",\"sha256\":\""
                    + sha
                    + "\",\"path\":\"sigs/t.weir\",\"version\":\"1\"}]}"
                )

                match Weir.Contracts.verify w false with
                | Ok(lines, findings) ->
                    Expect.equal (List.length findings) 0 "the hash arm still holds"
                    Expect.stringContains (String.concat "\n" lines) "version not probed" "the line says so"
                | Error e -> failtest e

                // asked, it runs the file from the project root [D:verify-probe-paths]
                if not (System.OperatingSystem.IsWindows()) then
                    let tool = Path.Combine(Path.GetDirectoryName w, "tools", "setup.sh")
                    Directory.CreateDirectory(Path.GetDirectoryName tool) |> ignore

                    let probed () =
                        match Weir.Contracts.verify w true with
                        | Ok(lines, findings) -> String.concat "\n" lines, List.length findings
                        | Error e -> failtest e

                    let answer (v: string) =
                        File.WriteAllText(tool, "#!/bin/sh\necho " + v + "\n")

                        File.SetUnixFileMode(
                            tool,
                            System.IO.UnixFileMode.UserRead
                            ||| System.IO.UnixFileMode.UserWrite
                            ||| System.IO.UnixFileMode.UserExecute
                        )

                        probed ()

                    Expect.equal (answer "1") ("sig tools/setup.sh: ok (hash + version)", 0) "the recorded version"
                    Expect.stringContains (fst (answer "2")) "VERSION MISMATCH" "a drifted tool"
                    File.Delete tool
                    Expect.stringContains (fst (probed ())) "TOOL MISSING" "no file there")

            let forged =
                Weir.Can.renderHuman "a\nb.weir" [
                    {
                        Weir.Can.Fact = Weir.Can.Runs "git"
                        Weir.Can.Site = {
                            Weir.Can.File = "m\n  runs  rm.weir"
                            Weir.Can.Line = 1
                            Weir.Can.Col = 1
                        }
                    }
                ]

            Expect.isFalse (forged.Contains "m\n  runs  rm.weir") "a newline in a site's file forges no line"

            Expect.isFalse (forged.StartsWith "a\nb") "nor in the header"
        }

        test "lock audit: a FIFO is no artifact, a refused sig write moves nothing, a long arrow renders in line" {
            if System.OperatingSystem.IsLinux() then
                withTempDir "weir-lockaudit2" (fun d ->
                    let w = Path.Combine(d, ".weir")
                    Directory.CreateDirectory(Path.Combine(w, "sigs")) |> ignore
                    let fifo = Path.Combine(w, "sigs", "t.weir")
                    use mk = System.Diagnostics.Process.Start("mkfifo", [| fifo |])
                    mk.WaitForExit()
                    Expect.isTrue (Weir.Contracts.pipeLike fifo) "a FIFO"
                    Expect.isFalse (Weir.Contracts.pipeLike (Path.Combine(w, "absent"))) "an absent path"

                    Expect.throws
                        (fun () -> Weir.Contracts.readArtifact fifo |> ignore)
                        "the read refuses, never waits"

                    let z = System.String(char 48, 64)

                    File.WriteAllText(
                        Path.Combine(w, "lock.json"),
                        "{\"schemaVersion\":1,\"artifacts\":[{\"kind\":\"sig\",\"name\":\"t\",\"url\":\"generated:help\",\"sha256\":\""
                        + z
                        + "\",\"path\":\"sigs/t.weir\"}]}"
                    )

                    match Weir.Contracts.verify w false with
                    | Ok(lines, findings) ->
                        Expect.equal (List.length findings) 1 "one finding"
                        Expect.stringContains (String.concat "\n" lines) "MODIFIED" "a FIFO compares unequal"
                    | Error e -> failtest e

                    let real = Path.Combine(d, "real.weir")
                    File.WriteAllText(real, "x")
                    let link = Path.Combine(w, "sigs", "l.weir")
                    File.CreateSymbolicLink(link, real) |> ignore
                    Expect.isError (Weir.Contracts.confinedLeaf w link) "a linked leaf, nothing written"
                    Expect.isOk (Weir.Contracts.confinedLeaf w (Path.Combine(w, "sigs", "n.weir"))) "a new leaf")

            let chain =
                List.fold (fun t _ -> Weir.Types.TFun(Weir.Types.TInt, t)) Weir.Types.TInt [ 1..50000 ]

            Expect.equal (Weir.Types.formatTy chain).Length (3 + 50000 * 7) "every arrow, once"

            Expect.equal
                (Weir.Types.formatTy (
                    Weir.Types.TFun(
                        Weir.Types.TFun(Weir.Types.TInt, Weir.Types.TStr),
                        Weir.Types.TTuple [
                            Weir.Types.TSeq Weir.Types.TInt
                            Weir.Types.TNamed(
                                "Map",
                                [ Weir.Types.TStr; Weir.Types.TRowVar("r", [ "a", Weir.Types.TInt ]) ]
                            )
                        ]
                    )
                ))
                "(int -> string) -> seq<int> * Map<string, { a: int; .. }>"
                "the spelling is unchanged"

            // a function type takes 256 arrows and no more [D:arrow-chain-bound]
            let signed (arrows: int) =
                Weir.Parser.parseLine cmdResolver ("let f : int" + String.replicate arrows " -> int")

            match signed 257 with
            | Error msg -> Expect.stringContains msg "bound is 256" "the refusal names the bound"
            | Ok _ -> failtest "257 arrows parsed"

            match signed 256 with
            | Error msg -> failtest msg
            | Ok _ -> ()
        }

        test "lock audit: spawns take PATH's file, a token keeps to its host, the lock is a file of its own" {
            if not (System.OperatingSystem.IsWindows()) then
                withTempDir "weir-lockaudit" (fun d ->
                    let bin = Path.Combine(d, "bin")
                    let repo = Path.Combine(d, "repo")
                    Directory.CreateDirectory bin |> ignore
                    Directory.CreateDirectory repo |> ignore

                    let plant (dir: string) (name: string) =
                        let p = Path.Combine(dir, name)
                        File.WriteAllText(p, "#!/bin/sh\n")

                        File.SetUnixFileMode(
                            p,
                            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                        )

                        p

                    let onPath = plant bin "weirlocated"
                    plant repo "weirplanted" |> ignore
                    let savedPath = System.Environment.GetEnvironmentVariable "PATH"
                    let savedCwd = Directory.GetCurrentDirectory()

                    try
                        Directory.SetCurrentDirectory repo
                        // a relative and an empty entry: the working directory, to a shell
                        System.Environment.SetEnvironmentVariable("PATH", ".:" + ":" + bin)
                        Expect.equal (Weir.Contracts.locateTool "weirlocated") (Some onPath) "PATH's file"
                        Expect.equal (Weir.Contracts.locateTool "weirplanted") None "never the working directory's"
                        Expect.equal (Weir.Contracts.locateTool "./weirplanted") None "a path is not a tool name"
                        Expect.equal (Weir.Contracts.locateTool "..") None "nor a dot name"
                    finally
                        Directory.SetCurrentDirectory savedCwd
                        System.Environment.SetEnvironmentVariable("PATH", savedPath)

                    // a linked lock is refused, never read or rewritten through
                    let w = Path.Combine(repo, ".weir")
                    Directory.CreateDirectory w |> ignore
                    let real = Path.Combine(d, "elsewhere.json")
                    File.WriteAllText(real, "{\"schemaVersion\":1,\"artifacts\":[]}")
                    File.CreateSymbolicLink(Path.Combine(w, "lock.json"), real) |> ignore

                    match Weir.Contracts.readLock w with
                    | Error e -> Expect.stringContains e "symbolic link" "the refusal names the link"
                    | Ok _ -> failtest "a linked lock was read"

                    // a linked .weir would move the confinement root with it
                    let repo2 = Path.Combine(d, "repo2")
                    Directory.CreateDirectory repo2 |> ignore
                    let w2 = Path.Combine(repo2, ".weir")
                    Directory.CreateSymbolicLink(w2, repo2) |> ignore
                    File.WriteAllText(Path.Combine(repo2, "lock.json"), "{\"schemaVersion\":1,\"artifacts\":[]}")

                    match Weir.Contracts.readLock w2 with
                    | Error e -> Expect.stringContains e "vendor directory is a directory of its own" "a linked root"
                    | Ok _ -> failtest "a lock was read through a linked .weir"

                    match Weir.Contracts.confineRealUnder w2 (Path.Combine(w2, "x")) with
                    | Error e -> Expect.stringContains e "symbolic link" "nothing is confined under a linked root"
                    | Ok _ -> failtest "a write was confined under a linked .weir"

                    // a closure file is fetched under its entry's url, or not at all
                    let w3 = Path.Combine(d, "repo3", ".weir")
                    Directory.CreateDirectory w3 |> ignore
                    let z = System.String(char 48, 64)

                    File.WriteAllText(
                        Path.Combine(w3, "lock.json"),
                        "{\"schemaVersion\":2,\"artifacts\":[{\"kind\":\"module\",\"name\":\"m\",\"url\":\"https://host.example.test\",\"sha256\":\""
                        + z
                        + "\",\"path\":\"modules/m\",\"entry\":\".evil.example.test/m.weir\",\"files\":[{\"path\":\".evil.example.test/m.weir\",\"sha256\":\""
                        + z
                        + "\"}]}]}"
                    )

                    match Weir.Contracts.restore w3 with
                    | Error e -> Expect.stringContains e "leaves the entry's url" "the fetch never starts"
                    | Ok lines -> failtest $"restored: {lines}")

            Expect.notEqual
                (Weir.Contracts.hostTokenVar "gitlab-acme.com")
                (Weir.Contracts.hostTokenVar "gitlab.acme.com")
                "a hyphen and a dot name different variables"

            let var = Weir.Contracts.hostTokenVar "modules.example.test"
            let saved = System.Environment.GetEnvironmentVariable var

            try
                System.Environment.SetEnvironmentVariable(var, "t1")

                let auth (url: string) =
                    fst (Weir.Contracts.moduleFetchHeaders url)
                    |> List.filter (fun (k, _) -> k <> "User-Agent")

                Expect.equal (auth "http://modules.example.test/m.weir") [] "no token over cleartext"

                Expect.equal
                    (auth "https://modules.example.test/m.weir")
                    [ "Authorization", "token t1" ]
                    "https carries it"
            finally
                System.Environment.SetEnvironmentVariable(var, saved)
        }
    ]
