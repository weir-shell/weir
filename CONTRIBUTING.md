# Contributing to weir

Thanks for looking. Before you open a PR, read the unusual part —
it changes what a good contribution looks like here.

## How this project actually works

**weir is developed almost entirely by an AI agent working from
human-blessed plan documents.** A human writes and blesses a plan;
the agent executes it against a heavy evidence discipline; the human
reviews and merges. That discipline is the project's real interface,
and it applies to every change regardless of author:

- **Decisions are rows.** Every design ruling lives in
  [docs/DECISIONS.md](docs/DECISIONS.md) as a keyed row; code
  comments cite keys (`[D:some-key]`), never re-explain. A feature
  without a row is half-landed.
- **Claims are pinned.** Behaviour ships with tests that would fail
  if the claim drifts — unit pins against messages and values, e2e
  pins against the compiled binary, doc examples that execute in CI
  (`ci/skill-doc.sh`), an F# oracle for fidelity claims, and a
  metamorphic fuzzer. "It works" is not evidence; a pin is.
- **The battery is the bar.** `ci/local.sh` mirrors CI: unit,
  publish, e2e, fuzz smoke, doc-tests, oracle, timing. Green before
  review, on every platform the matrix covers.
- **Receipts before features.** New surface area waits for a
  demonstrated need (a "receipt"); parked ideas carry their
  reopening trigger in the ledger. A PR adding speculative surface
  will be asked for its receipt.

## What a contribution needs

1. **An issue first** for anything design-shaped — the decision
   discipline means design happens before code, and a PR that
   embodies an unmade decision is hard to review kindly.
2. **Pins for every claim** the change makes, in the layer that can
   see it (unit / e2e / doc-test / oracle).
3. **A DECISIONS row** (or an amendment to one) if the change rules
   anything — with the pin sites cited in the row.
4. **The battery green**, including the doc-tests if you touched
   any fenced example.
5. Bug reports are gold, need none of the above, and the fastest
   route is a failing `.weir` snippet.

## Style

Match the file you are in. Comments state constraints the code
cannot (`[D:key]` citations welcome); no history narration. Messages
speak the user's language — see the error-message rows in
DECISIONS for the house rules.
