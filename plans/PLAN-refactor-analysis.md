# weir — refactoring analysis: how clean is it, really

Status: EXECUTED (2026-08-01; blessed same day). Analysis only —
ZERO code changes (verified: the branch's diff is this file).
Distinct from its three predecessors: the regroup census covered
docs/plans/debt, the dedupe sweep covered duplicated blocks, the
maintenance sweep covered comments/dead code/tests. This one asks:
does the architecture still hold its shape after ~six weeks of dense
feature work?

Baseline for deltas: main @ 2026-07-29 (ceacb76, the pipe-rule
commit — the maintenance-sweep era).

## THE REPORT

### 1 · Numbers

| module | now | Δ since 07-29 | note |
|---|---|---|---|
| Script.fs | 3291 | +302 | the pipeline + 5 tenants (below) |
| Check.fs | 3101 | +299 | yaml templates + spans |
| Parser.fs | 2492 | +549 | biggest delta: district parser + for/do + guards |
| Builtins.fs | 1882 | +10 | registry, stable |
| Eval.fs | 1582 | +357 | yaml boundary + district eval |
| Lsp.fs | 1447 | +4 | REMARKABLY stable (see hover verdict) |
| Repl.fs | 914 | +513 | biggest relative delta: 2D editor + history |
| Contracts.fs | 666 | new | the spine + schema validator |
| Yaml.fs | 613 | new | the owned subset parser |
| Fmt.fs | 457 | +44 | district-aware since |
| everything else | ≤349 each | 0 | untouched six weeks |

Total src: 18,244 lines. The zero-delta tail (Types, Argv, Proc,
Extern, Session, Diagnose, Prelude, Ast±) is a third of the module
count and none of the incident history — the churn is concentrated
exactly where the features went.

### 2 · Touch-counts (the real coupling number)

**The yaml boundary+district** (session-1 commit 6e53fe1): **11 src
files** (Ast, Builtins, Check, Eval, Lsp, Parser, Prelude, Script,
Yaml-new, fsproj; grammars followed in a later session). **The
schema attachment** (e09d659): **8 src files** (Ast, Check,
Contracts-new, Eval, Parser, Program, Script, fsproj) + 2 grammar
files.

Reading the numbers honestly: ~6 of those files are the PIPELINE
ITSELF (Ast → Parser → Check → Eval → Script is one value flowing
through five stages — a new EXPRESSION KIND touches all five by
construction, and that is the architecture working, not failing).
The coupling that is real: the GRAMMARS (3 files per the drift rule,
by policy) and the fsproj. The one avoidable touch in the schema
feature was Program.fs growing bespoke command plumbing per
subcommand — see candidate R4.

### 3 · One-job verdicts (lens question 1)

- **Types, Argv, Proc, Extern, Session, Diagnose, Prelude, Ast** —
  CLEAN. Small, single-purpose, zero delta (Ast grew its yaml nodes,
  which is its job).
- **Yaml.fs** — CLEAN. The owned subset parser + render law + the
  comment scanner (one machine, two faces at Yaml.fs:~55). The
  district parser CONSUMES it (blockHeader, scalarCore, indentOf,
  stripDistrictComment) — a stated reuse seam, not a reach-in.
- **Contracts.fs** — CLEAN shape, two small silent duplicates inside
  (findings F-A, F-B below). The validator walks Check's TYPED
  template and never touches Yaml.fs — the schema/yaml seam is
  properly indirect (Contracts.fs:1-17 states it).
- **Check.fs** — CLEAN. Big but one job (typing); the yaml template
  checking is the same job on a new node family.
- **Parser.fs** — CLEAN with a stated annex: the district template
  parser (parseTplBlock ~200 lines, Parser.fs:1212+) is almost a
  second parser, but it shares the fragment machinery (runFragment,
  spans) and Yaml's lexical helpers — moving it out would cut those
  seams for a file-count win. Not recommended.
- **Eval.fs** — CLEAN. The yaml renderer + boundary adapters are
  evaluation-side by rights (output-goes-where-the-meaning-goes).
- **Fmt.fs** — CLEAN since the district fixes.
- **Repl.fs** — CLEAN; +513 lines are the 2D editor, which is its
  one job.
- **Lsp.fs** — CLEAN, and the census's Hover.fs question answers
  itself with evidence now: +4 lines in six weeks of feature work
  means the pressure never materialized. Hover has ~5 lookup paths
  (paramTypeAt Lsp.fs:422, localDef :447, declHover, varUseType,
  builtinDocs) but they share childExprs and did not grow. VERDICT:
  leave; re-ask only when signatures/schemas actually add hover data.
- **Script.fs** — ONE TENANT TO MOVE, maybe two. Inventory of jobs
  (by section): (a) the string scanner family (:15-230 — stated
  shared machinery, 8 foldOutsideStrings consumers, LOAD-BEARING,
  stays); (b) doc attachment + misalignment lint (:103-460); (c)
  line/piece classification + district mask (:232-420); (d) THE
  ASSEMBLER (:472-1428 incl translate); (e) colorizeRepl
  (:1432-1583 — the REPL's colorizer living in Script because it
  shares the scanner and classifyPiece); (f) resolver + checkStatement
  + analyzeLines + schemaDiagnostics (:1700-2700 — the pipeline
  proper); (g) THE MODULE LOADER (:2183-2500+ — resolveImportPath,
  loadModuleCached, module envs, ~500 lines). VERDICT: (g) is the
  census's suspected second tenant, confirmed grown — a Modules.fs
  extraction is clean (it consumes analyzeLines-shaped machinery via
  a narrow surface). (e) is a half-tenant: colorizeRepl is
  REPL-owned logic kept here for the scanner; moving it means
  exposing inStringMask/stripComment — cheap, cosmetic. SIZE: (g)
  medium (~500 lines, mechanical, zero pin movement EXPECTED because
  the tests call Script.assemble/analyzeLines which stay — justified
  by: the move is cut-paste of private functions + one open); (e)
  small, cosmetic.

### 4 · The law table (lens question 2)

| law | enforcement sites | stated? |
|---|---|---|
| pipe rule (RHS decides) | Parser pipeSep + pipeToCommand walk | 2 sites, STATED at ruling |
| depth guard | Parser guard + post-parse validation | 2 sites, STATED |
| machine boundary (sentinel glue) | Parser: head guard :1750, schema-suffix guard :1756, arg guard :1712 | 3 guards, ONE file, each commented — stated, coherent |
| content-is-bytes | assemble (raw joins), parseTplBlock (consume-first), fmt (Substring not TrimStart), districtContentMask consumers | N sites BY DESIGN, named in PROCESS with the mask as the shared answer — stated |
| splice law (liftable) | Check.yamlSpliceable + Eval.liftYaml | 2 sites, STATED (the sortBy posture) |
| marker classification | Parser.isYamlMarkerPiece; Script aliases; Complete + colorize + mask consume | ONE source since schema-polish — clean |
| yaml comment scan | Yaml.commentCutAt, two faces | ONE machine, stated |
| quoting law (reverse-Norway) | Yaml.renderScalar + Eval.renderString (block split) | 2 sites, stated at block-scalars |
| casing law | Parser (binders) + Check (decl names) | 2 sites, long-stated |
| **scalar self-typing** (plain 3→int, true→bool, ""→null) | **Eval.evalYamlTpl :1490 AND Contracts.literalKind :511** | **SILENT second site — finding F-B** |
| **did-you-mean** | **Types.didYouMean (shared, 5 consumers) AND Contracts.levenshtein+didYouMean :470-489** | **SILENT duplicate, different message shape — finding F-A** |
| never-fetch-during-check | schemaDiagnostics (reads only) + e2e negative pins | stated |

Both silent sites arrived in the SAME session (contracts-spine, mine)
— the lesson is that a new module's authors reach for local helpers
before greping the tree; the dedupe sweep's token-hash hunt would
have caught both.

### 5 · The specific reads

**The assembler: one machine or several sharing a file?** ONE
machine with documented annexes. The core is a single fold with four
mutable stacks (brackets, matches, district, lets) and a 9-case join
table (applyJoin :711). Districts, the sentinel, block scalars, and
comment/blank transparency are all JOIN KINDS or per-line state, not
parallel machines. The four alignment stacks: STILL FOUR (bracket
entry-anchoring, match arms, pipe alignment, doc-align as an
external lint) — no fifth arrived; districts/block-scalars
deliberately use verbatim relative indent, not alignment, so the
trigger has not fired. It is the hardest 700 lines in the tree and
splitting it would sever the state the join decisions share. VERDICT:
leave; the cost is real but it is the essential complexity (see §6).

**The resolver seam — the project's most-repeated failure.** The
divergence is CONTAINED BY SHAPE: assumeResolver (Script.fs:1801) is
ONE record-update overriding ONE field (IsExternal) with a ~10-line
predicate; every incident (verdict split, Args.load gate, Local
literal, the yaml head, now the schema-suffix) was the PREDICATE
being wrong or a grammar branch trusting it too much — never a
scattered-site problem. So it is containable but not yet CONTAINED:
nothing asserts check-agrees-with-run. THE COST OF THE INVARIANT
(the twice-proposed fuzzer property): a metamorphic-style property
running analyzeLines (assume) vs the runner's hard check on every
generated program, asserting same-verdict + same-sexpr-when-accepted.
The harness already spawns the binary and generates only echo-headed
programs (real on PATH), so the property is ~60–100 lines in Main.fs
with no new generator work. It would have caught 3 of the 5 incidents
at generation time (the two others involved districts the generator
cannot yet produce — the stated GRAMMAR.md gap compounds here).
RANKED #1 below; it is a harness addition, not a refactor.

**Yaml/contracts:** clean seams both directions (validator ↔ typed
tree only; district parser ↔ Yaml's lexical helpers, stated).

**Boundary-loader family:** the suspicion is TRUE but benign: three
hand-written field-law walkers (jsonableRecord/-Elem Check.fs:710,
yamlableOut :765, Env's inline `loadable` :1463) plus a fourth
mini-law in Contracts.tyKind :497. They are NOT duplicates — each
walker IS its law (json: flat+Option; yaml: trees+pairs; env:
scalars+enums; schema: kinds) and merging them would encode the
differences as flags, which is worse. The shared part (walk fields,
first violation, teach with span) is ~10 lines of skeleton each.
VERDICT: stated non-duplicates; the trigger for revisiting is a
FIFTH loader.

### 6 · The newcomer answer (lens question 4)

**The logical-line model, concretely**: to change assembly safely you
must hold (a) raw vs stripped text and which of the two every
consumer sees (the content-is-bytes audit exists because this was
gotten wrong 7 times); (b) the join table — 9 join kinds, of which
yaml districts GLUE the sentinel and everything else SPACES it, the
single fact the machine-boundary guards encode; (c) the
segment/translate arithmetic — every join is 3 physical chars or the
spans lie; (d) the district state machine inside the fold. That is
~700 lines of Script.fs plus the two pages of PROCESS that explain
why. Everything else in the tree can be changed locally; this cannot,
and the ledger's incident density (sentinel, verdict split, blanks,
tabs, strip) maps onto exactly this region. The mitigation that
exists: the fixture-realism discipline + the hostile-byte guard. The
mitigation that does not: the check-agrees-with-run property.

### 7 · Hand-rolled vs BCL (exact APIs, AOT verdicts)

| what | lines | BCL candidate (exact API) | AOT | gives up | verdict |
|---|---|---|---|---|---|
| JSON read (from json, lock, schemas, LSP) | — | System.Text.Json.JsonDocument | VERIFIED in use (published binary, e2e) | nothing | ALREADY BCL |
| JSON write (to json, lock, diags) | — | System.Text.Json.Utf8JsonWriter | VERIFIED in use | nothing | ALREADY BCL |
| reflective JsonSerializer | 0 | correctly AVOIDED everywhere | n/a (IL2026/3050) | — | keep avoiding |
| YAML subset parser | 613 | none acceptable (YamlDotNet: positions fault, +677KB) | — | positions, subset control, 0 deps | DECIDED-OWNED (config spike receipt), closed |
| glob matcher | ~90 in Builtins | Microsoft.Extensions.FileSystemGlobbing | probe FAILED the dotfile SPEC | bash dotfile law | DECIDED-OWNED (Path.glob session), closed |
| hashing | 3 | SHA256.Create (BCL) | verified in use | — | ALREADY BCL |
| URL parsing | 2 | System.Uri | verified in use | — | ALREADY BCL |
| edit distance | 2×~15 | none in BCL | — | — | not a BCL case; a DEDUPE case (F-A) |
| HTTP fetch | ~25 | HttpClient (GetAsync/ReadAsByteArrayAsync, default SocketsHttpHandler) | VERIFIED two-tier: HTTP-on-loopback e2e-pinned (offline CI); HTTPS+TLS probed in-session on the AOT binary against raw.githubusercontent.com (Linux/OpenSSL) — Windows/macOS TLS and proxy-env handling UNVERIFIED until the CI matrix | — | ALREADY BCL; publish carries zero System.Net.Http IL warnings (only the pre-existing FSharp.Core/FParsecCS IL2104/IL3053) |
| Windows argv quoting | 0 yet | ProcessStartInfo.ArgumentList (per the spike) | UNVERIFIED — no probe published on Windows | — | note for the Windows arc, not now |

No swap is recommended: everything swappable is already on the exact
AOT-clean BCL API, and both owned parsers carry measured decisions.
The table's value is closing the question.

### 8 · Ranked candidates (value ÷ risk; breakage named)

1. **The check-agrees-with-run fuzzer property** (harness, ~60–100
   lines). Value: guards the tree's most-repeated failure shape at
   generation time. Risk: LOW — additive property; breakage: it may
   IMMEDIATELY find a real divergence (that is value wearing red);
   deep runs lengthen ~20%. Pairs naturally with adding a yaml
   production to the generator (the stated GRAMMAR.md gap).
2. **F-A: Contracts' levenshtein/didYouMean → Types.didYouMean**
   (small). Value: kills a silent duplicate of a shared helper.
   Breakage NAMED: message shape differs (" — did you mean 'x'?" vs
   ". Did you mean 'x'?") — the six re-pinned schema messages move
   again, or Types gains a separator parameter. Pins to move: the
   e2e schema block + 1 unit pin. Do it inside the next schema
   session rather than alone.
3. **F-B: scalar self-typing → one home** (small). Extract the
   plain-scalar typing rule (int/bool/null/string) to Yaml.fs;
   Eval's district eval and Contracts' literalKind both consume.
   Breakage: none expected — same rule, two consumers; justify by
   the diff being pure call-site substitution. Zero pin movement is
   claimable because both sites are pinned and the rule is identical
   (if it is NOT identical, the extraction will prove it — also
   value).
4. **Program.fs command plumbing** (small-medium, cosmetic). Each
   subcommand hand-rolls findWeirDir + error printing (:135-200). A
   third contracts command (the restore-rename note's trigger) is
   ALSO the trigger for a tiny command harness. Wait for it.
5. **Modules.fs extraction from Script** (medium, structural but
   calm). ~500 lines of module loading are Script's clearest second
   tenant. Risk: LOW (private functions, narrow seam); breakage:
   none expected — the public surface (analyzeLines, run) stays.
   Value: Script.fs back under 2800 and the pipeline readable
   top-to-bottom. Schedule with the modules arc's next session
   rather than standalone.
6. **colorizeRepl → Repl side** (small, cosmetic). Requires
   exposing 2 scanner helpers. Value: file hygiene only.
7. **NOT RECOMMENDED: splitting the assembler.** The state sharing
   is the machine. The alternative that actually reduces its risk is
   candidate 1.

### 9 · Nothing needed here (named, so the findings are credible)

Types, Argv, Proc, Extern, Session, Diagnose, Prelude (six weeks
untouched, single-job, small); Yaml.fs (new but already carries its
decisions); Check.fs (big is not a smell when it is one job); Fmt
post-district-fixes; Lsp.fs (the +4-line six-week delta is the best
evidence in this report); Builtins' registry; the e2e battery's
structure; the grammar-drift machinery; the four alignment stacks
(trigger unfired); the boundary-loader walkers (laws, not
duplicates).

### Structural vs cosmetic, in one line each

STRUCTURAL: the missing check-agrees-with-run invariant (1); the
module-loader tenant (5). COSMETIC: F-A, F-B, Program plumbing,
colorizeRepl placement. Nothing found is urgent; nothing is rotting;
the tree's real debt is one unwritten fuzzer property.
