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
      mutable Closed: bool }

/// open the listener on loopback:port. Raises a WORDED error on a bind
/// failure (a port already taken is the common one — the acceptance's
/// "second bind succeeds after close" pins the inverse) [D:http-serve]
let start (port: int) : Handle =
    let l = new HttpListener()
    // loopback only — v1 is a reverse-proxy posture, no public bind,
    // no TLS (TLS is the proxy's, a stated bounded-out) [D:http-serve]
    l.Prefixes.Add($"http://127.0.0.1:{port}/")

    try
        l.Start()
    with :? HttpListenerException as ex ->
        // the bind failure in its own words — the port is the fact the
        // caller needs to reword ("address in use")
        failwith $"serve: cannot listen on 127.0.0.1:{port} — {ex.Message}"

    { Listener = l; Port = port; Closed = false }

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

/// read the request primitives off a context — path, query (no '?'),
/// method, headers (in wire order, duplicates preserved), body as UTF-8
/// text (request-body streaming is out of scope v1 [D:http-serve])
let readRequest (ctx: HttpListenerContext) : SReq =
    let req = ctx.Request

    let headers =
        [ for k in req.Headers.AllKeys do
              match k with
              | null -> ()
              | key -> key, (req.Headers[key]) ]

    let body =
        if req.HasEntityBody then
            use r = new IO.StreamReader(req.InputStream, req.ContentEncoding)
            r.ReadToEnd()
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
/// connection) even on a mid-stream client disconnect [D:http-serve]
let writeResponse (ctx: HttpListenerContext) (resp: SResp) : unit =
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
            // ends. A client disconnect raises on Write — we stop pulling
            // (the producer sees the enumeration end, the Proc.stop
            // analogue) and close.
            if isNull out.ContentType then
                out.ContentType <- "text/event-stream"

            out.SendChunked <- true

            try
                for line in lines do
                    let frame = Encoding.UTF8.GetBytes($"data: {line}\n\n")
                    out.OutputStream.Write(frame, 0, frame.Length)
                    out.OutputStream.Flush()
            with _ ->
                // client gone mid-stream: end enumeration, close cleanly
                ()

        out.OutputStream.Close()
    with _ ->
        // any late write failure (client vanished) — the connection is
        // already lost; closing is best-effort
        try
            out.Abort()
        with _ ->
            ()
