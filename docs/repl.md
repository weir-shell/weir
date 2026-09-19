# The weir REPL

`weir` with no arguments starts the REPL. Bare names work here
(`map`, `where` — scripts require the qualified names), values
echo back, and tab completion and history behave as you'd expect.
`Ctrl+C` abandons the line; `Ctrl+D` exits, and typing `#quit` does
the same. This page is the REPL's manual; the tour lives in the
[guide](GUIDE.md#the-repl).

## Tab completion

Tab completes the word under the cursor: module members, record
fields, keywords, bindings, and — at a head slot — the callable
commands. A head slot is the statement start *or a top-level `let`'s
RHS*: `let svc = kubect<TAB>` completes the command exactly as a bare
`kubect<TAB>` does, the words after that head complete as argv
(directory entries, nothing else), and the live head tint follows the
same rule — `let svc = kubectl …` paints its RHS head by the session
verdict. Session [aliases](#alias-command-head-aliases) are known
heads at both slots (tint and Tab); a `^`-led head completes against
PATH alone — the sigil skips bindings and the alias table. Two
behaviours worth naming:

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
- **A bound map completes its keys.** An open map's keys are data
  (`data: seq<string * string>` from `#infer`), so completion reads
  them from the value you already hold: inside the open key literal
  of a pipe-form `Map.get`/`Map.tryGet`/`Map.has` —
  `d |> Map.tryGet "Cor<TAB>` — the receiver binding's own keys
  complete, prefix-filtered, inside the quotes (you own the closer).
  The receiver must be a bare session binding (`it` counts) whose
  value is already materialized — a map, or a forced pair-seq. The
  boundary is peek-versus-evaluate: a pipeline receiver
  (`cm |> from json … |> _.data |> Map.tryGet "`) completes nothing,
  because knowing its keys would mean running the pipeline — bind it
  first (`let d = …`) and the keys complete; an unforced seq is never
  pulled for the same reason.
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
- a named function — as a mini-help (below)
- anything else — as the literal

The type footer sits below in every case, along with a sentence
noting when a seq is unforced. `NO_COLOR` strips the dressing. Type
variables in the footer read `'a`, `'b`, … — the display form; error
messages keep the checker's own names.

A bare expression that evaluates to a **named function** echoes what
`#help` would answer, composed from the same sources:

- a builtin — its qualified signature plus the doc's first line
  (`find` names its home, `Seq.find`)
- a function you defined in the session — `name : scheme` plus a dim
  line showing the definition's first physical line (a redefinition
  shows the last accepted)
- an anonymous or composed closure — `<fun> : ty`, nothing to name

A `let` binding echoes the same way — the value's lines (or table)
first, then a `name : type` footer that carries the very same
unforced sentence a bare echo shows. The footer sits BELOW the lines
in both, so a truncated bind can never look like it silently dropped
data: you see the clip, then the sentence telling you it was clipped
and how to see the rest (`Seq.force`).

The binding footer also states the seq's state, tty-only:

```
weir> let pods = kubectl get po -A
…the pods…
pods : seq<string> (command-backed — re-runs on each use)
weir> let snap = kubectl get po -A |> Seq.force
…the pods…
snap : seq<string> (frozen)
```

A command-backed unforced bind re-runs its command on every use —
the annotation says so before it bites (the checker warns on the
same hazard in a script: an unforced binding pulled twice). A
materialized bind reads `(frozen)`; a pure lazy seq gets no
annotation — only the hazard and its resolution speak. State and
the truncation sentence share one parenthetical; the piped surface
never carries the annotation.

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
The signature line and the example block tint too: the signature
structurally (name bold, types in the casing-law yellow, punctuation
dim) and the example through the REPL's own input colorizer — an
example is weir code, so it renders exactly like the line you would
type at the prompt. Piped, or under `NO_COLOR`/`TERM=dumb`, none of
it fires: the literal backticks stay, the byte surface is unchanged
and the span boundary survives the stripping.

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

Expressions and commands rebind `it` — always, unit included (FSI's
law). So a pipeline you just built is one word away:

```
weir> kubectl get po -o json |> from json Pods |> _.items
weir> it |> Seq.map _.name
```

A `let` binds its name and nothing else — `let o = 10` leaves `it`
alone, exactly as F# Interactive's `let o = 10;;` binds no `it`. A
directive never touches it; a fresh session's `it` is unbound. `it`
is REPL-only (scripts and `-e` never see it, like the bare aliases).
The spelling is `it` deliberately: `_` is taken (the `_.field`
shorthand and the `let _ =` discard), so it cannot be the last
result — `it` (FSI's and ghci's convention) collides with nothing.

A bare command at a tty streams straight to the terminal (the
colour-inherit path — weir never holds the bytes), so its value is
`()` and that is what `it` binds. Using that unit `it` where a value
is needed is an ordinary type error, and the error appends the
repair with the streamed command verbatim:

```
weir> kubectl get po -A -o yaml
…the pods, live…
: seq<string>
weir> #infer it from yaml as Pods
#infer: the source has type unit; the adapter needs seq<string> (a captured JSON/YAML sample)
to capture: let x = kubectl get po -A -o yaml
```

## `#infer`: draft types from a sample

Exploring an unknown JSON/YAML blob (or a kubectl-style aligned
table) means hand-transcribing its shape. `#infer` drafts it from an
evaluated sample:

```
weir> let raw = kubectl get po -o json
weir> #infer raw from json as Pods
defined: Pods, Metadata, Status (3 types)
weir> raw |> from json Pods |> _.items |> Seq.map _.⟨TAB⟩
```

`#infer <source> from <json|jsonl|yaml|table> as <Name>` evaluates the
source ONCE, parses it by the named adapter, walks it to a set of
named `type` declarations, and injects them into the session (as if
you had typed them) — so `from json Pods` checks and field
completion lights up. The source defaults to `it` when omitted
(`#infer from json as Pods` reads the last result — the way to "pipe
into" a directive). A bare top-level array (or `from jsonl`) names
the ELEMENT: you write `seq<Name>`. `from table` drafts the ROW
record from the header and a per-column scan of the cells — a column
with empty/`<none>` cells drafts `Option` with a note, and the value
reads as `seq<Name>`.

The output is ordinary `type` decls you own and edit — this is the
`weir add schema` category, not check-time inference (`check` never
evaluates; `from json` never sniffs). When the shape has a published
JSON Schema, generate the types from the CONTRACT instead:
[`weir gen types`](tooling.md#types-from-a-schema) reads a locked
schema and knows what no sample can — `required` vs optional,
`additionalProperties`, the definition names. Array elements MERGE: the
element type is the union of every element's keys, a key absent in
some elements drafts `Option<T>` — one sample of a k8s List sees the
optional fields its items disagree on. Where the sample still cannot
decide, `#infer` PRINTS notes rather than guessing: an empty array
(`seq<string>` default), a null field (`Option<string>`), a genuine
type conflict for one key across elements (the first element's type,
"verify").

An object whose keys are DATA drafts as the open mapping
`seq<string * V>` instead of a record, with a note. The detection:
its values share ONE shape, and either a majority of its keys are
not identifier-shaped (k8s `labels`/`annotations` — dots, slashes,
dashes; a reserved word like `type` still counts as
identifier-shaped) or its key sets differ across the array's sibling
elements (a ConfigMap's `data`). Mapping keys are data, so no
`[<Wire>]` is drafted — read them as pairs
(`cm.data |> Seq.tryFind (fun (k, _) -> k == "Corefile")`); both
`from json` and `from yaml` speak the shape. An object with
identifier keys identical across elements — `metadata` — stays a
record. An empty `{}` is an open map with zero entries: it drafts
`seq<string * string>` with a note (no evidence for V, so string).
The same inference is a builtin — `sample |> Json.inferShape |> print`
(and `Yaml.inferShape`, `Table.inferShape`) returns the declaration
text outside the REPL.

A derived type name never lands on a name the session already
resolves — a builtin (`Secret`, `Yaml`, `Duration`, …) or a type you
declared. A k8s volume's `secret:` sub-object would draft
`type Secret`, and every `secret: Secret` field would then bypass it
for the builtin (the redaction type — `from yaml` refuses to cross
it); the draft parent-prefixes instead (`VolumeSecret`) and prints a
note naming the rename. The `as` name is YOURS, so it is never
renamed: `#infer … as Secret` refuses and asks you to pick another.

A key weir cannot spell as a field name never breaks the draft — and
an object the open-map detection claims never needs one: its keys
are data, not fields. On the RECORD path (mixed value shapes) a
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
