# Boundaries: argv and env

Command-line arguments and environment variables load the same way:
declare a record, load it once, and use the typed values from then
on. Loading is strict and reports every problem at once, in a single
error, before the script does anything.

## `Args.load`

Field names become kebab-case flags (`dryRun` → `--dry-run`), plus a
first-letter short flag where that is unambiguous. A `bool` field is
true when the flag is present. `string`, `int` and the other scalars
are required, and `Option` makes a field optional. The first line of
the field's `///` comment is its `--help` text:

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
invalid.

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
match Args.load Cmd with // no argv here: "missing subcommand; one of: clone, status"
| Clone a -> print a.remote
| Status -> print "status"
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
comments; no `export` and no `$VAR` expansion).

## What both refuse

Both refuse `Bytes` fields, and the error names the conversion to
use. Both parse `Instant` fields as ISO 8601. A `Secret` passed as a
flag is visible in `ps` output; weir does not hide argv.
