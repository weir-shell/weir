# weir — semantic tokens: the mode boundary made visible

Status: BLESSED (user 2026-07-24). One session + a rider. This
OPENS the standing semantic-tokens park on its own named trigger —
"command-mode highlighting is the thing regex genuinely cannot do"
— fired by user call with the receipt attached: the fuzzer's
verdict-split find would have been VISIBLE the instant it rendered.

(Blessed plan text as delivered; the executed decisions below.)

## Session report (2026-07-24)

GATE: the dependency session ran first and CORRECTED its own blessed
diagnosis — the assume-resolver was never the door (the head gate
already refuses known names; tenv-known junk errored correctly all
along). The mechanism was seqExpr's attempt-wrapped `;`-chain: a
failing element un-consumed the `;` and re-parsed the tail OUTSIDE
its let-in scope, where the genuinely-unknown binding was claimable.
Fix [D:seq-commit]: a consumed `;` commits to its element (the
consumed-`|` fatal's sibling, one line). Check≡run on the class;
the district span class fixed FOR FREE (primary now anchors on the
junk); deep fuzz green on the finding seed; strict spans red on one
remaining class (bare-pipe fatal — its own open question).

The tokens session, per the decided design:

- Engine: analyzeLines → TypedExpr walk → translate(); mode spans
  only (weirCommandHead/weirArgv/weirSplice), expression land emits
  nothing. Bareword argv only — quoted/raw/interp args keep lexical
  string coloring.
- The synthetic-span rule implemented as VERBATIM-at-physical-home:
  emit only where the logical slice appears verbatim at its
  translated position — district wrapper glyphs and joins emit
  nothing; the district pin proves body lines token with no leakage
  past EOL.
- Two recognizers the plan did not enumerate: reified chains
  (desugared to an application spine before the checker — the walk
  recognizes it; the reifier NAME stays lexical, the recommendation
  taken) and sigil-origin heads (TECmd spans open on `$(`/`!(` —
  the head scanner walks past the glyphs; found by the depth-2
  nesting pin). Fixture truth: `$(...)` as a direct command ARG is
  a type error (seq) — nesting rides a paren splice, pinned.
- The shadowed-cat trio passes as acceptance (bound → silence;
  unbound → tokens; ^ in the head span); the verdict-split repro
  renders expression-colored (a failed statement emits nothing —
  partial truth pinned separately with junk mid-file).
- Protocol battery: legend asserted at initialize; the two-line
  two-token minimal decodes the five-int deltas first; the
  position-matrix fixture covers statement/block-let/district/
  shadowing/stage cells; latency bounded (<500ms, comfortably met —
  whole-file recompute per the standing license).
- Client: semanticTokenTypes + semanticTokenScopes onto standard
  TextMate scopes (argv = string.unquoted, the fish/nu inert-words
  convention) — default themes color the boundary; SMOKE.md carries
  the interactive checks. Micro coarse by design, noted in-file.
- The REPL rider: the fish-trick head arms a dim argv tint that
  un-dims at `|` — same resolver, no new derivation; transparency
  pins re-ran green.
- The park CLOSED with its archaeology in NOTES.

## Parked (as blessed, standing)

- Delta/range tokens — on latency receipts only.
- Lexical tokens server-side — the minimal-legend rationale;
  re-askable against it.
- Micro semantic support — uninvestigated; the static grammar stays
  micro's truth.
