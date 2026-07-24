# weir — argv splat: `$@xs` (N things, N words)

Status: BLESSED (user 2026-07-24, opened by call). GATED on
Path.glob (satisfied).

(Blessed plan text as delivered; the executed decisions below.)

## Session report (2026-07-24)

- Forms `$@name`/`$@(expr)` land as an ESplat node — checked in the
  ECmd arg path (seq<string> exact; unanchored elements like `[]`
  resolve to string at the splice), expanded in argvOf at spawn
  (both TECmd eval sites, one helper; order preserved, once).
- The twin type teachings pinned (scalar → `$x`, seq<int> → map
  show); the head rejection (computed-head park named) and the
  mid-word fatal (N words can't build one word) both pinned with
  their fixes, dumps carrying the teaching at the right span.
- Empty seq → zero words, pinned by argc AND behavior; adversarial
  elements stay single words (the injection pin).
- POSITIONS swept: statement, block-let RHS, sigil interior,
  district, env chain all verified; head + mid-word rejected.
- Fuzzer: the generator emits `echo $@([...])`, the splatInline
  transform asserts ≡ inline words. Boundary FOUND by the
  transform's first failure — `$@[` bare-bracket is not a form,
  only `$@(expr)`; the transform paren-wraps. Deep run: see below.
- Semantic tokens learned the form (one-brain, same session); fmt
  respace guard rides the fuzzer's totality.
- The `$@"` interp-verbatim cell pinned both ways (lookahead); the
  parked opener inherits a held boundary.
- Docs: SEMANTICS the argv word law (sharpened); SKILL the yield!
  line; GUIDE the bare-mode file-batch idiom. The splat park CLOSES.
- Honest degradation recorded (no-silent-caps): feed's args is a
  builtin seq position, not command-arg splice — a splat there needs
  no glyph (pass the seq); the e2e notes it.
