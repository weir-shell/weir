# Errors and diagnostics

## Check, then run

`weir check` reports every diagnostic — located, coded, collected —
with no evaluation. A missing command is a WARNING at check (exit
0; scripts for uninstalled tools stay editable) and a refusal at
run:

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

## Raising

A failing command raises when its stream is FORCED; the four
exit-code forms bind it as data instead
([commands](commands.md#exit-codes)). Builtins raise with located,
weir-shaped messages (`File.read: no such file: …`). There is no
try/catch and no exception values: a resource that needs cleanup
takes a [`within` scope](scopes.md) (release on every exit); a
fallible middle step becomes data with `| complete`.

## `fail` and `exit`

`fail "reason"` stops the script with a located error and exit 1.
`exit n` exits with a specific code, silently — how a child's
failure passes through:

```weir
let r = sh -c "exit 3" | complete
if r.exitCode <> 0 then print $"would exit {r.exitCode}"
```

## `Log`, and the level law

`Log.trace`/`debug`/`info`/`warn` write levelled diagnostics to
stderr; `WEIR_LOG` picks the level for one run (`off` silences).
There is deliberately no `Log.error`: an error an env var can
silence is the one message you needed — unconditional messages are
`printerr`, stopping is `fail`. Stdout stays byte-identical at
every level:

```weir
let e = Env.ofPairs [("WEIR_LOG", "off")]
!e(weir -e "Log.info \"hidden\" ; printerr \"loud\" ; print 1")
```

## Warnings

Warnings never stop a run — they name a hazard and continue: a
missing command at check, `>` passing through as a literal argv
word, `;` in a command line. Each says what to use instead.
