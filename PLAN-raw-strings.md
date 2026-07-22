# weir — raw strings: `@"..."` and `"""..."""`

Status: BLESSED (user 2026-07-21). One session. Origin: the regex
session's unstated-decision flag (pattern-position literals shipped
as positionally-raw; the shout-if clause invited review) → the
review concluded rawness is a STRING property, not a regex property
or a position property — paths, fixtures, and quoted-pattern regex
all want it. The design walked the candidates in the open: single
quotes (rejected — deleted from the highlighter once already for
apostrophe-swallowing; dotenv's quoting one adapter away), `~`
(rejected — no prior, home-directory/=~ associations, and the last
free sigil is worth more unspent), backticks (rejected — weir-only
kind with no referee, JS inverted the prior to "template string",
and F# claims the glyph for double-backtick identifiers). The
answer was in-house twice over: F# has BOTH raw kinds, so the
feature arrives oracle-refereeable as a parity GAIN.

## The two literals, F# semantics exactly

- **`@"..."` verbatim**: backslashes literal, no escape sequences;
  the ONE rule is `""` for an embedded double-quote. The common raw
  case: `@"(\w+)=(\d+)"`, `@"a\path\like\thing"`.
- **`"""..."""` triple-quoted**: NO escapes at all, not even quote
  doubling — bare `"` legal inside; only the `"""` sequence itself
  is unrepresentable. The quoted-pattern case:
  `"""(\w+)="(\d+)"""` -class regex over quoted fields, porcelain-
  domain text. Lexer cost given `@` is near zero (longer opener,
  same region machinery).
- **Weir's known divergence, both kinds: single-line only.** F#'s
  verbatim and triple-quoted strings span physical lines; weir's
  single-line strings are load-bearing (the assembler, fmt's
  refuse-on-mismatch soundness argument, the highlighter's swallow
  analysis all rest on strings closing before EOL). One divergence
  row covering both kinds, same family as
  blank-line-ends-statement.

## Pre-made decisions

- DECIDED — **Oracle probes BEFORE bless-to-code** (the
  folklore-vs-compiler rule's first scheduled application — every
  F#-fact this plan asserts gets its verdict-visible pin first):
  `@"a\nb"` has length 4 (Same); `@"x""y"` contains one quote
  (Same); `\w` legal inside `@` (Same); `"""a"b"""` contains a bare
  quote (Same); the `""""` edge shapes (weir matches FCS's verdict
  or rows it — do not reason from memory, ask); multi-line `@` and
  multi-line `"""` (F# accepts, weir rejects — the pre-known row);
  `$@"..."` / `$"""..."""` interpolated-raw (F# accepts; weir's
  verdict per the park below). Any probe that surprises is a plan
  amendment before implementation, not a mid-session discovery.
- DECIDED — **The positional raw-regex rule RETIRES, same week it
  shipped**, with archaeology: pattern position now accepts any
  string literal uniformly (ordinary, `@`, `"""`), escapes owned by
  the literal kind, never by position — "strings mean the same
  thing everywhere" restored as a hard law. The regex plan's
  asymmetry docs (SEMANTICS/SKILL/GUIDE) are superseded in the same
  commit; the shout-if flag is credited in the archaeology (the
  clause worked, one exchange late). Migration: one `@` per
  existing pattern-position regex in repo scripts/examples/pins.
- DECIDED — **Expression side de-uglified in the same pass**: the
  GUIDE/SKILL regex examples move from `"\\.md$"` to `@"\.md$"` —
  including the taught condition idiom
  (`if x |> Str.isMatch @"^test" then`) and the =~ park entry's
  pre-made design (its literal-RHS clause now reads "raw string
  literal"). Doc-tests prove the sweep (the extractor is the
  referee for doc edits, per standing mechanism).
- DECIDED — **Interpolated-raw (`$@`, `$"""`) is PARKED as one
  decision**: regex is the driving use case and regex-with-holes is
  a computed pattern — the expression side's string territory. The
  park is pre-scoped (lexer composition is mechanical: the `$`
  adjacency rule already exists; holes re-enter code land per the
  mode-stack scanner) and reopens on receipts of raw-with-splice
  friction. The oracle probe above records F#'s verdict now so the
  divergence row is born accurate either way.
- DECIDED — **No third raw kind, ever-until-receipts**: backticks
  carry their tombstone (weir-only, JS template prior, F#'s
  double-backtick identifier claim — AND the honest credit: the
  1:1-raw want that motivated them was real, answered in-house by
  `"""`). Single-quote and `~` rejections recorded in the same
  entry. The raw-string budget is two kinds because F#'s is.
- DECIDED — **Highlighter + tooling ride along**: micro syntax file
  gains `@"` and `"""` regions (no escape rules inside except the
  `""` specialChar for verbatim; the earlier keyword-lookahead NOTE
  resolves — the prefix IS the region opener, no lookahead needed);
  the comment-stripper/scanner (foldOutsideStrings) learns both
  openers [this is the load-bearing tooling item — the quote-state
  scanner currently knows one string kind; verbatim's `""` and
  triple-quote's bare `"` are new states; the formalization rule
  applies: it lands in the ONE scanner, and fmt/oracle-mirror/
  assembler inherit]; interpolation-hole scanning unaffected (raw
  kinds have no holes until the park opens).
- DECIDED — **Rider (user-raised at review): the `Regex` pattern's
  literal is RAW-ONLY** — `@"..."` or `"""..."""`; an ordinary
  escaped string in that position is a CHECK error with the hint
  "regex literals are raw: use @\"...\" (or \"\"\"...\"\"\" for
  patterns containing quotes)". Rationale, on record: the position
  is already bespoke (check-time compiled, arity-read), so the kind
  restriction is a clause of the existing arm, not a mechanism;
  regex's own escape language (`\t`, `\x0A`, `￿` — engine
  escapes, raw-transparent) makes ordinary-string escaping
  expressively redundant there, leaving only the double-escape
  footgun, which this makes UNREPRESENTABLE at the one checked-regex
  position. Not a strings-law violation: no string's meaning varies
  by position — a kind is rejected at one, casing-law-style. The
  expression side (`Str.isMatch`, `Str.rmatch`) stays unrestricted
  (computed patterns are strings by nature; restricting literals
  while accepting computed strings would be incoherent — the
  boundary IS the coherence). Parity cost zero: the Regex form's
  weir-only row refines, no new row. The `=~` park's pre-made
  design inherits (literal-RHS clause now reads raw-only).
  Pins: ordinary string in Regex position rejected with the hint;
  both raw kinds accepted; a no-backslash pattern still requires
  `@` (uniformity of habit — the error fires on kind, not on
  content); migration already covered by this plan's sweep.
- DECIDED — **Ceremony**: lexer + scanner change, not checker —
  tripwires cheap, run anyway; POSITIONS sweep for both literal
  kinds (every expression position + pattern position + command
  SPLICE position — `echo (@"\n")` passes a literal backslash-n as
  one argv entry, pinned); products: raw × interpolation (a `$"`
  hole containing... nothing — holes can't contain string literals
  today, re-pin that unchanged), raw × comments (`//` inside a raw
  string is string content — the scanner pin), raw × doc-test
  extractor (a fenced block containing `"""` — verify the extractor
  survives), raw × fmt roundtrip (verbatim content byte-preserved).

## Work items

1. The oracle probe set (first commit — any surprise amends the
   plan).
2. Lexer: both openers; the one scanner extended (foldOutsideStrings
   states for `@"..""` and `"""..."""`); all scanner consumers
   inherit — zero parallel string-state logic (the review flag).
3. Positional-rule retirement + migration + superseded docs, one
   commit with archaeology.
4. Expression-side example sweep (GUIDE/SKILL/=~ park), doc-tested.
5. Highlighter regions; the battery per ceremony; e2e: a regex
   script using both kinds on the AOT binary; timing pins.

**Done when:** both literals lex with F#'s semantics (probe-pinned);
single-line divergence rowed; the positional rule retired with
archaeology; one scanner owns all string states; docs de-escaped;
all green.

## Parked

- `$@` / `$"""` interpolated-raw — one decision, pre-scoped above.
- Multi-line raw strings — the single-line law's general question,
  not this plan's; the divergence row is its tombstone-pointer.
- Backtick/single-quote/`~` literal kinds — rejected with reasons
  in the NOTES entry; re-askable only against it.
