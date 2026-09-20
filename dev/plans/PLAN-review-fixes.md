# weir — fixing the multi-area review's fourteen findings

Status: DRAFT, awaiting bless.

DEPENDENCY GATE: this plan consumes `PLAN-multi-area-review.md` and gates on
that review's SESSION REPORT (executed 2026-09-20, findings-only, gate green
in the reproducing direction: 13 probes REPRODUCE, 5 controls pass, exit 1) —
not on its bless, per the dependency-gate rule.

The acceptance instrument already exists and is committed:

    weir tools/multi-repro.weir --bin ./path/to/weir

Every probe label here matches the review's numbering (`F1`–`F14`). A bundle is
done when its probes print OK **and** the rest still print what they printed
before — a fix that flips a neighbour is a stop-and-report, not a bonus.

## The three rulings owed BEFORE implementation

Stated here so the fixer inherits decisions, not open questions — the failure
the prior review's F4 paid for ("DECIDED BEFORE implementation rather than
during"). Each carries a recommendation; the bless is the maintainer's.

**R1 — F8, a `Stream` producer that raises mid-body.** Today: N elements, then
a proper zero-length chunk and a 200, so the client reads a complete stream and
the script exits 0. Options: (a) abort the response without the terminating
chunk, so the client's own HTTP layer reports a truncated body; (b) surface the
failure to the script; (c) both.
**RECOMMEND (c), with (a) as the load-bearing half.** The client is the party
that acts on the data, and a proper terminator after a failure is the one
outcome that must not survive. (b) alone leaves every existing client trusting
a truncated stream. Note the shape `within proc` already chose for this exact
question — a scoped child's failure gets a DESIGNATED channel (`watch=`,
`Proc.wait`, `[D:scoped-procs]`) rather than raising through the scope; `serve`
wants the same treatment, not a raise out of the handler.

**R2 — F9, a method weir's union cannot name.** Today: TRACE, FROBNICATE, and
`QUERY` all arrive as `Get`. Options: (a) carry it — an `Other of string` case,
the `[<Other>]` shape weir already uses for tagged unions; (b) refuse before the
handler with 405.
**RECOMMEND (a), plus fixing `QUERY` outright** — it is a case the client side
already has, so its absence inbound is a plain bug in the "ONE type family,
shared client↔server" claim, not a design question. (a) keeps routing decidable
by the script (a handler can still answer 405 itself), and (b) makes weir refuse
traffic a proxy may legitimately forward. Open-world beats closed here for the
same reason `[<Other>]` exists.

**R3 — F12 and F13, bound-or-declare.** F12: no request-body read timeout, so a
slow client parks a handler slot. F13: escape sequences from data reach the
terminal verbatim.
**RECOMMEND: bound F12, sanitize F13.** F12 wants a `serve` field with a stated
default rather than a non-claim, because the script cannot defend itself today
(`{ port; maxConcurrent }` has no knob) and the reverse-proxy posture is a
deployment assumption, not a language guarantee. F13's `\r` case decides F13: a
non-claim saying "weir does not sanitize" still leaves a user reading
`b-carriage.txt` for a file named `a\rb-carriage.txt` and then pasting it into
a command — that is a wrong ANSWER, not an unprotected terminal, and
`[D:binary-echo]` already ruled that hostile bytes must not reach a tty. The
scope to sanitize is DATA in tty-bound renderers (the `ls` table, `show`,
interpolation, error text), never weir's own colouring.

## Bundling ruling

**A+B run together.** They are one unguarded-IO boundary seen twice: A's crash
face IS B's defect at `restore` instead of `import`. Splitting them means
writing the same guard twice and leaving one caller crashing.

**C runs alone.** It changes what crosses the HTTP boundary in both directions,
and it is the bundle most likely to move a neighbour (every `Http.send` caller,
every `serve` response) — it wants the corpus re-run to itself.

**D runs alone and FIRST if anything is time-boxed.** It is the cheapest
(one predicate, four call sites), it unblocks `pure`/`readonly`/`plan` for
weir's only loop statement, and it clears a vocabulary violation.

**E gates on R1+R2** and must not start before they are blessed.

## Bundle A+B — the lockfile path boundary, and unguarded IO

Findings: **F1** (restore writes outside `.weir/`, overwrites, crashes rc=134;
verify reads outside), **F14** (`add schema --as` vendors outside; `gen types`
follows), **F2** (an unreadable import crashes `check`/`--json`/`--can`).

Root cause, stated once: **a path that came from outside is joined with
`Path.Combine` and then used, and an IO call that can fail is not in a
Result.** weir has the answers to both already — `Path.under` for the first,
`File.read`'s located "permission denied" for the second.

Sites, verified:

- `Contracts.fs:895` (`restore`) and `Contracts.fs:1067` (`verify`) —
  `Path.Combine(weirDir, e.Path)` with `e.Path` read verbatim from the lock at
  `Contracts.fs:98–99`. **Confine at LOCK READ**, once, so every consumer
  inherits it — the same reasoning that put the NUL guard at the argv crossing
  rather than in every producer. A rejected entry is a located diagnostic
  naming the entry and its path, not a silent skip.
- `Program.fs:315–317` — the plain-name test exists as an inline `nameOk` in
  the `add module` branch only. **Extract it** (one validator, `Contracts`
  side) and apply it to `add schema`, `add module`, `add sig`, and `gen types
  --schema`. Do not write a second copy: a third spelling of this rule is the
  next finding.
- `gen types` must additionally refuse a lock entry whose path is not under
  `.weir/` — it writes weir SOURCE, so it is the highest-value consumer of the
  lock-read confinement, not a separate check.
- `Script.fs:3513` `readImportSource` — `IO.File.Exists` then
  `IO.File.ReadAllLines`, unguarded. Note it is called TWICE (`:3577` then
  `:3586` with `Option.get`), so the current shape also races: a file deleted
  between the two calls crashes the same way. **Read once into a Result**;
  distinguish absent (today's "cannot resolve import: no file at …") from
  present-but-unreadable (new text, author's language, naming the path).
- `restore`'s overwrite: decide and STATE it. weir's own `File.copy`/`move`
  refuse existing destinations (`[D:fs-members]`); `restore` repairing a
  drifted artifact it owns is legitimate, writing over a file it never wrote is
  not. The confinement makes this mostly moot — inside `.weir/` the artifact is
  weir's — which is why this is a one-line ruling in the bless note, not a
  design.

DONE WHEN: `F1`, `F14`, `F2` print OK; `weir restore` on a hostile lock refuses
per entry with a located diagnostic and writes nothing; `verify` refuses to read
outside `.weir/`; every `--as` kind shares ONE validator; `check`, `check
--json`, `check --can` and the LSP all give a located diagnostic on an
unreadable import, never rc ≥ 128; a mode-000 fixture lands in e2e on BOTH the
import path and the restore path; `generated:` entries stop reporting "present"
without saying what was checked (the doc-level row).

## Bundle C — the HTTP header boundary, both directions

Findings: **F3** (a CRLF in a request header value forges a second header on the
wire), **F11** (a header weir cannot send is silently dropped), and **F4**
(`secretHeaders` cross a redirect's origin) if R-less — F4 is a policy fix, not
a validation one, and may be split out if the bless prefers.

One rule, applied at both crossings: **a header name or value carrying CR, LF
or NUL is REFUSED with a located diagnostic naming the header.** Not dropped
(F11's silence), not forwarded (F3's forgery). The asymmetry today is the tell:
`Http.fs:119` `TryAddWithoutValidation` forges on values and drops on names;
`Serve.fs:142` `writeResponse` drops. The argv boundary's NUL fix is the
precedent for the shape and for the message register.

F4: drop `secretHeaders` on a cross-origin redirect exactly as the BCL drops
`Authorization`, or refuse to follow a redirect for a request carrying one.
Recommend the former (it matches the `auth` union's behaviour, so the two
credential channels stop disagreeing).

DONE WHEN: `F3`, `F11`, `F4` print OK; a refused header is a located diagnostic
in the author's language; the referee log shows no forged header and no secret
at the redirect origin; the pin asserts the REFUSAL, not merely the absence of
the forged header (absence is also what a silent drop produces — the two must
be distinguishable, which is the whole point of the bundle).

## Bundle D — `|` is two namespaces, and four classifiers test the prefix

Findings: **F5** (a `for` body with no effect is refused by `pure`/`readonly`/
`plan`, message leaking `|seqIter`), **F7** (every `for` adds a phantom
`^$(…)` capability and fails `--strict`), **F6** (`readonly` changes the typing
of a piped unit statement).

Root cause for F5+F7, and it is exact: **`n.StartsWith "|"` is used as the test
for "this is a command"** at `Purity.fs:139`, `Purity.fs:427`,
`Effects.fs:78`, `Effects.fs:112` and `Can.fs:275` — while `Parser.fs:2576`
desugars `for` to `EVar "|seqIter"`, a LIBRARY name that `Builtins.fs:5867`
maps to `Seq.iter`. Two families share one marker and five classifiers test the
marker instead of the family.

Fix shape — the same remedy PROCESS already prefers for the foreign-keyword
leak (read the keyed table, never a second copy of the list): **the classifiers
consult the desugar mapping (`Builtins.fs:5867`) and treat a mapped
library-desugar as its target member**, or the for-desugar is renamed out of
the reifier namespace. Recommend consulting the table — a rename fixes today's
collision and leaves the next `|`-prefixed library desugar to rediscover it.
Either way the `|`-prefixed name must not reach a message
(`[D:user-language-messages]`), so the vocabulary sweep is part of this bundle,
not a follow-up.

F6 is a separate defect in the same area: inside `readonly`, an unbound
`xs |> Seq.iter f` statement fails to type (`Seq.iter` seen unapplied) while
the identical statement types under `pure` and types when bound. Diagnose
before patching — the question to ask is why the readonly body's statement
path re-checks a unit statement differently from `pure`'s.

DONE WHEN: `F5`, `F5-vocabulary`, `F6`, `F7` print OK; `pure`, `readonly` and
`plan` each carry a for-loop fixture; the `--can` opaque-site COUNT for a
for-only script is pinned at 0 (a count, not a text match); `--strict` exits 0
on it; no `|`-prefixed name appears in any message, asserted by a sweep over
the message corpus rather than by these three fixtures.

## Bundle E — serve wire fidelity (gated on R1 + R2)

Findings: **F8** (silent mid-stream truncation), **F9** (unrepresentable verbs
become `Get`), **F10** (duplicate request headers collapse to last-wins).

F10 has no ruling owed: `Serve.fs:109` `readRequest` builds its pair list from
a structure that has already merged duplicates. Read the per-name VALUES so
three duplicates arrive as three pairs in wire order — the failure scenario is
the documented reverse-proxy posture, where repeated `X-Forwarded-For` is
normal and a script reading the client IP silently gets the last hop only.

DONE WHEN: `F8`, `F9`, `F10` print OK; the F10 pin uses THREE duplicates (two
cannot tell last-wins from a merge); F9 covers one verb of each kind (a
standard one, `QUERY`, and a nonsense one); F8's pin asserts the client can TELL
— a proper terminator after a producer failure must not survive.

## Standing bars

- Message TEXT unchanged unless a section says otherwise; new diagnostics are
  located with exact `line:col` pins, never contains-checks.
- A crash is never a fix: every crash probe asserts `== 1`, and the review's
  gate already encodes that.
- Composition-product rule: each bundle's new decision ships with pins for its
  products against the EXISTING decisions — A+B against `[D:contracts-spine]`
  and `[D:modules-v1]`; C against `[D:http-ua]`, `[D:http-serve]` and the
  `Secret` rendering law; D against `[D:for-binder]`, `[D:can-report]`,
  `[D:pure-stage1]`, `[D:pure-stage2]` and `[D:plan-apply]`; E against
  `[D:http-serve]`.
- Acceptance is the CORPUS, not the gate: the gate samples one payload per
  finding, the review drove many. Bundle C especially — narrowing what a header
  may carry routes values down paths the single probe does not sample.
- New `[D:]` rows owed: the lockfile-path confinement (A+B), the header-byte
  refusal (C), the `|`-namespace split (D), plus rows for whatever R1–R3 decide.

## Verification

1. `weir tools/multi-repro.weir --bin <freshly built binary>` — the bundle's
   probes print OK, every other line unchanged, controls still pass.
2. `dotnet test tests/Weir.Tests` + `ci/e2e.sh` + the fuzzer, per `ci/local.sh`.
3. Bundle A+B additionally: a mode-000 import fixture through `check`,
   `check --json`, `check --can` and an LSP didOpen; a hostile-lock fixture
   through `restore`, `verify` and `gen types`.
4. Bundle C additionally: the review's header corpus through the logging
   referee, both directions.
5. `ci/skill-doc.sh` — any doc sentence this plan changes is a fenced,
   executed claim where it can be (the prior review's "promote load-bearing
   prose to fences" lesson).

## Stop-and-report triggers

- A behaviour delta outside the bundle's stated scope (the standing rule).
- F6's diagnosis turning out to be a checker-core issue rather than a
  readonly-path one — that is a different session's risk profile.
- Any fix that would require changing a message this plan did not authorize.
- Bundle C moving a `Secret` rendering behaviour: that crosses into the
  prior review's closed ground and wants its own bless.
