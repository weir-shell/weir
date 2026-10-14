# PLAN — fixing the second bug hunt (H1–H57)

Status: DRAFT, awaiting bless.

Source: a five-area black-box hunt (regressions in v0.0.73, REPL/LSP/CLI,
modules/contracts/HTTP, docs-vs-binary, crash fuzzing) against main @ 16cc7c6.
Every item reproduced at least twice; the ones marked ✔ were also re-run by the
coordinator. Most v0.0.73 changes held: code-point strings, the iterative Seq
chain, EPIPE/`always`, fmt idempotence over 187 corpus files, YAML/JSON/XML,
the checker tightenings, LSP positions with astral text (64,800 fuzzed
requests), ~7,700 mutated corpus scripts — no crash among them.

Three stages, stacked branches, each green on its own. Gates per stage: unit,
fidelity, skill-doc, fuzz ×3, e2e, e2e with the macOS `timeout` polyfill
forced, and an acceptance harness `tools/hunt2-repro.weir` (one probe per item,
written first, REPRODUCES at 16cc7c6) — the sweep-repro pattern. Heavy agents
run at most two at a time (the OOM lesson).

## Rulings owed BEFORE implementation (recommendation first)

- **R1 — module commands at import (H4).** The check-time walk cannot see
  through lambdas, values and composition. RECOMMEND: a runtime backstop — any
  command spawned while a module body is being evaluated raises, located, like
  `plan`'s runtime guard; the check-time refusal stays for the direct forms.
- **R2 — `serve` request body limit (H20).** RECOMMEND: a `maxBody` field on
  the serve config (a `Size`), default 16 MiB, answered 413 past it.
- **R3 — builtin-named module members (H6).** RECOMMEND: export them; a member
  is always reachable qualified (`Doc.Text`). The bare name stays the builtin.
- **R4 — the same module through a symlink (H58 minor).** RECOMMEND: module
  identity = the real path, so a symlinked import is the same module.
- **R5 — invalid UTF-8 in a source file (H47).** RECOMMEND: a located error
  (line:col of the first bad byte), never a silent U+FFFD.
- **R6 — `Uuid.parse " …"` (H50).** RECOMMEND: refuse surrounding whitespace,
  as the doc says.
- **R7 — blocks of 500+ statements (H48).** RECOMMEND: statements in a block
  are width, not depth — they never count toward the 500 nesting limit (made
  safe by H2's iterative runner).

## Stage 1 — safety and crashes (`fix/hunt2-safety`)

- **H1 ✔ `into` runs `sh -c` past every effect check** (pure, readonly, plan,
  module let, `--can --strict` says "nothing"). RULED: delete `into` outright —
  builtin, reserved-name entry, the e2e cell moves to `xs | sh -c "…"`;
  DECISIONS row. `Builtins.fs:405`, `:7459`.
- **H2 ✔ stack overflow on a long flat script** — 6000 `print` lines pass
  `check`, run dies rc 134 (700 under a 1 MB stack). `Script.fs:7232`
  `let rec exec` recurses per statement inside `try … with`. Make the runner a
  loop. e2e at 20k statements under `ulimit -s 1024`.
- **H3 ✔ a module body runs once per import site** — three different
  `Uuid.v4 ()` for one module; diamonds blow up exponentially (14 levels 15s,
  18 > 120s). `Script.fs:4730` `replayModule`: evaluate once per program,
  cached by module identity (R4).
- **H4 module `let` runs a command at import through a value/lambda**
  (`[()] |> Seq.map helper`, `(fun () -> helper ()) ()`, `helper >> …`,
  `Seq.collect helper`, a record field, `Seq.map (fun _ -> $(sh …))`). Per R1.
- **H5 `weir restore` can overwrite `lock.json`** — an entry with
  `"path":"lock.json"`. Confinement excludes the lockfile.
- **H6 ✔ builtin-named module members silently not exported** (`Doc.Text`,
  `let id`). `Script.fs:4649-4651`. Per R3.
- **H7 a `Secret` argument is refused whenever the command is reified**
  (`| complete/line/text/succeeds/exitCode/orFail/or`, `$e(…)`, `NAME=$s cmd`,
  `within env A=$s`) — the documented `curl -H $auth … | complete` fails.
- **H8 SIGTERM intermittently orphans a `pmap` worker's grandchild** (1/8 to
  3/5 runs). Race between the handler's kill loop over `liveProcs`
  (`Session.fs:586`) and the main-thread unwind / pmap cancellation; the kill
  must cover the tree of every live child exactly once, after spawning stops.
- **H9 ✔ `HOME= weir` crashes the REPL** (TypeInitializationException;
  module-level `HistoryPath` in `Repl.fs:588`). Lazy, and an unusable home
  disables history with a note.
- **H10 ✔ `#save` writes `let _(a, b) = …`** — the saved script fails.
  `protectUnusedLet` (`Repl.fs:3052`) must not prefix a pattern.
- **H11 unreadable entry script → internal UnauthorizedAccessException** in
  run/check/`--can`/`--json`/fmt; fmt on a read-only file the same. Route the
  entry read through the import path's guard (`readImportSource`); a directory
  argument names itself.
- **H12 malformed lock entries crash restore/verify** (short sha →
  `Substring`, null path/url → NRE, missing key unnamed, `""` path wrong
  message). `Contracts.fs:1275,1445`, `Program.fs:547`.
- **H13 `weir restore` omits the host token** — private modules 404.
  `Contracts.fs:1268`.

## Stage 2 — wrong behavior (`fix/hunt2-behavior`)

HTTP / serve
- **H14 `Http.header`/`headerAll` join repeated headers with ","** and rewrite
  single values (`BaseHTTP/0.6 Python/3.14.4` → `…,…`). `Http.fs:291`.
- **H15 unknown response charset reported as "cannot reach host"**
  (`Http.fs:299`) — decode as bytes→UTF-8 fallback or a precise error.
- **H16 serve sends a body on 204 and HEAD**, breaking a pipelined connection.
  `Serve.fs:267`.
- **H17 serve passes a handler's `Transfer-Encoding` next to
  `Content-Length`** — strip hop-by-hop/framing headers. `Serve.fs:271-279`.
- **H18 a `Stream` element with newlines forges SSE events** — split each
  element into one `data:` line per line.
- **H19 `gen types` from a self-referential schema emits `Option<Yaml>`**,
  which `from json` refuses.
- **H20 serve has no request body limit** (300 MB → 1.25 GB RSS). Per R2.

Modules / checker
- **H21 a transitively reachable union makes a local constructor
  "ambiguous"** (`Check.fs:1968` `ctorOwnersAll` ignores `TypeOrigins`).
- **H22 multi-line copy-and-update aligned under the first field is
  rejected** (anchors on the source column; spurious "{ is still open"; no
  column) — implement [D:field-alignment]'s "update headers anchor on their
  first field"; fix SKILL l.2330 "Braces ignore indentation".
- **H23 imported union cases can't type an unannotated parameter** —
  contradicts [D:match-scrutinee-infer].

REPL / LSP / fmt
- **H24 REPL editor splits surrogate pairs** (Left/Right/Backspace by UTF-16
  unit, `Repl.fs:1355-1408`) — move by code point.
- **H25 path completion inserts unquoted/unescaped paths** (`Complete.fs:823`).
- **H26 piped REPL: an incomplete line swallows the rest of stdin**
  (`Repl.fs:1631`) — a blank line ends a pending entry, as at the tty.
- **H27 Enter typed before the REPL is ready becomes a newline**
  (`Repl.fs:346`).
- **H28 fmt vs LSP formatting and the shebang:** error line off by one
  (`Fmt.fs:707`); LSP trims the shebang, indents where the CLI refuses, and
  omits the final newline (`Lsp.fs:2504`). One formatter entry point for both.
- **H29 LSP protocol:** a throwing handler sends no response (`Lsp.fs:2965`);
  unknown method → -32601; invalid JSON → -32700; `exit` without `shutdown`
  exits 1; requests after `shutdown` → InvalidRequest; header names are
  case-insensitive.
- **H30 fmt flip-flops forever** on `match {((}` / `| _ -> 1`.
- **H31 fmt changes whether a file checks** (trailing U+3000, U+2028, a
  second BOM): trim only what the parser treats as whitespace.
- **H32 run time quadratic in a statement's physical lines** (64k-line
  heredoc 53s vs check 0.7s): `Script.fs:2428` `translate` scans segments per
  node — index once.
- **H33 a pmap/retry/regex leftovers:** `Path.glob` bracket bodies go raw into
  .NET regex (`[z-a]`, `[]`, `[\w]`; `Builtins.fs` ~826) — translate bracket
  classes properly.
- **H34 no field completion on anonymous records** (`Complete.fs:105`).

## Stage 3 — messages and small inconsistencies (`fix/hunt2-messages`)

- **H35** `weir -e ''` / `-e '// c'` print a bare `^` (`Program.fs:34`).
- **H36** `#quit now` → "did you mean '#quit'" (`Repl.fs:3268`).
- **H37** a destructuring `let` echoes without values (`Repl.fs:1941`).
- **H38** error column past the end of a joined continuation line.
- **H39** a timed-out `retry` reports configured, not actual, attempts
  (`Eval.fs:3390`).
- **H40** a lone surrogate escape in JSON leaks .NET's message.
- **H41** `from json` type errors say `got True`/`got String` (JsonValueKind
  names) — weir type words.
- **H42** comma between record-literal fields gives misleading errors (teach as
  type declarations do).
- **H43** `show` doubles the parens around tuple payloads (`Some ((1, 2))`,
  Plan ops).
- **H44** `Path.tempRoot` called "the pure query" in SKILL but `pure` refuses
  it — fix the doc.
- **H45** `sh -c "exit 3" |> try` → raw "Expecting: …".
- **H46** `| _ | None` "unreachable" message points at the wrong arm.
- **H47** invalid UTF-8 source silently becomes U+FFFD. Per R5.
- **H48** a block of 500+ statements gets the "long chain" depth message. Per R7.
- **H49** `poll interval=0s` busy-loops — refuse it as `timeout=0s` is.
- **H50** `Uuid.parse` accepts surrounding whitespace. Per R6.
- **H51** float-field record `==` says "sequences and functions cannot be
  compared" — name `Float.near`.
- **H52** union-only `Args.load`: `build --help` prints the full usage.
- **H53** single-case union destructuring (`let un (W x) = x`) rejected as
  refutable.
- **H54** contracts/imports raw .NET text: importing a directory or
  `/dev/zero`; `within lock` on a directory or `""`; `timeout=-1s` accepted.
- **H55** HTTP: Basic auth + a plain `Authorization` header merge into one
  invalid header; a plain `Cookie` header follows a cross-origin redirect; a
  redirect loop returns 302 as data.
- **H56** contracts: `as Some` accepted as an alias; `.weir/modules` silently
  shadows `.weir/types`; the restore mismatch message says "weir add schema"
  for modules.
- **H57** REPL: `#alias let = …` accepted; piped `#history` says `(empty)`;
  the `repl-it` DECISIONS row is stale (a `let` does not bind `it`);
  `#help map` vs `#help Seq.map`; `#infer` note spells `'DE'` for `dE`.

## Not in scope — backlog candidates (one line each in dev/BACKLOG.md)

`a + b + 0` doesn't infer while `0 + a + b` does (inference order); the
re-enumeration warning misses indirect pulls; generic records refused at the
json/yaml boundary and `from json seq<B<int>>` a parse error (user generics
are undocumented — a feature question); runtime error positions are
statement-granular inside a one-line `if`; LSP re-analyzes a 20k-line file per
hover (~3s); a Secret spliced into argv appears in clear in "command failed"
errors (SECURITY.md non-claim — revisit); `from xml` mixed content silently
concatenated; fmt formats a file with a yaml-district parse error and spaces
inside multi-line interpolation holes; history/fmt rewrite invalid UTF-8 (R5
covers source files only).
