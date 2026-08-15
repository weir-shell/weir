# weir — adversarial review of the shipped claims

Status: PROPOSED (findings-shaped; not blessed, nothing fixed yet). Five
findings, ordered by which stated property they falsify. Every one
reproduces on the AOT binary at `e961984` through the harness:

    weir tools/adversarial-repro.weir --bin ./path/to/weir

The harness exits nonzero while anything reproduces, so it is both the bug
report and the acceptance gate.

Method: weir's non-claims are stated well enough that attacking them would
be a strawman, so the review attacked only the POSITIVE claims —
SECURITY.md's four defended properties plus the checker soundness that the
check-before-effects promise rests on. Each became a falsifiable property
driven mechanically against an INDEPENDENT oracle where one exists (PyYAML
at the yaml boundary, `json` at the json boundary, an argv-dumping child for
word integrity), never by reading code and asserting. Corpus: 84 hostile
strings (1.1/1.2 boolean and null forms, number-alikes, structural sigils,
leading/interior/trailing whitespace, CR/CRLF/NEL/LS, block-scalar shapes,
emoji, long lines).

## F1 — the depth guard covers ONE of three sub-grammars (Property 3)

`[D:depth-guard]` named unbounded expression depth "a SAFETY bug — a
memory-unsafe SEGV (rc 139) in a safe-by-design language" and closed two
seams. It closed them on the EXPRESSION grammar. The TYPE grammar and the
PATTERN grammar are both unguarded:

    type T = { x: seq<seq<...>>> }              # ~4.5k JIT / ~10k AOT -> SEGV
    let v = match 1 with | ((((0)))) -> 0 | _ -> 1   # ~10k -> SEGV

    weir check -> rc 139 (SIGSEGV, core dumped)
    weir fmt   -> rc 139
    weir lsp   -> rc 139 on didOpen

Every constructor on each axis nests the same way. Type side: `seq<…>`,
`Option<…>`, `Map<string, …>`, `{| a: … |}`. Pattern side: parenthesised,
tuple, constructor, list, cons-chain, let-binder, and lambda-param patterns
— all seven crash at 20000. Import chains are FINE to 2000 (checked).

REACH, and why this outranks a local crash: `weir lsp` reads
import-reachable files from disk, so no hostile buffer is needed —

    # importer.weir, entirely benign
    import "./evil.weir"
    print "hi"

Opening `importer.weir` kills the server; the editor restarts it and it dies
again. Cloning a repo carrying one hostile module is the whole attack, and
`weir check` in CI dies identically. SECURITY.md Property 3 ("No input
crashes the process… a machine-checked invariant, not a prose promise") is
false as written.

Why it survived: `tests/Weir.Fuzz/Main.fs:293-295` pins the depth axis with
three seeds — `deepNest "(" ")"`, `opSpine`, `deepNest "[" "]"` — all
expression-side, carrying the comments "was SEGV ~6000" and "was SEGV in
check". The denominator `[D:depth-guard]` called honest covered one
sub-grammar of three.

FIX SHAPE — and the reason to prefer the general one. The mechanism exists:
`[D:depth-stack-probe]` put a stack probe in `deepen` that "owns SAFETY —
any depth, any stack, any platform". Routing the type and pattern parsers
through `deepen` fixes both known axes. But this review found the type
grammar by attacking a NAMED axis and the pattern grammar only by then
asking what else exists — which is the argument against a third patch. The
durable form is a coverage check: **every recursive nonterminal in the
grammar routes through `deepen`**, asserted mechanically rather than
remembered, so axis four is caught by machinery. Then extend the fuzzer's
depth seeds to the type and pattern constructors per the
fuzzer-grammar-membership rule.

THE LIKELY FAILURE MODE, stated because it is easy to miss: fixing the
parser and declaring victory. `[D:depth-guard]` records that the
checker/evaluator tree-walk dies on deep ASTs INDEPENDENTLY of the parser.
F5 below is a live instance that no parse-depth guard can ever see — parse
depth ~1, and `weir check` never returns. The parse fix does not touch it.

DONE WHEN: all eleven constructors (four type, seven pattern) yield a
located diagnostic at 20000, never rc ≥128, through `check`, `fmt`,
`check --json`, `lsp` didOpen and the import path; the coverage check
exists; the seeds are in the fuzzer's depth axis; SECURITY.md Property 3
names what is covered. AND the acceptance is run under a CLOCK, not only an
exit code — a hang is not a crash, so `rc >= 128` cannot see F5's shape.

## F2 — the yaml boundary: one defect, four instances (round-trip + interop)

Treating these as four bugs is the mistake the first draft of this plan
made. They are one defect — **the yaml emitter/reader pair has no external
referee** — and the four instances are what that bought.

| # | instance | referee that catches it |
|---|---|---|
| a | first line begins with whitespace → block scalar weir itself refuses | weir's own reader |
| b | trailing whitespace-only line dropped on read | external only |
| c | `.inf` / `.nan` emit plain → other readers type them as floats | external only |
| d | CR / NEL / LS unescaped in quoted scalars → value changes or fails to parse | external only |

Instance (a): `Eval.fs:1063` picks block form for any newline-bearing "tame"
string, but content indentation is detected from the first non-empty line,
so a leading-whitespace first line needs an explicit indentation indicator —
which `[D:block-scalars]` deliberately rejects. weir emits block form
anyway, and `from yaml` answers with its own extent-consistency guard: "this
line sits left of the block scalar's content indentation". weir writes YAML
weir refuses to read.

Instance (b): the write side is right and the READ side drops the line;
PyYAML round-trips those bytes correctly.

Instances (c) and (d): `Yaml.fs:554`'s `ambiguousPlain` covers the boolean
and null families but not `.inf`/`.nan`, and `renderScalar` (`Yaml.fs:598`)
escapes `"`, `\`, `\n`, `\t` and nothing else. U+2028 does not even trigger
quoting (`Char.IsControl` is false for it), so it emits plain and PyYAML
cannot parse the document at all.

NOT INJECTION — checked, and stated so the next reader does not re-derive
it: payloads were crafted to land an escaping line on an enclosing mapping
key, in nested districts at several depths. It FAILS CLOSED every time; the
emitter's base indent means no content line reaches column 0 or aligns with
an outer key, so a parse error is the worst case and never a forged key. The
README's no-yaml-injection claim stands. What breaks is correctness.

THE PREMISE THIS PLAN FIRST GOT WRONG, corrected because it changes the fix:
the natural reading is "json has an independent oracle and yaml does not,
hence the clustering". Checked — **neither does**. `ci/e2e.sh:3802` validates
yaml with `d |> to yaml |> from yaml Deploy` (weir reading its own output)
plus substring assertions; `to json` is validated the same way
(`ci/e2e.sh:2366`, `:2396`). The only `import json` in the suite parses the
editor grammar inventory and mutates a lockfile. So json is not better
tested — it is one emitter change away from the same exposure, and it came
through 84/84 on the strength of a simpler escape law, not machinery.

FIX SHAPE: the four instances are each a small patch (extend the block-form
predicate to require no content line begins with space or tab, so those
values take the quoted fallback `[D:content-bytes]` R1 already established;
preserve whitespace-only content lines on read; add the `.inf`/`.nan` family
to `ambiguousPlain`; emit `\r`, `\N`, `\L`, `\P` and widen `needsQuote` past
`Char.IsControl`). The DURABLE fix is the referee: an external YAML reader
in CI asserting `to yaml` round-trips through a parser weir did not write —
and the same for `to json`, since the gap is symmetric. `tools/adversarial-repro.weir`
already carries a working one (PyYAML via `sh -c`, with a positive control
that fails on a value the oracle must reject); promoting it into `ci/e2e.sh`
is the change that makes instance five machine-caught.

DONE WHEN: all four instances round-trip through the external reader; the
external referee runs in CI over the hostile corpus for both adapters.

## F3 — a non-spine block let degrades its reifier into a PATH lookup (Property 2)

`[D:block-let-cmd]` holds the block-let command RHS with a ThreadLocal spine
flag, true only along topLet's RHS and its let-in chain: "parens, lambda
bodies, and single-line let-in stay expression-only, pinned". Off the spine
the boundary is not enforced by a teaching error — it is enforced by silent
degradation.

| position | result |
|---|---|
| statement form anywhere; top-level let; let in a function body | reifier applied |
| let in an if-body, a within-body, a lambda body | degrades to PATH lookup |

Off the spine the command still RUNS and `| complete` is re-read as the
value-headed pipe `[D:value-headed-pipe]` into an external program of that
name. Measured with a decoy `complete` on PATH:

    [1] |> Seq.iter (fun _ ->
        let r = sh -c "echo payload-data" | complete
        r |> Seq.iter print)

the decoy runs AND receives `payload-data` on stdin. All four reifiers
behave this way. Two consequences: a reifier KEYWORD becomes a PATH lookup,
so ambient PATH decides what runs — the shape Property 2 exists to deny; and
with nothing on PATH the diagnostic reads "unknown command 'complete' — not
found on PATH… install the tool", telling the author to install a tool named
after a language keyword.

FIX SHAPE: make the boundary teach rather than degrade.
`[D:reifier-family-complete]` already refuses reifiers in four wrong
contexts with located teaching errors; this is a fifth cell. Whether the
position should instead SUPPORT the reifier is a bless-note question — the
silent PATH lookup is wrong under either answer.

DONE WHEN: the fifth cell is pinned for all four reifiers across if-body,
within-body and lambda-body; no PATH resolution is attempted for a reifier
name.

## F4 — a NUL-bearing value truncates at the argv hand-off (Property 1)

Property 1 says a spliced value "arrives at the child process byte-for-byte
as a single argument". The word-integrity half holds everywhere (84/84
payloads, argc always 1). The byte-for-byte half does not, for one byte:

    let v = Str.fromBase64 "YQBi"     // "a<NUL>b", Str.length 3
    python3 argvlen.py $v             // child receives "a" — length 1

Silent truncation, no diagnostic. Isolated: the byte survives in memory
(length 3) and survives `File.write` (`a \0 b` on disk), so weir carries it
faithfully everywhere except the spawn hand-off — this is not a general
string defect.

THE ROUTE IN is a gate that does not mean what it says. `[D:encoding-law]`
states the intent plainly: "`Str.fromBase64` raises on malformed input AND
on valid-base64-of-non-text (a PNG's bytes through GetString would be U+FFFD
corruption wearing a success); `Str.tryFromBase64` = None for BOTH cases."
The implementation gates on UTF-8 VALIDITY, and NUL is valid UTF-8 — so
`Str.tryFromBase64 "AAA="` returns `Some` of a two-NUL string while a real
PNG header correctly returns `None` (both verified). The gate stops binary
that is invalid UTF-8 and passes binary that happens to be valid UTF-8. NUL
is the whole gap, and it is the one byte weir's own binary detector keys on
(`[D:binary-echo]`'s NUL-probe) — weir's decoder and weir's binary detector
disagree about what "binary" means.

FIX SHAPE — and the two answers COMPOSE rather than compete (an earlier
draft of this plan called them mutually exclusive; that was wrong):

1. Tighten the gate so "non-text" includes NUL, matching `[D:encoding-law]`'s
   stated intent and `[D:binary-echo]`'s probe. This costs LESS than it
   appears: it removes no capability weir has, because the text-only posture
   is already decided and genuine binary is already refused. It does mean
   base64→binary waits for the parked BYTES type (`[D:binary-echo]` records
   "live receipt no. 1 for the parked BYTES"), which is where binary
   payloads are supposed to land and which will need its own decoder anyway.
2. Refuse a NUL-bearing value AT THE SPLICE with a located error naming the
   truncation. Worth doing independently of (1), because once BYTES lands
   there will be other routes to a NUL and the argv boundary should hold on
   its own.

(2) IS REQUIRED; (1) is desirable. The boundary should hold on its own
rather than depending on every upstream constructor being careful — once
BYTES lands there will be other routes to a NUL, and an invariant enforced
at the one place the value crosses into argv cannot drift the way a
collection of careful producers can. Same reasoning that put the equality
arm in the type rather than in a Map-plus-order-field: put the invariant
where it cannot drift. So (1) is a gate-consistency fix worth making on its
own merits, and (2) is the one the property actually rests on.

The choice of whether to also do (1) belongs in the bless note, DECIDED
BEFORE implementation rather than during. Silent truncation is wrong under
every answer. Note the harness probe assumes (2); under (1) the string
cannot be constructed, so the probe RAISES and reads as reproducing —
re-point it at the rejection rather than loosening it.

DONE WHEN: a NUL-bearing splice either cannot be constructed or is refused
with a diagnostic; never silently truncated. Property 1's sentence gains the
qualifier it needs either way.

## F5 — the checker has no time bound (Property 3, the post-parse seam)

A 149-byte file, eight lines, parse depth ~1, on which `weir check` never
returns:

    let f0 x = (x, x)
    let f1 x = f0 (f0 x)
    ...                       // each line DOUBLES the inferred type
    let f5 x = f4 (f4 x)
    let v = f5 1

Measured: n=4 → 0.23s, n=5 → still running past 150s. No parse-depth guard
can see this — the source is flat. This is the seam F1's fix will sail past.

NOT A FIDELITY DIVERGENCE, and not a weir-specific defect — checked against
the F# oracle before writing it up, which changed the finding. The same
program in `dotnet fsi`: n=4 in ~2.2s of work, n=5 times out at 180s. Same
cliff, same place; weir is if anything FASTER at n=4 (0.23s). This is the
inherent DEXPTIME property of Hindley-Milner type inference, faithfully
reproduced. `tests/fidelity/divergences.md` should gain no row.

WHY IT IS STILL WEIR'S PROBLEM: F# makes no totality claim; weir does.
Property 3 says "on any input the checker returns a located diagnostic
rather than silently mis-executing". Here it returns NOTHING, forever — and
`weir lsp` inherits the hang, so the editor stops answering rather than
crashing (worse to diagnose than F1, which at least dies loudly). "A crash
is the only wrong answer" (`[D:depth-stack-probe]`) needs its sibling: no
answer is also a wrong answer.

FIX SHAPE: not "make it fast" — that is impossible in general, and the
oracle proves the reference implementation does not manage it either. The
shape is a BUDGET, the same move the depth guard already makes: a bounded
work counter (unification steps, or type-node allocations) that converts
non-termination into a located diagnostic.

THE UNITS NEED CARE. The counter is an implementation detail; the number a
script author SEES must not be, or people tune against it and it becomes a
contract nobody meant to sign. Same property the depth guard's 500 has, and
it wants the same treatment: a stated cost bound, re-askable on a receipt,
never a measured constant presented as a guarantee. The teaching itself
writes in the author's language — this expression's type grew too large —
without naming unification steps.

TWO HANG SHAPES, and only one is reachable today. F5 hangs by growing type
SIZE exponentially. A pathological constraint graph or a non-terminating
occurs check could hang WITHOUT growing anything, and a step budget catches
both — but only the size-growth shape earns the good message. Probed for the
second shape and did NOT find one: seven programs across five families
(generalization/env scan over a 1600-long polymorphic chain, row-constraint
accumulation, a shared tyvar threaded through 1600 bindings, a wide env
where each binding cites all priors, self-application `x x`, nested
self-application, and a row cycle `{ r with a = r }`) are all LINEAR and
fast — 0.06s at n=1600, and the occurs-check pathologies diagnose in ~110ms.
So the only non-termination reachable today is type-size growth.

The corollary for the message: the budget must NOT assert "your type grew
too large" merely because the budget was exhausted, since it cannot prove
that in the general case. Assert it where the size is evidenced, and fall
back to a neutral exhaustion message otherwise — a future second shape
should get an honest generic answer, not a confidently wrong specific one.

DONE WHEN: the inference bomb yields a located diagnostic inside a stated
bound; the message names a bound an author can act on rather than an
internal counter; the fuzzer's totality invariant gains a TIME axis that
looks for ANY non-termination (not the size-growth shape specifically),
since a hang and a crash are different failures and only one is currently
patrolled.

## Documentation defects — and the pattern is NOT what it looks like

Three found by reading, each independently checked against the binary:

1. **SECURITY.md:155** — "TLS verification is ON and there is no `insecure`
   in v1", fifteen lines below the bullet documenting `insecure` as a
   shipped loud opt-in `[D:http-s2]`. Verified: `{ Http.get u with insecure
   = true }` checks clean. Line 155 is stale. It sits in the SECURITY
   document's non-claims list, where it understates the surface a reviewer
   must audit.
2. **SKILL.md** — "BLOCK lets inside bodies (and lambda bodies) take the
   same command RHS": the parenthetical contradicts the qualifier in its own
   sentence, and the pinned design (`[D:block-let-cmd]`) is the qualifier.
   This is F3's documentation face.
3. **SKILL.md** — `fromBase64`/`tryFromBase64` "None on non-text bytes",
   false. This is F4's documentation face.

The tempting diagnosis is that parenthetical asides are where contradictions
live. TESTED, and it does not hold: twelve SKILL.md parenthetical claims
were run against the binary (`f -1` passes -1 as the argument; `f [0]` with a
space applies a list; `Path.dir` gives `""` at the top; `Seq.windowed 0`
raises and a short source gives the empty seq; `fst` on a triple is a type
error; `1.` and `.5` are parse errors; `Seq.reduce` raises on empty;
`Instant.parse` reads a bare date as midnight UTC; `Seq.pmap` returns
ordered results). Eleven of twelve hold.

The real discriminator is COVERAGE, not grammar: `ci/skill-doc.sh` executes
every fenced `weir` block in SKILL.md, so fenced claims cannot drift — and
all three defects are PROSE, which nothing executes. Two of the three are
also stale supersessions (`[D:http-s2]` added `insecure`; `[D:block-let-cmd]`
narrowed the boundary), which is the standing "a change that makes a
spelling redundant sweeps for that spelling" rule not being mechanised.

FIX SHAPE: promote load-bearing prose claims to fences. "Lambda bodies take
a command RHS" and "tryFromBase64 gives None on non-text bytes" are both one
`weir-error` block away from being machine-checked, and a fenced claim is
one an agent following the protocol can trust.

## DENOMINATOR — what was attacked and HELD

Recorded so the next review starts past it rather than re-running it.

- Injection safety (Property 1), word integrity: 84/84 payloads reached the
  child as exactly ONE argv word. Newlines, quotes, `;`, `&&`, `$(...)`,
  glob characters, leading dashes, emoji — all inert. (The byte-for-byte
  half is F4, and only for NUL.)
- The json boundary: 84/84 clean round trips through Python's `json` — on
  the emitter's merits, not on test machinery it does not have.
- `Secret`: masked in every renderer reachable — `show`, interpolation
  (refuses), tuple, `Option`, seq, `Map` value, union payload, doubly nested
  record, seq-of-records, and the REPL's separate table renderer. `to json`
  refuses. No leak found.
- Checker soundness, 16 probes aimed at `dev/READ-ORDER.md`'s own debt list
  (`instantiate`, occurs-through-`TNamed`, `envFreeVars`, constructor
  schemes, `isEquatable`): all correctly rejected with precise codes.
  Eq/Show/Ord recurse through tuples, `Option`, nesting, and laundering
  through a generic `let`; function types are UNWRITABLE in declared types,
  closing the smuggling family by construction rather than case by case.
- Resolution integrity (Property 2): PATH overridden to a decoy directory
  through both `Env.ofPairs` + `!e(…)` and `within env` still ran the real
  `/usr/bin/git`. (F3 is the one cell where a name escapes to PATH.)
- Check-before-effects: a file-writing first line followed by a type error
  produced zero side effects.
- yaml dynamic keys: hostile keys through `for (k, v) in pairs` (`a: b`,
  embedded newline, `? a`, `- x`) all correctly quoted. No key injection.
- Import-chain depth: clean to 2000 modules.
- Type-inference cost BELOW the cliff: n=4 of the F5 bomb checks in 0.23s,
  against ~2.2s for the same program in `dotnet fsi`. weir is not slow here;
  it is unbounded (F5).

## Instrument honesty

Stated rather than presumed, per the vacuous-probe bar. The harness is LOUD
(nonzero exit while anything reproduces; a probe that fails to RUN counts as
reproducing) and carries three positive controls: `control-sees-broken`
proves the OK/BROKEN channel can report failure, `control-shallow-checks`
proves the depth runner tells a clean check from a crash, and
`control-oracle-rejects` proves the external YAML oracle FAILS on a value it
must reject.

F2's interop instances are refereed EXTERNALLY (PyYAML through `sh -c`, the
stated escape for what weir cannot do itself) rather than by weakening them
to something weir can check about its own bytes — the property is interop,
so the oracle has to be foreign. Where PyYAML is absent the harness prints a
named SKIP and keeps the finding OPEN; absence is never a pass.

F5 is measured on a CLOCK, not an exit code, because a hang and a crash are
different failures: the depth probes assert `rc < 128`, which a
non-terminating checker satisfies forever. The probe wraps `check` in
`timeout 20` and treats rc 124 as reproducing. Any acceptance run for F1
that only reads exit codes will pass while F5 is wide open.

The harness is written in weir per the scripting policy. Its shape is
dictated by F3: every command-running helper is a top-level function,
because a block let with a command RHS loses its reifier off the spine.

Probe labels AND binding names match this document's numbering exactly
(`F1a`, `F1b`, `F2a`–`F2d`, `F3`, `F4`, `F5`), so a fixer never translates
between the two documents.

Three instrument defects were found by review of the harness itself and are
recorded because each is a shape that recurs:

- **A control that borrows a finding's payload is not a control.** The oracle
  control originally emitted `.inf` — the same value as F2c — so it passed
  only BECAUSE the finding reproduced, and would have failed the moment F2c
  was fixed, making the gate unreachable. It now asserts a STRUCTURAL
  mismatch (emit `abc`, expect `xyz`), independent of every finding. The
  oracle is verified TWO-SIDED (it accepts `abc`/`abc` and rejects
  `abc`/`xyz`), because a one-sided "correctly rejected" is vacuous.
- **`<> n` is not the complement of a fix.** The budget probe tested
  `exit <> 124`, which a SEGV, an abort, and a missing binary all satisfy —
  it would have reported "fixed" on a crash, in the one file whose subject is
  that crashes are not fixes. Every depth/budget probe now tests `== 1` (a
  located diagnostic) and names the crash and the no-longer-reaching-the-seam
  cases separately.
- **An instrument must be probed, not assumed.** `timeout` is GNU coreutils
  and absent on a stock macOS, where `sh` returns 127 — which the old `<> 124`
  test read as a pass. It is now probed like the oracle, and its absence is a
  named SKIP that keeps F5 OPEN.
