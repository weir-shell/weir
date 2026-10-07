# Tooling

This page covers the tools around the language: the `weir` command,
command signatures, YAML schemas, remote modules, the `.weir/`
directory and configuration. Read it when you set up a project or a
CI pipeline; for the language itself, see the reference.
[Editors](editors.md) and [the REPL](repl.md) have their own pages.

## The CLI

weir is a single binary with a handful of subcommands (CI checks this
block matches `weir --help` exactly):

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
       weir gen types --schema <name>          generate weir types from a locked schema
       weir restore                            re-materialize the lock's artifacts
       weir verify                             vendored contracts vs the lock
       weir --version                          the build stamp
```

### Running

- `weir` — start the [REPL](repl.md). It accepts short unqualified
  names like `map`, which scripts don't.
- `weir script.weir args...` — run a script. A `#!/usr/bin/env weir`
  shebang works too. The script gets its arguments as `Self.args`, or
  parsed into a type you declare with `Args.load`.
- `weir -e '<program>'` — run a program and print the value of its
  last statement. Newlines separate statements, just as in a file. The
  last statement must be an expression; if it is a declaration (a
  `let`, a `type`, …), the error says which kind.

### Checking

- `weir check script.weir` — print every diagnostic, each with its
  location and code, without running anything. A command that isn't
  on PATH is only a warning here (when you run the script, it's an
  error), so you can work on scripts for tools you haven't installed.
  `--json` prints the same diagnostics as JSON, for other tools and
  AI agents to read.
- `weir check --can script.weir` — list what the script can run,
  read, write and connect to, with a `file:line` for each item. This
  is what the script *could* do, not what it will do, so a branch
  that never runs still counts. With `--strict`, it exits 2 if the
  script contains anything weir can't look inside (`sh -c` and other
  interpreters), so CI can reject scripts that can't be analysed.
  `--json` gives machine-readable output.
- `weir fmt script.weir` — format the script in place. With
  `--check`, it doesn't write anything and exits nonzero if the file
  isn't formatted, which is what you want in CI.

### The project subcommands

These maintain the [`.weir/` tree](#project-layout-weir):

- `weir add sig <tool>` — read the flags of the installed tool and
  write `.weir/sigs/<tool>.weir`, plus an entry in the lock
  ([signatures](#command-signatures)).
- `weir add schema <url> --as <name>` — download a JSON schema into
  `.weir/schemas/` and record it in the lock ([schemas](#yaml-schemas)).
- `weir add module <src>//<file>@<ref> --as <name>` — download a copy
  of a remote module into `.weir/modules/` and record it in the lock
  ([modules](#remote-modules)).
- `weir gen types --schema <name>` — generate weir `type` declarations
  from a locked schema into `.weir/types/<name>.weir`
  ([types from a schema](#types-from-a-schema)).
- `weir restore` — bring the downloaded files back in line with the
  lock, checking each hash. A missing file is downloaded again, and so
  is a file that was changed locally. A missing signature can't be
  restored, because it was generated rather than downloaded; `restore`
  reports it and tells you to run `weir add sig` again. Apart from
  `add schema` and `add module`, this is the only subcommand that
  downloads anything.
- `weir verify` — compare the vendored files against the lock. It
  reports every missing or modified file, and for signatures a tool
  version mismatch or a missing tool; it exits 1 if there are any.

### Conventions

- `weir --help` prints usage on stdout and exits 0 when asked for;
  any unrecognized invocation prints the same usage on stderr and
  exits 2.
- If you mistype an option, weir suggests the closest one.
- `weir lsp` speaks JSON-RPC over stdio; arguments that editors
  commonly pass, like `--stdio`, are accepted ([editors](editors.md)).
- `weir --version` prints `<tag>+<hash>`, identifying the exact
  build.

## Command signatures

weir checks that a command exists before running a script. A
signature goes one step further and checks the flags you pass it.
With a signature for `bicep`, the typo in `bicep build --outfil x` is
an error from `weir check` that points at the line, instead of a
failure at 3am. (A generated signature is partial, so there it is a
warning.)

### Creating a signature

Generate a signature from the installed tool:

```text
weir add sig bicep      # probes the installed binary, writes .weir/sigs/bicep.weir + a lock entry
```

Then declare it in each script that should be checked against it.
Scripts without the `#sig` line aren't affected:

```text
#sig bicep
bicep build --outfile x.json
```

The signature records the tool's version: the first line of its
`--version` output, with runs of whitespace collapsed. (Only the first
line, because some tools, like az, print pages of environment
details.) `weir verify` compares it with the installed tool's version
and reports any difference at all. When the tool updates, run
`weir add sig` again. If the regenerated file shows no diff, nothing
that matters to your scripts changed.

### Partial by default

A generated signature is partial: an unknown flag is a warning, not
an error, because the flags read from a tool's help may be
incomplete. Once you have checked the flag list by hand, add
`let exhaustive = true` to the signature file, and unknown flags
become errors.

`weir add sig` looks for flags in this order: the tool's fish
completions, fish completion files shipped with the tool, then its
`--help`. If the help lists subcommands, it reads each subcommand's
help too, up to four levels deep (enough for
`kustomize edit add resource`).

For a tool with subcommands, the signature lists flags per
subcommand:

- weir uses the longest subcommand path that matches (`jira issue list`
  rather than `jira issue`).
- Each subcommand accepts its own flags, its parents' flags and the
  global ones. So `docker ps --detach` gets a warning that names
  `docker ps`.

A tool without subcommands gets one flat list of flags. If no flags
are found at all, `weir add sig` says so. You can also write
`.weir/sigs/<tool>.weir` by hand, in either form; it's an ordinary
weir file.

### Checking in CI

`weir check` never runs the tool and never downloads anything. A
signature is a checked-in file that only matters at check time
([project layout](#project-layout-weir)). So you can check scripts for
tools that are only installed in CI, and CI can check them offline.
Commit the signature file: if it is in the lock but missing on disk,
`weir restore` can't recreate it, and tells you to regenerate it with
`weir add sig`.

For why you would want a signature, see the short section in the
[guide](GUIDE.md#declaring-a-tool-command-signatures).

## YAML schemas

A `yaml` block can name a JSON schema on its first line, and
`weir check` then validates the block's structure against it before
anything runs:

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

The schema file is checked in, recorded in the lock with its hash,
and only matters at check time ([project layout](#project-layout-weir)).
`weir check` never downloads anything, so checking works offline and
in CI. If a schema is in the lock but missing on disk, `weir restore`
recreates it and verifies its hash. `weir verify` reports missing or
modified schemas.

For Kubernetes, use the schema files whose names end in
`…-standalone-strict.json`. Their `additionalProperties: false` is
what makes an unknown field an error; the plain variants accept any
key.

### What the schema check covers

A passing check doesn't mean every value was validated:

- a spliced `int` is checked against an `integer` constraint
- a spliced `string` is not checked against a `pattern` or `enum`
  constraint, because its value is only known at run time
- content generated with `for` is not checked against the schema's
  structure

In short, the schema checks what weir can see before the script runs.
Templates, block scalars and splices are explained in the
[guide](GUIDE.md#commands-and-processes).

### Types from a schema

The same locked schema can generate the `from json`/`from yaml` types
for you. This is the more reliable counterpart of the REPL's
[`#infer`](repl.md#infer-draft-types-from-a-sample):

```text
weir gen types --schema pod [--as Pod] [--out <path>|-]
```

This reads the vendored `.weir/schemas/pod.json` (never the
network), writes a weir module containing only type declarations to
`.weir/types/pod.weir`, and prints the line to import it.

`#infer` drafts types from a sample, so it only knows what that
sample contained: if the next run returns a container without `env`,
the drafted type breaks. The schema knows what no sample can:

- a `required` property becomes a plain field, and any other property
  becomes `Option<…>`
- `additionalProperties` becomes a `seq<string * V>` mapping
- `$ref` definition names become type names
  (`io.k8s.api.core.v1.PodSpec` → `PodSpec`)

More of the mapping:

- nullable types (`type: [.., "null"]` / `nullable: true`) become
  `Option`
- an `allOf` of one `$ref` plus annotations is flattened (a common
  pattern in Kubernetes schemas)
- an `enum` becomes `string` plus a `//` note listing the values
  (string unions are planned)
- `anyOf` uses the first variant, with a note asking you to check it
- a self-referential definition becomes untyped `Yaml`, with a note
- field names are cleaned up the same way `#infer` does it, so a
  `type:` key becomes `[<Wire "type">] kind`

Where the schema leaves a question open, the file has a `// note:`
line saying what was chosen, instead of a silent guess.

The generated file is **yours**: edit it freely. Nothing regenerates
it behind your back (signatures work the same way). Running
`weir gen types` again regenerates it and overwrites your edits. It
is deliberately **not in the lock**: the lock records the files that
`add` downloaded, and this file belongs to you once it's generated.
Instead, a header in the file records the schema name and the lock
hash it came from.

The same locked schema always produces the same output. Before
writing, `weir gen types` checks the module with the normal checker;
if the schema can't be expressed in weir, the command fails with the
reason and writes nothing.

Import it anywhere in the project. The `weir:` prefix looks in
vendored modules first, then in generated types:

```text
import "weir:pod" as Pod
let p = kubectl get pod web -o json |> from json Pod
```

(An imported type can be named without its module prefix here, so
`from json` takes `Pod`, not `Pod.Pod`.)

## Remote modules

To share code across repos, you vendor it: weir downloads a copy into
your repo. This is not a package manager. There is no registry, no
dependency resolution and no version ranges.

```text
weir add module github.com/org/repo//lib/retry.weir@v1.2.0 --as retry
```

The source starts with the host. `//` separates the repo from the
path of the file inside it. (GitLab groups can nest, so weir can't
guess where the repo name ends. Marking it explicitly also means one
syntax works for every host.)

The `@ref` is required: a tag, branch or commit sha. weir resolves it
to the **full commit sha** and records that in the URL stored in the
lock, so `restore` downloads exactly what you reviewed. This short
form works for `github.com` and `gitlab.com`. For any other host,
give the full raw URL:

```text
weir add module https://raw.githubusercontent.com/org/repo/<sha>/lib/retry.weir --as retry
```

Before writing anything, `add` checks that the file:

- is a `module` (only declarations)
- typechecks
- doesn't `import` anything (for now, a vendored module can't import
  other modules, though that may change)

If any check fails, nothing is written to `.weir/`.

Then import the module by name anywhere in the project. `weir:` finds
it by searching upward for `.weir/`, the same way `#sig` does,
stopping at a `.git` directory or file or at the filesystem root (see
[project layout](#project-layout-weir)). So the import line is the
same at any directory depth, and it works the same way for a module
vendored outside any git repo:

```text
import "weir:retry" as Retry
```

To update, run `add` again. It overwrites the file and prints the
old and new sha, and you review the change in the vendored file's
diff. If the vendored copy gets modified, `verify` reports it and
`restore` downloads it again.

`check --can` follows imports, so a vendored module's commands, file
writes and network access appear in **your** report, each at the
module's own `file:line`. You can see what a dependency is able to do
before running anything.

For a private repo, set `WEIR_TOKEN_GITHUB_COM` or
`WEIR_TOKEN_GITLAB_COM`. Only `add` and `restore` need the token;
using the committed file doesn't. Tokens are read from the
environment and never stored. GitHub answers 404 for a private repo
when no token is given, so weir's not-found error mentions the token.

Modules aren't signed. You decide to trust a module by reviewing its
code. From then on, the lock holds its hash, and your repo's history
records every change to it.

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
  types/<name>.weir  # generated, user-owned type modules (weir gen types — not locked)
```

A script finds its `.weir/` by searching upward from its own
directory and using the first one found. The search stops at a `.git`
directory or file (so git worktrees work) or at the filesystem root,
and if nothing is found, the error says what was looked for and where
the search stopped. One `.weir/` at the repo root serves every script
below it.

### Commit all of it

Check in the whole directory, including the lock, signatures and
schemas. That is the point of vendoring: `weir check` never downloads
anything, so CI checks against exactly what you committed. If a fresh
clone is missing a downloaded file, `weir restore` gets it back and
verifies its hash, but with `.weir/` committed you never need to.

### How it behaves

- **Checked in.** Everything is in your repo; nothing is downloaded
  during a check.
- **Exact versions.** Each file is locked to one exact version, never
  a range. Each file is tracked on its own; there is no dependency
  graph.
- **Signatures and schemas only affect checking.** Delete every
  signature and schema, and every script still runs exactly the same;
  they only change what `weir check` accepts.
- **Scripts opt in.** The mere presence of a `.weir/` directory never
  changes how a file is checked. A script opts in with `#sig` or
  `schema=`.

## Configuration

weir has very little configuration: one environment variable for
logging and one JSON file that only the REPL reads. Neither changes
what a script does, so a script behaves the same wherever it runs;
only its own file decides what it does.

### `WEIR_LOG`

`Log.trace`, `Log.debug`, `Log.info` and `Log.warn` write log lines
to stderr. By default you see `info` and `warn`; `WEIR_LOG` sets the
level for one run (`trace`, `debug`, `info`, `warn` or `off`):

```text
WEIR_LOG=debug weir script.weir    # turn the detail on
WEIR_LOG=off weir script.weir      # silence the log
```

`printerr` and `fail` always print, whatever the level. There is
deliberately no `Log.error`, because an environment variable should
never be able to hide an error message. Logging never writes to
stdout, so stdout is the same at every level.

### The REPL config

The REPL reads its settings from `$XDG_CONFIG_HOME/weir/config.json`
(`~/.config/weir/config.json` if `XDG_CONFIG_HOME` isn't set;
`%APPDATA%\weir\config.json` on Windows). Nothing else reads this
file.

| key | default | meaning |
|---|---|---|
| `historySize` | `5000` | entries kept |
| `historyDedup` | `true` | remove duplicates and keep the latest, so each entry appears once, most recent last |
| `historyPath` | `<state>/weir/history` | where history lives (`$XDG_STATE_HOME`, `~/.local/state`, or `%LOCALAPPDATA%`) |
| `finderFlags` | `["--height", "40%", "--reverse"]` | extra arguments for the `Ctrl+R` fzf search |
| `echoElems` | `100` | how many elements of a lazy seq the REPL prints ([the REPL](repl.md)) |

An unknown key gets a warning suggesting the closest valid key, so
a typo doesn't silently do nothing. If the file is missing or can't be
parsed, the defaults apply.

The REPL also loads an optional [init file](repl.md#the-init-file)
from the same directory. It holds your own declarations (functions, a
custom prompt), `#alias` lines, and a `#session` block for settings.

weir also honors [`NO_COLOR`](https://no-color.org) everywhere: it
turns off color in the REPL and the checker. Piped output is always
plain.
