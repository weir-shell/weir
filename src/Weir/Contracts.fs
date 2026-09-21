module Weir.Contracts

// External contracts [D:contracts-spine]: vendored, pinned artifacts
// that constrain what the CHECKER accepts and contribute NOTHING at
// run time. Four properties, each load-bearing: vendored (checked in
// under .weir/, never fetched during check), pinned (exact identity,
// no ranges — pairwise comparisons, not a dependency graph), check-time
// only (deleting every contract leaves every script running
// identically), declared not discovered (a .weir/ directory's mere
// existence never changes how a file checks).
//
// The first customer is YAML schemas [D:yaml-schemas]; command
// signatures and remote modules inherit this spine.

open System
open System.IO
open System.Runtime.InteropServices
open Weir.Ast
open Weir.Types

// ---- the path/name boundary [D:lockfile-confinement] -----------------------
// A path that came from OUTSIDE (a lock entry, an `--as` name) is joined
// with a base and then used; a hostile or fat-fingered value must not
// reach a write/read outside `.weir/`. Two guards, ONE copy each — the
// confining join (Path.under's lexical core, Session-free so it lives
// here, before Builtins) and the plain-name validator (the rule
// `add module` had inline, extracted so `add schema`/`sig`/`gen types`
// share it).

/// absolute/rooted on ANY platform — refused by shape, the safe
/// direction (a script must confine identically on Linux and Windows).
/// Mirrors Builtins.absoluteShaped [D:path-under]; kept here so the lock
/// read (before Builtins) can confine.
let private absoluteShapedPath (p: string) : bool =
    p.StartsWith "/"
    || p.StartsWith "\\"
    || (p.Length >= 2 && System.Char.IsLetter p[0] && p[1] = ':')

/// the CONFINING join, Path.under's lexical core [D:path-under]: join
/// `rel` under an ALREADY-RESOLVED `root` and return the confined
/// absolute path, or Error when `rel` escapes (absolute, or `..` past the
/// base). Purely textual — GetFullPath never touches disk, so a symlink
/// out is textually under (the same bound Path.under states). `root` is
/// resolved by the caller (weirDir is already absolute).
let confineUnder (root: string) (rel: string) : Result<string, string> =
    if absoluteShapedPath rel then
        Error $"'{rel}' is an absolute path — it must be a relative path under the base"
    else
        let baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
        let joined = Path.GetFullPath(Path.Combine(baseDir, rel))
        let sep = string Path.DirectorySeparatorChar
        let prefix = if baseDir.EndsWith sep then baseDir else baseDir + sep

        // SEGMENT-WISE, never prefix-string-wise (`/safe/uploads-evil`
        // starts with `/safe/uploads` and is NOT under it)
        if joined = baseDir || joined.StartsWith prefix then
            Ok joined
        else
            Error $"'{rel}' escapes the base — it must stay under it"

// ---- symlink-resolving confinement [D:lockfile-symlink-confinement] ---------
// confineUnder is purely LEXICAL — GetFullPath normalizes `..` without
// touching disk, so a symlink OUT (an intermediate dir like `.weir/schemas`
// → outside, or a final component that is itself a link) escapes a
// lexically-clean path. This layer resolves the REAL filesystem object and
// requires it under the REAL root, closing DA-04.

/// segment-wise containment of an ALREADY-RESOLVED path under an
/// ALREADY-RESOLVED root — the same rule confineUnder's textual check uses
/// (`/safe/uploads-evil` is not under `/safe/uploads`), applied to real
/// paths. Both arguments are absolute; the empty tail (path == root) is in.
let private underResolved (realRoot: string) (realPath: string) : bool =
    let root = Path.TrimEndingDirectorySeparator realRoot
    let sep = string Path.DirectorySeparatorChar
    let prefix = if root.EndsWith sep then root else root + sep
    realPath = root || realPath.StartsWith prefix

/// the REAL path of `p` when it exists (symlinks resolved to their final
/// target), or None when it does not. Uses .NET link resolution — a
/// non-link returns itself, a link chases to the final target; a broken
/// link resolves to a non-existent target and reads as None here.
let private realPathOf (p: string) : string option =
    try
        if Directory.Exists p then
            let info = DirectoryInfo p
            match info.ResolveLinkTarget true with
            | null -> Some(Path.TrimEndingDirectorySeparator info.FullName)
            | tgt -> Some(Path.TrimEndingDirectorySeparator tgt.FullName)
        elif File.Exists p then
            let info = FileInfo p
            match info.ResolveLinkTarget true with
            | null -> Some info.FullName
            | tgt -> Some tgt.FullName
        else
            None
    with _ ->
        None

/// resolve `path`'s symlinks up to its DEEPEST EXISTING ancestor, then
/// re-append the not-yet-existing lexical tail. Nothing on disk is created.
/// Returns the real absolute path a write/read would actually land on, or
/// Error when an existing ancestor cannot be resolved. `path` is absolute
/// and lexically normalized (confineUnder's output).
let private resolveExistingPrefix (path: string) : Result<string, string> =
    let rec walk (dir: string) (tail: string list) : Result<string, string> =
        match realPathOf dir with
        | Some real -> Ok(List.fold (fun acc seg -> Path.Combine(acc, seg)) real tail)
        | None ->
            match Directory.GetParent dir with
            | null ->
                // the filesystem root itself does not resolve — a hostile
                // shape rather than a real tree; refuse in the safe direction
                Error $"'{path}': its filesystem root does not resolve"
            | parent -> walk parent.FullName (Path.GetFileName dir :: tail)

    walk (Path.TrimEndingDirectorySeparator path) []

/// the REAL-PATH confinement [D:lockfile-symlink-confinement]: `dest` is
/// already lexically confined under `root` (confineUnder ran), but a
/// symlink on the way — an intermediate dir OR the final component — can
/// still redirect the actual object OUTSIDE. Resolve BOTH the root and the
/// destination's existing prefix to their real paths and require the
/// destination under the root. The final component is checked too: if it
/// exists as a link out, realPathOf chases it and the check fails.
let confineRealUnder (root: string) (dest: string) : Result<string, string> =
    // resolve BOTH sides through their existing prefixes: a not-yet-created
    // root (the add fallback, a fresh tree) resolves to its lexical self —
    // there is no symlink to follow when nothing exists, so confinement
    // matches the lexical result until a real symlinked component appears.
    match resolveExistingPrefix root, resolveExistingPrefix dest with
    | Error e, _
    | _, Error e -> Error e
    | Ok realRoot, Ok realDest ->
        if underResolved realRoot realDest then
            Ok dest
        else
            Error $"'{dest}' resolves through a symlink to outside .weir/ — the real object escapes the vendor directory"

/// POSIX open(2) with O_NOFOLLOW on the FINAL component, the TOCTOU-tight
/// write [D:lockfile-symlink-confinement]: the parent is realpath-verified
/// under root by the caller, and O_NOFOLLOW makes the kernel REFUSE if the
/// final name is a symlink — so the check and the open refer to the same
/// object with no replacement window on the leaf. Unix only; the Windows
/// path falls back to the realpath preflight (its residual window is
/// stated in the ledger).
module private Posix =
    [<Literal>]
    let private O_WRONLY = 0x0001

    [<Literal>]
    let private O_CREAT = 0x0040

    [<Literal>]
    let private O_TRUNC = 0x0200

    [<Literal>]
    let private O_NOFOLLOW = 0x20000 // Linux value; macOS is 0x0100

    [<Literal>]
    let private O_NOFOLLOW_BSD = 0x0100

    [<DllImport("libc", SetLastError = true)>]
    extern int private ``open``(string pathname, int flags, int mode)

    // set the mode on the ALREADY-OPEN fd [D:lockfile-symlink-confinement]: libc
    // open(2) is VARIADIC (mode_t is a `...` arg), and on ARM64 macOS a
    // variadic trailing arg travels on the STACK — the fixed-signature
    // `open` P/Invoke above passes mode positionally, supplying garbage
    // there, so the leaf lands with a wrong/zero mode and a later
    // File.ReadAllBytes hits Permission denied. fchmod is NON-variadic
    // (two fixed args), so its P/Invoke is register-exact on every ABI;
    // applied to the fd already held it re-follows nothing — the
    // O_NOFOLLOW leaf confinement stands.
    [<DllImport("libc", SetLastError = true)>]
    extern int private fchmod(int fd, int mode)

    [<DllImport("libc", SetLastError = true)>]
    extern int private close(int fd)

    let private noFollowFlag () =
        if OperatingSystem.IsMacOS() then O_NOFOLLOW_BSD else O_NOFOLLOW

    /// write bytes creating/truncating the final component, REFUSING to
    /// follow a symlinked leaf (ELOOP). Returns Error on any libc failure,
    /// naming the leaf — the caller turns that into the located refusal.
    let writeNoFollow (path: string) (bytes: byte[]) : Result<unit, string> =
        let fd =
            ``open`` (path, O_WRONLY ||| O_CREAT ||| O_TRUNC ||| noFollowFlag (), 0o644)

        if fd < 0 then
            let err = Marshal.GetLastWin32Error()
            // ELOOP (40 on Linux, 62 on macOS) is the symlinked-leaf refusal
            Error $"cannot open '{path}' without following symlinks (errno {err})"
        else
            // NAIL the mode on the open fd before writing [D:lockfile-symlink-confinement]:
            // the variadic-open mode arg is unreliable on ARM64 macOS, so the
            // 0o644 passed above may not have taken — fchmod on the fd we hold
            // makes the leaf 0o644 regardless (no re-follow; the O_NOFOLLOW
            // confinement already fixed which object this fd names).
            fchmod (fd, 0o644) |> ignore

            try
                use fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(nativeint fd, true), FileAccess.Write)
                fs.Write(bytes, 0, bytes.Length)
                Ok()
            with ex ->
                Error $"cannot write '{path}': {ex.Message}"

/// write `bytes` to `dest`, which is confined under `root`
/// [D:lockfile-symlink-confinement]. The parent directory is realpath-
/// verified under root; on Unix the leaf is opened O_NOFOLLOW so a
/// symlinked final component is refused by the kernel at open time (check
/// and write name the same object). On Windows the realpath preflight
/// stands alone (residual window stated in the ledger).
let writeConfined (root: string) (dest: string) (bytes: byte[]) : Result<unit, string> =
    let parent = Path.GetDirectoryName dest

    // the PARENT must resolve under root — an intermediate symlink out is
    // caught here even when the leaf does not yet exist
    match confineRealUnder root parent with
    | Error e -> Error e
    | Ok _ ->
        // and the leaf, if it already exists as a link out, is caught too
        match confineRealUnder root dest with
        | Error e -> Error e
        | Ok _ ->
            try
                Directory.CreateDirectory parent |> ignore
            with _ ->
                ()

            if OperatingSystem.IsWindows() then
                try
                    File.WriteAllBytes(dest, bytes)
                    Ok()
                with ex ->
                    Error $"cannot write '{dest}': {ex.Message}"
            else
                Posix.writeNoFollow dest bytes

/// a vendored name safe as a FILE-NAME segment [D:lockfile-confinement]:
/// the F14 guard at the argv crossing — no separators, no `..`, no
/// leading dot, no absolute shape, so `--as` can only ever name a file
/// directly inside its kind's directory. Hyphens ARE allowed (a schema
/// name like `k8s-configmap` is legitimate); the stricter identifier
/// rule an import ALIAS needs is `plainName`, layered on top by
/// `add module`. This is the confinement floor every `--as` shares; the
/// lock-read confinement is the defence in depth behind it.
let vendorNameSafe (name: string) : bool =
    name.Length > 0
    && not (absoluteShapedPath name)
    && not (name.Contains "..")
    && not (name.StartsWith ".")
    && name |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_' || c = '-' || c = '.')

/// a plain NAME that can also be an import ALIAS: a letter, then
/// letters/digits/_ — the rule `add module` enforces (its name becomes a
/// module alias, so no hyphen, no dot). Stricter than vendorNameSafe.
let plainName (name: string) : bool =
    name.Length > 0
    && System.Char.IsLetter name[0]
    && name |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c || c = '_')

// ---- discovery -------------------------------------------------------------

/// walk UP from `fromDir` to the first `.weir/`; stop there. Bounded by
/// a `.git` (dir or file — worktrees) and the filesystem root. The
/// error names both what was looked for and where the walk stopped.
let findWeirDir (fromDir: string) : Result<string, string> =
    let rec walk (dir: string) =
        let candidate = Path.Combine(dir, ".weir")

        if Directory.Exists candidate then
            Ok candidate
        elif
            Directory.Exists(Path.Combine(dir, ".git"))
            || File.Exists(Path.Combine(dir, ".git"))
        then
            Error $"no .weir directory between {fromDir} and the repo root {dir}"
        else
            match Directory.GetParent dir with
            | null -> Error $"no .weir directory between {fromDir} and the filesystem root"
            | parent -> walk parent.FullName

    walk (Path.GetFullPath fromDir)

// ---- the lockfile ----------------------------------------------------------

// per artifact: kind, name, source url, sha256 of the file bytes, and
// the path relative to .weir/. THE LOCKFILE IS THE MANIFEST (a
// deliberate choice — no ranges means nothing for a separate manifest
// to hold): `weir add` writes it, `weir restore` re-materializes from
// it, `weir verify` checks it. Absent until the first add.
type LockEntry =
    { Kind: string
      Name: string
      // for a GENERATED artifact (a signature) there is no URL: the
      // slot records the generation source instead ("generated:help",
      // "generated:completion-fish", …) [D:command-signatures] — the
      // ninth ruling's edge: the lock is still the record of intent,
      // and the intent of a generated entry is "this signature
      // describes the tool I had"
      Url: string
      Sha256: string
      Path: string
      // sig entries only: the tool's VERBATIM --version output at
      // generation time — denormalized from the file (hash-protected,
      // so they cannot drift apart) so verify needs no weir parser
      Version: string option }

/// the CONFINED dest for a lock entry [D:lockfile-confinement] — every
/// consumer (restore/verify/gen types) resolves an entry's Path through
/// THIS, PER ENTRY: a hostile path (absolute, `..`) is a located refusal
/// naming the entry, never a write/read outside `.weir/`. Per-entry, not
/// whole-lock: a tampered entry does not strand the benign siblings — the
/// hostile one refuses, the rest proceed.
let entryDest (weirDir: string) (e: LockEntry) : Result<string, string> =
    // TWO gates [D:lockfile-symlink-confinement]: the lexical confineUnder
    // (an absolute/`..` path), THEN the real-path check (a symlinked
    // intermediate dir or final component that redirects the actual object
    // outside .weir/ — DA-04). Both refuse PER ENTRY, naming the entry, so
    // a benign sibling still restores.
    let tampered () =
        $"{e.Kind} {e.Name}: the lock records path '{e.Path}', which escapes .weir/ — a lock entry must stay under the vendor directory; this lock was tampered with or hand-edited"

    match confineUnder weirDir e.Path with
    | Error _ -> Error(tampered ())
    | Ok dest ->
        match confineRealUnder weirDir dest with
        | Ok _ -> Ok dest
        | Error _ -> Error(tampered ())

let sha256Hex (bytes: byte[]) : string =
    use sha = Security.Cryptography.SHA256.Create()

    sha.ComputeHash bytes
    |> Array.map (fun b -> b.ToString "x2")
    |> String.concat ""

let private lockPath (weirDir: string) = Path.Combine(weirDir, "lock.json")

let readLock (weirDir: string) : Result<LockEntry list, string> =
    let p = lockPath weirDir

    if not (File.Exists p) then
        Ok [] // absent until the first fetch — nothing vendored yet
    else
        try
            use doc = Text.Json.JsonDocument.Parse(File.ReadAllText p)

            let ver =
                match doc.RootElement.TryGetProperty "schemaVersion" with
                | true, v -> v.GetInt32()
                | _ -> 1 // pre-field locks are version 1

            if ver > 1 then
                failwith $"lock schemaVersion {ver} is newer than this weir understands — upgrade weir"

            doc.RootElement.GetProperty("artifacts").EnumerateArray()
            |> Seq.map (fun e ->
                { Kind = e.GetProperty("kind").GetString()
                  Name = e.GetProperty("name").GetString()
                  Url = e.GetProperty("url").GetString()
                  Sha256 = e.GetProperty("sha256").GetString()
                  Path = e.GetProperty("path").GetString()
                  Version =
                    match e.TryGetProperty "version" with
                    | true, v -> Some(v.GetString())
                    | _ -> None })
            |> List.ofSeq
            |> Ok
        with ex ->
            Error $"{p}: cannot read the lockfile: {ex.Message}"

let writeLock (weirDir: string) (entries: LockEntry list) : unit =
    use ms = new MemoryStream()

    (use w =
        new Text.Json.Utf8JsonWriter(ms, Text.Json.JsonWriterOptions(Indented = true))

     w.WriteStartObject()
     w.WriteNumber("schemaVersion", 1)
     w.WriteStartArray "artifacts"

     for e in entries do
         w.WriteStartObject()
         w.WriteString("kind", e.Kind)
         w.WriteString("name", e.Name)
         w.WriteString("url", e.Url)
         w.WriteString("sha256", e.Sha256)
         w.WriteString("path", e.Path)

         match e.Version with
         | Some v -> w.WriteString("version", v)
         | None -> ()

         w.WriteEndObject()

     w.WriteEndArray()
     w.WriteEndObject())

    File.WriteAllBytes(lockPath weirDir, ms.ToArray())

// ---- fetch (ruling 4: each failure mode its own message) -------------------

let fetchBytesWith (headers: (string * string) list) (url: string) : Result<byte[] * string option, string> =
    try
        use client = new Net.Http.HttpClient()
        client.Timeout <- TimeSpan.FromSeconds 60.0

        for k, v in headers do
            client.DefaultRequestHeaders.TryAddWithoutValidation(k, v) |> ignore

        use resp = client.GetAsync(url).Result

        if not resp.IsSuccessStatusCode then
            Error $"{url} answered {int resp.StatusCode} ({resp.StatusCode})"
        else
            let ct =
                match resp.Content.Headers.ContentType with
                | null -> None
                | t -> Some t.MediaType

            Ok(resp.Content.ReadAsByteArrayAsync().Result, ct)
    with ex ->
        let host, port =
            try
                let u = Uri(url)
                u.Host, u.Port
            with _ ->
                url, 0

        // the shared transport classifier [D:transport-words] — 60s is
        // this fetch's own client timeout above
        Error(Http.transportMessage host (Http.classifyTransport 60000 port ex))

let fetchBytes (url: string) : Result<byte[] * string option, string> = fetchBytesWith [] url

/// the single most likely user error for `add schema <url>` is a
/// GitHub/GitLab FILE PAGE where the raw URL was meant — recognize the
/// host and OFFER the rewritten raw URL, because the fix is a URL edit
/// the user may not know how to construct [D:add-validates]
let rawUrlHint (url: string) : string =
    let m =
        Text.RegularExpressions.Regex.Match(url, "^https://github\\.com/([^/]+)/([^/]+)/blob/(.+)$")

    if m.Success then
        $" — this is a GitHub file page; use the raw URL: https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{m.Groups[3].Value}"
    else
        let g =
            Text.RegularExpressions.Regex.Match(url, "^https://gitlab\\.com/(.+)/-/blob/(.+)$")

        if g.Success then
            $" — this is a GitLab file page; use the raw URL: https://gitlab.com/{g.Groups[1].Value}/-/raw/{g.Groups[2].Value}"
        else
            " — if this is a GitHub or GitLab file page, use the raw URL"

// ---- remote module sources [D:add-module] ----------------------------------
// The shorthand is CLI SUGAR ONLY — the lock never sees it: a plain URL
// plus content hash goes in, host-agnostic, so restore/verify stay
// generic. Host-first with the `//` repo/path separator REQUIRED on
// every host (GitLab nests groups, so the boundary must be spelled; a
// host-conditional parse is a guess). Tag in, FULL SHA stored. An
// explicit @ref is required — weir does not guess a default branch.

/// the env var a host's token is read from — never stored anywhere
let hostTokenVar (host: string) : string =
    "WEIR_TOKEN_" + host.ToUpperInvariant().Replace(".", "_").Replace("-", "_")

let private hostToken (host: string) : string option =
    match Environment.GetEnvironmentVariable(hostTokenVar host) with
    | null
    | "" -> None
    | t -> Some t

/// GitHub answers 404 (not 403) for private-without-auth — the teach
/// must fire on both, or every private repo reads as a typo
let hintPrivate (host: string) (e: string) : string =
    if e.Contains "answered 404" || e.Contains "answered 403" then
        e + $" — or the repo is private: set {hostTokenVar host}"
    else
        e

type ResolvedModuleSource =
    { Url: string
      Sha: string option
      FetchHeaders: (string * string) list
      Host: string option }

let private uaHeader = [ "User-Agent", "weir/" + Version.current ]

let private shaFrom (field: string) (bytes: byte[]) : string option =
    try
        use doc = Text.Json.JsonDocument.Parse bytes
        Some(doc.RootElement.GetProperty(field).GetString())
    with _ ->
        None

let resolveModuleSpec (spec: string) : Result<ResolvedModuleSource, string> =
    if spec.StartsWith "http://" || spec.StartsWith "https://" then
        // the explicit raw-URL form — any host, no expansion
        let host =
            try
                (Uri spec).Host
            with _ ->
                ""

        let hdrs =
            match hostToken host with
            | Some t -> [ "Authorization", "token " + t ]
            | None -> []

        Ok
            { Url = spec
              Sha = None
              FetchHeaders = uaHeader @ hdrs
              Host = (if host = "" then None else Some host) }
    else
        match spec.IndexOf "//" with
        | -1 ->
            Error
                "the shorthand needs the // repo/path separator — <host>/<org>/<repo>//<path>@<ref> (GitLab nests groups, so the boundary must be spelled); any host also takes the full raw URL"
        | i ->
            let left = spec.Substring(0, i)
            let right = spec.Substring(i + 2)

            match right.LastIndexOf "@" with
            | -1 ->
                Error "an explicit @ref is required — @main, @v1.2.0, or @<sha>; weir does not guess a default branch"
            | j ->
                let path = right.Substring(0, j)
                let refName = right.Substring(j + 1)

                if path = "" || refName = "" then
                    Error
                        "an explicit @ref is required — @main, @v1.2.0, or @<sha>; weir does not guess a default branch"
                else
                    match left.IndexOf "/" with
                    | -1 -> Error $"the shorthand is <host>/<org>/<repo>//<path>@<ref>; '{left}' has no repo part"
                    | k ->
                        let host = left.Substring(0, k)
                        let repo = left.Substring(k + 1)

                        match host with
                        | "github.com" ->
                            let apiAuth =
                                match hostToken host with
                                | Some t -> [ "Authorization", "Bearer " + t ]
                                | None -> []

                            let api = $"https://api.github.com/repos/{repo}/commits/{refName}"

                            (match
                                fetchBytesWith (uaHeader @ [ "Accept", "application/vnd.github+json" ] @ apiAuth) api
                             with
                             | Error e -> Error(hintPrivate host $"resolving @{refName}: {e}")
                             | Ok(bytes, _) ->
                                 match shaFrom "sha" bytes with
                                 | None -> Error $"unexpected answer resolving @{refName} at {api}"
                                 | Some sha ->
                                     let rawAuth =
                                         match hostToken host with
                                         | Some t -> [ "Authorization", "token " + t ]
                                         | None -> []

                                     Ok
                                         { Url = $"https://raw.githubusercontent.com/{repo}/{sha}/{path}"
                                           Sha = Some sha
                                           FetchHeaders = uaHeader @ rawAuth
                                           Host = Some host })
                        | "gitlab.com" ->
                            let auth =
                                match hostToken host with
                                | Some t -> [ "PRIVATE-TOKEN", t ]
                                | None -> []

                            let api =
                                $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString repo}/repository/commits/{Uri.EscapeDataString refName}"

                            (match fetchBytesWith (uaHeader @ auth) api with
                             | Error e -> Error(hintPrivate host $"resolving @{refName}: {e}")
                             | Ok(bytes, _) ->
                                 match shaFrom "id" bytes with
                                 | None -> Error $"unexpected answer resolving @{refName} at {api}"
                                 | Some sha ->
                                     Ok
                                         { Url = $"https://gitlab.com/{repo}/-/raw/{sha}/{path}"
                                           Sha = Some sha
                                           FetchHeaders = uaHeader @ auth
                                           Host = Some host })
                        | h ->
                            Error
                                $"unknown host '{h}' — the shorthand knows github.com and gitlab.com; any other host takes the full raw URL: weir add module https://… --as <name>"

/// the shared vendoring tail: write the artifact and upsert its lock
/// entry together, or neither. Returns (sha256, prior entry's sha) so
/// the caller can render added/updated — a re-add IS the update path,
/// and the sha change is the review signal.
let vendorFile
    (weirDir: string)
    (kind: string)
    (name: string)
    (rel: string)
    (url: string)
    (bytes: byte[])
    : Result<string * string option, string> =
    match readLock weirDir with
    | Error e -> Error e
    | Ok entries ->
        let prior =
            entries
            |> List.tryFind (fun e -> e.Kind = kind && e.Name = name)
            |> Option.map (fun e -> e.Sha256)

        let dest = Path.Combine(weirDir, rel)
        let hash = sha256Hex bytes

        let entry =
            { Kind = kind
              Name = name
              Url = url
              Sha256 = hash
              Path = rel
              Version = None }

        let others = entries |> List.filter (fun e -> not (e.Kind = kind && e.Name = name))

        // even a vendored kind directory (.weir/modules) can be a symlink
        // OUT [D:lockfile-symlink-confinement] — writeConfined realpath-
        // verifies the parent and opens the leaf O_NOFOLLOW
        match writeConfined weirDir dest bytes with
        | Error w -> Error w
        | Ok() ->
            try
                writeLock weirDir (others @ [ entry ])
                Ok(hash, prior)
            with ex ->
                (try
                    File.Delete dest
                 with _ ->
                     ())

                Error $"write failed: {ex.Message} — the partial file was removed"

// ---- the JSON Schema subset [D:yaml-schemas] -------------------------------

// corpus-measured (six real k8s standalone-strict schemas), then grown
// for the raw k8s OpenAPI shape [D:schema-types]: IN — type (string or
// array-of-strings, the nullable spelling), properties, required,
// items, additionalProperties (bool or schema), enum, oneOf RESTRICTED
// to scalar-type alternatives (every corpus occurrence is the
// IntOrString idiom), anyOf (all-scalar folds like oneOf; otherwise the
// alternatives are kept as SChoice — first-variant for the generator,
// unvalidated for the district), `nullable: true` (the OpenAPI 3.0
// spelling), allOf of ONE schema plus annotations (the k8s
// $ref-with-description idiom), and IN-DOCUMENT $ref
// (#/definitions/<name>, #/$defs/<name>, #/components/schemas/<name>)
// resolved against ROOT-level holders. Annotations accepted and
// ignored: description, format, title, $schema, x-*. EVERYTHING else
// rejects with a teaching error naming the keyword and its JSON path —
// a cross-file $ref's teaching names the standalone variants (refs
// inlined at publish).
type Schema =
    | SAny
    | SScalar of kinds: Set<string> // "string" | "integer" | "number" | "boolean" | "null"
    | SEnum of values: string list
    | SObject of props: (string * Schema) list * required: string list * additional: AdditionalProps
    | SArray of items: Schema
    // an in-document reference, resolved via SchemaDoc.Defs — the NAME
    // is kept (the generator mints type names from it) [D:schema-types]
    | SRef of name: string
    // a general anyOf: alternatives in document order [D:schema-types]
    | SChoice of Schema list
    // `nullable: true` on a non-scalar (a scalar folds "null" into its
    // kind set instead) [D:schema-types]
    | SNullable of Schema

and AdditionalProps =
    | Closed
    | OpenProps
    | Vals of Schema

/// a parsed schema DOCUMENT: the root shape plus the root-level shared
/// definitions in-document $ref resolves against [D:schema-types]
type SchemaDoc =
    { Root: Schema
      Defs: Map<string, Schema> }

let private annotationKeys =
    set
        [ "description"
          "format"
          "title"
          "$schema"
          "example"
          "examples"
          "default" ]

let private rejectedTeaching (path: string) (kw: string) : string =
    let where = if path = "" then "the schema root" else path

    match kw with
    | "$defs"
    | "definitions"
    | "components" ->
        $"at {where}: '{kw}' holds shared definitions at the schema ROOT only — in-document $ref resolves against the root holders"
    | "not"
    | "if"
    | "then"
    | "else" -> $"at {where}: '{kw}' (schema composition) is outside the subset — zero uses in the measured corpus"
    | _ -> $"at {where}: '{kw}' is outside the schema subset (it joins when a corpus needs it)"

/// the in-document $ref forms the subset resolves [D:schema-types]:
/// #/definitions/<name>, #/$defs/<name>, #/components/schemas/<name> —
/// one segment, JSON-pointer unescaped (~1 → /, ~0 → ~)
let private refTarget (r: string) : string option =
    [ "#/definitions/"; "#/$defs/"; "#/components/schemas/" ]
    |> List.tryPick (fun prefix ->
        if r.StartsWith prefix then
            let rest = r.Substring prefix.Length

            if rest <> "" && not (rest.Contains "/") then
                Some(rest.Replace("~1", "/").Replace("~0", "~"))
            else
                None
        else
            None)

/// an allOf member that carries ONLY annotations (description, x-*, …)
/// contributes no shape — the k8s idiom wraps one $ref with one of these
let private annotationOnly (el: Text.Json.JsonElement) : bool =
    el.ValueKind = Text.Json.JsonValueKind.Object
    && el.EnumerateObject()
       |> Seq.forall (fun p -> annotationKeys.Contains p.Name || p.Name.StartsWith "x-")

let rec private parseNode (path: string) (el: Text.Json.JsonElement) : Result<Schema, string> =
    let where = if path = "" then "the schema root" else path

    if el.ValueKind <> Text.Json.JsonValueKind.Object then
        Error $"at {where}: a schema node must be an object"
    else
        // reject unknown keywords FIRST, so the teaching names them; the
        // definition HOLDERS are known at the ROOT only [D:schema-types]
        let mutable rejection = None

        for p in el.EnumerateObject() do
            let k = p.Name

            let known =
                k = "type"
                || k = "properties"
                || k = "required"
                || k = "items"
                || k = "additionalProperties"
                || k = "enum"
                || k = "oneOf"
                || k = "anyOf"
                || k = "allOf"
                || k = "$ref"
                || k = "nullable"
                || (path = "" && (k = "definitions" || k = "$defs" || k = "components"))
                || annotationKeys.Contains k
                || k.StartsWith "x-"

            if not known && rejection.IsNone then
                rejection <- Some(rejectedTeaching path k)

        match rejection with
        | Some e -> Error e
        | None ->

            let getProp (name: string) =
                match el.TryGetProperty name with
                | true, v -> Some v
                | _ -> None

            // `nullable: true` (the OpenAPI 3.0 spelling) folds into a
            // scalar's kind set; anything else wraps [D:schema-types]
            let applyNullable (r: Result<Schema, string>) =
                match getProp "nullable" with
                | Some n when n.ValueKind = Text.Json.JsonValueKind.True ->
                    r
                    |> Result.map (function
                        | SScalar kinds -> SScalar(Set.add "null" kinds)
                        | s -> SNullable s)
                | _ -> r

            let typeKinds =
                match getProp "type" with
                | None -> None
                | Some t when t.ValueKind = Text.Json.JsonValueKind.String -> Some(Set.singleton (t.GetString()))
                | Some t when t.ValueKind = Text.Json.JsonValueKind.Array ->
                    Some(t.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq)
                | Some _ -> Some Set.empty

            // the scalar-alternatives fold shared by oneOf and anyOf —
            // the IntOrString idiom in both its spellings
            let scalarAlts (alts: Text.Json.JsonElement) =
                let kinds =
                    alts.EnumerateArray()
                    |> Seq.collect (fun alt ->
                        match alt.TryGetProperty "type" with
                        | true, t when t.ValueKind = Text.Json.JsonValueKind.String -> [ t.GetString() ]
                        | true, t when t.ValueKind = Text.Json.JsonValueKind.Array ->
                            t.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
                        | _ -> [ "?" ])
                    |> Set.ofSeq

                if kinds.Contains "?" || kinds.Contains "object" || kinds.Contains "array" then
                    None
                else
                    Some(SScalar kinds)

            match getProp "$ref" with
            | Some r when r.ValueKind = Text.Json.JsonValueKind.String ->
                // a $ref node IS the reference — siblings are annotations
                // (draft-4 semantics, the k8s shape) [D:schema-types]
                let raw = r.GetString()

                (match refTarget raw with
                 | Some name -> Ok(SRef name)
                 | None ->
                     Error
                         $"at {where}: '$ref' \"{raw}\" does not point into this document — in-document refs only (#/definitions/<name>, #/$defs/<name>, #/components/schemas/<name>); for cross-file refs add a bundled or standalone variant of the schema")
                |> applyNullable
            | Some _ -> Error $"at {where}: '$ref' must be a string"
            | None ->

                match getProp "allOf" with
                | Some members ->
                    // ONE schema plus annotations flattens (the k8s
                    // $ref-with-description idiom); anything else stays
                    // outside the subset [D:schema-types]
                    let substantive =
                        members.EnumerateArray()
                        |> Seq.filter (fun m -> not (annotationOnly m))
                        |> List.ofSeq

                    (match substantive with
                     | [ one ] -> parseNode (path + "allOf.") one
                     | _ ->
                         Error
                             $"at {where}: 'allOf' is supported as ONE schema plus annotations (the k8s $ref-with-description idiom) — general composition is outside the subset")
                    |> applyNullable
                | None ->

                    match getProp "enum" with
                    | Some e ->
                        let values =
                            e.EnumerateArray()
                            |> Seq.map (fun v ->
                                match v.ValueKind with
                                | Text.Json.JsonValueKind.String -> v.GetString()
                                | _ -> v.GetRawText())
                            |> List.ofSeq

                        applyNullable (Ok(SEnum values))
                    | None ->

                        match getProp "oneOf" with
                        | Some alts ->
                            // the IntOrString idiom: every alternative must be scalar-typed
                            (match scalarAlts alts with
                             | Some s -> Ok s
                             | None ->
                                 Error
                                     $"at {where}: 'oneOf' is supported only over scalar type alternatives (the IntOrString idiom) — general composition is outside the subset")
                            |> applyNullable
                        | None ->

                            match getProp "anyOf" with
                            | Some alts ->
                                // all-scalar folds like oneOf; otherwise the
                                // alternatives are KEPT — first-variant for the
                                // generator, unvalidated for the district
                                // [D:schema-types]
                                (match scalarAlts alts with
                                 | Some s -> Ok s
                                 | None ->
                                     alts.EnumerateArray()
                                     |> Seq.indexed
                                     |> Seq.fold
                                         (fun acc (i, alt) ->
                                             match acc with
                                             | Error e -> Error e
                                             | Ok list ->
                                                 parseNode (path + $"anyOf[{i}].") alt
                                                 |> Result.map (fun s -> s :: list))
                                         (Ok [])
                                     |> Result.map (List.rev >> SChoice))
                                |> applyNullable
                            | None ->

                                // `type: ["object","null"]` / `["array","null"]`
                                // wrap like `nullable: true` does — the null
                                // spelling follows the value [D:schema-types]
                                let nullListed =
                                    match typeKinds with
                                    | Some ks -> ks.Contains "null"
                                    | None -> false

                                let wrapNullListed (r: Result<Schema, string>) =
                                    if nullListed then Result.map SNullable r else r

                                (match typeKinds with
                                 | Some kinds when
                                     kinds.Contains "object" || (kinds.IsEmpty && getProp("properties").IsSome)
                                     ->
                                     objectNode path el getProp |> wrapNullListed
                                 | None when (getProp "properties").IsSome -> objectNode path el getProp
                                 | Some kinds when kinds.Contains "array" ->
                                     (match getProp "items" with
                                      | Some items -> parseNode (path + "items.") items |> Result.map SArray
                                      | None -> Ok(SArray SAny))
                                     |> wrapNullListed
                                 | Some kinds when not kinds.IsEmpty -> Ok(SScalar kinds)
                                 | _ -> Ok SAny)
                                |> applyNullable

and private objectNode (path: string) (el: Text.Json.JsonElement) (getProp: string -> Text.Json.JsonElement option) =
    let required =
        match getProp "required" with
        | Some r when r.ValueKind = Text.Json.JsonValueKind.Array ->
            r.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
        | _ -> []

    let additional =
        match getProp "additionalProperties" with
        | Some a when a.ValueKind = Text.Json.JsonValueKind.False -> Ok Closed
        | Some a when a.ValueKind = Text.Json.JsonValueKind.True -> Ok OpenProps
        | Some a when a.ValueKind = Text.Json.JsonValueKind.Object ->
            parseNode (path + "additionalProperties.") a |> Result.map Vals
        | Some _ -> Ok OpenProps
        | None -> Ok OpenProps

    match additional with
    | Error e -> Error e
    | Ok additional ->
        let props =
            match getProp "properties" with
            | Some ps when ps.ValueKind = Text.Json.JsonValueKind.Object ->
                ps.EnumerateObject()
                |> Seq.fold
                    (fun acc p ->
                        match acc with
                        | Error e -> Error e
                        | Ok list ->
                            parseNode (path + "properties." + p.Name + ".") p.Value
                            |> Result.map (fun s -> (p.Name, s) :: list))
                    (Ok [])
                |> Result.map List.rev
            | _ -> Ok []

        props |> Result.map (fun props -> SObject(props, required, additional))

// every SRef a tree reaches — the parse-time resolution check's walk
let rec private refsOf (s: Schema) : string list =
    match s with
    | SRef n -> [ n ]
    | SObject(props, _, additional) ->
        (props |> List.collect (snd >> refsOf))
        @ (match additional with
           | Vals v -> refsOf v
           | _ -> [])
    | SArray items -> refsOf items
    | SNullable inner -> refsOf inner
    | SChoice alts -> alts |> List.collect refsOf
    | SAny
    | SScalar _
    | SEnum _ -> []

/// parse a vendored schema file's text into the subset tree — the root
/// shape plus the ROOT-level definition holders (definitions / $defs /
/// components.schemas) in-document $ref resolves against; every $ref is
/// checked to resolve HERE, so consumers never meet a dangling name.
/// Errors carry the schema NAME and the JSON path of the offender.
let parseSchema (name: string) (text: string) : Result<SchemaDoc, string> =
    try
        use doc = Text.Json.JsonDocument.Parse text
        let root = doc.RootElement

        let holders =
            if root.ValueKind <> Text.Json.JsonValueKind.Object then
                []
            else
                [ (match root.TryGetProperty "definitions" with
                   | true, h -> Some h
                   | _ -> None)
                  (match root.TryGetProperty "$defs" with
                   | true, h -> Some h
                   | _ -> None)
                  (match root.TryGetProperty "components" with
                   | true, c ->
                       match c.TryGetProperty "schemas" with
                       | true, h -> Some h
                       | _ -> None
                   | _ -> None) ]
                |> List.choose id
                |> List.filter (fun h -> h.ValueKind = Text.Json.JsonValueKind.Object)

        let defs =
            holders
            |> List.collect (fun h -> h.EnumerateObject() |> List.ofSeq)
            |> List.fold
                (fun acc p ->
                    match acc with
                    | Error e -> Error e
                    | Ok m ->
                        parseNode $"definitions.{p.Name}." p.Value
                        |> Result.map (fun s -> Map.add p.Name s m))
                (Ok Map.empty)

        (match defs with
         | Error e -> Error e
         | Ok defs ->
             match parseNode "" root with
             | Error e -> Error e
             | Ok rootSchema ->
                 let dangling =
                     (refsOf rootSchema @ (defs |> Map.toList |> List.collect (snd >> refsOf)))
                     |> List.tryFind (fun n -> not (Map.containsKey n defs))

                 match dangling with
                 | Some n ->
                     Error $"'$ref' to '{n}' does not resolve — no such definition in the document's root holders"
                 | None -> Ok { Root = rootSchema; Defs = defs })
        |> Result.mapError (fun e -> $"schema {name}: {e}")
    with ex ->
        Error $"schema {name}: not valid JSON — {ex.Message}"

/// follow a chain of PURE refs to a shape (a ref-to-a-ref); a ref CYCLE
/// with no shape in between lands on SAny — validation relaxes, the
/// generator notes [D:schema-types]
let rec deref (defs: Map<string, Schema>) (seen: Set<string>) (s: Schema) : Schema =
    match s with
    | SRef n when Set.contains n seen -> SAny
    | SRef n ->
        match Map.tryFind n defs with
        | Some t -> deref defs (Set.add n seen) t
        | None -> SAny
    | s -> s

/// `additionalProperties: false` present ANYWHERE in the document — a
/// schema without one cannot fire unknown-field checks [D:yaml-schemas];
/// add warns off this fact, the schema= hover renders it [D:schema-hover]
let rec anyClosedProps (el: Text.Json.JsonElement) : bool =
    match el.ValueKind with
    | Text.Json.JsonValueKind.Object ->
        el.EnumerateObject()
        |> Seq.exists (fun p ->
            (p.Name = "additionalProperties"
             && p.Value.ValueKind = Text.Json.JsonValueKind.False)
            || anyClosedProps p.Value)
    | Text.Json.JsonValueKind.Array -> el.EnumerateArray() |> Seq.exists anyClosedProps
    | _ -> false

// ---- add / restore / verify ------------------------------------------------
// `add <kind>` is KIND-AWARE (acquiring differs per kind: a schema is
// a url fetch; a signature will GENERATE from the installed tool; a
// module will clone at a ref); `restore` and `verify` are
// kind-agnostic BY CONSTRUCTION — every lock entry is source + hash +
// path, so they need to know nothing about the artifact.

/// `weir add schema <url> --as <name>`: fetch, write under the kind
/// directory, upsert the lock entry.
let addFetched (weirDir: string) (kind: string) (name: string) (url: string) : Result<string, string> =
    match fetchBytes url with
    | Error e -> Error e
    | Ok(bytes, contentType) ->
        // [D:add-validates]: add validates EVERYTHING the checker will
        // later require and writes NOTHING if it cannot — an artifact
        // that passes add and fails at check has already put a broken
        // entry in the one file restore and verify trust. Gates in
        // order; the first failure returns with .weir/ untouched.
        let ct = contentType |> Option.defaultValue "unknown"

        let parsed =
            try
                Ok(Text.Json.JsonDocument.Parse bytes)
            with _ ->
                Error $"the response is not JSON (Content-Type: {ct}){rawUrlHint url}; nothing was written"

        match parsed with
        | Error e -> Error e
        | Ok doc ->
            use doc = doc
            let root = doc.RootElement

            let schemaShaped =
                root.ValueKind = Text.Json.JsonValueKind.Object
                && [ "$schema"; "type"; "properties"; "$defs" ]
                   |> List.exists (fun k ->
                       match root.TryGetProperty k with
                       | true, _ -> true
                       | _ -> false)

            if kind = "schema" && not schemaShaped then
                Error
                    "valid JSON, but not a schema — no $schema, type, properties, or $defs at the top level; nothing was written"
            else

                // the subset check runs AT ADD, not at first use: the failure
                // lands where the user can act, and an out-of-subset schema
                // never reaches the lockfile
                let subset =
                    if kind = "schema" then
                        parseSchema name (Text.Encoding.UTF8.GetString bytes) |> Result.map ignore
                    else
                        Ok()

                match subset with
                | Error e -> Error $"{e}; nothing was written"
                | Ok() ->

                    match readLock weirDir with
                    | Error e -> Error e
                    | Ok entries ->
                        // a schema with no `additionalProperties: false` ANYWHERE cannot
                        // fire unknown-field checks — the feature's whole point — so a
                        // silently-inert contract warns at ADD time (the vacuous-pin
                        // class). Plain `-standalone` k8s variants have exactly this
                        // shape; `-standalone-strict` is the load-bearing variant.
                        if kind = "schema" then
                            if not (anyClosedProps root) then
                                Console.Error.WriteLine
                                    $"weir add: warning: {name} has no `additionalProperties: false` anywhere — unknown-field checking will NOT fire for it (for k8s, use the -standalone-strict variant)"

                        // only now touch the disk — the file and the lock entry
                        // land together or not at all
                        let rel = Path.Combine(kind + "s", name + ".json")
                        let dest = Path.Combine(weirDir, rel)
                        let hash = sha256Hex bytes

                        let entry =
                            { Kind = kind
                              Name = name
                              Url = url
                              Sha256 = hash
                              Path = rel
                              Version = None }

                        let others = entries |> List.filter (fun e -> not (e.Kind = kind && e.Name = name))

                        // a symlinked kind directory (.weir/schemas → outside)
                        // must not redirect the vendored write
                        // [D:lockfile-symlink-confinement]
                        match writeConfined weirDir dest bytes with
                        | Error w -> Error w
                        | Ok() ->
                            try
                                writeLock weirDir (others @ [ entry ])
                                Ok $"added {kind} {name} ({hash.Substring(0, 12)}…) from {url}"
                            with ex ->
                                (try
                                    File.Delete dest
                                 with _ ->
                                     ())

                                Error $"write failed: {ex.Message} — the partial file was removed"

let restore (weirDir: string) : Result<string list, string> =
    match readLock weirDir with
    | Error e -> Error e
    | Ok [] -> Ok [ "the lock records nothing yet — add with: weir add schema <url> --as <name>" ]
    | Ok entries ->
        let results =
            entries
            |> List.map (fun e ->
                // CONFINE PER ENTRY [D:lockfile-confinement]: a hostile
                // path refuses HERE (located, naming the entry) and writes
                // nothing; a benign sibling still restores. restore
                // overwrites only its OWN artifacts inside .weir/, never a
                // path outside it — the confinement makes "outside"
                // unreachable [D:lockfile-confinement]
                match entryDest weirDir e with
                | Error refusal -> Error refusal
                | Ok dest ->

                // a PRESENT-BUT-MODIFIED url artifact is drift from the
                // lock's intent — restore repairs it by refetching, the
                // same hash-checked path an absent file takes (a
                // deliberate local edit is a re-add, not an edit-in-place)
                let presentAndTrue =
                    File.Exists dest
                    && (e.Url.StartsWith "generated:" || sha256Hex (File.ReadAllBytes dest) = e.Sha256)

                if presentAndTrue then
                    Ok $"{e.Kind} {e.Name}: present"
                elif e.Url.StartsWith "generated:" then
                    // the ruled restore behaviour for a GENERATED entry
                    // [D:command-signatures]: NEVER regenerate (that would
                    // make a checked-in signature depend on the machine
                    // running restore). Present = confirmed by the verify
                    // pass; absent = it was never checked in, and only
                    // regeneration can recreate it — say so.
                    Error
                        $"{e.Kind} {e.Name}: ABSENT and generated (nothing to fetch) — the file should be checked in; recreate deliberately with `weir add sig {e.Name}`"
                else
                    match fetchBytes e.Url with
                    | Error err -> Error $"{e.Kind} {e.Name}: {err}"
                    | Ok(bytes, _) ->
                        let hash = sha256Hex bytes

                        if hash <> e.Sha256 then
                            Error
                                $"{e.Kind} {e.Name}: fetched bytes hash {hash.Substring(0, 12)}… but the lock records {e.Sha256.Substring(0, 12)}… — the source changed; if intended, `weir add schema` again"
                        else
                            let repaired = File.Exists dest

                            // the TOCTOU-tight write [D:lockfile-symlink-confinement]:
                            // writeConfined realpath-verifies the parent and opens the
                            // leaf O_NOFOLLOW, so a symlinked component (DA-04) is
                            // refused at open, not merely at the earlier preflight
                            match writeConfined weirDir dest bytes with
                            | Error w -> Error $"{e.Kind} {e.Name}: {w}"
                            | Ok() ->

                            Ok(
                                if repaired then
                                    $"{e.Kind} {e.Name}: repaired — refetched over a modified copy"
                                else
                                    $"{e.Kind} {e.Name}: restored from {e.Url}"
                            ))

        match
            results
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some firstErr -> Error firstErr
        | None ->
            Ok(
                results
                |> List.map (function
                    | Ok s -> s
                    | Error _ -> "")
            )

/// the version probe's three honest answers [D:sig-version-probe]:
/// absent, speaks, or REFUSES every rung — a nonzero exit's output is
/// a usage dump (paths included), never an identity; recording it
/// leaked the error text into committed sigs
type VersionProbe =
    | ToolAbsent
    | RefusesVersionFlag
    | ToolVersion of string

/// probe output is PARSED, so terminal escapes are noise — a colored
/// help banner fails an all-caps test (ToUpper turns the CSI final
/// byte `m` into `M`), and a colored version would store escapes in
/// the sig [D:sig-version-probe]
let stripAnsi (text: string) : string =
    Text.RegularExpressions.Regex.Replace(text, "\x1b\\[[0-9;?]*[A-Za-z]", "")

/// one probe rung: spawn `<tool> <arg>` and read the exit code.
/// stdin is NULL and the cwd a fresh temp dir — probing the bare
/// `version` word must not hang a stdin-reader (`grep version`) or
/// serve a local VERSION file as the identity (`cat version`)
/// [D:sig-version-probe]
let private probeRung (resolve: string -> string) (tool: string) (arg: string) : VersionProbe =
    try
        let psi = Diagnostics.ProcessStartInfo(resolve tool, arg)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.RedirectStandardInput <- true
        psi.UseShellExecute <- false
        psi.WorkingDirectory <- IO.Path.GetTempPath()
        use p = Diagnostics.Process.Start psi
        p.StandardInput.Close()
        // ASYNC reads, then the bounded wait — a synchronous ReadToEnd
        // blocks before any timeout can fire if the child holds stdout
        let outTask = p.StandardOutput.ReadToEndAsync()
        let errTask = p.StandardError.ReadToEndAsync()

        // a version probe that needs a minute is a hang, not a slow
        // tool — kill and record no identity
        if not (p.WaitForExit 15000) then
            (try
                p.Kill true
             with _ ->
                 ())

            RefusesVersionFlag
        elif p.ExitCode <> 0 then
            RefusesVersionFlag
        else
            let out = outTask.Result
            let err = errTask.Result

            match stripAnsi (if out.Trim() <> "" then out else err) with
            | blank when blank.Trim() = "" -> RefusesVersionFlag
            | text ->
                // the IDENTITY is the FIRST line, whitespace-collapsed
                // [D:sig-version-probe]: az's --version is a whole
                // environment report — machine paths, dependency lists,
                // even a volatile update marker; the headline is the
                // version, the rest is environment
                let firstLine =
                    text.Split '\n'
                    |> Array.tryFind (fun l -> l.Trim() <> "")
                    |> Option.defaultValue text

                ToolVersion(Text.RegularExpressions.Regex.Replace(firstLine.Trim(), "\s+", " "))
    with _ ->
        ToolAbsent

/// the LADDER — `--version`, then the `version` subcommand (jira,
/// kubectl, terraform, go, gh). Two rungs ONLY, both stated: the
/// single-dash spellings are excluded by ruling (`-v` is verbose on
/// half the world, and `ls -v` exits 0 with a listing as the
/// "identity"). FENCED to the two commands allowed to ask the
/// environment (`weir verify`, `weir add sig`); check/completion
/// never call this [D:command-signatures]
/// `resolve` is the spawn-side PATHEXT resolution (Proc.resolveProg —
/// this module compiles before it): CreateProcess appends only .exe,
/// so a .bat tool needs its real file handed over [D:windows-s2]
let probeToolVersion (resolve: string -> string) (tool: string) : VersionProbe =
    match probeRung resolve tool "--version" with
    | RefusesVersionFlag -> probeRung resolve tool "version"
    | answer -> answer

/// the rungs SEPARATELY, for the add side's gated ladder
/// [D:sig-version-probe]: a FLAG probe is safe on any tool; a bare
/// word is not (`code completion fish` opened VS Code on two files) —
/// the add side runs the word rung only when the tool's help
/// advertises a `version` subcommand. verify keeps the full ladder:
/// it only probes tools with a RECORDED version, i.e. tools that
/// answered a rung at add time.
let probeVersionFlag (resolve: string -> string) (tool: string) : VersionProbe = probeRung resolve tool "--version"

let probeVersionWord (resolve: string -> string) (tool: string) : VersionProbe = probeRung resolve tool "version"

/// `weir verify`, two-arm shaped (ruling: today the hash arm; the
/// signature arm — tool `--version` against the recorded identity —
/// landed beside it [D:command-signatures]).
type VerifyFinding =
    | Absent of LockEntry
    | Modified of LockEntry * actual: string
    // the signature arm [D:command-signatures]: the tool's --version
    // no longer matches the recorded identity, or the tool is missing
    // (an environment mismatch — verify is the command allowed to ask
    // the environment)
    | VersionMismatch of LockEntry * actual: string
    | ToolMissing of LockEntry

let verify (resolve: string -> string) (weirDir: string) : Result<string list * VerifyFinding list, string> =
    match readLock weirDir with
    | Error e -> Error e
    | Ok entries ->
        let lines = ResizeArray<string>()
        let findings = ResizeArray<VerifyFinding>()

        for e in entries do
            // CONFINE PER ENTRY [D:lockfile-confinement]: verify must not
            // READ outside .weir/ either — a hostile path is a MODIFIED-class
            // finding naming the escape, never a read of the escaped file
            match entryDest weirDir e with
            | Error refusal ->
                findings.Add(Modified(e, "escapes .weir/"))
                lines.Add $"{refusal}"
            | Ok dest ->

            match e.Kind with
            // the hash arm: vendored artifacts verify by content
            | _ when not (File.Exists dest) ->
                findings.Add(Absent e)
                lines.Add $"{e.Kind} {e.Name}: ABSENT — run `weir restore`"
            | _ ->
                let actual = sha256Hex (File.ReadAllBytes dest)

                if actual <> e.Sha256 then
                    findings.Add(Modified(e, actual))

                    lines.Add
                        $"{e.Kind} {e.Name}: MODIFIED — sha256 {actual.Substring(0, 12)}…, lock records {e.Sha256.Substring(0, 12)}…"
                else
                    // the VERSION arm — sig entries compare the tool's
                    // verbatim --version against the recorded identity;
                    // exact match, no tolerance [D:command-signatures]
                    match e.Version with
                    | Some recorded ->
                        match probeToolVersion resolve e.Name with
                        | ToolAbsent ->
                            findings.Add(ToolMissing e)

                            lines.Add
                                $"{e.Kind} {e.Name}: TOOL MISSING — '{e.Name}' is not on PATH (the signature records: {recorded})"
                        | RefusesVersionFlag ->
                            findings.Add(VersionMismatch(e, "(the tool no longer answers --version or version)"))

                            lines.Add
                                $"{e.Kind} {e.Name}: VERSION MISMATCH — the installed tool answers neither --version nor version, the signature records '{recorded.Trim()}' — regenerate: weir add sig {e.Name}"
                        | ToolVersion actual when actual.Trim() <> recorded.Trim() ->
                            findings.Add(VersionMismatch(e, actual))

                            lines.Add
                                $"{e.Kind} {e.Name}: VERSION MISMATCH — installed says '{actual.Trim()}', the signature records '{recorded.Trim()}' — regenerate: weir add sig {e.Name}"
                        | ToolVersion _ -> lines.Add $"{e.Kind} {e.Name}: ok (hash + version)"
                    | None -> lines.Add $"{e.Kind} {e.Name}: ok"

        Ok(List.ofSeq lines, List.ofSeq findings)

// ---- validation against a district template [D:yaml-schemas] ---------------
//
// STRUCTURAL validation always (unknown fields, missing required
// fields, misplaced nesting); VALUE validation where types permit
// (a splice checks by its weir TYPE; enum/pattern constraints on
// splices do not check). `for`-generated entries and key splices
// RELAX the unknown/required checks for the map they touch — dynamic
// keys may supply what the checker cannot see. All stated in docs.

let private levenshtein (a: string) (b: string) =
    let d = Array2D.create (a.Length + 1) (b.Length + 1) 0

    for i in 0 .. a.Length do
        d[i, 0] <- i

    for j in 0 .. b.Length do
        d[0, j] <- j

    for i in 1 .. a.Length do
        for j in 1 .. b.Length do
            let cost = if a[i - 1] = b[j - 1] then 0 else 1
            d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)

    d[a.Length, b.Length]

let private didYouMean (k: string) (props: (string * Schema) list) =
    props
    |> List.map fst
    |> List.filter (fun p -> k.Length > 3 && levenshtein k p <= 2)
    |> function
        | best :: _ -> $" — did you mean '{best}'?"
        | [] -> ""

/// the scalar kind a weir TYPE guarantees, or None when the type
/// cannot speak (Yaml nodes, unresolved template parameters)
let rec private tyKind (t: Ty) : string option =
    match t with
    | TStr -> Some "string"
    | TInt -> Some "integer"
    | TFloat -> Some "number"
    | TBool -> Some "boolean"
    | TNamed("Option", [ inner ]) -> tyKind inner
    | _ -> None

let private kindOk (kinds: Set<string>) (got: string) =
    kinds.Contains got
    || (got = "integer" && kinds.Contains "number")
    || (got = "null" && kinds.Contains "null")

let private kindsText (kinds: Set<string>) = String.Join("/", Set.toList kinds)

let private literalKind (raw: string) (quoted: bool) =
    if quoted then
        "string"
    elif raw = "" then
        "null"
    elif raw = "true" || raw = "false" then
        "boolean"
    else
        match Int64.TryParse raw with
        | true, _ -> "integer"
        | _ ->
            match parseFloat raw with
            | Ok _ -> "number"
            | Error _ -> "string"

// paths are ALWAYS in the message (ruling: a few characters buys a
// self-contained CI log — the span still carries editor identity).
// The root renders without a suffix so shallow messages stay terse.
let private atPath (p: string) = if p = "" then "" else $" at {p}"

let private fieldName (p: string) =
    if p = "" then "this value" else $"field {p}"

// enum rendering: a SINGLE allowed value states it plainly (k8s `kind`
// is a one-element enum — the common case); longer lists cap at 6 with
// an honest remainder count, never a decorative ellipsis
let private enumText (values: string list) =
    match values with
    | [ one ] -> $"'{one}'"
    | vs when List.length vs <= 6 -> "one of " + String.Join(", ", vs)
    | vs ->
        let shown = vs |> List.truncate 6 |> String.concat ", "
        $"one of {shown} (+{List.length vs - 6} more)"

let rec validateTpl
    (name: string)
    (defs: Map<string, Schema>)
    (path: string)
    (schema: Schema)
    (tpl: Check.TypedYamlTpl)
    : (Span * string) list =
    let child k = if path = "" then k else path + "." + k

    match schema, tpl with
    | SAny, _ -> []
    // unreachable: patch x schema= refuses at check [D:yaml-nodes]
    | _, Check.TYtDrop _ -> []
    | _, Check.TYtSplice te -> spliceCheck name defs path schema te
    // an in-document ref validates against its target; a pure ref cycle
    // derefs to SAny (the template drives every other recursion, so it
    // always terminates) [D:schema-types]
    | SRef _, tpl -> validateTpl name defs path (deref defs Set.empty schema) tpl
    // a general anyOf is UNVALIDATED — a value legal under any variant
    // must not error, and the checker picks no variant (stated
    // relaxation, never a false positive) [D:schema-types]
    | SChoice _, _ -> []
    // nullable: a null scalar passes; anything else validates the inner
    | SNullable _, Check.TYtScalar(raw, quoted, _) when literalKind raw quoted = "null" -> []
    | SNullable inner, tpl -> validateTpl name defs path inner tpl
    | SObject(props, required, additional), Check.TYtMap(entries, mspan) ->
        let hasDynamic =
            entries
            |> List.exists (function
                | Check.TYtPair(Check.TYtKeySplice _, _) -> true
                | Check.TYtForEntries _ -> true
                | _ -> false)

        let literalKeys =
            entries
            |> List.choose (function
                | Check.TYtPair(Check.TYtKeyLit(k, _), _) -> Some k
                | _ -> None)

        let rec entryErrors (es: Check.TypedYamlTplEntry list) =
            es
            |> List.collect (function
                | Check.TYtPair(Check.TYtKeyLit(k, kspan), v) ->
                    match props |> List.tryFind (fun (p, _) -> p = k) with
                    | Some(_, sub) -> validateTpl name defs (child k) sub v
                    | None ->
                        match additional with
                        | Vals s -> validateTpl name defs (child k) s v
                        | OpenProps -> []
                        | Closed -> [ kspan, $"schema {name}: unknown field '{k}'{atPath path}{didYouMean k props}" ]
                | Check.TYtPair(Check.TYtKeySplice _, v) ->
                    // a dynamic key: unknowable at check; its VALUE still
                    // checks when the schema constrains all values
                    match additional with
                    | Vals s -> validateTpl name defs path s v
                    | _ -> []
                | Check.TYtForEntries(_, _, body) -> entryErrors body)

        let missing =
            if hasDynamic then
                [] // dynamic keys may supply the required fields
            else
                required
                |> List.filter (fun r -> not (List.contains r literalKeys))
                |> List.map (fun r -> mspan, $"schema {name}: missing required field '{r}'{atPath path}")

        entryErrors entries @ missing
    | SObject _, Check.TYtScalar(_, _, span)
    | SObject _, Check.TYtBlock(_, span) -> [ span, $"schema {name}: {fieldName path} expects a mapping, got a scalar" ]
    | SObject _, Check.TYtSeq(_, span) -> [ span, $"schema {name}: {fieldName path} expects a mapping, got a sequence" ]
    | SArray items, Check.TYtSeq(elems, _) ->
        let rec itemErrors (es: Check.TypedYamlTplItem list) =
            es
            |> List.collect (function
                | Check.TYtItem t -> validateTpl name defs path items t
                // unreachable: patch x schema= refuses at check [D:yaml-nodes]
                | Check.TYtDropItem(t, _) -> validateTpl name defs path items t
                | Check.TYtForItems(_, _, body) -> itemErrors body)

        itemErrors elems
    | SArray _, Check.TYtScalar(_, _, span)
    | SArray _, Check.TYtBlock(_, span) -> [ span, $"schema {name}: {fieldName path} expects a sequence, got a scalar" ]
    | SArray _, Check.TYtMap(_, span) -> [ span, $"schema {name}: {fieldName path} expects a sequence, got a mapping" ]
    | SScalar kinds, Check.TYtScalar(raw, quoted, span) ->
        let got = literalKind raw quoted

        if kindOk kinds got then
            []
        else
            [ span, $"schema {name}: {fieldName path} expects {kindsText kinds}, got {got} ('{raw}')" ]
    | SScalar kinds, Check.TYtBlock(_, span) ->
        if kindOk kinds "string" then
            []
        else
            [ span, $"schema {name}: {fieldName path} expects {kindsText kinds}, got a block scalar (string)" ]
    | SScalar _, Check.TYtMap(_, span) -> [ span, $"schema {name}: {fieldName path} expects a scalar, got a mapping" ]
    | SScalar _, Check.TYtSeq(_, span) -> [ span, $"schema {name}: {fieldName path} expects a scalar, got a sequence" ]
    | SEnum values, Check.TYtScalar(raw, _, span) ->
        if List.contains raw values then
            []
        else
            [ span, $"schema {name}: {fieldName path} expects {enumText values}, got '{raw}'" ]
    | SEnum values, Check.TYtBlock(text, span) ->
        if List.contains text values then
            []
        else
            [ span, $"schema {name}: {fieldName path} expects {enumText values}, got a block scalar" ]
    | SEnum _, Check.TYtMap(_, span)
    | SEnum _, Check.TYtSeq(_, span) ->
        [ span, $"schema {name}: {fieldName path} expects a scalar (enum), got a collection" ]

and private spliceCheck
    (name: string)
    (defs: Map<string, Schema>)
    (path: string)
    (schema: Schema)
    (te: Check.TypedExpr)
    : (Span * string) list =
    // value validation WHERE TYPES PERMIT: the splice's weir type is
    // all the checker can see. Yaml-typed and unresolved splices skip;
    // enum constraints on splices skip (stated).
    match schema with
    | SAny
    | SEnum _ -> []
    // the ref resolves; a general anyOf skips; nullable checks the
    // inner (a splice's Option-ness already unwraps in tyKind)
    // [D:schema-types]
    | SRef _ -> spliceCheck name defs path (deref defs Set.empty schema) te
    | SChoice _ -> []
    | SNullable inner -> spliceCheck name defs path inner te
    | SScalar kinds ->
        match te.Ty with
        | TSeq _ ->
            [ te.Span, $"schema {name}: {fieldName path}: a seq splices as sequence items; it expects {kindsText kinds}" ]
        | t ->
            match tyKind t with
            | None -> []
            | Some got ->
                if kindOk kinds got then
                    []
                else
                    [ te.Span, $"schema {name}: {fieldName path} expects {kindsText kinds}, but the splice is {got}" ]
    | SObject _ ->
        match tyKind te.Ty with
        | Some got -> [ te.Span, $"schema {name}: {fieldName path} expects a mapping, but the splice is {got}" ]
        | None -> []
    | SArray items ->
        match te.Ty with
        | TSeq elem ->
            (match items, tyKind elem with
             | SScalar kinds, Some got when not (kindOk kinds got) ->
                 [ te.Span,
                   $"schema {name}: {fieldName path}: sequence items expect {kindsText kinds}, but the spliced seq's elements are {got}" ]
             | _ -> [])
        | t ->
            match tyKind t with
            | Some got -> [ te.Span, $"schema {name}: {fieldName path} expects a sequence, but the splice is {got}" ]
            | None -> []
