# Backlog

(Moved from SEMANTICS.md at its retirement — planning residue never
belonged in a semantics document.)

0. ~~**Block effect-sequencing**~~ — done, and was already done when this
   note moved here from SEMANTICS.md: the sibling-sentinel ESeq spine
   ([D:seq-commit]/[D:sibling-sentinel]) sequences any unit expression
   mid-block, a non-unit line refuses with `[seq-unit]` ("bind it or
   print it" — the statement rule's discipline inside blocks, exactly as
   priced), and the fuzz invariant "block siblings and single-line ';'
   agree" pins it. Probe-confirmed 2026-08-28.
1. ~~**Measure algebra**~~ — superseded: **measures were removed
   entirely** (2026-07-18; see the tombstone section and the NOTES arc).
   The 2026-07-17 drop decision and the `no_unit_algebra` tripwire
   retired with them *and* the `*`/`/`-defaulting rule above.
(Done: backlog #1 and the exit-code policy — old #3 — landed as
`Seq.force` (né `collect`) and `complete`; see "Processes and the
session".)

(Done: comparison/boolean completeness — landed with `<>` inheriting `==`'s
equatability and short-circuit `&&`/`||`, as pre-committed.)

## From the fuzzer's hunt-2 invariants (2026-10-15, W6, [D:fuzz-hunt2-invariants])

Invariant 8 was red on deep runs (green on the pinned smoke and the
e2e `--seed 4242 --count 10` cell). Found over 6,500 generated
programs x 5 neighbors; two classes, nothing else (no crash, no
"internal error", no fmt idempotence break). Both closed 2026-10-16.

- ~~**check positions past the end of their line** (23 of 6,500)~~ —
  done: assembly recovery's re-indent of a misaligned line no longer
  moves any position; segments carry the source indent, so assembly,
  parse, check and LSP columns land on the line as written
  [D:recovery-physical-indent]. Minimal (was `4:12` on a 9-wide line):

      let xs = [ 1
                 2
                3
      print "a"

- ~~**fmt repairs a `doc-align` error** (14 of 6,500)~~ — ruled
  (user, 2026-10-16): the law narrows to "fmt never makes a checking
  file fail and never masks a non-layout error"; repairing a pure
  layout error is fmt's job [D:fmt-repairs-layout].

## From the second bug hunt (2026-10-14, PLAN-hunt2-fixes.md)

Ruled 2026-10-15 (PLAN-daily-driver W4): **every item below is daily-driver — fix all.**

- ~~**`from json` reading a `Yaml` field**~~ — done: `Yaml` crosses json
  both ways and `gen types` drafts a cyclic field as `Yaml`
  [D:json-yaml-field].
- **`serve` answers only the first of pipelined requests** on one
  connection. Ruled LATER (user, 2026-10-16): the managed HttpListener
  drops the pipelined bytes itself; the fix is a Kestrel move (also fixes
  F10 and retires the F8 `_trailer_sent` stopgap) — parked as
  dev/plans/PLAN-kestrel.md.
- ~~**Inference order:**~~ — done: `+` over two unknowns defers to any
  later anchor in the statement [D:plus-defer].
- ~~**Re-enumeration warning** misses indirect pulls (through a function, a
  loop, a derived binding).~~ — done: carriers replay their pulls, a
  loop read counts twice [D:reenum-indirect].
- ~~**User generic types at the wire**~~ — done: `from json B<int>` /
  `seq<B<int>>` and instances inside records cross json/jsonl/yaml;
  SKILL documents user generics [D:wire-generics].
- ~~**Runtime error positions** are statement-granular inside a one-line
  `if … then fail … else …`.~~ — done: a raising call reports its
  callee's column [D:runtime-position-call].
- ~~**LSP re-analyzes a large file per hover** (20k lines ≈ 3s).~~ — done:
  one analysis per (file, text), dropped on any document event
  [D:lsp-analysis-cache].
- ~~**A Secret spliced into argv shows in clear in "command failed" errors**
  (SECURITY.md non-claim; CI logs make it worth revisiting).~~ — done:
  argv-rendering messages mask it as `***` [D:secret-argv-mask].
- ~~**`from xml` mixed content** (`<N>a<b/>c</N>`) is silently concatenated.~~
  — done: a text field over mixed content refuses [D:from-xml-mixed].
- ~~**fmt** formats a file with a yaml-district parse error, and spaces inside
  multi-line interpolation holes.~~ done: fmt refuses any file check cannot
  parse; respace skips district content and lines inside a hole [D:fmt-parse-gate].
- ~~**History file and fmt rewrite invalid UTF-8** as U+FFFD ([D:source-utf8]
  covers source files only).~~ — done: fmt refuses located and leaves the
  file untouched; history skips an invalid line from recall, writes it back
  byte-for-byte on every rewrite and says so once [D:history-utf8].
