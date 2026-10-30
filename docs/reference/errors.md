# Errors and diagnostics

## Check, then run

`weir check` reports all diagnostics at once, each with a location
and a code, without running anything. A missing command is a warning
at check time (exit 0, so you can still edit scripts for tools you
haven't installed) and an error at run time:

```weir
["nosuchcmd --flag"] |> File.write "ref-missing.weir"
let c = weir check ref-missing.weir | complete
print $"check exits {c.exitCode}"
let r = weir ref-missing.weir | complete
print $"run exits {r.exitCode}"
```

`weir check --json` emits the same diagnostics as an array of
`{file, line, col, endLine, endCol, severity, code, message}` — for
editors, CI gates and agent loops.

## Which error you see

### At check time

A line with several mistakes reports one error: the one at the
furthest point the parser reached. So a later mistake can't hide an
earlier one, and each broken statement reports its own.

The position is always a real spot in your file. A hint may replace
the message but never moves the position. `check`, `check --json`
and the LSP report the same error.

### At run time

A runtime error is `file:line:col` too, at the statement that failed,
not at the first line of its block (`if`, `match`, `for`, `within`,
`retry`, a pipeline, a lambda). It points at:

| failure | position |
|---|---|
| a command | the command, even if its output is read lines later |
| a reifier (`\| text`, `\| line`, …) | its command |
| a call (`fail`, a builtin such as `Seq.item`) | the function called, so `if bad then fail "x" else …` points at `fail` |
| anything else | the start of the failing line |

A failure inside a function reports the line in the function, then
one `called from` line per call, innermost first:

```text
lib.weir:7:5: error: command failed with exit code 9: sh -c exit 9
  called from deploy.weir:4:1
```

Inline lambdas and `for` bodies add no `called from` line; they're
part of their statement.

## Raising

Operations raise on failure. `|> try` / `|> try result` (or
a `tryX` twin such as `Str.tryToInt`) turn a failure into a value.
`|> orFail` turns an `Option`/`Result` back into a raise.

A failing command raises when its output is read; a reifier makes the
failure a value instead ([commands](commands.md#reifiers-and-chaining)).
Builtins raise with a location and a readable message
(`File.read: no such file: …`).

There is no try/catch block. Cleanup belongs in a
[`within` scope](scopes.md), which releases its resource however the
block exits.

### `try`

The expression twin of `| complete`:

- `e |> try` is `Option<'T>`: `Some value`, or `None` if `e` raised.
- `e |> try result` is `Result<'T, string>`: `Ok value` or
  `Error message`. `Result` has `map`, `defaultValue`, `toOption` and
  `isOk`.
- It captures everything to its left (`|>` groups left to right) and
  evaluates it fully, sequences included, so no failure surfaces after
  `try` returns. An infinite sequence on the left never finishes.
- `exit` still exits, and weir's own internal errors aren't caught.
  Effects before the failure are not undone.
- `try` is only valid after `|>`.

### `orFail`

The inverse: a failed value becomes an error. Fail fast in helpers
with `|> orFail`; decide at the boundary with `|> try result`.

- `e |> orFail` takes a `Result<'T, string>` or an `Option<'T>`: `Ok v`
  and `Some v` give `v`. Anything else is a check error naming both
  shapes.
- `Error msg` raises `msg`; `None` raises `orFail: no value (None)`.
- `e |> orFail "no port"` adds a message (any string expression in the
  atom position `| orFail "msg"` takes): `Error msg` raises
  `no port: msg`, `None` raises `no port`. Control characters in an
  `Error`'s text show escaped, like any data in an error message.
- The error points at the `orFail` and is ordinary: `|> try` catches
  it, and a `pure` block allows it.
- Like `try`, it captures everything to its left, and `orFail` is a
  keyword. After a command the same word is the reifier,
  `<command> | orFail "msg"` (one `|`); `<command> |> orFail` is an
  error pointing at it.

```weir
let cfg = Map.ofPairs [("port", "8080")]
let port = Map.tryGet "port" cfg |> orFail "no port"
let host = Map.tryGet "host" cfg |> orFail "no host" |> try result
print $"{port} {host}"
```

```weir-error
let n = 3 |> orFail // orFail takes a Result<'T, string> or an Option<'T>
```

A script's own union cases take precedence over the prelude's: a
`type Status = Ok | Error` declared in the script keeps constructing
its own `Ok`, while a match on a `try result` value still sees
`Result`'s cases.

## `fail` and `exit`

`fail "reason"` stops the script with a located error and exit 1.
`exit n` exits silently with a specific code, which is how you pass
on a child process's failure:

```weir
let r = sh -c "exit 3" | complete
if r.exitCode <> 0 then print $"would exit {r.exitCode}"
```

## `Log` and log levels

`Log.trace`/`debug`/`info`/`warn` write levelled diagnostics to
stderr; `WEIR_LOG` sets the level for one run (`off` silences them).
There is deliberately no `Log.error`, because an error message should
never be hidden by an environment variable. For a message that must
always appear, use `printerr`; to stop, use `fail`. Stdout is exactly
the same at every log level:

```weir
let e = Env.ofPairs [("WEIR_LOG", "off")]
within env e
    weir -e "Log.info \"hidden\" ; printerr \"loud\" ; print 1"
```

## Warnings

Warnings never stop a run; they point out a likely mistake and carry
on. Examples are a missing command at check time, `>` passed as a
literal argv word, and `;` in a command line. Each warning says what
to use instead.
