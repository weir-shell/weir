# weir — range slicing, the F# way (`x[a..b]`)

Status: PLANNED — the reopening of [D:range-slice-costing], whose park
said it reopens "only if the convention argument is answered." It is
answered: F#-FIDELITY answers it, and weir already referees against F#
(`ci/fsharp-oracle.sh`). Nothing here is implemented; this is the
spec, the rulings, the declined part, and the costed remainder.

## What reopens it

[D:range-slice-costing] costed the feature at ~0.5–0.75 session
(seq-only) or ~1.5–2 (string+seq), and parked it on ONE objection:
the CONVENTION costs (two readings of `..` inside `[]`; a slice that
truncates while `x[i]` raises), which "reopens only if the convention
argument is answered, never on the code getting easier."

The convention argument is answered by deferring to F#, which is not
picking a convention off a menu — weir is F#-shaped and gates every
divergence against F# through the oracle, so F#'s slicing IS weir's
convention by construction. Every parked worry resolves:

- `..` two ways — F# does exactly `[1..50]` (generate) and `x[3..7]`
  (slice). Idiomatic in the parent language.
- inclusive — F# `[3..7]` is 5 elements; matches weir's already-forced
  inclusive ruling (`[1..3]` is an inclusive generator).
- truncate-slice vs raise-index — F# TRUNCATES out-of-range slices and
  raises `x[i]`; Python and Nushell agree. The asymmetry is the norm,
  not a novelty.
- strings + seqs under one syntax — the "expensive with strings" half
  of the costing is exactly what F# does; the oracle pins both.

## The spec — verified against F# (`fsy` / `dotnet fsi --langversion:preview`)

Every cell below was RUN in F#, the reference weir mirrors:

    [1..10].[3..7]      -> [4;5;6;7;8]        inclusive, 5 elements
    "abcdefghi".[3..7]  -> "defgh"            strings slice, same syntax
    [1..10].[3..100]    -> [4;5;6;7;8;9;10]   out-of-range END truncates
    "…".[3..100]        -> "defghi"           string truncates too
    [1..10].[50..60]    -> []                 fully past = empty, no raise
    [1..10].[7..3]      -> []                 reversed = empty, no raise
    [1..10].[..3]       -> [1;2;3;4]          open-ended start
    [1..10].[7..]       -> [8;9;10]           open-ended end

So the target semantics: **inclusive both ends, clamp/truncate both
ends (out-of-range and reversed yield the empty result, never a
raise), strings and seqs under the one `x[a..b]` syntax, open ends
`x[..b]` / `x[a..]`.** This is `x[i]`'s sibling: `x[i]` is an
ASSERTION (raises if absent), `x[a..b]` is a FILTER (takes what is
there) — the same split Python/nu/F# all make.

## Declined: from-the-end indexing, in ANY spelling

F# completes its slicing with `^n` from-the-end (`xs[^1]`, `xs[^4..]`,
`xs[..^1]` — verified in fsi preview). weir does NOT adopt it, and the
decline is total, not `^`-specific:

- `^n` (C#/F#) — `^` is weir's command-force sigil (`^ls`), the one
  escape [the glyph law: one glyph, one meaning]. `xs[^count]` is
  lexically the command-force shape; even resolvable by position it is
  the same-glyph-two-meanings weir refuses on principle.
- negative `xs[-1]` (Python/Ruby/JS) — `-1` is ALREADY a legal index
  and weir RAISES on it (`item: negative index -1`, verified). That
  raise is a safety net: a computed index slipping negative is a bug
  weir catches. Repurposing to from-end SILENTLY reinterprets the bug
  as "last element" — the silent-wrap footgun, against the raise-don't-
  guess law — and diverges from F# (which also raises on `-1`).
- `$-1` (D, `$`=length) — `$` is splice/capture, live inside `[]`.
- `end` (MATLAB/Julia) — `end` is a valid identifier today
  (`let end = 99` checks); a contextual keyword there clashes with a
  binding, and it is non-F#.
- `*-1` (Raku Whatever) — `*` is multiply.

From-the-end stays a NAMED operation, explicit and footgun-free:
`Seq.last` / `Seq.tryLast` (last element), `Seq.rev` (reverse, then
forward-slice). Documented as a divergence from F#: weir keeps `^` for
command-force and keeps the negative-index raise as a safety net, so
from-end is a Seq member, not a glyph. Reopen only on a receipt for a
from-end that these do not serve.

## The cost — implementation only; the design is settled

Per [D:range-slice-costing], ~1.5–2 sessions for string+seq. The
dominant cost is architecture, not slice logic:

- **weir's FIRST type-directed desugar.** Today `x[i]` is a PURE
  parse-time desugar to a hidden `|seqItem i x` — no indexer AST node,
  no checker arm, NO type dispatch. `x[a..b]` must resolve the TARGET
  type (string → substring, seq → subsequence) and pick the impl, so
  it needs a real node plus a checker arm that runs after the target
  types. This is the new machinery.
- **a clamping slice primitive.** A naive `Seq.skip a |> Seq.take n`
  will NOT match F#: weir's `Seq.skip` RAISES past the end, where F#
  `[50..60]` returns `[]`. So a bounded slice that truncates both ends
  is needed (int-index → clamp to `[0, length)`, reversed → empty).
  Lazy where it can be (open/forward), forcing only what truncation
  demands.
- grammar ~0.2 (one interior alternative in the index bracket;
  `indexExpr` is already deepen-wrapped, so depth-coverage inherits —
  the record-pattern precedent). `_[1..4]` is FREE (the `_[0]`
  shorthand wraps whatever the index desugars to). fmt is FREE
  (layout-based). In-repo grammars ~0 (micro/tmLanguage position-blind;
  tree-sitter external since [D:ts-split]).

## Groundwork already shipped

- [D:accessor-teaching] — `xs[1..4]`, `xs[..b]`, `xs[a..]`, `xs.[i]`
  all TEACH today (`no range indexing — weir accessors are offset-and-
  length…`). When slicing lands, those teachings FLIP to documenting
  the feature, and the open-ended forms it already names become legal.
- [D:message-ownership] — the out-of-range / skip text is already in
  weir's own voice, so the slice primitive's raises (index side) and
  its clamp (slice side) inherit the right register.

## Rulings, pre-decided so an implementation is turnkey

- INCLUSIVE end — forced by internal consistency (`[1..3]` is already
  an inclusive generator; an exclusive slice would put two readings of
  `1..4` in one bracket).
- TRUNCATE both ends — F#-matched (and Python/nu-matched); the length
  probe a raising-end would need breaks the pinned laziness anyway.
- `Str.sub start len` STAYS — offset-and-length is a different, still
  useful shape; `x[a..b]` is offset-and-offset. Two spellings, one for
  each intent (decide at implementation whether the guide leads with
  which).

## Verification plan (when executed)

- The `fsy` oracle gains Same pins for the eight cells above — the
  spec IS the oracle target, so slicing is oracle-refereed by
  construction (string and seq, inclusive, truncating, open-ended,
  reversed-empty, fully-past-empty).
- e2e cells: a sublist, a substring, both open ends, an out-of-range
  end (truncates), a fully-past (empty), a reversed (empty), and
  `x[i]` UNCHANGED (still raises — the assertion/filter split pinned
  both ways).
- The [D:accessor-teaching] teachings and their fences flip from
  error-demos to run-demos; SKILL/GUIDE/reference accessor prose moves
  from "never ranges" to "one element raises, a window truncates".
- Parser touched → fuzz 3×10k on a published AOT build.
- From-end declines pinned: `xs[^1]` and `xs[-1]` both refuse with
  their teachings (command-force / negative-index raise), so the
  divergence is a gate, not a comment.
