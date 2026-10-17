module Weir.Serve

// the typed server boundary [D:http-serve] — a standalone .NET leg
// (like Http.fs, the client twin): primitives in, primitives out, no
// Value dependency, so Builtins owns the Value<->primitive translation.
// The server is Kestrel from the ASP.NET Core shared framework, driven
// directly — no generic host, no console lifetime, no log provider
// [D:serve-kestrel]. Kestrel pushes a request delegate per request; Eval
// pulls (accept, readRequest, writeResponse), so the delegate parks each
// request on a channel and awaits the worker's finish. This module owns
// the socket lifetime: Serve.start binds, Serve.stop unbinds — the
// within-serve scope wires stop to the finally + signal hook so the
// socket frees on every exit [D:http-serve].

open System
open System.Net
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options

// the request as flat primitives, the server mirror of Http.Req: no
// url (path + query instead), no auth/timeout/insecure (client
// concerns) — the shared family is HttpMethod + header pairs + the
// body union, never a second copy [D:http-serve]
type SReq =
    { Method: string
      Path: string
      // the raw query string without the leading '?' (empty when none);
      // the handler splits it (weir's Str.splitOnce / Http.withQuery
      // shape), so the server stays out of query-DSL business
      Query: string
      Headers: (string * string) list
      Body: string }

// the response body, the server side of the shared HttpBody union
// [D:http-serve]: NoBody/Text/Json mirror the client cases; Stream is
// the server-only case — a lazy line source written chunked as it is
// pulled (SSE-shaped), the pattern print set for streaming, server-side
type SBody =
    | RNoBody
    | RText of string
    | RJson of string
    // the urlencoded case [D:http-dx], pre-encoded by the caller (the
    // shared Http.formUrlEncode) — written like RText with its own type
    | RForm of string
    // the lazy body [D:http-serve]: each element written and flushed as
    // produced, so a client sees bytes before the seq ends (the
    // incremental law the acceptance pins). Chunked transfer, one SSE
    // event per element, one `data:` line per line of it.
    | RStream of seq<string>

type SResp =
    { Status: int
      Headers: (string * string) list
      Body: SBody }

/// the request-body bounds, applied as the body is read
/// [D:serve-body-timeout] [D:serve-max-body]
type Limits = { BodyTimeoutMs: int; MaxBody: int64 }

// the body as the request delegate read it, before any handler slot is
// taken [D:serve-kestrel]: a slow or oversized body is settled here, so
// it never holds one of maxConcurrent
type private BodyRead =
    | BodyText of string
    | BodyTimedOut
    | BodyOverMax

/// one parked request: Kestrel's context, the pre-read body, and the
/// completion its request delegate awaits. Opaque outside this module.
[<ReferenceEquality>]
type SCtx =
    private
        { Http: HttpContext
          Body: BodyRead
          Done: TaskCompletionSource
          // writes and detach serialize on it [D:serve-drain]: once
          // detached (scope closed past the drain), a late handler's
          // writes are dropped, never raced against Kestrel's own end
          Gate: obj
          mutable Detached: bool
          // a refused body's connection close, applied with the head
          mutable CloseAfter: bool }

// the listener handle: the socket lifetime lives here, closed by stop
// on every scope exit path [D:http-serve]
type Handle =
    { Server: KestrelServer
      Port: int
      // the loopback addresses actually bound [D:serve-loopback-names]
      Bound: string list
      mutable Closed: bool
      Queue: Channel<SCtx>
      // every parked request whose delegate still awaits — what stop
      // detaches when the drain runs out [D:serve-drain]
      Live: Collections.Concurrent.ConcurrentDictionary<SCtx, unit>
      LiveLock: obj
      // the stream-producer failure channel [D:serve-stream]: a Stream
      // body whose producer raises mid-flight records its message here
      // (Server.streamError surfaces it, Proc.wait's shape); it is not a
      // raise out of the handler. Guarded by its own lock — worker threads
      // write it, the scope reads it.
      StreamErrors: System.Collections.Generic.List<string>
      StreamErrorLock: obj }

let private await (t: Task) = t.GetAwaiter().GetResult()

/// release a parked request's delegate without a response: abort the
/// connection (outside the gate, so a flush blocked on a slow client
/// returns), then latch Detached so later writes are dropped
let private detach (s: SCtx) : unit =
    (try
        s.Http.Abort()
     with _ ->
         ())

    lock s.Gate (fun () -> s.Detached <- true)
    s.Done.TrySetResult() |> ignore

/// whether a Host names this server [D:serve-loopback-names]: one of the
/// loopback names, any port. A foreign name is refused before any handler
/// runs — the loopback posture, which is also a DNS-rebinding guard
/// [D:serve-kestrel]. An HTTP/1.0 request may omit Host.
let private hostOk (authority: string) (http10: bool) : bool =
    if String.IsNullOrEmpty authority then
        http10
    else
        match HostString(authority).Host.ToLowerInvariant() with
        | "127.0.0.1"
        | "localhost"
        | "[::1]" -> true
        | _ -> false

let private rawTarget (http: HttpContext) : string =
    match http.Features.Get<IHttpRequestFeature>() with
    | null -> http.Request.Path.Value + http.Request.QueryString.Value
    | f -> f.RawTarget

// the request target as a Uri: origin-form joins a fixed authority, so the
// path keeps its percent-escapes and loses its dot segments the way the
// handler has always seen it; absolute-form is its own Uri
let private targetUri (raw: string) : Uri option =
    let ok, u =
        if raw.StartsWith "/" then
            Uri.TryCreate("http://localhost" + raw, UriKind.Absolute)
        else
            Uri.TryCreate(raw, UriKind.Absolute)

    if ok then Some u else None

/// the request body's text encoding: the Content-Type charset when the
/// runtime knows it, UTF-8 otherwise
let private bodyEncoding (http: HttpContext) : Encoding =
    match http.Request.ContentType with
    | null -> Encoding.UTF8
    | ct ->
        match Net.Http.Headers.MediaTypeHeaderValue.TryParse ct with
        | true, mt when not (String.IsNullOrEmpty mt.CharSet) ->
            try
                Encoding.GetEncoding(mt.CharSet.Trim('"'))
            with _ ->
                Encoding.UTF8
        | _ -> Encoding.UTF8

/// read the whole body under the deadline and the size bound
/// [D:serve-body-timeout] [D:serve-max-body], on the delegate (no thread,
/// no slot): the bound counts bytes as they arrive, so a chunked body with
/// no Content-Length is bounded too. The deadline is the only rate rule —
/// Kestrel's minimum body data rate is off [D:serve-kestrel].
let private readBody (http: HttpContext) (lim: Limits) : Task<BodyRead> =
    task {
        let req = http.Request

        let canHaveBody =
            match http.Features.Get<IHttpRequestBodyDetectionFeature>() with
            | null -> true
            | f -> f.CanHaveBody

        if not canHaveBody then
            return BodyText ""
        else
            // a declared length is Kestrel's to enforce: its own refusal
            // closes the connection at once instead of draining the unread
            // body. A chunked body is counted here — Kestrel's count takes
            // in framing (a 3-byte chunked body trips a 10-byte bound).
            match http.Features.Get<IHttpMaxRequestBodySizeFeature>() with
            | null -> ()
            | f when f.IsReadOnly -> ()
            | f -> f.MaxRequestBodySize <- (if req.ContentLength.HasValue then Nullable lim.MaxBody else Nullable())

            use cts = new CancellationTokenSource(lim.BodyTimeoutMs)
            use ms = new IO.MemoryStream()
            let buf = Array.zeroCreate<byte> 65536
            let mutable result = None

            try
                while result.IsNone do
                    let! n = req.Body.ReadAsync(buf.AsMemory(), cts.Token)

                    if n = 0 then
                        ms.Position <- 0L
                        use r = new IO.StreamReader(ms, bodyEncoding http)
                        result <- Some(BodyText(r.ReadToEnd()))
                    else
                        ms.Write(buf, 0, n)

                        if ms.Length > lim.MaxBody then
                            result <- Some BodyOverMax
            with
            | :? OperationCanceledException when cts.IsCancellationRequested -> result <- Some BodyTimedOut
            | :? Microsoft.AspNetCore.Http.BadHttpRequestException as b when b.StatusCode = 413 -> result <- Some BodyOverMax

            return result.Value
    }

/// the request delegate [D:serve-kestrel]: refuse a foreign Host, read the
/// body, park the request on the channel, and await the worker's finish.
/// Runs on Kestrel's pool — a parked request costs no thread.
let private onRequest (h: Handle) (lim: Limits) (features: IFeatureCollection) : Task =
    task {
        let http = DefaultHttpContext features :> HttpContext
        let raw = rawTarget http

        let authority =
            if raw.StartsWith "/" then
                http.Request.Headers.Host.ToString()
            else
                match targetUri raw with
                | Some u -> u.Authority
                | None -> ""

        if not (hostOk authority (http.Request.Protocol = "HTTP/1.0")) then
            http.Response.StatusCode <- 404
            http.Response.ContentType <- "text/plain; charset=utf-8"
            http.Response.ContentLength <- Nullable 12L
            do! http.Response.WriteAsync "unknown host"
        else
            let! body = readBody http lim

            let s =
                { Http = http
                  Body = body
                  Done = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
                  Gate = obj ()
                  Detached = false
                  // a refused body is left unread, so the connection
                  // cannot stay in frame past the refusal
                  CloseAfter =
                    (match body with
                     | BodyText _ -> false
                     | _ -> true) }

            // add-then-check against stop's set-then-sweep, under one lock:
            // either stop sees this request in Live or it sees Closed
            let parked =
                lock h.LiveLock (fun () ->
                    if h.Closed then
                        false
                    else
                        h.Live[s] <- ()
                        h.Queue.Writer.TryWrite s)

            if parked then
                do! s.Done.Task
                h.Live.TryRemove s |> ignore
            else
                h.Live.TryRemove s |> ignore
                http.Abort()
    }

/// bind loopback:port and start serving. Raises a worded error on a bind
/// failure (a port already taken is the common one — the acceptance's
/// "second bind succeeds after close" pins the inverse) [D:http-serve]
let start (port: int) (lim: Limits) : Handle =
    // loopback only — v1 is a reverse-proxy posture, no public bind,
    // no TLS (TLS is the proxy's, a stated bounded-out) [D:http-serve].
    // ::1 sits beside 127.0.0.1 [D:serve-loopback-names]; a host with no
    // IPv6 loopback binds 127.0.0.1 alone.
    let build (addrs: IPAddress list) =
        let opts = KestrelServerOptions()
        // the settings weir pins rather than inherits [D:serve-kestrel]
        opts.AddServerHeader <- false
        // set per request by readBody
        opts.Limits.MaxRequestBodySize <- Nullable()
        // bodyTimeout bounds the body read; a second, rate-shaped rule
        // would refuse a slow upload the documented deadline admits
        opts.Limits.MinRequestBodyDataRate <- null
        opts.Limits.MaxRequestLineSize <- 256 * 1024
        opts.Limits.MaxRequestHeaderCount <- 1000

        for a in addrs do
            opts.Listen(a, port, fun lo -> lo.Protocols <- HttpProtocols.Http1)

        let transport =
            SocketTransportFactory(Options.Create(SocketTransportOptions()), NullLoggerFactory.Instance)

        new KestrelServer(Options.Create opts, transport, NullLoggerFactory.Instance)

    let h0 server bound =
        { Server = server
          Port = port
          Bound = bound
          Closed = false
          Queue = Channel.CreateUnbounded<SCtx>(UnboundedChannelOptions(SingleReader = true))
          Live = Collections.Concurrent.ConcurrentDictionary<SCtx, unit>(HashIdentity.Reference)
          LiveLock = obj ()
          StreamErrors = System.Collections.Generic.List<string>()
          StreamErrorLock = obj () }

    let run (addrs: IPAddress list) =
        let server = build addrs
        let h = h0 server (addrs |> List.map string)

        let app =
            { new IHttpApplication<IFeatureCollection> with
                member _.CreateContext f = f
                member _.ProcessRequestAsync f = onRequest h lim f
                member _.DisposeContext(_, _) = () }

        try
            await (server.StartAsync(app, CancellationToken.None))
            h
        with ex ->
            (try
                server.Dispose()
             with _ ->
                 ())

            raise ex

    let unwrap (ex: exn) =
        match ex with
        | :? AggregateException as a when not (isNull a.InnerException) -> a.InnerException
        | e -> e

    let v6Absent (ex: exn) =
        let rec go (e: exn) =
            match e with
            | null -> false
            | :? Sockets.SocketException as se ->
                se.SocketErrorCode = Sockets.SocketError.AddressFamilyNotSupported
                || se.SocketErrorCode = Sockets.SocketError.AddressNotAvailable
            | e -> go e.InnerException

        go ex

    try
        if Sockets.Socket.OSSupportsIPv6 then
            try
                run [ IPAddress.Loopback; IPAddress.IPv6Loopback ]
            with ex when v6Absent (unwrap ex) ->
                run [ IPAddress.Loopback ]
        else
            run [ IPAddress.Loopback ]
    with ex ->
        // the bind failure in its own words — the port is the fact the
        // caller needs to reword ("address in use")
        failwith $"serve: cannot listen on 127.0.0.1:{port} — {(unwrap ex).Message}"

/// record a stream-producer failure on the handle [D:serve-stream] — the
/// designated channel a Stream body's mid-flight raise reports through
let recordStreamError (h: Handle) (msg: string) : unit =
    lock h.StreamErrorLock (fun () -> h.StreamErrors.Add msg)

/// the stream-producer failures seen so far, in occurrence order
/// [D:serve-stream] — Server.streamErrors reads this
let streamErrors (h: Handle) : string list =
    lock h.StreamErrorLock (fun () -> List.ofSeq h.StreamErrors)

/// close the server idempotently — the scope-exit tail and the signal
/// sweep share it, so a double close (finally after a signal) is benign.
/// The port frees at once; queued requests are dropped unstarted; requests
/// in flight get `drainMs` to finish, then their connections close and
/// their late writes are dropped [D:serve-drain]. Closing ends the queue,
/// which the accept loop reads as "shut down".
let stop (h: Handle) (drainMs: int) : unit =
    let first =
        lock h.LiveLock (fun () ->
            if h.Closed then
                false
            else
                h.Closed <- true
                true)

    if first then
        h.Queue.Writer.TryComplete() |> ignore

        let mutable s = Unchecked.defaultof<SCtx>

        while h.Queue.Reader.TryRead(&s) do
            detach s

        use cts = new CancellationTokenSource(max 0 drainMs)

        // past the drain, every still-parked delegate is released so
        // Kestrel's abort does not wait on a handler that never returns
        use _reg =
            cts.Token.Register(fun () ->
                for kv in h.Live do
                    detach kv.Key)

        (try
            await (h.Server.StopAsync cts.Token)
         with _ ->
             ())

        for kv in h.Live do
            detach kv.Key

        try
            h.Server.Dispose()
        with _ ->
            ()

/// pull one request, or None once the server has stopped (the
/// signal/scope-exit path). Blocks until a request is parked or the queue
/// closes. A request whose client already left is dropped unstarted
/// [D:serve-drain]. This is the accept-loop's step; the caller owns the
/// concurrency ceiling around the handler it runs per request.
let accept (h: Handle) : SCtx option =
    let rec go () =
        let mutable s = Unchecked.defaultof<SCtx>

        if h.Queue.Reader.TryRead(&s) then
            if h.Closed || s.Http.RequestAborted.IsCancellationRequested then
                detach s
                go ()
            else
                Some s
        else
            let more =
                try
                    h.Queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult()
                with _ ->
                    false

            if more then go () else None

    go ()

/// release a request's delegate once the worker is done with it — every
/// accepted request is finished exactly once, response or not
let finish (ctx: SCtx) : unit = ctx.Done.TrySetResult() |> ignore

/// a well-formed HTTP method is a non-empty RFC 7230 token — tchar only
/// [D:serve-method]: no control chars, whitespace, or separators. A
/// malformed token is refused at the boundary (400), the same byte-class
/// refusal Bundle C's header guard performs; a well-formed unlisted verb
/// (TRACE, a proxy's own) is preserved, carried to the handler as Other.
let methodTokenOk (m: string) : bool =
    not (String.IsNullOrEmpty m) && m |> Seq.forall Http.isTchar

/// raised when the request-body read exceeds the configured bound
/// [D:serve-body-timeout] — a distinguishable signal so the accept loop
/// answers 408 rather than a generic 500 (a slow client is not an error
/// inside the handler)
exception BodyReadTimeout

/// raised when the request body is larger than the configured maxBody
/// [D:serve-max-body] — the accept loop answers 413 without reading on
exception BodyTooLarge

/// raised when a handler-returned response header carries CR/LF/NUL
/// [D:http-header-bytes] — the inbound-response face of the review's F3 —
/// or cannot be sent at all [D:http-header-unsendable]. A distinguishable
/// signal (BodyReadTimeout's shape) so the accept loop refuses the
/// response without emitting the header (never a silent drop) and
/// surfaces the located message to the script through the stream-error
/// channel. Carries the located refusal.
exception ResponseHeaderRefused of message: string

/// raised when a handler returns a status HTTP cannot carry (outside
/// 100–999) [D:serve-handler-errors] — the accept loop answers 500 and
/// records it, like a header refusal
exception ResponseStatusRefused of status: int

/// read the request primitives off a parked request — path, query (no
/// '?'), method, headers (one pair per value; values of one name in wire
/// order), body as text (request-body streaming is out of scope v1
/// [D:http-serve]). The body was read before the request was parked, under
/// `bodyTimeout` [D:serve-body-timeout] and `maxBody` [D:serve-max-body];
/// a refused body raises its signal here.
let readRequest (ctx: SCtx) : SReq =
    let req = ctx.Http.Request

    // one pair per value [D:serve-kestrel]: a repeated header (an
    // X-Forwarded-For chain) reaches the handler whole, never collapsed
    let headers =
        [ for kv in req.Headers do
              for v in kv.Value do
                  if not (isNull v) then
                      kv.Key, v ]

    let body =
        match ctx.Body with
        | BodyOverMax -> raise BodyTooLarge
        | BodyTimedOut -> raise BodyReadTimeout
        | BodyText t -> t

    let path, query =
        match targetUri (rawTarget ctx.Http) with
        | Some u -> u.AbsolutePath, (if isNull u.Query then "" else u.Query).TrimStart('?')
        | None -> req.Path.Value, req.QueryString.Value.TrimStart('?')

    { Method = req.Method
      Path = path
      Query = query
      Headers = headers
      Body = body }

/// the framing and hop-by-hop headers (RFC 9110 §7.6.1, RFC 9112 §6) —
/// the server owns them: a handler's Transfer-Encoding beside the
/// computed Content-Length (or a stale Content-Length) desynchronizes the
/// client's framing, so they are dropped from a handler's response
let framingHeaders: Set<string> =
    Set.ofList
        [ "transfer-encoding"
          "content-length"
          "connection"
          "keep-alive"
          "proxy-connection"
          "te"
          "trailer"
          "upgrade" ]

/// refuse the first response header the server cannot send
/// [D:http-header-unsendable]: the shared predicate words the reason, so a
/// header the wire would refuse is never dropped unnamed (writeResponse's
/// add raises the same refusal as a backstop). Framing headers are
/// skipped — the server owns them (dropped by design)
let refuseUnsendableResponseHeaders (headers: (string * string) list) : unit =
    for (k, v) in headers |> List.filter (fun (k, _) -> not (framingHeaders.Contains(k.ToLowerInvariant()))) do
        match Http.headerUnsendable (k, v) with
        | Some reason -> raise (ResponseHeaderRefused(Http.headerUnsendableMessage "response header" k reason))
        | None -> ()

/// write a response to a parked request. A Stream body is written chunked
/// and flushed per element (the incremental law); every other body is a
/// single write. The response is completed (or the connection aborted)
/// before this returns; the caller still owes `finish` [D:http-serve].
///
/// `onStreamFailure` is the designated channel for a Stream producer
/// raising mid-body [D:serve-stream]: modelled on `within proc`'s
/// scoped-child surfacing, a producer failure does not raise out of the
/// handler — it aborts the connection with the chunked body unterminated
/// [D:serve-stream-truncate] (so the client's own HTTP layer sees a
/// truncated body: curl exits 18) and reports the message here, where the
/// serve scope surfaces it to the script (a Server member, Proc.wait's
/// shape). A client disconnect is not a producer failure: it ends
/// enumeration (the slot frees) and is unreported.
let writeResponse (ctx: SCtx) (resp: SResp) (onStreamFailure: string -> unit) : unit =
    // every header and the status are checked before the response is
    // touched [D:http-header-unsendable], so a refusal leaves a clean
    // response for the accept loop's 500
    refuseUnsendableResponseHeaders resp.Headers

    if resp.Status < 100 || resp.Status > 999 then
        raise (ResponseStatusRefused resp.Status)

    let http = ctx.Http
    let out = http.Response

    // a write to a detached request is dropped [D:serve-drain]; Kestrel's
    // writes are asynchronous, awaited here on the worker's own thread
    let guarded (f: unit -> unit) : bool =
        lock ctx.Gate (fun () ->
            if ctx.Detached then
                false
            else
                f ()
                true)

    let write (bytes: byte[]) =
        await (out.Body.WriteAsync(bytes, 0, bytes.Length))

    let ok =
        guarded (fun () ->
            if not out.HasStarted then
                out.Headers.Clear()

            out.StatusCode <- resp.Status

            if ctx.CloseAfter then
                out.Headers.Connection <- "close"

            for (k, v) in resp.Headers |> List.filter (fun (k, _) -> not (framingHeaders.Contains(k.ToLowerInvariant()))) do
                try
                    out.Headers.Append(k, Microsoft.Extensions.Primitives.StringValues v)
                with ex ->
                    out.Headers.Clear()
                    raise (ResponseHeaderRefused(Http.headerUnsendableMessage "response header" k ex.Message)))

    // no body on HEAD, 1xx, 204 or 304 (RFC 9110 §6.4.1): bytes there
    // are read as the start of the next response on a kept-alive
    // connection. HEAD still declares the length a GET would carry.
    let isHead = HttpMethods.IsHead http.Request.Method
    let bodyless = isHead || resp.Status < 200 || resp.Status = 204 || resp.Status = 304

    let defaultType (t: string) =
        if String.IsNullOrEmpty out.ContentType then
            out.ContentType <- t

    let fixedBytes =
        match resp.Body with
        | RNoBody -> Some [||]
        | RText s
        | RJson s
        | RForm s -> Some(Encoding.UTF8.GetBytes s)
        | RStream _ -> None

    let typeOf () =
        match resp.Body with
        | RJson _ -> defaultType "application/json"
        | RForm _ -> defaultType "application/x-www-form-urlencoded"
        | RStream _ -> defaultType "text/event-stream"
        | _ -> ()

    if ok then
        try
            match resp.Body, fixedBytes with
            | _ when bodyless ->
                guarded (fun () ->
                    typeOf ()

                    match fixedBytes with
                    | Some b when isHead && resp.Status >= 200 && resp.Status <> 204 && resp.Status <> 304 ->
                        out.ContentLength <- Nullable(int64 b.Length)
                    | _ when resp.Status >= 200 && resp.Status <> 204 && resp.Status <> 304 -> out.ContentLength <- Nullable 0L
                    | _ -> ()

                    await (out.CompleteAsync()))
                |> ignore
            | RStream lines, _ ->
                // SSE-shaped chunked stream [D:http-serve], buffering off and
                // a flush per element, so the client sees each before the seq
                // ends. On HTTP/1.1 weir frames the chunks itself
                // [D:serve-stream-truncate]: Kestrel's own chunking writes the
                // terminator on every graceful end, so a producer failure
                // could only abort (a reset — curl 56, not 18)
                let framed = HttpProtocol.IsHttp11 http.Request.Protocol

                guarded (fun () ->
                    typeOf ()

                    if framed then
                        out.Headers.TransferEncoding <- "chunked"

                    match http.Features.Get<IHttpResponseBodyFeature>() with
                    | null -> ()
                    | f -> f.DisableBuffering()

                    await (out.StartAsync()))
                |> ignore

                // the pull (producer) and the write (client) are separated
                // so their failures do not conflate [D:serve-stream]: a
                // producer raise on MoveNext is the script's own error
                // (abort + report); a client gone ends the stream quietly
                use e = lines.GetEnumerator()
                let mutable go = true
                let mutable producerError = None

                while go do
                    let hasNext =
                        if http.RequestAborted.IsCancellationRequested then
                            Some false
                        else
                            try
                                Some(e.MoveNext())
                            with ex ->
                                // the producer raised mid-body — the
                                // designated channel, not a raise out of
                                // the handler
                                producerError <- Some ex.Message
                                None

                    match hasNext with
                    | None
                    | Some false -> go <- false
                    | Some true ->
                        // one `data:` line per line of the element: SSE
                        // ends a field at CR, LF or CRLF, so an embedded
                        // newline would otherwise forge a new field or event
                        let fields =
                            e.Current.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                            |> Array.map (fun l -> $"data: {l}\n")
                            |> String.concat ""

                        let event = Encoding.UTF8.GetBytes(fields + "\n")

                        let frame =
                            if framed then
                                Array.concat
                                    [ Encoding.ASCII.GetBytes $"{event.Length:x}\r\n"
                                      event
                                      Encoding.ASCII.GetBytes "\r\n" ]
                            else
                                event

                        let wrote =
                            try
                                guarded (fun () ->
                                    write frame
                                    await (out.Body.FlushAsync()))
                            with _ ->
                                false

                        // client gone or scope closed: end enumeration
                        if not wrote || http.RequestAborted.IsCancellationRequested then
                            go <- false

                match producerError with
                | Some msg ->
                    // no terminating chunk [D:serve-stream-truncate]: the
                    // connection closes (gracefully) mid-body, so the
                    // client's HTTP layer can tell the stream died
                    let closed =
                        framed
                        && (match http.Features.Get<Microsoft.AspNetCore.Connections.Features.IConnectionLifetimeNotificationFeature>() with
                            | null -> false
                            | f ->
                                f.RequestClose()
                                guarded (fun () -> await (out.CompleteAsync())))

                    if not closed then
                        try
                            http.Abort()
                        with _ ->
                            ()

                    // surface the failure to the script through the
                    // designated channel — Proc.wait's shape, not a raise
                    onStreamFailure msg
                | None ->
                    guarded (fun () ->
                        if framed then
                            write (Encoding.ASCII.GetBytes "0\r\n\r\n")

                        await (out.CompleteAsync()))
                    |> ignore
            | _, Some bytes ->
                guarded (fun () ->
                    typeOf ()
                    out.ContentLength <- Nullable(int64 bytes.Length)

                    if bytes.Length > 0 then
                        write bytes

                    await (out.CompleteAsync()))
                |> ignore
            | _, None -> ()
        with
        | ResponseHeaderRefused _ -> reraise ()
        | _ ->
            // any late write failure (client vanished) — the connection is
            // already lost; closing is best-effort
            try
                http.Abort()
            with _ ->
                ()
