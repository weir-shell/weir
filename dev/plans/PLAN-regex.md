# weir — regex: the `Regex` pattern and the `Str` match family

Status: EXECUTED (landed 2026-07-22) — as blessed: LANDED 2026-07-22 (proposed 2026-07-21). Opens the standing
regex park. Origin: user question on the F# `ParseRegex`
active-pattern idiom — adopted in spirit, redesigned against its known
hole and weir's machinery.

Completion addenda (2026-07-22):
- SUPERSEDED (2026-07-22, PLAN-raw-strings): the positional raw
  lexer below retired the same week — rawness moved to the literal
  KIND (`@`/`"""`), and the Regex position became raw-ONLY by rider.
- The RAW-literal decision, implied by the plan's own examples but
  unstated in prose: the pattern-position literal writes `(\w+)=(\d+)`
  (backslashes belong to the engine, only `\"` escapes); the
  expression side (`Str.isMatch "\\.md$"`) keeps ordinary strings.
  Design case on record (review, same day): the pattern literal is not
  a string — it is a regex the CHECKER consumes (compiled, arity-read,
  cached); different literal kinds owning their own escape rules is
  precedent (interpolated strings' `{{`/`}}`), and double-escaped
  regex is the classic silently-matches-the-wrong-thing source, which
  would undercut the check-time story. The expression side must take
  ordinary strings because computed patterns are strings by nature — a
  magic literal rule there would evaporate under `let p = ...`.
  Boundary, compressed: regex literals in pattern position belong to
  the regex engine (raw, `\"` only); strings everywhere remain
  strings — the boundary is who consumes the escapes, and it never
  depends on context within a position.
- Verified per the plan's report items: System.Text.RegularExpressions
  is trim-clean in interpreted mode (AOT publish + full battery on the
  binary); the checker-arm route was taken and the deferral-regime tax
  paid (TRANSCRIPTION addendum, cache tripwire, POSITIONS pattern
  checklist).

## The F# idiom, graded first (the design's foil)

    | ParseRegex "(\d+)-(\d+)" [a; b] -> ...

What it gets right: matching and extraction in one pattern, in match
position — the shell sweet spot. What weir cannot import: it requires
user-definable ACTIVE PATTERNS and LIST PATTERNS, neither of which
exists (and neither opens here — see parks). What it gets WRONG, and
weir can fix: the regex is an opaque string to F#'s checker, so a
group-count/binder-arity mismatch is a SILENT runtime non-match —
no diagnostic, the arm just never fires. Weir owns its checker; a
regex literal is fully analyzable at check time.

## The design

    match line with
    | Regex "(\w+)=(\d+)" (key, count) -> ...   // 2 groups → pair, statically
    | Regex "^#" () -> ...                       // 0 groups → unit binder
    | Regex "v(\d+)" v -> ...                    // 1 group → bare binder
    | _ -> ...

- The regex literal is COMPILED AT CHECK TIME: an invalid pattern is
  a check error (before line one runs — check-everything-first now
  covers regex syntax); the capture-group count is read from the
  compiled pattern and the binder must be a tuple of exactly that
  arity (bare binder for 1, `()` for 0). Arity mismatch is a check
  error naming both counts. The F# hole, closed statically.
- All groups bind as `string` in v1. Conversions are explicit in the
  arm (`tryToInt count |> ...`) — no typed-group regex dialect
  (inventing `(?<n:int>...)` syntax inside regex is a weir-only
  regex flavor; reject-don't-guess).
- `Regex` patterns are REFUTABLE — legal in match, banned in binders
  by the existing refutable-binder rule (consistency free, pin it).
- Non-capturing groups `(?:...)` do not count toward arity (the
  engine's own group numbering is the authority — pin a mixed case).

## Pre-made decisions

- DECIDED — **Literal-only in pattern position.** A computed regex
  cannot be arity-checked; rather than a fallback with a different
  type shape (the guess), computed patterns live on the EXPRESSION
  side (below). The pattern-position error for a non-literal names
  that spelling.
- DECIDED — **The expression-side family, data-last, shipping
  together** (grep-in-the-language for pipelines):
  - `Str.isMatch : string -> string -> bool` — the where-filter:
    `where (Str.isMatch "\\.md$")`. Computed patterns fine (runtime
    regex errors join the boundary-validation class).
  - `Str.rmatch : string -> string -> Option<seq<string>>` — groups
    as a seq, unarity-typed, the computed-pattern workhorse; None on
    no match.
  - A literal-regex BESPOKE arm for rmatch is deliberately NOT taken
    (arity-typed Option<string * string> returns would be nice but
    duplicates the pattern form's machinery for a position match
    already serves — one typed spelling, in match, is the v1 story).
- DECIDED — **Named groups are the pre-scoped extension, not v1**:
  `| Regex "(?<key>\w+)=(?<val>.+)" ->` binding `key`/`val` directly
  is the natural v2 (names visible in the literal at check time;
  the casing law applies to group names — lowercase, enforced at
  check). Parked WITH this scoping so the reopened session is
  mechanical; v1's positional tuple does not preclude it.
- DECIDED — **AOT/engine discipline**: .NET Regex in INTERPRETED
  mode only — RegexOptions.Compiled is Reflection.Emit, banned by
  the standing rule [verified at completion: trim-clean].
  GeneratedRegex (source-gen) is inapplicable (weir-script literals
  are runtime to the host). Check-time compilation result is CACHED
  by pattern string (the check compiles once; eval reuses — one
  Regex instance per distinct literal, the snippet-hash-cache
  precedent).
- DECIDED — **No user active patterns open here.** The Regex pattern
  is a bespoke pattern form like literal patterns — one checker arm,
  one pattern kind — NOT a general active-pattern mechanism. User
  active patterns remain parked (they are the user-adapter question
  in disguise, with generalization/refutability design weight of
  their own); this plan neither needs nor enables them, stated so
  the door stays visibly closed rather than ajar.
- DECIDED — **Ceremony**: checker arm ⇒ deferral-regime tax in full
  (battery + tripwires + TRANSCRIPTION addendum). POSITIONS sweep
  for the pattern kind (nested in tuples/constructors; under guards;
  in binders → the refutable rejection). Products: Regex × literal
  patterns in one match (mixed arms), × exhaustiveness (Regex arms
  never complete a match — wildcard required, pinned), × classes
  (groups are strings; no interaction — one pin proving it), × the
  casing law (binder names lowercase as everywhere). Oracle: F# has
  no built-in Regex pattern (ParseRegex is userland) — the FIRST
  weir-only match form; divergence row regex-pattern, oracle-pinned.
- DECIDED — **Docs**: SKILL (the pattern with arity examples + the
  literal-only rule + must-fail arity mismatch), GUIDE (the
  grep-replacement idioms: isMatch filters, Regex-match extraction
  vs `| from` adapters — when structured parsing beats regex), and
  SEMANTICS (the pattern form, check-time compilation, group-arity
  rule, non-capturing exclusion, the raw-literal boundary).

## Parked (with rationale)

- **`=~` — condition-position match operator** (user-raised at
  review). The gap is real but narrow: `if x =~ "^test"` vs today's
  data-last inversion. The pipe spelling fixes the word order at a
  three-character cost — `if x |> Str.isMatch "^test" then` — and v1
  TEACHES that as the condition idiom (GUIDE's Matching-text
  section, shipped). The operator parks because its prior is a
  minefield, not an asset (bash ERE + BASH_REMATCH side effect, Perl
  contextual return, Ruby position-or-nil — importing the glyph
  imports a fourth dialect's ambiguity). Reopen trigger: bleed
  receipts against the TAUGHT pipe idiom (stranded `=~` attempts or
  recurring word-order friction), not the itch alone. IF built, the
  design is pre-made: literal RHS compiled at check time (the
  pattern form's machinery reused — RAW-ONLY, `@"..."` or triple,
  per PLAN-raw-strings' rider; invalid regex is a check error),
  typed `string -> string -> bool`, right-side literal-only, and NO
  capture side-channel ever (extraction stays the pattern form's
  job; groups-from-`=~` is how Perl's contextual mess starts).
  Divergence row acknowledged as weir-only on arrival.
- Named-group binding — pre-scoped above; v2 on receipts or call.
- Arity-typed expression-side rmatch — one typed spelling suffices;
  reopen if match-position proves the wrong home in practice.
- User active patterns — the adapter question; own design if ever.
- Regex flags/options syntax (`(?i)` works inline — .NET native;
  a weir-level options argument waits for a receipt).
- `Str.replace` regex variant, split-by-regex — members-session
  items once the engine is in; not v1.
