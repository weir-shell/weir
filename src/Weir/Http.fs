module Weir.Http

// the typed request boundary [D:http] — a standalone .NET leg (like
// Contracts.fs): primitives in, primitives out, no Value dependency, so
// Builtins owns the Value<->primitive translation. HttpClient is already
// AOT-linked for the contracts fetch, so this adds ~0 dependency bytes.

open System
open System.Net.Http

// the request as flat primitives: auth and secret headers are already
// resolved to plain header pairs by the caller (Builtins), so THIS module
// never sees a Secret — the reveal happened at the boundary, deliberately
type Req =
    { Method: string
      Url: string
      // header pairs, in order, auth included; duplicates preserved
      Headers: (string * string) list
      // the CREDENTIAL header names [D:secret-redirect]: auth's
      // Authorization plus every secretHeaders name (case-insensitive).
      // On a CROSS-ORIGIN redirect these are DROPPED, exactly as the BCL
      // drops Authorization — so weir's two credential channels agree
      // (the auth union rode .NET's rule for free; secretHeaders did not).
      // Empty for a request with no credentials.
      SensitiveHeaders: Set<string>
      // (contentType, body) — the body is the EXACT bytes to send, no
      // re-encoding, no newline stripping (the whole point over curl -d)
      Body: (string * string) option
      TimeoutMs: int
      // TLS verification OFF for THIS request [D:http-s2]: a loud
      // per-request opt-out for self-signed clusters — never global
      Insecure: bool }

type Resp =
    { Status: int
      Headers: (string * string) list
      Body: string }

// a transport failure in its own words [D:transport-words]: the four
// probed shapes each carry what the caller needs to word a repair;
// Other keeps the root message under the cannot-reach umbrella
type TransportError =
    | Timeout of ms: int
    | NoSuchHost
    | Refused of port: int
    | TlsUntrusted
    | OtherTransport of root: string

let private rootMessage (ex: exn) : string =
    let rec inner (e: exn) =
        match e.InnerException with
        | null -> e
        | i -> inner i

    (inner ex).Message

/// classify by exception TYPE down the chain, never by message text:
/// timeout is the TaskCanceledException HttpClient.Timeout throws (weir
/// passes no cancellation tokens, so cancellation IS timeout), TLS is
/// AuthenticationException, DNS/refused are SocketErrorCode
let classifyTransport (timeoutMs: int) (port: int) (ex: exn) : TransportError =
    let rec walk (e: exn) =
        match e with
        | null -> OtherTransport(rootMessage ex)
        | :? OperationCanceledException -> Timeout timeoutMs
        | :? Security.Authentication.AuthenticationException -> TlsUntrusted
        | :? Net.Sockets.SocketException as se ->
            match se.SocketErrorCode with
            | Net.Sockets.SocketError.HostNotFound
            | Net.Sockets.SocketError.TryAgain
            | Net.Sockets.SocketError.NoData -> NoSuchHost
            | Net.Sockets.SocketError.ConnectionRefused -> Refused port
            | _ -> OtherTransport(rootMessage ex)
        | e -> walk e.InnerException

    walk ex

let private fmtMs (ms: int) : string =
    if ms % 1000 = 0 then $"{ms / 1000}s" else $"{ms}ms"

let transportMessage (host: string) (err: TransportError) : string =
    match err with
    | Timeout ms -> $"timed out after {fmtMs ms} reaching {host}"
    | NoSuchHost -> $"cannot resolve {host} — no such host"
    | Refused port -> $"{host}:{port} refused the connection — nothing is listening there"
    | TlsUntrusted -> $"cannot establish TLS with {host} — the certificate is not trusted"
    | OtherTransport root -> $"cannot reach {host} — {root}"

/// two URLs share an ORIGIN [D:secret-redirect] iff scheme, host and port
/// all match (a port change is a different origin — the same rule the BCL
/// uses to decide whether Authorization survives a redirect). A parse
/// failure on either side is treated as a DIFFERENT origin (fail safe: a
/// URL weir cannot compare must not keep the credential).
let sameOrigin (a: Uri) (b: Uri) : bool =
    a.Scheme = b.Scheme
    && String.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
    && a.Port = b.Port

/// redirect statuses weir follows [D:secret-redirect]: the BCL's set. 303
/// (and 301/302 from a POST) rewrite the method to GET and drop the body;
/// 307/308 preserve both.
let isRedirect (code: int) : bool =
    code = 301 || code = 302 || code = 303 || code = 307 || code = 308

// the redirect bound [D:secret-redirect]: the BCL's default is 50; weir
// bounds explicit-follow the same, so a redirect loop cannot hang past the
// per-request timeout by hop count.
[<Literal>]
let private maxRedirects = 50

/// send the request. Status is DATA — a 4xx/5xx is Ok with that status,
/// never an Error [D:http]. Error is TRANSPORT failure only, classified
/// [D:transport-words] — the worded message plus the case, so the caller
/// can append a case-specific repair (Builtins: insecure on TlsUntrusted).
///
/// Redirects are followed EXPLICITLY [D:secret-redirect], not by the
/// handler's AllowAutoRedirect: on a CROSS-ORIGIN hop the SensitiveHeaders
/// (auth's Authorization + every secretHeaders name) are DROPPED, exactly
/// as the BCL drops Authorization — so weir's two credential channels stop
/// disagreeing (the review's F4). Same-origin hops keep every header.
let send (req: Req) : Result<Resp, string * TransportError> =
    try
        // a per-request handler only when insecure — the default path keeps
        // the plain HttpClient (TLS verification ON) [D:http-s2]. Auto-redirect
        // is OFF so weir controls the credential-drop on an origin change
        // [D:secret-redirect] — the BCL would re-send secretHeaders.
        use handler = new HttpClientHandler()
        handler.AllowAutoRedirect <- false

        if req.Insecure then
            handler.ServerCertificateCustomValidationCallback <- (fun _ _ _ _ -> true)

        use client = new HttpClient(handler)
        client.Timeout <- TimeSpan.FromMilliseconds(float req.TimeoutMs)

        // build one request message for a hop: `method`/`url`, the CURRENT
        // header set (already credential-filtered by the caller loop), the
        // body when the method still carries one, and the UA default.
        let buildMessage (method: string) (url: string) (headers: (string * string) list) (body: (string * string) option) =
            let msg = new HttpRequestMessage(HttpMethod(method), url)

            // the body is attached BEFORE headers so a content-header (e.g. a
            // caller-set Content-Type override) can land on the content
            match body with
            | Some(ct, b) ->
                let content = new StringContent(b, Text.Encoding.UTF8)
                // StringContent defaults text/plain; set the declared type,
                // charset preserved from UTF8
                (try
                    content.Headers.ContentType <- Headers.MediaTypeHeaderValue(ct)
                 with _ ->
                     ())

                msg.Content <- content
            | None -> ()

            for (k, v) in headers do
                // request headers first; a content header (Content-*) lands on
                // the content instead — TryAddWithoutValidation keeps the bytes
                // verbatim (no folding, no re-encoding)
                if not (msg.Headers.TryAddWithoutValidation(k, v)) then
                    match msg.Content with
                    | null -> ()
                    | c -> c.Headers.TryAddWithoutValidation(k, v) |> ignore

            // the default User-Agent [D:http-ua]: weir/<stamp>, the same
            // string --version prints — applied at SEND time, never a field
            // in Http.defaults, so the request RECORD stays stable across
            // releases (a pinned/shown request must not break on a version
            // bump; the cost — show req omits a header the wire carries — is
            // stated in the docs). A caller's User-Agent is already on the
            // message (headers and secretHeaders both arrive merged in
            // req.Headers) and BLOCKS this: header names compare
            // case-insensitively, so exactly one is ever sent. A DEFAULT, not
            // a fixed header — the explicit pair is the override spelling.
            if not (msg.Headers.Contains "User-Agent") then
                msg.Headers.TryAddWithoutValidation("User-Agent", $"weir/{Weir.Version.current}")
                |> ignore

            msg

        // the explicit follow loop [D:secret-redirect]: at each hop, send;
        // on a redirect status with a Location, rebuild for the next hop,
        // dropping the credential headers when the origin changed.
        let mutable curMethod = req.Method
        let mutable curUrl = req.Url
        let mutable curHeaders = req.Headers
        let mutable curBody = req.Body
        let mutable hops = 0
        let mutable result: HttpResponseMessage = null

        let mutable go = true

        while go do
            let msg = buildMessage curMethod curUrl curHeaders curBody
            let resp = client.SendAsync(msg).Result
            let code = int resp.StatusCode

            let location =
                match resp.Headers.Location with
                | null -> None
                | l -> Some l

            match location with
            | Some loc when isRedirect code && hops < maxRedirects ->
                resp.Dispose()
                hops <- hops + 1

                let fromUri = Uri curUrl
                // a relative Location resolves against the current URL
                let nextUri = if loc.IsAbsoluteUri then loc else Uri(fromUri, loc)

                // CROSS-ORIGIN? then drop the credential headers — the F4 fix
                // [D:secret-redirect], the same drop the BCL does for
                // Authorization, now covering secretHeaders too
                if not (sameOrigin fromUri nextUri) && not (Set.isEmpty req.SensitiveHeaders) then
                    curHeaders <-
                        curHeaders
                        |> List.filter (fun (k, _) -> not (req.SensitiveHeaders.Contains(k.ToLowerInvariant())))

                // 303, and 301/302 from a method with a body, rewrite to GET
                // and drop the body (the BCL/browser rule); 307/308 preserve
                let m = curMethod.ToUpperInvariant()

                if code = 303 || ((code = 301 || code = 302) && m <> "GET" && m <> "HEAD") then
                    curMethod <- "GET"
                    curBody <- None

                curUrl <- nextUri.ToString()
            | _ ->
                result <- resp
                go <- false

        use resp = result

        let headers =
            [ for h in resp.Headers -> h.Key, String.concat "," h.Value ]
            @ (match resp.Content with
               | null -> []
               | c -> [ for h in c.Headers -> h.Key, String.concat "," h.Value ])

        let body =
            match resp.Content with
            | null -> ""
            | c -> c.ReadAsStringAsync().Result

        Ok
            { Status = int resp.StatusCode
              Headers = headers
              Body = body }
    with ex ->
        let host, port =
            try
                let u = Uri(req.Url)
                u.Host, u.Port
            with _ ->
                req.Url, 0

        let err = classifyTransport req.TimeoutMs port ex
        Error(transportMessage host err, err)

/// Basic auth is an ENCODING, not a prefix: base64(user:pass) [D:http] —
/// which is why it is a union case the runner encodes, not a header a
/// caller would build by hand and get wrong
let basicToken (user: string) (password: string) : string =
    let raw = Text.Encoding.UTF8.GetBytes($"{user}:{password}")
    Convert.ToBase64String raw

/// the header-injection byte class [D:http-header-bytes]: CR, LF or NUL in
/// a header NAME or VALUE forges a second header / splits a response.
/// Refused at BOTH crossings (the client send path and a serve response) —
/// the same one-boundary refusal the argv NUL guard performs, never the
/// silent forge (client) or drop (server) the review found. Shared so the
/// two directions agree byte-for-byte.
let private headerByteName (c: char) : string option =
    match int c with
    | 13 -> Some "CR"
    | 10 -> Some "LF"
    | 0 -> Some "NUL"
    | _ -> None

/// the offending byte's name and where it sits (NAME or VALUE), or None if
/// the pair is clean — the caller words the located refusal
let headerInjection (name: string, value: string) : (string * string) option =
    match name |> Seq.tryPick headerByteName with
    | Some b -> Some(b, "name")
    | None ->
        match value |> Seq.tryPick headerByteName with
        | Some b -> Some(b, "value")
        | None -> None

/// the located refusal message [D:http-header-bytes], the argv-NUL guard's
/// register: name the header, the byte, and where it sat. `who` is the
/// crossing ("request header" / "response header") so the two directions
/// read the same shape.
let headerInjectionMessage (who: string) (name: string) (byteName: string) (where: string) : string =
    $"{who} '{name}' carries a {byteName} byte in its {where} — that forges a second header (response splitting / header injection); a header name or value cannot contain CR, LF or NUL"

/// refuse any injecting pair in a header list, or return the pairs
/// untouched [D:http-header-bytes]. `fail` raises the located message the
/// caller supplies — the client and server share this walk so neither can
/// drift from the other's byte class.
let refuseHeaderInjection (who: string) (fail: string -> unit) (headers: (string * string) list) : unit =
    for (k, v) in headers do
        match headerInjection (k, v) with
        | Some(byteName, where) -> fail (headerInjectionMessage who k byteName where)
        | None -> ()
