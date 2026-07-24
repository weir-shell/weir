# weir — assembler formalization: the text layer earns its structure

Status: EXECUTED (landed 2026-07-20) — as blessed: BLESSED (2026-07-20). Executed on branch
assembler-formalization — SEQUENCED AFTER grammar-consolidation, not
before as proposed (the consolidation session had already shipped
when this arrived), so the fourth string hack was born there and
retired here the same day. Blessed decisions as received:
behavior-preserving contract (zero pin edits; delta = stop-and-
report); one classifier consumed by assembler/fmt/oracle; Join type
with self-measuring insertions; quote-scanner extracted once;
structured parse errors (or pinned-scrape fallback, reported);
future line-shape logic lands in classify/scanner/Join.

## Completion notes (2026-07-20)

All six items DONE, ZERO pin edits — 510 unit (500 held + 10 new
contract pins) / 43 oracle / e2e / skill-doc / timing all green.
(1) foldOutsideStrings extracted; stripComment and braceDelta are
consumers; contract pinned (escapes, single quotes, URL boundary,
interp holes). (2) Two granularities, honestly: classifyLine
(Blank/CommentOnly/Code — runner filter, fmt, oracle mirror) and
classifyPiece (exclusive Kind: PipeHead/ElseHead/LetHead/Plain +
orthogonal IsMarker/OpensCompound/IsBangSigil/ClosesBrace flags +
BraceDelta) — the plan's single-enum sketch could not express
`if c then !` being a compound head AND a district marker; flags are
the truthful shape. (3) Join algebra (JIn/JSibling/JSpace/
JDistrictOpen/JDistrictSibling/JDistrictPipe); applyJoin owns every
insertion string and derives joinedStart from it; the `+ 5` and
`- 1 + 2` offsets deleted; district/sibling span e2e re-ran
byte-identical. (4) STRUCTURED taken (not the fallback):
Parser.ParseFailure { Message; Col } from FParsec's ParserError
position; parseLine kept as a Message-only wrapper so no test-site
churn; the runner's regex deleted. (5) All suites green, no pin
edits. (6) Boundary archaeology in NOTES; the review-flag rule
recorded.

## Parked

- Relocating assembly into the parser — rejected with the NOTES
  archaeology; re-askable only against that entry.
- A full trivia-preserving lexer for fmt — fmt v1's verbatim
  discipline stands; this refactor makes that future work cheaper
  but does not start it.
