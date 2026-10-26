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
       weir add sig [--global] <tool>          generate a command signature from the installed binary
       weir add schema <url> --as <name>       fetch an external contract, lock it
       weir add module <src>//<file>@<ref> --as <name>  vendor a remote module, lock it
       weir add module <url> [--path <file>] --as <name>  the raw-URL form (--path: the URL is the root)
       weir add module <name>@<tag> [--as <name>]      an official library module (github.com/weir-shell/lib)
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
  last statement with its type (`3 : int`), as the REPL echoes it
  with `#types on`.
  Newlines separate statements, just as in a file. The last statement
  must be an expression; if it is a declaration (a `let`, a `type`,
  …), the error says which kind.

### Checking

- `weir check script.weir` — print every diagnostic, each with its
  location and code, without running anything. A command that isn't
  on PATH is only a warning here (when you run the script, it's an
  error), so you can work on scripts for tools you haven't installed.
  `--json` prints the same diagnostics as JSON, for other tools and
  AI agents to read. A diagnostic that has a related site — the
  signature an implementation mismatches, the import an imported
  error came through, the rebinding that shadows an unread binder —
  carries it in a `related` array of `{file, line, col, message}`;
  the key is absent otherwise. A diagnostic weir knows how to repair carries a
  `fix`: a `title` and the `edits` that repair it (`line`, `col`,
  `endLine`, `endCol` — 1-based, end-exclusive, all against the
  unedited file — and `newText`), the same edits the editor's quick
  fix applies.
- `weir check --can script.weir` — list what the script can run,
  read, write and connect to, with a `file:line` for each item. This
  is what the script *could* do, not what it will do, so a branch
  that never runs still counts. With `--strict`, it exits 2 if the
  script contains anything weir can't look inside (`sh -c` and other
  interpreters), so CI can reject scripts that can't be analysed.
  `--json` gives machine-readable output.
- `weir fmt script.weir` — format the script in place. With
  `--check`, it doesn't write anything and exits nonzero if the file
  isn't formatted, which is what you want in CI. A shebang line is
  left exactly as written, and a formatted file ends in one newline.
  A file that doesn't parse is not touched; the error names its line.

### The project subcommands

These maintain the [`.weir/` tree](#project-layout-weir):

- `weir add sig <tool>` — read the flags of the installed tool and
  write `.weir/sigs/<tool>.weir`, plus an entry in the lock
  ([signatures](#command-signatures)). With `--global`, write it to
  your user sig directory instead, with no lock entry
  ([signatures drive Tab](#signatures-drive-tab-completion)).
- `weir add schema <url> --as <name>` — download a JSON schema into
  `.weir/schemas/` and record it in the lock ([schemas](#yaml-schemas)).
- `weir add module <src>//<file>@<ref> --as <name>` — download a copy
  of a remote module, with the files it imports, into `.weir/modules/`
  and record it in the lock ([modules](#remote-modules)). From any
  other host: `weir add module <url> --as <name>` for one file, or
  `weir add module <root url> --path <file> --as <name>` for a file
  and its imports. `weir add module <name>@<tag>` vendors a module of
  the official library ([the library](#the-library)).
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
  any unrecognized invocation exits 2 with a message on stderr: the
  usage (of the subcommand, if one was named), or for an unknown
  option a pointer to `weir --help`.
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
`--version` output, with runs of whitespace collapsed and a trailing
list of library versions dropped (curl's `libcurl/8.18.0
OpenSSL/3.5.5 …`). (Only the first line, because some tools, like az,
print pages of environment details.) `weir verify` compares it with the installed tool's version
and reports any difference at all. When the tool updates, run
`weir add sig` again. If the regenerated file shows no diff, nothing
that matters to your scripts changed.

### Signatures drive Tab completion

A signature also completes the tool's arguments: the REPL's Tab and
the editor (through `weir lsp`) offer the same things.

- After the tool and after each subcommand word: the subcommand words
  (`git com<TAB>` gives `git commit`).
- A word starting with `-`: the flags of the subcommand you are in,
  `--long` and `-x`. When the signature is `negatable`, typing `--no-`
  offers the `--no-x` forms too (a bare `--` lists only the real flags).
  A Go tool's flags are offered as `-name`, find's predicates as
  `-name`. For a tool that takes unique prefixes, the full name is
  offered.
- After a flag that takes a value, and after `--flag=`: paths when the
  value is a file or directory (the flag's name or its description
  says so: `--file`, `-C <path>` described as "run in this
  directory"…), the choices when the signature lists them (a field
  typed by a union of bare cases: `type Color = | Always | Never |
  Auto`, then `color: Option<Color>`), otherwise nothing.
- After the first operand of a `stopAtOperand` subcommand, inside an
  `unchecked` subcommand, after `--`, or after a subcommand the
  signature doesn't know: paths only, since the signature can't know
  what comes next.

In a script, completion uses the signatures the `#sig` lines declare.
The REPL has no `#sig` line, so it looks a signature up by the command
name when you press Tab:

1. the nearest project's `.weir/sigs/` (found the way `#sig` finds
   it, from the REPL's current directory);
2. then your user sig directory, `$XDG_CONFIG_HOME/weir/sigs/`
   (`~/.config/weir/sigs/`; `%APPDATA%\weir\sigs\` on Windows).

No signature means plain path completion, as before. A signature is
read once and re-read when its file changes.

`weir add sig --global <tool>` writes to the user directory, so one
signature serves the REPL in every directory. It gets no lock entry:
`weir verify` and `weir restore` only look at the project's lock, so
they never see it, and scripts never read it (a script's `#sig` always
means the project's file, so the script checks the same way on every
machine). To update it, run `weir add sig --global <tool>` again.

#### Values from the tool itself

Some values only the tool knows at the moment you type: branch names,
pods, pull requests. A signature can let the REPL ask for them with
one line naming how:

- `let dynamic = "git"`: branches, tags and remotes, read by weir's own
  fixed git commands (`git for-each-ref --format=%(refname:short)
  refs/heads refs/remotes refs/tags`, `git remote`). Which operand takes
  what is a small table: any operand of checkout, switch, merge, rebase,
  diff, log, reset, cherry-pick, revert and show is a ref; `branch -d`/
  `-D` and `-m`/`-c` take a branch, `tag -d` a tag; the first operand of
  push, fetch and pull is a remote, push's and pull's second a branch;
  `git remote remove|rename|set-url|… <remote>`. A typed `--`, or `-C`,
  `-c`, `--git-dir`, `--work-tree` before the subcommand, asks nothing.
- `let dynamic = "cobra"`: a Cobra tool (kubectl, helm, gh, docker…)
  answers `<tool> __complete <the words typed> <partial>` itself.

`weir add sig` writes the line when it read the tool through its Cobra
endpoint or git's completion helper; delete it to turn it off.

The tool is asked only for an operand or a flag's value where the
signature has paths or nothing to offer; subcommands and flag names stay
the signature's, so Tab starts no program there. If the tool answers,
you get its candidates; if it has none, fails, or takes longer than
300 ms, you get what the signature alone gives. A Cobra tool that says
"no files" gets no path fallback. The child runs in the REPL's directory
and environment with empty input, no terminal, and its error output
discarded. It is killed (with its children) at 300 ms, past 1 MiB or
10,000 lines of output, or as soon as you press another key, and that
key still lands in the buffer. Answers are kept for the rest of the line
you are editing. A candidate is offered only if it can be typed as a
plain argument; anything that would need quoting is dropped.

```text
#alias co = git checkout
co <TAB>          # your branches
```

Trust: a project's `.weir/sigs/` is read without asking, so a
signature there must never make Tab run a program on its say-so. `"git"`
runs only weir's own read-only git commands, never text from the
signature, so any signature can use it. `"cobra"` runs the tool, so it
is honored only from your user directory (`weir add sig --global
<tool>`). A project signature asking for it still completes
subcommands, flags and values, and the REPL says once per session:
`dynamic completion for kubectl needs a user-level sig: weir add sig
--global kubectl`.

Only the REPL asks. In the editor a script names what it means, not
whatever branch or pod exists right now, so `weir lsp` stays with the
signature alone.

### Partial by default

A generated signature is partial: an unknown flag is a warning, not
an error, because the flags read from a tool's help may be
incomplete. Once you have checked the flag list by hand, add
`let exhaustive = true` to the signature file, and unknown flags
become errors.

`weir add sig` reads the tool's own descriptions of itself, best
first:

- a Cobra tool (gh, docker, kubectl, kustomize, yq): its completion
  endpoint, `<tool> __complete <command> -`, for every command —
  exact flag names with their short forms, plugin commands included;
- git: its command list (`git --list-cmds=builtins`) and, per command,
  its completion helper and `-h` (run in an empty temporary repository);
- fish completions the tool prints (`<tool> completion fish`) or ships
  beside its own install (`<prefix>/share/fish/…` for
  `<prefix>/bin/<tool>`);
- a full help the tool's `--help` names: `--help all` (curl),
  `--help-all` (python), `--v8-options` (node), and GnuPG's
  `--dump-options`;
- then `--help` itself, or `-h`/`-help` when `--help` is refused.

The help pages are always read too: they show which flags take a value
and list the subcommands. Only the words a help page lists under a
commands heading are ever run as subcommands (`<tool> <command>
--help`), up to four levels deep and depth-first within a budget per
top-level command, so `gh pr create` and `docker compose up` are both
reached. A subcommand whose help is the tool's main page again
(systemctl's verbs) is not a separate case.

For a tool with subcommands, the signature lists flags per
subcommand:

- weir uses the longest subcommand path that matches (`jira issue list`
  rather than `jira issue`).
- Each subcommand accepts its own flags and the tool's global ones,
  which the signature records once, as `type Globals`. So
  `docker ps --detach` gets a warning that names `docker ps`.
- A word that names no subcommand the signature knows (a plugin, an
  alias it never saw, a command it could not walk) ends the check:
  the flags after it belong to a command weir has no list for.
- A flag written before the subcommand is checked against the
  subcommand the line reaches, as Cobra parses it
  (`gh --repo o/r issue view 3`).

A tool without subcommands gets one flat list of flags. If no flags
are found at all, `weir add sig` says so and names the file to
write by hand (`.weir/sigs/<tool>.weir` for a plain name), in either
form; it's an ordinary weir file.

### What a signature records

Each flag is a field; its type says what the flag takes:

```text
type Cmd = {
    verbose: bool              // --verbose takes nothing
    [<Short "o">]
    output: string             // -o x, -ox, --output x, --output=x
    color: Option<string>      // only --color=when (the value is attached)
    [<Wire "-m">]
    mod: seq<string>           // -m mod: the tool's own options end here
}
```

So `docker --context prod ps -a` reads `prod` as the value, and a
bundle like `tar -xzf a.tgz` is checked letter by letter. A field
whose `[<Wire>]` starts with a dash has exactly that spelling: a
second short (`-r` beside `-E`), `-h`, a short-only flag (python's
`-B`), or a single-dash word.

A few `let`s describe the tool's conventions; an older signature has
none of them and checks exactly as before (GNU flags, none taking a
value, a multi-letter single-dash word like `-fsSL` never read):

- `let bundles = true` — a multi-letter single-dash word is a bundle of
  short flags, checked letter by letter (`tr -dQ` warns on `-Q`).
  `weir add sig` writes it into every GNU-style signature it generates.

- `let convention = "go"` — Go's flag package: `-name`, `--name`
  and `-n` are one flag, never bundled. `"find"` — whole single-dash
  words (`-name`, `-mtime`), and `--x` only where listed. The default
  is `"gnu"`.
- `let abbreviations = true` — the tool takes a unique prefix of a
  long flag (`tar --verb`), as GNU getopt does; detected by asking
  `--versio`.
- `let negatable = true` — `--no-x` turns off any switch `--x`
  (node).
- `let dashWords = true` — a single-dash word that does not start
  with a known short is the tool's own value (kill's `-<signal>`:
  `kill -TERM 1`).
- `let stopAtOperand = [ "run"; "exec" ]` — in these subcommands
  (`""` is the tool itself) the first operand ends the tool's options:
  `docker run --rm alpine sh -c x` checks nothing after `alpine`, and
  `python3 script.py --verbose` nothing after the script.
- `let unchecked = [ "log" ]` — subcommands whose flags no source can
  list (git's `log` and `show` take revision options outside its
  option parser); a line that reaches one is not checked. `[ "" ]`
  names the tool itself: npm takes any config key as a flag (`npm
  <command> --key=value`, its help says), so its generated signature
  checks none of its flags.

Unknown flags get a did-you-mean when a known flag is close, short
forms included (`--H` suggests `-H`); a distant match is never
offered.

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
- validation keywords — `minLength`, `maxLength`, `pattern`,
  `minimum`, `maximum`, `multipleOf`, `minItems`, `maxItems`,
  `uniqueItems`, `minProperties`, `maxProperties`, `readOnly`,
  `writeOnly`, `deprecated`, `const`, `format`, `contentEncoding`,
  `contentMediaType` — are accepted in a schema but not enforced; a
  root `$id` is ignored

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
`.weir/types/pod.weir`, and prints the line to import it. `weir:pod`
then names that file; if a vendored module `.weir/modules/pod.weir`
also exists, the import is an error — rename one.

`#infer` drafts types from a sample, so it only knows what that
sample contained: if the next run returns a container without `env`,
the drafted type breaks. The schema knows what no sample can:

- a `required` property becomes a plain field, and any other property
  becomes `Option<…>`
- `additionalProperties` becomes a `seq<string * V>` mapping
- `$ref` definition names become type names
  (`io.k8s.api.core.v1.PodSpec` → `PodSpec`); a deeper in-document
  pointer (`#/definitions/opts/properties/inner`) names its type from
  its last segment, and `#` is the whole schema (a reference to it from
  inside is a cycle, drafted as `Yaml` below)
- a `$ref` with sibling `properties`/`required` (JSON Schema 2020-12)
  is merged into one record, with a note

More of the mapping:

- nullable types (`type: [.., "null"]` / `nullable: true`) become
  `Option`
- an `allOf` of one `$ref` plus annotations is flattened (a common
  pattern in Kubernetes schemas)
- an `enum` becomes `string` plus a `//` note listing the values
  (string unions are planned); an enum under `type: boolean`,
  `integer` or `number`, or of values that are all one such kind,
  becomes `bool`, `int` or `float`; an enum mixing kinds becomes `Yaml`;
  a `null` among the values makes it `Option<…>`
- an `anyOf` or `oneOf` of one schema and `{"type": "null"}`, in
  either order, becomes `Option<…>` of that schema
- a field that can be an integer or a string (`anyOf` of the two,
  `x-kubernetes-int-or-string`, as Kubernetes ports are, or `type:
  string` with `format: int-or-string`) becomes `Yaml`: `8080` reads
  as `YInt 8080` and `"25%"` as `YStr "25%"`, and both write back
  unchanged to JSON and YAML; so does a number-or-string field (a
  Kubernetes quantity such as `cpu: 1` or `cpu: "500m"`); a `oneOf`
  alternative that is a `$ref` counts by its target's type, and a
  `oneOf` with one alternative per JSON kind (a string or an object,
  say) becomes `Yaml` too
- a `const` drafts its type (`type`, or the value's own kind)
- each validation keyword the schema uses gets one
  `// note: not enforced: …` line naming where it appears
- an `anyOf` that mixes a scalar with an array or object (a string or
  a list of strings, say) becomes `Yaml`, with a note naming how each
  kind reads; any other `anyOf` uses the first variant, with a note
  asking you to check it
- a field that refers back to its own definition becomes `Yaml`, with
  a note — a weir record is a finite tree, so the subtree is read
  whole as an untyped node
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

(An imported type can be named without its module prefix while no
other import declares the same name, so `from json` takes `Pod`;
`Pod.Pod` always works. Two generated modules that both declare, say,
`ObjectMeta` are each reached by the qualified name.)

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

A module that imports other files of its repo by relative path
(`./lib/config.weir`, `../fmt.weir`, `sib.weir`) is vendored together
with them. `add` follows the imports and downloads every file at the
same commit, then copies them into `.weir/modules/<name>/`, keeping
their positions relative to each other, so the imports work without
changes. For example, the starship port's entry imports eight files
under `lib/`:

```text
weir add module github.com/weir-shell/ports//starship/starship.weir@main --as starship
```

```text
.weir/modules/starship/
  starship.weir
  lib/config.weir
  lib/mods/vcs.weir   # imports ../config.weir
  ...
```

A module without imports is a single file, `.weir/modules/<name>.weir`,
as before.

Inside a vendored file, `add` refuses three kinds of import, each
reported with the file and line:

- a `weir:` import. Vendoring a module that depends on another
  remote module isn't supported yet; vendor both and import each from
  your own code.
- an absolute path
- a relative path that leads outside the repo

A raw URL is one file, fetched exactly as written (a `//` in it is
part of the URL), and has no repo to fetch imports from, so a raw URL
whose file imports is refused. To vendor imports from any host, give
the repo root as the URL and the entry file, relative to it, with
`--path`:

```text
weir add module https://raw.githubusercontent.com/org/repo/<sha>/ --path lib/retry.weir --as retry
```

The root gets a trailing `/` if it lacks one; a root with a `?` or
`#` is refused. `--path` must be a relative path that stays inside
the root (no leading `/`, no `..` that climbs out), and it only goes
with a raw URL: the short form already carries the path after `//`.

Before writing anything, `add` checks that every file:

- is a `module` (only declarations)
- typechecks (the entry file is loaded with its imports, the way an
  import loads it)

If any check fails, nothing is written to `.weir/` and the lock is not
changed. The report line names the number of files and the entry's
members.

Then import the module by name anywhere in the project. `weir:` finds
it by searching upward for `.weir/`, the same way `#sig` does,
stopping at a `.git` directory or file or at the filesystem root (see
[project layout](#project-layout-weir)). So the import line is the
same at any directory depth, and it works the same way for a module
vendored outside any git repo:

```text
import "weir:retry" as Retry
```

### The library

Modules the weir project itself maintains live in one repo,
[github.com/weir-shell/lib](https://github.com/weir-shell/lib), one
file per module under `modules/`. A bare name vendors one of them:

```text
weir add module semver@v0.1.0
```

This is the same as
`weir add module github.com/weir-shell/lib//modules/semver.weir@v0.1.0 --as semver`:
the name is the file and the import alias, and `--as` renames it as
usual (`weir add module semver@v0.1.0 --as sv` gives `import
"weir:sv" as Sv`). Everything after the spelling is the ordinary
`add` — the lock holds the raw URL at the full commit sha, and
`verify` and `restore` can't tell the two spellings apart. A spec
with a `/` is never read as a library name.

The `@tag` is required; `weir add module semver` refuses and names
the spelling. The library is tagged as a whole (`v0.1.0`, `v0.1.1`,
…), so one tag pins every module, and there is no `latest` and no
branch to track — point at a tag. (Any ref the long form takes works
here too, a branch or a sha, since the lock holds the sha either
way.) A name whose alias is a builtin module (`retry` → `Retry`) is
refused as with `--as`; the library never publishes such a name.

Every library module is a single file with no imports, because a
vendored module can't import another vendored module yet (above).
The module list and the versioning rule are in the repo's README.

The first module is `semver`: parse, order and bump a version, with
`[<Ord n>]` on the parsed record's key so `Seq.sort`, `<` and
`Seq.max` follow the spec's precedence (build metadata is the one
field left unnumbered). Vendored as above and used from a release
script:

```text
import "weir:semver" as SemVer

let strip t = if Str.startsWith "v" t then t[1..] else t
let latest =
    git tag
    |> Seq.map strip
    |> Seq.choose SemVer.tryParse
    |> Seq.where (fun v -> not (SemVer.isPrerelease v))
    |> Seq.max
print (SemVer.toString (SemVer.bumpMinor latest))
```

`strip` is the consumer's leniency — the `v` of a git tag — named
where it lives; the module has none (`v1.2.3`, whitespace and `1.2`
are refusals, and `tryParse` turns each into `None`).

To update, run `add` again. It overwrites the file and prints the
old and new sha, and you review the change in the vendored file's
diff. For a module with imports it prints the old and new sha of each
file that changed, and deletes files the module no longer imports. If
a vendored file gets modified or deleted, `verify` reports it and
`restore` downloads it again, at the commit in the lock.

The lock entry of a module with imports records the commit, the entry
file and every file's sha256. A lock with such an entry has
`"schemaVersion": 2`; older weir versions refuse to read it rather
than misread it. `.weir/modules/<name>.weir` and
`.weir/modules/<name>/` both existing is an error at the import, as is
a `.weir/modules/<name>/` that the lock has no entry for (the lock
names the entry file).

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
  modules/<name>/    # a vendored module with the files it imports
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
| `types` | `"off"` | `"on"` shows types in the REPL echo (`2 : int`); `#types` overrides it for the session ([the REPL](repl.md#what-the-echo-shows)) |

An unknown key gets a warning suggesting the closest valid key, so
a typo doesn't silently do nothing. If the file is missing or can't be
parsed, the defaults apply.

The REPL also loads an optional [init file](repl.md#the-init-file)
from the same directory. It holds your own declarations (functions, a
custom prompt), `#alias` lines, and a `#session` block for settings.

weir also honors [`NO_COLOR`](https://no-color.org) everywhere: it
turns off color in the REPL and the checker. Piped output is always
plain.
