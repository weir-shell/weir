# weir — adversarial review of the developer experience

Status: PROPOSED (findings-shaped; not blessed, nothing fixed yet). Nine
findings, all reproducing on the AOT binary at `e961984` — D1-D8 from the
review, D9 measured while answering rider 2. Gate:

    weir tools/dx-repro.weir --bin ./path/to/weir

AMENDED BY TWO RIDERS. Rider 1 (D1): `#loose` is DISPOSED AS A REMOVAL, not a
fix, and `weir fmt --qualify` goes with it. Rider 2 (D9): `-e` takes a PROGRAM
and is STRICT — which closes rider 1's open `Program.fs:10` question. Neither
rider's work is done here; both are recorded for the executing session. Read
D1's disposition before touching the gate — **the committed `D1-loose-bare-names` probe is mis-pointed under that
disposition** (it asserts `mustCheckClean`, which can only go green if the
mode is implemented, and its inverse would pass on an untouched tree). It is
left as-committed deliberately: re-pointing it is execution work, and this
document is findings-only. D2-D8 are unaffected and their probes stand.

Method: DX is where "adversarial" most easily degrades into taste, so the
review was built to produce NUMBERS a later session can re-measure. Three
instruments, in descending order of evidential weight:

1. **Task attempts.** Five realistic scripts written the way the docs teach,
   run, and graded on whether they check AND do what was meant.
2. **A mistake census.** 28 realistic wrong-first-tries — bash, F#, Python
   priors plus weir-specific slips — each graded mechanically into
   teaches / plain / dump / silent by `tools/dx-message-census.weir`.
3. **A friction receipt.** The 326-line `tools/adversarial-repro.weir` was
   written from the docs the day before. Every wall it hit is recorded here
   with what the wall cost, which is the one thing a synthetic probe cannot
   produce.

Standing caveat on the numbers: the author had already read SKILL.md end to
end. These counts are a FLOOR on newcomer friction, never a ceiling.

## What is good, measured first

The denominator, recorded so the next reviewer starts past it and so the
findings below are read at their true weight — this is a strong DX baseline
with specific holes, not a rough one.

- **The edit loop is effectively instant.** `check` 12-18ms, run 13ms on the
  AOT binary. Nothing in this review was slowed by the tool.
- **Naive attempts mostly just work.** Of five realistic tasks (five largest
  files; JSON config into a record; retry a flaky command; count commits per
  author; a script with flags), FOUR checked clean on the first attempt and
  produced correct output unedited. The fifth failed with a model diagnostic
  (below).
- **Most mistakes are diagnosed, and the good ones are very good.** Measured
  by `tools/dx-message-census.weir` over 28 realistic wrong-first-tries:
  **teaches 11 / plain 10 / dump 6 / silent 1**. `plain` = a correct, located
  error naming no repair; it is a MIXED bucket, because the phrase-list proxy
  also lands genuinely explanatory messages there (non-exhaustive-match and
  operator-precedence both teach well and both score `plain`), so 11 is a
  FLOOR. Two earlier passes of this same census reported 19 and then 14 — the
  first folded `plain` into `teaches`, the second graded the whole output so a
  CASCADING secondary error could carry a teach phrase and rescue a case whose
  real message was a dump. Each correction made the instrument stricter and
  the number smaller; 11 is what the committed tool reproduces. Several
  messages are exemplary:
  - `retry` without `until`: *"retry without an until segment needs a bool
    body (the body IS the predicate); this one yields seq<string> — add
    `until r` to bind the value, or end the body with a condition"* — states
    the rule, the actual type, and TWO repairs.
  - `xs |> Seq.length == 2`: names the misparse and the fix.
  - `let Total = 1`: the casing law, by name.
  - `--file=$f`: *"a splice cannot join a word under construction"*.
  - `|> ignore`: *"'|>' applies functions; feed a program with '|'"*.
- **A retired-name registry exists and works.** `Seq.filter` → *"is retired:
  weir's filter is 'Seq.where' — one name per operation"*; `Seq.flatMap` →
  *"F# parity names it 'Seq.collect'"*. This is the right mechanism for
  prior-bleed and it is already built.
- **`#help` is excellent.** `#help Seq.chunkBySize` returns signature, prose,
  and a RUNNABLE example. Receipt: the arg order this author got wrong while
  writing the harness (`Str.sub`) is answered exactly by `#help Str.sub`. The
  information was one question away; the failure was not asking.
- **did-you-mean works on both fields and module members** — `r.bytse` →
  `bytes`, `Str.toUpperr` → `toUpper`, `Seq.lenght` → `length`.
- **Typed values render well** where a shell would give bytes: `$"{f.bytes}"`
  prints `2.9 KiB`, durations and instants likewise.

## D1 — `#loose` does not work in `check` or run (HIGH)

SKILL.md: *"Bare names (`map`, `where`, `sortBy`) exist only in the REPL and
`#loose` scripts."* The REPL half is true. The script half is not:

    #loose
    let xs = [1; 2; 3]
    xs |> where (fun n -> n > 1) |> Seq.iter (fun n -> print $"{n}")

    error [parse]: '|>' applies functions; feed a program with '|'

Identical to the strict-mode error — `#loose` changes nothing. Unpiped is no
better: bare `map` resolves as a PROGRAM (*"command not found on PATH: map.
Did you mean 'YMap'?"*). Controls: the same file with `Seq.where` checks
clean in both modes, and the same pipeline in the REPL works
(`xs |> where (fun n -> n > 1)` → `[2; 3]`).

Three parts of the toolchain believe the feature exists. `Script.fs:1923`
carries the branch (`| Loose -> Builtins.typeEnv`). `fmt --qualify` not only
accepts the file but correctly rewrites it — *"1 name(s) qualified; #loose
directive removed"*, producing valid strict source. Only the checker refuses,
because the parse-time command-vs-expression classification runs before mode
is consulted.

WHY IT SHIPPED — the whole root cause is one fixture, `ci/e2e.sh:377`:

    cat > loose.weir <<'WEOF'
    #loose
    [2; 1] |> where (...) |> map (...) |> first 1 |> sum |> print
    WEOF
    $BIN fmt --qualify loose.weir      # converts to STRICT, strips #loose
    out=$($BIN loose.weir)             # runs the CONVERTED file
    expect "fmt --qualify graduates loose to strict-clean" "6" "$out"

The test writes a loose script, converts it, and runs the conversion. It
never checks or runs a loose script AS loose. `#loose` has zero fixtures in
`tests/Weir.Tests/Tests.fs`. The named test passes from birth while the mode
it names has never executed — the vacuous-probe genus, in its
measures-the-wrong-thing form.

FIX SHAPE: make the classifier consult mode (or, if `#loose` is meant to be
retired, retire it loudly — remove it from SKILL.md, make the directive a
located error, and drop `fmt --qualify`'s reason to exist). Either answer is
defensible; the current state, where three components support a mode the
checker rejects, is not. A fixture that checks and runs a loose script
without converting it first is the acceptance.

## D9 — `-e` parses as a program, and is strict (rider 2)

Amends rider 1, which left `-e` open after `Program.fs:10` turned out to take
the bare env.

TWO DECISIONS, AND THE ORDER MATTERS.

1. **`-e` takes a program**, not a single statement. Newlines are statement
   boundaries exactly as in a file.
2. **`-e` is therefore strict** — no bare module members. `Builtins.typeEnv`
   stays for the REPL; `-e` moves to `typeEnvStrict` alongside files.

(2) follows from (1). An earlier draft ruled `-e` bare-name-friendly because
it is the REPL's non-interactive twin and anything complex should be a script.
That reasoning depended on `-e` input being trivially small. It isn't, and
never was.

WHY (1) — measured, and reconfirmed for this amendment:

    weir -e 'let x = 1
    print $"{x}"'
    type error: this expression is not a function taking 2 argument(s);
    it has type int

    (the identical bytes in a file print `1`)

The two lines are read as `let x = 1 print $"{x}"` and the arity error lands
on the application. Nothing points at the line break, so a reader who does not
already suspect the cause goes looking at `print`.

The convention is uniform across the ecosystem and weir is the outlier:
`sh -c`, `perl -e`, `python -c`, `ruby -e`, `node -e` and PowerShell
`-Command` all take a whole program with working newlines, and users arrive
from every one of them. F# offers no counter-precedent — `dotnet fsi` has no
direct `-e` equivalent. Internally it should already work: the statement rule
is "a statement ends at the next column-0 line", which a string containing a
column-0 line satisfies whether the bytes came from a file or from argv.

WHY (2). Once `-e` takes programs, "ephemeral like the REPL" stops being true.
A multi-line `-e` in a Makefile, a CI step or a systemd unit is source in
every sense that matters — read repeatedly, by people who did not write it,
with no completion and no editor. The rule stays sayable in one sentence:
**bare names live in the REPL session; everything else is strict.** No list,
no clause about argv strings.

The counterargument, recorded so it is not rediscovered as an inconsistency:
`weir -e 'ls | where ...'` typed at a shell prompt is genuinely ad-hoc, and
strictness there is friction with no editor to help. The decision is made
knowing that. The mitigation is free — the bare-name diagnostic already names
the qualified spelling.

WHAT THIS CHANGES IN RIDER 1: `Program.fs:10` moves from `Builtins.typeEnv` to
`typeEnvStrict` and is no longer an open question — it is part of the removal.
Rider 1's report-back item "does `-e` keep bare names" is answered: no.
SKILL.md's `-e` mentions (currently only that `Self.*` and `import` are
absent) gain the strictness fact, and the REPL-only sentence for bare names
stops needing an `-e` caveat.

SCOPE NOTE: do NOT fold this into the `#loose` deletion commit. The mode
removal and the `-e` parsing fix are separate changes that happen to touch
adjacent lines. Strictness for `-e` belongs with the mode removal; the
program-parsing fix is its own commit.

VERIFICATION: `weir -e` with a two-statement program produces the same result
as the same text in a file — pin both AND pin that they agree, since the
agreement is the property, not either alone; a `-e` program with an indented
block (`if` body, `for` body) parses; a `-e` program with a bare module member
errors naming the qualified spelling; the REPL still accepts bare names (the
thing most likely to break while narrowing the entry point); and if the fix is
routing, check what ELSE differs between the two paths.

### Report-back, answered (read-only; nothing changed)

**1. Routing artifact or deliberate design? DELIBERATE — so this is a
reversal, not a bug fix.** `Program.fs:22` calls `Script.singleLine`, which
wraps the whole input as ONE logical line (`Head = 1`, a single segment),
where files go through `analyzeLines` — the assembly layer that implements
column-0 statement boundaries and block-let joining. And the contract is not
merely implied: `Program.fs:60-70` rejects by statement KIND with four
dedicated teachings —

    KType    -> "-e takes an expression, not a declaration"
    KLet     -> "-e takes an expression, not a let statement"
    KLetPat  -> "-e evaluates one expression; use 'let (x, y) = ... in ...'"
    KModule  -> "-e takes an expression, not a module declaration"

`weir -e 'let x = 1'` errors today with the second of those, and the `let ...
in ...` workaround the third points at works. So the single-EXPRESSION model
was built on purpose, taught in four messages, and documented (`Program.fs:262`
help text and `docs/GUIDE.md:74`, both "one expression").

WHERE IT WAS DECIDED: nowhere keyed. There is no `[D:]` row for it — the
contract lives only in the help text, GUIDE:74, and those four messages. Per
the append-only ledger rule a reversal "gets a new entry naming the old key",
and here there is no old key to name; that absence is worth stating in the new
row rather than passing over.

**SCOPE QUESTION THIS RAISES, needing an answer before implementation.**
Decision (1) says "a program, not a single statement". Taken literally that
retires all four rejections above, because a program may contain `let`, `type`
and `module` declarations at top level — `weir -e 'let x = 1'` becomes legal
and prints nothing. That is a substantially larger change than "newlines
work", and the rider's motivating example (`let x = 1` then `print`) needs
only the `KLet`-in-a-multi-statement case, not `type`/`module`. Three readings
are available and they are not equivalent: (a) full program, all four
teachings retire; (b) program of statements but declarations still refused
alone, so the messages survive for single-declaration input; (c) newlines only.
The plan does not choose — this belongs in the bless note.

**2. Other `-e` / file-path differences.** Besides `singleLine` vs
`analyzeLines` and the four kind-rejections: `-e` passes
`Script.scriptOnlyImport`, so `import` is unavailable; `Self.*` is absent
(documented); and `-e` calls `Script.stripComment` on the input up front
`[D:trailing-comments]`, which files do inside assembly instead. Whether that
last one behaves identically on a multi-line input is untested and is the
likeliest second divergence hiding behind the first.

**3. Anything depending on `-e` being single-statement?** 117 `-e`
invocations across `ci/e2e.sh` and `tests/` — all expression-shaped, so all
survive the widening. No test asserts the "one expression" help text or any of
the four kind messages (the only textual match is an unrelated test name at
`tests/Weir.Tests/Tests.fs:6379`). So the test-breakage risk is low and sits
in the messages, not the invocations — which is also why nothing caught the
multi-line mis-parse: there is no fixture that feeds `-e` a newline.

## D2 — `&&` and `||` are silently accepted (HIGH)

`Check.fs:3912` names the family in a comment: *"the bash prior-bleed family:
; does not chain, > / >>"*. It has three members. The two commonest bash
chaining glyphs are not among them:

    echo a && echo b     # no error, no warning, prints: a && echo b
    echo a ; echo b      # warning: ';' does not chain commands in weir
    echo hi > out.txt    # warning: '>' does not redirect in weir

The pass-through is documented and deliberate. The SILENCE is the defect, and
it is inconsistent with the two siblings that do warn. `&&` is the most
common way a shell user sequences two commands, so this is the single most
likely first-hour surprise in the language, and it produces a wrong result
with no diagnostic at all rather than a teaching.

FIX SHAPE: add `&&` and `||` to the existing family. The message writes
itself from the siblings' pattern — *"'&&' does not chain commands in weir —
put commands on separate lines"*.

## D3 — the raw expecting-list dump, 6 of 28 (MEDIUM-HIGH)

Six cases produce a bare FParsec expecting-list as their FIRST diagnostic,
naming no repair: `=` used for equality, `let x: int = 1` (a type annotation),
a glob in argv, a Python `:` block, a Python `def`, and a `,` between record
fields. A stray `\"` inside an interpolation hole is a seventh, outside the
census. (`let x: int = 1`'s list is short — `identifier, '(', '()' or '='` —
and is the least bad of the six.)

The worst is `=`, because it is the difference the docs emphasise most
(*"Equality is `==` (never `=`)"*):

    print $"{if 1 = 1 then 1 else 2}"
    error [parse]: Expecting: identifier, infix operator, '!', '"', '"""',
    '$', '$"', '(', '-', '.', '@"', '[', 'then' or '{'

The parser knows enough to want `then` — it is inside an `if` — and says
nothing about `==`.

THE COST, receipted rather than asserted. Writing the harness the day before,
this author hit the hole case (`\"` inside `$"{…}"`), read that dump, and
concluded *"string literals are not allowed inside interpolation holes."*
That rule is FALSE — `$"{Str.length "abc"}"` is fine, as are escaped quotes
inside a string in a hole, verbatim strings, and triple-quoted strings. Only
a stray backslash at hole level fails, which is correct (weir has no
expression-level `\` escape). Acting on the wrong rule, the author
restructured a 326-line program to hoist every string out of every hole, and
carried the false rule into written commentary. One dump, one wrong mental
model, a whole session of unnecessary contortion — by a reader with the docs
open. This class is already named in PROCESS as message-domination; this is
what it costs downstream.

FIX SHAPE: the highest-value single message in the language is `=` → *"use
`==` for equality; `=` binds in let and record fields"*. The hole case wants
*"`\` is not an escape here — a quote needs none inside an interpolation
hole"*.

## D4 — other languages' keywords land as "command not found on PATH" (MEDIUM)

`while`, `return`, `try`, `def` are not reserved, so they fall through to the
PATH resolver:

    while true do
        print "x"
    warning [cmd-not-found]: command not found on PATH: while

This teaches an actively wrong model — that `while` is a program the user
could install. The gate exists and works for four words (`rec`, `mutable`,
`import`, `function` → *"'rec' is a keyword"*); it is simply under-populated.

FIX SHAPE: add the common foreign control-flow keywords to the reserved set
with a one-line teaching each pointing at weir's spelling (`while` → `retry`
/ `poll` / `for`; `try` → `| complete` and resource scopes; `return` → the
last expression is the value).

## D5 — the commonest .NET prior gets an actively misleading suggestion (MEDIUM)

    List.length [1]     →  unbound variable 'List'. Did you mean 'Post'?
    map (...) [1]       →  command not found on PATH: map. Did you mean 'YMap'?

`Post` is an HTTP method constructor; `YMap` is a YAML node. Both are
edit-distance neighbours and semantic nonsense. A wrong suggestion is worse
than none: it sends the reader somewhere confidently.

The retired-name registry that answers `Seq.filter` correctly is the right
home for this — `List` and `Array` are not in it.

FIX SHAPE: register `List.*` and `Array.*` → *"weir's sequences are `Seq`"*,
and suppress a did-you-mean whose target is not plausibly the same KIND of
thing (a module suggestion for a module, not a union case).

## D6 — hole-defaulting reports at the call site, not the cause (MEDIUM)

    let name n = $"item-{n}"
    print (name 5)
    error [check]: expected string, got int          # points at `name 5`

A parameter used ONLY inside an interpolation hole is defaulted to `string`
(documented: *"a bare hole defaults an unresolved type to string"*), so every
integer call site is an error. The rule is stated in the docs; the message
points at the call rather than the defaulting decision in the body, and the
repair (`$"item-{n + 0}"`, or taking the value in typed) is not obvious from
where the caret lands.

FIX SHAPE: when a defaulted-to-string parameter causes a mismatch, say so and
name the anchor — this is a defaulting decision the author can see, not a
type error at the call.

## D7 — `Seq.iter print` fails on `seq<int>` (LOW-MEDIUM)

    [1] |> Seq.iter print       error [check]: expected int, got string
    ["a"] |> Seq.iter print     clean

`print` accepts string, int, bool and `seq<string>`, but point-free it
defaults to string, so the obvious spelling fails on the obvious type — and
the message reads backwards ("expected int, got string" when an int was
supplied). SKILL.md's own guidance sends readers to `Seq.iter print` for the
string case, so the shape is taught.

FIX SHAPE: either resolve `print` at the use site like the other generics, or
teach at the point-free position naming `Seq.iter (fun n -> print $"{n}")`.

## D8 — small, real, and already documented

Recorded because each cost time and each has a one-line answer that the error
does not give.

- **Strings are single-line**, so a pasted multi-line literal fails with an
  end-of-input dump rather than *"strings are single-line — use `\n`"*.
- **`\r` is not an escape** (deliberate, and the message is good: it names
  the verbatim-string repair for Windows paths). Noted as the counter-example
  — this is what the other escape errors should read like.

## The pattern across both reviews

Worth stating once, because it is the same shape four more times and it
suggests where to look next rather than what to patch.

**The mechanism is built and correct; the membership list is incomplete.**

| mechanism | members it has | members it lacks |
|---|---|---|
| bash prior-bleed warnings | `;`, `>`, `>>` | `&&`, `\|\|` |
| reserved-word gate | `rec`, `mutable`, `import`, `function` | `while`, `return`, `try`, `def` |
| retired-name registry | `Seq.filter`, `Seq.flatMap` | `List.*`, `Array.*` |
| depth guard (security review, F1) | expression grammar | type + pattern grammars |

None of these is a design error, and every one of them was found by asking
"what else belongs in this set?" rather than by finding the feature broken.
That is the same enumeration-versus-search point the security review closed
on, and it has the same mechanical answer: the sets want to be derived or
asserted, not maintained by hand. Where a set cannot be derived, a test that
enumerates its members is the next best thing — which is exactly what `#loose`
did not have.

## Instrument honesty

- The mistake census is 32 cases, hand-chosen for realism, not sampled from a
  corpus of real weir errors — nobody has one yet. The 19/32 ratio is a
  measurement OF THIS CORPUS and is meaningful as a regression baseline, not
  as an absolute grade.
- The "teaches vs dump" split is regex-graded (does the message contain a
  repair phrase and more than an `Expecting:` list) and then eyeballed. Two
  cases sat on the line and were classified by hand; both are noted in the
  harness.
- **Two candidate findings were dropped after the control was fixed**, and
  are recorded so they are not re-found: (a) "did-you-mean is missing for
  module members" — false, the first probe used a name beyond the
  edit-distance threshold; (b) "the command/expression classifier ignores
  local scope" — false, the observed failure was collateral from the reifier
  bug on the preceding line. Both were caught only by building a passing
  control, which is the same discipline the findings above are held to.
