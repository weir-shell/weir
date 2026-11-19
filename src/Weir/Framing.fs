/// Content-Length framing [D:one-framing]: the header-then-body messages
/// the language server and the debug adapter both read and write on
/// stdio. One reader holds the transport's bounds [D:lsp-transport-caps]:
/// nothing is allocated for a length a client only claims, a header line
/// cannot grow without limit, and a body past the cap is drained so the
/// stream stays framed.
module Weir.Framing

open System
open System.IO
open System.Text

/// what one read of the stream gave
type Read =
    /// a message's body
    | Message of byte[]
    /// a body longer than the cap, read past and dropped; the stream is
    /// still framed
    | Dropped of declared: int
    /// the stream ended, or a header no reader can recover from
    | End

/// the bound on one header line
let maxHeaderLineBytes = 64 * 1024

let private drainScratch () = Array.zeroCreate<byte> 65536

/// the next message: ASCII header lines ending `\r\n`, a blank line, then
/// a body of exactly Content-Length bytes
let read (input: Stream) (maxBodyBytes: int) : Read =
    let mutable contentLength = -1
    let line = StringBuilder()
    let mutable headerDone = false
    let mutable eof = false
    // past the bound the line is malformed: its bytes are no longer kept,
    // and the header ends malformed at its newline
    let mutable headerOverflow = false

    while not headerDone && not eof do
        let b = input.ReadByte()

        if b < 0 then
            eof <- true
        elif b = int '\n' then
            if headerOverflow then
                eof <- true
            else
                let l = line.ToString().TrimEnd('\r')
                line.Clear() |> ignore

                if l = "" then
                    headerDone <- contentLength >= 0
                elif l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                    match Int64.TryParse(l.Substring(15).Trim()) with
                    | true, n when n >= 0L && n <= int64 Int32.MaxValue -> contentLength <- int n
                    // a length past Int32 is oversized for certain: a marker
                    // past any cap sends it down the drain below
                    | true, n when n > int64 Int32.MaxValue -> contentLength <- Int32.MaxValue
                    | _ -> ()
        elif line.Length >= maxHeaderLineBytes then
            headerOverflow <- true
        else
            line.Append(char b) |> ignore

    if eof || contentLength < 0 then
        End
    elif contentLength > maxBodyBytes then
        let scratch = drainScratch ()
        let mutable remaining = contentLength

        while remaining > 0 && not eof do
            let n = input.Read(scratch, 0, min remaining scratch.Length)
            if n <= 0 then eof <- true else remaining <- remaining - n

        if eof then End else Dropped contentLength
    else
        let buf = Array.zeroCreate<byte> contentLength
        let mutable got = 0

        while got < contentLength && not eof do
            let n = input.Read(buf, got, contentLength - got)
            if n <= 0 then eof <- true else got <- got + n

        if eof then End else Message buf

/// one message out: the header, the body, flushed. The caller serializes
/// its writers
let write (output: Stream) (body: byte[]) (count: int) : unit =
    let header = Encoding.ASCII.GetBytes $"Content-Length: {count}\r\n\r\n"
    output.Write(header, 0, header.Length)
    output.Write(body, 0, count)
    output.Flush()
