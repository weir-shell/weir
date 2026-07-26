# Spike Notes

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
  oracle's first live catch, and the strongest possible argument for
  it: the failure mode (F# muscle memory silently doing something
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
  section says it doesn't work. Strongest possible validation of both
  the doc-test discipline and the parked let-RHS extension.
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
