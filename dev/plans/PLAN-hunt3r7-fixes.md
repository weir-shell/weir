# PLAN — fixing hunt 3 round 7 (H3R7-*)

Source: round 7, the targeted black-box check of round 6's new code and
#387. Findings and repros: `/output/tmp/h3r7-findings.md` (repros under
`/output/tmp/h3r7-<brief>/`). Target: main @ aea52bc
(`/output/tmp/weir-h3r7/Weir`).

2 high (T3-1, T1-2), 12 medium. Lows go to BACKLOG ('From hunt-3
round 7'). If this round's fixes gate green, hunt 3 closes.

## Gates (every stage)

As PLAN-hunt3r6-fixes, plus: `tools/hunt3r7f-repro.weir --bin <aot>` —
the stage's probes OK, nothing newly failing; `hunt3r6f-repro` 46/46,
`hunt3r5f` 50/50, `hunt3r4f` 30/30, `hunt3r4` 9/9, `hunt3r3` 22/22,
`hunt3r2` 25/25 stay green. Stage A also: every storm.yml step 0 bad / 0
known, and T1-2's pinned-contention shape at 0 misreads.

## Stage A — core (`fix/h3r7f-core`)

- **T1-2 (high)** a signal whose handler starts late (a .NET pool item
  under CPU contention) is never misread or lost: the settle decision
  must not depend on the managed handler having started — e.g. record
  the signal at delivery (a native/early flag or the runtime's own
  signal registration path), or settle on the child's own death signal
  when a teardown signal is pending at the OS level. Keep R6's 5 ms
  grace for the no-signal case.
- **T1-3** a `| complete` arm uses one reader thread again (both pipes
  on one thread, or the stderr reader cut-aware without its own
  thread), and a thread that cannot start (EAGAIN under a pid cap)
  raises a clear weir error naming the limit.
- **T1-1** the shim's exec check follows the interpreter chain to a
  native image or refuses, as execve would.

## Stage P — plans and completion (`fix/h3r7f-plan`)

- **T2-1** the simulation keeps a moved symlink a link at its new path.
- **T2-2** a write through a dangling link creates its target in the
  simulation.
- **T2-3** a write through a symlink loop refuses at capture (ELOOP).
- **T2-4** `$` completion offers nothing where `$` is literal: `//`
  comments, `<<<`/`$<<<` bodies, yaml quoted scalars, yaml comments,
  yaml `|` block scalars.

## Stage B — safety, sigs, formats (`fix/h3r7f-safety`)

- **T3-1 (high)** userinfo is masked through the LAST `@` of the
  authority (scheme or scheme-relative or scheme-less), in every
  display and message path, including the proxy guard; a password with
  raw `/ ? # @` never shows.
- **T3-2** `lock.json` and sig files are written atomically (temp +
  rename) under a lock, so a signal or a parallel `add` never tears or
  loses them.
- **T3-3, T3-4** the remaining message sinks escape data
  ([D:error-data-escape]).
- **T3-5** gen types drops an empty `""` key with a note, as `#infer`
  does.
- **T3-6** a malformed proxy variable refuses even when another proxy
  variable is set.

## Order

Harness first (`tools/hunt3r7f-repro.weir`, every probe REPRODUCES on
aea52bc). A, P and B in parallel (disjoint files). Merge B → P → A.
Lows to BACKLOG with the ledger at the end.
