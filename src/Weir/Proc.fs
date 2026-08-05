module Weir.Proc

open System.Diagnostics

// The spawn spec [D:spawn-spec]: ONE description of a child —
// Prog / Args / Env / Input — consumed by one starter. The output
// axis is the CONSUMER function (lines / streamCode / complete): the
// reifier law restated in code — the consumer IS the meaning. The
// public wrappers keep their signatures; they are thin constructors
// over the spec.
type Spec =
    { Prog: string
      Args: string list
      Env: (string * string) list
      Input: seq<string> option }

// the one starter: psi construction, env overlay, cwd, the not-found
// mapping, and the stdin writer — which PULLS the input seq lazily as
// the pipe accepts (laziness reaches inputs too)
let private start (redirectOut: bool) (redirectErr: bool) (s: Spec) : Process =
    let psi = ProcessStartInfo(s.Prog)

    for a in s.Args do
        psi.ArgumentList.Add a

    for k, v in s.Env do
        psi.Environment[k] <- v

    psi.WorkingDirectory <- Session.Cwd()
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- redirectOut
    psi.RedirectStandardError <- redirectErr
    psi.RedirectStandardInput <- s.Input.IsSome

    let p =
        try
            Process.Start psi
        with :? System.ComponentModel.Win32Exception ->
            failwith $"command not found or not executable: {s.Prog}"

    // a racing arm's children join its group [D:seq-pfirst] — a no-op
    // outside pfirst
    Session.registerChild p

    match s.Input with
    | Some lines ->
        System.Threading.Tasks.Task.Run(fun () ->
            try
                try
                    for l in lines do
                        p.StandardInput.WriteLine l
                with _ ->
                    ()
            finally
                p.StandardInput.Close())
        |> ignore
    | None -> ()

    p

// tree-kill then reap — the lifecycle tail shared by the streaming
// consumers (complete disposes without killing: it has already read
// both pipes to the end)
let private reap (p: Process) =
    try
        p.Kill true
    with _ ->
        ()

    try
        p.WaitForExit()
    with _ ->
        ()

let private raiseNonzero (s: Spec) (code: int) =
    let shown = String.concat " " (s.Prog :: s.Args)

    let signalNote =
        // 128+N = terminated by signal N; name the common ones so a
        // cancelled fzf reads as a cancel, not a mystery number
        match code with
        | 130 -> " (SIGINT — interrupted/cancelled)"
        | 143 -> " (SIGTERM — terminated)"
        | 137 -> " (SIGKILL — killed)"
        | c when c > 128 && c < 165 -> $" (signal {c - 128})"
        | _ -> ""

    failwith $"command failed with exit code {code}{signalNote}: {shown}"

// ---- the consumers (the output axis) -------------------------------

// stdout as a lazy line seq; raise-at-force on nonzero; stderr inherits
let linesOf (s: Spec) : seq<string> =
    seq {
        use p = start true false s

        try
            let out = p.StandardOutput
            let mutable line = out.ReadLine()

            while line <> null do
                yield line
                line <- out.ReadLine()

            p.WaitForExit()

            if p.ExitCode <> 0 then
                raiseNonzero s p.ExitCode
        finally
            reap p
    }

// stdout relayed to the console as it arrives; the code as the result
// [D:exit-reifiers]: output goes to the human, the code is the meaning
let streamCodeOf (s: Spec) : int =
    use p = start true false s

    try
        let out = p.StandardOutput
        let mutable line = out.ReadLine()

        while line <> null do
            System.Console.Out.WriteLine line
            line <- out.ReadLine()

        p.WaitForExit()
        p.ExitCode
    finally
        reap p

// ---- capture representation [D:capture-buffer]: ONE byte buffer per
// stream + line offsets; stdout/stderr are lazy VIEWS decoding a
// string per pull. Same observable seq<string>, ~2x the text in RSS
// instead of ~18x (per-string object overhead + UTF-16 were the old
// cost). Offsets are int into one array — a single capture caps at
// the .NET ~2GB array bound, where the old representation met OOM at
// a fraction of that text anyway. The view holds the buffer alive
// while referenced (the same data the old lists held).

let private utf8 = System.Text.Encoding.UTF8

// fixed-size segments ARE the storage — no doubling, no final
// assembly copy, so peak touched pages ≈ the text itself (a growable
// array's doubling+trim touched ~3x and MaxRSS keeps LOH pages)
let private segBits = 22 // 4MB
let private segSize = 1 <<< segBits
let private segMask = segSize - 1

type private Segments =
    { Segs: byte[][]
      Total: int }

    member this.At(g: int) : byte = this.Segs[g >>> segBits][g &&& segMask]

let private readAllBytes (stream: System.IO.Stream) : Segments =
    let segs = ResizeArray<byte[]>()
    let mutable total = 0
    let mutable seg = Array.zeroCreate<byte> segSize
    let mutable fill = 0
    let mutable n = stream.Read(seg, fill, segSize - fill)

    while n > 0 do
        fill <- fill + n

        if fill = segSize then
            segs.Add seg
            seg <- Array.zeroCreate segSize
            fill <- 0

        n <- stream.Read(seg, fill, segSize - fill)

    total <- segs.Count * segSize + fill

    if fill > 0 || segs.Count = 0 then
        // the tail segment, trimmed exact (small)
        System.Array.Resize(&seg, fill)
        segs.Add seg

    { Segs = segs.ToArray(); Total = total }

// StreamReader's BOM detection is part of today's pinned contract: a
// UTF-16/32 BOM SWITCHES decoding. Those captures take the fallback
// (a real StreamReader over the bytes — byte-for-byte the old
// behavior); everything else takes the per-line fast path.
let private nonUtf8Bom (b: Segments) =
    let len = b.Total

    (len >= 2 && b.At 0 = 0xFFuy && b.At 1 = 0xFEuy)
    || (len >= 2 && b.At 0 = 0xFEuy && b.At 1 = 0xFFuy)
    || (len >= 4 && b.At 0 = 0uy && b.At 1 = 0uy && b.At 2 = 0xFEuy && b.At 3 = 0xFFuy)

let private utf8BomSkip (b: Segments) =
    if b.Total >= 3 && b.At 0 = 0xEFuy && b.At 1 = 0xBBuy && b.At 2 = 0xBFuy then
        3
    else
        0

// the rare-path reader (encoding-switch captures are tiny in practice)
let private readerOver (b: Segments) =
    let all = Array.zeroCreate<byte> b.Total
    let mutable off = 0

    for seg in b.Segs do
        System.Array.Copy(seg, 0, all, off, seg.Length)
        off <- off + seg.Length

    new System.IO.StreamReader(new System.IO.MemoryStream(all), utf8, true)

// decode one line: within a segment it is a zero-copy GetString; the
// rare boundary-crossing line assembles a small temp first. Newline
// bytes are hard UTF-8 sequence boundaries, so per-line decoding with
// the default replacement fallback matches the streaming decoder.
let private decodeAt (b: Segments) (start: int) (len: int) : string =
    if len = 0 then
        ""
    else
        let si = start >>> segBits

        if (start + len - 1) >>> segBits = si then
            utf8.GetString(b.Segs[si], start &&& segMask, len)
        else
            let tmp = Array.zeroCreate<byte> len

            for k in 0 .. len - 1 do
                tmp[k] <- b.At(start + k)

            utf8.GetString tmp

// two passes over the bytes: count lines, then fill EXACT (start,len)
// arrays — no growable-array churn, the offsets cost is 8B/line flat.
// isStdout selects the oracle-pinned rule: stdout = ReadLine (\n,
// \r\n, lone \r; empties kept); stderr = \n-only split, empties
// dropped, \r retained.
let private scanLines (b: Segments) (isStdout: bool) : int[] * int[] =
    let len = b.Total
    let start0 = utf8BomSkip b

    let pass (fill: bool) (starts: int[]) (lens: int[]) : int =
        let mutable count = 0
        let mutable i = start0
        let mutable ls = start0

        let add (s: int) (l: int) =
            if isStdout || l > 0 then
                if fill then
                    starts[count] <- s
                    lens[count] <- l

                count <- count + 1

        while i < len do
            let c = b.At i

            if c = 10uy then
                add ls (i - ls)
                i <- i + 1
                ls <- i
            elif c = 13uy && isStdout then
                add ls (i - ls)
                i <- if i + 1 < len && b.At(i + 1) = 10uy then i + 2 else i + 1
                ls <- i
            else
                i <- i + 1

        if ls < len then
            add ls (len - ls)

        count

    let n = pass false Array.empty Array.empty
    let starts = Array.zeroCreate<int> n
    let lens = Array.zeroCreate<int> n
    pass true starts lens |> ignore
    starts, lens

let private linesView (b: Segments) (isStdout: bool) : seq<string> =
    if nonUtf8Bom b then
        // byte-for-byte the old StreamReader behavior, eagerly (rare)
        use r = readerOver b

        if isStdout then
            let acc = ResizeArray<string>()
            let mutable line = r.ReadLine()

            while line <> null do
                acc.Add line
                line <- r.ReadLine()

            acc :> seq<string>
        else
            r.ReadToEnd().Split('\n') |> Array.filter (fun l -> l <> "") :> seq<string>
    else
        let starts, lens = scanLines b isStdout

        seq {
            for i in 0 .. starts.Length - 1 do
                yield decodeAt b starts[i] lens[i]
        }

// both pipes captured to completion; the record's raw material
let completedOf (s: Spec) : int * seq<string> * seq<string> =
    use p = start true true s

    let stderrTask =
        System.Threading.Tasks.Task.Run(fun () -> readAllBytes (p.StandardError.BaseStream))

    let outBytes = readAllBytes (p.StandardOutput.BaseStream)
    let errBytes = stderrTask.Result
    p.WaitForExit()
    p.ExitCode, linesView outBytes true, linesView errBytes false

// ---- the public wrappers (signatures unchanged) --------------------

// Child-env overlay [D:child-env-overlay]: `lines` IS the empty
// overlay, so cmd/cmdEnv share one path by construction.
let linesWith
    (overlay: (string * string) list)
    (prog: string)
    (args: string list)
    (input: seq<string> option)
    : seq<string> =
    linesOf
        { Prog = prog
          Args = args
          Env = overlay
          Input = input }

let lines (prog: string) (args: string list) (input: seq<string> option) : seq<string> = linesWith [] prog args input

let streamCode (overlay: (string * string) list) (prog: string) (args: string list) : int =
    streamCodeOf
        { Prog = prog
          Args = args
          Env = overlay
          Input = None }

let completeWith
    (overlay: (string * string) list)
    (prog: string)
    (args: string list)
    (input: seq<string> option)
    : int * seq<string> * seq<string> =
    completedOf
        { Prog = prog
          Args = args
          Env = overlay
          Input = input }

let complete (prog: string) (args: string list) (input: seq<string> option) : int * seq<string> * seq<string> =
    completeWith [] prog args input

let resolveProg (prog: string) : string =
    if prog.Contains '/' then Session.resolve prog else prog
