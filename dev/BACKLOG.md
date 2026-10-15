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

## From the second bug hunt (2026-10-14, PLAN-hunt2-fixes.md)

Ruled 2026-10-15 (PLAN-daily-driver W4): **every item below is daily-driver — fix all.**

- **`from json` reading a `Yaml` field** — would let `gen types` keep a
  self-referential field instead of omitting it [D:gen-types-cycles].
- **`serve` answers only the first of pipelined requests** on one
  connection.
- **Inference order:** `a + b + 0` doesn't infer while `0 + a + b` does.
- **Re-enumeration warning** misses indirect pulls (through a function, a
  loop, a derived binding).
- **User generic types at the wire:** generic records are refused at the
  json/yaml boundary and `from json seq<B<int>>` is a parse error; user
  generics are undocumented in SKILL.md.
- **Runtime error positions** are statement-granular inside a one-line
  `if … then fail … else …`.
- **LSP re-analyzes a large file per hover** (20k lines ≈ 3s).
- **A Secret spliced into argv shows in clear in "command failed" errors**
  (SECURITY.md non-claim; CI logs make it worth revisiting).
- **`from xml` mixed content** (`<N>a<b/>c</N>`) is silently concatenated.
- ~~**fmt** formats a file with a yaml-district parse error, and spaces inside
  multi-line interpolation holes.~~ done: fmt refuses any file check cannot
  parse; respace skips district content and lines inside a hole [D:fmt-parse-gate].
- **History file and fmt rewrite invalid UTF-8** as U+FFFD ([D:source-utf8]
  covers source files only). fmt half done: fmt reads through the entry
  read, refuses located and leaves the file untouched (e2e pins the bytes);
  the history half awaits a ruling (PLAN-hunt2-backlog-fmt.md).
