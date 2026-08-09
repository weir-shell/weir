# weir — the library gap audit: what is actually missing

Status: ANALYSIS, EXECUTED (blessed + run 2026-08-03). Zero code —
the deliverable is this report. Branch library-gap-audit.

## The framing, applied

Parity with F#'s ~70-member `Seq` is the wrong target and this
report closes that question rather than leaving it perennial: a
large fraction of F#'s surface is irrelevant to shell work,
unrepresentable (recursion/laziness weir declines by design), or
noise. The measured targets: what has a real script WANTED and not
found (evidence), and what will a newcomer's first hour reach for
(judgment) — kept separate below.

## Pass 3 first — the inventory (counts, so nothing recommended exists)

- **Seq (30)**: map, where, first, take, head, sum, force, tryHead,
  tryFind, isEmpty, length, fold, choose, append, sortBy,
  sortByDescending, iter, pmap, piter, range, pairwise, zip, exists,
  forall, item, tryItem, skip, contains, distinct, groupBy
- **Str (19)**: contains, startsWith, endsWith, trim, trimStart,
  trimEnd, toLower, toUpper, split, join, replace, length, sub,
  toInt, tryToInt, tryIndexOf, isMatch, rmatch, rmatchAll
- **Path (6)**: extension, fileName, stem, dir, combine, glob
- **Option (3)**: map, defaultValue, defaultWith
- **Env (5)**: pair, ofPairs, get, vars, fromFile
- **File (4)**: read, write, append, exists
- **Log (8)**, **Args (2)** — complete for their charters.

The recommend-what-exists error this pass prevents, caught twice
while drafting: most of the plan's own judgment candidates
(`take/skip/zip/sum/contains/sortBy`, `Str.split/replace/trims/
case/startsWith/endsWith`) ALREADY EXIST. **The Str side — which the
plan guessed matters more — is nearly complete**; the real gaps are
Seq-side and one hashing hole.

## Pass 1 — the receipts (each with its citation)

- **sha256 hashing** — `tools/corpus-mine.weir:84` (`| sha256sum`,
  THE ONLY coreutils shell-out left in the whole corpus), plus the
  portable-showcase session (NOTES 2026-08-03): the digest demo was
  DELETED because no portable spelling exists. Two citations, one of
  them a live file. Own-the-data-munging says this is a gap, not a
  choice. Design question riding it: the content-is-bytes law (hash
  of LINES vs hash of BYTES; File-vs-Str member placement).
- **Seq.windowed** — NOTES-agent `## fallbacks` 2026-07-29, the
  ledger's ONLY entry: the M2 dedupe detector ran as awk because a
  6-line sliding window has no spelling.
- **Seq.last / tryLast** — NOTES-agent friction 2026-07-21:
  `Seq.skip (n - 1) |> Seq.head` spelled it, called obscure at the
  time of writing.
- **Option.iter** — probe.weir (2026-08-03) spells it as a
  match-with-unit-arm, twice in 40 lines.
- **Option.orElse** — showcase.weir's tmpBase (2026-08-03) spells
  first-Some-of-two as a parenthesized tuple match.
- **Path.tempDir** — same site: the TMPDIR/TEMP/fallback dance is
  three EnvCfg fields plus a match; a member dissolves it.
- **The stranded log is EMPTY** — zero abandoned scripts; and the
  closed-receipt history (fold, multi-param lambdas,
  contains/exists, pairwise, item, Env.get, elif, exit-reifiers,
  scriptPath, child-env) shows the receipt loop works: every prior
  cited want landed.

## Pass 2 — the blocked pass, and the Ord fork ANSWERED

**Nothing in the wanted set is blocked.** The fork dissolves against
the source: `sortBy`'s scheme is ALREADY the key-projection option —
`('a -> 'b) -> seq<'a> -> seq<'a>` with `Ord` demanded on `'b` only
(Builtins.fs sortByScheme). `sortBy (fun r -> r.name)` works today;
no widening was ever needed for the receipt class. `Ord` stays
int/string/bool with no structural decomposition; the composite-key
case (tuple keys, F#-lexicographic) is DELIBERATELY refused
(divergence row no-tuple-ord), and chained stable sortBy is its
spelling. Widening reopens only on a real composite-key receipt.
Consequence: `min/max/minBy/maxBy/sort` are ordinary unbuilt members
on existing machinery, not a design session.

## Pass 4 — judgment (labelled as such; no citations exist)

First-hour candidates worth building without a receipt:
`Seq.min/max/minBy/maxBy` (the sortBy-then-head spelling is obscure),
`takeWhile/skipWhile`, `chunkBySize` (windowed's sibling — build
together), `rev`, `sort` (identity over scalars),
`Str.padLeft/padRight` (column reports; no citation found — the
corpus pads nothing today), `File.delete` (the showcase leaves temp
residue it cannot clean), `Seq.collect` (flatten has no spelling —
fold+append is the workaround). Declined even as judgment: see the
not-needed list.

## The table

| member | kind | status | reason |
|---|---|---|---|
| sha256 | Str/File | **has-receipt** | corpus-mine.weir:84 + showcase deletion; the last shell-out |
| windowed | Seq | **has-receipt** | fallbacks ledger 2026-07-29 (awk) |
| last / tryLast | Seq | **has-receipt** | friction 2026-07-21 |
| iter | Option | **has-receipt** | probe.weir match-workaround ×2 |
| orElse | Option | **has-receipt** | showcase tmpBase tuple-match |
| tempDir | Path | **has-receipt** | showcase EnvCfg dance |
| min/max/minBy/maxBy | Seq | judgment | sortBy+head spells it, obscurely |
| takeWhile/skipWhile | Seq | judgment | no receipt; first-hour reflex |
| chunkBySize | Seq | judgment | windowed's sibling, same session |
| rev | Seq | judgment | no receipt |
| sort (identity) | Seq | judgment | sortBy (fun x -> x) spells it |
| collect | Seq | judgment | flatten has no direct spelling |
| padLeft/padRight | Str | judgment | column reports, uncited |
| delete | File | judgment | showcase temp residue |
| take/skip/zip/sum/contains/distinct/groupBy/sortBy/pairwise/item | Seq | **exists** | inventory |
| split/replace/trims/case/startsWith/endsWith/contains/sub | Str | **exists** | inventory |
| sort-by-record-field | — | **exists** | sortBy key-projection IS the design |
| anything-composite-key-Ord | — | blocked-on-receipt | no-tuple-ord row refuses; chained sortBy spells |

## NOT needed — the parity question closed with reasons

- `unfold`, recursion-shaped combinators — weir declines recursion
  by design.
- `cache`, `delay`, `readonly` — laziness plumbing; weir's seq
  memoization is internal where it matters (seq patterns), never a
  user knob.
- `average`, float aggregates — weir has no float.
- `fold2`, `zip3`, `map2`, `allPairs`, `transpose`, `permute`,
  `splitInto` — noise for shell work; zip + fold compose the rare
  need.
- `ofList/toList/ofArray/toArray/cast` — ONE sequence type by
  design; nothing to convert.
- `reduce` — sum and fold cover it; reduce's empty-seq exception is
  a worse contract than fold's explicit seed.
- `iteri/mapi/indexed` — `Seq.zip (Seq.range 0 n)` spells it; an
  index-hungry loop is usually a fold. Revisit on receipt.
- `tryPick` (choose+tryHead), `countBy` (groupBy+map length),
  `scan` (fold with trace — receipt first), `except/intersect`
  (distinct-family, receipt first).

## Ranked build order

1. **Session A — the receipt session** (one session): sha256 (with
   its bytes-law ruling), Seq.windowed (+chunkBySize riding along),
   Seq.last/tryLast, Option.iter/orElse, Path.tempDir. Every item
   cited; the session justifies itself.
2. **Session B — the first-hour session** (one session, judgment,
   bless separately): Seq.min/max/minBy/maxBy, takeWhile/skipWhile,
   rev, sort, collect; Str.padLeft/padRight; File.delete. A
   member-mill session; each member is small, the count is the work.
3. **Blocked: nothing.** The Ord fork dissolved; no design session
   gates any of this.

## Launch cross-check

Against the standing ranking (Duration → retry/poll → Ord+sort →
this audit → HTTP): the **Ord+sort slot DISSOLVES into session B**
(no widening; min/max/sort are ordinary members), so the lane
shortens by one design session. Sessions A/B slot after retry/poll
as ranked. HTTP stays last and is cheaper than ranked-time believed:
System.Net.Http's 3.4 MB is already paid for by the contracts fetch
(measured, windows-v1 session 1).
