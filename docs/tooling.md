# Tooling

Reference is the language. Tooling is everything around it — this
page is for someone setting up a project, a pipeline, or an editor,
rather than someone writing a line of weir. Its two manuals sit
alongside: [Editors](editors.md) and [The REPL](repl.md).

## The CLI

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
       weir add module <src>//<file>@<ref> --as <name>  vendor a remote module, lock it
       weir restore                            re-materialize the lock's artifacts
       weir verify                             vendored contracts vs the lock
       weir --version                          the build stamp
```

### Running

- `weir` — the [REPL](repl.md). Bare names work there; scripts are
  strict.
- `weir script.weir args...` — run a script; `#!/usr/bin/env weir`
  works. The script's argv is `Self.args`, or typed via `Args.load`.
- `weir -e '<program>'` — evaluate a program whose LAST statement is
  an expression; newlines are statement boundaries exactly as in a
  file. A lone declaration is refused with its kind named.

### Checking

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

### The project subcommands

These maintain the [`.weir/` tree](#project-layout-weir):

- `weir add sig <tool>` — probe the installed binary and write
  `.weir/sigs/<tool>.weir` plus a lock entry
  ([signatures](#command-signatures)).
- `weir add schema <url> --as <name>` — fetch a JSON schema into
  `.weir/schemas/`, lock it ([schemas](#yaml-schemas)).
- `weir add module <src>//<file>@<ref> --as <name>` — vendor a remote
  module into `.weir/modules/`, lock it ([modules](#remote-modules)).
- `weir restore` — re-materialize everything the lock records,
  hash-verified — absent files are fetched, and a present-but-modified
  URL artifact is repaired by refetching (the lock is the intent). The
  one subcommand that fetches.
- `weir verify` — compare the vendored contracts against the lock:
  absent or modified artifacts are findings, exit 1.

### Conventions

- `weir --help` prints usage on stdout and exits 0 when asked for;
  any unrecognized invocation prints the same usage on stderr and
  exits 2.
- An unknown option gets a did-you-mean against the real spellings.
- `weir lsp` speaks JSON-RPC over stdio; conventional client argv
  like `--stdio` is tolerated ([editors](editors.md)).
- `weir --version` prints `<tag>+<hash>` — the exact build, not a
  marketing number.

## Command signatures

Weir checks that a command exists before running a script. A
signature closes the next gap: with one declared, `bicep buidl
--outfil x` is a located check error instead of a 3am failure.

### The cycle

Generate, verify, regenerate:

```text
weir add sig bicep      # probes the installed binary, writes .weir/sigs/bicep.weir + a lock entry
```

Then declare it per script — checking is opt-in, per file:

```text
#sig bicep
bicep build --outfile x.json
```

`weir verify` compares the vendored signature against the installed
binary's verbatim `--version` — an exact match; patch churn is
handled by regenerating, and an empty diff is the useful signal.
When the tool updates, `weir add sig` again.

### Partial by default

A generated signature is PARTIAL — unknown flags warn, not error,
because a scraped surface may be incomplete. Verify the surface by
hand, add `let exhaustive = true` to the signature file, and unknown
flags become errors.

Generation probes, in order: the tool's fish completions, shipped
fish completion files, then `--help`. A tool that yields no flags is
told so — `.weir/sigs/<tool>.weir` can always be written by hand;
it is an ordinary weir file.

### CI posture

`weir check` never runs the tool and never fetches — a signature is
a vendored, check-time artifact ([project layout](#project-layout-weir)), so
checking works for tools that only exist in CI, and CI checking
works offline. A locked-but-missing signature is `weir restore`'s
job to re-materialize, hash-verified.

The teaching version of this page — why you would want one — is one
paragraph in the [guide](GUIDE.md#declaring-a-tool-command-signatures).

## YAML schemas

A `yaml` block can name a JSON schema on its marker line, and the
checker validates the template's structure before line one runs:

```text
let svc = yaml schema=k8s-service
    apiVersion: v1
    kind: Service
    ...
```

### Vendoring

```text
weir add schema <url> --as <name>    # fetches into .weir/schemas/<name>.json, locks it
```

The schema is a vendored, pinned, check-time artifact
([project layout](#project-layout-weir)): `weir check` never fetches, so
checking works offline and in CI. A locked-but-missing schema file
is re-materialized by `weir restore`, hash-verified against the
lock; `weir verify` reports absent or modified schemas.

For Kubernetes, use the `-standalone-strict` schema variants —
their `additionalProperties: false` is what makes unknown-field
checking fire; the plain variants accept any unknown key.

### The validation boundary

Stated plainly so the green check is not over-read:

- a spliced `int` checks against an `integer` constraint;
- a spliced `string` against a `pattern` or `enum` constraint does
  NOT — the value is runtime data;
- `for`-generated content is structurally unchecked.

The schema validates what the checker can see. The teaching version
— templates, block scalars, splices-as-nodes — lives in the
[guide](GUIDE.md#commands-and-processes).

## Remote modules

Share code across repos by vendoring it — a fetch, not a package
manager: no registry, no resolver, no version ranges.

```text
weir add module github.com/org/repo//lib/retry.weir@v1.2.0 --as retry
```

Host-first, with `//` separating the repo from the in-repo path
(GitLab nests groups, so the boundary must be spelled — and one
spelling beats a per-host rule). An explicit `@ref` is required — a
tag, branch, or sha; weir resolves it to the **full commit sha** and
stores that in the lock's URL, so what you reviewed is what restore
refetches. The shorthand knows `github.com` and `gitlab.com`; any
other host takes the full raw URL:

```text
weir add module https://raw.githubusercontent.com/org/repo/<sha>/lib/retry.weir --as retry
```

`add` validates before writing: the file must be a `module`
(declaration-only), it must typecheck, and it must not `import` —
vendored modules are leaves for now, a current boundary rather than
a permanent one. Nothing lands in `.weir/` unless all three hold.
Then import it anywhere under the project by name — the `weir:`
namespace resolves via the same upward walk `#sig` uses, so the
spelling is depth-independent:

```text
import "weir:retry" as Retry
```

A re-add is the update path: it overwrites and prints the old and
new sha — the vendored file's diff is the review surface. `verify`
flags a modified copy; `restore` repairs it by refetching.

Worth stating as a feature: `check --can` walks imports, so a
vendored module's commands, writes and network access appear in
**your** report, each at the module's own `file:line` — "what can
this dependency do" is answerable before anything runs.

Private repos need a token only at `add`/`restore` time (the
committed file needs neither): set `WEIR_TOKEN_GITHUB_COM` /
`WEIR_TOKEN_GITLAB_COM`. GitHub answers 404 for
private-without-auth, so the not-found teach names the token.
Tokens are read from the environment and never stored. Stated
non-claim: no signing — trust is review-then-hash plus your own
repo's history.

## Project layout: `.weir/`

A project that uses [signatures](#command-signatures),
[schemas](#yaml-schemas) or [remote modules](#remote-modules) grows
one directory:

```text
.weir/
  lock.json          # the lock: exact identity + hash for every vendored artifact
  sigs/<tool>.weir   # command signatures (weir add sig)
  schemas/<name>.json# JSON schemas (weir add schema)
  modules/<name>.weir# vendored modules (weir add module)
```

A script finds its `.weir/` by walking UP from its own directory to
the first one — the walk stops at a `.git` boundary (directory or
file, so worktrees behave) or the filesystem root, and the error
names both what was looked for and where the walk stopped. One
`.weir/` at the repo root serves every script under it.

### Commit all of it

The directory is designed to be checked in — lock, signatures and
schemas alike. Vendoring is the point: `weir check` never fetches,
so what CI checks against is exactly what you committed. After a
fresh clone with a missing artifact, `weir restore` re-materializes
what the lock records, hash-verified — but a committed `.weir/`
never needs it.

### Four properties, each load-bearing

- **Vendored** — checked in, never fetched during check.
- **Pinned** — exact identity, no ranges; comparisons are pairwise,
  not a dependency graph.
- **Check-time only** — deleting every contract leaves every script
  running identically; contracts constrain what the CHECKER accepts
  and contribute nothing at run time.
- **Declared, not discovered** — a `.weir/` directory's mere
  existence never changes how a file checks; a script opts in with
  `#sig` / `schema=`.

## Configuration

Weir's configuration surface is deliberately small: one env var for
logging, one JSON file for the REPL. Scripts read neither — a
script's behaviour is its file's business, which is the property the
language exists to keep.

### `WEIR_LOG`

`Log.trace`/`debug`/`info`/`warn` write levelled diagnostics to
stderr; `WEIR_LOG` selects the level for one run:

```text
WEIR_LOG=debug weir script.weir    # turn the detail on
WEIR_LOG=off weir script.weir      # silence the log
```

`printerr` and `fail` reach you at every level — deliberately, there
is no `Log.error`, because an error an env var can silence is the
one message you needed. Stdout stays byte-identical at every level:
logging never touches the data channel.

### The REPL config

The REPL also loads an optional [init file](repl.md#the-init-file) —
declarations for the prompt plus the `#session` settings directive —
from the same directory.

`$XDG_CONFIG_HOME/weir/config.json` (fallback
`~/.config/weir/config.json`; on Windows
`%APPDATA%\weir\config.json`) — read by the REPL only:

| key | default | meaning |
|---|---|---|
| `historySize` | `5000` | entries kept |
| `historyDedup` | `true` | drop consecutive duplicates |
| `historyPath` | `<state>/weir/history` | where history lives (`$XDG_STATE_HOME`, `~/.local/state`, or `%LOCALAPPDATA%`) |
| `finderFlags` | `["--height", "40%", "--reverse"]` | argv extras for the `Ctrl+R` fzf search |
| `echoElems` | `100` | the echo's unforced-element cap ([the REPL](repl.md)) |

An unknown key is warned about with a did-you-mean — a typo
silently doing nothing is the config-file failure mode this
refuses. An absent or unparseable file means defaults.

Also honored everywhere: [`NO_COLOR`](https://no-color.org)
strips the REPL's and the checker's dressing; piped output is
always plain.
