# PLAN — fixing hunt 3 round 2 (H3R2-*)

Source: PLAN-hunt3.md "Round 2". Findings, repros and rulings:
`/output/tmp/h3r2-findings.md` (repros under `/output/tmp/h3r2-<area>/`).
Target the fixes against: main @ ae61572 (the round's binary,
`/output/tmp/weir-h3r2/Weir`).

8 high, 17 medium. A (xplat) clean. Lows go to BACKLOG.

## Gates (every stage)

- unit: `dotnet test tests/Weir.Tests/Weir.Tests.fsproj` (Debug), plus
  `dotnet build tests/Weir.Fuzz/Weir.Fuzz.fsproj` after merges;
- AOT publish to the stage's own dir, then e2e under
  `flock /output/wt/e2e.lock`;
- `tools/hunt3r2-repro.weir --bin <aot>` — the stage's probes OK, no
  other probe or control newly failing;
- `tools/hunt3-repro.weir`, `hunt2-repro`, `sweep-repro` stay green;
- every fix lands with a pin (unit or e2e) and, where behaviour is
  user-visible, the SKILL.md / docs line that states it.

## Rulings (user 2026-10-17, all as recommended)

- **R1 (B7)** add schema accepts root `$id` (ignored), `const` (drafts
  the base type + a not-enforced note), `contentEncoding`/
  `contentMediaType`/`format` (not-enforced notes).
- **R2 (E3)** Secret.map rethrows with the message withheld, keeping
  the error kind and the builtin's name. High.
- **R3 (E4)** a serve handler still running at scope close is cut at
  its next effect boundary (the pfirst-loser mechanism); no handler
  effect after `within serve` returns.
- **R4 (F1/C2/F3)** weir owns one stdin reader that buffers lines; a
  cancelled read (lost race, REPL ^C) leaves its line for the next
  reader — a cut never consumes input.
- **R5 (C1)** a signal's teardown cancels in-flight pmap/piter/pfirst
  arms at their next boundary and joins them before any enclosing scope
  closes; pure compute holds teardown, the second signal hard-exits.
- **R6 (B6)** mappings read last-wins; `to json`/`to yaml` refuse a
  mapping with duplicate keys, naming the key.
- **R7** a bare CR passes the tty sanitizer on every sink.
- **R8 (F2)** Path.glob segments match with a linear wildcard matcher
  (`*`, `?`, classes), no regex.

## Stage 1 — secrets and the terminal (`fix/h3r2-safety`)

- **E1 (high)** compile-time diagnostics echo raw escapes: check's
  unresolved-import path, the parse/type error source echo, the
  invalid-regex message, `#sig` tool name, `no such script`. Route every
  diagnostic sink through the round-1 tty sanitizer.
- **E2 (high)** from json/jsonl/toml errors echo the document with a
  Secret field's value. When the target type holds a Secret anywhere,
  the excerpt is withheld (say so); otherwise unchanged.
- **E3 (high)** Secret.map raising prints the cleartext. Per R2.
- **R7** bare CR through the sanitizer on every sink.

## Stage 2 — cancellation, teardown, stdin (`fix/h3r2-cancel`)

- **F1/C2 (high) + F3 (medium)** stdin reads cannot be cut. Per R4:
  one weir-owned stdin reader (Self.prompt, Self.stdin, the REPL's
  piped input), cancellable waits on its queue, no line lost. A FIFO
  `File.read` in a loser: cut the wait (reader abandoned) — the R2
  pipe-read precedent.
- **C1 (high)** teardown does not join in-flight arms. Per R5.
- **C3 (high)** a pfirst loser's `always` commands are killed at
  registration. Spawns inside a cleanup are exempt from condemnation
  (the [D:teardown-always-spawns] law), and the race joins them.
- **C4 (high)** REPL ^C during a fan-out whose arms run commands does
  not stop it: an arm whose child died of the interrupt is an
  interrupt, not an arm failure — the fan-out stops taking arms.
- **C5 (high)** a signal during pfirst races leaves SIGSTOPped orphans:
  the teardown must settle in-flight race kills (or kill without the
  stop-then-kill tree walk) before exit.
- **E4 (medium)** late serve handlers keep doing effects. Per R3.

## Stage 3 — formats, plan, gen (`fix/h3r2-formats`)

- **B1** gen types: number-or-string and `format: int-or-string`
  draft Yaml (the int-or-string fix's class).
- **B2** gen types: an enum under an explicit boolean/number type, and
  mixed enums, draft the declared type (or Yaml + a note for mixed).
- **B3** to yaml: an all-None record writes `{}`, an empty top-level
  seq `[]`; the stream form never drops a document.
- **B4** the known-after-apply guard compares resolved paths (symlink
  aliases), and folds case where the filesystem does (macOS too).
- **B5** a nested plan checks every enclosing frame.
- **B6** per R6.
- **B7** per R1.
- **D1** the stale-read guard is O(ops) per read: index captured paths
  (a trie or per-directory set) so a read is O(depth).
- **D2** deep-JSON refusal is O(depth²): refuse at the bound in one
  pass.

## Stage 4 — network, glob, perf (`fix/h3r2-misc`)

- **E5** a failing redirect target's error names the failing host (and
  the TLS hint names it); restore too.
- **E6** `NO_PROXY=*` bypasses the proxy for every host.
- **F2** per R8 (and dedupe repeated-`**` results — BACKLOG L2 rides it).
- **D3** frozen seqs know their length and index in O(1).
- **D4** `Str.length`/`Str.sub`/single-char slices on ASCII-only
  strings are O(1) (detect once, or cache the code-point index).
- **D5** the regex timeout message names input size as a cause when the
  input is large, instead of asserting backtracking.

## Order

Stages 1, 3, 4 in parallel (disjoint enough; merge order 1 → 3 → 4);
stage 2 alone after them (it touches Session/Builtins concurrency that
the others also edit, and its probes are timing-shaped). The harness
`tools/hunt3r2-repro.weir` is written first, against main, every probe
REPRODUCES.
