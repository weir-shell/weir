module Weir.Desugar

// The grammar's shared half [D:hand-parser]: everything both parsers
// (Parser.fs's FParsec grammar and Fast.fs's hand-written one) must
// agree on byte for byte — the keyword list, the identifier classes,
// the sentinels, and the desugars that build the tree. One copy each,
// so the two can never drift apart on what a construct means.

open Weir.Ast

let keywords =
    Set [
        "within"
        // the bare scope's teardown segment [D:within-always]
        "always"
        // the bounded-loop pair and their predicate segment [D:retry-poll]
        "retry"
        "poll"
        // the loop a stop signal ends [D:repeat]
        "repeat"
        "until"
        "let"
        "in"
        "fun"
        "true"
        "false"
        "match"
        "with"
        "type"
        "of"
        "from"
        "to"
        "if"
        "then"
        "else"
        "when"
        "elif"
        "rec"
        "mutable"
        // the purity assertion's two spellings [D:pure-stage1]: the
        // `pure` block head and the `let pure` modifier
        "pure"
        // the read-only assertion [D:pure-stage2]: a `readonly`
        // block head, one tier up from `pure`
        "readonly"
        // the plan/apply capture [D:plan-apply]: a `plan` block head —
        // the third standalone withinKinds head. Reserved so a bare
        // `plan` never resolves as an identifier or command head; with
        // plan/apply in the language, `let plan = …` is a confusing
        // shadow (the `let match =` class). apply/preview stay ordinary
        // members, no reservation.
        "plan"
        // the module system's two words [D:modules-v1]: reserved so a
        // bare `module`/`import` never resolves as an identifier or a
        // command head (keyword-domination)
        "module"
        "import"
        // the general effect loop [D:for-do]
        "for"
        "do"
        // its parallel twin over Seq.piter [D:pfor]
        "pfor"
        // the implicit-match lambda [D:function-keyword]
        "function"
        // foreign control-flow words, reserved so they teach weir's
        // spelling instead of resolving as PATH programs
        // (PLAN-dx-review D4); `def` is an ordinary name whose
        // teaching keys on the statement shape [D:def-shape]
        "while"
        "return"
        "try"
        // the inverse of try [D:orfail-form]: `e |> orFail` turns a
        // failed Result/Option back into a raise; reserved like try
        "orFail"
    ]

// the keyword set, exposed for tooling resolvers (weir check's
// assume-command rule must never claim a keyword head)
let isKeyword (w: string) = keywords.Contains w

type Resolver = {
    IsKnown: string -> bool
    IsCommandCallable: string -> bool
    IsExternal: string -> bool
    ExternalNames: unit -> seq<string>
    // a bare module member's home (where -> Seq) [D:bare-partition]:
    // the strict-context teaching names the qualified spelling
    BareHome: string -> string option
    // a command-head alias [D:command-head-alias], REPL-only: a short
    // name -> (real exe, fixed prefix args). Consulted only in
    // command-head position, before PATH; the `^` sigil skips it.
    // Single-hop by construction (the table stores exes, never aliases).
    // Scripts/-e supply the always-None resolver, so aliases never leak.
    AliasHead: string -> (string * string list) option
}

let isIdentStart (c: char) = System.Char.IsLetter c || c = '_'

let isIdentCont (c: char) =
    System.Char.IsLetter c || (c >= '0' && c <= '9') || c = '_'

// The sibling sentinel [D:sibling-sentinel]: the assembler joins body
// statement-siblings with this instead of ';' so command mode — which
// swallows a user-typed ';' as a bareword arg (the prior-bleed
// teaching, kept) — stops at the machine boundary. Same width as
// " ; " (3 chars) so every span mapping through the segment table is
// byte-identical. Unproduceable: assemble rejects any source line
// carrying it, so it reaches the grammar only from the assembler (as
// the '|' keys do — a token user text cannot form).
[<Literal>]
let sibSep = '\u001F'

let sibSepStr = System.String(sibSep, 1)

// the district content terminator [D:district-terminates]: a district
// (`yaml`/`<<<`) that closes mid-statement — a following `|>`, `in`, or
// sibling — has the assembler append this after its content, so
// districtTail knows where content ends and the expression grammar
// resumes (heredocDistrict is already an opp term, so the pipe composes
// for free). Same unproduceability contract as sibSep: a source line
// carrying it is rejected where text becomes logical lines.
[<Literal>]
let districtClose = '\u001E'

let districtCloseStr = System.String(districtClose, 1)

// the record/list field separator [D:field-sep-sentinel]: the assembler
// joins newline-separated record fields and list elements with this, not
// a bare `;`, so a field value's own `;`-sequencing (a lambda body, a
// block) cannot swallow the separator and the next field. seqExpr does
// not sequence on it (only `;` and sibSep) — it ends the field, and the
// record/list sepBy splits on it. Same unproduceability contract as sibSep.
[<Literal>]
let fieldSep = '\u001D'

let fieldSepStr = System.String(fieldSep, 1)

let mkExpr (kind, span) = { Kind = kind; Span = span }

let binOp op l r = {
    Kind = EBinOp(op, l, r)
    Span = Span.union l.Span r.Span
}

// `x |> try` folds into one node [D:try-form]: the form captures its left
// side, so it is not an application of the left's value
let pipeOp l r =
    match r.Kind with
    | ETryForm asResult -> {
        Kind = ETry(l, asResult)
        Span = Span.union l.Span r.Span
      }
    // and its inverse [D:orfail-form]: the form's own span is the raise site
    | EOrFailForm msg -> {
        Kind = EOrFail(l, msg, r.Span)
        Span = Span.union l.Span r.Span
      }
    | _ -> {
        Kind = EPipe(l, r)
        Span = Span.union l.Span r.Span
      }

let curryParams (ps: Pattern list) (value: Expr) : Expr =
    List.foldBack
        (fun (p: Pattern) body ->
            let kind =
                match p.PKind with
                | PVar n -> ELambda(n, p.PSpan, body)
                | PUnit -> ELambda("()", p.PSpan, body)
                | _ -> ELambdaPat(p, body)

            // span covers the param — binder diagnostics point at
            // it, not at the RHS
            {
                Kind = kind
                Span = Span.union p.PSpan value.Span
            })
        ps
        value

// xs[i] desugars to Seq.item i xs — F# 6 dotless-indexing whitespace
// rule: no space = indexing; a space means application (f [1; 2] stays
// an application of a list). Immediacy is checked against the target's
// span end (spans record positions before trailing whitespace).
let indexDesugar (target: Expr) (idx: Expr) (endPos: Pos) : Expr =
    let span = {
        Start = target.Span.Start
        End = endPos
    }

    let seqItem = { Kind = EVar "|seqItem"; Span = span }

    {
        Kind =
            EApp(
                {
                    Kind = EApp(seqItem, idx)
                    Span = span
                },
                target
            )
        Span = span
    }

// xs[^n] — F#'s from-the-end single index [D:range-slicing]: the element
// n from the end (`^1` is the last), `|seqItemFromEnd n xs` — its own
// desugar, so an out-of-range raise names the `^n` and the length.
let fromEndIndexDesugar (target: Expr) (idx: Expr) (endPos: Pos) : Expr =
    let span = {
        Start = target.Span.Start
        End = endPos
    }

    let fromEnd = {
        Kind = EVar "|seqItemFromEnd"
        Span = span
    }

    {
        Kind =
            EApp(
                {
                    Kind = EApp(fromEnd, idx)
                    Span = span
                },
                target
            )
        Span = span
    }

// [a..s..b] is pure sugar for Seq.range a s b; [a; b; c] stays an eager list.
let rangeExpr (span: Span) (start: Expr) (step: Expr) (stop: Expr) : Expr =
    let rangeFn = { Kind = EVar "|seqRange"; Span = span }

    let app f a = { Kind = EApp(f, a); Span = span }
    app (app (app rangeFn start) step) stop

// a typed `~` is the home directory [D:tilde]: an unquoted word-leading
// `~` or `~/…` in a command line desugars to the run-time home plus the
// rest. Source syntax only — a quoted string, an interpolation or a
// spliced value never reaches here, so data is never re-read as syntax;
// `~user` stays literal
let isTildeWord (w: string) = w = "~" || w.StartsWith "~/"

let tildeExpr (w: string) (span: Span) : Expr =
    let at k = { Kind = k; Span = span }
    let home = at (EApp(at (EVar "|home"), at EUnit))

    if w = "~" then
        home
    else
        at (EBinOp("+", home, at (EStr(w.Substring 1))))

// right-nested ESeq over a non-empty element list — shared by seqExpr
// and topLet's command-first RHS so both fold identically
let foldSeqExpr (all: Expr list) : Expr =
    List.foldBack
        (fun e acc ->
            match acc with
            | None -> Some e
            | Some tail ->
                Some {
                    Kind = ESeq(e, tail)
                    Span = Span.union e.Span tail.Span
                })
        all
        None
    |> Option.defaultWith (fun () -> failwith "foldSeqExpr: empty")

// every named binder in a pattern, walked with an explicit stack: a
// cons chain is flat to read but a deep right spine to walk, and a
// recursive walk would overflow the call stack [D:pattern-width].
// Callers use the names as a set, so the order is immaterial.
let patLeafNames (p: Pattern) : string list =
    let names = System.Collections.Generic.List<string>()
    let stack = System.Collections.Generic.Stack<Pattern>()
    stack.Push p

    while stack.Count > 0 do
        match (stack.Pop()).PKind with
        | PVar n -> names.Add n
        | PTuple ps
        | PSeqList ps -> ps |> List.iter stack.Push
        | PRecord fields -> fields |> List.iter (snd >> stack.Push)
        | PCase(_, Some arg, _) -> stack.Push arg
        | PCons(h, t) ->
            stack.Push h
            stack.Push t
        | PRegex(_, _, _, binder) -> stack.Push binder
        | _ -> ()

    List.ofSeq names

// `| exitCode` streams; capture/discard contexts are destination
// conflicts [D:exit-reifiers] — reject at parse with the teaching text
let rec exitCodeSpine (e: Expr) : bool =
    match e.Kind with
    | EVar("|exitCoded" | "|exitCodedEnv") -> true
    | EApp(f, _) -> exitCodeSpine f
    | _ -> false

// [D:statement-lets] which reifier heads a probed chain — the hardened
// teaching's evidence: a refused-context RHS that parses as a reifier
// chain is a command the position cannot take, whether or not the
// expression grammar would survive its argv (`--flag` heads die there
// before the pipe is ever seen). App spines head the desugar; a post-
// reifier `|>` stage rides an EPipe.
let rec chainReifier (e: Expr) : string option =
    match e.Kind with
    | EVar v -> reifierOfVar v
    | EApp(f, _) -> chainReifier f
    | EPipe(l, r) -> chainReifier r |> Option.orElseWith (fun () -> chainReifier l)
    | _ -> None


type Seg =
    | Stage of Expr
    | CompleteMarker of Span
    // the exit-code reifiers [D:exit-reifiers] — complete's family,
    // one rule (single external segment, nothing follows)
    | SucceedsMarker of Span
    | ExitCodeMarker of Span
    | OrFailMarker of Expr * Span
    // process replacement [D:exec] — the diverging reifier: it never
    // returns (execve replaces the image), so it ends a command chain
    // like the rest of the family
    | ExecMarker of Span
    // the single-line capture [D:reify-line] — `cmd | line` reifies to
    // the one trimmed stdout line as a string
    | LineMarker of Span
    // the whole-output capture [D:reify-text] — `cmd | text` reifies to
    // stdout as one string
    | TextMarker of Span
    // command chaining [D:cmd-chaining] — bash &&/||: the RHS is a full
    // command line (right-associative), captured whole
    | AndMarker of Expr * Span
    | OrMarker of Expr * Span


// fold a parsed pipeline — an initial head expression plus piped stages
// and reifier markers — into one Expr. Shared by the command-headed
// chain and the value-headed chain [D:value-headed-pipe]: the only
// difference is the head (a command segment vs an expression value), so
// the stage/reifier desugar is one function. A reifier's segment must be
// a single external command; a value threaded into it (`xs | grep |
// complete`) carries as the ECmd's stdin position.
// the error carries the offending segment's span [D:anchor-before-read]
// so the caller anchors on it, not the chain's drifted end
// the right-hand side decides the glyph [D:pipe-rhs-decides]: `|` when the
// stage is a program or a reifier (command grammar), `|>` when it is a
// function. A mismatch is the teaching error, anchored on the glyph.
let requiredPipeOp (seg: Seg) : string =
    match seg with
    | Stage { Kind = ECmd _ } -> "|" // a program
    | Stage _ -> "|>" // a function/value stage
    | _ -> "|" // a reifier terminates the command chain

let pipeOpError (got: string) : string =
    if got = "|" then
        "'|' chains commands; pipe expressions with '|>'"
    else
        "'|>' applies functions; feed a program with '|'"

// shared by foldChain's arms [D:cmd-chaining]: a command streams through
// the armed |print (stream + raise); a builtin command (cd) or a nested
// and/or returns a value or unit, so it runs for effect and is discarded
let streamOf (e: Expr) =
    match e.Kind with
    | ECmd _
    | EPipe(_, { Kind = ECmd _ }) -> {
        Kind = EPipe(e, { Kind = EVar "|print"; Span = e.Span })
        Span = e.Span
      }
    | _ -> {
        Kind = ELet("_chain", e.Span, e, { Kind = EUnit; Span = e.Span })
        Span = e.Span
      }

// a chain head is command-ish (an external segment or a command→command
// pipe); a value head is anything else
let isCommandish (e: Expr) =
    match e.Kind with
    | ECmd _
    | EPipe(_, { Kind = ECmd _ }) -> true
    | _ -> false

// a dynamic head rides the desugar as EDynProg — typed string-exactly at
// check, erased [D:dynamic-head]
let progArgOf (h: CmdHead) (span: Span) =
    match h with
    | HeadLit prog -> { Kind = EStr prog; Span = span }
    | HeadDyn(disp, he) -> {
        Kind = EDynProg(disp, he)
        Span = he.Span
      }

let orLeftMsg =
    "'| or' must follow an external command or a pipeline of them — a builtin like cd raises an error instead of returning an exit code"

// a reifier (bar exec, which hands over and never returns) at the end of
// an and/or right-hand side, with the chain word
let chainEndReifier (seg: Seg) : (Expr * string * string) option =
    match seg with
    | AndMarker(rhs, _) -> Some(rhs, "and")
    | OrMarker(rhs, _) -> Some(rhs, "or")
    | _ -> None
    |> Option.bind (fun (rhs, kw) ->
        chainReifier rhs
        |> Option.filter ((<>) "exec")
        |> Option.map (fun n -> rhs, kw, n))

// which chain word a folded and/or right-hand side starts with: `| and`
// folds to a sequence of streamed commands, `| or` to an if on the left's
// exit code [D:and-or-no-mix]
let chainWord (e: Expr) : string option =
    match e.Kind with
    | EIf({
              Kind = EBinOp("==", code, { Kind = EInt 0L })
          },
          { Kind = EUnit },
          Some _) when chainReifier code = Some "exitCode" -> Some "or"
    | ESeq({
               Kind = EPipe(_, { Kind = EVar "|print" })
           },
           _)
    | ESeq({ Kind = ELet("_chain", _, _, _) }, _) -> Some "and"
    | _ -> None

let mixedChain (seg: Seg) : (string * string * Span) option =
    match seg with
    | AndMarker(rhs, sp) when chainWord rhs = Some "or" -> Some("and", "or", sp)
    | OrMarker(rhs, sp) when chainWord rhs = Some "and" -> Some("or", "and", sp)
    | _ -> None

// a capture sigil closes its chain at the ')' — the composition exists
// inside the parens [D:exit-reifiers]: teach the spelling, not just the law
let captureReifierMsg (inner: Expr) (stageName: string) =
    let sigil =
        match inner.Kind with
        | ECmd(_, _, Some { Kind = EVar n })
        | EPipe(_,
                {
                    Kind = ECmd(_, _, Some { Kind = EVar n })
                }) -> "$" + n
        | _ -> "$"

    $"'{stageName}' must directly follow a single external command segment — the capture sigil closes its chain at the ')'; the reifier composes inside the parens: {sigil}(cmd | {stageName})"

let rec foldChain
    (bareHome: string -> string option)
    (h: Expr)
    (rest: ((string * Span) * Seg) list)
    : Result<Expr, string * Span> =
    rest
    |> List.fold
        (fun acc ((op, opSpan), seg) ->
            match acc with
            | Result.Error m -> Result.Error m
            // a bare module member read as a program after `|>` (`cmd | line
            // |> split "/"`) is the bare-name slip, taught as in a value
            // pipe [D:bare-partition]
            | Result.Ok _ when
                op = "|>"
                && (match seg with
                    | Stage { Kind = ECmd(HeadLit n, _, _) } -> (bareHome n).IsSome
                    | _ -> false)
                ->
                match seg with
                | Stage({ Kind = ECmd(HeadLit n, _, _) } as e) ->
                    let home = (bareHome n).Value

                    Result.Error(
                        $"'{n}' is a bare module member — spell it '{home}.{n}' (bare names live in the REPL session)",
                        e.Span
                    )
                | _ -> Result.Error(pipeOpError op, opSpan)
            | Result.Ok _ when op <> requiredPipeOp seg -> Result.Error(pipeOpError op, opSpan)
            | Result.Ok acc when
                (match seg with
                 | AndMarker _
                 | OrMarker _ -> (chainReifier acc).IsSome
                 | _ -> false)
                ->
                // `a | succeeds | and b`: the reifier made a value, and
                // and/or branch on a command's exit — the value would be
                // dropped and the branch never taken [D:cmd-chaining]
                let kw, sp =
                    match seg with
                    | AndMarker(_, sp) -> "and", sp
                    | OrMarker(_, sp) -> "or", sp
                    | _ -> "", acc.Span

                let name = (chainReifier acc).Value

                Result.Error(
                    $"'| {kw}' can't follow '| {name}' — '| {name}' turned the command into a value, and '| {kw}' needs a command's exit; to branch on it, write `if <command> | succeeds then … else …`",
                    sp
                )
            | Result.Ok _ when (mixedChain seg).IsSome ->
                // a mixed chain groups from the right, unlike bash and every
                // precedence convention — refused, not reinterpreted
                // [D:and-or-no-mix]
                let first, second, sp = (mixedChain seg).Value

                Result.Error(
                    $"'| {first}' and '| {second}' can't be mixed in one chain — for a branch, write an if: "
                    + "`if a | succeeds then b else c`",
                    sp
                )
            | Result.Ok _ when (chainEndReifier seg).IsSome ->
                // `a | and b | complete` reads `a | and (b | complete)`: the
                // reifier would see only b and the chain's value is unit, so
                // the record is lost [D:cmd-chaining]
                let rhs, kw, name = (chainEndReifier seg).Value

                Result.Error(
                    $"'| {name}' can't end a '| {kw}' chain — it would apply to the last command only, and the chain's value is discarded; run the commands on separate lines and capture the one you need: `let r = <command> | {name}`",
                    rhs.Span
                )
            | Result.Ok acc ->
                match seg with
                | Stage seg ->
                    Result.Ok {
                        Kind = EPipe(acc, seg)
                        Span = Span.union acc.Span seg.Span
                    }
                | AndMarker(rhs, _) ->
                    // `cmd | and <rest>` = bash &&: stream the left (raising on
                    // failure — the right is then skipped), then the right
                    // [D:cmd-chaining]. The armed |print streams+raises; a nested
                    // and/or is already a unit effect, so it passes through.
                    Result.Ok {
                        Kind = ESeq(streamOf acc, streamOf rhs)
                        Span = Span.union acc.Span rhs.Span
                    }
                | OrMarker(rhs, sp) ->
                    // `cmd | or <rest>` = bash ||: stream the left capturing its
                    // exit code (no raise); on nonzero, stream the right
                    // [D:cmd-chaining]. Right-associative, so the left is one
                    // command or a pipeline, reified through the exitCode family's vars.
                    // the left's exit code is the exitCode reifier folded onto it
                    // — one desugar for a command, an env overlay, piped input,
                    // splats and a pipeline (leftmost failing code)
                    // [D:chain-reifiers]
                    let codeResult =
                        match acc.Kind with
                        | ECmd _
                        | EPipe _ ->
                            foldChain bareHome acc [ ("|", sp), ExitCodeMarker sp ]
                            |> Result.mapError (fun _ -> orLeftMsg, sp)
                        | _ -> Result.Error(orLeftMsg, sp)

                    match codeResult with
                    | Result.Error e -> Result.Error e
                    | Result.Ok codeExpr ->
                        let cond = {
                            Kind = EBinOp("==", codeExpr, { Kind = EInt 0L; Span = sp })
                            Span = sp
                        }

                        Result.Ok {
                            Kind = EIf(cond, { Kind = EUnit; Span = sp }, Some(streamOf rhs))
                            Span = Span.union acc.Span rhs.Span
                        }
                | (CompleteMarker _ | SucceedsMarker _ | ExitCodeMarker _ | OrFailMarker _ | ExecMarker _ | LineMarker _ | TextMarker _ as marker) ->
                    let stageName, mspan, extraArgs =
                        match marker with
                        | CompleteMarker sp -> "complete", sp, []
                        | SucceedsMarker sp -> "succeeds", sp, []
                        | ExitCodeMarker sp -> "exitCode", sp, []
                        | OrFailMarker(msg, sp) -> "orFail", sp, [ msg ]
                        | ExecMarker sp -> "exec", sp, []
                        | LineMarker sp -> "line", sp, []
                        | TextMarker sp -> "text", sp, []
                        // unreachable — the outer arm matched only the seven above
                        | Stage _
                        | AndMarker _
                        | OrMarker _ -> "complete", acc.Span, []

                    // the desugar variables, from the one reifier table
                    // [D:reifier-term]
                    let stem = reifierStem stageName
                    let plainVar, envVar, stdinVar = "|" + stem, $"|{stem}Env", $"|{stem}In"

                    // a reified segment's mixed literal+splat argv denotes a
                    // seq value [D:splat-reifier-chains]: contiguous non-splat
                    // args chunk into list literals, each splat splices its
                    // interior seq whole, folded with Seq.append. Element =
                    // one word carries through the builtin's argv (the same
                    // boundary as spawn-argv-build); ESplat never leaves argv
                    // in the AST. Splat-free argv keeps the plain list node.
                    let argvExpr (args: Expr list) : Expr =
                        let hasSplat =
                            args
                            |> List.exists (fun a ->
                                match a.Kind with
                                | ESplat _ -> true
                                | _ -> false)

                        // each literal chunk rides `|argv`, the checker's marker
                        // for argv words: elements obey the command-argument
                        // law (Secret, Uuid, int, bool), not seq<string>'s
                        // element unification [D:secret]; erased at check
                        let argvList (items: Expr list) (span: Span) = {
                            Kind = EApp({ Kind = EVar "|argv"; Span = span }, { Kind = EList items; Span = span })
                            Span = span
                        }

                        if not hasSplat then
                            argvList args acc.Span
                        else
                            let seqAppend (a: Expr) (b: Expr) =
                                let span = Span.union a.Span b.Span

                                let f = {
                                    Kind = EVar "|seqAppend"
                                    Span = span
                                }

                                {
                                    Kind = EApp({ Kind = EApp(f, a); Span = span }, b)
                                    Span = span
                                }

                            let listOf (chunk: Expr list) =
                                argvList chunk (Span.union (List.head chunk).Span (List.last chunk).Span)

                            let flush chunk parts =
                                match chunk with
                                | [] -> parts
                                | c -> listOf (List.rev c) :: parts

                            let parts, chunk =
                                args
                                |> List.fold
                                    (fun (parts, chunk) a ->
                                        match a.Kind with
                                        // the interior keeps the full `$@...`
                                        // span (diagnostics + tokens)
                                        | ESplat inner -> ({ inner with Span = a.Span } :: flush chunk parts, [])
                                        | _ -> (parts, a :: chunk))
                                    ([], [])

                            match List.rev (flush chunk parts) with
                            | [] -> { Kind = EList []; Span = acc.Span }
                            | first :: rest -> rest |> List.fold seqAppend first

                    match acc.Kind with
                    | ECmd(h, args, cenv) ->
                        let span = Span.union acc.Span mspan

                        // env sigils route through the *Env twins — the same
                        // desugar family, env threaded up front
                        let headVar =
                            match cenv with
                            | Some e -> {
                                Kind = EApp({ Kind = EVar envVar; Span = mspan }, e)
                                Span = mspan
                              }
                            | None -> { Kind = EVar plainVar; Span = mspan }

                        let progArg = progArgOf h acc.Span
                        let argList = argvExpr args

                        let applied =
                            (extraArgs @ [ progArg; argList ])
                            |> List.fold (fun f a -> { Kind = EApp(f, a); Span = span }) headVar

                        Result.Ok applied
                    // a value-headed single external segment [D:value-headed-pipe]:
                    // `xs | grep foo | complete` reifies grep with xs as stdin —
                    // the stdin-carrying twin, value appended. Only when the LHS
                    // is a value: a command→command LHS is the multi-external
                    // case below, rejected as always (the family's single-segment
                    // rule, unchanged).
                    // exec replaces this process — there is no parent left to
                    // feed a piped stdin, so the value-headed route is refused
                    // [D:exec] (the stdin twin is never emitted)
                    | EPipe(stdinE, { Kind = ECmd(_, _, None) }) when not (isCommandish stdinE) && stageName = "exec" ->
                        Result.Error(
                            "'exec' replaces the current process, so it cannot take a piped stdin — there is no parent left to feed it; drop the value pipe (spawn the command instead if you need its input)",
                            mspan
                        )
                    | EPipe(stdinE, { Kind = ECmd(h, args, None) }) when not (isCommandish stdinE) ->
                        let span = Span.union acc.Span mspan
                        let headVar = { Kind = EVar stdinVar; Span = mspan }
                        let progArg = progArgOf h acc.Span
                        let argList = argvExpr args

                        let applied =
                            (extraArgs @ [ progArg; argList; stdinE ])
                            |> List.fold (fun f a -> { Kind = EApp(f, a); Span = span }) headVar

                        Result.Ok applied
                    // a capture sigil closes its chain at the ')' — the
                    // composition exists inside the parens [D:exit-reifiers]:
                    // teach the spelling, not just the law
                    | ECapture inner -> Result.Error(captureReifierMsg inner stageName, mspan)
                    // a reifier after a command chain [D:chain-reifiers]: the
                    // stages ride a list of (prog, argv, env) — each built as a
                    // single reified segment's — with the value head's stdin
                    | EPipe _ ->
                        // flatten left to right; a value may head the chain
                        let rec stagesOf (e: Expr) : (Expr option * Expr list) option =
                            match e.Kind with
                            | ECmd _ -> Some(None, [ e ])
                            | EPipe(l, ({ Kind = ECmd _ } as r)) ->
                                match stagesOf l with
                                | Some(head, segs) -> Some(head, segs @ [ r ])
                                | None when not (isCommandish l) -> Some(Some l, [ r ])
                                | None -> None
                            | _ -> None

                        match stagesOf acc with
                        | Some(_, segs) when stageName = "exec" && segs.Length > 1 ->
                            Result.Error(
                                "'exec' replaces the current process with ONE program — a pipeline cannot; exec a shell that runs it: sh -c \"a | b\" | exec",
                                mspan
                            )
                        | Some(stdinO, segs) when segs.Length > 1 ->
                            let span = Span.union acc.Span mspan
                            let at k = { Kind = k; Span = span }

                            let stage (seg: Expr) =
                                match seg.Kind with
                                | ECmd(h, args, cenv) ->
                                    let envE = cenv |> Option.defaultValue (at (EList []))
                                    at (ETuple [ progArgOf h seg.Span; argvExpr args; envE ])
                                | _ -> seg

                            let stdinE =
                                match stdinO with
                                | Some v -> at (EApp(at (EVar "Some"), v))
                                | None -> at (EVar "None")

                            let chainVar = "|chain" + plainVar.Substring(1, 1).ToUpper() + plainVar.Substring 2

                            Result.Ok(
                                (extraArgs @ [ at (EList(segs |> List.map stage)); stdinE ])
                                |> List.fold (fun f a -> at (EApp(f, a))) (at (EVar chainVar))
                            )
                        | _ -> Result.Error($"'{stageName}' must directly follow a command or a command chain", mspan)
                    | _ -> Result.Error($"'{stageName}' must directly follow a command or a command chain", mspan))
        (Result.Ok h)


// cd (the one command-callable builtin) is APPLIED, not spawned —
// `EApp(EVar "cd", arg)`, defaulting to the home path with no arg
// (a typed `~`, so it resolves at run time [D:tilde-literal]). Used
// by a bare builtin head AND by an alias that resolves to it
// (`#alias up = cd ..`) [D:command-head-alias]: the alias must not
// fall through to HeadLit, which would spawn the external /usr/sbin/cd.
let buildBuiltinApp (name: string) (nameSpan: Span) (callArgs: Expr list) =
    let headVar = { Kind = EVar name; Span = nameSpan }

    let effectiveArgs =
        match callArgs with
        | [] -> [ tildeExpr "~" nameSpan ]
        | _ -> callArgs

    effectiveArgs
    |> List.fold
        (fun acc arg -> {
            Kind = EApp(acc, arg)
            Span = Span.union acc.Span arg.Span
        })
        headVar
