# weir — `Seq.fold` + `fun a b ->` sugar

Status: EXECUTED (landed 2026-07-22) — as blessed: LANDED 2026-07-22 (blessed same day). Origin: the git-subrepo
translation receipt — one load-bearing gap; the library-phase sleeper
prediction fired with its receipt.

Completion addenda:
- Probes ruled twice: (a) F# REJECTS duplicate lambda params — and
  the probe caught weir's LET-param sugar accepting them (latent
  divergence); dup rejection now ships in BOTH positions (one rule).
  (b) Three probe shapes were amended in-session for wrong-reason
  rejects (weir has no `string` builtin — interp is the conversion;
  the +-on-unknowns limit collided with the currying and empty-fold
  shapes; isolating shapes substituted, limits documented).
- The landing surfaced a REAL checker gap: check-mode's hasVars
  fallback inferred nested lambda bodies, dropping the resolved
  inner domain — the canonical piped sum rejected. Fixed as a
  one-clause push-through (nested lambda vs TFun cod checks
  directly); zero pin movement.
- `fun a b ->` sugar needed NO checker adapter at all — pure parse
  desugar through curryParams (less than budgeted; flag 7's rent
  paid differently than predicted).
- Ride-alongs: Env.pair AND Env.ofPairs (both, two lines each; the
  three-var receipt reads best with ofPairs); ctor-pattern-scrutinee
  tagged live: git-subrepo. Anon-records disposition recorded in the
  builtins comment and SEMANTICS. Nothing else rode.
- The unpiped arithmetic fold (`Seq.fold (fun s x -> s + x) 0 xs`
  un-piped, or over `[]`) hits the documented anchor-one-side limit;
  the PIPED spelling is the taught idiom and anchors fully.

[Blessed text: see the plan message; all DECIDED items held or
amended as above.]

## Parked (unchanged)

- `Seq.reduce`/`scan`/`foldBack` — own receipts required.
- Multi-line strings — receipt logged, park holds.
- The git-subrepo port as flagship — follow-up decision; the
  graduation-paragraph refinement rides the next docs pass.
