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
open Weir.Ast
open Weir.Types

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

        try
            Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
            File.WriteAllBytes(dest, bytes)
            writeLock weirDir (others @ [ entry ])
            Ok(hash, prior)
        with ex ->
            (try
                File.Delete dest
             with _ ->
                 ())

            Error $"write failed: {ex.Message} — the partial file was removed"

// ---- the JSON Schema subset [D:yaml-schemas] -------------------------------

// corpus-measured (six real k8s standalone-strict schemas): IN — type
// (string or array-of-strings, the nullable spelling), properties,
// required, items, additionalProperties (bool or schema), enum, and
// oneOf RESTRICTED to scalar-type alternatives (every corpus occurrence
// is the IntOrString idiom). Annotations accepted and ignored:
// description, format, title, $schema, x-*. EVERYTHING else rejects
// with a teaching error naming the keyword and its JSON path — $ref's
// teaching names the standalone variants (refs inlined at publish).
type Schema =
    | SAny
    | SScalar of kinds: Set<string> // "string" | "integer" | "number" | "boolean" | "null"
    | SEnum of values: string list
    | SObject of props: (string * Schema) list * required: string list * additional: AdditionalProps
    | SArray of items: Schema

and AdditionalProps =
    | Closed
    | OpenProps
    | Vals of Schema

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
    | "$ref"
    | "$defs"
    | "definitions" ->
        $"at {where}: '{kw}' is outside the schema subset — add the STANDALONE variant of this schema instead (refs inlined at publish; kubernetes-json-schema ships both)"
    | "allOf"
    | "anyOf"
    | "not"
    | "if"
    | "then"
    | "else" -> $"at {where}: '{kw}' (schema composition) is outside the subset — zero uses in the measured corpus"
    | _ -> $"at {where}: '{kw}' is outside the schema subset (it joins when a corpus needs it)"

let rec private parseNode (path: string) (el: Text.Json.JsonElement) : Result<Schema, string> =
    let where = if path = "" then "the schema root" else path

    if el.ValueKind <> Text.Json.JsonValueKind.Object then
        Error $"at {where}: a schema node must be an object"
    else
        // reject unknown keywords FIRST, so the teaching names them
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

            let typeKinds =
                match getProp "type" with
                | None -> None
                | Some t when t.ValueKind = Text.Json.JsonValueKind.String -> Some(Set.singleton (t.GetString()))
                | Some t when t.ValueKind = Text.Json.JsonValueKind.Array ->
                    Some(t.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> Set.ofSeq)
                | Some _ -> Some Set.empty

            match getProp "enum" with
            | Some e ->
                let values =
                    e.EnumerateArray()
                    |> Seq.map (fun v ->
                        match v.ValueKind with
                        | Text.Json.JsonValueKind.String -> v.GetString()
                        | _ -> v.GetRawText())
                    |> List.ofSeq

                Ok(SEnum values)
            | None ->

                match getProp "oneOf" with
                | Some alts ->
                    // the IntOrString idiom: every alternative must be scalar-typed
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
                        Error
                            $"at {where}: 'oneOf' is supported only over scalar type alternatives (the IntOrString idiom) — general composition is outside the subset"
                    else
                        Ok(SScalar kinds)
                | None ->

                    match typeKinds with
                    | Some kinds when kinds.Contains "object" || (kinds.IsEmpty && getProp("properties").IsSome) ->
                        objectNode path el getProp
                    | None when (getProp "properties").IsSome -> objectNode path el getProp
                    | Some kinds when kinds.Contains "array" ->
                        match getProp "items" with
                        | Some items -> parseNode (path + "items.") items |> Result.map SArray
                        | None -> Ok(SArray SAny)
                    | Some kinds when not kinds.IsEmpty -> Ok(SScalar kinds)
                    | _ -> Ok SAny

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

/// parse a vendored schema file's text into the subset tree; errors
/// carry the schema NAME and the JSON path of the offending keyword.
let parseSchema (name: string) (text: string) : Result<Schema, string> =
    try
        use doc = Text.Json.JsonDocument.Parse text

        parseNode "" doc.RootElement |> Result.mapError (fun e -> $"schema {name}: {e}")
    with ex ->
        Error $"schema {name}: not valid JSON — {ex.Message}"

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

                        try
                            Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
                            File.WriteAllBytes(dest, bytes)
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
                let dest = Path.Combine(weirDir, e.Path)

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
                            Directory.CreateDirectory(Path.GetDirectoryName dest) |> ignore
                            File.WriteAllBytes(dest, bytes)

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

/// spawn `<tool> --version` and capture its combined first output —
/// FENCED to the two commands allowed to ask the environment
/// (`weir verify`, `weir add sig`); check/completion never call this
/// [D:command-signatures]
/// `resolve` is the spawn-side PATHEXT resolution (Proc.resolveProg —
/// this module compiles before it): CreateProcess appends only .exe,
/// so a .bat tool needs its real file handed over [D:windows-s2]
let toolVersionOutput (resolve: string -> string) (tool: string) : string option =
    try
        let psi = Diagnostics.ProcessStartInfo(resolve tool, "--version")
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        use p = Diagnostics.Process.Start psi
        let out = p.StandardOutput.ReadToEnd()
        let err = p.StandardError.ReadToEnd()
        p.WaitForExit()
        Some(if out.Trim() <> "" then out else err)
    with _ ->
        None

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
            let dest = Path.Combine(weirDir, e.Path)

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
                        match toolVersionOutput resolve e.Name with
                        | None ->
                            findings.Add(ToolMissing e)

                            lines.Add
                                $"{e.Kind} {e.Name}: TOOL MISSING — '{e.Name}' is not on PATH (the signature records: {recorded})"
                        | Some actual when actual.Trim() <> recorded.Trim() ->
                            findings.Add(VersionMismatch(e, actual))

                            lines.Add
                                $"{e.Kind} {e.Name}: VERSION MISMATCH — installed says '{actual.Trim()}', the signature records '{recorded.Trim()}' — regenerate: weir add sig {e.Name}"
                        | Some _ -> lines.Add $"{e.Kind} {e.Name}: ok (hash + version)"
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

let rec validateTpl (name: string) (path: string) (schema: Schema) (tpl: Check.TypedYamlTpl) : (Span * string) list =
    let child k = if path = "" then k else path + "." + k

    match schema, tpl with
    | SAny, _ -> []
    | _, Check.TYtSplice te -> spliceCheck name path schema te
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
                    | Some(_, sub) -> validateTpl name (child k) sub v
                    | None ->
                        match additional with
                        | Vals s -> validateTpl name (child k) s v
                        | OpenProps -> []
                        | Closed -> [ kspan, $"schema {name}: unknown field '{k}'{atPath path}{didYouMean k props}" ]
                | Check.TYtPair(Check.TYtKeySplice _, v) ->
                    // a dynamic key: unknowable at check; its VALUE still
                    // checks when the schema constrains all values
                    match additional with
                    | Vals s -> validateTpl name path s v
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
                | Check.TYtItem t -> validateTpl name path items t
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

and private spliceCheck (name: string) (path: string) (schema: Schema) (te: Check.TypedExpr) : (Span * string) list =
    // value validation WHERE TYPES PERMIT: the splice's weir type is
    // all the checker can see. Yaml-typed and unresolved splices skip;
    // enum constraints on splices skip (stated).
    match schema with
    | SAny
    | SEnum _ -> []
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
