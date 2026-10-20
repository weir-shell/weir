module Weir.Scan

// compiled before Complete, which asks where a `$` stands
// [D:argv-splice-complete]; Script re-exports it

// The quote-aware scanner, the single string-state primitive
// [D:one-scanner]. Folds f over the characters outside string
// literals: double quotes honor backslash escapes, single quotes close
// at the next single quote.
// The outside-string fold and the end-state question share this one
// machine [D:one-scanner] — a second inline quote machine is a review
// flag. stringScan returns the fold result and whether the line ends
// inside a string.
// The scanner's lexical modes: an interpolation hole is code nested
// inside a string — its own strings and braces nest, and none of it is
// "outside strings" (a `//` or `{` in a hole is string turf).
type private ScanMode =
    | Plain
    | Single
    | Verbatim
    | Triple
    | Interp
    | InterpTriple
    | Hole of depth: int

// the scanner's state between chunks: the open modes, innermost first
type StringState = private StringState of ScanMode list

let private stringScanFrom
    (modes0: ScanMode list)
    (f: 'a -> int -> char -> 'a)
    (init: 'a)
    (s: string)
    : 'a * ScanMode list =
    let mutable st = init
    let mutable i = 0
    // innermost first; empty = top-level code
    let mutable modes: ScanMode list = modes0

    let tripleAt (j: int) =
        j + 2 < s.Length && s[j] = '"' && s[j + 1] = '"' && s[j + 2] = '"'

    // a string opener at i in code (top level or a hole): raw kinds
    // [D:raw-strings] — verbatim @"..." ("" = one quote, no escapes) and
    // triple-quoted """...""" (no escapes, closes at the first triple);
    // `$` turns a plain or triple quote into its interpolated twin
    let opener () : bool =
        let c = s[i]
        let dollar = i > 0 && s[i - 1] = '$'

        if c = '@' && i + 1 < s.Length && s[i + 1] = '"' then
            modes <- Verbatim :: modes
            i <- i + 1
            true
        elif tripleAt i then
            modes <- (if dollar then InterpTriple else Triple) :: modes
            i <- i + 2
            true
        elif c = '"' then
            modes <- (if dollar then Interp else Plain) :: modes
            true
        elif c = '\'' then
            modes <- Single :: modes
            true
        else
            false

    while i < s.Length do
        let c = s[i]

        match modes with
        | [] ->
            if not (opener ()) then
                st <- f st i c
        | Hole d :: rest ->
            if not (opener ()) then
                if c = '{' then modes <- Hole(d + 1) :: rest
                elif c = '}' then modes <- (if d = 0 then rest else Hole(d - 1) :: rest)
        | (Plain
          | Interp as m) :: rest ->
            if c = '\\' && i + 1 < s.Length then
                i <- i + 1
            elif c = '"' then
                modes <- rest
            elif m = Interp && c = '{' then
                if i + 1 < s.Length && s[i + 1] = '{' then
                    i <- i + 1
                else
                    modes <- Hole 0 :: modes
        | Single :: rest ->
            if c = '\'' then
                modes <- rest
        | Verbatim :: rest ->
            if c = '"' then
                if i + 1 < s.Length && s[i + 1] = '"' then
                    i <- i + 1
                else
                    modes <- rest
        | (Triple
          | InterpTriple as m) :: rest ->
            if tripleAt i then
                i <- i + 2
                modes <- rest
            elif m = InterpTriple && c = '{' then
                if i + 1 < s.Length && s[i + 1] = '{' then
                    i <- i + 1
                else
                    modes <- Hole 0 :: modes

        i <- i + 1

    st, modes

let stringScan (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a * bool =
    let st, modes = stringScanFrom [] f init s
    st, not modes.IsEmpty

let foldOutsideStrings (f: 'a -> int -> char -> 'a) (init: 'a) (s: string) : 'a = fst (stringScan f init s)

// All four weir string kinds are single-line, so a line ending inside
// one can never be completed by more input — the multiline REPL submits
// such a buffer instead of growing it [D:repl-multiline]
let endsInsideString (s: string) : bool =
    snd (stringScan (fun () _ _ -> ()) () s)

// endsInsideString over a text fed in chunks, linear in the total
// [D:one-scanner]: the state after `a` then `b` is the state after
// `a + b` whenever `b` starts with a newline (no lookahead or lookbehind
// matches across one)
let stringStart: StringState = StringState []

let stringStep (StringState modes) (chunk: string) : StringState =
    StringState(snd (stringScanFrom modes (fun () _ _ -> ()) () chunk))

let isInsideString (StringState modes) : bool = not modes.IsEmpty
