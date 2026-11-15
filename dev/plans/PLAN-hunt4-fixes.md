# PLAN — fixing hunt 4 (H4-*)

Source: hunt 4 (2026-11-14), five briefs against main @ 4725def: parser /
checker / fmt, wire formats, builtins, processes and scopes, CLI and
tooling. 7 high, 12 medium, the rest low. Two more items came from the
user at the prompt while the fixes were under way (U1, U2). Lows go to
BACKLOG ('From hunt 4'). One branch, `fix/hunt4`, a commit per fix.

## Gates

The unit suite in Debug, `fantomas --check`, `ci/e2e.sh`,
`ci/skill-doc.sh`, `ci/decision-keys.weir`, the earlier hunts' repro
harnesses unchanged, and `tools/hunt4-repro.weir --bin <aot>`: every
probe OK, every control ok. The harness reproduces 18 of 18 on 4725def.

## High

- **S1** `readonly` admitted `File.writeBytes` (missing from
  `Effects.fsWriteMembers`).
- **S2** `check --can` reported "nothing" for `File.delete`/`move`/
  `writeBytes`, `cd`, `ls` and a dozen read members: its two hand-kept
  sets had drifted from the partition. They are gone; `Can` asks
  `Effects.effectClass`, and a unit test places every `File`/`Dir` member.
- **S3** `count-1` was a command to check and a subtraction to run
  ([D:dash-binding]).
- **S4** `| None when … ->` was a parse error.
- **S5** in an arm a lazy command's first line waited for a fourth byte.
- **S6** a `retry`/`poll`/`repeat` body refused `let r = cmd | complete`.
- **S7** in an arm a value-headed pipe drained its producer before the
  consumer read.

## Medium

- **M1** `<` on a generic `[<Ord n>]` type skipped the instance check.
- **M2** a name starting `module` could not head a statement that the
  grammar parser took; `type-check`-style command names read as keywords.
- **M3** `within serve` inside `plan` ran handler writes for real: now
  refused, as the fan-outs are ([D:plan-parallel-refusal]).
- **M4** `let out = xs | cmd` in a block was a raw parse error.
- **M5** `Table.render` refused same-typed rows in different field order;
  a newline cell broke its row; cells clipped when piped.
- **M6** astral code points in regex class ranges; found on the way: a
  class holding U+FFFD or spanning the surrogate block matched every
  astral character (the probe's lone-surrogate literal compiled as U+FFFD).
- **M7** `inferShape`: the top name went to a nested object; a name
  handed out twice; a repeated key drafted twice; null array elements
  dropped without `Option`.
- **M8** `Yaml.merge` was not idempotent for an item tombstone over
  duplicates (and two `by=` corners).
- **M9** `check --can` printed a literal's control characters raw; a
  reified command's env overlay was missing from the report.
- **M10** a redirect rewrote PUT/DELETE/PATCH to GET on 301/302.
- **M11** a byte-order mark split across pipe reads was not recognised
  on the blocking read paths.

## From the user, at the prompt

- **U1** `Seq.initInfinite id |> Seq.freeze` at the REPL ignored ^C and
  needed `kill -9` ([D:repl-interrupt-compute]).
- **U2** an entry edited while browsing history lost the edit when the
  cursor left it and came back ([D:repl-history-edits]).

## Rulings

Recorded in DECISIONS: [D:dash-binding], [D:repl-interrupt-compute],
[D:repl-history-edits]. Without a row of their own, as they extend an
existing one: `within serve` joins [D:plan-parallel-refusal]'s refusals;
a keyword glued to a dash is a command word; `Yaml.merge`'s item
tombstone removes every match ([D:yaml-nodes]' idempotence law); only a
POST becomes a GET on 301/302 ([D:secret-redirect] follows the BCL).
