# weir — site search: client-side, two tiers

Status: APPROVED (2026-09-13, designer: "plan and execute"). Independent
of the language; rides after v0.0.30 without touching it.

## Shape

Static Astro site, no server — search is CLIENT-SIDE, two tiers in one
Ctrl-K modal (a header button on every page opens it):

1. MEMBER TIER — exact/prefix/substring over the shipped
   `site/src/data/reference.json` (the docs-json dump: every member,
   signature, doc first-line). ~220 entries: a hand-rolled filter, NO
   library. Hits deep-link to the reference anchors ([D:ref-anchors]
   made them stable and gate-checked). Because the index IS the
   binary's own dump, member search can never drift from the language —
   the one-source property #help has.
2. PROSE TIER — Pagefind over the built site (postbuild
   `pagefind --site dist`): static chunked index, lazy-loaded only when
   search opens, zero effect on the no-JS baseline. Scope with
   `data-pagefind-body` on content layouts; exclude nav chrome; include
   docs/guide/reference/showcase, changelog optional.

Member hits render ABOVE prose hits — symbol queries (`Seq.groupBy`,
`from xml`, `$-`) are what weir users actually type, and generic
tokenizers are weak exactly there.

## Laws / guards

- The modal's JS loads on open, not on page load.
- A `site-staleness`-style postbuild check: the Pagefind index exists in
  dist, and the member tier's JSON parsed — a broken postbuild cannot
  ship a dead search box.
- No new content pipeline: reference.json is already generated and
  currency-gated (e2e's docs-json diff); Pagefind indexes rendered HTML.

## Environment contingency

The container may lack npm-registry access or a workable site build.
Order of work so everything landable lands: member tier first (pure
component + client JS against committed data — verifiable without a
build), Pagefind wiring second (package.json + workflow postbuild +
component include), with any unverifiable half explicitly deferred to
CI/user-side and said so in the report.

## Footprint

site/ only: search modal component + header trigger, member-tier JS,
Pagefind postbuild wiring (package.json script + the site workflow), the
postbuild guard, minimal styles matching the site. No src/Weir movement,
no DECISIONS row owed unless a real design ruling emerges (the modal
placement is taste, not law).
