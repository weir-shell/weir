# PLAN — fmt keeps the layout inside an open bracket

Status: APPROVED (user, 2026-10-13) — R1: preserve (shift, don't re-level).

Found while assembling the bug sweep (PLAN-sweep-fixes.md): `weir fmt` over
`ci/feature-docs.weir`, `ci/release-published.weir` and
`ci/site-staleness.weir` rewrites them badly. The 92789e3 build does the same,
so this was not introduced by the sweep. The parse is unchanged in every case
(the safety check passes and the output runs identically), and fmt is
idempotent on its own output — only the layout is destroyed, which is why no
gate caught it: the repo-wide "fmt is a no-op" gate was never enforced in CI.

## F1 — a multi-line value inside a `{ … }` entry collapses to the entry column

```weir
let step st line =
    { A = not st.A
      Kept =
        (if st.A then
             st.Kept
         else
             $"{st.Kept}\n{line}") }
```
becomes
```weir
    { A = not st.A
      Kept =
      (if st.A then
      st.Kept
      else
      $"{st.Kept}\n{line}") }
```
**Cause.** Inside a non-Stroustrup `{`/`[`, every continuation line is placed
at the first entry's measured column (`Fmt.fs:299-307`,
[D:field-alignment]). That rule is right for a new *entry* and wrong for a
line that continues an entry: the value's own structure is flattened.

## F2 — lines inside an open `(` are re-levelled as if they were offside blocks

```weir
let s =
    (if true then
         "a"
     else
         "b")
```
becomes `"a"` at 8, `else` at 8, `"b"` at 12; and
```weir
let burned =
    (File.read "x.yaml"
     |> from yaml T)
        .burned
```
shifts the `|>` to 8 and `.burned` to 12.

**Cause.** `(` is not on the brace stack (`Fmt.fs:367`: "Parens are the only
opener that reaches here"), so lines inside an open paren go through the
offside-level path (`Fmt.fs:338-345`). That path turns every distinct indent
into a new 4-space level, so paren-aligned columns (`(` + 1 = 5, 9, …) become
levels 2 and 3.

## One rule for both: shift, don't re-level, inside an open bracket

Inside any open `(`, `[` or `{`, a line that is not a new entry and not a
closer keeps its offset from its opener:

    newIndent = opener's new column + (line's original indent − opener's original column)

- **New entries** (a line at the first entry's original column in `[`/`{`)
  keep [D:field-alignment]: they snap to the anchor.
- **Closers** and **Stroustrup** bodies keep their current rules.
- **`(match` arms** keep [D:fmt-match-arms]; the match-arm path runs before
  the bracket rule, as now.
- **Parens join the brace stack**, so F2's lines take the same rule. A paren
  opened and closed on one line is never pushed, so ordinary calls don't change.
- **When nothing outside moved,** the shift is 0 and the author's layout
  inside the bracket is kept byte-for-byte. That is the common case and the
  one the three ci scripts need.

**Ruling owed (R1):** inside a bracket, should fmt *preserve* the author's
relative layout (the rule above, recommended) or *normalise* it to a
canonical shape (fantomas-style: `if/then/else` at fixed offsets)?
RECOMMEND preserve: normalising needs a real pretty-printer over the AST,
which fmt deliberately is not (it is a line re-indenter plus a guarded
respace, [D:fmt-respace]). Preserve fixes both bugs and can never make a
layout worse than its author wrote it.

## Work

1. **Repro and corpus first.** Unit tests for F1 and F2 (the snippets above,
   plus `[ … ]` and nested `({ … })` variants), each asserting fmt is the
   identity on already-aligned input and idempotent. Add the three ci scripts
   as fixtures.
2. **Parens on the stack.** Push `(` in `Script.braceStack` for fmt's use
   (check what other callers of `braceStack` expect; give fmt its own variant
   if they rely on parens being absent).
3. **The shift rule** in the bracket branch (`Fmt.fs:288-307`), recording
   each opener's original column alongside its new column.
4. **Gate.** Add a CI cell: `weir fmt --check` over every tracked `.weir` file
   must pass, so a repo-wide fmt regression fails CI instead of waiting for
   someone to notice. Run fmt over the three ci scripts once the fix lands; the
   expected diff is empty.
5. **Parser depth:** fmt doesn't touch the parser, so no depth measurement is
   needed. If step 2 changes `Script.braceStack` for the assembler too, measure
   per the depth-budget rule.

Delivery: one branch `fix/fmt-bracket-layout`, stacked on
`fix/sweep-diagnostics` (it touches `Fmt.fs` after B2/B14/B15). Area prefix
from `ci/commit-area.weir`; CHANGELOG line under v0.0.73 Fixed.

## Follow-ups riding the same branch (approved 2026-10-13)

- **Bidi controls at a tty** [D:tty-bidi-sanitize]: extend
  `Eval.sanitizeTtyData` to render U+202A–202E, U+2066–2069, U+200E/200F and
  U+061C as a visible `\uXXXX` when the sink is a tty. Pipes and files stay
  byte-faithful.
- **CLI JSON at a tty:** `weir check --json` and `--can --json` pass their
  output through `sanitizeIfTty`. The JSON encoder itself is unchanged.
