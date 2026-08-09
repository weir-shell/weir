# weir — the "content is bytes" audit

Status: EXECUTED (2026-07-31; blessed same day). Audit-shaped:
mechanical fixes landed, findings sized. Zero pin movement outside
named fixes (the R1 pin and the reverse-Norway multiline case, both
named).

Session report: the fixture came first and HEAD failed it four ways —
tab-in-content (assembler rejection), `//` and `///` lines vanished
(stripComment + the comment-only filter, pre-assembly), trailing
whitespace trimmed (sentinel split + fmt). The central fix moved
classification/stripping INTO assemble (raw lines in; an active yaml
district joins raw bytes; structure parses stripped text). The
doc-attachment pass, doc-align lint, and fmt's canonicalizeDocs
shared one missing piece — `Script.districtContentMask`, defined once
off the real marker classifier. CRLF decided: normalized at read
(ReadAllLines already does; a source file's line ending is not data),
pinned via the fixture's CRLF twin. R1: the quoted-with-escapes
fallback ADOPTED (2+ trailing newlines render quoted — valid, exact,
round-trips; the ERROR spelling retired; the ledger's reasoning
corrected). R2: the maintenance plan gained quoted-message
verification. R3: the `{{ }}` interp nit filed in
editors/tree-sitter-weir/README.md Known nits.

DENOMINATOR: 16 sites examined / 10 touching potential content / 7
mangling, all fixed: stripComment, the CommentOnly filter, the tab
rejection, the sentinel-split trim, fmt's district trim (+ tab via
TrimStart), docAttachments, the doc-align lint, canonicalizeDocs.
Clean by construction or display-only: the REPL colorizer (paints the
same chars), semantic tokens (emit-only), cleanParseDump (error text
only), the doc-test extractor (fences), bracket/alignment machinery
(district lines take the district join before bracket logic), the
sentinel-split consumers (suite-green), fuzzer transforms (no yaml
districts generated — a sized COVERAGE finding; reindent is uniform
so rel-offsets are safe by construction). Stated value-preserving
normalizations: fmt renders whitespace-only content lines empty;
mid-line ` #` on district structure lines is data while the read side
strips it (pre-existing asymmetry, noted).

The standing guard: the hostile-byte fixture in ci/e2e.sh
([D:content-bytes]) — printf-generated (a checked-in literal with
trailing spaces/tabs/CRLF invites the mangling it guards against),
one block scalar carrying every hostile class, byte-exact through
check AND run, plus the CRLF twin and fmt value-preservation +
idempotence. The class is named in PROCESS ("Content is bytes") with
its three-collision archaeology so the next byte-exact region — a
here-doc, a raw block, an embedded language — inherits the audit.

## The original charter

Block-scalar content is BYTES. Every layer that reasonably
normalizes text is wrong inside it. The block-scalar session hit
three instances, all by collision rather than by audit: parseDocs
dropped blank lines and `#` lines and stripped trailing `#` comments
before the block parser ran; the assembler's total blank
transparency would have silently dropped blanks from block content;
the directive scan claimed `#!/bin/sh` as a misplaced directive.
Three for three found the hard way is the argument for looking at
the rest deliberately instead of waiting for a fourth. The question
for every site: does this run on lines that could be block-scalar
content, and if so does it preserve them byte-for-byte?
