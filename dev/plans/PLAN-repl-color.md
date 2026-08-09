# weir — REPL syntax coloring

Status: BLESSED (user 2026-07-23). One session. EXECUTED 2026-07-23
— see the completion addenda. Tier scope: input-line lexical
coloring + the fish-style head-resolution trick + colored error
carets. Semantic coloring is NOT this session.

## Pre-made decisions (abridged; full text in the blessing message)

- DECIDED — the colorizer IS the classifier + scanner, no new
  string-state logic by law; the one highlighter correct by
  construction.
- DECIDED — lexical grade per keystroke; no parser invocation.
- DECIDED — the fish trick: head colors by the session resolver's
  verdict; `^`-forced heads resolve PATH-only.
- DECIDED — tier 2, errors only (echo type-tails not ridden).
- DECIDED — plain text everywhere non-tty; NO_COLOR honored; the
  harness-sees-no-ANSI pin FIRST; fixed palette, no theming.
- DECIDED — width bookkeeping through one strip-escapes helper.
- DECIDED — products: completion, history, multi-byte, unreachable
  classifier states, timing ceiling.

## Completion addenda (2026-07-23)

### Done-when, discharged

Typing shows live head resolution (bold known / blue PATH / red
unresolved) and lexical spans (keywords, all three string kinds,
comments, numbers, uppercase per the casing law, sigils/splices,
operators); an unclosed `@"` colors to EOL (pinned as live
feedback); piped output is byte-clean (the harness guard, e2e);
NO_COLOR under a pty suppresses every span (pty pin); the
transparency property (strip ∘ colorize = id) holds over fixtures
including emoji, unclosed raw, and an unclosed bracket; the
redraw-cost ceiling pinned (1000 pathological 200-char lines
< 2s). 779 unit / 131 oracle / full e2e / 49 doc blocks; timing
unchanged.

### Two mechanisms dissolved on contact

- **The strip-escapes width helper is UNNECESSARY**: the editor's
  buf/pos never hold escapes — coloring happens only at the paint
  (redraw's single choke point), so all cursor arithmetic stays on
  plain text and the off-by-escape class cannot exist. The helper
  survives only as `stripAnsi` for tests.
- **Tier 2 pre-existed**: error carets/underlines were colored by
  the colored-diagnostics session (red carets, yellow warnings) —
  verified, not rebuilt.

### Verify clauses closed

- The cache-cost clause: `Extern.exists` is a set lookup; head
  re-resolution per redraw is noise. The ceiling pin covers the
  whole paint including resolution — no word-boundary fallback
  needed.
- `^print` paints red while bare `print` paints bold — the
  forced-head PATH-only semantics, pinned.

### The predicted regression, caught by the battery

The plan named it: "every existing REPL pin asserts prompt output;
ANSI leaking into them is the regression risk." The wordnav pty pin
failed on first full run — the painted input echo split `ab9.cd`
across spans. The harness now strips color before asserting (pins
are about TEXT, not paint), which is the structural form of the
guard the plan asked for.

## Parked (unchanged)

- Semantic coloring — the LSP semantic-tokens park; the classifier
  reuse recorded as evidence for its eventual design.
- Echo type-tail coloring; themes/palette config.
- Continuation-prompt coloring — rides the multiline-REPL park;
  the colorizer inherits the classifier's states for free when it
  opens.
