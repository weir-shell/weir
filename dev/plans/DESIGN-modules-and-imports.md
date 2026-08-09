# weir — modules and imports: the design

Status: BLESSED (user 2026-07-28) — the DESIGN is settled across
eleven questions plus the `module` marker; this file is the record of
those decisions and the scope statement for the implementation arc
that follows. **The implementation is a separate arc of sessions**,
each blessed on its own; this plan's deliverable is the design plus
the engineering scope it implies.

Why it is being built: **adoption, not a receipt.** A language whose
answer to "how do I share code between two scripts" is *"you don't"*
does not get evaluated past a user's first serious script. The
receipts regime is structurally silent here (there are no users to
file receipts) — the same category as editor support and Duration,
both of which the user correctly prioritized ahead of the advisor's
sequencing.

## VOCABULARY (enforced, per the one-word-one-meaning discipline)

The unit is a **module**. Builtin namespaces (`Seq`, `Str`, `Env`,
`Args`, `Self`) and user-authored ones are the SAME concept with
different origins — LEXICON states that unification. **"library" and
"package" are retired as synonyms**; "dependency" survives for the
RELATIONSHIP ("the dependency graph", "a dependency's errors"), and
"import" is the verb and the statement. Every doc, error message, and
row uses `module`; a grep at the arc's end catches strays (the
idioms-rot sweep's shape).

## THE FILE SHAPE

    #!/usr/bin/env weir          -- optional
    // comments anywhere
    module Git                   -- or bare `module` (name from filename)
    import "./lib/paths.weir"    -- imports next, before declarations
    type Ctx = { … }             -- declarations at column 0
    let revParse r = …

## The decisions

**1. The unit is the file, marked.** A `module` declaration makes a
file a module: importable, declaration-only, not runnable. `module`
alone derives the name from the filename (F#'s fallback shape);
`module Git` overrides — which is the answer to "hard to guess what a
weird filename derives". At most ONE `module` per file, and it comes
first (after shebang/comments): with no body and no `=`, a second
declaration has nowhere to live, so one-per-file needs no extra rule.

**2. `module` is top-level: no `=`, no indented body.** Three
reasons, all load-bearing: an indented body would fight the col-0
statement law (a module's contents ARE statements); one-per-file
falls out for free; and `=` binds values in weir — a module is not a
value. `module` joins the reserved words (the keyword-domination work
applies; one pin).

**3. Access is ALWAYS qualified.** `Git.revParse`, or `G.revParse`
with an alias. **No named imports, no `open`, no
`RequireQualifiedAccess`-style opt-out** — weir gets to skip the
mistake F# patched: unqualified imported names would fight the
bare-command grammar (a bare `deploy` competing with PATH resolution
and local bindings, with nothing at the use site saying where it came
from), and it would destroy the property that the closed bare
hot-path table is the COMPLETE list of names that can appear bare.
One way to do it.

**4. The statement has exactly two forms**: `import "path"` and
`import "path" as Name`. The path is a **literal string only** (no
interpolation, no computation — resolution is check-time; a teaching
error says so). `as` requires an **uppercase** name (the casing law:
uppercase declares). Ruled out with reasons: named imports (3),
version specifiers (the remote conversation, parked), conditional
imports (a missing import is a check error, not a runtime absence),
re-export (belongs with `pub`), multiple paths per statement (noise).

**5. Imports come before all declarations.** Shebang and comments
free above; `module` first, then imports, then `type`/`let`. A
declaration above an import is an error naming the rule. `///` above
an import does NOT attach (a doc there documents your *reason for
importing*, which is a comment's job) — no sixth documented
position, no warning, one docs line.

**6. Types cross the boundary.** Values-only was rejected: any
function whose signature mentions a module's type would be unusable,
and a shared `Ctx` plus helpers over it is the actual want (the
subrepo demo/tool copy-divergence).

**7. Record-literal inference: the collapsed rule.** Imported record
types JOIN the inference pool. A bare literal `{ a = 1; b = 2 }`
resolves if exactly ONE record in scope has that field set;
otherwise it is an **error at the literal**, naming the candidates
QUALIFIED and the fix. **Declarations never collide** — two records
with the same field set are legal, local or imported.
`Git.Ctx { … }` / `G.Ctx { … }` / `Ctx { … }` (a
**qualified-literal form**, a small grammar addition) always works
and reads better at distance regardless of ambiguity.
*This reverses an earlier decision in this same design session — a
declaration-time collision error was considered and rejected because
its only fixes were bad (rename a field in your own type because a
module you imported has the same shape). Recorded as a reversal with
its reason: every error must have a good local fix. The literal-site
error has one; the declaration-site error did not.*

**8. Everything top-level is exported (v1), and the future is
`pub`.** No marker, no privacy — justified by scope (imports between
scripts you own). **The direction is decided now** so the later
session is mechanical: explicit `pub`, NOT implicit-export-with-
`private` (F#'s shape means a module's API is whatever the author
forgot to hide). **The leak rule is decided**: a `pub` value's
signature may only mention `pub` types — a private type reaching the
outside through a public signature is a check error. The migration
(unmarked goes public→private) is breaking and accepted; **the
trigger is distribution**, because that is when "who can see this"
starts mattering. Noted for that session: a `pub` signature
mentioning a THIRD module's type is legal, discouraged, and a GUIDE
line ("it makes that module part of your API; prefer converting at
your boundary") — a lint if it ever needs teeth, never a check
error.

**9. Cycles and self-import are check errors.** Self-import gets its
OWN message (*"a file cannot import itself"*) because rendering a
one-element chain reads oddly; same detector, different rendering.
**Path identity**: resolve to an absolute, normalized path for cycle
detection and for import CACHING; **do not resolve symlinks** (the
`Path.glob` precedent: explicit paths follow links, so two paths
reaching one file via different links are two files) — decided, not
inherited from `GetFullPath`'s defaults. **Diamonds are not cycles**:
`a`→`b`,`c`→`d` is legal and `d` is checked ONCE with its namespace
shared.

**10. A module is DECLARATION-ONLY, and that is a property of the
file.** A `module` file may contain only `type` and `let`. No bare
command statements, no `!(…)`, no districts, no `print`, no
top-level expression statements — a check-time rule, checkable in
isolation, so `weir check lib/git.weir` enforces exactly what an
import would. **Plus the weak purity rule**: a module `let` whose
RHS is SYNTACTICALLY a command (bare command RHS, `$(…)`, `!(…)`, a
reifier chain) is rejected — that catches the realistic mistake (a
command running at import) without requiring purity analysis, which
weir does not have. `let x = someComputation ()` still evaluates at
import, which is normal.

**11. Running a module errors**, naming the rule and the escape: *"a
module declares; it does not run. To run a script from a script,
invoke it as a command."* The marker is what makes this message
possible — a statement-less SCRIPT is a different situation ("nothing
to run") with a different message. And **importing a non-module file
errors exactly**: *"./tools/deploy.weir is not a module; add `module`
at the top, or invoke it as a command."*

**12. `Self` splits: `Self.scriptPath` and `Self.entryPath`.**
`scriptPath` is the FILE'S OWN path (the common need — locating
sibling resources); `entryPath` is the invoked script's. The short
name goes to the local one because "which file am I in" is the
frequent question and "what did the user invoke" is rare — and every
existing use site stays correct (today a file IS the entry, so
`scriptPath` means the same before and after: no migration).
`Self.args`/`Self.stdin` are PROCESS facts (one argv, one stdin), so
they are the entry's regardless of which file reads them — the
asymmetry is stated in the docs, not inferred. **`import` is
script-only**: `-e "import …"` errors with the script-only family
message (there is no file to resolve relative paths against).

**13. Diagnostics: the real error at its own site, PLUS a note at
the import line** (option (c)). `weir check main.weir` checks the
whole graph, reports module errors at their own file and line, and
**fails** on them. `weir check lib/git.weir` alone works — a module
is checkable as an entry point.

**14. The LSP invalidates dependents** (option (b)): the server
tracks reverse edges and re-checks importers when a dependency
changes. Diagnostics are published **per URI**, including for
files the client has not opened (hiding a real error until someone
opens the file would be the guarantee lapsing at the editor
boundary). **When a dependency IS open, the client's buffer wins
over disk** — otherwise you check the saved version and report
errors already fixed.

**15. Resolution is a PATH, resolved by one function.** Not an
"opaque specifier + strategy" — that framing bought nothing (both
resolve at check time; both error on a missing file). The version
that costs nothing: call it a path, and write resolution as ONE
function (`importingFile -> path -> Result<content, error>`) so a
future scheme branch has one seam. A missing or unreadable import is
a **check error at the import line, with the RESOLVED ABSOLUTE PATH
in the message** (relative-resolution confusion is the usual
failure).

## THE ENGINEERING SCOPE (what decision 13 turns this into)

**Diagnostics must carry a file.** Today they do not — the segment
table maps a span to *the* file, and every consumer assumes one.
Multi-file means:

- a diagnostic gains a file identity;
- **`translate` becomes per-file** — each file has its own line map,
  and a span must translate through ITS OWN file's map (the Session D
  lesson exactly: a raw span riding across a boundary translates
  through the wrong map, which is why row origins are captured
  physically);
- the LSP publishes per URI;
- `check --json` grows a file field, and its consumers move;
- the fuzzer and the corpus stay SINGLE-FILE, stated as their
  denominator (GRAMMAR.md).

**This is what makes the arc large**: decision 13 turns a resolution
feature into a multi-file compiler. The plan says so rather than
discovering it.

**The security non-claim changes and must be revised.** SECURITY.md
currently says the LSP reads client-sent text and does not read
files the editor did not send. With imports that becomes false:
resolving a dependency means reading it from disk when unopened.
New wording: *the server reads client-sent text, plus files
reachable by import from an open document.* A stale security claim
is the lying-comment class — revise it in the session that lands
LSP resolution, not later.

## The arc (each session its own bless)

1. **`module` + `import`, single-hop, CLI only** — the marker, the
   statement, path resolution, the declaration-only rule, the
   qualified-literal form, cycles/self-import, the not-a-module and
   running-a-module errors. Diagnostics still single-file: a module
   error in this session may report at the import line only, IF
   splitting that out keeps the session honest — state which.
2. **Multi-file diagnostics** — the file identity, per-file
   `translate`, `check --json`'s field, the at-its-own-site error
   plus the import-line note (decision 13 completed).
3. **The graph** — transitive imports, diamonds checked once,
   caching keyed by normalized absolute path, cycle chains.
4. **The LSP** — reverse edges, dependent invalidation, per-URI
   publishing, buffer-over-disk, and the SECURITY revision.
5. **`Self.entryPath`** — can ride session 1 or 3; small.

Parked with triggers: **`pub` and privacy** (trigger: distribution);
**remote imports** (their own arc, with pinning/lockfile/fetch-step
prerequisites — never implicit-on-check); **in-file modules** (a
`module` block inside a file — the marker's design deliberately
forecloses it; reopen only against this entry).

## Bars for the whole arc

- **Check-before-effects survives the file boundary** — this is the
  reason the whole design is shaped as it is. A module is resolved
  and checked at check time; nothing is loaded at runtime; `weir
  check` on an entry means the same thing it means today, across the
  graph. Any design pressure toward runtime resolution is a
  stop-and-report.
- Zero movement on single-file behavior at every step.
- Every error named in this plan gets a pin with its exact text.
- The vocabulary grep at the arc's end.
