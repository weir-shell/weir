# weir — mini-plan: `for`/`do` — the general iteration form

Status: PROPOSED (drafted 2026-07-30, awaiting bless). One small
session. Origin: the YAML-templates design put `for x in xs` inside
yaml districts only; the user challenged the restriction — why not
F#'s own general form? On the merits the restriction was SCOPE
discipline (a yaml arc should not mint a language form as a
side-effect), not a verdict. This plan is that form's own decision.

## Why (three reasons, all load-bearing)

1. **It is F#'s own form.** Weir takes F#'s spellings (`elif`, raw
   strings, `{ r with }`); F# has `for x in xs do body` and
   `[for x in xs -> e]`. Declining them is a DIVERGENCE needing its
   own justification, and "we have Seq.iter" is weaker than the
   divergence bar.
2. **Shell muscle memory.** `for f in *.txt; do …; done` is THE
   shell loop; weir's current answer (`|> Seq.iter (fun f -> …)`)
   is correct FP and alien at a prompt.
3. **It makes the yaml district's `for` a SPECIALIZATION** of a
   general form (context-typed yield) instead of a one-place oddity.

## The forms (exactly two; `seq { }` CEs never)

1. **Statement/effect**: `for <binderPat> in <expr> do <body>` — an
   expression of type `unit` (F#-faithful), body must be `unit` (the
   statement rule — falls out of the desugar's type, no new gate).
2. **Comprehension**: `[for <binderPat> in <expr> -> <elem>]` — the
   F# list-comprehension spelling, yielding weir's eager list
   literal. IN this session ONLY IF it is genuinely the same desugar
   path; otherwise parked with that finding stated.

## The one mechanism: desugar at the typed tree (the reifier precedent)

- `for p in xs do body` ⇢ `xs |> Seq.iter (fun p -> body)`
- `[for p in xs -> e]` ⇢ `xs |> Seq.map (fun p -> e)` materialized
  as the list literal's elements

Zero new eval machinery; warnings, hover, and the discard family ride
the existing tree. The desugar is PINNED byte-identical: the same
loop spelled both ways produces identical output and identical typed
behavior.

## Decisions pre-made (state in DECISIONS, pin each)

- **Two new reserved words**: `for`, `do` — keyword-domination
  applies; the completion-inventory pin moves; both OFFERED (statement
  starters, like `let`/`match`).
- **The binder is `binderPat`** — `for (k, v) in pairs` works day
  one; wildcard `for _ in xs` too.
- **After `in` is plain expression land** — any seq-typed
  expression: `for x in images |> Seq.where _.public do …`. Commands
  need their expression spellings (`$(git branch)`) — no bare command
  chains after `in` (the mode rules are untouched).
- **Body rules are the `if … then` body rules** — inline expression,
  `;`-sequencing, multi-line block by indentation, and the `!`
  district opener composes:

      for f in files do !
          git add $f
          git status --short $f

- **Eager**, stated (it IS `Seq.iter` — no lazy-loop surprise).
- **No guard syntax** — filtering is `Seq.where` upstream or `if` in
  the body; a `when` clause is a THIRD spelling for filtering and is
  refused.
- **The docs law, stated once**: *pipelines transform; `for`
  effects.* `map/where/fold` chains stay the idiom for producing
  values; `for … do` is the idiom for doing things N times. The
  desugar makes "same machine" true by construction.

## Relationship to the YAML arc

This lands BEFORE DESIGN-yaml-templates session 2: the district's
`for` then arrives as the general form in node context (sequence
context yields items, mapping context yields pairs) — the yaml design
keeps only the context-yield rules, not the form's definition.

## Bars

- Zero movement on everything existing (`for`/`do` were not legal
  identifiers in any pinned text — verify with the sweep grep, state
  the count).
- The desugar-equivalence pin (both spellings, byte-identical).
- The unit-body discard error reads as the statement rule's existing
  message family.
- Keyword pins: reserved-word errors for `let for = 1` /
  `let do = 1`; the completion inventory row.
- e2e: an effect loop over a real command (`for f in $(ls …) do
  !(…)`) against the AOT binary; the district-body form.
- Docs: SEMANTICS (the law + the form, once), GUIDE (the shell-loop
  example), SKILL (the idiom line), LEXICON if "comprehension" enters
  the vocabulary.

**Done when:** both forms parse/check/run with pinned desugars; the
keywords are reserved and offered; the district-body composition
works; the docs law is stated once; zero pin movement outside the
named inventory pins; the report names any comprehension-path finding.
