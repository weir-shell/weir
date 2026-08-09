# weir — mini-plan: `Seq.choose` + the verbatim-highlighter fix

Status: BLESSED (user 2026-07-23). One short session, one member +
one tooling fix, both receipt-bearing from the flagship.
EXECUTED 2026-07-23 — see the completion addenda.

## Seq.choose — the partial-map member

Origin: statusRefs in the flagship — map-to-`""`-then-filter-empties,
the sentinel-empty idiom that Option exists to replace, now taught by
example in the showcase script. The natural consumer of the Regex
pattern (match-or-skip over a stream is grep's own shape).

- DECIDED — `Seq.choose : ('a -> Option<'b>) -> seq<'a> -> seq<'b>`
  — F# parity (oracle-probed), LAZY, qualified-only, plain generic
  scheme.
- DECIDED — the flagship rewrite is the e2e; `Seq.choose id` decided
  in-session (see addenda).
- DECIDED — NOT ridden: `Seq.tryPick`, `Seq.pick`, `List.choose`.

## Rider — the verbatim-region highlighter fix (micro, live bug)

Symptom: everything after `@"\", "%5c")]` in the flagship highlights
as string. Plan's root cause: the repo verbatim region carrying a
backslash skip. See addenda — the root cause was corrected.

## Completion addenda (2026-07-23)

### Seq.choose, landed

Signature/laziness/scheme as decided; 3 oracle pins Same (both
languages reject a non-Option chooser); 6 unit pins (the Regex-arm
idiom, infinite-source laziness, all-None empties, constraint-free
row-projection, qualified-only, non-Option check rejection);
statusRefs reads match-or-skip and the live repo-pair smoke is
green ("the fetch ref surfaces through the Regex pattern" now
exercises choose). SKILL and GUIDE teach the idiom with doc-tested
blocks next to the Regex teaching; no other doc carried the
sentinel-empty idiom (swept).

**`Seq.choose id` decided: the lambda stands.** `id` does not exist
— probing `id 5` runs the PATH *binary* `id` (command mode owns the
name at statement level), so an `id` builtin is not merely unbuilt
but name-contested. Not ridden; noted with the receipt.

### Rider: ROOT CAUSE CORRECTED — the repo file was never broken

The decided fix describes content the repo has carried since the
VS Code session (779e703): verbatim skip `""` only, triple listed
first, plain keeping its `\.` skip — verified across every commit
touching the file; no version ever had the copied backslash skip.
The SYMPTOM was real and the mechanism as described, but the
carrier was the INSTALLED copy (`~/.config/micro/syntax/weir.yaml`),
stale from the pre-raw-strings era: it has NO verbatim region, so
`@"\"` opens the PLAIN region whose `\.` skip eats `\"` — the
region closes at the wrong quote and the trailing quote opens an
unterminated string. Sixth member of the stale-artifact class,
first in editor config.

What landed: the installed copy synced from the repo (the container
side; the user's machine copy needs the same `cp`); the inventory
guard's presence-not-semantics limitation on record in its comment,
with the flagship's encodeSubref line named as the by-eye canary (a
committed repro file was considered and dropped — it cannot guard
against a stale INSTALLED copy, which is the actual failure class,
and the flagship line already shows it). The TextMate twin was audited by hand: its
verbatim rule (`end: "\"(?!\")"` + `""` escape) is correct — no fix
needed there either.

**User-side action**: re-copy `editors/micro/weir.yaml` to
`~/.config/micro/syntax/weir.yaml` on any machine where the symptom
appeared — the fix is installation, not code.
