module Weir.Serve

// the typed server boundary [D:http-serve] — a standalone .NET leg
// (like Http.fs, the client twin): primitives in, primitives out, no
// Value dependency, so Builtins owns the Value<->primitive translation.
// HttpListener is already AOT-linked (System.Net), so this adds ~0
// dependency bytes. This module OWNS the socket lifetime: Serve.start
// opens the listener, the caller drives an accept loop, Serve.stop
// closes it — the within-serve scope wires stop to the finally +
// signal hook so the socket frees on EVERY exit [D:http-serve].

open System
open System.Net
open System.Text

// the request as flat primitives, the SERVER mirror of Http.Req: no
// url (path + query instead), no auth/timeout/insecure (client
// concerns) — the shared family is HttpMethod + header pairs + the
// body union, never a second copy [D:http-serve]
type SReq =
    { Method: string
      Path: string
      // the raw query string WITHOUT the leading '?' (empty when none);
      // the handler splits it (weir's Str.splitOnce / Http.withQuery
      // shape), so the server stays out of query-DSL business
      Query: string
      Headers: (string * string) list
      Body: string }

// the response body, the server side of the SHARED HttpBody union
// [D:http-serve]: NoBody/Text/Json mirror the client cases; Stream is
// the SERVER-ONLY case — a lazy line source written chunked as it is
// pulled (SSE-shaped), the streaming precedent print sets, server-side
type SBody =
    | RNoBody
    | RText of string
    | RJson of string
    // the lazy body [D:http-serve]: each element written and FLUSHED as
    // produced, so a client sees bytes before the seq ends (the
    // incremental law the acceptance pins). Chunked transfer, one SSE
    // `data:` line per element.
    | RStream of seq<string>

type SResp =
    { Status: int
      Headers: (string * string) list
      Body: SBody }

// the listener handle: the socket lifetime lives HERE, closed by stop
// on every scope exit path [D:http-serve]
type Handle =
    { Listener: HttpListener
      Port: int
      mutable Closed: bool
      // the stream-producer failure channel [D:serve-stream]: a Stream
      // body whose producer raises mid-flight records its message HERE
      // (Server.streamError surfaces it, Proc.wait's shape); it is not a
      // raise out of the handler. Guarded by its own lock — worker threads
      // write it, the scope reads it.
      StreamErrors: System.Collections.Generic.List<string>
      StreamErrorLock: obj }

/// open the listener on loopback:port. Raises a WORDED error on a bind
/// failure (a port already taken is the common one — the acceptance's
/// "second bind succeeds after close" pins the inverse) [D:http-serve]
let start (port: int) : Handle =
    // loopback only — v1 is a reverse-proxy posture, no public bind,
    // no TLS (TLS is the proxy's, a stated bounded-out) [D:http-serve].
    // The loopback NAMES sit beside each other on the one port
    // [D:serve-loopback-names]: a client saying Host: localhost must
    // reach the handler, not .NET's prefix-miss 404 — a `+`/`*` bind
    // would answer it but expose the server past loopback, the
    // regression this refuses.
    let v4 = [ $"http://127.0.0.1:{port}/"; $"http://localhost:{port}/" ]
    // the ::1 name is GUARDED [D:serve-loopback-names]: a host with no
    // IPv6 loopback makes Start() throw and DISPOSE the listener, so a
    // failed start cannot reuse it — build once WITH ::1, and on failure
    // build a fresh listener on the v4 names alone (an IPv6-less host
    // still serves both 127.0.0.1 and localhost).
    let build (prefixes: string list) =
        let l = new HttpListener()

        for p in prefixes do
            l.Prefixes.Add p

        l.Start()
        l

    try
        try
            build (v4 @ [ $"http://[::1]:{port}/" ])
        with :? HttpListenerException ->
            build v4
    with :? HttpListenerException as ex ->
        // the bind failure in its own words — the port is the fact the
        // caller needs to reword ("address in use")
        failwith $"serve: cannot listen on 127.0.0.1:{port} — {ex.Message}"
    |> fun l ->
        { Listener = l
          Port = port
          Closed = false
          StreamErrors = System.Collections.Generic.List<string>()
          StreamErrorLock = obj () }

/// record a stream-producer failure on the handle [D:serve-stream] — the
/// designated channel a Stream body's mid-flight raise reports through
let recordStreamError (h: Handle) (msg: string) : unit =
    lock h.StreamErrorLock (fun () -> h.StreamErrors.Add msg)

/// the stream-producer failures seen so far, in occurrence order
/// [D:serve-stream] — Server.streamErrors reads this
let streamErrors (h: Handle) : string list =
    lock h.StreamErrorLock (fun () -> List.ofSeq h.StreamErrors)

/// close the listener idempotently — the scope-exit tail and the signal
/// sweep share it, so a double close (finally after a signal) is benign.
/// Closing unblocks a pending GetContext with an ObjectDisposed
/// /HttpListenerException, which the accept loop reads as "shut down"
let stop (h: Handle) : unit =
    if not h.Closed then
        h.Closed <- true

        try
            h.Listener.Stop()
        with _ ->
            ()

        try
            (h.Listener :> IDisposable).Dispose()
        with _ ->
            ()

/// pull ONE request, or None when the listener has been stopped (the
/// signal/scope-exit path). Blocks until a connection arrives or the
/// socket closes. This is the accept-loop's step; the caller owns the
/// concurrency ceiling around the handler it runs per request.
let accept (h: Handle) : HttpListenerContext option =
    try
        Some(h.Listener.GetContext())
    with
    // Stop() / Dispose() during a blocked GetContext lands here — the
    // shutdown signal, not an error
    | :? ObjectDisposedException -> None
    | :? HttpListenerException -> None
    | :? InvalidOperationException -> None

/// a well-formed HTTP method is a non-empty RFC 7230 token — tchar only
/// [D:serve-method]: no control chars, whitespace, or separators. A
/// malformed token is refused at the boundary (400), the same byte-class
/// refusal Bundle C's header guard performs; a well-formed unlisted verb
/// (TRACE, a proxy's own) is PRESERVED, carried to the handler as Other.
let methodTokenOk (m: string) : bool =
    not (String.IsNullOrEmpty m)
    && m
       |> Seq.forall (fun c ->
           (c >= 'A' && c <= 'Z')
           || (c >= 'a' && c <= 'z')
           || (c >= '0' && c <= '9')
           || "!#$%&'*+-.^_`|~".Contains c)

/// raised when the request-body read exceeds the configured bound
/// [D:serve-body-timeout] — a distinguishable signal so the accept loop
/// answers 408 rather than a generic 500 (a slow client is not an error
/// inside the handler)
exception BodyReadTimeout

/// read the whole body under a DEADLINE [D:serve-body-timeout]: a slow
/// client dribbling bytes cannot park the slot forever. The read runs on
/// a task the deadline cancels; exhaustion raises BodyReadTimeout. The
/// underlying socket read is not itself cancellable, so the task is
/// abandoned on timeout and the connection is closed by the caller.
let private readBodyBounded (stream: IO.Stream) (enc: Text.Encoding) (timeoutMs: int) : string =
    let work =
        System.Threading.Tasks.Task.Run(fun () ->
            use r = new IO.StreamReader(stream, enc)
            r.ReadToEnd())

    if work.Wait timeoutMs then
        work.Result
    else
        raise BodyReadTimeout

/// read the request primitives off a context — path, query (no '?'),
/// method, headers (in wire order, duplicates preserved), body as UTF-8
/// text (request-body streaming is out of scope v1 [D:http-serve]). The
/// body read is bounded by `bodyTimeoutMs` [D:serve-body-timeout].
let readRequest (ctx: HttpListenerContext) (bodyTimeoutMs: int) : SReq =
    let req = ctx.Request

    // GetValues over the indexer [D:http-serve]: for a header the parser
    // folded to a comma-joined value, GetValues yields the pieces as
    // separate pairs (the wire-order-pairs intent). NOTE the platform
    // limit behind F10: the managed HttpListener collapses REPEATED
    // request headers (three `X-Forwarded-For` lines) to the LAST value at
    // parse time — those earlier values never reach this code, so faithful
    // duplicate preservation is not reachable through HttpListener's
    // parsed headers (see the session report).
    let headers =
        [ for k in req.Headers.AllKeys do
              match k with
              | null -> ()
              | key ->
                  match req.Headers.GetValues key with
                  | null -> ()
                  | values ->
                      for v in values do
                          key, v ]

    let body =
        if req.HasEntityBody then
            readBodyBounded req.InputStream req.ContentEncoding bodyTimeoutMs
        else
            ""

    let rawQuery =
        match req.Url with
        | null -> ""
        | u -> (if isNull u.Query then "" else u.Query).TrimStart('?')

    { Method = req.HttpMethod
      Path = (match req.Url with
              | null -> "/"
              | u -> u.AbsolutePath)
      Query = rawQuery
      Headers = headers
      Body = body }

/// write a response to a context. A Stream body is written CHUNKED and
/// FLUSHED per element (the incremental law); every other body is a
/// single buffered write. Always closes the response (frees the
/// connection) even on a mid-stream client disconnect [D:http-serve].
///
/// `onStreamFailure` is the DESIGNATED CHANNEL for a Stream PRODUCER
/// raising mid-body [D:serve-stream]: modelled on `within proc`'s
/// scoped-child surfacing, a producer failure does NOT raise out of the
/// handler — it aborts the response WITHOUT the terminating chunk (so the
/// client's own HTTP layer sees a truncated/broken body) and reports the
/// message here, where the serve scope surfaces it to the script (a
/// Server member, Proc.wait's shape). A CLIENT disconnect is NOT a
/// producer failure: it ends enumeration and closes cleanly, unreported.
/// abort a chunked response on a producer failure [D:serve-stream]. The
/// INTENT is to leave the client a TRUNCATED body (no zero-length chunk),
/// but the managed HttpListener emits the terminator on both Close() and
/// Abort() and exposes no public way to suppress it; the only mechanism
/// found reaches into HttpListener's private fields, which the AOT trimmer
/// refuses (IL2075) — see the session report. So (a) degrades to a plain
/// Abort here, and the DESIGNATED CHANNEL (b) — Server.streamErrors — is
/// the reliable, script-observable signal: a monitoring loop reads it and
/// learns its producer died, which the pre-fix silent path never allowed.
let private abortWithoutTrailer (out: HttpListenerResponse) : unit =
    try
        out.Abort()
    with _ ->
        ()

let writeResponse (ctx: HttpListenerContext) (resp: SResp) (onStreamFailure: string -> unit) : unit =
    let out = ctx.Response
    out.StatusCode <- resp.Status

    for (k, v) in resp.Headers do
        // ContentType/ContentLength are properties, not header adds;
        // set the common one explicitly, pass the rest through
        try
            if String.Equals(k, "Content-Type", StringComparison.OrdinalIgnoreCase) then
                out.ContentType <- v
            else
                out.Headers.Add(k, v)
        with _ ->
            ()

    try
        match resp.Body with
        | RNoBody -> out.ContentLength64 <- 0L
        | RText s ->
            let bytes = Encoding.UTF8.GetBytes s
            out.ContentLength64 <- int64 bytes.Length
            out.OutputStream.Write(bytes, 0, bytes.Length)
        | RJson s ->
            if isNull out.ContentType then
                out.ContentType <- "application/json"

            let bytes = Encoding.UTF8.GetBytes s
            out.ContentLength64 <- int64 bytes.Length
            out.OutputStream.Write(bytes, 0, bytes.Length)
        | RStream lines ->
            // SSE-shaped chunked stream [D:http-serve]: no ContentLength
            // (SendChunked instead), text/event-stream, one `data:` line
            // per element, FLUSHED so the client sees each before the seq
            // ends.
            if isNull out.ContentType then
                out.ContentType <- "text/event-stream"

            out.SendChunked <- true

            // the pull (producer) and the write (client) are separated so
            // their failures do not conflate [D:serve-stream]: a producer
            // raise on MoveNext is the script's own error (abort + report);
            // a Write raise is the client vanishing (clean stop).
            use e = lines.GetEnumerator()
            let mutable go = true
            let mutable producerError = None

            while go do
                let hasNext =
                    try
                        Some(e.MoveNext())
                    with ex ->
                        // the producer raised mid-body — the designated
                        // channel, not a raise out of the handler
                        producerError <- Some ex.Message
                        None

                match hasNext with
                | None -> go <- false
                | Some false -> go <- false
                | Some true ->
                    let line = e.Current
                    let frame = Encoding.UTF8.GetBytes($"data: {line}\n\n")

                    try
                        out.OutputStream.Write(frame, 0, frame.Length)
                        out.OutputStream.Flush()
                    with _ ->
                        // client gone mid-stream: end enumeration, close cleanly
                        go <- false

            match producerError with
            | Some msg ->
                // NO terminating chunk [D:serve-stream]: the client's HTTP
                // layer must read a TRUNCATED chunked body (no 0-length
                // terminator) so it can TELL the stream died — a proper
                // terminator after a failure is the one outcome that must
                // not survive.
                abortWithoutTrailer out

                // surface the failure to the script through the designated
                // channel — Proc.wait's shape, not a raise out of the handler
                onStreamFailure msg
            | None -> out.OutputStream.Close()

        match resp.Body with
        | RStream _ -> () // the stream arm closed/aborted above
        | _ -> out.OutputStream.Close()
    with _ ->
        // any late write failure (client vanished) — the connection is
        // already lost; closing is best-effort
        try
            out.Abort()
        with _ ->
            ()
