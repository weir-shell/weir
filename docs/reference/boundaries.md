# Boundaries: argv and env

Command-line arguments and environment variables load the same way:
declare a record, load it once, and use the typed values from then
on. Loading is strict and reports every problem at once, in a single
error, before the script does anything.

## `Args.load`

Each field becomes a flag:

- the name in kebab case: `dryRun` → `--dry-run`
- a first-letter short flag when unambiguous (a letter or digit only:
  `__y` gets none)
- `bool` is true when the flag is present; `string`, `int` and the
  other scalars are required; `Option` makes a field optional
- the first line of the field's `///` doc is its `--help` text

```weir
type Cli = {
    [<Short "C">]
    /// clean the target first
    clean: bool

    port: Option<int>
}

let cli = Args.load Cli
print $"{cli.clean} {cli.port}"
```

`[<Short "c">]` sets a short flag (`"h"` is reserved for `--help`),
and `[<NoShort>]` removes one. These problems are all reported
together:

- unknown flags (with a did-you-mean)
- unexpected arguments
- missing required flags
- unparseable values

`--help` prints the usage even when the rest of the command line is
invalid. A `///` doc on the record type is the program's description;
its first paragraph shows under the usage line. Empty sections are
left out, and `[flags]` shows in the usage line only when there are
flags.

A bad command line is the user's mistake, not the script's, so the
error looks like any tool's usage error: the script's name, one line
per problem, the help lines of the flags involved, and a pointer to
`--help`. No weir source location; exit code 2.

The word after an unknown flag is reported without its text, since the
flag may be a misspelled secret. That holds for every spelling (`-tok`,
`-xv`, and `--tok=…` hides its attached text) and after a subcommand
that takes no arguments. A known subcommand name after an unknown flag
is still read as the subcommand.

`deploy.weir --trget prod` gives:

```text
deploy.weir: unknown flag '--trget'. Did you mean '--target'?
deploy.weir: unexpected argument after '--trget'
deploy.weir: missing required flag '--target'
  -t, --target <string>       required — the environment to deploy to
run 'deploy.weir --help' for usage
```

`try` still captures it, with this text as the message.

Load the arguments first. A top-level statement above the first
top-level `Args.load` or `Env.load` that writes files, runs a command
or uses the network would still run on `--help` or a bad argument, so
`weir check` warns (`effect-before-args`). Reads, printing, function
definitions and a `within` whose body holds the load don't count.

There are no positional arguments; pass operands as flags. To parse
arguments by hand, `Args.flag` and `Args.value` scan the raw
`Self.args`.

## Subcommands

Declare subcommands as a union whose cases carry records. The first
argument picks the case, the rest are parsed as that case's flags,
and the checker makes sure your `match` covers every case. Flags
shared by all subcommands go once on a containing record, and may
appear before or after the subcommand name:

```weir-error
type CloneArgs = { remote: string; force: bool }
type Cmd = Clone of CloneArgs | Status
match Args.load Cmd with // no argv here: "missing command — one of:" and the commands
| Clone a -> print a.remote
| Status -> print "status"
```

A case's `///` doc (its first line) is shown beside the subcommand in
`--help`, and `tool clone --help` shows it as the subcommand's
description. A case without a doc falls back to its payload record's
doc:

```weir
let src = <<<
    type CloneArgs = { remote: string }

    /// A tiny git front end.
    type Cmd =
        /// copy a repository
        | Clone of CloneArgs
        /// show the working tree state
        | Status

    let cmd = Args.load Cmd
    print $"{cmd}"

src |> File.write "ref-help.weir"
weir ref-help.weir --help
```

prints

```text
usage: ref-help.weir <command> [flags]

A tiny git front end.

commands:
  clone    copy a repository
  status   show the working tree state
clone flags:
  -r, --remote <string>       required
```

## `Defaults`

`[<Default v>]` supplies a value when the flag is absent; the field
stays non-`Option`, and `--help` shows the default. On a bool,
`[<Default true>]` adds a `--no-x` flag to turn it off.
`[<Default false>]` is rejected on a bool, since a missing flag is
already false. The attribute takes literals only; for a computed
default, keep the field an `Option` and fill it in with one line of
code.

## `Env.load`

The same approach works for environment variables. Field names match
variable names exactly, with no case conversion (use
`[<Wire "NAME">]` for a name that is not a legal identifier).
`[<Default>]` fills in missing variables, and a `Secret` field is the
standard way to take in a token. Env bools are read from text
(`FLAG=false`), not from whether the variable is set.

When a variable has a fixed set of allowed values, declare it as a
union of cases without payloads. Matching is case-insensitive (env
values are usually uppercase), and an unknown value is an error that
lists the allowed ones with a did-you-mean:

```weir
type Level =
    | Debug
    | Info

type LogCfg = { REF_BOUND_LEVEL: Level }

["REF_BOUND_LEVEL=debug"] |> File.write "ref-bound.env"
let e = Env.fromFile "ref-bound.env"
within env e
    sh -c "echo level=$REF_BOUND_LEVEL"
print "declared sets beat stringly config"
```

`Env.get "NAME"` reads one variable as `Option<string>`.
`Env.fromFile` reads a subset of dotenv (`KEY=VALUE`, quotes, `#`
comments; no `export` and no `$VAR` expansion). It reads the file once,
when it is called: the result is a snapshot, and a bad line is an error
at the call.

## What both refuse

Both refuse `Bytes` fields, and the error lists the field types they
take. Both parse `Instant` fields as ISO 8601. A `Secret` passed as a
flag is visible in `ps` output; weir does not hide argv.
