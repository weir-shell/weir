# PLAN: receipts — `Completed.ok` and read-only receipt records

**Status: BUILT (2026-10-26, branch feat/receipts) — user-ruled 2026-10-26: Completed.ok and read-only receipts**

## The receipt

`let r = cmd | complete` then `if r.exitCode == 0 then …` restates the
law `| succeeds` already names. The user ruled a derived field, `ok`,
and with it closed the write side: a `Completed` a script writes by
hand asserts a run that never happened, and once a field is derived a
literal can contradict itself (`ok = true; exitCode = 3`). Refusing the
literal and the update closes both and lets receipts grow derived
fields later without breaking anyone.

## Anchors (verified)

- `Builtins.completedDef` — the record def; `recordOf` is the key-checked
  funnel [D:record-keys]. Three construction sites (`completedWith`,
  `completedWithIn`, `chainCompletedImpl`) collapse into `completedOf`.
  The `complete` and `Completed` docs sit in `builtinDocs`.
- `Check.fs` — a named literal `Completed { … }` is `namedRecord`
  (`EApp(EVar ty, ERecord)`); a bare literal is the `ERecord` arm's
  exact-field-set candidate search; `{ r with … }` is `EUpdate`'s
  `updOne`, which resolves the source to `TNamed` per hop.
- `Eval.fs` `TEUpdate` — every update passes here, including a row-typed
  updater whose source turns out to be a receipt at the call.
- Compile order: Types → … → Check → … → Eval → Builtins. The receipt
  table lives in `Types.fs` so Check and Eval both read it.

## Shape

- `Completed = { ok; exitCode; stdout; stderr }`, `ok` first
  ([D:record-order], the `PromptStatus.ok` precedent), set at
  `completedOf` as `code = 0`.
- `Types.receipts : Map<name, Receipt>` — derived fields, provenance
  sentence, what the record reports, a ground-typed anonymous example;
  `receiptLiteralMsg` / `receiptUpdateMsg` build the two messages.
- Check: `namedRecord` refuses a receipt name; the bare literal's
  candidate set excludes receipts (a same-shaped user record wins; the
  `HttpResponse`/`HttpServerResponse` ambiguity dissolves) and a literal
  of a receipt's shape minus its derived fields gets the teaching;
  `updOne` refuses a receipt-typed source at any hop.
- Eval: `TEUpdate` refuses a receipt value with the update message —
  the row-typed updater's late refusal.

## The two messages

Literal (Completed):

    a Completed is what '| complete' returns — run a command to get one (sh -c "exit 3" | complete); a function over its fields takes any record with them, so a test can pass {| exitCode = 3 |}

Update (Completed):

    a Completed is read-only — it reports a run that happened (ok follows exitCode), so no field changes after the fact; to carry different values build a record of your own, {| exitCode = 3 |}

FileRow and HttpResponse read the same two templates with their own
provenance (`ls`/`Dir.stat`/`File.stat`; `Http.send`) and examples.

Deviation from the ruling's text: the example is `{| exitCode = 3 |}`,
not `{| exitCode = 3; stdout = []; stderr = [] |}` — an anonymous
literal is ground-typed [D:anon-literals] and `[]` is refused at the
literal. Verified on v0.0.78 before the change; pinned (the refusal of
the `[]` spelling is part of the pin, so the example cannot drift back).

## Pins

- unit `receipts`: ok over exit 0 / 3 / grep's 1; show, REPL echo and
  `to json` lead with ok; every receipt name is a prelude record; six
  literal refusals (bare with/without `ok`, named; FileRow bare/named;
  HttpResponse named) and four update refusals (three types + a nested
  path) byte-equal with carets; a same-shaped user record wins; the
  row-typed function over `{| exitCode = 3 |}` and a real Completed; the
  row-typed updater raising at run time.
- surface-gate [D:surface-gate]: the inventory line gains `ok`.
- e2e: `ok == (exitCode == 0)` over 0/1/3/130 on the AOT binary; `to
  json` carries ok; the REPL echo of a bound Completed shows `ok = true`.
- skill-doc: a `weir` block with `if r.ok then`, a `weir-error` block
  with the bare literal (SKILL.md and docs/reference/commands.md).

## The sweep

A grep for receipt literals (`{ … exitCode = … }`, `stdout = [`,
`kind = Regular`, `status = …; headers`) found none in tests, docs,
skills or examples. The ten `exitCode =` hits are
`let codeOf { exitCode = c } = c` in tools/*-repro.weir — record
PATTERNS in row-typed functions, exactly the escape the message names;
they stay. The two FileRow strings in Tests.fs (`show` pins) are
expected output, not literals.

## Risks

- Breaking for a script that wrote a `Completed` literal (none in the
  repo); the CHANGELOG says so.
- Field order: `ok` first changes `show`/echo/json of every Completed.
  No pin printed a whole Completed before this; the new ones do.
- The run-time refusal (row-typed updater) is the one receipt refusal
  the checker cannot see; it is pinned so the gap stays named.

## Non-goals

- No `ok` on `HttpResponse` — status is data [D:http].
- No new receipt types; the table is three names.
- `from json Completed` still decodes — a receipt `to json` wrote reads
  back; the ruling names the literal and the update.
- No change to `| succeeds`, `| exitCode`, or the discard-family
  messages.
