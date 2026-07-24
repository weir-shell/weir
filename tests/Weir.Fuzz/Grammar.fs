module Fuzz.Grammar

// The generator grammar of the assembler fuzzer [D:fuzz-harness].
// Valid-by-construction programs from a combinator grammar of weir's
// LINE SHAPES — the subject is line-shape composition, never type
// complexity, so expression bodies stay trivial (unique print/echo
// markers, small ints, safe strings). The committed coverage statement
// (which shapes this grammar can produce, which it cannot yet) lives
// in tests/fuzz/GRAMMAR.md — keep both in sync.

open System
open FsCheck

// ---------------------------------------------------------------------------
// AST

type VTy =
    | VInt
    | VStr

type Expr =
    | EInt of int
    | EStr of string
    | EVar of string * VTy
    | EBin of string * Expr * Expr // int op int
    | EInterp of (string * Expr) list * string // $"t0 {e0} t1 {e1} tail" — holes are int-typed by construction
    | EField of recVar: string * field: string * VTy
    | EIfElse of Cond * Expr * Expr
    | ESeqLen of string // (xs |> Seq.length)

and Cond =
    | CCmp of string * Expr * Expr // ">" | "==" on ints
    | CLit of bool

// literal-scrutinee match: distinct literal arms, optional guard arm,
// catch-all always last (exhaustive by construction)
type LitArm =
    | ALit of Expr * ArmRhs
    | AGuard of binder: string * Cond * ArmRhs

and ArmRhs =
    | RExpr of Expr
    | RMatch of MatchE // nested, rendered multi-line

and MatchE =
    { Bid: int // the arm group is a reindentable block
      Scrut: Expr
      Arms: LitArm list
      Catch: ArmRhs }

type RecStyle =
    | RInline
    | RStroustrup of bid: int
    | RAligned

type ListStyle =
    | LInline
    | LStroustrup of bid: int
    | LAligned

type Stmt =
    | SLet of string * Expr
    | SLetBlock of string * Block
    | SLetMatch of string * MatchE
    | SLetUnionMatch of name: string * uVar: string * arms: (string * string option * Expr) list
    | SPrint of Expr
    | SIf of bid: int * Cond * Stmt list // unit body
    | STypeRec of string * (string option * string * VTy) list * RecStyle // (Doc attr, field, ty)
    | STypeUnion of string * (string * VTy option) list * multiline: bool * bid: int
    | SRecLet of binder: string * tyName: string * (string * Expr) list * RecStyle
    | SUnionLet of binder: string * case: string * payload: Expr option
    | SListLet of binder: string * VTy * Expr list * ListStyle
    | SPipeLet of bid: int * name: string * src: string * addend: int
    | SEcho of string list
    | SDistrict of bid: int * headed: Cond option * cmds: string list list
    | SCmdLet of binder: string * words: string list
    | SSeqPrint of string // xs |> print (seq<string> binders only)

and Block =
    { Bid: int
      Body: Stmt list
      Result: Expr }

type Program = { Stmts: Stmt list }

// ---------------------------------------------------------------------------
// Rendering. `extra bid` adds uniform indent to that block's lines —
// the re-indent transform IS a render config (offside is relative), so
// transformed programs are well-formed by construction too.

let private atomic =
    function
    | EInt _
    | EStr _
    | EVar _
    | EField _ -> true
    | _ -> false

let rec renderExpr (e: Expr) : string =
    match e with
    | EInt n -> string n
    | EStr s -> $"\"{s}\""
    | EVar(v, _) -> v
    | EBin(op, a, b) -> $"({renderExpr a} {op} {renderExpr b})"
    | EInterp(parts, tail) ->
        let inner =
            parts |> List.map (fun (t, e) -> $"{t}{{{renderExpr e}}}") |> String.concat " "

        $"$\"{inner} {tail}\""
    | EField(r, f, _) -> $"{r}.{f}"
    | EIfElse(c, a, b) -> $"(if {renderCond c} then {renderExpr a} else {renderExpr b})"
    | ESeqLen x -> $"({x} |> Seq.length)"

and renderCond (c: Cond) : string =
    match c with
    | CCmp(op, a, b) -> $"{renderExpr a} {op} {renderExpr b}"
    | CLit b -> (if b then "true" else "false")

let private renderPat (a: LitArm) : string =
    match a with
    | ALit(e, _) -> renderExpr e
    | AGuard(b, c, _) -> $"{b} when {renderCond c}"

// Render configuration: every field is a semantics-NEUTRAL spelling
// choice the ledger claims equivalent, so a transformed program is
// well-formed by construction.
type RenderCfg =
    { Extra: int -> int // re-indent: added indent for that block id
      ExplicitDistrict: int -> bool // district bid -> `!(...)`-per-line spelling
      SigilCmdLet: string -> bool // cmd-let binder -> `$(...)` RHS spelling
      InlineBracket: int -> bool // Stroustrup bid -> inline bracket spelling
      JoinBlock: int -> bool } // block bid -> single-line `;` join

let defaultCfg =
    { Extra = (fun _ -> 0)
      ExplicitDistrict = (fun _ -> false)
      SigilCmdLet = (fun _ -> false)
      InlineBracket = (fun _ -> false)
      JoinBlock = (fun _ -> false) }

// a block is `;`-joinable only when every body statement is a print —
// lets spell `in` inline, and command lines take `;` as a literal argv
// word (both probed)
let joinable (body: Stmt list) =
    body
    |> List.forall (function
        | SPrint _ -> true
        | _ -> false)

let private printArg (e: Expr) =
    match e with
    | EInterp _ -> renderExpr e
    | e when atomic e -> renderExpr e
    | e -> $"({renderExpr e})"

// Lines carry a tag for the span-soundness invariant: true = expression
// territory (an appended bad token must error HERE); false = command
// territory (junk becomes argv, not an error).
let renderTagged (cfg: RenderCfg) (p: Program) : (string * bool) list =
    let sp n = String(' ', n)
    let out = ResizeArray<string * bool>()
    let emit ind (text: string) = out.Add(sp ind + text, true)
    let emitCmd ind (text: string) = out.Add(sp ind + text, false)
    let extra = cfg.Extra

    let tyText =
        function
        | VInt -> "int"
        | VStr -> "string"

    let rec emitMatch (ind: int) (m: MatchE) =
        // head at ind; arms at ind + extra (a group may sit deeper
        // than its head uniformly)
        emit ind $"match {renderExpr m.Scrut} with"
        let armInd = ind + extra m.Bid

        let emitArm (pat: string) (rhs: ArmRhs) =
            match rhs with
            | RExpr e -> emit armInd $"| {pat} -> {renderExpr e}"
            | RMatch inner ->
                emit armInd $"| {pat} ->"
                emitMatch (armInd + 4) inner

        for a in m.Arms do
            emitArm
                (renderPat a)
                (match a with
                 | ALit(_, r) -> r
                 | AGuard(_, _, r) -> r)

        emitArm "_" m.Catch

    let rec emitStmt (ind: int) (s: Stmt) =
        match s with
        | SLet(v, e) -> emit ind $"let {v} = {renderExpr e}"
        | SLetBlock(v, b) when cfg.JoinBlock b.Bid && joinable b.Body ->
            let parts =
                (b.Body
                 |> List.map (function
                     | SPrint e -> $"print {printArg e}"
                     | _ -> failwith "joinable lied"))
                @ [ renderExpr b.Result ]

            emit ind ($"let {v} = " + String.concat " ; " parts)
        | SLetBlock(v, b) ->
            emit ind $"let {v} ="
            let bodyInd = ind + 4 + extra b.Bid

            for st in b.Body do
                emitStmt bodyInd st

            emit bodyInd (renderExpr b.Result)
        | SLetMatch(v, m) ->
            emit ind $"let {v} ="
            emitMatch (ind + 4) m
        | SLetUnionMatch(v, uv, arms) ->
            emit ind $"let {v} ="
            emit (ind + 4) $"match {uv} with"

            for (case, binder, rhs) in arms do
                match binder with
                | Some b -> emit (ind + 4) $"| {case} {b} -> {renderExpr rhs}"
                | None -> emit (ind + 4) $"| {case} -> {renderExpr rhs}"
        | SPrint e -> emit ind $"print {printArg e}"
        | SIf(bid, c, body) when cfg.JoinBlock bid && joinable body ->
            let parts =
                body
                |> List.map (function
                    | SPrint e -> $"print {printArg e}"
                    | _ -> failwith "joinable lied")

            emit ind ($"if {renderCond c} then " + String.concat " ; " parts)
        | SIf(bid, c, body) ->
            emit ind $"if {renderCond c} then"

            for st in body do
                emitStmt (ind + 4 + extra bid) st
        | STypeRec(n, fields, style) ->
            let fieldText (attr, f, ty) =
                match attr with
                | Some d -> $"[<Doc \"{d}\">] {f}: {tyText ty}"
                | None -> $"{f}: {tyText ty}"

            let style =
                match style with
                | RStroustrup bid when cfg.InlineBracket bid -> RInline
                | s -> s

            match style with
            | RInline ->
                let fs = fields |> List.map fieldText |> String.concat "; "
                emit ind $"type {n} = {{ {fs} }}"
            | RStroustrup bid ->
                emit ind $"type {n} = {{"
                let entryInd = ind + 4 + extra bid

                for (attr, f, ty) in fields do
                    match attr with
                    | Some d ->
                        // the attr-dangle spelling: attr line + field line
                        // form ONE entry, both at the anchor column
                        emit entryInd $"[<Doc \"{d}\">]"
                        emit entryInd $"{f}: {tyText ty}"
                    | None -> emit entryInd $"{f}: {tyText ty}"

                emit ind "}"
            | RAligned ->
                emit ind $"type {n} ="
                let anchor = ind + 4 + 2 // "{ " on the opener line

                fields
                |> List.iteri (fun i f ->
                    let text = fieldText f
                    let closing = (if i = fields.Length - 1 then " }" else "")

                    if i = 0 then
                        emit (ind + 4) $"{{ {text}{closing}"
                    else
                        emit anchor $"{text}{closing}")
        | STypeUnion(n, cases, multiline, bid) ->
            let caseText (c, payload) =
                match payload with
                | Some t -> $"{c} of {tyText t}"
                | None -> c

            if multiline then
                emit ind $"type {n} ="

                for c in cases do
                    emit (ind + 4 + extra bid) $"| {caseText c}"
            else
                let cs = cases |> List.map caseText |> String.concat " | "
                emit ind $"type {n} = {cs}"
        | SRecLet(r, _, fvs, style) ->
            let style =
                match style with
                | RStroustrup bid when cfg.InlineBracket bid -> RInline
                | s -> s

            match style with
            | RInline ->
                let fs =
                    fvs |> List.map (fun (f, e) -> $"{f} = {renderExpr e}") |> String.concat "; "

                emit ind $"let {r} = {{ {fs} }}"
            | RStroustrup bid ->
                emit ind $"let {r} = {{"

                for (f, e) in fvs do
                    emit (ind + 4 + extra bid) $"{f} = {renderExpr e}"

                emit ind "}"
            | RAligned ->
                let head = $"let {r} = {{ "
                let anchor = ind + head.Length

                fvs
                |> List.iteri (fun i (f, e) ->
                    let text = $"{f} = {renderExpr e}"
                    let closing = (if i = fvs.Length - 1 then " }" else "")

                    if i = 0 then
                        emit ind $"{head}{text}{closing}"
                    else
                        emit anchor $"{text}{closing}")
        | SUnionLet(u, case, payload) ->
            match payload with
            | Some e ->
                let arg = (if atomic e then renderExpr e else $"({renderExpr e})")
                emit ind $"let {u} = {case} {arg}"
            | None -> emit ind $"let {u} = {case}"
        | SListLet(x, _, elems, style) ->
            let style =
                match style with
                | LStroustrup bid when cfg.InlineBracket bid -> LInline
                | s -> s

            match style with
            | LInline ->
                let es = elems |> List.map renderExpr |> String.concat "; "
                emit ind $"let {x} = [{es}]"
            | LStroustrup bid ->
                emit ind $"let {x} = ["

                for e in elems do
                    emit (ind + 4 + extra bid) (renderExpr e)

                emit ind "]"
            | LAligned ->
                let head = $"let {x} = [ "
                let anchor = ind + head.Length

                elems
                |> List.iteri (fun i e ->
                    let closing = (if i = elems.Length - 1 then " ]" else "")

                    if i = 0 then
                        emit ind $"{head}{renderExpr e}{closing}"
                    else
                        emit anchor $"{renderExpr e}{closing}")
        | SPipeLet(bid, n, src, k) ->
            emit ind $"let {n} ="
            let bodyInd = ind + 4 + extra bid
            emit bodyInd src
            emit bodyInd $"|> Seq.map (fun a -> a + {k})"
            emit bodyInd "|> Seq.length"
        | SEcho words -> emitCmd ind ("echo " + String.concat " " words)
        | SDistrict(bid, headed, cmds) when cfg.ExplicitDistrict bid ->
            // the marker's desugar claim: `!` block = `!(...)` per line
            let line cmd = "!(echo " + String.concat " " cmd + ")"

            match headed with
            | Some c ->
                emit ind $"if {renderCond c} then"

                for cmd in cmds do
                    emitCmd (ind + 4 + extra bid) (line cmd)
            | None ->
                for cmd in cmds do
                    emitCmd ind (line cmd)
        | SDistrict(bid, headed, cmds) ->
            (match headed with
             | Some c -> emitCmd ind $"if {renderCond c} then !"
             | None -> emitCmd ind "!")

            for cmd in cmds do
                emitCmd (ind + 4 + extra bid) ("echo " + String.concat " " cmd)
        | SCmdLet(g, words) ->
            let rhs = "echo " + String.concat " " words

            if cfg.SigilCmdLet g then
                emitCmd ind ("let " + g + " = $(" + rhs + ")")
            else
                emitCmd ind ("let " + g + " = " + rhs)
        | SSeqPrint x -> emit ind $"{x} |> print"

    for s in p.Stmts do
        emitStmt 0 s

    List.ofSeq out

let render (cfg: RenderCfg) (p: Program) : string list = renderTagged cfg p |> List.map fst

let renderPlain (p: Program) : string list = render defaultCfg p


// every reindentable block id in the program, for the transform to pick from
let blockIds (p: Program) : int list =
    let ids = ResizeArray<int>()

    let rec ofRhs r =
        match r with
        | RExpr _ -> ()
        | RMatch m -> ofMatch m

    and ofMatch (m: MatchE) =
        ids.Add m.Bid

        for a in m.Arms do
            ofRhs (
                match a with
                | ALit(_, r) -> r
                | AGuard(_, _, r) -> r
            )

        ofRhs m.Catch

    let rec ofStmt s =
        match s with
        | SLetBlock(_, b) ->
            ids.Add b.Bid
            List.iter ofStmt b.Body
        | SLetMatch(_, m) -> ofMatch m
        | SIf(bid, _, body) ->
            ids.Add bid
            List.iter ofStmt body
        | STypeRec(_, _, RStroustrup bid) -> ids.Add bid
        | SRecLet(_, _, _, RStroustrup bid) -> ids.Add bid
        | SListLet(_, _, _, LStroustrup bid) -> ids.Add bid
        | STypeUnion(_, _, true, bid) -> ids.Add bid
        | SPipeLet(bid, _, _, _) -> ids.Add bid
        | SDistrict(bid, _, _) -> ids.Add bid
        | _ -> ()

    List.iter ofStmt p.Stmts
    List.ofSeq ids

// ---------------------------------------------------------------------------
// defs/uses — names are program-unique, so dependency closure for the
// shrinker is set arithmetic. uses(stmt) excludes names the statement
// defines internally (block-local binders never escape).

let rec private exprUses (e: Expr) : string list =
    match e with
    | EInt _
    | EStr _ -> []
    | EVar(v, _) -> [ v ]
    | EBin(_, a, b) -> exprUses a @ exprUses b
    | EInterp(parts, _) -> parts |> List.collect (snd >> exprUses)
    | EField(r, _, _) -> [ r ]
    | EIfElse(c, a, b) -> condUses c @ exprUses a @ exprUses b
    | ESeqLen x -> [ x ]

and private condUses (c: Cond) : string list =
    match c with
    | CCmp(_, a, b) -> exprUses a @ exprUses b
    | CLit _ -> []

let rec private rhsUses r =
    match r with
    | RExpr e -> exprUses e
    | RMatch m -> matchUses m

and private matchUses (m: MatchE) : string list =
    exprUses m.Scrut
    @ (m.Arms
       |> List.collect (fun a ->
           match a with
           | ALit(e, r) -> exprUses e @ rhsUses r
           | AGuard(b, c, r) -> (condUses c @ rhsUses r) |> List.filter ((<>) b)))
    @ rhsUses m.Catch

let rec stmtDefs (s: Stmt) : string list =
    match s with
    | SLet(v, _)
    | SLetBlock(v, _)
    | SLetMatch(v, _)
    | SLetUnionMatch(v, _, _) -> [ v ]
    | STypeRec(n, _, _) -> [ n ]
    | STypeUnion(n, cases, _, _) -> n :: (cases |> List.map fst)
    | SRecLet(r, _, _, _) -> [ r ]
    | SUnionLet(u, _, _) -> [ u ]
    | SListLet(x, _, _, _) -> [ x ]
    | SPipeLet(_, n, _, _) -> [ n ]
    | SCmdLet(g, _) -> [ g ]
    | SPrint _
    | SIf _
    | SEcho _
    | SDistrict _
    | SSeqPrint _ -> []

let rec stmtUses (s: Stmt) : string list =
    match s with
    | SLet(_, e) -> exprUses e
    | SLetBlock(_, b) ->
        let localDefs = b.Body |> List.collect stmtDefs |> Set.ofList

        (b.Body |> List.collect stmtUses) @ exprUses b.Result
        |> List.filter (fun n -> not (localDefs.Contains n))
    | SLetMatch(_, m) -> matchUses m
    | SLetUnionMatch(_, uv, arms) ->
        uv
        :: (arms
            |> List.collect (fun (case, binder, rhs) ->
                case :: (exprUses rhs |> List.filter (fun n -> Some n <> binder))))
    | SPrint e -> exprUses e
    | SIf(_, c, body) -> condUses c @ (body |> List.collect stmtUses)
    | STypeRec _
    | STypeUnion _
    | SEcho _
    | SCmdLet _ -> []
    | SRecLet(_, ty, fvs, _) -> ty :: (fvs |> List.collect (snd >> exprUses))
    | SUnionLet(_, case, payload) -> case :: (payload |> Option.map exprUses |> Option.defaultValue [])
    | SListLet(_, _, elems, _) -> elems |> List.collect exprUses
    | SPipeLet(_, _, src, _) -> [ src ]
    | SDistrict(_, headed, _) -> headed |> Option.map condUses |> Option.defaultValue []
    | SSeqPrint x -> [ x ]

// ---------------------------------------------------------------------------
// Generator. Scope threads in-scope names by type; Fresh/Marker/Bid are
// global counters (unique names make the shrinker's closure sound).
// Placement rules the probes established: bare command statements
// (echo, standalone districts) are TOP-LEVEL/if-body only — inside a
// let-block body a bare command line becomes the let's command RHS and
// `;`-joins its successor; headed districts (`if ... then !`) are legal
// in nested positions; type declarations are top-level only.

type Scope =
    { Ints: string list
      Strs: string list
      IntSeqs: string list // seq<int> binders
      StrSeqs: string list // seq<string> binders (lists + command lets)
      Recs: (string * (string * VTy) list) list
      RecVals: (string * string) list // binder -> record type
      Unions: (string * (string * VTy option) list) list
      UnionVals: (string * string) list // binder -> union type
      Fresh: int
      Marker: int
      Bid: int }

let emptyScope =
    { Ints = []
      Strs = []
      IntSeqs = []
      StrSeqs = []
      Recs = []
      RecVals = []
      Unions = []
      UnionVals = []
      Fresh = 0
      Marker = 0
      Bid = 0 }

let private freshVal (sc: Scope) =
    $"v{sc.Fresh}", { sc with Fresh = sc.Fresh + 1 }

let private freshTy (sc: Scope) =
    $"T{sc.Fresh}", { sc with Fresh = sc.Fresh + 1 }

let private freshMarker (sc: Scope) =
    $"m{sc.Marker}", { sc with Marker = sc.Marker + 1 }

let private freshBid (sc: Scope) = sc.Bid, { sc with Bid = sc.Bid + 1 }

// state-threaded repetition and plain sequencing (the gen builder has
// no For, and mutables cannot cross its binds)
let rec private genN (n: int) (f: Scope -> Gen<'a * Scope>) (sc: Scope) : Gen<'a list * Scope> =
    if n <= 0 then
        Gen.constant ([], sc)
    else
        gen {
            let! x, sc1 = f sc
            let! xs, sc2 = genN (n - 1) f sc1
            return x :: xs, sc2
        }

let rec private sequenceGen (gs: Gen<'a> list) : Gen<'a list> =
    match gs with
    | [] -> Gen.constant []
    | g :: rest ->
        gen {
            let! x = g
            let! xs = sequenceGen rest
            return x :: xs
        }

let private genSafeWord: Gen<string> =
    gen {
        let! n = Gen.choose (0, 999)
        return $"w{n}"
    }

let rec genExpr (sc: Scope) (ty: VTy) (depth: int) : Gen<Expr> =
    let atoms =
        match ty with
        | VInt ->
            [ 3,
              gen {
                  let! n = Gen.choose (0, 99)
                  return EInt n
              } ]
            @ (if sc.Ints.IsEmpty then
                   []
               else
                   [ 4,
                     gen {
                         let! v = Gen.elements sc.Ints
                         return EVar(v, VInt)
                     } ])
            @ (let intFields =
                [ for (r, tyName) in sc.RecVals do
                      match sc.Recs |> List.tryFind (fst >> (=) tyName) with
                      | Some(_, fields) ->
                          for (f, fty) in fields do
                              if fty = VInt then
                                  yield EField(r, f, VInt)
                      | None -> () ]

               if intFields.IsEmpty then
                   []
               else
                   [ 2, Gen.elements intFields ])
            @ (if sc.IntSeqs.IsEmpty && sc.StrSeqs.IsEmpty then
                   []
               else
                   [ 1,
                     gen {
                         let! x = Gen.elements (sc.IntSeqs @ sc.StrSeqs)
                         return ESeqLen x
                     } ])
        | VStr ->
            [ 3, Gen.map EStr genSafeWord ]
            @ (if sc.Strs.IsEmpty then
                   []
               else
                   [ 4,
                     gen {
                         let! v = Gen.elements sc.Strs
                         return EVar(v, VStr)
                     } ])
            @ (let strFields =
                [ for (r, tyName) in sc.RecVals do
                      match sc.Recs |> List.tryFind (fst >> (=) tyName) with
                      | Some(_, fields) ->
                          for (f, fty) in fields do
                              if fty = VStr then
                                  yield EField(r, f, VStr)
                      | None -> () ]

               if strFields.IsEmpty then
                   []
               else
                   [ 2, Gen.elements strFields ])

    if depth <= 0 then
        Gen.frequency atoms
    else
        let compound =
            match ty with
            | VInt ->
                [ 2,
                  gen {
                      let! op = Gen.elements [ "+"; "*" ]
                      let! a = genExpr sc VInt (depth - 1)
                      let! b = genExpr sc VInt (depth - 1)
                      return EBin(op, a, b)
                  } ]
            | VStr ->
                // interp holes are int-typed by construction (a string
                // literal inside a hole would nest quotes)
                [ 2,
                  gen {
                      let! e = genExpr sc VInt (depth - 1)
                      let! t = genSafeWord
                      let! tail = genSafeWord
                      return EInterp([ t, e ], tail)
                  } ]

        let ifElse =
            [ 1,
              gen {
                  let! c = genCond sc (depth - 1)
                  let! a = genExpr sc ty (depth - 1)
                  let! b = genExpr sc ty (depth - 1)
                  return EIfElse(c, a, b)
              } ]

        Gen.frequency (atoms @ compound @ ifElse)

and genCond (sc: Scope) (depth: int) : Gen<Cond> =
    Gen.frequency
        [ 4,
          gen {
              let! op = Gen.elements [ ">"; "==" ]
              let! a = genExpr sc VInt (max 0 depth)
              let! b = genExpr sc VInt (max 0 depth)
              return CCmp(op, a, b)
          }
          1, Gen.map CLit Arb.generate<bool> ]

let rec genMatch (sc: Scope) (resTy: VTy) (depth: int) : Gen<MatchE * Scope> =
    let genArmRhs (sc: Scope) : Gen<ArmRhs * Scope> =
        let plain =
            gen {
                let! e = genExpr sc resTy 1
                return RExpr e, sc
            }

        if depth > 0 then
            Gen.frequency
                [ 4, plain
                  1,
                  gen {
                      let! inner, sc = genMatch sc resTy (depth - 1)
                      return RMatch inner, sc
                  } ]
        else
            plain

    gen {
        let bid, sc = freshBid sc
        let! scrutTy = Gen.elements [ VInt; VStr ]
        let! scrut = genExpr sc scrutTy 0
        let! nArms = Gen.choose (1, 3)

        let lits =
            match scrutTy with
            | VInt -> [ 0 .. nArms - 1 ] |> List.map EInt
            | VStr -> [ 0 .. nArms - 1 ] |> List.map (fun i -> EStr $"k{i}")

        let! arms, sc =
            genN
                nArms
                (fun sc ->
                    gen {
                        let! r, sc = genArmRhs sc
                        return r, sc
                    })
                sc

        let litArms = List.zip lits arms |> List.map ALit

        let! withGuard = Gen.frequency [ 3, Gen.constant false; 1, Gen.constant true ]

        let! guardArms, sc =
            if withGuard && scrutTy = VInt then
                gen {
                    let b, sc = freshVal sc
                    let! cmpv = Gen.choose (1, 9)
                    let! ge = genExpr { sc with Ints = b :: sc.Ints } resTy 0
                    return [ AGuard(b, CCmp(">", EVar(b, VInt), EInt cmpv), RExpr ge) ], sc
                }
            else
                Gen.constant ([], sc)

        let! catch, sc = genArmRhs sc

        return
            { Bid = bid
              Scrut = scrut
              Arms = litArms @ guardArms
              Catch = catch },
            sc
    }

// inBlock: inside a let-block body (bare command statements and type
// declarations excluded there)
let rec genStmt (sc: Scope) (depth: int) (inBlock: bool) : Gen<Stmt * Scope> =
    let candidates =
        [ // simple let
          yield
              5,
              gen {
                  let v, sc = freshVal sc
                  let! ty = Gen.elements [ VInt; VStr ]
                  let! e = genExpr sc ty 2

                  let sc =
                      match ty with
                      | VInt -> { sc with Ints = v :: sc.Ints }
                      | VStr -> { sc with Strs = v :: sc.Strs }

                  return SLet(v, e), sc
              }

          // print with a unique marker riding along
          yield
              4,
              gen {
                  let m, sc = freshMarker sc
                  let! e = genExpr sc VInt 1
                  return SPrint(EInterp([ m, e ], "")), sc
              }

          if not inBlock then
              // echo (external command statement — top level / if bodies)
              yield
                  2,
                  gen {
                      let m, sc = freshMarker sc
                      let! w = genSafeWord
                      return SEcho [ m; w ], sc
                  }

              // record type declaration
              yield
                  2,
                  gen {
                      let n, sc = freshTy sc
                      let bid, sc = freshBid sc
                      let! nFields = Gen.choose (1, 3)
                      let! styleIx = Gen.elements [ 0; 1; 2 ]

                      let style =
                          match styleIx with
                          | 0 -> RInline
                          | 1 -> RStroustrup bid
                          | _ -> RAligned

                      let! specs =
                          Gen.listOfLength
                              nFields
                              (gen {
                                  let! ty = Gen.elements [ VInt; VStr ]
                                  let! attr = Gen.frequency [ 5, Gen.constant None; 1, Gen.map Some genSafeWord ]
                                  return attr, ty
                              })

                      // field names carry the type's tag: same-field-set
                      // record types make literals ambiguous
                      let fields =
                          specs |> List.mapi (fun i (attr, ty) -> attr, $"F{n.Substring 1}_{i}", ty)

                      return
                          STypeRec(n, fields, style),
                          { sc with
                              Recs = (n, fields |> List.map (fun (_, f, t) -> f, t)) :: sc.Recs }
                  }

              // union type declaration
              yield
                  2,
                  gen {
                      let n, sc = freshTy sc
                      let bid, sc = freshBid sc
                      let! nCases = Gen.choose (2, 3)

                      let! payloads =
                          Gen.listOfLength
                              nCases
                              (Gen.frequency
                                  [ 2, Gen.constant None
                                    1, Gen.constant (Some VInt)
                                    1, Gen.constant (Some VStr) ])

                      let cases = payloads |> List.mapi (fun i p -> $"C{sc.Fresh}_{i}", p)
                      let sc = { sc with Fresh = sc.Fresh + 1 }
                      let! multiline = Arb.generate<bool>

                      return
                          STypeUnion(n, cases, multiline, bid),
                          { sc with
                              Unions = (n, cases) :: sc.Unions }
                  }

              // standalone district
              yield
                  1,
                  gen {
                      let bid, sc = freshBid sc

                      let! n = Gen.choose (1, 3)

                      let! cmds, sc =
                          genN
                              n
                              (fun sc ->
                                  gen {
                                      let m, sc = freshMarker sc
                                      let! w = genSafeWord
                                      return [ m; w ], sc
                                  })
                              sc

                      return SDistrict(bid, None, cmds), sc
                  }

          // record literal (needs a record type in scope)
          if not sc.Recs.IsEmpty then
              yield
                  3,
                  gen {
                      let! (tyName, fields) = Gen.elements sc.Recs
                      let r, sc = freshVal sc
                      let bid, sc = freshBid sc
                      let! styleIx = Gen.elements [ 0; 1; 2 ]

                      let style =
                          match styleIx with
                          | 0 -> RInline
                          | 1 -> RStroustrup bid
                          | _ -> RAligned

                      let! fvs =
                          sequenceGen (fields |> List.map (fun (f, ty) -> Gen.map (fun e -> f, e) (genExpr sc ty 1)))

                      return
                          SRecLet(r, tyName, fvs, style),
                          { sc with
                              RecVals = (r, tyName) :: sc.RecVals }
                  }

          // union value
          if not sc.Unions.IsEmpty then
              yield
                  2,
                  gen {
                      let! (tyName, cases) = Gen.elements sc.Unions
                      let u, sc = freshVal sc
                      let! (case, payloadTy) = Gen.elements cases

                      let! payload =
                          match payloadTy with
                          | Some t -> Gen.map Some (genExpr sc t 0)
                          | None -> Gen.constant None

                      return
                          SUnionLet(u, case, payload),
                          { sc with
                              UnionVals = (u, tyName) :: sc.UnionVals }
                  }

          // full-coverage match over a union value
          if not sc.UnionVals.IsEmpty then
              yield
                  2,
                  gen {
                      let! (uv, tyName) = Gen.elements sc.UnionVals

                      let cases =
                          sc.Unions
                          |> List.tryFind (fst >> (=) tyName)
                          |> Option.map snd
                          |> Option.defaultValue []

                      let v, sc = freshVal sc

                      // arms threaded explicitly (payload binders extend scope
                      // only inside their own arm)
                      let rec buildArms cs sc =
                          match cs with
                          | [] -> Gen.constant ([], sc)
                          | (case, payloadTy) :: rest ->
                              gen {
                                  let! arm, sc =
                                      match payloadTy with
                                      | Some VInt ->
                                          gen {
                                              let b, sc = freshVal sc
                                              let! e = genExpr { sc with Ints = b :: sc.Ints } VStr 1
                                              return (case, Some b, e), sc
                                          }
                                      | Some VStr ->
                                          gen {
                                              let b, sc = freshVal sc
                                              let! e = genExpr { sc with Strs = b :: sc.Strs } VStr 1
                                              return (case, Some b, e), sc
                                          }
                                      | None ->
                                          gen {
                                              let! e = genExpr sc VStr 1
                                              return (case, None, e), sc
                                          }

                                  let! more, sc = buildArms rest sc
                                  return arm :: more, sc
                              }

                      let! arms, sc = buildArms cases sc
                      return SLetUnionMatch(v, uv, arms), { sc with Strs = v :: sc.Strs }
                  }

          // list literal
          yield
              3,
              gen {
                  let x, sc = freshVal sc
                  let bid, sc = freshBid sc
                  let! ty = Gen.elements [ VInt; VStr ]
                  let! n = Gen.choose (1, 4)
                  let! styleIx = Gen.elements [ 0; 1; 2 ]

                  let style =
                      match styleIx with
                      | 0 -> LInline
                      | 1 -> LStroustrup bid
                      | _ -> LAligned

                  let! elems = Gen.listOfLength n (genExpr sc ty 1)

                  let sc =
                      match ty with
                      | VInt -> { sc with IntSeqs = x :: sc.IntSeqs }
                      | VStr -> { sc with StrSeqs = x :: sc.StrSeqs }

                  return SListLet(x, ty, elems, style), sc
              }

          // pipeline over an int list
          if not sc.IntSeqs.IsEmpty then
              yield
                  2,
                  gen {
                      let! src = Gen.elements sc.IntSeqs
                      let n, sc = freshVal sc
                      let bid, sc = freshBid sc
                      let! k = Gen.choose (1, 9)
                      return SPipeLet(bid, n, src, k), { sc with Ints = n :: sc.Ints }
                  }

          // seq<string> |> print
          if not sc.StrSeqs.IsEmpty then
              yield
                  1,
                  gen {
                      let! x = Gen.elements sc.StrSeqs
                      return SSeqPrint x, sc
                  }

          // command-backed let (legal in blocks too — the spine flag)
          yield
              1,
              gen {
                  let g, sc = freshVal sc
                  let m, sc = freshMarker sc
                  let! w = genSafeWord
                  return SCmdLet(g, [ m; w ]), { sc with StrSeqs = g :: sc.StrSeqs }
              }

          // literal-scrutinee match as a let RHS (binder stays out of the
          // pools: its arm type is local to the match)
          yield
              2,
              gen {
                  let v, sc = freshVal sc
                  let! resTy = Gen.elements [ VInt; VStr ]
                  let! m, sc = genMatch sc resTy (min depth 1)
                  return SLetMatch(v, m), sc
              }

          if depth > 0 then
              // block-bodied let
              yield
                  3,
                  gen {
                      let v, sc = freshVal sc
                      let bid, sc = freshBid sc
                      let! nBody = Gen.choose (1, 3)
                      let! body, scB = genN nBody (fun sc -> genStmt sc (depth - 1) true) sc
                      let! resTy = Gen.elements [ VInt; VStr ]
                      let! res = genExpr scB resTy 1

                      // pop block-local bindings, keep the counters
                      let scOut =
                          { sc with
                              Fresh = scB.Fresh
                              Marker = scB.Marker
                              Bid = scB.Bid }

                      let scOut =
                          match resTy with
                          | VInt -> { scOut with Ints = v :: scOut.Ints }
                          | VStr -> { scOut with Strs = v :: scOut.Strs }

                      return SLetBlock(v, { Bid = bid; Body = body; Result = res }), scOut
                  }

              // if with a unit body
              yield
                  3,
                  gen {
                      let bid, sc = freshBid sc
                      let! c = genCond sc 0
                      let! nBody = Gen.choose (1, 3)
                      let! body, scB = genN nBody (fun sc -> genUnitStmt sc (depth - 1) inBlock) sc

                      return
                          SIf(bid, c, body),
                          { sc with
                              Fresh = scB.Fresh
                              Marker = scB.Marker
                              Bid = scB.Bid }
                  }

          // headed district (legal in nested positions too)
          yield
              2,
              gen {
                  let bid, sc = freshBid sc
                  let! c = genCond sc 0
                  let! n = Gen.choose (1, 3)

                  let! cmds, sc =
                      genN
                          n
                          (fun sc ->
                              gen {
                                  let m, sc = freshMarker sc
                                  let! w = genSafeWord
                                  return [ m; w ], sc
                              })
                          sc

                  return SDistrict(bid, Some c, cmds), sc
              } ]

    Gen.frequency candidates

// unit-typed statements only (if bodies). Every if body is expression
// territory — a bare command line is an unbound variable there, at any
// nesting; districts are THE command spelling in bodies.
and genUnitStmt (sc: Scope) (depth: int) (inBlock: bool) : Gen<Stmt * Scope> =
    let candidates =
        [ yield
              4,
              gen {
                  let m, sc = freshMarker sc
                  let! e = genExpr sc VInt 1
                  return SPrint(EInterp([ m, e ], "")), sc
              }

          yield
              1,
              gen {
                  let bid, sc = freshBid sc
                  let! c = genCond sc 0
                  let! n = Gen.choose (1, 2)

                  let! cmds, sc =
                      genN
                          n
                          (fun sc ->
                              gen {
                                  let m, sc = freshMarker sc
                                  let! w = genSafeWord
                                  return [ m; w ], sc
                              })
                          sc

                  return SDistrict(bid, Some c, cmds), sc
              }

          if depth > 0 then
              yield
                  1,
                  gen {
                      let bid, sc = freshBid sc
                      let! c = genCond sc 0
                      let! st, sc = genUnitStmt sc (depth - 1) inBlock
                      return SIf(bid, c, [ st ]), sc
                  } ]

    Gen.frequency candidates

let genProgram: Gen<Program> =
    Gen.sized (fun size ->
        gen {
            let! nTop = Gen.choose (2, 2 + min 8 (size / 8))
            let! stmts, _ = genN nTop (fun sc -> genStmt sc 2 false) emptyScope
            return { Stmts = stmts }
        })

// ---------------------------------------------------------------------------
// Shrinker: delta debugging on top-level statements with dependency
// closure — dropping a statement also drops every later statement that
// (transitively) uses one of its defs, so every shrink stays
// valid-by-construction. Inner-block shrinking is out of scope
// (recorded in GRAMMAR.md).

let shrinkProgram (p: Program) : seq<Program> =
    seq {
        for i in 0 .. p.Stmts.Length - 1 do
            let mutable droppedDefs = Set.ofList (stmtDefs p.Stmts[i])
            let kept = ResizeArray<Stmt>()

            for j in 0 .. p.Stmts.Length - 1 do
                if j <> i then
                    let s = p.Stmts[j]

                    if stmtUses s |> List.exists droppedDefs.Contains then
                        droppedDefs <- Set.union droppedDefs (Set.ofList (stmtDefs s))
                    else
                        kept.Add s

            if kept.Count < p.Stmts.Length then
                yield { Stmts = List.ofSeq kept }
    }

// ---------------------------------------------------------------------------
// Transforms (invariant 1) and mutators (invariant 2). Blank and
// comment insertion are LINE surgery on the rendered program (the laws
// claim total transparency — any position is fair); re-indent is a
// render config (offside is relative).

module Transform =
    let insertBlanks (rnd: Random) (lines: string list) : string list =
        let n = 1 + rnd.Next 3
        let mutable ls = lines

        for _ in 1..n do
            let at = rnd.Next(ls.Length + 1)
            ls <- List.insertAt at "" ls

        ls

    let insertComments (rnd: Random) (lines: string list) : string list =
        let n = 1 + rnd.Next 3
        let mutable ls = lines

        for _ in 1..n do
            let at = rnd.Next(ls.Length + 1)
            let indent = String(' ', rnd.Next 13)
            ls <- List.insertAt at $"{indent}// fuzz noise {at}" ls

        ls

    // site collectors for the spelling transforms
    let private allStmts (p: Program) : Stmt list =
        let acc = ResizeArray<Stmt>()

        let rec go s =
            acc.Add s

            match s with
            | SLetBlock(_, b) -> List.iter go b.Body
            | SIf(_, _, body) -> List.iter go body
            | _ -> ()

        List.iter go p.Stmts
        List.ofSeq acc

    let districtBids (p: Program) =
        allStmts p
        |> List.choose (function
            | SDistrict(bid, _, _) -> Some bid
            | _ -> None)

    let cmdLetBinders (p: Program) =
        allStmts p
        |> List.choose (function
            | SCmdLet(g, _) -> Some g
            | _ -> None)

    let bracketBids (p: Program) =
        allStmts p
        |> List.choose (function
            | STypeRec(_, _, RStroustrup bid) -> Some bid
            | SRecLet(_, _, _, RStroustrup bid) -> Some bid
            | SListLet(_, _, _, LStroustrup bid) -> Some bid
            | _ -> None)

    let joinBids (p: Program) =
        allStmts p
        |> List.choose (function
            | SLetBlock(_, b) when joinable b.Body -> Some b.Bid
            | SIf(bid, _, body) when joinable body -> Some bid
            | _ -> None)

    // a random nonempty subset of the applicable sites
    let private pickSites (rnd: Random) (xs: 'a list) : 'a list =
        match xs with
        | [] -> []
        | _ ->
            let chosen = xs |> List.filter (fun _ -> rnd.Next 2 = 0)

            if chosen.IsEmpty then
                [ xs[rnd.Next xs.Length] ]
            else
                chosen

    let private withSites (rnd: Random) (sites: 'a list) (build: ('a -> bool) -> RenderCfg) (p: Program) =
        match sites with
        | [] -> None
        | _ ->
            let s = Set.ofList (pickSites rnd sites)
            Some(render (build s.Contains) p)

    // None when the program has no reindentable block
    let reindent (rnd: Random) (p: Program) : string list option =
        match blockIds p with
        | [] -> None
        | ids ->
            let bid = ids[rnd.Next ids.Length]
            let k = 1 + rnd.Next 6

            Some(
                render
                    { defaultCfg with
                        Extra = (fun b -> if b = bid then k else 0) }
                    p
            )

    // district marker form <-> explicit `!(...)` lines (the desugar claim)
    let districtSigil (rnd: Random) (p: Program) : string list option =
        withSites rnd (districtBids p) (fun f -> { defaultCfg with ExplicitDistrict = f }) p

    // bare command RHS <-> `$(...)` (the pinned equivalence, at scale)
    let cmdSigil (rnd: Random) (p: Program) : string list option =
        withSites rnd (cmdLetBinders p) (fun f -> { defaultCfg with SigilCmdLet = f }) p

    // Stroustrup <-> inline bracket style
    let bracketStyle (rnd: Random) (p: Program) : string list option =
        withSites rnd (bracketBids p) (fun f -> { defaultCfg with InlineBracket = f }) p

    // block siblings <-> single-line `a ; b` (the assembler's join claim;
    // print-only bodies — the probed boundary)
    let joinSiblings (rnd: Random) (p: Program) : string list option =
        withSites rnd (joinBids p) (fun f -> { defaultCfg with JoinBlock = f }) p

    // everything at once: random subsets of every spelling flip + one
    // re-indent, then comment and blank surgery over the result — the
    // laws must hold under COMPOSITION
    let composedAll (rnd: Random) (p: Program) : string list =
        let sub (xs: 'a list) =
            Set.ofList (xs |> List.filter (fun _ -> rnd.Next 2 = 0))

        let districts = sub (districtBids p)
        let cmdlets = sub (cmdLetBinders p)
        let brackets = sub (bracketBids p)
        let joins = sub (joinBids p)

        let extraBid, k =
            match blockIds p |> List.filter (fun b -> not (joins.Contains b)) with
            | [] -> -1, 0
            | ids -> ids[rnd.Next ids.Length], 1 + rnd.Next 6

        let cfg =
            { Extra = (fun b -> if b = extraBid then k else 0)
              ExplicitDistrict = districts.Contains
              SigilCmdLet = cmdlets.Contains
              InlineBracket = brackets.Contains
              JoinBlock = joins.Contains }

        insertBlanks rnd (insertComments rnd (render cfg p))

module Mutate =
    let deleteLine (rnd: Random) (lines: string list) : string list =
        if lines.IsEmpty then
            lines
        else
            List.removeAt (rnd.Next lines.Length) lines

    let duplicateLine (rnd: Random) (lines: string list) : string list =
        if lines.IsEmpty then
            lines
        else
            let at = rnd.Next lines.Length
            List.insertAt at lines[at] lines

    let swapLines (rnd: Random) (lines: string list) : string list =
        if lines.Length < 2 then
            lines
        else
            let at = rnd.Next(lines.Length - 1)

            lines |> List.updateAt at lines[at + 1] |> List.updateAt (at + 1) lines[at]

    let perturbIndent (rnd: Random) (lines: string list) : string list =
        if lines.IsEmpty then
            lines
        else
            let at = rnd.Next lines.Length
            let line = lines[at]
            let indent = line.Length - line.TrimStart(' ').Length
            let delta = [ -3; -2; -1; 1; 2; 3 ][rnd.Next 6]
            let newIndent = max 0 (indent + delta)
            lines |> List.updateAt at (String(' ', newIndent) + line.TrimStart ' ')
