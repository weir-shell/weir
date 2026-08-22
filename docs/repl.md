# The weir REPL

`weir` with no arguments starts the REPL. Bare names work here
(`map`, `where` — scripts require the qualified names), values
echo back, and tab completion and history behave as you'd expect.
`Ctrl+C` abandons the line; `Ctrl+D` exits, and typing `#quit` does
the same. This page is the REPL's manual; the tour lives in the
[guide](GUIDE.md#the-repl).

## What the echo shows

At a terminal, the echo presents a value by its shape:

- a seq of records — as a table: bold header, dim rule, clamped to
  the terminal width (the widest column absorbs the clip)
- a seq of strings — as its lines
- anything else — as the literal

The type footer sits below in every case, along with a sentence
noting when a seq is unforced. `NO_COLOR` strips the dressing.

## The echo is a glance, not the output

Three output roles, three ways to ask:

- the **glance** is the echo: bounded at 100 unforced elements by
  default, so command-sized output fits without a `Seq.force`; long
  strings clip, and a hint names the cap in effect
- the **read** is `|> print`: every element, one line each — for
  non-string seqs, `|> Seq.map show |> print`
- the **stream** is a bare command statement: live, as the child
  produces it

`#echo` moves the glance's cap for the session — `#echo 25`, or
`#echo all` (uncapped: an infinite seq will hang). Bare `#echo`
reports the current cap, and `echoElems` in the config seeds it. A
forced seq always echoes whole; the cap only ever clips unforced
ones. Piped output keeps its own fixed surface regardless.

## Help

`#help` lists the directives and the modules. `#help Seq` lists one
module's members — a question FSI cannot answer. `#help Seq.collect`
shows one member's doc, rendered from the same source hover uses, so
the two cannot disagree.

The `#` prefix marks a line addressed to the tooling rather than the
language. File directives (`#sig`, `#schema`) are read at check
time; session directives (`#help`, `#quit`) run now. One glyph, two
lifetimes.

## The prompt and the colors

The prompt reddens after an entry that errors, and clears on the
next success. A nonzero exit you asked for as a value —
`cmd | exitCode`, `| complete` — is data, not an error, so it stays
quiet: weir has no ambient `$?`, and the tint tracks the error path
only.

Input colors as you type — keywords, strings, comments, numbers and
the command markers — and the head word colors by live resolution: bold for a
known binding or builtin, blue for found on PATH, red for
would-fail. A red head is the typo caught before Enter. `NO_COLOR`
is honored, and piped sessions are always plain text.

## The init file

`init.weir` beside the [config file](tooling.md#configuration)
(`$XDG_CONFIG_HOME/weir/`, else `~/.config/weir/`; `%APPDATA%\weir\`
on Windows) loads before the first prompt. It is DECLARATION-ONLY —
`type` and `let`, the module rule applied to the prompt — plus one
`#session` directive for the settings a declaration cannot express:

```text
#session {
    cwd = "/home/me/work"
    logLevel = "debug"
    echoCap = 50
    env = [
        "EDITOR", "hx"
    ]
}

/// push the current branch and set upstream
let pu () = git push --set-upstream origin HEAD
```

The four keys: `cwd` (applied before the first prompt), `env`
(`seq<string * string>` — set into the process environment once, so
`Env.vars`, every spawn, and `within env` layering all see it; an
entry adds or overrides, never unsets), `logLevel` (the `WEIR_LOG`
levels, same parsing), and `echoCap` (the `#echo` cap's persistent
form — it wins over the config file's `echoElems`). A typo'd key
gets a did-you-mean; values cannot run commands.

Aliases are functions — `let pu () = …` already takes params and
spans lines, so there is no separate alias concept; calling a
nullary one costs `()`.

Loading is ALL-OR-NOTHING: a broken init prints its located weir
error plus `init: NOT loaded`, and the session starts with none of
it — safe precisely because nothing in the file can run. A missing
init is silent; a loaded one reports one line
(`init: 3 name(s) from …`). `#help` on an init name shows its `///`
doc.

## Multi-line editing

Enter submits when the statement is complete, and opens a
continuation line when it is not — weir asks its own parser, so
`match x with` grows and `1 + 1` submits. Up and Down move within
the buffer, and a recalled history entry returns whole: a three-line
match comes back as three lines and re-edits.

The fixed bindings (this is not a keybinding-config feature):

| key | in the buffer |
|---|---|
| <kbd>Enter</kbd> | submit if complete, else newline; on an empty final line, submit ANYWAY (the escape from a pending buffer — the error shows, the input is kept) |
| <kbd>Alt+Enter</kbd> / <kbd>Ctrl+J</kbd> | force a newline (formatting; an entry stays one statement). <kbd>Shift+Enter</kbd> is NOT bindable — terminals do not distinguish it from <kbd>Enter</kbd>. Windows Terminal claims left-<kbd>Alt+Enter</kbd> for fullscreen: use <kbd>Ctrl+J</kbd> or right-Alt there |
| <kbd>Up</kbd> / <kbd>Down</kbd> | move between lines; <kbd>Up</kbd> on the first line recalls history |
| <kbd>Ctrl+R</kbd> | history search (fzf when installed; entries display one-line, ⏎-joined) |
| <kbd>Esc</kbd> / <kbd>Ctrl+C</kbd> | abandon the whole buffer |
| <kbd>Ctrl+D</kbd> | EOF on an empty buffer; delete/join otherwise |
