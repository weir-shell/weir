# Spike Notes

## attribute positions: the prerequisite that fit in an afternoon (2026-09-04)

Session P of the wire-unions plan, and the sizing held because the
hard part was NAMED before writing code: the assembler. A col-0
attribute line above `type` is two statements by the col-0 law, so
the join had to be taught — and the teaching already existed one
level down, where a field's `>]` line dangles into its field. The
statement-level twin: a pend that is ONLY a complete attr list
joins the next col-0 line instead of closing. Guarding it on
empty-brackets/lambdas/district took one condition; everything else
(typeDecl's attr prefix, caseDecl's opt attrList, the
position-scoped registry) was mechanical.

Two error classes came out of the registry rework, and keeping them
distinct is the part worth remembering: an UNKNOWN name keeps the
did-you-mean ('Othr' → Other), a REGISTERED name in the wrong
position teaches its HOME ('Tag' attaches to a union declaration).
Collapsing those into one message would have made the common
misspelling worse to fix.

The FCS probes ran before any code: F# hosts attributes at both new
positions, so the divergence row NARROWS — weir's border with F#
got smaller by building this, which is the right direction for a
prerequisite to move it. Tag and Other validate today and bind
nothing (a tagged union still refuses every boundary, pinned) —
the attribute law's validation-at-attachment / binding-at-
consumption split carrying a whole release-sized gap without a
special case.

## to json writes one document; to jsonl writes lines (2026-09-04)

The write side had the read side's old backwardness — `to json` was
an NDJSON writer wearing the plain name — and it fell to the same
ruling one month later, on the trigger [D:from-jsonl] parked:
"to jsonl waits for a receipt". The receipt was the wrap. The
anon-literals session opened on `[{| … |}] |> to json` — build a
one-off object, then wrap it in a list to serialize it — and the
question "why does to json take a seq" came back independently a
day later. Two arrivals of the same friction is a trigger fired.

The reshape cost almost nothing because the renderer already did
both jobs: jsonLine renders ANY admitted value as one minified
token (a nested seq field was already an array), so the new
`to json` is that renderer applied once and `to jsonl` is the old
per-element map, byte-identical. The probes deleted the scary line
items up front: `to jsonl` was a CHECKER refusal, not a parse error
(no parser movement, no fuzz owed), and all three grammars tokenize
the adapter direction-agnostically (jsonl already coloured after
`to`). The break is the honest cost — `xs |> to json` checks clean
and means something else now — the first real entry in the
checks-clean-behaves-differently changelog class since publication.
A transitional refusal was considered and rejected: it would refuse
the array-document form, which has no other spelling.

The yaml half of the conversation produced a ruling, not a change.
`to yaml` on a seq keeps writing a `---` stream although `from yaml`
refuses to read one — and that asymmetry is PRINCIPLED: a typed
write produces homogeneous documents (legitimate; kubectl consumes
them), a typed read would have to hold heterogeneous ones (a real
bundle is Deployment+Service+ConfigMap — [D:yaml-seq]'s reason).
The one-way door is correctly one-way, recorded so no audit reflags
it. And seq-at-the-top now deliberately differs per format: array
is JSON's native plural — the stream form is the thing literally
named jsonl — while the stream is YAML's.

## the marker that ate a word, and the glyph that gave it back (2026-08-27)

The `text` marker survived the whole battery and died in the first
minute of user contact — not to a bug, to a name. The soft-reserve
that yaml carries invisibly (nobody binds `yaml`) was, for `text`,
three breaks in our own tree and one silent trap: with `text` bound,
`$text` at a line end is the interpolated marker, not a splice, and
the block parses to something the reader did not write. The ruling
walked heredoc-the-word (reserve it? still steals a word), a
trailing glyph (invents a convention), `>>>` (Python's prompt,
composition's neighbor) and landed on `<<<`: shell's own heredoc
chevrons, pointing into the binding, in lexical space the language
had left empty. The lesson worth the ink: a marker word's cost is
not its rarity in the grammar, it is its frequency in USER bindings
— and a glyph pays zero. The probe set from the first round
(union-case lines, record fields, line-end shapes) re-ran unchanged
and proved the glyph collision-free before the sweep started.

## the text block: one guard, two blames, one inherited bug (2026-08-27)

The design was frozen tight enough that the body of the work was
transcription onto yaml's rails — MarkerKind.Text rode every
verbatim-join arm untouched, and the parser's district body is the
yaml one with the sentinel-decode loop extracted and shared. The
session's one real fight was `$text` failing to arm while `text`
worked, and the instinct (assembler) was wrong: deepenAfter's
lookahead guard listed only the bare word, so the district parser
never ran and its attempt failed silently at the `$`. The probe
that settled it in one step: inject the sentinel by hand through
`-e`, which bypasses the assembler entirely — parser still refused,
blame assigned. Worth keeping as a move; the alternative was reading
join arms for another hour.

The error-message pass paid twice. Naming the marker in the
assembler's three errors surfaced that the EOF no-block arm had
said `'!'` since the district retirement — for yaml too. And
carrying the column out of the fragment run surfaced that FParsec's
"Other error messages:" trailer had been eating the Expecting line:
a bad expression in a yaml `$()` splice reported an EMPTY parse
error before this session. Both were inherited, both are now fixed,
neither was the feature's fault — the feature just walked the paths
with fresh eyes.

## the rulebook died of being absorbed (2026-08-25)

SEMANTICS.md is gone. Not archived, not excluded — deleted, because
twelve reference pages now hold its rule-half and the ledger always
held its history-half. The audit's yield was honest and small: three
rules had no home (re-enumeration re-runs effects; ranges terminate
where arithmetic raises; the furthest-point diagnostics rule), which
is what "the reference absorbed it through SKILL" predicts — the
marbled archaeology was the file's bulk, and archaeology has a
ledger. Fence arithmetic for the whole of Half B: 55 new fences
across seven sittings' worth of pages done in two, zero first-run
failures, all of it riding probe-before-prose.

## half B's top four, and the probe-first dividend (2026-08-25)

Bytes and record patterns paid the audit's debt in the guide, and
the reference gained its four most-looked-up pages — Commands,
Types, Patterns, Statements — in the strict register. The number
worth keeping: 35 new fences, zero failures on the first skill-doc
run, because every uncertain claim was probed before it was prose
(subset record patterns legal, ; binds into if, fst refuses
triples, Map == refused, overflow raises) and everything else was
adapted from fences CI already executes. Writing reference pages
from a gated skill file is transcription plus ordering — the
priced-and-declined generator would have saved less than it cost.

## add module: the frozen design survived contact (2026-08-25)

Two sessions of exploration meant the implementation was mostly
transcription — the one design decision left open (R1) resolved
against BOTH friendly options: the probe showed extensionless bare
imports already work, so the vendored namespace became the weir:
scheme inside the ordinary string, shape-scoped with no fallback and
no grammar movement. The acceptance battery's one real find was in
the SIBLINGS: restore had never repaired a modified artifact, only
materialized absent ones — "re-materialize, hash-verified" read as a
promise the code kept only half of. Now drift refetches.

Add-time typechecking was free, as the plan hoped: the import
loader with a phantom importer IS the validator, so the add refuses
with the checker's own located words. And the update experience the
design rests on held: a re-add prints the sha pair and the vendored
file's git diff is the whole review.

## the init guard, and a fix that could not compile (2026-08-24)

The change was small and right in intent: [D:repl-init] made the init file
declaration-only and all-or-nothing, but declaration-only is not effect-free —
a `#session` field's VALUE and a plain `let` both run user code at startup, and
an exception escaping either killed the REPL before it reached a prompt. Two
`try`s and an `evalFailed` flag, and both failures report through the same
`initDiag` as every other init error.

**It arrived not compiling**, and that is the part worth writing down. The
commit was a half-applied re-indent: `match chk.Kind with` had moved right, two
of its four arms had moved with it, two had not, and the compiler said exactly
that (FS0058, offside at 1908:33 against a context started at 1907:37). Under
it sat a second, independent breakage the compiler never got far enough to
mention — an `else` with an empty body, its whole tail left dedented outside
it. One error hid the other, which is the ordinary shape of a broken build and
the reason the ritual runs the compiler before anything else.

**The pin's claim is the NEGATIVE.** Pre-fix behaviour was reconstructed by
building the parent commit in a worktree rather than reasoned about: both sites
printed `Unhandled exception. System.Exception: …` and four stack frames. So
the located line is not the assertion that matters — a build that reports
politely and ALSO dumps a stack passes a located-line check. The e2e rows
assert `grep -qF "Unhandled exception" && fail`, which is the only assertion
that would have been red before.

**Coverage the smoke test did not reach, said aloud.** The first pass at
exercising the `#session` guard used `#session` + `key = value` lines and never
reached `applySessionField` at all — the block splitter rejected it first,
because the form wants braces. The guard looked exercised and was not. The
committed fixture uses the brace form and was re-checked against the pre-fix
binary to confirm it actually reaches the new code.

**Ritual note**: `ci/local.sh` could not run — docker is installed but its
daemon is unreachable in this container — so the battery ran directly instead.
Same steps, one less layer of isolation, stated rather than implied.

## the module exploration: the siblings had already decided (2026-08-24)

Phase 0 was the whole exploration. The contracts spine fetches file
BYTES by URL and locks a content hash — no git, no refs, no graph —
so the old `<repo> --ref <sha>` sketch dissolved on contact with
what actually shipped: v1 is a single file by raw URL, and the
rawUrlHint even teaches the GitHub file-page rewrite already. The
question the exploration existed for (are annotations prerequisite?)
answered itself the same way: a pinned lock means interface drift
arrives only at a deliberate re-add against a diffable vendored
file, which is a local-module edit in experience. Adjacent, with the
non-claim stated.

The one thing the siblings could not answer: import resolution is
literally file-relative, so vendoring needs either ugly ../ paths or
one bare-name-resolves-via-the-walk rule — which is not new design,
it is #sig's resolution verbatim. Two sessions if a trigger ever
fires; parked as unmotivated with the triggers named, because one
repo cannot want a package manager.

## the init file, and the overlay that refused to exist (2026-08-23)

The plan braced for an env matrix hunt — every spawn route times the
new layer times nested within env. The hunt dissolved at the design
step: #session env is not an overlay, it IS the process environment,
set once before the first prompt. That is what a login shell means
by environment, it is what exec'ing weir with that env would mean,
and every reader and spawner sees it with zero new machinery. The
matrix became two pinned lines (sigil wins inside, base restored
outside).

The record-literal question answered sideways: no new grammar. The
block's fields become synthesized lets — dedent four, prefix four,
columns survive — through the real parser and checker, so the config
surface gets the record law's errors for free. Two rounds of
continuation-line bugs later (the closer at base indent is not a
field), the located carets point at the user's own file.

The plan's own example carries a small trap worth knowing: `let lg n
= git log --oneline $"-{n}"` types n as STRING (the bare-hole
default), so `lg 1` teaches. The example is honest — that is the
language working — but the e2e cell calls it as written.

## the audit found the word but not the teaching (2026-08-22)

Word-presence said import was covered; the hit was `import
socketserver` in a python fence. Backtick spans then hid the
opposite direction — rec and mutable WERE taught, in prose the
first gate draft refused to see. Both false modes are now stripped
or admitted in the gate itself, which went red three separate ways
before it was allowed to stay. The worst bucket was neither absent
nor passing-mention: coming-from actively DENIED Map exists, one
session after it shipped — a denial reads as authoritative exactly
because the page is honest everywhere else. Owed and recorded:
Bytes and record patterns are SKILL-only; the process finding was
empty (every gap had its ledger row).

## the extensions plan met a friendly registry (2026-08-22)

The decisive unknown — does zed-industries/extensions take a
subdirectory — dissolved on contact: the live extensions.toml has
entries with path = "editors/zed" character-for-character, so the
feared repo split (and the currency-check surgery it would drag
along) never happens. The trap worth the entry: extension tags named
vscode-v* would have FIRED THE WEIR RELEASE WORKFLOW — release.yml
triggers on the v* glob, and "vscode-v0.1.0" starts with v. Tags are
ext-*, and the release glob is v[0-9]* now regardless.

The icon forced the parked logo question, answered with a designed
minimal mark (a weir: three bars stepping down, palette blues on the
dark tile) rather than a placeholder — deliberately small, easy to
supersede. What stays human: the marketplace accounts and tokens,
the clean-profile installs, the screenshot, and the merge on Zed's
side of the fence.

## the palette came home, and the wrap ate an operator (2026-08-22)

The theme reversal is recorded as what it is: preference. Nord's
arguments never fell; the site should look like the author's other
work, and pretending otherwise would be the dressed-up version the
rider warned against. The port cost was real but small — eleven
scopes, four AA lifts (the classic GitHub palette fails modern
contrast on comments, numbers, punctuation and builtins), and a dark
variant that is an invention with its inventedness stated.

The Http bug was the better find: a source wrap put `>=` at line
start and CommonMark's blockquote-interrupts-paragraph rule ate the
`>`. One instance repo-wide, but the class is nasty — prose damage
that no fence can catch because fences are exactly what wrap cannot
touch. The gate distinguishes real blockquotes by their predecessor
line, which held across all seventeen rendered docs.

Iosevka arrived by HTTP range request — two zip members pulled from
a 254MB release without downloading it — then harfbuzz subset 1.6MB
faces to 132KB. The number the rider wanted: 264KB total for both
weights, swap + preload.

## the count beside the list, and the generator that closes it (2026-08-21)

F0 was one wrong word — "Four string forms" over a list of five —
but the class is the finding: prose counts next to lists are
unchecked by construction. The keyword table now cannot have that
defect: the region renders from the grammar manifest, the four kinds
are classified inside the generator with a two-way completeness
check, and e2e diffs. The classification itself could not be derived
(the manifest is flat), which is fine — hand judgement with a
machine-checked cover is the pattern this repo keeps arriving at.

Two small discoveries en route: the attribute registry is five, not
the four the rider listed (Positional returned for signature files),
and weir has no modulo operator at all — the operator table is
positive-only rather than claiming an absence with no repair to
name. The one-page reference weighed in at 26KB gzipped for all 230
entries, which settles the size question the rider refused to guess.

## the CLI page could not be generated, and that was the finding (2026-08-21)

The tooling plan's highest-value question — can the CLI reference
render from --help the way module pages render from builtinDocs —
answered no: hand-written match arms, hand-concatenated usage. The
predicted cost was already on the books: the binary's own --help had
drifted three arms behind the binary (add sig, check --can,
--version). So the fix ran in the honest direction — complete the
usage string in the binary, then byte-pin the docs page against it
in e2e. A hand-written list that a gate diffs is generated-enough;
a hand-written list nothing checks is how weir's own front door
went stale.

The .weir/ commit question needed no judgement call: the Contracts
module header already ruled it (vendored = checked in; check never
fetches), and the page just says so out loud.

## the guide learned to put the reader first (2026-08-21)

The reorder rider's six findings shared one root: sections landed in
writing order, not reading order — the REPL manual before the first
script, the shell section two-thirds down a shell language's guide.
The feared cost of reordering (fences depending on earlier fences)
turned out to be zero by construction: skill-doc runs every block as
its own file, so the 162 blocks held on the first build after the
move. The real breakage was elsewhere — the site's link rewriter
holds a hand-maintained RENDERED set that quietly GitHub-links any
docs page it does not know, and the new repl.md walked straight into
it. A comment says the set mirrors the content globs; nothing checks
that, and it now has a second entry proving the drift mode. Worth a
gate if a third page appears. (Also: an Astro cache in
node_modules/.astro survives rm .astro — two builds chased a stale
render before the third found it.)

## Nord, and the two patches a bundled theme still needed (2026-08-21)

The swap was the config line the rider promised, but "bundled" did not
mean "done": the probe over every SKILL fence found weir's
entity.name.type falling back to default white — Nord styles the
.class child, never the parent — and arithmetic found stock comments
at 2.44:1 on nord0, an accessibility REGRESSION from github-dark.
Both fixed in one 25-line theme module; the grammar untouched, which
was the rider's own test for whether the scope names were sound (they
were — the gap was the theme's).

Checking contrast by arithmetic before shipping beat eyeballing: the
numbers token (nord15) at 4.41:1 is the one deliberate sub-AA
survivor, named in the ledger rather than discovered later.

## the design pass: hierarchy, not a theme (2026-08-21)

The user's "nothing says what weir is" and "needs a theme" were one
finding wearing two coats: no typographic hierarchy, so nothing on
the page had emphasis to spend on the category line. The plan's
option (b) shipped — the category is the sub-headline, the tagline
folded into the lede as "…and nothing runs until everything checks",
which reads as the sentence's natural second half rather than a
run-on (the fold-in survived; no third line needed).

Tokens over taste: five type sizes, five spacing steps, the existing
accent used thrice (links, h1 rule, copy buttons), and one code
treatment that finally gives bare pre blocks a background and border
— the same defect the readability rider named for inline code, one
level up. The install pair got a bounded container so the call to
action reads as a block, not prose. Cold read still owed: whether a
stranger can say what weir is in five seconds is the one check that
cannot be self-assessed.

## the fourth LF surface: a formatter's embedded newlines (2026-08-21)

Windows e2e failed the reference-currency gate: the docs-json dump
byte-differed from the committed LF file. Third CRLF appearance this
session, and a genuinely new surface for [D:lf-output] — the startup
NewLine gates cover streams, but Utf8JsonWriter with Indented=true
bakes Environment.NewLine INSIDE the payload string, data before it
reaches any gated writer. Console.WriteLine then added a fourth CR at
the tail. NewLine="\n" at the writer options plus Write("..."+"\n")
closes both; Linux bytes provably unchanged (diff against the
committed file is empty), so only the Windows output moves.

The writer fix was right and insufficient: Windows failed again,
and the instrumented gate (the diff now prints on failure) showed
1728 lines differing by exactly one CR each — on the COMMITTED side.
The fifth surface is git itself: the runner image sets autocrlf=true,
so the checkout converted the LF artifact before the diff ever ran.
That also dissolves the pre-fix puzzle: the writer's indents were
CRLF (matching the converted file) but the WriteLine tail had already
been flipped to LF by the startup redirected-stdout gate — the
pre-fix diff was one byte on the last line. .gitattributes
`* text=auto eol=lf` closes it (every blob was already LF, no
binaries, so renormalize was a no-op — verified before committing).

The pattern across all five surfaces: the law is stated once, but
everything that *composes or rewrites* line endings (WriteLine,
StreamWriter, a child's stdin writer, an indenting serializer, and
git's own checkout) defaults to platform behavior independently —
each must be caught at its own site, and only a byte-equality
comparison on Windows ever catches one. The gate now prints its diff
on failure precisely because the silent version cost two rounds of
armchair theorizing about which side held the CR.

## the repair that was the bug, and the pins that met Windows (2026-08-21)

The user asked whether the path teachings should suggest Path.under
rather than Path.combine, and the probe answered harder than the
question: Path.combine root "/*" RETURNS /* — absolute-second-wins
means the message's own mechanical repair, applied to its canonical
trigger, was the Steam bug. Path.under raises on the same input,
naming the escape. Both teachings now route to under; combine keeps
its member doc (it is the right tool for controlled joins — the
control/confine split SECURITY already states).

Windows then caught the pins themselves: FParsec wraps messages with
Environment.NewLine, so the wrap-point byte pins held \n against a
\r\n reality. One Replace normalizes; the pin that exists to catch
drift caught its own platform assumption first.

## the like-for-like request found the hole (2026-08-21)

Making the hero mirror Steam exactly meant writing `rm -rf
$steamroot/*` — and weir accepted it, silently running two arguments,
the second being `/*`. The law said whole-words everywhere it was
stated; the guard only covered the prefix side. The homepage was one
probe away from demonstrating a refusal the language did not make.

The fix generalizes the prefix guard into one mechanism — a piece
starts only after whitespace or a separator — gated per piece opener
so pipes and closing parens stay untouched. The corpus answered the
over-refusal question empirically: 1391 unit tests, every e2e cell,
and 162 doc blocks pass unchanged, which also proves the repo itself
never glued (any use would have been silently buggy already).

Two precision notes that matter for the homepage. FParsec wraps long
messages, so the byte pin pins the wrap points too. And the interp
repair alone would NOT have saved Steam — an empty root interpolates
to /* just as catastrophically; what saves the like-for-like is
Self.scriptPath being absolute by construction. The caption says
both layers, because the skeptical reader checks exactly that.

## provenance shipped as economics, and the receipts got audited (2026-08-21)

The provenance section survived its own discipline: every receipt was
verified against the live tree before the copy shipped, and one was
corrected by the verification — the interop referee runs eighty
payloads, not the hundred the plan remembered. A provenance section
citing a stale gate would be the worst possible home for a stale
claim, so the audit was the work.

The method piece landed as docs/METHOD.md through the existing
pipeline. Writing it was mostly transcription — the ledger already
held the seven instrument failures, the zero-needs-a-nonzero rule,
the fired triggers, and the mispricings; the page's one new synthesis
is the closing boundary line: the agent built the system, the human
built the judgement the system was built inside.

## the hero reframed mid-build, and the probes carried it (2026-08-21)

The catastrophe pitch became a maintenance pitch halfway through — the
author's own story (PowerShell at tool scale, F# scripts' weight,
Nushell's type gap) leads, and Steam/pgpool moved below the fold as
the predictability illustration. The material survived the reframe
because none of it was prose: probes, refusals, and pins move; only
the framing was rewritten.

The probes corrected the copy twice before it shipped. A missing
command is a check-time WARNING (uninstalled tools stay editable) and
a RUN-time refusal — so beat 1 demonstrates the run, and its money
line is asserted by the gate: the e2e cell checks that tar's output
file does not exist after the refusal. And my first --can pin failed
against my own cell — the cell ran --can with an absolute path while
the homepage quotes relative file:lines. The pin caught its author
before it caught anyone else, which is the best first catch a pin can
have.

One deliberate flag for review: the "Why not the alternatives"
section is written in the author's first person, good-and-bad per
alternative. The rider asked whether it reads honest rather than
defensive — that judgement is the maintainer's, not mine.

## the image ran for the first time and named a missing symbol (2026-08-21)

GLIBC_2.38 not found — the release builds on ubuntu-24.04 and the
distroless base was a Debian behind. The ldd probe that chose the base
answered the wrong question: it proved the binary is glibc-LINKED, not
which glibc VERSION the base must carry, and nothing short of running
the image could ask that one. Three live-only catches this release
week (the SPA fallback, the planted checksums, this) — all three in
surfaces every offline gate passed.

The fix pattern matters more than the base bump: an image is packaging
around the untouched release binary, so image.yml gained a dispatch
rebuild — re-push :v0.0.3 on a working base, no v0.0.4 needed, while
the release itself stays immutable. :latest on a rebuild follows
releases/latest, never the dispatcher's whim.

## the first real install found what no fixture could (2026-08-20)

v0.0.2's `curl weir.sh/install.sh | sh` worked — and printed six
"command not found" lines first. The template's header comment NAMED
the sums placeholder, the generator's contains-match ate the comment
and planted the six checksums bare at the top of the script, and every
offline pin walked past it: bare hash-name lines parse, the
leftover-placeholder grep was satisfied precisely because the mention
got consumed, and the extraction read the real heredoc. The only
reason it was noise and not a broken install is that the planted lines
sat above set -eu. A defect with three green gates standing on it —
found by the one check that cannot be faked, running the served
artifact for real.

The fix is the anchor rule stated properly (the line that IS the
placeholder, trimmed-exact), templates that stop spelling their own
placeholders, and an exactly-once-per-checksum pin that catches any
second copy wherever it lands. The served v0.0.2 stays noisy — the
release is immutable, GitHub-enforced — and the fix rides v0.0.3,
which is exactly the economics the immutable-tags ruling bought.
Also fixed in the pass: the post-deploy check that lost a race with
edge propagation now polls before it accuses.

## v0.0.1 died queueing, and the recovery worked (2026-08-20)

The osx-x64 job waited forever for a macos-13 runner GitHub retired in
December — the label we added back when the rider guessed the missing
platform was an omission, verified against a matrix that was already
dead. The interesting mechanics: a tag's workflow run is pinned to the
workflow file AT THE TAG, so no main-side fix can rescue a tagged run —
the immutable-tags recovery (cancel, fix, next number) is not just
policy, it is the only mechanism that works. Two recoveries before
anything shipped: rc1 for the changelog, v0.0.1 for the runner. Both
tags stand as records.

The fix kept the platform: macos-15-intel is the free x86_64 label,
with GitHub's own sunset (Fall 2027, when Intel macOS goes entirely) —
so the drop-Intel decision has a date instead of a debate, and the
matrix comment carries it.

## the rehearsal dissolved into the release (2026-08-20)

Once tags became immutable, the rc lost its reason to exist: its whole
design (throwaway tag, deletable everything) assumed mutations we no
longer allow, and under the new rules a failed v0.0.1 and a failed rc
have identical cost — delete the release, cut the next number. Add
that most of the rehearsal's receipts were already collected without
any release (staging, content-type, 404s, the changelog gate's live
red, the offline pins), and the rc was a number without added safety.

The honest residue is named rather than buried: the prerelease→staging
routing and the :latest-withholding are now unexercised code paths,
waiting for a genuinely risky change to warrant an rc. The release
procedure absorbed the verification checklist, and its last step is
the recovery: delete the release, never the tag.

## the rehearsal's first catch caught the runbook too (2026-08-20)

rc1's tag went up without its changelog section and the gate refused —
the exact red it was built for, collected live. But the obvious repair
(re-point the tag at a fixed commit) and the runbook's own teardown
(delete the tag) were both history mutation, the thing this project
refuses everywhere else. The user called it: we shouldn't be deleting
tags.

The fix was a ruling plus an enabling change. Tags are immutable —
failed attempts get the next rc number, teardown deletes only the
release, and rc1's tag stays as the record of the gate's first catch.
The enabling half: kept rc tags must not poison the gates, so
release-published and site-staleness now count stable tags only. A
prerelease is excluded from releases/latest by GitHub's own
semantics — demanding publication of a tag that cannot satisfy /latest
is what generated the delete-the-tag pressure in the first place. The
trade is stated in the row: the rehearsal no longer shows
release-published red (an rc cannot trip it); that observation moves
to the real release's tag→publish window, where it happens inherently.

## the reference arrives by subtraction (2026-08-20)

The audit reframed the job exactly as the plan suspected, with one
twist: SEMANTICS' archaeology is not in sections you could cut — it is
marbled into the rule bullets mid-sentence, dates and plan names and
gate-owner waivers wrapped around every rule. Only ~50 of 2021 lines
are purely archaeological, and one of those blocks is a BACKLOG, which
is planning pretending to be semantics. The audit also caught a rule
that had silently drifted ("no elif (parked)" — elif shipped weeks
ago), which is the strongest single argument for retirement: fences
execute, prose rots, and a 2000-line prose document cannot be fenced
into honesty.

Half A cost almost nothing because everything already existed: the
docs were data (builtinDocs), the coverage was pinned, the examples
already ran in the unit suite, and the REPL's #help had already
forced the one-source discipline. The whole generated half is a
60-line JSON dump plus two Astro pages, and its currency gate is the
grammar-manifest pattern verbatim. 21 modules, 207 members, zero
undocumented on the first dump.

The calibration slice (Lexical) came in at ~150 lines and 16 fences —
every weir-error probed against the binary before landing, and all 16
held in skill-doc on the first full run (143→159 blocks). At that
rate the remaining eleven sections are a-section-per-sitting work,
with Commands and Types the big ones.

One self-inflicted scare: 15 fuzz "failures" that were my own
HOME=/tmp/h build workaround leaking into the runner's default binary
path — the read-only home strikes again. WEIR_BIN pointed at the real
binary: 47/47.

## the whole GUIDE, de-walled (2026-08-20)

The full prose pass landed in one stretch after all: every remaining
wall broken into single-idea passages, with the REPL mega-bullet (one
list item, fifty lines, a dozen ideas) becoming a six-passage section
with its bindings table intact. Two register decisions worth
recording: "district" no longer appears in GUIDE at all — five uses,
zero definitions, was the purest form of the register defect — and
the [D:] ledger keys came OUT of GUIDE prose entirely: a stranger
cannot follow them, the site renders them as noise, and the citations
still live where maintainers read (SEMANTICS, SKILL, the code).

The pass also caught two glued-sentence defects — the ConfigMap line
and the try/finally passage each carried two spliced versions of the
same sentence, artifacts of earlier edits that no gate could see
because prose does not execute. skill-doc held at 143 green through
all ~30 edits, which is what made a mechanical sweep of a document
full of executing examples safe.

## the stylesheet that styled nothing, and the prose pass (2026-08-20)

The typography question ("theme default, or did something remove it?")
had the best possible answer: neither — the styles were written,
shipped, and never matched anything. Astro scopes component styles,
markdown content carries no scope attribute, so the entire content
area — code, tables, blockquotes, leading — rendered browser-default
while the stylesheet sat there compiled to pre[data-astro-cid-…]. One
is:global fixed more than the rider asked for, because everything the
rider diagnosed was downstream of the same miss.

The register pass confirmed the screenshot was representative: 3 of 5
sampled sections needed real work, and the failures were exactly the
rider's four kinds — a cloned-repo cross-reference (fuzz.weir named as
if the reader has the tree), lineage-as-rationale (bash's $0), rules
bundled at density ("the same attribute, each consumer's own law"),
and the purest form: "district" used five times, defined nowhere. The
worked target's shape (facts into short sentences, spellings on their
own lines, the why GIVEN) held up across all five rewrites without
losing a fact, and two claims became fences on the way — the
Default-false rejection's fence comment is the checker's own message,
so the doc and the diagnostic cannot drift.

The remaining ~30 sections at the sampled ratio are a multi-session
pass; filed. skill-doc 141→143 green throughout — the examples could
not silently break, which is what made a prose pass on a doc full of
executing code safe to do at all.

## the site went live and the first fetch found the gap (2026-08-20)

weir.sh is up — docs render, changelog renders, install.sh serves
text/plain at the edge (the _headers rule works; the content-type
question from the runbook is answered). And the very first verification
fetch surfaced the thing the rehearsal exists for: /install.sh answered
200 with the HOMEPAGE. Cloudflare Pages without a 404.html serves
index.html at 200 for every missing path, so the pre-release
docs-only deploy made `curl -fsSL | sh` pipe HTML into sh instead of
failing. A 404.astro page fixes it (Pages switches to real 404s the
moment dist/404.html exists), and the staleness gate gained a worded
arm for exactly this shape — red-tested against the LIVE fallback by
pointing the check at a repo that has releases, so the site arm
actually ran. Also learned the container's egress proxy needed weir.sh
allowlisted, and `docker info` prints a Server: header even when the
daemon dial fails — two instrument-reading traps in one day.

## the image is the binary with a digest (2026-08-20)

The container rider's framing did all the work: an artifact, not a
product. Phase 0 killed two wrong turns before they started — the
.dockerignore that suggested prior container work belongs to
ci/local.sh's battery container (a dev artifact; the release image is a
different thing with a different name), and ldd answered the base
question in one line: the AOT binary links glibc, so distroless/cc, not
scratch. The workflow is the site deploy's shape a third time: publish
event, artifacts from the firing release, prerelease routing (here:
:latest withheld rather than staging-aliased).

The honest part is what could NOT be verified: this container has a
docker client and no daemon (docker info prints a Server: header even
when the dial fails — a trap worth remembering). So the -it question
was probed at its root instead: the REPL under a pty with a
distroless-shaped env — no TERM, minimal vars — renders the prompt,
evaluates, quits. The build/push/pull/size half is stated as untested
and lands with the rc1 rehearsal or a lent DOCKER_HOST.

## the grammar that was green and 70 commits stale (2026-08-20)

The tree-sitter check asked "do I agree with the ref I chose?" — a
question that cannot come back no. Third sighting of the frozen-subject
instrument (fuzz driver reporting 10k while running 200; the census
nobody invoked), and this one had a second frozen pin stacked on top:
Zed pinning the grammar's ROOT commit while the grammar pinned a weir
ref 70 commits old. Three links, two stale pins, and the coupling
carried by a well-written comment nothing enforced.

The root-cause framing did the design work: the check lived in the
wrong repo. A grammar repo can only compare itself against a target it
chose; the party that KNOWS weir moved is weir. So the new checks live
weir-side (grammar-currency compares the grammar's PUBLISHED main
against weir's own gate-verified manifest, content-to-content, same
extraction shapes as the grammar's own gate so they cannot disagree),
and the grammar repo's own instrument unfreezes by tracking a RELEASE —
a number with ordering, the bare-hash --version finding wearing a
different hat. Pre-first-release it tracks moving main, which is the
honest transitional subject.

The establishing question paid off: "tree-sitter via the manifest" in
the e2e cell names turned out to mean only "the manifest is current so
the OTHER repo can trust it" — the Zed rev and highlights.scm were
fenced by ritual alone. So the zed check is two assertions, and the
second (queried nodes exist at the pinned rev) caught its own
extraction bug in testing: scm comment prose like "(a query override)"
collects as node names unless ;-comments are stripped per line.

The red tests cost nothing because the tree WAS the failing state:
grammar-currency red against the pushed main is today's live gap, and
zed-rev red against c333922 was free. Both went green against the
refreshed clone — with the standing caveat that the new rev pins a
LOCAL commit sha, so the grammar push must be fast-forward of exactly
that commit.

## Http gains a default User-Agent, and the friction log paid out (2026-08-20)

Yesterday's friction entry (Http sends no UA; GitHub 403s that with a
status that reads as auth failure) came back as a rider within a day —
the telemetry loop doing what it exists for. The implementation point
turned out to be one send-time check in Http.fs: Builtins merges auth,
headers, and secretHeaders into one pair list before the transport leg
sees them, so "explicit wins, from either path" needed no separate
handling — the caller's UA is already on the message and blocks the
default. Exactly-one held on the first run because .NET's header-name
comparison is case-insensitive (a lowercase `user-agent` blocks too);
the duplicate-UA bug the pairs shape invites never materialized, but
the e2e /ua fixture counts arrivals anyway, in all five cases.

Full stamp over tag-only: the UA is weir stating identity, and the
stamp is already the one string weir states identity in — `--version`,
serverInfo.version, now the wire. The e2e pins the relationship to
`--version` output, never a literal, so releases don't break the pin.
Send-time application (never a field of Http.defaults) keeps shown and
pinned requests stable across releases; the cost — `show req` omits a
header the wire carries — is stated in SKILL rather than discovered.

## the site lands as a skeleton, and the GUIDE was lying in two places (2026-08-20)

The site scope call came back "build it," so site/ now holds an Astro
skeleton whose whole design is reuse: Shiki eats the existing tmLanguage
grammar (53 weir fences render through the grammar e2e already gates —
no fourth grammar), the content loaders point at ../docs and the repo
root (no copy, no sync step), and the one piece of real work was the
remark plugin — three rewrite rules for relative .md links (rendered doc
→ /docs/slug/#anchor, CHANGELOG → /changelog/, everything else → the
GitHub blob URL). Frontmatter-free markdown went through as-is with no
schema; titles derive from each file's first heading. The generator will
not be written in weir — markdown→HTML needs a parser weir lacks, and
shelling out is a dependency without the dogfood benefit — recorded in
the row so it stays declined.

The deploy insight worth keeping: the `release: published` event CARRIES
the release, so the workflow fetches installers from the release that
fired (never /latest, which races and excludes prereleases) and routes
by `release.prerelease` — a prerelease deploys to the staging branch
alias, a full release to production. The rc1 rehearsal therefore
exercises the production chain against staging with zero workflow
changes. Fail-loud is by construction, not host trust: direct upload
only creates a deployment when wrangler runs, and the research turned up
a community-reported Pages git-integration case where an exit-1 build
still deployed — the exact failure mode the direct-upload choice removes.

The GUIDE stranger-read caught two real defects, both the prose-not-
executed shape the DX review named. The attributes section still called
typed argv "a coming feature" — Args.load has shipped for weeks and is
documented at length later in the SAME file. And the front-door
section's own tail (flag derivation, subcommands, positionals) had been
stranded inside "Scraping text", so a stranger reading about regex hit
argv semantics mid-section. Seven behaviour claims became weir-error
fences (dropped value, with-adds-fields, apply-a-param, unit-param
arity, literal non-exhaustion, typo'd-constructor did-you-mean,
discarded exitCode) — each verified failing with the documented message
before landing; skill-doc went 134 → 141 blocks.

The changelog gate's exact-match ruling is small but load-bearing: the
workflow's awk and the weir gate must agree on one heading spelling, so
the gate refuses stray whitespace BY NAME instead of letting the two
parsers drift apart — the same one-source argument as the release body
being extracted from the file rather than typed into a text box.

## pre-tag code: the stamp gains a tag, the splice hint gains a path (2026-08-19)

Two of the pre-tag items were pure code and independent of the site, so they
went first.

The version stamp was the more interesting one because the constraint was
hidden. `--version` printed a bare hash, and the obvious fix — always print
`tag+hash` — collides with the freshness gates, which prefix-matched the whole
stamp against HEAD. Tag-first-for-humans and hash-first-for-gates cannot share
one string. The reconciliation is to stop prefix-matching: the gates now read
the hash as the part after the LAST `+`, which is backward-compatible (a bare
stamp has no `+` and passes through), so the stamp can lead with the tag. The
untagged tag is `0.0.0-dev` — an honest marker that sorts below any release;
an empty tag would be dishonest, and untagged is what every dev build shows.
The gates only ever run on untagged builds (the gate job never sets
WeirRelease), so the release format was never the gate's concern — worth
confirming before touching the load-bearing stamp mechanism rather than after.

One source, two consumers: `Weir.Version.current`, read off Weir.dll itself
(not GetEntryAssembly, which in-proc is the test runner), feeds both
`--version` and the LSP `serverInfo.version`. The strongest pin is the
lsp-e2e asserting the two are byte-equal — two lookups is the shape that
drifts, so there is one, and equality proves it.

The splice hint was smaller but sharper: the homepage hero is
`rm -rf ./tt3/$build`, and the diagnostic's leading repair — "add a space" —
produces `rm -rf ./tt3/ $build`, a directory and a separate operand, a worse
command. Path-joining is not flag-joining. The fix detects a path separator in
the word-under-construction (a backward peek over the already-read word) and
leads with interpolation and Path.combine for paths, keeping the space repair
for flags. Both branches are pinned in e2e and fenced in SKILL so neither is
incidental.

## the same mistake twice, and what it has in common (2026-08-19)

Second amendment to the record-pattern rows in two days, same shape:
a rule that correctly governs one pattern form was applied to record
patterns without testing whether its reason transfers. Refutability
first, then known-type. The rider's diagnosis is exact — `Some 1`
needs nominal identity because it names a case from a closed set;
`{ name = n }` names a field and needs a row. Two errors that read
alike resting on different requirements, and the inheritance came
from the reading rather than the requirement.

The fix was threading ctx through checkPattern and copying EField's
row arms, but it forced a second change that generalises past
records: checkPattern's arms DEMANDED types rather than unifying, so
a literal under a row-typed field failed. Patterns whose shape
determines a type now bind an unresolved scrutinee first — which
also makes `match p with | 1 -> ...` work on a bare param, the same
principle one level up. Constructor patterns stay off that list
deliberately; theirs is the shape that genuinely needs the nominal
type.

Two probe designs bit me the same way in one session: both my
accumulation test and my e2e provenance fixture led with an
irrefutable arm, so the second arm was unreachable and the harness
correctly said so rather than answering the question I meant to ask.
When testing accumulation across arms, the first arm has to be
refutable or there is no second arm to accumulate from.

## the park that was priced wrong (2026-08-19)

The rider's argument was the whole session: refutable record patterns
were parked as "exhaustiveness over field values", and that burden
never existed because weir already rules that literal arms never
complete a match. The costing had leaned on that rule twice without
noticing it answered the question it was pricing. One deletion.

Except it wasn't quite, and the exception is the interesting part.
Removing the filter exposed a hole that had been unreachable by
construction: missingCases had no record case, so a record scrutinee
reported nothing missing — fine while every record pattern was
irrefutable (the early guard caught them), and a silent
always-exhausts the moment one wasn't. The feature's own fence found
it: the no-catch-all pin failed the way a missing rule fails, not the
way a bug does. Records now answer like tuples.

The other half of the original session's work turned out to be
half-built rather than absent: isIrrefutablePat already judged record
patterns BY CHILDREN, and the filter sitting above it made that
correct code unreachable. So binder rejection, match composition, and
dead-arm detection all just started working — three report-back
questions that answered themselves.

## the installer goes two-origin and pinned — decided before the tag (2026-08-19)

The deferred half of the install rider came back a "take it, before the
first tag." The reasoning is timing, not security: retrofitting two-origin
is unrecoverable once people have bookmarked a URL — flipping always-newest
to pinned after the fact is a silent behavior change — so it's a decision to
make while nobody depends on it, or not cleanly at all.

The shape that fell out is nicer than expected. install.sh/install.ps1
become TEMPLATES (`@WEIR_TAG@`/`@WEIR_SHA256SUMS@`); a weir script,
ci/gen-install.weir, substitutes the tag and embeds that release's
SHA256SUMS to produce the artifact weir.sh serves. The dogfood is that the
just-built RELEASE BINARY generates its own installer in the publish job —
no second weir build, and the generator is the scripting policy in its own
release path. Because the served script's checksums arrive from a different
origin than the binaries, verification never touches the binary's origin:
tamper-evident, not just integrity.

Two template traps, both caught before they shipped. The generator does a
blanket `@WEIR_TAG@`→tag replace, so a template guard keyed on the literal
`"@WEIR_TAG@"` would be substituted along with everything else and always
fire (or never) — the guard keys on `*@*` instead, since a real tag never
contains '@'. And the SUMS placeholder had to move into a heredoc so BOTH
the template and the generated artifact `sh -n`-parse; the first draft put
it in a plain double-quoted string and the whole-line expansion ate the
`SUMS="` prefix. Offline e2e generates against a synthetic SHA256SUMS and
checks parse + no-leftover-placeholders + embedded-checksum-matches-binary;
the actual download→install path stays untestable until a real release and
weir.sh serving the asset, said plainly rather than papered over.

gh attestation rode the same pass (one workflow line + a best-effort verify
in the installer), and osx-x64 turned out to be a build-matrix omission, not
a decision — the AOT publish is native on a macos-13 runner, so it's one
matrix line and INSTALL's "stated gap" paragraph goes away. The canonical
URL moved to weir.sh/install.{sh,ps1} everywhere; raw.githubusercontent was
a third copy of "where the script lives" that the site would never serve.

## install-script hardening: the truncation trick and an honest checksum (2026-08-19)

Six findings against install.sh/ps1; the install work split cleanly
into implement-now and decide-first. The one real defect was the
curl|sh truncation hazard: a dropped connection leaves a shorter but
COMPLETE script that runs up to the cut, and set -eu is blind to it
(nothing failed). Wrapping the body in main() invoked on the last line
turns a truncated fetch into an unclosed-function syntax error —
defines nothing, runs nothing. It's the canonical curl|sh objection
and the canonical one-trick answer. install.ps1 needs no equivalent:
irm buffers and throws on an incomplete read, so iex never sees a
partial (confirmed, not assumed). The pin is offline — no published
release needed — because it's a `sh -n` parse property.

The honest-comment fix mattered more than it looks: the checksum
verification catches truncation and CDN corruption but NOT tampering
(SHA256SUMS shares the release origin), and the old comment ("worse
than no installer") oversold it. Corrected in both scripts, the ps1
header, and INSTALL.md. The two-origin option (bake per-release
checksums into a script served from weir.sh) is a real workflow change
that pins the script to a version — deferred to a decision before the
first tag, not made unilaterally.

The gate that outranks all six: releases/latest excludes drafts, and
[D:releases] produces drafts, so tagging alone leaves the installers
404ing with no hint the fix is a publish click. A weir script
(release-published.weir, wired into CI) fails loud on exactly that
state. A check, not a reminder — it keeps working when nobody reads
the workflow output.

## the metamorphic red was the generator all along (2026-08-19)

Followed the width crash's unmasked signal and it landed somewhere
better than the hypothesis. The ~8/47 fresh-seed metamorphic failures
weren't the assembler: filtering to the output-neutral invariants at
seed 771349 count 10000 gave 8 "base program rejected: integer
overflow" and ZERO "transform changed behavior". So the transforms
are correct; the base PROGRAM overflows. The harness needs base rc=0
because error-message line numbers shift under blank insertion, so it
can't compare erroring bases — the generator is supposed to produce
runnable programs and wasn't.

The cause matched the shape of the guess without matching its target.
"State accumulating across statements" — yes, but the state is
generated VALUES, not assembler state: `let vN = v(N-1) * v(N-1)`
squares each statement, 99^(2^N), and more statements (higher count →
higher endSize) overflow int64. weir's overflow detection is doing
its job; the fuzzer was handing it invalid programs and calling them
valid. 1500 passes, 10000 fails, for exactly this reason.

The fix bounds generated int values by construction (genIntExpr, cap
1e9, + splits the budget, * takes its sqrt). It's structure-
preserving — the assembler invariants test shape not magnitude — so
the one casualty (var*var, dropped to hold the bound) renders
identically to any other EBin and costs no coverage.

Two ledger facts fell out. The 3-fresh-seed gate on the width fix was
BLOCKED by this, not waived — a named blocker is auditable, a waiver
invites the READ drift. And the generator has no nested-pattern arm at
all, so record patterns shipped unfuzzed and the width crash was only
ever reachable through a hand-written fixture; a wide-not-deep
generator axis closes both. This is the fifth time this month a fix
unmasked something the defect was hiding — the argument for chasing
these promptly, before unexplained red gets normalised.

## the width axis: patLeafNames overflows on a wide pattern (2026-08-19)

A Property-3 falsification the coverage machinery couldn't see. The
depth-guard sessions closed the SEGV class by making axis N+1 a CI
failure, but the gate reasons about recursion STRUCTURE (graph minus
guarded nodes acyclic) — it is blind to unbounded recursion over a
bounded-DEPTH structure. patLeafNames recursed once per leaf, so a
`a :: a :: … :: _` cons chain (flat source, deep spine, like the
opSpine SEGV but in a name walk) blew the stack during parse. Width,
not depth.

It hid behind stack size: the depthDiagnoses harness runs on a
Task.Run 1MB thread, where a 20000-node walk overflows; the main
thread's roomier stack does not — so #85 merged green (its CI ran the
fixtures on whatever stack) while my tools/fuzz.weir path (→ 1MB
thread) crashed, surfaced by the observed-count e2e row at seed 4242.
That row earned its keep twice now.

Two findings worth more than the fix. The membership sweep:
checkPattern/binderShape/isIrrefutablePat are sibling per-element
pattern walks, but the parse depth guard bounds pattern nesting — a
width probe over seed 4242 found zero patterns exceeding 600 nodes
reaching the name walk, and direct deep patterns hit the guard at
~500. patLeafNames was the outlier only because it runs during the
parse ATTEMPT, transiently exposed before the guard rejects. And the
generator gap: Grammar.fs produces NO nested patterns at all — only
name-binders and single-level Ctor arms — so record patterns (#85)
shipped with zero generative fuzz coverage. The pattern axis is
entirely the hand-written depthDiagnoses fixtures; a wide-not-deep
generator arm is the real mechanical answer, filed.

## record patterns: the row seat pays for the whole feature (2026-08-19)

The costing's shape survived contact almost untouched, which is rare
enough to note. The one real design moment was recognising that the
pattern's field list should become row constraints KEYED BY THE
FIELD-NAME SPAN — after that, generality, provenance, and the meet
note were all inheritance rather than work. The by-construction
claims (casing law and builtin reservation reaching record binders
through the PVar leaf) held on first run, which is what
by-construction is supposed to buy.

Two small lessons for the file. A parse-time fatal for `{ }` was
swallowed by an enclosing attempt exactly as [D:anchor-residue-ab]
warns, so the refusal moved to the checker where both binder paths
already meet. And the hole-defaults teaching ([D:hole-defaults])
surfaced twice during probing looking like a record-pattern bug —
a binder used only in a bare interpolation hole defaults to string,
and with a record param the row note rides the mismatch. Pre-existing,
correct, and composing; it cost twenty minutes of suspicion anyway.

until's binder turned out to be a plain identSpanned, so it did not
come free and stays a name — recorded rather than forced. The
feature ships on preference with the receipt scan re-run empty; the
row says so in those words, so the park's trigger cannot be
misremembered as having fired.

## colour by not being in the middle (2026-08-18)

The plan's own reframing held: the expensive answer (a pty so isatty
lies truthfully) dissolved into not redirecting at all, and Phase 0
kept shrinking the risk. Stderr was already inherited on the
statement path, so the change extends a precedent by one fd; the
byte-identity worry (0.4) vanished by gating on the |print arm's own
existing tty test — a redirected weir keeps the pipe and every byte
law with it. The guard ruling paid immediately: bare gzip at the
REPL now refuses its own terminal, fast, with its own message — the
incident that motivated the guard cannot be set up any more, and the
e2e cell pins the cure where it used to pin the symptom.

The one thing built and torn out the same hour: a DSR-based mid-line
tidy. Console.CursorLeft hangs under any terminal that does not
answer the cursor query — the pty harness ate three sessions
silently before the cause surfaced. bash's wart (a mid-line tail
overwritten by the next prompt) is the honest posture; a tidy that
can hang a session to fix a cosmetic is the wrong trade.

The relay kept exactly one customer — |print, which was always the
sanctioned raw spelling — so the streaming-echo work survives with a
narrower purpose, and the 4KiB/100ms bounded guard retired with its
only subject. The divergence the row names (value paths pipe,
statement paths inherit) is the first deliberate break of the
agreement property this project keeps choosing; the reason — a value
must decode, and decoding requires a pipe — is the whole argument.

## reserving the names the language owns (2026-08-18)

Phase 0 turned the plan's cleanest sentence into the actual design.
There WAS a standing rule — values shadow builtins, stated in
SEMANTICS and pinned twice — and deriving the builtin set showed why
it existed: most bare names are aliases of qualified members, and
reserving `max` or `item` or `find` would make ordinary scripts
illegal. The rule survives for exactly those, because the qualified
home is an escape in the same structural position as PATH's ^. The
no-home set — thirteen names, derived from the flat entries minus
the alias map, never hand-listed — is where the plan's no-way-back
argument lands whole, and those are now reserved in every binding
position.

The implementation found the second disagreement the plan predicted:
not just command-vs-binding (bare `ls` statements were already
refused with the ^ls teaching while `let ls` bound silently), but
two PATTERN paths — binderShape routed through the casing gate,
checkPattern's PVar did not. One choke point now, which is what
checkBinderName was always shaped to be. The two shadow pins flipped
rather than retired: the same instrument now asserts the refusal,
and the positive twins pin the alias distinction from both sides
(`let max` binds AND Seq.max still answers).

## the coverage rider: a gate measured, a pin that tested nothing (2026-08-18)

The sweep-gate probe needed one insight to be possible at all: a ^C
during a CHILD dies-and-unwinds the scope, so the only way to hold a
within-tmp scope open across a survived signal is a slow PURE body —
the fold keeps the scope alive while the SIGINT arrives, cancels,
and (the measured fact) does not sweep. En route the editor taught
its own rule: plain Enter submits a block the moment it parses
complete, so multi-line bodies enter via Alt-Enter — one more line
in the harness header.

The Path.under cell caught something better than a gap: the unit
pin's UNC row was VACUOUS. Weir strings escape backslashes, so
"\\server\share" as weir source is a PARSE error, and
Expect.throws passed on it — the instrument-defect genus (fails
toward a pass) living inside a test. The doubled spelling tests rule
7 for real now, in the pin and the cell both, and the
backslash-leading refusal neither had is pinned too.

The rest was book-keeping the rider asked for by name: the macOS
verification recorded (repl-isig's cell is green there — the stated
divergence risk held), PyYAML onto the macOS image (one line,
doubling the referee), and the within-lock skip now says UNTESTABLE
rather than unrun — flock(1) does not exist off Linux, which is a
different fact than nobody having tried.

## the split closes, and .NET has opinions about terminals (2026-08-18)

Ruling (a) held, but the diagnosis needed one more layer than the
review recorded: restoring the cooked termios around eval WORKED
(tcsetattr rc=0, read-back isig on) and the child still saw -isig ten
milliseconds later — .NET re-applies its own terminal notion when a
child spawns, so the real fix is telling .NET (TreatControlCAsInput
off around eval), with the termios restore as the immediate half.
The debug prints that found this were three lines and worth more
than any amount of theorising about ConsolePal.

The transition-window question the plan flagged dissolved rather
than resolved: the cancel registration means no ^C timing can kill
the session, so the windows were probed (6/6 benign, byte adjacent
to Enter and aimed at child-exit time) but the safety is structural,
not empirical. The sweep gate is the subtle dependency — both signal
handlers run and Cancel only stops termination, so without the gate
a survived ^C would have deleted live within-tmp dirs mid-session.

Two deliberate fate changes rode along: a delivered SIGINT at the
prompt now survives (bash parity; the review measured death), and
^C during a pure computation does nothing until the next prompt —
weir has no cooperative cancellation and the honest doc line says
so. Linux-verified only; the e2e pins carry the claim to macOS when
CI runs there.

## the pty items: one byte explains the incident, two more instruments die (2026-08-18)

Phase 0 paid for itself before the first probe ran: the gzip incident
is three source lines seen from the tty's side. TreatControlCAsInput
clears ISIG and nothing restores it around eval; a bare command
inherits the pty as stdin; the child od's its read and there it is —
03 68 69, the ^C as data. The rider's framing held exactly: C14's
cancel claim and the SIGINT observation are one mechanism, and the
script path (isig on, group delivery, abort-at-the-fault 10/10) is
the working half that shows the REPL half is a MODE, not a law.
Nothing was fixed; the fix shape is obvious from 0.2 (restore ISIG
around eval, or forward from the editor) but it is its own session.

C14 itself: single-arm verified, both paths. The concurrent case is
the finding — the kernel distributes the BYTES of one typed line
across simultaneous readers (one arm printed PICKED-oe, the other
PICKED-ntwo; "one" split o/n/e). Not degradation, interleaving at
the character level, 4/10. The docs' single-arm claim should stay
single-arm.

The instrument ledger grew by two, both toward a pass, both caught
by this review's own controls. pty-run.py reported "EXIT timeout"
for a child that died at 1ms — the reap loop keyed on the OUTPUT
side staying open, and gzip's tty-refusal closed it first; the real
status was collected and discarded. Then B3 reported "weir survives
a direct SIGINT" ten times straight — bisected to the harness
preamble: a non-interactive shell's `&` bequeaths SIGINT=SIG_IGN
through exec, so the probe was signalling a process born deaf. The
instrument now resets dispositions in the child, and the same probe
says died 10/10. Between the two: weir honours an inherited SIG_IGN
(the nohup courtesy) — a denominator fact found by instrument
autopsy. The editor-echo self-match (type "alive", grep ALIVE) and
Enter-is-CR joined the harness header as standing rules.

## the referee ran a different string universe (2026-08-17)

The membership sweep the rider asked for came back with a number too
big to audit (196 culture-sensitive StartsWith/EndsWith sites, 9
string-arg IndexOf/LastIndexOf) and a fact that dissolved it: the
shipped binary has run InvariantGlobalization=true all along, and
invariant mode IS ordinal — production was never exposed. The real
finding is that the test hosts were not: dotnet test ran under ICU,
where U+001F, NUL, ZWJ and the soft hyphen are ignorable
("a<US>b".StartsWith "ab" is true, String.Compare calls them equal,
LastIndexOf lands past the end). So the completion crash could ONLY
ever fire in tests, and the language surface (Str.startsWith is
s.StartsWith verbatim) was being refereed under semantics the binary
does not have. The mode landed in all three host fsprojs; the
ignorables pin was shown failing in the culture host first, and the
1354 existing tests all held under invariant — nothing in weir
wanted culture. The class fix is one property per host, not 205
annotations; explicit Ordinal stays reserved for crash sites.

## path completion learns what a parameter wants (2026-08-17)

The rider priced its own recommendation on a hand-maintained registry
and the price was wrong in the good direction: builtinDocs' named
params ARE the registry, and the docs-coverage pin from the previous
session guarantees they exist — so `named ["path"]` marks the
position and a future member enrols by documenting itself. The two
sessions composed by accident: the coverage check was built for
hover, and its first structural customer was the completer.

The membership sweep answered wider than the rider's list again, in
both directions: it missed Dir.copy, Path.under, and the four Path
string queries; it wrongly included `within tmp`, which BINDS a
fresh name — path-completing a binder position would vouch for
collisions. `cd` needed its docs entry written (a bare builtin,
outside the module sweep), which closed its hover gap as a side
effect.

The instrument scar is a BCL comparison default: culture-sensitive
String.LastIndexOf treats U+001F as ignorable and "matches" past the
end of the string, so the sibsep cut threw on every completion — 27
collateral failures from one missing Ordinal. And the invisible-byte
lesson recurred in its pettiest form yet: the comment EXPLAINING that
trap briefly contained a literal U+001F, which then rode two
tool-call round-trips before chr(31) could name it without carrying
it.

## the File.stat rider: an existence lie and eleven silent members (2026-08-17)

F1's premise needed correcting before its fix: the rider predicted
File.mode raised "no such path" off a following File.Exists, but
.NET's File.Exists is already lstat-true for a dangling link on
Unix — so the guard PASSED and GetUnixFileMode surfaced the raw BCL
"Could not find file". Less honest than assumed, same defect class.
Ruling (a) landed: existence is not a mode question, so the dangling
case now fails at the READ with its own message (link-vs-absent split
on LinkTarget), while the mode read keeps following — the File.*
group's consistent reading, and (b) would have prejudged the parked
mode-on-the-row question. [D:mode-existence]

F2's count was the finding: ELEVEN members with no builtinDocs entry,
and the shape of the eleven is the argument for the check — not a
scattering but the entire Log module (8 members, a whole module
shipped without a single hover) plus the pmapWith/piterWith twins and
File.mode. The check is a unit pin, not a fourth harness: the subject
is two in-process maps (env.Modules x builtinDocs), where
skill-surface must shell out through #help because SKILL.md is prose
outside the binary. Shown failing at 11 before the entries landed;
enumeration must exceed 100 before zero-missing can pass.
[D:docs-coverage]

## File.stat: the bridge, and the agreement that came free (2026-08-17)

Phase 0 reshaped nothing, which is its own finding. `ls` cannot take
a path — it is a typed VALUE, not a function, so `ls <path>` is a
value/function overload the surface does not do (and `within cd`
already lists elsewhere); the bridge was the right first fix, not the
narrow one. The enumeration question answered with a probe: one
lstat per entry, cached on the FileSystemInfo, Length/LinkTarget/
UnixFileMode all served from it — so `mode` on the row is now
cost-free, but it stays a query: [D:filerow]'s recorded reason was
"narrow facts are queries, not columns", never cost, so nothing
dissolved and nothing landed.

The implementation IS the agreement argument: File.stat is lsRow over
one resolved path, and the probe settled the construction split —
FileInfo.Exists is lstat-true (a dangling symlink is a row, not an
absence), directories and links-to-them take the DirectoryInfo shape
exactly as the enumeration does. The agreement pin passed
byte-identical on the FIRST run, all four shapes (plain, dir, live
link, dangling) — the report-back item closes as "nothing in FileRow
construction assumed the enumeration path", because after the reuse
there was no second construction path to assume anything.

One instrument scar, F#-side this time: the BCL probe printed nothing
for its first run — printfn with nine format specifiers and eight
arguments partially applies and the loop discarded the function
silently. The suite's own parallelism caught the real hazard: a
second setCwd test races the first (17 collateral failures in one
run), so fileStatTests is sequenced with the reason in the comment.
## the commit-area rider: loud beats plausible (2026-08-17)

Post-rider, the check itself got the treatment the repo's own policy
demanded from the start: it is a WEIR script now (ci/commit-area.weir
— the bash version was an unlogged fallback, which is exactly the
kind of drift the policy line exists to catch; the user caught it).
The rewrite was also a fair ergonomics probe: the offside rule
refuses F#'s dedented guard-else (full nesting instead), and a
command in a match ARM needs the $() capture — both already
documented, both hit anyway. The CI step moved after publish since
the check needs the binary; the price of dogfood, paid where it
shows.



The follow-up's audit vindicated R1 immediately: the cli catch-all
was already covering seven files — six that belonged there and one
(the fsproj) that was never a semantic area at all, just build infra
that should ride. A fallback that produces plausible answers had
been producing one wrong one since the map was written, which is the
whole argument in a sentence. Unmapped src now stops loud with the
file named; unmapped root files fall back to docs and say so.

Two rules minted while the reasoning was fresh: derived checks print
their derivation (the autocomplete-with-a-gate property, stated
before the next gate-shaped thing gets built without it), and there
is ONE commit-message harness — ruled before a second existed,
which is the cheapest a fourth-harness question will ever be.

## commit areas: the taxonomy was already in the log (2026-08-17)

Phase 0 did what it was told and the answer reshaped the design
twice. The vocabulary extraction was easy — the sessions describe
themselves by file, and the counts confirmed every candidate area
except none. The number that mattered was 53/60 multi-area: not
because commits are sloppy but because this project's whole ritual
lands a feature WITH its pins, docs, and ledger rows in one commit.
A convention that fights that loses; the riding rule (src areas ARE
the set; everything else rides) encodes the ritual instead, and the
effective multi rate falls to sets of 1-3 that read like the prose
already does.

The informal-format answer was "yes, two of them, at war": type
prefixes (add/fix/chore, 27 of 60) and area prefixes (repl/ledger/
notes, ~8). The area shape carries information the diff can VERIFY;
the type shape carries what the prose already says. Types retire.

The confession the plan demanded up front: the path map could not be
derived from directory structure — src/Weir is flat and the areas
are semantic groupings — so it is a hand-maintained membership list,
the thing this project has learned to distrust. Priced accordingly:
ONE copy (the check script), PROCESS documents the shape and points
at it, and the check's own failure mode when the map drifts is loud
(a new src file falls to `cli`, a new root file to `docs` — visible
in the first commit that touches them, not silent).

The self-repair property fell out of the derivation: since CI knows
the right answer, the rejection prints it — `subject should start
'checker,parser: '` — and the fix is a copy-paste. A remembered
convention nags; a derived one autocompletes.

## wire keys: the attribute that reads the world's JSON (2026-08-17)

Phase 0 settled the mechanism question fast: roughly half the
reserved words are realistic wire keys (type, to, from, in, when,
until near-certain), so this was never a special-case list. The
baseline was worse than expected — the documented Map workaround has
a hole (`from json Map<string, string>` fails to PARSE in the
adapter slot while `Map<string, R>` parses; logged for the
broken-spellings sweep, which keeps accumulating receipts).

One attribute, named Wire rather than the rider's placeholder Json,
because yaml shares it today and csv inherits it later — the CSV
plan's [<Column "Total Amount">] is this attribute. The interesting
implementation seam was the WRITE side: attributes are erased from
values by law, so `to json` cannot look at the record it holds — the
rename table rides the TETo node instead, computed at check. That is
the attribute law's own shape (consumers bind at consumption) and it
kept the erasure ruling intact rather than bending it.

Two things the pins caught before CI could: the wire-collision
refusal crashed on a zero span when the PLAIN field was the second
collider (translate cannot take line 0 — report at whichever side
carries the attribute), and the missing-field message needed to cite
the wire key only when it differs. The keyword teaching finally has
a repair to name, which was the rider's quiet best point: the error
was always good; it just had nowhere to send you.

Parser.fs changed message-text only (no grammar movement), so the
deep-seed obligation was judged not triggered; the pinned smoke ran.

## the echo learns to stream, and buys an instrument (2026-08-17)

The invisible-prompt diagnosis survived contact; the architecture
claim did not. The seq<string> abstraction cannot carry a partial
line — ReadLine upstream holds it — so no amount of consumer-side
cleverness streams a prompt; the fix had to be a chunk relay under
the line splitter (streamSegmentsOf, ReadLine's exact split with
immediacy). Three consumers, three different hooks: the script
runner's CCmd prints via its own printResult (not a desugar — the
first attempted TEApp/TEPipe arm was dead code for scripts and only
serves effect sigils), the REPL's statement echo needed the
threshold machinery, and redirected output stays batched untouched.

The blessed threshold needed a second dimension discovered by the
first probe: 4 KiB alone holds a 20-byte prompt forever — the
guarantee window is size AND time (100ms flush timer), and the
SECURITY non-claim states both. The raise ruling implemented as
blessed: unflushed suppression matches the batched path exactly, so
the divergence surface is only what was already shown.

The pty instrument (tests/pty/pty-run.py) is the session's durable
purchase: scripted input, millisecond-stamped output chunks, one
command under a real pty. Built general per the ruling — C14 and the
REPL SIGINT split are now testable when someone picks them up. Two
python-heredoc escape scars in one session (\n became a newline in a
char literal; \000 typed as a literal NUL tripped the tool's own
control-character guard) — the raw-bytes lesson from the completer
session, relearned same-day.

## the completer meets argv (2026-08-17)

The union-of-every-field repro decomposed exactly as the rider
predicted: no command-position gate anywhere in the completer, and a
declared-fields fallback that fired on any unresolved head. Path
completion already existed (filesystemComplete) — it was just never
wired for argv words, so the fix was a gate, not a feature. The
membership sweep answer: every other thing the completer offers is
expression furniture, and argv gated none of it; one early branch now
covers the class rather than the instance.

The binding-vs-resolution question came out on the binding side: a
lambda or let param that types to nothing keeps the high-signal
union (that was always the fallback's purpose), an unbound name gets
nothing. The subtlety was WHERE the binder evidence lives — the LSP
completes one line, but `let quality t =` sits two lines up, so
suggest split into suggestScoped(binderScope, text); the REPL's
logical line is both texts at once. The e2e LSP probe (bicep lint
(t. — a param, a command head, AND an unclosed paren in one line)
forced both refinements the unit pins missed: let-params as binders,
and unclosed parens as expression interior.

One tooling scar for the log: a python-heredoc \b became a literal
BACKSPACE in an F# regex — the word boundary silently never matched,
and only cat -A showed it. Escape-sensitive strings do not survive
python string literals; write them raw or check the bytes.

## FileRow: one row, and the table learns two tricks (2026-08-17)

The ruling did the shaping: no -la dichotomy, so the session was two
field changes and a rendering layer, not a flag. `type` collided with
the keyword exactly as the plan suspected (record field declarations
refuse keywords), so `kind` — and then the interesting collision: the
natural case names File and Dir would have broken File.*/Dir.*
resolution entirely (a constructor in scope turns `Dir.create` into
field access on a union — probed, pinned in the row). Regular |
Directory | Symlink dodges the module namespace and Regular is
POSIX's own word.

The display work generalized past FileRow twice. The relative
timestamp is the Duration split again — lossless show, abbreviated
cell — and it lands in tableCell, so every Instant column everywhere
now reads "a week ago" at echo. The all-None column hiding is a
table-renderer rule, not a FileRow special case: any record seq with
an Option column that says nothing on every shown row saves the
width; one Some restores it. target is just the first customer.

File.mode landed with the stat referee agreeing on the first run;
owner/group deferred for want of a receipt (uid/gid portable but
unreadable, names need passwd machinery .NET does not expose). The
migration was 31 pins, nearly all of them using readOnly as an
arbitrary filterable bool — hidden serves identically, which says
readOnly was never load-bearing even in the tests. The doc-block
symlink demonstration had to soften to a where+iter shape (ln -s
copies on Windows Git Bash; the fence machinery has no
platform-scoped variant — noted if a posix-fence receipt ever
arrives); the Some-assertion lives in the POSIX e2e row instead.

## the owed small fixes, batched — and a masked flake, recorded (2026-08-16)

Five one-liners from four reviews' findings, each its own commit —
A6 is the batch's sixth item but outgrew the class, so it gets its own
paragraph below: WEIR_LOG
went case-insensitive (the env-enum rule applied to weir's own channel);
the ladder prose was off by one in FOUR places, not the three the finding
named (SEMANTICS too — "nested twice runs serially", since depth 2 hits
the 1); watch= now says "fails at the next interval tick" (C11 measured
721ms of lag under an 8s poll — prompt detection noted as the unbuilt
alternative); the SIGKILL mirror non-claim is stated (within proc does
not run your child's SIGTERM cleanup — wait=137 is the receipt); the
concurrency-model sentence names the machine in one clause (threads
in-process at the surface's edge).

A6 earned its probe-first instruction. Under a real pty the REPL's seq
echo REFUSED the binary — the #44 fix held — and the leak was one line
up: the `| complete` RECORD echo rendered the same bytes raw, because
echoBinary probed top-level shapes and never looked inside containers.
The membership question also found the -e echo carrying no guard at
all. Both fixed (the probe recurses; -e wears the REPL's refusal);
print/stream and Proc.tail stay raw BY LAW (deliberate channels, the
byte-identity claim); redirected output everywhere stays the data
channel.

THE MASKED FLAKE, recorded so it is findable: in the window before
45970b1 (the ambient-env starter fix), a run failed and then "passed
with a different seed" — but under the env-overlay bug both runs were
the SAME 200 cases, so that was a timing-flaky failure waved through by
a change that changed nothing. Its identity is unknown and it is still
out there. If something intermittent surfaces in the concurrency
neighborhood later: this window is where it was first seen.

B3's root, established while cheap: the block-interior value-headed
pipe failure is ONE place — pipeOrHint is wired at the three spine
positions (stmtWith, topLet, letIn) and absent from stmtElem — and the
let-RHS half no longer reproduces at all. Same CLASS as F3 (an
interior position missing a spine capability), different mechanism (no
gate to widen; a wiring absence plus one arming decision: what does a
value-headed pipe MEAN as a discarded interior statement). A short
session, not a line; not two places.

## parallel ladder: bounded at spawn, receipted before fixed (2026-08-16)

The plan's own discipline did the work. The prerequisite gap was real —
the review had measured that `pmapWith 1` serialises but never that 64
is the base, and the new base probe was the first thing to run (64 arms
one round at 416ms, 65 arms two rounds at 813ms — the cap is real and
exactly 64). The receipt then beat the reserve-math: 512 concurrent
LongRunning arms (the 64x8 shape) cost 444ms wall and 26.4MB peak RSS,
not 512MB — .NET commits thread stacks lazily, so the ladder's
constants stood as proposed without a fight.

Two details worth their sentences. The depth counter needed exactly
zero new plumbing in the workers: set the AsyncLocal before StartNew,
restore after the join, and ExecutionContext capture does the rest —
enterWorker/exitWorker never touch it, which also means pfirst's losers
that outlive the winner keep the deeper context they should have. And
the best verification came free from an existing instrument: the env-
overlay finding's exact-match property (kept items == worker count)
moved 64 -> 8 the moment the ladder landed, corroborating the inner
ceiling from a probe written before the feature existed.

pfirst kept pmap's numbers after the consideration the plan asked for:
a race does want width more than a map, but a NESTED race wide enough
to feel 8 has no receipt, and pfirstWith is the author's escape if one
appears — recorded in the row rather than uniformed for tidiness.
Nothing nested through a path enterWorker does not see; the one known
AsyncLocal asymmetry remains F1's (env cleared, never restored), which
is the rider's own item and out of this plan's scope.


## two within members, and the measurement that shaped both (2026-08-16)

Phase 0 before either part, as directed, and both answers moved the
design. `exit n` turned out to be an ExitRequest RAISE — the tmp dir
in the probe was removed by the scope's own finally, not the hook
sweep — so `always` runs on exit with no carve-out and the previously
ambiguous door is now a pinned e2e row instead of folklore. The BCL
answer was the load-bearing one: FileShare.None on Unix is flock(2),
per-open-file-description — two pmap arms exclude each other (the
fcntl per-process trap the plan warned about does NOT apply), and it
interoperates with flock(1) in both directions, which is what makes
the eventual deep-lock.sh migration honest. FileStream.Lock IS the
fcntl path; we never touch it.

The bare within + always segment cost almost nothing at the grammar —
the assembler's isWithinHead already accepted a bare `within`, and
`always` is until's machinery with no binder — but it surfaced a
pre-existing family bug: ElseHead segments joined with a SPACE, so a
COMMAND body statement ate the keyword into its argv. `retry` with a
command body and an `until` segment had been silently mis-parsing
since until landed (the error surfaced as "retry without an until
segment", one step removed from the truth). Both segments now join at
the sentinel; seqExpr's separator commit backs out exactly when the
next segment is until/always; pinned for both.

The raise rulings came out as blessed: original wins, marker to
stderr, LIFO unconditional — and the eval shape that honors them is
explicit Ok/Error on the body rather than F# try/finally, because a
finally cannot raise cleanly on the clean-exit case. Signals run
pending cleanups through the exit hook (registered thunks, removed
before running so SIGTERM-then-ProcessExit cannot run one twice).
exit-inside-always refused lexically at check; the unknown-scope-kind
teaching moved — a non-kind word now falls into the bare reading and
the missing-always error names both readings (the old pin's bogus
example was literally `within lock`, which is now real).

Lock probes that were one-liners: two processes serialize
(enter/exit/enter/exit with overlapping starts — the contention IS
the non-zero control), pmap arms serialize, flock -n fails while weir
holds and succeeds after kill -9. The no-lock e2e control interleaves
on the same instrument. Not done here, recorded: within proc + always
(ordering vs tree-kill unruled — nesting is the spelling until the
receipt), and the deep-lock.sh migration (publish.sh shares that
protocol; its own session).

## bytes: the capture law grows a second customer (2026-08-16)

Phase 0's trace turned up the sharpest fact of the session: the capture
BUFFER has been byte-faithful all along (segments of raw bytes, line
offsets, decode per pull) — only the VALUE surface is text. Everything
else crosses seq<string> somewhere: File.read decodes leniently and
line-splits, File.write re-encodes and appends LF per line, stdin
writes lines. So weir had NO byte-faithful passthrough, and the scoping
ruling's escape hatch ("unbounded data streams to a sink") did not
exist for binary — File.readBytes/writeBytes are that hatch for the
bounded case, and the unbounded command→file case is stated in the
ledger as still-external (curl -o) pending the deferred reifier/HTTP
work rather than papered over.

The laws all had precedents to copy and none needed invention: the
summary render is [D:binary-echo]'s rule made structural (a Bytes
value CANNOT put content on a terminal, rather than a probe catching
it); the fromUtf8 gate is fromBase64's gate verbatim, NUL included, so
[D:nul-boundary] stays closed — the argv route did not reopen because
the splice arm refuses Bytes at check. Secret-of-Bytes decided NO:
Secret's laws are string-shaped end to end, and a receiptless widening
that doubles a security type's surface is exactly what the park
discipline exists to refuse. One pleasant surprise: sha256/toBase64
gaining Bytes homes demoted no bare names — the allowlist counts homes
within Seq/Str only, so the inverted-blocklist design absorbed a new
module without a single collision. File.sha256 streams (HashData over
an open stream); the bounded value type never forced a bounded
implementation. External referees per [D:yaml-interop]: cmp on the
round trip, sha256sum on the digest, both agreeing.

## dx review fixes: the lists complete, and two instruments were wrong (2026-08-16)

The plan's premise that needed correcting: D1's rider assumed "the
bare-name diagnostic already names the repair" and gated the removal on
an LSP code action riding it. Measured, the diagnostic did not exist —
bare `where` in a strict file hit the pipe-glyph error or "install the
tool" — so the session shipped the diagnostic (Resolver.BareHome,
three teaching sites) AND the code action (the LSP's first: quickfix +
source.fixAll, edits derived from the published diagnostics). The
removal itself confirmed the vacuous-probe diagnosis end to end: zero
fixtures, one e2e test that converted before running, and a checker
that refused the mode the other components believed in. [D:strict-only]

-e's widening honored reading (b) exactly — multi-line input routes
through the FILE assembler, so blocks, comments and block-lets came
free, and the agreement pin (-e output == file output on identical
bytes) is the property, not either alone. The four kind teachings
survive on the last statement. [D:e-programs]

The message families: `=` was fixed by PARSING it (an operator the
checker always rejects) — failing the parse was what buried the repair
under the expecting-list; `||` needed a stage-position guard because
the first `|` reads as the pipe; the unclosed-string teaching needed
the DepthExceeded exception channel (the fatal-in-attempt property's
fourth sighting, looked up this time, not rediscovered). while/return/
try/def are reserved with per-word teachings and excluded from
completion. List/Array teach Seq; did-you-mean pools are kind-filtered
with the Exit->exit case-insensitive carve-out. [D:dx-message-families]

D7 became a SEMANTIC fix, not a message: point-free print now defers
its sentinel to the statement boundary like splice holes — the obvious
spelling works on the obvious type, printArgTy stays the one law, and
`let f = print` keeps the string default. D6 rode row-provenance's
pattern: hole-default anchors on the scheme, named at the call-site
mismatch with the exact hole position. [D:print-use-site]
[D:hole-default-provenance]

Two instruments were themselves wrong, recorded per the honesty rule:
the D5 gate probe's FILENAME contained its own needle ("Post" in the
path echoed by every diagnostic), so it stayed red after the fix; and
the census called the fixed D7 spelling "silent" — its worst bucket —
because correct code has no bucket in a mistake census; the case left
the corpus with a stated reason, as loose-bare-where did. Census after:
teaches 17 / plain 5 / dump 4 / silent 0 (n=26, baseline 17, vocab
extended with the new messages' repair phrases).

## adversarial review fixes: the coverage question keeps paying (2026-08-16)

The session's biggest finding wasn't in the plan — the plan predicted
it. F1's fix shape argued for a mechanical coverage check over a third
patch, and the check's first run found FIVE more unguarded axes beyond
the review's eleven constructors: index interiors (`a[a[…]]` — the
suffix loop re-enters expr without touching atom), block-form chains
(`fun…->`/`let…in`/elif — their bodies nest through seqExpr, atom
never on the stack), sigil nests through command args, the
yaml-district block walker (`let rec`, not even an FParsec parser),
and three SPINES that parse shallow (field chains, bare cons, elif —
plus the parser's own construction recursion in `build`, which had to
go iterative before the post-parse gate could ever see the tree). The
formulation that made it mechanical: the parser's reference graph
minus deepen-guarded nodes must be ACYCLIC — a feedback-vertex
property, not a constructor list. The guard set was chosen off the
499-paren counting path, so the boundary pin never moved.
[D:depth-coverage]

F5's diagnosis had its own correction: bind-entry counting missed the
blowup entirely (the bomb burns in the resolution WALKS — occurs,
finalTy, the structural compares — not in bind entries), so the budget
ticks in `resolve`, the one step every walk pays. And the plan's
"n=4 checks in 0.23s" denominator had already rotted: at HEAD, n=4
runs >60s — the cliff moved between e961984 and now, unnoticed, which
rather proves the budget's point. [D:inference-budget]

F3's mechanism turned out to be the in-stop: `reifierEnd`'s `in`
lookahead is spine-gated, so off-spine the marker can never match and
the segment path resolves the keyword on PATH. The fix teaches (fifth
cell, `^name` the escape) — and promptly caught THIS SESSION's own
harness edits, which had put `sh -c … | complete` block-lets inside
`within` (the harness header literally warns about it). A guard that
bites its own author on landing day is a guard aimed right.
[D:reifier-fifth-cell]

F4 took both fixes per the review recommendation; the argv guard's
worth showed immediately — File.read is a live NUL route TODAY, not a
future-BYTES hypothetical, so the boundary refusal (argv AND env,
naming the truncation) is load-bearing, not belt-and-braces.
[D:nul-boundary]

F2's four instances plus the symmetric-json gap got the external
referee (~100 payloads, PyYAML + json, in e2e); the whitespace-only
block-line semantics were pinned against PyYAML byte-for-byte before
writing the read-side fix — beyond-indent whitespace is content,
at-or-below is an empty line. Both re-point rules from the acceptance
section were honored, and both probes now assert the
rejection/teaching rather than the old behavior. [D:yaml-interop]
## raw interpolation: a park's trigger fires (2026-08-16)

The third reservation-called-on-schedule this stretch, and the
cleanest: the no-interpolated-raw cell recorded its own reopening
condition in 2026-07-22 and the receipt arrived shaped exactly as
predicted. Two things came free that the plan budgeted as work — the
Regex rejection (the computed-pattern fatal predates the feature and
already refuses any non-literal kind) and the Secret refusal (the raw
form produces the same EInterp node, so one check serves both
syntaxes) — both instances of the same lesson: route new syntax
through existing nodes and the existing law follows. The scanner
needed literally nothing (a $ is a plain char; """ opens triple),
which is the one-scanner rule paying rent. Phase 3's diagnostic turned
out to be a five-kind family, fixed with one shared anchored fatal;
the pre-existing splat-head guard needed splitting so $@" teaches the
new spelling instead of accusing the user of splatting. Phase 2
(multi-line) deferred by ruling with its four dependencies priced and
the district machinery named as precedent — the deferral is ON the
divergence row, so the next person finds a price, not a mystery.

The fuzz gate then earned its keep twice. First, a real regression:
the anchored-fatal rewrite used `getPosition >>= fun opener -> …`,
and getPosition succeeds at EVERY atom position — the bind ran its
lambda and RECONSTRUCTED the whole combinator chain per attempt, for
all four rewritten literals, at every atom the choice walks past.
Invisible on real scripts; the 50k-operator depth-guard spine paid it
~200k times and blew its 5s hang bound (+60% parse time). The fix is
a `followedBy <opener>` gate in front of each bind — the lambda now
runs only when a string actually starts. The FParsec lesson: a bind
whose left side always succeeds turns parser CONSTRUCTION into a
per-call cost; gate it or hoist it.

Second, diagnosing WHY "three fresh seeds" all failed identically
exposed a defect in the harness's own instrument: a command piped
into a reifier (`cmd | orFail msg`) never sees the ambient
`within env` overlay. The reifier desugar builtins are registered
with a BAKED overlay (`orFailedWith []`, Builtins.fs) and spawn
through it — only Eval's overlayOf path reads Session.envOverlay().
The Env-sigil twins drop the ambient layers too, against the
documented outer-first law. Consequence: tools/fuzz.weir's
`dotnet test | orFail` has run at the harness DEFAULTS (seed 1789001,
count 200) since the tool adopted `within env` — every "deep run" was
a 200-case smoke wearing a 10k banner. Probe:
`within env` + `sh -c "echo $X" | orFail "…"` prints empty; the bare
and `$( )` forms print the overlay. NOT fixed here (raw-strings
session) — the fix wants its own pins (each reifier family member ×
ambient env) and an e2e row. This session's three seeds ran via the
env route directly.

## reifiers drop the ambient env — the receipt audit (2026-08-16)

The bug (found mid-raw-strings, fixed here): reifier desugar builtins
spawned through a baked empty overlay; `within env` reached bare
commands and $() but never `cmd | orFail` or its family. Fixed at the
LAW: ambient layers now apply inside Proc.start/startSpilled, under
the explicit env, so every spawn — reifiers, cmd, into, future
members — obeys outer-first [D:ambient-env-starter]. The membership
question ("what else is registered that way?") answered wider than
the report guessed: cmd and into spawned with NO overlay at all, and
the Env-sigil twins dropped the ambient underlay. feedWith turned out
to be dead code — defined, never registered.

THE RECEIPT AUDIT — the window is 793f945 (2026-08-04, the !-district
retirement moved fuzz.weir from `!fenv` to `within env`) to this fix.
Before it, `!fenv` rode the ECmd env slot into the |orFailedEnv twin:
those runs were genuine. Inside it, every tools/fuzz.weir run executed
seed=1789001 count=200 — the defaults — under whatever banner was
requested. Affected claims, each marked, none silently re-run:

- duration (03acd33, 08-04): "3 fresh 10k deep seeds (lexer changed)"
  — actually 3x the same pinned 200. Decision stands on unit/e2e/
  oracle pins; the depth claim was corroboration.
- floats session 2 (222a822, 08-04): "the fuzzer's expect-red
  prediction did not fire: 30k fresh-seed cases green" — actually
  ~200 effective. The WEAKEST survivor: the did-not-fire observation
  was nearly vacuous at that depth; the analytical explanation
  (exact-quarter literals render deterministically) still argues the
  prediction, but as theory, not measurement.
- dedent correct-join (d87c5f9, 08-04): "30k strict-span deep cases
  green" — ~200 effective; shapes covered directly by 1027 unit + e2e.
- trailing comments (08-04): "3 fresh 10k strict-span seeds" — same.
- function lambda (8656b01, 08-08): "three fresh 10k seeds came back
  clean" — the one that matters most: that session ADDED the function
  production to the generator, so the new grammar arm was exercised
  ~200 times, not 30k.
- the transcript sessions (08-10..08-15): Map, anonymous records,
  scoped-procs-era runs, and spot-checks all "3 fresh 10k" — all 200.
  One run in that stretch FAILED then "passed with a different seed" —
  under the bug both runs were the SAME 200 cases, so that pass
  masked a timing-flaky failure, not a seed difference.
- nightly fuzz-deep.yml: three invocations per night (pinned + two
  RUN_NUMBER-derived), all 200-case since 08-04.
- raw-strings (08-16): caught it; its three seeds re-ran genuinely via
  the env route, 3x10k clean.

No blessed DECISION rested solely on a deep-fuzz green — every row
above also carried unit/e2e/oracle evidence — but every "N fresh 10k"
sentence in that window overstated its evidence, and floats2's
prediction-did-not-fire is downgraded to analysis. RE-RUN (post-fix,
recorded here against the rows above): three fresh 10k-case seeds
through the FIXED driver — 660017, 812338, 447291 — ALL CLEAN, each
closing line self-reporting "count=10000 (observed)".
These exercise the CURRENT grammar union (function lambdas, dedent
joins, trailing comments, float production included); the historical
intermediate trees are unrecoverable and stay marked.

The class fix is [D:observed-report]: the harness writes what it
read, the driver verifies requested==observed and fails loud. The
sweep found no other restated-input banner in tools/ or ci/. Two
parser gaps logged while building the pins, not fixed here:
value-headed pipes (plain or reified) parse at top level but not as
block-interior statements or block-interior let-RHS ("Expecting: end
of input" / the '|' teaching); the e2e matrix pins the In twin
through a function called inside the scope instead — which also pins
the dynamic-scope semantics deliberately.

## check --can: the constraint pays out visibly (2026-08-15)

The feature was a walk, not an analysis — Phase 0 established that
everything the report needs was already computed and mostly already
RULED: the deliberate TSecret->Ok() arm at the CmdArg position, a
months-old recorded ruling, turned out to be exactly the
secret-reaches-argv analysis, which is the strongest argument yet for
writing rulings down. Two design moments to remember: the review's
exit-code pushback (the day-one CI gate greps for new capabilities
and would sail straight past `opaque` — so the incomplete header is
loud AND --strict exists; correctness of the JSON key alone was
insufficient), and the walker discovering module members are dotted
TEVars, not field nodes — the typed tree's own shape teaching the
walker its arms. My favorite probe failure of the stretch: the
Secret-argv pin first spelled `curl -H t` with a bareword, and the
report correctly showed no secret reaching argv — because t WAS the
literal string "t"; the argv law taught its own author. Standing
category, fourth instance now noted elsewhere: docs recommending
spellings that error (the `cmd "["` teaching) deserve a sweep of
their own, not incidental fixes.

## record order: the container was wrong (2026-08-15)

Phase 0 before code, and the measurement rewrote the task: what
looked like "add ordering to a Map" was "the Map never earned its
place" — 6-23x slower to build at the widths records actually have,
buying sublinear access nothing needs. The review's reframing of the
equality cost deserves its NOTES line verbatim: order-insensitive Eq
as a custom arm is the invariant moving somewhere better, one place
that cannot drift, versus a Map-plus-order-field carrying agreement
between two representations at every construction site. The Ord
check the review asked for came back closed three deep (NoComparison,
the class solver, scalarCompare's unreachable wildcard) — and found
the sortBy teaching had never learned Instant, the kind of drift a
one-line check catches in passing. The fixture estimate (30-40) came
in at ten because most JSON pins were single-field or coincidentally
alphabetical — and because doc blocks assert NOTHING about output,
which is its own finding now on the row: executability is pinned,
rendered claims are not, and the site-snippet question that raises is
deliberately left as separate work.

## ls-sort rider, and the merge that lost a ruling (2026-08-15)

The rider itself was small — one sort call, one sibling (Env.vars'
hashtable order), one doc line. The session's real event was the
archaeology tripping over a LOST RULING: PR #43 merged the instant
branch's pre-amend push, so the user-ruled age→modified swap
(amended locally as b163e6b, fully verified, reported as landed)
silently never reached main — main still had age, and three later
sessions rebased onto it without anyone noticing because the swap's
pins lived only in the unmerged amendment. Recovered by applying the
b163e6b diff verbatim as this branch's first commit. The process
lesson is sharp: an AMEND after the user may have already pushed the
branch invisibly forks local from remote — the local verify passes,
the merged tree differs, and nothing in the ritual compares the two.
A push-state check (local tip == origin tip before reporting
ready-to-push) would have caught it for the cost of one git command.
## nesting, reversed on a live receipt (2026-08-15)

The rider ruled the depth restriction intentional in the morning and
the user reversed it by evening with the receipt the original ruling
never weighed: REPL shape exploration, where declaring a type
mid-sketch breaks the loop the anonymous form exists to serve. The
implementation vindicated the synthetic-nominal design a second time
— because the canonical name IS the type, nesting cost one parser arm
and a pending-table drain at the two registration seams, with
unification, display, dotted error paths, and the JSON reader all
recursing for free. The pending table's design note is the part worth
remembering: parser backtracking means shapes can be pushed for
parses that never survive, and the fix is to not care — registering
an unreachable hidden type is benign, so the drain needs no
parse-success bookkeeping at all. Both halves of the day are honest
on the ledger: the investigation that proved the rule was intentional
AND the receipt that retired it, in one row, same branch.

## the nesting rider: intent, verified from the ledger (2026-08-15)

The rider's third hypothesis — a narrowing nobody asked for, hardened
into apparent semantics — is the failure mode an agent-authored
codebase should fear most, and the investigation's answer is the
ledger working: the rule was recorded at introduction, in the row, in
exactly the words needed four days later ("tySyn does NOT nest the
form"). What LOOKED like depth-dependent inconsistency was two
grammars each behaving as separately ruled — the slot's alternatives
grew (map-string, rowed) while the one type grammar stayed closed
(anon-records, rowed). The real defect was the seam BETWEEN two
correct decisions: a parser-level expecting-list where users first
meet the boundary, and two SKILL sentences written by different
sessions that never met. Both fixed as one motion. The discipline
that made the answer cheap is the discipline the rider asked for:
evidence before code — the archaeology took four greps because the
rows are dense enough to hold the mechanism, not just the verdict.

## Instant: absolute time, the boring subset (2026-08-14)

The session where the review's scoping did the design work in
advance: instants-only/UTC/no-calendar was settled before the first
edit, so the implementation was mostly arms — the binop table, the
class lists, the two boundary tables — each following Duration's
groove exactly. The named-format reader was the real work as
predicted, and the acceptance immediately taught it a lesson: the
plan's strptime subset met openssl's actual enddate output and needed
%b (month names) and %e (space-padded days) before the cert-expiry
fixture would parse — the acceptance-first discipline catching a
subset drawn from theory rather than from the receipts. Two smaller
notes: the try-twin's error split is worth remembering (a mismatch is
None but an unknown DIRECTIVE raises even in tryParseWith — a format
bug is not a data miss), and `24h * 300` is the spelling for long
spans because Duration deliberately has no day unit — the cert
fixture tripped on `300d` first, which is Duration's own law working.
## the table dresses, the data does not (2026-08-14)

A polish rider whose design cost was one decision made twice: keep
the machinery plain and put the presentation at the edge. The tint is
applied by the PRINTER positionally (line 0, line 1, the trailing
clip row) so echoTable's output stays byte-plain and every existing
pin holds untouched; the clamp is a parameter the tty caller fills
and every other caller declines, so the piped surface and the test
battery keep byte-parity by construction. The one taste call worth
recording: cells are never tinted — the header/rule/footer are
weir's furniture, but a cell is the user's data, and data does not
get decorated. Declines on the row: borders/zebra (copy-paste), column
reordering (would fork show's field law), interactivity (the
dynamism boundary).
The session's coda: FileRow's `age` became `modified: Instant` the
same day — the ls-truth row had reserved that reopening explicitly
("the DateTime gap stays its own question"), and the type arriving
flipped the balance against a field that was derived, snapshotted,
and stale the moment a listing was bound. A reservation written into
a row four days earlier getting called on schedule is the ledger
working exactly as designed.

## the surface gate: doc completeness as a checked property (2026-08-14)

The user's framing was the whole design: #help already enumerates the
real surface machine-readably, so diffing it against SKILL.md is the
same shape of job as running the fenced blocks — and worth more than
any single feature, because the fallback protocol ("not in the skill
file, assume it does not exist") silently depends on the doc being
complete. The first audit proved the point: 35 members invisible,
including all of Str's core splitters and four Http verbs — a fifth
of the surface the protocol was silently wrong about. The interesting
call was the fix: an inventory section is a hand list of a derived
set, exactly the drift this project keeps retiring — but a hand list
WITH a gate is the same move as the bare-partition's pinned collision
set: the derivation checks the copy, so the copy is safe to hold.
Negative controls earned their keep immediately — the first missing-
member probe chose pfirstWith, which the gate "passed" because prose
already spelled it qualified; a control that cannot fail is the
vacuous-probe class, and the second pick (tryIndexOf) proved the gate
both ways. The rider was a user find: '#' is not a word char to the
editor, so #he completed to head from the general pool — the fix
returns BARE directive names at a line-head '#' so replacement
composes with the glyph already typed.

## scoped processes: within proc, and the no-orphan law (2026-08-13)

The session where the plan's design questions kept resolving
structurally. The sugar probe (can watch=srv reach an Option field?)
dissolved by moving watch OUT of the options record entirely — a head
key peeled before the desugar, which is also the honest semantics: a
live handle is not configuration. The real fight was the ASSEMBLER:
the dangle rule space-joins a block's first statement, and a proc
head's tail is a command whose argv happily ate it — the fix is the
sibling-sentinel's own founding argument applied one position
earlier, and it took three tries to find all the join sites (the
lambda-continuation pair only surfaced under a pmap probe) and to
make the detector string-blind and word-bounded instead of
segment-anchored. Two adjacent findings were the session's bonus
material: def-less builtin type names were silently declarable (the
tests' own `type Proc` fixture proved the collision by breaking), and
the #help gap the user reported turned out to be exactly one missing
builtinDocs entry per form head — within's now derives its kind list
from the table, so the next kind cannot miss the doc. The .NET traps
were the usual suspects: Process.StartTime throws on an exited child
(the watch error now uses the poll's own clock), and a spilling child
holds its spill dir open, so the exit hook kills before it deletes.

## the echo cap: 100 by default, #echo to change it (2026-08-12)

The rider on echo-lines that closes the arc's last papercut: the
number 10 was tuned for literals and inherited by command output,
where it made Seq.force a display verb. The session's real content is
what it REFUSED: auto-forcing looked like the fix until the lazy
inventory (nats, glob, pmap) made it a hang, an OOM, or the echo
spawning processes on its own initiative — the double-run bug the
echo-once work just killed, reintroduced as a feature. The cap+1 pull
discipline survives unchanged; only the number moved, and the number
became state. Two things worth remembering: the footer sentence had
to become a function of the live cap in the same commit (a pinned
"first 10" beside a #echo 25 is the lying-message class — the plan
called it the likeliest forgotten pin and it was the first test I
wrote), and the piped surface deliberately did NOT move (echoPipedCap
keeps the historical 10 — piped REPL bytes are load-bearing across
the e2e battery, and a session directive should not be able to move a
pinned byte surface). The persistence question resolved the fuller
way: echoElems, cut once as vacuous wiring, revived for one record
field once the wiring existed — the cut was about the wiring, not the
key. One plan premise corrected: the #help directive list is a
literal, not derived; only the module list derives.

## the bare partition widened: unambiguous means bare (2026-08-12)

The session that un-froze the previous session's answer. The bare-rule
sitting was right to replace the frozen twenty with an explicit
partition — but its declined half inherited the very shape it fixed:
nobody had CHOSEN iter-qualified-map-bare, it was just where the
accident's dust settled. With the accident gone, the derivation the
data once refuted became correct: single-home means bare, and the
whole curation collapsed to the allowlist plus a two-name collision
set (contains, length). Two things earned their keep beyond the plan:
the PINNED collision set — under pure derivation a NEW collision
silently demotes a bare name, a breaking change nobody decided, so
the pin turns it into a loud decision — and the constrained-scheme
finding: the Ord/Eq schemes lived only on the module map, so a
newly-bare sortBy would have typechecked LAXER than Seq.sortBy;
the overrides now apply at both surfaces from one list, and the gate
pins bare-refusal == qualified-refusal. Four pins inverted or moved
to strict mode (choose/append/iter/groupBy asserted non-resolution
that the rule now grants); their replacements pin the promotion
instead. The overloading decline is recorded with all four reasons
precisely so `contains` inferring its home stops being re-proposed.

## the bare-member rule, discovered then stated (2026-08-12)

The frozen-list session: twenty bare names nobody could explain, and
the discovery found the explanation was "someone chose them once and
the choosing was never written down" — plus one accident hiding
inside the choice. `contains` lives in both Seq and Str, and
Map.ofList's last-write-wins had silently made bare contains Str's
since the list froze: the Seq hot-path spelling errored with
"expected string" and its only users were string-flavored tests that
never noticed. Candidate rule C (bare iff unambiguous) was refuted by
the data in both directions, which settled the choice on curation
with a gate: the partition test makes an undecided member a failing
test, and single-home-ness makes the contains accident unrepeatable.
collect joined on its receipt; contains left with its homes named;
and the "moved into a module" message — which asserted a history most
of its subjects never had — was retired rather than split, because no
message that claims nothing needs its claims reconstructed.
## Map<string, T> — the ID-keyed object (2026-08-12)

The design's best moment was structural: FromMap WRAPS the existing
shapes instead of standing beside them, so the checker peels the wrap
and resolves the inner name through the path FromName and FromAnon
already share — and the plan's two-features-in-one-slot bar
(`from json Map<string, {| id: string |}>`) passed on the first run
with zero rules written for it. Composition you don't have to
implement is the argument for the wrapper shape. Two smaller
payoffs of existing closed sets: Env.load/Args.load refused a Map
field before I wrote anything (the loadable allowlists), and #help
Map + completion appeared free once the module joined the enumerable
table. The probe-found gap worth remembering: validateTy's
string-keys-only teaching guards DECLARATIONS, so an anonymous
`{| m: Map<int, string> |}` slid past it to jsonAdmitted's generic
enumeration — boundary teachings need to live at the CATEGORY level
(jsonAdmitted), not just the declaration gate, or the anon path
regresses to worst-message. Eq stayed excluded by construction and
the premise re-check the plan demanded held: the message names the
type. Indexing stays parked — m[k] desugars at parse time where no
type is known, so Map.get is the spelling; the ledger row states it
so the next session doesn't re-derive the refusal.

## the echo presents string seqs as lines (2026-08-12)

The smallest session of the arc and the one a newcomer meets first:
^ls -la echoed as a quoted, semicolon-joined literal of the very lines
it had just split apart. The fix was echoTable's discipline applied to
the type the table rule skipped — type-keyed (seq<string> exactly,
never sniffed), tty-gated, one enumeration on the papercuts session's
cache, footer keeping the honest unforced sentence. The instructive
part was the pins: two existing presentation pins asserted the OLD
literal (repl-color's leading-space triple among them), and my own new
pin first matched the keystroke echo instead of the result — pinning a
presentation means pinning the discriminating spelling, and the
typed-line echo contains the input text by construction.
## predicates over fields — explored, declined (2026-08-12)

The exploration session that ended in a ruled no, exactly as its plan
allowed. The probes sharpened everything: every candidate operator is
provably a type error with a function operand today (the safety table
is real, not asserted), the two-placeholder case has a principled
pointwise answer, and `not` turned out to need nothing — `where
_.isDirectory` and `_.field >> not` both work today and the composed
form would have participated in lifted conjunctions. What the corpus
said mattered most: four predicate lambdas in the entire teaching
corpus, zero multi-predicate pipelines — the pain is prompt-side, and
the prompt already has two working spellings. The decline stands on
Scala's extent footgun, the row-DSL's five-incident shadowing shape,
and the lambda being documentation. The report stays on the shelf
with a stated re-opening trigger, so the next time someone types
`_.age > 24h` the answer is a ledger row, not a re-exploration.
## operators as values, unapplied only (2026-08-12)

The session where the hard question evaporated on contact: `+` is
overloaded five ways and the plan rightly feared its type as a value —
but the desugar-to-the-lambda design means there IS no type as a
value, only the lambda's, and context resolves it exactly as infix
does. The one real piece of work was the check-mode twin (synthesis
alone left `Seq.reduce (+)` unable to see the element type the way a
spelled lambda could), and the contract that fell out is the session's
best pin: byte-parity INCLUDING failure — the form is never better or
worse than what it abbreviates, same successes, same messages. The
class system answered its open question for free: `let eq = (==)`
generalizes with Eq riding the scheme, machinery that existed since
containsScheme and had just never been entered from a parenthesized
operator. The refusals carry their reasons — short-circuit for &&,
grammar for pipes, and the partial-application message shows both
lambda directions because the direction is the documented mistake.
## session directives, and two papercuts that shared the surface (2026-08-11)

The '#' worry dissolved into the feature's best sentence: a hash line
is addressed to the tooling, not the language — check-time file
directives and run-now session directives are one family, and the
statement retroactively explains #sig and #schema. The discovery the
plan insisted on came back almost empty: :q was the only non-#
spelling in the entire surface, one literal match arm, and the feared
migration was 25 mechanical fixture sites. FSI parity brought one
genuine better: #help Seq works because weir's module tables are
enumerable where FSI resolves value paths.

The papercut probe found something the plan only suspected: the
trailing-comment session's REPL half never covered the SINGLE-LINE
path — `let x = 5 // note` was a parse error at the prompt since
birth, hidden because the multiline arm rides the assembler and most
sittings type short lines. One stripComment at the right seam closed
it, and comment-only lines became silent no-ops in the same arm. The
typed value slot is the with-slot session's machinery reused one slot
deeper: the field's declared type was always known and simply unused
— unions complete their cases now, which is the case a user cannot
guess without reading source.
## the matrix campaign, rounds 5-21 (2026-08-10..11)

Eighteen more rounds of platform triage, and the ledger's real
content is the CLASS CATALOG. The separator class alone showed five
faces: MSYS /tmp, drive colons in PATH entries (round 5 fixed one
site; round 6's sweep found mkweirtmp's own mixed-form output
poisoning all 22 prefix sites — one pathEntry helper closed it),
drive case on the LSP wire, 8.3 short names (TEMP is RUNNER~1;
.NET answers long-form), and path-separator greps in pins (the
leaf-pair-with-a-dot spelling is now the law). The shell class:
`sh, never bash` — System32's WSL stub shadows Git Bash for any
NATIVE process resolving `bash`, and it bit weir's spawns and
python's subprocess separately. The stdin class: /dev/stdin rides
MSYS's virtual /proc into a native binary that has no /proc.

Two product findings earned their keep. Captured output is LF on
every platform now ([D:lf-output]) — found by the battery's ONE
byte-equality pin, then extended twice as the law met its surfaces
(File.write; a child's stdin, where the miner-hash pin's failure
hash was literally the CRLF twin of the expected one). And the
PATHEXT spawn arm: the checker resolved bare `x` to x.bat for
EXISTENCE while the spawn handed CreateProcess the bare name — the
gap windows-viability sized and nobody built, found because a
fixture finally put a .bat-backed command in weir's path.

The harness lessons compound: fixed time budgets lose to loaded
runners (three pins fell to it; marker-driven and quiescence-driven
drains replace guessed settles), a drain budget that counts READS
exhausts under a steady stream with input still queued, and
System32's sort.exe IsTextUnicode-misdetects short even-length
LF-only stdin as UTF-16 — the Bush-hid-the-facts bug, reachable
from a test fixture in 2026. Round 12's raw-tail diagnostic paid
for itself by naming round 21's cause verbatim.

## show names its niche (2026-08-11)

The rider that dissolved a naming debate: interpolation is the
rendering path, and `show` turned out to be a thin convenience
behind the load-bearing Show class — barely reachable, so barely
worth renaming. The sweep migrated ten-ish sites and then the
corpus-as-build-gate did its exact job: migrating showcase-lib's
`$"{show c.port}"` BROKE the check, because a bare hole defaults an
unresolved row field to string while `show` keeps it polymorphic.
The niche came out three places wide (point-free, Secrets, row-typed
holes) instead of one, and the PROCESS rule landed: a change that
makes a spelling redundant sweeps for that spelling — in the
superseding session, prose included, reporting what it deliberately
keeps.

## the Seq gaps, diffed and closed (2026-08-11)

Twenty-five members in one pass, but the session's real design
content was three rulings. The sum ruling: no numeric class means no
generic sum, so each type owns its own, module-qualified — and
`Seq.average` alone crosses types because the mean of ints IS a
float. The synonym ruling: first/take stay, documented as one
implementation with two readings. The one-name law, enforced both
directions: filter teaches where, flatMap teaches collect. The
reservation audit found the collect teaching had promised a member
that did not exist — and zero other reservations do. nats answered
its own mystery: BCL initInfinite inside a builtin, no recursion
involved, so init/unfold stay out on no-receipt grounds rather than
the false impossibility the plan assumed.

## the field law goes recursive (2026-08-10)

The session that unblocked real JSON, and both of the plan's risk
assumptions inverted under investigation: the boundaries never
shared a field law (Args/Env were structurally safe from the
widening), and from yaml was ALREADY the recursive tree boundary —
json was the one behind. The cycle guard names its path; the mutual
cycle turned out unrepresentable (no forward type refs). Null means
None at every depth; a missing array is an error because absence is
Option's job. The World Bank fixture is the plan's captured receipt
— the egress proxy blocked a fresh pull, stated rather than papered.

## named groups reject (2026-08-10)

The audit's in-by-accident feature, ruled out on a stronger fact
than the plan opened with: every other engine numbers groups
left-to-right; .NET numbers positionals first — so weir's binder
order would betray exactly the users named groups serve. The
implementation collapsed to one site (compileRegex) and the sharp
edge dissolved: detection reads the ENGINE'S own group table, and
lookbehinds create no groups, so (?<= cannot false-positive by
construction. The declined renumbering option is on the record so
it stops being re-proposed.

## anonymous record types (2026-08-10)

`from json {| ip: string |}` — the twice-receipted ergonomic, and
the design fork priced itself: synthetic-nominal, where the
canonical name IS the rendered shape. One trick bought three
behaviors — same-shape anons unify (deterministic name), declared
records stay distinct (the nominal law untouched), and formatTy/
errors/echo render the shape with zero new arms. Everything
downstream (validation, field access, tables, hover) rode existing
paths because the hidden name is just a name. The session's cost
came in under the plan's fear precisely because the mechanism was
one mangling function.

## the papercuts sitting (2026-08-10)

Six planned items off a live REPL sitting, and the headline was the
seventh nobody planned: the waiting indicator's own pty pin WEDGED,
and the trail led through .NET's cooked-terminal restore to the real
root — the tty echo path enumerated a lazy seq TWICE (echoTable's
probe, then echoValue's render), so a bare command's child ran twice
per echo, and the second run's cooked window ate the user's next
Enter as a phantom continuation. Three fixes: one-enumeration echo
(Seq.cache), a termios snapshot + watchdog, and the pins that hold
both. The planned items landed too — transport errors in weir's own
words with the classifier keyed on exception TYPES, the wider
raw-leak sweep that corrected a prior "zero remain" claim (9 more
sites), the label-leak class closed with a marker, the record-update
span blaming the non-record, the with-slot completion, and the
fetch/get ruling that made the misreading self-correcting instead
of renaming anything.

## the matrix's first-green triage (2026-08-10)

Round one guessed and was wrong; round two instrumented, and round
three read the instrument. The Linux failure's text — plain
diagnostic in the stdout file, empty stderr, TERM unset — convicted
the PIN, not the product: weir check has reported on stdout since
birth, and the walk-findings-era pin asserting colored STDERR had
passed locally by matching .NET's terminfo init noise on the pty.
A pin that asserts an accident is the vacuous-control class with a
green checkmark on it; the citation on the colored-diagnostics row
is corrected, and instrument-before-guessing goes in the round's
ledger as the transferable lesson.

Windows was a class, and closing it as a class bit back once: the
mkweirtmp sweep (Git Bash mktemp yields virtual /tmp paths no
native exe can resolve; cygpath -m at one choke point, 84 sites)
also rewrote the helper's own body into self-recursion — a fork
bomb that left 1571 stray bashes in the container and, at
exhaustion, the empty-variable /x.weir symptom that briefly looked
like a filesystem problem. The sweep-hits-its-own-definition
failure mode joins the batched-edit lessons.

macOS remains the one lane without a named culprit: the hang is
real, Expecto's silence hid it, and the detailed logger plus step
timeout turn the next hang into a name. The /etc/hostname fixture
was the cheap half — a Linux-ism, not the GNU-ism the sweep
expected.

And the warnings question closed as a posture: library-level trim
warnings we cannot fix, mitigated by the battery-against-artifact
discipline that already exists, with the one real find being
FSharp.Core floating between CI and local — now pinned.

## example maintenance (2026-08-10)

The sweep's one real finding: not every sigil was redundant. Bicep's
!e(...) wrappers looked like pre-within-env leftovers, and two of
them were — but dropping the effect sigils naively tripped the
seq-unit checker, because a final command in a block is the block's
VALUE. The sigil had been doing unit-coercion work. The idiomatic
replacement turned out better than the original on both axes:
`| orFail "stack deploy failed"` is unit-typed AND names the
failure, where !() raised anonymously.

The rest was the expected modernization: Option.iter for the
match-Some-effect-None-unit shape (probe, twice), tryHead with a
default for first-argv-or-dot (repo-report), orElse chains for the
two-Option fallback (showcase's temp root), and one genuinely
redundant capture dropped. The showcase keeps its sigil pair on
purpose — it is the teaching site, and within env demos two lines
below it — and the for-source and pmap captures stay because $()
is required in expression positions, not style.

git-subrepo leaves as the flagship it was: 555 lines that proved
the translation story when the story needed proving, now
maintenance surface without a reader. Its fold receipt outlives it
as its own fixture, and the grammar-drift canary role passed to the
showcase's verbatim and triple-quote lines.

## the Windows column's first run (2026-08-10)

Twelve reds and none of them the product: every failure was a
fixture Linux-ism in pins written after the hand-run, and two of
the twelve were features firing correctly — the backslash-escape
teaching catching a raw C:\ interpolation, and the fs guards
naming Windows paths in weir's own words. The stated skip list
rendered exactly as designed at 43.

The four classes and their lesson: temp paths spliced into weir
source now route through weirPath (the rule stated at the helper,
because five of the twelve were the same mistake written twice);
expected messages pin the shape and the name, never the platform's
path rendering — the same pin-the-class lesson the ls-truth wire
pin taught, now applied across the fs family; existence-dependent
fixtures stopped assuming /tmp and /etc; and the three if-succeeds
pins that drive POSIX tools joined the stated skips visibly rather
than failing mysteriously.

## builtin record keys: one source, checked (2026-08-10)

The probe made the case better than the plan did: a missing key did
not error at access, or silently nothing — it hit an unreachable
that BLAMED THE CHECKER for a field the checker had approved. A
lying diagnostic is the worst loudness class, so the guard moved to
the construction site.

recordOf takes values in declaration order and takes the keys from
the declaration itself, which is stronger than sharing constants:
omission is an arity error at the build site and a wrong key cannot
be spelled at all. The census found nine hand-keyed sites across
four locally-declared records — one EnvVar site even spelled the
record name as a string literal rather than the def's — and all
nine converted with the full suite green before any new pin landed,
which is the zero-value-change bar demonstrated rather than
claimed.

The prelude-declared four (HttpRequest, HttpResponse, Retry, Poll)
live as weir source, out of the helper's reach, so their hand-built
defaults are agreement-pinned against the parsed defs — and
HttpResponse is excused by the e2e battery that reads every field
off a live response. The consumer sweep came back derive-everywhere
except two stated items: the table's column order reads the value's
sorted map (alphabetical — show's law) rather than declaration
order, accepted because every renderer already agreed on
alphabetical; and the FileRow doc string is the one copy that
cannot derive without losing its teaching, so it carries a
drift-pin instead: every declared field name must appear in it.

## the echo rule (2026-08-10)

The audit found exactly the two defects the bless filed and one
non-defect worth recording: a function echoes <fun> with its type
rather than refusing — the echo is presentation and show still
refuses, so the class rule holds where it matters and the prompt
stays useful.

The forcing probe was the session's hinge, and it answered cleanly:
both renderers pull MaxItems+1 and nothing more — nats echoes
instantly as a bracketed seq and falls through the table detector
untouched. That meant uncapping unconditionally would turn every
big lazy seq into a hang, and it meant the candidate rule was
implementable, because forced-ness was already detectable: the
materialized-collection probe that used to print "10 of 12" is the
forced test.

So Seq.force means something at the prompt now. Forced echoes
whole — at the top level, with each element keeping the inner
clips, because [nats] |> Seq.force is a forcible value holding an
infinite inner and the echo must not be the thing that hangs on
it (pinned). Unforced shows ten and says the honest sentence. The
old hint — a pipe that produced a different rendering than the one
it promised to continue — is deleted outright, and the pty pin
asserts its absence by fragment so it cannot resurface.

No ceiling on the forced side, ruled: the user forced the value,
print already streams everything, and the scrollback belongs to
whoever asked for a million rows.

## ls tells the whole truth (2026-08-10)

The question arrived as "why only three columns" and the probe made
it worse before it made it better: ls did not just lack an
isDirectory column — it omitted directories entirely, GetFiles()
doing exactly what its name says while nothing stated the
restriction. A month of scripts saw half of every directory.

The seven-field ruling's interesting corner is age. The obvious
answer was "weir needs a date type"; the actual use cases said
otherwise — changed-in-the-last-hour and newest-first are both
DURATION questions, so age: Duration since last write covers them
with a type weir already has, snapshotted once per enumeration
pass like Env.load. The DateTime gap remains real and remains its
own question; this row deliberately does not answer it.

The wire pin taught a small lesson about pinning on classes: with
age alphabetically first, the to-json refusal now names Duration
where it named Size — the pin that asserted "Size" broke, and the
fix was to assert the CLASS (is-not-representable) rather than
whichever field happens to trip first. A fragment pin on an
incidental detail is a pin on field ordering.

And the method note the bless added: this was the format-surface
audit's enumerate/classify/state pass pointed at a builtin record
for the first time, and it held without modification. The
surface-filerow row records the generalization so the third
receipt-built surface — which will be neither a format nor a
record — inherits the pass by rule rather than by rediscovery.

## the builtin-surface audit (2026-08-10)

The method generalized, and mostly it found statements to write
rather than defects to fix — zero code changed, which is itself the
result: the earlier sessions (fs-members, sized-findings, the format
audit, encoding-law) had already swept the class's worst corners.

Per-target denominators: glob 13 cells (11 unexamined -> 0), capture
4 (2 -> 0), regex 9 (6 -> 0), Path edges 6 (4 -> 0), env fields 2
(1 -> 0), Dir.list-vs-ls stated, shape grep 203/36/26 with no eighth
suspect.

The verbatim probes worth keeping. Glob: bare `*` lists files AND
directories (a.txt, b.txt, c.md, linkdir, lit*eral, sub); `{a,b}`
and backslash-escape match nothing silently ([*] matches the literal
star file); `.*` yields dotfiles; `linkdir/*.txt` FOLLOWS the
symlink while `**` refuses to descend it. Capture: a 0xFF byte
becomes EF BF BD through both $() and complete — lenient, like
File.read, unlike fromBase64, and that three-way split is the
encoding law's actual shape. Regex: `(?<x>a)(b)` against binders
(p, q) gives p="b", q="a" — .NET renumbers positional groups first,
named after, and Str.rmatch returns the same order; nothing stated
it until now, and whether named groups should instead reject is the
audit's one real sized decision. The env teaching enumerates its
whole admitted set in the message — the best surface statement in
the codebase, written before anyone asked for one.

Two premise corrections against the plan as blessed: glob returning
directories disconfirmed the same-defect-twice suspicion (the
parent's README-sweep counter-evidence held at source), and the
parked Path.combine doc already existed in SKILL, thorough — the
park had been discharged and nobody had marked it so.

Sized (2): the named-group ruling (reject with a teaching, or
document the order — behaviour either way); glob brace expansion
(waits for a receipt). The ls half of the inversion is [D:ls-truth],
in flight beside this audit.

## launch hygiene: the files a visitor expects, and the shape (2026-08-09)

The licence decision turned out to be already made — Apache-2.0 and
a NOTICE sat at the root — so item 1's real work was the dependency
verification the plan insisted on: FParsec's nuget package carries
no LICENSE file at all (a licenseUrl pointing at a 403ing page),
and the truth came from the project's own README — Simplified BSD
for the code plus the Unicode data licence for the derived tables.
Both compatible; the binary statically links FParsec, so the
2-clause notice-reproduction condition is met by
THIRD-PARTY-NOTICES.txt rather than by hoping.

CONTRIBUTING leads with the unusual part because it is the part a
drive-by contributor cannot guess: the agent authorship, the
blessed plans, and the evidence discipline that makes a PR without
a row or a pin half-landed. The README's Provenance section says
the same thing in one paragraph — the disclosure decision was
plain-over-buried, on the reasoning the plan gave: hiding it would
be wrong and discovering it late would sour.

The move put nine process artefacts under dev/ and the three
reader-facing ledgers under docs/, and the citation sweep patched
twelve consumer files — CLAUDE.md's three paths mattering most,
since the agent instructions must be literally true. The canary
the plan named (the doc-test battery) held at 96 blocks, and the
full battery held behind it. The root now reads: README, LICENSE,
NOTICE, THIRD-PARTY-NOTICES, SECURITY, CONTRIBUTING,
CODE_OF_CONDUCT, and the build scripts — a language project.

The site is deliberately not here: it is gated on the first tag,
because its most valuable line is the install command and there is
nothing to install yet. When v0.1.0 exists, the site sitting picks
a markdown-in generator, wires Pages + the weir.sh CNAME, and
renders the repo's own docs — the source of truth stays here.

## the tree-sitter split (2026-08-09)

The manifest earned its keep before the split even finished: the
first run of the grammar repo's gate found rec and mutable missing
from the keyword rule — reserved words since the let-sugar session,
never colored, invisible to the old three-way gate because that gate
only compared the sets the grammars DID carry. Fixed in the grammar,
regenerated, and the corpus counter picked up the other old ghost on
the way: tree-sitter's parse summary repeats the first ERROR node,
the exact double-count the grammar README once recorded and
corrected in prose — now corrected in the counter.

The protection splits cleanly but asymmetrically, and the row says
who catches what. The main repo cannot see a grammar repo whose CI
is red or simply never runs — the release gate should probably
assert the grammar's latest CI before shipping, and that question is
deferred by name rather than silently dropped.

The consumer sweep vindicated grep-over-memory again: the migration
session's URL sweep had missed Zed's extension.toml entirely (both
repository fields and the authors name), and vscode's package.json
somehow still carried gitlab despite being on that sweep's list —
squash-merge archaeology not worth doing; the grep is the truth.
Zed's rev now points at the grammar repo's local root commit and
must be refreshed after the user pushes — stated in the toml itself.

The one-motion ritual (bump the rev, refresh the highlights copy)
stands where the retired drift guard stood, in both the toml and the
Zed README. A ritual is weaker than a gate; when the Zed extension
gets its own home the gate can rejoin CI, and that is written next
to the ritual so nobody mistakes the current state for the end
state.

## the REPL tabulates record seqs (2026-08-09)

The user's question was whether the pretty table needed a login
shell; it needed forty lines in Eval and a gate. echoTable is a pure
function — same-named records, scalar cells, alphabetical columns
(show's own law), numeric right-alignment, the echo's existing
10-row limit with the in-table ellipsis and counts hint — and the
two REPL echo sites consult it only when stdout is a tty, so the
piped REPL's pinned byte surface never moves (both halves hold in
the pty harness). show is untouched; every renderer that matters to
scripts is untouched; this is the REPL presenting, not the language
changing.

Cells reuse show's spellings except strings, which drop their
quotes — a display, not a literal — and Secret's rendering marker
holds in cells (pinned against the reveal). Widths are char counts,
the same assumption the wrap math already makes; CJK cells will
misalign and the row says so rather than hiding it.

With FileRow.bytes as a Size underneath, the README's first
exchange became the table this arc kept circling: aligned columns,
4 MiB in the bytes column because the unit lives in the type, and
the type footer naming the row. Regenerated from a live run, as
always.

## FileRow.bytes is a Size (2026-08-09)

The change was one line and the truth of it was forty-five: the type
flip in the row definition plus the conversion where ls builds rows,
and then every fixture that had quietly depended on bytes being an
int. The fixture migration told the feature's own story — byte
literals became Size literals (1048576 turned into 1MiB, which is
the point), and the json-cluster tests that had borrowed FileRow as
a convenient three-field record now declare their own JRow, which
is exactly what a user serializing ls output must now do.

That wire boundary is the one real behaviour change beyond the
type: `ls |> to json` used to work and now rejects, because Size is
non-representable in JSON by the unit-type ruling. It is pinned as
a named rejection rather than discovered by a user, and the
stand-in spelling rides in the suite. The sibling audit came back
zero — FileRow was the only builtin record carrying a byte count as
an int — so the Size migration is complete rather than continued.

The README got the payoff for free: the REPL echo now reads
`bytes = 4 MiB` where it read `bytes = 4200000`, which answers the
table-envy question better than a table would — the unit lives in
the type, so every renderer everywhere shows it.

## releases: the binary someone can actually have (2026-08-09)

The pitch said one binary and the repo could not hand you one. Now a
v* tag produces a draft release with four platform binaries and a
SHA256SUMS, behind the only gate that matters: the full battery,
with check-fresh running strict — WEIR_REQUIRE_CLEAN=1's designed
release arm gets its first real customer — and the conflict-marker
gate riding e2e as everywhere. Draft-then-publish so a missing
platform is caught by eyes before it is caught by a user.

The stamp extension is one opt-in fsproj arm: WeirRelease=<tag>
stamps `<tag>+<sha>`, verified locally, and every other build path
stamps exactly as before so the freshness gates' stamp==HEAD
contract never notices. The release job asserts --version starts
with the tag and refuses the artifact otherwise.

linux-arm64 made it in scope because public repos get the arm
runners free and cross-OS AOT is unsupported — a native runner was
the only route anyway. Intel macOS is the one stated gap. The
signing posture is unsigned-with-explanations: the INSTALL page
walks the quarantine xattr and the SmartScreen more-info path,
because a scary dialog with no explanation reads as malware and
loses exactly the reader the release exists to win.

One install path got built (the curl|sh installer, which verifies
checksums before installing and says so), two got parked with real
triggers (brew on a macOS adoption receipt, winget/scoop on a
Windows one), and one got declined with its reason on the record:
dotnet tool install requires the SDK the target audience lacks —
distributing the no-runtime shell through a runtime's package
manager would argue against the product on its own install page.

Windows joined both halves the same day: win-arm64 rides the free
windows-11-arm runner (the same native-runner-only argument as
linux), and install.ps1 is the curl|sh installer's twin — same
policy (detect, download, VERIFY, install, PATH note), Windows
spellings (Get-FileHash, %LOCALAPPDATA%), written 5.1-compatible
and verified in-container the same day (the user dropped a pwsh
7.6.4 into the workspace): an offline rig stubbed the two network
calls and ran the real script end to end — arch detect, tag
resolve, download, the cross-tool checksum handshake (a
sha256sum-written SHA256SUMS read by the Get-FileHash side), the
install, and the PATH note — then the negative control: a corrupted
checksum refuses with both hashes named, exits 1, installs nothing.
Only the live-release download path remains for the first tag.

Awaiting the push and the first tag: the draft release flow end to
end, the two arm64 runner labels' first contact with reality, and
install.ps1's first live run.

## the CI matrix at the new home (2026-08-09)

The move itself was already done — the user migrated to
github.com/weir-shell/weir on 2026-08-08 and seven squash PRs have
landed since — so this session was the plan's actual deliverable:
the matrix. Billing got verified against the live docs rather than
trusted: public repositories run free on all three OSes, so the
macOS and Windows arms cost nothing and the public-vs-private
coupling the plan worried about is moot (had it been private, macOS
meters at roughly ten times linux).

The workflows encode the expectations, not just the steps. The
macOS e2e step is labeled as what it is — the GNU-ism sweep finally
running with a real signal, where first-run reds are findings — and
the Windows unit step states that its Skipped count IS the stated
skip list, so a new skip reads as a finding rather than noise.
Windows runs the same bash gates under Git Bash: one freshness-gate
implementation, the .ps1 twin refused as the same-policy-twice
shape. Whether Git Bash actually carries the whole e2e battery is
exactly what the first push will answer; the hand-run's evidence
says the surface is viable, and HTTP is named in the step title as
the one surface never run there.

The URL sweep came out smaller than the plan guessed: four live
surfaces (the SECURITY channel — now GitHub private vulnerability
reporting plus the new noreply — the two Helix source pointers, and
the VS Code repository field). Everything else that greps for
gitlab is deliberate: the raw-URL teaching and its fixtures are
product behavior about external hosts, and the plans/NOTES mentions
are archives. The old .gitlab-ci.yml is deleted; ci/README says
what replaced it and why the kaniko image's job dissolved into
setup-dotnet.

Awaiting the push, stated rather than pretended: the macOS triage,
the Windows first run, the tree-sitter split (org repo creation is
user-side, and it remains the Zed prerequisite), and the
Codeberg/GitLab archive disposition.

## from yaml seq — and the stream retirement that came first (2026-08-09)

The subtraction was the session's real content. `from yaml T` used
to map every document in a `---` stream onto the same T, which is a
homogeneity assumption the format itself argues against — multi-
document YAML exists to carry a Deployment AND a Service AND a
ConfigMap, and weir deliberately refused the kind-discrimination
that typing such a stream would need. So stream support claimed
what the type system cannot back, the same class as NDJSON living
under the plain `from json` name. With streams gone the two plural
readings collapse into one and `seq<T>` is unambiguous: bare T is a
mapping document, seq<T> is a sequence document, a stream teaches
with its count and the split-and-parse route.

The feature itself cost almost nothing, as the plan predicted: the
slot, the seqOf payload, and the checker unwrap were all built by
the from-json-seq session, and yamlShape already knew how to build
SSeq — the yaml arm now just shapes the DECLARED type instead of
always the record. TEFromYaml carries the same payload as before,
so the LSP needed zero movement (the inner-name hover pin
confirms). The parser was not touched at all; no fresh seeds owed.

The probe settled the bare-`from yaml` question honestly: the
optional type argument is the error path (a located "needs a record
name" teaching), not a vestigial porcelain leftover — and no
Yaml-node read direction exists. The heterogeneous bundle therefore
still has no spelling; the typeless `from yaml` producing Yaml
nodes is parked with a real trigger, and the audit's YAML row now
answers "can I read arbitrary YAML?" with a plain no and the
reasons.

Every migrated site is named in the row. The showcase's PeerDoc
example came out better than it went in: it used to demonstrate the
stream reading that no real bundle could use, and now it
demonstrates the sequence form that real documents actually have.

## the testing residue cleared (2026-08-09)

Two headlines. Every invariant's detector is now evidenced: the
span-soundness checker came out of its two inline homes into
spanSoundCore with a three-way verdict, both properties call the
core, and two positive controls in the smoke fail by name — a
wrong-site claim trips "lost its report", a clean-program claim
trips "no diagnostic". That closes the vanished-control class the
walk opened: four invariants, four controlled, all on extracted
cores.

And the ratio reads 175 / 0 / 0 / 7. The last partial — the
dup-type REPL replace-note — got its pty pin in the
repl-multiline shape (declare a type, bind a value through it,
redeclare, assert the note) with the citation added in the same
motion. Zero partials is now a statement, not an aspiration.

The plan's checklist was written before the walk-findings merge
landed, so four of its five items already existed with citations —
verified by content rather than re-written. The one real gap was
textDocument/formatting: wired since the lsp-requests session,
never once exercised over the protocol. It answers correctly both
ways (one whole-document edit carrying fmt's output; no edits for
a canonical doc), and no behavior defect surfaced anywhere — the
plan budgeted for at least one across five never-tested surfaces,
and the surfaces held.

## the format-surface audit (2026-08-09)

Denominators, per format: JSON 14 cells — 9 in, 3 out-with-teaching,
2 accident-now-stated, 0 unexamined left (was ~7 before). YAML 8
checked — statement matches code, 1 sized (top-level sequence
documents). Schema: the catch-all makes the denominator moot — no
unlisted keyword can pass silently, probed live twice. dotenv 12 —
9 in, 3 out-with-teaching, 0 unexamined (was 12: the whole surface
lived in one code comment). Platform + scrapers: written down where
the work happened; pointer rows say where.

The nine adversarial inputs, all recorded verbatim in the session:
JSON — pretty object across CRLF lines reads (b=2); a top-level
array reads via seq<Name>; null-field + big-integer: null handling
pinned since json-option, 99999999999999999999 into float gives
1e+20 (stated), into int gave the session's one real find. dotenv —
quotes/escapes file reads with escapes LITERAL; export file teaches
the sh escape; CRLF file reads clean. YAML — the GH-Actions shape
splits: nested maps read, a top-level sequence teaches 'expected a
mapping, got a sequence' (sized); docker-compose quoted scalars
read; anchors and merge keys reject with the repeat-or-build
teaching.

The one mechanical fix: an integer-shaped overflow into an int field
was called "a decimal number" — TryGetInt64 fails for both shapes
and the message guessed. The raw token tells them apart; the
overflow now says "number out of int range — declare it float",
both arms pinned. Also pinned while here: the 64-deep parser cap
rejects through the teaching, never leaking MaxDepth.

Sized, not built: from yaml over a top-level sequence document (the
from-json-seq question again, one adapter over); a dotenv unescaping
mode for backslash sequences (dialects disagree; weir currently
keeps them literal, which is at least honest); a strict
duplicate-key mode for from json (last-wins is System.Text.Json's
choice, now stated).

The standing rule landed as its own row: a new adapter's row names
the shape its receipt came from and what that leaves unexamined —
the sentence that would have put the array gap in the ledger instead
of in a user's error.

## from json seq<Name> — arrays declare themselves (2026-08-09)

The probe's pricing held exactly. The parser cost was the two-token
case it predicted — "seq" "<" UpperName ">" in fromExpr's existing
optional slot, tySyn untouched and unreachable — and the payload
change (seqOf: bool on EFrom/TEFrom) was found by the compiler at
every consumer, all of them one-underscore edits except the two that
were the feature: the checker's unwrap and eval's dispatcher.

The dispatcher is where the no-sniffing property lives now, stated
as code: jsonDoc takes wantSeq and demands the top level the
DECLARED type named — an array under seq<T>, an object under bare T
— so the same input can never mean two things and the input is never
consulted to decide. The five refusals all speak: the inner gate
fails with a designed expectation ("a record name inside seq< >" —
the leaked 'type name' label from the probe's ride-along is retired
and pinned absent), yaml and jsonl gate the wrap as a category
error, an object under seq names both shapes and the way back, and
the array-under-bare error finally points at a spelling that exists.
The "no typed spelling" caveat lived one day in three doc sites and
is gone from all of them.

Two decisions worth their sentences: completion after `from json `
offers record names only — no synthetic "seq<" item — because the
teaching and the hover carry the form and the one-source rule stays
trivially true. And Args.load/Env.load needed nothing because the
spelling cannot reach their bespoke EVar arms — refusal by
construction, recorded so nobody adds a guard that has nothing to
guard.

## from json reads a document; from jsonl reads lines (2026-08-08)

The rename fixed a backwards default. `from json` was an NDJSON
adapter wearing the plain name, which made the HTTP docs' headline —
"body pipes straight into from json T" — true only for servers that
minify. Now the plain name carries the common case: `from json T`
joins its elements internally and parses one document, so
`resp.body |> from json Peer` is the entire spelling against a
pretty-printed body. The join idiom this repo taught YESTERDAY
(wrap, join, head — three taxes) lived one day and is retired; the
GUIDE block that demonstrated it now demonstrates the two-adapter
distinction instead. `from jsonl` took the old behavior under the
name the format actually goes by, and since it is the shape
`to json` writes, the roundtrip reads `to json |> from jsonl`.

The array question got its honest answer: `type Peers = seq<Peer>`
is not expressible (probed — weir has no type aliases), so a
top-level array document has no typed spelling, and the located
error says exactly that — an array where an object was expected,
lines-users pointed at jsonl, nothing promised. The blocker is
reported here rather than papered over with sniffing: neither
adapter inspects its input to decide what it means.

The raw-leak sweep closed with a count. Six sibling boundaries
probed across the two sessions — from yaml, to json/to yaml, the
schema validator, Env.fromFile, Env.load, Args.load — every one
already wraps its parser's errors in weir's words. from json was
the only leaker, the third historical instance of the class
(floats' FormatException refusal, File's read side, this); zero
remain. The class rule stands in [D:json-boundary]: a boundary
loader that hands a value to a .NET parser owes its own error
wrapper.

Every migrated call site came out shorter or unchanged — none
longer. The document-form sites dropped their `|> Seq.head`; the
stream sites renamed to jsonl and kept their shape. The GUIDE's
list-endpoint example became a single-item endpoint, because a list
endpoint genuinely cannot be read yet — the docs now say the true
thing instead of the aspirational thing.

## the json boundary: a class closed, an idiom written down (2026-08-08)

The probe session's verdict was docs-not-member-not-type, and both
halves landed. The join idiom is now taught where from json and Http
chaining are taught: `[body |> Str.join "\n"] |> from json T`, with
the wrapping brackets called out as the tell — you are building a
one-element seq because from json takes a stream. The GUIDE example
runs in CI (the 95th block), so the claim has an executable twin.

The real find was the third instance of a shape now worth stating as
a rule: any boundary loader that hands a value to a .NET parser
needs its own error wrapper. The floats session refused raw parses;
the filesystem session wrapped File's read side; and from json
turned out to leak System.Text.Json's own sentence for every
non-object top level — the exact shape a GitLab-style list endpoint
produces. The guard now names the kind in weir's words, all five of
them, and repeats the one-object-per-element contract. The array
case deliberately gives no advice: no spelling exists for a
top-level array today, and the message must not promise one — the
document mode stays a member-sized park with its own bless.

The sweep is what made it a class closed rather than a defect fixed:
the schema validator already conforms (weir prefix, .NET detail as
tail — the ioGuarded shape), and from yaml is hand-rolled and speaks
its own words at every probe (tags, overflow, flow syntax). from
json was the last leaker at this boundary.

## function — the implicit-match lambda (2026-08-08)

The reservation paid out smaller than the plan sized, twice over.

The blessed plan's one design question — how fmt preserves the
spelling, flag versus node — dissolved on contact: weir's fmt is
token-level and has no lambda or match printer, so `function`
survives formatting by construction (verified idempotent across all
four layouts, plus a round-trip pin). And the flag itself shrank to
nothing: the desugar binds the internal name `|function`, which no
consumer can ever see — completion and did-you-mean pools filter on
isUserName (first char letter/underscore), the LSP filters |-vars,
and the name cannot be typed. Zero new AST nodes, zero new arms in
Check/Eval/Lsp/fuzz printers. Every diagnostic is match's own,
pinned byte-identical against the spelled-out form.

The real work turned out to be one assembler rule: arms are
PipeHead lines, and after another pipe line they must sibling-align
— so `|> Seq.choose (function` followed by deeper arms died with
"indented off its siblings". The fix is the dedent-join session's
own vocabulary: line-end `function` joins dangleEnders
(word-bounded, the `do` precedent), and a dangling pipe line now
opens a deeper arm group. Unit suite unmoved; the fuzz alternation
(a third of flat matches render as `(function | arms) scrut`,
deterministic on Bid parity) holds every invariant across the
spelling, and three fresh 10k seeds came back clean.

One survey miss worth recording: the walk found three reservation
sites; a fourth (the let-binder position) surfaced only when its pin
failed — reservation retirements should grep the MESSAGE, not trust
the site list. Two old pins asserted the retired teaching and were
retargeted to the generic keyword refusal.

Adoption followed the bless: SKILL, GUIDE, and repo-report speak
`function` now; the showcase adopts it in the git-status section but
its `hits` block deliberately keeps `fun l -> match l with` — the
tour teaches both spellings side by side. The README stays explicit
for the same reason stated at bless time: a reader who has never
seen weir needs the lambda and the match visible to parse what is
happening.

## the porcelain adapter retired (2026-08-08)

Pure subtraction, the drop-command-builtins shape. `from porcelain`
was the adapter family's odd member on every axis: hardwired to the
builtin Change record where json/yaml are generic over a shape the
user declares; one external tool's one subcommand parsed inside the
binary, in a language whose command knowledge otherwise lives in
signature FILES; read-only where the others round-trip; and brittle
against git's own surface — `--porcelain=v2`, a `--branch` header
line, or `-z` termination all killed the pipeline at runtime. It read
like the proof-of-concept the real adapters grew out of, because it
was.

Nothing replaced it because the replacements pre-existed: `from json
T` / `from yaml T` where the output is structured, and the
arity-typed Regex pattern with Seq.choose where it is line-shaped —
that spelling now stands in every place the adapter used to
(README's snippet, the showcase's typed-output section, repo-report
declaring its OWN Change record, SKILL/GUIDE/COMING-FROM blocks, all
checked by CI). The from/to inventories came out symmetric —
{json, yaml} in both directions — which deleted the read-only
asymmetry caveat from adapterNames and two pins that existed only to
assert it.

The one deliberate survivor: `git status --porcelain` as an ARGV
WORD, in fixtures, the git signature, and the docs' replacement
spellings — that is git's flag, not our adapter, and parsing its
output with a Regex you can see is exactly the point.
## hovering a schema= name (2026-08-07)

The residue session's diagnosis held exactly: `schema=k8s-configmap`
names a vendored FILE, not an env.Types entry, so the ty-chain arm had
nothing to render and the district's `Yaml` type leaked. The fix is a
node-gated arm (TEYaml with a declared name, cursor within the
`schema=<name>` token) answering FILE facts: the resolved vendored
path, the lock's source URL, and — the load-bearing line — whether the
schema carries an `additionalProperties: false` anywhere, because a
permissive schema's validation LOOKS like it works while unknown-field
checking never fires.

No new source of truth, mechanically enforced: the resolution was
EXTRACTED from the checker (Script.resolveSchemaFile — the same walk,
the same restore-vs-add teaching on a miss, so hover and check cannot
diverge), and the strictness rule was extracted from `weir add`'s
warning closure (Contracts.anyClosedProps — one rule, two renderers).
A miss hovers the checker's exact message (locked-but-missing teaches
`weir restore`, never-added teaches `weir add … --as`), an
out-of-subset or corrupt file hovers parseSchema's own error — every
state answers with words some other surface already emits. Definition
on the name opens the vendored file (the sig-file precedent: path,
1,1,0), including a corrupt one — the file existing is exactly when
opening it helps. Not-vendored stays quiet; the hover carries the
teaching.

The lockfile question resolved cheap: read per hover, no cache — the
completion arm already lists `.weir/schemas/*.json` per keystroke, so
a per-request lock read is the same class of work, and the stateless
discipline keeps holding. One divergence found and NOT fixed, rowed
here: the schema-name COMPLETION arm anchors its upward walk at "."
(the LSP process's cwd — right in the REPL, approximate in an editor
launched elsewhere), while hover/definition/check anchor at the
document's directory. Unifying means threading the document path
through Complete.suggest's signature (2 real + 6 test call sites) —
do it when suggest next changes shape.

## the walk's two findings, closed (2026-08-07)

The vanished control came back bigger than it left. The fuzz smoke
now holds five positive controls — a deliberately output-changing
transform, an rc-changing one, a throwing work item, a hanging one,
and a shape-distinguishing program pair — each asserting that its
invariant FAILS BY NAME ("transform changed behavior", "THREW",
"exceeded"). The mechanism matters: the detectors were extracted into
cores (compareRuns, totalityCore) that both the invariants and the
controls call, so a control cannot pass against a copy while the real
detector rots. The survey corrected the plan's premise: there are
four invariants, not six; 1, 2, and 4 are controlled, and invariant
3's control is sized (its span-soundness checker lives inline in two
properties — extract first, then feed it a known-bad span).

The cohort went from sixteen partials to one. Fifteen rows flipped to
pinned with the citation added in the same motion — the finding was
that citing rows stay pinned, so a pin without a citation reproduces
the disease. env-sugar-layers recategorized as pinned-via-supersession
rather than partial: its live half (sigils) was always pinned and its
dead half (the line-end district) has a pinned retirement — "partial"
was miscounting a supersession as a gap. The one survivor is
dup-type-decl: the declare-after-import message is now unit-pinned,
but the REPL replace-note needs a pty pin, sized and not written
(drive the REPL under a pty, redeclare a type, assert the
"redeclared; earlier values keep the old shape" note — one more
repl-multiline.py-shaped case).

New ratio over the walked 182: 174 pinned / 1 partial / 0 unpinned /
7 unpinnable — from 159/16/0/7.

The cohort theory held with one correction worth keeping. The era
clustering was real (rows 2-46 carried seven of the sixteen) and the
mid-era gaps were exactly the predicted class — the rejecting half of
a symmetric rule, one cheap pin each (unparenthesized params, the
destructuring pipe-hint site, the bracket range endpoint). But the
early cohort's gaps were mostly harness-shaped, not message-shaped:
consumer parity needed an LSP probe, the recheck license a latency
bound, colored diagnostics a stream-splitting pty, the recovery cap a
12-line fixture. The early rows predate not just the citing habit but
the harness inventory itself — so their misses cost a probe each
where the mid-era ones cost a grep each. The next audit should expect
that split: old rows are expensive per pin, not just more often
partial.

## the full DECISIONS-to-pins walk (2026-08-07)

All 182 rows categorized: 159 pinned, 16 partial, 0 unpinned, 7
unpinnable — 91% of the pinnable rows fully pinned and not one row
with nothing asserting it. The number the sample promised, now with
the whole denominator behind it.

The strategy's hit-rate, since the next audit needs it: the grep pass
came first and it paid. 249 message strings extracted from source, 26
genuinely source-only after hand-verification — and writing those
pins up front (including `command failed with exit code {n}`, the
set-e analogue whose raise was pinned but whose words never were)
meant the four row-walkers found almost no message-shaped misses
left. What they found instead was exactly what the plan predicted the
grep cannot see: absences (renamed members gone without hints, the
dropped builtins' pure subtraction, parser reject sides), exact codes
(exit 4 → rc 4; fail → rc exactly 1), and two rows independently
converging on the same hole — File.readSecret had zero test coverage
anywhere. The grep-then-walk recipe stands: the grep finds the
message-shaped majority cheaply, the walk covers the rest.

The walk's second finding is about the ledger itself: the 16 partials
cluster by era. Rows 2-46 carry seven of them; rows 91-134 carry
none. The pin-site-citing habit that began around 07-26 is measurably
load-bearing — where the story column cites its pins, the pins are
real (the sample said this; the full walk confirms it), and where the
row predates the habit, the partial rate quadruples.

About thirty cheap pins were written in-session; the sized-not-written
list, for future sittings: the one-pipeline three-consumer parity
harness (one two-error script through check --json, the runner, and
LSP didOpen, asserting the identical code/line/col set), the LSP
formatting-request probe, the LSP recheck-latency bound, TERM=dumb and
per-stream diagnostic color under a pty, the dup-type import-half and
REPL-note pins, the assembly-recovery ten-line cap — and one that is
more than a missing pin: the fuzz harness's "equality detector graded
positive control" has no surviving artifact in the tree. The claim's
evidence vanished; restoring it (a deliberately output-changing
transform that must FAIL invariant 1) is the highest-value item on
the sized list.

Zero defects: no row's message names a renamed member, and every
superseded row traces to a later rowed decision whose retirement is
itself pinned. The seven unpinnable rows are the architecture and
process rulings a test cannot see, counted separately so the ratio
stays honest.

## the sized findings, landed (2026-08-07)

Five items from the sweep's sizing, ordered by user visibility, and
the visible one first: File's read side now fails in weir's words.
read/readSecret/write/append leaked raw .NET exception text — "Could
not find a part of the path" where the delete side said "no such
file: {path}". The fix is two pre-check guards reusing the delete
side's exact shapes plus a residual wrapper (permission denied named;
exotic IO prefixed with op and path), 4 members fixed of 13
filesystem-touching examined — the other 9 were already weir-shaped.
Encoding turned out not to be a failure mode at all (ReadAllLines
substitutes replacement chars), so not-found / is-a-directory /
permission is the whole enumerable surface. Found in passing:
File.read's doc claimed "lazily" while ReadAllLines is eager — trued.

Dir.copy landed with the family's overwrite rule and one sentence
that prevents a future naming debate: copying a directory MEANS
copying its contents — there is no non-recursive reading — so the
delete/deleteAll split does not repeat and nobody should propose
Dir.copyAll.

The lookup-spelling diagnosis went exactly as the plan predicted:
one row, zero renames. Env.get's bare noun is defensible because no
asserting twin exists to distinguish it from (and the row states
that if one ever lands, the Option one takes try- at that moment);
rmatch keeps its domain name because regex-returns-Option is the
universal expectation. The rule is now stated as a prediction
procedure in [D:lookup-spellings].

Http.withQuery was confirmed operand-first in the source and flipped
while Http is young enough for the flip to be free: params first,
url last, `url |> Http.withQuery [(k, v)]` — three unit pins and four
doc sites moved, named in the row.

The two mechanical guards: the conflict-marker gate runs at the top
of e2e (any diff3 marker at line start in a tracked file fails — one
grep, and the |||||||-leftover class from the rebase resolutions
cannot recur), and the redirection idiom got its fenced twin in both
SKILL and GUIDE after failing twice as unchecked prose — the blocks
run in CI now (94 total), and the prose points at them. The general
prescription is recorded: a spelling that fails twice in prose gets
a fenced twin.

The two durable records from the sweep are now findable rows rather
than paragraphs in a session report: [D:fail-unit] (the decline with
its mechanism — the unsolved-var surface and the dead-value
tripwires) and the 178-row walk's search strategy inside
[D:sized-findings] (grep source-only teaching messages first).

## the maintenance sweep, second edition (2026-08-07)

Findings-first with denominators, run as five read-only agents with
every fix applied serially in one checkout — the shared-tree collision
class from the fork era made that split the safe one. The
denominators: 62 comments, 34 quoted messages, 8 retired-name
patterns, ~180 dedupe cells, 121 members across 11 families, 30 of
178 DECISIONS rows, 20 pins from a 285-pool, ~411 prose spellings
checked of ~474 found, ~15k prose lines de-slopped.

The headline is Movement 4's ratio: 93% of sampled DECISIONS rows are
pinned (28/2/0), and both partials were the same class — the
REJECTING half of a symmetric rule, where the accepted direction got
its pin and the teaching message lived only in the parser. If the
full 178-row walk ever runs (sized: ~6x this, ~10-15 expected misses),
grep for source-only teaching messages first. The reverse cross-check
found exactly one dangling D-key citation in eight test surfaces,
which says the citation discipline held.

The find nobody planned: three leftover diff3 `|||||||` marker lines
in NOTES and DECISIONS, from the rebase resolutions — the resolve
regex captured up to `=======`, which includes the base-section label.
Content was verified un-duplicated before removing the markers (each
section and row exists exactly once); the damage was three stray
lines, not lost or doubled records. The rebase-resolution scripts
should match the diff3 shape explicitly next time.

The quality verdicts, briefly. The quoted-message walk (the item the
movement was amended for): 30 of 34 verbatim, and the ledger's
pin-site citations checked out real in every sampled case — the
ledgers are trustworthy where they cite. The 20-pin sample: 19 sound,
zero spelling-pins — message-substring pins are MEANING pins in this
tree because the wording is the deliverable; the one weak pin
(asserting a routing predicate while its title claimed the assembler
teaches) now observes the message. The de-slop pass: almost no slop —
zero throat-clearing, zero tic vocabulary, zero intent-narrating
comments in ~3400 comment lines; the one habit was the ledgers
grading their own work ("sharpest" ×9), and ten delete-only cuts
covered everything that could be cut without rewording a finding.

Prose spellings confirmed the coming-from page's own warning about
unexecuted surfaces: all six failures were in prose, four of them
docs lagging a retirement or a later guard (the skill file still
taught the whole line-end-`!` block; SEMANTICS' generic-declaration
example was `type Option<'a>`, which the builtin-redeclare guard now
rejects), and two the recurring `cmd | function` pipe class — the
SKILL File.write bullet was the second verbatim recurrence of a
failure found once before, which argues the class needs its fenced
twin some day.

Sized and not fixed, for future rulings: File's read side raises raw
.NET exceptions where its delete side raises weir-shaped messages
(the strongest intra-family split the shape audit found);
Option-returning lookups are spelled three ways (tryX, bare Env.get,
domain-named rmatch); Http.withQuery is operand-first; Dir.copy is
missing where File has both. And the `fail` ruling stands as found:
unit-typed on purpose per the existing Builtins comment — going
polymorphic is one line of type but opens the unsolved-var surface
(`let x = fail "m"` generalizing to `forall a. a` against the class
solver, untested) and deletes the dead-value tripwires; `fail "x" ; 0`
stays the taught idiom.

## the Windows hand-run's findings: banked, and four fixes (2026-08-06)

The confirmations first, banked so nothing re-tests them: the 2D
editor's whole surface held on metal (multiline buffer, blank-line
escape, forced newlines, Ctrl+R's fzf-absent fallback, leading-space
heads), RESIZE re-wraps correctly — SIGWINCH is guarded off on
Windows, so this was a suspected gap and is not one: the redraw
recomputes from the current width. All three within kinds ran,
including the four-piece composition never before exercised together
(within env → a cmd /c child → %GREETING% expands). retry's
delay-before-retry-not-first arithmetic was visible in the timings (3
attempts over ~305ms). Secret masked a containing record. And the
whole contracts chain ran offline — the .weir walk, schema load,
district validation, and the #sig git signature with no network. THE
STANDING WINDOWS HAND-RUN ITEM the contracts and signatures sessions
each left open is DISCHARGED. The only weir surface never yet run on
Windows is HTTP.

A1 was the plan's predicted shape exactly: one mapping function
(cursorDisplay), one wrong arm. At an exact wrap boundary the arm
always chose end-of-previous-row and emitted column W — which does
not exist on a W-column terminal, so the emission clamped to W-1 and
two logical columns painted at one cell; the crossing then jumped
two. The fix splits the boundary by whether the next row EXISTS:
mid-line it does (start of next row), end-of-line it does not (the
wrap-pending cell, named explicitly as W-1). The pin class also
needed correcting, and carefully: the harness's standing law says
assertions ride values because DRIVERS lie about cursors — but A1 is
invisible to values (the logical column was always right; only the
paint was wrong). The new pins replay OUR OWN emitted positioning
escapes — the tail after the final carriage return — which is
deterministic and does not query the driver; the law's reason is
intact, and the pins now assert cursor COLUMN at two widths in both
directions plus the exact-fill pending case.

A2 was one line: the candidate-list arm parks the cursor at the
region end so the list prints below the buffer — via toEnd(), which
MUTATES the tracked (row, col) and never restored it. Every symptom
follows, including the "second Tab offers the namespace" one: the
cursor genuinely was at end-of-line by then. Save/restore around the
park; pinned by VALUE (type into the parens after a Tab-list — the
tail lands inside).

B2's diagnosis mattered more than its fix: the "specific diagnosis
buried under the generic one" reading was WRONG. The line underneath
the 12-item expecting list — "block-let command RHS is spine-only" —
is the letCmdOk gate's internal label leaking into FParsec's
backtrack pile; there was no specific diagnosis to demote, so the
first-reached-owns-report policy was never inverted and the stop
condition did not fire. The fix is a new first-reached arm: a bare
`let x =` now says "this binding has no value — give 'x' a
right-hand side", and the pin asserts the internal label never leaks.
B1 is the name-the-repair principle once more: backslash+letter in a
string is a Windows path in the person's first hour; the message now
names @"C:\path" and forward slashes, while non-letter escapes keep
the bare list (a hint on an ambiguous shape is worse than none).

## hover residue: three form words, one leaking type argument (2026-08-06)

Applying the form-word rule to its remaining customers cost almost
nothing because the machinery was already one-brained: retry/poll/
until join the formWordHover UNION, and the single gated call site
means the string/comment exclusion covered them the moment they
existed — there was no second path to forget. The one design choice
worth recording: the plan said "read the keys from the options
records", meaning Retry.defaults/Poll.defaults — but those are VALUES,
and the LSP is check-side. The prelude TYPE declarations are the same
single source (the key=value desugar keys off the record shape), live
in env.Types, and carry field types, so the hover reads richer
(`attempts: int, delay: Duration, timeout: Option<Duration>`) and the
derivation pin computes its expectation from the same env — a new key
reaches hover and pin with zero further edits.

The type-argument leak was exactly the predicted shape: the type name
in `from json T` is no expression node (the bespoke checker arm
absorbs it), so nodeAt returns the enclosing TEFrom and the node
FALLBACK answered with the adapter's arrow. The doc half never leaked
— usageDoc already resolved through definitionTarget to the
declaration and attached the /// doc — so the fix is one ty-chain arm
just before the fallback, rendering from env.Types byte-equal to
declHover's declaration rendering. The equality pin (hover at use =
hover at declaration, shape and doc) is the drift-proof form, and it
caught my own wrong guess in review: fields render in DECLARED order,
not sorted — the pin's fragment had to be corrected, which is the pin
doing its job before it ever shipped.

All four sibling positions (from yaml, Env.load, Args.load record and
union) shared the cause and the one arm fixed them. schema=<name> is
different in kind — a vendored file name, not a type in env.Types; it
currently hovers the district's Yaml type via the fallback, and a
schema-file hover would be its own small design, reported not built.

## the exit hook: temp dirs survive a hard exit no more (2026-08-06)

The park's two customers both close. The mechanism is small — a
per-process ConcurrentDictionary of live tmp dirs in Session, a
register after CreateDirectory and a deregister after the finally's
own delete in `within tmp`, and a lazily installed sweep (first
registration installs it, so the shebang path pays nothing; timing
gates unchanged). Registration-not-scanning is the sharpest
constraint and it is pinned: two concurrent weirs, one killed, the
other's directory untouched — a blind temp-root sweep of weir-tmp-*
would have been a one-line bug deleting a neighbour's scratch space.

The coverage matrix, live-verified on POSIX:

    path                    covered   mechanism
    normal process exit     YES       AppDomain.ProcessExit (Probe B fixed)
    SIGINT (Ctrl-C)         YES       PosixSignalRegistration, sweep then die
    SIGTERM (kill)          YES       same registration pair
    SIGKILL / kill -9       NO        unreachable by definition — residual,
                                      weir-tmp- prefix keeps leftovers findable
    Windows normal exit     YES       ProcessExit (same arm)
    Windows Ctrl-C          compiled  Console.CancelKeyPress — untested until
                                      the Windows walk

The harness finding is the session's best lesson: the first e2e pin
sent `kill -INT` to a backgrounded weir and NOTHING happened — no
sweep, no death, while the identical interactive probe worked. The
cause is POSIX, not weir: a shell without job control starts
background jobs with SIGINT set to SIG_IGN, and .NET honors an
inherited SIG_IGN (the nohup convention — a deliberately-ignored
signal stays ignored). So a set -e harness literally cannot deliver
a meaningful SIGINT to a backgrounded child; real Ctrl-C, with the
default disposition, takes the handler (verified interactively). The
e2e pins the signal path via SIGTERM — bash only ignores INT/QUIT
for background jobs — which exercises the identical sweep handler.
Chasing that took three instrumented probes because the first
blocktest run ALSO tripped an unguarded `kill` under set -e, stacking
the known silent-death lesson on top of the SIG_IGN one.

Scope stayed temp-dirs-only as blessed: within cd/env are AsyncLocal
state with nothing on disk, Dir.newTempDir is exempt by contract
(it exists to outlive the scope — its doc's "neither cleans on
Ctrl+C" half was the one stale sentence this session had to fix
beyond SEMANTICS), and this is NOT the defer/trap feature — that
stays a predicted receipt.

Session note: a concurrent fork was mid-flight in the same tree
(retry/poll/until form hovers in Lsp.fs), so this session's commit is
scoped to its own files and the one unit failure in the full run is
that fork's in-progress pin, not this session's.

## if cmd | succeeds then — the gate was already built (2026-08-06)

Diagnose-first, and the diagnosis dissolved the fear. Movement 4's
"acceptance gate" — the item that would have stopped this session if
it meant a mode decision at the condition head — turned out to name
something the language already owns: the let-RHS's ordered choice.
`rhsCmd <|> seqExpr` never decides command-vs-expression up front;
it ATTEMPTS the command grammar, whose head parser backtracks on
keywords and known bindings ("known name; expression mode" +
`attempt head`), and falls through to the expression grammar having
consumed nothing. The if condition is that gate one position over:
`withIfCond true ifCondCmd <|> expr`. Bindings-beat-PATH does the
rest — `if ok then` stays a variable, and `if true then` stays the
bool literal even though /usr/bin/true exists, because keywords
refuse the head slot. No mode boundary was created; the fuzz and
arming pins never moved.

The two additions priced exactly as their precedents suggested. The
argv `then`-stop generalized `cmdArgWith stopAtIn` into `cmdArgStops`
with a second stop word — and the positionality is free: only the
condition's command grammar passes stopAtThen, so `echo then` at top
level never consults it, and a literal `then` in a condition quotes
(the strLit arm precedes barewords), which is the let-RHS's
quote-"in" law one keyword over. The reifierEnd follower is the
inStop shape with a new ThreadLocal (ifCondOk), because the marker
parsers are module-level and cannot be re-parameterized per position
— the same reason letCmdOk exists.

The checker does the enforcing, deliberately: a condition chain that
does not end in a bool-producing reifier PARSES and then teaches —
"expected bool, got seq<string>" for a streaming chain, got Completed
/ got int / got unit for the other three cells. Parse-then-teach
beats a parse-time reifier requirement both in message quality and in
grammar complexity.

The survey: elif landed with if (same production — the asymmetry
would have read as a bug); until's predicate block already took bare
chains on main (block position is statement territory, so the
interior-arming follower covers it — verified against the main
binary before claiming it); for…do's header needs nothing, because
`$(cmd)` is its spelling and the wrap-it hint already names it. One
probe-shape lesson along the way: the "obvious" multi-line if with a
col-0 `else` after an indented body block was never legal weir — the
else-join is the space-joined single-statement form — and half an
hour of apparent regression was the fixture's bug, not the parser's.
Confirmed against the main binary before touching anything, which is
the habit that kept this session honest.

## the bare-alias allowlist, and two ledger items (2026-08-06)

The inversion was the easy half; the verification was the point. The
pre-inversion homes map came out 100% Seq/Str-homed — twenty aliases,
none from a third module — which means the blocklist survived four
rounds only because no OTHER module happens to carry a hot-path name.
File, Dir, Path, Env, Duration, Size, Retry, Poll all pass the old
filter and contribute nothing by luck of naming (`parse` is not a bare
alias; `read` is not). That is "unsafe by default" made concrete. The
usage enumeration across surfaces found the real bare names in use —
where, first, map, sum, trim, startsWith, join — in two REPL and two
#loose e2e fixtures plus the loose-env unit corpus, all Seq/Str; the
28 grep hits in the .weir corpus are user BINDINGS named `head` and
`force` in strict scripts (git-subrepo's), which is shadowing, not
aliasing. So the change moved zero usages, verified from both ends
(homes identity + usage walk).

The property pin is the session's actual product: bareEntriesOf and
bareAliasHomesOf are factored over the table, and a hypothetical Evil
module with map/head/trim members is pinned to contribute nothing
while Seq keeps the alias. The allowlist SET is pinned too — {Seq,
Str} exactly — so widening it fails a test until done deliberately.
The failure mode teaches as required: a bare non-allowlisted member
gets "moved into a module; use 'X.y'", multi-home lists candidates.

The seq-equality rider corrected its own plan. The prose claimed
upcast lists compare by reference — BACKWARDS, the 0.1 == 0.2 pattern
again: real F# (fsy-refereed) says `(list :> seq) = (list :> seq)` is
TRUE, because generic equality dispatches on the RUNTIME type and
lists are structural regardless of static type. What IS reference
equality: computed seqs — `Seq.map id [1;2] = Seq.map id [1;2]` is
false on equal values, `seq { 1; 2 } = seq { 1; 2 }` false. So F#'s
answer changes with PROVENANCE at one static type, which is a
sharper argument for weir's refusal than plain reference equality
would have been. The row, the oracle pinT (the fidelity harness
itself refused the pin until the row existed — the drift guard
working), and the coming-from paragraph all carry the verified
version. One near-miss in my own docs line: I first wrote "compare
after Seq.force-ing", which is wrong in weir (Eq excludes the TYPE,
forced or not) — the honest idiom is compare a value you mean
(Seq.length, a Str.join-ed string).

The batched-edit lesson went to PROCESS with both halves stated: a
failed assert leaves NOTHING including the parts that succeeded, and
per-edit printouts are not receipts — check the file, re-apply the
whole intent, never the "missing half".

## Http session 2: constructors, methods, the shorthand (2026-08-06)

Purely additive, and it went fast because session 1's shape held. The
constructors are the interesting ruling: `Http.get u` is a record
PRODUCER, not a builder combinator — it names only the method (the one
thing already enumerated), so it does not reopen session 1's rejection
of a function-per-field. The pin that keeps it honest is value-level:
`Http.get u` must equal `{ Http.defaults with method = Get; url = u }`
BYTE-IDENTICALLY, which I could not write as `a == b` in weir because
the record has a seq field (`headers`) and `==` refuses seqs — so the
pin compares the two evaluated records in F#. A constructor that
drifted from the record it claims to produce would fail it.

`Http.fetch`/`Http.send` are the raising/returning pair — the same 404
that `fetch` raises on, `send` binds as data, pinned together. That is
Arquidev's `ensureOk=true` boolean turned into two names, which reads
better and needs no flag.

The name collision the plan flagged was real and I ruled it before
building: `Http.query` is the METHOD constructor (consistency with the
other seven), and the query-STRING builder is `Http.withQuery`. It
percent-encodes so a space or `&` cannot break the url — which narrows
the URL-construction non-claim's query half, though the path half
stands and the docs still say so.

`insecure` I first parked, then built when the user said the receipts
were coming. It is a loud per-request field (`insecure: bool`, default
false) — TLS verification on by default, off for one request via a
self-signed `HttpClientHandler` callback, never global. The e2e pin is
a real self-signed server (openssl-generated cert): the default
request rejects it with UntrustedRoot, `insecure = true` accepts it.
One implementation slip worth remembering: a batched Python edit that
did two replaces in one write ABORTED on the second's failed assertion
BEFORE writing the file, so the FIRST replace (the `insecure = false`
default) silently did not land either — the symptom was a runtime
"key not present in dictionary" when a bare constructor's record lacked
the field. Batched edits are all-or-nothing on the assertion, so a
half-applied intent reads as a missing field, not an error at edit
time.

The recurring footgun bit a THIRD time: `Http.head` polluted the bare
`head` alias (Seq.head), exactly as `Secret.map` hit bare `map`. The
fix is the same — Http joins Option/Float/Secret in the qualified-only
blocklist — but three occurrences is a pattern. The bare-alias
mechanism is a BLOCKLIST (exclude these modules), so every new module
with a hot-path-named member (`map`, `head`, `of`, `first`, `sum`, …)
silently collides until remembered. A future harden should invert it
to an ALLOWLIST (only Seq and Str contribute bare aliases), which
would make a new module safe by default. Noted, not done — it is its
own small change with its own risk.

## Http: the typed request boundary, session 1 (2026-08-06)

The design collapsed once the body question was answered honestly. The
plan sketched `Json of 'a` — send serializes a typed value — but weir
has no Jsonable CLASS (the closed, erased Eq/Show/Ord set), so a
generic union case carrying a jsonability constraint, inside a record,
behind a polymorphic `defaults`, is heavy checker machinery for a
feature that needs none. The realization: `to json` ALREADY produces
`seq<string>`, so the body is `NoBody | Json of seq<string> | Text of
string` (monomorphic) and the caller writes `body = Json (payload |>
to json)`. The jsonable law is reused for free, `Json`/`Text` just tag
the content type, and the whole feature becomes prelude types + two
ordinary builtins — no new Ty, no bespoke checker node, no generic
records. That is the session's main lesson: a typed-value-to-wire
feature does not need a generic body if the serializer already exists
as a value-producing function.

Three things fell out for free from prior sessions. `show` masks the
request's secrets with zero new code — an `HttpRequest` is a record
containing an `Auth` union containing a `Secret`, and `formatWith` is
the one recursive renderer, so `Bearer (***)` and `secretHeaders =
[(..., ***)]` render by construction (the Secret session's property,
confirmed and pinned). The `defaults`-template pattern is retry/poll's
second customer and wanted nothing shared — a prelude type plus a
`Http.defaults` record value is the whole mechanism. And the transport
error reuses the contracts fetch's message shape ("cannot reach {host}
— {root}") rather than inventing a third.

Auth as a union rather than a function-per-scheme paid off exactly
where the plan predicted: `Basic` is base64(user:pass), an ENCODING
the runner does inside `send`, so `Secret.map` is not even needed —
the union case carries `string * Secret` and the base64 happens once,
correctly, where a hand-built `Authorization` header would get it
wrong.

The reifier-over-HttpResponse question was answered before coding by
reading the machinery: the reifiers are spawn-and-reify FUSED (they
call Proc.completeWith) and pure parse markers (`succeeds` is not a
nameable value), so `Http.send | succeeds` has nothing to attach to.
The honest generalization needs a runtime-witnessed success-predicate
class, which breaks the erased-class invariant — so it is deferred on
benefit/cost, not tractability, and written up in PLAN-http with
`Completed`/`HttpResponse` named as its two customers. `Map` is parked
the same way, with headers staying `seq<string * string>`.

Binary delta is ~0 as the plan leaned on: HttpClient is BCL and
already linked for the contracts fetch, so no NuGet reference was
added and only ~130 lines of IL joined the 12MB binary. e2e is
offline against an inline Python echo server — the mangling pin sends
a 3-object NDJSON body and confirms it returns byte-exact, the bytes
`curl -d` would have joined into one line.

## Secret: a marker the renderers respect (2026-08-06)

The type is trivial and the coverage IS the feature — which the build
process kept proving. `TSecret`/`VSecret` wrap a plain string; the
work is that every renderer refuses it. Two of those renderers are
exhaustive matches (formatWith, validateTy), so they SHOULD have
warned FS0025 on the new case — but the incremental build reported
zero warnings and a clean binary. `--no-incremental` surfaced them.
That is the three-part-ritual's part one made concrete: an incremental
F# build caches away the very incompleteness warnings the ritual
exists to catch. The value-side wildcards were the invisible half:
`scalarString` ends in `| v -> unreachable`, so a Secret spliced to
argv (which the checker now ALLOWS) would have crashed rather than
warned — the probe caught it, and VSecret→raw is the argv ruling's
teeth (the value reaches argv in the clear, ps-visible, a stated
non-claim).

The load-bearing render is the recursion: formatWith is the ONE
recursive renderer, so `| VSecret _ -> "***"` there makes a Secret
inside a shown record, union, tuple, or seq render `***` for free —
the containing-record case the plan named as the likeliest naive
miss, handled by construction rather than by enumerating containers.
The REPL echo falls out the same way (echoValue routes through
formatWith).

The splice law split cleanly along the two sites it already had: a
Hole REFUSES a Secret (a secret must not reach a log line — show
gives ***, reveal is the deliberate use), a CmdArg ALLOWS it (curl -H
$auth is the point). The wire boundaries refuse like the unit types
do, each naming reveal.

One regression the plan did not predict:
adding `secretMembers` (of/map/reveal) polluted the BARE ALIASES.
`bareEntries` flattens every module's members into unqualified names
for loose mode, and bare `map` collided with `Seq.map` the moment
`Secret.map` existed — 22 unrelated ls/sortBy tests failed with
"expected Secret, got seq<FileRow>". Secret joins Option and Float as
qualified-only (excluded from the flattening). The lesson: a new
module with a member named like a hot-path alias (map, of, reveal) is
a bare-alias event, not just a type-family event.

The in-memory honesty is stated up front in SECURITY.md, and the
SecureString pre-refusal in DECISIONS was verified live (dotnet/
platform-compat DE0001): Microsoft say don't use it for new code, it
is unencrypted off .NET Framework (theatre on Linux/macOS), the value
must be plaintext at every use, and immutable strings cannot be
zeroed. So the type claims exactly what it does — a rendering marker
weir respects consistently — and no more.

## error messages speak the user's language (2026-08-06)

The motivating message carried two faults, and the second was the
better find. A user wrote `| code, _ :: rest` meaning `(code, _) ::
rest`; because `::` binds tighter than `,`, it parsed as a two-element
tuple whose second element is a cons, and the checker reported a
faithful type mismatch that pointed nowhere near the two-character
repair. The fix names the grouping — not the category — because the
grouping IS the repair: on the unambiguous shape (a tuple pattern
whose last element is a cons, matched against a seq) the message now
reads "`,` groups looser than `::`, so `code, _ :: rest` is a tuple;
did you mean `(code, _) :: rest`?", the suggestion rendered straight
from the parsed pattern via sexprPat. The near-miss (a plain `a, b`
tuple against a seq) keeps the bare message — pinned, because a hint
that fires on the ambiguous case is worse than none.

The sibling positions turned out to be covered by redirection: a
refutable tuple/cons in a `let`-destructure or a `for`-binder trips
"this pattern can fail; use match" FIRST, which routes the user to a
match — where the did-you-mean then fires. So the footgun only
reaches its full form in a match scrutinee, and that is the one place
that needed the hint.

The vocabulary sweep was smaller than feared: "scrutinee" was the
ONLY compiler-writer word in the message surface — ten of them, all
now "value" — and the other suspects (unify, desugar, arity, row
variable, thunk, variant) grepped zero in message strings. The
synthetic-name grep came back clean too: no `__hole`, no `|`-prefixed
desugar key, no internal name reaches a message (the district-splice
session's B6 cascade suppression already guards the hole case). The
one thing that DOES surface is a generated type variable — `'a1` in
"this one has type 'a1" — but that is a legitimate "some type"
rendering, F#'s `'a` with a numeric suffix, not an internal leak;
prettifying it to sequential letters is a display concern that would
churn many pins for little gain, so it is reported, not touched.

The boundary that makes the sweep decidable rather than a taste
argument is now one line in PROCESS: a message speaks the script
author's language; SEMANTICS, DECISIONS, and NOTES may speak the
implementation's, because a reader of the theory opted in and a reader
of an error did not. That is the rule that says "scrutinee" belongs in
this very file but never in the binary's output.


## Seq.pfirst follow-ups: the exit-race park, named customers (2026-08-06)

Six small closes after the race landed, and one decision worth the
record. The empty-sequence message gained the guard it should always
have named — "a race needs at least one arm; guard with Seq.isEmpty"
— the reject-don't-guess full form (there is no tryPfirst, so the
message points at the pre-check). The member doc gained the
swallowed-loser hazard in one sentence: if only one arm can succeed,
the others' errors are hidden, so a misconfigured fan-out still looks
healthy — correct semantics, an hour of debugging without the warning.
And `pfirstWith n` was confirmed already shipped (not a
recommendation), so the coming-from parallelism row now carries it
beside pmapWith with the Promise.any anchor.

The decision is the exit-race leak (Probe B, from the design review):
pfirst leaks a `within tmp` directory under NORMAL, uninterrupted
completion. The workers are background threads; when the script exits
right after the winner returns, a losing arm still inside its `within
tmp` block is killed mid-`finally`, so the temp dir never gets removed.
This is NOT a race-mechanism bug — the loser's `finally` runs fine on
its own un-aborted thread WHEN the process outlives it (pinned in e2e:
loser blocked in `sleep 5`, winner returns, dir gone 1s later). It is
the process-exit case that leaks.

PARKED — a process-exit cleanup hook that removes live/registered
`weir-tmp-*` dirs on exit. TWO customers, which is the argument to
eventually build it: (1) pfirst's exit-race — a background loser
thread killed mid-`finally` on NORMAL, uninterrupted completion leaks
its `within tmp` dir (Probe B, reproduced); (2) the PRE-EXISTING leak
every script already has — `within tmp` cleanup is a managed `finally`
(Eval.fs, "best-effort" by comment), so Ctrl-C / `kill` has always
leaked it. pfirst didn't create the class; it added the first
NO-signal instance, which is what makes the class worth a hook.
Trigger: a receipt where leaked temp dirs cause real trouble, OR the
Windows job-objects session (same hard-exit-skips-cleanup family
[D:windows-s2]).

## from/to adapters in the editor — the within-kinds sibling (2026-08-06)

The live check shrank this before it started: the three adapter WORDS
(`json`, `yaml`, `porcelain`) already hovered with signature + purpose
+ usage. But the backing turned out to be HARDCODED per-(direction,
adapter) strings in `builtinDocs` (`"from json"`, `"to yaml"`, …), not
a table — so the plan's premise "three adapters with docs means a
table already exists" was wrong, and its "keep the shape already
serving three members" resolved to the opposite of what it expected:
there is no table to keep, so DERIVE from the one source that already
works. `adapterNames dir` scans the `builtinDocs` keys with the `from
`/`to ` prefix — direction-aware for free (`from` → json/porcelain/
yaml, `to` → json/yaml), and `porcelain`'s read-only-ness falls out
because there simply is no `"to porcelain"` key. Nothing can drift
from the adapters' own hovers because it IS their source.

What was actually missing was three things, all the within-kinds
shape one form over: `from`/`to` themselves hovered NOTHING (they are
keywords, swallowed by the hover-silence guard), so `adapterFormHover`
adds the DISCOVERY surface — the form plus the adapter list, which is
the one thing no other editor affordance tells you. Completion after
`from `/`to ` offered the general identifier soup; it now offers the
direction-appropriate adapters and nothing else, a sibling arm to the
`within ` and `schema=` slots (three instances — closed-set slot
completion earned its name). And the adapter word painted as an
identifier in all three grammars.

The colour work confirmed the "verify each" lesson again: tree-sitter
already had an `adapter` token — but `yaml`-ONLY, built to stop `to
yaml` opening a district, not for colour. Widening its inner `choice`
to `json`/`yaml`/`porcelain` colours all of them (and harmlessly
over-accepts `to porcelain` — a renderer bound). TextMate and micro
had nothing; each got a two-word rule in its own idiom. An adapter
inventory e2e now pins `builtinDocs` == all three grammars, the same
mechanical drift guard the kinds got.

Two things reported, not fixed (out of scope): `retry`/`poll`/`until`
still hover nothing — the same form-keyword class, trivially fixable
under the now-stated form-word hover rule, owed a line when someone
touches them. And the type argument `T` in `from json T` hovers the
adapter's arrow type (`seq<string> -> seq<Config>`) rather than the
record's own shape — not `unit`, not wrong, but imprecise; the same
enclosing-node-leak class the within binder question raised, left
untouched under zero-movement. The form-word hover rule is now in
PROCESS so `within`, `from`/`to`, and the next form-keyword are ruled
the same way once, not re-litigated per keyword.


## within kinds in the editor — one table, three consumers (2026-08-06)

The bug that opened it: hovering `within cd helperDir` returned
`unit`. Not silence — a WRONG answer, teaching that `cd` is a
unit-typed value. The hover-completeness session's own law (a wrong
type is worse than nothing) was being violated by the very form the
session added. The fix is the table: Ast.withinKinds (Name, Binds,
Doc) as ONE source the parser dispatches off (Binds is the arity
switch — tmp binds a directory, cd/env consume an atom), hover reads
(kind → doc + binds/consumes; `within` itself answers; the binder
hovers `string` even unused — it used to fall through to `unit`), and
completion reads (the kind slot, a closed set). The teaching list
"tmp, cd, or env" derives from the table so a fourth kind cannot ship
with a stale message.

The subtle bug, caught by the from/to fork reading my work: the form
hovers run BEFORE the silence guard — they have to, because `within`
is a keyword the guard would silence, and the whole point is that it
answers. But that means a form word inside a STRING (`"run within cd
first"`) or a trailing comment also bypasses the guard and fires the
discovery text. The first fix gated on the joined logical-line text
and broke the code case — the sentinel separators in the joined text
threw off stripComment's length, so every real `within` read as "past
the comment". The gate has to run on the PHYSICAL line at the physical
column (inStringOrComment), never the joined text. One gate now covers
both within and the adapter form hover — the shared consumer the
sibling session predicted.

Grammars: the kinds are a closed set hard-coded in three files
(micro whole-match, tmLanguage two-capture rule before keywords,
tree-sitter within_head/within_kind nodes with regenerated parser.c
and a Zed sync). Engine-verified individually — vscode-textmate
tokenize and a tree-sitter query, not eyeballed. And the single-source
law is made mechanical: an e2e inventory pins Ast.withinKinds equal to
all three grammars' hard-coded sets, so a kind added to the table but
not a grammar fails CI. The REPL colorizer paints the kind as the form
(a prevWord=within arm), with a pty pin.

## LSP cross-file navigation: the retention answer (2026-08-05)

The plan ordered a retention diagnosis first, and the answer is the
fact the next LSP session will want: THERE IS NOTHING TO RETAIN. The
server's stateless design (per-document text, whole-file re-analyze
per request) already carries everything — a KImport statement holds
the full LoadedModule (path, typed body, type defs), and doc
attachments are a pure function of lines. All five symptoms were one
gap: the reference-site→definition-site mapping stopped at the file
boundary. definitionFor returned file-less positions and skipped
dotted names by guard; the doc lookup read only the entry file. So
the fix is definitionTarget growing a file dimension and re-analyzing
the TARGET file's lines through the import channel — the same
stateless discipline, one file over. Invalidation therefore holds
trivially (pinned: edit the module buffer, the importer's hover
refreshes).

The probes corrected two assumptions in the plan's own text. "Lib.Ctx
in a qualified literal" does not exist — imported types merge
UNQUALIFIED (`from json Ctx`; `Lib.Ctx` is a parse error), so the
type half of the fix is a fallback from the local KType search into
the import whose TypeDefs declare the name, and every type-position
arm (fields, cases, from json, Args.load…) crossed for free through
that one seam. And imported union cases never enter value scope at
all (`Bad 3` reads as a command head) — only pattern positions reach
them, which is where the pin sits.

The signature half really was the same fix with a different
reference-site shape — but SigInfo had to learn its own file first
(it carried Tool + DeclLine; the resolved sig path died inside
loadSigs). cmdSurfaceAt mirrors the unknown-flag check's recovery
(bare TECmd + the reified spine), and the flag word reverses through
kebabFlag/explicitShorts to its field. The off-PATH question
resolved cleanly: a signed absent tool only WARNS cmd-not-found, so
the runner-only-tool story works, and the head-hover pin runs with
the tool genuinely off PATH — version is the RECORDED string, spawn
never happens.

One found-and-fixed beyond the plan: the buffer-over-disk override
looked open buffers up by pathToUri(abs) — a dependency open under
the client's own spelling (%6C…) silently read DISK. The Windows
session's mirror-bug rule (spellings must round-trip, both
directions) applied to the read direction; now matched by decoded
path, and the e2e invalidation probe runs under a percent-encoded
spelling on purpose.

Completion report, as ordered: `Lib.` offers member NAMES (that was
never blocked) with no docs — docs did not come free because the fix
was not retention. Flag completion did not fall out either. Both
stay future work. Stale docs trued while there: SECURITY.md's
non-claim now names #sig files (missed by the signatures session),
and editors.md's scope line still claimed the server reads no files
at all (pre-modules text).
## Seq.pfirst: the race (2026-08-05)

The park's shape held almost exactly — the one design correction:
the recorded "Task.WhenAny + CTS" became a TaskCompletionSource with
an Interlocked winner claim. WhenAny answers "who finished first",
but the contract is "who SUCCEEDED first" — a fast failure must not
win the race, it must be swallowed and the race must continue. So
the winner is claimed by CompareExchange on the success path only,
failures accumulate, and only the last failure (count == n) forfeits
the race with the first error by input order. No CTS either:
cancellation's real target is child PROCESSES, and the kill is the
RaceGroup, not a token — weir arms have no await points a token
could interrupt anyway.

The kill mechanism is two small pieces: Session.RaceGroup (a
ConcurrentBag of processes + a condemned flag) carried by AsyncLocal
exactly like the cwd fork, and one line in Proc after Process.Start
registering the child into the ambient group. The condemned flag
closes the register-vs-condemn window: a child spawned a moment
after its arm lost is killed at registration instead of leaking.
Condemn runs BEFORE TrySetResult, so by the time the winner's value
is in the caller's hands every loser tree is already dead — the e2e
pin is a loser running `sleep 2 && touch marker`: winner returns in
~275ms, and the marker still absent past the 2s mark proves the
orphan died rather than merely being un-waited.

Honesty line, stated in SEMANTICS: loser THREADS are cooperative. A
pure-compute loser finishes in the background and is discarded; the
kill reaches what arms actually wait on. The 64-ceiling pool shape
is shared with pmap — workers stop pulling indices once won flips,
so a win with unstarted arms never spawns them.

Probe surprises, small: `fail` is string -> unit, not polymorphic —
a racing arm mixing `fail` with a value needs `fail "x" ; 0` to
type (the unit-pin lambdas hit this). And found-in-passing staleness
from the tasks session: pmap's doc string and the runner comment
both still said ProcessorCount; fixed to the ceiling. The docs table
never got pmapWith/piterWith entries — left as found, noted in the
DECISIONS row.

## the leftovers sweep: yet-retired, hole-sealed, ledger-trued (2026-08-05)

Four leftovers closed in one pass. The four unit-type boundary
messages dropped their "yet" ("has no JSON convention yet" → "is not
representable in JSON"), each now naming BOTH exits (toX into an int
field, or show for a string) — the receipt framing is gone from
behaviour as well as the docs; four pins moved with the wording.

The district-splice finding resolved into two smaller things than it
looked like: the splice already HAD a located message (the '__hole1
error was a CASCADE — the failed splice left the binding's type
unresolved, and the next line's boundary wall printed the internal
hole name). The splice message now teaches the unit-type exits, and
both yaml walls join the B6 cascade-suppression law (a __hole var
means the real error was already reported — stay silent, never print
the name). Net: ONE located error where there were two, the second
of which leaked internals. Pinned as exactly-one-error.

The divergence ledger: no-user-modules RETIRED (modules landed
07-29; the no-let-param-sugar precedent), no-imperative-loops
NARROWED to no-while (for…do landed 07-30; retry/poll are the
bounded loops — the tombstone comment carries the history), the
no-heredoc row's stale `xs | into "cmd"` spelling corrected to the
value-headed pipe, and the long-unrowed type-name-in-expression-
position family (Args.load Cli, from json T) got its row. Oracle
green — no pins referenced the touched rows.

The branch sweep: 31 local branches deleted, 29 verified by their
merge commits in main's log, two (duration, floats-cost — they rode
the combined Floats+Duration MR) verified by CONTENT per the
standing lesson, plus one straggler (gap-a-remainder) the same way.

## a duplicate type declaration errors; the REPL replaces (2026-08-05)

The seam the desugar-capture session sized, closed as ruled. The
script check lives in both pipelines (the analyze diagnostics and
the runner's own gate), one KType arm covering records and unions,
message naming both sites. The declare-after-import direction turned
out UNGUARDED — the module session's collision check only fired the
other way — so the local check covers it symmetrically, and the two
cannot double-report (probed, one error per direction).

The REPL probe made the note decision, as the bless demanded: an old
binding still ECHOES with its old fields ({ a = 1 } : T) while field
access resolves against the new shape ("T has no field 'a'") — the
value and the checker visibly disagreeing is the confusing case, so
redeclaration prints the one-liner. The pty probe is in the report;
no pty pin added (the divergence is documented and the script side
is unit-pinned — a repl-quality scenario can join later if the REPL
suite grows a types section).

Sibling audit: duplicate top-level lets are forward-only lexical
shadowing (correct); duplicate union cases across types are forward
constructor shadowing, F#-consistent, values stay tagged; duplicate
fields were already an error. The type map was the only retroactive
one — which is exactly why it was the defect.

## the desugar-capture seam, closed everywhere it existed (2026-08-05)

The bless asked for the retry fix plus an enumeration, and the
enumeration was the session: every Seq-referencing rewrite (for/do,
comprehensions, ranges, indexers, the splat fold) was capturable by
`type T = Seq of int`, and the arming pipe — parse-side and
check-side both — was capturable by shadowing print, which meant a
legal-by-design shadow changed what BARE COMMANDS meant. The probe
that showed it: `let print = fun s -> "SWALLOWED"` turned an armed
interior command into a seq-unit error; with a unit-returning shadow
it would have silently eaten output.

All of it takes one fix shape: the reifier precedent generalized.
Nine parser sites and two checker sites now target `|`-prefixed
un-typeable keys registered with the SAME objects as the public
members — reference-equality pinned, because the sugar and the
manual spelling silently diverging would be worse than the capture.
The boundary is stated and pinned: the fix protects the DESUGAR;
a user who shadows Seq or print and writes the name themselves gets
their ordinary shadow.

The type half turned out real: `type Retry = { x: int }` silently
redeclared the prelude type and re-broke the sugar through env.Types
after the value was fixed. Built-in type names now refuse
redeclaration, with the prelude's own replay exempted (ThreadLocal,
the toPhys pattern — tests rebuild envs in parallel). User-vs-user
redeclaration is ALSO silent (last wins) — a separate pre-existing
seam, sized for its own decision rather than smuggled in here.

The equivalence pin moved by design and got stronger: byte-identical
ASTs became both-forms-pinned plus behavioral equivalence plus
reference-equality. Twelve pins retargeted, all of the same species —
they pinned the desugar's SPELLING, and pins of implementation
spelling move with the implementation. The LSP's splat-token matcher
was the one non-test consumer of the old spelling.

The second audit closed its own finding: signature checking sees
commands in for/within/retry bodies, if groups, and splats — and the
reified+splat corner was UNCOVERED when probed (the fold buries the
literal chunks below the spine recovery); the chunks are now
deep-collected. What remains unknowable is inherent: a spliced seq's
runtime contents. Both PROCESS rules are recorded with this session
as archaeology.

## unit types are non-representable at the wire (2026-08-05, rider)

Docs and reasoning only, zero behaviour moved. The Duration and Size
json/yaml parks said "undecided encoding, wants a receipt" — a
temporary framing that would have been re-litigated forever, and the
Size session's SI-in-parse asymmetry was already being read as an
argument for boundary parsing. The replacement reasoning closes it:
the wire formats have no such types, an emitted encoding would be
weir's invention in someone else's document, and a read-side law
would assert — silently, on the author's behalf — that the document's
writer used weir's format. The parse asymmetry's own principle cuts
the other way: parse is invoked BY the author on text they chose.
Receipt trigger dropped, explicitly.

All eight direction-cells read live and confirmed tailored. Two
findings reported rather than folded, per the zero-change bar:

1. Every message says "no … convention YET" — the receipt framing
   survives in the message text itself. Retiring the word moves four
   pins; a separate rider if wanted.
2. A FIFTH cell: a unit-type DISTRICT SPLICE (`wait: $d`) rejects
   with the generic yaml-boundary wall and LEAKS an internal name —
   "type '__hole1 cannot cross the yaml boundary". Untailored plus a
   cosmetic bug (a synthetic hole variable shown to a user); its own
   small fix.

## the showcase covers everything, and what it caught (2026-08-05)

The stale sweep came back empty — zero hits per document across
File.size, the toSeconds/toMillis rename, and floats-absent claims.
The session discipline of updating docs WITH the change held; the
sweep's value was proving it.

The interesting half was the tour catching two real things the
suites had not:

1. **The retry-desugar capture.** The showcase's own
   `Verdict = … | Retry of string * int` captured the key=value
   sugar: `retry attempts=2 …` desugars to `Retry.defaults`, and the
   local constructor named Retry shadowed the options module —
   "only records have fields; this expression has type
   string * int -> Verdict". Any user binding named Retry or Poll
   breaks the sugar the same way — the F#-type-name-capture class
   the record-update rows deliberately dodged. The showcase renamed
   its case (Again); the LANGUAGE seam stays open and recorded: the
   desugar wants a non-capturable module reference, its own small
   session.

2. **The reifier signature gap, fixed in-session.** Reified commands
   — `git status --porcelain | succeeds`, the most idiomatic shape —
   desugar their ECmd into the |succeeded-family builtin spine, so
   signature checking silently missed them. The walker now recovers
   (prog, argv) from the spine; prog is the LAST string because
   orFail's message precedes it. All four reifiers pinned. Worth
   saying plainly: before this, the signatures feature checked bare
   commands and pipes but not the reifier shapes scripts actually
   use most.

The tour now hosts a module (qualified literal crossing the
boundary), a committed .weir/ (real ConfigMap schema + lockfile —
a fresh-tree copy CHECKS offline with no restore, e2e-pinned), a
hand-written partial git signature (no lock entry — hand-written
sigs are source; git refuses to scrape, per its own session's
report), and a two-line retry demo (added beyond the bless because
the header's honesty rule demanded demonstrated-or-excused and no
excuse existed). Still not in the tour, with reasons in the header:
the REPL (interactive) and add-sig generation (spawns the probe).
Windows walk = the standing hand-run item.

And one for the pattern book: my own e2e fixture died the silent
set-e death (the typo probe tripped weir's discard rule, rc 1 inside
a command substitution) — diagnosed in one step this time because
the dedent session named the pattern.

## size: the Duration copy, and where it did not fit (2026-08-05)

The session's brief was to copy a proven pattern and report the
misfits, and there were exactly three — the useful part for whatever
unit type comes third:

1. **show is a truncated report, not an exact encoding.** Duration's
   show renders the exact ms decomposition, so value→text→value is
   total. Base-1024 decimals do not terminate, so Size's one-decimal
   rendering truncates by design; the round-trip law is
   text→value→text plus the representable value cases, and toBytes
   is the exact exit. A third unit type should decide this FIRST —
   whether its rendering can be exact decides which round-trip law
   it gets to claim.
2. **parse met ambiguous foreign units.** Duration's units are
   unambiguous, so its parse had nothing to rule. Size's SI-in-parse
   asymmetry (literals refuse MB; parse reads it as 10^6, because
   the writer of foreign text chose the unit) is new ground — the
   json integer-widens shape applied to unit systems.
0. [Filed 2026-08-05 by the unit-types-wire rider — the rule the
   misfits below feed:] **a unit type's wire representation is the
   CONSUMER's convention, not weir's.** The json/yaml boundary
   rejects (non-representable — not awaiting a receipt), the author
   converts: toX into an int field, or show where a string is what
   the consumer wants. Decide rendering-exactness first (misfit 1),
   expect foreign-unit questions in parse (misfit 2), and write BOTH
   boundary rejections tailored on day one (misfit 3).

3. **the YAML park was not in the copy — and the first explanation
   of why was WRONG** (a user question caught it): yaml landed
   07-30, Duration 08-04, so "predates" does not hold. The true
   story: the Duration bless parked JSON explicitly and never
   mentioned yaml, whose generic field law caught TDur with an
   untailored rejection. The follow-up commit gave Duration the
   matching tailored park (Duration.toMillis named), so both unit
   types now teach their exit at both boundaries.

The zero-edit interpolation property CONFIRMED for the second time:
$"{2MiB}" rendered "2 MiB" with the interpolation machinery
untouched — the class was asked, the class answered. Floats were
the first confirmation; the property is now load-bearing.

Mechanical notes: the suffix table MERGED rather than doubled (one
choice serves durations and sizes; case distinguishes MiB from
minutes; the digit-follow compound fix and the letter-follow
backtrack are inherited by construction). The batch-edit discipline
earned its keep twice — two failed asserts (a fantomas'd attr
anchor, a reworded doc string) discarded whole batches with nothing
half-written, and the one silently-missing edit (the main suffix
choice, lost between the two batches) announced itself as every
literal probing as int-applied-to-ident. Exactly one suite pin
moved for the File.size break (the Ord key-list message). FParsec
wraps long teaching messages across lines — pin fragments, not the
joined sentence.

## command signatures: customer two rides the spine (2026-08-04)

Both sessions landed on one branch, and the spine held exactly where
its report said it would: verify was genuinely two-arm-shaped (the
version arm is an arm, not a rewrite), the #sig walk is findWeirDir's
second customer, and #sig sits in the directive family — the
marker-line fourth-face guard is not applicable, confirmed rather
than assumed.

The design simplification worth recording: a signature file is an
ordinary weir MODULE. loadModuleCached supplies parsing, the
decl-only and weak-purity laws, and cycle guards for free; `let
version` and `let exhaustive = true` are plain bindings extractable
from the typed body without evaluation; and non-inheritance through
imports — the design's own worry — is vacuously strong, because
modules cannot contain commands at all.

Validate-before-write caught its own first bug: the generator's
probe loaded the temp file as importing itself and the self-import
guard fired — nothing persisted, which is the principle doing its
job against the generator itself. The fixture-based e2e then cost
four wrong hypotheses on a failure I misattributed: under set -e a
died battery prints NO fail() line, and the real culprit was the
SECOND add (missing its env prefix, so the fixture's --help touched
"" and exited nonzero) while I instrumented the first. The lesson,
recorded: when e2e dies showing only a program's stderr, the
unguarded line ABOVE the next fail() is the suspect — and the
capture-then-grep rule caught its third victim in the same block
(verify exits nonzero on mismatch; pipefail sinks the grep).

Session 2's honest hit rate: dotnet scraped 10 flags from --help;
git and python3 refuse cleanly to "write by hand" (their help shapes
defeat the leading-whitespace scraper — git puts flags in the usage
line, python3 uses single-dash longs); no Cobra/clap tools in this
container, so the completion-fish source is fixture-pinned and
awaits a kubectl-shaped receipt. The provenance comment and the
lock's generated:<source> slot record which probe produced what.

## CI fix: the dedent block's child weir missed the PATH prefix (2026-08-04)

The dedent-join e2e block's script spawns a child weir
(`!(weir -e "print 10")`) but its runner line lacked the
`PATH="$(dirname $BIN):$PATH"` prefix every other child-spawning
block carries — the exact lesson the first CI-fix session recorded,
re-learned: locally the install dir happens to be on PATH, so only
CI sees it. One line fixed. The verification is worth keeping as a
recipe: running the WHOLE battery under `PATH=/usr/bin:/bin` (weir
invoked by absolute $BIN, children resolving only through explicit
prefixes) passes end to end — so every child-spawning block is
prefix-honest, not just the fixed one. A new e2e block that spawns
`weir` as a child must carry the prefix; the stripped-PATH run is
the check when in doubt.

## retry/poll: the bounded loops (2026-08-04)

The framing arrived settled and held; every seam the implementation
met had a precedent to inherit. The heads are OpensCompound members
five and six (still no stack). The head parser consumes key=value
pairs and desugars AT PARSE into the exact nodes the manual record
spelling builds, so the byte-identical pin is by construction. With
no keys, one atom is the options value — the within-cd consumption
rule, same teaching class for the empty head (which is therefore not
a form: bounds are explicit at the site or in the record you pass).

The one piece with no precedent to inherit whole: `until` at column
0. The bless's own example puts it there, and the col-0 law closes
statements — the fix is the col-0 `|` arm's routing (a third member
of that exemption), plus two carve-outs where until rides the
else/arm branch: it skips the let-needs-a-body guard (the let HAS
its body — the retry block), and it never reads as a pipe for
alignment purposes.

The keyword-completion inventory tripwire fired exactly as designed
— retry/poll/until arrived and the suite refused them until the
completion decision was recorded (offered, the for/do shape). The
POSITIONS sweep: retry/poll are expression forms — legal at
statement (unit rule applies to the bool form's unit), let-RHS
(top-level and block), and paren interior (pinned via the
computed-options and worked examples); excluded from command-arg
splices and interp holes by the scalar law (a Retry value is a
record — nothing new to rule); sigil interiors are command grammar
and take no expression pins.

The rulings whose reasoning is worth one line beyond the row: raises
PROPAGATE because catching would make retry a try/catch in disguise
(the not-doing list has try/catch, and the reifiers already turn
command failure into data); unbounded is UNREPRESENTABLE rather
than refused because no key spells forever — a better property than
a guard; and the cancellable wait landed with timeout= as its first
customer (poll timeout=80ms with a 10s interval exhausts in
milliseconds — wall-clock pinned, unit and e2e).

Tree-sitter: keywords added to all three grammars; regeneration
churned the generated boilerplate (cli version drift — ABI 14 held);
the retry sample parses 0 ERROR and the showcase's ERROR is
PRE-EXISTING (verified against the pre-change grammar): it is ONE
node, the README's known {{literal braces}} nit — the "2" first
reported was a grep artifact counting tree-sitter's summary line,
and the README entry carried the same stale count plus a stale line
number; both corrected, no new nit filed.
The stash used for that verification collided with the working tree
on pop — recovered cleanly, but the lesson stands: verify-by-stash
in a tree full of session work is the wrong tool; a worktree would
have been right.

## tasks underneath: the fan-out fits the domain (2026-08-04)

Two sessions on one branch, split exactly where the bless drew the
line: the rekeying was the risk, and it landed provably inert before
anything about scheduling moved.

Session 1 was two lines of mechanism and a day of proof surface:
ThreadLocal → AsyncLocal for cwd and the env overlay stack. The
semantics wanted (a scope belongs to its arm, wherever the arm runs)
is AsyncLocal's exact contract, and it degrades correctly to plain
threads because every pmap arm enters/exits per invocation — pool
reuse cannot leak a scope. The AOT probe: three publish warnings,
which is the pre-existing baseline from the config spike — zero new.
Zero pin movement, including the load-bearing within-cd-in-piter pin,
plus both scope disciplines live-probed on the AOT binary.

Session 2 replaced Parallel.For with a dedicated-thread worker pool
(LongRunning) under a ceiling of 64. The wall-clock tells the story:
128 blocking arms at 100ms each now finish in about two rounds where
ProcessorCount sizing needed sixteen; the e2e probe runs 100 arms
each SPAWNING a child weir in ~470ms with order and the parent cwd
held. One contract moved, deliberately and recorded: Parallel.For
stopped launching iterations after a failure and surfaced a
nondeterministic first error; now every arm runs ("all arms
complete" is literally true) and the first error BY INPUT ORDER
rethrows — deterministic, pinned with distinguishable messages.

The bless's "also worth fixing" item was half-done on arrival: the
negative Duration.sleep raise landed as the dedent session's rider;
this session added only the granularity doc line (a small sleep is a
floor, not a promise — ~15ms Windows, ~1ms Linux).

Unblocked and recorded, not built: Seq.pfirst is now the canonical
Task.WhenAny + CancellationTokenSource + tree-kill shape; retry/poll
get cancellable waits. Duration.sleep stays Thread.Sleep — session 2
surfaced no reason to move a sibling.

## the dedent correct-join (2026-08-04)

The bless made the enumeration the first deliverable and the
stop-if-empty gate real. It was not empty, and the matrix was
sharper than expected: block-lets, if/match, and col-0 continuations
ALREADY resolved dedents (Lets and Compounds carry levels); `within`
and `for` bodies owned no level bookkeeping at all — a statement
after either, at any interior level, hit the floor, and even
within-in-within could not continue its own outer body. One shape
(within under an if) worked purely by riding the if's close. The
corpus was clean only because the district-retirement migration
restructured around the limitation — the workarounds were the
evidence.

The landed fix is smaller than the plan sized. The plan sketched a
statement-level stack in Pend and worried it would read as a fifth
alignment stack; the fix adds NO stack — within/for heads became two
more members of the existing compound machinery (one classifier
expression: OpensCompound). The reasoning that got there: in
sentinel-flattened text a dedent-JOIN alone cannot work — the parser
cannot see columns, so a within body would eat the joined sibling as
its next statement. The block must close WRAPPED, and
close-and-wrap-on-sibling is precisely what compounds already do for
if/match, with alignment, spans, and deepest-first pops paid for.
The ambiguity ruling (innermost wins) is therefore inherited, not
implemented.

What the probes re-found, recorded but not this session's business:
an interior block ending in a bare command still owes a !()-tail
(the arming rule's final-position capture under a unit demand — the
migration's own convention). And one probe briefly blamed the join
for a splice-default error that was its own: a parameter-sourced
interp hole defaults to string, dedent or no dedent.

A rider landed in-tree mid-session (background agent):
Duration.sleep on a NEGATIVE duration now raises located, naming the
value in the Show shape — it was a silent no-op by accident of the
`> 0L` guard, and the deadline idiom (sleep (deadline - now)) must
not silently skip a past deadline; zero is pinned immediate-return.
Go's return-immediately alternative is a one-arm flip if ever
preferred; the ruling is a check-and-teach, the reject-don't-guess
posture.

Zero movement: 1021 pre-existing pins untouched, the four alignment
stacks untouched (Compounds gained members, not a sibling — the
census trigger should not fire), fmt is IDENTITY on every
newly-joining shape, 30k strict-span deep cases green. 1027 unit /
e2e +1 block (the git-subrepo shape joins on AOT, the floor stays).

## floats session 2: the boundaries, the fuzzer, and a rename rider (2026-08-04)

The two boundaries the cost report called clean WERE clean, confirmed
not redesigned: kindOk's integer ⊆ number needed nothing, and the
reverse-Norway machinery quoted "1.5"-the-string before floats
existed. The two that needed design got their rulings: integer-shaped
numbers WIDEN into float fields at json/yaml (a parse, not weir
arithmetic — stated in SEMANTICS so it does not read as an exception
to no-implicit-widening), and .inf/.nan reject on the way in with the
finite-only teaching (there is no value to read into).

One deviation from the bless, recorded: "a float-shaped number into
an int field stays a rejection with today's message" — today's
message was a RAW FormatException leak ("One of the identified items
was in an invalid format"). Pinning that would enshrine an accident;
it teaches "declare it float" instead.

One judgment call the bless's wording forced: the YFloat node. The
bless says the district splice path admits floats, but the district
tree is TYPED Yaml — a raw VFloat inside it would be unsound, and
the literal case decided it: `cpu: 1.5` in a district self-typed
YStr and rendered QUOTED ("1.5") — wrong for any manifest that means
a number. YFloat joined the prelude union (Yaml was already non-Eq
via its seq payloads, so no class movement), and unquoted
float-shaped literals self-type like ints always did.

The fuzzer's expect-red prediction did not fire: 30k fresh-seed
cases green on the first run — exact-quarter literals (n/4.0) made
every render deterministic, which is the flakiness the prediction
priced. The Eq cost is stated in GRAMMAR.md's denominator: floats
never join the CCmp "==" arms; they are ==-free, not
comparison-free (Ord prints generate). Division stays out of the
production — a generated zero divisor would raise by our own law.

Mid-session, two background agents landed work in this same tree:
five showcase review fixes (folded, plus the blessed --rate 0.5
flag), and the Duration.toS/toMs → toSeconds/toMillis rename rider
(members, three teaching messages). A did-you-mean PREFIX arm was
landed and REVERTED within the hour on the user's ruling — no hint
machinery: a stale toS/toMs spelling gets the plain unknown-member
error, and the rename row records the widening as tried-and-
unshipped so the question stays closed. The ledger's rows sit in
chronological order: the boundary row's member names predate the
rename row that follows it.

## trailing comments (2026-08-04)

The session's shape surprised twice, both in the good direction.

**The scanner already existed and already implemented the blessed
rule.** stripComment IS option B — whitespace-preceded `//` outside
strings, with the URL receipt cited in its own comment, riding the
one string-state machine (all four string forms; holes sit inside
the string extent, so holes-don't-host-comments is true BY
CONSTRUCTION). The feature was one call site away the whole time:
the assembler now strips per PHYSICAL line, before joining — masked
yaml content excluded (bytes), comment-ONLY lines excluded (their
class carries transparency semantics a blanked line would lose:
blankSinceHead is the difference). `-e` gained the same one-line
strip; the REPL already stripped, so the divergence closed from the
script side. The grammars were AHEAD of the language: micro's region
is `(^|[ \t])//` with the URL note, tree-sitter approximates the
boundary — zero grammar changes owed.

**The new fuzz metamorphic (append ` // fuzz tail N` to random code
lines; output must not change) found a real bug on its first deep
look**: a trailing comment on a yaml district HEAD line broke head
detection — the mask ran on RAW lines and missed the commented head.
The cut now runs twice: once over everything to FIND the content
regions, once — content excluded — for real. The commented-head case
is pinned (the head strips and still opens; the body keeps `a // b`
as bytes).

Decisions made in writing, per the bless: option B (a command line's
own trailing comment is legal; the cost — a bare unquoted `//` argv
word reads as a comment — is stated with its escape hatch, quote
it); holes do not host comments (to-EOL cannot nest in delimited;
located error pinned); trailing `///` is an ordinary comment, docs
stay attachment-only; fmt PRESERVES trailing comments and alignment
verbatim (all three passes probed: respace touches code and
reattaches the tail — qualifyFile's Substring reattachment predates
the feature and was already correct — and the match reflow moves the
arm with its comment).

One contract change rippled: stripComment now TrimEnds ON CUT (the
agreement pin caught the REPL/script byte-difference; district heads
need EndsWith to hold). Five pins retargeted to the new contract;
the colorizer paints from the `//` itself so the alignment gap stays
uncolored. 1015 unit / 22 fuzz (the new metamorphic) / 3 fresh 10k
strict-span seeds / pty color+multiline+quality / e2e incl. the
hostile-byte fixture and a new trailing block / full ritual green.

## showcase refresh: the review's nine items (2026-08-04)

All nine actioned on the floats branch (the tour needed Duration and
floats anyway, so the freshness complaint and the branch coincide).
The substantive ones: the files section now runs INSIDE `within tmp`
(the scope creates and deletes — two File.delete cleanup lines died;
the env file keeps one as the member's demo), `within cd` joins its
family after `within env`, and Cli gained `[<Default 30s>] timeout:
Duration` feeding a Duration/floats section — `budget 30s, half 15s,
30.0 seconds` demonstrates the literal, the algebra, `Duration.toS`,
and the integral-float rendering in one line. The `== ` demo lost its
unreachable `else "?"`; the seq-print spelling unified with the
equivalence stated once; the shadow section's load-bearing LAST
position is now said in the file; the dead match arms name their env
var; `print ()` gained the comment its section title was owed (it
prints NOTHING — verified zero bytes; the reviewer's empty-line
premise was wrong but the code was under-documented).

A finding the edit surfaced: **trailing comments after code are not a
script feature** — `let x = 5 // note` is a parse error in a script.
stripComment exists and is unit-tested, but its callers are Fmt, the
REPL, and doc-name extraction only; the execution pipeline never
strips, and CANNOT safely: command lines carry bare argv words that
legitimately contain `//` (`http://a`, `--format=a//b`), and
quote-awareness alone cannot tell them from a comment. Full-line
comments are the script law BY NECESSITY, not omission. Worth a
SKILL/GUIDE sentence some session (the REPL divergence too); not
landed here — the showcase uses full-line comments throughout, as it
already did.

[AMENDED same day, by the trailing-comments bless: "BY NECESSITY"
was true of the PRE-PASS and overstated about the language — the
whitespace-preceded rule (yaml's own `#` law) distinguishes
`http://a` from ` // note` without parsing, and stripComment already
implemented exactly that rule for the REPL. The session below landed
it everywhere.]

## floats, finite-only — session 1 (2026-08-04)

The design held everywhere the report predicted, and the two report
items the bless asked to confirm both resolved cleanly:

**The interp-show zero-edit property: CONFIRMED.** `$"{100.0 * 7.0 /
8.0}"` rendered `pct=87.5` with zero interpolation changes — the
hole asked the Show class and the class said yes. The property's
first customer arrived before `Size`, as the rider hoped. Pinned as
its own test naming the property.

**The Ty-addition ritual: both halves run.** FS0025 grep came back
CLEAN this time (the Duration session's arms left wildcards in the
right places), the tyVars arm went in with the first Types.fs batch
(the startup-death lesson applied preemptively), and the binary ran
before probes. What the compiler could NOT catch: `print`'s VALUE
side (printImpl's scalar group is a pattern the checker admits
types into — check passed, eval hit unreachable). The probe caught
it; the lesson is that value-side scalar GROUPS (print/printerr/
scalarString) are the FS0025-invisible sites of a scalar addition.

**What the Eq exclusion made awkward in practice — one real thing:**
the round-trip pin itself. `Float.parse (show x) == x` is the
natural spelling and it is a check error by our own rule. The pinned
spelling is `Float.near (Float.parse (show x)) x 0.0` — near with
zero epsilon IS exact equality, so the idiom covers the exact case
too. Worth knowing before someone asks.

Unplanned finds, both landed:
- **`-30s` was broken** (prefix minus desugars to `0 - e`, mixing
  int with Duration) — the identical seam floats hit, so negAtom now
  FOLDS the minus into float and Duration literals. Pinned.
- **`Float.toInt` shadowed bare `toInt`** (Str's) through the
  bare-alias flattening — Float now joins Option in the
  never-flattens filter. Pinned (`toInt "42"` stays Str's).
- **The oracle refereed the equality pin and won**: my first
  spelling claimed F# accepts `0.1 == 0.2` — it does not (`==` is
  weir's; F#'s is `=`). pinT with both spellings landed. The
  divergence machinery caught a wrong claim about F# before it
  entered the ledger, which is exactly its job.

Deferred with reasons, not silently: fuzz float production waits for
session 2 — the generator's typed int/str core wires literals into
`CCmp "=="` arms, which floats cannot join (Eq excluded); giving
floats their own comparison-free productions is a design change to
the grammar, not a mechanical add. Boundaries (json/yaml/Args/Env)
are session 2 by the bless; float fields reject with today's
messages — verified nothing is half-admitted.

## floats: the cost analysis (2026-08-04, zero code)

Report at plans/PLAN-floats-cost.md; every decision priced with a
pointer, both DECISIONS row texts written. Three things the pricing
turned up that the bless did not predict:

1. **Finite-only floats dissolve the two worst costs.** weir
   arithmetic is already checked, not wrap-silent (`checkedInt`,
   Eval.fs:196, raises on overflow; `1 / 0` already raises). A
   checkedFloat that raises on non-finite results makes NaN and
   Infinity unrepresentable — Eq stays reflexive, Ord becomes
   genuinely total (no `<`-vs-sortBy divergence), show never faces
   special values, and to-json cannot emit invalid tokens. The
   report's recommendations all assume it.

2. **Show admission is not a choice.** Since interp-show, holes
   consult the class and print follows the scalar family — a
   non-Show float cannot be printed or interpolated. Floats would be
   the zero-edit property's first confirmation, before Size.

3. **The schema seam the bless worried about is clean.** kindOk
   already encodes integer ⊆ number (Contracts.fs:575), and the
   reverse-Norway law already quotes `"1.5"`/`"1e5"` strings
   (Yaml.fs:584) — the yaml quoting machinery was built for exactly
   this adjacency and does not weaken.

Corpus sweep for section 5: zero ratio/percentage/average sites in
examples/ and tools/ today — every numeric shape is a Seq.length
count or integer ms; ci/timing.sh divides ns→ms in shell. Stated as
the family-shaped hole it is, not as evidence against.

The Eq recommendation (exclude, teach the eps idiom) is the one most
likely to draw a veto — it is the honest reading of reject-don't-
guess, but it will surprise anyone arriving from pwsh/nushell. The
row texts cover both worlds; the ruling is one line either way.

## interpolation holes consult Show, not a list (2026-08-04, rider)

The diagnosis the plan asked for first: **the arm enumerated.**
`checkScalarSplice` hardcoded string/int/bool, and the deferred-var
discharge in `resolvePendingSplices` repeated the same list — the
policy existed twice and neither asked `Cls.Show`, which was one
`demand` call away. The fix was the clean substitution: one
`spliceAdmit` law shared by both paths; the hole arm demands Show
with its own Describe. The class exposed everything the arm needed —
nothing was missing from it.

**The property, stated for `Size` to confirm:** a new Show-admissible
type needs no edit at the hole — the arm asks the class. When Size
lands, `$"{sz}"` should render with zero interpolation changes; if it
does not, this property broke and the report was wrong.

The splice question was decided separately, outcome (1) — split. A
hole renders for a human; a command argument becomes an argv word for
a program, and `sleep 1h30m` working on GNU sleep but not elsewhere
is exactly the semantic bet weir refuses. The Duration splice arm
teaches both deliberate spellings; both check clean, pinned.

Deliberate flips, each retargeted or converted rather than deleted:
records, seqs, and unit now render in holes (show's form — the
update-in-hole rejection pin became a render pin with the exact
string; unit's exclusion pin became an admission pin). The two
rejection pins that guarded the rule now guard it with functions
(eager and deferred paths). The bare top-level string hole stays RAW
— eval renders `VStr` as the value itself and everything else through
`formatValue`, which is precisely show's renderer. Deferred-hole
defaulting untouched: an unresolved hole var still defaults to string
([D:splice-default-last] — the pipe-into-lambda pins stayed green).

## duration — time as a type, decimals only at the edges (2026-08-04)

The plan executed as blessed; the law held everywhere: a Duration is
an integer count of milliseconds, and no float exists in the
implementation — `formatDuration`/`parseDurationMs` are digit
manipulation (pow10 division, sub-ms rejection), and even
`Duration.sleep` runs on integer ticks after a first draft reached
for `TimeSpan.FromMilliseconds(float n)` and got caught by the
grep-clean bar. The no-floats divergence row now points here: the
decimal cases that actually arrive are time.

The sharpest pin (command-mode `30s` stays an argv word) was true by
construction — the literal lives in `intLit`, command arguments never
visit it — and is pinned at the parse level (`expectCmd "echo 30s"`)
plus on the AOT binary in e2e. The class widening (TDur into
Eq/Show/Ord) cost exactly the predicted shape: one arm per class
table, plus every exhaustive `Ty`/`AttrArg` match in the codebase —
the compiler found them all as FS0025 warnings except `tyVars` in
Types.fs, which had no wildcard and DIED AT STARTUP (Builtins' static
init generalizes schemes). Lesson repeated from the last widening
attempt: after adding a Ty case, grep the build output for FS0025
AND run the binary once before probing behavior.

Unplanned but landed: the compound-literal teaching. `1m30s` lexed as
`1m` applied to `30s` and produced "takes at most 1 argument, got 2"
— the doc-runner hit it in my own `Duration.toMs 1m30s` example
before any user could. The suffix parser now captures a digit-follow
instead of rejecting it (`lookAhead` inside the `attempt`, the fatal
raised OUTSIDE it — `attempt` un-fatals anything inside), and the
error names both spellings: add single units or `Duration.parse`.
Letter-follow still backtracks, so `30si` stays an ordinary unbound.

Reported, not widened: interpolation holes and command-argument
splices share one rule (`checkScalarSplice`) and stay scalar-exact —
a Duration in a hole errors; the spelling is `$"took {show elapsed}"`.
Admitting TDur there is one arm and arguably follows from Show, but
it was not in the blessed scope and the plan's text is gone
(transcript compacted), so it waits for a ruling. Same posture as
the JSON park, which landed as planned: a teaching rejection naming
`Duration.toMs` into an int field.

One session-ritual note: the [<Default 30s>] attr literal needed its
own lexing (attrArgLit is a separate tiny parser from intLit — easy
to forget it exists), and Args.load pins need the Self module in the
test env (script-only teaching fires otherwise).

Ritual: 991 unit / e2e green (new duration block: defaults rest,
both boundaries parse, rejection locates, argv word) / 21 fuzz +
3 fresh 10k deep seeds (lexer changed) / 68 doc blocks (+1: the
GUIDE timeout idiom) / 158 oracle / timing flat.

## the filesystem family — File completed, Dir created (2026-08-04)

Ten members, six rulings, all made in the plan and executed as
written. The one that will read as inconsistent without its
distinction, stated in DECISIONS: destinations REFUSE to overwrite
(reject-don't-guess; delete-first is the overwriting spelling) while
Dir.create is idempotent — because an existing directory IS create's
post-condition, and an existing copy destination is data the caller
did not ask to destroy. Dir.deleteAll is the visibly-named
destructive one, shipped (scratch-tree scripts need it; rm -rf is
worse in every way), its doc's first line all caps about what it
does.

THE PRODUCTS: the four error shapes pinned each naming its path;
Dir.list full-path/sorted/both-kinds/eager; glob-then-delete as the
composition e2e; and the within-tmp DOUBLE-DELETE pin — a block that
deleteAlls its own binder exits clean, promoting the scope finally's
vanished-dir tolerance from an implementation accident to a pinned
product. The showcase's File.delete judgment row (the gap's first
symptom) CLOSED with real cleanup — the scratch files it used to
leave in the temp root now go.

ONE HARNESS SHAPE learned: doc examples are single-LINE (the runner
joins), so the filesystem examples became self-cleaning let-in + ';'
chains (`let d = Path.newTempDir () in (…) ; File.delete … ;
Dir.delete d`) — which incidentally makes each example a tiny
composition demo of the family it documents.

## gap audit session A, the remainder (2026-08-04)

Six members, every one receipt-cited, every decision made in the
plan and executed as written: windowed (lazy with its pull-count
pin; empty-on-short; raising n<=0), last/tryLast (the X/tryX rule's
newest instance, "last: empty sequence" on the head-message model,
both forcing — the forcing family's fifth customer), Option.iter
(None-runs-nothing proven by a counter's absence), Option.orElse
(fallback-first as ruled — pipes data-last; the orElseWith twin
parked on the defaultWith precedent), and the temp pair — tempRoot
the pure query, newTempDir the visible creator matching `within
tmp`'s weir-tmp- spelling exactly, with the cleanup SIGNPOST
pointing at `within tmp` rather than the original plan's sweeper
shrug.

THE FILESYSTEM-MEMBERS GAP, reported as the plan asked: File is
read/write/append/exists ONLY — no delete, no copy, no move — and
there is NO Dir module at all, which makes newTempDir weir's only
directory-creating operation. The gap audit's receipt pass DID
inventory File/Path (the counts are in its report) but its
recommendations were receipt-driven, so a family-shaped hole with no
citation went unstated. A filesystem-members audit is its own item;
the showcase's File.delete judgment row was the first symptom.

One fixture lesson: the side-effect probe needed a TYPED name —
runWith overrides values, not types, so the counter rode print's
own scheme (whose bare-value position defaults to string; the
payload followed).

## the district retires — five movements, one long day (2026-08-04)

THE SUBTRACTION (M1): the `!` and `!name` districts are gone.
Mechanically small — the districts were sugar over the !() sigil at
assembly (markerOpener), so two DU cases died and the compiler found
every consumer. The retired spelling TEACHES (checked up front over
every non-content line; yaml bodies are bytes). The sigil forms stay
with the retention stated. The migrations, named: showcase ×3,
jira-branch ×1, git-subrepo ×6, bicep ×2, fuzz.weir ×1, GUIDE ×2
blocks, 10 e2e blocks, 17 unit pins, the LSP fixture. The subtle
migration class: groups that ENDED function bodies made those
functions unit; bare migration flips them to seq<string> — the
!()-tail is the unit assertion (three sites). The fuzzer's
production RETARGETED rather than deleted: it now generates the
arming spelling, and the per-line-!() transform became the
bare-vs-sigil arming equivalence — coverage preserved, meaning
upgraded.

THE FLOOR (M2): the silent swallow is dead. A line dedenting below
the open block that aligns with no enclosing level now ERRORS with a
located teaching instead of space-joining as the previous command's
argv. The strict-first approach earned its keep: the full suite
found exactly ONE legitimate exemption class (open lambdas/brackets/
parens take dedented continuations). The correct-join goal is
deferred WITH its size — a stack of open statement levels in Pend —
its own session. Poetic justice: the floor BIT during this session's
own migration (post-scope lets absorbed as argv in git-subrepo),
the bug demonstrating itself while being fixed. Four shapes pinned.

THE HINT PAIR (M3+M5): a bare command in a fragment now says
"wrap it: $(sh …)" beside did-you-mean; a discarded $(cmd) says
"drop the $( ) to run it as a command" — the same principle in both
directions, each fired only where the shape is unambiguous. M5 took
DIRECTION B as ruled: $() means capture EVERYWHERE, via a new
ECapture marker (erased at check, invisible to armTail by
construction — no armTail special case added, one removed). Zero
corpus $()-statements existed. Five parse-shape pins wrapped.

M4, REPORTED: if-conditions keep bind-first as the canonical
(GUIDE-documented) spelling — the inline form wants a then-stop argv
context + a reifier-follower addition + an acceptance gate, more
than the tuple-comma entry-point swap; sized for its own session if
wanted. Record fields: NO, with the reason in the table row — a
field's RHS is newline-bounded, and bare commands there would
multiply the dedent-swallow class the floor just killed.

ONE RESTORATION along the way: the interior exitCode discard kept
its tailored teaching ("bind it (let rc = …)") via a check-side
spine test — the district cell died but its text was better than the
generic if-unit error that replaced it.

## within cd, within env — the kinds slot in, the table lands (2026-08-04)

The inheritance claim held to the letter: each kind cost one parser
match case and one eval arm. The node generalized for the asymmetry
that is the form's point — tmp PRODUCES a binder, cd/env CONSUME one
ATOM argument (a greedy expression would swallow the space-joined
first statement; compounds parenthesize).

`cd` is the Cwd axis arriving where three predictions said it would.
Nested relative scopes compose (build then sub lands in build/sub),
restore is try/finally, a missing path errors AT ENTRY with the
resolved absolute path and the block never runs (marker-pinned), and
the piter interaction — the one place two cwd mechanisms meet —
nests correctly: each worker's scope rides its own forked cwd,
parent untouched. The interrupt gap inherits tmp's verdict and is
HARMLESS here (the process is gone; nothing observes its cwd) —
stated, because the distinction makes tmp's gap the real one.

`env` grew the one genuinely new mechanism: an ambient overlay STACK
in Session with cwd's exact locality discipline (root for the main
thread, fork-on-first-push for workers, cleared at exitWorker).
Spawns apply ambient layers outer-first UNDER any explicit sigil
env, so inner and explicit win at Proc's last-wins application — the
collision pin: inner wins the key, outer keys survive. Env.load is
untouched; a scope around pure work is legal-and-pointless, allowed.

`!env`'S FATE, reported not acted: the line-end !ev DISTRICT variant
is fully covered (retire-candidate at its own bless, which also
unblocks the !-district deletion the arming session reported). The
$e()/!e() SIGIL forms RETAIN: single-command overlays and
fragment-position uses have no block spelling while
parens-spanning-lines stay parked. Census: bicep-deploy ×4,
git-subrepo ×1, 13 e2e pin references.

THE TABLE EARNED ITS PROBE-FIRST ORDER: the draft rule died twice on
observation. The if-condition takes NEITHER a bare chain nor a
$()-piped one (both error; bind the verdict first — `let ok = cmd |
succeeds` then `if ok`), and record fields refuse bare commands
despite being whole value-slots. The rule the observations support:
bare commands live at STATEMENT positions and let-RHS/block-final
values; in every expression FRAGMENT the wrapper is mandatory, and
the observed error is the unbound-variable teaching on the head
word (a wrap-it hint there is a candidate improvement, recorded not
done). One asymmetry recorded without action: top-level $()
statements ARM (armTail) while interior $() elements seq-unit-error.

TWO FINDINGS FOR THE LEDGER: (1) pre-existing, sized — a COMMAND
line as the first statement after DEDENTING from a nested block
mis-joins (within: silently absorbed as argv of the previous
command; block-lets: a parse error) — the assembler's dedent
reprocessing wants its own session; until then, put post-scope
statements before nested blocks or make them non-command. (2) The
grammars needed NOTHING new (confirmed, not assumed — the keyword
and block landed with tmp; kinds are ordinary idents).

## within tmp — the first scope, on two sessions' shoulders (2026-08-03)

The form the prerequisites were FOR: `within tmp dir` + an ordinary
expression block. Because blocks yield (the probe) and commands arm
(the arming rule), the whole mode table came free — expression
position yields the body's value, statement position demands unit
through the EXISTING discard rule. One addition proved necessary the
moment the interrupt probe ran: statement-position scopes with a
commandish TAIL hit the discard error, so armTail now reaches
through EWithin and ELet bodies at the statement gate — the same
pre-pass if-then already had, extended two constructors.

THE ASSEMBLER was the real design surface, exactly as the probe
sessions suggested: `within tmp dir` ends with arbitrary argument
words, so no suffix rule can see it — isWithinHead is the
yaml-marker precedent (a lexical classifier, optionally behind
`let name =`) feeding dangleOpensBlock; the head SPACE-joins its
first body statement (the then-convention) and siblings arrive
sentineled, which the parser's optional-separator spelling absorbs.
Also found the hard way: TWO OPPs carry TermParser lists (segment
and main) — registering in one produced a keyword that parsed
nowhere; the expecting-list's missing 'within' was the tell.

THE PINS: the receipt (a command-heavy block yielding an exact
sha256sum-verified digest), the raise-path headline (dir removed AND
the raise propagates, exit nonzero), nested (both dirs removed,
inner finally first by construction), statement-position discard for
non-command non-unit bodies (the teaching text untouched), the
perverse binder (`within tmp git` — the binding beats PATH; the
patLeafNames family's fifth site, pinned on arrival rather than
discovered), a reifier body, fmt roundtrip. Unit: parse shape,
body-type flow, unknown-kind teaching (`within lock` names the
shipped kind), the reserved-word gate, the completion inventory
(within OFFERED, the for/do precedent).

THE INTERRUPT VERDICT, verified not assumed: SIGINT kills the
process without running managed finallys — the dir SURVIVES Ctrl+C.
Stated as the gap per the plan (the OS sweeper's problem; weir-tmp-
prefixed leftovers are identifiable), not engineered around. The
tree-kill discipline cleans child PROCESSES; it never promised
managed cleanup.

ORDER NOTE for session A's remainder: within landed FIRST — the
tempRoot/newTempDir members follow this spelling when they land.
FOR cd/env TO INHERIT: the kind arm is one match case in the parser
+ one TEWithin eval arm; the assembler classifier and the binder
extension are kind-agnostic already. Nothing the form made awkward
surfaced; the one caution to carry: eval's cleanup is try/finally —
cd's restore and env's un-overlay ride the same shape.

## commands as interior statements — the arming rule, unified (2026-08-03)

The probe's fork resolved by making the rule simpler: a command line
in interior-statement position is an EFFECT; final/value position
stays capture; top-level becomes an instance instead of a special
case. Everything the plan asked, plus two finds of its own.

THE QUESTION-FIRST ORDER PAID TWICE. (1) The reifier gap diagnosed
exactly as the tuple-comma class predicted: reifierEnd's follower
set (pipe, ')', eof, in) lacked the STATEMENT SEPARATOR, so
`| orFail "m"` as an interior statement demoted to a bareword stage
— one lookahead alternative added, and `git push | orFail "no"`
works in scope bodies. (2) The !-district enumeration probe found a
for/do seam nobody knew: the single-chain command body ate line one
and STRANDED the remaining siblings outside the binder's scope
(`unbound variable 's'`) — the body now yields to the sequence when
a sibling follows, and multi-line for bodies need no district.

THE IMPLEMENTATION HAS TWO HALVES because the plan's own if-example
demanded it: `if force then / git reset / git clean` arms its FINAL
line, which fold-position cannot see — so parse-time arming covers
non-final elements (the for/do desugar generalized through seqExpr's
new command element) and the CHECK DIRECTION covers finals under a
unit demand (an armTail pre-pass on if-then keeps the teaching text;
a check-mode ESeq rule propagates the direction; commandish-under-
TUnit arms lambda bodies). The first cut LEAKED — parens interiors
ride seqExpr, so `(scriptPath |> Path.dir)` phantom-commanded under
the assume resolver and ate the Self teaching (a unit pin caught it
within minutes) — plain parens now gate the element off, lambda
bodies re-enable through them, because `Seq.iter (fun f -> git add
$f)` is the idiom and it lives inside parens.

THE CARVE-OUT held without code: the top-level path never went
through seqExpr, so a script's last command still streams and raises
— pinned anyway, because the one place nobody wants a behaviour
change deserves its pin. Raise timings pinned both ways. Sharp case
4 decided: NO warning for a non-final command that would have been a
capture — it was never bound, and the armed effect is what the line
says on the new reading.

THE ! DISTRICT'S FATE, reported not acted: if-then bodies and
multi-line for bodies no longer need it; top-level runs keep only
the command-ONLY-territory guarantee; the ENV variants (!ev) are the
part nothing replaces until within-env lands. Plain ! is a
deletable-candidate for its own bless once this rule settles. The
corpus reads fine as-is (districts still work; no churn per the
bar).

FLIPPED PINS, each named in place: the sibling-sentinel ACCEPTANCE
shape (the non-final command is now armed EPipe), and the
command-first-body rejection twins (unit + e2e) — the body now
CHECKS, and the at-the-head/no-EOF-dump quality property moved to a
non-command fixture, where the statement rule still holds it.

THE CORPUS SWEEP EARNED ITS PLACE IN THE RITUAL: repo-report.weir
broke under the new rule — `| t :: _ -> t` read the arm BINDER t as
a phantom command (warning + seq<string> mismatch), because arm
bodies never got the bindings-beat-PATH resolver extension that
lambda params and let-in names always had; the new command element
made the gap observable. Fixed at the missing sites (match-arm
bodies via the pattern's leaf names, for bodies via the binder) —
a shared patLeafNames walker, the lambda treatment completed.
Unit-pinned; the corpus checks clean again.

RE-LEARNED, embarrassingly: `grep -c | && publish` short-circuited
AGAIN (count 0 exits 1) and a probe ran against yesterday's binary
mid-session — same trap as the squiggle session, caught faster this
time. The spelling that survives review is `grep -c … ; ./publish.sh`
on separate lines.

## probe: does a block yield its last expression? (2026-08-03)

Zero code; the load-bearing unknown under the `within`-scopes
design, answered with citations. The three questions separately, as
the plan required — and they DO have different answers.

Q1 — BLOCK-LET BODIES: YES, generically, and the statement rule
composes correctly. The machinery is ESeq: the parser folds a
block's statements right-nested (foldSeqExpr, Parser.fs:1637; wired
via seqExprRef at 1658 — the SAME production serves block-let RHS,
let-in bodies, and if bodies); the checker demands unit of every
non-final statement and types the whole as the last expression
(Check.fs:1236 — the non-unit first errors seq-unit); eval discards
firsts and returns the last (Eval.fs:1438). Probes, verbatim:
`let x = / let a = 1 / a + 1` → 2. `let y = / print "side" / 42` →
side, then 42 — the LAST line is exempt from the statement rule,
which is exactly the "yields" bar. `let y = / 41 / 42` →
"error [seq-unit]: a sequenced expression must be unit; this one is
int — bind it or print it" at the 41. A block that yields AND
discard-errors correctly on non-final lines: usable as-is.

Q2 — YAML DISTRICTS: the value is the DISTRICT'S OWN CONSTRUCTION,
not block-yield — the crux the plan flagged, confirmed. A yaml
district's body is VERBATIM template bytes behind the sentinel
(JYamlLine, Script.fs:709: "sentinel + rel spaces + VERBATIM line"),
parsed by parseTplBlock (Parser.fs:1297) into the EYaml AST node
(Ast.fs:72) — the body is never statements at all, so it is NOT
evidence for generic block-yield. (It is not evidence against it
either; Q1's machinery simply never runs here.)

Q3 — `!` DISTRICTS: unit BY CONSTRUCTION, not by discard — and the
distinction has teeth. District lines ride the ordinary ESeq
machinery (they are statements in the enclosing body), but every
line is ARMED as a command effect (chain |> print = unit), and the
grammar REFUSES anything else: a bare `42` as a district line is a
parse error ("not an external command"), and districtLineCheck
(Script.fs:779) rejects let-heads with "district lines are commands;
bind values outside the block". So there is no value to discard —
the last line is a constructed unit. Probes: `!(echo hi)` in value
position binds unit; a district under a let RHS (via if-then) binds
unit through the if-unit rule.

THE VERDICT: **A, with one load-bearing caveat.** Generic block-yield
exists and is battle-pinned — IF `within` bodies are ordinary
expression blocks (the block-let shape), the expression-position
cells work for free and the scopes design needs no new machinery.
The caveat: A holds for the BLOCK shape only. If the scopes design
wants the DISTRICT spelling (a marker-line species like `!ev`, lines
armed as commands), then a value-yielding scope district is verdict
B, and the size is NOT an entry-point swap: the assembler species
arms lines as commands, so admitting a final expression line means a
new line classification in the district grammar plus the ESeq tail
wiring — a small session, bigger than the tuple-comma swaps because
the assembler is involved. Recommendation embedded in the finding:
design `within` bodies as expression blocks and inherit everything.

ADDENDUM (user review — two confirmations before the scopes plan is
written, and the first caught a REAL gap in the verdict's assumption,
the same class the yaml crux caught):

(1) BARE COMMANDS ARE NOT LEGAL INTERIOR STATEMENTS of an expression
block. `let x = / echo hi / 42` PARSES — but `echo hi` in expression
territory is the CAPTURE-typed chain (seq<string>), so seq-unit
rejects it ("must be unit; this one is seq<string>"). The top-level
statement arming (bare command = effect, prints, raises) does NOT
extend into expression blocks; the in-block spelling is the sigil —
`let x = / !(echo hi) / 42` → hi, 42 (probed). A reifier-ended chain
as an interior statement degrades too: `echo ok | orFail "broke"`
warned cmd-not-found on orFail and seq-unit-rejected — reifier
recognition rides the statement/let-RHS paths, not interior
expression statements (unsized observation, recorded not diagnosed).
CONSEQUENCE FOR `within`: verdict A survives for VALUE semantics but
an ergonomics fork opens — scope bodies (tmp/env/cd!) will be
overwhelmingly command effects, and the block shape makes users
write !(...) per line. The design must choose: (i) block-shaped body
(yield free; !(...) tax on effects), (ii) district-shaped body (bare
commands; the value case is the sized B session), or (iii) a new
arming rule for within bodies (top-level-like commands inside a
block — new rule, new surface). The plan should weigh (i) vs (ii)
knowing cd/tmp scopes are effect-heavy and env scopes may want
values out.

(2) MODE-FROM-POSITION FALLS OUT WITH NO RULE, confirmed: a non-unit
block result in statement position already hits the existing discard
error ("computes a int and discards it — bind it, or pipe it to
print"). `within` in statement position is just an expression block
whose value must be unit — the statement rule enforces it today;
nothing to design.

## hashing, base64, and the encoding law (2026-08-03)

The gap audit's session A, extended with base64 because it is the
same species (pure data transformation, coreutils shell-out, no
portable spelling — `base64` on Linux, `certutil -encode` on
Windows). THE PREREQUISITE CAME FIRST as the plan ordered: SEMANTICS
had NO encoding law (verified — the capture session pinned
behaviour, nobody stated the rule), so it is stated now, phrased
about BOUNDARIES not representation: UTF-8 at every boundary; BOM
detection where a stream may declare otherwise; strict contexts
reject invalid UTF-8 rather than manufacturing U+FFFD — corruption
must not wear a success.

Four members, every decision recorded: sha256 ONLY (md5 broken —
offering it invites use; the reason recorded so the question closes);
lowercase hex = sha256sum parity, pinned by exact digest INCLUDING a
non-ASCII input (the law under test: café's UTF-8 digest); toBase64
emits ONE unwrapped line — GNU's 76-column wrap and the -w0 tax do
not exist here, the rare case where weir's version is simply better
than the tool it replaces; fromBase64 raises on malformed AND on
valid-base64-of-non-text (//79 decodes to FF FE FD — bytes, not
text), tryFromBase64 None for both; unpadded input accepted.

THE ACCEPTANCE LANDED: corpus-mine.weir dropped `| sha256sum` and
THE CORPUS IS COREUTILS-FREE — the claim the audit said was worth
having. Numbers reproduce exactly (base: extracted=5 unique=3
kept=0, before and after); the filename hashes shift by feed's
trailing newline, stated, partition unaffected. The GUIDE gains the
Secret idiom (base64 into a yaml district — the k8s receipt's home,
now a 64th runnable doc block).

PARKED with triggers: URL-safe alphabet (JWT payload receipt;
visible naming, never a silent flip); File.sha256/File.toBase64
(the byte-type gap, stated once — and the park is NOT gated on a
language addition: a byte-oriented File member that never becomes a
weir value serves the receipt).

Remaining session-A receipts (windowed, last/tryLast, Option.iter/
orElse, Path.tempDir) stay for their own sitting.

## the library gap audit — six receipts, one dissolved fork (2026-08-03)

Analysis only, zero code; the report is plans/PLAN-library-gaps.md.
Three findings worth the journal:

THE INVENTORY PASS EARNED ITS ORDERING: most of the plan's own
judgment candidates already exist (take/skip/zip/sum/contains/
sortBy; the whole Str list except padding), and the plan's guess
that Str matters more inverted — Str is nearly COMPLETE at 19; the
real gaps are Seq-side plus one hashing hole.

THE CORPUS SWEEP IS THE HEADLINE: exactly ONE coreutils shell-out
remains in the entire corpus — `| sha256sum` in corpus-mine.weir:84
(and the portable showcase DELETED its digest demo for lack of any
portable spelling — two citations for one member). Own-the-data-
munging says that is a gap, not routing. The stranded ledger is
EMPTY, and every historical friction want (fold, contains, pairwise,
item, elif, …) landed — the receipt loop demonstrably works.

THE ORD FORK DISSOLVED AGAINST THE SOURCE: sortBy's scheme already
IS the key-projection option the plan called "the interesting one" —
Ord demanded on the KEY only, int/string/bool, no widening ever
needed for the receipt class; composite keys stay deliberately
refused (no-tuple-ord) with chained stable sortBy as the spelling.
min/max/minBy/maxBy/sort are ordinary unbuilt members, not a design
session — the launch lane loses one slot.

Sized: session A (all six receipts, self-justifying) and session B
(the first-hour judgment list, bless separately). Parity with F#'s
Seq is CLOSED by the not-needed list, reason per member.

## add validates before it writes — and the validator's first catch (2026-08-03)

Live use produced the rider's origin: a schema URL pointed at a
GitHub BLOB page, and `weir add schema` saved the HTML and LOCKED
it. The lockfile is the one artifact restore and verify TRUST — a
locked HTML page means a fresh clone faithfully re-materializes
garbage, and the old inert-warning even swallowed the unparseable
case (`with _ -> true`, "the loader teaches later") — the exact
disease. The principle now stated once for every contract kind:
add validates everything the checker will later require and writes
NOTHING if it cannot.

Six gates, in order, nothing on disk until all pass: content-type
(reported); JSON parse — and the failure OFFERS THE FIX, because the
blob-page mistake is the most likely user error and the raw-URL
rewrite is one the user may not know how to construct
(github.com/OWNER/REPO/blob/REF/P → raw.githubusercontent.com/…;
gitlab's /-/blob/ → /-/raw/, subgroups riding the greedy group);
schema-shaped (a package.json rejects here); THE SUBSET AT ADD TIME
(the $ref teaching lands where the user can act, and an
out-of-subset schema never reaches the lock); the inert warning
(unchanged, warn-and-proceed); then file + lock together with
delete-on-failure. Program.fs stopped pre-creating .weir/ — the
strongest pin holds: after ANY failed add the tree is byte-identical,
including no empty .weir directory. No incident residue in the repo
(the user's local .weir carries theirs; verify flags it, re-add
heals it).

THE GOOD NEWS LINE THE PLAN ASKED FOR: the bonus finding is the
schema validator's FIRST UNPROMPTED CATCH — three real errors in the
showcase's own district, on a real file nobody was testing.
`annotation` was a typo (the did-you-mean nailed it); `replicas` and
`rollout` were Deployment concepts on a Service — the fixture was
describing the wrong resource, and only a schema could have said so.
The district now spells a REAL Service (annotations map with the
"on" auto-quote demo riding a real field, selector + ports with the
int splice), and the showcase RUNS in e2e as a build gate: the
block-scalars session called it a composition test disguised as a
document, and a document that can be invalid without failing a build
is not that.

## bare commas, four positions — the match gap closes (2026-08-03)

The rider the portable showcase earned: `match a, b with` and
`| Some d, _ ->` did not parse, while `let a, b = …` has been pinned
since the bare-comma amendment. A reader who learns "bare commas
build tuples" was entitled to the third and fourth positions; F#
grants both; the ledger would have needed a row to refuse. Closing
was cheap — the plan's diagnose-first split found BOTH positions to
be entry-point swaps onto productions that already existed: the
scrutinee now parses `commaExpr` (the let-RHS's production — `with`
is reserved, so it terminates the chain cleanly; the one risk the
plan named, already absent), the arm parses `commaPats` (the
binders' production — `when` and `->` are not commas, so the guard
sits outside the tuple by construction). Both produce the SAME
ETuple/PTuple nodes the parenthesized spellings always made: zero
checker surface, zero precedence renegotiation, no stop-and-report.

The risk surface, pinned: command-mode commas untouched (the
existing e2e pin runs); `f a, b` remains `(f a), b` — the footgun
[D:bare-comma] imported knowingly is now PINNED so this fix cannot
have moved it; `[a, b]` stays one tuple element; the guard parses
outside the tuple. One finding the diagnosis surfaced for free: the
bare `for k, v in pairs` binder ALREADY WORKED — for's binder was
commaPats all along; the corpus just never used the bare form.
Confirmed and pinned.

Ledger outcome: CLOSED, no row — both shapes joined the oracle as
verdict-Same rows (158 now). SEMANTICS states the rule the rider
existed for: bare commas build tuples in BINDER, RHS, SCRUTINEE,
and PATTERN positions — one rule, four positions, no exceptions.
The showcase's parenthesized spellings stay as they are.

## the portable showcase — weir is its own coreutils (2026-08-03)

The user's find after the LSP landed: the showcase script cannot run
on Windows — echo, sh, printf, grep, mktemp, sha256sum are POSIX
tools, and weir's check-before-run posture means ONE unresolvable
head refuses the whole file. Coreutils on Windows are cmd BUILTINS:
there is nothing for a resolver to find, and no external-command set
exists on both platforms... except the two already required to be
there: `git`, and WEIR ITSELF.

So the showcase's externals are now weir and git ONLY. Two tiny
helpers ship beside it (each an extra example in its own right):
echo-args.weir (Self.args, one per line — the raw-argv echo the
splat demos need) and probe.weir (typed Args.load flags: --out/--err
to either stream, --code to exit, --upper over Self.stdin, --greet
from $GREETING — the process stunt double for every `sh -c "echo
out; exit 3"` shape). The tour LOST nothing: capture, effect sigils,
reifiers (succeeds/complete/exitCode/orFail), stdin piping, env
overlays and districts, for/do command bodies, pmap fan-out — all
demonstrated through child weirs, which also showcases composition
the old version never showed (Self.scriptPath-relative helper paths,
cd-proof). mktemp retired: unique FILES under the platform's own
temp root (EnvCfg gained TMPDIR/TEMP as Options; HOME became Option
— stock Windows has no HOME, so the old EnvCfg would have refused
load there anyway, a second Windows break waiting behind the first).
The ^-force demo improved in passing: shadow `weir` itself with a
let, then ^weir still reaches the binary — and prints the build
stamp. The sh -c escape hatch stays in the tour as PROSE (a named
head must resolve on YOUR platform; cmd /c is the Windows spelling).

Two weir-syntax findings while writing it (the oracle's territory):
a bare-comma tuple SCRUTINEE (`match a, b with`) does not parse —
parenthesize; bare-comma tuple PATTERNS in match arms need parens
too (`| (Some d, _) ->`). Both spelled correctly in the file; worth
remembering that let-destructure's bare-comma affordance does not
extend to match.

Verified: check clean, full run on Linux from a foreign cwd, fmt
idempotent, e2e battery green (the repo-scripts sweep now checks the
helpers too). The Windows run is the user's next hand-check; every
head in the file resolves there by construction.

## the squiggle blink — one document, two spellings (2026-08-03)

The Windows report: the LSP now starts (session 3's fix) but
diagnostics BLINK — squiggles appear once and clear. The cause needed
no debugger once stated: a URI-SPELLING SPLIT. Clients own their URI
spelling (VS Code sends file:///c%3A/…), and the server stored docs
under the client's string but RE-DERIVED publish URIs via pathToUri —
so one document became two strings. The diagnostic published under
the server's spelling; the every-open-doc-publishes-empty pass then
published 0 diags under the CLIENT's spelling; the client applied
both to the same document in order. Blink.

REPRODUCED ON LINUX before fixing — no Windows machine needed: open a
doc as file:///tmp/%77eirspell.weir (an encoded 'w') and watch two
publishes, 1 diag on the decoded spelling, 0 on the client's. The fix
is the rule the code now states: a publish rides the CLIENT'S OWN URI
string whenever the diagnosed file is an open doc (a path→client-uri
map per refresh); pathToUri serves only files the client never named
(module dependencies). The lsp-e2e pin holds the whole property:
exactly ONE publish, under the exact client spelling, none under a
re-derived one.

`weir lsp --debug` landed with it (the user's ask): every dispatched
method and every publish (uri + diag count) to stderr, which editors
surface (VS Code: Output panel). The next blink-class mystery is a
log read, not a rebuild-and-guess. Tolerated alongside --stdio/
--clientProcessId; e2e-pinned.

THE SESSION'S OWN LESSON, re-learned at the desk: my first "the fix
does not work" was a STALE BINARY — an ad-hoc probe bypassed
assert_fresh, and the publish step had been short-circuited by
`grep -c` (exits 1 on zero matches) an `&&` earlier. The freshness
gate exists for exactly this; ad-hoc probes assert freshness or they
lie. Second publish, same probe: one publish, client spelling, fixed.

## Windows v1 session 3 — the hand-run's findings (2026-08-02)

Windows was effectively green coming in (918/963, 43 stated skips,
2 fixture misses) — this session is repairs, and both substantive
repairs carry a lesson that outlives the fix.

THE LSP CRASH: `System.Uri` refuses `C:\Users\...` (a one-letter
"scheme"), so `pathToUri` threw on the first refresh and the server
died — five restarts, then gave up. The whole LSP surface sat
untested behind that one line. Fixed HAND-ROLLED in BOTH directions
(the plan's warning held: `uriToPath` was one `System.Uri` away from
the mirror bug): per-segment percent-encoding, drive lowercase on
the wire / UPPER back, `file:///` empty-host form, VS Code's
`c%3A` spelling accepted. The acceptance is the ROUND-TRIP pin —
path → uri → path identity on the running platform, spaces and
non-ASCII included. THE ROBUSTNESS HALF is its own finding: the
server DIED because a request path threw. Now one bad document is a
logged skip (per-doc guard in refreshAll — the others still
publish) with a request-level backstop, and lsp-e2e pins survival:
a malformed didOpen, then the next request answers. The
coverage-gap note for the safety review: its LSP pass probed
malformed FRAMING; malformed PATHS were the untested face.

THE COLORIZER REGRESSION is the dedent's story completing: session 2
wrote "bufferComplete and submission must agree or the verdict and
the meaning split" — and the colorizer was the THIRD consumer
neither of us enumerated. Head verdicts ran on the raw indented
text; completeness ran dedented; `  ^cmd` executed but lost its
paint. One `dedentEntry`, three consumers now — verdicts computed on
the dedented text, painted back behind the typed prefix. The pin is
a TRIPLE (executes + completes-on-first-Enter + paints), because the
pair pin is exactly what let this through. PROCESS gains the general
rule: a normalisation's consumers are ENUMERATED, not remembered;
the pin for a normalisation is an N-tuple across all of them. (The
triple's paint leg caught its own authoring bug on the way in: known
heads paint 1;34, not bare bold — the pin was wrong before the code
was.)

THE TWO SWEEP MISSES: `into` is now skipOnWindows with the product
gap STATED, not shimmed — into IS the documented sh -c path; a
Windows `into` would mean `cmd /c` as its shell there, a real small
feature whose receipt has not arrived. The ninth `\U` site
(fromFile single-quotes) got its `weirPath`; the mechanical
GetTempFileName/GetTempPath grep now confirms NO TENTH — the one
unwrapped survivor is the shim directory itself, which is a native
PATH entry and correct as-is.

ALSO PINNED from the banked list: the blank-line escape's
keeps-the-input half — the escaped buffer SUBMITS (its parse error
shows), where Ctrl+C discards. That property is why the escape
outranked the leading-space fix.

DOCS: Windows Terminal claims left-Alt+Enter for fullscreen —
Ctrl+J (everywhere) or right-Alt+Enter is the spelling; GUIDE table
+ README Windows section carry it. THE SHIM RULE, stated while
fresh: only shim names that resolve to NOTHING on the target
platform (`sort` was the near-casualty — System32 ships a real one
the feed test RUNS); the rule makes the list's next addition safe by
construction.

PENDING THE USER'S NEXT HAND-RUN (§4, probe-and-report, case by
case): a wrapping line (typing/arrows/backspace across the
boundary), Up/Down in the 2D buffer + history at the first line,
RESIZE mid-edit in Windows Terminal and conhost (handling is absent
by construction — SIGWINCH guarded off; report what actually
happens), Ctrl+R with and without fzf on PATH, blank-line escape +
right-Alt+Enter. Garbling is a sized finding for a follow-up, not a
surprise. Mid-buffer Enter stays unreproduced; the ESC-prefix
hypothesis stands recorded, not hunted.

## Windows v1 session 2 — the second pass, and the correction (2026-08-02)

THE HEADLINE IS A RETRACTION: session 2 was planned as the
job-objects session and IS NOT, because the spike's tree-kill
verdict was overturned by a better fixture. `Start-Process` launches
DETACHED — the child was never in weir's process tree, so all three
"findings" (natural-exit orphans, Ctrl+C inert, BCL kill short)
measured PowerShell, not weir. Re-measured with a genuine grandchild:
Ctrl+C kills it, natural exit is clean. The ONLY gap is a hard kill
(TerminateProcess — no user-mode cleanup), which POSIX shares under
SIGKILL modulo the process group — a parity question. JOB OBJECTS
PARKED with that trigger (a receipt where hard-kill orphans cause
real trouble); the fixture lesson is in PROCESS beside the pgrep one:
both are instruments that lie, and this one manufactured a ~150-line
session out of nothing.

PATHEXT: the decision is FULL environment list, and session 1's code
already read it — what session 2 adds is the PINS (a custom .XYZ
extension via a PATHEXT override resolves on Windows; foo.bat
resolves bare; both platform-conditional) and the SECURITY non-claim:
batch interpreters re-parse their command line (BatBadBut), so word
integrity holds UP TO THE HAND-OFF; native executables get weir's
argv verbatim — the runbook's injection probe (spaces, semicolon,
quotes, brackets arriving as ONE word) is the receipt. CreateProcess
runs .bat directly — no cmd /c synthesis anywhere, which dissolves
the safety objection that made batch support look expensive.

THE LEADING-SPACE REPL BUG — the plan said check Linux first, and
Linux HAS IT: a regression from repl-multiline's adoption of the
script assembler, whose model makes a first line's indent a
continuation of nothing (evidence it is a regression: `-e`'s
single-line parse, the pre-multiline REPL path, tolerates leading
whitespace fine). The rule landed as DEDENT-BY-FIRST-LINE: the
entry's relative structure is preserved (a pasted indented block
keeps its shape), and the SAME dedent runs in bufferComplete and the
submission path — the two must agree or Enter's completeness verdict
and the submitted meaning split. Pty-pinned both halves (9a: a
leading-space first line executes; 9b: an indented buffer dedents
whole and still assembles).

THE 11 FAILURES, sorted: 7 fixture residue fixed mechanically (the
two Env.fromFile GetTempFileName sites now ride weirPath — the same
helper, applied where the first sweep missed; head + the two
/bin/sh positive controls + path-completion marked skipOnWindows;
the cd-missing message asserts platformPath). Path.combine's pin
was indeed a missed conversion — platformPath applied. ^print
DIAGNOSED, fixture wrong: Windows System32 ships a legacy print.exe,
so the verdict flip was the colorizer being RIGHT; the fixture now
forces ^show (a binding absent from every PATH). The env-sigil chain
survivor: same resolution class as the un-triaged three — closed by
the parse shims (empty grep.exe). AND ONE SELF-CAUGHT SHIM BUG:
sort came OUT of the shim list — System32 ships a real sort.exe
that the feed test RUNS; an empty shadow would have broken a
passing test (the Start-Process lesson's shape, caught at the desk).

THE REPL-PLAY FINDINGS (rode this branch): (2) the blank-line
ESCAPE landed — Enter on an empty FINAL line closes a pending buffer
even when incomplete: the parse error shows and the input is kept,
where Ctrl+C (the previous only exit) lost it. Worth more than the
leading-space fix, as the user called it: it covers EVERY
uncompletable state, including ones we have not met. Ctrl+J/Alt+Enter
deliberate blanks never trigger it (pty pins 9c/9d, both ways).
(3) mid-buffer Enter NOT REPRODUCIBLE on Linux — the Down-then-Enter
probe submits the complete |>-case buffer from line 1's end.
Hypothesis: Alt detection is ESC-prefix on most terminals, so a
just-consumed arrow-key escape can leave Enter reading as Alt+Enter
(= force-newline) on the user's terminal; the blank-line escape
un-traps that symptom regardless. Re-test on macOS with the escape
in place; if it persists, the fix hunts the ESC-sequence parser, not
bufferComplete. (docs) Alt+Enter/Ctrl+J documented as THE
force-newline bindings; Shift+Enter stated NOT bindable (terminals
do not distinguish it from Enter).

BANKED (the runbook's confirmed list, do not re-walk): build/AOT/
--version/-e/scripts; parse/check/eval; spawn/argv/stdout/stderr/
exit codes; the injection guarantee under Windows' command-line
join; graceful tree-kill INCLUDING grandchildren; splat argv; 913 of
963 at the second pass (pre-shim, pre-session-2). Riders already
landed in session 1 (stated, not redone): CLI teaching arms +
--help, the three-platform size baseline, HTTP's measured 3.4MB
share.

## Windows v1 session 1 — make it work (2026-08-02)

The runbook's implementation session, on branch windows-v1, developed
on Linux with every Windows arm platform-guarded: POSIX behaviour is
byte-identical (the bar), Windows arms are VERIFIED-BY-HAND on the
user's machine until the CI matrix lands — stated here, per the plan.

1a THE GATE: Extern splits PATH on `Path.PathSeparator` (one BCL
constant, no #if) and, on Windows only, resolves bare names through
PATHEXT (default .COM;.EXE;.BAT;.CMD when unset) — the name as-given
always wins first; `names()` lets a PATHEXT file answer to its bare
name too, so completion and the head-resolver agree. On POSIX the ext
list is EMPTY, so every changed site reduces to the old code —
byte-identical by construction, and the both-ways pin asserts each
platform's own rule. ORDERING, stated as the plan required: PATHEXT
was untestable until the separator landed — the spike could not
separate them; the pin now can. ONE BOUND, stated: resolution accepts
.BAT/.CMD per PATHEXT, but CreateProcess only auto-appends .exe — a
.bat tool may resolve yet fail to SPAWN; the user's Windows run should
probe one (`cmd /c` is the likely spelling if it fails; sized small).

1b: the SIGWINCH registration guarded off on Windows (2 lines) — the
REPL starts; what the 2D editor DOES there is the new information the
user's hand-run reports (garbling = session 3, not a surprise).

1c THE RULING (DECISIONS row): Path members are PLATFORM-NATIVE out,
LIBERAL in — the System.IO.Path contract weir already inherits.
Alternative (POSIX-normalized out) recorded not just rejected;
`Path.toPosix` the escape hatch; a Path TYPE parked with the
real-bug-not-test-expectation trigger. The suite's first
platform-conditional expectation got its spelling: `platformPath`
(identity on POSIX).

1d THE STAMP MOVED INTO THE BUILD: WeirStamp MSBuild target in
Weir.fsproj (short hash + porcelain-dirty + nogit) — every publish
path stamps identically; publish.sh is publish-and-copy. VERIFIED
HONEST ACROSS INCREMENTAL REBUILDS in a scratch tree: clean build
stamps HEAD, a dirty-only change flips -dirty without a clean, a
HEAD-only move restamps — the three transitions the freshness gate
lives on. (Not Windows-specific; Windows made it visible.)

1e: config -> %APPDATA%, state -> %LOCALAPPDATA% (helpers beside
xdgHome; POSIX unchanged). THE 0600 GAP answered by INHERITANCE,
stated: no chmod on Windows; the history file inherits profile ACLs
that already deny other non-admin users — SECURITY.md now carries the
platform split instead of an unconditional promise. No AccessControl
package (zero-dep outranks).

1f: confirmed ~nothing — every env read rides
Environment.GetEnvironmentVariable, case-insensitive on Windows BY
THE OS. Pinned both-ways (Windows Some / POSIX None for a
case-mismatched read).

THE SUITE: 39 POSIX fixtures marked `skipOnWindows` (marked, NOT
shimmed — a shim built blind on Linux is the class of fix that lies).
Shim candidates for when the CI matrix can verify (~14, the
just-needs-a-command shapes): cmd yields stdout lines, cmd is lazy,
failing command raises, unforced runs nothing, real exec x3, complete
x3, force x2, short-circuit, splatted reifier argv. Inherently POSIX
(~25): sh-as-escape-hatch glob, capture oracles (POSIX sh emits the
fixture bytes), zombie/lifecycle counts (ps semantics), cd //tmp
family, worker-fork cd. Bucket 2 closed by `weirPath` (temp paths
ride as forward slashes into weir source — the \U escape trap, 8
sites at construction). Bucket 3 closed by 1a. Bucket 4 closed by
`platformPath` on the Path.dir pin. UN-TRIAGED THREE (splat echo,
sibling-sentinel acceptance, env-sigil parses): need a Windows
machine to diagnose — awaiting the user's run, NOT assumed bucket 1.

RIDERS: CLI teaching arms — `--e` did-you-means `-e`, `-e` names its
arity (the Windows shell-splitting trap: one intended expression
arrives as many argv), and `--help`/`-h` now EXIST (stdout, exit 0) —
added because the did-you-mean message references usage, and a hint
naming a spelling that does not exist is the lying-comment class.
E2e-pinned. BINARY BASELINE, first time all three exist: linux-x64
11,630,568 / win-x64 11,478,016 (runbook) / osx-arm64 10,635,256
(runbook) — Windows is NOT the outlier; macOS is smallest. THE GROWTH
CONFIRMED, not assumed: 7.7MB -> 11.6MB since the spike decomposes as
System.Net.Http ≈ 3,399,584 B (~29% of the binary — measured by
stubbing Contracts.fetchBytes and republishing: 8,230,984 without)
plus ~0.5MB of contracts/yaml/log/editor code. The HTTP builtin plan
inherits this number: the stack is already paid for.

NEXT: session 2 (job objects + Ctrl+C wiring + the AOT P/Invoke
probe; acceptance = the grandchild fixture both ways). The user's
Windows hand-run reports: REPL editor behaviour, a .bat spawn probe,
the un-triaged three.

ADDENDUM (hand-run receipt, same day): publish.ps1 works end to end,
incl. the PATH teaching hint. THE UN-TRIAGED THREE DIAGNOSED — one
cause, no fifth bucket: `echo` (and sh/grep/printf) are cmd BUILTINS
or absent on Windows, so PARSE-ONLY fixtures under the realResolver
failed at resolution, not grammar. Fix better than skipping: empty
<tool>.exe shims on a prepended PATH dir at suite init (resolution is
File.Exists, never execution) — ~20 realResolver parse pins
(env-sigil, sibling-sentinel ACCEPTANCE, capture-sigil, fmt) keep
their Windows coverage; tests that RUN the tools stay skipOnWindows
(an empty exe cannot run). POSIX: guarded no-op, 963 green. Shim
verified by reading only — the user's next Windows `dotnet test` is
its first execution.

## mutation-testing spike — the pins-sufficiency claim, measured (2026-08-01)

Throwaway spike (branch deleted, mutator deleted, tool uninstalled —
the TOML precedent). The question: "these pins are sufficient" is the
regime's one unfalsifiable claim; mutate the source and every
survivor is a behaviour nothing checks. The natural successor to the
vacuous-probe audit (can this probe FAIL?) and the maintenance
sweep's stated 20-pin sample — this one is exhaustive over its
region, and the region is stated.

TOOLING VERDICT: Stryker.NET 4.16.0 (dotnet-stryker, probed against
Weir.fsproj) throws `System.NotSupportedException: Language not
supported: Fsharp` at project analysis — a hard exception, no partial
mode. Hand-rolled instead: a ~100-line python mutator, textual,
line-scoped to target ranges, compile-as-filter. MUTATION SET (the
denominator rule): spaced `<`↔`<=`, `>`↔`>=`, `=`→`<>`, `<>`→`=`,
`&&`↔`||`, `not (` deletion, `true`↔`false`, `List/Option.
forall`↔`exists`, `.IsNone`↔`.IsSome`, int literal n→n+1, plus a
bespoke rotation of the QUOTED kind words in Contracts
tyKind/kindOk/literalKind (those strings are logic, not messages).
Stated exclusions: string contents and interpolation-hole
expressions (quote-count guard), and the ASSEMBLER by design — its
mutants crash rather than survive, which is uninformative.

TARGETS: Check.fs class solver `demand`+`dischargeCons` (349–411),
json field law (695–726), yaml tree law (735–792), Env loadable
family (1458–1523); Contracts.fs tyKind→spliceCheck (496–666).

RAW COUNTS: 61 generated / 0 invalid (every mutant compiled) /
55 killed / 6 survived / 0 timed out. Unit suite only (957 tests).
19.0s per mutant, 19.3 min total wall.

THE CLUSTERING READ IS THE FINDING: Contracts.fs killed 35/35 —
the schema-polish pins are demonstrably tight in the newest code.
Five of six survivors sit in ONE function: the class solver's
`demand`. Every survivor classified, verified EMPIRICALLY (mutant
applied, rebuilt, probe re-checked — verdict flip or byte-identical
error):

REAL (3), all in `demand`, each with its missing pin's exact fixture:
- Check.fs:371 decompose `List.forall`→`exists`: a MIXED record
  passes Eq. Probe: `type R = { A: int; B: seq<int> }` + `r == r` —
  real binary errors, mutant accepts. Root cause visible in the
  suite: the Eq fixtures are uniform single-field records
  (`Holder = { S: seq<int> }`), where forall and exists agree.
- Check.fs:389 Show `TTuple` `forall`→`exists`: a tuple with a
  function component passes show. Probe: `(1, (fun x -> x + 1)) |>
  show` — real errors [show-fn], mutant accepts. The Eq tuple twin
  (line 383) was KILLED — Eq tuples are pinned, Show tuples are not.
- Check.fs:362 TRowVar pend-arm `true`→`false`: an Eq constraint
  riding a ROW var. Probe: `let f = fun r -> r.A == 1 && r == r` —
  real binary accepts (constraint pends on the row), mutant rejects.
  Nothing pins that acceptance.

EQUIVALENT (3) — reported separately, NOT gaps:
- Check.fs:397 var/rowvar fallback arm flip: dead by construction —
  resolve's outer arms consume vars first; the code's own comment
  says the compiler cannot see it. Textbook equivalent mutant.
- Check.fs:377 decompose unknown-type `false`→`true`: unreachable by
  checker invariant — a TNamed reaching demand was already resolved
  against env.Types; unknown names error at mention, upstream.
- Check.fs:1460 isEnum `forall`→`exists`: masked by ordering — the
  payloadCase check runs first and any mixed union has a payload
  case; probe confirms byte-identical error text under the mutant.
E2E-COVERED (c): none — no e2e pin touches the three real shapes.

VERDICT (outcome one: survivors cluster somewhere real): the sized
finding is THREE unit pins, ~20 lines, fixtures above verbatim —
ride the next Check.fs session, or a 15-minute errand. The cluster
says something usable: the solver's STRUCTURAL region (decompose,
pend arms) is where pins thinned — concrete-type arms are pinned to
the letter, the recursive/constraint-carrying paths aren't; any
future predicate family (the signatures arc's solver work) should
pin its mixed/structural cases from day one. Adoption: NOT periodic,
NEVER CI (20s × mutant count and growing); re-run the hand-rolled
mutator after the next session that grows a predicate family — the
script is 100 lines and fully reconstructible from the operator list
above. The pins-sufficiency claim now has a measurement: sufficient
in Contracts, three named holes in the solver.

ADDENDUM (same day): the three pins LANDED (branch solver-pins) —
mixed-record Eq, mixed-tuple Show, Eq-rides-a-row-var acceptance,
each placed beside its family. Acceptance was pin-kills-mutant: all
three mutants re-applied, each now killed by its pin, source
restored. 959 unit; tests-only change (no src, no binary), so the
publish/e2e legs of the ritual were skipped with this stated reason.

## the resolver invariant — check agrees with run, and the yaml production (2026-08-01)

Item 1 of the refactor follow-ups, and the plan's thesis held
precisely: EXPECT RED, TREAT IT AS THE DELIVERABLE. The reds came —
two of them — but from the yaml PRODUCTION, not the property. The
production's transform battery (comment insertion, the uniform
reindent) caught a real product bug in `parseTplBlock`'s comment
transparency, in two faces: `firstContentRel` derived a nested
block's indent from a shallow `// comment` sitting between a key and
its content (smoke, first run — red inside the hour), and the extent
scan STOPPED at a comment at the unit's own indent, orphaning the
nested block (deep run, seeds 994247893 and 589527467). Root cause:
transparency was taught at the midline-# session as INLINE COPIES in
two of four structure loops — the classic sibling-loop drift. The
fix is ONE predicate (`tplTransparent`) consumed by all four loops,
both faces unit-pinned, both seeds green, five fresh 1200-case deep
seeds clean. These were parser bugs, not resolver divergences — the
plan's size-don't-fix rule targets the RESOLVER class; a one-predicate
mechanical fix with pins met the mechanical bar, stated here so the
report is honest about which rule applied.

The property itself (invariant 6): every generated program's logical
lines parse to the same sexpr and check to the same verdict under
`assumeResolver` and the hard resolver, envs folded per side, an
agreed rejection stops the walk. It found NO live divergence — the
five historical incidents are fixed in today's tree; the property is
the guard against the sixth, which is exactly what the analysis
asked it to be. Generated command heads are real on PATH, so the
hard side resolves as the runner would — the production keeps the
marker line non-error territory deliberately: junk after `yaml`
re-reads as command argv under assume, which is THIS property's
quarry, not the span property's.

Production shape: top-level districts only, 1–3 literal keys,
int/word/splice leaves, one optional nested map, a trailing
`|> to yaml |> print` so output identity SEES the district. 44/120
sampled programs carry one. Deep runs lengthen to ~65s/1200 — about
the plan's predicted 20%. GRAMMAR.md updated: production to CAN, the
coverage-gap denominator narrowed to the shapes still outside
(for-entries, key splices, block scalars, schema=), invariant 6
listed, CAN/CANNOT diffed (the fuzz audit's meta-lesson) — no
contradictions.

Riders and the not-scheduled list are recorded in
PLAN-refactor-followups.md so none is re-proposed from shape:
Modules.fs rides the modules arc; F-A (didYouMean separator
parameter, schema pins unmoved) and F-B (scalar self-typing to
Yaml.fs) ride the next schema session; Program.fs plumbing waits for
the third contracts command; assembler split / Hover.fs / walker
merge / BCL swaps stay closed by the analysis' own arguments.

One process lesson re-learned the hard way: a replace without an
assert was a SILENT NO-OP (the stmtUses arm), surfacing later as an
incomplete-match crash INSIDE the shrinker — assert replace counts,
always.

## the Log module — four levels, no error, stderr as law (2026-08-01)

Small session, landed as blessed with the four rulings folded in.
The load-bearing decision is the stderr LAW: weir's stdout is a
value, so `Log.*` has no stream knob at all, and the e2e headline
pin is byte-identical stdout at every level including `off`. The
dropped `Log.error` is the module's best boundary — a filterable
error is the one message `WEIR_LOG=off` should never eat, and weir
already has the channels (filterable → Log, unconditional →
printerr, stop → fail; `warn` tops the filterable range). The docs
pre-empt the missing member.

The eager-argument question ruled 1+2 as recommended: plain members
evaluate their strings (weir has no lazy positions and does not
pretend otherwise), and the `With` twins run the thunk only when the
level passes — proven by a side-effect pin (a thunk that writes a
file: absent at info, present at trace). Everything else fell out of
existing machinery: WEIR_LOG rides the env sigil to child weir
processes (a pin, not a feature), `-e` and the REPL both log
(prompt probe added), and the tint reuses the shared palette — which
required moving `module Color` from Script to Types so Builtins
could reach it (Script keeps a file-local alias; Repl and Program
repointed — the one discovery: F# module abbreviations are
file-local, so the alias could not serve external consumers).

Invalid `WEIR_LOG` errors before dispatch (exit 2, naming the
levels) — validated once in Program.main, so even `weir verify`
under a typo'd level refuses loudly rather than running with a
silent fallback.

## schema polish — the ellipsis was a bug, the paths were free (2026-08-01)

The rider's suspicion list went two for two. The enum `…` WAS "a bug
wearing a formatting convention": `List.truncate 4` plus an
unconditional ellipsis, so k8s `kind` — a one-element enum, the
common case — rendered `(ConfigMap…)`. Now one value states plainly
(`field kind expects 'Service', got 'Deployment'` — which is also
1b's point demonstrated: IDENTITY mismatches are caught, so the
rejected `kind`-inference costs nothing; the schema itself carries
the convention). And threading the PATH (ruled: always) gave every
message its field name in the same stroke — items 1 and 2 were one
change. Root paths render without a suffix, so the four acceptance
messages kept their exact text and the six shapes are e2e-pinned
verbatim.

The strict-variant finding is the load-bearing one: unknown-field
checking IS `additionalProperties: false` (mechanism confirmed), so
a plain `-standalone` schema validates structure while silently
accepting `apiVerison` — the exact failure the feature exists to
prevent, wearing the appearance of working. `weir add schema` now
warns when a fetched schema closes nothing, naming
`-standalone-strict`; pinned, and the docs carry the reason.

Completion ruled `schema` MARKER-LOCAL — a Parser.keywords entry
would have RESERVED the identifier. The marker context offers
`schema=` after `yaml ` (adapters offer nothing — the predicate
knows), and the genuinely useful half: vendored schema NAMES from
`.weir/schemas/` complete after `schema=`. The shared marker
predicate moved to Parser with Script aliasing it — one classifier,
now three consumers (assembler, REPL tint, completion). The
grammars needed nothing (the spine session had already extended the
marker token through the suffix, engine-verified) — the rider's
observation predated that fix; the REPL tint gained its pin.

Filed, sized, not built: F1 path-aware did-you-mean (a schema-tree
name index — `'containers' — did you mean
spec.template.spec.containers?`; ~60–100 lines); F2 internal
`#/$defs` support (never separately measured — the corpus had zero
refs of ANY kind; ~80–120 lines, would widen the corpus to most of
SchemaStore; measure SchemaStore first). The /null noise: kept,
ruled, not to be re-litigated. 956 unit, e2e re-pins green, full
ritual green.

## external contracts — the spine lands, schemas prove it (2026-08-01)

PLAN-contracts-spine executed in one session: the shared spine
(discovery, `weir add`/`weir restore` (renamed from `vendor` by the
pre-merge rider PLAN-restore-rename — the branch merges with one
coherent naming), `weir verify`, the lockfile, sha256,
never-fetch-during-check) built WITH its first customer exercising
every part, exactly as the ordering argument demanded. The eight
rulings were written before code (the ninth emerged: where name→URL
lives — the LOCK is the standing record of intent AND the manifest;
`add` writes it, `restore` re-materializes hash-verified, no
separate manifest). The corpus measurement DECIDED the subset rather than
informing it: six real k8s standalone-strict schemas showed zero
`$ref` (standalone inlines everything — remote-$ref MOOT, the
teaching names the variant), zero composition, zero constraint
keywords, and exactly one `oneOf` idiom (IntOrString, 143/143) — so
`oneOf` is in as scalar-alternatives-only and everything unmeasured
stays out until a corpus needs it.

The session's find was an old friend: the machine boundary needed a
THIRD face. `yaml schema=x` glued to the sentinel parsed as a
COMMAND under check — the head guard protects a glued HEAD, the
block-scalars session added the arg-transparency, and now a
` schema=<name>` suffix glued to the sentinel refuses at the head
too (no user argv is ever glued; the parse falls through to the
district arm). The arg-face guard added alongside surfaced a
FIXTURE bug the audit's class predicted: the sibling-sentinel
acceptance pin glued its sentinel where the assembler always SPACES
it — corrected to the machine-real join, named here.

Validation walks the TYPED template (it gained spans — key spans
included, so `apiVerison` errors at the key's own columns with a
did-you-mean from the schema's properties). Structural always;
value-by-type on splices (Option unwraps, seq elements check against
items); for-entries and key splices relax unknown/required for
their map. Property 3 pinned twice (in-session probe + e2e): with
and without contracts, byte-identical output. The run path gates
too — check-before-effects extends to contracts.

The e2e battery vendors the committed REAL configmap schema (17KB,
tests/fixtures) from a LOCAL server — CI stays offline; the
published-schema fetch (yannh/kubernetes-json-schema over the
network, all six files) ran in-session and its measurements are in
the plan. weir's first network code (HttpClient) arrived fenced
exactly as designed: two explicit commands, never check, never
completion, never run.

Grammars rode along: the tree-sitter marker token extends through
`schema=<name>` (one keyword), TextMate's district begin gained the
optional suffix — both engine-verified; micro stays exempt. GUIDE
and SKILL carry prose (a runnable doc block needs a `.weir/` the
doc-test cwd cannot safely host — the e2e battery holds the
executable truth; stated).

955 unit (+2 contract pins, one fixture corrected), e2e battery all
green incl the contracts section, full ritual green.

## the fuzz-coverage audit — claims vs measured reality (2026-08-01)

GRAMMAR.md says "the fuzzer passed" is a claim with the file as its
denominator, so the file itself got audited: 400 base programs
dumped through the real generator (fsi against the built FsCheck) and
every CAN/CANNOT row measured, every harness config claim checked
against the code, and deep runs actually run.

THE FIND, and it is the audit's justification: FRESH-SEED DEEP RUNS
WERE RED while the pinned smoke stayed green — the masked-failure
shape the denominator exists to prevent. Root cause was a HARNESS
bug, not a product bug: the renderer tagged `///` field-doc lines as
expression territory, so the span invariants injected junk onto doc
lines and demanded a diagnostic THERE — but junk appended to a doc
line is legal doc TEXT (docs are inert), the real first error sits on
the field line below, and the arbitration property called that a
stolen report. One-line fix (doc lines tag as comment territory,
like argv); the failing seed 481895091 now passes and three fresh
1200-case runs came back clean. The field-doc generation landed
2026-07-30; every deep run since would have been red — nobody ran
one until now, which is its own lesson: THE DEEP RUN IS PART OF A
GENERATOR CHANGE'S RITUAL.

Measured and TRUE (the reassuring bulk): every other CAN row appears
in the sample (block lets 288/400, match heads 154, headed districts
188, multiline lambdas 233, standalone districts 55, command-backed
block lets 16, aligned record types 7...); all fifteen CANNOT rows
measure zero; markers are program-unique (0 duplicates in 1743);
placement laws hold (0 indented type decls); block-let nesting never
exceeds 2 (0 at depth 3); seed/count/strict-spans defaults and the
CI wiring order match the claims; tools/fuzz.weir's 10k default is
the attribute literal.

Corrected in GRAMMAR.md, measurement-anchored: the splat is a
TRANSFORM-ONLY spelling (0/400 base programs — it sat under CAN
unqualified); the comments/blanks exclusion now scopes to plain `//`
and inside-statement blanks (the base render HAS between-statement
separators and /// docs — the old line contradicted the CAN list);
the union-value match is marked THIN (~1%, 4/400 — a 200-case smoke
barely sees it); the smoke timing claim updated ~7s → ~15s measured;
the graded positive control restated as verified-at-bring-up, not a
standing test. One incidental confirmation: the first fuzz run of
the session failed 12/20 on the STALE-BINARY stamp gate (the merge
had moved HEAD past the installed binary) — the harness-truth guard
working exactly as institutionalized.

ADDENDUM (user review): the CANNOT list still said "multiline
lambdas (not a weir feature yet)" — stale since the feature landed,
directly contradicting the CAN list's [D:multiline-lambda] entry
(and the measurement: 233/400). Removed. The audit's own miss: it
measured each list's rows individually but never cross-checked
CANNOT against CAN for contradictions — a denominator audit should
diff the two lists first, since a row present in both is the
cheapest lie to detect.

## mid-line `#` in districts — the two paths agree (2026-07-31)

The audit's "stated asymmetry" did not survive review: YAML's own
rule (a whitespace-preceded `#` begins a comment) says the read side
was right and the district was wrong, and the bite was in generated
OUTPUT — reverse-Norway faithfully quoted the polluted value, so
`to yaml` emitted `image: "nginx:latest # pinned by ops"`, silently
wrong, uncatchable from inside the self-consistent weir→weir round
trip. Paste-a-manifest is the district's pitch; pasted text now means
what `from yaml` says it means.

The cut is ONE machine with two faces: the yaml comment scanner
grew a `holes` parameter — the plain face (read side) is YAML's
lexical rule unchanged; the district face also skips `$(...)` holes,
whose interior follows weir string rules (double with escapes,
single raw, paren depth). The template parser cuts STRUCTURE lines
and `for` headers before dispatch; block content was already
consumed as bytes before the loop, so the ordering law did the
protecting. A bonus the rule bought: trailing comments after
splices (`key: $name # note`) parse now — they errored before.

All five table rows landed first try on the probe, acceptance
included (`image: nginx:latest`, unquoted). The corpus sweep found
ZERO mid-line `#` occurrences in committed district sources — no pin
moved anywhere. Grammars: the tree-sitter scanner stops yaml_text at
a whitespace-preceded `#` and the pre-existing `hash_line` token
paints the comment (quotes, holes, and block mode excluded — engine
verified); TextMate's district comment rule was ALREADY right (the
district session's foresight), needing only a lookbehind widening of
the value catch-all so glued `a#b` stays one token; micro exempt.
The fuzzer's missing yaml-district production is now in GRAMMAR.md's
stated denominator, closing the audit's last loose thread.

## the content-bytes audit — three collisions become seven, deliberately (2026-07-31)

The block-scalars session found three normalizing layers the hard
way; the audit walked the rest and found four more. The fixture came
first, as the plan ordered: one block scalar carrying every hostile
byte class, run against HEAD before any fix. HEAD's failure list —
the audit's first result: the tab rejection reached inside content
(assembler error); `//` and `///` lines VANISHED (stripComment plus
the comment-only filter, both running before assembly ever saw a
district); trailing whitespace was trimmed (the sentinel split and
fmt, independently). Blanks, `#`/`#!`, `---`, splice-lookalikes,
`for`-lines, and indentation games already survived — the
block-scalars session's own work.

The central fix moved the stripping INTO the assembler: assemble now
takes raw lines and classifies/strips internally, so an active yaml
district joins its lines' raw bytes while structure still parses the
stripped text — `//` comments keep working everywhere outside
districts, and full-line `#`/`//` shapes are structure-transparent
inside them (bytes in block content, as the # precedent). The three
callers dropped their filter+strip pipelines; the tests' direct
assemble calls needed nothing (stripping is idempotent). The tab
rejection narrowed the same way the directive scan did last session:
a tab after the space indentation inside an active district is
content. Trailing whitespace: the sentinel split stopped trimming,
and fmt now strips only the space indentation (so a content-leading
tab survives too).

The subtler trio shared one missing piece: doc-attachment, the
doc-align lint, and fmt's canonicalizeDocs all treated `///`-shaped
content as doc syntax — the lint ERRORED on district bytes, and fmt
re-anchored them (the safety net caught the latter, honestly). One
shared mask (`districtContentMask`, defined once in Script off the
real marker classifier) taught all three to skip content. Verified
by adversary: a `/// doc-shaped` line above a deeper content line
survives fmt untouched, silently passes check, and reads back as
bytes.

CRLF resolved without code: ReadAllLines normalizes line endings at
read, so a CRLF-saved source behaves byte-identically — decided as
the rule (a source file's line ending is not data), pinned in the
fixture's CRLF twin. R1 resolved for the fallback: 2+ trailing
newlines render quoted-with-escapes (valid, exact, round-trips); the
ERROR spelling retired and the ledger's never-drop-bytes reasoning
now argues FOR the fallback, as it always should have. R2 amended the
maintenance plan (quoted-message verification — two ledger-claimed
messages have now failed to exist). R3 filed the `{{ }}` interp nit
in the grammar's own README under Known nits.

The denominator: 16 sites examined, 10 touching potential content, 7
mangling — all seven fixed. Stated normalizations, not mangles: fmt
renders a whitespace-only content line as empty (value-identical);
mid-line ` #` on district structure lines is data while the read side
strips it (pre-existing asymmetry). Findings sized, not fixed: the
fuzzer grammar generates no yaml districts (coverage gap — its
reindent is uniform, so safe by construction when it does);
attachments inside bang districts are unmasked but benign. The class
is named in PROCESS with its archaeology, so the next byte-exact
region inherits the audit.

## block scalars — the ConfigMap gap closes, and two walls moved (2026-07-31)

PLAN-block-scalars executed, with the bless amendment folded in:
chomping is NOT a policy — `|` MEANS ends-with-one-newline, `|-`
means ends-with-none, and the form follows the value in both
directions. The probe answered the plan's first question: a
multiline splice rendered as the `\n`-escaped quoted spelling
(valid, unreadable — a bug against session 1's stated intent), and
the read side's `|` produced a MISLEADING error ("has both an inline
value and a nested block"), not even the promised teaching.

The read side's real requirement surfaced immediately: block content
must come from the RAW lines, because parseDocs drops blank lines
and `#` lines and strips trailing ` #` comments BEFORE the block
parser runs — and inside a block scalar all of those are bytes. So
parseBlock threads the unfiltered source, and an extent-consistency
guard refuses the one shape that could silently strand content (a
dedented `#` line inside the extent). The write side is one law:
multiline (and control-tame) strings render as blocks, one trailing
newline → `|`, none → `|-`, two or more ERROR (that is `|+`'s job,
rejected — a renderer never drops bytes silently). The reverse-Norway
pin's multiline case moved WITH the feature, deliberately.

The district half moved two walls the plan didn't fully see. FIRST,
blank lines: the assembler's total transparency [D:body-blanks]
would have silently dropped blank lines from block content — the
never-drop class — so inside an ACTIVE yaml district a blank now
rides the sentinel as an empty verbatim line, and the template
parser skips blanks (and full-line `#`) at structure level while
block content keeps them. The sentinel-split had to stop dropping
empty fragments, and every nested-block decision switched from
extent-length to CONTENT-counting (trailing district blanks were
tripping "both inline and nested"). SECOND, the directive scan: the
ConfigMap workload's first line is `#!/bin/sh`, and the run path
called it a misplaced directive. A directive is a statement-position
thing — the scan narrowed to COLUMN-0, which also closed the parked
full-line-`#`-in-districts bound as a side effect (yaml comments at
structure level, bytes inside blocks).

The centrepiece held with no drama: content lines are consumed as
TEXT before the splice/for scanners run, so `$name`, `$(1 + 2)`,
`for x in xs`, and `key: value` shapes survive verbatim — pinned at
unit and e2e, plus the sharp span pin (a rejected `>` header inside
a district errors AT THE HEADER under check --json). fmt needed
nothing: block content is offsets-within-offsets and the district
relative-indent law already refuses to re-indent it — pinned anyway,
with idempotence. Grammars: the tree-sitter scanner gained a block
mode (5-byte state; whole content lines as single text tokens — no
splice/for/key scanning inside), and TextMate bounds content with a
while-BACKREFERENCE to the header's captured indent (the markdown-
blockquote trick) — engine-verified both. micro stays exempt.

Found while verifying, not fixed here: the tree-sitter grammar has a
pre-existing 2-ERROR nit on showcase.weir's `{{literal braces}}`
interp line (present at HEAD before this session — a future
coloring-nit candidate). 952 unit (+4), e2e green with the ConfigMap
battery, full ritual green.

## the showcase, and the two findings it earned (2026-07-30)

examples/showcase.weir landed: one runnable tour of the language —
typed argv/env, all four string kinds, records/unions/tuples, Regex
patterns and seq patterns, the reifier family, adapters, files/glob,
env sigils, all three districts, for/do, pmap, and the YAML arc as
the finale (a `--tag` Option splicing into a district, omitting when
None). Three iterations to green, both errors mine (a match on an
untyped param; a group-less `rmatchAll` pattern — the groups seq is
CAPTURES only, group 0 excluded).

Writing it earned two findings, both fixed the same day:

FIRST, `weir fmt` refused any file with a NESTED yaml district — the
formatter flattened every district line to marker+1 depth (right for
`!` blocks, whose command lines are flat), which destroyed the yaml
block's RELATIVE indentation, and the safety re-parse correctly
refused. The fix keeps the `!` behavior and adds the yaml species:
the block's first line is the base, every line keeps its offset from
it, and only the base re-anchors to marker+1. fmt now canonicalizes
weir's indentation while never rewriting the yaml's own nesting —
that is the user's data. Pinned: re-anchor + offset preservation +
idempotence on nested districts.

SECOND, a `///` doc ABOVE an attribute line silently detached — the
attribute line broke the contiguity run, so F#'s canonical
doc-then-attribute order dropped the doc from --help and hover with
no warning (the showcase's `--limit` lost its help line this way).
Attribute-only lines (`[<...>]`) are now TRANSPARENT to doc
attachment — the doc rides through to the declaration below, both
orders attach, and the doc-align lint rides through too (it anchors
on the FIELD, so a misaligned doc above an attribute still errors).
One helper (`isAttributeOnlyLine`), used by both walkers; fmt's
canonicalizeDocs needed nothing (the attribute line already sits at
the field's anchor). SKILL + GUIDE prose updated.

Ritual note: one fuzz failure appeared on the first post-publish run
and never again (10 consecutive green). The smoke seed is PINNED, so
a code-caused failure would repeat deterministically; the harness
spawns the binary under a wall-clock hang bound, and the failing run
rode the machine's post-AOT-publish load — the environmental-timeout
signature. Not chased further; noted so a recurrence has a trail.

## yaml district editor grammars — the arc's rider (2026-07-30)

The district got its coloring treatment across all three grammars,
closing DESIGN-yaml-templates. tree-sitter took the real work: the
external scanner (until now only the `'a`-vs-`'echo x'` discriminator)
grew a district state machine — `yaml_marker` (line-end word with an
indented block behind it), `yaml_key` (text before a real `: `),
`yaml_text` (scalar runs), with `$name` splices, `$( )` holes, quoted
scalars, `:` and for-headers handed back to the internal lexer. The
finding that shaped it, observed not theorized: state mutations on a
FALSE-returning scan are dropped on deserialize — the first cut set
"rest of line is weir" mode while returning false for the `for`
keyword and the `$(` opener, and both came back as one text blob. So
every mode flip rides a SUCCESSFUL scan: `yaml_for` and `yaml_hole`
are the scanner's own tokens, and district exit is the zero-width
hidden `_yaml_end` (the indent-scanner convention). The adapter
exclusion is LEXICAL, no lookbehind: `to yaml`/`from yaml` are one
internal `adapter` token, and longest-match at the `to`/`from`
boundary means the scanner is never consulted at the `yaml` position
inside one.

TextMate uses the begin/while indentation region with lookbehind
guards for the adapters and inner mini-rules (keys, splices, holes
with a weir subset, for/in, quoted scalars, ` #` comments, unquoted
values as data); the `while` continues on ANY indented line, not
indent>=base — a stated renderer bound. micro is exempt
([D:micro-exempt]): RE2 cannot lookbehind, so a marker rule would
actively mis-color `x |> to yaml`, and its regions cannot close on a
dedent — the body renders with the base rules. Inventory: 25 rules,
4 stated exemptions.

The keyword sweep caught real drift: `for`/`do` had reached NO grammar
at the for/do session, and `module`/`import` (the modules arc) plus
reserved `function` were missing too — all three grammars closed the
gap in one pass. The REPL colorizer needed nothing there — it reads
`Parser.keywords`, invariant by architecture. It did gain the marker
tint: line-end `yaml` colors like the `!` markers via
`isYamlMarkerPiece`, EXTRACTED from classifyPiece so the tint and the
assembler share one classifier; body lines stay per-line lexical (the
block treatment is the static grammars' and semantic tokens' job).
The new repl-color pin's first cut hung the pty: a marker line OPENS
the multiline buffer, and Ctrl+D is EOF only at an empty buffer — the
fixture now cancels with Ctrl+C (fixture error, not product).

Verified on engines, not by eye: tree-sitter CLI parse over the weir
corpus (8 files, 0 ERROR) plus adversaries (blank lines mid-district,
nested dedent exit, quoted keys, `url: http://host:8080/x` colons in
values, `for: 3` as key not header), captures asserted via
`tree-sitter query`; vscode-textmate engine assertions including the
adapter negatives; the vsix rebuilt (the stale-vsix lesson); the Zed
copy synced (the drift guard diffs it).

## yaml session 2 — the district, and the machine boundary (2026-07-30)

The `yaml` district landed: a checked block literal on the assembler's
district machinery, third species after `!` and `!name`. The design
that made it small: block lines ride into the logical line VERBATIM
with RELATIVE indentation behind the sentinel, and every fragment
parse (splices, for-headers) runs PADDED to its logical column — so
spans land true by construction and `translate` maps them physically,
with no span-shift walk anywhere. The `for` specialization arrived
exactly as PLAN-for-do promised: same head, no `do`, the body is the
sub-template, and context decides the yield — the labels case
(`for (k, v) in pairs` → `$k: $v`) is the open-map answer working on
the first try.

Two findings earned their keep. FIRST, the machine boundary bit
before it protected: under `weir check` (never under run) the
assume-resolver made `yaml` a COMMAND head, and the let-RHS command
path swallowed the district — the sentinel then read as a statement
separator and the block text parsed as junk. The fix is the boundary
stated as grammar: statement joins always SPACE the sentinel, only
the yaml wrap GLUES it to the marker word, so a command head glued to
the sentinel is impossible from user text and `commandSegment` now
refuses it. That guard is also why `yaml` needs no reservation — the
district arm triggers on a shape users cannot type, and `to yaml` /
`from yaml` stay plain words. SECOND, the splice law met polymorphism:
a template parameter (`$name` where name is a lambda param) is an
unresolved TVar at check, and the liftable law cannot constrain it
(no type classes). The sortBy posture answered it — concrete
violations check-error at the splice, unresolved vars defer to the
VALUE-driven lift, and a bad polymorphic splice is a located runtime
failure naming the law. Same trade, same words, now in a second
place.

One eval-side decision worth its sentence: runtime duplicate keys
(for-generated or key-spliced) are ERRORS, not last-wins — invalid
YAML must never render silently, and the check-time literal-dup error
cannot see what a `for` will generate. And the README got its line
verbatim, per the bless note: you cannot write a YAML injection in
weir, for the same reason you cannot write an argv injection.

## yaml session 1 — the tree boundary, on an owned parser (2026-07-30)

`from yaml T` and `to yaml` landed with the `Yaml` node union, and the
bless note's ordering — value-domain questions BEFORE the parser —
paid immediately: probes showed recursive unions and seq-of-tuple
payloads simply DECLARE in weir source, so the union went into the
prelude next to Option and inherited constructors, Show, and the
class laws for free. The Eq question answered itself: the existing
no-seq rule refuses the union with its own teaching text — the right
answer produced by machinery that never heard of YAML. And a second
probe surprise: `from`/`to` parse their format word generically, so
the whole feature needed ZERO parser changes — checker arms and eval
arms only.

The owned-parser bet (the config spike's receipt) came in at ~300
lines including the k8s compact form (`- name: web` items), and its
dividend is POSITIONS: every rejection — anchors, flow style, tags,
duplicate keys, null-in-required — carries its line, which is
exactly what the spike faulted YamlDotNet's messages for. The
subtlety worth keeping: QUOTEDNESS is load-bearing on the read side.
The internal node tree records whether a scalar was quoted (the
public union deliberately does not — construction never needs it),
because `flag: "true"` at a bool field must error as a string while
`flag: true` converts — and bool is EXACTLY true/false (the Env.load
law), so the Norway problem cannot fire by construction: `flag: no`
errors, naming the rule. The reverse direction is the quoting law:
`"no"`, `"007"`, `"1e5"`, mid-line ` #`, `: ` all auto-quote, so no
YAML reader can mis-type weir's output either. Both directions of
the same promise.

The shape bridge repeated [D:env-enums]: eval has no env.Types, so
the checker resolves the whole target tree into a `Yaml.Shape` packed
in a new `TEFromYaml` node. Renders: record fields alphabetical (the
VRecord representation's law, same as json) with `YMap` as the
user-ordered escape; a `None` field omits, a `None` element renders
`null` (the json-option split, transplanted). Multi-doc `---` in and
out; null-where-a-collection-sits reads as the empty collection (the
k8s `ports:` idiom). Block scalars stayed out (a teaching error
pointing at the `\\n`-escaped quoted spelling) — the district session
can add them as polish. One pre-existing pin moved for an honest
reason: the unknown-format test's example WAS `yaml`.

## for/do lands — the divergence bar worked in reverse (2026-07-30)

The general effect loop shipped in one sitting, and the interesting
part is WHY it exists: the district-only `for` in the yaml design was
scope discipline misread as a verdict, and the user's challenge
reframed it as a DIVERGENCE question — F# has `for x in xs do` and
`[for x in xs -> e]`; declining a sibling's form needs its own
justification, and "we have Seq.iter" does not meet the bar the
project holds its other divergences to. The divergence discipline
usually stops weir from drifting AWAY from F#; here it pulled weir
TOWARD F#. Same law, opposite direction.

The desugar-at-parse choice (the reifier precedent) made the session
small: the typed tree never sees `for`, so checking, hover, warnings,
and eval all rode existing machinery — the whole feature is a parser
construction plus one assembler predicate. The bless notes each paid:
the COMPREHENSION finding came out the good way (`[for p in xs -> e]`
bypasses EList entirely — no interaction with empty-list inference —
and `Seq.force` in the desugar preserves the literal's eagerness, so
it shipped rather than parked); the LAYOUT was pinned against the
then-body rule rather than analogized (and the assembler DID bite:
`dangleEnders` uses bare `EndsWith`, so adding `"do"` naively would
have made `sudo` at EOL dangle a block open — the new ender is
word-boundary guarded while the existing enders keep their exact
suffix behavior); the KEYWORD sweep found `do` in 38 prose spots and
ZERO identifier positions, so reserving both words moved only the
completion-inventory pin; and the BARE COMMAND BODY (the bless
addition) is the feature's best moment — `for f in files do git add
$f` parses the body as a command chain and wraps it as implicit
`!(…)`, streaming and raising per iteration. That rule is
for-specific and stated: `do` is a statement position, `then` stays
expression-only (probed — `if c then git status` still rejects).

One fixture bit back during probing, instructively: `for w in xs do
sh -c "echo got-$w"` printed empty suffixes — because `$w` inside a
plain quoted string is SH'S variable, not weir's splice (bash muscle
memory in the test author, not a bug). The correct spellings are
interpolation (`sh -c $"echo got-{w}"`) or a bare argv splice
(`git add $f`) — and the fact that the loop variable reaches command
splices at all is the desugar paying again: it is just a lambda
param, and command splices already resolve those.

## TOML reverted — the dependency outweighed the comfort (2026-07-30)

Same day as the adoption, the user reconsidered — not the numbers
(they held: 0 warnings, +330 KB, startup unmoved) but the DEPENDENCY.
Weir's sole runtime dependency is FParsec; Tomlyn would have been the
second, a supply-chain and maintenance surface bought for a 4-key
file whose only real win was comments. Zero-dependency outranks
config comfort; JSON is restored.

The reversal itself is the cheap-window argument completing its own
demonstration: the TOML commit lived a few hours on an unmerged
branch, so reverting was `git revert` plus two ledger paragraphs —
no user ever held a config.toml, nothing to migrate, no compat
sentence to write. The journal keeps both entries (adopted below,
reverted here) because the DECISION TRAIL is the value: the config
question is now doubly closed — a measurement (the spike) and a
tried-it data point (this) — and the next "why not TOML?" finds
both. The TomlReader implementation knowledge (the token walk, the
Deserialize-under-AOT trap) stays recorded should the calculus ever
change, e.g. if a second dependency-worthy need arrives and the
marginal cost of Tomlyn drops to its bytes.

## The Windows spike — the Linux half, and two fears retired (2026-07-30)

The native-Windows viability spike ran on a throwaway repo copy — on
LINUX, which the spike's own bar ("every Tier-1/Tier-3 claim is a real
Windows run, not reasoning") turned into a discipline: every cell is
labeled MEASURED / SIZED / PREPARED / BLOCKED-HERE, and the
Windows-run cells became a runbook rather than a guess. The verdict
so far: **3–4 small sessions plus two bounded probes, no blocker
visible** — pending the runbook on a real Windows host.

The headline is that both feared Tier-3 items shrank under
measurement. The `\r\n` blast radius — billed as "touches the most
surface" — is **zero stdout pins**: the capture path already handles
CRLF and lone CR (Proc.fs `scanLines`, oracle-pinned), because the
representation session solved it in passing and the fear outlived the
fix. Only the stderr rule (deliberately `\n`-only, CR retained) holds
exactly ONE pin — keep it and Windows stderr lines wear a trailing
`\r`; strip and one pin moves (no deliberate-`\r` fixture exists
anywhere else, grepped). And tree-kill is NOT a POSIX
process-group reimplementation waiting to happen: the one kill site
is the BCL's `p.Kill(entireProcessTree = true)` (Proc.fs:62) — the
same call on Windows — narrowing the question to whether the BCL's
snapshot-walk survives the orphaned-grandchild case (a probe), with
job objects (~150 lines) only if it fails. A stale premise audit in
miniature: two of the plan's hard items were already half-solved by
sessions that never mentioned Windows.

What is certain without a run: **cross-OS NativeAOT is unsupported**
(`error : Cross-OS native compilation is not supported`) — a Windows
release needs a Windows build host, full stop — while the managed
win-x64 compile is clean. Tier-2 sums to well under ~100 lines across
four arms: the PATH resolver genuinely breaks today (`:` split, no
PATHEXT, no `\`), config/state dirs work-but-non-native, env
case-insensitivity costs ~nothing, and the history file's 0600 is a
SILENT GAP on Windows (the `SetUnixFileMode` try/with swallows —
default ACLs, no error) — the secret-surface story needs an ACL arm
and an honesty line if Windows is ever claimed. The `.cmd`/`.bat`
BatBadBut hazard is a stated non-claim regardless of any result.

One process note: the spike agent REPORTED its copy deleted, but the
directory was still there — the parent verified and deleted it.
Subagent cleanup claims get verified, same as any other claim.

A second, deeper pass ran in parallel (an accidental redundancy that
paid): every shared number CONVERGED — the same verbatim ilcompiler
refusal, the same 1-pin stderr radius, the same BCL-kill mechanism —
which is what two independent measurements are for. And it caught one
thing the first pass missed: **the SIGWINCH registration is unguarded**
(`PosixSignalRegistration.Create` in Repl.fs) — on Windows that is a
`PlatformNotSupportedException` AT INTERACTIVE-REPL STARTUP, a genuine
crash-on-arrival; `-e`, scripts, lsp, and fmt route around it. The
2-line guard is item one of any Windows session — not fixed now,
because nothing merges from a spike. Refinements worth keeping: the
product's whole POSIX surface is EIGHT arms totaling ~80-95 lines
(zero DllImport, zero termios, zero setsid/killpg in the product);
the suite splits 884 pure / 38 spawn-or-console of 922 blocks; the
would-have-been stdout radius population is 24 equality pins + 37 of
206 e2e expects, all immune by the existing strip; and one concrete
consequence: `Extern.exists "fzf"` misses `fzf.exe`, so Windows
Ctrl+R would silently degrade to the built-in search until the
PATHEXT arm lands. Non-AOT self-contained win-x64 builds clean at
82 MB / 208 files — the number that makes the AOT single-binary story
worth a Windows build host.

The runbook (paste-and-run on a Windows host with the .NET 10 SDK;
fills the BLOCKED-HERE cells):

    # §1 build + size + startup
    git clone <repo> weir; cd weir
    dotnet publish src/Weir -c Release -r win-x64
    dir src\Weir\bin\Release\net10.0\win-x64\publish\Weir.exe   # size vs 7,696,168 B Linux
    .\Weir.exe --version; .\Weir.exe -e '(1 + 2) * 2'
    1..20 | % { Measure-Command { .\Weir.exe -e '1 + 1' } } |
        measure -Property TotalMilliseconds -Average             # vs ~5.4 ms Linux
    # §2 the suite: dotnet test tests/Weir.Tests -c Release
    #    (spawn/tty failures expected; parse/check/eval failures are FINDINGS)
    # §3 REPL editor in BOTH Windows Terminal and classic conhost:
    #    type, arrows, Tab, Enter — record paint or garbage per host
    # §4 tree-kill: a weir script spawning
    #    powershell -c "Start-Process notepad; Start-Sleep 60" via | complete,
    #    Ctrl+C weir, then: tasklist | findstr notepad  — record the survivor
    # §5 argv integrity: .\Weir.exe -e 'echo "a b" "-rf" "c""d"' through a
    #    real .exe echo — compare word-for-word

## TOML adopted — the spike's numbers became a ruling (2026-07-30)

The spike said "TOML is the one worth a user decision"; the user
decided, hours later, and the swap cost one sitting — which is the
phase argument working exactly as designed: the JSON config had
shipped DAYS earlier with zero users, so config.json → config.toml is
an amendment, not a migration. After publication the same change
would have needed a compat story.

Measured on weir itself, the spike's numbers held: 0 new AOT warnings
(the 3 pre-existing FSharp.Core/FParsecCS ones), +324,928 bytes
(spike predicted +329.5 KB on its harness), startup unmoved. The
implementation honors the spike's trap finding — `TomlReader` pull
API only, never the readme's `Deserialize` path (reflection, and
poisoned under PublishAot by the package's own buildTransitive). The
reject-unknown posture came through IMPROVED: Tomlyn's reader carries
Line/Column, so a typo'd key now reads
`config.toml:1:1: unknown key 'historySizee'. Did you mean
'historySize'?` — position + did-you-mean, which the JSON version
could not offer. A leftover config.json gets one plain sentence
rather than silence (the reject-don't-guess posture applied to the
transition itself).

## The config-format spike — TOML and strict-YAML measured, JSON stands (2026-07-30)

A throwaway spike (repo copy, branch deleted, nothing merged) replaced
estimates with numbers for the "why not TOML?" question the JSON
config decision left open. Both candidates turned out AOT-clean —
which the estimates did NOT predict — and JSON still stands: TOML
(Tomlyn 2.10.1, `TomlReader` pull API) costs +329.5 KB with zero new
trim warnings and the best error text of the three; strict-YAML
(YamlDotNet 18.1.0, node API only) costs +677.3 KB, also zero
warnings, with the measured subset (scalars, block maps, block
sequences, `#` comments) provably enforceable — aliases resolve
silently but DETECTABLY (the substituted node keeps its `Anchor`),
and the Norway problem never fires because node scalars stay raw
strings until you convert. Startup was unmoved everywhere (5.0–5.4 ms
medians) — the shebang story is safe from a config parser.

The findings that outlive the verdict. Tomlyn's readme path is a
trap under AOT: `Deserialize<TomlTable>` is reflection
(IL3050/IL2026), and the package's own buildTransitive targets flip
reflection OFF under `PublishAot` — so the documented path would
fail at runtime while the `TomlReader` pull API sails through; the
FileSystemGlobbing lesson again, probe-don't-trust, now with a case
where the SAME package contains both a clean path and a poisoned
one. And a weir-relevant caution caught by the spike harness itself:
F# `printfn "%d"` CRASHES under AOT (MakeGenericMethod) — the
repo's Console.WriteLine-plus-interpolation convention is
load-bearing, not stylistic. Verdict recorded in the repl-quality
DECISIONS row with all the numbers, so the next reopener finds a
measurement, not an opinion.

## REPL multiline — the buffer becomes two-dimensional (2026-07-30)

Up-arrow moves within the buffer; on line 3 of a match with a wrong
scrutinee on line 1 you go up and fix it. The load-bearing discovery
was that the REPL had NO continuation machinery to extend — the plan's
"continuation machinery" is the SCRIPT assembler, and the session's
real shape was: put a 2D editor in front of `Script.assemble`, and let
the assembler answer the one question other shells approximate. Enter
submits exactly when the statement is complete: assemble answers
STRUCTURE (open brackets, pending bindings, dangling openers), and a
parse failure AT THE VERY END of the assembled text means "more input
wanted" — `match x with` fails at its end and grows; `let = 5` fails
mid-text and SUBMITS, showing the error, so the user is never trapped
adding newlines to a broken line. Because submission runs through the
same assembler a script uses, the both-ways pin is almost free:
identical lines, identical statements.

In-session decisions, with reasons. STORAGE: one entry per file line,
backslash-escaped newlines (`\`→`\\`, LF→`\n`) — keeps the file
line-oriented and greppable, the cap counts lines = entries, and a
legacy entry containing a weir `"\n"` literal decodes with a real
newline: accepted now, pre-adoption, exactly the amendment-not-
migration window the plan named. DISPLAY: fzf matches per line, so an
entry feeds as its lines joined with ` ⏎ ` — every line stays
searchable (first-line-plus-ellipsis would hide the match arms, which
is where the searching happens) — mapped back through a
display→entry dictionary (identical displays imply identical text, so
the map is lossless). DOWN: the plan leaned no-op at the last line,
but a pure no-op would strand history browsing (Up walks back, nothing
walks forward on a one-line buffer); refined to forward-only-while-
browsing (histIdx < count), no-op on a fresh buffer — the asymmetry
kept where it matters. RESIZE: full repaint on SIGWINCH
(PosixSignalRegistration), best-effort — the climb to the region top
uses pre-resize wrap math. AUTO-INDENT: deferred; the continuation
line starts at column 0 (match-previous-indent is on the still-lacking
list).

The redraw is a region repaint — climb to the region top by the
tracked cursor display-offset, `ESC[J`, repaint each line with its
prompt, reposition by display-row math — and the wrap arithmetic is
where the colorizer's cursor-math scars applied directly: display rows
are ceil((prompt+len)/width), and the line-fills-its-row-exactly case
leaves the terminal wrap-PENDING, which the ceil and the `\r\n`
emission happen to agree about. The continuation prompt is `"  ... "`
— the same width as `weir> ` — bought uniform column math across rows
for one design choice. Pins assert on EVALUATED VALUES at two widths
(30 and 80), never on cursor positions: the driver lesson, applied
before it could bite. Ctrl+J doubles as Alt+Enter's deterministic pty
spelling, which is why the harness can force newlines byte-exactly.

The existing color probe found the completeness rule's one real hole,
by hanging. It types `let s = @"raw` + Enter — an UNCLOSED VERBATIM
string — and under the first completeness rule that grew the buffer
(the parse fails at the end of the text), so Ctrl+D never saw an empty
buffer and the probe waited forever. The rule was wrong, not the
probe: weir strings are SINGLE-LINE (all four kinds), so a line ending
inside one can never be completed by more input — growing is a trap.
The fix reuses the ONE string-state machine [D:one-scanner]:
`foldOutsideStrings` refactored over a `stringScan` core that also
returns the end-state, and a new `Script.endsInsideString` makes
`bufferComplete` submit such buffers (the parse error shows). A
second quote machine in Repl.fs was the tempting wrong move.

Still lacking, for the next interactive decision: sticky column on
Up/Down (clamp only today); auto-indent to the previous line's indent;
buffers taller than the terminal repaint only the visible portion
(`ESC[A` clamps at the screen top — fish shares the limit); perfect
resize reflow; syntax-aware submit heuristics (a one-armed match
parses complete, so adding arms takes Ctrl+J — the parser's answer is
precise but eager).

## REPL quality — good enough to evaluate weir in (2026-07-30)

Four affordances, none touching semantics: completion, Ctrl+R search,
persistent history, an inert config. The bound was chosen and stated —
NOT a login shell (no job control, aliases, prompt commands,
keybindings). The recorded reason is the sharp one: most interactive
affordances push toward dynamism (ambient state, unqualified names),
each a hole in check-before-effects; completion, history, and inert
settings are the low-hanging fruit precisely because they do not.

The completion win was a compile-order finding, exactly the shape the
plan predicted ("a shape `Complete.fs` cannot serve is a FINDING,
extend it"). `Complete.fs` sat BEFORE `Extern`/`Builtins` in the
fsproj, so it could not see the PATH cache or the command-callable set
— which is why it had never offered command heads. Moving it after
both (nothing between used it) let the ONE brain grow two contexts
without a second implementation: PATH executables + command-callable
at a statement head, filesystem entries for an explicit `/`-or-`~`
word. Members/fields/keywords were already there.

The config's best property is structural, not enforced. "Scripts
never read this file" is the whole point — it is what keeps `weir
script.weir` meaning the same thing on two machines. Rather than
guard it, the `ReplConfig` type LIVES IN the Repl module: a script
never touches Repl, so it *cannot* reach the config. The pin proves
it the honest way — a malformed config plus `weir script.weir` runs
clean, because the broken JSON is never parsed. An in-session cut:
`echoElems`/`echoChars` were dropped from the key set. A config key
that is accepted but does nothing is the config-file's vacuous pin —
the same disease as an unknown key silently ignored — so keys ship
only when they are wired.

fzf was absent in the harness, which forced a better test than a real
fzf would have: a STUB `fzf` on PATH (`head -1`) makes the
spawn-feed-select-restore mechanics deterministic and
environment-independent, and a second run with fzf off PATH exercises
the minimal fallback. Both under the pty driver, asserting on the
re-evaluated line, never on redraw escapes.

Dogfooding found the collision the stub could never see (2026-07-30):
weir's glyphs ARE fzf's query operators. In fzf's extended-search mode
(the default) `^` is a prefix-anchor, so searching `^ls` EXCLUDES
every history entry that literally starts with the force-PATH sigil —
the user got `ls -la` and never `^ls -la | grep X`. And it is not just
the caret: `|` is fzf's OR, `$` its suffix-anchor, `!` its negation —
history lines are weir CODE, dense in exactly fzf's metacharacters.
The fix is a correctness default, not a style flag: `fzfSearch`
hard-wires `--no-extended` BEFORE the config's finderFlags, and
because fzf is last-flag-wins, a user who wants the operators back
adds `--extended` to finderFlags — correctness in code, preference in
config, one knob. Verified against a real fzf (0.55.0, fetched for
the session): the repro (`--filter '^ls'` returns only `ls -la`), the
fix (returns both caret variants, excludes the plain one — literal
semantics), the override path, and a live pty run (Ctrl+R with buffer
`^ls` recalls the caret entry). The committed pin is the stub's argv
contract (`--no-extended` present, before the config flags); the live
fzf run is session evidence, and real-fzf env pins stay on the
still-lacking list. The probe also re-taught the driver lesson: fzf
PANICS on a zero-size pty (`--height 40%` of 0 rows), which reads as
a weir bug until you size the pty — and weir's cancel path held even
through the panic (the line came back unchanged).

## The pipe rule — the right-hand side decides (2026-07-29)

`|` for a program or a reifier, `|>` for a function. The one removal
(`cmd | Seq.head` stops parsing) is what makes the rule total, and it
fixes an asymmetry that had no user-statable justification: you could
pipe a command into a function with `|` but not a value. Reifiers
staying `|` is a THIRD case, not an exception — a reifier is command
grammar, it terminates a chain and produces a value, it is not a
function applied to output.

The surprise was that there are TWO pipe paths, and the rule needed
enforcing in both. The command-CHAIN (`foldChain`) sees each stage's
kind directly, so `pipeSepSpanned` captures the glyph and `foldChain`
checks it against the RIGHT-hand seg — that catches `cmd | fn`,
`cmd |> cmd`, `cmd |> reifier`, anchored on the glyph. But
`expr |> cmd` goes through the `|>` OPERATOR, where `grep foo` parses
as an ordinary application `EApp(grep, foo)` and would only ever say
"unbound grep". That needed a separate, resolver-aware post-parse walk
(`pipeToCommand`): an `EPipe` whose RHS is headed by an external
command, anchored on the offending program. Two mechanisms for one
rule, because the grammar reaches the same shape by two roads.

Testing bit twice with `ls`. `ls` is a builtin VALUE (a lazy FileRow
seq), not an external command, so `ls | succeeds` and `ls |> grep`
route through the value/operator paths, not the command chain — the
early "failures" were bad fixtures, not bugs. Real externals (`git`)
exercised the chain. And the adapters answered themselves: `from
json`/`from porcelain`/`to json` already parse as expression stages,
so the new rule required `|>` for them with no special-casing — the
migration just had to respell every `| from porcelain` in the corpus.
The migration was the real work (12 unit + 13 e2e pins + docs +
examples + an lsp fixture); byte-identical because `|` and `|>` build
the same `EPipe` — only the spelling contract moved.

The sweep missed a surface, and the doc-tests caught it a day later —
the completeness proof working exactly as designed. Two blind spots
compounded: GUIDE lives at `docs/GUIDE.md` and the existence check
looked only at the repo root ("no GUIDE.md" — wrong), and the
migration grep's `| Seq.` patterns missed pipes with a QUOTED ARG
between head and glyph (`git log -1 "--format=%h" | Seq.head`).
`ci/skill-doc.sh` failed on block 59 and the fix swept five GUIDE
sites — plus a finding the grep could never see: four TEACHING
MESSAGES (the `>`/`>>` redirect and composition hints) named the
retired spelling `cmd | File.append` as the fix, an error message
prescribing an error. The message pins held because they were
contains-checks on the stable fragments, not the spelling. Lessons:
a "does X exist" check must search, not stat one path; and a
migration sweep owes a pass over MESSAGE STRINGS, not just code —
the errors are part of the corpus.

## User modules and imports — the five-session arc (2026-07-29)

A file marked `module` becomes importable, declaration-only, not
runnable; `import "path" [as Name]`; access always qualified. The arc
ran design → single-hop → multi-file diagnostics → the graph → the
LSP → `Self.entryPath`, each its own session. Check-before-effects
survived the file boundary by construction: a module resolves and
checks at check time, nothing loads at runtime.

Three findings worth keeping. FIRST, the load-bearing representation
choice: imported types go into the flat `env.Types` under their PLAIN
name, not qualified `Git.Ctx` keys. That makes the common case — a
module's own helper signatures mentioning its own `Ctx` — resolve with
ZERO scheme-rewriting, because the schemes already say `TNamed "Ctx"`.
The cost is a session-1 collision guard (two modules exporting the
same type name is a check error). Qualified keys would have been
"cleaner" and cost a rewrite pass over every imported scheme; plain
keys were the right trade.

SECOND, the named record literal (`Git.Ctx { .. }`, decision 7) needed
NO new AST node and NO grammar change. `Ctx { .. }` already parses as
`EApp(EVar Ctx, ERecord)`, and a record type name is never a VALUE, so
intercepting that shape in the checker fires ONLY where a bare
application would already error — `Some { .. }` (a ctor applied to a
record) is byte-identical. The parser route was tried first and
abandoned: `Ctor { record }` is legal weir, so `Upper {` cannot be
grammatically claimed for named records without knowing types, which
the parser does not.

THIRD, `assumeResolver` bit hard. `weir check` treats unknown letter-
initial words as commands so uninstalled tools still parse — but that
read the UPPERCASE type name in `Local { .. }` as a command, so a
named literal type-checked at RUN (the plain resolver) yet errored
under CHECK. Commands are never a declared type or module; excluding
`tenv.Types`/`tenv.Modules` from the assume-command rule fixed it. The
lesson repeats: a parse-shape that works one way and not the other is
usually a resolver disagreement, not a grammar bug.

The diagnostics arc (sessions 2–3) taught its own lesson about notes.
A module error reports at its OWN file with an "imported here" note at
the import line; through a transitive graph the note kept landing in
the wrong file, because each level re-created it. The fix is that the
OUTERMOST importDiag wins — every level overwrites the note with its
own import position, so the entry's (the last to run) survives, and
its file matches where analyzeLines publishes it. For the graph, the
cache is keyed by normalized absolute path (a diamond checks its
shared module once, re-aliased per site via `NaturalName`) and the
chain is the DFS path (a repeat is a cycle, rendered as the loop). No
re-export means a module's exported types come from its Body's own
decls, NOT the env-diff — the diff would leak whatever it imported.
And the LSP shipped re-check-all-open-docs rather than reverse edges:
the plan named reverse edges, but eager re-check achieves the same
observable guarantee with no stale-graph bug, so the optimization is
deferred, stated.

## /// feeds --help — the Doc attribute retires (2026-07-28)

`--help` used to read its text from a `[<Doc "…">]` attribute while
hover read the `///` doc — two stores for one fact, free to drift.
Now there is one: `--help` takes the `///` doc's FIRST line, and
`[<Doc>]` is gone from the registry (a stale one is now the ordinary
unknown-attribute error). This is the 6th time the project chose an
invariant-by-architecture over a pin (after [D:doc-comments]'s five):
hover and help read the same source, so "they agree" is not a test —
it is the representation. A `[<Help>]` rename was on the table and
rejected: it keeps two sources that can still disagree, which is the
disease.

The interesting part was the bridge. Docs live OUT-OF-BAND
([D:doc-comments]) — position-keyed, never on the AST — because
erasure should be a property of the representation, not a discipline.
But `--help` renders at EVAL off the `RecordDef` that `Args.load`
captured at CHECK, and that def has no docs. Rather than thread docs
through the typed tree (undoing the whole point), the runner enriches
`env.Types[decl].Docs` in `Script.run`'s fold the instant a `type`
decl checks — which is BEFORE the following `Args.load` reads it, so
the captured def already carries them. `RecordDef.Docs` is check-time
data, erased exactly like `Attrs`; only the run path fills it (hover
never needed it, check never renders help). Both help tiers merge in
`scopeDef`; the alignment lint still governs a crooked doc.

Migration bit back once: a `///` only attaches when it sits DIRECTLY
above its field. An attribute line between doc and field breaks it,
and an inline `{ /// text` is a trailing comment, not a doc line — so
every inline `[<Doc>]` record had to reshape to `[<attr>]` / `///` /
field on their own lines. Not a text swap; the corpus, the docs, and
the e2e fixtures all reflowed.

## Annotated signature rendering — hover shows parameter names (2026-07-28)

A builtin hovered as `string -> string -> bool` — which argument is
which was invisible. Now it hovers as the annotated DECLARATION form:
`Str.isMatch (pattern: string) (subject: string) : bool`. The form was
chosen over two alternatives because it is the only one that lies about
nothing: a `pattern: string -> …` pseudo-type is not weir syntax (paste
it into source, get a parse error), and a man-page usage line is weaker
and not copy-pasteable. Valid F# declaration syntax, and the shape a
user writes when wrapping a member — for an F# sibling, that coherence
is the whole argument.

One rule, degrading gracefully: annotated when parameter names are
KNOWN, arrow otherwise. Names come from two places — builtins carry a
`BuiltinDoc.Params` field (SEPARATE, never parsed from the prose, which
is D1's F#-literal trap again), and user declarations read them off the
binder-span lambda chain. Everything else — lambdas, expressions,
partial applications, unnamed builtins — falls back to the arrow, and
every fallback is pinned, not silent. A zero-param value is
`Self.pid : int`, no empty parens.

The load-bearing distinction is presentation vs computation: the arrow
`formatTy` is untouched, so type ERRORS quote exactly what they did
before (pinned byte-identical). `formatSignature` is a second view of
the same type, for hover and completion only — not a second type
system.

This also amends `///` half 2's template before its writing pass runs:
name every parameter (`subject` beats `stringToMatch` — the naming
forces clarity, and it is the part of the docs read most), keep the
signature declaration-shaped even for data-last members
(`Seq.choose (f: 'a -> Option<'b>) (xs: seq<'a>)`), and let the EXAMPLE
carry the piped idiom (`xs |> Seq.choose f`). Signature says what it is;
example says how it is written.

## Hover completeness — reference sites, patterns, silence (2026-07-28)

Five dogfooding reports against the shipped hover, all one diagnosis:
half 1 wired hover at DECLARATION sites; every report is a hover at a
REFERENCE, a PATTERN, or a TOKEN, where `nodeAt` finds nothing and falls
back to the enclosing expression — whose type is usually `unit`/`int`.

Group 3 first, per the plan — cheapest, highest value: a wrong `unit` on
the most-hovered tokens teaches the user that hover lies. `onSilentToken`
returns null for a keyword / operator / punctuation / whitespace / `_`,
BEFORE the enclosing-node fallback, scoped by what the cursor is ON. The
one subtlety: `true`/`false` are in the keyword set but are LITERALS, so
they answer.

Groups 1a/1c/2 all reduce to "the machinery exists, only the wiring is
missing." 1a composes with `definitionFor` — a usage / field / case
reference resolves to its declaration site, and the `///` doc is read
there (so `hoverType` moved BELOW `definitionFor`); shadowing is free
(innermost-wins). 1c: a field in a LITERAL is only a `TERecord` node, so
it showed the record type — resolve the field's own type off the record
def. 2: a union case in a pattern is no expression node, so key the
constructor signature off the WORD (skipped at the type decl, where
declHover renders it); the payload binder is no node either, but its USES
carry the type (`varUseType`) — which had to beat the enclosing-match
fallback, so the order is exact-TEVar-use, then use-of-the-word, then the
enclosing node, and the KLet scheme fires ONLY on the binder name.

Group 1b (module-member hover) was already shipped in half 2 — the plan
named it the half-2 prerequisite, but half 2 wired `TEVar -> builtinDocs`
already. So half 2 did not, in fact, wait on this.

The matrix (site / expected / before / verdict):

| hover site | expected | before | verdict |
|---|---|---|---|
| `let`/`if`/`then`/`else`/`in` | nothing | enclosing type | FIXED G3 |
| `\|` / `->` / `=` / `;` / brackets | nothing | enclosing type | FIXED G3 |
| `_` wildcard | nothing | enclosing type | FIXED G3 |
| a binding usage | type + decl doc | type only | FIXED 1a |
| a module member | type + doc | type + doc | DONE (half 2) |
| field access `t.f` | field type + doc | field type only | FIXED 1c |
| field in literal `{ F = }` | field type + doc | record type | FIXED 1c |
| pattern constructor | signature + doc | nothing | FIXED G2 |
| payload binder | its own type | nothing | FIXED G2 |
| an identifier / literal | its type | its type | UNCHANGED (negative) |

Pinned per group, nulls included, at least one per group over the
PROTOCOL (the helix false-alarm precedent: a null at a space is a correct
answer). 915 unit + 4 protocol probes; e2e + timing green.

## Result removed — the apologizing doc (2026-07-28)

Documenting the builtin types in half 2 surfaced a smell: `Result`'s doc
had to apologize — "you construct it; no builtin returns one." A type
whose hover explains why it's useless is a type that hasn't earned its
place. So: is it needed? No. Zero uses in scripts, examples, corpus, or
docs; no builtin produces or consumes one; and weir's error model has no
gap for it — expected absence is `Option` (the `try*` family), command
outcomes are `Completed`/exit-codes/`succeeds`/`orFail`, and the
unrecoverable ones raise (`fail`, `Seq.head []`, overflow) to exit. A
recoverable-error-with-a-payload channel has no home: weir routes
"recoverable" to Option and "structured" to Completed. Option earns its
place — `tryFind`/`get`/`tryToInt` return one; Result never did.

Its one real job was a TEST FIXTURE — a single test exercised a 2-param
generic union with cross-arm inference, and Result was the only 2-param
union in the tree. That is not a reason to ship a type; it is a reason to
declare `type Either<'a,'e> = Left of 'a | Right of 'e` in the test env,
which is what the coverage moved to (the `Proc` ctor-values pattern
already existed). A type should not exist in the language solely to be
checked. Removal fits the subtractive line — computed-head tier,
reify-builtins, `sh`, measures — and teaches itself: a stale `Ok`/`Error`
is an unbound-constructor error. Reversible if a Result-returning family
is ever designed; none is.

## Builtin docs — half 2 (2026-07-28)

The content half: every builtin member gets a hover/completion doc,
reusing half 1's out-of-band representation (`Builtins.builtinDocs`,
keyed by the name at a use site; Value/Eval/Check never see it).

D1 — whether examples are EXECUTABLE — was the gating decision, and the
answer came from a mechanism the plan didn't weigh. The plan framed it
as (a) extend the extractor to parse example lines out of F# string
literals in bash, vs (b) mirror the example in a doc-tested markdown
file and have the hover POINT at it; it leaned toward (b) because the
F#-literal parsing (escaping, multi-line) is fiddly. But the example is
already weir CODE the registry holds — so weir runs it: an Expecto test
iterates `builtinDocs` and runs each Example through the same check+eval
path, asserting success. That is (a)'s prize (rot-proof, on the build's
critical path) without (a)'s cost (no bash, no F#-literal parsing) and
without (b)'s loss (the example stays IN the hover, not a pointer to
elsewhere). A rotted example now fails the suite by name.

The template is fixed: Summary (imperative, never restating the
signature — half 1 renders the type first), an optional executable
Example, an optional Pointer that names the LAW a member obeys (quoted
from the ledgers: force memoizes for reuse+timing, pmap is
ordered/eager/first-error, Env.load names the field law, Args.load the
three shapes). Members whose only honest example touches the filesystem,
stdin, or raises (File.read/write, Env.fromFile/load, Args.load, Self.*,
fail, exit) are summary-only — the template allows it, and a fake
one-liner that returns `false` teaches nothing.

Hover resolves the name off the typed node — TEVar for members and bare
names, and the boundary forms explicitly (TEEnvLoad/TEArgsLoad/TEFrom/
TETo key into the registry, since they carry their own node, not a
TEVar). Both surfaces were verified over the PROTOCOL (initialize +
didOpen + hover / completion against `weir lsp`), because "the doc text
reaches the client for a builtin" is a distinct path from the pure
function the unit test pins.

Coverage is FULL (~97/97): Seq 30, Str 19, Option 3, Env 6, Args 3,
File 4, Path 6, Self 4, bare 7, boundary 5, reifiers 4, types 6. The two
follow-ups the first pass named turned out to be the same shape of gap
the boundary forms were — a node kind hover didn't yet key on — and
closed the same way. The reifiers DO leave a TEVar node after all:
`complete` desugars to the un-typeable `|completed` key (plus Env/In
twins) carried at the surface span, so `reifierSurface` maps that back
to the surface name. Builtin types are the one genuinely node-less case
(a type name is no expression), so hover adds a word-at-cursor fallback —
safe because type names are uppercase and distinctive. One find while
enumerating types: `Result` is Prelude-declared (`Ok of 'a | Error of
'e`) but no builtin returns one, so its doc states that plainly rather
than pretend a source ("you construct it; no builtin returns one").

## `///` doc comments — half 1 (2026-07-28)

The machinery half of PLAN-doc-comments: `///` attaches to the
declaration below it, shows on hover (type first) and in completion,
at the five declaration positions. Half 2 (builtin docs) consumes it.

Two verifies set the shape, reported before any edit. (1) `///` already
lexes as an ordinary comment everywhere — `stripComment` cuts at the
first `//` regardless of a third slash, and comment-only lines drop
transparent — so `///` was ALREADY inert in a running script; the
feature is an ATTACHMENT change, not a tokenizer change. That one fact
rippled through the design: because `///` is dropped before assembly,
attach + hover + completion need ZERO assembler changes. (2) the
doc-test extractor scans markdown ```weir fences from SKILL/GUIDE only —
making `///` examples executable is a real extractor addition, deferred
to half 2 as its template input.

Storage was the fork, and the answer inverted the plan's letter to keep
its spirit. The plan said "the AST carries them, the typed tree mirrors
them" (the binder-spans precedent). The user's argument stood the
comparison on the project's own criterion: **erasure is better as a
property of the representation than as a pin**. Store docs OUT-OF-BAND —
a pure `Script.docAttachments` pass over raw lines, keyed by the
physical (line,col,len) of the documented name — and Value/Eval/Check
never see a doc, so the plan's "pin byte-identical runtime" guards
nothing because nothing can observe it. Fifth instance of
invariant-by-architecture-over-pin (one-pipeline, emergent-bound,
one-spawn, capture-views). A doc is metadata about a source LOCATION,
not program meaning; the binder-spans churn bought a field the resolver
READS — docs would buy a field no arm reads. Key by position, never
name (shadowing, inner lets, two records with a like-named field).
Stated residue: a future `weir doc` command is fine (same pass), but a
typed consumer wanting "the doc for this resolved symbol" goes through
the key, not the node — chosen, not defaulted.

The alignment guard got the same treatment. The plan wanted `///` woven
into the entry-start / attribute machinery, citing the `>]`-dangle
precedent (a misaligned entry once surfaced a misleading RUNTIME error).
But that precedent is about PARSE-affecting lines: an attribute a column
off changes what the assembler builds. Docs are inert — a misaligned
`///` cannot mis-parse, nothing for the parse-time machinery to protect.
Misalignment is a LINT (`Script.docMisalignments`, both directions
pinned), not assembler surgery — lower risk AND more correct. fmt
canonicalizes each `///` to its following declaration's formatted anchor
as a post-pass; docs being transparent to assembly, the re-indent never
changes the logical lines, so fmt's safety re-check holds and it stays
idempotent.

Hover at the type/field/case positions needed real work regardless of
storage — those aren't expression nodes, so `nodeAt` finds nothing.
`declHover` reads the `KType` decl and renders the type's structure, a
field's type, or a case's signature — type first, then the doc.

Coloring: a distinct `comment.line.documentation` scope, all three
grammars (verified on the vscode-textmate and tree-sitter engines),
inventory 24->25. Param docs parked (structured-doc design; params
already hover their types). 907 unit pins; the erasure "pin" is the
absence of one, by design.

## The Self module (+ pid) — introspection grouped (2026-07-27)

`args`/`stdin`/`scriptPath` moved under a `Self` module and `pid`
joined them, so `let mypid = $(sh -c 'echo $PPID') |> Seq.head` becomes
`acquire $"{Self.pid}"` and the tool that motivated the feature
(fuzz.weir) deletes its workaround. Feasibility was the easy
part — module members are already mangled `"Module.member"` value
keys, so a value member is no different from a function member; the
per-run injection just moved from bare `Values` to a `Modules` entry
+ mangled `Values` in baseEnvs.

The one real risk was the plan's flagged finding, and re-verifying it
with a PIN (not a grep) EARNED its keep: `Args.load`'s script-only
gate keyed on the bare `args` binding's presence, not just
Session.ScriptArgs — so removing `args` made `Args.load` wrongly
report "script-only". Caught the instant the argv tests ran; fixed by
gating on the `Self` module's presence instead. The lesson banked: a
"reads X directly" source claim is a hypothesis until a pin proves the
absence of a second reader.

Two smaller catches: `Self.pid` is an int (kept — a PID reads as a
number; the type-vs-string question was weighed and the payoff of
string was one `show`, erased anyway by interpolation), so a command
uses it as `$"{Self.pid}"` — argv is strings, the hole converts inline;
and a bare name at STATEMENT HEAD is a command candidate
(cmd-not-found), so the move-to-Self teaching fires
in EXPRESSION position, where it matters. The clean break needed no
new machinery — the existing bare-name->module-member teacher already
says "'args' moved into a module; use 'Self.args'".

## `| complete` joins the discard family — the one-cell gap (2026-07-27)

Live dogfooding: a bare `git status | complete` statement was
ACCEPTED while `| succeeds` (bool) and `| exitCode` (int) were
rejected as discards. The discard check (Script.checkStatement, the
`SCmd` arm) matched `TBool` and the exit-code spine but not the
`Completed` record — `complete` predated the exit-code session and
never got its cell. Fix: one arm, keyed on the distinctive value type
`TNamed("Completed", _)` (a type match, not a spine check — it covers
every route uniformly, unlike `exitCode` which needs the spine because
`int` is ambiguous). Message in the family voice with the per-cell use
clause: "computes a Completed record and discards it — bind it (let r
= ... | complete) or read a field (.exitCode, .stdout)"; caret at the
reifier stage (1:14, matching `succeeds`, pinned exact — the
anchor-before-read lesson). SWEEP confirmed: four cells, four stated
positions (SEMANTICS reifier law) — `orFail` is EXEMPT because its
result is unit (the assert idiom), verified by type not assumed. The
binding path (`let r = ... | complete`) is a let-RHS, never reaches
the SCmd arm — unchanged, pinned; value-headed `... | complete` was
already caught by the expression `discardError` (uniform with its
value-headed siblings); sibling pins unmoved.

## The last keyword slot — let-destructure, closed by a lexical scan (2026-07-27)

`let (rec) = 1` was the one slot left. The draft priced two costly
routes (parse a pattern in the guard, or narrow SLetPat's attempt —
predicted to stop, since that attempt IS the plain-vs-destructure-vs-
letin discrimination). The user added option 1b, the cheapest that
works: finding a keyword in an identifier slot between `let` and the
top-level `=` is LEXICAL, not structural — so extend letKeywordGuard's
bareword scan to skip pattern delimiters `()[]{},;_` and stop at `=`,
no pattern parse at all. It stays outside SLetPat's attempt (a stmtWith
alternative), so the fatal escapes. Landed clean: `let (rec)`→1:6,
`let (a, rec)`→1:9.

The one bite: the scan flagged `true`/`false` (both live in the
keyword set), but those are LITERAL patterns — a refutable-binder
check error, not a name error. Excluded them per patWord's rule; the
existing refutable-binder pin caught it immediately. Option 2 was not
attempted — the prediction that it stops held, and framing it as
expected (the C-neg-int way) kept the session from a heroic attempt.

That closes the whole message-domination arc: every keyword-name slot
— top-level binder, params, record-decl field, record-literal field,
match-arm/lambda/param/destructure pattern binders — now dominates.
ZERO open keyword slots.

## The last two keyword-domination slots (2026-07-27)

Both keyword slots the A+B session left as findings, now closed
(bar one residual). ITEM 2 (pattern binders): `patWord`'s keyword
check dominates outside its own attempt — and the predictor called
it exactly. It surfaces where the context is committed: match arm
(arm-commit `|`), lambda (`fun`), curried param — clean; swallowed
only for `let (rec) =`, inside SLetPat's plain-vs-destructure attempt.
That last one is the single remaining finding (a pattern-peeking
guard or narrowing that commit — out of remit). ITEM 1 (record-literal
field name) was PREDICTED to possibly need the arm-commit boundary; it
did NOT — a `keywordFieldGuard` placed right after `{ ws`, before the
literal-vs-update choice, dominates `{ <keyword> = ` while the
commit-check stays untouched (decision inside the attempt so real
fields backtrack; `{` already committed the atom so the fatal
escapes). The prediction was cautious and the fix was cleaner — the
inverse of C, where the prediction was right in direction but the
mechanism cleaner still.

That closes the keyword-domination class down to ONE slot:
let-destructure. The whole message-domination arc is now: name,
param, decl-field, record-literal-field, match-arm/lambda/param
pattern binders — all dominate; only `let (rec) =` and the two
already-noted commit-boundary shapes remain, each a stated finding.

## The anchor residue D — the field-type mismatch sibling (2026-07-27)

Session D's residue, closed. Session D anchored the cross-statement
NO-FIELD error at the access with the meet as a note; the sibling —
right field, WRONG type, cross-statement — still reported at the meet,
an asymmetry worse than either behavior alone. The load-bearing
diagnose-first question ("does the mismatch flow through
dischargeRow's Some-arm, or unify earlier?") answered YES: the
Some-arm's `bind … fspan …` produced "expected int, got string" at the
meet, and the origin was already populated (same access mechanism as
no-field). So the fix factored the origin-positioning into a shared
`atAccess` helper (RowOrigins + TypeError.Origin + meet note, all
Session D machinery) and applied it to both arms. Cross-stmt mismatch
moved 5:1 → 3:25; within-statement byte-identical (no origin → no
note); no-field unchanged; oracle green.

That closes the whole anchor residue (A/B/C/D). Findings that remain
their own future blesses: the record-LITERAL field-name keyword and
parenthesised-param-pattern keywords (both inside recordLit's
field-vs-update commit-check [D:arm-commit]).

## The anchor residue C — neg-int, the predicted-hard case that wasn't (2026-07-27)

Ran alone as the plan required. The prediction ("the `-` is the unary
minus's contested spot, so seek-back buries") pointed the right way
but its stated PREMISE was wrong — the THIRD claim-not-fact correction
of this arc (after the sweep's "ambiguous" and A's foldChain).
"Parsed twice by negIntLit and prefix-minus" is false: `negIntLit` is
RANGE-ONLY; a plain `let x = -N` goes through `negAtom` (prefix minus).
The real swallower was `negAtom`'s OWN `attempt` eating the operand's
int-out-of-range fatal — the exact property this residue had just
documented, biting one function over.

So the fix was the property applied, not the plan's three options
(option 1 breaks `-5`/`f -1`; option 2 breaks range steps): narrow
negAtom's attempt to the prefix DETECTION only, so the operand parses
outside it and its fatal escapes. Because that narrows an EXISTING
attempt rather than adding a consumed-separator commit, and because
the whole risk surface is BYTE-IDENTICAL (`a - 1`, `a-1`, `-5`,
`[10.. -1 ..8]`, and the spacing-sensitive `f -1`=99 application — all
pinned before/after), the plan's stop-condition never triggered. The
overflow now reports at the digits (1:10), clean. Lesson banked: a
prediction can be right in direction and wrong in mechanism — diagnose
even the cases the plan calls known-hard.

## The anchor residue A+B — foldChain + param/field keywords (2026-07-27)

The A+B session of PLAN-open-findings. First to run, so it OWES the
property: **a fatal inside an `attempt` is not a fatal** — FParsec
backtracks fatals, so a `failFatally` in speculative attempt/choice is
advisory. Now in LEXICON and PROCESS with its three sightings
(arm-commit, the depth guard's thrown `DepthExceeded`, and this
session's reserved-word gate firing outside topLet's attempt). The
worst failure mode it names: a guard placed INSIDE an attempt looks
implemented and passes any not-worse test while doing nothing — so B's
per-slot work started by identifying each slot's attempt boundary.

A (foldChain): the sweep called this anchor "ambiguous" — WRONG on
re-read, recorded (a finding is a claim, not a fact, and that one was
made under sweep pressure). foldChain already holds each segment's
span; its one real error branch (a reifier not following a single
external command) is owned by the marker. Widened the error to carry
the span, both callers `failFatallyAtCol` — the marker has no
surviving competitor, so it dominates clean.

B (name-required slots): PARAM keyword dominates via `letKeywordGuard`
EXTENDED to scan the name + simple-ident params (it fires outside
topLet's attempt — the boundary that makes it work); record-DECL field
via `fieldNameDecl` (typeDecl commits, the fatal propagates). The
keyword×position matrix (if/then/else/elif/match/when/fun/let/in/type)
confirms every keyword still heads its own construct. FINDINGS left:
the record-LITERAL field (`{ let = 1 }`) and keywords inside a
parenthesised param pattern both sit inside recordLit's
field-vs-update commit-check [D:arm-commit] — reworking that boundary
is out of the residue's remit. neg-int (C) and the field-type mismatch
(D) are their own sessions still.

## Message domination — the teaching fatal wins its spot (2026-07-27)

Finding-class (b) from the anchor sweep, closed. Those sites had the
CORRECT caret but their teaching was buried under a raw FParsec
expecting-list, because a NON-consuming `fail`/`failFatally` fails
where sibling alternatives also failed and FParsec MERGES their
expected-set. Fix: the same consume-then-anchor as the caret sweep —
consuming the trigger clears the competitors, then `failFatallyAt`
seeks back.

SPLAT family (splat head `$@`, splice/splat mid-word ×2): these spots
are always errors, no fall-through, so consume-then-anchor dominates
cleanly. RESERVED-WORD in the binder name (`let function/rec/mutable`)
was the CRUX: the shared `ident`/`notKeyword` must stay NON-fatal
because a keyword there falls through to its own parser (`many1
postfixAtom` stops at `in` so `1 in y` closes its let; `let x = if …`
reaches ifExpr). A blanket fatal breaks the grammar. So a dedicated
`letKeywordGuard` gates the domination to the `let <name>` slot and
fires OUTSIDE topLet's `attempt` — FParsec's `attempt` BACKTRACKS
fatals too, so a fatal inside it is swallowed; the guard peeks, and
engages only when the name is reserved, so real binders fall through.

Two findings remain (their own bless): (i) **neg-int-out-of-range**
is contested between `negIntLit` and the unary-minus operator's
operand — both hit int-out-of-range (cols 9 vs 10), neither
dominates, so the clean message wins and the caret drift stays; (ii)
**reserved word in PARAM/FIELD position** (`let f rec`, `{ let = 1 }`,
`type T = { let: int }`) fails at distinct sites with a generic
"expecting '='", not the keyword teaching — out of the binder-name
scope fixed here. Finding-class (c), the foldChain reifier drift, is
untouched.

## The bare-pipe caret, and the anchor-before-the-read sweep (2026-07-27)

The parked bare-pipe narrow question ("where should a bare-`|` fatal
point when the LHS spans lines?") got its receipts — and they
DISSOLVED the question. The multi-line record LHS I'd sketched as
having three defensible carets behaves IDENTICALLY to the flat
pipeline LHS: LHS shape and line-span are irrelevant. There was no
position law, just one off-by-one — the caret sat on the whitespace
AFTER the `|`, everywhere, because `failFatally` fires at the current
stream position and the `|` (plus its trailing ws, which crosses
physical lines in an assembled statement) had been consumed first.

FIX SHAPE (`failFatallyAt`/`failFatallyAtCol`): consume the trigger
(this CLEARS the competing "expected" errors that sit at that spot —
a plain lookAhead-restore keeps the fatal non-consuming, so `<|>`
merges them back into a dump), then Seek the stream back to the
captured anchor and raise the fatal there. `run` reads err.Position,
so the seek is the whole mechanism. Caret now lands ON the `|` in all
three shapes; message text unchanged.

### The sweep — anchor-before-the-read's SECOND sighting

The first was caseDecl (a lowercase union case validated AFTER
consuming the word + trailing ws, landing 39:5 not 38:7). Two
instances is this project's convert-a-lesson-into-a-sweep threshold.
Every consume-then-fail parser site, with the minimal shape's caret:

| site | trigger | expected | before | verdict |
|---|---|---|---|---|
| bare-pipe `\|` | the `\|` | on `\|` | ws after (`\|`+1) | FIXED |
| int-out-of-range (expr) | the number | its start | its end | FIXED |
| int-out-of-range (pattern) | the number | the literal | its end | FIXED |
| measure (expr `5<m>`) | the literal | its start | past it | FIXED |
| measure (type `int<m>`) | the `<` | the `<` | past it | FIXED |
| range step zero | the `0` step | the step | range end | FIXED |
| duplicate parameter | 2nd binder | 2nd binder | the `=` | FIXED |
| `;` does-not-chain (warn) | the `;` | on `;` | on `;` | CLEAN |
| `>` / `>>` redirect (warn) | the redirect | on it | on it | CLEAN |
| discarded exitCode | the reifier | on it | on it | CLEAN |
| cmd-not-found `^head` | the head | on head | on head | CLEAN |
| depth-guard | over-deep node | node span | node span | CLEAN |
| neg-int-out-of-range | the number | on `-` | its end | FINDING (a) |
| splat head / mid / bare `$@` | the splice | on it | on it (ok) | FINDING (b) |
| reserved function/rec/mutable | the word | on word | on word (ok) | FINDING (b) |
| multi-external / reifier fold | the segment | ? | chain end | FINDING (c) |

Denominator: every `failFatally`/`err`/warning site that emits a
located diagnostic. 7 caret drifts FIXED via the shared anchor
helper; 5 already clean; 3 finding-classes left for their own bless:

- **(a) neg-int-out-of-range**: the anchor is the `-`, but that is
  the unary-minus operator's contested spot — seeking there drops the
  fatal where the opp also failed, and FParsec MERGES the
  expecting-list into a dump. So the clean message wins and the caret
  drift stays; coupled to (b). The seek-back trick is clean ONLY when
  the anchor position has no competing parser (why every FIXED site
  works and this one doesn't).
- **(b) message-domination class**: splat ×3 and the reserved-word
  errors have CORRECT carets, but their non-consuming `fail`/
  `failFatally` merges competitors into a buried "Other error
  messages" dump. Distinct from caret drift — the caret is right, the
  MESSAGE is polluted. Its own class (make the teaching fatal
  dominate), out of this sweep's position-only scope.
- **(c) multi-external / reifier foldChain**: the caret drifts to the
  chain's end, but foldChain builds the error from a computed string
  with no per-segment positions, and WHICH segment owns it is
  ambiguous. Needs foldChain to thread positions — a restructure,
  reported not guessed.

### The park retires

The bare-pipe park [was: "where should a bare-`|` fatal point when the
LHS spans lines?"] is RETIRED with that archaeology: the question was
"which line?"; the answer was "off by one column, everywhere." No
position law existed to decide. Its strict-span acceptance is met.

## The sibling sentinel — command mode stops at the machine's ';' (2026-07-27)

Session E's blessed successor (Option B). The assembler now joins
STATEMENT-siblings with a machine sentinel (U+001F), not `;`. A
command-first body (`cmd` then `let x = … in body`) used to run
command mode past the sibling boundary, mis-parse, and fail at the
joined line's EOF with a raw expecting-list; now command mode stops
at the sentinel, `seqExpr` sequences on it compositionally, and
topLet's RHS command branch folds a real `ESeq(ECmd, …)`. The repro
reports AT THE COMMAND HEAD (the ordinary seq-unit discard rule),
no EOF note, no dump.

THE ENGINEERING that made this low-risk: the sentinel join is the
SAME 3-char width as `" ; "`, so `joinedStart` and every
segment-table offset are byte-identical — `translate` did not move.
The strict-spans deep run (4000+ cases, on by default) is the
acceptance and stayed green. Only the statement-sibling join
changed (`JStmtSibling`); bracket field/element `;` (`JSibling`) and
district-internal `;` (`JDistrictSibling` — district lines are
commands, no inner `let` possible, so the bug can't occur) stay
literal `;`. That scope split is why the ~20 assembler-text pins
moved (statement siblings) while the record/list/district ones did
NOT — the test helper `asmSib` rewrites a display `;` to the
sentinel for exactly the statement-sibling pins.

USER-`;` IS BYTE-IDENTICAL (the whole reason B beat A): `git status
; echo hi` on one line still parses as ONE command with a `;` argv
word and still warns "';' does not chain" — the machine sentinel is
a DISTINCT token, so machine structure and user text never compete.
UNPRODUCEABLE: assemble rejects any source line carrying the
sentinel ("illegal control character"); it never surfaces
(cleanParseDump scrubs it to `;`; fmt builds output from the
original physical lines and only uses the sentinel-bearing `ll.Text`
as an internal structural oracle — the equality check and the
respace shape guard, which parses the sentinel as a separator and
renders a sentinel-free sexpr). FMT QUESTION ANSWERED: fmt can SEE
it but never RENDERS it — it dies at the assemble→check boundary.

FUZZER: the `joinSiblings` transform now routes every block-form
print-sibling THROUGH the sentinel and asserts it stays equivalent
to the single-line `;` form (the load-bearing check). It stays
print-only BY NECESSITY, not convenience: for a bare command
sibling the two forms are DELIBERATELY not equivalent
(block-sentinel = real ESeq; single-line `;` = swallowed argv), so
the command-first shape cannot be a metamorphic pair — pinned
directly in Tests "Sibling sentinel" and e2e instead. GRAMMAR.md
invariant-1 carries that note.

## Diagnostics arc, Session E — DIAGNOSIS, stopped for a grammar bless (2026-07-27)

The backtrack-to-EOF dump is NOT a typo-diagnostics problem. `head er`
alone parses clean (both words are command tokens). The failing shape
needs a second ingredient, minimal:

    let f t =
        git status          -- any bare EXTERNAL command, FIRST in the body
        let e = "x"
        print e

→ `4:12: error [parse]: Note: The error occurred at the end of the
input stream. Expecting: …`. Bisected and instrumented (dumped the
assembled logical line): the assembler joins to
`let f t = git status ; let e = "x" in print e`. topLet
(Parser.fs:1531) parses the RHS as `cmdLineLetRhs r' <|> (seqExpr …)`,
command mode FIRST — it engages only when the first body token
resolves external. `;` is a cmdWordChar (Parser.fs:1028), so command
mode greedily eats the assembler's sibling `;` AND the inner
`let e = "x"` as barewords, halting at the bareword `in` (the
cmdArgWith-true guard, Parser.fs:1096). It succeeds as a command that
ends mid-statement; the leftover `in print e` fails the `.>> eof`;
topLet is attempt-wrapped so it rolls fully back; the expression
fallback re-parses the whole line as nested `let…in` and runs to EOF.
BOTH plan smells (useless EOF position, raw expecting-list —
cleanParseDump touches neither `Note:` nor `Expecting:`) are
downstream of this ONE over-consumption. Diagnostic proof points:
known-name head (`header …`) → expression mode → parses; command NOT
first → expression mode → clean `unbound 'git'` at the head; command
first → the EOF dump. A bare external command as a mid-body sibling
is effectively an UNSUPPORTED shape — real code puts commands in
`$()` captures or `!e` districts; the `head er` typo demotes a
known-name application into a bare command and lands in the hole.

MERGE ANSWER (the bless note's required question): E does NOT merge
with the parked bare-pipe narrow question ("where should a bare-`|`
fatal point when the LHS spans lines"). That is a `|`-fatal POSITION
law (the fatal is correct, only its multi-line position is open); E
is a command-mode `;`-BOUNDARY mis-parse (no `|` involved, and the
program is one a user reasonably expects to parse). They share only
the furthest-reached arbitration family and the symptom shape; the
fixes are disjoint. The bare-pipe park stays open on its own terms.

STOP per the plan — the honest fix wants a commit-point / grammar
boundary decision, three options:
- A: command args stop at `;` (cleanest; but moves the user-typed
  `;`-does-not-chain warning pins — `git status ; echo hi` would
  parse as two sequenced commands instead of one command + `;` arg).
- B: the assembler injects a DISTINCT sibling separator (sentinel,
  not `;`) so command mode never crosses it while user `;` keeps its
  teaching — preserves the warning; touches assembler + parser +
  translate. RECOMMENDED.
- C: diagnostics-only — detect "command RHS stalled at an `in` it
  can't eat" and report at the command head. Papers over the shape
  without making `cmd ; let…in` PARSE; not recommended.

## Diagnostics arc, Session D — row provenance crosses the statement boundary (2026-07-27)

The bless note's diagnosis question, answered: spans EXIST
(ctx.Rows carries `Ty * Span`; dischargeRow already errs at the
field's fspan) and within-statement reporting was already
origin-exact — the bicep 62/107 case is CROSS-statement. The row
escapes through generalization (schemes carried no spans), and
instantiate re-registered every field with the call-site span:
provenance died at the generalize→instantiate boundary. So D stayed
a session, not a rider — the fix is representation, not reporting.

The design pivot: a raw Span cannot ride a scheme across
statements — it would later translate through the WRONG statement's
line map. Origins are captured PHYSICALLY at the boundary (the
warnings-translation precedent): `Check.toPhys` is an ambient
ThreadLocal translator that Script.checkStatement sets around each
statement's check (try/finally), so `generalizeLet` records
`(field, physLine, physCol, len)` per quantified row var into
`Scheme.RowOrigins`, and the SLet path — which generalizes OUTSIDE
the checker via `generalizeWith` — exports origins alongside the
constraint residue from `typecheckWith` (the residue channel was
exactly the precedent). Instantiate rehydrates into a ctx side
table, mergeRows merges it, re-generalization chains it (a prior
origin outranks the local span, so provenance survives
function-calls-function). The no-field discharge then POSITIONS at
the access via `TypeError.Origin` (physical override, honored in
Script's diag builder) with the meet as the message note:
`T has no field 'BicepPath2'. Did you mean 'BicepPath'? (the value
becomes a T at 5:1)`. Complete/tests see no ambient translator and
record nothing — zero signature churn on typecheck entries.

Movement, all intended-class: the pinned b5 shape (error 5:1 →
3:14-24, the field word itself), and within-statement inner-let
discharge (`let f = fun t -> t.Bogus in f {A=1}`) now also points
at the access with the note. Direct access on a concrete value:
byte-identical, pinned. Full battery green (884 unit — one new pin
pair — e2e + prov block, 60 doc, 156 oracle, fuzz, lsp, timing
8-17ms medians).

FLAGGED, not fixed: the field-type MISMATCH sibling (right field
name, wrong type, cross-statement) still reports at the meet — the
`bind` inside dischargeRow's Some arm doesn't consult origins. Same
mechanism applies when someone hits it; needs its own small bless.

INCIDENT, recorded for process honesty: the casing rider fork
committed its gate stop (60106ca, correctly NOTES-only) but then
KEPT SWEEPING the rename in the live tree and was killed mid-sweep,
leaving ~350 uncommitted lines including wire-format breakage its
own gate exists to prevent (`from json` refusing `"Name"` keys —
live-observed as 37 unit failures). The runaway sweep was reverted
to the gate-stopped state (preserved at
/tmp/fork-runaway-plus-sessionD-*.patch) and Session D replayed
onto the clean tree. The gate note below stands as written: no
rename is IN; the wire-format bless is still the owed decision.
## Builtin fields go lowercase — the gate's question answered (2026-07-27)

The stop below got its answer the same day: **field names are the
wire contract, so choosing lowercase fields is choosing lowercase
keys — there was no second decision.** The rider resurrected at
session size with the wire change riding [D:builtin-fields-lowercase].

**The rename**: five defs, thirteen fields — FileRow
`name`/`bytes`/`readOnly`, Change `status`/`staged`/`unstaged`/`path`,
Completed `exitCode`/`stdout`/`stderr`, Group `key`/`items` (the def
census's find — the gate had only priced four defs), EnvVar
`name`/`value`. Type names stay uppercase; they declare. Wire today:
`ls |> Seq.first 1 |> to json` →
`{"bytes":…,"name":…,"readOnly":false}`.

**Ordinal order held**: within every def, lowercasing moved no field
past another (bytes<name<readOnly, exitCode<stderr<stdout, …), so
Map-iteration order in `show`/`to json` — and the completion lists'
sorted order — changed only in spelling. The four completion
field-list pins re-pin that literally.

**Pins that moved SEMANTICALLY (3)**, everything else textually:
"typo in field is rejected with exact span and a hint" and "row
discharge through a let reports the typo at the use site" respell
their probe `Bytse`→`bytse` — against lowercase `bytes` the old probe
sits at distance 3, past the hint threshold, so the old spelling
would have silently degraded the pins to hint-less errors; and "field
prefix narrows the suggestions" types `f.b` for `f.B`. Textual
movers, named: the roundtrip coherence pin (`ls |> to json |> from
json FileRow` — both sides together, the point of the exercise), the
`show` render pin (`{ bytes = 0; name = "a.txt"; readOnly = false }`),
the five `from json` JSON-literal pins, the porcelain fixture +
`fields["path"]` asserts, the Env.pair/ofPairs/fromFile pins, the
groupBy `_.key`/`g.items` pins, the FileRow row-poly and checker-error
pins, ~18 e2e exitCode-family lines plus the `_.name`/`_.path`/
`_.bytes` probes, lsp-e2e's ls-row and EnvVar-hole completions.

**The battery caught two sites the grep denominator missed** —
tools/loc.weir (Group fields; the e2e repo-scripts gate failed with
`Group has no field 'Items'. Did you mean 'items'?` — the free
teaching doing its job on us first) and ci/timing.sh's expression
probe. Also swept: the Parser capture teaching now says "read
.exitCode"; git-subrepo/bicep-deploy/repo-report examples; SKILL,
GUIDE, SEMANTICS, README, SECURITY field references. User records
keep their freedom — bicep's `t.Name`, jira-branch's `issue.Key`
stayed, correctly, PascalCase.

**No retirement hints**, phase-scoped per [D:drop-reify-builtins] —
and none needed: `has no field 'ExitCode' … Did you mean 'exitCode'?`
is distance 1; the migration teaches itself.

Battery: 883 unit / e2e all green (lsp included) / 20 fuzz / 60
skill-doc blocks / 156 oracle / timing 8·17·11ms. Diff 223+/223− —
a pure rename.

## Builtin-fields-lowercase rider — STOPPED AT THE GATE (2026-07-27)

The blessed rider's own gate fired on its first probe; per its rule
("any hit on (1) ⇒ report and stop"), NO rename happened. The five
answers, recorded for the follow-up bless:

1. **`to json` — HIT, confirmed live.** The writer emits
   `WritePropertyName kv.Key` — field names verbatim — and builtin
   records are reachable: `ls |> Seq.first 1 |> to json` emits
   `{"Bytes":79817,"Name":"SEMANTICS.md","ReadOnly":false}` today.
   Builtin field names ARE wire format; lowercasing them is a
   serialized-output change wearing a rename's clothes.
2. **`from json T` — HIT, same species.** `TryGetProperty(name)`
   matches JSON keys verbatim against the def's fields, and builtin
   defs are reachable (`from json FileRow` is a passing unit pin,
   including the `to json |> from json FileRow` roundtrip). External
   JSON carrying `Bytes`/`Name` keys would stop parsing.
3. **Porcelain — CLEAN.** `porcelainRow` maps git's columns
   positionally (`line[0]`, `line[1]`, `Substring 3`); the field
   names are construction literals with no coupling to git's format.
4. **`show` — renders field names**, as expected; the sweep surface
   naming builtin fields is ~76 grep hits in unit + ~24 in e2e
   (a superset count; the show-render pins proper are a subset).
5. **LSP/completion — CLEAN.** Zero hardcoded builtin field strings
   in Lsp/Complete/Fmt; those surfaces follow the defs.

THE DECISION NOW OWED (its own bless, as the rider priced): "do we
also change the wire format?" — the rider's argument (casing
consistency) applies to the wire too, and the phase-boundary
urgency (pre-publication = cheap, post = migration story) applies
DOUBLY to json keys. If blessed, the roundtrip pin moves with both
sides and the coordinated rename is the rider resurrected at
session size; if declined, builtin fields stay PascalCase and THAT
becomes the recorded reason (wire compatibility beats casing
symmetry). Either way the answer belongs in a row, not in drift.

## Diagnostics arc, Session C — the binder-span park opens (2026-07-27)

The park closed the way parks are supposed to: its criterion (a real
user demanding local navigation) fired in the author's first hour of
VS Code dogfooding, and the priced session ran [D:binder-spans].

**Cheaper than priced, in one dimension**: pattern binders needed
NOTHING — PSpan always carried them; only ELet/ELambda were nameless.
The spans ride the AST (`nameSpan` from a `spanned` ident;
`paramSpan` from each param-pattern's PSpan through curryParams; the
`_` shorthand keeps its atom span), mirror into the typed tree, and
the threading proved contained: 4 parser constructions, ~10 Check
sites, 2 Eval, 1 Lsp, 7 test matches — the compiler chased every one.

**The resolver is lexical**: `localDef` carries the enclosing binder
scope down the typed tree; a use resolves to the INNERMOST binding
(params shadow top-levels inside their lambda; outside, the
top-level owns the use — both directions pinned). Cursor ON a binder
stays null. The park's two conservative-null pins FLIPPED to jumps —
the acceptance, by the plan's own definition.

**Verified end to end**: unit (param/inner-let-through-block-joins/
payload-binder/shadowing), the nvim rig over the real protocol
(acc → 2:9, param t → 1:8), and the ORACLE re-ran green (the AST
grew a field; no semantics moved). 883 unit / e2e / 20 fuzz / 60
doc / 156 oracle / 17-10ms.

**Unlocked, not built**: rename and references now have their
prerequisite; each is its own bless.

## Diagnostics arc, Session B — the visibility pair (2026-07-27)

Both confusing-error killers landed [PLAN-diagnostics-arc B]; 882
unit / e2e / fuzz / doc / timing green, zero movement on the
pre-existing discard and ambiguous-constraint pins.

**B5 — errored statements keep their head warnings.** The Session-A
diagnosis held: cmdHeads already walked the whole typed tree; the
gap was that the Ok branch OWNED the walk, so a statement that
errored lost its warnings — exactly when they explain the error
(the bicep shape: `targ etEnv t` claimed as a command, the only
signal a type error one line later). The Error branch now re-parses
under the assume resolver and walks the UNTYPED tree; the warning
spelling extracted to one local shared by both branches (the
dedupe-sweep lesson, applied preemptively).

**B6 — the cascade, and the pick the bless note demanded.** A failed
`let NAME` now binds NAME to a hole scheme — but the plain hole
under-delivered on first contact: weir's no-HOF rule means a
hole-typed HEAD refuses application ("not a function, has type
'a1"), and hole results leak into discard/constraint demands
("ambiguous-constraint", "computes 'a2 and discards"). The landed
pick is HOLE + ARROW-SHAPING + DESCENDANT SILENCE — poison-lite via
the sentinel-name trick: the quantified name `__hole` survives
instantiation (unforgeable: ctx-fresh vars are the only type-var
namespace, the printScheme argument verbatim), checkSpine shapes an
applied hole into an arrow of hole vars, and the discard +
stranded-constraint sites skip hole-descended vars. Result: ONE
real error, ZERO echoes of any species — the poison alternative's
goal without a new type node through unify. The deferral trade
stands as blessed: a hole unifies with anything, so a genuine
downstream mismatch may surface only after the first error is
fixed; one real error beats N echoes, stated here as the trade.

## Diagnostics arc, Session A — four riders, four levels of pin (2026-07-26)

The dogfooding arc's opener [PLAN-diagnostics-arc]; all four landed
in one pass, 879 unit / e2e / lsp-e2e / timing green.

- **A1, the load near-miss teaching**: the module-member-missing arm
  now claims `Args.load`/`Env.load` before did-you-mean — "takes ONE
  record (or union) type name, e.g. Args.load Config". One fix site
  covers ALL bad shapes (zero args, two args from a space inside the
  type name, extra args) because they all route through the same
  member lookup. Pinned ×3 in unit.
- **A2, hover on inner-let binders**: binder names are not expression
  nodes, so nodeAt returned the enclosing let-in (the BODY's type —
  unit at the end of an effect function). The hover now detects a
  binder position textually (`let ` before the word) and walks for
  the innermost TELet of that name, showing the bound VALUE's type.
  Top-level lets unchanged (their KLet-scheme fallback already
  worked). Pinned over the protocol in lsp-e2e.py.
- **A3, `from json T` jumps**: definitionFor gains the TEFrom node —
  the adapter's type name resolves to its declaration. Unit-pinned.
- **A4, full-word squiggles**: cmd-not-found diags carried
  EndCol=None and the LSP defaults a None end to col+1 — the
  one-character squiggle. Now EndLine/EndCol span the head word.
  Pinned in check --json e2e.

Session-B intel banked while here: cmdHeads ALREADY walks the whole
tree — the real B#4 bug is that an ERRORED statement's head warnings
are never emitted (the Ok-branch owns the walk), which is exactly
when they would explain the error. The fix is a parse-level walk on
the Error branch, not an extension of the typed walk.
## Env.load enums — the declared set at the boundary (2026-07-26)

[D:env-enums] `LOG_LEVEL: Level` loads: a 0-arity union field converts
its env text exactly the way int/bool always did, with the DECLARED
set as the rule — `=debgu` dies at the boundary with "expected one
of: Debug, Info, Warn. Did you mean 'Debug'?" instead of steering a
wrong branch three functions later. Option<Level> rides; the
collect-then-raise reports enum problems alongside int/missing ones.

**The one design choice, decided and diverging on purpose**: Env
matching is CASE-INSENSITIVE (=DEBUG, =debug, =Debug all select
Debug — env convention is uppercase) while Args subcommands stay
lowercase-verbatim (`git subrepo clone`, never CLONE). Verified
Args' exact rule before writing the row: `c.ToLowerInvariant() =
tok`. The did-you-mean compares like the matcher (case-insensitive)
but names the DECLARED spelling — the stock hint machinery is
case-sensitive and called debgu→Debug distance 3; the wrapper calls
it per-candidate on lowered pairs.

**Check-time rejections, three, each teaching**: a payload-carrying
case names itself ("env values are single tokens, so enum fields
need 0-arity cases; case 'Carry' of LvlB carries a payload"); a
same-cased pair (Debug/DEBUG) collides ("collide as env value
'debug'" — the Args collision's env sibling); Default on an enum
field names the alternative ("spell the resting point Option<Lvl>
with Option.defaultValue"). VERIFIED before coding: the bare
`[<Default Info>]` form never parses (the attribute grammar takes
literals — the reachable shape is `[<Default "A">]`), and an EMPTY
value already means miss-not-None for int (`PORT=` fails TryParse) —
the enum empty follows the precedent.

**Mechanics**: `TEEnvLoad` grew a payload — the checker packs each
enum's case list into the node because eval has no type environment
(TRANSCRIPTION addendum). The eval arm slots between bool and the
unreachable catch-all. show on the loaded record was verified
unchanged — `Some (Debug)` (non-scalar union payloads parenthesize,
the shipped rendering; the first e2e expectation guessed wrong and
was corrected to the pinned truth).

**Session logistics, on record**: executed in an isolated WORKTREE
(branch env-enums off c2788dc) because the diagnostics-arc session
was live in the main tree — Tests.fs moved under the first edit
attempt; the plan's own sequence-after clause enforced by isolation
instead of waiting. The battery ran against a worktree-local stamped
binary (WEIR_BIN + a lowercase shim — the publish dir names the
binary Weir, and the overlay e2e's PATH trick found the stale
installed weir first, a small trap worth remembering). fuzz n/a (no
syntax). 879 unit / e2e all green / 60 doc blocks / timing 18/11ms.

## VS Code extension packaging hygiene (2026-07-26)

The user's first real `vsce package` run surfaced six issues at once;
all fixed:

- **LICENSE**: `SEE LICENSE IN ../../LICENSE` cannot resolve outside
  the extension dir — the root Apache-2.0 LICENSE is now copied in
  and the field reads `Apache-2.0`.
- **Bundling**: esbuild compiles every runtime dependency into
  `out/extension.js` — the vsix went from 339 files / 499 KB (whole
  node_modules) to **8 files / 106 KB**, and the SHIPPED surface has
  zero third-party modules. esbuild skips typechecking, so `compile`
  runs `tsc --noEmit` first (bundler moduleResolution — the
  languageclient-v10 package-exports floor).
- **`.vscodeignore`**: allowlist-style — bundle + grammar + metadata
  only.
- **Audit zero**: the 7 highs were @vscode/vsce@2's baggage (glob@7,
  inflight, prebuild-install) plus brace-expansion under
  languageclient v9 — vsce ^3.6 and languageclient ^10 clear all
  (`npm audit`: 0 vulnerabilities). v10 floors the engine at
  VS Code ≥ 1.91; bumped.
- **`repository` field** added — `--allow-missing-repository`
  retired; the build is now `npm install && npm run package` (the
  LOCAL vsce via devDependency, never a stale global npx).

**The v10 landmine, found live on the user's machine**: languageclient
v10 APPENDS transport argv (`--stdio`, `--clientProcessId=N`) to
Executables — v9 did not — so the audit-driven bump made the server
receive `lsp --stdio`, trip its own takes-no-arguments usage arm, exit
2, and crash-restart five times. Two fixes: the SERVER now tolerates
exactly the conventional client argv (`--stdio`,
`--clientProcessId*`) and still refuses/teaches on anything else
(e2e-pinned both ways); the client stays idiomatic v10 (the explicit
transport now exercises the tolerance path in every real session).
Also that session: the client resolves the binary ITSELF (PATH then
~/.local/bin — the macOS GUI-PATH case) with actionable errors; the
user's original ENOENT was `weir.serverPath = "weir lsp"` — the
setting is the BINARY only, now stated in the setting text, the
error, and docs/editors.md.

## The argv twins — the order-observable dedupe lands (2026-07-26)

The dedupe sweep's first sized finding, executed as its own careful
session [D:argv-rules]. Oracle FIRST: four exact-string e2e pins
against the pre-extraction binary — not contains-checks, full
aggregated-error text INCLUDING order. The capture session's lesson
paid again: the probes taught two things memory would not have
written — the twins already AGREE on order (scan problems in token
order, then fills in declaration order, shared tier before payload;
no canonicalization needed), and a bad int yields TWO problems
(not-an-int at scan, missing-required at fill, because the value
stays unset) — preserved as-is; deduping that message pair would be
a behavior change wearing a cleanup's clothes.

The extraction: `argvDup`, `argvParseValue`, `argvFill` — one
implementation each, accumulators as PARAMETERS rather than closure
captures, so order stays the caller's; the twins' scan loops are
untouched (partial-application aliases keep every call site
verbatim). Placement: all three in Eval — run-time token parsing and
Value construction; the Default policy consumed (Argv.defaultOf)
stays check-side in Argv.fs beside the Args/Env flip. Net lines ≈ 0
— the explicit signatures and comments ate the ~60-line estimate;
the win is one implementation at the boundary whose promise is
"collect every problem," where a rule fixed in one twin and not the
other was the real risk.

877 unit / e2e (+4 order-oracle pins) / 20 fuzz / timing green.

## The keywords divergence — drift, fixed by derivation (2026-07-26)

The dedupe sweep's finding #2, called and closed [D:keyword-completion].

**The verdict check came first and settled it in one blame**: the
suggestion list dates to Spike 6 (2026-07-12) — before if/then/else
landed (07-18), before elif and when existed at all. No comment, no
row, no curated exclusion anywhere. Drift, exactly the advisor's
read: a list written before the keywords it was missing.

**The fix is derivation, not synchronization**: completion's keywords
are now `Parser.keywords - unsuggestedKeywords` — the same-policy-
twice pattern closed by having one policy. The exclusion set carries
its reasons beside its definition: rec/mutable (reserved, no
meaning) and function (parked match-lambda sugar) — offering any
would suggest a spelling whose only outcome is the reserved-word
teaching error. `in` stays offered: the explicit let-in form is
legal exactly where completion lives (REPL, -e).

**The durable half is the inventory pin**: the offered/excluded split
is pinned AS DATA, so a new grammar keyword fails the test until its
completion decision is recorded — derivation alone would auto-offer
silently, which is convenient and undecided; the pin makes it
decided. (The inventory-match pattern from the highlighter grammars,
third application.)

One pre-existing pin legitimately gained an entry, named per the
plan: `suggest "whe"` now yields when + where. 877 unit / e2e green.

## The code-block dedupe sweep — closing the census blind spot (2026-07-26)

The rider that hunts what the regroup census could not see:
duplicated STATEMENT BLOCKS, not duplicated records.

**The detector (the denominator):** no CPD tool in-container, so the
plan's twenty-line script — a token-window hash over normalized
src/Weir lines (blank/comment lines dropped, window = 6), overlapping
windows merged into clusters. **11 clusters.** The detector
self-validated on its first output: it found the Argv Default pair —
the deliberate divergence commented one rider ago — exactly as a
cluster.

**Fixed (mechanical, five, −32 net):**
- `Eval.echoTail` — the clipped-echo tail computation ×3 (REPL let
  arm, REPL expr arm, -e); each consumer keeps its own WriteLine
  format.
- `Lsp.wordFind` — the word-bounded search written TWICE by the same
  author one session apart (binderCol, then definitionFor's local);
  binderCol now delegates.
- `Complete.withHoles` — the hole-binding env fold ×2
  (pipelineElemTy, fieldsAtRepaired).
- `Parser.commaPats` — the sepBy1→one-or-PTuple fold ×2 (paren
  interiors, binder positions).
- `Parser.escapedChar` — the escape decoder ×2 (stringChar,
  interpChar).
Zero pin movement (876 unit + e2e green) — every pair was truly
identical; no latent divergence surfaced.

**Left, with reasons:** the Check refutable-pattern enumerations ×2
are COMPILER-GUARDED (a new PatternKind breaks both matches via
exhaustiveness — divergence cannot be silent); the Builtins VBuiltin
shells ×11 are the idiomatic builtin shape (bodies and teaching text
differ — nothing to extract without losing the product); the Script
assembler join sites are the alignment-stacks family (the census's
non-duplicates verdict re-verified: different closing conditions);
the generalizeLet call preambles are the RESIDUE of the previous
dedupe (4-line arms, each body distinct).

**Findings (sized, not fixed):**
1. **The Eval argv runtime twins** — argvParseRecord and
   argvLoadShared each carry their own `dup` polarity checker,
   `parseValue`, and resting-point fields-fill as closures over local
   accumulators. A closure-lift (~60 lines saved) is possible but
   NOT mechanical: problem-collection ORDER is observable in the
   aggregated error. Size: its own careful rider.
2. **The keywords divergence** — Complete's suggestion list (11
   entries) and Parser's keyword set DIFFER: completion never offers
   if/then/elif/else/when. Drift or decision? Needs a call before
   any merge; if drift, completion under-offers. Size: one line once
   decided.
3. **The Script exec catch-blocks** ×3 (ExitRequest/render/exit-1) —
   mechanical-looking but inside the runner's exec recursion; listed
   under the cap rather than chained.

PROCESS now says the next census hunts BOTH shapes and blocks, with
the token-window script as the floor when no tool exists.

## The checker's weight — one extraction, one duplication, one precedent (2026-07-26)

The rider, executed after the capture-representation session landed
(the plan's own ordering caution — and it fired for real: the fork was
mid-flight in this very tree when the rider was blessed; execution
waited for its 0e0596f).

**Item 2 first: the scheme-scooping block was verbatim ×3**, not the
plan's ×2 — both ELet arms AND generalizeBinding carried it (the
binding case adds only finalTy + name-pairing). One `generalizeLet`
now owns the rule; generalizeBinding is its per-name sibling by name
and by call. Zero pin movement — so the copies had NOT diverged; no
finding. The census-blind-spot note, as the plan asked: the regroup
census pattern-matched duplicated RECORDS and missed duplicated
BLOCKS — the next sweep should grep for repeated expression sequences,
not just repeated shapes.

**Item 1: Argv policy is out of the checker.** `src/Weir/Argv.fs`
(205 lines): ShortOwner + the schema policy (kebab, short tables,
minted twins, sharedOf) verbatim, plus the validators extracted as
pure `string option` functions — message text byte-identical, the
arms keep only span plumbing (`err span msg`). THE TWO Default rules
now sit ADJACENT with the flip stated once: Env.load ACCEPTS
[<Default false>] (an env-backed bool genuinely rests at false),
Args.load REJECTS it as redundant (presence already rests at false) —
two rules on purpose, not one rule copied wrong. Check.fs: 2653 →
2454 (−199; the plan predicted ~200). Consumers (Eval, tests) gained
one `open`/qualifier change each; zero pin movement, e2e green
(every Args/Env teaching message pinned there unchanged).

**Item 3: the print precedent has its ceiling** — recorded beside the
sentinel itself: the ONE place a builtin's ergonomics buys checker
complexity, not to be extended to a second builtin without a bless.

Verdict from the read, restated for the ledger: 2.5kloc was already
lean for a checker carrying rows + three classes + exhaustiveness +
six typed boundaries, with much of its bulk being teaching text —
the product, not the weight. Now it is 2454 with the one
non-typing tenant moved out.

## Capture representation — one buffer, not N strings (2026-07-26)

[D:capture-buffer] The safety review's one host-caused pain, measured
then fixed: `| complete` held N line-strings (UTF-16 + ~40B/line
object overhead) — the review's 13x, 18.4x on this session's harsher
fixture. Now: fixed 4MB byte[] SEGMENTS as the storage + two-pass
exact (start,len) int arrays; `Stdout`/`Stderr` are lazy views
decoding a string per pull. Observable type unchanged — it was
always `seq<string>`; zero pin movement across the battery.

**The oracle came first** (the session's whole safety): the old
rules pinned against the OLD binary before any change — and they are
weirder than anyone would have written from memory: stdout is
ReadLine (\n, \r\n, lone \r; empties kept; unterminated tail is a
line); stderr is a DIFFERENT rule (\n-only split, empties DROPPED,
\r retained on CRLF); UTF-8 BOM stripped; an invalid byte decodes to
one U+FFFD (per-line GetString ≡ the streaming decoder — newline
bytes are hard UTF-8 sequence boundaries, so the equivalence is
structural); and a UTF-16/32 BOM SWITCHES decoding entirely
(StreamReader's detect flag) — preserved via a rare-path fallback
through a real StreamReader rather than silently dropped.

**The numbers** (seq 1..4M, 29.5MB text): MaxRSS 555MB → 82MB
(18.4x → 2.7x including baseline and full enumeration;
capture-HELD is 2.13x — the ~2x target; this fixture is
offset-pathological at 7.7B/line, so real logs land lower), wall
0.92s → 0.42s — the buffer path is 2.2x FASTER, and the 4M capture
now completes under a 100MB GC hard ceiling. V1 (single doubling
array) taught the lesson worth keeping: doubling+trim TOUCHES ~3x
the text and MaxRSS keeps LOH pages — held 5.6x despite a 2.1x live
payload; segments+two-pass got the touched pages down to the data.

**Stated, per the plan**: a view holds its buffer while referenced —
the same data the old lists held, not a regression. Re-enumeration
STRENGTHENS: a fixed buffer is stable, cheap, and cannot become
effectful (pinned). Int offsets: a single capture caps at the ~2GB
array bound — the old representation met OOM at a fraction of that
text on the same box. Declined as specified: interning, bounding
(the SECURITY non-claim stands, its number is now ~2x).

875 unit (+4 oracle pins) / e2e all green / 20 fuzz / 59 doc /
timing 8/19/11ms. SKILL's capture bullet and SECURITY's non-claim
carry the new number; the Rust-rewrite thought experiment's one real
motivation is answered in place.

## The macOS harness sweep — catalog, not cadence (2026-07-26)

The third native e2e run died at `sed -i` (~line 2004 of 2700) — one
finding per run is a bad exchange rate, so the rest was swept by
CATALOG against ci/ + tools/ + Tests.fs instead of waiting for the
next run. Fixed:

- **`sed -i`** — BSD sed demands a suffix argument; the one site is
  now rewrite-and-move (the portable spelling, no flag dialects).
- **`date +%s%N`** — BSD date has no %N. e2e's two wall-clock bounds
  now use a `now_ms` helper (python3 fallback — already a harness
  dependency; ~30ms overhead is nothing against 900/2000ms bounds).
  timing.sh is DIFFERENT: its 6–14ms medians would be swamped by a
  subprocess clock, and its gates are pinned on Linux-container
  numbers anyway — on a %N-less platform it now prints an explicit
  STATED skip pointing at ci/local.sh (the documented-skip pattern;
  never a silent pass [D:vacuous-probe-audit]).
- **`mapfile`** — bash 4; macOS ships bash 3.2. skill-doc now uses
  while-read (same array, same loudness).
- **`sha256sum`** — absent on macOS (`shasum -a 256` emits the
  identical digest). The value-headed hash fixture detects the tool;
  the pinned digest is unchanged.

Checked and CLEAR: `grep --line-buffered` (BSD grep has it), `find
-print -quit` (BSD find has it), `install -m`, `seq`, `head -c`,
process substitution under bash 3.2. Left with a note:
tools/corpus-mine.weir names sha256sum — a Linux-workflow tool (the
corpus lives in the container), not harness.

Linux battery unchanged and green — every BSD path is behind a
platform test, dormant here. The macOS claim is now "the catalog is
swept", not "the file was run to the end" — the user's next native
run is the honest check.

## The vacuous-probe audit — does this test test anything? (2026-07-26)

Verification-only [D:vacuous-probe-audit]; zero src/ moves. The bar,
now standing in PROCESS: every shelling-out probe is PORTABLE, LOUD
(its own failure is a named test failure, never a benign value), and
POSITIVE-CONTROLLED (or flagged "loud but uncontrolled" in writing).

**The denominator: 18 probe sites/groups examined, 6 vacuity matches,
all fixed; 2 new committed positive controls; 0 newly-red probes.**

The matches:
- `survivors` — pgrep missing/erroring read as zero survivors
  (pattern 1+2). Now: pgrep rc>1 → exit 9 → failwith naming the
  platform; plus a live-marker control (a sh whose argv carries the
  marker must count ≥1, then clear after kill).
- `defunctChildren` — the origin (fixed prior); this session
  refactored to `defunctChildrenOf pid` and COMMITTED the control:
  python fork-without-reap under another parent. Two construction
  facts worth keeping: the .NET runtime auto-reaps our own children
  (a zombie-of-us is not cheaply constructible), and bash reaps bg
  jobs as they exit (the `sh -c 'x & sleep'` shape does NOT hold a
  zombie — verified, rejected).
- deep-lock — an UNREADABLE or garbage lock was silently
  cleared-as-stale: the exact republish-mid-run race the lock
  closes, reopened by an EACCES. Now exit 3, lock preserved,
  e2e-pinned (garbage lock → 3, file untouched).
- publish.sh ×2 — `if holder=$(check)` read ANY nonzero as "free",
  so a broken probe meant PROCEED. Now rc≠1 refuses, naming the
  probe.
- Tests.fs "external pipes into external" — asserted survivors of a
  marker no process ever carried, and its own message admitted it
  ("trivially true marker check"). The marker now rides the command.
- e2e's /etc/hostname — the unit fix's twin; landed via the
  parallel fork (f6ce026) while this audit ran.

Classified SOUND with reasons: check-fresh (fail-safe — its probe
failing lands on the STALE side), skill-doc (zero-blocks-extracted
is loud; the weir-error blocks are positive controls by design),
harness.py (harness-selftest IS a committed control suite),
the fuzz Runner gate (incident-controlled this very window — the
12-failure stale event was the gate firing correctly), timing
(loud under set -e), the fuzz equality detector (graded control at
bring-up), e2e's structure (set -euo pipefail; the errout=&&fail
pattern is two-sided), the timeout polyfill (controlled at
introduction: pass-through + killed-hang).

Loud but uncontrolled, stated: survivors' exit-9 path has no
committed pgrep-breaking input. Uncommitted: the tree-sitter corpus
bar is session-run; if it becomes a standing script it takes the
template (naive re-run with a missing CLI would pass vacuously).

**Zero newly-red probes** — the instruments were broken, the claims
they guarded happened to be true. That is luck, now retired: the
controls make the next platform's version of this loud on day one.

## The first macOS receipts — the depth guard learns about stacks (2026-07-26)

The user ran `dotnet test` on macOS (arm64, Debug) and sent back two
failures — the first cross-platform receipts, arriving exactly on the
phase boundary the reify-drop row predicted.

**1. Stack overflow at ~420 of the 500-deep at-ceiling fixture**
[D:depth-stack-probe]. The depth guard's ceiling carried "margin for
smaller stacks" as a COMMENT — a constant measured on Linux's 8MB
threads. macOS test-host worker threads are smaller, Debug/arm64
frames fatter: the stack died before the counter reached the ceiling.
The guard guarded the wrong resource.

FIX: `deepen` now also probes the actual stack —
`RuntimeHelpers.TryEnsureSufficientExecutionStack()` (the primitive
System.Text.Json's recursion guards use; NativeAOT-safe) — and takes
the existing DepthExceeded escape with the located diagnostic
("nested too deeply (limit 500, less on small stacks)").

THE DIVISION OF LABOR, restated in the row: the probe owns safety at
any depth on any stack; the counted 500 is now purely cost +
portability — deterministic rejection above it everywhere, bounded
ASTs for the probe-less downstream walks (check/eval/fmt/Lsp recurse
over the tree with no probes; the parse gate is their one choke
point). Its only load-bearing consumer is SPINE length via the
post-parse gate (spines parse shallow, produce deep ASTs — no probe
can help there). Corpus max nesting is ~11; the user's
could-it-be-lower question is answered in the row: defensible, no
receipt, re-ask = a real script the gate rejects.

The over-pinned test flipped to the law: at-ceiling parses OR
diagnoses located — a crash is the only wrong answer; capacity
between floor and ceiling is platform-dependent BY DESIGN. Pinned
three ways: unit (a 512KB thread parsing depth-499 gets the
diagnostic — this pin IS the macOS crash, reproduced in-container;
it would have killed the test host pre-fix), e2e on the AOT binary
(`ulimit -s 512` → located diagnostic at depth ~240, rc=1, no
SIGSEGV; full stack still parses 499), timing unchanged (the probe
is a compare).

**2. BSD `wc -l` left-pads** — the into-test pinned GNU's output
formatting, not weir's stdin plumbing. Now `wc -l | tr -d ' '`.
The e2e battery has more of this class waiting for a macOS run
(container paths, GNU-isms) — flagged, not swept: no macOS CI exists
to keep a sweep honest.

**SECOND BATCH, same run** (the user re-ran after the probe fix):

- **/tmp is a symlink on macOS** (→ /private/tmp): the child-spawn
  cwd test asserted the literal — a child's getcwd reports the
  PHYSICAL path. Now `cd /usr` (real dir on both). The pwd BUILTIN
  test was fine — it reads weir's stored path, not a child's getcwd.
- **/etc/hostname doesn't exist on macOS**: grep exit 2 (error), not
  1 (no-match) — the two grep-no-match pins moved to /etc/hosts.
- **THE REAL ONE — a vacuously-passing pin**: `defunctChildren` used
  GNU `ps --ppid`; BSD ps errored, `grep -c Z` counted zero, and the
  zombie pin PASSED while pinning nothing — the masked-failure family
  gains a cross-platform member (the harness-truth lesson: a probe
  that can fail must fail LOUDLY). Now portable
  (`ps -A -o ppid=,stat=` + awk) AND loud (ps failure → exit 9 →
  failwith naming the platform). Re-verified with a graded positive
  control: a real forked zombie counts 1, counts 0 after reap.

Also merged from the user's shell: publish.sh arch detection
(3ebdeff — uname -m → linux-x64/linux-arm64/osx-arm64). weir builds
and runs on macOS; the Zed verification ran on that build.

## Zed verified on metal — the arc's last open cell closes (2026-07-26)

The user ran the 5-step verification on a real machine (macOS,
dev-extension install): tree-sitter highlighting ✓, diagnostics ✓
(`unbound variable 'undefinedName'`), hover ✓, formatting ✓. The
UNTESTED cell flips; every editor row in docs/editors.md is now
verified end to end.

Two install findings, both now in the extension README + .gitignore:

- **Zed clones the grammar ANONYMOUSLY from `[grammars.weir]`, even
  for a dev extension** — it never reads the grammar from the local
  extension dir. The weir repo is private, so the first install
  failed at the clone. Dev-mode fix: `repository = "file:///path/to/
  clone"` + a COMMITTED rev. PUBLISHING CONSEQUENCE HARDENED: the
  registry's CI clones anonymously too, so the public
  tree-sitter-weir repo split is now a PREREQUISITE for the Zed
  publish, not just preferred.
- **A failed attempt poisons the retry**: Zed clones INTO the
  extension dir (`editors/zed/grammars/`) and refuses a cached clone
  whose origin differs from the new URL — delete `grammars/` before
  retrying. Those artifacts (`grammars/`, `extension.wasm`,
  `target/`) are build junk written into SOURCE; .gitignore added.

## Definition, extended — members and cases (2026-07-26)

User follow-up to the arc: go-to-definition now covers record fields
and union cases, both resolving to the KType declaration site
[D:lsp-requests, extended]. The diagnosis beat the earlier estimate:
match-PATTERN position needed no new machinery at all — `Pattern`
already carries `PSpan` and `TEMatch` keeps the arm patterns, so
`| Pull ->` resolves today, not behind the binder-span park.

Four new routes in the pure `definitionFor`: expression-position
ctors (uppercase TEVar → the union whose Cases contain it), field
ACCESS (TEField, cursor after the target's span, target.Ty names the
record), record-LITERAL field names (word-at-cursor on a TERecord
node — per-field-name spans aren't kept, the text is), and pattern
cases (PSpan walk, ctor word only — a payload binder stays null).
The declaration column is textual, bounded by the decl's first `=`
(members after, the type name before); multi-line decls work free
because the logical line IS the whole declaration and translate maps
back. Builtin types null (no declaration in the script).

The binder-span park SHRINKS to exactly: params, local/block lets,
and pattern payload binders (+ rename/references). Pinned: five new
positions in unit (868), and three jumps through the real protocol
via the nvim rig (ctor 3:7, field 5:28, pattern 2:7).

## The Zed extension — the arc closes (editor arc, session 3) (2026-07-26)

editors/zed/ lands complete: extension.toml (grammar pinned by FULL
sha to session 2's commit, `path` into the subdir), languages/weir/
config.toml (path_suffixes + first_line_pattern shebang + `//` +
tab 4 + the server), highlights.scm (shared with the grammar), and
the ~30-line Rust glue (worktree.which "weir" → `weir lsp` — the one
command, the VS Code client's sibling).

**UNTESTED in-container, stated everywhere it matters**: Zed is
GUI-only and dev extensions compile with a local Rust toolchain
(neither installable here — no root, no cargo). The extension README
carries the 5-step local verification (PATH → dev-install →
highlighting on the flagship → diagnostic+hover → format) and the
subdir-grammar fallback (if the installed Zed rejects `path`, the
dedicated-repo split is the prerequisite — the priced decision from
session 2).

**Publishing prepared, NOT sent** (the plan's ruling): the README
names the exact zed-industries/extensions steps; putting weir in a
public registry is the user's explicit go, and the container has no
GitHub access regardless.

The editor arc is complete: LSP requests (session 1, 61c1cbb),
tree-sitter-weir (session 2, b4a9ff8), Zed (session 3). Standing
after: the binder-span park (definition/rename/references, one
medium session); the dedicated-repo split when Zed publishing or a
strict Zed version demands it; linguist when the popularity bar is
met.

## tree-sitter-weir — the renderer grammar (editor arc, session 2) (2026-07-26)

editors/tree-sitter-weir/ lands: grammar.js + highlight queries +
COMMITTED generated src/ (consumers build without Node — the
tree-sitter convention). The README states the law extension up
front: a RENDERER, not a second parser — `weir check`'s pipeline
stays the one truth, the grammar over-accepts freely, no assembler
replication (a continuation line may highlight as a fresh statement),
never cite it as the language definition.

**Repo location (the flagged decision, execution call).** The
container cannot create a GitLab repo, so the grammar ships as a
SUBDIRECTORY structured as a complete standalone repo. Helix consumes
subdirs natively (`source = { git = ..., subpath = ... }`); Zed's
grammar entries want a repo root, so the dedicated-repo split
(tree-sitter-weir under the arquidevio namespace) is the USER'S
action when session 3's publishing needs it — priced here, not
silently defaulted.

**Grammar shape**: token-soup with composite islands — comments,
hash-lines (shebang/#loose), strings/raw/interp (holes admit quoted
strings — a corpus find), attributes, let/type heads (binder name
captured), keywords, uppercase=type, the full sigil family
($( / $e( / $name / $@name / $@( / !( / !e( / !), operators incl.
`>>` (a doc-block find), and a single-char stray fallback so command
argv (--flags, paths, globs) can never ERROR.

**The corpus bar: zero ERROR/MISSING** — 8 .weir files (examples/ +
tools/) AND all 46 SKILL/GUIDE doc blocks. Two fixes the corpus
forced: interp holes containing strings (`{$(git log -1
"--format=%h") |> Seq.head}`), and `>>`.

**Helix rendering verified in-container**: grammar built via
`hx --grammar build` (path source; the runtime/grammars dir must
exist — a helix quirk), health shows parser+highlights ✓, and the
color-tracked pty reconstructor proved DIFFERENTIATED rendering on
the flagship's commitTreeAs lines: keyword / binder-name / string /
type / sigil-family / default = six distinct treatments. (A COLORTERM
artifact in the probe's SGR parser briefly read everything as one
color — the per-cell segmentation settled it.)

**GitHub rendering: NOT promised.** linguist acceptance requires
in-the-wild usage (its popularity bar: ~200 unique :user/repo hits),
which weir does not meet. The grammar is linguist-consumable when
that day comes; the item reopens on the criteria, not before.

## LSP requests — formatting + definition (editor arc, session 1) (2026-07-26)

The two most-expected-and-silently-missing requests land
[D:lsp-requests]. Zero language change; ~140 lines in Lsp.fs + one
capabilities line + pins.

**Formatting.** Client-sent text only, the same Fmt.formatLines the
CLI runs, one whole-document TextEdit, editor options ignored (fmt is
canonical — a 2-space editor still writes 4-space weir, and the nvim
rig proved the tabSize=2 request produced canonical output). The
plan's verify answered well: formatLines is total-ish on broken
buffers — unparseable statements ride verbatim while indent
normalizes, so format-on-save on a broken file stays useful; only an
assemble failure (a dangling continuation) refuses, and the LSP maps
that to no-edits. Both pinned.

**Definition v1.** Top-level `let`/`let`-pattern bindings; null for
params/match binders/block-lets (no binder spans in the checker —
conservatively silent) and builtins (no source). The hover plumbing
already had the hard half: nodeAt hands over the TEVar; the reverse
lookup is last-binder-above (shadowing free), binder column found
textually before the first `=`, translated back to physical. Factored
pure (`definitionFor`) so the unit pins hit it directly — the inline
handlers are untestable, the semanticTokensFor lesson reused.

**End-to-end, both editors.** nvim headless: the applied edit is
byte-identical to `weir fmt`; the definition range lands on the
binder (1:5–10). helix pty: `:format` visibly rewrites 2→4; `gd`
proven by inserting a marker at the landing site — it appears at
`let #HEREalpha`. THE FALSE ALARM was the driver, not the server:
helix's `w` leaves the selection head on trailing whitespace and gd
sends the HEAD, so the first test asked for a definition at a space —
the server's null was correct. The -vv transport log settled it in
one read (position character:10 = the space).

**The binder-span park (filed, three customers).** Full definition,
rename, and references all need Check to record binder spans — one
medium session shared by all three. Reopens when any is demanded by
a real user; until then definition's nulls are the design.

867 unit (+2) / e2e / fuzz / doc green.

## LSP editor configuration — shipped work made visible (2026-07-26)

Docs-and-packaging, zero language change. docs/editors.md carries the
four editors; README points at it from the tooling bullet;
editors/emacs/weir-mode.el ships the ~20-line major mode Emacs needs
to hang the association on. Every block registers BOTH `.weir` and
the shebang (extensionless scripts are the shell-language case most
configs omit); comment `//` and indent 4 stated once and encoded
everywhere — 4 is what fmt canonicalizes (verified on a nested file;
no .editorconfig exists in-repo to contradict it).

**The ruling held with one gap.** `weir lsp` is the shipped spelling,
stdio, in `weir --help`, and the VS Code extension already wraps
exactly it — but `weir lsp --help` fell through to script-running
("no such script: lsp"). Fixed in Program.fs, the fmt-usage
precedent: `"lsp" :: _` now prints usage naming docs/editors.md,
exit 2.

**The verification matrix (the point of the session).** Editors
installed as portable builds under /tmp (no root in-container; apt
denied — the plan's UNTESTED rule invoked only where downloads could
not substitute):

| editor | installed | attach | diagnostic | hover | tokens |
|---|---|---|---|---|---|
| Neovim 0.11.3 | y (tarball) | y | y (unbound msg) | y ("int") | y — 15 data ints; needs nvim_set_hl links (custom token types), stated in the doc |
| Helix 25.01.1 | y (tarball) | y (health ✓ + live) | y (gutter ● + statusline ● 1) | y (space-k popup) | n/a — Helix has no LSP semantic tokens; tree-sitter is the fast-follow |
| Emacs | NO (no portable build, no root) | — | — | — | — UNTESTED, marked in the doc |
| VS Code | elsewhere | — | — | — | extension's own plan; forward-linked |

Neovim ran fully headless (`-l` driver: attach wait, diagnostic wait,
raw hover + semanticTokens/full requests, shebang filetype assert).
Helix has no headless mode — driven through a pty with a hand-rolled
CUP-sequence screen reconstructor (no pyte, no pip in-container); the
reconstructed screen shows the diagnostic marker ON the error line,
the statusline count, the hover popup's "int", and the shebang route
attaching on an extensionless file. Both doc blocks are the TESTED
blocks verbatim.

**FINDING (sized, not fixed): textDocument/formatting is unwired.**
The server's capabilities advertise sync/hover/completion/
semanticTokens only — format-on-save, the most-expected LSP feature
after diagnostics, silently does nothing in every editor above. The
wiring is small: advertise documentFormattingProvider, handle the
request by running the fmt pipeline on the client-sent text, return
one whole-document TextEdit (empty on fmt's safety refusal — the
respace guard's contract carries over). Size: a small session (~60–
100 lines + a protocol pin in the Lsp tests). Worth doing before the
VS Code extension ships format-on-save expectations.

Troubleshooting section covers the four failure modes (PATH, filetype,
token visibility, server logs); the SECURITY non-claim (server reads
client-sent text only) is pointered from the doc.

## The debt riders — the census's convictions cleared (2026-07-26)

Rider-sized, zero behavior. Sequencing: ran AFTER the tier drop's
sessions (all landed through 107e7a4) — the preferred path; nothing
interleaved.

**Resolver base record.** The census convicted ×3 verbatim (Fmt /
Program / Script) — the sweep found a FOURTH verbatim copy in
Repl.fs:253 (postdating or miscounted; either way the conviction's
point, made by the copies themselves). `Script.resolver` is the one
constructor — Script owns it: the pipeline module, first of the four
in compile order, and it already held the exact shape as a private.
Four call sites collapsed, ~25 lines net negative, ZERO pin movement
(no latent divergence between the copies — all four were truly
verbatim modulo Repl's closure spelling).

**argStrings.** ×6 at census, ×11 at landing (the spawn family grew
it) — one private local in Builtins, next to `asString`.

**LEXICON re-sourced — the plan's predicted finding class, confirmed.**
The type-class entry said "Eq is the only class": written from
recollection, wrong since the type-classes sessions closed the set at
{Eq, Show, Ord} [D:inferred-type-classes]. Re-sourced from the
DECISIONS row AND the checker's class solver (Check.fs `demand`),
which settled the per-class rules the entry now states separately:
Eq = no function or seq anywhere, recursively; Show = no function,
seqs DO show (wider than Eq by exactly the seq rule — same KIND of
failable constraint, so no in-kind split to declare); Ord = int/
string/bool EXACTLY, no structural decomposition (narrower than
both). The hypothesis that Show might be total was checked and is
false — `show` on a function-carrying type is a check error.

**The census ledger updated in place** (bracket-amend style): both
convictions CLEARED with pointers; the bare-pipe park's phrasing
tightened — the diagnostics-policy session RAN and kept barePipeHint,
so "until the policy is decided" misread as satisfied; the park now
states its remaining narrow question (where should a bare-`|` fatal
point when the LHS spans lines?). Deep-run-vs-republish: closed by
ci/deep-lock.sh, confirmed struck. Alignment-stacks note: already in
the census verbatim, nothing added. Multiline-lambda overage:
accepted as spent, stands.

**DESIGN-multiline.md → plans/** (git mv, history follows), header
stamped EXECUTED-with-supersessions (block lets / bracket stacks /
body-blanks / lambdas grew past the sketch; the logical-line
reconstruction decision stands), indexed in plans/README.md marked
as a DESIGN doc. Reference sweep: both mentions are name-citations —
left to the index, the PLAN-sweep's own convention. ROOT INVENTORY,
stated not assumed: CLAUDE / DECISIONS / LEXICON / NOTES /
NOTES-agent / PROCESS / README / SECURITY / SEMANTICS all living;
READ-ORDER + TRANSCRIPTION are the debatable pair — artifacts of the
STANDING read gate (open thread, anchor maintained), judged
in-service and left; they become moveable when the gate closes.

**The debt list now reads**: empty, except what is deliberately
parked with criteria (the bare-pipe narrow question; the
fifth-alignment-feature trigger; the standing capture/OOM flags from
the safety review — all parks, not debt).

## The names go, third pass, right reason (2026-07-26)

`completed`/`succeeded`/`orFailed`/`exitCoded` + Env twins are gone as
user-callable names [D:drop-reify-builtins]; the desugar targets live
on as `|`-prefixed keys only the chain grammar can reach. Mechanical —
mostly a re-application of session 2's diff, which was green at 861
before its own battery killed it for the WRONG REASON being wrong.

**Why the third pass is different.** Session 2's argument (computed-
head escape hatch) was false — the builtins' head was always a literal.
The live argument is redundancy: since the splat rides reifier chains,
the computed-argv job has a better spelling and the builtins had no
job left. The DECISIONS row carries all three parts — wrong reason
(dead, so nobody revives it), right reason, and the total inventory
(flagship was the only caller and converted LAST session; this session
touched no .weir file at all).

**What moved**: 12 registrations renamed to `|`-keys; foldChain
re-aimed; exitCodeSpine (×2), Lsp reifierHeads (×12) re-keyed;
didYouMean + completion pools filter through Types.isUserName. e2e's
three direct-call sites converted (piter probe → `$(sh -c ... |
complete)`, env variant → `$e(... | complete)`, expression-position →
`let r = echo hi | complete in r.Stdout`). SKILL's demonstration block
→ `sh -c "exit 3" | complete`; the homonym line RETIRED from SKILL and
the SEMANTICS complete entry — the collision is gone, one fewer thing
to explain.

**Pinned**: un-typeability (binding or calling `|completed` is a parse
error; no `|`-leak in didYouMean — the scheme's guarantee proven);
the four chain routes byte-identical (re-run, plus the env-sigil/
district/value-headed/splat sweep already standing); three desugar-
shape pins flipped to the `|`-names; the directly-callable pin retired
1:1 for the un-typeability pin.

**Phase boundary, on record**: no retirement hints is PHASE-SCOPED —
experimental-phase policy, not doctrine. The first external user makes
removals teachable again; `retiredBare`'s original three are the
precedent to extend. The phase is about to change.

865 unit / e2e green / 20 fuzz / 59 doc.

## The splat rides — `$author(git $@argv | complete)` (2026-07-25)

The twist session [D:splat-reifier-chains]: the ugly line that survived
the keep-reify decision (`(completedEnv author "git" argv).Stdout |>
Seq.head`) was diagnostic — the only corner where a command is built as
DATA rather than written. Now it's written:

    $author(git commit-tree -m $msg $@parentArgs $tree | complete)
    |> _.Stdout |> Seq.head

**Part 0, both verifies landed.** `|> _.Stdout` already parses in pipe
position (a passing unit pin used `| complete |> _.ExitCode` all along)
— the parens-then-project smell was docs-only. And the crash the plan
remembered was already a located rejection: the keep-reify rider
converted it last session; the totality item was struck as done.

**The confinement diagnosis (stated before coding, as demanded):
implementation gap, not law.** The splat's story is element = one word,
produced at spawn-argv-build and nowhere else. A reified segment IS a
spawn with an argv: the builtins take `argv : seq<string>` and map each
element to exactly one argv entry (`Seq.map asString` → Proc) — the
same word boundary. The crash was representational: foldChain rebuilt
args as `EList args`, which has no slot for an N-word element, so
ESplat leaked into general expression position and hit the totality
backstop. No law was loosened because none was in the way.

**The fix is a desugar, not a lift.** Mixed literal+splat argv folds
into the seq value it denotes: non-splat runs chunk into list
literals, splat interiors splice whole (keeping the full `$@...` span
for diagnostics and tokens), joined with `Seq.append` — the
range-literal desugar's own node. ESplat still never leaves argv in
the AST, so the confinement pin holds LITERALLY; zero checker/eval
changes; splat-free chains produce the identical old node (zero pin
movement). The interim teaching rejection retires, replaced by
behavior.

**THE safety pin held.** Adversarial elements (`"one two"`,
`"semi;colon"`, `"star*glob"`) through the reifier path: argc=3,
identical to spawn argv — pinned in unit AND e2e. Empty splat = zero
words. All four reifiers × env sigil × district × value-headed swept
in one e2e fixture. Fuzz invariant 6: splatted ≡ inline words,
byte-identical (dedicated generator, the depth-axis pattern).

**Tokens.** The Lsp reified-chain arm now walks the append fold back
to its parts; a splat interior tokens like the TESplat arms (whole
`$@name`, delimiters for `$@(...)`). Rider: the In twins joined
reifierHeads — value-headed reified chains never tokened at all
(pre-existing gap, found by reading the set). Learned en route:
module members type as `TEVar "Seq.append"`, not TEField.

**Nuances on record**: a wrong-typed splat interior in a reified chain
gets the generic unify error, not argv's twin teachings (it's an
ordinary Seq.append argument post-desugar). The let-RHS bare-command
route still swallows family messages behind its backtrack —
pre-existing, unchanged.

**Part 2 inventory (report, no code).** The flagship's commitTreeAs was
the corpus's ONLY builtin call site — and this session converted it
(the argv-assembly line deleted, not rewritten). tools/: zero.
Remaining uses are docs demonstrations (SKILL's `completed "sh" [...]`
block — converts trivially if asked). EVERY SITE CONVERTS: the
hide/drop question reopens on its merits for a separate bless — the
homonym would resolve, the family shrinks, the internal desugar
targets take un-typeable keys as session 2 designed.

865 unit (+2: word-integrity, tokens) / e2e (+the reifier-path sweep) /
20 fuzz (+invariant 6) / doc green.

## Session 2 dies on its own gate — the reify builtins stay (2026-07-25)

The owed second session of the drop (hide `completed`/`succeeded`/
`orFailed`/`exitCoded` + Env/In twins behind un-typeable internal
names) was EXECUTED and then REVERTED whole [D:keep-reify-builtins].
The rename itself was clean — `|`-prefixed names ('|completed' etc.,
un-typeable since identifiers are `[A-Za-z_]..`), foldChain re-aimed,
suggestion/completion pools filtered, 861 unit green. The battery
killed it: the flagship's commitTreeAs (`completedEnv author "git"
argv`) stopped checking, and the replacement probe crashed.

**The premise was wrong, and precisely how is the finding.** The plan
mapped the tier by NAME, not capability — the same error class as the
"six of nine are desugar-only" correction inside session 1. The drop's
law is about HEADS: every command head is a literal, resolved at check
time. `completed "git" argv` already satisfies it — the head is a
literal string; what's computed is the ARGV (commit-tree's
variable-length parent list), which the law never spoke to. The
builtins aren't the computed-head escape hatch; they're the
computed-argv reification path — a different, legitimate job. Hiding
them bought zero law-level gain and removed a real capability.

**Why `| complete` can't take the job**: `$@` is confined to argv
assembly ([D:argv-splat] — the splat's whole safety story), and the
reifier desugar moves the chain's arguments OUT of argv into the
completed family's application. `$e(git $@argv | complete)` is
therefore unspellable-by-construction, and lifting the confinement to
enable it would spend real soundness work to buy a subtraction we no
longer want.

**Rider 1 — the probe found a crash.** `echo $@xs | complete` (any
route: plain, env-sigil, value-headed) was an UNHANDLED EXCEPTION —
the checker's ESplat arm is `failwith unreachable` because the parser
confines splats to argv, and the reifier desugar violated the
invariant by relocating them. rc-139-family treatment: foldChain now
rejects located at parse, naming the confinement and the per-route
builtin escape (`completed`/`completedEnv`/`exitCodedIn` ...). The
let-RHS bare-command route backtracks to a generic parse error —
family-uniform (the multi-external rejection surfaces the same way
there). Streaming splat (`xs | sort $@flags`) untouched. Pin note:
FParsec wraps long messages, so the unit pins assert single-word
fragments, not phrases that can straddle a line break.

**Rider 2 — a stale teaching taught retired names.** The head-splat
rejection still said "run/cmd take the program as a value" — session
1's gate grepped user CODE, not message TEXT. Now names the law
(branch the whole command line), pinned.

Recorded so nobody re-proposes: computed argv is not computed head
(SEMANTICS, next to the law); the `complete`/`completed` homonym does
two distinct jobs and keeps both names (one SKILL line, no rename).

863 unit (+1 splat-ride pin) / e2e green / fuzz + doc green.

## Dropping the computed-head tier — every head is a literal (2026-07-25)

Removed `cmd`/`run`/`cmdEnv`/`runEnv`/`feed`/`feedEnv` — the six
expression-position command builtins ([D:drop-command-builtins]).
Their only job was a COMPUTED head (a program name in a variable);
every other need — capture, effect, exit code, stdin — already has a
better spelling. So the whole tier subtracts to: every command head is
a LITERAL, resolved at check time.

**GREP-GATED, and the gate came back empty.** The plan was stop-and-
report if any real script had a computed head. The audit (e2e + tools
+ tests) found ZERO. The one near-miss — git-subrepo's `completedEnv
author "git" argv` — has a LITERAL head (`"git"`); it is a reifier, not
a dropped name, and reifiers stay this session. So the drop is pure
subtraction, no user rewrite forced by a genuine computed head.

**NO retirement hints (user, mid-session).** The first pass added a
teaching machine — every dropped name fired a "use X instead" hint.
The user cut it: we are in the experimental phase where anything can
change, so teaching retirements is teaching a fiction. Pure removal;
the dead impls sit in Builtins.fs (no FS warning), the retiredBare
hint table keeps only its original three.

**TWO SESSIONS (user decision).** The six "clean" names now; the
REIFIER family (`completed`/`succeeded`/`orFailed`/`exitCoded` + Env/In
twins) is its OWN session — those names are dual-use desugar targets
`foldChain` emits for `| complete` etc., so dropping the user-callable
spelling is an internal rename, not a subtraction. Untouched here.

**The reorg (user: "push through").** `cmd "sh" ["-c"; X]` was
pervasive in tests — the value-context idiom for a shell string. It
rewrites to `$(sh -c X)` (capture) or bare `sh -c X` (statement);
value-headed `xs |> feed "p" []` becomes `xs | p`; env twins become
sigils (`!e(...)`/`$e(...)`). ~25 e2e rewrites, the resolver block
moved above the first command test (runReal forward-ref), 5 unit tests
retired (env-twin type tests, redundant cmd-cwd, cmd-not-found), the
fuzz feed-equivalence property (invariant 5) retired with feed.

**Tool substitution branches the line.** The tier's imagined use —
"pick rg or grep at runtime" — reads better as `if hot then rg pat
else grep pat`. Branch the whole command, not the head. That is the
re-ask criterion inverted: reopen only for a head set that genuinely
cannot be enumerated into a branch.

862 unit / e2e green / 19 fuzz (feed law retired) / 59 doc / green.

## Value-headed pipelines, session 2 — the deferrals close (2026-07-25)

Session 1 shipped the streaming form and deferred four chunks with their
seams named. Session 2 closes them; two closed with a diagnosis instead
of a fix (ground rule one earned its keep twice).

**Chunk A — reifier-with-stdin (`xs | grep foo | complete`).** The seam
was exact: `Proc.completeWith`/the streaming `Spec` already carry the
input param; the reifier builtins passed `None`. Four INTERNAL
stdin-carrying twins (`completedIn`/`succeededIn`/`exitCodedIn`/
`orFailedIn`) — the public expression-position spellings keep their
arities ZERO-DIFF. `foldChain` gained the value-headed reifier case,
distinguished from multi-external by an `isCommandish` check on the
inner LHS: a value LHS → the stdin twin (value appended); a command LHS
→ the multi-external rejection, unchanged (the family's single-segment
rule, no new law). `files | grep -c foo | complete` reifies the count.

**Chunk B — semantic tokens. DIAGNOSIS, not fix.** The plan's premise
("the recognizer walks command-mode STATEMENTS, not EPipe-into-ECmd")
was wrong: the walk ALREADY recurses `childExprs` and handles `TECmd`
anywhere. Session 1's "verified empty" was the DISCARD ERROR of a bare
`["hi"] | cat` (non-unit statement) — no typed tree to walk, not a walk
gap. A clean value-headed pipe (`let h = ["hi"] | cat`) colors its head
correctly. No code change; a token pin locks it. The no-op-edit lesson:
the recognizer's entry point was already general.

**Chunk C — the fuzzer's transform law.** `xs | prog args` ≡ `xs |>
feed "prog" [args]`, byte-identical, as fuzz invariant 5. A DEDICATED
generator (literal seq LHS into a safe external — cat/sort/tr/wc), not a
RenderCfg flip, because the shape is expression-position (outside the
command-line renderer) — it rides its own property like the depth axis.
GRAMMAR.md records it. Since both spellings meet the same `foldChain`,
the law guards against the paths ever diverging.

**Chunk D — the two edges.** (D1) sigil-interior `$(xs | cat)` confirmed
NON-parsing — the redundancy argument holds (a value-headed pipe is
already an expression; `$()` is command-grammar), reported not fixed.
(D2) the district/sigil value-headed pipe now TEACHES: the `[`-head fail
message became a teaching naming the value-headed spelling. The clean
seam — that message is DISCARDED at statement level (the expression
grammar takes the list) and only SURFACES in command-only contexts
(district/sigil interiors), so improving it helps exactly where the
mistake is made, zero statement-level effect.

**Spawn-park pressure note (recorded per the plan).** The reifier row's
axis product (output × env × stdin) now has its STDIN column populated —
but by INTERNAL twins, no new user-facing spec-type NAMES, so the parked
user-facing spawn spec does NOT open. The env×stdin cell is unpopulated
(a value-headed pipe carries no env sigil today). A TENTH pressure on
the spawn family makes the expose-not-build session overdue.

867 unit / e2e (reifier + district pins; one session-1 pin flipped —
value-headed reifier now works) / 20 fuzz (+the equivalence law) / 59
doc / green.

## LEXICON.md — the project's own vocabulary, defined (2026-07-25)

Docs-only, zero src. Fifteen weeks produced a working vocabulary that
existed nowhere as a unit — type-theory terms weir uses precisely,
testing techniques adapted here, corpus-mining lingo, process idioms
(receipt/park/pin) a newcomer reads as jargon. LEXICON.md is the fifth
prose store, and it honours the no-fourth-store rule by DEFINING, not
deciding: 76 entries across four sections (type theory & language
design; testing & verification; corpus mining; process idioms), each ≤
a paragraph, each ruling-or-rule entry pointered at its `[D:key]` or
PROCESS §. Sourced from the five stores + GRAMMAR.md, not recollection.
README and PROCESS now point newcomers at it.

**The highest-value content is the field-divergences** — where weir
uses a term DIFFERENTLY: type class (closed/structural/erased, not
Haskell's open dictionaries), erasure (also names attributes),
receipt/park/pin (process idioms with no field meaning), force/cache
(materialization vs memoization — the pair that caused real confusion).

**Findings (the session's second deliverable), reported not resolved:**
- DOUBLE-MEANING: `scheme` (type scheme vs the "sentinel scheme"
  checker mechanism) and `stage` (pipeline stage vs proposal-lifecycle
  stage). Disambiguations proposed, not silently picked — the
  one-glyph-inventory precedent at the vocabulary level.
- UNDER-INDEXED: `district` and `sigil` have no `[D:key]` (unlike their
  sibling `exit-reifiers`) — described in SEMANTICS/NOTES but never got
  an index row. Proposed `[D:command-district]`/`[D:command-sigils]`
  rows; reported not added (docs-only session; a DECISIONS row is a
  decision, not a definition).

## Value-headed pipelines — `snips | sha256sum` (2026-07-25)

The park opened on its own criterion, fired BY HAND: writing the miner,
the user asked why `feed "sha256sum" []` is stringly when `cmd` is not.
`feed` was the only command-value-family member with no bare spelling.
(Provenance per the new lens: hand-written receipt — weight it.)

**The diagnosis was the win.** `EPipe(seqExpr, ECmd)` ALREADY
type-checks — the checker binds the arg to `seq<string>` and threads
it as the command's stdin — and ALREADY evals to `Proc.linesWith …
(Some stdin)`, byte-identical to `feed`. So the entire streaming
feature is a PARSER change: after an expression LHS, a bare `|` whose
head resolves EXTERNAL seeds the command-chain fold with the LHS
expression instead of firing barePipeHint. No new runtime, no
feed-desugar — the `EPipe`-into-`ECmd` machinery that command chains
already use IS the mechanism. Extracted `foldChain` so the
command-headed and value-headed paths share one desugar.

**Resolution decides, one step further.** `xs | sha256sum` (external
head) → value-headed pipe; `xs | Seq.length` (library) or `xs | f`
(binding) → the barePipeHint SHARPENS, firing exactly on
expression-into-expression, the case it was always right about. The
gate is `commandSegment` resolving to an ECmd; a known/library head
fails it and falls through to the hint. LHS demand is `seq<string>`
with twin teachings (scalar → `[x]`/show; seq<int> → map show),
shared by command chains and value-headed pipes (one checker arm —
an existing pin's message improved as a result, named).

**Scope — session 1 of the feature; the rest reported, not silently
narrowed.** SHIPPED: streaming pipelines (single + multi-external) at
expression positions (let-RHS, statement, captures); the miner's
`| sha256sum` receipt closed (corpus reproduces, byte-identical).
DEFERRED as separable chunks, each with its seam:
- **Reifier-with-stdin** (`xs | grep | complete`): needs the reifier
  builtins to expose `Proc.completeWith`'s existing `input` param (they
  pass `None` today); changing their arity breaks the expression
  spelling `completed "p" [a]`, so it wants stdin-carrying twins. Today
  it gives the SAME "single external segment" rejection a multi-external
  reifier gives — no new law (the plan's own multi-segment ruling).
- **Semantic tokens** for the value-headed head: the recognizer walks
  command-mode statements, not `EPipe`-into-`ECmd`, so `cat` in
  `xs | cat` isn't colored yet (verified empty).
- **Fuzzer** value-headed shapes + the `xs | prog` ≡ `xs |> feed`
  transform law.
- **Sigil-interior `$()`** value-headed (largely redundant — a
  value-headed pipe is already an expression) and the district
  rejection message (generic parse error today).

Zero pin movement on legitimate programs beyond the one improved
message; 867 unit / e2e (7 new value-headed checks) / 59 doc / green.

## The deep-run lock — the OWED window closes (2026-07-25)

The deep-run-vs-republish race (harness-truth class, promoted watch→OWED
on its second occurrence) is mechanized. The race: a `./publish.sh`
DURING a live deep fuzz run swaps `~/.local/bin/weir` underfoot, so a
metamorphic property runs `P` against the old binary and `T(P)` against
the new one and fails against a half-swapped binary — a MANUFACTURED
failure that reads like a real one. The freshness gate is start-of-run
(stamp+mtime), structurally blind to a mid-run swap; this was the one
window it could only STATE, not close.

**The fix — a lock, the check-fresh.sh pattern.** `ci/deep-lock.sh`
(acquire/release/check) is the single shared implementation: one
lockfile (`.weir-deep-run.lock`, gitignored), one liveness definition
(the recorded pid still running). `tools/fuzz.weir` holds it for the
run — the holder is the weir process itself, obtained via a child sh's
`$PPID` (weir has no getpid; the child reports its parent), alive for
the whole run. `publish.sh` refuses to install while a LIVE holder
exists — twice: a fail-fast check before the 30s build, and the
authoritative barrier right before the install swap.

**Staleness is decidable, so it never wedges.** A crashed run (no
release) leaves the lock with a dead pid; `check` clears it and any
actor proceeds. On a FAILED deep run, `orFail` raises before the
release line — the process exits, the pid dies, the next publish clears
the stale lock by liveness. No cleanup-on-crash needed.

Verified end-to-end: `fuzz.weir` acquired (pid caught mid-run),
`publish.sh` refused during, the lock released on clean finish; the
e2e battery pins the acquire/refuse-double/stale-clear lifecycle. The
check-fresh.sh header now reads "this gate checks freshness at start;
that lock guards the middle" — one boundary, closed, not stated.

## Drop [<Positional>] — the squat, and the provenance lens (2026-07-25)

Rider. `[<Positional>]` was registered with a deliberate not-yet
consumer — reserve the name, pre-answer the positionals park's marker
fight. Re-examined and answered NO: unregistered, its not-yet deleted;
`[<Positional>]` in source is now the ordinary unknown-attribute error
with did-you-mean over the remaining names (Short/NoShort/Doc/Default).

**The reason is a finding, not a feature critique.** The park's one
receipt was git-subrepo's `config <key> [<value>]` — and that receipt
is CONTRACT-MIMICRY from model-authored code: the port reproduced
git's CLI, and positional CLIs are what the training distribution
overwhelmingly holds (argparse/click/getopt, every man page). No weir
script was ever blocked; `config` was skipped and nothing broke. The
names-beat-positions house rule (records-over-tuples, extended to
argv) stood unchallenged, and three weeks of real scripts produced
zero organic demand. A registered name reserving syntax for a feature
whose only receipt is model idiom is SQUATTING, not pre-scoping.

**The durable finding → PROCESS "Receipt provenance".** A friction
entry or receipt from model-authored code may reflect the training
distribution's idioms rather than a wall the script hit. Distinguish
a script that COULD NOT be written (demand) from one written in an
unfamiliar idiom that FELT awkward (acclimation). A lens, not distrust.

**Diagnosis corrections (ground rule one).** (1) There is no
`PLAN-positionals.md` — the plan's "restamp it DROPPED" assumed a file
that never existed; positionals lived in PLAN-attributes/PLAN-typed-argv,
and the "five decisions" were design thinking never committed. Noted in
PLAN-attributes' four-name-clause discharge (3 bound, 1 dropped)
instead. (2) `--` flag-termination NEVER landed (bare `--` is just an
unknown flag, verified) — it was only planned WITH positionals, so it
goes unmentioned, untouched.

**Pins flipped, named.** The registry-membership pin ("argless
recorded" via Positional) now records NoShort instead; the argless-
rejects-args pin drops its Positional case; the not-yet-at-consumption
pin FLIPS to "Positional is an unknown attribute — dropped, not
reserved"; the Default+Positional composition pin RETIRES (net −1
test). Docs swept: SKILL/GUIDE/SEMANTICS lose Positional from the
registry lists, GUIDE states plainly that what's dropped is the typed/
declared/help path, NOT the ability — the untyped floor (`args`,
`Args.flag`/`Args.value`) remains for hand-rolled operands. Re-askable
against a HAND-WRITTEN-weir receipt a flags reshape can't serve.

## The miner — Str.rmatchAll and the last python retires (2026-07-25)

PLAN-miner-receipts, the remaining scope (feed / Seq.distinct /
scriptPath shipped ahead in their own sessions; Seq.groupBy already
existed). One new surface — `Str.rmatchAll` — and the corpus-mine.py
rewrite as its acceptance.

**`Str.rmatchAll pat s : seq<seq<string>>`** — the plural of `rmatch`,
every match's groups lazily via a Match/NextMatch walk (a match
computed only on pull). No Option: the plural's absence IS the empty
seq. `(?s)`/`(?m)` inline flags cover DOTALL/MULTILINE, so no options
API grows. The whole-file-as-string question RESOLVED to the existing
`Str.join` (`File.read |> Str.join "\n"`); `File.readText` stays parked
(no receipt asked for it).

**The rewrite reproduces the python EXACTLY.** On the committed corpus
(dotnet/fsharp @ 5928e91, in-container at /tmp/fsharp-corpus): base
`extracted=4253 unique=3700 kept=76`, wide `kept=102` — count-identical
to `tools/corpus-mine.py`, AND the kept content-SET byte-identical in
both modes (verified by per-file content hash, order-independent). In
0.8s. The three receipts all fire in one script: `Str.rmatchAll` (all
triples, not the first), `Seq.distinct` (dedup as a pipeline stage, not
threaded `seen` state — more idiomatic than the python), `feed` (each
snippet hashed through `sha256sum`'s stdin for its filename).
`tools/corpus-mine.py` deleted — tools/ is now python-free (the
remaining .py are LSP/REPL protocol drivers, a different category).

**Two fidelity subtleties, both handled.** (1) Python's `.strip("\n")`
strips ONLY newlines, not whitespace — baked into the pattern as
`(?s)"""\n*(.*?)\n*"""` rather than a weir `Str.trim` (which would strip
spaces too and merge distinct snippets). (2) `feed` writes its input as
LINES, so `sha256sum` sees `src\n` where python hashes `src` exactly —
the 12-char FILENAME differs from python's by that trailing newline,
but nothing observable does: dedup is by CONTENT (`Seq.distinct` on the
snippet), not by hash, so the partition and the unique/kept counts are
identical. Stated, not hidden.

**Ceremony.** Builtin/Str surface only — no assembler grammar, no
fuzzer obligation, no oracle (API not shape). `rmatchAll`'s pull-count/
DOTALL/all-matches/empty pins landed; `feed` and `distinct` carry their
own from prior sessions. Parks stand with criteria: Map/Set (membership
is answered by distinct; reopen on a KEYED receipt), value-headed
pipelines (`xs | fzf` — the grammar form of feed; reopen on the fzf
receipt), `Seq.countBy`/`groupBy` (no receipt), `File.readText` (Str.join
picked). 867 unit / e2e / fuzz / doc all green.

## Diagnostics policy — who owns the error the user reads (2026-07-25)

Blessed as "state the arbitration policy + reconcile three features
that routed around it." Ground rule one (diagnose before changing)
paid immediately, twice: the plan's premise was stale AND its
acceptance metric was wrong.

**Stale premise.** The two invariant-3 repros the plan wanted to
"flip from current-behavior markers to true-site" were ALREADY
true-site — seq-commit flipped the district-wrap class, arm-commit
the nested-arm class (Tests.fs:5245 "anchors on the junk itself"
(4,20); :5282 (6,23)). The plan was written from the pre-commit-law
picture. So there is no instance to fix; the work is the POLICY.

**What actually wins today (the diagnosis).** There is NO weir-side
arbiter. FParsec's furthest-error merge picks one error at one
assembled-text position; `Script.translate` maps it to a physical
line/col via the segment table. The consumed-separator law is what
makes that furthest position the TRUE cause: it removes the
backtracks that used to rewind a deep failure into a shallow,
synthetic-anchored one (the class-one "col past the line end"
symptom). Adversarial probes (district-then-junk, compound-wrap-then-
junk, malformed district header) all now anchor physical, on the
junk. No remaining synthetic-anchor bug.

**Diagnosis CORRECTION — "deeper wins" is wrong.** The plan's
acceptance said "junk at two sites reports the deeper one." I encoded
that as a new fuzz property and it FAILED at once:
`let v0 = [ (if …) / (10+17) ?!? / 67 ] ?!?` reports at line 2 (the
FIRST junk), not line 3 — one statement, the parse stops at the first
obstruction. The right law is "furthest REACHED, not latest in the
file": with two problems in one statement the first owns the report;
across statements each reports its own. Flipped the property to
first-reached-owns-it (a deeper junk must not steal the site) — green.
The plan's own metric would have shipped a false policy; the property
caught it.

**The bypass-channel question, EVALUATED and rejected.** Should
teaching errors bypass the merge entirely (throw WeirDiagnostic, catch
at the statement boundary, like DepthExceeded)? No. The two needs are
DISTINCT: a teaching hint OVERRIDES THE MESSAGE at a position that is
already the furthest reached (barePipeHint fires after the whole LHS
is consumed — failFatally suffices, given the commit law); the depth
guard ESCAPES a position that would be DISCARDED, because it fires
inside `atom` deep in speculative attempt/choice that backtracks (a
failFatally there gets swallowed — observed in the hardening session).
Generalizing the throw would risk unwinding out of a branch that
SHOULD retry, for a benefit the commit law already delivers. Verdict:
keep both channels, named for their two jobs.

**The three routed-around features reconciled — as three CORRECT
roles, zero code change.** barePipeHint KEEPS its `failFatally`: it is
the same-position teaching override, not a workaround (retiring it
would lose the teaching); its past misfire was a backtrack, closed by
the law. arm-commit's law stays (correct on its own terms).
DepthExceeded stays the escape channel, now NAMED as policy rather
than a hack. None needed touching — the policy statement is what they
were collectively implementing.

**Code delta:** only the fuzzer. The single-junk span property's
`noteHit` escape hatch (which had accepted "a backtrack note naming
the line" as a positional hit — a concession to the district-wrap
class) is RETIRED: dead since the commit law, so the assertion
tightens to the primary error at the true site. Added the two-error
arbitration property (first-reached owns the report). SEMANTICS gains
a "Diagnostics — which error you see" section in the user's terms;
[D:diag-arbitration] indexes it.

Zero grammar change, zero language-behavior change, zero pin movement
(the behavior was already correct — this session RATIFIES and locks
it). Reported as a finding, not a shortfall: the plan expected pin
movement because it believed the repros were broken; they were not.

## The freshness gate unified (+ dirty stamp, doc riders) (2026-07-25)

Part two's highest-value flag: the mechanism that catches masked
failures was itself incomplete and, worse, one copy LIED. Mechanical
fix, zero language change (no .fs touched).

**The consumer denominator (enumerated, the report's point).** Every
binary consumer, before/after:
- `ci/e2e.sh` — full inline gate → calls `ci/check-fresh.sh`.
- `ci/skill-doc.sh` — WARN-only mtime, no stamp (part two read only
  its first 20 lines and called it "NONE" — corrected here) → calls
  the shared gate, now HARD.
- `ci/timing.sh` — no gate → calls the shared gate.
- `tests/lib/harness.py:assert_fresh` (used by lsp-e2e, repl-color,
  repl-wordnav, harness-selftest) — stamp-ONLY, with a comment
  claiming "mtime gates still apply" that WERE NOT → subprocesses the
  shared gate (stamp+mtime), comment gone.
- `tests/Weir.Fuzz/Runner.fs` — already a complete F# gate
  (stamp+mtime, `-dirty`-tolerant via `StartsWith`); kept as the SOLE
  F#-native twin (shelling out from the test runtime is a worse
  dependency), cross-referenced from the shared script.
- `ci/fsharp-oracle.sh` — dotnet test, no binary, correctly ungated.

So: two consumers gained a gate, one lost a lie, one deduped; the
shell+Python family now has ONE implementation (`ci/check-fresh.sh`).

**The lying comment is the seventh masked-failure member, a new
shape.** Not a stale artifact — a guard whose DOCS drifted from the
guard, promising a protection it didn't have. Logged in PROCESS.md:
worse than no comment; the fix is that the doc and code are now the
same file for all shell/Python consumers.

**The republish window, STATED not mechanized.** The gate is
start-of-run, not per-case; a republish mid-run swaps the bytes while
the stamp still matches (the deep-fuzz P-vs-T(P) case). Documented in
`check-fresh.sh`'s header as the ONE remaining window, with the
standing rule (never republish during a live deep run) until a
publish lockfile lands. One boundary stated, not two half-known.

**Dirty stamp.** `publish.sh` now appends `-dirty` (via
`rev-parse --short` + a `git status --porcelain` check — NOT `git
describe`, which turns tag-relative the moment a tag exists and would
break the gate's short-hash comparison). The gate WARNS on a dirty
build but PASSES.

CORRECTION (same day, user hit it): the first cut HARD-FAILED on dirty
when `$CI` was set. Wrong on two counts — a freshly built dirty binary
is the FRESHEST binary, not a stale one (the mtime gate is the real
staleness check; the stamp's job is identity), and real CI publishes
from a CLEAN checkout so a dirty stamp can't even arise there — meaning
the `$CI`-keyed hard-fail had NO true-positive case and only ever
false-positived on developers whose environment happened to carry `CI`.
"Don't ship dirty" is a shipping concern, not a per-consumer test-gate
one. Fix: dirty warns everywhere; the hard-fail is an explicit opt-in
(`WEIR_REQUIRE_CLEAN=1`) for a release job that genuinely wants it.
Verified: dirty stamp → rc=0 plain AND under CI=true, rc=1 only under
WEIR_REQUIRE_CLEAN.

**Doc riders (three).** SKILL: capture is heavy — stream don't
capture for big output, carrying the measured 4M-lines/44MB→573MB
(~13x). SKILL: the two path non-claims (Path.combine's absolute-
second-arg + no `..` normalize; File.* follows symlinks glob skips —
each correct in isolation). PROCESS: the pgrep lesson — an instrument
whose pattern can match the instrument is not a measurement (the
harness-truth class's manufactured-failure variant); count by name
(`ps -C`), not by a pattern the measuring command carries.

865 unit / 318+ e2e / 18 fuzz / 58 doc blocks, all green through the
unified gate.

## Safe-by-design review, part two — the surfaces outside the language (2026-07-25)

Verification-only (zero src/), the four surfaces part one didn't touch.
Most confirmed clean; the real yield was runtime resource behavior and
the build-integrity mechanism's completeness. A test-harness lesson
threaded the whole session: `pgrep -f "sleep 300"` MATCHES THE PROBE'S
OWN SHELL (its command line contains the pattern) — every early
"orphan" and "self-kill (rc 144)" was a false positive; `ps -C sleep`
(named-process match) is the honest counter. Recorded so the next
resource audit doesn't re-walk it.

**Property 5 — runtime resource exhaustion. Mostly CONFIRMED, one doc
flag.** Across 10k sequential `run "true"` spawns: fd count oscillates
10–35 and does NOT grow (GC-delayed pipe finalization, bounded); RSS
plateaus at ~19MB; zero zombies mid-run; tree-kill leaves zero orphans
for direct children, sh-grandchildren, backgrounded-and-`wait`ed
grandchildren, and a 15-child burst (all zero once counted with
`ps -C`). `feed` to a non-reading child (`true`, 2M-line source): the
writer fills the pipe, the child exits, the writer's EPIPE is swallowed
— rc=0 "survived", no hang. Infinite `$(yes) |> Seq.force` under a
1.5GB ceiling: a LOCATED rc=1 "Insufficient memory" at the script line,
graceful, not a crash.
- FLAG (doc, no code): `| complete` capture is unbounded AND
  memory-HEAVY — 4M small lines (~44MB of text) drove RSS to 573MB,
  ~13x, from per-`string` object overhead. Landed as a SECURITY
  non-claim; a SKILL "stream, don't capture, for big output" line is
  the follow-up doc rider; an optional bounded-`complete` is a park,
  not owed.

**Property 6 — the LSP as an attack surface. CONFIRMED.** The
depth-guard placement question (the one that would have gone to the
hardening session immediately) PASSES: a `didOpen` carrying the
6000-paren fixture returns "expression nested too deeply (limit 500)"
and the server keeps serving — the guard is in the PARSER
(analyzeLines/parseLine), which is the LSP's own path, exactly where
it belongs. Malformed framing (a `Content-Length: 100000000` with a
short body; garbage JSON in a valid frame; invalid UTF-8 bytes) never
kills the server. Notifications for unopened documents and hover at a
past-EOF position return gracefully (result=None). `didOpen
file:///etc/passwd` analyzes the client-sent TEXT, never reading the
real file — the scope non-claim, now stated. (Minor note, not a flag:
a huge Content-Length blocks the read loop until the stream closes —
irrelevant for a trusted editor client.)

**Property 7 — path handling. Two doc flags, the rest non-claims.**
- FLAG (doc-or-diverge): `Path.combine` inherits .NET's absolute-
  second-arg rule (`combine "/safe" "/etc/x"` → `/etc/x`) and does not
  normalize `..` (`combine "/safe" "../../etc/x"` → `/safe/../../etc/x`).
  Options with sizes: a doc line (landed as the SECURITY non-claim;
  a SKILL note is the follow-up rider), OR a ~10-line divergence that
  rejects an absolute second argument. Reported both; a divergence is
  a possible future session.
- FLAG (doc-or-align): `File.read` FOLLOWS a symlinked directory that
  `Path.glob`'s `**` deliberately SKIPS (`File.read
  "link/f.txt"` → the target's contents). An inconsistency, not a
  vuln. Options: document the split (glob skips symlink dirs for
  loop-immunity; File.*/explicit paths follow, as the shell does) or
  align. Reported; recommend document — the behaviors are each correct
  in isolation.
- CONFIRMED: hostile filenames (`-rf`, `a b.txt` with a space) through
  `Path.glob` reach a command as exactly one argv word each (argc=1).
  Residual stated in SECURITY: word-integrity is not flag-safety.

**Property 8 — the stamp/build-integrity mechanism. Two gate flags.**
`--version` stamps `git rev-parse --short HEAD` at build — SOURCE
revision, commit-granular. Two completeness gaps:
- FLAG (small): NO dirty-tree marker. A build from a dirty working
  tree stamps the clean HEAD hash, indistinguishable by `--version`
  from a clean build. Fix: `git describe --dirty` or a
  `git status --porcelain` check appending `-dirty` in publish.sh.
- FLAG (small): the freshness gate is applied UNEVENLY. `ci/e2e.sh`
  has the full guard (stamp == HEAD AND no `.fs` newer than the
  binary). But `ci/skill-doc.sh` and `ci/timing.sh` have NO gate —
  they run against whatever binary is installed; and the Python
  `assert_fresh` (lsp-e2e, harness-selftest) checks the STAMP ONLY,
  no mtime, despite a comment claiming "mtime gates still apply"
  (they don't, in that path). So a same-HEAD-but-stale binary passes
  three of the consumers. Fix: hoist the e2e gate into a shared
  helper all consumers call; correct the misleading comment. The
  concurrent-republish race (the standing OWED item) is therefore
  NOT the only unguarded window — these are additional ones.

**Deliverable:** SECURITY.md gains a Non-claims section (path funcs
don't confine; word-integrity ≠ flag-safety; LSP reads client text;
capture unbounded). Zero src/ changes. Flags → blessed follow-ups:
the Path.combine divergence (optional), the freshness-gate unification
(small, the higher-value one — it protects the whole test apparatus),
the dirty-stamp marker (small), and the standing capture/OOM parks.

## Totality hardening — the parser survives hostile depth (2026-07-25)

The safe-by-design review's Property-3 flag paid off: unbounded
expression depth was not "hardening polish", it was a memory-unsafe
SEGV (rc 139) in a language whose SECURITY.md pitch is safe-by-design.
Three finds, now all diagnose-or-bound (no rc 139, no hang, ~100-300ms):
deep parens (`((…))` ≳6000), a long operator spine (`1 + 1 + …` ≳8000
terms), nested brackets (`[[…]]`, was O(2^n)).

**Diagnosis corrected the plan's premise.** The plan asserted "the two
segfaults share ONE fix: a parse-depth guard." Probing (fmt = parse-
only, check = parse+walk) split them: paren nesting SEGVs in `fmt`
(parser recursive descent), but the operator spine parses fine at 20k
(fmt=0) and SEGVs in `check` — a deep left-nested AST overflowing the
CHECKER's tree-walk, not the parser. So it is ONE ceiling, TWO
enforcement points, because the two overflows live in two engines:
- `deepen` (a ThreadLocal depth counter wrapping `atom`) stops NESTING
  during the parse, before the parser's own stack dies. It THROWS
  `DepthExceeded` rather than `failFatally` — the fatal gets swallowed
  by the surrounding attempt/choice backtrack (a shallower "expecting
  expression" wins the error merge; observed directly — the located
  message only surfaced once I switched to an exception).
- a post-parse ITERATIVE (explicit-stack, itself crash-proof) depth
  gate in parseLineFull catches the operator/app/pipe SPINE, which
  parses shallow and can't be caught by a nesting counter.

**The ceiling is 500, probed not guessed.** Crash floors on this
container's stack: parens ~6000, operator spine 4000–8000. Corpus max
nesting: 11 (one Python test file; real .weir is ≤ ~6). 500 sits ~45x
above any legit program and ~10x below the crash floor, with margin
for smaller stacks. Boundary pinned: depth 499 parses, 501 rejected.

**The bracket hang was a SEPARATE mechanism, diagnosed first** (the
no-op-edit lesson). Not depth — BACKTRACKING: `rangeBody`'s
`attempt (rangeTerm .>> dotdot)` descends into nested brackets probing
for a range's `..`, fails, backtracks, and the list path re-parses the
SAME nested structure — 2^n (measured: 607ms→9s doubling over depths
18→22). A range endpoint is numeric and never starts with `[`/`{`, so
one line — `notFollowedBy (anyOf "[{")` at the range probe — commits to
the list path on the opener and kills the exponential [D:range-probe].
The consumed-separator law's shape, exactly as the plan anticipated
("if `[` commits on its open, the backtrack cannot blow up"). Cost: a
list/record as a range START (`[[1]..[3]]`, nonsensical, type-error
before) is now a parse error — unpinned, no legit range starts there.
The depth guard alone would NOT have fixed brackets (2^40 blows the
clock long before depth 500); both fixes were needed.

**Acceptance is the fuzzer, permanently.** Invariant 2 gains a depth
axis (the three fixtures as seeds + a generated over-ceiling sweep) —
closing the honest denominator the review named (the generators favor
breadth). A crash there takes the runner down, so survival IS the
no-crash pin. 865 unit (5 new Depth-guard) / 318+ e2e / 18 fuzz props /
58 doc blocks, zero pin movement on legitimate programs.

**Riders.** (A) mid-word scalar splice `--file=$x` now fatals like its
splat twin [D:argv-splat] — the value was safe (isolated) but the
prefix silently dropped; `notMidWord` is shared, gated behind the `$`
(the first cut fired on plain barewords — 46 reds — because the check
ran before confirming a splice was present). SKILL's "passes literally"
wording corrected. (B) LICENSE (Apache-2.0) + NOTICE (FParsec, the
only shipped dep) added — the Property-4 OSS-readiness follow-up.

## Safe-by-design review — the four safety properties verified (2026-07-24)

Verification-only session (blessed), the security sibling of the
oracle and the fuzzer: confirm weir's four already-CLAIMED safety
properties were built soundly, flag what isn't, change nothing in
src/. Deliverable is SECURITY.md (new) + this report. The plan called
it: the secret-leak and extreme-input passes were "the two most
likely to surface a real flag" — extreme-input surfaced THREE.

**Property 1 — injection safety: CONFIRMED.** A hostile value
(`a b;\tc"d'e\nf$(id)`+backtick+`id`+backtick+`&&*?`) spliced as `$x`
arrives at the child ONE argv word, byte-for-byte identical to the
source (`od` diff clean). Confirmed at every splice position: bare
argv (argc=1 under `; && $(...) *`), `$@xs` splat (N words, each one
word), interpolation `$"pre {x} post"` (one word), capture-sigil
interior `$(… $x)` (one word). Architectural leg confirmed: ONE
`Process.Start` in src/ (Proc.fs:37), `UseShellExecute=false`, argv
vector; the only `/bin/sh -c` is the explicit `into` builtin
(Builtins.fs:134), never on the implicit spawn path.
- MINOR FLAG (diagnostic asymmetry, not a safety hole): mid-word
  `$x` (`--opt=$x`) SILENTLY splits into two words `["--opt="; x]`
  — the value stays isolated (safe), but the prefix drops off with
  no warning, while the SAME mistake with `$@` is a hard fatal
  ("cannot join a word under construction"). Size: ~10 lines
  (mirror splat's `previousCharSatisfiesNot`-space check into
  spliceVar, emit the same teaching). Shape: a diagnostics session,
  not a safety fix — the doc already warns ("spell `--file $f`"),
  this makes the checker say so too. The `--file=$f` "passes
  literally" doc phrasing is misleading (it splits, not glues) —
  fold the wording fix into the same session.

**Property 2 — resolution integrity: CONFIRMED.** With a malicious
`git` planted first on PATH: a `let git …` binding runs the binding
(`bound:status`), never the planted binary; bare unshadowed `git`
reaches PATH (`MALICIOUS` — correct, that IS normal shell
resolution); `^git` in command-head position forces PATH through the
shadow. Check-time verdict matches run-time (checks clean, runs the
binding). Note recorded, not a flag: `^` is command-mode HEAD syntax
— `run "^git" [...]` does not strip it (errors "not found: ^git"),
which is the documented head-vs-string-arg distinction.

**Property 3 — untrusted-text totality: FLAGGED (three violations).**
The claim is diagnose-or-bound, never crash-or-hang. Adversarial
LOCAL text breaks it three ways (all availability, not
execution/injection — you already chose to run the file):
- SEGFAULT on deep paren nesting: `((((1))))` at depth ≳6000
  (nest5000 ok 207ms, nest7000 rc=139). Stack exhaustion in
  FParsec's recursive descent.
- SEGFAULT on long operator chains: `1 + 1 + …` at ≳50000 terms
  (rc=139). Same mechanism, the OperatorPrecedenceParser recursion.
- HANG (super-linear/exponential) on nested empty brackets:
  `[[[…]]]` — depth 20 = 2.3s, depth 40+ = >20s timeout. Backtracking
  blowup in the list-literal grammar.
  Size: medium — the two segfaults share a fix (a parse-depth guard
  that converts stack-death into a located "expression nested too
  deeply" diagnostic); the bracket hang is separate (left-factor or
  memoize the list production). Shape: one hardening session,
  acceptance = these exact fixtures diagnose-or-bound under a wall
  clock, folded into the fuzzer's totality property as pinned seeds.
  Honest denominator: the fuzzer patrols totality but under-weights
  extreme DEPTH (its generators favor breadth); this pass IS that
  gap's receipt.

**Property 4 — supply chain + secret surface: CONFIRMED.** Shipped
binary PackageReferences exactly FParsec 1.1.1 (Simplified BSD);
FCS + FsCheck are test-side only (grep: neither in src/Weir/*.fsproj),
so the AOT single-binary links neither. `weir --version` stamps the
source revision; the batteries hard-gate on stale binaries. Secret
surface: a nonzero-exit raise renders `Prog :: Args` only
(Proc.fs:72, Env structurally excluded), and a `complete` record is
`{ExitCode; Stdout; Stderr}` with NO env field — a secret in
`runEnv`'s overlay does not appear in either. A secret leaks only if
the script author puts it in argv itself.
- FOLLOW-UP (OSS-readiness, not a vuln): no LICENSE / NOTICE /
  third-party-notices file exists in the repo. SECURITY.md names
  FParsec's license inline; a proper LICENSE + a one-line FParsec
  notice is a small housekeeping rider before OSS publish.

**Net:** two properties clean, two carry flags (one minor-diagnostic,
one real-totality-hardening), plus one OSS housekeeping rider. Zero
src/ changes this session — every flag is a blessed follow-up's seed,
per the verification/hardening separation. SECURITY.md carries the
threat model, the not-a-sandbox scope line, the provenance note, and
the confidential-issue reporting channel.

## Regroup — the debt census; no features (2026-07-24)

A stock-take session under the zero-behavior contract: nothing in
src/ moved beyond comments, zero pin movement (860 unit / 318 e2e /
13 fuzz-deep at count=10000 / 58 doc blocks — all green on the
settled tree). Deliverable is this entry; the DEBT.md question
resolves AGAINST a new file by the repo's own rule (DECISIONS.md is
an index "never a fourth prose store" — a debt store would be one).
Flags live here; each parks with its criterion.

**Duplication census.** The suspects mostly acquitted: didYouMean is
ONE mechanism (Types.fs) with 19 call sites; the reifier desugar is
ONE parameterized table; foldOutsideStrings is one scanner with 8
users; the three IsKnown resolver-extension sites in Parser
(withAmbientName :83, lambda withParams :607, topLet paramNames
:1359) share a shape but never mask, consistently. Two convictions:

- FLAG (small session or rider): the base Resolver record is
  duplicated VERBATIM ×3 — Fmt.fs:58, Program.fs:11, Script.fs:1353.
  Shape: extract one constructor (Builtins or Script owns it), three
  call sites collapse, zero behavior. Size: ~30 lines net negative.
  [CLEARED 2026-07-26, the debt rider: `Script.resolver` is the one
  constructor — and the census UNDERCOUNTED: a fourth verbatim copy
  sat in Repl.fs:253. All four collapsed; zero pin movement.]
- MICRO-FLAG (rider only): `args |> Seq.map asString |> List.ofSeq`
  ×6 in Builtins.fs — one `argStrings` local would do. Not worth its
  own session. [CLEARED 2026-07-26: `argStrings` landed — ×11 by
  then; the spawn family had grown it.]
- DESCRIBED, not flagged: the assembler's four alignment stacks
  (Brackets / PipeGroups / Compounds / Lambdas) are four bespoke
  stack disciplines over one line stream. They are NOT duplicates —
  each tracks a different closing condition — but any fifth
  alignment feature should force the unification question before
  adding a sixth.

**Diagnostics policy.** The two pinned span-repro classes CLOSED
ahead of this census: seq-commit fixed the district class, arm-commit
fixed the arm class (both pins flipped to fixed-behavior markers).
Strict spans remain red on exactly ONE class — the bare-pipe fatal —
which is a policy question (where should a bare `|` fatal point?),
not a bug hunt. Parks until: the policy is decided in a session that
owns it; the fuzzer's strict-span property is the acceptance.
[AMENDED 2026-07-26: the diagnostics-policy session RAN (77762ed)
and KEPT barePipeHint's failFatally — "the policy is decided" no
longer parks this. The park's remaining NARROW question: where
should a bare-`|` fatal point when the LHS spans lines? The
strict-span property stays the acceptance.]
[AMENDED 2026-07-27, the sibling-sentinel session [D:sibling-sentinel]
checked the merge and DECLINED it: Session E's command-mode-boundary
defect is NOT this park. That was "where does a bare `|` fatal point"
(a POSITION law for a correct fatal); this park stays exactly that.
The sentinel fixed a command-mode `;`-BOUNDARY MIS-PARSE — different
law, disjoint fix, common ancestor only in the furthest-reached
arbitration family. Do not re-propose the merge; the park's own
narrow question is untouched and still open.]

**Assume-resolver residue.** The question closes clean: one
definition [D:assume-resolver] (Script.fs:1439), four consumers
(Fmt shape pass, Lsp, Script parseLineFull + checkStatement), all
shape-only, no masking. No residue; nothing to do.

**Multiline-lambda budget overage.** The session ran ~90 lines over
its assembler budget; the overage read: the Lambdas stack carries
four fields (line, indent, depthBefore, restore) where the plan
sketched two — the restore field is the price of dangling-open
recovery, structural, not accidental. Accepted as spent; no
follow-up owed.

**Suite hygiene.** Timing sane: 860 unit tests in 3s; e2e 318 checks;
fuzz deep run 5m47s at count=10000. No slow-test flag.

**OWED (standing, promoted last session): deep-run-vs-republish.**
Second occurrence of the harness-truth race (a republish during a
live deep run makes properties fail against a half-swapped binary).
Mechanize: per-case stamp check or a lockfile the publisher and the
deep runner both honor. Until then the rule is procedural: NEVER
republish while a deep run is live. — CLOSED 2026-07-25 by the lockfile
(ci/deep-lock.sh; fuzz.weir holds, publish.sh refuses): see "The
deep-run lock" entry at the top.

**Docs fix-up (landed in-session, mechanical).** Five rot greps came
back zero-hit (sentinel, defaultValue, tryHead, collect, blank-line
prose); one real stale-prose find fixed (SKILL's block-let sentence
predated the block-let-RHS session); GUIDE's intro example moved to
the bare spelling; when-do-I-force now names its FOUR customers
(REUSE, TIMING, GLOB's cd seam, splat-forces-at-spawn as the
non-customer); SEMANTICS gained the one-glyph role inventories for
`;` `|` `$` ahead of the argv word law. A doubled heading in this
file (shared-flags entry) deduplicated.

**Plan tidy-up (landed in-session).** All 48 PLAN*.md moved to
plans/, each header status-stamped (EXECUTED + git landing date;
the miner proposal re-stamped PROPOSED-AMENDED with its remaining
scope: rmatchAll + the corpus-mine.weir rewrite), plans/README.md
is the chronological one-line index. Reference sweep: 55 PLAN-
mentions outside plans/; the 5 path-form references (.md-suffixed)
updated to plans/ paths, the 50 name-citations left as citations —
the index resolves them. Root now holds only living docs.

## The argv splat — N things, N words; the park closes (2026-07-24)

The one-splice-one-word law always carried a silent asterisk ("...and
if you have many, leave the grammar"): `run prog (Seq.append base
files)` was the only spelling for a variable-length command. `$@xs`
retires the asterisk — it is the bare-mode spelling of an operation
weir already committed to (the argv law plus variable-length commands
makes it structural, exactly the block-let-RHS genus). Bash's `"$@"`
is the one expansion bash got right; weir adopts precisely its
semantics under a distinct glyph while `$*`-rejoining stays
unrepresentable, and the safety story GAINS a clause rather than
loses one — adversarial elements (spaces, semicolons, glob chars)
stay single words by the same law that made scalar splices safe,
pinned adversarially.

The sharpest new cell is the contrast the mid-word rejection draws:
`$file` GLUES into a word under construction (`--file=$f` works),
but `$@files` CANNOT (N words have no single word to join) — a
fatal at the splice naming both honest spellings. The head
rejection names the computed-head park (a splat head is N heads);
the empty seq vanishes to zero words, which is the typed replacement
for bash's `${arr:+...}` conditional-flag dance. Enumeration is
once at spawn — argv is finite, so the splat forces by necessity;
the when-do-I-force doc gains that as a fact, not a warning.

The fuzzer was owed and paid: the generator emits `echo $@([...])`
and the transform library asserts splat-of-literal ≡ inline words,
byte-identical (paren-wrapped — `$@[` bare-bracket is not a form,
only `$@(expr)`, a boundary the transform's first failure taught).
The `$@"` cell is pinned both ways so the parked interp-verbatim
opener inherits a held boundary. Tokens learned the form
same-session (one-brain): `$@name` whole, `$@(` delimiters, in the
splice legend. The park closes with its arc intact: proposed at the
sigil audit, parked for receipts, two arrived (conditional flags,
glob-into-git-add), opened by call.

## Seq.distinct — pulled forward by its customer (2026-07-24)## Seq.distinct — pulled forward by its customer (2026-07-24)

The glob session deferred its overlap-dedupe product cell honestly;
this closes it same-day. Distinct rides the class machinery the
containsScheme precedent laid down (one scheme, one override line),
stays lazy with first-occurrence-wins, and remembers only what it
has yielded — the pull-count pin shows first-2 over a counted dupe
source stopping at exactly the second novel element. The Eq
constraint rejects seq/function elements at the use site with the
teaching the equality work already wrote.

A finding for the stashed miner proposal while here: Seq.groupBy
already exists in the module table — the proposal's "countBy/groupBy
are NOT pulled in (no receipt names them)" premise was doubly stale
(one of them had been shipped all along). The proposal now needs
only rmatchAll and the rewrite itself; feed and distinct landed
ahead of it.

## Path.glob — discovery typed, the expansion law untouched (2026-07-24)## Path.glob — discovery typed, the expansion law untouched (2026-07-24)

The recategorization leads, as the plan demanded: the old exclusion
guarded against bareword EXPANSION in argv, and nothing here touches
argv — `Path.glob` is a function taking a pattern and returning a
typed seq; a bare `*.txt` in a command line remains a literal word.
The law amends, it does not retire.

Two premise corrections, both probe-caught. The library probe:
FileSystemGlobbing fails the SPEC before AOT is even asked — its `*`
matches dotfiles and the API exposes only final match sets, so
bash's dotfile law cannot be imposed (it is also unrestorable in the
offline container). The fallback clause was taken as written: a
hand-rolled matcher over the standard subset, ~90 lines, whose
per-segment structure is exactly what the dotfile law needs. And
the symlink bracket: the plan's "follow, matching bash; visited-set
protection" described PRE-4.3 bash — modern globstar does NOT
traverse symlinked dirs at all. The first implementation followed
links with a visited set and the loop fixture promptly showed the
set's identity was wrong (paths reached THROUGH links never
canonicalize); the bash-parity law is simpler and loop-immune by
construction — `**` skips symlinked dirs, explicit segments follow
them — and the fixture pins both halves.

The seams taught rather than smoothed: relative patterns resolve
against the cwd AT ENUMERATION (the standing laziness/Cwd seam —
the e2e shows the lazy glob seeing the post-cd world and
`|> Seq.force` pinning the batch, the when-do-I-force doc's third
customer); no matches is the empty seq with the match-`[]` idiom
(no nullglob/failglob ceremony — weir's empty composes where bash
needed shopt). The scriptPath gate paid off on schedule: the
script-relative fixture globs its own tree after `cd /`. The
argv-splat park holds its second receipt (glob-into-git-add is the
exact shape; argv building spells `Seq.append` today) without
opening. One product cell deferred honestly: the distinct-dedupe
composition waits for Seq.distinct (the miner session) — the plan
listed the product ahead of its dependency. 10k files enumerate in
14ms against a 2s ceiling.

## scriptPath — the $0 gap closes by sequencing (2026-07-24)## scriptPath — the $0 gap closes by sequencing (2026-07-24)

Opened by dependency-ordering rather than receipt count, and the
row says so: Path.glob is next in the queue, its first customers
are script-relative by nature, and landing scriptPath first means
glob arrives composable instead of generating its second receipt
as friction. The thin-receipt honesty from the stashed proposal is
superseded, not forgotten.

The semantics went in exactly as decided and pinned: absolute at
STARTUP (the fixture cds before reading and the answer doesn't
move), symlinks unresolved (bash's $0; the realpath spelling is
one GUIDE line away), one answer across relative/dot/absolute
invocation, and the shebang boundary reads from the other side of
the typed-argv argv-slicing verification — the script's path, not
the interpreter's. The plan's Path.parent verify resolved to the
already-shipped Path.dir, so the derived idiom is taught
(`scriptPath |> Path.dir`) and nothing new was built — one
primitive, compositions in the library. fuzz.weir does NOT adopt:
git-toplevel is the better spelling for a tool that wants the repo
root, and the friction ledger closes with that non-adoption named.

## The !-glyph audit — fourteen challengers, the incumbent holds (2026-07-24)## The !-glyph audit — fourteen challengers, the incumbent holds (2026-07-24)

An audit, not a session: the effect glyph stood trial against
fourteen challengers and holds by demonstrated elimination. The
scoring principle was minted mid-walk and is the entry's keeper:
**priors are weighed by audience-relevance and correction-cost, not
existence** — the `*`/C-deref objection died there (the prior
exists; weir's audience never reaches for it in command position,
and correcting it would cost nothing because nobody pays it).

The named findings, one line each:
- `^` — opposite-resolution-same-glyph: `^(cat x)` as effect beside
  `^cat x` as force would make one glyph mean both resolutions;
  disqualifying on its own.
- `+e` — the env form collides with `set +e`'s INVERSION prior
  (bash's + DISABLES); an env-carrying effect reading +e as
  "errors off" is a trap laid on purpose.
- `&` — the background whisper (trailing-& prior) plus a
  binding-time mismatch against the PowerShell call-operator prior
  (`& cmd` invokes NOW; weir's form is a block header).
- `\` — escape freight everywhere plus line-continuation
  collision; its one GOOD prior (bash `\ls` = bypass-the-alias)
  belongs to FORCE, not effect — which is the `^` family's job,
  and yielded the `\cmd` doc byproduct (the stranded log shows
  ZERO attempts to date; count recorded, feeding the
  hint-vs-nothing decision if they appear).
- `@` — the early "taken" objection was STALE (`@(` lexes clean
  beside `@"raw"`); the real finding is PowerShell's
  array-subexpression prior: `@(...)` CAPTURES there, and weir
  would make it an effect — capture-vs-effect inversion for the
  nearest audience.
- `$` / `$$` — inverting the value law: `$` is weir's CAPTURE
  family marker; any effect use subtracts its defining meaning.
- `.` `_` `'` — multi-collision tombstones (paths/field access,
  the lambda-shorthand, char-literal adjacency); eliminated
  without ceremony.
- `$!` — the last real challenger, felled by the user's
  capture-not reading: the modifier SUBTRACTS the base glyph's
  defining capability (a $-form that does not capture) — backwards
  pedagogy, teaching the exception before the rule.

The incumbent's actual virtue, stated once: `!`-as-negation is the
CHEAPEST misread in the space to correct — one type error,
instantly diagnostic, one taught sentence (now taught: the
not-law sits in SKILL and GUIDE beside the sigil teaching,
doc-tested). The revisit trigger is behavioral, not aesthetic:
stranded `!x`-as-negation attempts weigh a HINT ("`!` runs
commands; negate with `not`") before any rename is entertained.

Advisor errata, kept honest: "exhausted" was called twice before
the walk was; `$!` was mis-ranked for three rounds by grading the
DESIGNED framing instead of the reader's prior — both
user-caught, both the audit's own scoring principle applied to
its scorer.

## Env.load consumes Default — the flip cell, and the stack's floor (2026-07-24)## Env.load consumes Default — the flip cell, and the stack's floor (2026-07-24)

The follow-up landed on its gate, and its one design sentence held:
no twin mints. Env bools are true/false TEXT — there is no presence
semantics to make false unreachable — so `[<Default true>]` is a
plain fill and `[<Default false>]` is a REAL statement
("absent → false"). That second half is the flip cell: the same
attribute, the same literal, LEGAL at Env and REJECTED at Args
(where presence already rests at false) — pinned both sides in one
test, and the sharpest demonstration yet that validation belongs to
the consumer's arm, exactly as the attributes architecture drew it.

The three-layer premise translated faithfully rather than
literally: Env.load reads process env only, so the file layer
enters via runEnv/fromFile overlays becoming the CHILD's process
env — the e2e spawns a real child script and proves the order
(overlay beats env beats attribute; the resting point sits below
the whole stack, literally). The receipt is bicep-deploy's
detachAll dance, deleted at the declaration — and the user pushed
the auth toggle to `[<Default 0>]` too (its `defaultValue 0` use
site was equally literal), so BOTH env dances deleted; the
absent-is-data half of the boundary keeps its living example in
fuzz.weir's computed seed. Prior art noted as the
plan required: the Arquidev Env lib's [<Env.Default>] is the
convergent design — the user's own library arrived at
attribute-defaults for env records independently; convergence
noted, no API mimicry.

## [<Default>] — the resting point moves; a door prediction cashes (2026-07-24)## [<Default>] — the resting point moves; a door prediction cashes (2026-07-24)

Forward-archaeology grading line, per the plan: the attributes
session left "field defaults — the third door stays open as a
candidate with its own customers" — and the prediction CASHED
exactly as written. The customers arrived (fuzz.weir's count; Env
config fallbacks queued behind it), and the attribute serves them
at zero grammar cost while the language door (defaults in type
declarations) stays shut. Park discipline working as designed:
the door entry now carries the pointer.

The law is one sentence — Default moves the resting point — and
the bool case is where it earns its subtlety: presence semantics
make false unreachable under a true default, so the attribute
MINTS the `--no-X` twin. The bless ruled the minted names into the
collision namespace and did-you-mean but out of short derivation;
implementation-wise they ride the eval indices as negative entries,
so the hint machinery saw them for free, and polarity-aware dup
detection keeps the old given-twice message for same-spelling
repeats while naming both spellings across polarities
('--color' and '--no-color' are both given).

The rejection cells all teach: false-on-bool (redundant — the
resting point is already there), Default-on-Option (optional with
a default IS a default), literal-type mismatch, and the subcommand
slot (no flag derives — checked against the OUTER record since
sharedOf strips the slot's attrs; a cell the implementation had to
go find). Positional's not-yet wins its composition by ORDER (the
positional check runs first in validateFields), pinned so the
precedence is decided rather than incidental. The zero-diff bar
held: no pin moved, e2e unchanged, and records without Default
take byte-identical paths (empty minted lists, same messages).

fuzz.weir is the boundary's living example: count went
[<Default 10000>] and its match DELETED; seed stayed Option
because its default is computed (fresh nanoseconds, spawned only
when absent) — literals in the attribute, computation in code,
both shapes in one Cli. The Env.load follow-up gates on this
report; its pre-note stands (env bools are true/false text — no
presence semantics, plain fill, no twin).

## The spawn spec — six names audited, one spawn, feed lands (2026-07-24)## The spawn spec — six names audited, one spawn, feed lands (2026-07-24)

The family audit found what audits find: six builtin names
(cmd/run/cmdEnv/runEnv/completedEnv/exitCodedEnv) over a three-axis
product (output × env × stdin), with the spawn wiring copied three
times inside Proc — psi construction, env overlay, the not-found
mapping, the stdin writer, the kill/reap tail. The refactor is the
assembler-formalization move one layer down: an internal Spec record
(Prog/Args/Env/Input), ONE starter, and the output axis as consumer
functions — linesOf, streamCodeOf, completedOf — which is the
reifier law restated in code: the consumer IS the meaning. The
public wrappers kept their signatures, and the zero-behavior
contract held exactly: no pin moved, and the two byte-identity pins
(run ≡ cmd |> print; !() ≡ orFail-default) re-ran unchanged — those
equivalences now hold structurally, not by discipline.

`feed`/`feedEnv` then landed as constructors seven and eight rather
than as a third copy of the wiring — cmd + stdin, data-last because
the input is the pipeline's subject. The laziness rule met its
first INPUT customer: the writer task pulls the source as the pipe
accepts, pinned by pull count (a million-line source into `head -1`
pulls a pipe-buffer's worth, not the million) and by the huge-range
e2e on the binary. Stdin closes on exhaustion (sort-class children
get their EOF, pinned). The miner's sha256 shape runs end to end.

The PARK, banked as designed: a user-facing spec type
(axes-as-fields over a base value, the Rust-Default idiom;
consumers as the output axis) waits for the family to genuinely
need a NINTH name or a new axis — Cwd is visibly next, at the
Session seam. When that session comes it is an EXPOSE, not a
build: the internal spec is already the thing it would surface.

[AMENDED 2026-07-25 by [D:drop-command-builtins]: the reopen axis
is no longer "a ninth NAME" — the six head-tier constructors were
dropped (every command head is now a literal). It is now
SCOPES-VS-ARGUMENTS: a value that must vary the child's ENV or
STDIN still routes through internal spec twins (no user-facing
name — the park stays shut); the park reopens only if a value must
vary the HEAD and that head set cannot be enumerated into a
branch. Cwd, if it comes, is still an EXPOSE.]

## The reifier family completes — one law, four cells (2026-07-24)## The reifier family completes — one law, four cells (2026-07-24)

The archaeology ran first and settled the genus: orFail's silence
was accident-grade. No pin held it — only the message desugar was
pinned — and the in-code comment revealed the mechanism (riding
complete's capture plumbing, stderr replayed after the fact). Both
flagship orFail sites used --quiet git commands, which is exactly
why the corpus never noticed. The fix gives orFail the streaming
spawn (stderr inherits, stdout relays live), and the implied claim
from the reifier session became provable: bare `!(cmd)` and
`cmd | orFail msg` now stream byte-identically, differing only in
the failure message — pinned behaviorally in e2e.

`| exitCode` fills the fourth cell: streams like a bare command
line, never raises, the code as int. The old park opened on its own
criterion — it was right that complete carries the code; the missing
cell was the code STREAMED, and the axis only became visible when
the family sat in a table. The law now governs instead of a zoo:
output goes where the meaning goes. Predicates and inspectors are
quiet/captured (their output IS the result); asserts and control
flow stream (their output is for the human).

The conflict cells were decided on paper and landed as parse-time
teaching errors: `$(… | exitCode)` names the tension and points at
`| complete` (the captured-code cell that already exists);
`!(… | exitCode)` and bare statements get the discarded-int hint
(`set +e; cmd; rc=$?` muscle memory will type that shape); district
lines inherit the bang ruling through the wrap desugar — all four
pinned. The env story follows the completed precedent: exitCodedEnv
rides the same desugar twin, and the expression-position builtins
are exposed. The semantic-token walk learned the new spine names the
same session — exitCode chains keep their command coloring.

Both agent frictions closed with their arcs: fuzz.weir's
reproduce-hint un-workarounds into the orFail message (streaming AND
deciding, the thing that had no spelling), and graceful-cancel's
code-as-data half is the match-on-130 idiom — its selection half
stays complete's, said explicitly. The flagship's diff-files guard
swapped --quiet for --exit-code as the living example: when the
pull guard fails, the failing paths now SHOW.

## Arm-commit — the span-soundness arc closes (2026-07-24)## Arm-commit — the span-soundness arc closes (2026-07-24)

The smallest session of the window carries the arc's last stone.
Invariant 3 was built to audit spans generatively; its two finds
turned out to be one disease at two separators — an attempt-wrapped
`many` silently backing out after consuming a separator, handing the
tail to whatever alternative would take it. Seq-commit killed the
`;` instance (and the district-wrap class with it); arm-commit kills
the `|` instance: one line, the attempt retired, and the pinned
repro now reports at 6:23 — the junk itself — where FCS, run as the
referee, reports the NEXT arm's line. Weir beats its oracle on
error placement here, recorded without a pin (the oracle compares
verdicts, not positions; the FCS probe is in this entry).

The probe set earned its place in the plan. A bare `|` inside an arm
RHS is an ARM SEPARATOR — F# reads it identically — so the "greedy
chain absorption" premise was corrected: command chains in arms ride
`$()`, and the committed loop gives the bare spelling a located
error at the pattern position. Or-patterns: weir rejects (located at
the second bar), F# accepts — the unnamed divergence is now the
or-patterns row, filed with no receipt and a reopen condition.
Guards, Regex arms, reserved-word heads, and the REPL one-line
grammar all pass under commit; the bare-pipe hint's original
customers re-ran untouched.

The graduation earned its keep the day it happened: the first
strict deep run found the law's THIRD instance — junk on a
record-literal field line reported at the PREVIOUS line with the
update grammar's dump ("expecting 'with'"), because the literal's
attempt spanned the whole record and a deep field failure rewound it
into the update alternative. Fix in the same session, same shape:
the literal COMMITS on its head (`ident =`, guarded against `==` —
an update source can start that way) — the field junk now reports at
its own site with the literal's own expectations. Trailing `;}` was
already a reject (probed — nothing to preserve); updates keep their
path (pinned).

So the arc's period: WEIR_FUZZ_STRICT_SPANS defaults ON — the
invariant that shipped gated-red is a standing guarantee in the CI
smoke, and the soundness premise (a `|` after a completed arm at the
same paren depth can only be another arm) is pinned to the assembler
invariant it rides (offside-close wrapping). Built → found twice →
fixed twice → promoted → found a third the same day → fixed the same
day: the fuzzer plan's "finds are pins, triaged" clause, running as
a flywheel now.

Harness-truth watch, RECURRED (argv-splat deep run, 2026-07-24): a
deep run CONCURRENT with a republish fails en masse — P and T(P)
execute against different binaries mid-swap, and the stamp gate
(checked once at first use) does not catch it (0 STALE messages,
5/13 spurious diffs; both re-runs on the settled binary clean).
Second occurrence promotes it from watch to OWED: the fix is a
per-case stamp echo or a run-vs-publish lockfile. Until then, the
discipline is DO NOT republish while a deep run is live.

## Semantic tokens — the mode boundary made visible; the park closes (2026-07-24)## Semantic tokens — the mode boundary made visible; the park closes (2026-07-24)

The standing semantic-tokens park closes on its named trigger with
its arc intact: parked at LSP v1, the REPL-coloring session laid the
one-brain groundwork ("the eventual semantic-tokens conversation
gets its evidence"), and the verdict-split receipt fired it — the
find whose whole story was an expression silently becoming a phantom
command is now a feature whose whole job is making that impossible
to miss.

The engine is ~90 lines riding entirely on what existed: analyzeLines
for the checked statements, a TypedExpr walk for TECmd nodes,
translate() for physical homes. The legend is three types and the
claim deliberately minimal — mode spans only, expression land emits
NOTHING (TextMate keeps lexical coloring; rebuilding it server-side
would double the drift surface). The synthetic-span rule is the
span-soundness lesson institutionalized: a token is emitted only
where the logical slice appears VERBATIM at its translated physical
position, so district wrapper glyphs and join text emit nothing
rather than something mislocated — the district pin proves body
lines token while nothing lands past EOL.

Two shapes needed recognizers the plan didn't enumerate: reified
chains (| succeeds/complete/orFail) desugar their ECmd into an
application spine before the checker sees it — the walk recognizes
the spine so the command still tokens while the reifier name stays
lexical (the in-session decision, as recommended); and sigil-origin
TECmd spans open on the `$(`/`!(` glyphs, so the head scanner walks
past them (the depth-2 pin found it — painting `$(ec` as a head on
the first try). A fixture correction along the way: `$(...)` as a
direct command ARGUMENT is a type error (seq arg) — the nested
spelling rides a paren splice; pinned as found.

The shadowed-cat trio is the acceptance it was designed to be:
bound → no tokens; unbound → command tokens; `^` → forced head with
the prefix in the span. The verdict-split repro renders
expression-colored (no tokens at all — the failed statement emits
nothing, which IS the correct rendering of a parse error). The REPL
rider took eleven lines: the existing fish-trick head arms a dim
argv tint that un-dims at `|` — same resolver, no new derivation.
VS Code maps the three types onto standard TextMate scopes (argv as
string.unquoted — fish/nushell's inert-words convention), so default
themes color the boundary with no user config; SMOKE.md carries the
interactive checks. Micro stays coarse by design, noted in its
header.

## The seq-commit fix — the verdict split dies, and the diagnosis corrects itself (2026-07-24)## The seq-commit fix — the verdict split dies, and the diagnosis corrects itself (2026-07-24)

The semantic-tokens plan gated on "the assume-resolver fix", and the
gate session's probes promptly corrected the blessed diagnosis: the
resolver was never the door. A tenv-known binding with junk after it
errored correctly all along (the head gate refuses known names); the
swallow needed BLOCK scope, and the explicit `in`/`;` single-line
spelling reproduced it identically. The mechanism: seqExpr's
`;`-chain wrapped `; elem` in attempt, so a failing element
un-consumed the `;` and the backtrack re-parsed the tail OUTSIDE the
let-in scope it belonged to — where the binding is genuinely unknown
and check's assume-resolver legitimately claims it as a command. The
scope was never lost in valid programs (probed: let-in spans `;` in
both spellings); only the FAILING parse could rewind into the
wrong-scope alternative and come back "successful".

The fix is the consumed-`|` fatal's sibling, one line: a consumed
`;` commits to its element. Check and run now agree on the class,
located at the junk. Two dividends: the DISTRICT span class fixed
for free — the primary error now anchors ON the junk instead of
past the wrapped segment's end (that pin flipped to the gain), and
the fuzzer's hard floor is green on deep runs again (seed 8675309,
10k clean). Strict spans remain red on exactly one class — the
bare-pipe fatal — which is its own open policy question, unchanged.
The verdict-split pin flipped from open-bug marker to fixed
behavior, tying the find to its closure.

## Shared flags by containment — the friction arc closes (2026-07-24)

The shape sighted at the bicep original (Argu's [<Inherit>],
reshaped away in translation) and paid for at the port (two
three-arm extraction matches for quiet/verbose) deletes
structurally: a record CONTAINING the subcommand union declares
shared flags once. The review's design premise held clean through
implementation — an [<Inherit>] marker would have PRESERVED the
repetition; containment is the deletion.

The mechanics stayed one-owner all the way down. Check side: the
field law widens by exactly one clause (at most one union-typed
field), the bare union's payload validation hoisted and shared, and
both collision routes (kebab-derived and explicit-Short) reject at
declaration — the runtime scanner never faces an ambiguous name.
Eval side: pass 1 floats shared flags and anchors on the first
non-flag token (unknown flags consume no value — the standing
precedent made the classification total); the scope's short tables
derive over the UNION of tiers via a combined pseudo-record, so the
cross-tier contest cell (-q for --quiet vs --query: neither derives
in that case's scope, both named in the ambiguity error, the global
help omits the short while the scoped help shows it) fell out of
shortTables unmodified. Validation collects across both tiers into
the one boundary error; did-you-mean candidates are tier-aware
(before the case: shared + case names; after: shared + the selected
payload). Case-scoped --help decided YES and pinned.

The port's Cli gained the containing record, the extraction matches
deleted, the lifecycle smoke stayed green, and jira-branch's
record-only load proves the unchanged shapes zero-diff (the shipped
argv e2e section passed untouched). The fuzzer is NOT owed here —
argv parsing is not assembler grammar; stated so the
grammar-membership rule's scope stays crisp.

## Multiline lambdas — the park's face opens, and the harness bites twice (2026-07-24)## Multiline lambdas — the park's face opens, and the harness bites twice (2026-07-24)

The probes-first pass reshaped the session before a line landed: MOST
of the blessed form already worked. Deeper-line bodies, block lets,
sibling `;`, compounds (the compound-paren prune carrying its
port-session fix as designed behavior), districts, blanks, nested
lambdas, and the receipts' pipeline-stage-spanning shape — all
emergent from the standing indent machinery. What was actually
missing: closer-alone lines (the col-0 law and the sibling rule both
broke them), the guard rails (leak/EOF errors naming the lambda), and
the parser-side spine.

The FCS probes drew the boundary sharper than the plan guessed. Body
AT the opener's indent: F# accepts — a silent pre-change weir-reject,
now F#-parity (gain). Body LEFT of the opener: weir errors naming the
lambda where F# floors at the enclosing context and tolerates the
dedent — the lambda-body-offside row, pipe-alignment's strictness
sibling. Closer-alone at any indent: F# accepts, weir now agrees
(gain). And the sigil-interior premise CORRECTED itself under probe:
multiline lambdas inside $() were already legal as physical
continuation — the single-LOGICAL-line law never spoke to line count.

The restore rule is the session's story, and the new-grammar
obligation paid for itself within hours of existing. The fuzzer
(lambda shapes freshly in its grammar) caught the attached-closer +
block-sibling swallow — `40` after a lambda's deep last line joined
as an APPLICATION, a pre-existing hole the hand pins never named.
The opener-indent fix for that promptly broke e2e's fold receipt —
Seq.fold's init at the opener's own column must CONTINUE the
application while v2-block siblings at the same relative geometry
must SEQUENCE. (prior, opener) cannot discriminate; the true
quantity is where the lambda's STATEMENT started. Pend now tracks
StmtLevel (sibling/`in` joins and first-line-after-dangling-head
start statements); the lambda entry records it at arm, the pop
restores it. Both counter-shapes are pins.

Parser side, two moves: lambda bodies inherit the letCmdOk spine
(the block-let-cmd boundary widened BY PLAN — command block-lets
legal in lambda bodies on a let-RHS spine; bare-statement lambdas
keep the law), and lambda params extend the ambient resolver — found
by e2e when tools/test-counts' `let hash = line |> ...` turned into
a phantom command under the assume-resolver (params-shadow-PATH
reached check time; pinned).

The parens-spanning park AMENDS: lambda parens opened by their own
receipt; general parens still parked, content shrunk. The port's
friction site reads multiline; the greedy-`;` single-line spelling
demoted to "also legal" in the docs. Budget honesty: ~90 assembler
lines against the ~40 estimate — the overage is the restore rule's
two counter-shapes, caught by the layered nets exactly as the
fuzzer plan promised its first customer.

## The assembler fuzzer — Session 2: the net earns its keep (2026-07-24)## The assembler fuzzer — Session 2: the net earns its keep (2026-07-24)

The transform library completed to the plan's full list — district ↔
`!(...)` (the desugar claim), bare command RHS ↔ `$(...)` (the pinned
equivalence at scale), block siblings ↔ single-line `;`, Stroustrup ↔
inline — each law probed before encoding, and the probes drew one
boundary the docs imply but never state as a unit: `;`-joining is for
unit-EXPRESSION siblings only (an inner let demands the `in` spelling;
a command line takes `;` as a literal argv word), so the transform's
eligibility is print-only bodies, on record in GRAMMAR.md. The
composition property now flips random subsets of every spelling at
once over a re-indent plus comment/blank surgery.

Invariant 3 (span soundness) paid for itself within its first 200
cases — twice. Class one: a parse error AFTER a district in the same
statement anchors on the district's wrapped segment, col past the
physical line's end (synthetic wrap text has no physical home), with
the user's actual line surviving only as a translated backtrack note.
Class two, nastier: junk in a nested match arm, when the outer match
already carries a completed arm, makes the retry read the next `|` as
pipe confusion — the consumed-`|` fatal (built to WIN the
furthest-error rule) wins against the TRUE deeper failure and aims
its teaching hint at an innocent line. Both hand-minimized to
6–8-line repros and pinned as current behavior in Weir.Tests. The
policy question they jointly pose — who wins the furthest-error
competition across wraps and fatals; is deepest-wins the rule the
diag anchor should follow — is parser-diagnostics surgery beyond a
harness session's remit: STOPPED and reported, strict positional
assertion parked behind WEIR_FUZZ_STRICT_SPANS, the hard floor (junk
is always diagnosed — the silent-swallow guarantee) unconditional.

Invariant 4 (fmt roundtrip: succeeds, idempotent, sexpr-shape via the
respace guard's own predicate, output-neutral on the binary) came up
clean over the smoke and the deep run — consistent with fmt's
internal safety gate doing its job.

Then the deep run (seed 8675309, 10k/invariant) delivered the find
the plan predicted, via invariant 3's HARD floor: `weir check` exits
0 — one cmd-not-found warning — on a script the runner REJECTS. The
5-line repro: inside a block, after a sequenced unit statement, a
`let` RHS headed by a KNOWN binding (`let v4 = v3 ?!?`) parses under
check's assume-resolver as an ASSUMED COMMAND — the user's junk
vanishes into a phantom command's argv, and the "missing tool" it
warns about is the user's own string binding. The verdict SPLIT
breaks check's contract (check-green must mean runnable modulo
uninstalled TOOLS — v3 is not a tool), and the product triple nobody
named is block-let-command-RHS × assume-resolver ×
bound-head-after-`;`. Pinned as the open-bug marker in Weir.Tests;
the fix candidate is one clause — the assume-resolver stops claiming
names the env already KNOWS (bindings-beat-PATH extended to check
time) — held for bless, since the resolver feeds check, LSP, and
fmt's shape guard alike. Deep runs stay honestly RED on this class
until it lands; the CI smoke's pinned seed does not reach it.

The harness is wired: CI smoke (pinned seed, after publish + e2e, in
.gitlab-ci.yml and ci/local.sh), tools/fuzz.sh for fresh-seed deep
runs (prints its seed; failures reproduce by command line), and
PROCESS.md now carries the grammar-membership rule — new assembler
features owe their shapes to the generator and their equivalence
claims to the transform library, the metamorphic law part of the
feature. With that, the widen-the-net park from the silent-swallow
postmortem is formally CLOSED: this harness is its answer, and the
owner question is resolved — the fuzzer owns the unnamed-triple
space, the hand-pinned matrices keep the named cells. Grading note
for the Session 1 prediction: REVERSED to FOUND. With Session 2's
instruments in the battery, the deep run surfaced a genuine
correctness bug (the check/run verdict split) plus two span-quality
classes — the prediction missed only Session 1's narrower instrument
set; the base-rate argument was right about the seam, wrong only
about which invariant would catch it. The metamorphic net
(invariants 1, 2, 4) remains clean at 40k+ cases across four fresh
seeds — the equivalence laws themselves appear to hold.

## The assembler fuzzer — Session 1, and the boring outcome's shape (2026-07-24)## The assembler fuzzer — Session 1, and the boring outcome's shape (2026-07-24)

The widen-the-net answer, executed: a generator of valid-by-
construction line-shape programs (FsCheck, test-side — the FCS
precedent; the gen builder took the stateful scope threading fine
once repetition went through an explicit state-passing combinator,
so the hand-rolled fallback stayed unused). Invariant 1 runs the AOT
binary and demands (rc, stdout, stderr) byte-identical under blank
insertion, comment insertion, whole-block re-indent, and their
composition; invariant 2 demands analyzeLines totality on every
generated program and mutated neighbor. Shrinking is delta debugging
on top-level statements with dependency closure — program-unique
names make the closure set arithmetic — and it produced minimal,
readable repros from the first failure on.

Bring-up was the probes-first discipline paying twice. The generator
had to LEARN two placement laws the docs state only obliquely: bare
command lines are top-level-only (an if body is expression territory
at any nesting — bare `echo` is an unbound variable there; districts
are the command spelling in bodies), and a bare command inside a
let-block body becomes the let's command RHS and `;`-joins its
successor into argv (warned, then type-errored — surfaced, not
silent). And the fuzzer's own first failures found a third: two
record types with the same field SET make every literal ambiguous —
field names now carry their type's tag.

The prediction, graded: MISSED on its own words. Three deep runs
(10k cases each, fresh seeds 424242/777/20260724, ~70k mutated
neighbors, composition included) found zero product bugs — the
recent hardening sessions appear to have actually hardened the seam
the ledger said was softest. The denominator is committed
(tests/fuzz/GRAMMAR.md): no env districts, no sigils, no seq/Regex
patterns, no tuples, no update literals yet — the boring outcome is
bounded by that list. Harness truth held to its own rule: the
equality detector carries a graded positive control (a deliberately
non-neutral edit fails the property), so "passed" means "compared",
not "never fired".

One real find anyway, from the law probes: SKILL.md still taught
"Blank lines END statements — never leave one inside an indented
block" — the founding divergence RETIRED by [D:body-blanks] the day
before, contradicting the same file's own line 15. Prose the
doc-executor cannot see is exactly where claim-vs-behavior drift
survives; fixed to the col-0 law. Session 2 owes: the remaining
transforms (district ↔ sigil, bare ↔ $(), sibling ↔ `;`, bracket
styles), span soundness, fmt roundtrip, CI smoke + tools/fuzz.sh,
and the PROCESS line making grammar membership part of every new
assembler feature.

## Field alignment — the records half (2026-07-24)## Field alignment — the records half (2026-07-24)

The pipes rule's sibling, opened on the planted `Commit` off-by-one.
The probes corrected ME this time: the first read said "F# tolerates
deeper fields in record literals" — but that probe's snippet had no
record type, so both sides were rejecting for a missing type, not
indentation. The typed re-probe showed F# errors on off-by-one
fields in declarations, literals, and lists alike — the whole rule
is F#-PARITY, no strictness-family caveat needed. (Pin hygiene
lesson, same day it was needed: a probe that can fail for a reason
other than the one it names proves nothing.)

The mechanism rides the bracket stack: each bracket records its
first entry's MEASURED column (opener-line content for inline
openers; the first continuation entry for dangling Stroustrup;
update headers skip their source and anchor on the first field —
found by the update pin going red). Entry-starting lines must hit
the anchor exactly; value continuations, closers, and explicit
trailing-`;` lines stay free. records-fields-ignore-indent NARROWS
to its honest residue: update/closer offside edges.

Two real catches on landing day. The flagship's own gitrepoHeader
list had sat one column off since the port session — latent in
every commit since, caught by the first full e2e under the rule.
And fmt's +1/+2 offset arithmetic disagreed with the assembler on
a `[ x`-spaced list (anchor at +2, fmt pulling to +1): fmt now
aligns to the measured anchor, so the two components agree by
construction instead of by convention.

Day-two catch, user-planted: a field one column off its OWN
attribute line slipped through — the `>]` dangle exemption (there
to suppress the separator between an attribute and its field)
was also skipping the alignment check, and the first symptom was a
MISLEADING runtime argv error while the checker stayed happy. The
dangle now suppresses only the separator; an attribute and its
field are one entry on two lines and must align, pinned both ways.

Third plant, third class: a lowercase union case (`upToDate`) got
the RIGHT message at the WRONG place — 39:5 instead of 38:7 —
because caseDecl validated AFTER consuming the word and its
trailing whitespace, and ws crosses physical lines in assembled
statements, so FParsec reported from the next case's segment. The
fix is peek-validate-then-consume (lookAhead restores the position
before the fatal fires), pinned on the exact column. The pattern to
watch: any parser that consumes-then-fails attributes its error to
wherever the stream drifted — spans want anchoring BEFORE the read.

## Pipe alignment — the indentation session (2026-07-24)## Pipe alignment — the indentation session (2026-07-24)

Opened on a live receipt sitting in the committed flagship: `| Init`
one column off its siblings, accepted. The probes-first discipline
immediately corrected the premise: F# does NOT error on off-by-one
pipes — FS0058 is a WARNING, and the oracle counts warnings as
accept. So the off-by-one rule lands as the warning-vs-error
strictness family (refutable binders, exhaustiveness — F#'s warnings
weir escalates), a NEW weir-stricter divergence row, not parity.

The probes also found two real F# gaps the same rule closes
F#-ward. Arms left of their match head are an F# ERROR weir
accepted (the col-0-arms-under-deeper-match shape) — now rejected
naming the head. And the nested-match return — an outer arm at the
outer column after a nested match — was a WEIR-reject: the flat
inert join handed every arm to the innermost match and the
exhaustiveness checker refused the remains, while F# reads the
columns and accepts. Shallower arms now offside-close nested
compounds (the wrap machinery pipes previously never touched), so
the canonical F# shape assembles correctly. A fidelity loss nobody
had reported, found and fixed by the session that came to tighten.

The mechanism: pipe GROUPS (a column stack; the first pipe after a
non-pipe line opens a group, anchored at-or-right of the innermost
compound head; consecutive pipes must hit a group column exactly).
The |-inertness law narrows honestly: indentation-blind WITHIN an
aligned group, never across groups. Zero unit-pin movement across
803 tests — the tightening rejected nothing the corpus ever did.

## The goodness sweep — every example on the newest idioms (2026-07-23)## The goodness sweep — every example on the newest idioms (2026-07-23)

The docs-and-examples sweep after the window's landings, per the
idioms-rot rule. bicep-deploy got the biggest lift: the hand-rolled
subcommand match, usage string, and empty-flag fail all deleted for
the typed front door (a three-case union over one Doc'd flags
record — the dispatch is now a check-time obligation), and its four
records went Stroustrup. repo-report matches argv and the porcelain
rows with seq patterns ([] + cons over a forced seq — the
double-enumeration it always had went away with the force).
git-subrepo's detectHeadBranch dropped its tryHead/Option layering
for cons matches, and graftStep binds the three author lines with a
fixed-arity [date; email; name] — the pattern the feature was made
for. test-counts swapped its cmd-builtin call for the capture
sigil. The README front-page snippet now leads with a seq match.
Everything checks, fmt-canonical, runs live; the lifecycle smoke
and all 50 doc blocks green.

## Block-let command RHS — the uniformity fix## Block-let command RHS — the uniformity fix, and its friction closes (2026-07-23)

The parked half of param-ful RHS opened on three encounters and
landed as the plan drew it: one grammar, one more position. The
mechanism is a SPINE FLAG — a ThreadLocal true only along topLet's
RHS and its let-in chain (exactly the text the assembler's block
join produces) — so the original in-swallow park's boundary holds
by construction: parens interiors, lambda bodies, and the
single-line `let ... in` spelling stay expression-only, each
pinned. The failing-first ordering ran verbatim: guard-dropped,
`zzshadow y` SPAWNED a real PATH binary from inside a body; the
ambient-resolver extension (the param-ful mkR one scope deeper)
turned it into the binding it names.

The depth battery found the one seam the pre-scoping missed: the
REIFIER end. `| succeeds in body` demoted succeeds to a bareword
stage because reifierEnd knew pipe/paren/eof but not the ` in `
boundary — the in-stop now reaches it, gated on the same spine
flag. And the e2e is the promised un-hoisting: the port's
treeExists/isAncestor wrappers — which existed only because the
grammar refused a local binding — inlined back into graftStep and
graftBranch, the lifecycle smoke green throughout. The friction
arc closes: parked with receipts → taught workaround → three
encounters (two hoists + the user hitting the wall knowing the
workaround) → opened → the workarounds deleted.

The rider: `function` joins the reserved words with a hint that
teaches the spelling AND holds the park's name ("'function' is
reserved; write 'fun x -> match x with'"); `^function` reaches a
PATH binary (pinned live with a fake executable), and FCS agrees
the binder rejection is Same.

## Investigation: the | stage whitelist that wasn't (2026-07-23)## Investigation: the | stage whitelist that wasn't (2026-07-23)

The blessed investigation returned verdict C-sharpened: the feared
mechanism does not exist, and neither does the symptom. The findings
by the plan's questions:

Q1/Q2 — `commandCallable` is not a stage gate. It is a one-member
set (`["cd"]`), born in Ergonomics session 2 (c0a3485) so the one
builtin that behaves like a shell command can HEAD a command-shaped
statement (`cd /tmp` with bareword args, cd not being on PATH). It
gates HEAD position only, was already once caught outranking the
known-name check (the `let workdir = cd target` regression → the
builtinHeads flag), and has never accreted a second member.

Q3 — the receipt does not reproduce. `git status | Seq.map f`
(named fn), `| Seq.map (fun l -> l)`, `| Seq.where (fun ...)` all
pass on HEAD — script, -e, piped REPL, sigil interiors, statement
level. Stages have been FULL EXPRESSIONS since session-3-command-
mode (a3ba058): `segment = commandSegment <|> segExpr`, with
resolution deciding — known names fail command mode and fall to the
expression parser, externals stay external, bindings beat PATH in
stage position by construction.

Q4/Q5 — moot: the prototype the plan sketched (resolution-based
stage rule) IS the shipped grammar; there is nothing to widen and
no pins to move.

THE REAL RECEIPT, recovered by probing adjacent spellings: `|`
after an EXPRESSION LHS — `$(git status) | Seq.map f` or
`[1; 2] | Seq.map f` — rejects with a RAW parse error ("Expecting:
end of input, ... infix operator ...") that never names `|>`. The
two-pipe distinction is fine; the CLIFF MESSAGE is the gap. The
follow-up is pre-scoped rider-size per the verdict convention:
a targeted parse-error hint at `|`-after-expression — "`|` chains
commands; pipe expressions with `|>`" — hints-name-the-spelling.
No code moved this session (investigation and change do not share
a session). The rider ran on the user's go, next session: barePipeHint
at the three expression-end sites (statement, let-RHS, destructuring
RHS), with the consumed-pipe fatal dominating FParsec's
furthest-error merge so the hint IS the message, caret on the `|`.
Zero pin movement; the hint pinned at all three sites — plus a
fourth found by the pin itself: topLet's attempt EATS fatals (by
design, for the let-in fallthrough), so the let-RHS spelling needed
the hint at the EXPRESSION let-in's value position, where the
statement-level grammar is not attempt-wrapped and the fatal
dominates.

## Seq patterns — the design closed as drawn (2026-07-23)

Part 2 opened on user call hours after going on file, and the
design survived contact almost verbatim. The one implementation
insight: the "statically bounded force + memoize-once" law is
Seq.cache — probing pulls at most maxArity+1 elements through one
cache, arms share the buffer, and `rest = cached |> Seq.skip k` is
literally "buffer suffix + untouched tail," so effects run once
TOTAL even when rest is consumed later. The live pin is the crown:
four arms probed AND rest summed over a counted command seq = one
spawn. The explicit bound computation the design sketched became
EMERGENT — probes cannot pull past maxArity+1 by construction, so
nothing computes the number; it is simply true.

The probes ran first and all four verdicts held: F# accepts the
spellings on list literals (Same pins), rejects them on seq
scrutinees (the seq-patterns row, born refereed, filed in the
param-ful-RHS class). The cons level is the pattern grammar's
first infix (right-assoc, tighter than comma — sepBy1 and a fold).
Two standing bounds carried over without new machinery: param
scrutinees ride ctor-pattern-scrutinee (pipe through a Seq op to
resolve), and interp holes in generic match arms ride
splice-default-last. The SKILL must-fail block flipped to a
working example — the taught workarounds retire.

## Seq.force — a rename with its reasons written down (2026-07-23)

`Seq.toList` was a permanent small lie (a type weir will never
have, per the no-split decision) and the materializer's history had
already burned the honest F# name: weir's original bare `collect`
squatted on F#'s Seq.collect, which is FLATMAP — an F# hand reading
`|> collect` expects concat-map. The session renames to
`Seq.force`/bare `force` (Haskell prior, zero collision, no false
parity claim) and RESERVES Seq.collect unbuilt for the flatMap that
may someday earn receipts — the reservation being the actual point:
later is breaking, now is a grep. The Option bundle rode
(`defaultTo` → `defaultValue` + `defaultWith`), closing the parity
audit's known backlog in the same migration mechanics.

All four retired spellings teach their replacement through one
table consulted at both lookup sites (bare-unbound and
module-member-miss — the measures-transition precedent, now
mechanism). The migration swept nine script/doc files and both
suites; the oracle kept its snippets F#-legal by swapping the
materializer for Seq.length where it wasn't the pin's point.
Part 2 (seq patterns over seqs: statically bounded force,
memoize-once, F#'s spelling on the type weir has) is design-on-file
with its trigger stated — SKILL carries the must-fail block and the
taught spellings until a stranded cons-pattern or a user call opens
it. The when-do-I-force answer is finally two lines: reuse, or
timing.

## git-subrepo: the exercise finishes — and harvests receipts (2026-07-23)

The full port lands: clone, init, pull, push, branch, commit, clean,
status, fetch — the complete lifecycle e2e against a real bare
upstream, with the crown assertion green: a host contribution pushed
upstream arrives WITH the host author preserved (the graft's
cherry-pick-style author/committer split), and a second pull merges
upstream movement over local contributions.

Two modernizations replaced the bash's heaviest machinery. The graft
builds each tree MINUS .gitrepo via GIT_INDEX_FILE temp-index
plumbing through runEnv — the deprecated filter-branch pass and its
refs/original cleanup do not exist in the port. And the pull join
runs `git merge-tree --write-tree` (real merge machinery, no
worktree) — which also EXPOSED that the inherited pull path had been
wrong all along: it merged upstream's unrelated history straight
into the mainline, masked until now because every fixture was
up-to-date. The lifecycle fixture found it in its first minute.

The port found a genuine assembler bug in the worst class
(legal-parse-wrong-meaning): with a block-let pending, a match
inside a multi-line lambda outlived the lambda's own `)` on the
compound stack, and the next outer-pipeline stage got offside-
WRAPPED INTO the lambda — piping the inner match's Option where a
seq belonged. Fix: compounds record their paren depth at open;
the user's own closers prune them ([D:compound-paren-prune]) — the
wrap can no longer cross a paren boundary. Pinned on the exact
assembled text.

The receipt harvest, filed against the standing parks: block-let
command RHS inside bodies bit TWICE (hoisted to top-level wrappers
— the idiomatic answer, but the friction is real); multi-statement
lambdas across lines are the parens-spanning park's face (spelled
single-line with greedy-`;`, which HELPS here); ctor-pattern-
scrutinee moved a match to the dispatch site where the Option is
nominally typed; `config` is NOT ported — its `<key> [<value>]`
shape is the positionals park's operand case, on record; and
Seq.append landed mid-port because variable-length argv had no
concat spelling at all. One exercise, five park receipts, two real
bugs — the flagship keeps earning its name.

## REPL coloring — the highlighter that cannot drift (2026-07-23)

Input lines color as you type, and the architecture note the plan
wanted for the ledger holds cleanly: the colorizer consumes
inStringMask (the one scanner), stripComment, and the parser's own
keyword set — zero re-derived string states, so this highlighter is
correct by construction where micro and TextMate hold only by
hand-audit. The eventual semantic-tokens conversation gets its
evidence: one brain, many surfaces.

Two planned mechanisms dissolved on contact with the code. The
strip-escapes width helper: unnecessary, because the design keeps
escapes out of the BUFFER entirely — coloring happens at the paint
(redraw's single choke point), buf/pos stay plain text, and cursor
math never meets an ANSI byte. And tier 2 (colored error carets)
was already shipped by the colored-diagnostics session — verified,
not rebuilt. The cache-cost verify clause closed trivially:
Extern.exists is a set lookup, and the redraw-cost ceiling pin
(1000 pathological 200-char lines under 2s) covers the whole paint
including head resolution.

The fish trick is the feature: the head word colors by the live
resolver verdict — bold for bindings/builtins, blue for PATH, red
for would-fail — and `^`-forced heads resolve PATH-only, consistent
with their semantics (pinned: `^print` paints red while bare
`print` is bold). The load-bearing pin is the transparency
property: strip ∘ colorize = id over fixtures including emoji,
unclosed raw strings, and an unclosed bracket — coloring can never
alter what runs. The harness guard landed FIRST per the plan:
piped sessions are byte-clean structurally (the tty editor never
runs), and NO_COLOR under a pty suppresses every span.

First live receipt within the hour: `let`/`match`/`with`/`from`
"all get red" — they were MAGENTA (35), which renders near-red in
the user's theme and read as four errors on a correct line. The
probe-the-codes-first discipline paid (the logic was right; the hue
collided), and the palette gained its one hard rule: the red family
belongs to exactly one signal, the head that would fail. Keywords
went blue, PATH heads bold blue.

## Bounded REPL echo — two premises corrected, one pin earns its keep (2026-07-23)

The echo now glances: 10 elements then "…", strings clipped at 120,
depth-bounded nesting, and a tail that names the way out —
"(10 of 12 shown — pipe to print for all)", with the count real for
materialized lists and "?" where counting would force. `-e` shares
the bound (decided in-session); `show` is byte-identical to its
shipped contract; bare command statements still stream everything —
echo = glance, print = read, the two paths' roles now crisp.

Both of the plan's premises were wrong in weir's favor, verified
before code: the "infinite seq HANGS today" expectation was refuted
(formatValue has bounded seq forcing at 21 since the show session),
and "show is total" contradicted show's own DOCUMENTED contract
("deliberately lossy," SEMANTICS and SKILL both). The decided
behaviors were executable exactly as written — only the reasoning
needed amending, and the DECISIONS row records both corrections.

The pull-count pin earned its keep in its first minute: the naive
echoValue rendered the seq AND re-truncated to decide the hint — 22
pulls where the property allows 11, a silent double-force that
re-runs effects on lazy streams. One materialize-once restructure
later the pin passed. Third instance of the pattern (fold's
strictness sentence, choose's infinite-source pin, now this) —
threshold reached, promoted to a PROCESS rule: laziness claims get
pull-count pins, never inspection.

Live correction within the hour: the hint said "pipe to print for
all" unconditionally, and `ls |> print` REJECTS — print takes
seq<string>, the echo's own origin receipt is seq<FileRow>. The
hint-names-the-spelling convention means the named spelling must
TYPE: the counts phrase now composes in Eval (where the value is)
and the spelling at the echo sites (where the type is) —
seq<string> hints `|> print`, everything else hints
`|> Seq.map show |> print`, both verified against the checker
before wording the hint.

Second live catch, same hour: Tab at `{ Line = x. })` with the
cursor mid-line offered nothing — the REPL passed the FULL buffer
to Complete.suggest, whose word runs from wordStart to end-of-text,
so the typed closer joined the prefix and killed every match. The
LSP caller already truncated at the cursor (`upto`); the contract
("text ends at the cursor") was implicit and one of two callers
violated it. Fixed at the caller, contract now stated on suggest,
pinned twice: the unit pin compares truncated-vs-full on the exact
receipt, and the PTY harness types the closer first, cursors back,
completes mid-line, and asserts the evaluated echo — verified to
FAIL against the pre-fix binary before counting it as a pin.

## The founding divergence retires — blanks never end statements (2026-07-23)

The oldest non-structural row in the divergence ledger is gone.
Blank-line-ends-statement was named at the first multi-line session
as THE safety boundary; comment transparency opened a second seam;
the bracket half narrowed it this morning; and this afternoon the
col-0 law revealed the rest redundant — every error the blank
boundary produced also fires at the col-0/EOF close, same or better
locality. The arc is the standing answer to "does the process ever
REMOVE strictness?": yes — when a receipt arrives, a stronger law
is shown to carry the load, and the oracle referees the direction.

The implementation was a DELETION, as the plan predicted: the blank
branch's three arms (noBodyBlank, the district-blank close,
close-on-blank) collapsed into "pending → skip", and the morning's
bracket-transparency arm was SUBSUMED — one rule again, no special
case. fmt's fix was smaller still: stop resetting on blank; the
col-0 branches already reset at real boundaries, which IS the
deferred decision. The bracket statement-head guard stays verbatim
(brackets are the one place col-0 does not close).

Probes: five gap shapes F#-accept (body, arms, head-to-first-body,
if-body, match-head-to-arm — the |-inertness customer exercised
explicitly, not assumed); the STRAY shape F#-REJECTS, which is
better than the plan hoped — the deliberate-consequence pin shows
weir and F# refusing the same mistake, and the gapped and
blank-free spellings error identically (the consistency claim,
e2e-compared).

Six pins flipped by name, including the twin added FOUR HOURS
earlier as the bracket plan's both-sides pin — it held the boundary
the user then moved, which is pin-as-regression-guard doing its
job, not churn. One stale doc line surfaced en route: SKILL still
said "a blank inside an open { is an error" from BEFORE the
morning's bracket landing — the docs-sweep rule (grep idioms, not
keywords) missed it once; swept now.

The board note lands as written: the remaining divergence rows are
structural identity or deliberate boundaries — the leanest the
F#-refugee's map has ever been.

## Blanks inside brackets — a reversal, honestly bookkept (2026-07-23)

Blank lines inside open brackets are transparent — the comment-line
class's second member. The reversal's honesty clause fired as
written: the plan's reframe assumed comment transparency POSTDATED
the records session's "blank = error" choice, and the archaeology
says otherwise — the transparency fix round landed EARLIER the same
day (2026-07-20) than the grammar-consolidation session that chose
the error rule. So the reversal entry records a re-weighing on new
receipts (Stroustrup grouping wants gaps; encodeSubref's ten pairs;
the user's ask), not a correction of an ill-informed decision. The
DECISIONS row says exactly that.

The statement-head guard pays the error-locality bill: a col-0
`let `/`type ` while a bracket is open errors immediately, naming
the opener's kind and line from data the bracket stack already
carries. The update×guard product cell (Stroustrup `{ r with`,
gapped fields, forgotten closer, col-0 let) fires the guard naming
the with-header's own line. The residue is on record with a WATCH:
the bound is keyword-anchored, so command-heavy scripts can swallow
many lines before a `let` appears — a long-swallow incident reopens
the guard's keyword set.

Probes first, all five verdicts harvested BEFORE implementation by
running the pins against the unmodified binary and reading the
disagreement messages: F# accepts blanks in type decls, literals,
and lists (Same after the flip), rejects them in UPDATE position
(the Stroustrup session's offside row absorbed it, as predicted),
and Same-rejects the guard shape. Two error pins flipped to
acceptance BY NAME (unit: blank-inside list/type-decl; e2e: the
at-blank brace error became three pins — gapped-closed runs,
unclosed still errors at statement end, the guard bounds the
runaway). The blank-line-ends-statement divergence NARROWED —
weir's F#-side gap shrank again.

## Stroustrup house style — and the oracle earns its keep again (2026-07-23)

The user called Stroustrup as the house bracket style (the fantomas
repo poll, ~70%): dangling opener, entries one level in, closer
alone at the opener's indent. The assembler needed ONE line — a
closer line never starts an entry (the list `]` was taking a
sibling separator) — everything else already followed from the
bracket stack. fmt learned to annotate each pushed bracket with its
style at push time (dangling-at-EOL or a `{ .. with` header =
Stroustrup; inline = column-aligned) and canonicalizes indentation
within either; fmt does no line surgery, so the aligned style stays
accepted rather than converted.

The probe set caught a real asymmetry: F# accepts the col-0 closer
for Stroustrup type decls, literals, and lists — but offside-REJECTS
it in copy-and-update position. The Same claim flipped to the
standing record-fields-ignore-indent row (weir is indentation-blind
inside brackets), and the fantomas controversy now has a refereed
pin instead of a vibe. Flagship, GUIDE, and SKILL swept to the new
style; the live smoke stayed green throughout.

## fmt: match arms align under the m (2026-07-23)

A user request landed the same day as the bracket stack, and the
same shape paid twice: fmt gained a match-head stack (original
indent, formatted column, first-arm anchor). The first pipe line
after a `match` head IS an arm — the assembler guarantees it — and
its indent anchors the arm set, so deeper-indented arms pull back
to the m while union cases (no match open), chain stages (deeper
than the anchor), and arm-body pipes keep their depth. Nested
matches align to their own m and the outer arms resume at the
outer column when the inner closes at the offside boundary. The
respace safety guard held throughout: every realignment is
parse-neutral by construction, and the whole example corpus stayed
--check clean.

## Multiline brackets — one stack, three shapes (2026-07-23)

Type declarations and list literals continue across lines, and the
mechanism SHRANK the special cases rather than adding one: the
assembler's brace depth (an int + a line number) became a bracket
STACK (kind, opening line), and the innermost bracket picks the
separator rule — `=`-fields for record literals, `:`-fields and
`[<` lines for type declarations, every-line-an-element for lists
unless the previous line dangles an opener, separator, or operator.
Nesting falls out for free: a multiline record inside a list
switches rules at the inner brace and switches back at its close,
no code asked. Cross-bracket closers error naming BOTH sides
("'}' closes the '[' opened at line 2") — the stack knows what the
depth int never could.

Preceding-line attributes (the F# house style) rode the widening
with zero parser change: an attribute line starts its field
(sibling separator before, none after — the `>]` dangle rule), and
the assembled text is exactly what the same-line parser already
accepts. The attributes session's same-line-only bound retires; the
GUIDE bite that surfaced the gap is healed in place (the attributed
Cli wraps).

Zero existing-pin movement across the rewrite — 736 unit + 110
oracle passed untouched before the new pins landed, the assembler
battery proving the stack is a strict generalization. All six new
oracle verdicts held first-try, including the wrapped-element
dangling-operator continuation (F# accepts) and the col-0 type
field riding the records-ignore-indent divergence unchanged.

fmt's "bracket+2" decided wording resolved in-session: the house
logic is align-under-the-first-entry, which is brace+2 for `{ x`
and bracket+1 for `[x` — the plan's own form block shows +1; the
bullet's arithmetic was records-specific. Probes clarified one
scope line early: piped stdin is the REPL (single-line by the
standing continuation-prompt park), so multiline forms are a
file-mode feature — the probe harness moved to files and the park
is unchanged.
## Seq.choose + the highlighter that was never broken (2026-07-23)

`Seq.choose` lands as the match-or-skip member — lazy, qualified-only,
constraint-free, FCS-probed (all three pins Same, including both
languages rejecting a non-Option chooser). The flagship's statusRefs
was the receipt and is now the showcase: the Regex arm returns
`Some line`/`None` straight into choose, and the sentinel-empty
detour (map to "", filter empties) is gone from the one place it was
taught by example. `Seq.choose id` was NOT ridden: `id` does not
exist (probing `id 5` runs the PATH binary — command mode claims the
name at statement level), and the identity-lambda spelling carries
no receipt beyond cosmetics.

The rider's root cause was WRONG and the correction is the story:
the plan blamed a backslash skip in the repo's verbatim region, but
the repo file has carried the correct per-kind escape laws since the
VS Code session — no commit ever had the claimed skip. The live
symptom was real and the MECHANISM was as described; the carrier was
the INSTALLED copy (~/.config/micro/syntax/weir.yaml), stale from
the pre-raw-strings era: no verbatim region at all, so `@"\"` fell
into the plain-string region whose `\.` skip ate `\"` and the
cascade painted the rest of the file as string. Sixth member of the
stale-artifact/masked-failure class, first in editor config — the
binary got stamps; syntax files have no mechanism, and the guard's
presence-not-semantics limitation comment plus the flagship's
encodeSubref line as by-eye canary are the honest manual substitute
(a committed repro file was dropped on review — it cannot catch a
stale INSTALLED copy, which is this class's actual failure mode). Diagnose-before-fix paid again: the decided "fix" would
have been a no-op edit to an already-correct file.
## Typed argv — the front door closes (2026-07-23)

`Args.load` lands as Env.load's sibling and the sixth typed-boundary
instance, and the four registered attribute names flip from inert to
bound — the activation session the attributes stop-and-report
deferred to, its acceptance criteria inherited pre-written. The
flags record and the subcommand union both ship; git-subrepo's
hand-rolled dispatch (`args |> Seq.tryHead` + a string match with a
`fail` floor) became a typed union front door whose exhaustiveness
the checker owns — the PullResult win at the script's own entrance,
running verbatim against the live repo-pair smoke. jira-branch's
Cli carries the `[<Short "c">]` worked example.

The derivation story held together better than expected: ONE
shortTables function (explicit claims first, then unambiguous first
letters, `h` excluded) is consulted by the checker, the loader, and
the usage renderer, so check-time truth, runtime resolution, and
--help output cannot disagree by construction. Contested letters
derive for nobody and the invocation error lists candidates;
explicit `[<Short "e">]` visibly retires `--env`'s derived short in
--help — derivation yields to declaration, pinned.

Strictness decisions the plan left open, resolved strict: repeated
flags reject ('--env' is given twice) rather than last-winning —
collect-then-raise makes the strict default cheap to relax and
expensive to regret. A four-problem invocation (typo'd flag, bad
int, stray token, missing required) reports all four in one
boundary error, did-you-mean included.

The kebab pins (`dryRun`/`DryRun` → `--dry-run` and its check-time
collision, `noFF` → `--no-ff`, `useHTTPSNow` → `--use-https-now`)
are exactly the plan's examples; hump-style variance collapsing to
one flag makes the casing law self-enforcing at the CLI face.
`weir -e` rejects type declarations, so the check-side e2e pins run
through `check /dev/stdin` — a harness spelling worth remembering.
## Attributes — syntax parity, invisible divergence (2026-07-23)

`[<Short "c"; Doc "count">]` attaches to record fields with F#'s
syntax and none of F#'s machinery. The re-costing that opened the
plan holds in the code: what makes .NET attributes expensive is
reflection — runtime metadata, an access API — and weir wants none
of it. Attributes here are check-time data on the RecordDef,
validated at attachment, fully erased after: the erasure pin shows
an attributed record constructing, updating, comparing, and showing
identically to a bare one. The registry is closed (Short, NoShort,
Doc, Positional) and unknown names are check errors with a
did-you-mean — no silent decoration, the reject-don't-guess posture
at the meta level.

The plan's premise partially failed on contact: it wrote consumers
into `Args.load` — typed argv with derived shorts and `--help` —
and Args.load DOES NOT EXIST (Args has `flag`/`value` only; the
consumers targeted an advisor-thread design that never landed).
Stop-and-report resolved by the plan's own
validate-at-attachment/bind-at-consumption rule: registered names
with no consumer are legal-and-inert, so the infrastructure lands
honestly and consumer activation waits for the typed-argv plan.
Positional's "registered now, consumed later" treatment turned out
to describe all four names.

Correction owed and paid: the attributes question was first waved
off with a fabricated "refused machinery" citation — an advisor
claim with no pointer into the archaeology. Decisions are citable
only by pointer; a claim without one is folklore and gets the
folklore rule (probe, don't recall). The parallel-record design for
shorts overrides retires unbuilt, superseded by `[<Short>]` field
locality — its record-update-based Default idiom stays valuable
independent of this feature.

Second advisor-error entry, same plan: the Args.load dependency was
the advisor's to sequence — PLAN-typed-argv was blessed and amended
in-thread but never reported executed, and the attributes plan
wrote work items into its machinery anyway, treating a blessed plan
as landed code. The countermeasure is the dependency-gate rule, now
in PROCESS: consuming plans name the dependency in their header and
gate on its session report, not its bless. The resolution's shape
gets its credit precisely: extending Positional's not-yet treatment
to all four names is the plan self-repairing through its own
decided law — validate-at-attachment/bind-at-consumption was
decided for a different reason and turned out to be the escape
hatch, and the carried-over done-when clauses mean the typed-argv
session inherits its acceptance criteria pre-written.

Found en route, now with a docs receipt: record TYPE declarations
do not continue across lines (literals continue — their fields
carry `=`; type fields carry `:` and the continuation join never
learned them). The GUIDE's two-field attributed Cli wanted to wrap
and could not. Candidate next fix, logged not ridden.

## Exit-code reifiers — the bash priors that were right (2026-07-23)

`| succeeds` and `| orFail "msg"` join complete's family through one
generalized fold arm (the three reifiers now share literally one rule
and one message shape). The flagship's assert functions are
one-liners; revExists reads `git rev-list $r -1 | succeeds`.

The verify-clauses earned their keep twice. "Nothing follows a
reifier" was folklore — complete has always allowed downstream stages
with types as the gate; the plan's cell inherited the REAL rule
(bool into Seq.head is a type error, pinned). And the flagged !( )
cell forced a real decision: orFail interiors are unit, and BOTH the
!( ) sigil and districts wrap interiors in print — so unit became
printable-as-nothing, one rule at printArgTy instead of a shadow
drain builtin twinning print's typing. `print ()` is now silent
(pinned as the deliberate consequence); seq<unit> still rejects.

Two runner seams closed en route: printResult skips unit (asserts are
silent on success), and bool-valued command statements join the
discard family — `git log | succeeds` bare is a check error with a
bind-or-condition hint, while record-valued complete statements keep
their standing echo. The exit-zero sentence ships in SKILL with the
grep counter-example doc-tested: succeeds is ExitCode == 0 exactly,
and no-match-is-data tools spell | complete.

## Param-ful command RHS — the first feature enabled by a bug fix (2026-07-23)

`let revParse r = git rev-parse $r | Seq.head` runs, and the flagship
git-subrepo example now reads bare where it wanted to. The enabling
arc the plan named: the defaulting-order WRONG-REJECTION was fixed
two days ago by making splice defaulting a boundary step — and that
architecture, built for correctness, turned out to be the wall this
feature needed. Fixing wrong-rejections properly compounds.

The ceremony's order mattered: the guard-dropped prototype printed
SPAWNED for `let f x = x` with an executable x on PATH — the
legal-parse-wrong-meaning hazard demonstrated live, the pin written
red, THEN the resolver learned param names (bindings-beat-PATH
reaching a scope commands could not previously occupy; ^x pinned as
still reaching the binary). The soundness note took its third
edition without the stop clause firing; zero checker arms; the
retired rule's own pin flipped with archaeology in its name.

Wild finding from the flagship rewrite: mid-word splices
(`--file=$file`) pass literally — the whole-argv law working as
designed but worth teaching; the live smoke caught it, SKILL now
says it. Advisor pins (sigil equivalence, splice-typo did-you-mean)
green first try. Friction retired for the param-ful half; block
lets inside bodies stay the parked half's receipt collector.

## Seq.fold + fun-sugar — the port unblocked (2026-07-22)

The strongest receipt on file cashes: both git-subrepo blocker folds
run verbatim on the AOT binary (the encode-subdir escape fold with a
tuple-pattern folder; the commit-walk four-accumulator record fold —
which is also the fold x record-update x rows product pin), and
Env.ofPairs feeds runEnv for the three-var author shape. "Post-fold,
a legitimate weir program" now holds in the report's own terms.

The probes ruled twice. F# REJECTS duplicate lambda params — and the
probe caught weir's let-param sugar silently ACCEPTING them (latent
divergence, fixed in both positions by the one-rule-two-positions
law). Three probe shapes were amended for wrong-reason rejects: weir
has no `string` builtin (interp converts), and the +-on-unknowns
limit collided with two shapes — the limits were already documented;
the pins now isolate their claims.

The real find: the canonical `xs |> Seq.fold (fun s x -> s + x) 0`
REJECTED at first — check-mode's hasVars fallback inferred nested
lambda bodies, dropping the already-resolved inner domain that the
pipe had anchored. One-clause push-through (nested lambda vs TFun cod
checks directly), zero pin movement. And the sugar needed NO checker
adapter at all — pure parse desugar through curryParams, less than
the budgeted flag-7 adapter.

## The small-items sweep — two sessions, four retirements (2026-07-22)

Session 1 (grammar+checker): ELIF landed as pure spelling after the
precondition probe confirmed chained else-if already worked in both
line models — no hidden second gap, so one keyword + one parser
desugar + the assembler's else-family extension. The DEFAULTING-ORDER
edge — the only wrong-rejection on the books — fixed by making
splice/hole defaulting a finalization step at the statement boundary
(ctx queue, resolved where stranded constraints error): the repro and
its variants accept, still-unresolved holes still default to string,
non-scalars still reject at the hole, and the soundness note
re-verified SIMPLER (boundary defaulting runs pre-generalization and
touches strictly fewer vars). Zero pin edits across both.

Session 2 (infrastructure): MASKING MECHANIZED — publish stamps the
git hash, `weir --version` prints it, e2e gates HARD on stamp==HEAD
plus source mtimes (the old WARNING is now a refusal), both python
probes gate through tests/lib/harness.py, and process census moved to
waitpid-truth (the zombie lie pinned in the harness selftest, which
also proves a deliberately-stale stub fails the gate). FLAG 7
discharged deliberately early: five lambda arms over one core,
adapters carrying only their judgment deltas — the TRANSCRIPTION
surface shrank, zero behavior change on the full battery.

The board after: no-elif retired, the wrong-rejection closed, the
masking class closed (open since incident one), Flag 7 closed — with
the re-mine follow-ups done, the emptiest board since the audit.

## fmt v2 — respace under the parse-shape guard (2026-07-22)

The respace park opened on the user's update-example receipt (fmt
insisting "already formatted" over `{Lomo: int...}` / `=  {    lomo`).
V1's byte-identity safety cannot hold when the formatter CHANGES
bytes, so the invariant evolved: each statement parses before and
after under Script.assumeResolver and must SEXPR-match, or that
statement reverts. The sexpr renderer moved from the test suite into
Ast (completing five node kinds and five pattern kinds it lacked) and
is now shared by the parse pins and the guard — one shape language.

The guard caught its own feature during the build: the first shape
resolver used IsExternal = always-true, which claimed `{Lomo` as a
command HEAD and made every let-RHS a command — every respaced record
line "changed shape" and reverted. Switching to assumeResolver
(command-SHAPED heads only) fixed it; the debug hook stays, env-gated
(WEIR_FMT_DEBUG). The guard also showed judgment on day one: it
ALLOWS `"x" ; echo` -> `"x"; echo` (quoted tokenization provably
keeps `;` a separate argv word) while reverting `echo {a}` (padding
would split the argv word). Pre-comment alignment gaps survive (an
existing pin caught the collapse and the gap is now preserved).
682 unit / e2e green incl. all prior fmt roundtrips (bicep, env
district, raw strings).

## Record update lands — the re-mine's headline cashed (2026-07-22)

`{ r with F = v }` in all four planned forms: flat, multi-field,
nested I.X, general-expression sources (unparenthesized application
included — FCS-probed before code, per the folklore rule; the bare
match source rejects with parens required, as guessed and verified).
The corpus snippets that found the absence run VERBATIM as e2e.

The plan's stop-and-report clause fired exactly as designed: the
nested-sugar parser desugar wanted source duplication (double eval of
effectful sources), so paths live in the AST and eval binds the
source once — reported, resolved in-session, eval-once by
construction. A probe-naming accident yielded a bonus FCS fact: F#'s
name resolution captures a TYPE named like the path head and rejects;
weir's field-only paths accept — update-path-plain, the designed
weir-accepts row, pinned.

The row-typed half is the weir-shaped win: an updater over an open
row generalizes, and the result type IS the source's row variable
(identity, tripwired on domain == codomain of the formatted scheme).
One assembler ride-along: the brace-continuation sibling rule gained
a with-header case. no-record-update retires with corpus archaeology;
680 unit / 93 oracle / e2e green; check median 10ms.

## VS Code extension — the second editor, zero server changes (2026-07-22)

The fsautocomplete question's answer, cashed: a second editor client
landed with NO server diffs — the LSP as shipped (Session 3's stdio
JSON-RPC) needed only glue (~40 lines of extension.ts on
vscode-languageclient) and a TextMate port of the micro grammar.
No protocol findings surfaced at build time; the interactive smoke
(SMOKE.md) runs on a machine with VS Code — this container has none,
so packaging + the protocol probes are the CI-side proof and the
checklist is the committed record of the rest.

Two plan-premise corrections, reported: the layout is editors/vscode/
(plural — micro lives in editors/; the plan's editor/ was a typo),
and the apostrophe "tombstone" is actually a LIVE guarded region in
the micro file (command-mode single-quote strings with the
space-before guard) — spec-equivalence means it PORTS, and did; the
tombstone was the earlier unguarded version's deletion.

The drift guard is mechanized: micro gained `# rule:` annotations
(20), the tmLanguage repository keys are the same 20 ids, and e2e
diffs the sets. Oniguruma extras used only to simplify existing
micro guards (lookbehind for comment/district/single-quote openers;
lookahead for the verbatim `"(?!")` end). Indent rules: NONE, decided
against real editing feel — VS Code's keep-previous-indent default
matches weir continuation style, and auto-indent guessing the
offside/district grammar wrong is worse than neutral. autoClosingPairs
carries `@"` (multi-char opens work); `"""` deliberately not paired —
it fights the plain-quote pair mid-type.

## The corpus re-mine — the four-wave debt paid (2026-07-22)

Owed since tuples (WEIR_CORPUS_DIR absent then; noted, not dropped),
paid with interest. Environment verified FIRST per plan: network up,
sparse clone at the pinned 5928e91. Finding zero: the original
mine's extraction/filter never survived its session — only the
report did. tools/corpus-mine.py is now the committed artifact,
calibrated to the published denominator (4253 extracted vs 4256;
base filter 76 vs 78) with the four wave-rejects explicit, so the
filter diff IS the language-growth record.

The two prize numbers: ZERO GOLD holds over the widened set (weir
accepts nothing F# rejects, now tested against tuple/pattern/
composition/raw shapes), and disagreements FELL 24 -> 18 while the
comparable set grew 34% (76 -> 102; agree-accepts 4 -> 9) — the
waves converted disagreement to agreement at scale. All 18 bucketed;
human residue after naming: zero.

Seven new rows (the absence class delivered again): no-record-update
(the headline — 2 hits incl. F# 8 nested I.X), column-zero-statements
(5 hits — the assembly law vs F#'s uniform-indent tolerance; the ///
doc comments in those snippets were innocent), ctor-pattern-scrutinee
(the predicted unnamed find: params are not typed FROM patterns),
no-auto-members, no-arrays, no-access-modifiers, and
record-field-comma-trap (weir REJECTS the famous tuple-in-field trap
— safe direction, strictness family). Corpus tags on four existing
rows; no-elif upgraded from "no demand" to top reopen candidate
(2 corpus hits + the loc.weir agent friction) alongside
no-record-update.

## Raw strings: @"..." and """...""" (2026-07-22)

PLAN-raw-strings, one session, probes FIRST — the folklore-vs-compiler
rule's first scheduled application worked exactly as designed: all
four F# facts held (verbatim doubling, triple bare-quote, the
quad-OPENER accepting with a leading-quote content), and the one
genuinely unknown edge was ASKED, not recalled — FCS rejects the
quad-closer (`"""a""""`), so weir's close-at-first-triple lexing is
the compiler's own verdict, pinned before a line of implementation.

The positional raw-regex rule retired the same week it shipped, with
credit to the shout-if clause: the flag on the regex session's
unstated decision drew the review that concluded rawness is a STRING
property. The Regex position is now raw-ONLY by rider (ordinary
strings rejected there on KIND, not content — the double-escape
footgun unrepresentable), and the strings-uniform law holds: no
string means different things in different positions.

Candidate tombstones, re-askable only against this entry: single
quotes (deleted from the highlighter once already for
apostrophe-swallowing; dotenv's quoting is one adapter away), `~`
(no prior, home-dir/=~ associations, the last free sigil is worth
more unspent), backticks (weir-only kind with no referee; JS
inverted the prior to "template string"; F# claims the glyph for
double-backtick identifiers — honest credit: the 1:1-raw want was
real and `"""` answers it in-house). The raw-string budget is two
kinds because F#'s is.

Scanner formalization paid a dividend on entry: braceStack turned
out to be a verbatim THIRD quote machine and was rewritten over
foldOutsideStrings in the same commit; the repair-path closers
learned V/T states (suffixes "\"" and "\"\"\""). Single-line-only
divergence rowed (raw-single-line); interpolated-raw parked with its
row born accurate from the probe (no-interpolated-raw). Check median
back at 10ms.

## The Regex pattern + Str match family (2026-07-22)

The regex park opens per plan. `| Regex "lit" binder ->` — one bespoke
checker arm, NOT active patterns (door stated closed). The foil's hole
closed as designed: the literal compiles at CHECK time (invalid regex
is a located check error; e2e proves zero effects), and binder arity
is verified against the ENGINE's capture count — GetGroupNumbers is
the authority, so non-capturing groups are excluded for free. One
Regex instance per literal in a check/eval-shared cache (tripwired on
reference identity). Interpreted mode only; the AOT publish and
battery confirm System.Text.RegularExpressions is trim-clean without
RegexOptions.Compiled (the plan's verify-and-report item).

One decision the plan implied but did not state: the pattern literal
is RAW — `"(\w+)"` is written with single backslashes, only `\"`
escapes — because the plan's own examples write it that way while the
expression side (`Str.isMatch "\\.md$"`) uses ordinary strings. The
asymmetry is deliberate and documented (SEMANTICS, SKILL, GUIDE).

First weir-only match form (ledger row regex-pattern, oracle-pinned
Diverges). Str.isMatch/Str.rmatch carry computed patterns with
boundary-class runtime errors. Ceremony paid in full: 15 unit pins
across the POSITIONS pattern sweep (nested tuple/constructor, guards,
binder rejection, exhaustiveness, dup binders, optional groups),
TRANSCRIPTION addendum, the cache tripwire, diagnostic codes
regex/regex-arity, GUIDE's Matching-text section teaching the
isMatch pipe idiom (the =~ park's precondition, shipped with v1).
Check median 11ms — within the guard.

## Parse errors show the unassembled source (2026-07-21)

"Can't we just show the unassembled? that is not what the user
expects in 100% cases" — correct, and the note was a band-aid.
cleanParseDump strips every FParsec snippet+caret block (they embed
the ASSEMBLED line), translates backtrack positions to physical
line/col, and keeps only diagnostic text; consumers render the
ORIGINAL source line with their own caret (runner from the raw file,
REPL under the prompt, -e echoing the expression; LSP needs neither
— editors show source). The first embedded position is dropped as
redundant with the header. Validation came instantly and strangely:
the new renderer showed `let x = /` for the bicep example — junk
that turned out to REALLY be in the working tree (editor-testing
stray lines, removed with notice), the renderer telling the truth on
its first outing.

## Runner missing-command diagnosis; a masking confession (2026-07-21)

User critique of the runner's missing-tool error, both barrels
correct: FParsec's PRIMARY error was irrelevant (the real cause —
"not an external command" — sat in the backtrack note), and the dump
showed the ASSEMBLED logical line (` ; `/` in ` insertions the user
never wrote). Fix, in the one pipeline so every consumer gets it: on
parse failure, RETRY under the assume-resolver — if that parses, the
failure IS missing command heads; name them precisely ("unknown
command 'bicep' — not found on PATH...", located at the head, all
missing heads listed). Genuine syntax errors keep the dump but now
carry a note naming the assembled-line rendering. CONFESSION for the
masking ledger (the class's fifth member): the diagnosis code sat
UNBUILT through several debugging rounds — an FS3373 error was
masked by a mis-piped error count, and every probe ran the stale
binary; two full fsi bisection rounds "proved" impossible facts
before a forced rebuild surfaced the truth. The verify rule (exit
code first, count second) exists for exactly this and was skipped.

## The deeper sweep — idioms rot, keywords don't (2026-07-21)

The user caught the docs sweep's blind spot twice in one message: the
bicep dispatch still used one `| c when c == "quality"` guard arm
(the sweep grepped for stale KEYWORDS, not obsolete IDIOMS), and
"check deeper" surfaced the real casualty — tools/test-counts.weir
had been BROKEN since the pairwise re-type (`p.Fst` on what is now a
tuple) because no repo script was CI-checked. Fixes: the guard arm
converted to a literal pattern; test-counts repaired with the very
feature that broke it (`fun (newer, older) ->` tuple param); SKILL's
record example de-Pair'd (it taught the tuple-shaped-record
anti-pattern); jira's record literal to bare-field canon; and the
INSTITUTIONAL fix — e2e now runs `weir check` over every script in
examples/ and tools/, so scripts cannot rot silently again
(cmd-not-found warnings pass, errors fail). Lesson for the ledger:
a doc sweep must grep for the OLD IDIOM each new feature obsoletes,
not just the old names.

## Open rows meet nominal records; cursor-local repair (2026-07-21)

Fourth round of the completion thread, and the deepest: the user
read the hover signature `{ BicepPath; Env; Stack; .. } -> unit` and
asked whether the missing Name related to the `..` — exactly right.
The open row only carries fields the OTHER lines demand; editing the
one Name-demanding line removes Name from its own completion. Fix 1:
ROW-RECORD COMPATIBILITY — an open row offers the full field set of
every declared record it fits inside (field-subset with type
agreement; TVar fields match anything). Fix 2: the repair's closers
were appended at statement END, so mid-statement edits with an
unterminated interp swallowed the REST of the statement into the
string; a second candidate closes the dangling delimiters AT THE
CURSOR (suffix preserved), tried first. The completion ladder's
repair rung now handles first, middle, and last lines of a
statement, with nominal enrichment on open rows.

## fmt field-drift + assembly recovery (2026-07-21, user bug report)

Two bugs behind one report ("fmt says already formatted but Name is
missing from completion; look at the record's indentation"):
(1) fmt's general depth model was CANONICALIZING record fields to
depth*4 (8 cols) instead of the house brace+2 alignment — and then
truthfully reporting already-formatted, since the drifted layout was
its own fixed point and assembly is indentation-blind inside braces
(the safety check could not object). Fixed: fmt tracks open-brace
columns (Script.braceStack, scanner-family) and aligns fields at
top+2; repo files reformatted back to the hand style.
(2) The completion regression's real cause was one layer below the
last fix: an ASSEMBLY-breaking mid-edit state made analyzeLines
return nothing — no statements, no types, builtins-only env — so
completion lost Target entirely. Fixed with assembly RECOVERY:
tooling drops the offending line (the error names it), retries up to
10 times, and keeps each drop as an assembly diagnostic; the runner
keeps hard failure. weir check now reports errors past an
assembly-broken line too (pinned). The recovery ladder is now
uniform: assembly-level drop -> statement-level continue ->
repair-typing -> fallback.

## Error-recovery completion — the park opens on a user push (2026-07-21)

"In let quality t we know what t is" — correct, and it opened the
parked step in two rounds. Round 1 (pipelines): unbound names in the
pipe-source prefix bind as HOLES (fresh vars) before inference — a
known function's result type falls out of unification regardless of
its argument (`targetEnv t |> Seq.where (fun e -> e.` → exactly
EnvVar's fields). Round 2 (broken statements): REPAIR the dangling
statement — blank the whole `head.prefix` to a neutral `""` (leaving
a bare row-typed head broke scalar-rule positions: printerr), append
closers, typecheck with holes, and read the head's type from ANY
OTHER occurrence (a param's uses share one type; the cursor's own
occurrence was just blanked). `closers` grew into a proper mode-stack
mini-scan (brackets in code, strings, interp holes re-entering code
land — it lives in Script with the scanner, per the formalization
rule) after two naive versions corrupted on hole-nested strings. Now
`printerr (t.` and `$"q: {t.` both complete to the body-inferred row
EXACTLY. The completion ladder: resolved head → its fields;
pipeline-with-holes → exact element; repairable statement → exact
row; truly unknowable → declared-fields fallback.

## Completion for params — the declared-fields fallback (2026-07-21)

Third live-testing receipt: `t.` inside a function body completed
nothing — params live in the checker's scope, never the completion
env, and the statement being TYPED is broken so no typed tree exists
either. The weir-shaped fallback: records are nominal and declared,
so an unresolvable dotted head offers EVERY declared record's fields
(high-signal in small scripts; `_.` completes too as a bonus).
HONEST LIMIT recorded: this is the union of all records, not the
param's actual type — cursor-accurate param typing needs
error-recovery parsing (analyze the broken statement with holes),
which is the parked next step if the noise ever bites. Also this
session: completion textEdit ranges (the doubling + micro's prefix
filter) — the pattern across all three reports: every client
disagreement became a frame-level pin.

## Three out-of-band asks: exit, Ctrl+D, usage (2026-07-21)

(1) `Exit.code` renamed to bare `exit` — F#-parity (`exit : int ->
'a` exists there; oracle Same pin added), the Exit module retired,
every pin/doc migrated. (2) The REPL's ReadLine NuGet package is
GONE, replaced by an owned ~150-line editor: Ctrl+D on an empty line
is EOF (exit 0), Ctrl+C cancels the LINE and keeps the session (bash
semantics — TreatControlCAsInput makes ^C a key, not a signal),
history/arrows/Home/End/^A^E^U^K, and tab completion via
Complete.suggest with common-prefix extension. Debug tale worth
keeping: the old lib "swallowing" Ctrl+D sent us probing .NET's
ReadKey (which delivers ^D fine as Key=D+Control) — and the final
"still broken" was the TEST harness counting a zombie as alive
(kill(pid,0) succeeds on zombies; waitpid told the truth). The
editor was correct for two rounds of debugging. Last non-FParsec
dependency deleted. (3) Usage text rewritten to the real surface
(REPL/script/-e/check/fmt/lsp — the obsolete [run] form dropped from
the text; the arm still accepts it).

## Live-testing receipts: check assumes commands; the resolver goes per-statement (2026-07-21)

The user's first real editing session delivered two receipts within
minutes — the LSP chain's acceptance test working as intended.

(1) CHECK-ONLY CONSUMERS ASSUME COMMANDS: editing bicep-deploy.weir
without az/bicep installed cascaded into parse errors ("not an
external command" in the backtrack), making the LSP useless for ops
scripts — the exact demo. Decided (flagged, overridable): weir check
and the LSP parse unknown COMMAND-SHAPED heads as commands and emit
cmd-not-found WARNINGS (exit 0); the runner keeps hard resolution.
Same pipeline, explicitly different resolver input — the gateExprs
pattern again — with the deliberate verdict difference PINNED
(check-warns-where-run-errors). Three narrowing rounds landed the
assumption: everything → broke dotted names (Env.load became a
command head); undotted → `{` hijacked a record RHS; final rule:
letter-initial ident-with-dashes, never keywords (`from porcelain`
must stay an adapter).

(2) THE PER-STATEMENT RESOLVER — the assumption exposed a LATENT
runner quirk: the parse resolver was built ONCE from the initial env,
so script-defined names were unknown at parse time; the runner was
correct only by accident (unknown + not-on-PATH falls to expression),
and a binding named like a PATH binary did NOT shadow it (`let cat =
1` then `cat x` ran the binary). checkStatement now takes a
resolver FACTORY and builds from the CURRENT env per statement:
bindings shadow PATH commands by construction (`^cat` still forces
the binary — pinned both ways). This closes the value>module>external
precedence rule's gap for script-defined names — the pin family that
existed for builtins now holds everywhere.

Also from the same session: UnsafeRelaxedJsonEscaping (the default
encoder's \u0022 quote escaping mangled in micro's display) with a
frame-level probe.

## weir check + weir lsp — the chain lands (2026-07-21, chain 2+3/3)

Session 2: `weir check [--json]` with statement-level RECOVERY (a
failed statement records its diag and checking continues env-
unchanged — a multi-error file reports every independent error),
codes seeded from the message families (casing-law, discard,
seq-unit, refutable-binder, non-exhaustive, ord-key, eq, show-fn,
unbound, ambiguous-constraint, parse, assembly), hand-rolled
AOT-safe JSON, warnings-as-exit-0 (decided, matching the runner).
The whole-file check median PINNED AT 10ms — the LSP's per-keystroke
license.

Session 3: `weir lsp` v1 — diagnostics/hover/completion over stdio.
AOT path: the hand-rolled JSON-RPC loop, taken BY PREDICTION (the
plan's gate pre-authorized it; Ionide.LanguageServerProtocol carries
a reflection serializer, exactly what the trimmer discipline bans).
CORRECTED same-day on user review: the hand-rolled JSON READER was
over-conservative AND buggy — the AOT ban covers reflection
SERIALIZERS, not System.Text.Json's JsonDocument DOM (reflection-
free, trim-annotated), and the hand-rolled reader mishandled
surrogate pairs (didChange carries whole documents as JSON strings;
emoji in a script would have corrupted it). Reader swapped to
JsonDocument, unicode round-trip probe added, binary 6.5MB, timing
unchanged. WRITING followed on the next review round:
Utf8JsonWriter (the DOM reader's write twin, equally AOT-safe) now
builds every dynamic payload — escaping is the library's job on BOTH
sides; hand-rolled JSON survives only as one constant capabilities
blob behind WriteRawValue. The full lesson, one line: the AOT ban is
on reflection SERIALIZERS; both halves of System.Text.Json's
imperative API were always allowed. NO incrementality, on
purpose: whole-file re-check per didChange under the 10ms license;
the server's only state is document TEXT (stale-cache bugs refused
by construction). Hover = smallest typed node at the position, with
the let-name fallback showing the generalized scheme; completion
re-plumbs Complete.suggest (the REPL's sources) + PATH commands at
line head. Integration probes speak the real protocol against the
AOT binary (python3-driven, loudly skipped if absent).

The arc that closes: week one asked whether fsautocomplete could
serve weir; the answer then was "your checker is the brain and FCS
cannot be it." This week the checker IS the brain of an LSP — through
the same single pipeline function every other consumer uses, built
three days after that function's absence caused the mirror incident.
The answer is cashed.

## One pipeline — the mirror incident's fix (2026-07-21, LSP chain 1/3)

The incident: the oracle's weirVerdict mirror kept a pre-class
generalization and OVER-ACCEPTED a shape the runner rejects — its own
fidelity pin caught it (type classes Session A). The diagnosis was
structural: FOUR consumers (runner, REPL, -e, mirror) re-derived the
statement pipeline and agreed by discipline. The fix is the
formalization pattern one layer up: Script.checkStatement owns
parse → dispatch → check → statement-rule gate, physical spans
computed inside; consumers render and evaluate, never re-derive. One
explicit switch (gateExprs) distinguishes scripts (statement rule)
from echoing consumers (REPL/-e) — caught during the rebase when the
gate would have killed `-e '1 + 2'`; a switch is a parameter, a
re-derivation would have been the disease again. Zero pin edits
across 621 unit / 145 e2e / 63 oracle — the behavior-preservation
contract held; the incident pin is annotated as a regression guard
(drift is now unconstructible). Reported deltas, both unpinned and
both improvements: -e reports a let RHS's REAL type error instead of
the form message (kinds are judged after checking now), and the
REPL's casing error gained the same underline as every other type
error. Dead code retired: the REPL's tryRun. Sessions 2 (weir check
--json) and 3 (weir lsp) consume this function next.

## The casing law — lowercase binds, uppercase declares (2026-07-21)

The mini-session that closed the casing triple: constructors
uppercase (standing since generics), modules uppercase, and now
binders lowercase — stated once in SEMANTICS. The user's challenge
during the design discussion mattered and is preserved in the row:
the sentinel guards were NEVER this law's payoff (print/show defend
LOWERCASE shadowing, which stays legal; Env.load's guard stays for
the still-declarable constructor collision `type T = Env of int`).
The honest payoff: the binders-session PCase fall-through hack is
unrepresentable, value-shadows-module is grammar-dead, and binder
patterns get intent-aware diagnosis (unknown uppercase → casing
hint; known constructor → match hint — the env-lookup disambiguator
the single-case park will inherit). The row calls itself the
strictness family's first STYLISTIC member rather than borrowing the
others' safety story. Migration grep: zero hits — the convention was
exactly as strong as assumed.

## Pattern binders + the bare-comma amendment (2026-07-21)

The no-pattern-binders row's arc COMPLETES: "destructuring is the
real scope" (the retired no-tuples row's own words) → the gap named
when a user probe hit it → this session. Everything in the plan's
forms block runs; the F#-negative is the row's remaining content
(refutable binders: F# warns-accepts, weir hard-errors — the
strictness family again). The bare-comma amendment took FULL F#
precedence rather than a let-RHS context hack, and its archaeology
addresses the parens-only rule's two original reasons: argv safety
holds by construction (pinned from both sides — expression commas
build tuples, command barewords keep commas), and the `f x, y`
footgun is imported knowingly. The comma×`;` cell (weir-only, no F#
to copy) decided: comma TIGHTER, so a sequenced tuple is the
familiar discard error, not a tuple of sequences. Two session
catches worth their pins: (1) uppercase idents (`let Seq = ...`)
initially routed into the pattern path as constructor patterns —
value shadowing broke until bare PCase fell through to the ident
path; (2) the check-mode ELambdaPat twin was MISSED first — piped
tuple lambdas lost the pushed element type and hole-defaulting fired
early ("expected int, got string" on a correct program); the e2e
battery caught it, and the bidirectional-twin lesson is flagged in
TRANSCRIPTION (flag 7: three lambda-arm duplicates now; helper on a
fourth). Observed, pre-existing, out of scope: interp holes on
unresolved vars default to string BEFORE a later pipe could resolve
them (`1 |> (fun k -> $"{k}")` errors) — logged as a defaulting-
order edge for a future look.

## REVERSAL: tuples land; "records are the product" retires (2026-07-21)

The named decision, dated: the generics session promoted "weir has no
product types; records with named fields are the product story" to a
rules bullet, and Seq.pairwise shipped its {Fst; Snd} Pair record
under that rule. WHAT HELD: the two-value CLI reshape was absorbed
cleanly by the one-flag-per-value idiom; ad-hoc pair records never
proliferated (Pair stayed pairwise's private shape); nothing in the
telemetry shows record-noise pain. WHAT PRESSURED: F# fidelity —
tuples are pervasive in the corpus and in agent priors (the
no-tuples divergence rows are among the most agent-visible, with
standing SKILL must-fail blocks); one anonymous-record bleed hit;
and the predicted {Fst; Snd} want for zip stood as the argument the
plan named. THE DIRECTION: user-opened ("ok go with tuples now"),
receipts thin and said so — the reversal ships on direction, not
evidence weight, and this entry is the honesty. THE SCOPE: F#
semantics bounded — types, literals, patterns, arity 2+; NO
lexicographic Ord (divergence row, with record-ordering if ever); NO
splice/Env.load/json membership; multi-payload constructors
un-restrict (the single-payload rule was this rule's corollary and
retires with it); Seq.zip ships WITH (the customer); pairwise
re-types to tuples (breaking, migrated with this archaeology).
RATIONALE SURVIVES AS STYLE: records remain the taught product for
named data; tuples are for transient pairs — the original decision
becomes a GUIDE paragraph instead of a grammar rule.

COMPLETION (same day): tuples were BORING, as the plan's model
demanded — the stop-and-report clause never fired; TTuple is one
more structural case in every walk, the class predicates took it
componentwise (Ord rejected by its existing catch-all — zero new
rules), and multi-payload constructors cost NOTHING (a tuple payload
is just a payload). json/splices stayed closed via existing
whitelists — reject-don't-guess held without new code. THREE rows
retired (no-tuples, no-literal-patterns' sibling single-payload-
unions, plus the SKILL must-fail flips, extractor-proven); THREE
rows born (no-tuple-ord, tuple-exhaustiveness-bounded,
no-pattern-params — the honest edges of the bounded scope). The
corpus re-mine was SKIPPED: WEIR_CORPUS_DIR absent in this
environment (time-box zero; noted, not silently dropped). Pairwise
migrated to seq<'a * 'a> with the {Fst; Snd} Pair record deleted.

## Literal patterns + () thunks (2026-07-21, Session 1 of the plan)

The strongest-receipts item shipped first, as sequenced. Literal
patterns landed with F#'s completion rule; the retirement flipped a
corpus pin to Same (among the most agent-visible divergences gone)
and SURFACED a divergence that had never been pinned: weir's
exhaustiveness-is-hard-error vs F#'s warning — the oracle refused
the naive Same pin and the new exhaustiveness-hard-error row records
what was implicitly true since bool branching. The thunk arm is the
session's honesty case: `()` params are a CHECKER touch, not sugar
(the generalization trap is tripwired as the arm's reason). The
thunk receipt had NOT arrived — opened by user choice with the plan,
on record. One old pin flipped with archaeology ("unit params are
not in the v1 sugar" → the desugar-shape pin); the SKILL must-fail
block flipped to must-pass and the doc-test extractor proved the
edit, the mechanism working as designed. Tuples (Session 2+) remain
gated-open: type classes landed, so the structural gate is
satisfied whenever the user calls it.

## The sentinel ledger CLOSES — type classes Session C (2026-07-21)

The arc, written as the measure-arc's counterpart: OPENED (unit/print
session — print's ∀__print scheme, the first hand-rolled capability
check, ledgered instead of generalized) → THREE CUSTOMERS accrued
(print/printerr, show, Seq.contains; sortBy's runtime rule counted as
the shadow fourth) → MACHINERY (qualified types over the existing
Damas-Milner, built when the design was on file and the user opened
it by fiat) → RETIRED (==, contains, show, sortBy all ordinary
constrained schemes; the print family alone remains a sentinel BY
DESIGN — the splice boundary is deliberately narrower than Show).
Where the measure arc is the precedent for CANCELLING speculative
checker machinery, this one is the precedent for building it: the
ledger accrued real customers first, the design waited on file, and
the machinery landed with zero existing-pin edits in A, one amended
pin in B, zero in C. Session C's matrix ran all green EXCEPT for one
scope CORRECTION (no code change): fn-typed record fields are
reachable via generic instantiation, so the Eq battery now pins the
Box<fn> rejection Session A had reasoned unreachable. Products
pinned across generic unions/records, rows (double instantiation,
mergeRows movement), nested generalization escape, match guards, the
print sentinel, splices, and pmap workers. TRANSCRIPTION's A/B
addenda consolidated into one section. The qualified-types question
that opened with the ledger is ANSWERED and closed.

## Type classes Session B — Show + Ord; the runtime check dies (2026-07-21)

Machine-regime again (standing choice from A). The headline landed
exactly as the plan wrote it: sortBy's runtime scalar-key rule is
replaced by a static Ord constraint, and the e2e proves the stronger
property — a script with an effect BEFORE a bad-key sortBy runs ZERO
effects (check-first). scalarCompare's failwith is now an
unreachable-marker; "zero runtime type checks" is fully true for the
first time. Show retired with all THREE of its sentinel arms
(bare-default included: bare-value show now stays generic with Show
riding — one pin amended with archaeology, the only churn). Show ≠
Eq structurally after all (seqs render but do not compare), so the
one-predicate consolidation note from the plan is moot — the classes
diverged at birth, vindicating keeping them distinct. Ord's
no-decomposition rule got its own tripwire (FileRow: all fields
orderable, record still rejected). Oracle: both flagship shapes
Same — including the generic sort helper, which F# also
constraint-infers. Session C (hardening: classes x rows x generics
product battery, ledger closure) remains.

## Type classes Session A — Eq, machine-regime-only (2026-07-20)

Opened by fiat (recorded; the trigger had not fired) and run
MACHINE-REGIME-ONLY by explicit user choice: no human read of the
constraint core — this is the deferral experiment's boldest test, in
those words. The tax paid in its place: the class battery (14 pins),
two new tripwires (ambient-constraint containment — the class analog
of transitive reachability — and per-use freshness), the
TRANSCRIPTION addendum with the new judgment surface and flag 6, the
suite run twice, oracle Same pins, and effect-level e2e. The
machinery landed inside the stop-and-report budget: Scheme + Ctx +
demand/discharge + the four audited arms; zero parser/eval/value
touches (erasure held). ZERO existing-pin edits — the == re-type
keeps concrete failure messages verbatim and the sentinel arms'
deletion is invisible to every pinned shape. Session-caught drift:
the ORACLE's runner mirror kept the old constraint-less
generalization and mis-verdicted a fidelity pin — the pin itself
caught it (weir=Accept claimed, mirror said Accept for a shape the
real runner rejects... inverted: the mirror over-accepted). The
mirror is a fourth statement-pipeline consumer that agrees by
discipline, not construction — logged as a formalization candidate
(unify the runner and mirror on one checked-statement function).
Retired: sentinel customer three (Seq.contains). Sessions B (Show/
Ord — the runtime check dies) and C (hardening) remain.

## Type classes: design filed ahead of trigger (2026-07-20)

plans/PLAN-type-classes.md is ON FILE, not opened — the district precedent
applied to the biggest parked item: settle the design while the
evidence ledger honestly shows the trigger unfired (sentinel ledger:
three builtin customers — Eq via ==/Seq.contains, Show via show, Ord
via sortBy's runtime rule — and ZERO user-code generic-equality
receipts). What the filing bought: the scope correction is now on
paper (generics exist; this is qualified types OVER them), the
rows×classes rules are pre-decided (the novel surface), the
stop-and-report budget is drawn (static filter only — any runtime
constraint presence is a model violation), and the read-regime
question is isolated as the ONE decision requiring explicit user
blessing before Session A (scoped constraint-core read, the plan's
lean, vs machine-regime-only as the deferral experiment's boldest
test). Session A's trigger remains: a user-code receipt, or recorded
fiat.

## Hardening sweep — the postmortem pays out (2026-07-20)

The silent-swallow postmortem, run through the deferral experiment's
two questions: (a) the bug lived in NEITHER an addendum NOR the
checker core — it lived in the assembler, and the measurement surface
was checker-scoped; first evidence that "process instead of read"
needs its net wider than the checker (raised for the experiment's
owner, not taken here). (b) The composition-product rule would have
caught it and postdated it; the retroactive matrix
(tests/PRODUCT-MATRIX.md) is the make-the-class-extinct response.

The sweep's own scoreboard argues for the rules it enforces: the six
missing matrix cells all landed GREEN (invariants held by behavior,
now by test) — but the FIXTURE-DIVERSITY backfill caught a real
parser-facing bug within minutes (field value opening on the next
line inside a record: spurious separator; classifier fix,
StartsField), and the ExitRequest insurance pin came up RED exactly
where the plan predicted a fifth site (the REPL swallowed the
carrier and exited 0; fixed, pin-per-site chosen over helper
unification — three differently-shaped sites, reported per the
plan). Two real bugs from the "mostly mechanical" session: the
mechanical sweeps are where the bugs were. PROCESS.md now exists as
the standing-rules index; POSITIONS.md as the copyable inventory.

One live masking incident during the session, for the ledger: a
2-error build slipped through a grep-chained pipeline and the battery
ran against a stale binary — the verify rule (exit code first)
exists for exactly this and was applied on the second look.

## Env sugar Layers 1+2 — the seam pays out (2026-07-20)

(Addendum, same day: modernizing the bicep example to the new idiom
immediately caught a LATENT district bug — a dedent back to marker
level after a standalone marker joined with space, not `;`; every
prior district shape was if-headed, where the offside compound
supplied the sibling level by accident. Fix: a closing district sets
its marker line as the sibling level, exactly like a compound. Pinned
for standalone `!` and `!e` both. The example-as-acceptance-test
pattern earns its keep again.)

Opened by user choice, not receipts (on record; the parks' reopen
triggers never fired). The pre-scoping was right that together is
cheaper than either alone: the `!name` line-end meaning got decided
once, and Layer 2 cost NOTHING below the assembler — a MarkerKind
variant and two parameterized joins emit `!name(...)` text that Layer
1's grammar reparses. Layer 1's env threads at parse-construction
(commandSegment/cmdLineWith param), so every spawn form — segments,
pipe stages, `| complete` — carries it by architecture rather than by
a post-walk; `completedEnv` completes the cmd/cmdEnv/completed
pattern family. The formalization session's success test ran forward
for the first time: this diff landed in classify/Join, no raw string
logic. Reservation cost, pinned: a line-end bareword `!word` is now a
district header — quote a literal one. The adjacency rule (ident
glued to glyph and paren) keeps `$e (...)` and `$name` splices
meaning what they always did.

## Child-env injection — the shEnv receipt lands (2026-07-20)

The premise did the design work: "injection, not session mutation"
(adopted from the receipt's shape analysis) dissolved the parked
Env.set question without building anything ambient. cmdEnv/runEnv are
the run/cmd precedent applied verbatim — even the implementation is
the same composition (`apply printImpl (...)`), and `Proc.lines =
linesWith []` makes the shared-path claim true by construction rather
than by discipline. Env.fromFile is typed-boundary customer five, and
the reject-don't-guess line (parser, not evaluator; every rejection
names the sh escape) held cleanly — single-quoted values are the one
place shell semantics leak in ON PURPOSE ($ is literal there, which
IS the shell's own rule, so the subset stays faithful). The rewritten
bicep example beat the sh-c translation on its own ground: values
that shell expansion silently passed as EMPTY (unset AZURE_CLIENT_ID
at translation time) now flow as typed argv lookups. Layer ledger on
record in SEMANTICS: 0 ships, 1-2 parked with split triggers (the
prediction, repeated: Layer 2 — the district header — is where
receipts will point), 3 tombstoned.

## Assembler formalization — the boundary question (2026-07-20)

"Shouldn't the assembler be part of the parser?" NO, on record so it
is not re-asked from scratch: the text-pre-pass architecture is the
reframe that won the multi-line gate (zero parser changes, expression
suite green by construction); it is how F# itself implements light
syntax (token insertion outside the grammar proper); every layered
feature since — block lets, siblings, districts, the offside close —
landed under budget clauses a small inspectable layer made possible;
and both grammar incidents (|-inertness, greedy-;) were RULE errors,
not LAYER errors — the same rules inside the parser would have been
the same bugs, harder to see. What the question correctly detected:
the layer had outgrown its shape — StartsWith classifications, TWO
quote-aware mini-lexers (stripComment's, then braceDelta's copy born
in the consolidation session hours earlier), hand-audited span
offsets (`+ 5`, `- 1 + 2`), and a regex scraping FParsec's error
text. Formalized IN PLACE: one scanner (foldOutsideStrings), one
classifier (classifyLine + classifyPiece — two granularities because
consumers genuinely operate at two; `if c then !` needs marker AND
compound flags, which an exclusive enum cannot say), one join algebra
(applyJoin owns each insertion string and derives joinedStart from
it), structured parse failures (ParseFailure.Col from FParsec's own
ParserError — the regex deleted). Behavior-preserving proven the
strong way: zero pin edits across 500 held pins + oracle + e2e span
positions. Sequencing inverted from the proposal (this landed AFTER
consolidation): the plan's success test — "the consolidation diff
touches the classifier, not raw string logic" — inverts to
archaeology: consolidation DID add raw string logic (braceDelta,
isCompoundHead/isElse), and this session absorbed it same-day.

RULE for future sessions: new line-shape logic goes in classify /
the scanner / Join — a StartsWith or quote-state loop in the fold is
a review flag.

## Greedy-`;` design review — the offside close (2026-07-20)

The bicep bite reopened greedy-`;` per its own revisit metric. The
review's discovery upgrades the finding: the bite class has a SILENT
member. Today `let f c =` over `if c then printerr "a"` + same-indent
`printerr "b"` swallows b INTO the then-branch — conditional execution
the user never wrote, no diagnostic (repro kept as a pin). Same-indent
`else` also fails today (`; else` from the sibling rule) — the fmt
refusal's likely root. So the seam was wrong three ways, one cause:
flattening erases the offside boundary between "deeper than the if"
(body) and "same level as the if" (sibling).

Candidates weighed: (b) revert to lowest-`;` — F#-faithful grammar,
retires the divergence row, but the collision shape (deeper effect
siblings under `then`) then needs parens INSIDE pieces at `then` /
`else` / `->` positions — grammar-interior surgery the assembler is
forbidden by its own layer separation; district-exclusivity cannot
cover expression effects (printerr blocks). (a) keep greedy, restore
the boundary at the assembler: an `if`/`match`-headed piece CLOSES
(paren-wraps, a balanced line-structural unit) when a sibling arrives
at its head indent or shallower; `else` and `|` pieces extend instead
of closing. Deeper siblings still join into the body, where greedy
grouping is exactly right.

SHIPPED as the offside close (paren-wrap at piece granularity;
else/| extend). The revisit-metric arc closed: shipped-under-collision
(sequencing session) → first bite (bicep) → review → the collision
model itself was the bug (flattening erased the offside boundary), so
BOTH candidates' framing was wrong and the fix keeps greedy while
making multi-line grouping F#-faithful. fmt's refusal shared the root
(let-only depth model) — fixed with the general indent-level stack.
DECIDED: (a). Multi-line shapes now group F#-faithfully (oracle-
pinned); the semicolon-greedy-bodies row AMENDS to single-line-typed
`;` only. Forward archaeology: the sigil session predicted this
revisit would be cheaper post-sigils — true, but not for the predicted
reason: sigils shrank nothing here; the reprocess-and-piece machinery
the district built made the compound stack a natural extension.

## Function-body sequencing — seqExpr in let-RHS (2026-07-20)

The bicep-script translation (the first F#-to-weir translation with
command-running FUNCTIONS, not just top-level flow) hit a parse error
on its very first shape: `let quality t =` over sibling effect lines.
The assembler correctly produced `let quality t = a ; b` — but both
let-RHS positions still parsed `expr`, not `seqExpr`: the sequencing
session wired `;` into then/else/arms/lambdas/parens/statements and
missed the binding positions. Two-token fix (topLet rhsP both
branches, letIn value); `in` still closes a let-in because elements
stop at keywords. 3 pins (function RHS, let-in value, no-params
fallthrough past cmdLineLetRhs). Full translation receipts in
NOTES-agent.md — shEnv/child-env is the headline.

## Typed Env — Env.load Config (2026-07-20)

PLAN-typed-env executed. 478 tests (tripwires suite-inclusive, run
twice); battery +2 e2e pins; timing holds. The checker gained ONE
bespoke arm exactly per the plan's model — EFrom's type-name
resolution relocated to expression position, monomorphic-record guard
and message family included; no new type-system concepts, so no
stop-and-report. TRANSCRIPTION addendum in-session per the
measurement-surface rule; TEEnvLoad is inert to finalize/warnings
(carries a RecordDef, not types).

The distinction the e2e pin names: field-TYPE violations (seq/record/
union fields) are CHECK-time; missing/garbage values are the BOUNDARY
class at force — so an effect before the load legitimately runs
(pinned with a proof-file, the inverse of the check-first pin).
Collect-then-raise proven with a three-problem environment reporting
all three in one message. The exact-bool decision (true/false only;
TRUE and 1 rejected) pinned in the battery.

## Indexers — xs[i] (2026-07-20)

User ask, F# 6 precedent applied verbatim: `xs[i]` = `Seq.item i xs`
(desugar-only, zero checker surface); NO space = indexing, space =
application of a list (`Seq.sum [1; 2]` untouched). The immediacy
check needed no atom refactoring: spans record positions before
trailing whitespace, so the suffix parser compares the current
position against the target's span end — the postfixAtom rewrite is
a recursive suffix loop (fields and indexes interleave; chains,
row[0].Name, $(...)[0] all compose). The `_.Field` shorthand
generalized to `_[0]` for free. jira-branch: the Seq.item lines are
now fields[0]/fields[1]. 471 tests; suites/batteries/timing hold.

## The command district — line-end ! blocks (2026-07-20)

PLAN-command-district executed with one human-reviewed budget
amendment. 465 + 37 tests; battery +5 e2e pins; timing holds. The
flagship: the jira-branch cleanup is now marker + three bare command
lines — zero per-line spelling tax.

**The budget amendment and its metric lesson (as directed at review)**:
the assembler diff came in at net +84 against the ~40 clause — 2×.
Stopped and reported per the clause; keep was granted on the reasoned
review: line count is a PROXY — the clause's real target is
|-inertness-class murk, parallel invariant logic reimplementing the
assembler's rules per-mode. The district does the architectural
opposite: closing lines are REPROCESSED through the one rule set (the
recursive `go`), which is where the overshoot lives (restructuring
amplification + F# record plumbing; district logic proper ≈35-40).
A 2× diff of clean reprocessing beats an in-budget diff of duplicated
rules. Future assembler budgets: report net-new-logic vs restructuring
amplification as separate numbers — the reviewer's distinction,
pre-computed. The stop itself was correct both times it has fired;
the clause routes exactly this judgment to a human.

**Keep conditions, discharged**: the composition battery went green
(not retroactively granted) including the two mechanism pins the
reprocess owes — a district-closing let-closing line yields exactly
ONE span-table entry (double-processing is the mechanism's native bug
class), and assembler recursion is bounded by nesting (a
500-district file assembles; `go` recurses at most once per line,
district→normal).

**The marker rejection ledger** (the real payload, per the plan —
`do` is the one someone will propose again):
- `do`: false friend — F#'s `do` opens EXPRESSION blocks; the district
  is commands-only; borrowing F# syntax to mean something F#'s doesn't
  is the inverted-prior class, unrefereeable by the oracle.
- `>>>`: one keystroke from `>>`, a redirect shape under a
  "no redirects" teaching.
- `sh`: would resurrect the exact confusion the sh-builtin removal
  fixed (the district is WEIR command mode — check-time PATH, typed
  splices — not /bin/sh).
- `!` wins: the plural of `!(...)` — one glyph, one concept; already
  claimed by the family; prior-inert at line end.

**Single-logical-line forcing argument** (recorded with the sigil
labels): (1) district classification must not need paren-balance
lookahead; (2) command text is the one place newline-join changes
meaning silently (whitespace IS argv separation); (3) the typed
escapes exist (`run` for long argv, statement-level pipes for long
chains). The unclosed-sigil errors name both outs.

District x else resolved by INHERITANCE: a dedented else at marker
indent rejoins its if (pinned); col-0 if/else remains the standing
multiline boundary, unchanged by districts.

## Command-mode sigils — !(...) and $(...) (2026-07-20)

PLAN-command-sigils executed. 457 + 37 tests; battery +6 pins; timing
holds. ZERO checker surface, confirmed — both sigils are pure parser
desugars ($() = the chain expression; !() = chain |> print), exactly
the plan's model; no stop-and-report needed.

- The resolver reached the expression grammar via a ThreadLocal
  ambient (parseLine sets/resets) — threading it through every parser
  signature would have been a rewrite; parallel test runs stay
  isolated (the worker-fork precedent).
- Uniform-interior paid immediately: `$(git status | complete)` binds
  the Completed record with no extra machinery — but the completeMarker
  needed its lookahead extended to the sigil closer (it demanded
  pipe-or-eof; `)` is now legal after `complete`).
- The composition-pin battery (the greedy-`;` lesson, mandated by the
  plan) is in: sigils x assembler (bare-if blocks, both branch ways,
  effect-counted in e2e), x greedy-`;` (single-line grouping pinned
  body-scoped), x interpolation (holes never open command mode),
  x complete (outside = parse error; inside composes), x strict.
- jira-branch final form: the cleanup is a bare `if clean then` with
  three `!(...)` lines — the spelling tax is two characters per
  command. The branch line stays BARE (`let branch = git rev-parse ...
  | Seq.head`) per user review: bare wins wherever legal (least ink);
  $() earns its keep where bare cannot go (expressions, holes, nested
  splices) — the docs teach that position, replacing the earlier
  canonical-$() wording.
- Forward archaeology as blessed: greedy-`;` protected flat blocks of
  BARE expressions; sigil atoms self-delimit, so the divergence
  protects a shrinking idiom — if its confusion metric ever fires,
  the revisit is cheaper now. Recorded, no action.

## Sequencing-and-args Session 2 — block effect sequencing (2026-07-20)

450 + 37 tests; battery green; the jira-branch cleanup now reads as
three `run` lines under one `if` — the plan's done-when, verbatim.

**STOP-AND-REPORT (the plan's own clause, exercised on the precedence
decision, not the assembler budget)**: the blessed lowest-precedence
`;` and the assembler sibling rule COLLIDED at the flagship shape —
flat-joining `if clean then run1 ; run2` with lowest-`;` parses the
runs OUTSIDE the if: silently unconditional cleanup, the worst
possible failure mode. Options were paren-wrapping sibling groups in
the assembler (over budget, murky invariants — the |-inertness class)
or making `;` GREEDY in body positions so it binds into blocks.
Shipped greedy: the flat text then means what the block-shaped source
says. Cost: F# VERBOSE grouping (`(if c then a); b` for a single-line
`if c then a ; b`) is now a named divergence (semicolon-greedy-bodies)
— parenthesize the if to sequence after it. The oracle Same pins
cover the block shapes (F# light accepts them natively; verdicts
agree) — grouping itself is invisible to a shapes-only oracle, so the
divergence is carried by tests and the row, stated explicitly.

Assembler budget: the sibling rule landed in ~6 lines (lastIndent in
the fold state + one match arm) — well under the 30-line clause.
Sequencing semantics ride ESeq (e1 ⇐ unit, tailored error); pipes and
let-closure are inert/priority exactly as planned. The `;`-command
warning fires on the pinned `git add -A ; git push` shape at CHECK
time (runtime bash-parity preserved — the argv still passes).

Also collected en route (Session 1 notes hold): the multi-line record
separator friction stands as a candidate rider — NOT taken into this
session either (scope discipline; it is a record-context rule, not a
sibling rule).

## Sequencing-and-args Session 1 — the library bits (2026-07-20)

PLAN-sequencing-and-args Session 1 executed. 443 tests; battery +2
pins; timing holds. The origin script (tools/jira-branch.weir) is the
committed acceptance test: flag check and field access are one call
each, verified end to end with jira/fzf stand-ins.

- Seq.contains/exists/forall/item/tryItem/skip + Args.flag/value +
  run, per the blessed decisions. `run` is literally
  `apply printImpl (apply (apply cmdImpl p) a)` — the shared-path
  decision implemented as composition, byte-identity pinned in e2e.
- **Sentinel ledger (the blessed bookkeeping entry)**: hand-rolled
  type-class instances now number three — equatable (`==`, `<>`,
  `Seq.contains`), showable (`show`), comparable (`sortBy`,
  runtime-checked only). Loophole noted: `contains` checks equatability
  on the evidenced shapes (piped, full application); a bare
  `Seq.contains` member value stays generic — same weakening as
  print's defaulted bare form. Qualified types stay parked; this entry
  is the accumulating evidence base.
- Acceptance-test yield (the pattern held): multi-line RECORD literals
  lose field separators in assembly — F# separates fields by newline,
  weir joins with a space; trailing `;` is the spelling
  (skill-lined + telemetry-logged). A record-field insertion rule is
  a named candidate rider for Session 2's assembler work — same
  technique, distinct context — NOT improvised into scope.

## The user guide — doc-tested from birth (2026-07-20)

User asked "guide or too early?" — answered not-too-early on two
grounds: the rot antidote already exists (doc-tested fenced blocks;
the guide churns WITH the language in CI instead of rotting behind
it), and the language just crossed guide-shaped completeness
(functions, branching, ranges, processes, parallelism, show/fail).

docs/GUIDE.md: a tour in 10 sections, 10 executable blocks; the
skill-doc harness generalized over both docs (19 blocks total).
README.md grew from one line to a real front page (its example
hand-verified once; prose-fenced deliberately — the guide carries the
CI-pinned blocks). Scope guard from the agent plan honored: SKILL.md
stays terse and agent-shaped; the guide is the separate human
artifact.

The writing-as-audit prediction held on the first run: the guide's
functions example used \" escapes inside an interpolation hole —
not weir (holes take plain nested strings) — and the harness rejected
the guide before the guide could teach the error. Also quietly
satisfying: the parallelism example opens with a line-head string
list, legal only since the [-head fix.

## Worker sessions fork — cd allowed in parallel (2026-07-20)

User question ("shouldn't we have nested sessions for parallel so cd
would be allowed?") upgraded the same-day cd guard from prohibition to
SEMANTICS: pmap/piter workers fork the ambient session — inherit the
parent cwd at fan-out, worker-local cd, fork dies at the join, root
untouched. This is the "session-as-value is the future shape" note
from the original thread-safety question, arriving incrementally:
Session.Cwd became a function over root + ThreadLocal override
(setCwd writes the layer it reads), nested pmap forks the WORKER's
session correctly for free. The make -C-style use case works:
dirs |> Seq.piter (fun d -> let x = cd d in ...).

Named caveat carried forward, not hidden: read-at-force-time is
unchanged, so a lazy stream built in a worker but forced after the
join resolves against the joiner's session — force inside the worker
when the cd matters (documented in SEMANTICS + skill). The cd guard
and its pin retired; fork-isolation pinned in unit (parent cwd
compared across the call) and e2e (workers print /, /etc; parent
prints /tmp after). parallelTests now testSequenced (they mutate the
root session via cd).

## Seq.pmap / Seq.piter — data parallelism (2026-07-20)

User question ("could we have parallelism though? think of
Array.parallel") answered yes with the line drawn precisely: the async
rejection covered concurrency MACHINERY (colored types, await,
schedulers); pmap/piter are combinators whose parallelism is an
implementation detail — no new types, blocking, eager, input-order
results, ProcessorCount degree, first worker error rethrown
(AggregateException unwrapped). Shell-native want: xargs -P with
types. Border row updated so the rejection and the combinators read
as one position.

The landmine was Session.Cwd: the single-threaded-session invariant is
now GUARDED, not trusted — cd inside a worker raises "cd is not
allowed inside parallel workers" via a ThreadLocal flag set around
each worker item. Interleaved piter output is line-atomic, user-owned
(documented, as with any parallel tool). Wall-clock e2e pin: 4x300ms
sleeps under 900ms on the AOT binary (measured 311ms locally). One
amusing scope self-collision: the first timing probe used `ignore` —
which weir deliberately parked; the unit-shaped spelling
(if ... then print) was the honest fix.

## The F# border classified — rejected vs pending (2026-07-20)

User question exposed the gap: divergences.md named oracle-refereed
shapes but never distinguished "decided against" from "nobody built
it", and the pending absences lived scattered in plan parks. The
artifact now carries a status column — different / rejected / pending
— and grew from 13 rows to 26, adding the major absences that had no
entry because no pin touched them (floats, chars, exceptions,
ascription, user modules, anonymous records, destructuring, OO,
imperative loops, elif). Honesty rule applied while classifying:
where no decision was ever made, status is pending even when absence
feels intentional (block comments, unary minus); rejected requires a
citable rationale. SEMANTICS gained "The F# border" section as the
pointer; the skill file routes agents to the table. The id-coupling
tripwire fired during the rework itself (a renamed id broke its pin —
the artifact and battery cannot drift apart).

## show — the debugging renderer (2026-07-20)

The collision parked in PLAN-unit-and-print ("first dogfood complaint
lands here") resolved on user go, taking the show-builtin fork over
widening print: print keeps its data-plane contract, show is the
explicitly lossy REPL-shaped renderer (the SAME formatValue — one
renderer). Composes as strings do: print (show row), $"{show r}",
Seq.map (fun r -> show r).

The what-is-showable question got the checker answer: showable = no
function anywhere in the type, recursively (hasFunction walks
records/unions/seqs/rows with a seen-set) — `show (Some f)` is caught
in the payload, not just the top type. Print-family sentinel
discipline reused verbatim: bespoke arms in applied/piped positions,
string -> string bare-value default, value-shadowing falls through.
430+ tests; battery +1; skill updated.

## Fix round: transparent comments, parse-error attribution (2026-07-20)

Two bug-class items from the standing ledger, done on user "anything
else to fix". 426 + 34 tests; battery +2 pins; timing holds.

- **Comment-only lines were ending statements** — stripComment reduced
  them to blank before assembly, so any block with an interior comment
  died ("continuation after a blank line"). Noted mid-bool-session,
  never fixed — a fidelity bug in the letter (F# comments are
  transparent). Fix at the runner layer: comment-only lines are
  filtered before assemble (the assembler itself cannot distinguish
  them post-strip); fmt and the oracle's weirVerdict mirror the
  filter, and the formatter no longer resets its block state at a
  comment. Oracle pin: comment-inside-block is Same-accept, refereed.
- **Parse errors now translate through the segment table** like type
  errors always did: the FParsec Ln/Col of the joined logical line is
  remapped to physical file:line:col ("perr.weir:2:8: parse error"
  pointing at the offending token on the continuation line). This was
  the read plan's wildcard prediction for stranding agents on
  multi-line scripts; e2e-pinned.

Remaining ledger is feature-class, user's pick: literal int patterns
(corpus-mined, guard idiom is the workaround), anonymous records (one
bleed hit), Seq.collect-as-flatMap (name freed, no demand yet),
show/record-print (predicted first debugging complaint), comprehensions
(evidence-gated), REPL multi-line.

## let f x = ... parameter sugar (2026-07-20)

The corpus session's top yield, shipped same day on user go. Pure
parser desugar (curryParams: nested lambdas) in both let forms;
checker untouched for the sugar itself. 425 + 33 tests; battery +1;
skill-doc reshaped (the must-fail block became the positive example).

Scope edges, decided reject-don't-guess: params are plain idents (no
(), no patterns, no annotations); a param-ful let takes an expression
RHS only — command mode under a lambda would break the splice-
defaulting soundness invariant; HOF restriction unchanged (named as
divergence no-hof-inference with a pin).

Two findings from the session's own tripwires:
- **The oracle caught a regression MINUTES after the sugar landed**:
  `let mutable x = 1` and `let rec f = 1` began parsing as functions
  named `mutable`/`rec` — both fidelity pins flipped to both=Accept.
  Fix: `rec` and `mutable` are reserved words now. This is the
  oracle's first live catch: the failure mode (F# muscle memory silently doing something
  else) is invisible to positive tests.
- The sugar made var-var operands common and exposed that `-` and the
  comparisons were missing from the defaulting family by accident of
  history (`let sub x y = x - y` failed to infer). Rule regularized:
  every UNIQUE-typing operator defaults; `+` alone rejects
  (int-or-string), named as divergence no-operator-defaulting.

## Corpus mining executed — the park reopened and paid (2026-07-20)

User overrode the park; the blocker dissolved on second look: the
"parser of its own" was wrong — ComponentTests snippets live in
triple-quoted strings, which are regex-extractable (no escapes inside
"""..."""). Pipeline: sparse clone (dotnet/fsharp @ 5928e91, 2978
files, 35MB) → 4256 extracted strings → aggressive mechanical filter →
78 unique weir-plausible snippets → bulk verdict comparison
(env-gated Corpus.fs; report committed as
tests/fidelity/corpus-report-5928e91.md).

Headline: **zero GOLD** — across the corpus, weir never accepts a
shape F# rejects. 4 agree-accept, 50 agree-reject, 24 disagreements
all in the F#-accepts direction; triage: ~11 filter leakage, plus
FOUR unnamed divergences nobody had listed:
1. **`let f x = ...` parameter sugar** — 8 of the 24; the most common
   F# line shape does not exist in weir. Named + pinned + skill lines
   with must-fail doc blocks. ALSO FLAGGED as a candidate feature:
   the desugar is parser-only (nested fun) and the agent prior-bleed
   pressure here will be relentless — user decision queued.
2. **Literal int patterns** (`| 0 ->`) — named + pinned + skill line
   (the guard idiom is the spelling).
3. **Function-valued interpolation holes** — the splice rule was
   documented as a weir rule but never as an F#-divergence; named.
4. **Format specifiers in holes** — in SEMANTICS, missing from the
   artifact; named.
The artifact audit's real lesson: divergences implied by a rule's
ABSENCE are invisible to prose review — only a corpus finds them.

## The F# oracle — FCS referees fidelity claims (2026-07-20)

PLAN-fsharp-oracle executed on branch fsharp-oracle. CI-side only:
FCS 43.12.204 is a dependency of a separate test project
(tests/Weir.Fidelity) and never approaches the binary; the 7ms story
is untouched. 27 fidelity pins green; both deliberately-wrong-tag
directions proven to fail the build (a bogus divergence id: "missing
from divergences.md"; a Same-tag on the tuple pin: "F# must agree
(weir=Reject, fsharp=Accept)") — captured, then reverted.

Decision archaeology, as the plan directed: the subtractive-fork
detour (fork dotnet/fsharp, subtract down to weir) was REJECTED —
compiler features are not subtractive, divergence must stay cheap
(one artifact row, by intent), and the fork would have inherited
measures the week after weir deleted them (see the removal arc).
The oracle is the salvage: dotnet/fsharp as REFEREE, not substrate.
Shapes only, accept/reject only — semantics, inference, and error
text are out of scope permanently.

Implementation findings:
- GetProjectOptionsFromScript is broken in sandboxed containers (its
  legacy-fsi default references produced a WebClient resolution error
  on EVERY snippet, poisoning all verdicts Reject). Fix: manual
  FSharpProjectOptions with the runtime's TRUSTED_PLATFORM_ASSEMBLIES
  as the reference set — complete and correct by construction.
- FCS is not safe under Expecto's parallelism on one virtual filename
  (nondeterministic verdicts run to run); the oracle is serialized
  behind a lock, cached by snippet hash.
- The |-inertness incident shape is now pin #4, refereed by the real
  compiler: the dedented-arm snippet gets F#'s own offside error and
  weir's needs-a-body — Same(reject), mechanically forever.
- divergences.md seeded from SEMANTICS (11 entries). The audit the
  plan predicted found the list complete — every codebase divergence
  had made it into SEMANTICS — except `no-mutation` and `no-let-rec`
  existed only as prose implications; they are named entries now.

Corpus mining: PARKED at the time-box, with findings. Github egress
works; dotnet/fsharp pinned at 5928e91; sparse checkout of
tests/fsharp/typecheck/sigs (428 files) yielded ZERO snippets after
the mechanical filter — sig tests are module/namespace-shaped, and no
tests/fsharp/parsing directory exists. The real corpus is embedded
strings inside ComponentTests test CODE — extraction is a parser of
its own, which is precisely the plan's park criterion. The oracle's
snippet-hash cache is ready if a corpus ever lands.

## weir fmt — the canonical formatter, v1 (2026-07-18)

User hit `weir fmt file` falling through to "no such script: fmt"
(dispatch had only --qualify) and asked for an actual formatter.
Scoped honestly: v1 normalizes leading indentation (4 per structural
depth, derived from the same pending-let stack the assembler tracks)
and strips trailing whitespace; comments and token spacing are
verbatim (respacing/re-flowing needs trivia-preserving parsing — the
AST has no comments — parked). Column-0 pipe continuations keep their
shell style. `--check` gates CI.

The safety property is the design's spine: format, re-assemble both,
compare logical-line texts — refuse to write on any mismatch. It
tripped on its FIRST real input: original trailing spaces joined into
the logical text (`sum   in` vs `sum in`) — a false positive fixed by
TrimEnd-normalizing both comparison passes (sound: trailing whitespace
cannot sit inside a string at line end; strings are single-line and
close with a quote). A formatter that can prove it didn't change the
parse is the claim-vs-behavior discipline applied to itself.

417 tests; battery +1 pin; skill line added; timing holds.

## Example modernization catches a let-RHS regression (2026-07-18)

Modernizing examples/repo-report.weir to current idioms (let-RHS
porcelain binding, a when-guarded status tier, a streaming command
section, workdir interpolated from cd's return) immediately exposed a
regression the let-RHS session shipped: **`let workdir = cd target`
silently changed meaning** — command-callable `cd` won the RHS head
decision and `target` became a bareword (literal "target"), where the
line had always been expression mode (cd applied to the binding). The
old test pin covered known-name heads (`let x = ls`) but commandCallable
outranks the known-check in the head decision — exactly the shadowing
subtlety the sh-removal amendment warned about, on the other builtin.

Fix: command-callable builtins stay ordinary functions on a let RHS
(builtinHeads flag threaded through the command grammar); only genuine
externals enter command mode there. Old meaning restored exactly;
regression pin added (cd applied to the binding, by AST shape).
411 tests. Lesson recorded: every mode-decision change needs pins for
ALL THREE head classes (external, known, command-callable), not just
the two that seem relevant.

## Part 3: overflow policy + data-range battery; collect renamed (2026-07-18)

Branch overflow-policy, gate waived by the owner (Part 2 pattern).
410 tests; battery +4 AOT pins; timing holds. PLAN-read-booleans-
overflow is now complete except the human READ.md.

- **collect → Seq.toList** (rides ahead of Part 3 as its own commit):
  F#'s Seq.collect is flatMap — a direct agent prior-bleed collision;
  toList is the F# name whose muscle memory matches (weir has no list
  type, so the seq return is the only reading). Frees Seq.collect for
  evidenced flatMap.
- **int literals were silently int32 while runtime was int64** — found
  as an AOT hard CRASH on `weir -e 9223372036854775807` (ParseInt32 in
  the literal path; F# 6 implicit widening had hidden the mismatch at
  compile time). Literals now parse as int64 with a parse-time range
  error beyond 64 bits. The data-range battery's first catch, before
  it was even written.
- Checked +,-,*,/ and Seq.sum raise "integer overflow" (uniform text;
  the Min/-1 division edge rides the same path). Ranges TERMINATE at
  the Int64 boundary instead of raising — yielded values are all
  correct, so termination is honest semantics; pinned.
- DataRange.fs is the permanent layer (boundaries, big strings,
  billion-element laziness); e2e adds >2GB sparse file, 0-byte file,
  Max-literal, and overflow-raise pins against the AOT binary.

## Ledger round 2 — fail, printerr, precedence hint (2026-07-18)

Second same-day fix round from the dogfood ledger. 401 tests;
skill-doc extended (fail/printerr blocks); timing holds.

- **`fail : string -> unit`** — located runtime error, exit 1;
  `if bad then fail $"..."` closes the checking-script gap (the
  cmd-sh-exit-1 workaround retires). Typed unit, not F#'s `'a`:
  weir statements want unit and a bottom-typed var would trip the
  statement rule; match-arm positions restructure instead (documented
  divergence). Joins the runtime-failure inventory in SEMANTICS.
- **`printerr`** — print's stderr twin, revived from parked on the
  ledger evidence; same sentinel scheme and argument rule (the print
  family generalized to two names, one shared checker path).
- **Pipe-into-operator is a targeted error** — operators yield values,
  never functions, so `xs |> f == v` is always wrong; the error now
  says so and names the fix ("parenthesize the pipeline").
- `in` question resolved at review: the token cannot leave the grammar
  (it is the assembler's join token) and -e/REPL need the one-liner
  form; user-typed `in` stays legal, scripts steer to block lets.

## Fix the findings — let-RHS commands, Seq.pairwise, blank-line attribution (2026-07-18)

Same-day fixes for the protocol's day-zero friction trio. 398 tests;
skill-doc blocks extended and green; timing holds.

- **Top-level let RHS admits command mode** (`let files = git ls-files`;
  `| complete` chains bind the record). The probe battery caught a
  cliff the naive version shipped with: the command grammar ate
  `in h` as argv in `let h = git log in h` — the exact in-eating
  hazard that keeps `let ... in` expression-only. Fixed by an
  `in`-bareword stop in the let-RHS arg parser (quote "in" to pass the
  word); statement-head commands keep bareword `in` untouched. The
  splice-defaulting soundness note re-derived and updated (commands
  can now sit under a generalizing let; eager splice binding keeps it
  sound — no lambda can enclose a command line).
- **Seq.pairwise** returns `Pair<'a>` records ({ Fst; Snd }), the
  Group<'k,'v> precedent. TUPLES were considered and deferred at user
  review: tuples without destructuring are worse than records, and
  destructuring means pattern binders in let/lambda — its own plan,
  to be fed by the prior-bleed catalog (the protocol will count
  `(a, b)` attempts). Pair does not foreclose it.
- **Blank-line-in-block error names its cause** ("a blank line ends
  the statement; keep the block's lines contiguous").
- Live task rewritten to its original ask (deltas via pairwise +
  let-RHS command): green in 3 iterations — the two new stumbles were
  anonymous-record prior-bleed (exact error, instant fix) and bareword
  word-splitting (bash-identical, quoted). Both logged.

## Agent dogfooding protocol — setup session (2026-07-18)

PLAN-agent-dogfooding executed on branch agent-dogfooding (off the
unpushed bool-branching tip, per user). No checker surface — runs
during the read week by design.

- **skills/weir/SKILL.md**: ~30 divergence rules, six executable
  fenced blocks (3 must-run + 3 must-fail), ALL verified
  against the AOT binary. Draft corrections found by verification:
  the "bare hot path in scripts" line was WRONG (strict mode has no
  bare aliases anywhere, including command-mode stages — probe:
  `git branch | map trim` errors in a script); `cmd ... |> complete`
  was invented syntax (the expression form is the `completed` builtin);
  the booleans [VERIFY] resolved to landed-reality including the
  non-exhaustive-is-a-hard-error rule.
- **The doc-test harness caught the author's own prior-bleed on its
  first run**: `let files = git ls-files` (the let-RHS command-mode
  gap) written reflexively INTO the skill file whose own commands
  section says it doesn't work.
- ci/skill-doc.sh wired into ci.yml and ci/local.sh. Scripting policy
  in repo-level CLAUDE.md (the shared /output/CLAUDE.md is
  workspace-agnostic by its own rule, so the policy lives in the repo —
  a placement deviation from the plan's letter, recorded).
- **Live task ran end to end** (tools/test-counts.weir): first check
  failed on a blank line inside a lambda block (the F#-divergence
  biting the agent immediately; error accurate but doesn't name the
  blank line — friction-logged); self-corrected in one iteration, no
  doc re-read, no fallback. Metric 1 day-zero: 1/1.
- == archaeology backfilled into SEMANTICS.
- Day-zero telemetry vs the plan's expected-findings list: none of the
  effectful-edge cluster hit yet; actual first hits were let-RHS
  command mode, Seq.pairwise, and blank-line error attribution — the
  last being adjacent to the plan's wildcard prediction (multi-line
  attribution stranding).

## Amendment: exhaustiveness is a hard error (2026-07-18, same session)

User decision at review: non-exhaustive match = type error, always.
390 tests; battery reshaped. Two things the upgrade forced:

- **Coverage went RECURSIVE, not shallow.** As a warning, shallow
  payload coverage (Some true does not cover Some) was fine; as a hard
  error it would reject genuinely-total nested matches — the existing
  nested-Option test failed immediately and proved it. `missingCases`
  now recurses through union payloads; `Some (Some x) | Some None |
  None` is exhaustive. Precision must match severity.
- **The match-failure runtime class is GONE.** Guarded arms never count
  toward coverage, so every accepted match is total; eval's no-arm case
  is now an unreachable, and SEMANTICS' runtime-failure inventory
  shrank by one. The old "still evaluates when an arm hits" and "fails
  at runtime" pins were deleted with it — this NOTES line is their
  tombstone.
- The "match on X needs a catch-all" error arm turned out defensively
  dead: refutable patterns on non-union/non-bool scrutinees are all
  rejected earlier by checkPattern. Kept as defense, noted here.
- Warnings channel now carries advisory findings only (unreachable
  arms); the -e/runner surfacing from earlier today pins on those.

## Bool branching — if/then/else, bool patterns, when-guards (2026-07-18)

Part 2 of PLAN-read-booleans-overflow, executed with the READ.md gate
explicitly waived by the gate owner (user go on 2026-07-18; the gate
remains theirs — flagged, not forgotten). 391 tests; battery +9 pins;
timing holds; tripwires ran with the suite (checker-touching session).

- **EIf is a dedicated checker arm**, not a match desugar — chosen for
  error quality: "an if without an else is unit-valued; this
  then-branch is string — add an else" beats a synthesized-unit-arm
  mismatch. Row merge across branches is the match-arm discipline
  (else checks against then's inferred type) — pinned including the
  subtlety that a branch-merged row conflict (Name vs Bytes at one row
  var) is LEGAL pre-discharge and errors at discharge; my first pin
  expected the error too early and the checker was right.
- **Guards**: arms are (pattern, guard?, body); guard checks bool under
  the arm's bindings; guarded arms never count toward exhaustiveness or
  reachability. Grammar: `-` gained notFollowedBy '>' so guard
  expressions can precede `->` — a latent OPP collision that only
  guards exposed.
- **Bool patterns** pre-bind an unresolved scrutinee (defaulting
  precedent); exhaustiveness knows true/false coverage.
- **Found: warnings were silently dropped by -e AND the script
  runner** — only the REPL surfaced them. Exposed by a warning-less
  non-exhaustive bool match in -e. Fixed in-session: both surfaces
  print located warnings to stderr; warnings never block execution.
  e2e-pinned. (Pre-existing since scripts landed.)
- **Shadowed-cd hint fixed** (the Part 2 rider): command-callable heads
  never fall to expression mode, so the hint now excludes them instead
  of claiming "expression mode" for cd lines.

**Dogfood ledger** (user directive: weir over python for session
tooling; friction = findings):
- Used weir scripts for: the bool-branching smoke battery, and a
  doc-staleness probe (File.read |> where (Str.contains ...) — reads
  naturally). Both worked first try apart from findings below.
- FRICTION 1 — precedence trap: `if xs |> Seq.length == 2 then` parses
  as `xs |> (Seq.length == 2)` (`==` binds tighter than `|>`,
  F#-consistent). The error surfaces as "expected seq<'a> -> int" at
  the pipe — correct but puzzling. Candidate: a targeted hint when a
  pipe RHS is a comparison of a function. Filed, not fixed.
- FRICTION 2 — no `fail`/`exit` builtin: a checking script cannot
  deliberately exit nonzero with a message; the workaround
  (`if bad then cmd "sh" ["-c"; "exit 1"] |> print`) is a paragraph
  where `fail "reason"` is the want. First-order candidate for the
  next library session; `fail : string -> unit` raising a located
  runtime error would also serve `printerr`'s parked use case.
- FRICTION 3 — python still required for: multi-line context-sensitive
  file surgery (no multi-line strings, no regex — regex is already
  parked as its own plan). Single-line filters/maps are fully covered.

## Remove measures — the evidence-standard case study (2026-07-18)

Units of measure are removed from weir. This entry is written BEFORE
the deletions (plan order) so every deletion references it; it is the
project's best case study of the evidence standard and should be
readable in one place.

The full arc, dated:
- Spike 2: measures land; `f.Size > 1<mb>` is the acceptance test and
  the UoM showcase. Advisor calls extensible measures "the single most
  compelling reason to do this over Nushell" — a claim the record never
  backed; logged here as advisor error.
- Rules doc: measures are nominal exact-match tags; no algebra;
  `f.Size * 2` a type error, flagged as "known ergonomic cliff, top
  backlog item."
- Measure algebra CANCELLED (library-phase review): zero dogfood
  findings in the ledger; the cliff never materialized. The
  `no_unit_algebra` tripwire made permanent (now retired below).
- ls-Size-always-0 bug: the `int<mb>` field truncates sub-megabyte
  files to zero — the measure *causes* a wrong-answer incident and
  forces the two-field Bytes/Size workaround. First shape change driven
  by dogfooding, and it cut against the feature.
- `Seq.sum` ships bare-int-only (no measure variables exist); measured
  ints silently excluded from aggregation.
- Range literals session, stop-and-report: `[1<mb>..3<mb>]` needs a
  Ty-level measure variable — a new variable kind through every audited
  checker arm. Third checker customer for machinery serving a feature
  with zero organic usage.
- 2026-07-18: grep standard applied — measure literals outside tests
  approximately zero — REMOVED. The queued measure-variables
  re-blessing is cancelled with a pointer here.

What this entry is the tombstone for (retire-loudly rule): the
`no_unit_algebra` tripwire, the gb-vs-mb conflict pin, every §4
checklist row and its tests, the measured-range pins from the range
session, and FileRow.Size (the truncating field the incident created —
`Bytes`, bare int, is the survivor; field names now carry quantity
semantics).

The named successor question: `f.Bytes > 1048576` will feel worse to
write than `f.Size > 1<mb>` — that feeling is DATA, the display/
conversion want surfacing honestly as an ergonomic gap instead of
hiding behind a type tag. Evidenced answers are cheap and type-free
when dogfooding demands them: multiplier builtins (`mb : int -> int`)
or underscore literals (`1_048_576`). Neither ships with the removal.

Consequences named: the pitch loses "extensible typed measures" and
keeps the stronger claim (sound static HM+rows, zero runtime type
checks, in a shell); `sortBy`'s runtime scalar-key check is again the
ONLY qualified-types customer, and the type-class conversation returns
to parked-until-customers with this arc as the precedent for
speculative checker machinery; the read anchor moves to the
post-removal commit and TRANSCRIPTION.md regenerates once, smaller
(§4 retires; measure cases vanish from bind; the splice rule
simplifies).

## Range literals (2026-07-18)

Mini-plan session (ranges taken from the comprehension decomposition;
for-yield sugar and Seq.collect stay parked with reopen criteria in the
plan). 375 tests; battery +3 pins; timing holds. No checker arm added —
the literal desugars to a `Seq.range` application before checking, as
the plan modeled.

**Deviation, stop-and-reported mid-session**: the DECIDED
measured-ranges bullet ("measure-polymorphic via the same scheme shape
as other Seq members") assumed machinery that does not exist. A `∀a`
scheme would also accept strings — weir has no measure variables or
int-constrained type variables — and the actual library precedent is
`Seq.sum : seq<int> -> int`, bare-only for exactly this reason. Shipped
the sound subset: `Seq.range` is monomorphic bare int; `[1<mb>..3<mb>]`
is the ordinary measure-mismatch error, pinned as a named limitation.
Measured ranges need re-blessing with either a Ty-level measure
variable or a checker arm.

Session decisions the plan left open, as resolved:
- Range-vs-list disambiguation is a bounded backtrack: `attempt` over
  the first simple term + `..` — one term deep, not exponential; noted
  per the plan's lookahead-vs-backtrack question.
- Zero literal step errors at PARSE time (failFatally in the desugar),
  slightly earlier than the plan's "check-time" — same guarantee
  (before anything runs), reported for precision.
- The negative literal needed for descending steps exists ONLY in
  range positions (`negIntLit` in rangeTerm) — no unary minus leaked
  into the general grammar.
- `..` never touches command mode (expression-land parser only);
  `cd ..` composed with a range in the same script is e2e-pinned.
- Field access in endpoints wraps fieldSuffix in `attempt` so the
  first dot of `..` is never eaten as a field dot — the `1.` /
  `1..` boundary the plan flagged (`1.` remains an error shape,
  pinned by the F#-negatives).

## Fix: |-inertness is statement-level only (2026-07-18)

Corrective session per the blessed fix plan. 358 tests; battery +2
pins; timing holds. Not a checker change (assembler only) — no tripwire
re-run needed; standard suite + battery + timing are the coverage. The
assembler diff came in at 4 lines against the plan's ~2-line model
(the `|` branch needs its own two-case match: error at-or-left, plain
when deeper) — within the ≤10 stop-and-report budget.

Decision archaeology, as blessed: the block-lets session shipped
`|`-headed lines as unconditionally inert to the pending-let stack,
justified by "match-as-let-body works with canonical arm style for
free." The justification rested on an invalid example — arms dedented
to or past their binding's indent, a shape F# *rejects*. The valid F#
shape has every arm deeper than the pending `let`, which the plain
indent rule already handles with zero special-casing: block bodies
never needed `|`-inertness at all. As shipped, the rule over-accepted a
join F# would reject at parse — cutting against the F#-fidelity
direction chosen in the same session. Caught by the user knowing F#'s
actual grammar; advisor drift, not implementation drift.

Corrected rule: `|` is inert only while the stack is empty (the two
statement-level customers: column-0 pipeline continuations under a
command line, column-0 match arms outside any binding). With a binding
pending: deeper = plain continuation; at-or-left = needs-a-body naming
the binding line.

**Process rule generated**: pin batteries for grammar/assembler rules
must include F#-rejects-this NEGATIVE cases, not only weir-accepts-this
positives — invalid-example drift is invisible to positive-only
batteries. The six pins of this session are the template.

## Block lets — F# light syntax at the assembly layer (2026-07-18)

User decision closing the `let ... in` thread (opened during the spikes,
kept then; reopened at the interpolation review): **do it like F#** —
implicit `in` at the offside boundary, not the austere removal. 353
tests; battery +2 pins; timing holds.

- **Zero parser/checker/evaluator changes.** F# implements light syntax
  by token insertion in the lexer; weir's logical-line assembler is the
  same layer. A continuation line starting with `let ` pushes a pending
  binding (indent, line); the next line at the SAME indent closes it by
  joining with " in " instead of " ". The single-line grammar sees the
  explicit form; ELet and envFreeVars serve unchanged — the austere
  option (kill ELet, kill envFreeVars) was assessed and set aside in
  favor of F# fidelity; that analysis is on record in the conversation
  and the read shrinkage it promised is forgone knowingly.
- Stack invariant: pending indents strictly increase, so each closing
  line pops at most one binding; a dedent past a deeper pending let is
  the "needs a body" error naming the deepest line — same verdict F#
  gives the shape.
- `|`-headed lines were shipped unconditionally inert to the stack —
  corrected the same day (see the fix entry above): inertness is
  statement-level only; block bodies are handled by the plain indent
  rule.
- **Span translation needed nothing**: Segments already carry
  per-segment joined offsets; " in " just makes the offset arithmetic
  4 instead of 1 (`typo at 2:24` pins it).
- Explicit `let ... in` stays legal — F#'s verbose-syntax analog and the
  only binding form in the line-based REPL/-e. Blocks are bindings + one
  result expression; effect-sequencing inside blocks (ESeq, unit-checked
  non-final lines) is backlog #0, revive on dogfood demand.
- Blank-line-ends-statement is retained and now documented as a named
  F# divergence (a blank inside a block is a "needs a body" error).

## Review amendments: [ heads nothing; the sh builtin is gone (2026-07-18)

Two user decisions at review of the unit-print session. 346 tests;
battery reshaped (+3 pins, −1 obsolete); timing holds.

- **`[` never heads a command.** The capture bug (line-head string list
  → bare `[` token → /usr/bin/[) is fixed at the head rule: `[`-initial
  words fail to expression mode; `^[` is a hard error naming
  `cmd "[" [...]`. `[` stays ordinary inside arguments (`[m]arker`).
  Pinned with realResolver (real PATH) and in e2e with the natural
  spelling that used to break.
- **The `sh` builtin is removed.** The statement rule had exposed it:
  a library function pretending to be a shell — bare effect lines
  needed `|> print`, `| complete` could never reach it, and it deferred
  resolution past check-everything-first. The external `/bin/sh` does
  everything with zero special-casing: `sh -c "..."` is command mode
  (exempt, streaming, completable — `sh -c "exit 7" | complete` works
  now, which the builtin structurally never could); expression
  positions use `cmd "sh" ["-c"; "..."]`. 27 test sites migrated
  mechanically; the parked "effect-only sh ergonomic" demand dissolves
  (effect lines are command mode now). One migration self-inflicted
  wound: the sed-style regex mangled `./build.sh \"--flag\"` inside an
  *expectation string* — the sed-migration lesson from the module
  session, relearned on a smaller stage.
- SEMANTICS updated with decision archaeology in place (hatch bullet,
  dead completed-boundary bullet removed, statement-rule clause
  superseded-noted, `[` rule added).

## Unit, print, and the statement rule (2026-07-18)

PLAN-unit-and-print Session 1, complete. 346 tests; battery +9 pins;
timing pins hold (7/20ms medians).

- **`unit` cost what the plan predicted**: a leaf. TUnit/VUnit/`()`/
  `unit` tySyn, equatable, invisible interactively (REPL, `-e`, and
  `let x = ()` all show nothing — pinned in e2e). No read-path arm
  touched; TRANSCRIPTION got the addenda.
- **The statement rule**: parser reifies its mode decision as
  `SCmd`/`SExpr` (new Stmt case — the classifier is a pattern match, so
  the removed form-2 exemption *cannot* be reintroduced without a
  parser change), and the runner's `discardError` gates pure statements
  on unit. `| complete` chains classify as commands for free because
  classification happens at parse time, before the desugar to
  `completed` application.
- **print**: sentinel-scheme guard (∀__print. __print → unit is
  unforgeable — ctx names are aN/rN) gives bespoke typing in applied/
  piped positions and defaulted `string -> unit` as a bare value;
  shadowing falls through to normal rules by construction. Renderer
  byte-identity with the retired statement printer is by shared
  function (`Eval.writeLines`), pinned by the adversarial e2e case
  (empty strings, embedded newline).
- **`File.write`/`append` → unit**; the path-return stopgap retired.
- **Found, not fixed (pre-existing, out of session scope): line-head
  string lists enter command mode.** `["a"; "b"] |> print` at a line
  head: quotes end the bareword, the head token is bare `[`, and
  `/usr/bin/[` is a PATH hit → ECmd. Ints escape (`[2;` fails the PATH
  probe). Migration workaround: `let`-bind the list. Needs a decision:
  may `[` head a command? (bash's `[ -f x ]` idiom vs list literals.)
- Effect-only `sh` lines (`sh "mkdir x" |> print`) read as predicted —
  noted in the plan as the first candidate demand for the discard
  hatch; no dogfood complaint yet.

## String interpolation (2026-07-18)

User-requested off-plan feature (dogfood follow-up to the example script's
`echo tracked changes: (...)` bareword question). `$"... {expr} ..."`,
F#-style. 331 tests.

- **One rule, two splice kinds**: holes reuse the command-splice typing rule
  verbatim — str/int/bool, unresolved defaults to string — via a shared
  `checkScalarSplice` extracted from the ECmd arm (behavior unchanged there).
  Eval-side the same consolidation: one `scalarString` renderer now serves
  both command argv and holes (was duplicated at two TECmd sites).
- Works as a command argument (`echo $"n={x}"` is one argv entry, never
  re-split) — cmdArg tries `interpLit` before `spliceVar` so `$"` isn't eaten
  by the `$name` path. `{{`/`}}` escape braces; no format specifiers.
- `$"{n}"` closes the filed int→string gap; the example script now uses
  interpolation throughout (including a `Dirty of int` payload rendered in a
  match arm).
- Checker arm added post-read-anchor: TRANSCRIPTION.md gained a "post-anchor
  addenda" section so the pending READ.md scope stays exactly d12aefd.
- e2e battery: interpolation + brace escapes, and the one-argv-entry pin,
  against the AOT binary.

## Read prep — transcription, read order, composition probes (2026-07-17)

Part 1 prep of PLAN-read-booleans-overflow, complete. 317 tests.

- **TRANSCRIPTION.md**: judgment-form rule per checker arm, anchored to d12aefd file:lines. Five arms flagged as resisting single-rule transcription — these are pre-read findings per the plan: (1) `instantiate` does two jobs (rename + Rows installation); (2) `dischargeRow`/`mergeRows` substitute-before-recurse is load-bearing for termination and gained a substParams premise in the generics session; (3) `checkSpine` braids three jobs around the piped-first semantic core; (4) the EField TVar-upgrade arm has a side-effectful premise; (5) the generalization computation exists at two code sites (infer-ELet and check-ELet — drift risk, not unsoundness).
- **READ-ORDER.md**: the a–g path with per-step purpose, the three formally-reopened checklist items (§1.1 occurs-through-TNamed, §1.5 the == fix as rule-not-patch, §3.1/§3.3 freshening now covering ctor schemes and module members), and the verdict protocol ending in READ.md.
- **Composition probes** (six, all green): generic-ctor-in-row discharge + conflict; envFreeVars through TNamed-inside-row (the audited×audited composition); occurs through two constructor layers under a row field; module-member freshening in one expression; Option-field row deep-copy across sibling discharges; and the advisor-flagged short-circuit gap resolved — pins existed (div-by-zero proxy, operators session) and a real-process spawn-count pin is now added beside them.

The gate is now the human's: Part 2 (booleans) starts when READ.md exists.

## Dogfood sweep — assistant-driven error hunt (2026-07-17)

Four probe batches against the AOT binary. Fixed this session:

- **int is now int64 end-to-end** (the big one): weir ints were int32, so `ls` on a >2GB file reported negative `Bytes` and `where (fun f -> f.Bytes > 0<b>)` silently returned zero files — wrong answers on exactly the data the filter exists for. Verified fixed: 3GB file → `3221225472<b>`, filter finds it, `2147483647 + 1 = 2147483648`. Sweep was mechanical (F# literal inference absorbed nearly all sites).
- Nested union display gains parens: `Some (Some (Some 1))`, not `Some Some Some 1`.
- `cmd "" []` reports "cmd: empty program name" instead of leaking a raw .NET exception.
- The fmt e2e entry asserted on filesystem enumeration order (`.gitignore` first) — latent flake exposed by adding files; now deterministic.

**Filed, not fixed (language decisions, not sessions)**:
- **No boolean branching exists**: no `if`/`then`/`else`, and `true`/`false` are not legal patterns (`match b with | true -> ...` is a parse error). There is currently no way to branch on a bool. Biggest known language gap; needs a chosen design (if-expression vs bool patterns vs both) — top of the next plan's input.
- Shadowed-`cd` diagnostic claims "expression mode" on a line that actually took the command-callable arm — minor hint inaccuracy.
- `Str.split "" s` returns `[s]` (.NET semantics) — fine, but undocumented.

Verified healthy: unicode strings, CRLF scripts, `/dev/stdin` as a script, `stdin` builtin one-shot, empty-everything edges, negative take, match-arm shadowing of builtin names, `Seq.groupBy` over row projections, `into`, porcelain garbage errors, `cd`-to-file error.

## Dogfood fix — ls Size was always 0 (2026-07-17)

Diagnosis: `Size : int<mb>` = bytes ÷ 1,048,576 truncated — the Spike-2 decision that made `f.Size > 1<mb>` work against real files; sub-megabyte files (i.e. nearly everything in a source tree) round to 0.

Fix shape, constrained by the measure system: conversion doesn't exist (measure algebra dropped), so a field's unit is permanent. Switching Size to bytes would kill the UoM showcase filter. Instead FileRow gains `Bytes : int<b>` as ground truth alongside the coarse `Size : int<mb>`; `ls` populates both from the real file length. Field-set-sensitive fixtures updated (from-json fixtures, to-json output, completion expectations — record literals and adapters match exact field sets). 308 tests, e2e green.

First shape-change driven purely by dogfooding — and a note for the future measure-conversion discussion: this two-field workaround is exactly the pattern conversion would delete.

## Modules Session 3 — the multi-line gate: CONTINUE, and shipped (2026-07-17)

308 tests; e2e + timing green. Gate verdict in DESIGN-multiline.md; the kill criteria were beaten so thoroughly the implementation shipped in the gate session.

- **The reframe that won**: no expression-level offside machinery — weir's line-oriented grammar means indentation-based multi-line is *logical-line reconstruction*: a ~90-line script-runner pre-pass joins continuations, and the existing parser consumes each logical line unchanged. Kill criteria results: parser-lines changed **0** (criterion: <150); expression suite green by construction; mode-decisions-in-continuations structurally impossible; timing unchanged.
- **Gate finding from the first live script**: the indent-only rule missed F#-canonical match arms (`| Some n -> ...` at column 0). Fixed on principle, not special case: no statement can begin with `|`, so pipe-headed lines are unambiguously continuations at any indentation — which also admits shell-style unindented pipeline continuations.
- **Error mapping is the real feature**: per-segment source tracking translates type-error spans to physical `file:line:col` — `multibad.weir:3:18: type error: FileRow has no field 'Nmae'. Did you mean 'Name'?` points at the continuation line, not the joined blob. Parse errors attribute to the head line (documented limitation, parked with REPL continuation prompts).
- Documented non-features: in-less nested `let`, indentation-as-scope, multi-line REPL. `let x = <command>` remains expression mode — orthogonal decision, unchanged.
- Session 4 as planned is dissolved: its scope was consumed here; the residue (parse-error column mapping, REPL continuation) sits on the parked list for dogfooding to prioritize.

## Modules Session 2 — script runner, strict-by-default, fmt --qualify (2026-07-17)

303 tests; tripwires green; e2e battery grown by five script entries, all green on the AOT binary; timing unchanged (7ms/18ms). weir runs shebang scripts.

- **Check-first works and is pinned**: touch-then-type-error script leaves no file, exits 1. The install-then-use divergence and its sh escape hatch are in SEMANTICS.
- **Strict by default** via a second builtin TypeEnv (bare aliases removed; modules/session/process names stay); `#loose` opts out; misplaced directives error with location. The multi-home moved-name hint ("use Option.map or Seq.map") landed as part of strict-mode ergonomics.
- **In-session decisions, documented**: shell-shaped statement output (strings/string-seqs raw — scripts compose with pipes; let/type silent); stdin inherit-unless-consumed (already cmd's behavior — stated, not built); `args`/`stdin` script-only.
- **fmt --qualify**: span-precise AST rewrite (EVar spans, right-to-left per line), splices and fields untouched, `#loose` dropped on success — the single-home guarantee (trial resolution deferred, Option excluded) makes it a table lookup, no type direction needed. Live: 8 names qualified across expression and command-mode segments, output runs strict-clean.
- Comment stripper is string-aware (`//` inside quotes preserved) and unit-tested; applied at the script boundary.
- Fmt splice guard was off by one on first cut (spanned wraps the `$`); caught by the battery.

## Modules Session 1 — builtin modules (2026-07-17)

297 tests; tripwires explicit-green; e2e + qualified-pipeline entry green on the AOT binary. `Seq`/`Str`/`Option` live; the migration commit landed in one dose.

- Mechanism as designed: `TypeEnv.Modules` (member schemes), one `EField` arm (value-shadow checked first, instantiate on hit), mangled flat names at runtime — eval untouched. Bare-module and unknown-member errors carry guidance; exact-name moved members hint their qualified home.
- **Caught my own version of the deferred conflict**: the bare-alias derivation initially collected from all modules, so `Option.map` silently overwrote bare `map` (Map.ofList last-wins) — precisely the collision the plan deferred trial resolution to avoid. The Option-qualified-only rule now applies to the derivation itself; the failure mode was 31 red tests, instantly visible.
- Three-way precedence pinned exactly as the advisor specified (`let Seq = {...} in Seq.map` → ordinary field error). Completion gained the module branch (`Seq.<TAB>`); resolvers/diagnostics treat module names as known (mode decision: `Seq` at line head is expression mode).
- Bonus from the smoke: qualified members work as command-mode pipe segments (`git branch | map trim | ... | Seq.length`) — the dot makes the head non-ident-like, falling through to the expression segment and the module arm. Free, but now observed and welcome.
- `Seq.length` added (didn't exist); `length` qualified-only per the interim rule.

## Library Session 3 — the Option sweep (2026-07-17)

288 tests; e2e + timing green on the AOT binary. Quick session, as planned.

- `tryHead : seq<'a> -> Option<'a>` and `tryToInt : string -> Option<int>` — the breaking change taken cleanly (dogfood history had zero uses; the two test sites migrated).
- Deferred Option customers landed: `tryFind` (data-last), `tryIndexOf`, `substring start len subject` (raising, bounds in the message).
- Helpers per the plan's recommendation: `defaultTo` and `mapOption` — the idiom's other half. Pinned verbatim: `ls |> tryFind _.ReadOnly |> mapOption _.Name |> defaultTo "none"`.
- SEMANTICS partiality convention flipped from INTERIM to FINAL: raising name / try-prefixed Option sibling. The 0-or-1-seq idiom retired without ever becoming case law — the interim marker did its job.
- Note for the record: session based on the local session-2 tip because this container has no ssh (origin/main updates have always come from the user's shell); squash-merge content-equivalence makes the PR diff come out right.

## Library Session 2 — generic unions and records (2026-07-16)

281 tests; tripwires explicit-green; e2e + timing green on the AOT binary (7ms — generics cost nothing at startup). Option/Result live as prelude declarations; `groupBy` landed as the generic-records validation customer.

**The finding that matters for the read**: the adversarial battery caught `==` violating its own spec. SEMANTICS has said "unifies operands first" since the operator session — but the implementation compared resolved types syntactically (`a = b`), which passed monomorphically because top-level vars got pre-bound by the retry cases. `Option<'a> == Option<int>` exposed it instantly. Fix: genuine unify-then-equatable. Fourth claim-vs-behavior instance, and the first where the claim was in our own spec rather than a plan — the doc was ahead of the checker.

Design decisions (per plan, plus in-session):
- Representation unified: `TNamed of name * args` (no parallel TApp); `seq` stays structural-builtin. Defs gain `Params`; one `substParams` helper feeds patterns, fields, discharge, and equatability.
- **Records came along** — Session 1's groupBy deferral was the demonstrated need; record literals freshen params per literal and infer args by unification.
- **Deviation from the plan's illustrative grammar**: no tuple payloads (`Case of 'a * 'b`) — weir has no product types; single payload, wrap in a record. Documented.
- Constructor schemes = ordinary `Scheme` with Forall = params; instantiation is the existing freshen-on-use machinery, so the §3 reopening rides audited code (pinned: `let s = fun x -> Some x` used at int and string; polymorphic `None` at two instantiations; occurs through constructor args → infinite-type).
- Prelude = weir source strings through the normal decl path, embedded in the binary (files would break the 6ms single-file story).
- `from json`/`to json` guard: monomorphic records only.

**Human-read targets, unchanged**: the `TNamed` pairwise arm in `bind` + `substParams` call sites, and the constructor-scheme construction in `checkDecl` — judgment-on-paper per plan.

## Library Session 1 — strings, tryHead, sortBy (2026-07-16)

265 tests; e2e battery + timing guard green on the AOT binary; the done-when dogfood task runs natively: `git branch | map trim | where (startsWith "feature") | join ","` — point-free, exactly the data-last payoff the plan pinned.

- 14 string builtins, all data-last (needle first, subject last), `strLen` per the collision decision. `split` keeps empty entries (documented).
- Seq additions: `tryHead` (interim 0-or-1 seq, marked for Option migration in SEMANTICS), `isEmpty`, `sortBy` (lazy via Seq.delay; scalar keys only, enforced at runtime and documented — no comparability constraint exists in the type system).
- **Scope finding: `groupBy` deferred with reason** — its return shape `{ Key: 'b; Items: seq<'a> }` requires generic records (Session 2 machinery); RecordDef fields are concrete types today. A string-keyed fake was rejected as case law in the wrong direction.
- Zero checker changes, as planned. Member-access-on-primitives logged as a candidate, not built.

## Session 3 addendum — advisor probes + institutionalized e2e rule (2026-07-15)

- **Deadlock probe (highest-value check available): PASSED** — 100KB stderr before stdout closes, under `| complete` → returns promptly. The concurrent drain (stderr Task starts before the stdout loop in `Proc.complete`) is now empirically confirmed and pinned in ci/e2e.sh with a timeout guard.
- **Composition probe: clear error, not silent** — `yes hi | grep hi | complete` hits the marker rule's parse error. Erroring over wiring stays the choice; pinned for the ext→ext shape specifically.
- **Standing rule institutionalized** (three claim-vs-behavior gaps: porcelain quoting, ext→ext piping, stderr passthrough): every done-when grammar shape or boundary behavior gets an eval test in ci/e2e.sh against the AOT binary. Five new e2e entries this session.
- **Doc lines added**: complete/collect force completion (`yes | complete` hangs by design); sh-streams can't be completed (sh = POSIX semantics at the price of POSIX error opacity; `shc` is the future shape if demanded); commit-to-command-mode as a stated grammar rule.
- **Process note for the record**: this addendum's first probe run tested the wrong code — the working tree had silently switched to main (session-2 state) between turns, and `publish.sh` faithfully installed it. Both probe failures were phantoms. Cost: fifteen minutes; lesson: `git log -1` before trusting any installed-binary probe.
- Also discovered while probing: `\0` isn't a weir string escape, so shell one-liners embedding octal escapes need restructuring — fine, but the parse error could name the offending escape. Micro-item for the dogfood list.

## Ergonomics Session 3 — complete, collect, and the stdin gap (2026-07-15)

251 tests, tripwires explicit-green. Three findings beyond the plan's scope, all fixed:

1. **ext→ext piping never type-checked** — command-mode session 3's battery pinned `git log | grep x`'s *parse shape* but nothing ever checked or evaluated one; `ECmd` had no stdin path ("right side of a pipe must be a function"). Fixed: dedicated `EPipe`-into-`ECmd` rule (left stream must be `seq<string>`), eval wires it into the child's stdin. `yes hi | cat | first 2` now works. Lesson repeated from the depth audit: parse-shape tests are not behavior tests.
2. **stderr was never passthrough** — the plan said "keep passthrough as default," but the implementation redirected stderr and only read it after stdout EOF: swallowed on success, and a latent deadlock (chatty-stderr child fills the pipe weir isn't reading). Now genuinely passthrough; failure messages lost the stderr suffix (it went to the terminal live instead — better).
3. **`failFatally` inside `attempt` is a no-op** — the marker-misuse diagnostic vanished because `stmtWith` wrapped the command line in `attempt`. Fix doubled as UX: once the first segment parses as a command, the line commits to command mode (head-decision fallback still backtracks cleanly), so command-line errors read as command-line errors.

The headline features:
- **`complete`/`completed`**: fallback design chosen and documented — command-suffix desugar to a plain builtin call (`grep x f | complete` → `completed "grep" ["x"; "f"]`), NOT a type-level process-backed-stream distinction (which wouldn't survive `where`/`first`). Zero checker changes. `Completed = { ExitCode; Stdout; Stderr }`, never raises; `grep nomatch f | complete |> _.ExitCode` → 1. Stderr captured only here.
- **`collect`**: eager materialization at application; pinned by the inverted liveness test (pwd snapshot survives cd) and a spawn-count test (1 spawn with collect, 2 without).

Backlog after this session: measure algebra is the last standing item.

## Ergonomics Session 2 — command-callable builtins + cliff diagnostic (2026-07-15)

`cd /work`, `cd ..`, `cd ~`, bare `cd` all work at the prompt; `ls -la` now yields a targeted hint instead of a bare subtraction error. 239 tests, tripwires re-run explicitly (green).

- **Design delta vs plan, in weir's favor**: the plan expected the checker to gain a builtin-desugar arm; in our architecture the desugar lives in the *parser* (builtin head + barewords → ordinary `EApp` with string literals), so splice typing and checking are inherited for free and the checker's only change is the arity-message improvement ("'cd' takes at most 1 argument(s), but got 2" — computed generically from the head's type, so `double 1 2` got better too; adversarial test updated).
- Mode decision gains the one arm, ordered before the known-name check (cd IS a builtin): forced → external; command-callable → builtin segment; known → expression; PATH hit → external; else fall through. Conservative-by-construction preserved.
- Bare `cd` desugars to `cd "~"` (case law while the set = {cd}); `^cd` verified absent as an external on Ubuntu → parse-time command-not-found, pinned.
- **Cliff diagnostic** (`Diagnose.fs`, pure, unit-tested): fires only on parse/check failures — the first smoke run exposed a false positive on `cd /wrok` (a *runtime* error on a valid command-mode line claiming "this line is expression mode"); gated on span presence. Bareword tails only hint when the head also exists in PATH (so `where p` stays quiet, a shadowed `git` doesn't).
- Human-read target: the head-decision function in `commandSegment` (Parser.fs) — one new arm.

## Ergonomics Session 1 — CI + hint hygiene (2026-07-13)

- **CI workflow** at `.github/workflows/ci.yml` (GHA syntax; Codeberg's Forgejo Actions reads `.github/workflows/` as fallback — move to `.forgejo/workflows/` if the runner wants it): test suite twice (flake detection — the workload that caught the Session-3 Session.Cwd race), AOT publish via `publish.sh`, then `ci/e2e.sh` + `ci/timing.sh` against the native binary.
- **`ci/e2e.sh`**: the Session-4 battery as a script (expression eval, argv-literal, splices, cd+porcelain+staged on a temp repo, `^ls`, forced-unknown rejection). Shared by CI and local runs; verified green locally.
- **`ci/timing.sh`**: pins the Session-4 medians (expression ~6ms, spawn ~14ms) with thresholds 18/42ms (>2x pinned + CI-runner headroom, env-overridable). Done-when verified both directions: green on the real binary, **trips on a deliberate 20ms-wrapper slowdown** (33ms > 18ms → exit 1).
- **Did-you-mean cap**: audited — all seven hint sites (fields, ctors, unbound vars, unknown types x2, decl types, PATH) route through the single `Types.didYouMean` with the <=2 filter; pinned by two new tests (checker-side and PATH-side distance-3 names get no hint).

224 tests. CI itself can't be exercised from this container — first push will tell; the scripts it runs are verified.

## Command-mode Session 4 — integration, timing, dogfood re-entry (2026-07-12)

**E2E on the AOT binary**, all green: `cd` into a temp repo; `git status --porcelain | from porcelain | where _.Staged | map _.Path` → exactly the staged file; `^ls` forces past the builtin; `let pattern = "more"` then `grep -l $pattern a.txt b.txt` splices a REPL binding into argv.

**Timing pinned — and one real cost found and removed**: the naive PATH check enumerated every PATH directory on any line whose head wasn't a known name, which taxed even `1 + 2` (head "1" isn't ident-like) at +10ms. Fixed: mode decision now uses `File.Exists` probes per PATH entry (microseconds); the full inventory is enumerated only for did-you-mean hints. Post-fix on the AOT binary: expression lines 6ms median (parity with pre-command-mode), command-mode `echo hi | first 1` 14ms median — spawn-dominated, weir overhead is noise.

**First dogfood finding** (queued as SEMANTICS backlog #3): "nonzero exit raises" collides with grep's no-match-exits-1 convention — a zero-hit filter is currently a runtime error. Policy needed (allowlist / try-combinator / exit-code-as-value), chosen not improvised.

plans/PLAN-command-mode.md complete: all four sessions done. 222 tests.

## Command-mode Session 3 — the mode decision and command grammar (2026-07-12)

Bare `cd` (via `cd "path"`... the *original* two lines) and `git status` now work at the prompt; `git status --porcelain | from porcelain | where _.Staged` is one typed line. 222 tests, expression mode zero regressions (old parseStmt = parseLine with a no-externals resolver — behavior identical by construction).

**Architecture — the parser stays pure**: `parseLine` takes an injected `Resolver` (`IsKnown` from the live env, `IsExternal`/`ExternalNames` from the `Extern` PATH cache), so the mode decision — the new security boundary — is one small `commandSegment` head function over injected facts, unit-tested against a fake PATH. REPL calls `Extern.refresh()` per submission (mid-session installs visible); completion reuses the cache (no per-keystroke stat).

**Mode decision is conservative by construction**: only a PATH *hit* enters command mode; keywords/bindings/builtins shadow PATH (`ls -la` parses as subtraction and fails in the checker — pinned as the shadow demonstration); every other shape falls back to expression parsing, so weird heads (`[1]`, `1 + 2`, quotes) can never accidentally exec. `^prog` forces PATH; a forced miss is a parse-time "command not found" with a PATH-based did-you-mean (cap ≤2 was already in place — the plan's one-liner was a no-op, verified rather than fixed).

**Grammar**: segments split on `|`/`|>` (both = pipe in command mode), each segment re-entering the mode decision — `git log | grep x | first 2` flows external→external→expression. Expression segments parse with a pipe-free OPP (the dual-OPP pattern returns, but without the match-arm ambiguity this time — segment pipes and expression pipes mean the same thing, so the split is semantically invisible). ECmd desugars to a checked node typed `seq<string>`, evaluated via Session 2's `Proc` machinery (extracted from Builtins to break the Eval→Builtins cycle) — lifecycle guarantees inherited, pinned by a command-mode survivor test.

**Splice typing rule** (the other human-read spot): args infer, then must resolve to string/int-any-measure/bool; unresolved defaults to string; rendered as single argv entries. Records/seqs/functions rejected with "command arguments must be strings, ints or bools". Real-exec pinned: `echo hi (1 + 2) true` → "hi 3 true"; `echo ; rm -rf x` emits the string.

**Flake found and fixed**: `Session.Cwd` is global mutable state and Expecto parallelism raced cwd-mutating tests (a parallel finally-reset landing between another test's `cd` and its spawn) — exactly the concurrency seam the plan flagged when it said mutate `Session.Cwd`, not `Environment.CurrentDirectory`. The cwd-mutating and process-census test groups are now `testSequenced`; suite ran 3× green.

**Tripwire suite re-run explicitly** (first checker change since the audit — the ECmd rule): green.

SEMANTICS.md: new "Command mode" section — mode-decision algorithm, grammar, splice rule, PATH staleness rule, and the exclusions list (globs/redirects/env-prefix/chaining pass through literally; `let`-lines are expression-only; expression mode never flows back into command mode).

## Command-mode Session 2 — sh/cmd split, Session.Cwd, cd/pwd (2026-07-12)

- `sh : string -> seq<string>` (renamed escape hatch, 17 test sites migrated) vs `cmd : string -> seq<string> -> seq<string>` (direct exec, argv verbatim, no injection class — done-when pinned: `cmd "echo" ["*"]` literal, `sh "echo *"` globs, injection arg inert).
- **Plan-vs-language deltas, resolved in the plan's spirit**: (1) `cmd`'s arg vector required *seq literals* — added `[a; b; c]` (homogeneous, eager-once evaluation unlike pipelines, `[]` polymorphic). (2) `pwd` can't be a plain `string` (env values compute once — stale); it's `seq<string>` via `Seq.delay`, same lazy-value pattern as `ls`; laziness pinned by test (`let p = pwd in let d = cd "/tmp" in p` → `/tmp`). (3) `cd : string -> string` returns the new cwd (no unit type); bare `cd` → HOME deferred to command mode (Session 3). (4) No List type — `List<string>` in the plan is `seq<string>`.
- `Session.Cwd` (module-level mutable, initialized once from the process cwd) is the single working-directory authority: spawn audit confirms exactly one `ProcessStartInfo` site, `Session.Cwd`-set at force time; `ls` migrated off `GetCurrentDirectory`; `Environment.CurrentDirectory` never touched.
- Direct-exec lifecycle duplicates green (no sh in front — tree-kill holds on its own), with the plan's comment noting the exec-optimization analysis doesn't apply.
- `cmd` not-found is a runtime error this session ("command not found or not executable"); check-time PATH lookup is Session 3's mode-decision work.
- SEMANTICS.md: new "Processes and the session" section (sh/cmd ownership line, `&` orphan rule, Session.Cwd rule, pwd/cd shapes, tripwire cross-ref); literals bullet gains seq literals.

203 tests.

## Command-mode Session 1 — process lifecycle (2026-07-12)

Plan's reproduce came up GREEN, for a documented reason: the prescribed fix (`Process.Kill(entireProcessTree: true)`) has been the implementation since Spike 5. The compound test (`yes | grep` under `first 3`) confirms tree-kill reaches sh's forked pipeline children; zombie tests (50 completed + 50 killed streams) confirm no `<defunct>` accumulation. First red was a probe bug, not a weir bug: `pgrep -f MARKER` matched the probe's own `sh -c` wrapper — fixed with the `[m]arker` bracket trick.

Real changes shipped:
- Teardown hardened: unconditional `Kill(true)` attempt (swallowing already-exited) + `WaitForExit()` reap — reaping is now deterministic instead of relying on the .NET runtime's SIGCHLD reaper, and the `HasExited` guard race is gone.
- Tripwire pair kept with the plan's comment: simple case passes without tree-kill (sh execs single commands); the compound test is the real guard; Session 2's sh-removal changes the analysis.
- Known unreachable case, documented not fixed: `sh`-backed `cmd "daemon &"` — sh exits, the orphan reparents to init, and no tree-kill can reach it. That is `&` semantics (user owns backgrounded processes); becomes a Session-2 rules-doc line for `sh`.

No CI exists yet ("run in CI" done-when clause pending infra). 189 tests.

## Rename: fslite -> weir (2026-07-12)

Full content rename: `Weir` namespace/projects (`src/Weir`, `tests/Weir.Tests`, `weir.slnx`), `weir>` prompt, `~/.weir_history`, `usage: weir`, docs. Historical NOTES entries below renamed too (codename swap, not history rewrite). Zero `fslite`/`FsLite` residuals; 185 tests; caret alignment unaffected (derived from `prompt.Length`). AOT binary name follows the fsproj (`Weir`) - republish on next release-shaped work.

## Operator completeness — backlog #1 landed (2026-07-12)

`<>`, `>=`, `<=`, `&&`, `||` as operators; `not` as a `bool -> bool` builtin. 185 tests. Both pre-commitments honored and pinned:
- `<>` inherits `==`'s full equatability path (one rule pattern `("==" | "<>")` — `nats <> nats` rejected with the same message shape).
- `&&`/`||` short-circuit: dedicated eval cases *before* the generic binop case (which evaluates both sides); pinned with division-by-zero as the effect proxy (`false && (1/0 == 1)` → `false`; `true && (1/0 == 1)` → raises).
- Precedence: `||` (2) < `&&` (3) < comparisons (4); all left-assoc. FParsec longest-match handles the `|>`/`||` and `<`/`<=`/`<>` prefix families; the measure-literal `attempt` still wins (`1<mb> <= 2<mb>` parses).
- Var-var `&&`/`||` bind both operands to `bool` (their only typing) — same deterministic-defaulting family as `*`/`/`, noted in SEMANTICS.md.
- The day-one filter shape now works: `ls |> where (fun f -> f.Name <> "tmp" && not f.ReadOnly)`.

SEMANTICS.md updated: operator surface stated as complete, short-circuit promoted from pre-commitment to rule, backlog renumbered (`collect` is #1, measure algebra #2 — still flagged as reopening §4.2 and the `*`-defaulting rule).

## Tripwires, semantics doc, and the two re-aimed read questions (2026-07-12)

Response to the advisor's second pass. Three deliverables:

**1. `Tripwires.fs`** — tests named for the incidental protections, with comments stating which checklist item reopens if the named mechanism changes: funParams-shields-occurs (§1.1, reopens with arrow-var unification), no-unit-algebra (§4.2, reopens with measure arithmetic), no-annotation-syntax (§2.3, reopens with ascription), plus the two generalization pins below. Confirmed empirically along the way: `f.Size * 2` rejects ("expected int<mb>, got int") — the day-one ergonomic cliff is real; measure algebra is the top post-review backlog item.

**2. `SEMANTICS.md`** — the accidental-looking rules written down as language rules: the HOF-inference restriction, the generalization regime (deliberately upgraded from the v0.1 "frozen at definition" rule during the rows work — the advisor is right that this happened without a decision point; it's now a documented decision), measure exactness, `|>`-only, `==`-only equality, laziness/re-enumeration semantics.

**3. The two re-aimed read questions, pre-answered with pins**:
- *instantiate × Rows aliasing (new #1)*: **deep copy, not aliasing.** `instantiate` renames every quantified var — row names included — recursing into the row snapshot (field types renamed first), then installs a fresh `ctx.Rows[r']` entry per use site from the scheme's snapshot, which is an immutable `Ty` inside the env map and is never written after generalization. Sibling instantiations use distinct keys; discharge writes `Subst[r']` only. Pinned by a tripwire whose comment states the failure mode (sibling poisoning), in the dangerous order (use A fully discharged before use B instantiates).
- *envFreeVars transitive reachability*: **covered, structurally.** `envFreeVars` collects vars from `finalTy` of each env entry, and `finalTy` expands row constraints (deep) before `tyVars` runs — so a var reachable only through an env-free parameter's row constraints is still subtracted from the quantifier set. Pinned by a tripwire where `'a` occurs in the enclosing param's type *only* inside its row constraints and a second contradictory use must (and does) error.

172 tests. The human line-read now has its two hardest questions answered-with-evidence and its remaining scope: `bind`/`dischargeRow`/`mergeRows` (verify substitute-before-recurse is structural), then judgment-on-paper for `infer`'s EField/ELambda and `check`'s lambda rule.

## Row-soundness checklist — pre-read probes + implementation map (2026-07-12)

Ran the advisor's checklist probes before the line-read; all pass (167 tests). Map of checklist → implementation for the read:

**§1 Row unification**
- 1.1 occurs/self-application: `fun f -> f.x f` rejects (no hang) — but note *why*: weir never unifies a TVar with a function type at application (`funParams` on an unresolved var → "not a function"), which blocks the standard cycle constructions before `occurs` is even consulted. `occurs` (TVar case in `bind`) covers var-mediated cycles; rows enter `Subst` only via occurs-checked TVar bindings or `dischargeRow`/`mergeRows`, both of which substitute the row var *before* recursing into constraints — that ordering is what makes potential cycles terminate in `bind`. `finalTy` additionally carries a seen-set now (defensive; a cyclic row prints `{ .. }` instead of hanging the formatter).
- 1.2 var-var merge: `mergeRows` binds shared field types (`bind ft2 ft`), never name-unions. Probe: two lambdas' rows merged through a shared arg, conflicting `A` demands → "expected int, got string". Pinned.
- 1.3 intra-lambda: same code path as 1.2 — `EField` on a row var returns the *existing* constraint's type var, so a second conflicting demand collides on that var. Pinned.
- 1.4 closed rows: `dischargeRow` sets `Subst[r] := TNamed n` — after that, `resolve` yields the nominal type and field access takes the nominal path, so the row is genuinely closed. Pinned (`Nonexistent` after a discharged stage → nominal rejection).
- 1.5 stale-compare: `bind` shallow-resolves at the top and re-resolves in structural recursion; the `e = a` shortcut is safe because equal-but-unresolved compares only misfire toward the *structural* case, which resolves. Binop operands are atomic types. Good-code sanity pinned.

**§2 Propagation** — 2.1: a discharged row can't be written to (its var is substituted away; `Rows` entries go stale-but-unread). 2.2: all argument positions go through `check` (uniform since the rows rewrite); record literals push declared field types; the exact-field-set rule means no subset-leniency. 2.3: N/A — no annotation syntax exists.

**§3 Generalization** — regime is *generalize at let/REPL, freshen per use* (Damas-Milner style), not freeze: pinned by 3.1 test (one `map _.V` used at `int` and `string` field types in one line, both accepted). Soundness edge (generalizing an enclosing lambda's live var) excluded by `envFreeVars` subtraction — pinned since the rows commit. 3.2 value restriction: no purchase — the language has no mutable bindings, and data sources (streams) are concretely typed; only functions are polymorphic. 3.3: types are erased at runtime; closures carry no row-store references; cross-line types are baked snapshots re-instantiated per use, and per-line fresh-name collisions are impossible because REPL-stored types are fully generalized (every var renamed at instantiation).

**§4 UoM × rows** — 4.1: field demands are measure-*exact* (`f.Size > 1<mb>` demands `int<mb>`, no conversion, measures nominal by name); discharge against `int<byte>` would reject — pinned by the gb-vs-mb conflict test. 4.2: N/A by construction — no unit algebra exists (measures are `string option`; `*`/`/` are unitless-only), so there's no non-normalized representation to mis-compare. 4.3: no dimensionless collapse hole — `f.Size / f.Size` is *rejected* (division on measured ints unsupported; `int<1>` inexpressible). Pinned.

**§5** — 5.1: constructor patterns on unsolved rows reject; var/wildcard arms bind (harmless). 5.2: shadow binds a fresh var; constraints are keyed by var id, not source name — pinned. 5.3: constraint spans travel with each field demand; discharge errors point at the demanding span (or use site across a generalization boundary — deliberate, documented in the rows entry). 5.4: `unreachable` inventory re-probed with row-typed code; field-on-missing-VRecord requires a 1.4 leak, which is pinned shut.

**Read order for the human pass** (matches advisor's §1→§2→§3): `bind`/`dischargeRow`/`mergeRows` (~60 lines), then `checkSpine`+`check`, then `instantiate`/`envFreeVars`/`ELet`. The judgment-on-paper exercise applies mainly to `infer`'s EField/ELambda rules and `check`'s lambda rule.

## Depth audit — poking each spike where it's most likely hollow (2026-07-12)

Ran the adversarial probe list against the row-poly branch. Results:

- **Spike 1 (checker)**: 9 new adversarial tests, all rejected at check time with correct messages/spans — wrong arity, UoM mismatch both directions, shadowing with a different type, element type contradicting use two stages later, row constraint vs declared measure conflict, lambda/constructor piped as data, field access on a union. **The line-read debt remains open** — these tests raise confidence but only a human read of Check.fs rules out unsoundness that green tests can't see. The read target is the post-row-polymorphism Check.fs.
- **Spike 2 (unreachable arms)**: every attempted source-level route to an `unreachable` arm is blocked at check time (now pinned by tests). None reached.
- **Spike 4 (process lifecycle)**: `cmd "yes" |> first 3` and a truncated print of unforced infinite `cmd "yes"` both terminate; `pgrep` confirms zero leaked children in both cases (the `seq{} try/finally + Kill` path works under partial consumption). Pull-count tests were already in the suite.
- **Spike 5 (porcelain)**: **HOLE FOUND, exactly where predicted** — not the space itself but git's C-quoting it triggers: `"spaced name.txt"` and `"qu\"ote.txt"` passed through with quotes and escapes intact. Fixed: `unquoteGitPath` (full C-style unquote incl. octal escapes for unicode under default `core.quotePath`) + quote-aware rename-target splitting. Live retest on a repo with rename + space + quote + untracked: clean paths. Regression test covers all cases incl. `caf\303\251.txt` → `café.txt`.
- **Spike 7 (honest numbers)**: the 6ms was already the `-c`-path measurement (`-e "1 + 2 |> double"` = parse+check+eval+exit), warm cache. Full typed pipeline `-e 'ls |> where (fun f -> f.Size > 1<mb>) |> first 5'`: **7ms median** (min 6, max 15). No suppression flags anywhere in the fsproj (`NoWarn`/`TrimmerSingleWarn` absent) — the 3 dependency-aggregate warnings are surfaced and empirically triaged, not silenced.
- **Cross-cutting integration on the AOT binary**: declare `type Pkg = { Name: string; Size: int<mb> }` at the prompt → `cmd` emitting NDJSON → `from json Pkg` → `where (fun p -> p.Size > 2<mb>)` → `map _.Name` → `first 3` → `["big"; "huge"; "mid"]`. The skeleton threads end to end natively.

159 tests. Verdict: one real hole (porcelain quoting), found and closed; everything else held at depth. Outstanding: the human line-read of Check.fs.

## Row polymorphism + |> only (2026-07-11, session 1) — first parked item, unparked

**Two changes**, committed separately on `row-poly`:

**1. Dropped bare `|`** (user decision). Single operator table again, `armExpr` deleted — match arm bodies are now full expressions (`| Running n -> n |> double` works without parens). Piping a whole match now needs parens (`(match ...) |> f`) because arm bodies are greedy — same as F#. Nested match in an arm still needs parens.

**2. Row polymorphism** — the predicted "biggest checker-complexity jump", and it restructured Check.fs into a miniature Damas-Milner with rows:
- `TRowVar of name * fields` in `Ty`: a record type with *at least* these fields, displayed `{ Size: int<mb>; .. }`. `Scheme = { Forall; Ty }` replaces bare `Ty` in `TypeEnv.Values` — proper generalization, so the classic unsoundness (generalizing a variable free in the environment, e.g. an enclosing lambda's parameter) is excluded by construction and pinned by a test.
- Per-line mutable `Ctx` (fresh counter, substitution, row-constraint store with **spans per constraint**). `bind` is one-way-matching upgraded to unification-lite with an occurs check. Lambda params get fresh vars; field access on an unknown *upgrades* it to a row var and accumulates constraints; constraints discharge nominally when the var meets a `TNamed` (wrong field → "FileRow has no field 'Sze'. Did you mean 'Size'?" at the constraint's span; through a let-generalization the error lands at the use site, which is the right model for multi-line REPL sessions).
- Instantiation is freshen-on-use from schemes — let-bound row values are genuinely polymorphic: one `map _.X` reuses across two record types (tested), and `sizes = map _.Size : seq<{ Size: 'a; .. }> -> seq<'a>` is polymorphic in the row *and* the field type, so measures flow through.
- **Net simplification in places**: the two special-case lambda rules (EApp-of-lambda, pipe-into-lambda) are gone — bare lambdas just infer (`fun x -> x : 'a -> 'a`). checkSpine's two-pass argument dance collapsed to uniform `check` calls. The Spike 5 casualty (`let staged = where (fun f -> f.ReadOnly)`) is un-killed.
- Deliberate limits: binops on two unknowns stay errors (except `*`/`/` which bind to unitless int — the only sound reading); no higher-order inference (`fun f -> f 1` rejected); constructor patterns need concrete scrutinees; rows are records-only. Adapters/decls unchanged; runtime untouched (types erase).

**Numbers**: 149 tests; AOT still clean (same 3 dependency-aggregate warnings), cold start unchanged ~6–8ms with row-polymorphic expressions.

**Review note**: Check.fs is a full rewrite of the inference core — this is a gate-grade review, bigger than Spike 5's. Reading order: `Ctx`/`resolve`/`finalTy` → `instantiate`/`envFreeVars` (the generalization pair) → `bind`/`dischargeRow`/`mergeRows` (the heart) → `infer`'s `ELambda`/`ELet`/`EField` rules → `checkSpine`/`check`. The soundness-critical spots: occurs check, `envFreeVars` subtraction in `ELet`, and discharge-before-recurse ordering in `dischargeRow`.

## Spike 7 — AOT reality check (2026-07-11, session 1) — TARGET MET

**Result: 6ms median cold start** (min 6 / max 9 over 20 runs of `Weir -e "1 + 2 |> double"`), vs the 5–20ms target and ~70–135ms for the same dll under JIT `dotnet`. Binary: 5.5MB self-contained. The no-FCS, no-reflection, no-printf discipline paid off in full.

**Setup**: `PublishAot=true`, `InvariantGlobalization=true`, `StripSymbols=true`, `OptimizationPreference=Speed`; `dotnet publish -c Release -r linux-x64` (needs clang + zlib1g-dev + binutils; container is Ubuntu 26.04 — note: the `.fc44` kernel string is the Fedora *host* kernel, containers share it).

**Warnings**: zero from weir's own code. Three aggregate dependency warnings — FSharp.Core (IL2104 trim + IL3053 AOT) and FParsecCS (IL2104) — from reflection fallback paths (structural equality/printf in FSharp.Core, FParsec's dynamic bits). Empirically benign: every feature exercised against the native binary works — FParsec parsing, checker, declarations, match, UoM errors, streaming with process spawn/kill (`cmd "yes" | first 3`), porcelain and JSON adapters, roundtrips. Custom equality on `Value`, hand-rolled `formatTy`/`formatValue`, and interpolation-only output mean the flagged paths are never hit.

**Also built**: `weir -e "<expr>"` eval-and-exit mode (the honest thing to measure, and a real shell wants it) — `value : type` on stdout, errors to stderr, exit codes 0/1/2.

**Verdict**: the plan's last hard question answered yes. All 8 spikes done (0–7) in one day against a 12–20 session estimate. What remains is the parked list: row polymorphism (now concretely motivated by mono-builtins strain + the `where`-lambda-standalone casualty), adapter automation, LSP, daemon (moot — 6ms needs no daemon).

## Spike 6 — REPL ergonomics (2026-07-11, session 1)

**Built**: line editor (ReadLine nuget — history, tab completion), checker-powered completion (`Complete.fs`, pure + unit-tested), `_.Field` lambda shorthand, string escapes (`\" \\ \n \t`), history persisted to `~/.weir_history`. 136 tests.

**Completion design**: `Complete.suggest : TypeEnv -> text -> wordStart -> string list`, pure so it's testable without a terminal.
- Dot-completion resolves the target: env-bound record vars directly; unbound names (lambda params) fall back to the *pipeline element type* — parse+typecheck everything before the last `|`, take the seq element. So `ls | where (fun f -> f.<TAB>` offers FileRow fields, and after `| from porcelain |` the same keystroke offers Change fields. Field chains resolve through nested records.
- `from json <TAB>` completes declared record names. Otherwise: values in scope + keywords.
- The REPL runs completion against the live TypeEnv (a ref updated per loop), so user-declared types/lets complete immediately.

**`_.Field`**: parser-level desugar in `postfixAtom` — `_.A.B` becomes `fun _ -> _.A.B` (the param is literally named `_`). Zero checker changes; rides the lambda rules including pipe-directed instantiation: `ls | where _.ReadOnly`, `ls | map _.Size` both work. Bare `_` stays an unbound-variable error, as in F#.

**Bug found by the escapes**: lazy adapter errors (e.g. invalid JSON in `from json`) escaped the REPL's try — eval returns an unforced seq, and the throw happened at `formatValue` time, crashing the process (SIGABRT). Fix: force/format inside the guard. Lesson filed: with lazy values, *printing is evaluation* — any REPL boundary must treat formatting as effectful.

**Piped-stdin fallback**: when input is redirected the REPL bypasses ReadLine (it needs a real terminal) and reads plainly — keeps automated smoke tests working.

**Open, deliberately**: the spike's real question — does checker-powered completion feel like the payoff? — needs the user's hands on an interactive terminal; unit tests can't answer it. Also pending the user's `|` vs `|>` verdict (drop bare `|` and the dual-OPP grammar simplifies; keep it if the shell feel wins).

**Verdict**: build complete; experience verdict pending user. → Spike 7 (AOT) is the last planned spike.

## Spike 5 — External command boundary (2026-07-11, session 1)

**Built**: `cmd`/`into` process builtins, `from json <Record>` / `from porcelain` / `to json` syntax forms, `Change` record, real-git acceptance test — **plus pipe-directed parametric instantiation in the checker**, which the acceptance forced. 121 tests.

**Done-when verified**: `cmd "git status --porcelain" | from porcelain | where (fun c -> c.Staged)` works on a real repo (temp-repo test + live REPL). One deviation from the plan's literal expression: commands are `cmd "..."` strings, not bare words — bare-command syntax is command-position parsing (a frontend question for Spike 6+), not a typed↔bytes question.

**The forced checker change (REVIEW THIS)**: the acceptance pipes `seq<Change>` into `where`, which was FileRow-mono — unmeetable without polymorphic combinators. Added the minimal version: `TVar` in `Ty`, and spine-directed instantiation (`checkSpine` in Check.fs). The pipe rule now infers the piped value FIRST, binds the combinator's type variables from it (one-way matching, no unification variables), and only then checks lambda arguments — whose parameter types are concrete by that point. Two-pass argument checking (non-lambdas bind first, lambdas after) makes full application `where p ls` work too. No generalization, no let-polymorphism, no row polymorphism — those stay parked; `didYouMean`-quality errors preserved.

**Casualty**: `let staged = where (fun f -> ...)` no longer checks (lambda in polymorphic position, no data to instantiate from; error hints "pipe the data in first"). Partial application with inferable args still works and stays polymorphic (`let firstTwo = first 2` : `seq<'a> -> seq<'a>`). Pipe-first is the shell idiom anyway.

**Typed↔bytes verdict (the spike's question)**: less painful than feared, with clear division of labor. The checker guarantees everything inside the pipeline; the adapter validates at the boundary and fails loudly per line (`from json: missing field 'Size' in: {...}`). Runtime boundary errors are honest — bytes are untyped, so check-at-the-edge is the contract. `from`/`to` as syntax (not builtins) works because a format+record isn't a value — and `from porcelain` still first-classes fine (`let p = from porcelain in ...`).

**Mechanics that mattered**:
- `TEFrom` carries the `RecordDef` (not just the name), so eval needs no TypeEnv — checker resolves, runtime trusts.
- Process streams: `seq {}` with `try/finally` kills the child when the consumer stops early — `cmd "yes" | first 3` terminates and reaps. Nonzero exit raises at stream end with stderr. `into` writes stdin from a background task (no deadlock on full pipes).
- JSON via `JsonDocument`/`Utf8JsonWriter` — no reflection, AOT-safe. Serialization is value-driven (VRecord knows its shape); only parsing needs the def.
- weir string literals have no escapes, so you can't type JSON at the prompt — roundtrip demos via `ls | to json | from json FileRow`. Escape syntax → Spike 6.

**Surprised**: how little the poly machinery needed to be — ~100 lines, no unification state, because bidirectional + pipe-first gives instantiation order for free. Dunfield & Krishnaswami would call this a degenerate special case, and it's exactly enough for a shell.

**Verdict**: continue. → Spike 6 (REPL ergonomics) or the parked polymorphism/adapter work.

## Spike 4 — Streaming pipelines (2026-07-11, session 1)

**Built**: infinite `nats` builtin, lazy `map`/`take`/`sum` (int-mono), `==` equatability check in the checker, pull-count acceptance tests. 93 tests.

**Acceptance verified**: infinite source `| first 5` terminates; a counting source proves `first 5` pulls exactly 5 elements, `where ... | first 2` pulls exactly what the filter examined (4), and an unforced pipeline pulls 0. Laziness survives eval boundaries — including weir lambdas as filter/map stages (closures apply per-pull inside Seq.filter/Seq.map).

**The honest finding**: Spike 2's architecture had already answered this spike's question. `VSeq` wraps .NET `seq<Value>` (an enumerator factory), and `where`/`first` were built on `Seq.filter`/`Seq.truncate` from day one — nothing in the eval path materializes. This spike was proof + hardening, done in a fraction of the estimate.

**Hardening that was real**:
- Spike 2's flagged footgun closed: `==` on a seq would have hung on infinite input (Value equality materializes both sides). Fixed at the type level — `isEquatable` recursively rejects `==` on seqs, functions, and any record/union that transitively carries one (cycle-safe via a seen-set). Runtime equality on seqs is now unreachable through checked code.
- `formatValue` already truncated at 20 elements, so the REPL prints `nats` (an infinite value) safely.

**Naming pressure**: `map`/`take`/`sum` carry generic names but int-mono types, while `where`/`first` are FileRow-mono. Two element types now exist and the builtin table is already showing the strain — this is the concrete motivation for the parked polymorphism work, on schedule (revisit after Spike 5).

**Caveat noted**: `let s = ls | where p in ...` re-enumerates per use (standard seq semantics) — side-effecting sources run again. Fine for now; caching combinators are a product question, not a spike question.

**Verdict**: continue. → Spike 5 (external command boundary).

## Spike 3 — Type declarations (2026-07-11, session 1)

**Built**: `type X = { ... }` / `type X = A of t | B` statements at the prompt, record literals, `match` with constructor/var/wildcard patterns (nested allowed), exhaustiveness + unreachable-arm warnings, session persistence. 83 tests.

**Done-when verified at the REPL**: declare `type Proc = Running of int | Stopped`, construct (`Running 42`), match, get span-underlined exhaustiveness warnings.

**Design decisions**:
- `TRecord` → `TNamed`: the parser can't know record-vs-union when reading a type name, so `Ty` holds just the name and `env.Types` maps to `TypeDef = Record | Union`. Mechanical rename through checker/builtins.
- Constructors enter `Values` as ordinary typed entries (`Running : int -> Proc`, `Stopped : Proc`), so construction is just application — no new checker rule, and constructor typos get did-you-mean hints for free. Runtime counterparts built by `Eval.constructorValues`; a redeclared union shadows its constructors, but old values still match (pattern checking resolves cases via the scrutinee's type def, not a global ctor table).
- Case identifiers must start uppercase (F# convention) — that's what disambiguates `PCase` from `PVar` in patterns.
- **The `|` ambiguity**: match arms vs pipe. Resolution: arm bodies parse with a second OPP that omits the `|` operator (`|>` stays legal); a failed arm parse backtracks, so a trailing `| double` after the last arm becomes a pipe of the whole match — coherent and tested. Arm bodies containing `let`/`fun`/nested `match` need parens. Real fix is the offside rule — Spike 6 question at the earliest.
- Record literals resolve nominally by exact field-name set; ambiguity (two records, same fields) is an error. Type ascription syntax is the eventual disambiguator if needed.
- Exhaustiveness is a separate pure pass (`Check.warnings : TypeEnv -> TypedExpr -> Warning list`) walking the typed tree — zero signature churn on infer/check, no writer-monad plumbing, trivially testable. Top-level coverage only: a case counts as covered when some arm has its constructor with an irrefutable argument; nested refutations are conservatively "not covered". Proper usefulness matrices parked.
- Non-exhaustive match is a warning, not an error (per plan) — so `match failure` at runtime is reachable and is a `failwith`, not an `unreachable`.

**Surprised**: constructors-as-env-entries made construction genuinely free — the entire "constructor table" is `checkDecl` extending Values. The `|` grammar collision was the only real fight, and backtracking arms turned it into a feature (pipe-after-match without parens).

**Verdict**: continue. → Spike 4 (streaming pipelines).

## Spike 2 — Typed interpreter over checked AST (2026-07-11, session 1)

**Built**: `Eval.fs` rewritten over `TypedExpr` — untyped Spike 0 eval deleted. Value domain grown: `VRecord` (name + field map), `VUnion` (shape only, constructed in Spike 3), `VSeq`. All type-impossible arms are `unreachable` calls. New `Builtins.fs`: each builtin is one `(name, Ty, Value)` entry, so the TypeEnv and value env derive from a single list and can't drift. `ls` is real (`Seq.delay` over cwd — fresh listing per enumeration), typed `seq<FileRow>`. 52 tests.

**Checker→interpreter handoff**: holds. Spike 1's acceptance expression evaluates over records (fake `ls` fixture in tests, real one in the REPL). All former "fails at runtime" tests are now "rejected at check time" tests — the runtime error class they covered is unreachable through the checked pipeline.

**Learned**:
- The gate exposed a Spike-0-era fixture as untypeable: `let add = fun a -> fun b -> ...` — let-bound bare lambdas can't infer in bidirectional checking without annotations. Not a bug; the idiomatic replacement is partial application of typed functions (`let staged = where (fun f -> ...)`), which infers fine and is more shell-like anyway. Parameter annotation syntax is the eventual fix if the limitation bites.
- Runtime type errors did disappear. What remains at runtime is honest: division by zero, IO failures. Those are not the checker's job.
- `VSeq` equality materializes both sides — fine for tests, will be a footgun with infinite seqs in Spike 4 (flagged there).
- `unreachable` messages name the checker guarantee they rely on — each one is a soundness assertion; if one ever fires, it points at the checker rule that lied.

**Surprised**: how mechanical this spike was after Spike 1 — the typed eval is *simpler* than the untyped one (no defensive error paths, just `unreachable`).

**Verdict**: continue. → Spike 3 (type declarations) or Spike 4 (streaming).

## Spike 1 — Bidirectional checker, nominal only (2026-07-11, session 1)

**Built**: spanned AST (`Expr = { Kind; Span }`), `Ty` (int-with-optional-measure/str/bool/fn/seq/record-by-name), `TypeEnv` (Values + Types), `infer`/`check` pair in `Check.fs`, typo hints via edit distance, REPL now typechecks before eval and prints caret-underlined span errors. 43 tests.

**Acceptance**: `ls | where (fun f -> f.Size > 1<mb>) | first 5` checks to `seq<FileRow>`; `f.Sze` rejected with span exactly on `Sze` + "Did you mean 'Size'?". Perf: ~µs per check, 10ms bound trivially met.

**Design decisions**:
- Binops promoted from desugared builtins to `EBinOp` — overloading (`+` on int/str, measure-preserving arithmetic) doesn't fit monomorphic env entries. `typeBinOp` is the single overload table.
- Builtins are monomorphic (`where : (FileRow -> bool) -> seq<FileRow> -> seq<FileRow>`); polymorphism deliberately absent, revisit with row polymorphism (parked).
- UoM = `TInt of string option`, equality by name, erased at runtime. `+`/`-`/comparison require same measure; `*`/`/` unitless only (no measure algebra).
- Lambdas don't infer, but two refinement rules cover the shell idioms: lambda applied to a known arg, and pipe-into-lambda (arg type flows into the param).
- `EField` carries the field's own span so typo errors point at `Sze`, not all of `f.Sze`.
- `==` not `=` for equality (avoids let ambiguity). Composite spans are unions of child spans; leaf tokens capture position before ws-skip.
- Spans compose via `Span.union`; retrofitting confirmed as the right fear — touching every parser production once was enough, but only because the AST was 8 cases.

**Surprised**: how little the bidirectional core is — `check` has 3 real rules (lambda, let, fallback-to-infer-and-compare). The complexity lives in `infer`'s per-node rules and error message quality, not the discipline itself.

**Verdict (provisional)**: checker felt tractable to write. GATE CONDITION: user line-by-line review of Check.fs pending — spike isn't closed until then.

## Spike 0 — Toy interpreter (2026-07-11)

**Built**: `Expr` DU (int/str/bool/var/let/lambda/app/pipe), FParsec parser, tree-walk eval/apply, REPL with persistent top-level `let`. 23 tests. `1 + 2 |> double` → 6 end to end.

**Learned**:
- FParsec's `OperatorPrecedenceParser` handles the whole binop/pipe layer; binops desugar to `EApp(EApp(EVar "+", l), r)` against builtin env entries, so eval has no operator special cases.
- Lambda/let-in must be *terms* of the OPP (not alternatives outside it) or they can't appear on a pipe RHS (`5 |> fun x -> x * x`). Greedy lambda body = F# semantics for free.
- `Value` can't derive structural equality once `VBuiltin of (Value -> Value)` exists — custom equality (structural for data, reference for functions) needed for test assertions. Will matter again for `VSeq` in Spike 4.
- FParsec error messages come with line/col and a caret out of the box — good omen for Spike 1 span work.

**Surprised**: nothing structural. Keyword-vs-identifier ambiguity (`true`, `fun`) needed the usual `attempt` + `notFollowedBy` dance.

**Verdict**: continue. Eval/apply shape clicks, FParsec workable. → Spike 1.
