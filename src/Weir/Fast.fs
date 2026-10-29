module Weir.Fast

// The hand-written happy-path parser [D:hand-parser]: a scanner-driven
// recursive descent over one logical line that builds exactly the tree
// Parser.fs's grammar would build, or refuses. It never reports: every
// construct it does not know, every shape the command grammar could
// claim, every doubt is a refusal, and the FParsec grammar then parses
// the statement as the sole source of diagnostics. Refusal is a flag,
// not an exception — it is the common case on real code and must cost
// nothing but the characters read.

open System
open Weir.Ast
open Weir.Types
open Weir.Desugar

type private St =
    { Text: string
      R: Resolver
      mutable I: int
      mutable Ok: bool
      // a refusal no speculative path may retry: past the nesting bound
      // or the stack probe, where a retry per level would double the work
      mutable Fatal: bool
      mutable Depth: int
      // names bound by enclosing lambdas, lets and patterns — the
      // grammar's ambient-resolver extensions, for the command-head rule
      mutable Locals: Set<string>
      mutable InArm: int
      mutable ExprParen: bool
      // where the first refusal happened — the coverage work's one instrument
      mutable RefusedAt: int
      PendingAt: int }

let private maxNesting = 64

let private isDigit (c: char) = c >= '0' && c <= '9'

let private isHex (c: char) =
    isDigit c || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')

let private eos = '￿'

let inline private peek (st: St) =
    if st.I < st.Text.Length then st.Text[st.I] else eos

let inline private peekAt (st: St) (k: int) =
    let j = st.I + k
    if j >= 0 && j < st.Text.Length then st.Text[j] else eos

let inline private at (i: int) : Pos = { Line = 1; Col = i + 1 }

let inline private here (st: St) : Pos = at st.I

let inline private refuse (st: St) : 'a =
    if st.Ok then
        st.RefusedAt <- st.I

    st.Ok <- false
    Unchecked.defaultof<'a>

let private tooDeep (st: St) : bool =
    if
        st.Depth > maxNesting
        || not (Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
    then
        st.Fatal <- true
        true
    else
        false

// a speculative path's retry: back to `i` unless the refusal was fatal
let inline private retry (st: St) (i: int) : bool =
    if st.Fatal then
        false
    else
        st.Ok <- true
        st.I <- i
        true

let inline private isNull' (e: 'a) = obj.ReferenceEquals(e, Unchecked.defaultof<_>)

let inline private optOf (e: 'a) = if isNull' e then None else Some e

let private ws (st: St) =
    let t = st.Text
    let mutable i = st.I

    while i < t.Length && (t[i] = ' ' || t[i] = '\t') do
        i <- i + 1

    st.I <- i

let private matches (st: St) (s: string) =
    String.CompareOrdinal(st.Text, st.I, s, 0, s.Length) = 0

// the word under the cursor, unconsumed: an identifier shape or Unchecked.defaultof<_>
let private wordAhead (st: St) : string =
    let t = st.Text

    if st.I < t.Length && isIdentStart t[st.I] then
        let mutable j = st.I + 1

        while j < t.Length && isIdentCont t[j] do
            j <- j + 1

        t.Substring(st.I, j - st.I)
    else
        Unchecked.defaultof<_>

let private takeWord (st: St) : string =
    let w = wordAhead st

    if not (isNull' w) then
        st.I <- st.I + w.Length

    w

// `keyword s`: the word, not glued to an identifier char, then ws
let private keywordAhead (st: St) (s: string) =
    matches st s && not (isIdentCont (peekAt st s.Length))

let private keyword (st: St) (s: string) : bool =
    if keywordAhead st s then
        st.I <- st.I + s.Length
        ws st
        true
    else
        false

let private strWs (st: St) (s: string) : bool =
    if matches st s then
        st.I <- st.I + s.Length
        ws st
        true
    else
        false

let private expect (st: St) (s: string) : unit =
    if not (strWs st s) then
        st.Ok <- false

// the record/list field separator: `;` or the assembler's sentinel
let private fieldSepP (st: St) : bool = strWs st ";" || strWs st fieldSepStr

let private spannedWord (st: St) : string * Span =
    let s = st.I
    let w = takeWord st
    w, { Start = at s; End = here st }

// identSpanned: a non-keyword identifier, then ws
let private identSpanned (st: St) : string * Span =
    let w = wordAhead st

    if isNull' w || keywords.Contains w then
        Unchecked.defaultof<string>, Unchecked.defaultof<Span>
    else
        let r = spannedWord st
        ws st
        r

// ---- the command-head rule, mirrored [D:hand-parser] ------------------
// Parser.fs tries the command grammar first at every statement position;
// its head decides against the resolver in this exact order. A head the
// command grammar would claim is a refusal here (P1 parses no commands).
let private cmdWordChar (c: char) =
    not (Char.IsWhiteSpace c)
    && c <> '|'
    && c <> '('
    && c <> ')'
    && c <> '"'
    && c <> '\''
    && c <> '$'
    && c <> sibSep
    && c <> fieldSep

let private isIdentLike (w: string) =
    isIdentStart w[0] && w |> Seq.forall isIdentCont

let private isKnown (st: St) (w: string) = st.Locals.Contains w || st.R.IsKnown w

let private couldBeCommand (st: St) (builtinHeads: bool) : bool =
    let t = st.Text
    let c = peek st

    if c = '^' || c = '\\' then
        true
    elif not (cmdWordChar c) || c = eos then
        false
    else
        let mutable j = st.I

        while j < t.Length && j - st.I < 1024 && cmdWordChar t[j] do
            j <- j + 1

        if j < t.Length && t[j] = sibSep then
            false
        else
            let w = t.Substring(st.I, j - st.I)

            if w[0] = '[' then
                false
            elif w.StartsWith "~/" then
                true
            elif isIdentLike w && (st.R.AliasHead w).IsSome then
                true
            elif builtinHeads && isIdentLike w && st.R.IsCommandCallable w then
                true
            elif isIdentLike w && (keywords.Contains w || isKnown st w) then
                false
            else
                st.R.IsExternal w

// the statement-position guards that fire before the expression grammar
// gets a look: foreign keywords, `NAME=value` env prefixes, `\`-heads
let private guardedHead (st: St) : bool =
    let t = st.Text
    let c = peek st

    if c = '\\' then
        true
    elif isIdentStart c then
        let w = wordAhead st

        if w = "while" || w = "return" || w = "try" || w = "def" then
            true
        else
            let j = st.I + w.Length
            // an env prefix is `ident=` glued, never `==`
            j < t.Length && t[j] = '=' && not (j + 1 < t.Length && t[j + 1] = '=')
    else
        false

// ---- literals -----------------------------------------------------------
let private unitSuffixes = [| "KiB"; "MiB"; "GiB"; "TiB"; "KB"; "MB"; "GB"; "TB"; "ms"; "B"; "s"; "m"; "h"; "d" |]
let private fracSuffixes = [| "KiB"; "MiB"; "GiB"; "TiB"; "ms"; "B"; "s"; "m"; "h" |]

let private digitsAhead (st: St) : string =
    let t = st.Text
    let mutable j = st.I

    while j < t.Length && isDigit t[j] do
        j <- j + 1

    if j = st.I then null else t.Substring(st.I, j - st.I)

let private intLit (st: St) : Expr =
    let start = st.I
    let digits = digitsAhead st
    st.I <- st.I + digits.Length
    let t = st.Text

    // a measure `<m>` is a refusal (the grammar's fatal)
    if peek st = '<' then
        let save = st.I
        st.I <- st.I + 1
        let w = takeWord st

        if not (isNull' w) && peek st = '>' then
            refuse st
        else
            st.I <- save

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        // unit suffix: first of the list that matches, a digit after it is
        // the compound teaching, a letter after it un-matches it
        let mutable sfx: string = null
        let mutable sfxDigit = false
        let mutable k = 0

        while isNull' sfx && k < unitSuffixes.Length do
            let u = unitSuffixes[k]

            if matches st u then
                let after = peekAt st u.Length

                if Char.IsLetter after || after = '_' then
                    k <- unitSuffixes.Length
                else
                    sfx <- u
                    sfxDigit <- isDigit after
                    st.I <- st.I + u.Length

            k <- k + 1

        // fraction: `.` not `..`, optional digits, optional unit
        let mutable dec: (string * string) option voption = ValueNone

        if peek st = '.' && peekAt st 1 <> '.' then
            st.I <- st.I + 1
            let fr = digitsAhead st

            if not (isNull' fr) then
                st.I <- st.I + fr.Length

            let mutable su: string = null
            let mutable k = 0

            while isNull' su && k < fracSuffixes.Length do
                if matches st fracSuffixes[k] then
                    su <- fracSuffixes[k]
                    st.I <- st.I + su.Length

                k <- k + 1

            dec <- ValueSome(Some(fr, su))

        // exponent
        let mutable expo: string = null

        if (peek st = 'e' || peek st = 'E') then
            let save = st.I
            st.I <- st.I + 1
            let sign = if peek st = '+' || peek st = '-' then string (peek st) else ""

            if sign <> "" then
                st.I <- st.I + 1

            let ds = digitsAhead st

            if isNull' ds then
                st.I <- save
            else
                st.I <- st.I + ds.Length
                let after = peek st

                if Char.IsLetterOrDigit after || after = '_' then
                    st.I <- save
                else
                    expo <- sign + ds

        let tail () =
            let mutable j = st.I

            while j < t.Length && (Char.IsLetterOrDigit t[j] || t[j] = '_' || t[j] = '.') do
                j <- j + 1

            t.Substring(st.I, j - st.I)

        let tailTeaches (text: string) (isFloat: bool) =
            let tl = tail ()
            let digitAt i = tl.Length > i && isDigit tl[i]

            (not isFloat
             && text = "0"
             && tl.Length > 1
             && "xXoObB".Contains tl[0]
             && Char.IsLetterOrDigit tl[1])
            || (tl.StartsWith "_" && digitAt 1)
            || (isFloat && tl.StartsWith "." && digitAt 1)

        let kind: ExprKind =
            match sfx with
            | null ->
                match dec with
                | ValueSome(Some(fr, su)) when not (isNull' fr) ->
                    if not (isNull' su) then
                        refuse st
                    else
                        let text = digits + "." + fr + (if isNull' expo then "" else "e" + expo)

                        match parseFloat text with
                        | Result.Ok f -> if tailTeaches text true then refuse st else EFloat f
                        | Result.Error _ -> refuse st
                | ValueSome _ -> refuse st
                | ValueNone ->
                    if not (isNull' expo) then
                        match parseFloat $"{digits}e{expo}" with
                        | Result.Ok f -> EFloat f
                        | Result.Error _ -> refuse st
                    else
                        match Int64.TryParse digits with
                        | true, n -> if tailTeaches digits false then refuse st else EInt n
                        | _ -> refuse st
            | u ->
                if sfxDigit || dec.IsSome || not (isNull' expo) then
                    refuse st
                else
                    match u with
                    | "KB"
                    | "MB"
                    | "GB"
                    | "TB"
                    | "d" -> refuse st
                    | _ ->
                        let isSize = u = "B" || u.EndsWith "iB"

                        let factor =
                            match u with
                            | "ms"
                            | "B" -> 1L
                            | "s" -> 1000L
                            | "m" -> 60000L
                            | "h" -> 3600000L
                            | "KiB" -> 1024L
                            | "MiB" -> 1024L * 1024L
                            | "GiB" -> 1024L * 1024L * 1024L
                            | _ -> 1024L * 1024L * 1024L * 1024L

                        match Int64.TryParse digits with
                        | true, n when n <= Int64.MaxValue / factor -> if isSize then ESize(n * factor) else EDur(n * factor)
                        | _ -> refuse st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let e = { Kind = kind; Span = { Start = at start; End = here st } }
            ws st
            e

// the one escape decoder, plain strings and interpolation text alike
let private escape (st: St) (sb: Text.StringBuilder) : unit =
    // at the backslash
    st.I <- st.I + 1

    match peek st with
    | '"' ->
        sb.Append '"' |> ignore
        st.I <- st.I + 1
    | '\\' ->
        sb.Append '\\' |> ignore
        st.I <- st.I + 1
    | 'n' ->
        sb.Append '\n' |> ignore
        st.I <- st.I + 1
    | 't' ->
        sb.Append '\t' |> ignore
        st.I <- st.I + 1
    | 'e' ->
        sb.Append '\u001b' |> ignore
        st.I <- st.I + 1
    | 'u' ->
        st.I <- st.I + 1

        if peek st <> '{' then
            st.Ok <- false
        else
            st.I <- st.I + 1
            let t = st.Text
            let mutable j = st.I

            while j < t.Length && j - st.I < 7 && isHex t[j] do
                j <- j + 1

            let n = j - st.I

            if n = 0 || n > 6 || j >= t.Length || t[j] <> '}' then
                st.Ok <- false
            else
                let v = Convert.ToInt32(t.Substring(st.I, n), 16)

                if (v >= 0xD800 && v <= 0xDFFF) || v > 0x10FFFF then
                    st.Ok <- false
                else
                    sb.Append(Char.ConvertFromUtf32 v) |> ignore
                    st.I <- j + 1
    | _ -> st.Ok <- false

// `"…"`: the body between the quotes, escapes decoded; Unchecked.defaultof<_> on refusal
let private plainStringBody (st: St) : string =
    // at the opening quote
    st.I <- st.I + 1
    let t = st.Text
    let sb = Text.StringBuilder()

    while st.Ok && st.I < t.Length && t[st.I] <> '"' do
        if t[st.I] = '\\' then
            escape st sb
        else
            let mutable j = st.I

            while j < t.Length && t[j] <> '"' && t[j] <> '\\' do
                j <- j + 1

            sb.Append(t, st.I, j - st.I) |> ignore
            st.I <- j

    if not st.Ok || st.I >= t.Length then
        refuse st
    else
        st.I <- st.I + 1
        sb.ToString()

let private verbatimBody (st: St) : string =
    // after `@"`
    let t = st.Text
    let sb = Text.StringBuilder()
    let mutable go = true

    while go do
        if st.I >= t.Length then
            go <- false
        elif t[st.I] = '"' then
            if st.I + 1 < t.Length && t[st.I + 1] = '"' then
                sb.Append '"' |> ignore
                st.I <- st.I + 2
            else
                go <- false
        else
            sb.Append t[st.I] |> ignore
            st.I <- st.I + 1

    if st.I >= t.Length then
        refuse st
    else
        st.I <- st.I + 1
        sb.ToString()

let private tripleBody (st: St) : string =
    // after `"""`
    let t = st.Text
    let sb = Text.StringBuilder()
    let mutable go = true

    while go do
        if st.I >= t.Length then
            go <- false
        elif t[st.I] = '"' then
            if st.I + 2 < t.Length && t[st.I + 1] = '"' && t[st.I + 2] = '"' then
                go <- false
            else
                sb.Append '"' |> ignore
                st.I <- st.I + 1
        else
            sb.Append t[st.I] |> ignore
            st.I <- st.I + 1

    if st.I + 2 >= t.Length || not (matches st "\"\"\"") then
        refuse st
    else
        st.I <- st.I + 3
        sb.ToString()

let private stringAtom (st: St) : Expr =
    let start = st.I

    let body =
        if matches st "\"\"\"" then
            st.I <- st.I + 3
            tripleBody st
        elif matches st "@\"" then
            st.I <- st.I + 2
            verbatimBody st
        else
            plainStringBody st

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let e = { Kind = EStr body; Span = { Start = at start; End = here st } }
        ws st
        e

// ---- types -----------------------------------------------------------------
let rec private tySyn (st: St) : Ty =
    st.Depth <- st.Depth + 1

    if tooDeep st then
        st.Depth <- st.Depth - 1
        refuse st
    else
        let first = tyTuple st
        let mutable items = [ first ]

        while st.Ok && strWs st "->" do
            let t = tyTuple st

            if st.Ok then
                items <- t :: items

        st.Depth <- st.Depth - 1

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match items with
            | [ one ] -> one
            | last :: rest -> rest |> List.fold (fun cod dom -> TFun(dom, cod)) last
            | [] -> refuse st

and private tyTuple (st: St) : Ty =
    let first = tyAtom st
    let mutable items = [ first ]

    while st.Ok && strWs st "*" do
        let t = tyAtom st

        if st.Ok then
            items <- t :: items

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        match items with
        | [ one ] -> one
        | _ -> TTuple(List.rev items)

and private tyAtom (st: St) : Ty =
    let c = peek st

    if c = '(' then
        st.I <- st.I + 1
        ws st
        let t = tySyn st
        if st.Ok && strWs st ")" then t else refuse st
    elif matches st "{|" then
        let fields = anonShape st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let n = anonRecordName fields
            pushAnonDef n fields
            TNamed(n, [])
    elif c = '\'' then
        st.I <- st.I + 1
        let w = takeWord st

        if isNull' w then
            refuse st
        else
            ws st
            TVar w
    elif isIdentStart c then
        let w = takeWord st

        match w with
        | "int" ->
            if peek st = '<' then
                refuse st
            else
                ws st
                TInt
        | "string" -> ws st; TStr
        | "bool" -> ws st; TBool
        | "unit" -> ws st; TUnit
        | "Duration" -> ws st; TDur
        | "Instant" -> ws st; TInstant
        | "Uuid" -> ws st; TUuid
        | "float" -> ws st; TFloat
        | "Size" -> ws st; TSize
        | "Bytes" -> ws st; TBytes
        | "Secret" -> ws st; TSecret
        | "seq" ->
            ws st

            if not (strWs st "<") then
                refuse st
            else
                let t = tySyn st
                if st.Ok && strWs st ">" then TSeq t else refuse st
        | w when keywords.Contains w -> refuse st
        | w ->
            let name =
                if peek st = '.' && isIdentStart (peekAt st 1) then
                    st.I <- st.I + 1
                    w + "." + takeWord st
                else
                    w

            ws st

            if strWs st "<" then
                let mutable args = []
                let mutable go = true

                while st.Ok && go do
                    let t = tySyn st

                    if st.Ok then
                        args <- t :: args

                        if not (strWs st ",") then
                            go <- false

                if st.Ok && strWs st ">" then
                    TNamed(name, List.rev args)
                else
                    refuse st
            else
                TNamed(name, [])
    else
        refuse st

and private fieldNameDecl (st: St) : string =
    let w = takeWord st

    if isNull' w || keywords.Contains w then
        refuse st
    else
        ws st
        w

and private anonShape (st: St) : (string * Ty) list =
    st.I <- st.I + 2
    ws st
    let mutable fields = []
    let mutable go = true

    while st.Ok && go do
        if matches st "[<" then
            refuse st
        else
            let n = fieldNameDecl st

            if st.Ok then
                if not (strWs st ":") then
                    refuse st
                else
                    let t = tySyn st

                    if st.Ok then
                        fields <- (n, t) :: fields

                        if not (fieldSepP st) then
                            go <- false

    if not st.Ok then
        Unchecked.defaultof<_>
    elif not (strWs st "|}") then
        refuse st
    else
        List.rev fields

// ---- the expression grammar ------------------------------------------------
let rec private seqExpr (st: St) : Expr =
    // elements sequenced by `;` or the sibling sentinel — the separator
    // backs out before a trailing `until`/`always` (the owning forms,
    // none of them parsed here, so that shape ends in a refusal)
    let first = stmtElem st

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let mutable items = [ first ]
        let mutable go = true

        while st.Ok && go do
            let save = st.I

            if strWs st ";" || strWs st sibSepStr then
                if keywordAhead st "until" || keywordAhead st "always" then
                    st.I <- save
                    go <- false
                else
                    let e = stmtElem st

                    if st.Ok then
                        items <- e :: items
            else
                go <- false

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match items with
            | [ one ] -> one
            | _ -> foldSeqExpr (List.rev items)

and private stmtElem (st: St) : Expr =
    if guardedHead st then
        refuse st
    elif not st.ExprParen && couldBeCommand st false then
        refuse st
    else
        let e = commaExpr st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            // a bare `|` after an element: the next arm's boundary inside a
            // match (left for the match to read), a value-headed pipe or the
            // pipe hint anywhere else — both the command grammar's, refused
            if peek st = '|' && peekAt st 1 <> '|' && peekAt st 1 <> '>' then
                if st.InArm > 0 || st.ExprParen then e else refuse st
            else
                e

and private commaExpr (st: St) : Expr =
    let first = expr st

    if not st.Ok then
        Unchecked.defaultof<_>
    elif peek st <> ',' then
        first
    else
        let mutable items = [ first ]

        while st.Ok && peek st = ',' do
            st.I <- st.I + 1
            ws st
            let e = expr st

            if st.Ok then
                items <- e :: items

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let all = List.rev items

            { Kind = ETuple all
              Span = Span.union first.Span (List.last all).Span }

// the operator under the cursor and its precedence, or Unchecked.defaultof<_> — the
// grammar's table: pipes and composition at 1, `||` 2, `&&` 3,
// comparisons 4, additive 6, multiplicative 7, all left-associative
and private opAhead (st: St) : struct (string * int) =
    let c = peek st
    let d = peekAt st 1

    match c with
    | '|' -> if d = '>' then struct ("|>", 1) elif d = '|' then struct ("||", 2) else struct (Unchecked.defaultof<_>, 0)
    | '&' -> if d = '&' then struct ("&&", 3) else struct (Unchecked.defaultof<_>, 0)
    | '=' -> if d = '=' then struct ("==", 4) else struct ("=", 4)
    | '!' -> if d = '=' then struct ("!=", 4) else struct (Unchecked.defaultof<_>, 0)
    | '<' ->
        if d = '>' then struct ("<>", 4)
        elif d = '<' then struct ("<<", 1)
        elif d = '|' then struct ("<|", 1)
        elif d = '=' then struct ("<=", 4)
        else struct ("<", 4)
    | '>' ->
        if d = '>' then struct (">>", 1)
        elif d = '=' then struct (">=", 4)
        else struct (">", 4)
    | '+' -> struct ("+", 6)
    | '-' -> if d = '>' then struct (Unchecked.defaultof<_>, 0) else struct ("-", 6)
    | '*' -> struct ("*", 7)
    | '/' -> struct ("/", 7)
    | '%' -> struct ("%", 7)
    | _ -> struct (Unchecked.defaultof<_>, 0)

and private expr (st: St) : Expr = opp st term 1

// precedence climbing over `termP`, the grammar's OPP with the same
// table — `updateSource` runs it over application chains alone
and private opp (st: St) (termP: St -> Expr) (minPrec: int) : Expr =
    st.Depth <- st.Depth + 1

    if tooDeep st then
        st.Depth <- st.Depth - 1
        refuse st
    else
        let mutable left = termP st
        let mutable go = st.Ok

        while go do
            let struct (op, prec) = opAhead st

            if isNull' op || prec < minPrec then
                go <- false
            else
                st.I <- st.I + op.Length
                ws st
                let right = opp st termP (prec + 1)

                if not st.Ok then
                    go <- false
                else
                    left <- (if op = "|>" then pipeOp left right else binOp op left right)

        st.Depth <- st.Depth - 1
        if st.Ok then left else Unchecked.defaultof<_>

and private term (st: St) : Expr =
    let c = peek st

    if isIdentStart c then
        let w = wordAhead st

        if keywords.Contains w && not (isIdentCont (peekAt st w.Length)) then
            match w with
            | "fun" -> lambda st
            | "let" -> letIn st
            | "if" -> ifExpr st
            | "match" -> matchExpr st
            | "function" -> functionExpr st
            | "try" -> tryForm st
            | "orFail" -> orFailForm st
            | "from" -> fromExpr st
            | "to" -> toExpr st
            | "true"
            | "false" -> appChain st
            | _ -> refuse st
        elif w = "yaml" && (peekAt st 4 = sibSep || peekAt st 4 = ' ') then
            // a district marker (`yaml`, `yaml schema=…` + sentinel) or an
            // ordinary word: the marker face needs the sentinel ahead
            if yamlMarkerAhead st then refuse st else appChain st
        else
            appChain st
    elif c = '<' || (c = '$' && (matches st "$<<<" || matches st "$$<<<")) then
        refuse st
    else
        appChain st

and private yamlMarkerAhead (st: St) : bool =
    let t = st.Text
    let mutable j = st.I + 4

    if j < t.Length && t[j] = sibSep then
        true
    else
        if matches { st with I = j } " patch" then
            j <- j + 6

            if String.CompareOrdinal(t, j, " by=", 0, 4) = 0 then
                j <- j + 4

                while j < t.Length && t[j] <> ' ' && t[j] <> sibSep do
                    j <- j + 1

        if String.CompareOrdinal(t, j, " schema=", 0, 8) = 0 then
            j <- j + 8

            while j < t.Length && (Char.IsLower t[j] || isDigit t[j] || t[j] = '-') do
                j <- j + 1

        j < t.Length && t[j] = sibSep

and private appChain (st: St) : Expr =
    let head = postfixAtom st

    if not st.Ok then
        Unchecked.defaultof<_>
    elif isNull' head then
        refuse st
    else
        // `Env.load {| … |}` takes an anonymous type, not parsed here
        let loadHead =
            match head.Kind with
            | EField({ Kind = EVar("Env" | "Args") }, "load", _) -> true
            | _ -> false

        if loadHead && matches st "{|" then
            refuse st
        else
            let mutable acc = head
            let mutable go = true

            while st.Ok && go do
                let a = postfixAtom st

                if not st.Ok || isNull' a then
                    go <- false
                else
                    acc <-
                        { Kind = EApp(acc, a)
                          Span = Span.union acc.Span a.Span }

            if not st.Ok then
                Unchecked.defaultof<_>
            elif matches st "<<<" || matches st "$<<<" || matches st "$$<<<" then
                refuse st
            else
                acc

// atom + the suffix chain (field, index, slice); Unchecked.defaultof<_> with Ok when no
// atom starts here, Unchecked.defaultof<_> without Ok on a refusal
and private postfixAtom (st: St) : Expr =
    let target = atom st

    if not st.Ok || isNull' target then
        target
    else
        let mutable cur = target
        let mutable go = true

        while st.Ok && go do
            let immediate = st.I = cur.Span.End.Col - 1
            let c = peek st

            if c = '.' then
                if peekAt st 1 = '[' then
                    refuse st
                else
                    let save = st.I
                    st.I <- st.I + 1
                    let w = wordAhead st

                    if isNull' w then
                        st.I <- save
                        go <- false
                    else
                        let name, fspan = spannedWord st
                        ws st

                        cur <-
                            { Kind = EField(cur, name, fspan)
                              Span = Span.union cur.Span fspan }
            elif c = '[' && immediate then
                let next = indexOrSlice st cur

                if isNull' next then go <- false else cur <- next
            else
                go <- false

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match target.Kind with
            | EVar "_" when not (obj.ReferenceEquals(cur, target)) ->
                { Kind = ELambda("_", target.Span, cur)
                  Span = cur.Span }
            | _ -> cur

// `[i]`, `[^i]`, `[lo..hi]` with `^` bounds, glued to the target; a
// shape that is none of these is left for the application chain
// (`f[1;2]` applies f to a list), exactly as the grammar's attempts do
and private indexOrSlice (st: St) (target: Expr) : Expr =
    let save = st.I
    st.I <- st.I + 1
    ws st
    let loCaret = peek st = '^'

    if loCaret then
        st.I <- st.I + 1

    // the first interior expression, parsed once; absent before `..`
    let lo =
        if matches st ".." then
            Unchecked.defaultof<Expr>
        else
            let e = expr st

            if st.Ok then
                e
            elif retry st (if loCaret then save + 1 else save) then
                // no expression here: the chain ends (`f[` + a list, say)
                Unchecked.defaultof<Expr>
            else
                Unchecked.defaultof<Expr>

    if not st.Ok then
        Unchecked.defaultof<_>
    elif strWs st ".." then
        let hiCaret = peek st = '^'

        if hiCaret then
            st.I <- st.I + 1

        let hi =
            if peek st = ']' then
                Unchecked.defaultof<Expr>
            else
                let e = expr st
                if st.Ok then e else Unchecked.defaultof<Expr>

        if not st.Ok then
            if retry st save then Unchecked.defaultof<_> else Unchecked.defaultof<_>
        elif peek st = ']' then
            st.I <- st.I + 1
            let endP = here st
            ws st

            { Kind = ESlice(target, optOf lo, optOf hi, loCaret && not (isNull' lo), hiCaret && not (isNull' hi))
              Span = { Start = target.Span.Start; End = endP } }
        else
            retry st save |> ignore
            Unchecked.defaultof<_>
    elif not (isNull' lo) && peek st = ']' then
        st.I <- st.I + 1
        let endP = here st
        ws st

        if loCaret then
            fromEndIndexDesugar target lo endP
        else
            indexDesugar target lo endP
    else
        retry st save |> ignore
        Unchecked.defaultof<_>

and private atom (st: St) : Expr =
    let c = peek st

    if isDigit c then
        intLit st
    elif c = '"' || (c = '@' && peekAt st 1 = '"') then
        stringAtom st
    elif c = '$' then
        if peekAt st 1 = '"' then interpLit st else refuse st
    elif c = '(' then
        parenAtom st
    elif c = '{' then
        if peekAt st 1 = '|' then anonRecordLit st else recordLit st
    elif c = '[' then
        if peekAt st 1 = '<' then refuse st else listLit st
    elif c = '-' then
        negAtom st
    elif c = '.' then
        if isDigit (peekAt st 1) then refuse st else Unchecked.defaultof<_>
    elif c = '\\' || c = '~' then
        refuse st
    elif c = '!' then
        if isIdentStart (peekAt st 1) then refuse st else Unchecked.defaultof<_>
    elif isIdentStart c then
        let w = wordAhead st

        if keywords.Contains w && w <> "true" && w <> "false" then
            if (w = "try" || w = "orFail") && not (isIdentCont (peekAt st w.Length)) then
                refuse st
            else
                Unchecked.defaultof<_>
        else
            let name, span = spannedWord st
            ws st

            match name with
            | "true" -> { Kind = EBool true; Span = span }
            | "false" -> { Kind = EBool false; Span = span }
            | _ -> { Kind = EVar name; Span = span }
    else
        Unchecked.defaultof<_>

and private negAtom (st: St) : Expr =
    let prev = peekAt st -1
    let next = peekAt st 1

    let prefix =
        not (
            Char.IsLetterOrDigit prev
            || prev = '_'
            || prev = ')'
            || prev = ']'
            || prev = '}'
            || prev = '"'
            || prev = '\''
            || prev = '-'
        )
        && next <> ' '
        && next <> '\t'
        && next <> '>'

    if not prefix then
        Unchecked.defaultof<_>
    else
        let p = at st.I
        st.I <- st.I + 1

        if peek st = '-' then
            refuse st
        elif
            matches st "9223372036854775808"
            && (let a = peekAt st 19 in not (Char.IsLetterOrDigit a || a = '_'))
            && not (peekAt st 19 = '.' && isDigit (peekAt st 20))
        then
            st.I <- st.I + 19
            let e = { Kind = EInt Int64.MinValue; Span = { Start = p; End = here st } }
            ws st
            e
        else
            let e = postfixAtom st

            if not st.Ok then
                Unchecked.defaultof<_>
            elif isNull' e then
                refuse st
            else
                let span = { Start = p; End = e.Span.End }

                match e.Kind with
                | EFloat f -> { Kind = EFloat(if f = 0.0 then 0.0 else -f); Span = span }
                | EDur n -> { Kind = EDur(-n); Span = span }
                | ESize b -> { Kind = ESize(-b); Span = span }
                | _ ->
                    { Kind = EBinOp("-", { Kind = EInt 0L; Span = e.Span }, e)
                      Span = span }

and private parenAtom (st: St) : Expr =
    let start = st.I
    st.I <- st.I + 1
    ws st

    if peek st = ')' then
        st.I <- st.I + 1
        let e = { Kind = EUnit; Span = { Start = at start; End = here st } }
        ws st
        e
    else
        // `(op)`: an operator value, or one of the refused spellings
        let opText =
            let c = peek st
            let d = peekAt st 1

            match c with
            | '>' -> if d = '=' then ">=" elif d = '>' then ">>" else ">"
            | '<' -> if d = '=' then "<=" elif d = '>' then "<>" elif d = '|' then "<|" elif d = '<' then "<<" else "<"
            | '=' -> if d = '=' then "==" else "="
            | '!' -> if d = '=' then "!=" else Unchecked.defaultof<_>
            | '|' -> if d = '>' then "|>" elif d = '|' then "||" else "|"
            | '&' -> if d = '&' then "&&" else Unchecked.defaultof<_>
            | '+' -> "+"
            | '-' -> if d = '>' then Unchecked.defaultof<_> else "-"
            | '*' -> "*"
            | '/' -> "/"
            | '%' -> "%"
            | _ -> Unchecked.defaultof<_>

        let asOp =
            if isNull' opText then
                Unchecked.defaultof<_>
            else
                let save = st.I
                st.I <- st.I + opText.Length
                ws st

                if peek st = ')' then
                    st.I <- st.I + 1

                    match opText with
                    | "&&"
                    | "||"
                    | "|>"
                    | "|"
                    | ">>"
                    | "<<"
                    | "="
                    | "!="
                    | "<|" -> refuse st
                    | op ->
                        let e = { Kind = EOpValue op; Span = { Start = at start; End = here st } }
                        ws st
                        e
                else
                    st.I <- save
                    Unchecked.defaultof<_>

        if not st.Ok then
            Unchecked.defaultof<_>
        elif not (isNull' asOp) then
            asOp
        else
            let savedParen = st.ExprParen
            st.ExprParen <- true
            let inner = seqExpr st
            st.ExprParen <- savedParen

            if not st.Ok then
                Unchecked.defaultof<_>
            elif peek st <> ')' then
                refuse st
            else
                st.I <- st.I + 1
                let e = { inner with Span = { Start = at start; End = here st } }
                ws st
                e

and private fieldAssign (st: St) : string * Span * Expr =
    let n, s = identSpanned st

    if isNull' n then
        refuse st
    elif not (strWs st "=") then
        refuse st
    else
        let v = commaExpr st
        if st.Ok then n, s, v else refuse st

and private recordLit (st: St) : Expr =
    let start = st.I
    st.I <- st.I + 1
    ws st
    let w = wordAhead st

    // the literal commits on `ident =` (not `==`); a keyword there is the
    // grammar's fatal
    let afterWord =
        if isNull' w then
            -1
        else
            let mutable j = st.I + w.Length

            while j < st.Text.Length && (st.Text[j] = ' ' || st.Text[j] = '\t') do
                j <- j + 1

            j

    let isAssign =
        afterWord >= 0
        && afterWord < st.Text.Length
        && st.Text[afterWord] = '='
        && not (afterWord + 1 < st.Text.Length && st.Text[afterWord + 1] = '=')

    if isAssign && keywords.Contains w then
        refuse st
    elif isAssign then
        let mutable fields = []
        let mutable go = true

        while st.Ok && go do
            let f = fieldAssign st

            if st.Ok then
                fields <- f :: fields

                if not (fieldSepP st) then
                    go <- false

        if not st.Ok then
            Unchecked.defaultof<_>
        elif peek st <> '}' then
            refuse st
        else
            st.I <- st.I + 1
            let e = { Kind = ERecord(List.rev fields); Span = { Start = at start; End = here st } }
            ws st
            e
    else
        // copy-and-update: a compound-free source, `with`, paths
        let src = opp st appChain 1

        if not st.Ok then
            Unchecked.defaultof<_>
        elif not (keyword st "with") then
            refuse st
        else
            let mutable ups = []
            let mutable go = true

            while st.Ok && go do
                if matches st "[<" then
                    refuse st
                else
                    let mutable path = []
                    let mutable more = true

                    while st.Ok && more do
                        let seg = wordAhead st

                        if isNull' seg then
                            refuse st
                        else
                            let p = spannedWord st
                            ws st
                            path <- p :: path

                            if peek st = '.' then
                                st.I <- st.I + 1
                                ws st
                            else
                                more <- false

                    if st.Ok then
                        if not (strWs st "=") then
                            refuse st
                        else
                            let v = commaExpr st

                            if st.Ok then
                                ups <- (List.rev path, v) :: ups

                                if not (fieldSepP st) then
                                    go <- false

            if not st.Ok then
                Unchecked.defaultof<_>
            elif peek st <> '}' then
                refuse st
            else
                st.I <- st.I + 1
                let e = { Kind = EUpdate(src, List.rev ups); Span = { Start = at start; End = here st } }
                ws st
                e

and private anonRecordLit (st: St) : Expr =
    let start = st.I
    st.I <- st.I + 2
    ws st

    if matches st "|}" then
        refuse st
    else
        let mutable fields = []
        let mutable go = true

        while st.Ok && go do
            let w = wordAhead st

            if isNull' w then
                refuse st
            else
                // the fences: a keyword field, punning, a type spelling,
                // `with` — every one the grammar's fatal
                let mutable j = st.I + w.Length

                while j < st.Text.Length && (st.Text[j] = ' ' || st.Text[j] = '\t') do
                    j <- j + 1

                let next = if j < st.Text.Length then st.Text[j] else eos

                if keywords.Contains w || next = ';' || next = ':' || (next = '|' && j + 1 < st.Text.Length && st.Text[j + 1] = '}') || String.CompareOrdinal(st.Text, j, "with", 0, 4) = 0 then
                    refuse st
                else
                    let f = fieldAssign st

                    if st.Ok then
                        fields <- f :: fields

                        if not (fieldSepP st) then
                            go <- false

        if not st.Ok then
            Unchecked.defaultof<_>
        elif not (matches st "|}") then
            refuse st
        else
            st.I <- st.I + 2
            let e = { Kind = EAnonRecord(List.rev fields); Span = { Start = at start; End = here st } }
            ws st
            e

// a range endpoint: a negative literal, or an atom with field suffixes
and private rangeTerm (st: St) : Expr =
    if peek st = '-' && isDigit (peekAt st 1) then
        let start = st.I
        st.I <- st.I + 1
        let digits = digitsAhead st
        st.I <- st.I + digits.Length

        match Int64.TryParse("-" + digits) with
        | true, n ->
            let e = { Kind = EInt n; Span = { Start = at start; End = here st } }
            ws st
            e
        | _ -> refuse st
    else
        let a = atom st

        if not st.Ok || isNull' a then
            Unchecked.defaultof<_>
        else
            let mutable cur = a
            let mutable go = true

            while go do
                if peek st = '.' && isIdentStart (peekAt st 1) then
                    st.I <- st.I + 1
                    let name, fspan = spannedWord st
                    ws st

                    cur <-
                        { Kind = EField(cur, name, fspan)
                          Span = Span.union cur.Span fspan }
                else
                    go <- false

            cur

and private listLit (st: St) : Expr =
    let start = st.I
    st.I <- st.I + 1
    ws st

    let asRange =
        if peek st = '[' || peek st = '{' then
            Unchecked.defaultof<_>
        else
            let save = st.I
            let a = rangeTerm st

            if not st.Ok || isNull' a || not (strWs st "..") then
                retry st save |> ignore
                Unchecked.defaultof<_>
            else
                let b = rangeTerm st

                if not st.Ok || isNull' b then
                    refuse st
                else
                    let c =
                        if strWs st ".." then
                            let c = rangeTerm st
                            if not st.Ok || isNull' c then refuse st else c
                        else
                            Unchecked.defaultof<_>

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    elif peek st <> ']' then
                        refuse st
                    else
                        st.I <- st.I + 1
                        let span = { Start = at start; End = here st }
                        ws st

                        let start', step, stop =
                            if isNull' c then
                                a, { Kind = EInt 1L; Span = a.Span }, b
                            else
                                a, b, c

                        match step.Kind with
                        | EInt 0L -> refuse st
                        | _ -> rangeExpr span start' step stop

    if not st.Ok then
        Unchecked.defaultof<_>
    elif not (isNull' asRange) then
        asRange
    elif peek st = ']' then
        st.I <- st.I + 1
        let e = { Kind = EList []; Span = { Start = at start; End = here st } }
        ws st
        e
    else
        let mutable items = []
        let mutable go = true

        while st.Ok && go do
            let e = commaExpr st

            if st.Ok then
                items <- e :: items

                if not (fieldSepP st) then
                    go <- false

        if not st.Ok then
            Unchecked.defaultof<_>
        elif peek st <> ']' then
            refuse st
        else
            st.I <- st.I + 1
            let e = { Kind = EList(List.rev items); Span = { Start = at start; End = here st } }
            ws st
            e

and private interpLit (st: St) : Expr =
    let start = st.I

    if matches st "$\"\"\"" then
        st.I <- st.I + 4
        let t = st.Text
        let mutable parts = []
        let mutable go = true

        while st.Ok && go do
            if st.I >= t.Length then
                refuse st
            elif matches st "\"\"\"" then
                go <- false
            elif matches st "{{" then
                refuse st
            elif t[st.I] = '{' then
                st.I <- st.I + 1
                ws st
                let e = expr st

                if st.Ok then
                    if peek st <> '}' then
                        refuse st
                    else
                        st.I <- st.I + 1
                        parts <- IExpr e :: parts
            elif matches st "\"\"" then
                st.I <- st.I + 2
                parts <- IStr "\"\"" :: parts
            elif t[st.I] = '"' then
                st.I <- st.I + 1
                parts <- IStr "\"" :: parts
            elif t[st.I] = '\n' || t[st.I] = '\r' then
                refuse st
            else
                let mutable j = st.I

                while j < t.Length && t[j] <> '"' && t[j] <> '{' && t[j] <> '\n' && t[j] <> '\r' do
                    j <- j + 1

                parts <- IStr(t.Substring(st.I, j - st.I)) :: parts
                st.I <- j

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            st.I <- st.I + 3
            let e = { Kind = EInterp(List.rev parts); Span = { Start = at start; End = here st } }
            ws st
            e
    else
        st.I <- st.I + 2
        let t = st.Text
        let mutable parts = []
        let mutable go = true

        while st.Ok && go do
            if st.I >= t.Length then
                refuse st
            elif t[st.I] = '"' then
                go <- false
            elif matches st "{{" then
                st.I <- st.I + 2
                parts <- IStr "{" :: parts
            elif matches st "}}" then
                st.I <- st.I + 2
                parts <- IStr "}" :: parts
            elif t[st.I] = '{' then
                st.I <- st.I + 1
                ws st
                let e = expr st

                if st.Ok then
                    if peek st <> '}' then
                        refuse st
                    else
                        st.I <- st.I + 1
                        parts <- IExpr e :: parts
            elif t[st.I] = '}' then
                refuse st
            else
                // a text run: plain characters and escapes, as one part
                let sb = Text.StringBuilder()

                while st.Ok && st.I < t.Length && t[st.I] <> '"' && t[st.I] <> '{' && t[st.I] <> '}' do
                    if t[st.I] = '\\' then
                        escape st sb
                    else
                        let mutable j = st.I

                        while j < t.Length && t[j] <> '"' && t[j] <> '\\' && t[j] <> '{' && t[j] <> '}' do
                            j <- j + 1

                        sb.Append(t, st.I, j - st.I) |> ignore
                        st.I <- j

                if st.Ok then
                    parts <- IStr(sb.ToString()) :: parts

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            st.I <- st.I + 1
            let e = { Kind = EInterp(List.rev parts); Span = { Start = at start; End = here st } }
            ws st
            e

// ---- binders and patterns ----------------------------------------------
and private binderParam (st: St) : Pattern =
    // a plain name or `()`; the pattern forms are left to the grammar
    if matches st "()" then
        let s = st.I
        st.I <- st.I + 2
        let p = { PKind = PUnit; PSpan = { Start = at s; End = here st } }
        ws st
        p
    else
        let c = peek st

        if isIdentStart c then
            let n, span = identSpanned st
            if isNull' n then refuse st else { PKind = PVar n; PSpan = span }
        elif c = '(' then
            st.I <- st.I + 1
            ws st
            let inner = commaPats st

            if not st.Ok then
                Unchecked.defaultof<_>
            elif peek st = ':' && peekAt st 1 <> ':' then
                refuse st
            elif not (strWs st ")") then
                refuse st
            else
                inner
        elif c = '{' then
            patAt st false
        elif c = '[' then
            refuse st
        else
            Unchecked.defaultof<_>

and private binderParams (st: St) : Pattern list =
    let mutable ps = []
    let mutable go = true

    while st.Ok && go do
        let p = binderParam st

        if st.Ok && not (isNull' p) then
            ps <- p :: ps
        else
            go <- false

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let ps = List.rev ps

        // duplicate params are the grammar's fatal
        let names = ps |> List.choose (fun p -> match p.PKind with PVar n -> Some n | _ -> None)

        if List.length names <> (Set.ofList names).Count then
            refuse st
        else
            ps

and private patNames (p: Pattern) : string list =
    match p.PKind with
    | PVar n -> [ n ]
    | PTuple ps
    | PSeqList ps -> ps |> List.collect patNames
    | PRecord fields -> fields |> List.collect (snd >> patNames)
    | PCase(_, Some a, _) -> patNames a
    | PCons(h, t) -> patNames h @ patNames t
    | PRegex(_, _, _, b) -> patNames b
    | _ -> []

and private pat (st: St) : Pattern =
    // cons is the one infix: right-associative over patCore; the nesting
    // bound and the stack probe sit here, where the grammar's deepen does
    st.Depth <- st.Depth + 1

    let first = if tooDeep st then refuse st else patCore st

    st.Depth <- st.Depth - 1

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let mutable items = [ first ]

        while st.Ok && strWs st "::" do
            let p = patCore st

            if st.Ok then
                items <- p :: items

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match items with
            | [ one ] -> one
            | last :: rest ->
                rest
                |> List.fold
                    (fun t h ->
                        { PKind = PCons(h, t)
                          PSpan = { Start = h.PSpan.Start; End = t.PSpan.End } })
                    last
            | [] -> Unchecked.defaultof<_>

and private commaPats (st: St) : Pattern =
    let first = pat st

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let mutable items = [ first ]

        while st.Ok && strWs st "," do
            let p = pat st

            if st.Ok then
                items <- p :: items

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match items with
            | [ one ] -> one
            | _ ->
                let all = List.rev items

                { PKind = PTuple all
                  PSpan = { Start = (List.head all).PSpan.Start; End = (List.last all).PSpan.End } }

and private patString (st: St) : string =
    if matches st "\"\"\"" then
        st.I <- st.I + 3
        tripleBody st
    elif matches st "@\"" then
        st.I <- st.I + 2
        verbatimBody st
    else
        plainStringBody st

// patCore: literals, parens, seq and record forms, words; `withArg`
// distinguishes the arm's core (a constructor takes an argument) from
// the argument position (patAtom)
and private patAt (st: St) (withArg: bool) : Pattern =
    let start = st.I
    let c = peek st

    if matches st "()" then
        st.I <- st.I + 2
        let p = { PKind = PUnit; PSpan = { Start = at start; End = here st } }
        ws st
        p
    elif isDigit c || (c = '-' && isDigit (peekAt st 1)) then
        let neg = c = '-'

        if neg then
            st.I <- st.I + 1

        let digits = digitsAhead st
        st.I <- st.I + digits.Length

        match Int64.TryParse((if neg then "-" else "") + digits) with
        | true, n ->
            let p = { PKind = PInt n; PSpan = { Start = at start; End = here st } }
            ws st
            p
        | _ -> refuse st
    elif c = '"' || (c = '@' && peekAt st 1 = '"') then
        let s = patString st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let p = { PKind = PStr s; PSpan = { Start = at start; End = here st } }
            ws st
            p
    elif c = '(' then
        st.I <- st.I + 1
        ws st
        let inner = commaPats st

        if not st.Ok then
            Unchecked.defaultof<_>
        elif peek st = ':' && peekAt st 1 <> ':' then
            refuse st
        elif not (strWs st ")") then
            refuse st
        else
            inner
    elif c = '[' then
        st.I <- st.I + 1
        ws st

        if peek st = ']' then
            st.I <- st.I + 1
            let p = { PKind = PSeqNil; PSpan = { Start = at start; End = here st } }
            ws st
            p
        else
            let mutable items = []
            let mutable go = true

            while st.Ok && go do
                let p = pat st

                if st.Ok then
                    items <- p :: items

                    if not (fieldSepP st) then
                        go <- false

            if not st.Ok then
                Unchecked.defaultof<_>
            elif peek st <> ']' then
                refuse st
            else
                st.I <- st.I + 1
                let p = { PKind = PSeqList(List.rev items); PSpan = { Start = at start; End = here st } }
                ws st
                p
    elif c = '{' then
        st.I <- st.I + 1
        ws st
        let mutable fields = []
        let mutable go = peek st <> '}'

        while st.Ok && go do
            let n, span = identSpanned st

            if isNull' n then
                refuse st
            elif not (strWs st "=") || peek st = '=' then
                refuse st
            else
                let sub = pat st

                if st.Ok then
                    fields <- ((n, span), sub) :: fields

                    if not (fieldSepP st) then
                        go <- false

        if not st.Ok then
            Unchecked.defaultof<_>
        elif peek st <> '}' then
            refuse st
        else
            st.I <- st.I + 1
            let p = { PKind = PRecord(List.rev fields); PSpan = { Start = at start; End = here st } }
            ws st
            p
    elif isIdentStart c then
        let w = wordAhead st

        if w <> "true" && w <> "false" && keywords.Contains w then
            refuse st
        elif w = "Regex" && withArg then
            // the bespoke Regex pattern: a literal (raw or plain), then the binder
            let name, span = spannedWord st
            ws st
            let litStart = st.I
            let c = peek st

            if c <> '"' && not (c = '@' && peekAt st 1 = '"') then
                refuse st
            else
                let raw = c = '@' || matches st "\"\"\""
                let lit = patString st

                if not st.Ok then
                    Unchecked.defaultof<_>
                else
                    let litSpan = { Start = at litStart; End = here st }
                    ws st
                    let binder = patAt st false

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    elif isNull' binder then
                        refuse st
                    else
                        { PKind = PRegex(lit, litSpan, raw, binder)
                          PSpan = { Start = span.Start; End = binder.PSpan.End } }
        else
            let name, span = spannedWord st
            ws st

            if name = "_" then
                { PKind = PWildcard; PSpan = span }
            elif name = "true" then
                { PKind = PBool true; PSpan = span }
            elif name = "false" then
                { PKind = PBool false; PSpan = span }
            elif Char.IsUpper name[0] then
                // a qualified case `A.Case`, then (in an arm's core) an argument
                let qual =
                    if peek st = '.' && isIdentStart (peekAt st 1) then
                        let save = st.I
                        st.I <- st.I + 1
                        let cw = wordAhead st

                        if keywords.Contains cw || not (Char.IsUpper cw[0]) then
                            st.I <- save
                            None
                        else
                            let cn, cs = spannedWord st
                            ws st
                            Some(cn, cs)
                    else
                        None

                let ctor, qualifier, headEnd =
                    match qual with
                    | Some(cn, cs) -> cn, Some(name, span), cs.End
                    | None -> name, None, span.End

                if withArg then
                    let arg = patAt st false

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    else
                        let e =
                            if isNull' arg then headEnd else arg.PSpan.End

                        { PKind = PCase(ctor, optOf arg, qualifier)
                          PSpan = { Start = span.Start; End = e } }
                else
                    { PKind = PCase(ctor, None, qualifier)
                      PSpan = { Start = span.Start; End = headEnd } }
            else
                { PKind = PVar name; PSpan = span }
    else
        Unchecked.defaultof<_>

and private patCore (st: St) : Pattern =
    let p = patAt st true
    if st.Ok && isNull' p then refuse st else p

// ---- the block forms ---------------------------------------------------
and private withLocals (st: St) (names: string list) (body: St -> Expr) : Expr =
    let saved = st.Locals

    for n in names do
        st.Locals <- Set.add n st.Locals

    let e = body st
    st.Locals <- saved
    e

and private bodyExpr (st: St) : Expr =
    // a statement body: expression territory ends, lets may take commands
    let saved = st.ExprParen
    st.ExprParen <- false
    let e = seqExpr st
    st.ExprParen <- saved
    e

and private lambda (st: St) : Expr =
    let p = here st
    keyword st "fun" |> ignore
    let ps = binderParams st

    if not st.Ok then
        Unchecked.defaultof<_>
    elif List.isEmpty ps then
        refuse st
    elif not (strWs st "->") then
        refuse st
    else
        let names = ps |> List.collect patNames
        let body = withLocals st names bodyExpr

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let inner = curryParams ps body

            { inner with
                Span = { Start = p; End = body.Span.End } }

and private isPlainBinder (p: Pattern) =
    match p.PKind with
    | PVar _
    | PCase(_, None, None) -> true
    | _ -> false

and private letIn (st: St) : Expr =
    let p = here st
    keyword st "let" |> ignore

    // the pattern form `let (a, b) = v in body`: tried first, as the
    // grammar does; a plain binder takes the ident path
    let save = st.I
    let binder = commaPats st

    if st.Ok && not (isNull' binder) && not (isPlainBinder binder) && peek st = '=' && peekAt st 1 <> '=' then
        st.I <- st.I + 1
        ws st
        let value = seqExpr st

        if not st.Ok then
            Unchecked.defaultof<_>
        elif peek st = '|' && peekAt st 1 <> '|' && peekAt st 1 <> '>' then
            refuse st
        elif not (keyword st "in") then
            refuse st
        else
            let body = withLocals st (patNames binder) seqExpr

            if not st.Ok then
                Unchecked.defaultof<_>
            else
                { Kind = ELetPat(binder, value, body)
                  Span = { Start = p; End = body.Span.End } }
    elif not (retry st save) then
        Unchecked.defaultof<_>
    else
    let w = wordAhead st

    if isNull' w || keywords.Contains w then
        refuse st
    else
        // `spanned ident`: the grammar's name span runs through the trailing ws
        let name, nameSpan0 = identSpanned st
        let nameSpan = { nameSpan0 with End = here st }

        // a pattern binder (`let x, y =`, `let (a, b) =`) is the grammar's
        if peek st = ',' || matches st "::" then
            refuse st
        else
            let ps = binderParams st

            if not st.Ok then
                Unchecked.defaultof<_>
            elif not (strWs st "=") then
                refuse st
            elif st.I >= st.Text.Length then
                refuse st
            else
                let names = ps |> List.collect patNames
                let value = withLocals st names (fun st -> if couldBeCommand st false then refuse st else seqExpr st)

                if not st.Ok then
                    Unchecked.defaultof<_>
                elif peek st = '|' && peekAt st 1 <> '|' && peekAt st 1 <> '>' then
                    refuse st
                elif not (keyword st "in") then
                    refuse st
                else
                    let body = withLocals st [ name ] bodyExpr

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    else
                        { Kind = ELet(name, nameSpan, curryParams ps value, body)
                          Span = { Start = p; End = body.Span.End } }

and private ifCond (st: St) : Expr =
    if couldBeCommand st false then refuse st else expr st

and private ifExpr (st: St) : Expr =
    let p = here st
    keyword st "if" |> ignore
    let cond = ifCond st

    if not st.Ok then
        Unchecked.defaultof<_>
    elif not (keyword st "then") then
        refuse st
    else
        let thn = bodyExpr st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let mutable elifs = []

            while st.Ok && keyword st "elif" do
                let c = ifCond st

                if st.Ok then
                    if not (keyword st "then") then
                        refuse st
                    else
                        let t = bodyExpr st

                        if st.Ok then
                            elifs <- (c, t) :: elifs

            if not st.Ok then
                Unchecked.defaultof<_>
            else
                let els =
                    if keyword st "else" then
                        let e = bodyExpr st
                        if st.Ok then Some e else None
                    else
                        None

                if not st.Ok then
                    Unchecked.defaultof<_>
                else
                    let chained =
                        (els, elifs)
                        ||> List.fold (fun inner (c: Expr, t: Expr) ->
                            let e = (inner |> Option.defaultValue t).Span.End

                            Some
                                { Kind = EIf(c, t, inner)
                                  Span = { Start = c.Span.Start; End = e } })

                    let endPos = (chained |> Option.defaultValue thn).Span.End

                    { Kind = EIf(cond, thn, chained)
                      Span = { Start = p; End = endPos } }

// one arm, as the grammar desugars it: an or-pattern is one arm per
// alternative sharing the guard and body, binder-free, at most 64
and private matchArm (st: St) : (Pattern * Expr option * Expr) list =
    let p = commaPats st

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let mutable alts = []

        while st.Ok && peek st = '|' && peekAt st 1 <> '|' && peekAt st 1 <> '>' do
            st.I <- st.I + 1
            ws st
            let a = commaPats st

            if st.Ok then
                alts <- a :: alts

        let alts = List.rev alts

        if not st.Ok then
            Unchecked.defaultof<_>
        elif List.length alts >= 64 then
            refuse st
        elif not alts.IsEmpty && (p :: alts |> List.exists (fun a -> not (List.isEmpty (patNames a)))) then
            refuse st
        else
            let names = patNames p

            let guard =
                if keyword st "when" then
                    let g = withLocals st names expr
                    if st.Ok then Some g else None
                else
                    None

            if not st.Ok then
                Unchecked.defaultof<_>
            elif not (strWs st "->") then
                refuse st
            else
                st.InArm <- st.InArm + 1
                let body = withLocals st names bodyExpr
                st.InArm <- st.InArm - 1

                if st.Ok then
                    (p :: alts) |> List.map (fun pat -> pat, guard, body)
                else
                    Unchecked.defaultof<_>

and private matchArms (st: St) : (Pattern * Expr option * Expr) list =
    strWs st "|" |> ignore
    let mutable arms = []
    let mutable go = true

    while st.Ok && go do
        let arm = matchArm st

        if st.Ok then
            arms <- arm :: arms

            if not (strWs st "|") then
                go <- false

    if st.Ok then List.concat (List.rev arms) else Unchecked.defaultof<_>

and private functionExpr (st: St) : Expr =
    let p = here st
    keyword st "function" |> ignore
    let arms = matchArms st

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let _, _, lastBody = List.last arms
        let kwSpan = { Start = p; End = { Line = p.Line; Col = p.Col + 8 } }
        let span = { Start = p; End = lastBody.Span.End }
        let scrut = { Kind = EVar "|function"; Span = kwSpan }

        { Kind = ELambda("|function", kwSpan, { Kind = EMatch(scrut, arms); Span = span })
          Span = span }

and private matchExpr (st: St) : Expr =
    let p = here st
    keyword st "match" |> ignore
    let scrut = commaExpr st

    if not st.Ok then
        Unchecked.defaultof<_>
    elif not (keyword st "with") then
        refuse st
    else
        let arms = matchArms st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let _, _, lastBody = List.last arms

            { Kind = EMatch(scrut, arms)
              Span = { Start = p; End = lastBody.Span.End } }

// the adapter slot's name: `Name` or `A.Name`, with optional `<args>`
and private slotShape (st: St) (w: string) : FromShape =
    let name =
        if peek st = '.' && isIdentStart (peekAt st 1) then
            let save = st.I
            st.I <- st.I + 1
            let m, _ = identSpanned st

            if isNull' m then
                st.I <- save
                w
            else
                w + "." + m
        else
            w

    if peek st = '<' then
        let save = st.I
        st.I <- st.I + 1
        ws st
        let mutable args = []
        let mutable go = true

        while st.Ok && go do
            let t = tySyn st

            if st.Ok then
                args <- t :: args

                if not (strWs st ",") then
                    go <- false

        if st.Ok && strWs st ">" then
            FromApp(name, List.rev args)
        elif retry st save then
            FromName name
        else
            Unchecked.defaultof<_>
    else
        FromName name

and private fromExpr (st: St) : Expr =
    let p = here st
    keyword st "from" |> ignore
    let fmt, _ = identSpanned st

    if isNull' fmt then
        refuse st
    else
        let streamW =
            if matches st "stream" && not (isIdentCont (peekAt st 6)) then
                st.I <- st.I + 6
                ws st
                true
            else
                false

        let arg: (FromShape * bool) option =
            if matches st "{|" then
                let fs = anonShape st
                if st.Ok then Some(FromAnon fs, false) else None
            else
                let w = wordAhead st

                if isNull' w || keywords.Contains w then
                    None
                elif w = "as" then
                    refuse st
                elif w = "Map" then
                    st.I <- st.I + 3
                    ws st

                    if not (strWs st "<") then
                        refuse st
                    else
                        let k = tySyn st

                        if not st.Ok || not (strWs st ",") then
                            refuse st
                        else
                            let inner =
                                if matches st "{|" then
                                    let fs = anonShape st
                                    if st.Ok then FromAnon fs else Unchecked.defaultof<_>
                                else
                                    let t = tySyn st
                                    if st.Ok then FromTy t else Unchecked.defaultof<_>

                            if st.Ok && strWs st ">" then Some(FromMap(k, inner), false) else refuse st
                elif w = "seq" then
                    st.I <- st.I + 3
                    ws st

                    if not (strWs st "<") then
                        refuse st
                    else
                        let sh =
                            if matches st "{|" then
                                let fs = anonShape st
                                if st.Ok then FromAnon fs else Unchecked.defaultof<_>
                            else
                                let inner, _ = identSpanned st

                                if isNull' inner || not (Char.IsUpper inner[0]) then
                                    refuse st
                                else
                                    slotShape st inner

                        if st.Ok && strWs st ">" then Some(sh, true) else refuse st
                elif Char.IsUpper w[0] then
                    let name, _ = identSpanned st
                    let sh = slotShape st name
                    if st.Ok then Some(sh, false) else None
                else
                    None

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let shape, seqOf =
                match arg with
                | Some(sh, s) -> Some sh, s
                | None -> None, false

            { Kind = EFrom(fmt, shape, seqOf, streamW)
              Span = { Start = p; End = here st } }

and private toExpr (st: St) : Expr =
    let p = here st
    keyword st "to" |> ignore
    let fmt, _ = identSpanned st

    if isNull' fmt then
        refuse st
    else
        let streamW =
            if matches st "stream" && not (isIdentCont (peekAt st 6)) then
                st.I <- st.I + 6
                ws st
                true
            else
                false

        { Kind = ETo(fmt, streamW)
          Span = { Start = p; End = here st } }

and private tryForm (st: St) : Expr =
    let s = st.I
    keyword st "try" |> ignore

    let asResult =
        if matches st "result" && not (isIdentCont (peekAt st 6)) then
            st.I <- st.I + 6
            ws st
            true
        else
            false

    { Kind = ETryForm asResult
      Span = { Start = at s; End = here st } }

and private orFailForm (st: St) : Expr =
    let s = st.I
    keyword st "orFail" |> ignore
    let save = st.I
    let msg = postfixAtom st

    if not st.Ok then
        if retry st save then
            { Kind = EOrFailForm None
              Span = { Start = at s; End = here st } }
        else
            Unchecked.defaultof<_>
    else
        { Kind = EOrFailForm(optOf msg)
          Span = { Start = at s; End = here st } }

let private attrArg (st: St) : AttrArg option =
    let c = peek st

    if c = '"' then
        let s = plainStringBody st
        if st.Ok then Some(AStr s) else None
    elif isDigit c || (c = '-' && isDigit (peekAt st 1)) then
        let neg = c = '-'

        if neg then
            st.I <- st.I + 1

        let digits = digitsAhead st
        st.I <- st.I + digits.Length
        let text = (if neg then "-" else "") + digits

        let mutable unit: string = null
        let mutable k = 0

        while isNull' unit && k < fracSuffixes.Length do
            let u = fracSuffixes[k]

            if matches st u then
                let after = peekAt st u.Length

                if Char.IsLetterOrDigit after || after = '_' then
                    k <- fracSuffixes.Length
                else
                    unit <- u
                    st.I <- st.I + u.Length

            k <- k + 1

        let frac =
            if peek st = '.' && isDigit (peekAt st 1) then
                st.I <- st.I + 1
                let f = digitsAhead st
                st.I <- st.I + f.Length
                f
            else
                Unchecked.defaultof<_>

        if not (isNull' frac) then
            let v = Double.Parse($"{text}.{frac}", Globalization.CultureInfo.InvariantCulture)
            Some(AFloat(if v = 0.0 then 0.0 else v))
        else
            match Int64.TryParse text with
            | false, _ -> refuse st
            | true, n ->
                let scaled factor =
                    if factor <> 0L && (n > Int64.MaxValue / factor || n < -(Int64.MaxValue / factor)) then
                        refuse st
                    else
                        n * factor

                match unit with
                | null -> Some(AInt n)
                | "ms" -> Some(ADur n)
                | "s" -> let v = scaled 1000L in if st.Ok then Some(ADur v) else None
                | "m" -> let v = scaled 60000L in if st.Ok then Some(ADur v) else None
                | "h" -> let v = scaled 3600000L in if st.Ok then Some(ADur v) else None
                | "B" -> Some(ASize n)
                | "KiB" -> let v = scaled 1024L in if st.Ok then Some(ASize v) else None
                | "MiB" -> let v = scaled (1024L * 1024L) in if st.Ok then Some(ASize v) else None
                | "GiB" -> let v = scaled (1024L * 1024L * 1024L) in if st.Ok then Some(ASize v) else None
                | _ -> let v = scaled (1024L * 1024L * 1024L * 1024L) in if st.Ok then Some(ASize v) else None
    elif keywordAhead st "true" then
        st.I <- st.I + 4
        Some(ABool true)
    elif keywordAhead st "false" then
        st.I <- st.I + 5
        Some(ABool false)
    else
        None

// `[<Name arg; …>]`: one attribute list, then ws
let private attrList (st: St) : AttrSpec list =
    st.I <- st.I + 2
    ws st
    let mutable specs = []
    let mutable go = true

    while st.Ok && go do
        let s = st.I
        let name, _ = identSpanned st

        if isNull' name then
            refuse st
        else
            let arg = attrArg st

            if st.Ok then
                if arg.IsSome then
                    ws st

                specs <- { AName = name; AArg = arg; ASpan = { Start = at s; End = here st } } :: specs

                if not (fieldSepP st) then
                    go <- false

    if not st.Ok then
        Unchecked.defaultof<_>
    elif not (strWs st ">]") then
        refuse st
    else
        List.rev specs

let private attrLists (st: St) : AttrSpec list =
    let mutable all = []

    while st.Ok && matches st "[<" do
        let l = attrList st

        if st.Ok then
            all <- all @ l

    if st.Ok then all else Unchecked.defaultof<_>

let private typeDecl (st: St) : Stmt =
    let p = here st
    let attrs = attrLists st

    if not st.Ok then
        Unchecked.defaultof<_>
    elif not (keyword st "type") then
        refuse st
    else
        let name, _ = identSpanned st

        if isNull' name then
            refuse st
        else
            // type parameters, each declared once
            let mutable tps = []

            if strWs st "<" then
                let mutable go = true

                while st.Ok && go do
                    if peek st <> '\'' then
                        refuse st
                    else
                        st.I <- st.I + 1
                        let w = takeWord st

                        if isNull' w then
                            refuse st
                        else
                            ws st

                            if List.contains w tps then
                                refuse st
                            else
                                tps <- w :: tps

                                if not (strWs st ",") then
                                    go <- false

                if st.Ok && not (strWs st ">") then
                    refuse st

            if not st.Ok then
                Unchecked.defaultof<_>
            elif not (strWs st "=") then
                refuse st
            else
                let body =
                    if peek st = '{' then
                        st.I <- st.I + 1
                        ws st
                        let mutable fields = []
                        let mutable go = true

                        while st.Ok && go do
                            let fattrs = attrLists st

                            if st.Ok then
                                let n = fieldNameDecl st

                                if st.Ok then
                                    if not (strWs st ":") then
                                        refuse st
                                    else
                                        let t = tySyn st

                                        if st.Ok then
                                            fields <- (n, t, fattrs) :: fields

                                            if not (fieldSepP st) then
                                                go <- false

                        if st.Ok && strWs st "}" then
                            DRecord(List.rev fields)
                        else
                            refuse st
                    else
                        strWs st "|" |> ignore
                        let mutable cases = []
                        let mutable go = true

                        while st.Ok && go do
                            let cattrs = attrLists st

                            if st.Ok then
                                let w = wordAhead st

                                if isNull' w || keywords.Contains w || not (Char.IsUpper w[0]) then
                                    refuse st
                                else
                                    st.I <- st.I + w.Length
                                    ws st

                                    let ty =
                                        if keyword st "of" then
                                            let t = tySyn st
                                            if st.Ok then Some t else None
                                        else
                                            None

                                    if st.Ok then
                                        cases <- (w, ty, cattrs) :: cases

                                        if not (strWs st "|") then
                                            go <- false

                        if st.Ok then DUnion(List.rev cases) else refuse st

                if not st.Ok then
                    Unchecked.defaultof<_>
                elif matches st "validate" || st.I <> st.Text.Length then
                    refuse st
                else
                    SType
                        { Name = name
                          Params = List.rev tps
                          Body = body
                          Attrs = attrs
                          Validate = None
                          Span = { Start = p; End = here st } }

let private moduleDecl (st: St) : Stmt =
    let s = st.I
    st.I <- st.I + 6
    let kwSpan = { Start = at s; End = here st }
    ws st

    if st.I >= st.Text.Length then
        SModule(None, kwSpan)
    else
        let w = takeWord st

        if isNull' w || not (Char.IsUpper w[0]) then
            refuse st
        else
            ws st
            if st.I = st.Text.Length then SModule(Some w, kwSpan) else refuse st

let private importDecl (st: St) : Stmt =
    keyword st "import" |> ignore

    if peek st <> '"' then
        refuse st
    else
        let s = st.I
        let path = plainStringBody st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let pathSpan = { Start = at s; End = here st }
            ws st

            let alias =
                if keyword st "as" then
                    let a = st.I
                    let w = takeWord st

                    if isNull' w || not (Char.IsUpper w[0]) then
                        refuse st
                    else
                        let sp = { Start = at a; End = here st }
                        ws st
                        Some(w, sp)
                else
                    None

            if not st.Ok then
                Unchecked.defaultof<_>
            elif st.I <> st.Text.Length then
                refuse st
            else
                SImport(path, pathSpan, alias)

let private letStmt (st: St) : Stmt =
    keyword st "let" |> ignore

    // the destructuring statement `let x, y = v`: the grammar's first
    // let alternative; a plain binder falls through
    let save = st.I
    let binder = commaPats st

    if st.Ok && not (isNull' binder) && not (isPlainBinder binder) && peek st = '=' && peekAt st 1 <> '=' then
        st.I <- st.I + 1
        ws st

        if couldBeCommand st false then
            refuse st
        else
            let value = seqExpr st

            if not st.Ok then
                Unchecked.defaultof<_>
            elif st.I <> st.Text.Length then
                refuse st
            else
                SLetPat(binder, value)
    elif not (retry st save) then
        Unchecked.defaultof<_>
    else

    let pureMod =
        if keywordAhead st "pure" then
            let save = st.I
            st.I <- st.I + 4
            ws st

            if isNull' (wordAhead st) then
                st.I <- save
                false
            else
                true
        else
            false

    let w = wordAhead st

    if isNull' w || keywords.Contains w then
        refuse st
    else
        let name, nameSpan = identSpanned st

        if peek st = ':' && peekAt st 1 <> ':' then
            // the signature form `let name : ty`
            if pureMod then
                refuse st
            else
                st.I <- st.I + 1
                ws st
                let ty = tySyn st

                if not st.Ok then
                    Unchecked.defaultof<_>
                elif st.I <> st.Text.Length then
                    refuse st
                else
                    SSig(name, ty, nameSpan)
        else
            let ps = binderParams st

            if not st.Ok then
                Unchecked.defaultof<_>
            elif not (strWs st "=") then
                refuse st
            else
                let names = ps |> List.collect patNames
                let rhs = withLocals st names (fun st -> if couldBeCommand st false then refuse st else seqExpr st)

                if not st.Ok then
                    Unchecked.defaultof<_>
                elif st.I <> st.Text.Length then
                    refuse st
                else
                    let curried = curryParams ps rhs

                    let value =
                        if pureMod then
                            { Kind = EWithin(WithinPure, None, None, None, curried)
                              Span = curried.Span }
                        else
                            curried

                    SLet(name, value)

let private statement (st: St) : Stmt =
    ws st

    if keywordAhead st "module" then
        moduleDecl st
    elif keywordAhead st "import" then
        importDecl st
    elif matches st "[<" || keywordAhead st "type" then
        typeDecl st
    elif keywordAhead st "let" then
        letStmt st
    elif guardedHead st || couldBeCommand st true then
        refuse st
    else
        let e = seqExpr st

        if not st.Ok then
            Unchecked.defaultof<_>
        elif st.I <> st.Text.Length then
            refuse st
        else
            SExpr e

// ---- the entry and its counters --------------------------------------------
let mutable private taken = 0L
let mutable private refused = 0L

/// (statements the fast parser took, statements it refused)
let stats () = Threading.Interlocked.Read &taken, Threading.Interlocked.Read &refused

/// The statement's tree, when every piece of it is within this parser's
/// subset and the command grammar could claim none of it; None otherwise,
/// with no trace left (pending anonymous shapes pushed on the way are
/// taken back) — the grammar then parses it from scratch.
let private parse (r: Resolver) (input: string) : Stmt * St =
    if input.IndexOfAny [| '\n'; '\r'; districtClose |] >= 0 then
        Threading.Interlocked.Increment &refused |> ignore

        Unchecked.defaultof<Stmt>,
        { Text = input
          R = r
          I = 0
          Ok = false
          Fatal = false
          Depth = 0
          Locals = Set.empty
          InArm = 0
          ExprParen = false
          RefusedAt = 0
          PendingAt = 0 }
    else
        let pending = pendingAnonDefs.Value

        let st =
            { Text = input
              R = r
              I = 0
              Ok = true
              Fatal = false
              Depth = 0
              Locals = Set.empty
              InArm = 0
              ExprParen = false
              RefusedAt = -1
              PendingAt = pending.Count }

        let s = statement st

        if st.Ok then
            Threading.Interlocked.Increment &taken |> ignore
        else
            if pending.Count > st.PendingAt then
                pending.RemoveRange(st.PendingAt, pending.Count - st.PendingAt)

            // a statement the fast parser refuses must leave no trace
            // (a refusal mid-way may have read past its own tree): the
            // flag says so, the tree is garbage
            Threading.Interlocked.Increment &refused |> ignore

        s, st

let tryStmt (r: Resolver) (input: string) : Stmt option =
    let s, st = parse r input
    if st.Ok then Some s else None

/// the fast parser's verdict with the refusal position — the coverage
/// work's instrument, never the pipeline's
let tryStmtDebug (r: Resolver) (input: string) : Result<Stmt, int> =
    let s, st = parse r input
    if st.Ok then Result.Ok s else Result.Error st.RefusedAt
