module Weir.Fast

// The hand-written happy-path parser [D:hand-parser]: a scanner-driven
// recursive descent over one logical line that builds exactly the tree
// Parser.fs's grammar would build, or refuses. It never reports: every
// construct it does not know, every command shape outside its subset,
// every doubt is a refusal, and the FParsec grammar then parses
// the statement as the sole source of diagnostics. Refusal is a flag,
// not an exception — it is the common case on real code and must cost
// nothing but the characters read.

open System
open Weir.Ast
open Weir.Types
open Weir.Desugar

type private St = {
    Text: string
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
    // the names that mask a command-head alias [D:command-head-alias]: the
    // grammar's aliasMasked — every scope's names but a top-level let's params
    mutable AliasMasked: Set<string>
    mutable InArm: int
    mutable ExprParen: bool
    // the grammar's statement-context flags: letCmdOk and its source
    // (letCmdStmt), ifCondOk, ifBodyOk — they pick a command line's stop
    // words and where its reifier may end
    mutable LetCmd: bool
    mutable LetCmdStmt: bool
    mutable IfCond: bool
    mutable IfBody: bool
    // the last stmtElem was a command chain (armSeq's Choice1)
    mutable LastChain: bool
    // where the first refusal happened — the coverage work's one instrument
    mutable RefusedAt: int
    PendingAt: int
    // the word at WordAt, so the lookaheads a position gets share one string
    mutable WordAt: int
    mutable Word: string
}

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

let inline private isNull' (e: 'a) =
    obj.ReferenceEquals(e, Unchecked.defaultof<_>)

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

    if st.WordAt = st.I then
        st.Word
    elif st.I < t.Length && isIdentStart t[st.I] then
        let mutable j = st.I + 1

        while j < t.Length && isIdentCont t[j] do
            j <- j + 1

        let w = t.Substring(st.I, j - st.I)
        st.WordAt <- st.I
        st.Word <- w
        w
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
// its head decides against the resolver in this exact order (headOf), and
// a command line follows the verdict.
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

// a head word glued to a yaml marker's modifier face (` patch [by=…]
// [schema=…]` or ` schema=…`, then the sentinel): litHeadWord's
// notFollowedBy, so the grammar never resolves it
let private markerFaceAt (t: string) (j0: int) : bool =
    let schemaChar (c: char) =
        Char.IsLower c || Char.IsDigit c || c = '-'

    let mutable j = j0

    let run (pred: char -> bool) =
        let s = j

        while j < t.Length && pred t[j] do
            j <- j + 1

        j > s

    if String.CompareOrdinal(t, j, " patch", 0, 6) = 0 then
        j <- j + 6
        let mutable ok = true

        if String.CompareOrdinal(t, j, " by=", 0, 4) = 0 then
            j <- j + 4
            ok <- run (fun c -> c <> ' ' && c <> sibSep)

        if ok && String.CompareOrdinal(t, j, " schema=", 0, 8) = 0 then
            j <- j + 8
            ok <- run schemaChar

        ok && j < t.Length && t[j] = sibSep
    elif String.CompareOrdinal(t, j, " schema=", 0, 8) = 0 then
        j <- j + 8
        run schemaChar && j < t.Length && t[j] = sibSep
    else
        false

// `ident=` glued, never `==`: an env prefix (the grammar's envAssign)
let private envPrefixAhead (st: St) : bool =
    let t = st.Text

    if not (isIdentStart (peek st)) then
        false
    else
        let w = wordAhead st
        let j = st.I + w.Length
        j < t.Length && t[j] = '=' && not (j + 1 < t.Length && t[j + 1] = '=')

type private HeadK =
    | NotCmd = 0
    | Refuse = 1
    | External = 2
    | Builtin = 3
    | Aliased = 4
    | Tilde = 5
    | Dynamic = 6
    // `NAME=value` words first: the head follows them
    | Env = 7

// the head verdict: the kind, the word, and where the word ends
[<Struct>]
type private Head = { K: HeadK; Word: string; End: int }

let private notCmd = {
    K = HeadK.NotCmd
    Word = null
    End = 0
}

let private refuseHead = {
    K = HeadK.Refuse
    Word = null
    End = 0
}

// litHead's decision against the resolver, in its exact order; Refuse
// where the grammar would raise or the shape is not this parser's
let private headOf (st: St) (builtinHeads: bool) : Head =
    let t = st.Text
    let c = peek st

    if envPrefixAhead st then
        { K = HeadK.Env; Word = null; End = 0 }
    // the def teaching fires ahead of every command line's head
    // [D:def-shape]: its shape is the grammar's to judge
    elif c = '\\' || (matches st "def" && (peekAt st 3 = ' ' || peekAt st 3 = '\t')) then
        refuseHead
    elif c = '^' then
        if peekAt st 1 = '$' then
            let d = peekAt st 2

            if d = '(' || isIdentStart d then
                {
                    K = HeadK.Dynamic
                    Word = null
                    End = 0
                }
            else
                refuseHead
        else
            let mutable j = st.I + 1

            while j < t.Length && j - st.I - 1 < 1024 && cmdWordChar t[j] do
                j <- j + 1

            if
                j = st.I + 1
                || j - st.I - 1 >= 1024
                || (j < t.Length && t[j] = sibSep)
                || markerFaceAt t j
            then
                refuseHead
            else
                let w = t.Substring(st.I + 1, j - st.I - 1)

                if w[0] = '[' || w.StartsWith "~/" then
                    refuseHead
                elif st.R.IsExternal w then
                    {
                        K = HeadK.External
                        Word = w
                        End = j
                    }
                else
                    refuseHead
    elif not (cmdWordChar c) || c = eos then
        notCmd
    else
        let mutable j = st.I

        while j < t.Length && j - st.I < 1024 && cmdWordChar t[j] do
            j <- j + 1

        if j < t.Length && t[j] = sibSep then
            notCmd
        elif markerFaceAt t j then
            refuseHead
        else
            let w = t.Substring(st.I, j - st.I)

            let k =
                if w[0] = '[' then
                    HeadK.NotCmd
                elif w.StartsWith "~/" then
                    HeadK.Tilde
                elif isIdentLike w && not (st.AliasMasked.Contains w) && (st.R.AliasHead w).IsSome then
                    HeadK.Aliased
                elif builtinHeads && isIdentLike w && st.R.IsCommandCallable w then
                    HeadK.Builtin
                elif isIdentLike w && (keywords.Contains w || isKnown st w) then
                    HeadK.NotCmd
                elif st.R.IsExternal w then
                    HeadK.External
                else
                    HeadK.NotCmd

            if k = HeadK.NotCmd then notCmd
            // the bounded head scan stopped mid-word, or a yaml marker word
            elif j - st.I >= 1024 || w = "yaml" then refuseHead
            else { K = k; Word = w; End = j }

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
let private unitSuffixes = [|
    "KiB"
    "MiB"
    "GiB"
    "TiB"
    "KB"
    "MB"
    "GB"
    "TB"
    "ms"
    "B"
    "s"
    "m"
    "h"
    "d"
|]

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

            let sign =
                if peek st = '+' || peek st = '-' then
                    string (peek st)
                else
                    ""

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
                        | true, n when n <= Int64.MaxValue / factor ->
                            if isSize then ESize(n * factor) else EDur(n * factor)
                        | _ -> refuse st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let e = {
                Kind = kind
                Span = { Start = at start; End = here st }
            }

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
        let e = {
            Kind = EStr body
            Span = { Start = at start; End = here st }
        }

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
        | "string" ->
            ws st
            TStr
        | "bool" ->
            ws st
            TBool
        | "unit" ->
            ws st
            TUnit
        | "Duration" ->
            ws st
            TDur
        | "Instant" ->
            ws st
            TInstant
        | "Uuid" ->
            ws st
            TUuid
        | "float" ->
            ws st
            TFloat
        | "Size" ->
            ws st
            TSize
        | "Bytes" ->
            ws st
            TBytes
        | "Secret" ->
            ws st
            TSecret
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

    if not st.Ok then Unchecked.defaultof<_>
    elif not (strWs st "|}") then refuse st
    else List.rev fields

// ---- the command grammar's lexical half [D:hand-parser] -------------------
// argv stop words, as cmdArgStops takes them
[<Literal>]
let private StopIn = 1

[<Literal>]
let private StopThen = 2

[<Literal>]
let private StopElse = 4

// a command-word char at offset k (end of input is none — the grammar's
// notFollowedBy succeeds there)
let private wordCharAt (st: St) (k: int) =
    let j = st.I + k
    j < st.Text.Length && cmdWordChar st.Text[j]

// `pstring w .>> notFollowedBy (satisfy cmdWordChar)`
let private stopWordAt (st: St) (w: string) =
    matches st w && not (wordCharAt st w.Length)

// reifierEnd: what may follow a reifier marker
let private reifierEndAhead (st: St) : bool =
    let c = peek st

    st.I >= st.Text.Length
    || c = '|'
    || c = ')'
    || c = ';'
    || c = sibSep
    || (st.LetCmd && stopWordAt st "in")
    || (st.IfCond && stopWordAt st "then")
    || (st.IfBody && (stopWordAt st "else" || stopWordAt st "elif"))

// `$(` or `$name(` — sigilOpen's shape
let private captureAhead (st: St) : bool =
    let t = st.Text

    if peek st <> '$' then
        false
    elif peekAt st 1 = '(' then
        true
    elif isIdentStart (peekAt st 1) then
        let mutable j = st.I + 2

        while j < t.Length && isIdentCont t[j] do
            j <- j + 1

        j < t.Length && t[j] = '('
    else
        false

// splicePath: the sigil, a name, its `.field` path; no trailing ws
let private splicePathAt (st: St) (sigilLen: int) : Expr =
    let s = st.I
    st.I <- st.I + sigilLen
    let w = takeWord st

    if isNull' w then
        refuse st
    else
        let sp = { Start = at s; End = here st }
        let mutable e = { Kind = EVar w; Span = sp }

        while peek st = '.' && isIdentStart (peekAt st 1) do
            st.I <- st.I + 1
            let fs = st.I
            let f = takeWord st
            let fsp = { Start = at fs; End = here st }

            e <- {
                Kind = EField(e, f, fsp)
                Span = { Start = sp.Start; End = fsp.End }
            }

        e

let private singleQuotedArg (st: St) : Expr =
    let s = st.I
    let j = st.Text.IndexOf('\'', s + 1)

    if j < 0 then
        refuse st
    else
        st.I <- j + 1

        let e = {
            Kind = EStr(st.Text.Substring(s + 1, j - s - 1))
            Span = { Start = at s; End = here st }
        }

        ws st
        e

let private bareword (st: St) : Expr =
    let t = st.Text
    let s = st.I
    let mutable j = s

    while j < t.Length && cmdWordChar t[j] do
        j <- j + 1

    // a bareword glued to the sentinel is the yaml wrap's, never argv
    if j < t.Length && t[j] = sibSep then
        refuse st
    else
        let w = t.Substring(s, j - s)
        st.I <- j
        let span = { Start = at s; End = at j }

        let e =
            if isTildeWord w then
                tildeExpr w span
            elif w.StartsWith "\\" then
                {
                    Kind = EVar("|backslash " + w)
                    Span = span
                }
            else
                { Kind = EStr w; Span = span }

        ws st
        e

// the statement grant [D:statement-lets]: withStmtLetCmd and
// clearStmtLetCmd over the two flags
let private withStmtLetCmd (st: St) (body: St -> Expr) : Expr =
    if st.LetCmd then
        body st
    else
        let savedStmt = st.LetCmdStmt
        st.LetCmd <- true
        st.LetCmdStmt <- true
        let e = body st
        st.LetCmd <- false
        st.LetCmdStmt <- savedStmt
        e

let private clearStmtLetCmd (st: St) (body: St -> Expr) : Expr =
    if st.LetCmdStmt then
        let savedOk = st.LetCmd
        st.LetCmd <- false
        st.LetCmdStmt <- false
        let e = body st
        st.LetCmd <- savedOk
        st.LetCmdStmt <- true
        e
    else
        body st

// a non-final command element streams [D:interior-arming]: armSeq
let private armSeq (st: St) (items: struct (Expr * bool) list) : Expr =
    let n = List.length items

    let armed =
        items
        |> List.mapi (fun i (struct (e, chain)) ->
            if chain && i < n - 1 then
                if exitCodeSpine e then
                    refuse st
                else
                    {
                        Kind = EPipe(e, { Kind = EVar "|print"; Span = e.Span })
                        Span = e.Span
                    }
            else
                e)

    if st.Ok then foldSeqExpr armed else Unchecked.defaultof<_>

// ---- the expression grammar ------------------------------------------------
let rec private seqExpr (st: St) : Expr =
    // elements sequenced by `;` or the sibling sentinel — the separator
    // backs out before a trailing `until`/`always` (the owning forms:
    // retry/poll/repeat and within read them)
    let first = stmtElem st

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let mutable items = [ struct (first, st.LastChain) ]
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
                        items <- struct (e, st.LastChain) :: items
            else
                go <- false

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match items with
            | [ _ ] -> first
            | _ -> armSeq st (List.rev items)

and private stmtElem (st: St) : Expr =
    st.LastChain <- false

    if guardedHead st then
        refuse st
    else
        let h = if st.ExprParen then notCmd else headOf st false

        if h.K = HeadK.Refuse then
            refuse st
        elif h.K <> HeadK.NotCmd then
            // the interior command element [D:interior-arming]: letRhsCmd
            let e = cmdLine st false (letRhsStops st) None h

            if st.Ok then
                st.LastChain <- true

            e
        else
            let e = commaExpr st
            // an interior element's chain is not this element's
            st.LastChain <- false

            if not st.Ok then
                Unchecked.defaultof<_>
            else if
                // a bare `|` after an element: the next arm's boundary inside a
                // match (left for the match to read), a value-headed pipe or the
                // pipe hint anywhere else — both the command grammar's, refused
                peek st = '|' && peekAt st 1 <> '|' && peekAt st 1 <> '>'
            then
                if st.ExprParen then
                    e
                else
                    let v = valueHeaded st e

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    elif isNull' v then
                        // the gate refused: inside an arm the `|` is the next
                        // arm's; anywhere else the pipe hint's
                        if st.InArm > 0 then e else refuse st
                    elif obj.ReferenceEquals(v, e) then
                        e
                    else
                        st.LastChain <- true
                        v
            else
                e

// letRhsCmd's argv stops: `in`, and a then/elif body's else/elif
and private letRhsStops (st: St) : int =
    if st.IfBody then StopIn ||| StopElse else StopIn

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

            {
                Kind = ETuple all
                Span = Span.union first.Span (List.last all).Span
            }

// the operator under the cursor and its precedence, or Unchecked.defaultof<_> — the
// grammar's table: pipes and composition at 1, `||` 2, `&&` 3,
// comparisons 4, additive 6, multiplicative 7, all left-associative
and private opAhead (st: St) : struct (string * int) =
    let c = peek st
    let d = peekAt st 1

    match c with
    | '|' ->
        if d = '>' then struct ("|>", 1)
        elif d = '|' then struct ("||", 2)
        else struct (Unchecked.defaultof<_>, 0)
    | '&' ->
        if d = '&' then
            struct ("&&", 3)
        else
            struct (Unchecked.defaultof<_>, 0)
    | '=' -> if d = '=' then struct ("==", 4) else struct ("=", 4)
    | '!' ->
        if d = '=' then
            struct ("!=", 4)
        else
            struct (Unchecked.defaultof<_>, 0)
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
    | '-' ->
        if d = '>' then
            struct (Unchecked.defaultof<_>, 0)
        else
            struct ("-", 6)
    | '*' -> struct ("*", 7)
    | '/' -> struct ("/", 7)
    | '%' -> struct ("%", 7)
    | _ -> struct (Unchecked.defaultof<_>, 0)

and private expr (st: St) : Expr = opp st term 1

and private opp (st: St) (termP: St -> Expr) (minPrec: int) : Expr = oppWith st termP minPrec false

// precedence climbing over `termP`, the grammar's OPP with the same
// table — `updateSource` runs it over application chains alone, and a
// command chain's expression stage without `|>` (segOpp)
and private oppWith (st: St) (termP: St -> Expr) (minPrec: int) (noPipe: bool) : Expr =
    st.Depth <- st.Depth + 1

    if tooDeep st then
        st.Depth <- st.Depth - 1
        refuse st
    else
        let mutable left = termP st
        let mutable go = st.Ok

        while go do
            let struct (op, prec) = opAhead st

            if isNull' op || prec < minPrec || (noPipe && op = "|>") then
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
            | "within" -> withinExpr st
            | "retry"
            | "poll"
            | "repeat" -> retryForm st
            | "for" -> loopExpr st "for" "|seqIter"
            | "pfor" -> loopExpr st "pfor" "|seqPiter"
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
                    acc <- {
                        Kind = EApp(acc, a)
                        Span = Span.union acc.Span a.Span
                    }

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

                        cur <- {
                            Kind = EField(cur, name, fspan)
                            Span = Span.union cur.Span fspan
                        }
            elif c = '[' && immediate then
                let next = indexOrSlice st cur

                if isNull' next then go <- false else cur <- next
            else
                go <- false

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match target.Kind with
            | EVar "_" when not (obj.ReferenceEquals(cur, target)) -> {
                Kind = ELambda("_", target.Span, cur)
                Span = cur.Span
              }
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
            if retry st save then
                Unchecked.defaultof<_>
            else
                Unchecked.defaultof<_>
        elif peek st = ']' then
            st.I <- st.I + 1
            let endP = here st
            ws st

            {
                Kind = ESlice(target, optOf lo, optOf hi, loCaret && not (isNull' lo), hiCaret && not (isNull' hi))
                Span = {
                    Start = target.Span.Start
                    End = endP
                }
            }
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
        if peekAt st 1 = '"' then interpLit st
        elif captureAhead st then cmdCapture st
        else refuse st
    elif c = '(' then
        parenAtom st
    elif c = '{' then
        if peekAt st 1 = '|' then anonRecordLit st else recordLit st
    elif c = '[' then
        if peekAt st 1 = '<' then refuse st else listLit st
    elif c = '-' then
        negAtom st
    elif c = '.' then
        if isDigit (peekAt st 1) then
            refuse st
        else
            Unchecked.defaultof<_>
    elif c = '\\' || c = '~' then
        refuse st
    elif c = '!' then
        if isIdentStart (peekAt st 1) then
            refuse st
        else
            Unchecked.defaultof<_>
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

            let e = {
                Kind = EInt Int64.MinValue
                Span = { Start = p; End = here st }
            }

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
                | EFloat f -> {
                    Kind = EFloat(if f = 0.0 then 0.0 else -f)
                    Span = span
                  }
                | EDur n -> { Kind = EDur(-n); Span = span }
                | ESize b -> { Kind = ESize(-b); Span = span }
                | EInt _ -> {
                    Kind = EBinOp("-", { Kind = EInt 0L; Span = e.Span }, e)
                    Span = span
                  }
                // any other operand negates in its own type [D:prefix-minus-typed]
                | _ ->
                    let v = { Kind = EVar "|neg"; Span = e.Span }

                    let sub a b = {
                        Kind = EBinOp("-", a, b)
                        Span = span
                    }

                    {
                        Kind = ELet("|neg", e.Span, e, sub (sub v v) v)
                        Span = span
                    }

and private parenAtom (st: St) : Expr =
    let start = st.I
    st.I <- st.I + 1
    ws st

    if peek st = ')' then
        st.I <- st.I + 1

        let e = {
            Kind = EUnit
            Span = { Start = at start; End = here st }
        }

        ws st
        e
    else
        // `(op)`: an operator value, or one of the refused spellings
        let opText =
            let c = peek st
            let d = peekAt st 1

            match c with
            | '>' ->
                if d = '=' then ">="
                elif d = '>' then ">>"
                else ">"
            | '<' ->
                if d = '=' then "<="
                elif d = '>' then "<>"
                elif d = '|' then "<|"
                elif d = '<' then "<<"
                else "<"
            | '=' -> if d = '=' then "==" else "="
            | '!' -> if d = '=' then "!=" else Unchecked.defaultof<_>
            | '|' ->
                if d = '>' then "|>"
                elif d = '|' then "||"
                else "|"
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
                        let e = {
                            Kind = EOpValue op
                            Span = { Start = at start; End = here st }
                        }

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
            let inner = clearStmtLetCmd st seqExpr
            st.ExprParen <- savedParen

            if not st.Ok then
                Unchecked.defaultof<_>
            elif peek st <> ')' then
                refuse st
            else
                st.I <- st.I + 1

                let e = {
                    inner with
                        Span = { Start = at start; End = here st }
                }

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

            let e = {
                Kind = ERecord(List.rev fields)
                Span = { Start = at start; End = here st }
            }

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

                let e = {
                    Kind = EUpdate(src, List.rev ups)
                    Span = { Start = at start; End = here st }
                }

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

                if
                    keywords.Contains w
                    || next = ';'
                    || next = ':'
                    || (next = '|' && j + 1 < st.Text.Length && st.Text[j + 1] = '}')
                    || String.CompareOrdinal(st.Text, j, "with", 0, 4) = 0
                then
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

            let e = {
                Kind = EAnonRecord(List.rev fields)
                Span = { Start = at start; End = here st }
            }

            ws st
            e

// a range endpoint: a negative literal, or an atom with field suffixes
and private rangeTerm (st: St) : Expr =
    if peek st = '-' && not (isDigit (peekAt st 1)) then
        refuse st
    elif peek st = '-' then
        let start = st.I
        st.I <- st.I + 1
        let digits = digitsAhead st
        st.I <- st.I + digits.Length

        match Int64.TryParse("-" + digits) with
        | true, n ->
            let e = {
                Kind = EInt n
                Span = { Start = at start; End = here st }
            }

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

                    cur <- {
                        Kind = EField(cur, name, fspan)
                        Span = Span.union cur.Span fspan
                    }
                else
                    go <- false

            cur

and private listLit (st: St) : Expr =
    let start = st.I
    st.I <- st.I + 1
    ws st

    // the range probe runs only on a leaf first token — a number, a
    // string, a name and its fields, `-digits` — whose retry re-reads one
    // token; an opener, a prefix minus or an interpolation would parse
    // its interior here and again in the element loop, doubling per
    // nesting level. Those start the element loop directly, and a `..`
    // after such an element is the grammar's range to parse.
    let leafStart =
        let c = peek st

        isDigit c
        || c = '"'
        || (c = '@' && peekAt st 1 = '"')
        || isIdentStart c
        || (c = '-' && isDigit (peekAt st 1))

    let asRange =
        if not leafStart then
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

        let e = {
            Kind = EList []
            Span = { Start = at start; End = here st }
        }

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

            let e = {
                Kind = EList(List.rev items)
                Span = { Start = at start; End = here st }
            }

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

            let e = {
                Kind = EInterp(List.rev parts)
                Span = { Start = at start; End = here st }
            }

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

            let e = {
                Kind = EInterp(List.rev parts)
                Span = { Start = at start; End = here st }
            }

            ws st
            e

// ---- binders and patterns ----------------------------------------------
and private binderParam (st: St) : Pattern =
    // a plain name or `()`; the pattern forms are left to the grammar
    if matches st "()" then
        let s = st.I
        st.I <- st.I + 2

        let p = {
            PKind = PUnit
            PSpan = { Start = at s; End = here st }
        }

        ws st
        p
    else
        let c = peek st

        if isIdentStart c then
            let n, span = identSpanned st

            if isNull' n then
                refuse st
            else
                { PKind = PVar n; PSpan = span }
        elif c = '(' then
            st.I <- st.I + 1
            ws st
            let inner = commaPats st

            if not st.Ok then Unchecked.defaultof<_>
            elif peek st = ':' && peekAt st 1 <> ':' then refuse st
            elif not (strWs st ")") then refuse st
            else inner
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
        let names =
            ps
            |> List.choose (fun p ->
                match p.PKind with
                | PVar n -> Some n
                | _ -> None)

        if List.length names <> (Set.ofList names).Count then
            refuse st
        else
            ps

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
                    (fun t h -> {
                        PKind = PCons(h, t)
                        PSpan = {
                            Start = h.PSpan.Start
                            End = t.PSpan.End
                        }
                    })
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

                {
                    PKind = PTuple all
                    PSpan = {
                        Start = (List.head all).PSpan.Start
                        End = (List.last all).PSpan.End
                    }
                }

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

        let p = {
            PKind = PUnit
            PSpan = { Start = at start; End = here st }
        }

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
            let p = {
                PKind = PInt n
                PSpan = { Start = at start; End = here st }
            }

            ws st
            p
        | _ -> refuse st
    elif c = '"' || (c = '@' && peekAt st 1 = '"') then
        let s = patString st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let p = {
                PKind = PStr s
                PSpan = { Start = at start; End = here st }
            }

            ws st
            p
    elif c = '(' then
        st.I <- st.I + 1
        ws st
        let inner = commaPats st

        if not st.Ok then Unchecked.defaultof<_>
        elif peek st = ':' && peekAt st 1 <> ':' then refuse st
        elif not (strWs st ")") then refuse st
        else inner
    elif c = '[' then
        st.I <- st.I + 1
        ws st

        if peek st = ']' then
            st.I <- st.I + 1

            let p = {
                PKind = PSeqNil
                PSpan = { Start = at start; End = here st }
            }

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

                let p = {
                    PKind = PSeqList(List.rev items)
                    PSpan = { Start = at start; End = here st }
                }

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

            let p = {
                PKind = PRecord(List.rev fields)
                PSpan = { Start = at start; End = here st }
            }

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
                        {
                            PKind = PRegex(lit, litSpan, raw, binder)
                            PSpan = {
                                Start = span.Start
                                End = binder.PSpan.End
                            }
                        }
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
                        let e = if isNull' arg then headEnd else arg.PSpan.End

                        {
                            PKind = PCase(ctor, optOf arg, qualifier)
                            PSpan = { Start = span.Start; End = e }
                        }
                else
                    {
                        PKind = PCase(ctor, None, qualifier)
                        PSpan = { Start = span.Start; End = headEnd }
                    }
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
    let savedMask = st.AliasMasked

    for n in names do
        st.Locals <- Set.add n st.Locals
        st.AliasMasked <- Set.add n st.AliasMasked

    let e = body st
    st.Locals <- saved
    st.AliasMasked <- savedMask
    e

// a top-level let's params: known names that mask an alias too
// [D:alias-binding-shadow] (the grammar's withSpine)
and private withSpineNames (st: St) (names: string list) (body: St -> Expr) : Expr = withLocals st names body

and private bodyExpr (st: St) : Expr =
    // a let-in body: expression territory ends
    let saved = st.ExprParen
    st.ExprParen <- false
    let e = seqExpr st
    st.ExprParen <- saved
    e

// a statement body [D:statement-lets]: expression territory ends, lets
// may take commands
and private stmtBody (st: St) : Expr =
    let saved = st.ExprParen
    st.ExprParen <- false
    let e = withStmtLetCmd st seqExpr
    st.ExprParen <- saved
    e

// a then/elif body: a statement body whose commands stop at else/elif
and private ifBody (st: St) : Expr =
    let saved = st.IfBody
    st.IfBody <- true
    let e = stmtBody st
    st.IfBody <- saved
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
        let names = ps |> List.collect patLeafNames
        let body = withLocals st names stmtBody

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let inner = curryParams ps body

            {
                inner with
                    Span = { Start = p; End = body.Span.End }
            }

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

    if
        st.Ok
        && not (isNull' binder)
        && not (isPlainBinder binder)
        && peek st = '='
        && peekAt st 1 <> '='
    then
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
            let body = withLocals st (patLeafNames binder) seqExpr

            if not st.Ok then
                Unchecked.defaultof<_>
            else
                {
                    Kind = ELetPat(binder, value, body)
                    Span = { Start = p; End = body.Span.End }
                }
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
                    let names = ps |> List.collect patLeafNames

                    // the block-let command RHS [D:block-let-cmd]: granted
                    // contexts only — elsewhere the grammar's probe owns it
                    let value =
                        withLocals st names (fun st ->
                            let h = headOf st false

                            if h.K = HeadK.Refuse then refuse st
                            elif h.K = HeadK.NotCmd then seqExpr st
                            elif st.LetCmd then cmdLine st false (letRhsStops st) None h
                            else refuse st)

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
                            {
                                Kind = ELet(name, nameSpan, curryParams ps value, body)
                                Span = { Start = p; End = body.Span.End }
                            }

and private ifCond (st: St) : Expr =
    // the condition's command grammar [D:if-succeeds]: argv stops at `then`
    let h = headOf st false

    if h.K = HeadK.Refuse then
        refuse st
    elif h.K = HeadK.NotCmd then
        expr st
    else
        let saved = st.IfCond
        st.IfCond <- true
        let e = cmdLine st false StopThen None h
        st.IfCond <- saved
        e

and private ifExpr (st: St) : Expr =
    let p = here st
    keyword st "if" |> ignore
    let cond = ifCond st

    if not st.Ok then
        Unchecked.defaultof<_>
    elif not (keyword st "then") then
        refuse st
    else
        let thn = ifBody st

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
                        let t = ifBody st

                        if st.Ok then
                            elifs <- (c, t) :: elifs

            if not st.Ok then
                Unchecked.defaultof<_>
            else
                let els =
                    if keyword st "else" then
                        let e = stmtBody st
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

                            Some {
                                Kind = EIf(c, t, inner)
                                Span = { Start = c.Span.Start; End = e }
                            })

                    let endPos = (chained |> Option.defaultValue thn).Span.End

                    {
                        Kind = EIf(cond, thn, chained)
                        Span = { Start = p; End = endPos }
                    }

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
        elif
            not alts.IsEmpty
            && (p :: alts |> List.exists (fun a -> not (List.isEmpty (patLeafNames a))))
        then
            refuse st
        else
            let names = patLeafNames p

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
                let body = withLocals st names stmtBody
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

    if st.Ok then
        List.concat (List.rev arms)
    else
        Unchecked.defaultof<_>

and private functionExpr (st: St) : Expr =
    let p = here st
    keyword st "function" |> ignore
    let arms = matchArms st

    if not st.Ok then
        Unchecked.defaultof<_>
    else
        let _, _, lastBody = List.last arms

        let kwSpan = {
            Start = p
            End = { Line = p.Line; Col = p.Col + 8 }
        }

        let span = { Start = p; End = lastBody.Span.End }

        let scrut = {
            Kind = EVar "|function"
            Span = kwSpan
        }

        {
            Kind =
                ELambda(
                    "|function",
                    kwSpan,
                    {
                        Kind = EMatch(scrut, arms)
                        Span = span
                    }
                )
            Span = span
        }

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

            {
                Kind = EMatch(scrut, arms)
                Span = { Start = p; End = lastBody.Span.End }
            }

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

        if st.Ok && strWs st ">" then FromApp(name, List.rev args)
        elif retry st save then FromName name
        else Unchecked.defaultof<_>
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

                            if st.Ok && strWs st ">" then
                                Some(FromMap(k, inner), false)
                            else
                                refuse st
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
                elif w <> "stream" then
                    refuse st
                else
                    None

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let shape, seqOf =
                match arg with
                | Some(sh, s) -> Some sh, s
                | None -> None, false

            {
                Kind = EFrom(fmt, shape, seqOf, streamW)
                Span = { Start = p; End = here st }
            }

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

        {
            Kind = ETo(fmt, streamW)
            Span = { Start = p; End = here st }
        }

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

    {
        Kind = ETryForm asResult
        Span = { Start = at s; End = here st }
    }

and private orFailForm (st: St) : Expr =
    let s = st.I
    keyword st "orFail" |> ignore
    let save = st.I
    let msg = postfixAtom st

    if not st.Ok then
        if retry st save then
            {
                Kind = EOrFailForm None
                Span = { Start = at s; End = here st }
            }
        else
            Unchecked.defaultof<_>
    else
        {
            Kind = EOrFailForm(optOf msg)
            Span = { Start = at s; End = here st }
        }

// ---- command lines [D:hand-parser] ------------------------------------------
// cmdArgStops' choice, piece by piece: a glued piece, a mid-word splice,
// a text-block marker or a shape outside this parser is a refusal
and private cmdArgs (st: St) (stops: int) : Expr list =
    let t = st.Text
    let mutable args = []
    let mutable go = true

    // notGluedPiece: a piece starts after whitespace, `|`, `(` or the sentinel
    let glued () =
        let p = peekAt st -1
        not (Char.IsWhiteSpace p || p = '|' || p = '(' || p = sibSep)

    // notMidWord: a splice starts after a blank
    let midWord () =
        let p = peekAt st -1
        p <> ' ' && p <> '\t'

    while st.Ok && go do
        let c = peek st

        let arg =
            if st.I >= t.Length then
                Unchecked.defaultof<_>
            elif matches st "<<<" || matches st "$<<<" || matches st "$$<<<" then
                refuse st
            elif c = '"' then
                if glued () then refuse st else stringAtom st
            elif c = '\'' then
                if glued () then refuse st else singleQuotedArg st
            elif c = '@' && peekAt st 1 = '"' then
                refuse st
            elif c = '$' then
                let d = peekAt st 1

                if d = '"' then
                    if glued () then refuse st else interpLit st
                elif d = '(' then
                    if glued () then refuse st else cmdCapture st
                elif d = '@' then
                    if peekAt st 2 = '"' || midWord () || not (isIdentStart (peekAt st 2)) then
                        refuse st
                    else
                        // the argv splat [D:argv-splat]
                        let s = st.I
                        let inner = splicePathAt st 2

                        if not st.Ok then
                            Unchecked.defaultof<_>
                        else
                            let e = {
                                Kind = ESplat inner
                                Span = { Start = at s; End = here st }
                            }

                            ws st
                            e
                elif isIdentStart d then
                    if midWord () then
                        refuse st
                    else
                        let e = splicePathAt st 1

                        if st.Ok then
                            ws st

                        e
                else
                    refuse st
            elif c = '(' then
                if glued () then refuse st else argParens st
            elif cmdWordChar c then
                if glued () then
                    refuse st
                elif
                    ((stops &&& StopIn) <> 0 && stopWordAt st "in")
                    || ((stops &&& StopElse) <> 0 && (stopWordAt st "else" || stopWordAt st "elif"))
                    || ((stops &&& StopThen) <> 0 && stopWordAt st "then")
                then
                    Unchecked.defaultof<_>
                else
                    bareword st
            else
                Unchecked.defaultof<_>

        if not st.Ok || isNull' arg then
            go <- false
        else
            args <- arg :: args

    if st.Ok then List.rev args else Unchecked.defaultof<_>

// the grammar's `parens` (no unit, no operator value): expression
// territory, the statement grant cleared
and private argParens (st: St) : Expr =
    let start = st.I
    st.I <- st.I + 1
    ws st

    if peek st = ')' then
        refuse st
    else
        let savedParen = st.ExprParen
        st.ExprParen <- true
        let inner = clearStmtLetCmd st seqExpr
        st.ExprParen <- savedParen

        if not st.Ok then
            Unchecked.defaultof<_>
        elif peek st <> ')' then
            refuse st
        else
            st.I <- st.I + 1

            let e = {
                inner with
                    Span = { Start = at start; End = here st }
            }

            ws st
            e

// `$(chain)` / `$env(chain)` — captureSigil: the interior is a full
// command line (builtin heads admitted), closed on this line
and private cmdCapture (st: St) : Expr =
    let start = st.I

    let envO =
        if peekAt st 1 = '(' then
            st.I <- st.I + 2
            None
        else
            st.I <- st.I + 1
            let ns = st.I
            let w = takeWord st
            let sp = { Start = at ns; End = here st }
            st.I <- st.I + 1
            Some { Kind = EVar w; Span = sp }

    ws st
    let h = headOf st true

    let chain =
        if h.K = HeadK.Refuse || h.K = HeadK.NotCmd then
            refuse st
        else
            cmdLine st true 0 envO h

    if not st.Ok then
        Unchecked.defaultof<_>
    elif exitCodeSpine chain || peek st <> ')' then
        refuse st
    else
        st.I <- st.I + 1
        let span = { Start = at start; End = here st }
        ws st

        {
            Kind = ECapture { chain with Span = span }
            Span = span
        }

// `NAME=value` words [D:env-prefix]: envAssign, many; a value the next
// piece glues to is refused
and private envAssigns (st: St) : ((string * Expr) * Span) list =
    let mutable acc = []

    while st.Ok && envPrefixAhead st do
        let s = st.I
        let name = takeWord st
        st.I <- st.I + 1
        let c = peek st

        let value =
            if c = ' ' || c = '\t' then
                Unchecked.defaultof<_>
            elif c = '$' then
                let d = peekAt st 1

                if d = '"' then
                    interpLit st
                elif d = '(' then
                    cmdCapture st
                elif isIdentStart d then
                    let vs = st.I
                    st.I <- st.I + 1
                    let w = takeWord st

                    {
                        Kind = EVar w
                        Span = { Start = at vs; End = here st }
                    }
                else
                    refuse st
            elif c = '"' then
                stringAtom st
            elif c = '\'' then
                singleQuotedArg st
            elif c = '@' && peekAt st 1 = '"' then
                refuse st
            elif cmdWordChar c && st.I < st.Text.Length then
                let vs = st.I
                let mutable j = vs

                while j < st.Text.Length && cmdWordChar st.Text[j] do
                    j <- j + 1

                let w = st.Text.Substring(vs, j - vs)
                st.I <- j
                let span = { Start = at vs; End = at j }

                if isTildeWord w then
                    tildeExpr w span
                else
                    { Kind = EStr w; Span = span }
            else
                refuse st

        if st.Ok then
            ws st
            let sp = { Start = at s; End = here st }

            if st.I < st.Text.Length && not (Char.IsWhiteSpace(peekAt st -1)) then
                refuse st
            else
                let v =
                    if isNull' value then
                        { Kind = EStr ""; Span = sp }
                    else
                        value

                acc <- ((name, v), sp) :: acc

    if st.Ok then List.rev acc else Unchecked.defaultof<_>

// commandSegment past its head verdict: env prefixes, the head, the
// argv, the node
and private cmdSegment (st: St) (builtinHeads: bool) (h: Head) (stops: int) (sigilEnv: Expr option) : Expr =
    if h.K = HeadK.Env then
        // `attempt (many envAssign .>>. head)`: no head, no command
        let assigns = envAssigns st

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            let h2 = headOf st builtinHeads

            if h2.K = HeadK.NotCmd || h2.K = HeadK.Refuse || h2.K = HeadK.Env then
                refuse st
            else
                headSegment st h2 stops (segmentEnv sigilEnv assigns)
    else
        headSegment st h stops sigilEnv

// commandSegment past a resolved head
and private headSegment (st: St) (h: Head) (stops: int) (sigilEnv: Expr option) : Expr =
    let start = st.I

    let fullSpan (span: Span) (args: Expr list) = {
        Start = span.Start
        End =
            (match args with
             | [] -> span.End
             | _ -> (List.last args).Span.End)
    }

    if h.K = HeadK.Dynamic then
        // ^$name / ^$(…) — the dynamic head [D:dynamic-head]
        st.I <- st.I + 1

        let he, disp =
            if captureAhead st then
                cmdCapture st, "$(…)"
            else
                let e = splicePathAt st 1
                e, (if st.Ok then "$" + sexpr e else null)

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            ws st
            let span = { Start = at start; End = he.Span.End }
            let args = cmdArgs st stops

            if not st.Ok then
                Unchecked.defaultof<_>
            else
                {
                    Kind = ECmd(HeadDyn(disp, he), args, sigilEnv)
                    Span = fullSpan span args
                }
    else
        let span = { Start = at start; End = at h.End }

        // the alias table's second read, as litHead's match takes it
        let alias =
            if h.K = HeadK.Aliased then
                (if st.AliasMasked.Contains h.Word then
                     None
                 else
                     st.R.AliasHead h.Word)
            else
                None

        st.I <- h.End
        ws st
        let args = cmdArgs st stops

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match h.K with
            | HeadK.External -> {
                Kind = ECmd(HeadLit h.Word, args, sigilEnv)
                Span = fullSpan span args
              }
            | HeadK.Tilde -> {
                Kind = ECmd(HeadDyn(h.Word, tildeExpr h.Word span), args, sigilEnv)
                Span = fullSpan span args
              }
            | HeadK.Builtin -> buildBuiltinApp h.Word span args
            | _ ->
                match alias with
                | Some(exe, prefix) ->
                    let allArgs = (prefix |> List.map (fun a -> { Kind = EStr a; Span = span })) @ args

                    if st.R.IsCommandCallable exe then
                        buildBuiltinApp exe span allArgs
                    else
                        {
                            Kind = ECmd(HeadLit exe, allArgs, sigilEnv)
                            Span = fullSpan span args
                        }
                | None -> refuse st

// the match-arm boundary [D:match-arm-commands]: `| <pattern> ->` or
// `| <pattern> when` ends the chain; a pattern this parser refuses
// refuses the statement (the grammar's verdict is unknown)
and private armBoundaryAhead (st: St) : bool =
    let save = st.I
    let d = peekAt st 1

    if d = '|' || d = '>' then
        false
    else
        st.I <- st.I + 1
        ws st
        let w = wordAhead st

        // a keyword heads no pattern (patWord's fatal, inside the probe's attempt)
        if
            not (isNull' w)
            && keywords.Contains w
            && w <> "true"
            && w <> "false"
            && not (isIdentCont (peekAt st w.Length))
        then
            st.I <- save
            false
        else
            let p = commaPats st
            let mutable alts = []

            while st.Ok && peek st = '|' && peekAt st 1 <> '|' && peekAt st 1 <> '>' do
                st.I <- st.I + 1
                ws st
                let a = commaPats st

                if st.Ok then
                    alts <- a :: alts

            let boundary =
                st.Ok
                && not (
                    not alts.IsEmpty
                    && (p :: alts |> List.exists (fun a -> not (List.isEmpty (patLeafNames a))))
                )
                && (matches st "->" || keywordAhead st "when")

            st.I <- save
            boundary

// a chain's expression stage: segOpp, the operator table without `|>`
and private segTerm (st: St) : Expr =
    match wordAhead st with
    | "try"
    | "orFail"
    | "for"
    | "pfor" when not (isIdentCont (peekAt st (wordAhead st).Length)) -> refuse st
    | _ -> term st

// one stage after a pipe glyph: a reifier marker, a command segment or
// an expression; and/or chains, the try/orFail forms and env prefixes
// are refused
and private cmdStage (st: St) (builtinHeads: bool) (stops: int) (sigilEnv: Expr option) (op: string) : Seg =
    let t = st.Text

    if peek st = '|' || (op = "|>" && keywordAhead st "orFail") then
        refuse st
    else
        let s = st.I
        let mutable j = s

        while j < t.Length && cmdWordChar t[j] do
            j <- j + 1

        let w = if j > s then t.Substring(s, j - s) else null

        if not (isNull w) && List.contains w reifierNames then
            st.I <- j
            let span = { Start = at s; End = at j }
            ws st

            let msg =
                if w = "orFail" then
                    postfixAtom st
                else
                    Unchecked.defaultof<_>

            if not st.Ok then
                Unchecked.defaultof<_>
            elif (w = "orFail" && isNull' msg) || not (reifierEndAhead st) then
                refuse st
            else
                match w with
                | "complete" -> CompleteMarker span
                | "succeeds" -> SucceedsMarker span
                | "exitCode" -> ExitCodeMarker span
                | "orFail" -> OrFailMarker(msg, span)
                | "exec" -> ExecMarker span
                | "line" -> LineMarker span
                | _ -> TextMarker span
        elif w = "and" || w = "or" || keywordAhead st "try" || envPrefixAhead st then
            refuse st
        else
            let h = headOf st builtinHeads

            if h.K = HeadK.Refuse then
                refuse st
            elif h.K = HeadK.NotCmd then
                let e = oppWith st segTerm 1 true

                if not st.Ok then Unchecked.defaultof<_>
                elif isNull' e then refuse st
                else Stage e
            else
                let e = cmdSegment st builtinHeads h stops sigilEnv
                if st.Ok then Stage e else Unchecked.defaultof<_>

// pipedStages: each glyph and its stage, up to a match arm's boundary
and private cmdStages (st: St) (builtinHeads: bool) (stops: int) (sigilEnv: Expr option) =
    let mutable rest = []
    let mutable go = st.Ok

    while go do
        if peek st <> '|' then
            go <- false
        elif st.InArm > 0 && armBoundaryAhead st then
            go <- false
        elif not st.Ok then
            go <- false
        else
            let os = st.I
            let op = if peekAt st 1 = '>' then "|>" else "|"
            st.I <- st.I + op.Length
            let opSpan = { Start = at os; End = here st }
            ws st
            let seg = cmdStage st builtinHeads stops sigilEnv op

            if st.Ok then
                rest <- ((op, opSpan), seg) :: rest
            else
                go <- false

    List.rev rest

// cmdLineWith past the head verdict: the segment, the piped stages, and
// the chain fold shared with the grammar
and private cmdLine (st: St) (builtinHeads: bool) (stops: int) (sigilEnv: Expr option) (h: Head) : Expr =
    st.Depth <- st.Depth + 1

    if tooDeep st then
        st.Depth <- st.Depth - 1
        refuse st
    else
        let head = cmdSegment st builtinHeads h stops sigilEnv

        let rest =
            if st.Ok then
                cmdStages st builtinHeads stops sigilEnv
            else
                []

        st.Depth <- st.Depth - 1

        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match foldChain st.R.BareHome head rest with
            | Result.Ok e -> e
            | Result.Error _ -> refuse st

// a value-headed pipeline [D:value-headed-pipe]: `xs | prog …`, the gate
// (a single `|`, no chain word or reifier, an external head) then the
// stages. Unchecked.defaultof<_> with Ok when the gate refuses; `lhs` itself when no
// stage follows (a match arm's boundary); a capture head, an alias head
// and a head this parser cannot judge refuse
and private valueHeaded (st: St) (lhs: Expr) : Expr =
    let save = st.I
    st.I <- st.I + 1
    ws st

    let blocked =
        match ("and" :: "or" :: reifierNames) |> List.tryFind (fun w -> matches st w) with
        | Some w ->
            let c = peekAt st w.Length

            st.I + w.Length >= st.Text.Length
            || Char.IsWhiteSpace c
            || c = ')'
            || c = ';'
            || c = '|'
            || c = sibSep
            || c = fieldSep
        | None -> false

    let h = if blocked then notCmd else headOf st true
    st.I <- save

    match lhs.Kind with
    | ECapture _ -> refuse st
    | _ when h.K = HeadK.NotCmd || h.K = HeadK.Builtin -> Unchecked.defaultof<_>
    | _ when h.K = HeadK.Refuse || h.K = HeadK.Aliased -> refuse st
    | _ ->
        st.Depth <- st.Depth + 1
        let stages = if tooDeep st then refuse st else cmdStages st true 0 None
        st.Depth <- st.Depth - 1

        match stages with
        | _ when not st.Ok -> Unchecked.defaultof<_>
        | [] -> lhs
        | (_, Stage { Kind = ECmd _ }) :: _ ->
            match foldChain st.R.BareHome lhs stages with
            | Result.Ok e -> e
            | Result.Error _ -> refuse st
        | _ -> refuse st

// within <kind> … [D:within-scopes]: cd/env/lock take one atom (env its
// pairs too), tmp/proc/serve a binder; a trailing `always` segment
// [D:within-always-any] and the bare scope [D:within-always]; `until
// stopped` [D:held-scope] is refused
and private withinExpr (st: St) : Expr =
    let p = here st
    keyword st "within" |> ignore
    st.Depth <- st.Depth + 1

    let w = wordAhead st

    let kind =
        if isNull' w || keywords.Contains w then
            None
        else
            Ast.withinKinds |> List.tryFind (fun k -> k.Name = w && not k.Standalone)

    // the dangle space-joins the first body statement
    let block (st: St) =
        (strWs st ";" || strWs st sibSepStr) |> ignore
        stmtBody st

    let e =
        if tooDeep st then
            refuse st
        else
            match kind with
            | None ->
                let body = block st

                if not st.Ok then
                    Unchecked.defaultof<_>
                else
                    match withinTail st stmtBody with
                    | Some cleanup when st.Ok -> {
                        Kind = EAlways(body, cleanup)
                        Span = { Start = p; End = cleanup.Span.End }
                      }
                    | _ -> refuse st
            | Some wk ->
                identSpanned st |> ignore

                let mk binder arg opts (body: Expr) =
                    if not st.Ok then
                        Unchecked.defaultof<_>
                    else
                        {
                            Kind = EWithin(wk.Id, binder, arg, opts, body)
                            Span = { Start = p; End = body.Span.End }
                        }

                match wk.Id with
                | WithinTmp ->
                    let b, bspan = identSpanned st

                    if isNull' b then
                        refuse st
                    else
                        mk (Some(b, bspan)) None None (withLocals st [ b ] block)
                | WithinProc ->
                    // the scoped process [D:scoped-procs]: one command, letRhsCmd's
                    let b, bspan = identSpanned st

                    if isNull' b then
                        refuse st
                    else
                        ws st

                        if not (strWs st "=") then
                            refuse st
                        else
                            let h = headOf st false

                            let cmdE =
                                if h.K = HeadK.Refuse || h.K = HeadK.NotCmd then
                                    refuse st
                                else
                                    cmdLine st false (letRhsStops st) None h

                            if not st.Ok then
                                Unchecked.defaultof<_>
                            else
                                match cmdE.Kind with
                                | ECmd _ -> mk (Some(b, bspan)) (Some cmdE) None (withLocals st [ b ] block)
                                | _ -> refuse st
                | WithinServe ->
                    // the scoped listener [D:http-serve]: a config atom, a handler atom
                    let b, bspan = identSpanned st

                    if isNull' b then
                        refuse st
                    else
                        ws st

                        if not (strWs st "=") then
                            refuse st
                        else
                            let cfg = postfixAtom st

                            let handler =
                                if st.Ok && not (isNull' cfg) then
                                    postfixAtom st
                                else
                                    Unchecked.defaultof<_>

                            if not st.Ok then
                                Unchecked.defaultof<_>
                            elif isNull' cfg || isNull' handler then
                                refuse st
                            else
                                mk (Some(b, bspan)) (Some cfg) (Some handler) (withLocals st [ b ] block)
                | WithinCd
                | WithinEnv
                | WithinLock ->
                    // env takes `NAME=value` words too [D:within-env-pairs]
                    let arg =
                        if wk.Id = WithinEnv && envPrefixAhead st then
                            let assigns = envAssigns st

                            if st.Ok then
                                envAssignsList assigns
                            else
                                Unchecked.defaultof<_>
                        else
                            postfixAtom st

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    elif isNull' arg then
                        refuse st
                    else
                        // lock's optional `timeout=` key [D:within-lock]
                        let opts =
                            if wk.Id = WithinLock && matches st "timeout" then
                                let save = st.I
                                st.I <- st.I + 7
                                ws st

                                if peek st = '=' then
                                    st.I <- st.I + 1
                                    ws st
                                    let o = postfixAtom st

                                    if st.Ok && isNull' o then
                                        st.I <- save
                                        None
                                    else
                                        optOf o
                                else
                                    st.I <- save
                                    None
                            else
                                None

                        if not st.Ok then
                            Unchecked.defaultof<_>
                        else
                            mk None (Some arg) opts (block st)
                | _ -> refuse st

    let e =
        if not st.Ok then
            Unchecked.defaultof<_>
        else
            match e.Kind with
            | EWithin(k, binder, arg, opts, body) ->
                let names =
                    match binder with
                    | Some(n, _) -> [ n ]
                    | None -> []

                match withinTail st (fun st -> withLocals st names stmtBody) with
                | _ when not st.Ok -> Unchecked.defaultof<_>
                | None -> e
                | Some cleanup ->
                    let inner = {
                        Kind = EAlways(body, cleanup)
                        Span = {
                            Start = body.Span.Start
                            End = cleanup.Span.End
                        }
                    }

                    {
                        Kind = EWithin(k, binder, arg, opts, inner)
                        Span = {
                            Start = e.Span.Start
                            End = cleanup.Span.End
                        }
                    }
            | _ -> e

    st.Depth <- st.Depth - 1
    e


// after a scope's block: `until stopped` (refused), then an optional
// `always` segment, never followed by an `until`
and private withinTail (st: St) (cleanupBody: St -> Expr) : Expr option =
    let save = st.I
    (strWs st ";" || strWs st sibSepStr) |> ignore

    if keywordAhead st "until" then
        refuse st
    elif keyword st "always" then
        (strWs st ";" || strWs st sibSepStr) |> ignore
        let c = cleanupBody st

        if not st.Ok then
            None
        else
            let after = st.I
            (strWs st ";" || strWs st sibSepStr) |> ignore

            if keywordAhead st "until" then
                refuse st
            else
                st.I <- after
                Some c
    else
        st.I <- save
        None

// retry/poll/repeat [D:retry-poll]: `key=atom` pairs desugar to the
// defaults record's update (watch= peeled [D:scoped-procs]), or one
// options atom; the body; an optional `until x` predicate block (`until
// stopped` is refused)
and private retryForm (st: St) : Expr =
    let p = here st
    let w = takeWord st
    ws st

    let kind =
        match w with
        | "retry" -> LoopRetry
        | "poll" -> LoopPoll
        | _ -> LoopRepeat

    let defaultsKey =
        match kind with
        | LoopRetry -> "|retryDefaults"
        | LoopPoll -> "|pollDefaults"
        | LoopRepeat -> "|repeatDefaults"

    st.Depth <- st.Depth + 1
    let mutable pairs = []
    let mutable go = not (tooDeep st)

    if not go then
        refuse st |> ignore

    while go do
        let save = st.I
        let k, ks = identSpanned st

        if isNull' k || peek st <> '=' then
            st.I <- save
            go <- false
        else
            st.I <- st.I + 1
            let v = postfixAtom st

            if not st.Ok then
                go <- false
            elif isNull' v then
                refuse st |> ignore
                go <- false
            else
                pairs <- ((k, ks), v) :: pairs

    let pairs = List.rev pairs

    let e =
        if not st.Ok then
            Unchecked.defaultof<_>
        elif
            pairs
            |> List.countBy (fun ((k, _), _) -> k)
            |> List.exists (fun (_, n) -> n > 1)
        then
            refuse st
        else
            let watchE =
                pairs |> List.tryPick (fun ((k, _), v) -> if k = "watch" then Some v else None)

            let pairs = pairs |> List.filter (fun ((k, _), _) -> k <> "watch")
            let at0 = { Start = p; End = p }
            let defaults = { Kind = EVar defaultsKey; Span = at0 }

            let optsE =
                match pairs with
                | [] when watchE.IsSome -> defaults
                | [] when
                    kind = LoopRepeat
                    && (st.I >= st.Text.Length || peek st = ';' || peek st = sibSep)
                      ->
                      {
                          Kind = EUpdate(defaults, [])
                          Span = at0
                      }
                | [] ->
                    let o = postfixAtom st
                    if st.Ok && isNull' o then refuse st else o
                | _ -> {
                    Kind = EUpdate(defaults, pairs |> List.map (fun ((k, ks), v) -> [ (k, ks) ], v))
                    Span = {
                        Start = p
                        End = (List.last pairs |> snd).Span.End
                    }
                  }

            if not st.Ok then
                Unchecked.defaultof<_>
            else
                (strWs st ";" || strWs st sibSepStr) |> ignore
                let savedParen = st.ExprParen
                st.ExprParen <- false
                let body = seqExpr st
                st.ExprParen <- savedParen

                if not st.Ok then
                    Unchecked.defaultof<_>
                else
                    let save = st.I
                    (strWs st ";" || strWs st sibSepStr) |> ignore

                    let until =
                        if keyword st "until" then
                            let b, bspan = identSpanned st

                            if isNull' b || b = "stopped" then
                                refuse st
                            else
                                (strWs st ";" || strWs st sibSepStr) |> ignore

                                let pred =
                                    withLocals st [ b ] (fun st ->
                                        let savedParen = st.ExprParen
                                        st.ExprParen <- false
                                        let e = seqExpr st
                                        st.ExprParen <- savedParen
                                        e)

                                if st.Ok then Some((b, bspan), pred) else None
                        else
                            st.I <- save
                            None

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    else
                        let endPos =
                            match until with
                            | Some(_, pr) -> pr.Span.End
                            | None -> body.Span.End

                        {
                            Kind = ERetry(kind, optsE, watchE, body, until)
                            Span = { Start = p; End = endPos }
                        }

    st.Depth <- st.Depth - 1
    e

// for/pfor … in … do [D:for-do]: `xs |> Seq.iter (fun p -> body)`; a
// lone command body streams, armed (the grammar's cmdBody)
and private loopExpr (st: St) (kw: string) (iterKey: string) : Expr =
    let p = here st
    keyword st kw |> ignore
    st.Depth <- st.Depth + 1

    let e =
        if tooDeep st then
            refuse st
        else
            let binder = commaPats st

            if not st.Ok then
                Unchecked.defaultof<_>
            elif isNull' binder || not (keyword st "in") then
                refuse st
            else
                let source = expr st

                if not st.Ok then
                    Unchecked.defaultof<_>
                elif matches st ".." || (kw = "pfor" && matches st "->") || not (keyword st "do") then
                    refuse st
                elif kw = "pfor" && keywordAhead st "yield" then
                    refuse st
                else
                    let body =
                        withLocals st (patLeafNames binder) (fun st ->
                            let h = headOf st true

                            if h.K = HeadK.Refuse then
                                refuse st
                            elif h.K = HeadK.NotCmd then
                                stmtBody st
                            else
                                let s = st.I
                                let chain = cmdLine st true 0 None h

                                if not st.Ok then
                                    Unchecked.defaultof<_>
                                // a multi-line body is seqExpr's, after a backtrack
                                elif peek st = ';' || peek st = sibSep || exitCodeSpine chain then
                                    refuse st
                                else
                                    let span = { Start = at s; End = here st }

                                    {
                                        Kind = EPipe(chain, { Kind = EVar "|print"; Span = span })
                                        Span = span
                                    })

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    else
                        let span = { Start = p; End = body.Span.End }
                        let mk k = { Kind = k; Span = span }
                        mk (EPipe(source, mk (EApp(mk (EVar iterKey), mk (ELambdaPat(binder, body))))))

    st.Depth <- st.Depth - 1
    e

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

                specs <-
                    {
                        AName = name
                        AArg = arg
                        ASpan = { Start = at s; End = here st }
                    }
                    :: specs

                if not (fieldSepP st) then
                    go <- false

    if not st.Ok then Unchecked.defaultof<_>
    elif not (strWs st ">]") then refuse st
    else List.rev specs

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
                    SType {
                        Name = name
                        Params = List.rev tps
                        Body = body
                        Attrs = attrs
                        Validate = None
                        Span = { Start = p; End = here st }
                    }

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

            if st.I = st.Text.Length then
                SModule(Some w, kwSpan)
            else
                refuse st

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

            if not st.Ok then Unchecked.defaultof<_>
            elif st.I <> st.Text.Length then refuse st
            else SImport(path, pathSpan, alias)

let private letStmt (st: St) : Stmt =
    keyword st "let" |> ignore

    // the destructuring statement `let x, y = v`: the grammar's first
    // let alternative; a plain binder falls through
    let save = st.I
    let binder = commaPats st

    if
        st.Ok
        && not (isNull' binder)
        && not (isPlainBinder binder)
        && peek st = '='
        && peekAt st 1 <> '='
    then
        st.I <- st.I + 1
        ws st

        let value = seqExpr st

        if not st.Ok then Unchecked.defaultof<_>
        elif st.I <> st.Text.Length then refuse st
        else SLetPat(binder, value)
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

                    if not st.Ok then Unchecked.defaultof<_>
                    elif st.I <> st.Text.Length then refuse st
                    else SSig(name, ty, nameSpan)
            else
                let ps = binderParams st

                if not st.Ok then
                    Unchecked.defaultof<_>
                elif not (strWs st "=") then
                    refuse st
                else
                    let names = ps |> List.collect patLeafNames

                    // the spine [D:block-let-cmd]: letCmdOk up, params known;
                    // a command-first RHS sequences its sentinel siblings after it
                    let rhs =
                        withSpineNames st names (fun st ->
                            let savedLet = st.LetCmd
                            st.LetCmd <- true
                            let h = headOf st false

                            let e =
                                if h.K = HeadK.Refuse then
                                    refuse st
                                elif h.K = HeadK.NotCmd then
                                    seqExpr st
                                else
                                    let first = cmdLine st false StopIn None h
                                    let mutable items = [ struct (first, true) ]

                                    while st.Ok && strWs st sibSepStr do
                                        let e = stmtElem st

                                        if st.Ok then
                                            items <- struct (e, st.LastChain) :: items

                                    if not st.Ok then
                                        Unchecked.defaultof<_>
                                    else
                                        match items with
                                        | [ _ ] -> first
                                        | _ -> armSeq st (List.rev items)

                            st.LetCmd <- savedLet
                            e)

                    if not st.Ok then
                        Unchecked.defaultof<_>
                    elif st.I <> st.Text.Length then
                        refuse st
                    else
                        let curried = curryParams ps rhs

                        let value =
                            if pureMod then
                                {
                                    Kind = EWithin(WithinPure, None, None, None, curried)
                                    Span = curried.Span
                                }
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
    elif guardedHead st then
        refuse st
    else
        let h = headOf st true

        if h.K = HeadK.Refuse then
            refuse st
        elif h.K <> HeadK.NotCmd then
            let e = cmdLine st true 0 None h

            if not st.Ok then Unchecked.defaultof<_>
            elif st.I <> st.Text.Length then refuse st
            else SCmd e
        else
            let e = seqExpr st

            if not st.Ok then Unchecked.defaultof<_>
            elif st.I <> st.Text.Length then refuse st
            else SExpr e

// ---- the entry and its counters --------------------------------------------
let mutable private taken = 0L
let mutable private refused = 0L

/// (statements the fast parser took, statements it refused)
let stats () =
    Threading.Interlocked.Read &taken, Threading.Interlocked.Read &refused

/// The statement's tree, when every piece of it is within this parser's
/// subset and the command grammar could claim none of it; None otherwise,
/// with no trace left (pending anonymous shapes pushed on the way are
/// taken back) — the grammar then parses it from scratch.
let private parse (r: Resolver) (input: string) : Stmt * St =
    if input.IndexOfAny [| '\n'; '\r'; districtClose |] >= 0 then
        Threading.Interlocked.Increment &refused |> ignore

        Unchecked.defaultof<Stmt>,
        {
            Text = input
            R = r
            I = 0
            Ok = false
            Fatal = false
            Depth = 0
            Locals = Set.empty
            AliasMasked = Set.empty
            InArm = 0
            ExprParen = false
            LetCmd = false
            LetCmdStmt = false
            IfCond = false
            IfBody = false
            LastChain = false
            RefusedAt = 0
            PendingAt = 0
            WordAt = -1
            Word = null
        }
    else
        let pending = pendingAnonDefs.Value

        let st = {
            Text = input
            R = r
            I = 0
            Ok = true
            Fatal = false
            Depth = 0
            Locals = Set.empty
            AliasMasked = Set.empty
            InArm = 0
            ExprParen = false
            LetCmd = false
            LetCmdStmt = false
            IfCond = false
            IfBody = false
            LastChain = false
            RefusedAt = -1
            PendingAt = pending.Count
            WordAt = -1
            Word = null
        }

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
