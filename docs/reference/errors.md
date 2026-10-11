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

When a line has more than one thing wrong, you see one error: the one
at the furthest point the parser reached. A later mistake cannot hide
an earlier one, and separate broken statements each report their own
error. The position is always a real spot in your file; a hint may
replace the message, but never moves the position. `check`,
`check --json` and the LSP all report the same error.

An error while the script runs is reported as `file:line:col` too, at
the statement that failed — inside an `if`, `match`, `for`, `within`
or `retry` body, a pipeline, or a lambda, not at the block's first
line. A failed command points at the command itself, even when its
output is only read a few lines later. Other errors (`fail`, a builtin)
point at the start of the failing line. A failure inside a function
reports the line in the function, then where it was called from, one
line per call, innermost first:

```text
lib.weir:7:5: error: command failed with exit code 9: sh -c exit 9
  called from deploy.weir:4:1
```

Inline lambdas and `for` bodies add no `called from` line; they are
part of the statement they're written in.

## Raising

A failing command raises an error when its output is read; a reifier
turns the failure into a value instead
([commands](commands.md#reifiers-and-chaining)). Builtins raise errors with a
location and a readable message (`File.read: no such file: …`). There
is no try/catch block. A resource that needs cleanup goes in a
[`within` scope](scopes.md), which releases it however the block exits.

To turn a failure into a value, end an expression with `|> try` — the
expression twin of `| complete`:

- `e |> try` is `Option<'T>`: `Some value`, or `None` if evaluating `e`
  raised.
- `e |> try result` is `Result<'T, string>`: `Ok value`, or `Error`
  with the message. `Result` has `map`, `defaultValue`, `toOption` and
  `isOk`.
- Everything to the left of `|> try` is captured (`|>` groups left to
  right), and the value is fully evaluated inside, sequences included,
  so a failure can't surface after `try` returns. An infinite sequence
  on the left never finishes.
- `exit` still exits, and weir's own internal errors are not caught.
  Effects that happened before the failure are not undone.
- `try` is only valid after `|>`.

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
