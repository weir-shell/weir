# PLAN — fixing hunt 3 round 6 (H3R6-*)

Source: round 6, the black-box check of round 5's fixes. Findings, repros,
rulings: `/output/tmp/h3r6-findings.md` (repros under
`/output/tmp/h3r6-<area>/`). Target: main @ 1b13a23
(`/output/tmp/weir-h3r6/Weir`).

4 high (E1, E2, C2-1, C1-2), 43 medium, plus W1 (Windows, known before
the hunt). Lows go to BACKLOG ('From hunt-3 round 6').

## Gates (every stage)

As PLAN-hunt3r5-fixes, plus: `tools/hunt3r6f-repro.weir --bin <aot>` —
the stage's probes OK, nothing newly failing; `hunt3r5f-repro` 50/50,
`hunt3r4f` 30/30, `hunt3r4` 9/9, `hunt3r3` 22/22, `hunt3r2` 25/25 stay
green. Stage A also: every storm.yml step 0 bad / 0 known. Stage S also:
`tools/sig-score.weir` fixtures and `--live` lose no line.

## Rulings (user 2026-10-20, all as recommended)

- **R1 (E7)** URL userinfo is sent as `Authorization: Basic …`, like curl,
  unless the script set Authorization; displays stay masked, the lock
  still drops it.
- **R2 (E8)** anything weir writes as a message or diagnostic (stderr,
  status lines such as "added …", "answered 404") escapes data, piped or
  not; byte-faithful applies only to data weir is asked to output.
- **R3 (C2-6)** Esc+Enter is a newline only when the Enter is already
  queued when the Esc is read (a paste, a real Alt+Enter); no waiting.
- **R4 (round-5 R10 narrowed; C1-1, C1-2, C2-2)** the pass applies only to
  a cleanup statement command whose output is unused and that was running
  when weir received the signal; a value capture fails on the death like a
  race arm; a command started after the signal gets no pass.

## Stage A — core: signals, settle waits, shim, fds, FIFO dirs (`fix/h3r6f-core`)

- **R4**: **C1-2 (high)** a truncated capture never continues; **C1-1** an
  own exit of 128+n is a failure; **C2-2** `| line` fails honestly.
- **C1-3, C2-3** the settle waits (`settleInterrupt`,
  `settleReplInterrupt`) run only when a signal is pending, never on an
  exit code alone.
- **C1-4** a cleanup command through the shim refuses exactly what a
  direct spawn refuses (no ENOEXEC fallback).
- **C1-5** the shim decision holds for a weir that becomes foreground
  after the spawn (bg → fg, ^Z + bg).
- **C1-6** a closed fd 0/1 is reopened on /dev/null at start, as fd 2 is.
- **C1-7** a dir created by `within tmp` is registered before any signal
  can see it.
- **C2-1 (high)** `Dir.copy` recreates FIFOs and other special files as
  `cp -r` does (never reads them).
- **C2-4** `#infer`'s source and the prompt function run with ISIG on.
- **D1** `| complete` reads stderr without holding a pool thread, so a
  wide pfirst never waits for thread injection.
- **W1** after a cut, waitProcess does not wait for a redirected stream's
  EOF (Windows: a grandchild missed by the tree kill holds the pipe).

## Stage S — command signatures (`fix/h3r6f-sigs`)

- **E1 (high)** a sig file name stays under NAME_MAX (long paths hash).
- **E2 (high)** a signal to `weir add sig` kills the probe's group and
  removes its temp dir.
- **E3** a bounded probe keeps what it read and says it was cut.
- **E4** a legacy-named sig owned by another lock entry is never
  overwritten; **E11** the hand-write hint names the encoded file.
- **S1** `git stash show` passes diff options (unchecked);
  **S2** wrapper usages: `[command [arg ...]]`, `COMMAND [ARGUMENTS...]`,
  `<priority> <command> [<arg>...]`, `PROG [ARGS]`, bunx/uvx;
  **S3** dashWords leaves single-letter typos checked;
  **S4** a listed `--x=BOOL` adds `--no-x`;
  **S5** a generated sig is never mistaken for an older one (a marker);
  **S6** git builtins with subcommands get a parent case;
  **S7** sig-score fails on a crashed check, a sig load error, and
  matches `// clean` exactly.
- Low folded in: `add sig git` runs each probe once (227 duplicates).

## Stage B — safety (`fix/h3r6f-safety`)

- **E5** Args.load never echoes a Secret's value (attached short forms).
- **E6** a malformed HTTP(S)_PROXY refuses, as curl does.
- **R1 (E7)**, **R2 (E8)**.

## Stage C — formats (`fix/h3r6f-formats`)

- **B4** floats YAML 1.1 reads as strings keep their text (`1e5`, `+.5`,
  `3.10`); **B7** a float field refuses a leading-zero literal, as int
  does; **B3** overflowing floats keep their text plain.
- **B1** `to json` of a leading-zero bigint writes digits; **B5** hex/
  octal past int64 as the docs say (or the docs change); **B2** a value of
  only NBSP/U+3000 is a string; **B8** a plain non-string key keeps its
  plain spelling on write (or a ruling-free note if by design).
- **B6** an XML declaration that contradicts the bytes says so;
  **E10** `from xml` gets a depth bound like JSON/YAML.
- **E9** gen types escapes schema keys and enum values; **B9** uint64
  under a nullable anyOf/oneOf drafts losslessly.
- **F4** `#infer` / `Yaml.inferShape` draft `0644` so the typed read of
  the same sample succeeds, and draft 0x/0o/.inf as numbers.

## Stage P — plans, completion, editor (`fix/h3r6f-plan`)

- **F1** Dir.copy checks the destination parent like every other op;
  **F2** a plan deletes/moves a dangling link itself; **F5** a write
  through a link into a missing dir refuses at capture.
- **D2** the plan simulation's link resolution is linear in depth
  (memoize the prefix walk).
- **F3** LSP `$` completion inside a yaml block offers the yaml-splice
  set; **C2-5** `$name` completion matches the checker everywhere (not
  inside `"…"`, not after `--x=`, records only with their fields, the
  filter in `$(…)`/if/for/match bodies).
- **R3 (C2-6)**.

## Order

Harness first (`tools/hunt3r6f-repro.weir`, every probe REPRODUCES on
1b13a23). Wave 1: A, S, C. Wave 2: P, B. Merge B → P → C → S → A. Lows to
BACKLOG at the end with the ledger; then the release (decided before the
round: release after round 6).

## Status (2026-10-21)

All four highs and every medium fixed on plan/hunt3r6f; W1 made
defensive ([D:wait-after-cut], the Windows cause unconfirmed) and its
Linux twin fixed; C1-7 pinned statistically in e2e. User rulings beside
the plan: R5 (B8) parsed YAML keys keep their plain spelling
([D:yaml-key-spelling]); R6 the settle wait is a 5 ms grace with no
signal in flight, one ≤250 ms wait before dying of an uncaught 129-143
failure ([D:settle-pending]). Final gates at cb777df:
tools/hunt3r6f-repro.weir 46/46 (twice) with its controls, every older
harness green, every storm step 0 bad / 0 known (300 per storm.py mode,
240 per REPL mode), the race-tree fuzzer 0 bad in 500 trees, sig-score
32 fixtures 0 failing. linux-hunts runs 12.5 min of its 20. Round-6
lows in dev/BACKLOG.md. Round 6 FIXED; the release follows.
