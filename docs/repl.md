# The weir REPL

`weir` with no arguments starts the REPL. Bare names work here
(`map`, `where` — scripts require the qualified names), values
echo back, and tab completion and history behave as you'd expect.
`Ctrl+C` abandons the line; `Ctrl+D` exits, and typing `#quit` does
the same. This page is the REPL's manual; the tour lives in the
[guide](GUIDE.md#the-repl).

## Tab completion

Tab completes the word under the cursor: module members, record
fields, keywords, bindings, and — at a statement head — the callable
commands. Two behaviours worth naming:

- **An empty prompt lists the session directives** (`#help` first),
  not the whole world. A bare Tab is a teaching Tab — `#help` itself
  lists the modules and members. Start typing and the usual filtered
  pool returns; a Tab in argument position still lists the directory.
- **Record fields complete through a pipe.** A record piped into `_.`
  or a lambda param completes its fields — `r |> _.`,
  `raw |> from yaml Pod |> _.` , `… |> (fun row -> row.`  — the same
  as a bound `r.`. This is the payoff of `#infer`: a typed record's
  fields surface wherever the value flows.
- **`#help <name>` completes what it documents.** `#help Pat<TAB>`
  offers every name `#help` can document with that prefix — modules,
  types (an `#infer`'d type included), and top-level forms; a
  `#help Module.<TAB>` completes that module's members. The offered
  set and the documented set are one, so a type you can `#help` is a
  type you can Tab.
- **A path completes quoted in an expression, bare as a command
  argument.** `File.read ./x<TAB>` yields `File.read "./x"` — a bare
  path is not a valid weir expression, so the completion is a string
  literal that parses. In command-argv position (`cat ./x`, `ls ./dir`)
  the path stays bare, the way argv wants it. If you already opened the
  quote (`File.read "./x<TAB>`), the completion lands inside it — no
  second quote; a directory keeps its trailing `/` within the quotes.

Completion never runs anything — a directory read or a cached PATH
lookup at most.

## What the echo shows

At a terminal, the echo presents a value by its shape:

- a seq of records — as a table: bold header, dim rule, clamped to
  the terminal width (the widest column absorbs the clip)
- a seq of strings — as its lines
- anything else — as the literal

The type footer sits below in every case, along with a sentence
noting when a seq is unforced. `NO_COLOR` strips the dressing.

A `let` binding echoes the same way — the value's lines (or table)
first, then a `name : type` footer that carries the very same
unforced sentence a bare echo shows. The footer sits BELOW the lines
in both, so a truncated bind can never look like it silently dropped
data: you see the clip, then the sentence telling you it was clipped
and how to see the rest (`Seq.force`).

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

`#help` lists the directives and the modules — one module per line
with a one-line blurb. `#help Seq` lists one module's members the
same way: one per line, each with the first line of its doc, clipped
to the terminal — a glanceable answer FSI cannot give. `#help
Seq.collect` shows one member's full doc, rendered from the same
source hover uses, so the two cannot disagree; the glance is that
doc's first line, so it cannot drift either.

At a tty the docs' `` `code` `` spans render tinted, the backticks
themselves dropped — the span reads as code, not markdown source.
Piped, or under `NO_COLOR`/`TERM=dumb`, the literal backticks stay:
the byte surface is unchanged and the span boundary survives the
stripping.

`#find [query]` searches all of it fuzzily. Every module
(`Seq — blurb`) and every member (`Seq.map — glance`) feeds fzf
(when installed, at a tty) with a **live preview** of the
highlighted name's full doc; Enter prints the `#help` answer for the
selection, Esc returns to the prompt with nothing. Without fzf — or
piped — `#find query` is a case-insensitive substring filter over
the same lines: deterministic, and never an "install fzf" message.
The preview runs the session's own binary headlessly
(`weir --repl-doc <name>` prints the exact `#help <name>` bytes —
builtin docs only, nothing evaluated).

The `#` prefix marks a line addressed to the tooling rather than the
language. File directives (`#sig`, `#schema`) are read at check
time; session directives (`#help`, `#quit`) run now. One glyph, two
lifetimes — the [reference table](reference/lexical.md#directives)
maps every directive to its context.

## The last result: `it`

Every line that produces a value binds it to `it` — an expression, a
command, or a `let` RHS (the echo path). So a pipeline you just
built is one word away:

```
weir> kubectl get po -o json |> from json Pods |> _.items
weir> it |> Seq.map _.name
```

`it` is REPL-only (scripts and `-e` never see it, like the bare
aliases). A unit statement or a directive leaves `it` untouched. The
spelling is `it` deliberately: `_` is taken (the `_.field` shorthand
and the `let _ =` discard), so it cannot be the last result — `it`
(ghci's convention) collides with nothing.

One statement produces output but no value: a bare command at a tty
streams straight to the terminal (the colour-inherit path — weir
never holds the bytes), so `it` does not bind there. The meta line
says so, and `let pods = kubectl get po` is the capturing spelling —
a `let` binds `it` too.

## `#infer`: draft types from a sample

Exploring an unknown JSON/YAML blob means hand-transcribing its
shape. `#infer` drafts it from an evaluated sample:

```
weir> let raw = kubectl get po -o json
weir> #infer raw from json as Pods
defined: Pods, Metadata, Status (3 types)
weir> raw |> from json Pods |> _.items |> Seq.map _.⟨TAB⟩
```

`#infer <source> from <json|jsonl|yaml> as <Name>` evaluates the
source ONCE, parses it by the named adapter, walks it to a set of
named `type` declarations, and injects them into the session (as if
you had typed them) — so `from json Pods` checks and field
completion lights up. The source defaults to `it` when omitted
(`#infer from json as Pods` reads the last result — the way to "pipe
into" a directive). A bare top-level array (or `from jsonl`) names
the ELEMENT: you write `seq<Name>`.

The output is ordinary `type` decls you own and edit — this is the
`weir add schema` category, not check-time inference (`check` never
evaluates; `from json` never sniffs). A single sample cannot see
optional/absent fields, so `#infer` PRINTS notes rather than
guessing: an empty array (`seq<string>` default), a null field
(`Option<string>`), a heterogeneous array (first element, "verify").
The same inference is a builtin — `sample |> Json.inferShape |> print`
(and `Yaml.inferShape`) returns the declaration text outside the REPL.

A key weir cannot spell as a field name never breaks the draft: a
non-identifier or KEYWORD key rides `[<Wire "the-key">]` over a
derived identifier (`k8s-app`→`k8sApp`, `in`→`inField` — the parser
rejects every keyword in field position), and an empty-string key —
which a field name cannot spell and `[<Wire>]` refuses to carry — is
DROPPED with a printed note (reading tolerates the extra key). When a
drafted type still fails to check — the sample carried the same key
twice, say — `#infer` does not stop at "a drafted type did not
check". The drafted text is weir's own synthesis, so the diagnostic
names the `line:col` WITHIN the drafted type and prints the offending
line with a caret, the same shape a normal parse or type error uses:

```
weir> #infer sample from json as Pods
#infer: a drafted type did not check at line 1, col 1: duplicate field 'a'
  type Pods = {
  ^
```

You can see exactly which generated field is wrong and fix the sample
(or hand-edit once you `#save`).

## `#save`: distill the session to a script

`#save <path>` DISTILLS the session to its reusable definitions — a
session is scratch; `#save` crystallizes what you'll keep. It writes
the `type` decls and named `let` bindings (with their real
multi-line source — a heredoc keeps its newlines), auto-qualifying
bare aliases (`map` → `Seq.map`, `startsWith` → `Str.startsWith`)
and formatting the result — so `#infer` to explore, `#save` to keep.
The bare-echo scratch (a glance) drops; a redeclared name is deduped
to its last form; injected `#infer` types come out as ordinary
`type` decls. The written file is GUARANTEED to `weir check` clean —
any surviving statement that still references session-only state (a
`let x = it`) is dropped and `#save` prints a note counting them.

`#save` also DESUGARS [command-head aliases](#alias-command-head-aliases):
the saved script has no alias table, so each kept line's command HEAD
is rewritten back to the real invocation — `let pods = k get po` saves
as `let pods = kubectl get po`, a kept `kb overlays/prod` as `kustomize
build overlays/prod`. The rewrite is span-based and head-only, so a `k`
in a string or an argument is untouched. The output is alias-free and
checks clean.

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
on Windows) loads before the first prompt. It is declaration-only —
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

A `let pu () = …` is a nullary FUNCTION, not a command-head alias:
it takes params and spans lines, but calling it costs `()` and it
does not accept bare argv. When you want a short HEAD that carries
argv straight through — `k get po` — reach for `#alias` below.

Loading is all-or-nothing: a broken init prints its located weir
error plus `init: not loaded`, and the session starts with none of
it — safe precisely because nothing in the file can run. A missing
init is silent; a loaded one reports one line
(`init: 3 name(s), 2 alias(es) from …`). `#help` on an init name
shows its `///` doc.

## `#alias`: command-head aliases

A `#alias` maps a short name to a program and a fixed prefix of
arguments, consulted ONLY in command-head position. Declare them in
`init.weir` (the canonical place), one per line:

```text
#alias k  = kubectl
#alias kb = kustomize build
```

Now `k get po -o yaml` runs `kubectl get po -o yaml`, and `kb
overlays/prod` runs `kustomize build overlays/prod` — the fixed
prefix is inserted after the exe, your argv appended. This is a
RESOLUTION-table entry, not a textual macro: your argv stays typed
argv, so `k get $x` passes `$x` as ONE argument (the injection law
holds), and a `k` in a string, a variable, or an argument is
untouched — only the HEAD token, only in command-head position.

The resolution order is **alias table → PATH**, and the `^`
force-PATH sigil skips the table: with `#alias ls = ls --color`, a
bare `ls x` runs `ls --color x` and `^ls x` runs the real `ls x`.
Aliases are **single-hop** — the target is a program, never another
alias (an alias-of-alias is rejected at load). The target need not
exist when defined (like bash); a malformed `#alias` line is a loud
init error (init is all-or-nothing).

Aliases are **REPL-only**: scripts and `-e` never load `init.weir`,
so their command resolution is unchanged and an alias name there is
an ordinary unknown command. `#save` [desugars](#save-distill-the-session-to-a-script)
aliases, so a saved script is alias-free.

A live `#alias` works at the prompt too (bare `#alias` lists the
table), but `init.weir` is the canonical home.

## Multi-line editing

Enter submits when the statement is complete, and opens a
continuation line when it is not — weir asks its own parser, so
`match x with` grows and `1 + 1` submits. Up and Down move within
the buffer, and a recalled history entry returns whole: a three-line
match comes back as three lines and re-edits.

The fixed bindings (this is not a keybinding-config feature):

| key | in the buffer |
|---|---|
| <kbd>Enter</kbd> | submit if complete, else newline; on an empty final line, submit anyway (the escape from a pending buffer — the error shows, the input is kept) |
| <kbd>Alt+Enter</kbd> / <kbd>Ctrl+J</kbd> | force a newline (formatting; an entry stays one statement). <kbd>Shift+Enter</kbd> is not bindable — terminals do not distinguish it from <kbd>Enter</kbd>. Windows Terminal claims left-<kbd>Alt+Enter</kbd> for fullscreen: use <kbd>Ctrl+J</kbd> or right-Alt there |
| <kbd>Up</kbd> / <kbd>Down</kbd> | move between lines; <kbd>Up</kbd> on the first line recalls history |
| <kbd>Ctrl+R</kbd> | history search (fzf when installed; entries display one-line, ⏎-joined) |
| <kbd>Esc</kbd> / <kbd>Ctrl+C</kbd> | abandon the whole buffer |
| <kbd>Ctrl+D</kbd> | EOF on an empty buffer; delete/join otherwise |

A REDIRECTED session (`printf '…' | weir`) has no editor, but it
assembles multi-line statements the same way — the way a script does.
It reads physical lines and keeps them together while the statement is
still open (a heredoc body, a multi-line `type`, an offside
`if`/`match` block, a leading-`|>` pipeline), so a pasted or scripted
block runs as one statement. A single statement per line is unchanged,
and a directive is always one line.

## weir and fzf

fzf is optional everywhere — every touchpoint has a built-in
fallback, and nothing ever tells you to install it. When it is on
PATH:

- **<kbd>Ctrl+R</kbd>** history search runs through fzf (fallback: a
  minimal reverse substring search).
- **`#find`** is fuzzy help search with a live doc preview
  (fallback: a substring filter over the same candidate lines).
- **fzf-class tools compose in ordinary pipelines** — an interactive
  picker draws on /dev/tty while stdio pipes, so `git branch | fzf`
  just works; when the selection AND the cancel code are both data,
  reach for `cmd | complete`
  ([guide](GUIDE.md#exit-codes-from-command-to-value)).
- **`finderFlags`** in the [config](tooling.md#configuration) tunes
  the invocation (`--height 40% --reverse` by default) for both fzf
  touchpoints.

One caveat is handled for you: weir's glyphs (`^`, `|`, `$`, `!`)
are fzf extended-search *operators*, so weir passes `--no-extended`
first — literal fuzzy matching over code and names. fzf is
last-flag-wins, so `finderFlags = ["--extended"]` restores it.
