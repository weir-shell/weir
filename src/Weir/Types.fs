module Weir.Types

// Reifier desugar targets carry an un-typeable '|' prefix
// [D:drop-reify-builtins]: identifiers start [A-Za-z_], so `| complete`
// resolves while user code cannot name it. Suggestion and completion
// pools filter to user-typeable names.
let isUserName (n: string) =
    n.Length > 0 && (System.Char.IsLetter n[0] || n[0] = '_')

// Receipt records [D:receipts-read-only]: only the runtime mints these
// (`| complete`, `ls`/`Dir.stat`/`File.stat`, `Http.send`), so a literal
// asserts a run that never happened and an update rewrites one — and a
// derived field (Completed.ok) could contradict its source. Both forms
// are refused, each naming where the value comes from and the
// anonymous-record escape a row-typed function takes. Per type: the
// derived fields (a literal missing only those is still the receipt's
// shape, so it gets the teaching rather than the generic no-such-record
// error), the provenance sentence, what the record reports, and a
// ground-typed anonymous example ([D:anon-literals] refuses `[]`).
type Receipt =
    { Article: string
      Derived: string list
      Source: string
      Reports: string
      Anon: string }

let receipts: Map<string, Receipt> =
    Map
        [ "Completed",
          { Article = "a"
            Derived = [ "ok" ]
            Source = "a Completed is what '| complete' returns — run a command to get one (sh -c \"exit 3\" | complete)"
            Reports = "a run that happened (ok follows exitCode)"
            Anon = "{| exitCode = 3 |}" }
          "FileRow",
          { Article = "a"
            Derived = [ "hidden" ]
            Source =
              "a FileRow is what 'ls', 'Dir.stat' and 'File.stat' return — stat a path to get one (File.stat \"a.txt\")"
            Reports = "a path as it was seen"
            Anon = "{| name = \"a.txt\"; path = \"a.txt\" |}" }
          "HttpResponse",
          { Article = "an"
            Derived = []
            Source = "an HttpResponse is what 'Http.send' returns — send a request to get one (Http.get url |> Http.send)"
            Reports = "a response that arrived"
            Anon = "{| status = 200 |}" } ]

let isReceipt (typeName: string) = Map.containsKey typeName receipts

/// the literal refusal (bare, named, or a bare literal that is the
/// receipt's shape minus its derived fields)
let receiptLiteralMsg (typeName: string) =
    let r = receipts[typeName]

    $"{r.Source}; a function over its fields takes any record with them, so a test can pass {r.Anon}"

/// the copy-and-update refusal; the same text at run time when a
/// row-typed updater reaches a receipt only at its call
let receiptUpdateMsg (typeName: string) =
    let r = receipts[typeName]

    $"{r.Article} {typeName} is read-only — it reports {r.Reports}, so no field changes after the fact; to carry different values build a record of your own, {r.Anon}"

// Parse/decode error excerpt [D:excerpt]: a builtin that embeds the
// caller's whole input in its error turns a 500KB invalid input into
// ~500KB of stderr (a log flood, printing the prefix of whatever
// derived value was fed in). So it is bounded: an input of 64 chars or
// fewer is quoted in full (the common typo stays readable, unchanged);
// a longer one shows a 64-char head and names the true length. The call
// site keeps its own surrounding quotes, so only the body is bounded.
[<Literal>]
let excerptHead = 64

/// String positions in Unicode code points [D:str-code-points]: a
/// surrogate pair is one position, so no index, length or cut can split
/// an astral char. An unpaired surrogate counts as one position.
module CodePoints =
    let private pairAt (s: string) (i: int) =
        System.Char.IsHighSurrogate s[i]
        && i + 1 < s.Length
        && System.Char.IsLowSurrogate s[i + 1]

    let private hasSurrogate (s: string) =
        System.MemoryExtensions.IndexOfAnyInRange(System.MemoryExtensions.AsSpan s, '\uD800', '\uDFFF') >= 0

    let private walkLength (s: string) : int =
        if not (hasSurrogate s) then
            s.Length
        else
            let mutable n = 0
            let mutable i = 0

            while i < s.Length do
                i <- i + (if pairAt s i then 2 else 1)
                n <- n + 1

            n

    /// `offset`, resuming a walk already at UTF-16 offset `fromUtf16` =
    /// code point `fromCp`: a run of increasing targets costs one pass
    let offsetFrom (s: string) (fromUtf16: int) (fromCp: int) (cp: int) : int =
        let mutable n = fromCp
        let mutable i = fromUtf16

        while n < cp && i < s.Length do
            i <- i + (if pairAt s i then 2 else 1)
            n <- n + 1

        i

    // a long string's code points are indexed once per string instance
    // [D:str-code-points], so a char loop over it is not quadratic: the
    // count, and the UTF-16 offset of every `stride`-th code point.
    // A short string is walked; the walk is cheaper than the lookup.
    [<Literal>]
    let private indexedFrom = 256

    [<Literal>]
    let private stride = 64

    [<AllowNullLiteral>]
    type private Index(count: int, marks: int[]) =
        member _.Count = count
        member _.Marks = marks

    let private indexes =
        System.Runtime.CompilerServices.ConditionalWeakTable<string, Index>()

    let private build (s: string) : Index =
        let marks = ResizeArray<int>()
        let mutable n = 0
        let mutable i = 0

        while i < s.Length do
            if n % stride = 0 then
                marks.Add i

            i <- i + (if pairAt s i then 2 else 1)
            n <- n + 1

        if n % stride = 0 then
            marks.Add i

        Index(n, marks.ToArray())

    // a long string with no surrogate needs no index [D:str-cp-plain]:
    // a position is its offset. It never enters the table, so a stream
    // of strings adds nothing; the last one each thread scanned is
    // remembered (weakly), so a loop over one string scans it once
    let private lastPlain =
        new System.Threading.ThreadLocal<System.WeakReference<string>>(fun () -> System.WeakReference<string>(null))

    // the long string's index; null when it is plain (no surrogate)
    let private indexOf (s: string) : Index =
        let memo = lastPlain.Value

        match memo.TryGetTarget() with
        | true, t when obj.ReferenceEquals(t, s) -> null
        | _ ->
            match indexes.TryGetValue s with
            | true, ix -> ix
            | _ when not (hasSurrogate s) ->
                memo.SetTarget s
                null
            | _ -> indexes.GetValue(s, System.Runtime.CompilerServices.ConditionalWeakTable<string, Index>.CreateValueCallback build)

    /// the number of entries in the index table (tests: plain strings add none)
    let indexedForTest () : int = Seq.length indexes

    /// the number of code points
    let length (s: string) : int =
        if s.Length <= indexedFrom then
            walkLength s
        else
            match indexOf s with
            | null -> s.Length
            | ix -> ix.Count

    /// the UTF-16 offset of code point `cp`, clamped to [0, s.Length] —
    /// O(cp) on a short string, O(stride) on an indexed one
    let offset (s: string) (cp: int) : int =
        if cp <= 0 then
            0
        elif s.Length <= indexedFrom then
            offsetFrom s 0 0 cp
        else
            match indexOf s with
            | null -> min cp s.Length
            | ix ->
                let marks = ix.Marks
                let k = min (cp / stride) (marks.Length - 1)
                offsetFrom s marks[k] (k * stride) cp

    /// the code-point index of a UTF-16 offset
    let ofOffset (s: string) (utf16: int) : int =
        if s.Length > indexedFrom && isNull (indexOf s) then
            utf16
        else
            length (s.Substring(0, utf16))

    /// `count` code points from code point `start` (both clamped)
    let sub (s: string) (start: int) (count: int) : string =
        let a = offset s start
        let b = offset s (start + count)
        s.Substring(a, b - a)

    /// the first `count` code points
    let head (s: string) (count: int) : string = s.Substring(0, offset s count)

    let padLeft (w: int) (s: string) : string =
        let n = length s
        if n >= w then s else System.String(' ', w - n) + s

    let padRight (w: int) (s: string) : string =
        let n = length s
        if n >= w then s else s + System.String(' ', w - n)

/// Terminal cell widths [D:str-width]: how many columns a string takes
/// when printed. East Asian Wide/Fullwidth and emoji presentation take
/// two, combining marks, format characters and controls none, the rest
/// one; an escape sequence (CSI such as SGR colour, OSC) takes none.
/// Emoji sequences count as one glyph: a ZWJ joins the next character
/// into the one before, VS16 turns a narrow character into emoji
/// presentation, a skin-tone modifier or a second regional indicator
/// (a flag's) adds nothing. Terminals disagree at the edges (ambiguous
/// width counts one).
module CellWidth =
    // East_Asian_Width W and F, Unicode 16.0, as inclusive (lo, hi) ranges
    // (generated from the UCD; planes 2 and 3 are wide whole)
    let private wide: (int * int)[] =
        [|
        0x1100, 0x115F; 0x231A, 0x231B; 0x2329, 0x232A; 0x23E9, 0x23EC; 0x23F0, 0x23F0;
        0x23F3, 0x23F3; 0x25FD, 0x25FE; 0x2614, 0x2615; 0x2630, 0x2637; 0x2648, 0x2653;
        0x267F, 0x267F; 0x268A, 0x268F; 0x2693, 0x2693; 0x26A1, 0x26A1; 0x26AA, 0x26AB;
        0x26BD, 0x26BE; 0x26C4, 0x26C5; 0x26CE, 0x26CE; 0x26D4, 0x26D4; 0x26EA, 0x26EA;
        0x26F2, 0x26F3; 0x26F5, 0x26F5; 0x26FA, 0x26FA; 0x26FD, 0x26FD; 0x2705, 0x2705;
        0x270A, 0x270B; 0x2728, 0x2728; 0x274C, 0x274C; 0x274E, 0x274E; 0x2753, 0x2755;
        0x2757, 0x2757; 0x2795, 0x2797; 0x27B0, 0x27B0; 0x27BF, 0x27BF; 0x2B1B, 0x2B1C;
        0x2B50, 0x2B50; 0x2B55, 0x2B55; 0x2E80, 0x2E99; 0x2E9B, 0x2EF3; 0x2F00, 0x2FD5;
        0x2FF0, 0x303E; 0x3041, 0x3096; 0x3099, 0x30FF; 0x3105, 0x312F; 0x3131, 0x318E;
        0x3190, 0x31E5; 0x31EF, 0x321E; 0x3220, 0x3247; 0x3250, 0xA48C; 0xA490, 0xA4C6;
        0xA960, 0xA97C; 0xAC00, 0xD7A3; 0xF900, 0xFAFF; 0xFE10, 0xFE19; 0xFE30, 0xFE52;
        0xFE54, 0xFE66; 0xFE68, 0xFE6B; 0xFF01, 0xFF60; 0xFFE0, 0xFFE6; 0x16FE0, 0x16FE4;
        0x16FF0, 0x16FF1; 0x17000, 0x187F7; 0x18800, 0x18CD5; 0x18CFF, 0x18D08; 0x1AFF0, 0x1AFF3;
        0x1AFF5, 0x1AFFB; 0x1AFFD, 0x1AFFE; 0x1B000, 0x1B122; 0x1B132, 0x1B132; 0x1B150, 0x1B152;
        0x1B155, 0x1B155; 0x1B164, 0x1B167; 0x1B170, 0x1B2FB; 0x1D300, 0x1D356; 0x1D360, 0x1D376;
        0x1F004, 0x1F004; 0x1F0CF, 0x1F0CF; 0x1F18E, 0x1F18E; 0x1F191, 0x1F19A; 0x1F200, 0x1F202;
        0x1F210, 0x1F23B; 0x1F240, 0x1F248; 0x1F250, 0x1F251; 0x1F260, 0x1F265; 0x1F300, 0x1F320;
        0x1F32D, 0x1F335; 0x1F337, 0x1F37C; 0x1F37E, 0x1F393; 0x1F3A0, 0x1F3CA; 0x1F3CF, 0x1F3D3;
        0x1F3E0, 0x1F3F0; 0x1F3F4, 0x1F3F4; 0x1F3F8, 0x1F43E; 0x1F440, 0x1F440; 0x1F442, 0x1F4FC;
        0x1F4FF, 0x1F53D; 0x1F54B, 0x1F54E; 0x1F550, 0x1F567; 0x1F57A, 0x1F57A; 0x1F595, 0x1F596;
        0x1F5A4, 0x1F5A4; 0x1F5FB, 0x1F64F; 0x1F680, 0x1F6C5; 0x1F6CC, 0x1F6CC; 0x1F6D0, 0x1F6D2;
        0x1F6D5, 0x1F6D7; 0x1F6DC, 0x1F6DF; 0x1F6EB, 0x1F6EC; 0x1F6F4, 0x1F6FC; 0x1F7E0, 0x1F7EB;
        0x1F7F0, 0x1F7F0; 0x1F90C, 0x1F93A; 0x1F93C, 0x1F945; 0x1F947, 0x1F9FF; 0x1FA70, 0x1FA7C;
        0x1FA80, 0x1FA89; 0x1FA8F, 0x1FAC6; 0x1FACE, 0x1FADC; 0x1FADF, 0x1FAE9; 0x1FAF0, 0x1FAF8;
        0x20000, 0x2FFFD; 0x30000, 0x3FFFD
        |]

    let private isWide (cp: int) =
        // binary search over the ranges
        let mutable lo = 0
        let mutable hi = wide.Length - 1
        let mutable found = false

        while not found && lo <= hi do
            let mid = (lo + hi) / 2
            let a, b = wide[mid]

            if cp < a then hi <- mid - 1
            elif cp > b then lo <- mid + 1
            else found <- true

        found

    let private isRegional (cp: int) = cp >= 0x1F1E6 && cp <= 0x1F1FF

    /// one code point's cells, out of context
    let ofRune (r: System.Text.Rune) : int =
        let cp = r.Value

        if cp = 0xAD then
            1 // the soft hyphen prints, as wcwidth counts it
        elif (cp >= 0x1160 && cp <= 0x11FF) || (cp >= 0xD7B0 && cp <= 0xD7FF) then
            0 // Hangul medial vowels and final consonants join the syllable
        else
            match System.Text.Rune.GetUnicodeCategory r with
            | System.Globalization.UnicodeCategory.NonSpacingMark
            | System.Globalization.UnicodeCategory.EnclosingMark
            | System.Globalization.UnicodeCategory.Format
            | System.Globalization.UnicodeCategory.Control -> 0
            | _ -> if isWide cp || isRegional cp then 2 else 1

    // the length of the escape sequence at i (s[i] = ESC): CSI to its
    // final byte, OSC/DCS/APC/PM/SOS to BEL or ST, else ESC and one char
    let private escapeLen (s: string) (i: int) : int =
        if i + 1 >= s.Length then
            1
        else
            match s[i + 1] with
            | '[' ->
                let mutable j = i + 2

                while j < s.Length && (s[j] < '@' || s[j] > '~') do
                    j <- j + 1

                min (j + 1) s.Length - i
            | ']'
            | 'P'
            | '_'
            | '^'
            | 'X' ->
                let mutable j = i + 2
                let mutable stop = false

                while not stop && j < s.Length do
                    if s[j] = '\a' then
                        stop <- true
                        j <- j + 1
                    elif s[j] = '\x1b' && j + 1 < s.Length && s[j + 1] = '\\' then
                        stop <- true
                        j <- j + 2
                    else
                        j <- j + 1

                j - i
            | _ -> 2

    /// the columns `s` takes in a terminal
    let ofString (s: string) : int =
        let mutable w = 0
        let mutable i = 0
        // the previous glyph's own cells, for VS16; -1 after a ZWJ
        let mutable prev = 0
        let mutable prevRegional = false

        while i < s.Length do
            if s[i] = '\x1b' then
                i <- i + escapeLen s i
                prev <- 0
                prevRegional <- false
            else
                let r =
                    match System.Text.Rune.TryGetRuneAt(s, i) with
                    | true, r -> r
                    | _ -> System.Text.Rune.ReplacementChar

                let step =
                    if System.Char.IsSurrogatePair(s, i) then 2 else 1

                let cp = r.Value

                if prev = -1 then
                    // joined into the glyph before the ZWJ
                    prev <- 2
                elif cp = 0x200D then
                    prev <- -1
                elif cp = 0xFE0F then
                    // emoji presentation of a narrow glyph
                    if prev = 1 then
                        w <- w + 1
                        prev <- 2
                elif cp >= 0x1F3FB && cp <= 0x1F3FF && prev = 2 then
                    () // a skin tone merges into its emoji
                elif isRegional cp && prevRegional then
                    prevRegional <- false // the flag's second half
                else
                    let c = ofRune r
                    w <- w + c

                    if c > 0 then
                        prev <- c

                    prevRegional <- isRegional cp

                i <- i + step

        w

// hostile bytes from data must not reach a terminal [D:binary-echo]:
// the length of an SGR sequence (`ESC [ [0-9;:]* m`) starting at i, or
// 0 [D:tty-color]. SGR sets display attributes only — colour, bold,
// underline, reset — so it cannot move the cursor, clear the screen,
// set the title, or write the clipboard; keeping it is safe where the
// other escape families are not.
let private sgrLenAt (s: string) (i: int) : int =
    if i + 1 < s.Length && s.[i + 1] = '[' then
        let mutable j = i + 2

        while j < s.Length && (System.Char.IsDigit s.[j] || s.[j] = ';' || s.[j] = ':') do
            j <- j + 1

        if j < s.Length && s.[j] = 'm' then j - i + 1 else 0
    else
        0

// [D:binary-echo] already ruled that a NUL-bearing echo refuses a tty;
// this is the same rule one class wider, covering the data a renderer
// prints. A filename or field carrying OSC/DCS escapes, a bare ESC, a
// cursor/screen CSI, or C0/C1 controls can clear the screen, set the
// window title, or write the clipboard. So tty-bound data renderers
// neutralize those bytes: the ESC introducer, C0 controls except \t,
// \n and a bare CR, DEL, and the C1 range render as a visible \xNN
// escape so the text stays honest. CR passes [D:tty-cr-pass]: it only
// returns the cursor, and progress lines rewrite themselves with it.
// SGR sequences are the other exception [D:tty-color]: display-only,
// so intentional colour (the Color module, a coloured prompt) survives
// while every terminal-driving family stays neutralized. Applied only at a tty and only to data — redirected
// output is byte-faithful.
// Bidi controls render as a visible \uXXXX too [D:tty-bidi-sanitize]:
// the text the user reads must match the bytes (Trojan Source).
let private sanitizeWith (strict: bool) (s: string) : string =
    // bidi = the embeddings/overrides U+202A..202E, the isolates
    // U+2066..2069, LRM/RLM (U+200E/200F) and ALM (U+061C)
    let bidi (c: char) =
        let n = int c
        (n >= 0x202A && n <= 0x202E) || (n >= 0x2066 && n <= 0x2069) || n = 0x200E || n = 0x200F || n = 0x061C

    // hostile = ESC (0x1B) the introducer, DEL (0x7F), any C0 control
    // (< 0x20) except TAB, LF and CR [D:tty-cr-pass], and the C1 range
    // (0x80..0x9F)
    let hostile (c: char) =
        let n = int c

        n = 0x1B
        || n = 0x7F
        || (n < 0x20 && c <> '\t' && c <> '\n' && (strict || c <> '\r'))
        || (n >= 0x80 && n <= 0x9F)

    if not (s |> Seq.exists (fun c -> hostile c || bidi c)) then
        s
    else
        let sb = System.Text.StringBuilder(s.Length)
        let hex = "0123456789abcdef"
        let hexU = "0123456789ABCDEF"
        let mutable i = 0

        while i < s.Length do
            let c = s.[i]

            if int c = 0x1B then
                match (if strict then 0 else sgrLenAt s i) with
                | 0 ->
                    // a non-SGR escape (OSC/DCS/cursor CSI/lone ESC): the
                    // introducer renders visible; the rest is now inert text
                    sb.Append "\\x1b" |> ignore
                    i <- i + 1
                | n ->
                    sb.Append(s.Substring(i, n)) |> ignore // SGR kept verbatim
                    i <- i + n
            elif hostile c then
                let n = int c
                sb.Append("\\x").Append(hex[(n >>> 4) &&& 0xF]).Append(hex[n &&& 0xF]) |> ignore
                i <- i + 1
            elif bidi c then
                let n = int c

                sb
                    .Append("\\u")
                    .Append(hexU[(n >>> 12) &&& 0xF])
                    .Append(hexU[(n >>> 8) &&& 0xF])
                    .Append(hexU[(n >>> 4) &&& 0xF])
                    .Append(hexU[n &&& 0xF])
                |> ignore

                i <- i + 1
            else
                sb.Append c |> ignore
                i <- i + 1

        sb.ToString()

let sanitizeTtyData (s: string) : string = sanitizeWith false s

/// weir's DATA renderers (the REPL and -e echo, table cells) show a
/// bare CR and SGR visibly too [D:tty-echo-strict]: what they render is
/// a value, never the user's intended terminal output
let sanitizeTtyEcho (s: string) : string = sanitizeWith true s

/// a data value quoted in a runtime message [D:error-data-escape]: CR,
/// LF, ESC and every control render visibly, as the echo renders them —
/// the value can neither rewrite, forge nor restyle the message line
let quoteData (s: string) : string =
    (sanitizeTtyEcho s).Replace("\n", "\\x0a")

// Regex counts code points [D:regex-code-points]: .NET's engine walks
// UTF-16 units, so `.`, a negated class or `\S` would take half an emoji
// and an empty match could land inside one. The pattern is rewritten so
// every atom that can match a surrogate takes a whole pair instead, an
// astral literal is one unit for a quantifier, and no match starts
// between the halves of a pair. Groups keep their numbers (only
// non-capturing groups are added); the user's own pattern is validated
// first, so errors still quote what they wrote.
let regexCodePoints (pat: string) : string =
    let pair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]"
    let notSurrogate = @"(?![\uD800-\uDFFF])"

    let takesSurrogate (atom: string) =
        try
            System.Text.RegularExpressions.Regex(atom).IsMatch "\uD83D"
        with _ ->
            false

    let isPairAt (s: string) (i: int) =
        i + 1 < s.Length
        && System.Char.IsHighSurrogate s[i]
        && System.Char.IsLowSurrogate s[i + 1]

    // the index just past the class opened at s[i] = '[', if it closes
    let rec classEnd (s: string) (i: int) : int option =
        let mutable j = i + 1

        if j < s.Length && s[j] = '^' then
            j <- j + 1

        if j < s.Length && s[j] = ']' then
            j <- j + 1

        let mutable result = None
        let mutable fin = false

        while not fin && j < s.Length do
            match s[j] with
            | '\\' when j + 2 < s.Length && (s[j + 1] = 'p' || s[j + 1] = 'P') && s[j + 2] = '{' ->
                match s.IndexOf('}', j + 3) with
                | -1 -> fin <- true
                | k -> j <- k + 1
            | '\\' -> j <- j + 2
            | '[' when s[j - 1] = '-' ->
                // a subtraction's nested class
                match classEnd s j with
                | Some k -> j <- k
                | None -> fin <- true
            | ']' ->
                result <- Some(j + 1)
                fin <- true
            | _ -> j <- j + 1

        result

    let wrapAtom (atom: string) =
        if takesSurrogate atom then
            $"(?:{pair}|{notSurrogate}{atom})"
        else
            atom

    let wrapClass (cls: string) =
        let neg = cls.Length > 2 && cls[1] = '^'
        let body = cls.Substring((if neg then 2 else 1), cls.Length - (if neg then 3 else 2))
        let astral = ResizeArray<string>()
        let rest = System.Text.StringBuilder()
        let mutable k = 0

        while k < body.Length do
            if body[k] = '\\' && k + 1 < body.Length then
                rest.Append(body, k, 2) |> ignore
                k <- k + 2
            elif isPairAt body k then
                astral.Add(body.Substring(k, 2))
                k <- k + 2
            else
                rest.Append body[k] |> ignore
                k <- k + 1

        let rest = rest.ToString()

        if astral.Count = 0 && not (takesSurrogate cls) then
            cls
        else
            let lits = System.String.Join("|", astral)
            let bmp = if neg then $"[^{rest}]" else $"[{rest}]"
            let bmpTakes = rest <> "" && takesSurrogate bmp

            let alts =
                [ if neg then
                      yield (if astral.Count = 0 then pair else $"(?!{lits}){pair}")
                  else
                      if astral.Count > 0 then
                          yield lits

                      if bmpTakes then
                          yield pair
                  if rest = "" then
                      if neg then
                          yield $"{notSurrogate}[\\s\\S]"
                  elif bmpTakes || neg then
                      yield $"{notSurrogate}{bmp}"
                  else
                      yield bmp ]

            "(?:" + System.String.Join("|", alts) + ")"

    let sb = System.Text.StringBuilder()
    let mutable i = 0

    while i < pat.Length do
        let c = pat[i]

        if c = '\\' && i + 1 < pat.Length then
            let e = pat[i + 1]
            let close = if i + 2 < pat.Length then pat.IndexOf('}', i + 2) else -1

            if (e = 'p' || e = 'P') && i + 2 < pat.Length && pat[i + 2] = '{' && close > 0 then
                sb.Append(wrapAtom (pat.Substring(i, close + 1 - i))) |> ignore
                i <- close + 1
            elif e = 'S' || e = 'W' || e = 'D' then
                sb.Append(wrapAtom (pat.Substring(i, 2))) |> ignore
                i <- i + 2
            else
                sb.Append(pat, i, 2) |> ignore
                i <- i + 2
        elif c = '(' && i + 2 < pat.Length && pat[i + 1] = '?' && pat[i + 2] = '#' then
            let k = pat.IndexOf(')', i)
            let k = if k < 0 then pat.Length - 1 else k
            sb.Append(pat, i, k + 1 - i) |> ignore
            i <- k + 1
        elif c = '[' then
            match classEnd pat i with
            | Some k ->
                sb.Append(wrapClass (pat.Substring(i, k - i))) |> ignore
                i <- k
            | None ->
                sb.Append(pat, i, pat.Length - i) |> ignore
                i <- pat.Length
        elif c = '.' then
            sb.Append(wrapAtom ".") |> ignore
            i <- i + 1
        elif isPairAt pat i then
            sb.Append("(?:").Append(pat, i, 2).Append(")") |> ignore
            i <- i + 2
        else
            sb.Append c |> ignore
            i <- i + 1

    let body = sb.ToString()

    // no match starts between the halves of a pair; a free-spacing
    // pattern ((?x)) could comment out the closing paren, so it goes
    // without that guard
    if System.Text.RegularExpressions.Regex.IsMatch(pat, @"\(\?[imnsx-]*x") then
        body
    else
        @"(?!(?<=[\uD800-\uDBFF])[\uDC00-\uDFFF])(?:" + body + ")"

/// .NET's string ceiling in chars: a result past it is refused in
/// weir's words [D:int-narrowing], never the runtime's exception text
let maxStrLen = 0x3FFFFFDFL

let strTooLarge (who: string) (chars: int64) : string =
    $"{who}: a {chars}-char result is too large (at most {maxStrLen})"

/// a builder whose result length is not known up front: the ceiling (or
/// a failed allocation) surfaces as the same refusal, sized when known
let guardStrBuild (who: string) (f: unit -> 'a) : 'a =
    try
        f ()
    with
    | :? System.OutOfMemoryException
    | :? System.ArgumentOutOfRangeException ->
        failwith $"{who}: the result is too large for a string (at most {maxStrLen} chars)"

let excerpt (s: string) : string =
    let n = CodePoints.length s

    if n <= excerptHead then
        quoteData s
    else
        $"{quoteData (CodePoints.head s excerptHead)}… ({n} chars)"

type Ty =
    | TInt
    // finite-only floats [D:floats]: NaN/Infinity are unrepresentable —
    // checked arithmetic raises, every boundary parse rejects them
    | TFloat
    | TStr
    | TBool
    | TUnit
    // bytes as a type [D:size]: an integer count of bytes (int64 —
    // ~8 EiB ceiling); decimals exist only in parse and render,
    // following the Duration pattern
    | TSize
    // the non-text value [D:bytes]: an in-memory byte array, opt-in at
    // both ends (readBytes/toUtf8 in, writeBytes/fromUtf8 out); the
    // capture law's scoping applies (bounded, in-memory — unbounded
    // data streams to a sink instead)
    | TBytes
    // time as a type [D:duration]: an integer count of milliseconds;
    // decimals exist only in parsing and rendering — how the no-floats
    // law accommodates time
    | TDur
    // a point on the UTC timeline [D:instant]: integer ms since the
    // Unix epoch — instants only (no local zones, no calendar
    // arithmetic; t + 1d is exactly 24h, never next-day-same-wall-time)
    | TInstant
    // a 128-bit identifier [D:uuid]: its own type, not a string alias —
    // ordered by its big-endian bytes, which is also the order of its
    // canonical text
    | TUuid
    // a marker the renderers respect [D:secret]: a plain string the
    // rendering machinery refuses to print — show is ***, interpolation
    // and the wire boundaries refuse; Secret.reveal is the one exit.
    // Not storage and not memory protection (the in-memory value is a
    // plain string) — flow control at the boundaries weir owns
    | TSecret
    | TFun of domain: Ty * codomain: Ty
    | TSeq of element: Ty
    | TTuple of elements: Ty list // arity 2+ [D:tuples-reversal]
    | TNamed of name: string * args: Ty list
    | TVar of name: string
    | TRowVar of name: string * fields: (string * Ty) list

let rec formatTy (ty: Ty) : string =
    match ty with
    | TDur -> "Duration"
    | TInstant -> "Instant"
    | TUuid -> "Uuid"
    | TSize -> "Size"
    | TBytes -> "Bytes"
    | TSecret -> "Secret"
    | TVar v -> $"'{v}"
    | TRowVar(_, []) -> "{ .. }"
    | TRowVar(_, fields) ->
        let fs =
            fields |> List.map (fun (f, t) -> $"{f}: {formatTy t}") |> String.concat "; "

        $"{{ {fs}; .. }}"
    | TInt -> "int"
    | TFloat -> "float"
    | TStr -> "string"
    | TBool -> "bool"
    | TUnit -> "unit"
    | TFun(domain, codomain) ->
        let dom =
            match domain with
            | TFun _ -> $"({formatTy domain})"
            | _ -> formatTy domain

        $"{dom} -> {formatTy codomain}"
    | TSeq element -> $"seq<{formatTy element}>"
    | TTuple elements ->
        let part (t: Ty) =
            match t with
            | TFun _
            | TTuple _ -> $"({formatTy t})"
            | _ -> formatTy t

        elements |> List.map part |> String.concat " * "
    | TNamed(name, []) -> name
    | TNamed(name, args) ->
        let argStr = args |> List.map formatTy |> String.concat ", "
        $"{name}<{argStr}>"

/// An anonymous record's canonical name [D:anon-records]: fields
/// sorted by name, rendered as the type's own spelling. The name is
/// also the display form, so formatTy and type errors render it with
/// no extra arm; the leading '{' keeps it un-typeable (no collision
/// with declared names, excluded from suggestion pools by isUserName);
/// two same-shape anonymous types get the same name and so unify. A
/// declared record with the same shape stays a different type — the
/// nominal law is untouched.
let anonRecordName (fields: (string * Ty) list) : string =
    let body =
        fields
        |> List.sortBy fst
        |> List.map (fun (n, t) -> $"{n}: {formatTy t}")
        |> String.concat "; "

    "{| " + body + " |}"

// Nested anonymous shapes [D:anon-nesting]: tySyn admits the form
// anywhere a type is written (reversing the earlier one-level rule for
// REPL shape exploration), and each nested shape it parses lands here.
// The registration seams (withAnonDefs, checkDecl) drain it into the
// env, so every existing TNamed lookup resolves the inner defs with no
// new resolution points. Backtracked parses may leave entries behind;
// registering an unreachable hidden name is benign, so the drain needs
// no parse-success bookkeeping. ThreadLocal because parsing is
// per-thread; workers never share an accumulator.
let pendingAnonDefs: System.Threading.ThreadLocal<ResizeArray<string * (string * Ty) list>> =
    new System.Threading.ThreadLocal<_>(fun () -> ResizeArray())

let pushAnonDef (name: string) (fields: (string * Ty) list) : unit = pendingAnonDefs.Value.Add(name, fields)

let drainAnonDefs () : (string * (string * Ty) list) list =
    let xs = List.ofSeq pendingAnonDefs.Value
    pendingAnonDefs.Value.Clear()
    xs

/// Per-part styling for the annotated signature [D:help-tint]: the tty
/// help render tints name/types/punctuation structurally at composition;
/// plain (id everywhere) is the hover and piped spelling. One
/// composition — never a second signature formatter.
type SigStyle =
    { Name: string -> string
      Ty: string -> string
      Punct: string -> string }

let plainSigStyle: SigStyle = { Name = id; Ty = id; Punct = id }

/// The annotated declaration form for hover [D:annotated-signature]:
/// `name (p1: t1) (p2: t2) : result`, decomposing `ty` by the given
/// parameter names (the arrow tail beyond the named params is the
/// result). Valid F# declaration syntax — claims nothing false. Zero
/// names -> `name : ty`, no empty parens. The plain arrow `formatTy`
/// stays the fallback (unnamed values) and the truth for type errors.
let private signatureParts
    (st: SigStyle)
    (name: string)
    (paramNames: string list)
    (ty: Ty)
    : string * (string * int * int) list =
    // presentation guards [D:sig-render]: the internal sentinel
    // quantifier ('__print) renders as plain 'a — the accepted set is
    // described in the member's prose, and a double-underscore name is
    // not a type a user can write; a unit param renders as bare `()` —
    // the declaration form (`let cleanup () =`), never an annotated
    // binder
    let rec sanitize t =
        match t with
        | TVar v when v.StartsWith "__" -> TVar "a"
        | TFun(a, b) -> TFun(sanitize a, sanitize b)
        | TSeq t -> TSeq(sanitize t)
        | TTuple ts -> TTuple(List.map sanitize ts)
        | TNamed(n, args) -> TNamed(n, List.map sanitize args)
        | TRowVar(r, fields) -> TRowVar(r, fields |> List.map (fun (fn, ft) -> fn, sanitize ft))
        | t -> t

    let ty = sanitize ty

    let rec split names t =
        match names, t with
        | n :: rest, TFun(dom, cod) ->
            let ps, result = split rest cod
            (n, dom) :: ps, result
        | _ -> [], t

    match split paramNames ty with
    | [], _ -> st.Name name + st.Punct " : " + st.Ty(formatTy ty), []
    | ps, result ->
        let sb = System.Text.StringBuilder(st.Name name)

        let parts =
            ps
            |> List.map (fun (n, t) ->
                sb.Append " " |> ignore
                let start = sb.Length

                (match t with
                 | TUnit -> sb.Append(st.Punct "()")
                 | _ -> sb.Append(st.Punct "(" + n + st.Punct ": " + st.Ty(formatTy t) + st.Punct ")"))
                |> ignore

                n, start, sb.Length)

        sb.Append(st.Punct " : " + st.Ty(formatTy result)).ToString(), parts

let formatSignatureWith (st: SigStyle) (name: string) (paramNames: string list) (ty: Ty) : string =
    fst (signatureParts st name paramNames ty)

let formatSignature (name: string) (paramNames: string list) (ty: Ty) : string =
    formatSignatureWith plainSigStyle name paramNames ty

/// the plain signature with each named `(p: t)` group's [start, end)
/// offsets in it [D:signature-help] — the one composition, so the
/// offsets cannot drift from the text
let formatSignatureParts (name: string) (paramNames: string list) (ty: Ty) : string * (string * int * int) list =
    signatureParts plainSigStyle name paramNames ty

/// Echo var normalization [D:repl-fn-echo]: the checker's fresh names
/// ('a1, 'a2, 'b3…) rename to 'a, 'b, … in first-appearance order.
/// Display only, for the echo's `: ty` metadata, shared by exactly two
/// consumers: the REPL echo meta lines and the `-e` last-statement echo
/// (formatEchoTy below is their one spelling). Type errors, hover, and
/// `#help` signatures keep their own formatting untouched — errors name
/// the checker's internal vars, #help renders canonical schemes already.
/// Row-var names never render (formatTy shows `{ fields; .. }`), so only
/// TVar names rename; nested row fields recurse.
let normalizeEchoVars (ty: Ty) : Ty =
    let mapping = System.Collections.Generic.Dictionary<string, string>()

    let renameVar (v: string) : string =
        match mapping.TryGetValue v with
        | true, r -> r
        | _ ->
            let i = mapping.Count

            let r =
                if i < 26 then
                    string (char (int 'a' + i))
                else
                    // beyond 'z (never seen in practice): 'a1, 'b1, …
                    $"{char (int 'a' + i % 26)}{i / 26}"

            mapping[v] <- r
            r

    let rec go (t: Ty) : Ty =
        match t with
        | TVar v -> TVar(renameVar v)
        | TFun(a, b) ->
            // domain first — left-to-right first appearance
            let a' = go a
            TFun(a', go b)
        | TSeq e -> TSeq(go e)
        | TTuple ts -> TTuple(List.map go ts)
        | TNamed(n, args) -> TNamed(n, List.map go args)
        | TRowVar(r, fields) -> TRowVar(r, fields |> List.map (fun (f, ft) -> f, go ft))
        | t -> t

    go ty

/// The one echo type spelling [D:repl-fn-echo] — normalizeEchoVars
/// composed with formatTy; every echo meta line renders through it
let formatEchoTy (ty: Ty) : string = formatTy (normalizeEchoVars ty)

let rec tyVars (ty: Ty) : Set<string> =
    match ty with
    | TVar v -> Set.singleton v
    | TRowVar(r, fields) -> fields |> List.fold (fun acc (_, t) -> acc + tyVars t) (Set.singleton r)
    | TFun(a, b) -> tyVars a + tyVars b
    | TSeq t -> tyVars t
    | TTuple ts -> ts |> List.fold (fun acc t -> acc + tyVars t) Set.empty
    | TNamed(_, args) -> args |> List.fold (fun acc t -> acc + tyVars t) Set.empty
    | TInt
    | TFloat
    | TStr
    | TBool
    | TUnit
    | TDur
    | TInstant
    | TUuid
    | TSize
    | TBytes
    | TSecret -> Set.empty

// The closed class family [D:inferred-type-classes] — fully erased
// after checking: a constraint never reaches the value domain.
[<RequireQualifiedAccess>]
type Cls =
    | Eq
    | Show
    | Ord
    // "renders in an interpolation hole" [D:hole-generic]: Show minus a
    // Secret at the top (a nested one renders masked, as show does);
    // compiler-owned, never written
    | Render

// Cs: constraints on quantified vars — `Eq a => a -> a -> bool` is
// { Forall = {a}; Cs = [a, {Eq}]; Ty = a -> a -> bool }.
type Scheme =
    { Forall: Set<string>
      Cs: Map<string, Set<Cls>>
      Ty: Ty
      // row-field provenance [D:row-provenance]: quantified row var ->
      // (field, physLine, physCol, len) of the access that demanded it
      // — translated to physical positions at generalization (spans die
      // at statement boundaries), rehydrated at instantiation, reported
      // by the row-vs-record discharge
      RowOrigins: Map<string, (string * int * int * int) list>
      // hole-default provenance (PLAN-dx-review D6): the physical
      // (line, col) of each bare interpolation hole whose var took the
      // string default at this statement's boundary — a later call-site
      // mismatch names the defaulting decision instead of blaming the
      // call
      HoleDefaults: (int * int) list }

let generalize (ty: Ty) : Scheme =
    { Forall = tyVars ty
      Cs = Map.empty
      Ty = ty
      RowOrigins = Map.empty
      HoleDefaults = [] }

// generalization with the checker's constraint residue: only
// constraints on vars actually quantified ride into the scheme
let generalizeWith (cs: Map<string, Set<Cls>>) (ty: Ty) : Scheme =
    let fa = tyVars ty

    { Forall = fa
      Cs = cs |> Map.filter (fun v _ -> fa.Contains v)
      Ty = ty
      RowOrigins = Map.empty
      HoleDefaults = [] }

// generalizeWith + row origins [D:row-provenance], filtered like Cs:
// only origins for quantified row vars ride into the scheme; hole
// defaults ride whole (they anchor prose, not vars)
let generalizeWithOrigins
    (cs: Map<string, Set<Cls>>)
    (origins: Map<string, (string * int * int * int) list>)
    (holeDefaults: (int * int) list)
    (ty: Ty)
    : Scheme =
    let fa = tyVars ty

    { Forall = fa
      Cs = cs |> Map.filter (fun v _ -> fa.Contains v)
      Ty = ty
      RowOrigins = origins |> Map.filter (fun v _ -> fa.Contains v)
      HoleDefaults = holeDefaults }

let mono (ty: Ty) : Scheme =
    { Forall = Set.empty
      Cs = Map.empty
      Ty = ty
      RowOrigins = Map.empty
      HoleDefaults = [] }

// attribute arguments [D:attributes]: literal-only, the splice family
type AttrArg =
    | AStr of string
    | AInt of int64
    | ABool of bool
    // a duration literal (30s, 250ms) — stored as ms [D:duration]
    | ADur of int64
    | AFloat of float
    // a size literal (10MiB) — stored as bytes [D:size]
    | ASize of int64

type RecordDef =
    { Name: string
      Params: string list
      Fields: (string * Ty) list
      // check-time data, fully erased [D:attributes] — never reaches
      // eval, Value, show, json, or equatability
      Attrs: Map<string, (string * AttrArg option) list>
      // the `[<Ord n>]` fields by ascending n [D:derived-ord]: the
      // record's ordering key, empty when nothing is numbered; the
      // unnumbered fields take no part. Check-time data like Attrs
      OrdKey: string list
      // field -> the `///` doc's first line, the derived --help text
      // [D:doc-help]. `--help` reads this instead of the retired
      // [<Doc>] attribute; hover still reads the full doc out-of-band.
      // Same check-time-erased nature as Attrs. The key "" holds the
      // type's own doc, its first paragraph [D:help-case-docs].
      Docs: Map<string, string> }

/// The wire-key attribute's single reader [D:wire-keys]: a field
/// carrying [<Wire "…">] uses that string as its adapter key
/// (json/jsonl/yaml — and csv when it lands); everywhere else the
/// attribute is inert (Args/Env derive from the field name, documented
/// verbatim)
let wireName (def: RecordDef) (field: string) : string =
    match Map.tryFind field def.Attrs with
    | Some specs ->
        specs
        |> List.tryPick (fun (n, a) ->
            match n, a with
            | "Wire", Some(AStr s) -> Some s
            | _ -> None)
        |> Option.defaultValue field
    | None -> field

/// The declared value encodings [D:wire-codecs]: a closed set, one field
/// attribute each, read by the json/yaml boundaries only — never by show
/// or ==. Which type a codec fits is the wire table's answer (Check).
type Codec =
    | Iso8601
    | EpochMs
    | EpochSec
    | Millis
    | Seconds
    | ByteCount

let codecName (c: Codec) : string =
    match c with
    | Iso8601 -> "Iso8601"
    | EpochMs -> "EpochMs"
    | EpochSec -> "EpochSec"
    | Millis -> "Millis"
    | Seconds -> "Seconds"
    | ByteCount -> "ByteCount"

/// every codec, for the attribute registry
let allCodecs = [ Iso8601; EpochMs; EpochSec; Millis; Seconds; ByteCount ]

let codecOfName (name: string) : Codec option =
    allCodecs |> List.tryFind (fun c -> codecName c = name)

/// The codec attribute's single reader [D:wire-codecs]: the field's
/// declared encoding, if any (fit was validated at the declaration)
let fieldCodec (def: RecordDef) (field: string) : Codec option =
    match Map.tryFind field def.Attrs with
    | Some specs -> specs |> List.tryPick (fun (n, _) -> codecOfName n)
    | None -> None

/// The XML attribute a field reads [D:from-xml]: Some name when the field
/// carries [<Attr>] (name defaults to the field) — else None (an element)
let xmlAttr (def: RecordDef) (field: string) : string option =
    match Map.tryFind field def.Attrs with
    | Some specs ->
        specs
        |> List.tryPick (fun (n, a) ->
            match n, a with
            | "Attr", Some(AStr s) -> Some s
            | "Attr", None -> Some field
            | _ -> None)
    | None -> None

/// The element name a seq< > field reads [D:from-xml]: the [<Elem "X">]
/// override, else the supplied fallback (the element type / field name)
let xmlElem (def: RecordDef) (field: string) (fallback: string) : string =
    match Map.tryFind field def.Attrs with
    | Some specs ->
        specs
        |> List.tryPick (fun (n, a) ->
            match n, a with
            | "Elem", Some(AStr s) -> Some s
            | _ -> None)
        |> Option.defaultValue fallback
    | None -> fallback

type UnionDef =
    { Name: string
      Params: string list
      Cases: (string * Ty option) list
      // the wire discriminator [D:wire-unions]: Some field-name when
      // the union is tagged — the key that admits it at the json/yaml
      // boundaries. CaseWires holds only the [<Wire>] overrides (a
      // case's tag value defaults to its name — caseWire resolves);
      // OtherCase names the [<Other>] fallback. All three inert when
      // Tag is None.
      Tag: string option
      CaseWires: Map<string, string>
      OtherCase: string option
      // a validated type [D:validated-types]: Some id when the union
      // declares `validate` — the id keys the runtime's derived
      // `create`, which every wire boundary runs (one per checked
      // declaration, so two imports' same-named types never share one)
      Validated: string option
      // the `[<Ord n>]` cases by ascending n [D:derived-ord]: the union
      // orders by this position, then payload; empty when none is
      // numbered (every case is numbered or none)
      OrdKey: string list
      // case -> the `///` doc's first line, a subcommand's --help text,
      // and "" -> the type's own doc [D:help-case-docs]; check-time-erased
      // like RecordDef.Docs
      Docs: Map<string, string> }

/// the variable a validated type's derived `create` evaluates through
/// [D:validated-types]: the prefix plus the declaration's id — `|` makes
/// it unwritable, so no source name can collide with it
let createVarPrefix = "|create:"

/// a writer's union-table entry [D:wire-unions]: a tagged case writes
/// its tag field and value first (isOther marks the [<Other>]
/// fallback); a validated case drops the case and writes the payload
/// [D:validated-types] — its own case, so no tag spelling collides
type WireCase =
    | WireTagged of tagField: string * tagValue: string * isOther: bool
    | WireValidated

/// a flat boundary field's validated marks [D:validated-types]: the
/// `create` ids (outermost first, space-separated) and the type's name
/// — `|` keeps them out of the attribute registry's reach
let validatedAttr = "|valid"

let validatedNameAttr = "|validName"

/// a flat boundary field's `create` ids, outermost first
let fieldValidators (def: RecordDef) (field: string) : string list =
    match Map.tryFind field def.Attrs with
    | Some specs ->
        specs
        |> List.tryPick (fun (n, a) ->
            match n, a with
            | n, Some(AStr ids) when n = validatedAttr -> Some(ids.Split(' ') |> List.ofArray)
            | _ -> None)
        |> Option.defaultValue []
    | None -> []

/// a flat boundary field's validated type name, for `--help`
let fieldValidatedName (def: RecordDef) (field: string) : string option =
    match Map.tryFind field def.Attrs with
    | Some specs ->
        specs
        |> List.tryPick (fun (n, a) ->
            match n, a with
            | n, Some(AStr t) when n = validatedNameAttr -> Some t
            | _ -> None)
    | None -> None

/// A tagged case's tag value — the [<Wire>] override or the case name
let caseWire (def: UnionDef) (case: string) : string =
    Map.tryFind case def.CaseWires |> Option.defaultValue case

type TypeDef =
    | Record of RecordDef
    | Union of UnionDef

// The non-closed schemes of one value map [D:env-open-index] — valid only
// while `For` is the env's own Values instance
type OpenIndex =
    { For: Map<string, Scheme>
      Open: Map<string, Scheme> }

[<NoEquality; NoComparison>]
type TypeEnv =
    { Values: Map<string, Scheme>
      // generalization walks only these [D:env-open-index]; an env whose
      // Values moved without bindValue has a stale index and is rescanned
      OpenIndex: OpenIndex option
      // purity of earlier top-level bindings [D:pure-stage1]: written by
      // the checked-statement pipeline's post-check layer (the classifier
      // lives after Builtins, so Check.fs itself never computes it),
      // carried here so every consumer that threads the env — scripts,
      // modules, the REPL — gets `pure` transitivity for free
      PureBindings: Map<string, bool>
      // earlier top-level FUNCTIONS whose body reaches a plan refusal
      // (a command, a Proc member, Plan.apply) -> that refusal's
      // message, so a call inside `plan` refuses through the helper as
      // `pure` does through PureBindings [D:plan-helper-refusal]
      PlanRefused: Map<string, string>
      Modules: Map<string, Map<string, Scheme>>
      Types: Map<string, TypeDef>
      // the record types the file passes to a typed boundary — type name
      // -> "Args.load"/"Env.load" — so an unbound field name can teach
      // loading it. Empty outside a file check/run.
      LoadTargets: Map<string, string>
      // imported modules [D:modules-v1]: alias -> each type that module
      // declares -> its key in `Types`. The key is the plain name while
      // no other import declares that name, else the qualified one
      // ("A.T") [D:qualified-type-names]; `A.T` resolves through here,
      // a bare name through `Types`. Empty for single-file.
      ModuleTypes: Map<string, Map<string, string>>
      // every imported type's identity [D:transitive-types]: its name in
      // this env -> "Module.Name@absPath" of the module that declared
      // it, so the same type reached twice is one type, and a type
      // reached only transitively can yield its plain name
      TypeOrigins: Map<string, string>
      // imported modules' private members [D:module-signatures]:
      // alias -> unsigned member name -> its inferred scheme. Never
      // resolvable — carried so the import-of-private error can teach
      // the migration (the signature to add, type included). Empty
      // for builtin modules and single-file runs.
      ModulePrivate: Map<string, Map<string, Scheme>>
      // anonymous literal defs minted mid-statement [D:anon-literals]:
      // a literal's canonical name exists only after inference — too
      // late for the withAnonDefs pre-walk — so the check arm writes
      // it here; `{ env with … }` copies share the instance, making
      // the def visible to the rest of the statement (to json's
      // admitted walk, field access, pattern rows). The arm also
      // pushes to pendingAnonDefs, so the next statement's drain
      // persists it into Types proper. Replaced fresh per typecheck
      // entry — one run is one thread.
      AnonLitDefs: System.Collections.Generic.Dictionary<string, TypeDef> }

let private schemeIsClosed (sch: Scheme) : bool = Set.isEmpty (tyVars sch.Ty - sch.Forall)

/// The env's non-closed schemes [D:env-open-index]: generalization needs
/// only the schemes with free vars, and scanning the whole env per let
/// made checking quadratic in the top-level binding count. Read from the
/// env's index when it is current, else by one scan.
let openSchemes (env: TypeEnv) : Map<string, Scheme> =
    match env.OpenIndex with
    | Some ix when obj.ReferenceEquals(ix.For, env.Values) -> ix.Open
    | _ -> env.Values |> Map.filter (fun _ sch -> not (schemeIsClosed sch))

/// The env with a fresh, current index [D:env-open-index]
let indexed (env: TypeEnv) : TypeEnv =
    match env.OpenIndex with
    | Some ix when obj.ReferenceEquals(ix.For, env.Values) -> env
    | _ ->
        { env with
            OpenIndex = Some { For = env.Values; Open = openSchemes env } }

/// Bind one value name, keeping the index current in O(log n)
/// [D:env-open-index]
let bindValue (name: string) (sch: Scheme) (env: TypeEnv) : TypeEnv =
    let values = Map.add name sch env.Values
    let opened = openSchemes env

    { env with
        Values = values
        OpenIndex =
            Some
                { For = values
                  Open = if schemeIsClosed sch then Map.remove name opened else Map.add name sch opened } }

/// The single type-name lookup [D:anon-literals]: declared/registered
/// types first, then the mid-statement anonymous-literal table —
/// the table only ever holds '{'-canonical names, so the fallback
/// is inert for every user-spellable name
let typeDefFor (env: TypeEnv) (name: string) : TypeDef option =
    match Map.tryFind name env.Types with
    | Some d -> Some d
    | None ->
        match env.AnonLitDefs.TryGetValue name with
        | true, d -> Some d
        | _ -> None

let editDistance (a: string) (b: string) : int =
    let d = Array2D.create (a.Length + 1) (b.Length + 1) 0

    for i in 0 .. a.Length do
        d[i, 0] <- i

    for j in 0 .. b.Length do
        d[0, j] <- j

    for i in 1 .. a.Length do
        for j in 1 .. b.Length do
            let cost = if a[i - 1] = b[j - 1] then 0 else 1
            d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)

    d[a.Length, b.Length]

let didYouMean (name: string) (candidates: seq<string>) : string =
    candidates
    |> Seq.map (fun c -> c, editDistance name c)
    |> Seq.filter (fun (_, d) -> d <= 2)
    |> Seq.sortBy snd
    |> Seq.tryHead
    |> Option.map (fun (c, _) -> $". Did you mean '{quoteData c}'?")
    |> Option.defaultValue ""

// the did-you-mean inverse [D:structured-fixes]: (typo, suggestion)
// read back from a message carrying the tail above — the typo is the
// last quoted name before it (or the bare word after a colon, the
// cmd-not-found spelling); the fix's edit replaces the typo
let didYouMeanTarget (msg: string) : (string * string) option =
    let m =
        System.Text.RegularExpressions.Regex.Match(
            msg,
            @"(?:'([^']+)'[^']*|: ([^\s']+))\. Did you mean '([^']+)'\?"
        )

    if m.Success then
        let typo =
            if m.Groups[1].Success then
                m.Groups[1].Value
            else
                m.Groups[2].Value

        Some(typo, m.Groups[3].Value)
    else
        None

module Color =
    open System

    let private enabled (redirected: bool) =
        not redirected
        && isNull (Environment.GetEnvironmentVariable "NO_COLOR")
        && Environment.GetEnvironmentVariable "TERM" <> "dumb"

    let onStdout = lazy enabled Console.IsOutputRedirected
    let onStderr = lazy enabled Console.IsErrorRedirected

    let private wrap (on: bool) (code: string) (s: string) =
        if on then $"\x1b[{code}m{s}\x1b[0m" else s

    let red on s = wrap on "31" s
    let yellow on s = wrap on "33" s
    let bold on s = wrap on "1" s
    // faint — the table's rule/ellipsis and the echo footers
    // [D:table-polish]: metadata recedes, cells stay untinted data
    let dim on s = wrap on "2" s
    // help text's `code` spans [D:help-tint]: the colorizer's number/
    // sigil tint reused, so "reads as code" is one colour everywhere
    let cyan on s = wrap on "36" s

// the tty signature style [D:help-tint]: the input colorizer's own
// palette — name bold (the known-head tint), types in the casing-law
// yellow (33), punctuation dim — so a #help signature reads like the
// line the user types; plainSigStyle stays the hover/piped byte surface
let sigTintStyle: SigStyle =
    { Name = Color.bold true
      Ty = Color.yellow true
      Punct = Color.dim true }

// the waiting indicator [D:waiting-indicator]: whoever owns the terminal
// and is working draws the progress — so it wraps only weir's own
// blocking operations (Http.send, Duration.sleep, retry/poll waits),
// never a spawned child (the child owns the terminal; drawing over its
// output is the corruption shells avoid). Stderr, tty-only, a 500ms
// grace so fast calls stay silent, erased before anything else prints —
// stdout is never touched, so the piped byte surface cannot move.
/// a console that is gone [D:console-hangup] [D:console-write]: a write
/// to a tty whose line hung up fails (EIO), one to a full or closed stderr
/// fails too, and an unhandled IOException on a cleanup or the spinner
/// thread aborted weir mid-unwind. Console writes ride this: the first
/// failure marks the writer gone and every later write is dropped — there
/// is no one left to read it; the SIGHUP that comes with a hangup ends
/// the run through its own teardown
type HangupWriter(inner: System.IO.TextWriter) =
    inherit System.IO.TextWriter()
    let mutable gone = false

    /// has a write failed (the terminal hung up)?
    member _.Gone = gone

    member private _.Guard(write: unit -> unit) =
        if not gone then
            try
                write ()
            with
            | :? System.IO.IOException
            // a closed or read-only fd 2: EBADF
            | :? System.UnauthorizedAccessException -> gone <- true

    override _.Encoding = inner.Encoding

    override _.NewLine
        with get () = inner.NewLine
        and set (v: string) = inner.NewLine <- v

    override this.Write(c: char) = this.Guard(fun () -> inner.Write c)
    override this.Write(s: string) = this.Guard(fun () -> inner.Write s)

    override this.Write(buffer: char[], index: int, count: int) =
        this.Guard(fun () -> inner.Write(buffer, index, count))

    override this.WriteLine() = this.Guard(fun () -> inner.WriteLine())
    override this.WriteLine(s: string) = this.Guard(fun () -> inner.WriteLine s)
    override this.Flush() = this.Guard(fun () -> inner.Flush())

module Waiting =
    open System

    let mutable private depth = 0

    /// the REPL turns the indicator on at its start; a script, `-e` and
    /// every other entry stay silent through every wait
    /// [D:waiting-repl-only]
    let mutable enabled = false

    /// the raw stderr under the tty sanitizer [D:tty-diag-sanitize] —
    /// the indicator's own line erase is terminal control, not data
    let mutable control: IO.TextWriter = null

    let private active () =
        enabled
        && not Console.IsErrorRedirected
        && Environment.GetEnvironmentVariable "TERM" <> "dumb"

    let private frames = [| "⠋"; "⠙"; "⠹"; "⠸"; "⠼"; "⠴"; "⠦"; "⠧"; "⠇"; "⠏" |]

    let during (label: string) (f: unit -> 'a) : 'a =
        if not (active ()) then
            f ()
        elif Threading.Interlocked.Increment(&depth) > 1 then
            // an indicator is already up — never stack a second line
            try
                f ()
            finally
                Threading.Interlocked.Decrement(&depth) |> ignore
        else
            use gone = new Threading.ManualResetEventSlim(false)
            let drew = ref false

            let spinner =
                Threading.Thread(
                    (fun () ->
                        let mutable i = 0
                        let mutable go = not (gone.Wait 500)

                        // a background thread's exception aborts the
                        // process: the indicator never takes weir down
                        // [D:console-hangup]
                        try
                            while go do
                                drew.Value <- true
                                Console.Error.Write $"\r{frames[i % frames.Length]} {label}"
                                i <- i + 1
                                go <- not (gone.Wait 80)
                        with _ ->
                            ()),
                    IsBackground = true
                )

            spinner.Start()

            try
                f ()
            finally
                gone.Set()
                spinner.Join()

                if drew.Value then
                    try
                        (if isNull control then Console.Error else control).Write "\r\u001b[2K"
                    with :? IO.IOException ->
                        ()

                Threading.Interlocked.Decrement(&depth) |> ignore

// ---- Duration text [D:duration] — the boundary where decimals live.
// Storage is integer ms; these two are the only places decimal text
// exists, and no float appears in either direction.

/// The Go shape: largest-unit-first compound, zero components dropped,
/// sub-second seconds as a decimal (1.5s), pure ms as Nms. Round-trips
/// through parseDurationMs (pinned in tests).
// floats render shortest-round-trippable [D:floats], and an integral
// float keeps a visible decimal so a float never renders identically
// to an int
let formatFloat (f: float) : string =
    let s = f.ToString(System.Globalization.CultureInfo.InvariantCulture)

    if s.Contains '.' || s.Contains 'E' || s.Contains 'e' then
        s.Replace("E", "e")
    else
        s + ".0"

let parseFloat (text: string) : Result<float, string> =
    match
        System.Double.TryParse(
            text,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture
        )
    with
    | true, f when System.Double.IsFinite f -> Ok(if f = 0.0 then 0.0 else f)
    | true, _ -> Error $"not a finite float: '{excerpt text}'"
    | _ -> Error $"not a float: '{excerpt text}'"

// sizes render binary units [D:size] — KiB/MiB/GiB/TiB, one decimal
// above bytes, plain bytes with no decimal. Tenths truncate: a report,
// not an encoding — base-1024 decimals do not terminate, so unlike
// Duration's show this is lossy by design; toBytes is the exact exit.
// Integer math throughout (no float, the grep-clean bar).
let formatSize (totalBytes: int64) : string =
    let sign = if totalBytes < 0L then "-" else ""
    // the magnitude unsigned (Int64.MinValue has no int64 abs), and tenths
    // split as whole + remainder so no `* 10` overflows near the max
    let b =
        if totalBytes < 0L then
            uint64 (-(totalBytes + 1L)) + 1UL
        else
            uint64 totalBytes

    let unitOf =
        [ 1024UL * 1024UL * 1024UL * 1024UL, "TiB"
          1024UL * 1024UL * 1024UL, "GiB"
          1024UL * 1024UL, "MiB"
          1024UL, "KiB" ]
        |> List.tryFind (fun (u, _) -> b >= u)

    match unitOf with
    | None -> $"{sign}{b} B"
    | Some(u, name) ->
        let whole = b / u
        let dec = (b % u) * 10UL / u

        if dec = 0UL then
            $"{sign}{whole} {name}"
        else
            $"{sign}{whole}.{dec} {name}"

// parse reads foreign text [D:size]: binary units at 1024-powers, the
// SI spellings as powers of ten (the writer chose the unit — unlike a
// literal, where weir would be guessing), optional space, decimals
// down to whole bytes (sub-byte precision rejects, as sub-ms does)
/// `whole.frac` × unit as an exact magnitude, for the Size and Duration
/// parsers: None when the fraction leaves a remainder (finer than the
/// base unit). Exact arithmetic, so zero padding of any length is
/// harmless; the int64 range is the caller's check AFTER the sign is
/// applied, so the minimum value show prints reads back.
let private scaleExact (whole: string) (frac: string) (unit: int64) : bigint option =
    let whole = whole.TrimStart '0'
    let frac = frac.TrimEnd '0'

    // past 40 digits no int64 can come out; spare bigint the work
    if whole.Length > 40 || frac.Length > 40 then
        Some(bigint.Pow(10I, 41))
    else
        let w = if whole = "" then 0I else bigint.Parse whole
        let f = if frac = "" then 0I else bigint.Parse frac
        let pow10 = bigint.Pow(10I, frac.Length)
        let scaled = f * bigint unit

        if scaled % pow10 <> 0I then
            None
        else
            Some(w * bigint unit + scaled / pow10)

let private inInt64 (v: bigint) =
    v >= bigint System.Int64.MinValue && v <= bigint System.Int64.MaxValue

let parseSize (text: string) : Result<int64, string> =
    let t = text.Trim()
    let neg = t.StartsWith "-"
    let t = if neg then (t.Substring 1).TrimStart() else t

    let units =
        [ "TiB", 1024L * 1024L * 1024L * 1024L
          "GiB", 1024L * 1024L * 1024L
          "MiB", 1024L * 1024L
          "KiB", 1024L
          "TB", 1000_000_000_000L
          "GB", 1000_000_000L
          "MB", 1000_000L
          "KB", 1000L
          "B", 1L ]

    let m =
        System.Text.RegularExpressions.Regex.Match(t, "^([0-9]+)(?:\.([0-9]+))? ?([A-Za-z]+)$")

    if not m.Success then
        Error $"not a size: '{excerpt text}' — expected digits then a unit (512B, 1.5MiB)"
    else
        match units |> List.tryFind (fun (u, _) -> u = m.Groups[3].Value) with
        | None ->
            Error
                $"not a size: '{excerpt text}' — unknown unit '{m.Groups[3].Value}' (binary KiB/MiB/GiB/TiB, SI KB/MB/GB/TB, or B)"
        | Some(_, unit) ->
            // the no-silent-wrap law of int arithmetic: overflow gets its
            // own words, never Int64.Parse's [D:transport-words]
            let frac = if m.Groups[2].Success then m.Groups[2].Value else ""

            match scaleExact m.Groups[1].Value frac unit with
            | None -> Error $"not a size: '{excerpt text}' — sub-byte precision (bytes are the unit)"
            | Some mag ->
                let v = if neg then -mag else mag

                if inInt64 v then
                    Ok(int64 v)
                else
                    Error $"not a size: '{excerpt text}' — beyond the 64-bit byte range"

let formatDuration (totalMs: int64) : string =
    if totalMs = 0L then
        "0s"
    else
        let sign = if totalMs < 0L then "-" else ""
        // the magnitude unsigned, as formatSize does (Int64.MinValue has no int64 abs)
        let ms =
            if totalMs < 0L then
                uint64 (-(totalMs + 1L)) + 1UL
            else
                uint64 totalMs

        let h = ms / 3600000UL
        let m = (ms % 3600000UL) / 60000UL
        let s = (ms % 60000UL) / 1000UL
        let frac = ms % 1000UL

        let sec =
            if s = 0UL && frac = 0UL then
                ""
            elif frac = 0UL then
                $"{s}s"
            else
                let f = $"%03d{frac}".TrimEnd '0'
                $"{s}.{f}s"

        if h = 0UL && m = 0UL && s = 0UL then
            $"{sign}{frac}ms"
        else
            let hPart = if h > 0UL then $"{h}h" else ""
            let mPart = if m > 0UL then $"{m}m" else ""
            $"{sign}{hPart}{mPart}{sec}"

// ---- Instant [D:instant]: a point on the UTC timeline ----------------

// the ISO reader: full timestamps (Z or a numeric offset, normalized
// to UTC on the way in; fractional seconds truncate to ms) and the
// bare date (midnight UTC). Only this format list is accepted — never
// the platform parser's leniency (invariant M/d/yyyy must not slip in).
let private instantFormats =
    [| "yyyy-MM-dd'T'HH:mm:ssK"
       "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"
       "yyyy-MM-dd'T'HH:mm:ss"
       "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
       "yyyy-MM-dd" |]

// RFC 3339 (and so TOML) also spells the separator as a space and the
// fraction past .NET's 7 digits (nanoseconds); both normalize into the
// list's spelling — the fraction truncates to ticks, then to ms anyway
let private instantSpaceSep =
    System.Text.RegularExpressions.Regex @"^(\d{4}-\d{2}-\d{2})[ t](\d{2}:\d{2})"

// RFC 3339 takes a lowercase `z` too, and TOML 1.1 (as ISO 8601) a time
// without seconds, which reads as :00 [D:instant-rfc3339-forms]
let private instantLowerZ =
    System.Text.RegularExpressions.Regex @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?)z$"

let private instantNoSeconds =
    System.Text.RegularExpressions.Regex @"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2})(?=$|[Z+-])"

let private instantLongFrac =
    System.Text.RegularExpressions.Regex @"(T\d{2}:\d{2}:\d{2}\.\d{7})\d+"

// .NET's F-fraction also matches zero digits, so `12:34:56.Z` would
// read; a fraction dot needs at least one digit
let private instantEmptyFrac =
    System.Text.RegularExpressions.Regex @"T\d{2}:\d{2}:\d{2}\.(?!\d)"

let parseInstantMs (text: string) : Result<int64, string> =
    let t = text.Trim()
    let norm =
        instantSpaceSep.Replace(t, "$1T$2")
        |> fun n -> instantLowerZ.Replace(n, "$1Z")
        |> fun n -> instantNoSeconds.Replace(n, "$1:00")
        |> fun n -> instantLongFrac.Replace(n, "$1")

    let refused () =
        Error
            $"not an ISO 8601 instant ('{excerpt t}'; 2026-08-14T12:34:56Z — offsets allowed, a bare date reads as midnight UTC)"

    if instantEmptyFrac.IsMatch norm then
        refused ()
    else
        match
            System.DateTimeOffset.TryParseExact(
                norm,
                instantFormats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                ||| System.Globalization.DateTimeStyles.AdjustToUniversal
            )
        with
        | true, dto -> Ok(dto.ToUnixTimeMilliseconds())
        | _ -> refused ()

/// an Instant is an epoch-ms in DateTimeOffset's range (years 1-9999);
/// a value outside it raises in weir's words at the point it is made,
/// never later inside a .NET formatter
let checkInstant (who: string) (ms: int64) : int64 =
    let lo = System.DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
    let hi = System.DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()

    if ms < lo || ms > hi then
        failwith $"{who}: instant out of range — epoch ms {ms} is outside years 0001-9999 ({lo}..{hi})"

    ms

/// UTC always; millis shown only when nonzero (show's spelling)
let formatInstant (ms: int64) : string =
    let dto = System.DateTimeOffset.FromUnixTimeMilliseconds ms

    if ms % 1000L = 0L then
        dto.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
    else
        dto.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)

// the uuid text forms [D:uuid]: 8-4-4-4-12 hex or 32 bare hex, any case,
// an optional urn:uuid: prefix. Only these — never the platform parser's
// braces, parens or hex-struct forms. Any 128-bit value reads (nil, max,
// every version and variant); inspection is Uuid.version's job.
// exact text, never trimmed [D:uuid-strict]: surrounding whitespace is
// not a uuid spelling
let parseUuid (text: string) : Result<System.Guid, string> =
    let t = text

    let body =
        if t.StartsWith("urn:uuid:", System.StringComparison.OrdinalIgnoreCase) then
            t.Substring 9
        else
            t

    let shaped =
        match body.Length with
        | 36 ->
            body
            |> Seq.indexed
            |> Seq.forall (fun (i, c) ->
                if i = 8 || i = 13 || i = 18 || i = 23 then
                    c = '-'
                else
                    System.Char.IsAsciiHexDigit c)
        | 32 -> body |> Seq.forall System.Char.IsAsciiHexDigit
        | _ -> false

    if shaped then
        Ok(System.Guid.ParseExact(body, (if body.Length = 36 then "D" else "N")))
    else
        Error
            $"not a uuid ('{excerpt t}'; 017f22e2-79b0-7cc3-98c4-dc0c0c07398f — 32 hex digits, hyphens and a urn:uuid: prefix optional)"

/// lowercase 8-4-4-4-12 — the one spelling show, json and toString share
let formatUuid (g: System.Guid) : string = g.ToString "D"

// the named-format reader [D:instant] — a strptime subset for log
// lines: %Y %m %d %H %M %S %f %z, %% for a literal percent, everything
// else literal text. Prefix semantics: the format's end ends the read
// (a log line's tail rides free — slicing is the use case). No %z
// means UTC, stated. The format must carry a full date — year-less
// log formats (syslog) need the year supplied; revisit given real
// cases.
//
// A format defect (an unknown directive, a lone trailing `%`, no full
// date) is found from the format alone, before any input is read — so
// tryParseWith raises on it whatever the input [D:instant].
let instantFormatDefect (fmt: string) : string option =
    let mutable i = 0
    let mutable defect = None
    let mutable year = false
    let mutable month = false
    let mutable day = false

    while defect.IsNone && i < fmt.Length do
        if fmt[i] = '%' then
            if i + 1 >= fmt.Length then
                defect <- Some "a lone '%' ends the format — write %% for a literal percent"
            else
                match fmt[i + 1] with
                | 'Y' -> year <- true
                | 'm'
                | 'b' -> month <- true
                | 'd'
                | 'e' -> day <- true
                | 'H'
                | 'M'
                | 'S'
                | 'f'
                | 'z'
                | '%' -> ()
                | c ->
                    let shown =
                        if System.Char.IsHighSurrogate c && i + 2 < fmt.Length then
                            fmt.Substring(i + 1, 2)
                        else
                            string c

                    defect <-
                        Some
                            $"unknown directive '%%{quoteData shown}' — the format takes %%Y %%m %%d %%e %%b %%H %%M %%S %%f %%z (and %%%% for a literal percent)"

            i <- i + 2
        else
            i <- i + 1

    match defect with
    | None when not (year && month && day) ->
        Some "the format must carry a full date (%Y %m %d) — a year-less log format needs the year supplied elsewhere"
    | d -> d

let parseInstantWithMs (fmt: string) (text: string) : Result<int64, string> =
    let mutable fi = 0
    let mutable ti = 0
    let mutable y = -1
    let mutable mo = -1
    let mutable d = -1
    let mutable h = 0
    let mutable mi = 0
    let mutable sec = 0
    let mutable frac = 0
    let mutable offMin = 0
    let mutable offText = "Z"
    let mutable offShapeOk = true
    // a format defect wins before any input is read
    let mutable error: string option = instantFormatDefect fmt

    let readDigits (n: int) (what: string) =
        if
            ti + n > text.Length
            || text.Substring(ti, n) |> Seq.exists (System.Char.IsAsciiDigit >> not)
        then
            let got =
                if ti < text.Length then
                    text.Substring(ti, min n (text.Length - ti))
                else
                    "end of input"

            error <-
                Some
                    $"input does not match the format at position {ti + 1}: expected {n} digits for {what}, got '{got}'"

            0
        else
            let v = int (text.Substring(ti, n))
            ti <- ti + n
            v

    while error.IsNone && fi < fmt.Length do
        if fmt[fi] = '%' && fi + 1 < fmt.Length then
            (match fmt[fi + 1] with
             | 'Y' -> y <- readDigits 4 "%Y"
             | 'm' -> mo <- readDigits 2 "%m"
             | 'b' ->
                 // the invariant month abbreviations — openssl's enddate,
                 // most access logs
                 let names =
                     [| "Jan"
                        "Feb"
                        "Mar"
                        "Apr"
                        "May"
                        "Jun"
                        "Jul"
                        "Aug"
                        "Sep"
                        "Oct"
                        "Nov"
                        "Dec" |]

                 if ti + 3 <= text.Length then
                     let w = text.Substring(ti, 3)

                     match
                         names
                         |> Array.tryFindIndex (fun n ->
                             System.String.Equals(n, w, System.StringComparison.OrdinalIgnoreCase))
                     with
                     | Some i ->
                         mo <- i + 1
                         ti <- ti + 3
                     | None ->
                         error <-
                             Some
                                 $"input does not match the format at position {ti + 1}: expected a month name (Jan..Dec) for %%b, got '{w}'"
                 else
                     error <-
                         Some
                             $"input does not match the format at position {ti + 1}: expected a month name for %%b, got end of input"
             | 'e' ->
                 // 1-2 digit day, optionally space-padded (openssl's 'Aug  4')
                 if ti < text.Length && text[ti] = ' ' then
                     ti <- ti + 1

                 let start = ti

                 while ti < text.Length && System.Char.IsAsciiDigit text[ti] && ti - start < 2 do
                     ti <- ti + 1

                 if ti = start then
                     error <- Some $"input does not match the format at position {start + 1}: expected a day for %%e"
                 else
                     d <- int (text.Substring(start, ti - start))
             | 'd' -> d <- readDigits 2 "%d"
             | 'H' -> h <- readDigits 2 "%H"
             | 'M' -> mi <- readDigits 2 "%M"
             | 'S' -> sec <- readDigits 2 "%S"
             | 'f' ->
                 let start = ti

                 while ti < text.Length && System.Char.IsAsciiDigit text[ti] && ti - start < 9 do
                     ti <- ti + 1

                 if ti = start then
                     error <- Some $"input does not match the format at position {start + 1}: expected digits for %%f"
                 else
                     let digits = text.Substring(start, min 3 (ti - start)).PadRight(3, '0')
                     frac <- int digits
             | 'z' ->
                 if ti < text.Length && (text[ti] = 'Z' || text[ti] = 'z') then
                     ti <- ti + 1
                 elif ti < text.Length && (text[ti] = '+' || text[ti] = '-') then
                     let sign = if text[ti] = '-' then -1 else 1
                     let start = ti
                     ti <- ti + 1
                     let hh = readDigits 2 "%z hours"

                     if error.IsNone then
                         if ti < text.Length && text[ti] = ':' then
                             ti <- ti + 1

                         let mm = readDigits 2 "%z minutes"
                         offMin <- sign * (hh * 60 + mm)
                         offText <- text.Substring(start, ti - start)
                         offShapeOk <- mm < 60 && (hh < 14 || (hh = 14 && mm = 0))
                 else
                     error <- Some $"input does not match the format at position {ti + 1}: expected Z or ±HH:MM for %%z"
             | '%' ->
                 if ti < text.Length && text[ti] = '%' then
                     ti <- ti + 1
                 else
                     error <- Some $"input does not match the format at position {ti + 1}: expected '%%'"
             | c ->
                 error <-
                     Some
                         $"unknown directive '%%{c}' — the format takes %%Y %%m %%d %%e %%b %%H %%M %%S %%f %%z (and %%%% for a literal percent)")

            fi <- fi + 2
        else
            (if ti < text.Length && text[ti] = fmt[fi] then
                 ti <- ti + 1
             else
                 let got = if ti < text.Length then string text[ti] else "end of input"

                 error <-
                     Some $"input does not match the format at position {ti + 1}: expected '{fmt[fi]}', got '{got}'")

            fi <- fi + 1

    match error with
    | Some e -> Error e
    | None when y < 0 || mo < 0 || d < 0 ->
        Error "the format must carry a full date (%Y %m %d) — a year-less log format needs the year supplied elsewhere"
    | None ->
        let dateText = $"{y:D4}-{mo:D2}-{d:D2} {h:D2}:{mi:D2}:{sec:D2}"

        // the three failures are told apart so the message blames the
        // part that is wrong: the date/time itself, the offset's shape,
        // or the UTC result the offset shifts past the Instant range
        match (try Ok(System.DateTime(y, mo, d, h, mi, sec, frac)) with _ -> Error()) with
        | Error() -> Error $"not a real date/time: {dateText}"
        | Ok local ->
            if not offShapeOk then
                Error $"'{offText}' is not a valid %%z offset — at most ±14:00, minutes below 60"
            else
                try
                    let dto = System.DateTimeOffset(local, System.TimeSpan.FromMinutes(float offMin))
                    Ok(dto.ToUnixTimeMilliseconds())
                with _ ->
                    Error $"{dateText} {offText} is outside the Instant range once shifted to UTC (years 0001-9999)"

/// Parse "30s" / "2.5s" / "1h30m" / "-90s" to ms — compound components
/// largest-first not required; decimals convert by integer math and
/// sub-millisecond precision is rejected rather than rounded.
let parseDurationMs (text: string) : Result<int64, string> =
    let t = text.Trim()
    let neg, body = (if t.StartsWith "-" then true, t.Substring 1 else false, t)

    let unitMs u =
        match u with
        | "ms" -> Some 1L
        | "s" -> Some 1000L
        | "m" -> Some 60000L
        | "h" -> Some 3600000L
        | _ -> None

    // the magnitude accumulates exactly; the int64 range is checked once,
    // after the sign — so the minimum Duration show prints reads back
    let rec go (i: int) (acc: bigint) =
        if i >= body.Length then
            if i = 0 then Error $"not a duration: '{excerpt text}'" else Ok acc
        else
            let j0 = i
            let mutable j = i

            while j < body.Length && System.Char.IsAsciiDigit body[j] do
                j <- j + 1

            if j = j0 then
                Error $"not a duration: '{excerpt text}' — expected digits at position {i + 1}"
            else
                let whole = body.Substring(j0, j - j0)

                let fracDigits, j =
                    if j < body.Length && body[j] = '.' then
                        let f0 = j + 1
                        let mutable k = f0

                        while k < body.Length && System.Char.IsAsciiDigit body[k] do
                            k <- k + 1

                        (if k = f0 then None else Some(body.Substring(f0, k - f0))), k
                    else
                        Some "", j

                match fracDigits with
                | None -> Error $"not a duration: '{excerpt text}' — a decimal point needs digits"
                | Some frac ->
                    let u0 = j
                    let mutable k = j

                    while k < body.Length && System.Char.IsLetter body[k] do
                        k <- k + 1

                    match unitMs (body.Substring(u0, k - u0)) with
                    | None -> Error $"not a duration: '{excerpt text}' — units are ms, s, m, h"
                    | Some unit ->
                        match scaleExact whole frac unit with
                        | None ->
                            Error $"not a duration: '{excerpt text}' — sub-millisecond precision (ms is the base unit)"
                        | Some part ->
                            let acc = acc + part

                            // past the range already: stop growing (a long
                            // compound stays linear) and report it
                            if acc > bigint System.Int64.MaxValue + 1I then
                                Ok acc
                            else
                                go k acc

    // overflow in its own words, never Int64.Parse's [D:transport-words]
    match go 0 0I with
    | Error e -> Error e
    | Ok mag ->
        let v = if neg then -mag else mag

        if inInt64 v then
            Ok(int64 v)
        else
            Error $"not a duration: '{excerpt text}' — beyond the 64-bit millisecond range"

// The one JSON string encoder: CLI/LSP payloads and user `to json` /
// `to jsonl` / `--can --json` [D:json-raw-user].
// UnsafeRelaxedJsonEscaping: "unsafe" means HTML-embedding only — these
// payloads are never HTML; the default encoder's \u0022-style quote
// escaping is valid but trips naive clients [D:json-relaxed-escaping]
// BMP non-ASCII passes through raw [D:json-relaxed-escaping]: a
// Private-Use glyph (a Powerline/Nerd-Font prompt icon, U+E000–U+F8FF)
// must reach the client as UTF-8, not a `\u`-escape a naive LSP client
// leaves as literal text. Every System.Text.Json encoder — including
// UnsafeRelaxedJsonEscaping and Create(UnicodeRanges.All) — escapes the
// PUA by a hardcoded policy, so this thin subclass delegates to
// UnsafeRelaxed but refuses to escape any BMP scalar >= 0x80 (surrogate
// halves excepted — astral chars stay a valid escaped pair). The
// JSON-mandatory escapes (`"`, `\`, controls) are untouched.
type RawBmpEncoder() =
    inherit System.Text.Encodings.Web.JavaScriptEncoder()
    let inner = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    let allow (u: int) = u >= 0x80 && u <= 0xFFFF && not (u >= 0xD800 && u <= 0xDFFF)
    override _.MaxOutputCharactersPerInputCharacter = inner.MaxOutputCharactersPerInputCharacter
    override _.WillEncode(u) = if allow u then false else inner.WillEncode u
    override _.FindFirstCharacterToEncode(text, textLength) = inner.FindFirstCharacterToEncode(text, textLength)
    override _.TryEncodeUnicodeScalar(u, buffer, bufferLength, written) =
        inner.TryEncodeUnicodeScalar(u, buffer, bufferLength, &written)

let rawJsonWriterOptions =
    System.Text.Json.JsonWriterOptions(Encoder = RawBmpEncoder())

// The JSON reading depth [D:json-depth]: weir's own value bound (show,
// equality, to yaml all stop at 100), not System.Text.Json's default 64
// — a valid document between the two read as "not valid JSON". Past the
// bound the refusal names the depth; invalid JSON stays its own error.
let jsonMaxDepth = 100

// telling the two failures apart: one reader pass over the rejected
// text, no document built — JsonDocument's own unbounded build is
// quadratic in depth, the reader linear [D:json-deep-linear]
let private validPastBound (utf8: System.ReadOnlySpan<byte>) : bool =
    // the document parser skips a UTF-8 BOM; the bare reader does not
    let utf8 =
        if utf8.Length >= 3 && utf8[0] = 0xEFuy && utf8[1] = 0xBBuy && utf8[2] = 0xBFuy then
            utf8.Slice 3
        else
            utf8

    let mutable reader =
        System.Text.Json.Utf8JsonReader(utf8, System.Text.Json.JsonReaderOptions(MaxDepth = System.Int32.MaxValue))

    let mutable deep = false

    try
        while reader.Read() do
            match reader.TokenType with
            | System.Text.Json.JsonTokenType.StartObject
            | System.Text.Json.JsonTokenType.StartArray when reader.CurrentDepth >= jsonMaxDepth -> deep <- true
            | _ -> ()

        deep
    with :? System.Text.Json.JsonException ->
        false

let private parseJsonBoundedWith
    (parse: System.Text.Json.JsonDocumentOptions -> System.Text.Json.JsonDocument)
    (utf8: unit -> byte array)
    : Result<System.Text.Json.JsonDocument, string option> =
    try
        Ok(parse (System.Text.Json.JsonDocumentOptions(MaxDepth = jsonMaxDepth)))
    with :? System.Text.Json.JsonException ->
        if validPastBound (System.ReadOnlySpan(utf8 ())) then
            Error(Some $"it nests deeper than {jsonMaxDepth} levels, weir's bound for a value")
        else
            Error None

/// parse one JSON document under the bound: Error (Some why) for a
/// valid document nested past it, Error None for text that is not JSON
let parseJsonBounded (text: string) : Result<System.Text.Json.JsonDocument, string option> =
    parseJsonBoundedWith
        (fun o -> System.Text.Json.JsonDocument.Parse(text, o))
        (fun () -> System.Text.Encoding.UTF8.GetBytes text)

/// parseJsonBounded over UTF-8 bytes
let parseJsonBytesBounded (bytes: byte array) : Result<System.Text.Json.JsonDocument, string option> =
    parseJsonBoundedWith (fun o -> System.Text.Json.JsonDocument.Parse(System.ReadOnlyMemory<byte> bytes, o)) (fun () -> bytes)
