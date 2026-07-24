# weir — shared flags by containment: the union-typed field

Status: BLESSED (user 2026-07-24). One session. Origin: the port's
quiet/verbose extraction friction (two three-arm matches to pull
flags every subcommand carries — the shared-flags shape, sighted
twice: the bicep original's Argu `[<Inherit>]`, reshaped away; the
port, paid in boilerplate). The design premise, settled at review:
**containment expresses inheritance, and no attribute is needed** —
an `[<Inherit>]` marker inside each payload would PRESERVE the
repetition (the disease itself); a containing record deletes it
structurally.

(Blessed plan text as delivered; the executed decisions below.)

## Session report (2026-07-24)

Executed to the letter; every DECIDED bullet held, including the
in-session decision (case-scoped `--help`: YES, pinned).

- **Field law**: exactly one clause — a record with ONE
  monomorphic-union-typed field is the shared shape (`ArgsShared`);
  two-plus rejects ("one subcommand slot"); zero is the shipped
  record shape verbatim. Payload validation hoisted
  (`unionPayloads`) and shared with the bare-union arm — one owner.
- **Collisions reject at check**, both routes pinned (kebab-derived
  long flags: "shared flags are declared once"; explicit [<Short>]
  claimed in both tiers). The runtime scanner never faces the
  question.
- **Two-tier scan**: `argvFindCase` (pass 1 — shared flags float,
  first non-flag token anchors; unknown flags consume no value, the
  standing precedent) then one classified walk. Scope short tables
  derive over the UNION of tiers via a combined pseudo-record
  (`scopeDef`) — the cross-tier contest cell (-q for --quiet vs
  --query → neither derives in that scope; ambiguity error names
  both; global help omits the short, scoped help shows it) fell out
  of `shortTables` unmodified.
- **Validation**: collect-then-raise across both tiers, one
  boundary error; tier-aware did-you-mean (before: shared + case
  names; after: shared + selected payload) — pinned with a typo on
  each side.
- **The union field's name derives no flag** (`--cmd` →
  unknown-flag) — pinned.
- **Zero-diff bar held**: the shipped argv e2e section passed
  untouched; jira-branch record-only unchanged; no pin edits on the
  existing shapes.
- **Flagship**: the port's Cli gained the containing record;
  quiet/verbose deleted from six payloads; the two extraction
  matches deleted; lifecycle smoke green. The friction entry closes
  with its arc (sighted at bicep → paid at the port → deleted by
  containment).
- Ceremony: battery (e2e 281, all invocation shapes × tiers ×
  validation), TRANSCRIPTION addendum, DECISIONS row, docs (SKILL's
  three loader shapes; GUIDE's containment idiom with the one-line
  Argu comparison — the GUIDE block is executed by skill-doc);
  timing green (8/16/11ms medians). The fuzzer is NOT owed (argv
  parsing is not assembler grammar — the grammar-membership rule's
  scope stays crisp).

## Parked (as blessed, standing)

- Nested subcommand hierarchies (union inside a payload's record)
  — no receipt; one anchor level is the deliberate budget.
- Shared flags on a BARE union load — incoherent by construction
  (nowhere to declare them).
- [<Inherit>]-style per-payload marking — rejected at review with
  its reason (preserves the repetition); re-askable only against
  this entry.
