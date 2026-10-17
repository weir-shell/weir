# PLAN — fixing hunt 3 round 3 (H3R3-*)

Source: PLAN-hunt3.md "Round 3". Findings, repros, rulings:
`/output/tmp/h3r3-findings.md` (repros under `/output/tmp/h3r3-<area>/`).
Target: main @ 04da379 (`/output/tmp/weir-h3r3/Weir`).

6 high, 16 medium (duplicates folded: F1 = E2, C3 = E3, F4b = B1). A
(xplat) clean. Lows go to BACKLOG.

## Gates (every stage)

As PLAN-hunt3r2-fixes, plus: `tools/hunt3r3-repro.weir --bin <aot>` —
the stage's probes OK, nothing newly failing; `tools/hunt3r2-repro.weir`
stays 25/25.

## Rulings (user 2026-10-17, all as recommended)

- **R1 (E6)** split by sink: `print`/`printerr`/`Log` keep CR and SGR
  ([D:tty-cr-pass]'s purpose); weir's DATA renderers escape them —
  `show` (`\r` like `\n`), the REPL echo, table cells, `Plan.preview`
  paths, the `-e` echo.
- **R2 (B4)** the plan guard also keys captured write targets by
  (device, inode) (Windows: the file index); a read of any path sharing
  one is refused as known-after-apply.
- **R3 (F2)** a tty `Self.prompt` stays in cooked mode and drops CSI/SS3
  key sequences from the answer (arrows ignored, as v0.0.74).

## Stage 1 — terminal, secrets, network, cache (`fix/h3r3-safety`)

- **E1 (high)** `Args.load --help` (and `<case> --help`) writes `///`
  text raw: route it through the tty sanitizer.
- **E2 (high)** `weir verify`/`restore` print lock.json fields (name,
  url, kind, the recorded/installed `--version` strings) raw: sanitize;
  an unknown `kind` refuses.
- **E6** per R1.
- **E4** a dead proxy's error names the proxy, not the target (http and
  https).
- **E5** `NO_PROXY` CIDR entries bypass (curl's convention).
- **D2** the code-point cache: no table entry for strings that need no
  index (ASCII/BMP-without-surrogates answer from the length); the
  cache must not grow with streamed strings.

## Stage 2 — cancellation, teardown, stdin (`fix/h3r3-cancel`)

- **C1 (high)** a requested teardown never preempts an `always` that is
  starting or running on the clean path: the cleanup runs whole, then
  the signal's exit.
- **C2 (high)** the teardown join abandons an arm blocked in a read the
  cut cannot reach (FIFO open/read, an orphan-held pipe) — the pfirst
  precedent; the arm's next boundary raises.
- **C4 (high)** an abandoned FIFO read never consumes later data: FIFO
  reads open non-blocking and poll beside the cut, so a cut closes the fd.
- **D4 (high)** SIGINT to weir's pid alone tree-kills the foreground
  command and fan-out arms, as SIGTERM does, scoped or not (SKILL).
- **C5** a cut `Self.prompt` never splits a line: bytes already pending
  finish their line, which stays buffered for the next weir reader.
- **E3** the serve cut joins a cut handler's `always` whole before
  `within serve` returns (pfirst parity; no 2 s cap on the cleanup).
- **D5** an arm killed by the teardown is the teardown, never a
  `command failed with exit code 137`.
- **F2** per R3.
- **F3** the piped REPL's lookahead line returns to the stdin buffer, so
  a `Self.prompt` in the entry reads the next line.
- **D3** tree kills stop scanning every process on the box (track the
  tree / kill the group): teardown of n arms is O(n).

## Stage 3 — formats, plan (`fix/h3r3-formats`)

- **B1 (+F4b)** `to yaml` writes `Some []` as `[]` (and an empty
  mapping as `{}`), so it reads back `Some []`.
- **F4a** `to yaml` writes `seq<seq<T>>` / nested YSeq in a form
  `from yaml` reads (block-nested), round-tripping.
- **B2** gen types: boolean-or-string (and bool|number etc., oneOf
  bool/string) draft Yaml with a note, as number-or-string.
- **B3** gen types: a type array mixing a scalar with array/object
  drafts Yaml with a note — never a silent pick.
- **B4** per R2.
- **D1** the guard's link resolution is O(depth) per path (resolve
  once per prefix, cached per plan), not O(depth²).

## Order

Harness first (`tools/hunt3r3-repro.weir`, every probe REPRODUCES on
04da379). Stages 1, 2, 3 in parallel (disjoint files: Script/Contracts/
Http/Types · Session/Builtins/Proc/Repl/Serve · Eval yaml+plan/
SchemaTypes/Contracts gen); merge 1 → 3 → 2.
