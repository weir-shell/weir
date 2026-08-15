# weir — adversarial review of the shipped claims

Status: PROPOSED (findings-shaped; not blessed, nothing fixed yet). Six
findings, each reproducing on the AOT binary at `e961984`. The repro
harness is `tools/adversarial-repro.weir` — it exits nonzero while any
finding reproduces, so it is both the bug report and the acceptance gate:

    weir tools/adversarial-repro.weir --bin ./path/to/weir

Method: weir's non-claims are stated well enough that attacking them would
be a strawman, so the review attacked only the POSITIVE claims —
SECURITY.md's four defended properties plus the checker soundness the
check-before-effects promise rests on. Each was turned into a falsifiable
property and driven mechanically with an INDEPENDENT oracle where one
exists (PyYAML at the yaml boundary, `json` at the json boundary, an
argv-dumping child for word integrity), never by reading code and
asserting. Corpus: 84 hostile strings (the 1.1/1.2 boolean and null forms,
number-alikes, structural sigils, leading/interior/trailing whitespace,
CR/CRLF/NEL/LS, block-scalar shapes, emoji, long lines).

## The findings

### F1 — the depth guard has a THIRD seam: the type grammar (SAFETY)

`[D:depth-guard]` named unbounded expression depth "a SAFETY bug — a
memory-unsafe SEGV (rc 139) in a safe-by-design language" and closed TWO
seams (in-parser nesting via `deepen`, post-parse spine via the iterative
gate). The TYPE grammar is a third, unguarded:

    type T = { x: seq<seq<seq< ... >>> }      # 20000 deep, 100 KB of source

    weir check -> rc 139 (SIGSEGV, core dumped, AOT)
    weir fmt   -> rc 139
    weir lsp   -> rc 139 on didOpen

Every type-constructor axis nests the same way: `seq<…>`, `Option<…>`,
`Map<string, …>`, and the anonymous-record form `{| a: … |}`. Threshold is
~4548 on a Release JIT build and between 8k and 20k on AOT — stack
dependent, exactly the premise `[D:depth-stack-probe]` was written to
remove.

REACH, and why this is worse than a local crash: `weir lsp` reads
import-reachable files from disk, so no hostile buffer is needed —

    # importer.weir, entirely benign
    import "./evil.weir"
    print "hi"

Opening `importer.weir` kills the server; the editor restarts it and it
dies again. Cloning a repo that carries one hostile module is the whole
attack, and `weir check` in CI dies identically. SECURITY.md Property 3
("No input crashes the process… a machine-checked invariant, not a prose
promise") is false as written.

Why it survived: `tests/Weir.Fuzz/Main.fs:293-295` pins the depth axis with
three seeds — `deepNest "(" ")"`, `opSpine`, `deepNest "[" "]"` — all
expression-side, carrying the comments "was SEGV ~6000" and "was SEGV in
check". The denominator `[D:depth-guard]` called honest never included the
type grammar.

FIX SHAPE: the mechanism already exists — route the type parser's recursive
descent through the same `deepen` that owns `atom`, so it inherits both the
counted ceiling and the stack probe (`[D:depth-stack-probe]`: "the probe
owns SAFETY — any depth, any stack, any platform"). Then extend the
fuzzer's depth seeds to `seq<`/`>`, `Option<`/`>`, `Map<string, `/`>` and
`{| a: `/` |}` per the fuzzer-grammar-membership rule, so the invariant
stays machine-checked rather than re-earned by hand.

DONE WHEN: all four axes yield a located diagnostic at 20000 (never rc
≥128) through `check`, `fmt`, `check --json`, `lsp` didOpen, and the import
path; the seeds are in the fuzzer's depth axis; the SECURITY.md Property-3
sentence names the type grammar as covered.

### F2 — `to yaml` emits a block scalar `from yaml` refuses (never-drop-bytes)

`Eval.fs:1063` (`renderString`) picks literal block form for any
newline-bearing "tame" string. Block-scalar content indentation is detected
from the first non-empty line, so a value whose FIRST LINE BEGINS WITH
WHITESPACE needs an explicit indentation indicator — which
`[D:block-scalars]` deliberately rejects ("explicit indentation indicators
`|2` — content indentation is detected"). The emitter emits block form
anyway:

    let orig = " a\nb"
    { k = orig } |> to yaml        // k: |- / "   a" / "  b"
                 |> from yaml Doc
    error: from yaml: line 3: this line sits left of the block scalar's
           content indentation

That error is the read side's own extent-consistency guard firing on the
write side's own output: weir writes YAML weir refuses to read. PyYAML
refuses it too, so this is malformed output, not a subset disagreement.
Where it does not fail outright it corrupts instead — one level deeper,
`" x\n  y"` round-trips through PyYAML as `"x\n y"`, one leading space
stripped from every line.

NOT INJECTION — checked, and stated so the next reader does not re-derive
it: payloads were crafted to land an escaping line on an enclosing mapping
key, in nested districts at several depths. It FAILS CLOSED every time; the
emitter's base indent means no content line can reach column 0 or align
with an outer key, so a parse error is the worst case and never a forged
key. The README's no-yaml-injection claim stands. What breaks is
correctness and the round trip.

FIX SHAPE: the fallback already exists and `[D:content-bytes]` R1 states
its law — 2+ trailing newlines "FALL BACK to the quoted-with-escapes
spelling: valid, exact, round-trips… every legal string stays renderable".
This is the same law, one case further: extend `renderString`'s block-form
predicate to require that no content line begins with a space or tab, so
these values take the existing quoted path.

DONE WHEN: `to yaml |> from yaml` is the identity for a leading-whitespace
multiline string, pinned both directions, with the hostile-byte fixture
extended to carry one.

### F3 — the read side drops a whitespace-only content line

    let orig = "a\n "               // emits: k: |- / "  a" / "   "
    { k = orig } |> to yaml |> from yaml Doc
    // orig len 3, read-back len 1 — ROUND-TRIP BROKEN, no diagnostic

Here the WRITE side is right and the READ side is wrong: PyYAML round-trips
these bytes correctly. `[D:block-scalars]` says "Content is BYTES both
sides"; the reader drops a trailing whitespace-only content line silently,
which is the never-drop class with no error to catch it.

FIX SHAPE: preserve whitespace-only content lines beyond the detected
content indentation on read. Note the deliberate neighbour so the fix does
not overreach: `[D:content-bytes]` states fmt rendering a whitespace-only
SOURCE line as empty is value-preserving — that is fmt on source text, not
the yaml reader on a runtime value.

DONE WHEN: the round trip is the identity for `"a\n "`, pinned.

### F4 — `.inf` / `.nan` escape the reverse-Norway quoting law

`Yaml.fs:554`'s `ambiguousPlain` covers the boolean and null families;
`looksNumeric` requires a digit after a leading `.`, so `.inf`/`.nan` match
neither gate and emit PLAIN:

    emitted:      k: .inf
    pyyaml reads: {'k': inf}        # the string became a float

weir reads its own output back as a string (finite-only rejects both), so
this is interop-only — but interop is the whole point of the law, which
`[D:yaml-v1]` calls "adversarially pinned": "a YAML reader cannot mis-type
weir's output". For `.inf` it can.

FIX SHAPE: add the `.inf`/`.nan` family (with the casings the boolean set
already carries) to `ambiguousPlain`.

DONE WHEN: both emit quoted, pinned alongside the existing reverse-Norway
cases.

### F5 — `renderScalar` escapes LF but not its siblings

`Yaml.fs:598` escapes `"`, `\`, `\n`, `\t` and nothing else. CR, NEL
(U+0085) and LS (U+2028) are line breaks to a YAML reader:

    emitted:      k: "a\rb"          # raw CR inside the quotes
    pyyaml reads: {'k': 'a b'}       # value silently changed

U+2028 does not even trigger quoting (`Char.IsControl` is false for it), so
it emits plain and PyYAML fails to parse the document at all. The tell that
this is an oversight rather than a policy: the emitter knows to escape LF
and misses its siblings.

FIX SHAPE: emit `\r`, `\N`, `\L`, `\P` escapes, and widen `needsQuote` past
`Char.IsControl` to cover the Unicode line/paragraph separators.

DONE WHEN: every payload in the corpus's CR/NEL/LS group round-trips
through an external reader, pinned.

### F6 — a non-spine block let degrades its reifier into a PATH lookup

`[D:block-let-cmd]` holds the boundary with a ThreadLocal spine flag, TRUE
only along topLet's RHS and its let-in chain: "parens, lambda bodies, and
single-line let-in stay expression-only, pinned". Off the spine the
boundary is not enforced by a teaching error — it is enforced by silent
degradation. Measured:

    | reifier applied          | statement form, every position; top-level
    |                          | let; let in a function body (on the spine)
    | degrades to PATH lookup  | let in an if-body, a within-body, a lambda
    |                          | body

Off the spine the command still RUNS and `| complete` is re-read as the
value-headed pipe `[D:value-headed-pipe]` into an external program of that
name. With a binary named `complete` on PATH:

    [1] |> Seq.iter (fun _ ->
        let r = sh -c "echo payload-data" | complete
        r |> Seq.iter print)

the fake binary runs AND receives `payload-data` on stdin. All four
reifiers behave this way. Two consequences:

- The legal-parse-wrong-meaning class PROCESS names after the silent
  swallow: a reifier KEYWORD becomes a PATH lookup, so what runs is decided
  by ambient PATH rather than by the source — the shape SECURITY.md
  Property 2 exists to deny.
- With nothing on PATH the diagnostic is "unknown command 'complete' — not
  found on PATH… install the tool", which tells the author to install a
  tool named after a language keyword. That is the opposite of naming the
  repair.

Note SKILL.md reads "BLOCK lets inside bodies (and lambda bodies) take the
same command RHS along a top-level let's spine" — the parenthetical and the
qualifier contradict each other, and the pinned design is the qualifier.

FIX SHAPE: make the boundary teach instead of degrade. The machinery
exists — `[D:reifier-family-complete]` already refuses reifiers in four
wrong contexts (sigil/bang/statement/district) with located teaching
errors; this is a fifth cell. A reifier marker terminating a command RHS
off the spine should name the position and the repair (hoist the binding,
or use the statement form), never resolve as a program name. Whether the
position should instead SUPPORT the reifier is a design question for the
bless note; the silent PATH lookup is wrong under either answer.

DONE WHEN: the fifth cell is pinned for all four reifiers across if-body,
within-body and lambda-body; no PATH resolution is attempted for a reifier
name; SKILL.md's sentence is corrected.

## DENOMINATOR — what was attacked and HELD

Recorded so the next review starts past it rather than re-running it.

- Injection safety (Property 1): 84/84 payloads reached the child as
  exactly ONE argv word, byte-identical. Newlines, quotes, `;`, `&&`,
  `$(...)`, glob characters, leading dashes, emoji — all inert.
- The json boundary: 84/84 clean round trips through Python's `json`. The
  contrast with yaml localises the defect: four of six findings are the
  yaml emitter/reader pair, and none are json.
- `Secret`: masked in every renderer reachable — `show`, interpolation
  (refuses), tuple, `Option`, seq, `Map` value, union payload, doubly
  nested record, seq-of-records, and the REPL's separate table renderer.
  `to json` refuses. No leak found.
- Checker soundness, 16 probes aimed at `dev/READ-ORDER.md`'s own debt list
  (`instantiate`, occurs-through-`TNamed`, `envFreeVars`, constructor
  schemes, `isEquatable`): all correctly rejected with precise codes.
  Eq/Show/Ord constraints recurse through tuples, `Option`, nesting, and
  laundering through a generic `let`; function types are UNWRITABLE in
  declared types, which closes the smuggling family by construction rather
  than case by case.
- Resolution integrity (Property 2): PATH overridden to a decoy directory
  through both `Env.ofPairs` + `!e(…)` and `within env` still ran the real
  `/usr/bin/git`. Check-time resolution is genuinely pinned to the spawn.
  (F6 is the one place a name escapes to PATH that should not.)
- Check-before-effects: a file-writing first line followed by a type error
  produced zero side effects.
- yaml dynamic keys: hostile keys through `for (k, v) in pairs` (`a: b`,
  embedded newline, `? a`, `- x`) were all correctly quoted. No key
  injection.

## Instrument honesty

Per the vacuous-probe bar, stated rather than presumed. The harness is LOUD
(nonzero exit while anything reproduces; a probe that fails to run counts
as reproducing) and carries two positive controls: `control-sees-broken`
proves the OK/BROKEN channel can report failure, and
`control-shallow-checks` proves the F1 runner tells a clean check from a
crash. F4 and F5 assert on weir's OWN emitted bytes, because a second YAML
reader is not available to a weir script — the property they name is
interop, so by that measure they are loud but UNCONTROLLED; the
external-parser evidence for both lives in this plan, not in the harness.

The harness itself is written in weir per the scripting policy. Its shape
is dictated by F6: every command-running helper is a top-level function,
because a block let with a command RHS loses its reifier off the spine.
