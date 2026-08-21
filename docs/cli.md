# The CLI

One binary, a handful of subcommands. The block below is byte-pinned
against `weir --help` in CI — if the binary's surface moves, this
page fails the build until it moves too.

<!-- cli-usage-pin: ci/e2e.sh diffs this fence against `weir --help` -->
```text
usage: weir                                    the REPL
       weir <script> [args...]                 run a script
       weir -e <program>                       evaluate a program; the result is its last expression
       weir check [--json] <script>            diagnostics only (no evaluation)
       weir check --can [--strict] [--json] <script>  the static capability report
       weir fmt [--check] <script>             canonical formatter
       weir lsp                                language server (stdio)
       weir add sig <tool>                     generate a command signature from the installed binary
       weir add schema <url> --as <name>       fetch an external contract, lock it
       weir restore                            re-materialize the lock's artifacts
       weir verify                             vendored contracts vs the lock
       weir --version                          the build stamp
```

## Running

- `weir` — the [REPL](repl.md). Bare names work there; scripts are
  strict.
- `weir script.weir args...` — run a script; `#!/usr/bin/env weir`
  works. The script's argv is `Self.args`, or typed via `Args.load`.
- `weir -e '<program>'` — evaluate a program whose LAST statement is
  an expression; newlines are statement boundaries exactly as in a
  file. A lone declaration is refused with its kind named.

## Checking

- `weir check script.weir` — every diagnostic, located and coded,
  with no evaluation. Commands missing from PATH are warnings here
  (the runner treats them as errors), so scripts for uninstalled
  tools stay editable. `--json` emits the same diagnostics for tools
  and agent loops.
- `weir check --can script.weir` — the static capability report:
  what the script can run, read, write, and reach, with a
  `file:line` for every claim. It reports capability, never
  behaviour — an untaken branch still counts. `--strict` exits 2
  when any opaque site exists (`sh -c` and the other interpreters),
  so a CI gate can refuse unanalysable scripts; `--json` for
  machines.
- `weir fmt script.weir` — the canonical formatter, in place;
  `--check` exits nonzero instead of writing, for CI.

## The project subcommands

These maintain the [`.weir/` tree](project.md):

- `weir add sig <tool>` — probe the installed binary and write
  `.weir/sigs/<tool>.weir` plus a lock entry
  ([signatures](signatures.md)).
- `weir add schema <url> --as <name>` — fetch a JSON schema into
  `.weir/schemas/`, lock it ([schemas](schemas.md)).
- `weir restore` — re-materialize everything the lock records,
  hash-verified. The one subcommand that fetches.
- `weir verify` — compare the vendored contracts against the lock:
  absent or modified artifacts are findings, exit 1.

## Conventions

- `weir --help` prints usage on stdout and exits 0 when asked for;
  any unrecognized invocation prints the same usage on stderr and
  exits 2.
- An unknown option gets a did-you-mean against the real spellings.
- `weir lsp` speaks JSON-RPC over stdio; conventional client argv
  like `--stdio` is tolerated ([editors](editors.md)).
- `weir --version` prints `<tag>+<hash>` — the exact build, not a
  marketing number.
