module Weir.Http

// the typed request boundary [D:http] — a standalone .NET leg (like
// Contracts.fs): primitives in, primitives out, no Value dependency, so
// Builtins owns the Value<->primitive translation. HttpClient is already
// AOT-linked for the contracts fetch, so this adds ~0 dependency bytes.

open System
open System.Net.Http

// the request as flat primitives: auth and secret headers are already
// resolved to plain header pairs by the caller (Builtins), so this
// module never sees a Secret — the reveal happened at the boundary,
// deliberately
type Req =
    { Method: string
      Url: string
      // header pairs, in order, auth included; duplicates preserved
      Headers: (string * string) list
      // the credential header names [D:secret-redirect]: auth's
      // Authorization plus every secretHeaders name (case-insensitive).
      // On a cross-origin redirect these are dropped, exactly as the BCL
      // drops Authorization — so weir's two credential channels agree
      // (the auth union rode .NET's rule for free; secretHeaders did not).
      // Empty for a request with no credentials.
      SensitiveHeaders: Set<string>
      // (contentType, body) — the body is the exact bytes to send, no
      // re-encoding, no newline stripping (the whole point over curl -d)
      Body: (string * string) option
      TimeoutMs: int64
      // TLS verification off for this request [D:http-s2]: a loud
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
    | Timeout of ms: int64
    | NoSuchHost
    | Refused of port: int
    | TlsUntrusted
    | OtherTransport of root: string
    // not a transport failure but the same Error channel: the response
    // declared a charset .NET cannot decode
    | UnknownCharset of charset: string * status: int
    // the redirect bound ran out — a loop, never a 3xx handed back as data
    | RedirectLoop of hops: int * last: string

// raised inside send when the body's declared charset is unknown — the
// catch turns it into UnknownCharset, never the cannot-reach umbrella
exception private CharsetUnknown of string * int

exception private TooManyRedirects of int * string

let private rootMessage (ex: exn) : string =
    let rec inner (e: exn) =
        match e.InnerException with
        | null -> e
        | i -> inner i

    (inner ex).Message

/// classify by exception type down the chain, never by message text:
/// timeout is the TaskCanceledException HttpClient.Timeout throws (weir
/// passes no cancellation tokens, so cancellation means timeout), TLS
/// is AuthenticationException, DNS/refused are SocketErrorCode
let classifyTransport (timeoutMs: int64) (port: int) (ex: exn) : TransportError =
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

let private fmtMs (ms: int64) : string =
    if ms % 1000L = 0L then $"{ms / 1000L}s" else $"{ms}ms"

/// redact a URL's userinfo for a diagnostic [D:url-redact]: a credential
/// in `scheme://user:pass@host/...` would otherwise print verbatim
/// (terminal, CI log, REPL). Replace the whole `user:pass` span (between
/// `://` and the first `@` before the path/query/fragment) with `***`,
/// keeping scheme and everything from the host on — a credential-free
/// URL is returned unchanged, so the target is still named in full.
/// Purely textual, so it works on a URL `Uri` could not parse (exactly
/// the transport fallback's case). The authority ends at the first `/`,
/// `?` or `#`; an `@` only counts as a userinfo separator before that,
/// so an `@` in a path or query is left alone.
let redactUrl (url: string) : string =
    match url.IndexOf "://" with
    | -1 -> url
    | schemeEnd ->
        let authStart = schemeEnd + 3
        // the authority runs to the first path/query/fragment delimiter
        let authEnd =
            let rest = url.Substring authStart

            match rest |> Seq.tryFindIndex (fun c -> c = '/' || c = '?' || c = '#') with
            | Some i -> authStart + i
            | None -> url.Length

        let authority = url.Substring(authStart, authEnd - authStart)

        match authority.IndexOf '@' with
        | -1 -> url
        | at -> url.Substring(0, authStart) + "***@" + url.Substring(authStart + at + 1)

let transportMessage (host: string) (err: TransportError) : string =
    match err with
    | Timeout ms -> $"timed out after {fmtMs ms} reaching {host}"
    | NoSuchHost -> $"cannot resolve {host} — no such host"
    | Refused port -> $"{host}:{port} refused the connection — nothing is listening there"
    | TlsUntrusted -> $"cannot establish TLS with {host} — the certificate is not trusted"
    | OtherTransport root -> $"cannot reach {host} — {root}"
    | UnknownCharset(cs, status) ->
        $"{host} answered {status} with a body in charset '{cs}', which weir cannot decode — weir decodes utf-8, utf-16, utf-32, us-ascii and iso-8859-1; a mislabelled body is never guessed at"
    | RedirectLoop(hops, last) ->
        $"{host}: still redirecting after {hops} hops (the last at {redactUrl last}) — a redirect loop"

/// two URLs share an origin [D:secret-redirect] iff scheme, host and port
/// all match (a port change is a different origin — the same rule the BCL
/// uses to decide whether Authorization survives a redirect). A parse
/// failure on either side is treated as a different origin (fail safe: a
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

// every valid Duration is honoured as a timeout [D:long-waits]: .NET
// timers (HttpClient.Timeout, CancelAfter) take at most Int32.MaxValue
// ms (~24.8 days), so a longer deadline re-arms in slices of `sliceMs`
// until the remainder fits. Stops when `cts` cancels (the deadline, or
// the request finishing first).
let cancelAfterSliced (sliceMs: int64) (cts: Threading.CancellationTokenSource) (ms: int64) : unit =
    let rec arm (left: int64) =
        if left <= sliceMs then
            cts.CancelAfter(TimeSpan.FromMilliseconds(float left))
        else
            Threading.Tasks.Task
                .Delay(TimeSpan.FromMilliseconds(float sliceMs), cts.Token)
                .ContinueWith(fun (t: Threading.Tasks.Task) -> if not t.IsCanceled then arm (left - sliceMs))
            |> ignore

    arm ms

let private maxTimerMs = int64 Int32.MaxValue - 1L

// NO_PROXY against an IPv6 literal [D:noproxy-v6]: .NET matches entries
// against the bracketed host, so `::1` never bypassed `http://[::1]` and
// `[::1]` did. An entry names the address as curl reads it — bracket-less,
// compared as an address (or `*`); every other host keeps the runtime's rule.
type private V6NoProxy(inner: Net.IWebProxy) =
    let entries =
        lazy
            (let raw =
                match Environment.GetEnvironmentVariable "no_proxy" with
                | null
                | "" -> Environment.GetEnvironmentVariable "NO_PROXY"
                | v -> v

             match raw with
             | null -> []
             | r -> r.Split(',') |> Array.map (fun e -> e.Trim()) |> Array.filter ((<>) "") |> List.ofArray)

    let listed (u: Uri) =
        match Net.IPAddress.TryParse u.DnsSafeHost with
        | true, host ->
            entries.Value
            |> List.exists (fun e ->
                e = "*"
                || (match Net.IPAddress.TryParse e with
                    | true, a -> a.Equals host
                    | _ -> false))
        | _ -> false

    let isV6 (u: Uri) = u.HostNameType = UriHostNameType.IPv6

    let proxyFor (u: Uri) : Uri =
        if not (isV6 u) then
            inner.GetProxy u
        elif listed u then
            null
        else
            // the runtime's proxy for the scheme, asked of a host no
            // bracketed entry can match (TEST-NET-1)
            inner.GetProxy(UriBuilder(u, Host = "192.0.2.1").Uri)

    interface Net.IWebProxy with
        member _.Credentials
            with get () = inner.Credentials
            and set v = inner.Credentials <- v

        member _.GetProxy(u: Uri) = proxyFor u

        member _.IsBypassed(u: Uri) =
            if isV6 u then isNull (proxyFor u) else inner.IsBypassed u

/// the environment's proxy for a client handler, NO_PROXY matched as above
let useEnvProxy (handler: HttpClientHandler) : unit =
    handler.Proxy <- V6NoProxy(HttpClient.DefaultProxy)

/// send the request. Status is data — a 4xx/5xx is Ok with that status,
/// never an Error [D:http]. Error is transport failure only, classified
/// [D:transport-words] — the worded message plus the case, so the caller
/// can append a case-specific repair (Builtins: insecure on TlsUntrusted).
///
/// Redirects are followed explicitly [D:secret-redirect], not by the
/// handler's AllowAutoRedirect: on a cross-origin hop the SensitiveHeaders
/// (auth's Authorization + every secretHeaders name) are dropped, exactly
/// as the BCL drops Authorization — so weir's two credential channels stop
/// disagreeing (the review's F4). Same-origin hops keep every header.
///
/// `cancel` cuts the request (a lost pfirst race, a REPL ^C); the caller
/// names why — the transport error then reads as a timeout.
let send (cancel: Threading.CancellationToken) (req: Req) : Result<Resp, string * TransportError> =
    try
        // the insecure callback is set per request only — the default
        // path keeps TLS verification on [D:http-s2]. Auto-redirect is
        // off so weir controls the credential-drop on an origin change
        // [D:secret-redirect] — the BCL would re-send secretHeaders.
        use handler = new HttpClientHandler()
        handler.AllowAutoRedirect <- false
        useEnvProxy handler

        if req.Insecure then
            handler.ServerCertificateCustomValidationCallback <- (fun _ _ _ _ -> true)

        use client = new HttpClient(handler)
        // the deadline rides a token, never client.Timeout's 24.8-day
        // ceiling [D:long-waits]; cancelled when the send returns
        client.Timeout <- Threading.Timeout.InfiniteTimeSpan
        use deadline = new Threading.CancellationTokenSource()
        use _stop = { new IDisposable with member _.Dispose() = deadline.Cancel() }
        cancelAfterSliced maxTimerMs deadline req.TimeoutMs
        use _cut = cancel.Register(fun () -> deadline.Cancel())

        // build one request message for a hop: `method`/`url`, the current
        // header set (already credential-filtered by the caller loop), the
        // body when the method still carries one, and the UA default.
        let buildMessage (method: string) (url: string) (headers: (string * string) list) (body: (string * string) option) =
            let msg = new HttpRequestMessage(HttpMethod(method), url)

            // the body is attached before headers so a content-header (e.g. a
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
            // string --version prints — applied at send time, never a field
            // in Http.defaults, so the request record stays stable across
            // releases (a pinned/shown request must not break on a version
            // bump; the cost — show req omits a header the wire carries — is
            // stated in the docs). A caller's User-Agent is already on the
            // message (headers and secretHeaders both arrive merged in
            // req.Headers) and blocks this: header names compare
            // case-insensitively, so exactly one is ever sent. A default, not
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
            let resp = client.SendAsync(msg, deadline.Token).Result
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

                // on a cross-origin hop, drop the credential headers — the
                // F4 fix [D:secret-redirect], the same drop the BCL does
                // for Authorization, now covering secretHeaders too
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
            | Some _ when isRedirect code ->
                resp.Dispose()
                raise (TooManyRedirects(hops, curUrl))
            | _ ->
                result <- resp
                go <- false

        use resp = result

        // one pair per received header line, the value verbatim: the
        // parsed view splits a value into tokens (a Server header's
        // product list) and the old "," join merged repeats into one
        let headers =
            [ for h in resp.Headers.NonValidated do
                  for v in h.Value -> h.Key, v ]
            @ (match resp.Content with
               | null -> []
               | c ->
                   [ for h in c.Headers.NonValidated do
                         for v in h.Value -> h.Key, v ])

        // an undecodable declared charset is its own error, never a
        // guessed decode — the same never-silent-U+FFFD law as
        // [D:source-utf8]
        let body =
            match resp.Content with
            | null -> ""
            | c ->
                match c.Headers.ContentType with
                | null -> ()
                | t when String.IsNullOrWhiteSpace t.CharSet -> ()
                | t ->
                    let cs = t.CharSet.Trim().Trim('"')

                    let known =
                        try
                            Text.Encoding.GetEncoding cs |> ignore
                            true
                        with _ ->
                            false

                    if not known then
                        raise (CharsetUnknown(cs, int resp.StatusCode))

                c.ReadAsStringAsync(deadline.Token).Result

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
                // the URL would not parse — the fallback names it in the
                // error, so redact its userinfo [D:url-redact] (a
                // `user:pass@host` credential must not print verbatim)
                redactUrl req.Url, 0

        let err =
            match ex with
            | CharsetUnknown(cs, status) -> UnknownCharset(cs, status)
            | TooManyRedirects(hops, last) -> RedirectLoop(hops, last)
            | _ -> classifyTransport req.TimeoutMs port ex
        Error(transportMessage host err, err)

/// Basic auth is an encoding, not a prefix: base64(user:pass) [D:http] —
/// which is why it is a union case the runner encodes, not a header a
/// caller would build by hand and get wrong
let basicToken (user: string) (password: string) : string =
    let raw = Text.Encoding.UTF8.GetBytes($"{user}:{password}")
    Convert.ToBase64String raw

/// the urlencoded body [D:http-dx]: pairs percent-encoded with the same
/// escaper withQuery uses, so a space or `&` in a value cannot break the
/// body — the OAuth-token-endpoint shape, shared by the client send path
/// and a serve response
let formUrlEncode (pairs: (string * string) seq) : string =
    pairs
    |> Seq.map (fun (k, v) -> $"{Uri.EscapeDataString k}={Uri.EscapeDataString v}")
    |> String.concat "&"

/// the header-injection byte class [D:http-header-bytes]: CR, LF or NUL
/// in a header name or value forges a second header / splits a response.
/// Refused at both crossings (the client send path and a serve response)
/// — the same one-boundary refusal the argv NUL guard performs, never
/// the silent forge (client) or drop (server) the review found. Shared
/// so the two directions agree byte-for-byte.
let private headerByteName (c: char) : string option =
    match int c with
    | 13 -> Some "CR"
    | 10 -> Some "LF"
    | 0 -> Some "NUL"
    | _ -> None

/// the offending byte's name and where it sits (name or value), or None
/// if the pair is clean — the caller words the located refusal
let headerInjection (name: string, value: string) : (string * string) option =
    match name |> Seq.tryPick headerByteName with
    | Some b -> Some(b, "name")
    | None ->
        match value |> Seq.tryPick headerByteName with
        | Some b -> Some(b, "value")
        | None -> None

/// the located refusal message [D:http-header-bytes], worded like the
/// argv-NUL guard's: name the header, the byte, and where it sat. `who`
/// is the crossing ("request header" / "response header") so the two
/// directions read the same shape.
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

/// an RFC 9110 tchar — the byte class of a header name and a method
/// token [D:serve-method] [D:http-header-unsendable]
let isTchar (c: char) : bool =
    (c >= 'A' && c <= 'Z')
    || (c >= 'a' && c <= 'z')
    || (c >= '0' && c <= '9')
    || "!#$%&'*+-.^_`|~".Contains c

let private describeChar (c: char) : string =
    if c = ' ' then "a space"
    elif c < ' ' || c = '\127' then $"U+{int c:X4}"
    else $"'{c}'"

/// why a header cannot cross the wire as given, or None
/// [D:http-header-unsendable]: a name that is not a token, or a value
/// with a control character or a non-ASCII one. Both crossings refused
/// these by dropping the header (the server) or skipping it (a client
/// name); CR/LF/NUL are the injection class, checked first with their
/// own message [D:http-header-bytes]. A secret value is described by the
/// UTF-8 byte offset of the offending character, never the character
/// [D:secret-header-offset].
let headerUnsendableWith (secret: bool) (name: string, value: string) : string option =
    let which (kind: string) (i: int) =
        if secret then
            $"a {kind} character at byte offset {System.Text.Encoding.UTF8.GetByteCount(value.AsSpan(0, i))} of the secret value"
        else
            $"the {kind} character {describeChar value[i]}"

    if name = "" then
        Some "its name is empty"
    else
        match name |> Seq.tryFind (isTchar >> not) with
        | Some c ->
            Some $"its name carries {describeChar c}, which a header name cannot (letters, digits and !#$%%&'*+-.^_`|~ only)"
        | None ->
            match value |> Seq.tryFindIndex (fun c -> (c < ' ' && c <> '\t') || c = '\127') with
            | Some i -> Some("its value carries " + which "control" i)
            | None ->
                match value |> Seq.tryFindIndex (fun c -> c > '\127') with
                | Some i ->
                    Some(
                        "its value carries "
                        + which "non-ASCII" i
                        + " (header values are ASCII; percent-encode or base64 it)"
                    )
                | None -> None

let headerUnsendable (pair: string * string) : string option = headerUnsendableWith false pair

/// the located refusal for an unsendable header [D:http-header-unsendable],
/// the injection message's shape: the crossing, the header, the reason
let headerUnsendableMessage (who: string) (name: string) (reason: string) : string =
    $"{who} '{name}' cannot be sent — {reason}"

/// a request header with no place on the wire [D:http-header-unsendable]:
/// a body header (Content-Type, Expires, …) on a request without a body
/// — HttpClient has nowhere to put it. The BCL's own header tables
/// decide, so weir never keeps a second list of content headers.
let requestHeaderHasNoPlace (hasBody: bool) (name: string, value: string) : bool =
    use msg = new HttpRequestMessage()

    if msg.Headers.TryAddWithoutValidation(name, value) then
        false
    elif hasBody then
        use c = new ByteArrayContent([||])
        not (c.Headers.TryAddWithoutValidation(name, value))
    else
        true

/// refuse the first unsendable request header, or pass
/// [D:http-header-unsendable] — the client crossing; runs after the
/// injection walk, so CR/LF/NUL keep their own message
let refuseUnsendableRequestHeader
    (fail: string -> unit)
    (hasBody: bool)
    (secretNames: Set<string>)
    (headers: (string * string) list)
    : unit =
    for (k, v) in headers do
        match headerUnsendableWith (secretNames.Contains(k.ToLowerInvariant())) (k, v) with
        | Some reason -> fail (headerUnsendableMessage "request header" k reason)
        | None ->
            if requestHeaderHasNoPlace hasBody (k, v) then
                fail (
                    headerUnsendableMessage
                        "request header"
                        k
                        "it is a body header and this request has no body (give the request a body or drop the header)"
                )
