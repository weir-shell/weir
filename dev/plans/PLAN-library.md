# weir — library phase: strings, head, generic unions, measure algebra

Status: EXECUTED (landed 2026-07-16).

Driven by the first genuine library-gap dogfood finding: writing `head`
surfaced (a) no Option type, (b) no string operations. These are coupled —
every string session adds partial functions, and each partial function either
grows the runtime-failure class or mints a `try*`-seq workaround whose real
answer is generic unions. Sequencing follows that dependency:

  Session 1: strings + head/tryHead   (unblocks dogfooding, zero checker work)
  Session 2: generic unions           (checker session, human read, Option/Result prelude)
  Session 3: try* migration to Option (one sweep, retires the interim idiom)
  Session 4: measure algebra          (last standing pre-existing backlog item)

Sessions 1 and 4 are independent of each other; 2->3 is a hard dependency.
Dogfooding resumes after Session 1 and continues throughout.

## Pre-made decisions (do not relitigate mid-session)

- **Partiality convention** (interim, until Session 3): raising name /
  `try`-prefixed seq-returning name. `head : seq<'a> -> 'a` raises on empty
  (joins the existing runtime-failure class: match failure, boundary
  validation, div-by-zero). `tryHead : seq<'a> -> seq<'a>` returns 0-or-1
  elements. Rules-doc documents this as **interim, with the intended
  migration to Option written down now** — the seq idiom must not accrete
  into case law.
- **String argument order: data-last, curried.** Needle/pattern first,
  subject last: `contains : string -> string -> bool`. Rationale (pin in the
  doc): partial application yields point-free pipeline predicates —
  `where (contains "error")`, `where (startsWith "fix:")` — no lambda. This
  is the decision that compounds; do not ship any string builtin data-first.
- **Name collision policy: `strLen`**, not polymorphic `length`, not member
  access. Member-access-on-primitives (`s.Length`, `map _.Length`) is logged
  as a candidate design (rides EField, real F# feel) but is a checker change
  and is NOT coupled to a builtins session. Log, don't build.
- **Regex is out of scope** for this plan. It's coming (grep-in-the-language)
  but is its own design (match vs captures vs typed groups); improvising it
  into a builtins session is how APIs go wrong. Backlog entry, not a session.
- **Generic unions are user-declared machinery, not builtin magic.** Option
  and Result ship as *prelude declarations* using the same `type
  Option<'a> = Some of 'a | None` any user could write. No special-cased
  types.
- **Measure algebra scope** (from the standing backlog): scalar-times-measure
  only (`f.Size * 2 : int<mb>`), which forces unit equality to become
  normalization-based (reopens checklist 4.2) and forces redesign of the
  `*`/`/`-binds-to-unitless-int defaulting rule. Division, measure-times-
  measure, and conversion (`int<mb> -> int<gb>`) remain out.

## Session 1 — strings + head/tryHead (no checker changes)

1. **Seq additions**: `head` (raising), `tryHead` (0-or-1 seq), `isEmpty :
   seq<'a> -> bool`. While in the neighborhood, audit the seq builtin set
   against the original dogfood battery needs: `sortBy`, `groupBy` — if
   either is missing, add here (both were [guess]-flagged gaps in the task
   list and both are pure builtin work). `groupBy`'s return shape needs one
   decision: seq of `{ Key; Items }` records (declare a builtin-owned nominal
   record, same pattern as Completed).
2. **String builtins**, all data-last: `contains`, `startsWith`, `endsWith`,
   `trim`, `trimStart`, `trimEnd`, `split : string -> string -> seq<string>`
   (separator first), `join : string -> seq<string> -> string`, `replace`
   (pattern, replacement, subject), `toLower`, `toUpper`, `strLen`,
   `toInt` (raising), `tryToInt` (0-or-1 seq).
3. **Deferred, logged in rules doc**: `substring`/`indexOf` (want Option —
   Session 3 customers), padding, regex (own design).
4. **Tests**: each builtin unit-tested; plus pipeline-idiom tests that pin
   the data-last payoff verbatim: `lines | where (contains "x")`,
   `split "," "a,b" |> join ";"`. E2E: the git-branch-cleanup dogfood task
   (untyped `git branch` lines -> trim -> startsWith filter) added to
   ci/e2e.sh — per the institutionalized rule, boundary behaviors get e2e
   tests against the AOT binary, not unit tests.
5. **SEMANTICS.md**: partiality convention (marked interim, migration
   intent stated), data-last rule with rationale, strLen collision decision,
   member-access candidate logged.

**Done when:** the git-branch-cleanup task runs end to end in weir; suite +
e2e green; dogfooding resumes with this session merged.

## Session 2 — generic unions (checker session; human read required)

The first inference-relevant checker change since the audit. Tripwire suite
re-run explicitly; checklist section-3 (generalization) items reopen because
constructor schemes must freshen per use exactly like generalized let
bindings.

1. **Declaration grammar**: `type Name<'a, 'b> = Case of 'a | Case2 of
   'b * 'a | Case3`. Parameterized records too if free
   (`type Pair<'a> = { Fst: 'a; Snd: 'a }`) — decide in-session whether
   records come along or unions only; either is fine, document which.
2. **Checker work**:
   - Type representation gains applied constructors (`TApp(name, args)` or
     equivalent — note `seq<'a>` already exists builtin-side; unify the
     representation rather than adding a parallel one).
   - Unification through applied constructors: `Option<int> ~ Option<'a>`
     binds `'a := int`; arity mismatch is an error; occurs check applies
     through arguments.
   - Constructors are generalized schemes: `Some : forall 'a. 'a ->
     Option<'a>`, instantiated fresh per use — same machinery as generalized
     lets (`instantiate` deep-copy discipline applies; this is the section-3
     reopening).
   - Pattern matching: `match o with Some x -> ... | None -> ...` binds `x`
     at the instantiated type; exhaustiveness recurses through applied
     constructors.
   - Equatability (`==`) recurses through applied constructors (Option<int>
     equatable, Option<int -> int> not).
   - Printing: `Some 3 : Option<int>` in REPL output.
3. **Prelude**: Option and Result declared in a prelude evaluated at session
   start (mechanism: the same path as user decls — this also creates the
   prelude concept, which future sessions will use; keep it a plain weir
   source file, not host-code registration).
4. **Adversarial battery** (extend the audit's): constructor scheme shared
   across two uses at conflicting types (must both succeed — freshening),
   `Some (fun x -> x)` then `==` on it (equatability rejection),
   `Option<Option<int>>` nesting, occurs through constructor
   (`'a ~ Option<'a>` rejected), arity error, exhaustiveness warning with
   nested constructor patterns, generalized let returning `Option<'a>`
   used at two instantiations.
5. **Human read targets**: the unification arm for applied constructors and
   the constructor-instantiation path (against the section-3 checklist
   items). Judgment-on-paper for both.
6. **SEMANTICS.md**: generic declarations rule, constructor-scheme rule with
   cross-reference to the generalization regime bullet, prelude concept.

**Done when:** Option/Result usable as if builtin but declared in prelude;
adversarial battery green; tripwires green; human read done.

## Session 3 — try* migration (one sweep)

1. `tryHead : seq<'a> -> Option<'a>`, `tryToInt : string -> Option<int>`,
   plus any `try*` added during dogfooding between sessions.
2. Add the deferred Option customers now that the type exists:
   `tryFind : ('a -> bool) -> seq<'a> -> Option<'a>`, `indexOf` ->
   `tryIndexOf : string -> string -> Option<int>`, `substring` (raising,
   with documented bounds behavior).
3. **Breaking-change handling**: this changes `tryHead`'s type. Pre-1.0,
   single-user — just break it; grep dogfood history for uses. The rules-doc
   interim marker is replaced by the final convention: **raising name /
   `try`-prefixed Option-returning name.**
4. Idiom tests pinning the payoff: `tryHead xs |> match ... with Some x ->
   ... | None -> ...` and Option-in-pipeline ergonomics — decide and pin
   whether `Option` gets helpers (`defaultTo : 'a -> Option<'a> -> 'a`,
   `mapOption`) in this sweep (recommended: yes, they're the idiom's other
   half; without `defaultTo`, every Option forces a match).

**Done when:** no seq-idiom `try*` remains; SEMANTICS.md convention updated;
e2e battery still green.

## Session 4 — measure algebra (scalar-times-measure)

Standing backlog item, now last. Reopens two documented rules; both
cross-references were written in advance — follow them.

1. **Unit equality becomes normalization-based** (checklist 4.2 goes live):
   with only scalar-times-measure the algebra is degenerate (no compound
   units yet), so "normalization" reduces to: measure tags survive
   multiplication by unitless int on either side. Keep the representation
   `string option` if it suffices; the 4.2 tripwire (`no_unit_algebra_means_
   no_normalization`) is retired and replaced by real normalization tests.
2. **The defaulting rule redesign** (pre-flagged in the rules doc): `*` on
   two unresolved operands no longer has "the only sound reading." Decide,
   document, pin: recommended — `*` requires at least one operand resolved
   to unitless int; two-unresolved is now an error asking for annotation
   (consistent with the governing principle: reject rather than guess).
3. **Typing rules**: `int<m> * int : int<m>`, `int * int<m> : int<m>`,
   `int<m> * int<n>` remains an error, `/` unchanged (unitless only) this
   session.
4. Adversarial: `f.Size * 2 > 1<mb>` through a row constraint (the 4.1
   UoM-row interaction re-probed with the new rule), measure survival
   through `map (fun s -> s * 2)`, the old `f.Size * 2` rejection test
   inverted.
5. Human read: the binop typing arm — small, but it's the 4.2 reopening.

**Done when:** `f.Size * 2` works, sum-then-scale pipelines work, tripwire
replacement in place, rules doc's two pre-written cross-references resolved.

## Deliberately NOT in this plan

Regex (own design, next plan), member access on primitives (logged
candidate), measure conversion (`int<mb> -> int<gb>`), measure division,
optional record fields / `from json` optionality (if dogfooding hits it,
that's a row/record design conversation, not a session), globs/redirects/
env-prefix/chaining (unchanged exclusions).

## Claude Code hygiene

- One session per branch. Session 1 merges first and dogfooding resumes
  immediately — sessions 2-4 proceed in parallel with live use.
- Human-read targets: Session 2's applied-constructor unification +
  instantiation (judgment-on-paper), Session 4's binop arm. Sessions 1 and 3
  review by behavior/tests.
- Tripwires re-run explicitly in Sessions 2 and 4 (checker changes).
- E2E rule stands: any done-when behavior gets a ci/e2e.sh entry against the
  AOT binary.
- After Session 4: the pre-existing backlog is empty. The next plan is
  written from dogfooding data only (`grep sh ~/.weir_history`, the
  times-left-weir log, and whatever the string/Option idioms surface).
