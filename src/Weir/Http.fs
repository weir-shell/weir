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
type Req = {
    Method: string
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
    Insecure: bool
}

type Resp = {
    Status: int
    Headers: (string * string) list
    Body: string
}

// a transport failure in its own words [D:transport-words]: the four
// probed shapes each carry what the caller needs to word a repair;
// Other keeps the root message under the cannot-reach umbrella
type TransportError =
    | Timeout of ms: int64
    | NoSuchHost
    | Refused of port: int
    | TlsUntrusted
    // the TLS handshake itself failed — the peer did not speak TLS (a
    // plain-http port) or offered no protocol weir accepts; never a trust
    // question, so never the insecure repair [D:tls-handshake-vs-trust]
    | TlsHandshake
    // the peer's answer was not HTTP (a status line that would not parse)
    | InvalidResponse
    // the URL cannot be sent to: not absolute, no host, or a scheme other
    // than http(s) — refused before any connection [D:http-url-refused]
    | BadUrl of why: string
    | OtherTransport of root: string
    // not a transport failure but the same Error channel: the response
    // declared a charset .NET cannot decode
    | UnknownCharset of charset: string * status: int
    // the redirect bound ran out — a loop, never a 3xx handed back as data
    | RedirectLoop of hops: int * last: string
    // the environment's proxy refused the CONNECT tunnel with this status
    // (407/403/502) [D:proxy-refusal-words]
    | ProxyRefused of status: int
    // the peer closed the connection before a response (through a proxy,
    // the proxy did — weir's socket only reaches it)
    | ConnectionEnded
    // the environment names a proxy for the scheme the runtime cannot
    // use (a malformed URL, a port past 65535) [D:proxy-env-malformed]
    | ProxyEnvUnusable of var: string * value: string

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
        | :? HttpRequestException as h when
            h.HttpRequestError = HttpRequestError.ProxyTunnelError && h.StatusCode.HasValue
            ->
            ProxyRefused(int h.StatusCode.Value)
        | :? HttpRequestException as h when h.HttpRequestError = HttpRequestError.ResponseEnded -> ConnectionEnded
        | :? HttpRequestException as h when h.HttpRequestError = HttpRequestError.InvalidResponse -> InvalidResponse
        | :? OperationCanceledException -> Timeout timeoutMs
        // the trust verdict and a broken handshake raise the same type;
        // send tells them apart by whether the certificate was ever
        // checked [D:tls-handshake-vs-trust]
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

// a `host[:port]` authority, bracketed IPv6 included
let private hostPortRx =
    Text.RegularExpressions.Regex(@"^(\[[^\]@]*\]|[^:@\[\]\s]*)(:\d*)?$")

/// redact a URL's userinfo for a diagnostic [D:url-redact]: a credential
/// in `scheme://user:pass@host/...` would otherwise print verbatim
/// (terminal, CI log, REPL). Replace the whole userinfo span with `***`,
/// keeping scheme and everything from the host on — a credential-free
/// URL is returned unchanged, so the target is still named in full.
/// Purely textual, so it works on a URL `Uri` could not parse (exactly
/// the transport fallback's case). The authority starts after `://`, a
/// leading `//`, or at the start of a scheme-less `user:pass@host`. A
/// password may hold a raw `/ ? # @`, so the span runs through the LAST
/// `@` of the authority (which ends at the first `/ ? # \`), and further
/// when in doubt: through a later `@` when the authority before the first
/// delimiter is not a `host` or carries an explicit port (a raw `/` cut a
/// password — `user:2024/pw` reads as `host:port`), or when that `@` is
/// followed by a dotted, ported, bracketed or localhost host.
/// An `@` in a path or query otherwise stays.
let private userinfoSpan (url: string) : (int * int) option =
    let authStart =
        match url.IndexOf "://" with
        | -1 -> if url.StartsWith "//" then 2 else 0
        | i -> i + 3

    let isDelim c =
        c = '/' || c = '?' || c = '#' || c = '\\'

    let delimFrom (i: int) =
        let mutable j = i

        while j < url.Length && not (isDelim url[j]) do
            j <- j + 1

        j

    let authEnd = delimFrom authStart

    let hostish (h: string) =
        h.Length > 0
        && hostPortRx.IsMatch h
        && (h.Contains '.'
            || h.Contains ':'
            || h.StartsWith "["
            || h.Equals("localhost", StringComparison.OrdinalIgnoreCase))

    let authority = hostPortRx.Match(url.Substring(authStart, authEnd - authStart))
    let authorityCut = not authority.Success

    // `host:port` is also how `user:<digits>` reads once a raw delimiter
    // cut the password, so an explicit port is ambiguous too
    let authorityPorted = authority.Success && authority.Groups[2].Length > 1

    let rec pick (from: int) =
        match if from < authStart then -1 else url.LastIndexOf('@', from) with
        | at when at < authStart -> None
        | at when at < authEnd -> Some at
        | at ->
            let host = url.Substring(at + 1, delimFrom (at + 1) - at - 1)

            if
                ((authorityCut || authorityPorted)
                 && host.Length > 0
                 && not (host |> Seq.exists Char.IsWhiteSpace))
                || hostish host
            then
                Some at
            else
                pick (at - 1)

    if url.Length = 0 then
        None
    else
        pick (url.Length - 1) |> Option.map (fun at -> authStart, at)

let redactUrl (url: string) : string =
    match userinfoSpan url with
    | Some(s, at) -> url.Substring(0, s) + "***@" + url.Substring(at + 1)
    | None -> url

/// a URL with its userinfo dropped, for what weir writes to a committed
/// file [D:lock-no-userinfo]; a credential-free URL is returned
/// unchanged. The client's own parse: the authority ends at the first
/// `/ ? #`, its userinfo at the last `@` before that
let stripUserinfo (url: string) : string =
    match url.IndexOf "://" with
    | -1 -> url
    | schemeEnd ->
        let authStart = schemeEnd + 3

        let authEnd =
            match url.IndexOfAny([| '/'; '?'; '#' |], authStart) with
            | -1 -> url.Length
            | i -> i

        match url.LastIndexOf('@', authEnd - 1, authEnd - authStart) with
        | -1 -> url
        | at -> url.Substring(0, authStart) + url.Substring(at + 1)

/// a proxy variable's value with its userinfo masked [D:proxy-env-malformed]:
/// the runtime takes the userinfo through the value's last `@`, wherever
/// it is, so the mask does too
let redactProxyValue (value: string) : string =
    let authStart =
        match value.IndexOf "://" with
        | -1 -> if value.StartsWith "//" then 2 else 0
        | i -> i + 3

    match value.LastIndexOf '@' with
    | at when at >= authStart -> value.Substring(0, authStart) + "***@" + value.Substring(at + 1)
    | _ -> value

/// every URL's userinfo in a runtime's text redacted [D:url-redact] — a
/// .NET message can quote a proxy URI whole, credentials included; a URL
/// (`scheme://` or `//`) runs to the next blank
let redactUrlsIn (text: string) : string =
    Text.RegularExpressions.Regex.Replace(text, @"(?:[A-Za-z][A-Za-z0-9+.\-]*:)?//\S+", fun m -> redactUrl m.Value)

let transportMessage (host: string) (err: TransportError) : string =
    match err with
    | Timeout ms -> $"timed out after {fmtMs ms} reaching {host}"
    | NoSuchHost -> $"cannot resolve {host} — no such host"
    | Refused port -> $"{host}:{port} refused the connection — nothing is listening there"
    | TlsUntrusted -> $"cannot establish TLS with {host} — the certificate is not trusted"
    | TlsHandshake ->
        $"cannot establish TLS with {host} — the server did not speak TLS on this port (a plain http:// endpoint, or one offering no TLS version weir accepts); the certificate was never checked"
    | InvalidResponse -> $"{host} answered with something that is not HTTP — its status line could not be read"
    | BadUrl why -> why
    // the residual keeps the runtime's root message, but framed: the host
    // and what weir knows (no response came) always precede it
    | OtherTransport root ->
        $"cannot reach {host} — the request failed before any response arrived: {Weir.Types.quoteData (redactUrlsIn root)}"
    | ConnectionEnded -> $"{host} closed the connection before answering"
    | ProxyRefused status -> $"the proxy refused to tunnel to {host}: {status}"
    | ProxyEnvUnusable(var, value) ->
        let shown = redactProxyValue value

        $"{var} is set but is not a usable proxy URL ('{Weir.Types.quoteData shown}') — weir does not fall back to reaching {host} directly; fix it or unset it"
    | UnknownCharset(cs, status) ->
        $"{host} answered {status} with a body in charset '{Weir.Types.quoteData cs}', which weir cannot decode — weir decodes utf-8, utf-16, utf-32, us-ascii and iso-8859-1; a mislabelled body is never guessed at"
    | RedirectLoop(hops, last) ->
        $"{host}: still redirecting after {hops} hops (the last at {Weir.Types.quoteData (redactUrl last)}) — a redirect loop"

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
                .ContinueWith(fun (t: Threading.Tasks.Task) ->
                    if not t.IsCanceled then
                        arm (left - sliceMs))
            |> ignore

    arm ms

let private maxTimerMs = int64 Int32.MaxValue - 1L

/// does a NO_PROXY value bypass `host` (a Uri's Host, IPv6 bracketed)?
/// curl's matcher [D:noproxy-curl]: entries split on commas and blanks
/// (a blank not followed by a comma ends the list); a name entry matches
/// the host or any subdomain, one leading and one trailing dot ignored
/// on either side; an IP-literal host matches only address entries,
/// optionally `/bits` (none, or the full width, is an exact address;
/// past the width never), bracket-less [D:noproxy-v6] [D:noproxy-cidr];
/// a name is never resolved to match. A `*` entry bypasses every host
/// [D:noproxy-star].
let noProxyMatch (noProxy: string) (host: string) : bool =
    let isBlank c = c = ' ' || c = '\t'

    let tokens =
        let out = ResizeArray<string>()
        let mutable i = 0
        let mutable go = true

        while go && i < noProxy.Length do
            while i < noProxy.Length && isBlank noProxy[i] do
                i <- i + 1

            let start = i

            while i < noProxy.Length && not (isBlank noProxy[i]) && noProxy[i] <> ',' do
                i <- i + 1

            if i > start then
                out.Add(noProxy.Substring(start, i - start))

            while i < noProxy.Length && isBlank noProxy[i] do
                i <- i + 1

            if i < noProxy.Length && noProxy[i] = ',' then
                while i < noProxy.Length && noProxy[i] = ',' do
                    i <- i + 1
            else
                go <- false

        List.ofSeq out

    // inet_pton's strict forms: a dotted quad, or an IPv6 text without
    // brackets, zone or port
    let strictV4 (t: string) =
        if Text.RegularExpressions.Regex.IsMatch(t, @"^[0-9]{1,3}(\.[0-9]{1,3}){3}$") then
            match Net.IPAddress.TryParse t with
            | true, a when a.AddressFamily = Net.Sockets.AddressFamily.InterNetwork -> Some(a.GetAddressBytes())
            | _ -> None
        else
            None

    let strictV6 (t: string) =
        if t.Contains ':' && not (t.IndexOfAny [| '['; ']'; '%' |] >= 0) then
            match Net.IPAddress.TryParse t with
            | true, a when a.AddressFamily = Net.Sockets.AddressFamily.InterNetworkV6 -> Some(a.GetAddressBytes())
            | _ -> None
        else
            None

    // atoi: the leading digits, 0 when none
    let atoi (t: string) =
        let digits = t |> Seq.takeWhile Char.IsAsciiDigit |> Seq.truncate 9 |> Array.ofSeq

        if digits.Length = 0 then 0 else int (String digits)

    let prefixMatch (hb: byte[]) (nb: byte[]) (bits: int) =
        Seq.forall
            (fun i ->
                let keep = min 8 (max 0 (bits - 8 * i))
                let mask = byte ((0xFF00 >>> keep) &&& 0xFF)
                (nb[i] &&& mask) = (hb[i] &&& mask))
            [ 0 .. hb.Length - 1 ]

    let ipMatch (hb: byte[]) (parse: string -> byte[] option) (token: string) =
        let addr, bits =
            match token.IndexOf '/' with
            | -1 -> token, 0
            | k -> token.Substring(0, k), atoi (token.Substring(k + 1))

        let width = 8 * hb.Length

        if bits > width then
            false
        else
            match parse addr with
            | Some nb when nb.Length = hb.Length -> prefixMatch hb nb (if bits = 0 then width else bits)
            | _ -> false

    let toks = tokens

    if host = "" then
        false
    elif List.contains "*" toks then
        true
    else
        let bare =
            if host.StartsWith "[" && host.EndsWith "]" then
                host.Substring(1, host.Length - 2)
            else
                host

        match strictV6 bare, strictV4 bare with
        | Some hb, _ -> toks |> List.exists (ipMatch hb strictV6)
        | None, Some hb -> toks |> List.exists (ipMatch hb strictV4)
        | None, None ->
            // a non-ASCII host compares by its punycode; an entry as
            // written [D:noproxy-idn]
            let ascii (s: string) =
                if s |> Seq.exists (fun c -> c > '\127') then
                    try
                        Globalization.IdnMapping().GetAscii s
                    with _ ->
                        s
                else
                    s

            let name =
                if bare.EndsWith "." then
                    bare.Substring(0, bare.Length - 1)
                else
                    bare

            let name = ascii name

            toks
            |> List.exists (fun t ->
                let t = if t.EndsWith "." then t.Substring(0, t.Length - 1) else t
                let t = if t.StartsWith "." then t.Substring 1 else t

                if t.Length = name.Length then
                    String.Equals(t, name, StringComparison.OrdinalIgnoreCase)
                elif t.Length < name.Length then
                    name[name.Length - t.Length - 1] = '.'
                    && name.EndsWith(t, StringComparison.OrdinalIgnoreCase)
                else
                    false)

// the runtime's proxy with weir's NO_PROXY rule [D:noproxy-curl]: the
// runtime matches a plain entry exactly and a dot entry as a text
// suffix (an IP too), so with NO_PROXY set weir decides the bypass and
// only asks the runtime which proxy the scheme takes; without it the
// runtime (a system proxy's own bypass list) decides
type private EnvNoProxy(inner: Net.IWebProxy) =
    let raw =
        lazy
            (match Environment.GetEnvironmentVariable "no_proxy" with
             | null
             | "" ->
                 match Environment.GetEnvironmentVariable "NO_PROXY" with
                 | null -> ""
                 | v -> v
             | v -> v)

    let proxyFor (u: Uri) : Uri =
        if raw.Value.Trim() = "" then
            inner.GetProxy u
        elif noProxyMatch raw.Value u.Host then
            null
        else
            // the runtime may bypass a host weir's rule does not: ask it
            // of hosts no entry is likely to name (TEST-NET-1, .invalid)
            [ u.Host; "192.0.2.1"; "weir-proxy-probe.invalid" ]
            |> List.tryPick (fun h ->
                let q = if h = u.Host then u else UriBuilder(u, Host = h).Uri
                Option.ofObj (inner.GetProxy q))
            |> Option.toObj

    interface Net.IWebProxy with
        member _.Credentials
            with get () = inner.Credentials
            and set v = inner.Credentials <- v

        member _.GetProxy(u: Uri) = proxyFor u

        member _.IsBypassed(u: Uri) =
            if raw.Value.Trim() = "" then
                inner.IsBypassed u
            else
                isNull (proxyFor u)

/// would the runtime take this proxy variable's value? The runtime's own
/// reading (HttpEnvironmentProxy): an optional http/https/socks scheme
/// (any other `://` refuses), userinfo through the last `@`, a port of
/// leading digits that must fit 0-65535 (trailing text ignored), then the
/// host must make a Uri [D:proxy-env-malformed]
let private envProxyUsable (value: string) : bool =
    let schemes = [
        "http://", "http", 80
        "https://", "https", 443
        "socks4://", "socks4", 80
        "socks5://", "socks5", 80
        "socks4a://", "socks4a", 80
    ]

    let picked =
        schemes
        |> List.tryFind (fun (p, _, _) -> value.StartsWith(p, StringComparison.OrdinalIgnoreCase))

    match picked with
    | None when value.Contains "://" -> false
    | _ ->
        let protocol, defaultPort, rest =
            match picked with
            | Some(p, proto, port) -> proto, port, value.Substring p.Length
            | None -> "http", 80, value

        let rest =
            match rest.LastIndexOf '@' with
            | -1 -> rest
            | at -> rest.Substring(at + 1)

        let v6End = rest.IndexOf ']'
        let colon = rest.LastIndexOf ':'

        let hostPort =
            if colon = -1 || (v6End <> -1 && colon < v6End) then
                Some(rest, defaultPort)
            else
                let digits = rest.Substring(colon + 1) |> Seq.takeWhile Char.IsDigit |> Seq.length

                match UInt16.TryParse(rest.AsSpan(colon + 1, digits)) with
                | true, port -> Some(rest.Substring(0, colon), int port)
                | _ -> None

        match hostPort with
        | None -> false
        | Some(host, port) ->
            try
                UriBuilder(protocol, host, port).Uri |> ignore
                true
            with _ ->
                false

exception private ProxyEnvBad of string * string

/// refuse before connecting when a proxy variable the runtime consults
/// for `url`'s scheme is set but the runtime could not use it
/// [D:proxy-env-malformed]: the runtime skips a malformed value — to the
/// next variable, or a direct connect; curl refuses. The variables in the
/// runtime's order: the scheme's lower case name, its upper case name
/// (HTTP_PROXY under CGI too), all_proxy, ALL_PROXY.
/// The first one set being malformed (or no proxy resolving) refuses. A
/// host NO_PROXY bypasses goes direct as asked.
let checkEnvProxy (url: string) : unit =
    let env (n: string) =
        match Environment.GetEnvironmentVariable n with
        | null -> None
        | v when v.Trim() = "" -> None
        | v -> Some(n, v)

    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, u when u.Scheme = "http" || u.Scheme = "https" ->
        let order =
            if u.Scheme = "http" then
                [ "http_proxy"; "HTTP_PROXY" ]
            else
                [ "https_proxy"; "HTTPS_PROXY" ]
            @ [ "all_proxy"; "ALL_PROXY" ]

        let set = order |> List.choose env

        let bypassed () =
            let raw =
                match env "no_proxy" with
                | Some(_, v) -> v
                | None -> env "NO_PROXY" |> Option.map snd |> Option.defaultValue ""

            raw <> "" && noProxyMatch raw u.Host

        // the runtime reads past a malformed value only, so the first set
        // variable is the one it takes or skips
        match set with
        | (var, value) :: _ when not (bypassed ()) ->
            let p = EnvNoProxy(HttpClient.DefaultProxy) :> Net.IWebProxy

            if not (envProxyUsable value) || isNull (p.GetProxy u) then
                raise (ProxyEnvBad(var, value))
        | _ -> ()
    | _ -> ()

/// the host a transport error names: the URL's host, or the whole URL
/// when it would not parse — redacted [D:url-redact], since a
/// `user:pass@host` credential must not print verbatim
let failingHost (url: string) : string =
    try
        Uri(url).Host
    with _ ->
        Weir.Types.quoteData (redactUrl url)

/// the port the transport classifier reports (0 when unparseable)
let failingPort (url: string) : int =
    try
        Uri(url).Port
    with _ ->
        0

/// why a URL cannot be sent to, or None [D:http-url-refused]: not an
/// absolute URL, a scheme other than http(s), or no host — the shapes the
/// runtime words as its own API ("BaseAddress must be set", "the 'file'
/// scheme is not supported"). The URL prints redacted [D:url-redact].
let urlUnsendable (url: string) : string option =
    let shown = Weir.Types.quoteData (redactUrl url)

    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, u when u.Scheme = Uri.UriSchemeHttp || u.Scheme = Uri.UriSchemeHttps ->
        if u.Host = "" then
            Some $"'{shown}' names no host — an http(s) URL needs one (https://host/path)"
        else
            None
    // a bare path reads as an implicit file: URI on Unix; only a written
    // scheme is named as one
    | true, u when url.Contains "://" ->
        Some
            $"'{shown}' uses the '{Weir.Types.quoteData u.Scheme}' scheme — Http sends to http:// and https:// URLs only"
    | _ -> Some $"'{shown}' is not a URL weir can send to — an absolute http(s) URL is needed (https://host/path)"

/// the environment's proxy for a client handler, NO_PROXY matched as above
let useEnvProxy (handler: HttpClientHandler) : unit =
    handler.Proxy <- EnvNoProxy(HttpClient.DefaultProxy)

/// a transport failure's words and case [D:transport-words]. Through a
/// proxy, weir's socket only ever reaches the proxy, so a refused or
/// unresolved connect names the proxy, not the target [D:proxy-err-names-proxy]
let private viaProxyFailure (timeoutMs: int64) (url: string) (ex: exn) : string * TransportError =
    let proxy =
        try
            let p = EnvNoProxy(HttpClient.DefaultProxy) :> Net.IWebProxy
            let u = Uri url
            if p.IsBypassed u then None else Option.ofObj (p.GetProxy u)
        with _ ->
            None

    let direct () =
        let err = classifyTransport timeoutMs (failingPort url) ex
        transportMessage (failingHost url) err, err

    match proxy with
    | None -> direct ()
    | Some p ->
        let via =
            $"(the environment's proxy for {failingHost url}; NO_PROXY lists the hosts that bypass it)"

        // the proxy named by host:port only — a Uri's userinfo never prints
        let named = $"{p.Host}:{p.Port}"

        match classifyTransport timeoutMs p.Port ex with
        | Refused port as err ->
            $"the proxy {p.Host}:{port} refused the connection — nothing is listening there {via}", err
        | NoSuchHost as err -> $"cannot resolve the proxy {p.Host} — no such host {via}", err
        // a CONNECT refusal is the proxy's answer, never the target's
        // [D:proxy-refusal-words]
        | ProxyRefused status as err ->
            let why =
                match status with
                | 407 -> " Proxy Authentication Required — the proxy URL's credentials are missing or wrong"
                | 403 -> " Forbidden"
                | 502 -> " Bad Gateway"
                | 504 -> " Gateway Timeout"
                | _ -> ""

            $"the proxy {named} refused to tunnel to {failingHost url}: {status}{why} {via}", err
        | ConnectionEnded as err -> $"the proxy {named} closed the connection before answering {via}", err
        | _ -> direct ()

let transportFailure (timeoutMs: int64) (url: string) (ex: exn) : string * TransportError =
    let rec unusable (e: exn) =
        match e with
        | null -> None
        | ProxyEnvBad(var, value) -> Some(ProxyEnvUnusable(var, value))
        | e -> unusable e.InnerException

    match unusable ex with
    | Some err -> transportMessage (failingHost url) err, err
    | None -> viaProxyFailure timeoutMs url ex

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
let rec send (cancel: Threading.CancellationToken) (req: Req) : Result<Resp, string * TransportError> =
    // a URL the transport could not even address is refused here, in
    // weir's words, before a connection is attempted [D:http-url-refused]
    match urlUnsendable req.Url with
    | Some why -> Error(why, BadUrl why)
    | None -> sendChecked cancel req

and private sendChecked (cancel: Threading.CancellationToken) (req: Req) : Result<Resp, string * TransportError> =
    // the hop in flight: a transport failure names the host that failed,
    // never the original one a redirect left behind
    let mutable curUrl = req.Url

    // set when this hop's certificate reached validation: a TLS failure
    // before that is a broken handshake, never a trust verdict
    // [D:tls-handshake-vs-trust]. The callback keeps the platform's
    // own verdict unless insecure.
    let certChecked = ref false

    try
        // insecure is honoured per request only — the default path keeps
        // the platform's TLS verdict [D:http-s2]. Auto-redirect is
        // off so weir controls the credential-drop on an origin change
        // [D:secret-redirect] — the BCL would re-send secretHeaders.
        use handler = new HttpClientHandler()
        handler.AllowAutoRedirect <- false
        useEnvProxy handler

        handler.ServerCertificateCustomValidationCallback <-
            (fun _ _ _ errors ->
                certChecked.Value <- true
                req.Insecure || errors = Net.Security.SslPolicyErrors.None)

        use client = new HttpClient(handler)
        // the deadline rides a token, never client.Timeout's 24.8-day
        // ceiling [D:long-waits]; cancelled when the send returns
        client.Timeout <- Threading.Timeout.InfiniteTimeSpan
        use deadline = new Threading.CancellationTokenSource()

        use _stop = {
            new IDisposable with
                member _.Dispose() = deadline.Cancel()
        }

        cancelAfterSliced maxTimerMs deadline req.TimeoutMs
        use _cut = cancel.Register(fun () -> deadline.Cancel())

        // build one request message for a hop: `method`/`url`, the current
        // header set (already credential-filtered by the caller loop), the
        // body when the method still carries one, and the UA default.
        let buildMessage
            (method: string)
            (url: string)
            (headers: (string * string) list)
            (body: (string * string) option)
            =
            let msg = new HttpRequestMessage(HttpMethod(method), url)

            let isContentType (k: string) =
                String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase)

            // the body is attached before headers so a content-header (e.g. a
            // caller-set Content-Type override) can land on the content. An
            // explicit Content-Type WINS over the body kind's default and
            // exactly one is sent [D:http-content-type-override] — the
            // User-Agent law; the first caller pair is the one taken
            match body with
            | Some(ct, b) ->
                // bytes, not StringContent: no default text/plain to displace
                let content = new ByteArrayContent(Text.Encoding.UTF8.GetBytes b)

                let declared =
                    headers |> List.tryPick (fun (k, v) -> if isContentType k then Some v else None)

                content.Headers.TryAddWithoutValidation("Content-Type", defaultArg declared ct)
                |> ignore

                msg.Content <- content
            | None -> ()

            for (k, v) in headers do
                // request headers first; a content header (Content-*) lands on
                // the content instead — TryAddWithoutValidation keeps the bytes
                // verbatim (no folding, no re-encoding). Content-Type is
                // already placed above, once.
                if isContentType k && msg.Content <> null then
                    ()
                elif not (msg.Headers.TryAddWithoutValidation(k, v)) then
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
        let mutable curHeaders = req.Headers
        let mutable curBody = req.Body
        let mutable hops = 0
        let mutable result: HttpResponseMessage = null

        let mutable go = true

        while go do
            checkEnvProxy curUrl
            let msg = buildMessage curMethod curUrl curHeaders curBody
            certChecked.Value <- false
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
            [
                for h in resp.Headers.NonValidated do
                    for v in h.Value -> h.Key, v
            ]
            @ (match resp.Content with
               | null -> []
               | c -> [
                   for h in c.Headers.NonValidated do
                       for v in h.Value -> h.Key, v
                 ])

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

        Ok {
            Status = int resp.StatusCode
            Headers = headers
            Body = body
        }
    with ex ->
        // a loop is the request's own failure (its last hop is in the
        // message); anything else names the hop that failed
        match ex with
        | CharsetUnknown(cs, status) ->
            let err = UnknownCharset(cs, status)
            Error(transportMessage (failingHost curUrl) err, err)
        | TooManyRedirects(hops, last) ->
            let err = RedirectLoop(hops, last)
            Error(transportMessage (failingHost req.Url) err, err)
        | _ ->
            match transportFailure req.TimeoutMs curUrl ex with
            // with verification off, or before any certificate was
            // checked, nothing was distrusted: the handshake itself
            // failed [D:tls-handshake-vs-trust]
            | _, TlsUntrusted when req.Insecure || not certChecked.Value ->
                transportMessage (failingHost curUrl) TlsHandshake, TlsHandshake
            | failure -> failure
            |> Error

/// Basic auth is an encoding, not a prefix: base64(user:pass) [D:http] —
/// which is why it is a union case the runner encodes, not a header a
/// caller would build by hand and get wrong
let basicToken (user: string) (password: string) : string =
    let raw = Text.Encoding.UTF8.GetBytes($"{user}:{password}")
    Convert.ToBase64String raw

/// percent-decode one userinfo part to its bytes (RFC 3986 2.1): an
/// escape is the byte it names, anything else is its UTF-8
let private pctDecodeBytes (s: string) : byte[] =
    let out = ResizeArray<byte>()
    let lit = Text.StringBuilder()

    let flush () =
        out.AddRange(Text.Encoding.UTF8.GetBytes(lit.ToString()))
        lit.Clear() |> ignore

    let mutable i = 0

    while i < s.Length do
        if
            s[i] = '%'
            && i + 2 < s.Length
            && Uri.IsHexDigit s[i + 1]
            && Uri.IsHexDigit s[i + 2]
        then
            flush ()
            out.Add(byte (Convert.ToInt32(s.Substring(i + 1, 2), 16)))
            i <- i + 3
        else
            lit.Append s[i] |> ignore
            i <- i + 1

    flush ()
    out.ToArray()

/// the Authorization a URL's userinfo carries, as curl sends it
/// [D:url-redact]: `user:pass@` is `Basic base64(user:pass)`, each part
/// percent-decoded to its bytes, a missing password empty. None for a
/// URL without userinfo or one that does not parse
let userinfoAuth (url: string) : (string * string) option =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | true, u when u.UserInfo <> "" ->
        let ui = u.UserInfo

        let user, pass =
            match ui.IndexOf ':' with
            | -1 -> ui, ""
            | i -> ui.Substring(0, i), ui.Substring(i + 1)

        let raw = Array.concat [ pctDecodeBytes user; [| byte ':' |]; pctDecodeBytes pass ]
        Some("Authorization", "Basic " + Convert.ToBase64String raw)
    | _ -> None

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
    $"{who} '{Weir.Types.quoteData name}' carries a {byteName} byte in its {where} — that forges a second header (response splitting / header injection); a header name or value cannot contain CR, LF or NUL"

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
    else $"'{Weir.Types.quoteData (string c)}'"

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
            Some
                $"its name carries {describeChar c}, which a header name cannot (letters, digits and !#$%%&'*+-.^_`|~ only)"
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

/// why a method cannot be sent, or None [D:http-method-token]: `Other v`
/// carries v verbatim to the wire, so v must be an RFC 9110 token — the
/// class the server crossing answers 400 to otherwise [D:serve-method].
/// Refused before anything is sent, like an unsendable header.
let methodUnsendable (method: string) : string option =
    if method = "" then
        Some "the method is empty — Other needs a method token such as \"PURGE\""
    else
        match method |> Seq.tryFind (isTchar >> not) with
        | Some c ->
            Some
                $"the method '{Weir.Types.quoteData method}' cannot be sent — it carries {describeChar c}, which a method token cannot (letters, digits and !#$%%&'*+-.^_`|~ only)"
        | None -> None

/// the located refusal for an unsendable header [D:http-header-unsendable],
/// the injection message's shape: the crossing, the header, the reason
let headerUnsendableMessage (who: string) (name: string) (reason: string) : string =
    $"{who} '{Weir.Types.quoteData name}' cannot be sent — {reason}"

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
        // a framing header is the transport's to write, never the
        // caller's [D:http-header-unsendable]: the server crossing drops
        // it, the client refuses it — the runtime would otherwise raise
        // its own words mid-send
        | None when String.Equals(k, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ->
            fail (
                headerUnsendableMessage
                    "request header"
                    k
                    "it frames the body, which the transport does on its own (a body is sent with its Content-Length); drop it"
            )
        // Content-Length is the same framing: the transport sets it from
        // the body itself, and a caller's value either contradicts the
        // body (the runtime's own words mid-send) or is ignored
        | None when String.Equals(k, "Content-Length", StringComparison.OrdinalIgnoreCase) ->
            fail (
                headerUnsendableMessage
                    "request header"
                    k
                    "it frames the body, which the transport does on its own (the length is taken from the body itself); drop it"
            )
        | None ->
            if requestHeaderHasNoPlace hasBody (k, v) then
                fail (
                    headerUnsendableMessage
                        "request header"
                        k
                        "it is a body header and this request has no body (give the request a body or drop the header)"
                )
