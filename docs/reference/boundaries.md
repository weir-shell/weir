# Boundaries: argv and env

Both load the same way: declare a record, load once, typed
thereafter. Loading is strict and COLLECTED — every problem arrives
together in one boundary error, before any effect runs.

## `Args.load`

Field names derive kebab-case flags (`dryRun` → `--dry-run`) and
unambiguous first-letter shorts. `bool` fields are presence flags;
`string`/`int` and the other scalars are required; `Option` makes a
field optional; the field's `///` first line is its `--help` text:

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

`[<Short "c">]` pins a short (`"h"` is reserved for `--help`);
`[<NoShort>]` suppresses one. The collected refusals: unknown flags
(with a did-you-mean), unexpected arguments, missing requireds,
unparseable values. `--help` prints the derived usage even on
otherwise-invalid invocations.

There are no positionals — spell operands as flags. For hand-rolled
shapes, `Args.flag` and `Args.value` scan the raw `Self.args`.

## Subcommands

A union of record-payload cases: the first token picks the case,
the rest parse as its flags, and the dispatch `match` is
exhaustiveness-checked. Shared flags live ONCE on a containing
record; they float around the case token:

```weir-error
type CloneArgs = { remote: string; force: bool }
type Cmd = Clone of CloneArgs | Status
match Args.load Cmd with // no argv here: "missing subcommand; one of: clone, status"
| Clone a -> print a.remote
| Status -> print "status"
```

## `Defaults`

`[<Default v>]` fills an absent flag; the field stays non-`Option`
and `--help` shows the default. On a bool, `[<Default true>]` mints
the `--no-x` opposite; `[<Default false>]` is rejected there
(presence already rests at false). The attribute takes literals
only — a computed default keeps the field `Option` plus one line.

## `Env.load`

The same declaration law over environment variables: field names
match var names VERBATIM (no case-mapping; `[<Wire "NAME">]` for a
name that is not a legal identifier), `[<Default>]` fills absences,
and a `Secret` field is the standard way a token enters. Env bools
are text (`FLAG=false`), not presence.

An env value with a fixed legal set declares it as a union of bare
cases — matching is case-insensitive (env convention is uppercase),
and a miss lists the candidates with a did-you-mean:

```weir
type Level =
    | Debug
    | Info

type LogCfg = { REF_BOUND_LEVEL: Level }

["REF_BOUND_LEVEL=debug"] |> File.write "ref-bound.env"
let e = Env.fromFile "ref-bound.env"
print "declared sets beat stringly config"
```

`Env.get "NAME"` reads one var as `Option<string>`;
`Env.fromFile` reads the dotenv subset (`KEY=VALUE`, quotes, `#`
comments — no `export`, no `$VAR` expansion).

## What both refuse

`Bytes` (the exits are named), and for `Instant` fields both parse
ISO 8601. A `Secret` passed as a flag is ps-visible — the stated
non-claim.
