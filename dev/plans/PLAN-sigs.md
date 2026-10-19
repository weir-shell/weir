# PLAN — command signatures that help (`weir add sig` + the flag check)

Source: the 2026-10-18 survey (21 tools, 192 scored lines; scratch
`/output/tmp/sig-survey/` — `score.txt`, `gen.tsv`, `p/<tool>/`).
Ruling (user 2026-10-18): improve the generator and the check, never a
curated sig library.

Today a sig is good on clap/modern-cobra tools (rg, kustomize, yq: 0
false positives) and harmful on the most-scripted ones (git, gh, docker,
curl, gpg, npm: valid flags warned ~2:1 over typos caught).

## Gate

`tools/sig-score.weir` — the survey's scorer over CAPTURED tool output:
each tool's help / completion / `__complete` / fish file / version text
recorded as fixtures under `tests/sigs/<tool>/` with a fake binary that
replays them, so CI needs no tool installed and a run is deterministic.
Per tool: generate the sig, check the scored lines, report TP / FP / FN
against the expected file. Rides linux-hunts. Target: FP 0 on every
fixture tool; FN only where documented; jq, yq, kustomize, rg, grep,
systemctl, journalctl, tar `exhaustive`-ready.

## Stage 1 — the check (`Script.fs` ~5729–5945)

- Parent fallback: a line whose next word names no known case under a
  case that has children is not checked against the parent (or the
  generator marks unwalked cases and the check skips them).
- Flags carry arity: a value-taking flag consumes its value (`--context
  prod ps -a`, `-d bridge`), `--flag=value` too.
- A sig's flag convention: GNU (default), Go (`-x`, `-long`, `--long`
  equivalent; checks single-dash long words), find-style predicates;
  GNU long-flag abbreviations accepted where the tool does.
- An operand boundary: after the first operand of an interpreter or
  `docker run IMAGE`-like case, nothing is checked (sig field).
- Short bundles (`-xvf`) checked letter by letter when the sig records
  shorts; did-you-mean considers shorts and refuses distant matches.

## Stage 2 — the generator (`Script.fs` ~5954–6600)

- Sources, best first: cobra `__complete` (flags with shorts, docs,
  subcommands incl. plugins), git `--list-cmds` + `--git-completion-helper`
  (a trailing `=` marks a value), clap/argparse/fish completions (the
  fish parser reads long-form `--long-option`/`--short-option`/
  `--old-option`, `-o` single-dash long, `-r`/`-x` value), full-help
  switches (`--help all`, `--dump-options`, `--help-all`,
  `--v8-options`), `--help`, then `-h`/`-help`/`-W help`.
- Rows: short-with-arg, several shorts per row, short-only rows,
  `-i[SUFFIX]`, Go single-char flags; record arity.
- Phantom filter: a subcommand token starts with a letter, never a `-`;
  description-column lines and headings like `Command options:` are not
  command lists; never probe a word that isn't a listed command.
- Budget: depth-first within a budget per top-level command, so depth 2
  is reached on gh/docker; plugin commands kept; globals recorded once,
  not copied into every case.
- Version line: the first line, trimmed of library lists (curl).
- The convention, the operand boundary and arities are detected and
  written into the sig.

## Order

Gate first (fixtures captured from the survey's tools; every current
defect visible as FP/FN). Stage 1, then stage 2, one agent, in
`Script.fs` (stays out of the round-4 core). Docs: tooling.md
"Command signatures", SKILL lines. Lands after round 4's fixes.
