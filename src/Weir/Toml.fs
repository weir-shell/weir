module Weir.Toml

// TOML 1.1 reading [D:from-toml]: an owned parser, no dependency. Every
// valid 1.0 document is valid 1.1. Datetimes are recognised and validated
// but read as their text (weir has no local date/time types; an offset
// date-time binds to Instant through [<Iso8601>]); inf/nan parse, and a
// float field that reads one refuses (weir floats are finite). The table tree converts to Yaml.Node, so
// `from toml T` reuses the yaml binder whole.

open System
open System.Collections.Generic
open System.Globalization
open System.Text

exception private TomlError of line: int * message: string

// how a table came to exist decides what may extend it later
type private Kind =
    // an intermediate of a [header] path: a later [header] may define it once
    | Implicit
    // defined by a [header], or an element of an array of tables
    | Header
    // created by dotted keys: a header may add sub-tables, never redefine it
    | Dotted
    // an inline table and everything inside it: closed
    | Inline

type private Value =
    | VStr of string * int
    // a number in normalised decimal text
    | VNum of string * int
    | VBool of bool * int
    | VDate of string * int
    // a static array: closed
    | VArr of List<Value> * int
    | VTab of Tab
    | VAot of List<Tab> * int

and private Tab =
    { Keys: List<string>
      Values: Dictionary<string, Value>
      mutable Kind: Kind
      Line: int }

let private newTab kind line =
    { Keys = List()
      Values = Dictionary()
      Kind = kind
      Line = line }

let private add (t: Tab) (k: string) (v: Value) =
    t.Keys.Add k
    t.Values[k] <- v

let private tryGet (t: Tab) (k: string) =
    match t.Values.TryGetValue k with
    | true, v -> Some v
    | _ -> None

let private maxDepth = 500

type private State =
    { Src: string
      mutable Pos: int
      mutable Line: int }

let private fail (s: State) msg = raise (TomlError(s.Line, msg))

let private eof (s: State) = s.Pos >= s.Src.Length
let private peekAt (s: State) k = if s.Pos + k < s.Src.Length then s.Src[s.Pos + k] else '\000'
let private peek s = peekAt s 0

let private startsWith (s: State) (lit: string) =
    String.CompareOrdinal(s.Src, s.Pos, lit, 0, lit.Length) = 0

// control characters TOML forbids outside escapes (tab is allowed)
let private isControl (c: char) =
    (c < ' ' && c <> '\t') || c = '\u007f'

let private quoted (k: string) = "'" + k + "'"

let private skipWs (s: State) =
    while not (eof s) && (peek s = ' ' || peek s = '\t') do
        s.Pos <- s.Pos + 1

// a newline is LF or CRLF; a bare CR is not one
let private atNewline (s: State) =
    peek s = '\n' || (peek s = '\r' && peekAt s 1 = '\n')

let private newline (s: State) =
    if peek s = '\r' then
        if peekAt s 1 = '\n' then s.Pos <- s.Pos + 2 else fail s "a carriage return must be followed by a newline"
    else
        s.Pos <- s.Pos + 1

    s.Line <- s.Line + 1

let private skipComment (s: State) =
    if peek s = '#' then
        s.Pos <- s.Pos + 1

        while not (eof s) && not (atNewline s) do
            let c = peek s

            if c = '\r' then fail s "a carriage return must be followed by a newline"
            elif isControl c then fail s "comments may not contain control characters"
            else s.Pos <- s.Pos + 1

// inside arrays and inline tables: whitespace, comments and newlines
let private skipBlank (s: State) =
    let mutable go = true

    while go do
        skipWs s

        if peek s = '#' then skipComment s
        elif not (eof s) && (peek s = '\n' || peek s = '\r') then newline s
        else go <- false

// after a key/value or a header: optional comment, then a newline or the end
let private endOfLine (s: State) what =
    skipWs s
    skipComment s

    if eof s then ()
    elif peek s = '\n' || peek s = '\r' then newline s
    else fail s $"unexpected '{peek s}' after the {what} — one {what} per line"

// ---- strings ---------------------------------------------------------------

let private hexValue (s: State) (digits: string) =
    match Int32.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture) with
    | true, n -> n
    | _ -> fail s $"'{digits}' is not a hexadecimal escape"

let private appendCodePoint (s: State) (sb: StringBuilder) (cp: int) =
    if cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF) then
        fail s $"U+{cp:X} is not a Unicode scalar value"
    else
        sb.Append(Char.ConvertFromUtf32 cp) |> ignore

// one escape after the backslash (the multi-line line-ending form is the caller's)
let private escape (s: State) (sb: StringBuilder) =
    let take n =
        if s.Pos + n > s.Src.Length then fail s "the escape is cut short by the end of input"
        let d = s.Src.Substring(s.Pos, n)

        if d |> Seq.forall Uri.IsHexDigit then
            s.Pos <- s.Pos + n
            d
        else
            fail s $"'\\{s.Src[s.Pos - 1]}{d}' needs {n} hexadecimal digits"

    let c = peek s
    s.Pos <- s.Pos + 1

    match c with
    | 'b' -> sb.Append '\b' |> ignore
    | 't' -> sb.Append '\t' |> ignore
    | 'n' -> sb.Append '\n' |> ignore
    | 'f' -> sb.Append '\f' |> ignore
    | 'r' -> sb.Append '\r' |> ignore
    | 'e' -> sb.Append '\u001b' |> ignore
    | '"' -> sb.Append '"' |> ignore
    | '\\' -> sb.Append '\\' |> ignore
    | 'x' -> appendCodePoint s sb (hexValue s (take 2))
    | 'u' -> appendCodePoint s sb (hexValue s (take 4))
    | 'U' ->
        let d = take 8

        match Int64.TryParse(d, NumberStyles.HexNumber, CultureInfo.InvariantCulture) with
        | true, n when n <= 0x10FFFFL -> appendCodePoint s sb (int n)
        | _ -> fail s $"U+{d} is not a Unicode scalar value"
    | c -> fail s $"'\\{c}' is not a TOML escape"

let private basicString (s: State) =
    s.Pos <- s.Pos + 1
    let sb = StringBuilder()
    let mutable go = true

    while go do
        if eof s || atNewline s || peek s = '\r' then
            fail s "a string must close on the line it opens (\"\"\" spans lines)"

        match peek s with
        | '"' ->
            s.Pos <- s.Pos + 1
            go <- false
        | '\\' ->
            s.Pos <- s.Pos + 1
            escape s sb
        | c when isControl c -> fail s "strings may not contain control characters — use an escape"
        | c ->
            sb.Append c |> ignore
            s.Pos <- s.Pos + 1

    sb.ToString()

let private literalString (s: State) =
    s.Pos <- s.Pos + 1
    let start = s.Pos

    while not (eof s) && peek s <> '\'' do
        if atNewline s || peek s = '\r' then
            fail s "a string must close on the line it opens (''' spans lines)"
        elif isControl (peek s) then
            fail s "strings may not contain control characters"
        else
            s.Pos <- s.Pos + 1

    if eof s then fail s "a string must close on the line it opens (''' spans lines)"
    let text = s.Src.Substring(start, s.Pos - start)
    s.Pos <- s.Pos + 1
    text

// """ and ''': the first newline is trimmed; up to two quote characters may
// sit against the closing delimiter
let private multiline (s: State) (q: char) =
    s.Pos <- s.Pos + 3

    if atNewline s then newline s

    let sb = StringBuilder()
    let delim = String(q, 3)
    let mutable go = true

    while go do
        if eof s then
            fail s $"the multi-line string needs its closing {delim}"
        elif startsWith s delim then
            let mutable n = 0

            while peekAt s n = q do
                n <- n + 1

            if n > 5 then fail s $"too many {q} characters at the end of a multi-line string"
            sb.Append(q, n - 3) |> ignore
            s.Pos <- s.Pos + n
            go <- false
        elif atNewline s then
            newline s
            sb.Append '\n' |> ignore
        elif peek s = '\\' && q = '"' then
            s.Pos <- s.Pos + 1
            // a line-ending backslash trims the newline and the whitespace after it
            let save = s.Pos
            skipWs s

            if atNewline s then
                while atNewline s || peek s = ' ' || peek s = '\t' do
                    if atNewline s then newline s else s.Pos <- s.Pos + 1
            else
                s.Pos <- save
                escape s sb
        elif peek s = '\r' then
            fail s "a carriage return must be followed by a newline"
        elif isControl (peek s) then
            fail s "strings may not contain control characters"
        else
            sb.Append(peek s) |> ignore
            s.Pos <- s.Pos + 1

    sb.ToString()

// ---- keys ------------------------------------------------------------------

let private isBare (c: char) =
    (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_' || c = '-'

let private simpleKey (s: State) =
    match peek s with
    | '"' when startsWith s "\"\"\"" -> fail s "a multi-line string cannot be a key"
    | '\'' when startsWith s "'''" -> fail s "a multi-line string cannot be a key"
    | '"' -> basicString s
    | '\'' -> literalString s
    | c when isBare c ->
        let start = s.Pos

        while not (eof s) && isBare (peek s) do
            s.Pos <- s.Pos + 1

        s.Src.Substring(start, s.Pos - start)
    | c when eof s -> fail s "expected a key"
    | c -> fail s $"'{c}' cannot start a key — bare keys are letters, digits, _ and -; quote anything else"

let private dottedKey (s: State) =
    let keys = List<string>()
    keys.Add(simpleKey s)
    skipWs s

    while peek s = '.' do
        s.Pos <- s.Pos + 1
        skipWs s
        keys.Add(simpleKey s)
        skipWs s

    List.ofSeq keys

// ---- numbers, booleans, datetimes ------------------------------------------

let private rx (p: string) = RegularExpressions.Regex(p, RegularExpressions.RegexOptions.CultureInvariant)

let private decInt = rx @"^[+-]?(0|[1-9](_?[0-9])*)$"
let private hexInt = rx @"^0x[0-9A-Fa-f](_?[0-9A-Fa-f])*$"
let private octInt = rx @"^0o[0-7](_?[0-7])*$"
let private binInt = rx @"^0b[01](_?[01])*$"

let private float' =
    rx @"^[+-]?(0|[1-9](_?[0-9])*)((\.[0-9](_?[0-9])*)([eE][+-]?[0-9](_?[0-9])*)?|[eE][+-]?[0-9](_?[0-9])*)$"

let private nonFinite = rx @"^[+-]?(inf|nan)$"

let private dateTime =
    rx
        @"^([0-9]{4})-([0-9]{2})-([0-9]{2})([Tt ]([0-9]{2}):([0-9]{2})(:([0-9]{2})(\.[0-9]+)?)?([Zz]|[+-]([0-9]{2}):([0-9]{2}))?)?$"

let private localTime = rx @"^([0-9]{2}):([0-9]{2})(:([0-9]{2})(\.[0-9]+)?)?$"

let private validTime (s: State) (h: string) (m: string) (sec: string) =
    if int h > 23 || int m > 59 || (sec <> "" && int sec > 60) then
        fail s "the time is out of range (hours 00-23, minutes 00-59, seconds 00-60)"

let private datetimeOrNone (s: State) (tok: string) =
    let d = dateTime.Match tok

    if d.Success then
        let y, mo, dy = int d.Groups[1].Value, int d.Groups[2].Value, int d.Groups[3].Value

        if mo < 1 || mo > 12 || dy < 1 || dy > DateTime.DaysInMonth(max y 1, mo) then
            fail s $"'{tok}' is not a calendar date"

        if d.Groups[4].Success then
            validTime s d.Groups[5].Value d.Groups[6].Value d.Groups[8].Value

            if d.Groups[11].Success && (int d.Groups[11].Value > 23 || int d.Groups[12].Value > 59) then
                fail s "the offset is out of range"

        Some tok
    else
        let t = localTime.Match tok

        if t.Success then
            validTime s t.Groups[1].Value t.Groups[2].Value t.Groups[4].Value
            Some tok
        else
            None

let private number (s: State) (tok: string) =
    let clean = tok.Replace("_", "")

    let radix (digits: string) (b: int) =
        try
            let v = Convert.ToUInt64(digits, b)

            if v > uint64 Int64.MaxValue then fail s $"'{tok}' is outside the 64-bit integer range"
            string v
        with
        | TomlError _ -> reraise ()
        | _ -> fail s $"'{tok}' is outside the 64-bit integer range"

    if decInt.IsMatch tok then
        match Int64.TryParse(clean, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
        | true, v -> Some(string v)
        | _ -> fail s $"'{tok}' is outside the 64-bit integer range"
    elif hexInt.IsMatch tok then Some(radix (clean.Substring 2) 16)
    elif octInt.IsMatch tok then Some(radix (clean.Substring 2) 8)
    elif binInt.IsMatch tok then Some(radix (clean.Substring 2) 2)
    // inf/nan and overflowing floats parse: only a float field that reads
    // one refuses (weir floats are finite), so an unread value never fails
    // the document
    elif float'.IsMatch tok || nonFinite.IsMatch tok then
        Some(clean.TrimStart '+')
    else
        None

// a bare value token: number, boolean or datetime. A date may be followed by
// a space and a time (`1979-05-27 07:32:00`)
let private bareToken (s: State) =
    let isTok c =
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
        || c = '_' || c = '+' || c = '-' || c = '.' || c = ':'

    let start = s.Pos

    while not (eof s) && isTok (peek s) do
        s.Pos <- s.Pos + 1

    let first = s.Src.Substring(start, s.Pos - start)

    if
        peek s = ' '
        && Char.IsDigit(peekAt s 1)
        && Char.IsDigit(peekAt s 2)
        && peekAt s 3 = ':'
        && rx(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$").IsMatch first
    then
        s.Pos <- s.Pos + 1

        while not (eof s) && isTok (peek s) do
            s.Pos <- s.Pos + 1

    s.Src.Substring(start, s.Pos - start)

// ---- values ----------------------------------------------------------------

let rec private value (s: State) (depth: int) : Value =
    if depth > maxDepth then
        fail s $"nesting is too deep (limit {maxDepth})"

    let line = s.Line

    match peek s with
    | '"' when startsWith s "\"\"\"" -> VStr(multiline s '"', line)
    | '\'' when startsWith s "'''" -> VStr(multiline s '\'', line)
    | '"' -> VStr(basicString s, line)
    | '\'' -> VStr(literalString s, line)
    | '[' -> array s depth
    | '{' -> VTab(inlineTable s depth)
    | _ when eof s || atNewline s || peek s = '#' -> fail s "a key needs a value after '='"
    | _ ->
        let tok = bareToken s

        if tok = "" then fail s $"'{peek s}' cannot start a value"
        elif tok = "true" then VBool(true, line)
        elif tok = "false" then VBool(false, line)
        else
            match datetimeOrNone s tok with
            | Some d -> VDate(d, line)
            | None ->
                match number s tok with
                | Some n -> VNum(n, line)
                | None -> fail s $"'{tok}' is not a TOML value (strings are quoted)"

and private array (s: State) (depth: int) =
    let line = s.Line
    s.Pos <- s.Pos + 1
    let items = List<Value>()
    let mutable go = true

    while go do
        skipBlank s

        if peek s = ']' then
            s.Pos <- s.Pos + 1
            go <- false
        elif eof s then
            fail s "the array needs its closing ']'"
        else
            items.Add(value s (depth + 1))
            skipBlank s

            match peek s with
            | ',' -> s.Pos <- s.Pos + 1
            | ']' ->
                s.Pos <- s.Pos + 1
                go <- false
            | _ when eof s -> fail s "the array needs its closing ']'"
            | c -> fail s $"expected ',' or ']' in the array, got '{c}'"

    VArr(items, line)

// a key/value into `t`: dotted keys create (or walk) Dotted tables
and private keyval (s: State) (t: Tab) (depth: int) =
    let line = s.Line
    let keys = dottedKey s
    skipWs s

    if peek s <> '=' then
        fail s $"expected '=' after the key {quoted (List.last keys)}"

    s.Pos <- s.Pos + 1
    skipWs s
    let v = value s depth

    let rec put (t: Tab) keys =
        match keys with
        | [ k ] ->
            if t.Values.ContainsKey k then
                fail s $"the key {quoted k} is defined twice"

            add t k v
        | k :: rest ->
            match tryGet t k with
            | None ->
                let sub = newTab Dotted line
                add t k (VTab sub)
                put sub rest
            | Some(VTab sub) when sub.Kind = Dotted -> put sub rest
            | Some(VTab _) -> fail s $"the table {quoted k} is already defined — a dotted key cannot reopen it"
            | Some _ -> fail s $"{quoted k} is already a value — a dotted key cannot extend it"
        | [] -> fail s "expected a key"

    put t keys

and private inlineTable (s: State) (depth: int) =
    if depth > maxDepth then
        fail s $"nesting is too deep (limit {maxDepth})"

    let t = newTab Dotted s.Line
    s.Pos <- s.Pos + 1
    let mutable go = true

    while go do
        skipBlank s

        if peek s = '}' then
            s.Pos <- s.Pos + 1
            go <- false
        elif eof s then
            fail s "the inline table needs its closing '}'"
        else
            keyval s t (depth + 1)
            skipBlank s

            match peek s with
            | ',' -> s.Pos <- s.Pos + 1
            | '}' ->
                s.Pos <- s.Pos + 1
                go <- false
            | _ when eof s -> fail s "the inline table needs its closing '}'"
            | c -> fail s $"expected ',' or '}}' in the inline table, got '{c}'"

    // closed from here on: nothing may extend it or anything inside it
    let rec close (t: Tab) =
        t.Kind <- Inline

        for v in t.Values.Values do
            match v with
            | VTab sub -> close sub
            | _ -> ()

    close t
    t

// ---- headers ---------------------------------------------------------------

let private header (s: State) (root: Tab) : Tab =
    let aot = startsWith s "[["
    s.Pos <- s.Pos + (if aot then 2 else 1)
    skipWs s
    let keys = dottedKey s
    let close = if aot then "]]" else "]"

    if not (startsWith s close) then
        fail s $"expected '{close}' to close the table header"

    s.Pos <- s.Pos + close.Length
    let line = s.Line
    let name = String.Join(".", keys)

    let rec walk (t: Tab) keys =
        match keys with
        | [ k ] ->
            match tryGet t k, aot with
            | None, false ->
                let sub = newTab Header line
                add t k (VTab sub)
                sub
            | None, true ->
                let sub = newTab Header line
                add t k (VAot(List [ sub ], line))
                sub
            | Some(VAot(l, _)), true ->
                let sub = newTab Header line
                l.Add sub
                sub
            | Some(VTab sub), false when sub.Kind = Implicit ->
                sub.Kind <- Header
                sub
            | Some(VTab sub), false when sub.Kind = Header -> fail s $"the table [{name}] is defined twice"
            | Some(VTab sub), false when sub.Kind = Dotted ->
                fail s $"[{name}] is already defined by dotted keys — a header cannot redefine it"
            | Some(VTab _), _ -> fail s $"[{name}] is an inline table, which is closed"
            | Some(VAot _), false -> fail s $"{quoted name} is an array of tables — [[{name}]] adds an element"
            | Some(VArr _), true -> fail s $"{quoted name} is a static array — [[{name}]] cannot extend it"
            | Some _, _ -> fail s $"{quoted name} is already a value, not a table"
        | k :: rest ->
            match tryGet t k with
            | None ->
                let sub = newTab Implicit line
                add t k (VTab sub)
                walk sub rest
            | Some(VTab sub) when sub.Kind <> Inline -> walk sub rest
            | Some(VAot(l, _)) -> walk l[l.Count - 1] rest
            | Some(VTab _) -> fail s $"{quoted k} is an inline table, which is closed"
            | Some _ -> fail s $"{quoted k} is already a value, not a table"
        | [] -> fail s "expected a table name"

    let t = walk root keys
    endOfLine s "table header"
    t

// ---- document --------------------------------------------------------------

let rec private toNode (v: Value) : Yaml.Node =
    match v with
    | VStr(text, l)
    | VDate(text, l) -> Yaml.NScalar(text, true, l)
    | VNum(text, l) -> Yaml.NScalar(text, false, l)
    | VBool(b, l) -> Yaml.NScalar((if b then "true" else "false"), false, l)
    | VArr(items, l) -> Yaml.NSeq(items |> Seq.map toNode |> List.ofSeq, l)
    | VTab t -> tableNode t
    | VAot(l, line) -> Yaml.NSeq(l |> Seq.map tableNode |> List.ofSeq, line)

and private tableNode (t: Tab) : Yaml.Node =
    Yaml.NMap(t.Keys |> Seq.map (fun k -> k, toNode t.Values[k]) |> List.ofSeq, t.Line)

/// Parse a TOML document into the yaml node tree (a mapping at the top);
/// errors read `line N: …`
let parse (text: string) : Result<Yaml.Node, string> =
    let s = { Src = text; Pos = 0; Line = 1 }
    let root = newTab Header 1
    let mutable current = root

    try
        if peek s = '\uFEFF' then
            s.Pos <- 1

        while not (eof s) do
            skipWs s

            match peek s with
            | '#' ->
                skipComment s

                if not (eof s) then newline s
            | '\n'
            | '\r' -> newline s
            | '[' -> current <- header s root
            | _ when eof s -> ()
            | _ ->
                keyval s current 0
                endOfLine s "key/value pair"

        Ok(tableNode root)
    with TomlError(line, msg) ->
        Error $"line {line}: {msg}"
