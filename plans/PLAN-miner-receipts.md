# weir — plan proposal: the miner's receipts (`rmatchAll`, `feed`, `distinct` — and the last python retires)

Status: PROPOSED-AMENDED (regroup 2026-07-24): `feed` [D:spawn-spec],
`Seq.distinct` [D:seq-distinct], and `scriptPath` [D:script-path]
SHIPPED ahead of this plan; `Seq.groupBy` already existed. REMAINING
SCOPE: `Str.rmatchAll` (+ the join/readText decision) and the
corpus-mine.weir rewrite itself. Originally: PROPOSED (awaiting
bless/edit). One session + one rider.
Origin: the corpus-mine.py capability assessment left three named
gaps, each already receipt-shaped, plus the `$0` half of the
fuzz.weir friction. The unifying frame: **tools/corpus-mine.py is
the last python in the repo**, and its rewrite is the natural
acceptance — not a synthetic battery but a reproduction target with
published numbers (`base: extracted=4256 kept=78` against the
committed corpus reports). The plan is sized by the receipts, not
by the features' biggest versions — each gap ships its smallest
closing move, and the bigger version parks with its criterion.

## The receipts, restated

1. **No global regex** — `Str.rmatch` yields ONE match's groups;
   the miner needs every `"""…"""` in a file (TRIPLE.finditer).
   Sighted once, but structurally common (every scrape-shaped
   script wants all matches, not the first).
2. **No value→command stdin** — hashing a snippet STRING via
   `sha256sum` has no spelling; the plumbing EXISTS
   (`Proc.linesWith` takes `input: seq<string> option`) — only the
   surface is missing. Second receipt standing by: `$(git branch)
   | fzf`-class interactive selection over weir-computed values.
3. **No dedupe** — the miner's `seen` set. The receipt is
   MEMBERSHIP-during-accumulation, not keyed lookup — which is why
   this does NOT open the Map/Set door (below).
4. **No `$0`** — fuzz.weir routed around it with
   `git rev-parse --show-toplevel`; correct in-repo, unavailable to
   scripts that live anywhere else. One receipt; rider-sized.

## Pre-made decisions (proposed)

- PROPOSED — **`Str.rmatchAll pat s : seq<seq<string>>`**: every
  match's group seq, lazily; empty seq = no matches (the plural
  needs no Option — the absence IS the empty). Same contract as
  `rmatch` otherwise: any string pattern (computed patterns are
  expression-side), groups bind as strings, `(?s)`/`(?m)` inline
  flags cover DOTALL/MULTILINE so no options API grows. The
  whole-file-as-string question rides along [decide in-session:
  `Str.join sep : seq<string> -> string` (recommended — a general
  combinator, `File.read |> Str.join "\n"` composes) vs a new
  `File.readText`; one of the two, not both].
- PROPOSED — **`feed : string -> seq<string> -> seq<string> ->
  seq<string>`** (prog, args, input — data-LAST for piping:
  `snips |> feed "sha256sum" []`): spawns with the input seq as
  stdin, streams stdout as the result seq, raise-at-force on
  nonzero like every command value; `feedEnv` twin per the
  precedent. Deliberately a BUILTIN, not grammar: the value-headed
  pipeline spelling (`xs | fzf`) is a precedence-class change that
  collides with the bare-pipe teaching fatal — it parks below with
  the fzf receipt named, re-askable against the reifier-law entry.
- PROPOSED — **`Seq.distinct : seq<'a> -> seq<'a>`** (Eq-constrained
  — the class machinery exists; rejected at functions/seqs at the
  use site like `==`). Lazy, first-occurrence-wins, memoizes only
  what it has yielded. This closes the miner's set: dedupe becomes
  a PIPELINE STAGE (`|> Seq.distinct`) instead of threaded state —
  more idiomatic than the python it replaces. `Seq.countBy`/
  `groupBy` are NOT pulled in (no receipt names them); full
  Map/Set stays parked — the receipt is membership, and a keyed
  type ships on a keyed receipt.
- PROPOSED — **the rider: `scriptPath : string`** (script-only,
  like `args`/`stdin`; absent in REPL/-e by the same rule).
  fuzz.weir keeps its git-toplevel spelling (it WANTS the repo
  root, not the script dir) — the rider's pin is a fixture script
  printing its own dir. [Alternative if one receipt feels thin:
  park with the criterion "second non-repo tool script".]
- PROPOSED — **corpus-mine.weir is the acceptance**: the rewrite
  reproduces the python's published numbers on the committed
  corpus in BOTH modes (base: extracted=4256 kept=78; wide: the
  remine report's numbers) — count-identical, and the kept
  snippet SET identical (content-hash filenames now spellable via
  `feed "sha256sum"`). The python deletes; the scripting-policy
  fallback ledger closes its last entry. The reject-lists port as
  data (seqs of strings), keeping the filter diff readable as
  language growth — the property the python's header claims.
- Ceremony: builtin/Str surface only ⇒ no assembler grammar, no
  fuzzer obligation (stated for the scope rule); no new syntax ⇒
  POSITIONS n/a; oracle n/a (API, not shape); laziness claims
  (`rmatchAll`, `distinct`, `feed`) get pull-count pins per the
  standing rule — three new lazy surfaces, three counted sources;
  tripwires; timing.

## Work items

1. `Str.rmatchAll` (+ the join/readText decision) — pull-count pin,
   `(?s)` cross-line pin.
2. `feed`/`feedEnv` — stdin-close-on-exhaustion, raise-at-force,
   tree-kill lifecycle (the linesWith guarantees inherited, pinned);
   the sha256 shape as the e2e.
3. `Seq.distinct` — Eq constraint pins (rejects at fn/seq use
   sites), laziness pin, first-wins pin.
4. corpus-mine.weir: the rewrite; the two-mode number reproduction
   against the committed reports; python deleted; fallback ledger
   closed.
5. The rider (`scriptPath`) or its park, per bless.
6. Docs (SKILL: three lines; GUIDE: the scrape idiom —
   rmatchAll+distinct+feed as one pipeline); DECISIONS (one row per
   surface, or one row for the receipts-plan [decide]); NOTES;
   timing.

**Done when:** corpus-mine.weir reproduces both modes'
published numbers on the committed corpus and the python is gone;
the three surfaces carry their pull-count pins; `feed`'s lifecycle
matches the command-value guarantees; the parks are filed with
criteria; all green.

## Parked (proposed)

- **Map/Set as types** — the miner's receipt is membership;
  `Seq.distinct` answers it. Criterion to reopen: a receipt that
  needs KEYED lookup/aggregation a fold-over-record cannot spell
  (fixed field sets cover today's counters).
- **Value-headed command pipelines** (`xs | fzf`) — the grammar
  form of `feed`; a precedence-class change colliding with the
  bare-pipe teaching. Criterion: the fzf-selection receipt firing
  in a real script, weighed against the teaching-fatal's value.
- **`Seq.countBy`/`groupBy`** — no receipt names them; first
  aggregation script that folds into a record awkwardly reopens.
- **`File.readText`** — only if the session picks `Str.join` and
  a later receipt still wants it.

## Flags for the bless

- `feed`'s laziness semantics need one design sentence in-session:
  does the input seq pull lazily as the child reads, or drain
  eagerly? Recommendation: lazy pull on the writer task, matching
  linesWith's existing plumbing.
- The corpus-report reproduction assumes the componenttests corpus
  is available at its recorded path; if not in-container, the
  done-when degrades to the miner's unit fixtures plus a recorded
  manual run — stated, not silently narrowed.
