# weir — command-mode block lets in every statement context

Status: IN EXECUTION (2026-09-14; probes run first, results below).

## The finding

[D:block-let-cmd] drew the command-RHS boundary at the top-level
let's SPINE, holding the original park's expression-side concerns by
construction: parens, lambda bodies, and single-line `let…in` stay
expression-only. But the spine ALSO excludes every statement body —
`within`, if-then, `for`, match arms in statement position — where
NONE of the park's ambiguities exist: those bodies end at indent
exactly like top-level, and bare command STATEMENTS already work in
all of them ([D:match-arm-commands], [D:interior-arming]). The
boundary there is an increment ("one more position per receipt"),
not a law — and the receipts arrived, twice in one day:

- tools/ext-release.weir's vsix probe needs
  `$(npx --no-install vsce package … | complete)` inside
  `within tmp d` / `within cd` — the sigil doing work bare should do.
- SKILL's own symlink example carries the same `$(sh -c … | complete)`
  wrapper inside `within tmp`/`within cd` — the doc teaching the wart.
- Probed 2026-09-14: all four statement contexts refuse a bare
  reifier block-let, uniformly.

Second finding, the teaching fragility: the off-spine refusal
("'complete' is a reifier, not a PATH program — reify on a
statement-level let") fires only when the EXPRESSION parse survives
to the `|`. `sh -c "…" | complete` teaches; `npx --no-install …`
dies raw at the double dash ("Expecting: identifier, '!', …") before
the reifier is ever seen. And the hint's text is lambda-shaped —
inside a `within` body the let IS statement-level, so the teaching
reads as nonsense exactly where it fired.

## The ruling to implement

A block `let` RHS takes command mode in every STATEMENT context —
any body where statements sequence: `within` (all kinds, `always`
blocks included), if/elif/else bodies, `for` bodies, match arms in
statement position, `pure` blocks (the parser admits the grammar;
purity refuses at CHECK with its own located teaching — the better
span). The principled line becomes:

- statement context ⇒ the full top-level command-let law: bare
  chains, reifiers, splices, `[D:paramful-rhs]`'s ambient-resolver
  extension, bindings-beat-PATH.
- expression context ⇒ refused with a robust teaching naming `$()`:
  paren interiors, single-line `let…in` (the in-swallow), and lambda
  bodies (see boundary below).

The `$()` sigil returns to its charter: positions bare genuinely
cannot reach.

The teaching hardens with it: in a refused context, a block-let RHS
whose head is command-shaped (resolves on PATH, or is followed by
argv-shaped tokens) refuses with the sigil teaching EVEN when the
expression parse dies before the pipe — the recovery must not depend
on reaching the reifier. The text names the actual context ("inside
a lambda body, a command needs `$(…)`"), never "statement-level"
where the let already is one.

## Probes (run FIRST, before code)

1. Transcribe the four-context refusal pins (probed live
   2026-09-14; the repros are one-screen each).
2. THE LAMBDA CARVE-OUT'S WHY, demonstrated: a multiline lambda body
   closes by PAREN BALANCE, so a command RHS there could swallow the
   lambda's own `)` as argv (`echo (x)` vs the closer). Build the
   repro; it is the reason lambda bodies stay spine-gated, and the
   plan's boundary section cites it. If the repro does NOT reproduce
   (the closer rule proves robust), the lambda question reopens as
   its own ruling — do not fold it into this one.
3. Inventory the pins that assert today's refusals: the SKILL
   weir-error block (statement-level `Seq.iter` lambda — stays
   refused, text updates), unit pins from [D:block-let-cmd] /
   [D:multiline-lambda] — classify each as flips vs stays.
4. check == run in every new context under the assume-resolver
   ([D:assume-resolver]) — the patch-district lesson says the two
   parse paths drift exactly here; pin parity per context.
5. `within proc srv = <command>` heads vs body command-lets — no
   grammar collision expected (the head owns its own line); pin it.
6. Fuzz grammar: does the generator emit block-lets in statement
   bodies at all? Extend it to generate command-RHS lets there, or
   the property never exercises the new position.

## Probe results (2026-09-14, before any parser change)

1. TRANSCRIBED. All four contexts refuse a bare reifier block-let
   uniformly, at the reifier, with the fifth-cell teaching
   ("'complete' is a reifier, not a PATH program — reify on a
   statement-level let RHS…"): `within tmp d`, `if 1 > 0 then`,
   `for x in [1] do`, a statement-position match arm — and the
   `always` block joins them. The text is nonsense at every one of
   these sites: the let IS statement-level there. `pure` on a let
   RHS is ALREADY the desired interplay (the RHS spine admits the
   grammar; purity refuses at check, located at the command:
   "this 'pure' block forbids effects, but '|completed' runs a
   command"). The dash-death twin transcribed too: `let r = npx
   --no-install vsce package | complete` in a within body dies RAW
   at the double dash ("Expecting: identifier, '!', '$', …") — the
   reifier teaching never fires; `sh -c … | complete` teaches.
2. THE PAREN-SWALLOW DOES NOT REPRODUCE. `cmdWordChar`
   (Parser.fs) excludes `)` from argv words, so a lambda's closer
   can never join a command's argv. Probed on the live spine
   (lambda bodies take command mode there today): `echo charlie)`,
   `echo bravo (1 + 1))`, `echo delta (x)` with the closer glued
   AND alone, `echo ")" tail)` (a QUOTED paren does not confuse the
   assembler's balance), and a command block-let with `")"` in
   argv — every shape parses, runs, and never swallows the closer.
   Per the plan's own honesty clause: lambda bodies STAY out of
   scope (the boundary holds on precedent, not on this hazard), and
   the lambda question reopens as its own ruling with its own
   receipt bar — not folded into this one.
3. PIN INVENTORY. FLIPS: the fifth-refusal-cell unit pin's if-body
   and within-body positions (4 reifiers × 2 positions become
   legal). STAYS: its lambda-body position (text moves to the new
   hardened teaching); "single-line let-in stays expression-only"
   (the standing park); "products: parens interiors stay
   expression-only" (off-spine); "command block-let in a lambda
   body parses on the let-RHS spine" ([D:multiline-lambda] —
   inheritance untouched); SKILL's weir-error statement-level
   `Seq.iter` block (stays refused, NEW text); SKILL's weir
   `Seq.map`-on-spine block (stays legal). Probed en route: the
   SPINE flag already rides through paren interiors, interpolation
   holes, and list literals (`let x = $"pre {let y = echo hi in
   y |> Seq.head}"` runs today) — unpinned, untouched by this plan.
4. PARITY BASELINE: check and run refuse identically today (same
   parse error, both paths); post-change pins go per context,
   analyzeLines (assume-resolver) against a real run.
5. `within proc srv = <command>` heads parse via letRhsCmd
   DIRECTLY, ungated — the head owns its line before the body block
   opens, so no collision with body command-lets is possible by
   construction; pinned in stage 4.
6. THE GENERATOR DOES NOT reach the new positions: command-backed
   lets render top-level and in block-let bodies (the spine) only;
   if bodies get bare commands/prints/nested ifs; `within` and
   `for` are outside the grammar entirely. Without the stage-6
   extension the property never exercises a statement-body
   command-let.

## Footprint

- Parser.fs: the ThreadLocal spine flag (`letCmdOk`) becomes
  context-derived — set where statement bodies open (if/for/arm/
  within/always/pure), cleared at paren interiors, `let…in`, and
  lambda bodies; the in-stop/reifierEnd machinery carries over
  unchanged (statement lets have no `in`). The hardened teaching
  path rides the same flag's FALSE branch.
- Pins: per context — bare command let, reifier let, splice + param
  resolution ([D:paramful-rhs] reaches the new depth), check==run
  parity; refused contexts keep (reworded) teachings; the
  paren-swallow repro pinned as still-refused.
- SKILL: the block-let paragraph rewrites — the spine sentence
  becomes the statement-context law plus the lambda/parens carve-out
  and its why; the symlink example drops its `$()` wrapper.
- tools/ext-release.weir: the vsix probe drops `$()`; its comment
  shrinks to the tmp-dir-is-absolute note.
- CHANGELOG (next unreleased section): Changed, leading with the
  user-visible sentence ("a `let` in any statement body takes
  command lines, exactly like top level").
- DECISIONS: `[D:statement-lets]` row extending [D:block-let-cmd]
  (whose expression-side boundary SURVIVES — the row says which half
  moved and why the other half cannot).
- Gates: build ×4, full units, publish, skill-doc, e2e (a cell per
  context), fuzz 3×10k with the extended grammar — parser moved.

## Boundaries — NOT doing

- Lambda bodies stay spine-gated: probe 2's paren-swallow repro is
  the reason; if it fails to reproduce, that is a NEW ruling with
  its own receipt bar, not a rider on this one.
- Single-line `let…in` stays expression-only — the `in`-swallow is
  structural.
- No computed command heads, no resolution changes: bindings beat
  PATH exactly as today, one scope deeper.

## Relation to other rulings

[D:block-let-cmd] (the boundary this moves — expression half kept),
[D:multiline-lambda] (spine inheritance untouched),
[D:paramful-rhs] (its resolver extension must reach the new
contexts), [D:match-arm-commands] / [D:interior-arming] (the
statement-side precedents that make the current asymmetry visible),
[D:command-sigils] (the `$()` charter this restores).
