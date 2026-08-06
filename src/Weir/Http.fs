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

let private rootMessage (ex: exn) : string =
    let rec inner (e: exn) =
        match e.InnerException with
        | null -> e
        | i -> inner i

    (inner ex).Message

/// send the request. Status is DATA — a 4xx/5xx is Ok with that status,
/// never an Error [D:http]. Error is TRANSPORT failure only (unreachable,
/// TLS, timeout), shaped like the contracts fetch's message.
let send (req: Req) : Result<Resp, string> =
    try
        // a per-request handler only when insecure — the default path keeps
        // the plain HttpClient (TLS verification ON) [D:http-s2]
        use handler = new HttpClientHandler()

        if req.Insecure then
            handler.ServerCertificateCustomValidationCallback <- (fun _ _ _ _ -> true)

        use client = new HttpClient(handler)
        client.Timeout <- TimeSpan.FromMilliseconds(float req.TimeoutMs)

        use msg = new HttpRequestMessage(HttpMethod(req.Method), req.Url)

        // the body is attached BEFORE headers so a content-header (e.g. a
        // caller-set Content-Type override) can land on the content
        match req.Body with
        | Some(ct, body) ->
            let content = new StringContent(body, Text.Encoding.UTF8)
            // StringContent defaults text/plain; set the declared type,
            // charset preserved from UTF8
            (try
                content.Headers.ContentType <- Headers.MediaTypeHeaderValue(ct)
             with _ ->
                 ())

            msg.Content <- content
        | None -> ()

        for (k, v) in req.Headers do
            // request headers first; a content header (Content-*) lands on
            // the content instead — TryAddWithoutValidation keeps the bytes
            // verbatim (no folding, no re-encoding)
            if not (msg.Headers.TryAddWithoutValidation(k, v)) then
                match msg.Content with
                | null -> ()
                | c -> c.Headers.TryAddWithoutValidation(k, v) |> ignore

        use resp = client.SendAsync(msg).Result

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
        let host =
            try
                Uri(req.Url).Host
            with _ ->
                req.Url

        Error $"cannot reach {host} — {rootMessage ex}"

/// Basic auth is an ENCODING, not a prefix: base64(user:pass) [D:http] —
/// which is why it is a union case the runner encodes, not a header a
/// caller would build by hand and get wrong
let basicToken (user: string) (password: string) : string =
    let raw = Text.Encoding.UTF8.GetBytes($"{user}:{password}")
    Convert.ToBase64String raw
