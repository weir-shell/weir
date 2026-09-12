# weir — byte pipes: raw command-to-command hops, typed edges

Status: PROPOSED (2026-09-12). Driver: the deterministic-archive probe
(gzip bytes through a weir pipe came out U+FFFD-substituted, silently,
rc=0 — the silent-corruption class). Carries a RIDER: the value-headed
pipe child's ambient-scope bug, probed same day, same spawn seam.

## The argument — the ledger already rules this way

[D:colour-inherit] states the law: a bare statement command at a tty
INHERITS stdout — weir "never holds the bytes" — and pipes are justified
in ONE sentence: "every VALUE form keeps the pipe — a value must DECODE
and decoding requires a pipe." A command→command hop makes NO value.
By the row's own rationale weir has no business holding those bytes —
yet today every hop decodes (lossy UTF-8, U+FFFD substitution), splits
lines, rejoins, re-encodes, and appends a trailing newline. Probed:
`gzip -nc f | sh -c "cat > out"` — every non-UTF-8 byte replaced, `0a`
appended, archive corrupt, exit 0. This plan is not a reversal of a
ruling; it is the COMPLETION of one.

## The edge model

Three boundaries, types kept exactly where they mean something:

- EXPRESSION → COMMAND (a value-headed head feeding stdin): encode
  UTF-8, one element per line — today's behavior, KEPT. This edge is
  where `files | tar -T -` gets its correctness; text IS the meaning.
- COMMAND → COMMAND: raw fd, 1:1, kernel-buffered. weir still owns
  every pid — per-stage exit codes for the raise-on-nonzero law and
  the reifiers survive untouched (the pipefail shape).
- COMMAND → EXPRESSION (`$()`, `| complete`, `orFail`, …): decode to
  `seq<string>` under the ALREADY-PINNED edge laws — [D:capture-buffer]
  froze the line-split rules, BOM handling, U+FFFD-per-invalid-byte,
  and the stderr variant against an oracle. Those pins do not move;
  ONLY the hops change.

## What it fixes

- The binary-corruption class goes away STRUCTURALLY: no decode exists
  where no value is born. The strict-vs-lossy design fork collapses to
  the few, user-visible edges.
- Byte-exactness: no appended newline, no CRLF laundering mid-chain.
- True concurrent streaming between stages with kernel backpressure,
  replacing per-line runtime plumbing.
- The deterministic-archive recipe becomes
  `files | tar -cf - … | gzip -n | sh -c "cat > out"` — raw throughout,
  the one `sh` holding only the redirect.
- Consistency bonus: the LAST stage of a statement chain can inherit
  the tty exactly as a bare command does — [D:colour-inherit] extended
  to pipelines, which is bash's behavior too.

## Owed diligence (probes before building)

1. Pin sweep: does ANY pin depend on hop-normalization (CRLF→LF between
   stages, the trailing newline, U+FFFD mid-chain)? Oracle + e2e + fuzz
   expectations.
2. [D:capture-buffer] pins re-run unchanged — they govern edges only;
   green means the edge laws genuinely didn't move.
3. Windows: sibling-pipe handle plumbing under the msys e2e weather
   (CreateProcess with connected handles is standard; the fork-flake
   environment is not).
4. Per-stage rc collection with fd plumbing (wait on all pids; confirm
   the raise-at-fault-stage law and `| complete`'s code source).
5. The tty inheritance extension (last stage of a statement chain):
   confirm the [D:colour-inherit] pins extend rather than flip.

## The edge reifier pair (staged behind the hops)

- `| bytes` — the EXPLICIT raw edge: command output as `Bytes`, the
  missing command-side source in the [D:bytes] table (the twin of
  `File.readBytes`, which sits beside `File.read` for exactly this
  reason). First receipt exists (the archive probe wanted to land
  gzip's stdout without an `sh` tail). Its discovery mechanism is the
  strict-decode teaching at the text edges: "output is not UTF-8
  (byte 0x8b at offset 1) — binary cannot become text; capture it with
  `| bytes`". The teaching can name the `sh -c '… > file'` fallback
  even before the reifier exists — teaching first, reifier second.
- `| lines` — the EXPLICIT strict-text edge. Alone it is near-useless
  (the command→expression edge already defaults to `seq<string>`; a
  bare `| lines` duplicates `$()` or `orFail`-without-message). Its
  seat: the NAMED home for strict UTF-8 decode (refuse, teach, point
  at `| bytes`) and any future decode options — while the implicit
  edges keep today's pinned LOSSY laws, so grep-a-log-with-one-stray-
  byte keeps working. Forgiving by default, strict by explicit
  spelling, raw by explicit spelling.
- Priority: hops first (the substance), `| bytes` second (receipt
  exists), `| lines` last — receipt-driven, possibly never.

## RIDER — resolved in two, one still open

BUILT (2026-09-12, branch ambient-capture): the ESCAPE class — a lazy
command bound inside a scope and forced outside now replays its
written-site ambient (Spec Cwd/Ambient snapshots; [D:ambient-capture]).
STILL OPEN, rediagnosed with debug-order evidence: the probe matrix's
within-body cells are a PARSER ASSOCIATION defect — `xs | cmd` written
as a block's TAIL line parses as `(within …) | cmd`, the pipe attaching
OUTSIDE the block, so the command never sits in the scope at all (and a
tail `s |> f` in the same position is a bare parse error). Fix lives in
the joined-form within-body grammar, its own branch; the matrix below
becomes its regression pins.

## The original probe matrix (the association defect's pins)

Probed 2026-09-12, six-cell matrix, same spawn seam as this plan:

| child shape | within cd | within env |
|---|---|---|
| bare command | ✓ `/tmp/wdir` | ✓ `overlay` |
| `["x"] \| sh …` (value-headed) | ✗ outer cwd | ✗ EMPTY |
| `cmd \| cmd` stage 2 | ✓ | ✓ |

The value-headed pipe's child spawns without the ambient scope context
WHOLESALE — both `within cd` and `within env` are invisible to it,
while bare commands and command-headed chain stages see both. One
spawn path missing the session context; the archive probe hit the cd
face first (`tar: Cannot stat` ×8) and worked around with `-C`. This
is a BUG fix, independent of and preceding the byte-hop work — but it
lives in the same child-spawn code this plan rewrites, so fix it first
and the hop rewrite inherits the corrected seam (plus its regression
pins: the six-cell matrix above as e2e cells).

## Relation to other plans

Independent of the effects/pure work ([D:pure] classifies `|`-reifiers
and commands identically either way). Touches [D:colour-inherit],
[D:capture-buffer], [D:stream-echo] territory — the DECISIONS row for
this work should cross-cite all three. The archive probe that receipts
`| bytes` also receipts `Path.glob` exclusion ergonomics and
`Path.normalize` (tracked in the migration plans, not here).
