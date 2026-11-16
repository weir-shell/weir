/// The step debugger [D:step-debugger], in two halves that talk over a
/// loopback socket, one JSON object a line.
///
/// The ENGINE lives in the script's own process: the evaluator tells it
/// each node it enters, and it stops the walking thread at a breakpoint
/// or a step's end, answering questions about the stopped frames until
/// it is told to go on.
///
/// The ADAPTER is `weir dap`: it speaks the Debug Adapter Protocol to an
/// editor on stdio, starts the script as a child with the engine
/// attached, and hands the child's output on as the protocol's own
/// output events — so a script's stdout never shares a stream with the
/// protocol.
module Weir.Debugger

open System
open System.IO
open System.Text
open System.Text.Json

// ---- the wire both halves share: one JSON object per line ---------------------

let private writeLine (stream: Stream) (gate: obj) (build: Utf8JsonWriter -> unit) : unit =
    use buf = new MemoryStream()

    (use w = new Utf8JsonWriter(buf)
     w.WriteStartObject()
     build w
     w.WriteEndObject())

    buf.WriteByte(byte '\n')

    lock gate (fun () ->
        stream.Write(buf.GetBuffer(), 0, int buf.Length)
        stream.Flush())

let private str (el: JsonElement) (name: string) : string =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
    | _ -> ""

let private num (el: JsonElement) (name: string) : int =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Number ->
        match v.TryGetInt32() with
        | true, n -> n
        | _ -> 0
    | _ -> 0

let private flag (el: JsonElement) (name: string) : bool =
    match el.TryGetProperty name with
    | true, v -> v.ValueKind = JsonValueKind.True
    | _ -> false

// ---- the engine -------------------------------------------------------------

/// what the walk does next
type private Mode =
    | Running
    | StepIn
    | StepOver of depth: int
    | StepOut of depth: int
    | Pausing

/// a frame's scope kept as the walk passes: frame d is the body d calls
/// deep, its line the last one the walk entered there
[<AllowNullLiteral>]
type private Frame(file: string, line: int, scope: Eval.Scope) =
    member _.File = file
    member _.Line = line
    member _.Scope = scope

[<AllowNullLiteral>]
type private Engine(channel: Stream) =
    let gate = obj ()
    let reader = new StreamReader(channel, UTF8Encoding false)

    let breakpoints =
        Collections.Generic.Dictionary<string, Collections.Generic.HashSet<int>>()

    let frames = ResizeArray<Frame>()
    // one thread stops at a time; a second that reaches a node waits here
    let stopGate = obj ()
    let mutable mode = Running
    let mutable lastFile = ""
    let mutable lastLine = 0
    let mutable lastDepth = -1

    let sameFile (a: string) (b: string) =
        String.Equals(
            a,
            b,
            (if OperatingSystem.IsWindows() then
                 StringComparison.OrdinalIgnoreCase
             else
                 StringComparison.Ordinal)
        )

    let hasBreakpoint (file: string) (line: int) =
        lock breakpoints (fun () ->
            breakpoints
            |> Seq.exists (fun kv -> sameFile kv.Key file && kv.Value.Contains line))

    // a value for the variables pane: never forces a lazy seq, never long
    let render (v: Eval.Value) : string =
        let text =
            match v with
            | Eval.VSeq items when not (items :? Collections.ICollection) && not (items :? Eval.Value list) ->
                "<lazy seq>"
            | Eval.VClosure _
            | Eval.VClosurePat _
            | Eval.VBuiltin _ -> "<fun>"
            | v ->
                try
                    fst (Eval.echoValue (Some 20) v)
                with _ ->
                    "<unshowable>"

        let text = Types.quoteData text

        if text.Length > 400 then
            text.Substring(0, 400) + "…"
        else
            text

    let kind (v: Eval.Value) : string =
        match v with
        | Eval.VInt _ -> "int"
        | Eval.VStr _ -> "string"
        | Eval.VBool _ -> "bool"
        | Eval.VSeq _ -> "seq"
        | Eval.VRecord(n, _) -> n
        | Eval.VTuple _ -> "tuple"
        | Eval.VClosure _
        | Eval.VClosurePat _
        | Eval.VBuiltin _ -> "function"
        | _ -> ""

    let send (build: Utf8JsonWriter -> unit) = writeLine channel gate build

    let writeVars (w: Utf8JsonWriter) (vars: (string * Eval.Value) seq) =
        w.WriteStartArray "variables"

        for name, value in vars do
            w.WriteStartObject()
            w.WriteString("name", name)
            w.WriteString("value", render value)
            w.WriteString("type", kind value)
            w.WriteEndObject()

        w.WriteEndArray()

    // the stopped thread answers questions until it is told to go on
    let rec serve (depth: int) : unit =
        match reader.ReadLine() with
        | null ->
            // the adapter is gone: a script with nobody to stop for runs on
            mode <- Running
            lock breakpoints (fun () -> breakpoints.Clear())
        | text ->
            use doc = JsonDocument.Parse text
            let m = doc.RootElement

            match str m "do" with
            | "continue" -> mode <- Running
            | "stepIn" -> mode <- StepIn
            | "next" -> mode <- StepOver depth
            | "stepOut" -> mode <- StepOut depth
            | "breakpoints" ->
                setBreakpoints m
                serve depth
            | "stack" ->
                send (fun w ->
                    w.WriteString("reply", "stack")
                    w.WriteStartArray "frames"

                    for d in depth .. -1 .. 0 do
                        if d < frames.Count && not (isNull frames[d]) then
                            w.WriteStartObject()
                            w.WriteNumber("id", d)
                            w.WriteString("file", frames[d].File)
                            w.WriteNumber("line", frames[d].Line)
                            w.WriteEndObject()

                    w.WriteEndArray())

                serve depth
            | "locals"
            | "globals" as which ->
                let d = num m "frame"

                send (fun w ->
                    w.WriteString("reply", which)

                    if d >= 0 && d < frames.Count && not (isNull frames[d]) then
                        let scope = frames[d].Scope

                        if which = "locals" then
                            writeVars w (scope.Locals())
                        else
                            writeVars
                                w
                                (scope.Globals()
                                 |> Map.toSeq
                                 |> Seq.filter (fun (n, _) -> not (n.StartsWith "Self.")))
                    else
                        writeVars w [])

                serve depth
            | "value" ->
                // a name read in a frame: what a hover asks
                let d = num m "frame"
                let name = str m "name"

                send (fun w ->
                    w.WriteString("reply", "value")

                    let found =
                        if d >= 0 && d < frames.Count && not (isNull frames[d]) then
                            Eval.lookupValue name (frames[d].Scope.ToMap())
                        else
                            None

                    match found with
                    | Some v ->
                        w.WriteBoolean("found", true)
                        w.WriteString("value", render v)
                        w.WriteString("type", kind v)
                    | None -> w.WriteBoolean("found", false))

                serve depth
            | _ -> serve depth

    and setBreakpoints (m: JsonElement) : unit =
        let file = str m "file"

        lock breakpoints (fun () ->
            let lines = Collections.Generic.HashSet<int>()

            match m.TryGetProperty "lines" with
            | true, arr when arr.ValueKind = JsonValueKind.Array ->
                for l in arr.EnumerateArray() do
                    match l.TryGetInt32() with
                    | true, n -> lines.Add n |> ignore
                    | _ -> ()
            | _ -> ()

            for key in breakpoints.Keys |> Seq.filter (fun k -> sameFile k file) |> List.ofSeq do
                breakpoints.Remove key |> ignore

            if lines.Count > 0 then
                breakpoints[file] <- lines)

    /// before the script's first statement: breakpoints arrive, then the
    /// word to start
    member _.AwaitStart() : unit =
        let mutable started = false

        while not started do
            match reader.ReadLine() with
            | null -> started <- true
            | text ->
                use doc = JsonDocument.Parse text
                let m = doc.RootElement

                match str m "do" with
                | "breakpoints" -> setBreakpoints m
                | "start" ->
                    if flag m "stopOnEntry" then
                        mode <- Pausing

                    started <- true
                | _ -> ()

    member _.OnNode(node: obj, scope: Eval.Scope) : unit =
        let struct (file, line) =
            match Eval.placeOf with
            | null -> struct ("", 0)
            | f -> f.Invoke node

        if line > 0 then
            let depth = Eval.DebugDepth.Depth

            lock stopGate (fun () ->
                while frames.Count <= depth do
                    frames.Add null

                frames[depth] <- Frame(file, line, scope)

                let entered = not (line = lastLine && depth = lastDepth && sameFile file lastFile)

                let reason =
                    if not entered then
                        ""
                    else
                        match mode with
                        | Pausing -> "pause"
                        | StepIn -> "step"
                        | StepOver d when depth <= d -> "step"
                        | StepOut d when depth < d -> "step"
                        | _ when hasBreakpoint file line -> "breakpoint"
                        | _ -> ""

                lastFile <- file
                lastLine <- line
                lastDepth <- depth

                if reason <> "" then
                    Console.Out.Flush()
                    Console.Error.Flush()

                    send (fun w ->
                        w.WriteString("event", "stopped")
                        w.WriteString("reason", reason)
                        w.WriteString("file", file)
                        w.WriteNumber("line", line))

                    serve depth)

let mutable private attached: Engine = null

/// the script's side: connect to the adapter named by WEIR_DEBUG_PORT, take
/// its breakpoints and wait for its word to start. Without the variable a
/// run has no debugger and pays nothing
let attachIfAsked () : unit =
    match Environment.GetEnvironmentVariable "WEIR_DEBUG_PORT" with
    | null
    | "" -> ()
    | port ->
        match Int32.TryParse port with
        | true, p ->
            // a child the script starts must not attach to this session too
            Environment.SetEnvironmentVariable("WEIR_DEBUG_PORT", null)

            try
                let client = new Net.Sockets.TcpClient()
                client.NoDelay <- true
                client.Connect(Net.IPAddress.Loopback, p)
                let stream = client.GetStream()

                // the adapter takes only the script it started: its word first
                let word =
                    match Environment.GetEnvironmentVariable "WEIR_DEBUG_TOKEN" with
                    | null -> ""
                    | t -> t

                Environment.SetEnvironmentVariable("WEIR_DEBUG_TOKEN", null)
                let hello = Encoding.ASCII.GetBytes(word + "\n")
                stream.Write(hello, 0, hello.Length)
                stream.Flush()
                let engine = Engine(stream)
                attached <- engine
                engine.AwaitStart()
                Eval.onNode <- Action<obj, Eval.Scope>(fun node scope -> engine.OnNode(node, scope))
            with ex ->
                Console.Error.WriteLine
                    $"weir: the debugger at port {p} did not answer — running without it ({ex.Message})"
        | _ -> ()

// ---- the adapter ------------------------------------------------------------

type private Adapter() =
    let input = Console.OpenStandardInput()
    let output = Console.OpenStandardOutput()
    let outGate = obj ()
    let mutable seq = 0
    let mutable child: Diagnostics.Process = null
    let mutable link: Stream = null
    let linkGate = obj ()
    let replies = new Collections.Concurrent.BlockingCollection<string>()
    let mutable listener: Net.Sockets.TcpListener = null
    let mutable stopOnEntry = false
    let pending = ResizeArray<string * int list>()
    let mutable finished = false

    let sendMessage (build: Utf8JsonWriter -> unit) : unit =
        use buf = new MemoryStream()

        (use w = new Utf8JsonWriter(buf)
         w.WriteStartObject()
         build w
         w.WriteEndObject())

        lock outGate (fun () ->
            let header = Encoding.ASCII.GetBytes $"Content-Length: {buf.Length}\r\n\r\n"
            output.Write(header, 0, header.Length)
            output.Write(buf.GetBuffer(), 0, int buf.Length)
            output.Flush())

    let nextSeq () = Threading.Interlocked.Increment &seq

    let event (name: string) (body: Utf8JsonWriter -> unit) : unit =
        sendMessage (fun w ->
            w.WriteNumber("seq", nextSeq ())
            w.WriteString("type", "event")
            w.WriteString("event", name)
            w.WriteStartObject "body"
            body w
            w.WriteEndObject())

    let respond (request: JsonElement) (ok: bool) (message: string) (body: Utf8JsonWriter -> unit) : unit =
        sendMessage (fun w ->
            w.WriteNumber("seq", nextSeq ())
            w.WriteString("type", "response")
            w.WriteNumber("request_seq", num request "seq")
            w.WriteBoolean("success", ok)
            w.WriteString("command", str request "command")

            if not ok then
                w.WriteString("message", message)

            w.WriteStartObject "body"
            body w
            w.WriteEndObject())

    let toChild (build: Utf8JsonWriter -> unit) : bool =
        match link with
        | null -> false
        | s ->
            try
                writeLine s linkGate build
                true
            with _ ->
                false

    // ask the stopped script something and wait for its one-line answer
    let ask (build: Utf8JsonWriter -> unit) : JsonDocument option =
        if toChild build then
            let mutable text = null

            if replies.TryTake(&text, 5000) then
                Some(JsonDocument.Parse text)
            else
                None
        else
            None

    let sendBreakpoints (file: string) (lines: int list) : unit =
        toChild (fun w ->
            w.WriteString("do", "breakpoints")
            w.WriteString("file", file)
            w.WriteStartArray "lines"

            for l in lines do
                w.WriteNumberValue l

            w.WriteEndArray())
        |> ignore

    let forward (category: string) (stream: StreamReader) : Threading.Thread =
        let pump () =
            let buf = Array.zeroCreate<char> 4096
            let mutable n = stream.Read(buf, 0, buf.Length)

            while n > 0 do
                let text = String(buf, 0, n)

                event "output" (fun w ->
                    w.WriteString("category", category)
                    w.WriteString("output", text))

                n <- stream.Read(buf, 0, buf.Length)

        let t = Threading.Thread(pump, IsBackground = true)
        t.Start()
        t

    // the script's side of the socket: its events go to the editor, its
    // answers to whoever asked
    let readChild (s: Stream) : unit =
        let reader = new StreamReader(s, UTF8Encoding false)

        let pump () =
            try
                let mutable text = reader.ReadLine()

                while not (isNull text) do
                    (use doc = JsonDocument.Parse text
                     let m = doc.RootElement

                     if str m "event" = "stopped" then
                         event "stopped" (fun w ->
                             w.WriteString("reason", str m "reason")
                             w.WriteNumber("threadId", 1)
                             w.WriteBoolean("allThreadsStopped", true))
                     else
                         replies.Add text)

                    text <- reader.ReadLine()
            with _ ->
                ()

        Threading.Thread(pump, IsBackground = true).Start()

    let launch (request: JsonElement) : unit =
        let args =
            match request.TryGetProperty "arguments" with
            | true, a -> a
            | _ -> Unchecked.defaultof<JsonElement>

        let program = str args "program"

        if program = "" then
            respond request false "launch needs \"program\": the script to run" ignore
        else
            stopOnEntry <- flag args "stopOnEntry"
            listener <- new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
            listener.Start()
            let port = (listener.LocalEndpoint :?> Net.IPEndPoint).Port
            let psi = Diagnostics.ProcessStartInfo(Environment.ProcessPath)
            psi.ArgumentList.Add program

            match args.TryGetProperty "args" with
            | true, a when a.ValueKind = JsonValueKind.Array ->
                for x in a.EnumerateArray() do
                    if x.ValueKind = JsonValueKind.String then
                        psi.ArgumentList.Add(x.GetString())
            | _ -> ()

            match str args "cwd" with
            | "" -> ()
            | cwd -> psi.WorkingDirectory <- cwd

            psi.Environment["WEIR_DEBUG_PORT"] <- string port

            // a loopback port is open to every local user: the script proves
            // itself with a word only it was given
            let token =
                Convert.ToHexString(Security.Cryptography.RandomNumberGenerator.GetBytes 16)

            psi.Environment["WEIR_DEBUG_TOKEN"] <- token
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.RedirectStandardInput <- true
            psi.UseShellExecute <- false

            try
                child <- Diagnostics.Process.Start psi
                child.StandardInput.Close()
                let outPump = forward "stdout" child.StandardOutput
                let errPump = forward "stderr" child.StandardError

                // the script connects before its first statement; one that
                // dies first ends the wait. A connection that does not open
                // with the word is dropped and the wait goes on
                let waiter () =
                    let mutable linked = false

                    while not linked && not child.HasExited do
                        if listener.Pending() then
                            let client = listener.AcceptTcpClient()
                            client.NoDelay <- true
                            client.ReceiveTimeout <- 2000
                            let stream = client.GetStream()
                            let said = StringBuilder()

                            try
                                let mutable b = stream.ReadByte()

                                while b >= 0 && b <> int '\n' && said.Length <= 64 do
                                    said.Append(char b) |> ignore
                                    b <- stream.ReadByte()
                            with _ ->
                                ()

                            if
                                Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                                    ReadOnlySpan(Encoding.ASCII.GetBytes(said.ToString())),
                                    ReadOnlySpan(Encoding.ASCII.GetBytes token)
                                )
                            then
                                client.ReceiveTimeout <- 0
                                link <- stream
                                linked <- true
                            else
                                client.Close()
                        else
                            Threading.Thread.Sleep 20

                    listener.Stop()

                    if linked then
                        readChild link

                        for file, lines in lock pending (fun () -> List.ofSeq pending) do
                            sendBreakpoints file lines

                        event "initialized" ignore

                    child.WaitForExit()
                    outPump.Join 2000 |> ignore
                    errPump.Join 2000 |> ignore
                    finished <- true
                    event "exited" (fun w -> w.WriteNumber("exitCode", child.ExitCode))
                    event "terminated" ignore

                Threading.Thread(waiter, IsBackground = true).Start()
                respond request true "" ignore
            with ex ->
                respond request false $"could not start {program}: {ex.Message}" ignore

    let stop () : unit =
        match child with
        | null -> ()
        | c ->
            try
                if not c.HasExited then
                    c.Kill true
            with _ ->
                ()

    let handle (request: JsonElement) : bool =
        let args =
            match request.TryGetProperty "arguments" with
            | true, a -> a
            | _ -> Unchecked.defaultof<JsonElement>

        match str request "command" with
        | "initialize" ->
            respond request true "" (fun w ->
                w.WriteBoolean("supportsConfigurationDoneRequest", true)
                w.WriteBoolean("supportsTerminateRequest", true)
                w.WriteBoolean("supportsEvaluateForHovers", true))

            true
        | "launch" ->
            launch request
            true
        | "setBreakpoints" ->
            let file =
                match args.TryGetProperty "source" with
                | true, src -> str src "path"
                | _ -> ""

            let lines =
                match args.TryGetProperty "breakpoints" with
                | true, bs when bs.ValueKind = JsonValueKind.Array -> [ for b in bs.EnumerateArray() -> num b "line" ]
                | _ -> []

            lock pending (fun () ->
                pending.RemoveAll(fun (f, _) -> f = file) |> ignore
                pending.Add(file, lines))

            sendBreakpoints file lines

            respond request true "" (fun w ->
                w.WriteStartArray "breakpoints"

                for l in lines do
                    w.WriteStartObject()
                    w.WriteBoolean("verified", true)
                    w.WriteNumber("line", l)
                    w.WriteEndObject()

                w.WriteEndArray())

            true
        | "setExceptionBreakpoints" ->
            respond request true "" ignore
            true
        | "configurationDone" ->
            toChild (fun w ->
                w.WriteString("do", "start")
                w.WriteBoolean("stopOnEntry", stopOnEntry))
            |> ignore

            respond request true "" ignore
            true
        | "threads" ->
            respond request true "" (fun w ->
                w.WriteStartArray "threads"
                w.WriteStartObject()
                w.WriteNumber("id", 1)
                w.WriteString("name", "script")
                w.WriteEndObject()
                w.WriteEndArray())

            true
        | "stackTrace" ->
            match ask (fun w -> w.WriteString("do", "stack")) with
            | Some doc ->
                use doc = doc

                respond request true "" (fun w ->
                    w.WriteStartArray "stackFrames"
                    let mutable count = 0

                    match doc.RootElement.TryGetProperty "frames" with
                    | true, fs ->
                        for f in fs.EnumerateArray() do
                            count <- count + 1
                            let file = str f "file"
                            w.WriteStartObject()
                            w.WriteNumber("id", num f "id")
                            w.WriteString("name", (if num f "id" = 0 then "script" else "function"))
                            w.WriteStartObject "source"
                            w.WriteString("name", Path.GetFileName file)
                            w.WriteString("path", file)
                            w.WriteEndObject()
                            w.WriteNumber("line", num f "line")
                            w.WriteNumber("column", 1)
                            w.WriteEndObject()
                    | _ -> ()

                    w.WriteEndArray()
                    w.WriteNumber("totalFrames", count))
            | None -> respond request false "the script is not stopped" ignore

            true
        | "scopes" ->
            let frame = num args "frameId"

            respond request true "" (fun w ->
                w.WriteStartArray "scopes"

                for name, offset in [ "Locals", 1; "Top level", 2 ] do
                    w.WriteStartObject()
                    w.WriteString("name", name)
                    w.WriteNumber("variablesReference", frame * 2 + offset)
                    w.WriteBoolean("expensive", (offset = 2))
                    w.WriteEndObject()

                w.WriteEndArray())

            true
        | "variables" ->
            let reference = num args "variablesReference"
            let frame = (reference - 1) / 2
            let which = if reference % 2 = 1 then "locals" else "globals"

            match
                ask (fun w ->
                    w.WriteString("do", which)
                    w.WriteNumber("frame", frame))
            with
            | Some doc ->
                use doc = doc

                respond request true "" (fun w ->
                    w.WriteStartArray "variables"

                    match doc.RootElement.TryGetProperty "variables" with
                    | true, vs ->
                        for v in vs.EnumerateArray() do
                            w.WriteStartObject()
                            w.WriteString("name", str v "name")
                            w.WriteString("value", str v "value")
                            w.WriteString("type", str v "type")
                            w.WriteNumber("variablesReference", 0)
                            w.WriteEndObject()
                    | _ -> ()

                    w.WriteEndArray())
            | None -> respond request false "the script is not stopped" ignore

            true
        | "evaluate" ->
            match
                ask (fun w ->
                    w.WriteString("do", "value")
                    w.WriteNumber("frame", num args "frameId")
                    w.WriteString("name", (str args "expression").Trim()))
            with
            | Some doc when flag doc.RootElement "found" ->
                use doc = doc

                respond request true "" (fun w ->
                    w.WriteString("result", str doc.RootElement "value")
                    w.WriteString("type", str doc.RootElement "type")
                    w.WriteNumber("variablesReference", 0))
            | Some doc ->
                doc.Dispose()
                respond request false "only a name in scope can be read here" ignore
            | None -> respond request false "the script is not stopped" ignore

            true
        | "continue"
        | "next"
        | "stepIn"
        | "stepOut" as command ->
            toChild (fun w -> w.WriteString("do", command)) |> ignore

            respond request true "" (fun w ->
                if command = "continue" then
                    w.WriteBoolean("allThreadsContinued", true))

            true
        | "pause" ->
            respond request false "a running script stops at its next breakpoint; pause is not supported" ignore
            true
        | "terminate"
        | "disconnect" ->
            stop ()
            respond request true "" ignore
            false
        | _ ->
            respond request false "not supported" ignore
            true

    member _.Run() : int =
        let header = ResizeArray<byte>()
        let mutable go = true

        let readExact (n: int) : byte[] =
            let buf = Array.zeroCreate<byte> n
            let mutable got = 0

            while got < n && go do
                let r = input.Read(buf, got, n - got)

                if r <= 0 then go <- false else got <- got + r

            buf

        while go do
            // a message: header lines, a blank line, then the counted body
            header.Clear()
            let mutable length = -1
            let mutable inHeader = true

            while inHeader && go do
                let b = input.ReadByte()

                if b < 0 then
                    go <- false
                elif b = int '\n' then
                    let line = Encoding.ASCII.GetString(header.ToArray()).TrimEnd('\r')
                    header.Clear()

                    if line = "" then
                        inHeader <- false
                    elif line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) then
                        match Int32.TryParse(line.Substring(15).Trim()) with
                        | true, n when n >= 0 && n <= 16 * 1024 * 1024 -> length <- n
                        | _ -> go <- false
                else
                    header.Add(byte b)

            if go && length >= 0 then
                let body = readExact length

                if go then
                    try
                        use doc = JsonDocument.Parse(ReadOnlyMemory body)

                        if str doc.RootElement "type" = "request" then
                            go <- handle doc.RootElement
                    with :? JsonException ->
                        ()

        stop ()
        0

/// `weir dap`: the adapter an editor starts
let runAdapter () : int = Adapter().Run()
