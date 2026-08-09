# weir — typed command signatures: the design (v1 local, v2 registry)

Status: DESIGN, BLESSED (user 2026-07-30) — the decisions are settled
here; implementation is its own arc of blessed sessions. Written on
the advisor's leanings for the four open questions, each marked
LEANING so a session may revisit with evidence. REVISED 2026-08-01:
L4 expanded (v1 checks, then HOVER, then completes) and the arc
renumbered to six sessions; reconciled with the landed contracts
spine (DESIGN-external-contracts, merged f45032a) — the `.weir/`
walk, the lockfile, and `verify`'s hash arm now EXIST (Contracts.fs),
so session 1 starts from a built spine rather than greenfield, and
generation is `weir add sig` per PLAN-restore-rename.

## The pitch

Weir checks that `bicep` EXISTS. It cannot check that you called it
correctly. A signature closes that gap: declare a tool's CLI surface
and `bicep buidl --outfil x` becomes a check error instead of a 3am
failure. **Check-before-effects extended to the argument level** —
the biggest remaining hole in the guarantee.

## What a signature IS (and is not)

**Checker metadata, not runnable code.** It contributes NOTHING at
runtime; it only constrains what the checker accepts. That is why it
is not an `import` (which brings values and types for the program to
USE) and why deleting every signature leaves every script running
identically, just checked less. Additive, removable, inert.

## Settled decisions

**1. A `#` directive, declared PER TOOL, not a directory pointer.**
The directive joins the existing family (`#loose`), whose meaning is
already "instructions to the tooling, inert at runtime" — the same
relationship a signature has. Two forms:

    #sig bicep                        -- default resolution
    #sig bicep "./tools/bicep.weir"   -- explicit override

- **Default resolution**: walk UP from the file containing the
  directive to the first `.weir/` directory, and resolve
  `<that>/sigs/<tool>.weir`. This is what makes the bare form worth
  having — most scripts live in subdirectories, and file-relative
  resolution would have made the override the norm and `#sig bicep`
  decorative.
- **Why discovery is SAFE here, when ambient discovery was rejected
  elsewhere** — the distinction is SILENCE. Ambient discovery is
  dangerous when a directory's mere existence changes how a file
  checks with nothing in the file mentioning it. Here **the directive
  IS the opt-in**: `#sig bicep` states the requirement, the walk only
  LOCATES what the file already asked for, and **absence is a check
  error, not a fallback** — so a machine without the signature fails
  loudly rather than checking differently in silence. Since
  `.weir/sigs/` is checked in, it travels with the repo. This is also
  the universal convention for project metadata (`.git`,
  `.editorconfig`, `tsconfig.json`, `pyproject.toml` all walk up)
  precisely because project-level metadata must be findable from
  anywhere inside the project.
- **Two boundaries, so the walk cannot surprise**:
  - **stop at the FIRST `.weir/` found** — if that directory exists
    but lacks `<tool>.weir`, that is the error. The walk does NOT
    continue upslope looking for a grandparent's signature, which
    would be exactly the surprising fallback discovery is feared for;
  - **do not cross a `.git` boundary** (nor reach the filesystem
    root) — a repo does not silently inherit its parent directory's
    or a sibling checkout's signatures. [Landed with the contracts
    spine: `Contracts.findWeirDir` stops at the directory containing
    `.git` (dir or file — worktrees), bounds at the filesystem root,
    and its errors name both ends. Pinned.]
- **The override remains** for out-of-tree or unconventional layouts:
  `#sig bicep "../vendor/bicep.weir"`, resolved relative to the
  declaring file, no walking.
- **Why `.weir/sigs/` rather than `./sigs/`**: "sigs" is a generic
  word and more commonly means CRYPTOGRAPHIC signatures (code
  signing, GPG, release artifacts) — a project could easily want
  `sigs/` for that. `.weir/` is unambiguous about whose concept it
  is and matches the tool-owned-directory convention (`.github/`,
  `.vscode/`) instead of squatting a plausible source directory
  name. It is also where **v2's artifacts belong**: one directory
  holds `.weir/sigs/<tool>.weir`, `.weir/lock.json`, and any cache —
  where `./sigs/` would have left the lockfile homeless at the root.
  And it is what the upward walk looks FOR, which a bare `sigs/`
  could not be (too generic a name to claim as a project marker).
- **Checked in vs ignored, stated now**: `.weir/sigs/` is CHECKED IN
  (signatures are source — hand-edited for the exhaustive marker and
  for correcting scraped output); `.weir/lock.json` is CHECKED IN
  (reproducibility); anything cache-shaped is ignored.
- **Missing file** is a check error naming the resolved absolute path
  AND the `.weir/` it searched from (the imports precedent, extended:
  relative-resolution confusion is the usual failure, and with a walk
  involved the reader needs to know where it landed).
- **Why PER TOOL rather than a directory** — the deciding argument is
  not ergonomics, it is that **an explicit declaration makes the
  check's behaviour visible in the file being checked.** With a
  directory pointer, a reader cannot tell whether
  `bicep build --outfil x` errors without going to look at what
  happens to be in that directory, and *adding a file silently
  changes how existing scripts check* — mild action-at-a-distance,
  the same species ambient discovery was rejected for, just with a
  shorter radius. Four things fall out of the explicit form:
  - **per-file precision** — a script that only calls `git` does not
    inherit a `kubectl` signature it never uses, so a stale kubectl
    signature cannot fail an unrelated script's verify;
  - **`verify` scoping** — the tools to check are exactly the ones
    declared, per file;
  - **better errors** — an unknown flag can say *declared by
    `#sig bicep` on line 3*, pointing at what the author opted into;
  - **honest repetition** — twelve scripts calling `az` declare it
    twelve times. That is one line per script, and it is the same
    trade qualified-always imports made: verbosity bought
    visibility, deliberately.
- **File-local and NON-INHERITED**: a signature constrains the file
  that declares it, full stop. A module's `#sig` does NOT reach its
  importers — inheritance would make the effective signature set
  depend on the import graph, the action-at-a-distance this form
  exists to avoid.
- **What IS still rejected: unannounced discovery.** A `.weir/sigs/`
  directory must never affect a file that does not declare `#sig`.
  Signatures apply because a file ASKS; the walk is a lookup for a
  stated requirement, never an ambient effect. That keeps the
  property protected everywhere else (no bashrc, config unreachable
  from scripts): a script's checking behaviour is legible from the
  script.
- **Rejected: a project manifest** (a root `weir.sigs.json`
  holding the name→path mapping). It was the answer to a problem
  that dissolved — see decision 2b: locally there is only ever ONE
  signature per tool, so there is nothing to disambiguate and
  nothing for a manifest to hold. It returns in v2 only as the
  LOCKFILE, whose job is different (which registry file was
  fetched).

**2b. Signature filenames carry NO version — locally there is exactly
one signature per tool.** You regenerate against the binary you have;
keeping an old one would be pointless because nothing would ever
consult it. So `.weir/sigs/bicep.weir`, with the version as DATA INSIDE the
file (decision 2), and regeneration overwrites in place. That is what
keeps the default resolution unambiguous: one tool, one candidate,
no selection question.

**Multiplicity appears only in v2**, and for a structural reason: a
*registry* serves many users with different installed versions, so it
must hold `0.45.2.weir` and `0.43.0.weir` side by side. A project's
VENDORED copy is still one file per tool — you fetched the one that
matches you — and the lockfile records which registry file that was.
So the invariant holds across both versions: **locally, one signature
per tool, always.**

**2. Exact version, embedded, captured at generation.** `weir sig
generate bicep` asks the tool and records **the whole `--version`
output verbatim** — which fingerprints IDENTITY and VERSION in one
value ("Bicep CLI version 0.45.2 (abc1234)"), so a `bicep` that is
actually someone's shell script fails the comparison. Plus a
human-provenance line the generator writes
(`/// https://github.com/Azure/bicep`, `/// generated from
'bicep --version' on <date>`) — not machine-checkable, but it is what
a registry reviewer needs.

**3. NO RANGE SYNTAX, EVER — stated as a prohibition.** Not `>=`,
`~>`, `^`, `*`, or `0.45.x`. **The reasoning, so nobody re-derives
the zoo**: ranges exist to solve dependency-GRAPH resolution — many
packages, transitive constraints, a solver. Signatures have no graph:
one tool, one signature, one installed binary, compared pairwise.
There is nothing for a range to negotiate, and anyone proposing `>=`
is importing a solution from a problem shape that is not present.
- Patch churn is handled by REGENERATION: `0.45.2 → 0.45.3` means
  re-run generate and commit the diff — which will be empty or
  near-empty, and **an empty diff is the useful signal** (proof the
  surface did not change). A range would have hidden that.
- **The exhaustive/partial distinction is NOT a range in disguise**
  (it answers "is an undeclared flag an error?", about the
  signature's COMPLETENESS) — keep them separate in the docs,
  because "partial" is exactly the word someone would stretch into
  "compatible with later versions".

**4. Verification is its own command, never part of `check`.**
`weir sig verify` compares each signature's recorded `--version`
output against the installed binary and **hard-fails on any
mismatch** — exact match, no tolerance. It belongs in CI.
[Post-unification the command is the SHARED `weir verify`, which
gains a signature arm beside the landed hash arm — the two-arm shape
`Contracts.verify` was built for; see the arc's session 3 and
PLAN-restore-rename. The reasoning here is unchanged.]
- **`weir check` NEVER spawns a tool.** Two invariants would break:
  never-execute-to-check, and check's latency (a shebang language
  checks constantly; `az --version` is seconds, not milliseconds).
- The rule that generalizes, and it is the third instance:
  **a check that needs the environment is a VERIFICATION and deserves
  its own command** (`weir fetch` outside check; the freshness gate
  outside the compiler; now this).
- **Edit-time signal without the spawn**: the LSP runs verify
  ASYNCHRONOUSLY in the background and publishes a diagnostic. The
  LSP is already a long-lived process that can afford it; `weir
  check`'s hot path stays clean. Version-string caching keyed by
  binary path+mtime was considered and rejected — a cache that gates
  correctness is a class this project distrusts.

**5. Generation is explicit and allowed to execute.** `weir sig
generate <tool>` probes, in fidelity order: a completion endpoint
(`__complete`, `completion fish|bash`, clap's generator — covers most
Go/Rust tooling: kubectl, gh, helm), then a shipped **fish**
completion file (declarative-ish and parseable, unlike bash's
arbitrary functions), then `--help` scraping (universal, unreliable,
a starting point the user edits). The chosen source is recorded in
the file's provenance comment, because **a scraped signature is
approximate and the user must know which to trust.**
- This is the first time weir executes a tool to LEARN about it —
  allowed because it is a deliberate, explicit, user-invoked step,
  never implicit during check or completion. Same distinction as
  `weir fetch` versus implicit-on-check.

**6. THE NON-CLAIM, stated when this ships**: **signatures check your
invocations, not your binaries.** A hostile `bicep` earlier on PATH
still runs; a signature only constrains what weir ACCEPTS as a
command line. Same genre as word-integrity-is-not-flag-safety.

## The four leanings (revisitable with evidence)

**L1 — the declaration shape: a SUPERSET of `Args.load`'s model.**
Reuse the record/union shape (it exists, derives flags, is
checkable), and add what foreign CLIs have and weir's own CLIs
deliberately do not:
- **positionals** — `bicep build <file>`, `git add <path>`. Note the
  archaeology: `[<Positional>]` was DROPPED for weir's own CLIs
  because its only receipt was model-authored contract-mimicry. It
  returns here for a different and honest reason: **foreign CLIs have
  operands, and we are describing theirs, not designing ours.** State
  that distinction loudly or the drop reads as reversed.
- repeatable flags (`--label a --label b`), short clustering
  (`-abc`), multi-value flags, mutual exclusion — each is a model
  addition, each earns its way in by a real tool needing it. Start
  with the minimum L2 requires.

**L2 — v1 checks UNKNOWN FLAGS only.** Highest-value typo catch,
needs no operand model, and it lets the declaration shape stay
minimal until the model is proven. Missing-required-operand and
wrong-typed-value are v1.1 candidates, sized after the first real
signatures exist.

**L3 — generated signatures are PARTIAL (warn); hand-marked
exhaustive ones ERROR.** This is what makes the feature usable rather
than infuriating: a scraped signature is incomplete, and a check
error on a flag the scraper missed would get the feature ripped out.
The marker's spelling is a session decision (a module-level
attribute, or a declared field).

**L4 — v1 checks, then HOVER, then completes.** Both editor payoffs
fall out of the declaration for free; the ordering matters because
hover is the cheap one that proves the path.

**Hover (next after checking).** A command head with a signature
declared hovers with: the tool's identity, the version the signature
describes, exhaustive-vs-partial, and the provenance (generated from
a local binary, or fetched). **Flag hover is the more valuable half** —
`--outfile` showing its description is what people actually want,
since `--help` is a context switch.

- **It works whether or not the tool is on PATH**, and that is worth
  stating rather than leaving implicit. A signature is check-time
  metadata; the binary's presence is irrelevant to it. So hover (and
  checking) serve a real workflow: **authoring a CI script locally
  for tools that only exist in the runner.** Same property as
  everything else here — deleting the tool does not change what the
  checker knows.
- **The version-mismatch state belongs in hover too**: the LSP
  already runs `verify` asynchronously, so if verify would fail,
  hover is the natural place to say so.
- **Flag hover needs a lookup path** — command argv words are not
  expression nodes, so the hover walk must recognise "cursor is on a
  flag inside a command whose head has a signature". Same shape as
  the pattern-position work in the hover-completeness session; that
  is the precedent to reuse.

**Completion (after hover).** `bicep <TAB>` → subcommands;
`bicep build <TAB>` → flags. It is more LSP work than hover and it
should follow, because hover is read-only and proves the signature
data is reachable from the LSP — completion then inherits a proven
path. The standing rule holds either way: **completion never executes
anything** — it reads the signature.

## v2 — the registry (inherits the remote-imports prerequisites)

A blessed git repo of signatures, pinnable. **Why signatures are the
right FIRST remote artifact**: they are inert — nothing executes at
fetch, check, or run — so the scariest part of remote imports does not
apply.

**But the supply chain does not vanish, it changes shape**: a
malicious signature cannot run code, it can LIE — declare a flag that
does not exist, or omit one, so a script is written against fiction
and fails in production. A checker fed bad metadata produces wrong
CONFIDENCE, which for this project's pitch is a specific poison. So
the prerequisites carry over:

- **pinned revisions, never a floating branch**;
- **integrity hashes in a lockfile** (the pin is the SHA; the
  lockfile makes a re-fetch verifiable, and records WHICH signature
  file was used so a re-check is reproducible even as the registry
  grows);
- **an explicit `weir sig fetch`** — never implicit-on-check
  [post-unification: the `add`/`restore` family against a registry
  source — PLAN-restore-rename];
- **a vendor/cache directory**, checked in or ignored per taste, so
  builds work offline.

**Structure mirrors the no-ranges discipline**: `azure/bicep/
0.45.2.weir` — exact files, exact names, resolution is a LOOKUP not a
solve. No resolver, no SAT. A missing version is a stated absence
(generate locally, contribute upstream), never a fallback-to-nearest.

**The registry is where versions multiply; the project is not.** A
fetch lands ONE file per tool in the vendor directory, and `#sig
bicep` resolves to it exactly as it resolves to a locally-generated
one — so v1's directive is unchanged by v2, and a script cannot tell
whether its signature was generated or fetched. The lockfile is the
only new artifact: it records which registry file was pulled (path +
version + integrity hash), which is the manifest's job finally having
a reason to exist.

**Namespacing answers the provenance problem structurally**: the
registry can say "here is `azure/bicep` 0.45.2"; only your project
can say "the thing my PATH calls `bicep` is that". **The ambiguity is
irreducibly local, so the mapping is local** — and in v2 that mapping
is what the lockfile pins.

## The arc (each session its own bless)

1. **The declaration model + `#sig` + unknown-flag checking** (L1
   minimum, L2, L3) — the checker's command-resolution step gains a
   signature lookup; the directive with its upward walk and override;
   the partial/exhaustive marker. Pin: both directive forms; the walk
   finding a signature from a nested script; **the first-`.weir/`
   stop** (a `.weir/` lacking the tool errors rather than continuing
   upslope); **the `.git` boundary** (a repo does not inherit a
   parent's signatures); the missing-file error naming both the path
   and the searched-from directory; non-inheritance through an
   import; and that a declared-but-unused signature is inert (legal,
   not an error).
2. **`weir add sig <tool>`** — the three sources in fidelity order,
   the version capture, the provenance comment. Sized separately
   because the source-probing is the bulk. (Named `weir sig
   generate` when this design was written; the contracts
   unification folded it into the `add <kind>` family — see
   `PLAN-restore-rename.md`.)
3. **`weir verify`** + the LSP's async diagnostic — the shared
   command, gaining a signature arm beside the hash arm.
4. **(later) hover from signatures** — head and flag hover, incl. the
   works-without-the-tool-installed property and the verify-mismatch
   state (L4).
5. **(later) completion from signatures** (L4), after hover has
   proven the LSP path.
6. **(v2) the registry**, gated on the pinning/lockfile/fetch work.

## Bars

- **Signatures never change what RUNS** — only what checks. Pin it:
  a script with and without its signature produces byte-identical
  output.
- `weir check` spawns nothing — pin the negative (a fixture tool that
  would leave a marker file if invoked).
- Every named error pinned with exact text; the non-claim in
  SECURITY.md.
- The no-ranges prohibition and the positionals distinction are in
  DECISIONS with their reasoning, because both are the kind of thing
  a later session would otherwise "reasonably" undo.
