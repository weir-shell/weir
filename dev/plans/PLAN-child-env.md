# weir — child-env injection: the shEnv receipt

Status: EXECUTED (landed 2026-07-20) — as blessed: BLESSED (2026-07-20). Executed on branch child-env (stacked on
assembler-formalization). Blessed decisions as received: explicit
per-call cmdEnv/runEnv (ambient combinator and stream-wrapping
rejected, recorded); overlay semantics (removal out of scope,
empty-string workaround); Env.fromFile dotenv-subset parser with
reject-don't-guess boundary errors naming the sh escape; layered
sugar story (Layer 0 ships, 1-2 parked pre-scoped with split reopen
triggers, 3 rejected tombstone-style); fromFile feeds cmdEnv not
Env.load; bicep deployStack shape as acceptance e2e. Includes the
two-value-options disposition (parked, idiom documented).

## Completion notes (2026-07-20)

All five items DONE. (1) Proc.linesWith overlay; `lines` IS
`linesWith []` — shared path by construction; byte-identity,
raise-at-force, and tree-kill e2e'd on the env variants. (2)
Env.fromFile hand-parses the subset — the formalization scanner was
NOT reused (it speaks weir-string quote rules and compiles later than
Builtins; dotenv's quoting is its own three-case grammar — reported
per the plan's ask). Grammar + all rejection classes pinned, each
naming the escape; single-quote-is-shell-literal ($ allowed) pinned
both directions. (3) Overlay pins: set/override/inherit in one e2e;
parent isolation; empty-string workaround. (4) The bicep acceptance
e2e runs the deployStack shape (fixture env file, stub asserting the
child saw the overlay); examples/bicep-deploy.weir rewritten on the
Layer-0 idiom — the typed path came out STRONGER than the sh-c
translation (client-id/tenant now flow as argv from Seq lookups where
shell expansion had silently passed empty). Timing holds. (5) GUIDE
worked idiom (doc-tested, 26 blocks), SKILL rules, SEMANTICS entry
with the full layer ledger + Layer-3 tombstone + both dispositions;
the Env.set park dissolved in place with pointer. 517 unit / 43
oracle / 109 e2e / 26 doc blocks green.

## Layers 1+2 addendum (2026-07-20, same day)

User-opened ("ok do layer 1 and 2 now") — NOT receipt-triggered; the
trigger discipline was overridden by choice, on record. Both shipped
in one session (branch env-sugar), which the pre-scoping predicted
would be the cheap path: they share the line-end seam and the `!name`
meaning was decided ONCE (district header; a literal `!word` final
arg needs quotes — classifier-pinned). Layer 1: sigilOpen env slot,
env threaded through commandSegment/cmdLineWith at CONSTRUCTION so
every spawn form gets it by architecture — segments, stages, and
`| complete` via the new completedEnv (completedWith [] = completed,
the cmd/cmdEnv pattern again); ECmd/TECmd gained an env field
(checked seq<EnvVar>, evaluated inside the stream delay —
raise-at-force preserved). Layer 2: pure assembler work per the
formalization's promise — a MarkerKind variant (NoMarker/Bare/Env)
plus parameterized district joins (JDistrictOpen strip+opener); the
district emits `!name(...)` text that Layer 1 reparses. Zero
new eval/checker surface for Layer 2. 527 unit / 116 e2e checks / 27
doc blocks / 43 oracle / timing green. The formalization plan's
success test finally ran forward: this session's diff touched the
classifier and Join types, not raw string logic — the rule held.
