# PLAN — fixing hunt 3 round 5 (H3R5-*)

Source: round 5, the black-box exit check of round 4. Findings, repros,
rulings: `/output/tmp/h3r5-findings.md` (repros under
`/output/tmp/h3r5-<area>/`). Target: main @ e526e58
(`/output/tmp/weir-h3r5/Weir`).

6 high (E1, E2, C1-1, C1-2, C1-3, B1), 51 medium. Lows go to BACKLOG
('From hunt-3 round 5').

## Gates (every stage)

As PLAN-hunt3r4-fixes, plus: `tools/hunt3r5f-repro.weir --bin <aot>` —
the stage's probes OK, nothing newly failing; `hunt3r4f-repro` 30/30,
`hunt3r4-repro` 9/9, `hunt3r3-repro` 22/22, `hunt3r2-repro` 25/25 stay
green. Stage A also: `ci/storm` (single, several-signal, `--tty`) and the
race-tree fuzzer at zero failures, plus C2's REPL storm on R6. Stage S
also: `tools/sig-score.weir` loses no live line.

## Rulings (user 2026-10-19, all as recommended)

- **R1 (round-4 R8 revised; C1-2, C1-3 orphan, E3/C1-6, C1-7)** cleanup commands
  stay in weir's foreground group with the terminal and their real stdin,
  started with SIGINT/SIGQUIT ignored (inherited SIG_IGN): a ^C cannot
  kill them, programs that handle ^C themselves still can; pagers work, no
  orphans. The shim stays only to set SIG_IGN, exec'd via /proc/self/exe
  on Linux; a direct spawn when there's no tty.
- **R2 (C1-3)** SIGQUIT runs the TERM teardown and dies by SIGQUIT;
  USR1/USR2/ALRM are a documented non-claim beside kill -9.
- **R3 (B1)** YAML stays 1.2, but a Yaml node keeps a leading-zero
  number's original text and writes it back unchanged; a typed int read
  of a leading-zero literal refuses, teaching it is octal to YAML 1.1
  readers (quote it or use 0o644).
- **R4 (F3)** pure-ASCII XML reads under any ASCII-compatible declared
  encoding; utf8/UTF8/Latin-1 aliases accepted; real non-ASCII bytes under
  an undecodable label refuse, the hint also says to change the
  declaration. No code pages.
- **R5 (E7)** weir-rendered displays (Plan.preview's send line, `show` of
  a request) mask URL userinfo as `http://***@host`; the value is
  unchanged and still sent.
- **R6 (C21)** the REPL entry's node is checked between statements: a ^C
  during a cleanup, pure compute or a `| exitCode` child ends the entry
  with ↳ exit 130.
- **R7 (C1-7, amends R1)** the shim is `/bin/sh -c 'trap "" INT QUIT;
  exec "$@"'` (~1 ms, no weir path, so E3/C1-6 go too), not a weir
  self-exec.
- **R8 (C1-9)** docs: weir keeps an inherited SIG_IGN SIGTERM, as shells
  do (I15 gains the carve-out); round 4's SIGINT reset stays.
- **R9** pid-cgroup exhaustion ("Failed to create the thread pool", then
  deaf to SIGTERM; base same): BACKLOG low.

- **R10 (D-2)** a group-directed TERM/HUP that kills a cleanup's command
  is the signal's, not a failure: no message, the cleanup's remaining
  steps run whole (I6/I7), then weir dies by the signal.

## Stage A — signals, terminal, scopes (`fix/h3r5f-core`)

Session/Proc/Repl/console. The R1/R7 shim and the console-write policy
(E1, C1-1) get a short ledger note; no review gate (user).

- **R1**: **C1-2 (high)** pager in a cleanup; C1-3's orphan; **E3/C1-6**
  upgrade/delete mid-run; **C1-7** per R7.
- **R2**: **C1-3 (high)** SIGQUIT teardown, no tmp leak, no core.
- **C1-1 (high)** tty REPL hangup mid-command dies by SIGHUP, never
  `internal error: IOException`.
- **E1 (high)** an unwritable stderr (`2>/dev/full`, `2>&-`) never
  aborts: final error/Log writes tolerate it, exit code kept.
- **C1-4** the tty REPL restores the terminal on TERM/HUP at its prompt.
- **C1-5** a group-directed SIGHUP settles like INT (no spurious
  "command failed … 129").
- **C1-8** a backstop-adopted flow prints "cleanup ALSO failed".
- **C1-9** per R8.
- **R6 (C21)**.
- **C22** FIFO file ops other than reads (sha256, copy, write, append,
  writeAtomic, writeBytes) are cut by race wins, fan-out exits and the
  first signal.
- **D1** `Proc.wait` waits on the exit and the cut together (no 50 ms
  floor).

## Stage S — command signatures (`fix/h3r5f-sigs`)

Sig generator and probe (Contracts/Script sig paths).

- **E2 (high)** probes are bounded in time and kill their process group
  (a daemon holding stdout cannot hang `add sig`).
- **E11** probe output bounded (bytes), with a note when cut.
- **E9** probes run in a fresh temp dir, as [D:sig-version-probe] says.
- **E10** sig file names are injective (no silent overwrite).
- **S1 (regression)** an old all-bool signature checks exactly as before
  (no letter-by-letter bundles).
- **S2** wrappers/interpreters detected as stopAtOperand (`COMMAND...`,
  `<command> [<argument>...]`, `script-file`, fd -x/-X).
- **S3** kill's `-<signal>, -s, --signal` row; **S4** perl attached
  values (-M -I -F); **S5** two-column help (unzip); **S6 (regression)**
  shfmt `-w,     --write` and typed `-i, --indent uint`; **S7
  (regression)** Go single-dash flags seen only in examples (mkcert);
  **S8** npx as an open tool; **S9** node's positive form of `--no-x`;
  **S10** git builtins with their own subcommands.

## Stage C — formats (`fix/h3r5f-formats`)

Yaml/Xml/SchemaTypes.

- **R3**: **B1 (high)**; **B3** non-decimal YAML numbers keep their text
  (1.2 core forms read as numbers; 1.1-only forms stay strings) — no type
  change on read-modify-write.
- **B2** `to yaml` exponent floats carry a dot (`1.0e-05`).
- **B4** tabs: tab-indentation refuses; `\t#` starts a comment.
- **B5** required + nullable drafts with a note (or a nullable wrapper)
  so null writes back.
- **B6** the int-beyond-int64 hint points at Yaml, not float; uint64
  drafts lossless.
- **B7** whitespace-only XML text kept (or documented — choose with
  xml.etree).
- **R4**: **F3 (regression)**; **E13** a malformed encoding label refuses.
- **F4** `|> try` / `try result` keep bigint digits.
- **F5** the enum note lists null only when the enum has it and the type
  allows it.
- **F8** a block-scalar line starting with `%` is content; **F9** U+2028
  escaped out of block scalars; **F10** plain scalars trim only
  space/tab.

## Stage P — plans and paths (`fix/h3r5f-plan`)

Plan simulation, File/Dir/Path.

- **F1 (regression)** Dir.copy in a plan sees simulated sources and
  parents; no raw .NET text.
- **E12** a captured copy into a missing directory raises at capture,
  naming the destination.
- **F6** the simulation follows symlinks (aliases, deleted targets).
- **F7** Dir.move into its own subtree refuses at capture and direct, as
  Dir.copy does.
- **F2 (regression)** Path.glob resolves `..` physically, like File.*/
  Dir.* ([D:fs-dotdot-physical]).

## Stage B — safety (`fix/h3r5f-safety`)

Http/Args/`weir add`.

- **E4** Http.expect's body snippet escaped when stderr is piped.
- **E5** header-name CR branch and Args.load bad values escape raw CR.
- **E6** `weir add schema` stores the URL without userinfo in the lock.
- **R5 (E7)**.
- **E8** NO_PROXY matches non-ASCII hosts by punycode, as curl does.

## Stage D — storm gaps

Extend `ci/storm` and storm.yml with the paths C1 found it can't reach:
^\, group-directed TERM/HUP, tty hangup, the REPL (C1's ptysig.py/grp.py,
C2's repl_storm.py), cleanup commands reading the tty. Gating once A lands.

## Stage E — what the widened storm found (`fix/h3r5f-storm-found`)

Stage D's new modes found three open in the merged branch, counted as
`known` until fixed; E fixes them and deletes the exemptions so the
nightly gates in full.

- **D-1** a REPL ^C during a command inside a `within` body ends the
  entry with `↳ exit 130`, never "command failed … 130".
- **R10 (D-2)**.
- **D-3** a second signal while a cleanup's pager holds the terminal raw
  leaves it restored (I12; the rest of C1-2).

## Order

Harness first (`tools/hunt3r5f-repro.weir`, every probe REPRODUCES on
e526e58). Wave 1: A, S, C in parallel (disjoint files; A runs pty work,
keep the others' parallelism modest). Wave 2: P and B. D after A. Merge
B → P → C → S → A → D. Lows to BACKLOG at the end with the ledger.
