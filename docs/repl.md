# The weir REPL

`weir` with no arguments starts the REPL. Here you can use short
unqualified names like `map` and `where` (scripts need the qualified
names), every value is echoed back, and tab completion and history
work as you'd expect. `Ctrl+C` abandons the current line; `Ctrl+D` or
`#quit` exits. For a tour, see the [guide](GUIDE.md#the-repl); this
page covers the details.

A line starting with `#` is an instruction to the tooling, not part
of the language. Directives in files (`#sig`, `#schema`) are read at
check time; session directives (`#help`, `#quit`) run immediately.
The [reference table](reference/lexical.md#directives) lists where
each directive is used.

## Tab completion

Tab completes the word under the cursor: module members, record
fields, keywords, bindings, and, where a command can go, the programs
you can run. A command can go at the start of a statement *or right
after `=` in a top-level `let`*, so `let svc = kubect<TAB>` completes
just like a bare `kubect<TAB>`. The words after a command complete as
arguments: directory entries, or session bindings after a `$` (see
below).

Coloring follows the same rule. The command word is colored as you
type, so in `let svc = kubectl …` the color of `kubectl` shows
whether it resolves. Session
[aliases](#alias-command-head-aliases) are recognized in both places,
for coloring and for Tab. A command starting with `^` completes from
PATH only, skipping bindings and aliases.

Some behaviours worth knowing:

- **Tab at an empty prompt lists the session directives** (`#help`
  first) rather than everything; `#help` itself lists the modules and
  members. Once you start typing, you get the usual filtered list. Tab
  in an argument position still lists the directory.
- **Record fields complete through a pipe.** A record piped into `_.`
  or a lambda parameter completes its fields, just like a bound `r.`:
  `r |> _.`, `raw |> from yaml Pod |> _.`, `… |> (fun row -> row.`.
  This is where `#infer` helps most: once a record has a type, its
  fields complete wherever the value goes.
- **`#help <name>` completes what it documents.** `#help Pat<TAB>`
  offers every name `#help` can document with that prefix: modules,
  types (including types from `#infer`), and top-level forms.
  `#help Module.<TAB>` completes that module's members. Anything you
  can `#help`, you can complete with Tab.
- **A bound map completes its keys.** A map's keys are data (such as
  `data: seq<string * string>` from `#infer`), so completion reads
  them from the value you already have. Type the opening quote of the
  key after a piped `Map.get`, `Map.tryGet` or `Map.has`, as in
  `d |> Map.tryGet "Cor<TAB>`, and the keys of `d` that match complete
  inside the quotes (you type the closing quote). This works when the
  value on the left is a plain session binding (`it` counts) whose
  value is already computed: a map, or a frozen seq of pairs.
  Completion never evaluates anything, so a pipeline on the left
  (`cm |> from json … |> _.data |> Map.tryGet "`) completes nothing:
  finding its keys would mean running the pipeline. Bind it first
  (`let d = …`) and the keys complete. A lazy seq is never read, for
  the same reason.
- **A `$` splice in a command argument completes session bindings.**
  `az … --location $l<TAB>` offers every binding whose name starts
  with `l`, such as `$location`. An argument without `$` completes
  paths only.
- **A path completes quoted in an expression, and unquoted as a
  command argument.** `File.read ./x<TAB>` gives `File.read "./x"`,
  because a bare path is not a valid weir expression. As a command
  argument (`cat ./x`, `ls ./dir`) the path stays unquoted. If you
  already typed the opening quote (`File.read "./x<TAB>`), the
  completion goes inside it without adding a second quote; a directory
  keeps its trailing `/` inside the quotes.
- **A leading `~` completes to your home directory's literal path.**
  `cat ~/.con<TAB>` becomes `cat /home/you/.config/`, and `cd ~<TAB>`
  becomes `cd "/home/you/"`, so the line shows exactly what will run.

Completion never runs anything; at most it reads a directory or
looks up a cached PATH entry.

## What the echo shows

At a terminal, how a value is echoed depends on its type:

- a seq of records — as a table with a bold header and a dim rule,
  fitted to the terminal width (the widest column is cut to fit)
- a seq of strings — as its lines
- a named function — as a short help entry (below)
- anything else — as a literal

The type is always shown on a line below, with a note when a seq has
not been fully evaluated. `NO_COLOR` turns off the styling. Type
variables in that line are renamed to `'a`, `'b`, …; error messages
use the checker's internal names for them instead.

An expression that evaluates to a **named function** echoes the same
thing `#help` would show:

- a builtin — its qualified signature plus the first line of its doc
  (`find` shows where it lives, `Seq.find`)
- a function you defined in the session — `name : type` plus a dim
  line showing the first line of its definition (for a redefined
  function, the latest one)
- an anonymous or composed function — `<fun> : ty`, since it has no
  name

A `let` binding is echoed the same way: the value's lines (or table)
first, then a `name : type` line with the same note about unevaluated
seqs. That line always comes after the value, so a cut-off value
never looks as if data silently went missing: you see where it was
cut, then the note saying so and how to see the rest (`Seq.freeze`).

At a terminal, that line also says what kind of seq you bound:

```
weir> let pods = kubectl get po -A
…the pods…
pods : seq<string> (command-backed — re-runs on each use)
weir> let snap = kubectl get po -A |> Seq.freeze
…the pods…
snap : seq<string> (frozen)
```

A binding backed by a command that hasn't been frozen re-runs the
command every time you use it, and the note warns you about that up
front (in a script, the checker warns about the same thing when such
a binding is used twice). A frozen binding shows `(frozen)`. A lazy
seq that doesn't run commands gets no note, since there is nothing to
warn about. This note and the truncation note share one pair of
parentheses, and piped output never includes them.

## Seeing all of the output

The echo is only a preview. There are three ways to see a result:

- the **echo**, for a quick look. By default it shows up to 100
  elements of a lazy seq, which is enough for typical command output
  without a `Seq.freeze`. Long strings are cut, and a hint tells you
  the current limit.
- **`|> print`**, which shows everything, one element per line. For a
  seq of anything other than strings, use `|> Seq.map show |> print`.
- a **bare command**, which streams its output live, as the program
  produces it.

`#echo` changes the echo's limit for the session: `#echo 25`, or
`#echo all` for no limit (an infinite seq will then hang). `#echo` on
its own shows the current limit, and `echoElems` in the config sets
the starting value. A frozen seq is always echoed in full; the limit
only applies to lazy ones. Piped output is not affected.

## Help

`#help` lists the directives and the modules, one module per line
with a short description. `#help Seq` lists one module's members the
same way: one per line, each with the first line of its doc, cut to
fit the terminal. `#help Seq.collect` shows one member's full doc.
It is the same doc that editor hover shows, and the one-line
summaries are its first line.

At a terminal, `` `code` `` in the docs is shown colored, without the
backticks. The signature line and the example are colored too: in the
signature the name is bold, types are yellow and punctuation is dim,
and the example is colored the same way as the input you type at the
prompt. When output is piped, or with `NO_COLOR`/`TERM=dumb`, there is
no coloring: the backticks stay, so you can still see where code
starts and ends.

`#find [query]` does a fuzzy search over all of it. When fzf is
installed and you are at a terminal, every module (`Seq — summary`)
and every member (`Seq.map — summary`) is listed in fzf with a
**live preview** of the selected name's full doc. Enter prints the
`#help` output for the selection; Esc returns to the prompt. Without
fzf, or when piped, `#find query` filters the same lines by
case-insensitive substring, and never asks you to install fzf. The
preview comes from running `weir --repl-doc <name>` in the
background, which prints exactly what `#help <name>` would. It covers
builtin docs only and evaluates nothing.

## History

`#history` shows the session's history. It starts with the **file
path**, `history at <path> (N entries)`, so you know where the file
is and can `cat` it directly. `#history` on its own prints every
entry, numbered; `#history 20` shows the last twenty. Each entry is
shown on one line, as in history search: a multi-line entry is joined
with `⏎`, so you can still grep it.

The history file is at `$XDG_STATE_HOME/weir/history` (otherwise
`~/.local/state/weir/history`; `%LOCALAPPDATA%\weir\` on Windows).
It is created with mode `0600`, since a REPL line can contain a
secret. To find that base directory from a script, use
`Path.stateHome ()`.

History keeps each entry once: when you enter a command again, it
moves to the most recent position and the earlier copy is removed. So
Up-arrow, `#history` and `Ctrl+R` each show a command once
(duplicates already in the file are merged when it loads). Turn this
off with `historyDedup = false` in the
[config](tooling.md#configuration).

## The last result: `it`

Every expression and command sets `it` to its result, even when the
result is unit (as in F# Interactive). So the result of the pipeline
you just ran is one word away:

```
weir> kubectl get po -o json |> from json Pods |> _.items
weir> it |> Seq.map _.name
```

A `let` binds only its own name: `let o = 10` leaves `it` alone, just
as `let o = 10;;` does in F# Interactive. Directives never change it,
and in a new session `it` is not yet defined. `it` exists only in the
REPL; scripts and `-e` don't have it, just as they don't have the
short unqualified names. The name comes from F# Interactive and ghci;
`_` wasn't an option, because it already means other things (the
`_.field` shorthand and the `let _ =` discard).

At a terminal, a bare command writes straight to the terminal, so its
colors are kept and weir never sees the output. Its value is
therefore `()`, and that is what `it` is set to.

When such a command exits nonzero, the REPL shows a dim `↳ exit N`
line and the next prompt turns red, instead of printing a red error.
The output has already been shown and the session carries on, much
like `$?` in a shell. The nonzero exit still *raises* an error,
though. In a script it stops the script. Inside a value (`$(cmd)`, a
reifier, a binding) you see the full `error:`, because there the
failure interrupted a computation. To get a nonzero exit as a value
instead, use `cmd | exitCode` or `cmd | complete`.

Using that unit `it` where a value is needed is an ordinary type
error, and the error shows how to capture the command instead, using
the exact command you ran:

```
weir> kubectl get po -A -o yaml
…the pods, live…
: seq<string>
weir> #infer it from yaml as Pods
#infer: the source has type unit; the adapter needs seq<string> (a captured JSON/YAML sample)
to capture: let x = kubectl get po -A -o yaml
```

## `#infer`: draft types from a sample

Exploring an unfamiliar JSON/YAML document (or a kubectl-style
aligned table) normally means writing out its shape by hand. `#infer`
drafts the types for you from a sample:

```
weir> let raw = kubectl get po -o json
weir> #infer raw from json as Pods
defined: Pods, Metadata, Status (3 types)
weir> raw |> from json Pods |> _.items |> Seq.map _.⟨TAB⟩
```

`#infer <source> from <json|jsonl|yaml|table> as <Name>` evaluates the
source once, parses it in the given format, derives a set of named
`type` declarations from it, and adds them to the session as if you
had typed them. After that, `from json Pods` type-checks and field
completion works. If you leave out the source, it defaults to `it`
(`#infer from json as Pods` reads the last result, which is how you
"pipe" into a directive). For a top-level array (or `from jsonl`),
`Name` is the element type, and you read the value as `seq<Name>`.
`from table` drafts the row record from the header and the cells of
each column; a column with empty or `<none>` cells becomes an
`Option`, with a note, and the value reads as `seq<Name>`.

The one-step form drafts the types *and* binds the parsed value:

```
weir> #infer let po = kubectl get po -o json |> from json as _
defined: Type1, Metadata, Status (3 types)
po : Type1 = { items = …; count = 3 }
weir> po.items |> Seq.map _.⟨TAB⟩
```

`#infer let <x> = <source> |> from <fmt> as <Name|_>` runs the source
exactly once and uses that one sample both to infer the types and to
bind the value. Nothing runs twice (no `Seq.freeze` needed), and you
don't need a third statement repeating the name. `as _` names the
top-level type automatically (`Type1`), the way nested types are
always named; it works in the two-step form too. A top-level array is
bound as `seq<Name>`. If the sample's top level is a scalar or a map
with data keys, `#infer` defines what types it can and binds nothing;
the notes show how to read the value.

`#save` writes out the explicit form, the `type` declarations plus
`let x = source |> from <fmt> Name`, never the directive line. The
one-step form is a REPL convenience; the saved file is ordinary weir
that `weir check` accepts. (In a script, `from json as Name` is a
check error that tells you to declare the type, and how to draft it
with `#infer`.)

The result is ordinary `type` declarations that you own and can edit.
Like `weir gen types`, `#infer` generates types once, up front;
nothing is inferred at check time (`check` never evaluates anything, and
`from json` never guesses a shape).

Array elements are merged: the element type has every key seen in any
element, and a key missing from some elements becomes `Option<T>`. So
one sample of a Kubernetes List reveals the optional fields that
differ between its items. Where the sample doesn't settle a question,
`#infer` prints a note rather than guessing:

- an empty array becomes `seq<string>`
- a null field becomes `Option<string>`
- a key whose type differs between elements gets the first element's
  type, and the note asks you to check it

When the format has a published JSON Schema, generate the types from
that instead: [`weir gen types`](tooling.md#types-from-a-schema) reads
a locked schema and knows what no sample can, such as which fields
are required, `additionalProperties`, and the definition names.

An object whose keys are data rather than field names is drafted as a
map, `seq<string * V>`, instead of a record, with a note. `#infer`
treats an object this way when all its values have the same shape,
and either:

- most of its keys don't look like identifiers (Kubernetes
  `labels`/`annotations`, with dots, slashes and dashes; a reserved
  word like `type` still counts as an identifier), or
- its keys differ between elements of the same array (a ConfigMap's
  `data`).

Map keys are data, so no `[<Wire>]` is drafted; read them as pairs
(`cm.data |> Seq.tryFind (fun (k, _) -> k == "Corefile")`). Both
`from json` and `from yaml` support this shape. An object with
identifier keys that are the same in every element, like `metadata`,
stays a record. An empty `{}` is a map with no entries; it is drafted
as `seq<string * string>` with a note (with no values to go on, the
value type defaults to string).

A drafted type name never reuses a name that already exists in the
session, whether a builtin (`Secret`, `Yaml`, `Duration`, …) or a type
you declared. For example, a Kubernetes volume's `secret:` object
would draft `type Secret`, and every `secret: Secret` field would
then refer to the builtin `Secret` type instead (which `from yaml`
refuses to read). So `#infer` prefixes the parent's name instead
(`VolumeSecret`) and prints a note about the rename. The name you give
with `as` is never renamed: `#infer … as Secret` is refused, and asks
you to pick another name.

A key that isn't a valid weir field name never breaks the draft. (An
object drafted as a map doesn't need field names at all, since its
keys are data.) For a record, a key that isn't an identifier, or is a
keyword, gets a derived field name with `[<Wire "the-key">]` on it
(`k8s-app`→`k8sApp`, `in`→`inField`; no keyword can be a field name).
An empty-string key can't be a field name and `[<Wire>]` doesn't
accept it, so it is dropped with a note (reading ignores the extra
key).

If a drafted type still fails to check (say the sample had the same
key twice), `#infer` doesn't just say "a drafted type did not check".
It shows the `line:col` within the drafted type and prints
the offending line with a caret, just like a normal parse or type
error:

```
weir> #infer sample from json as Pods
#infer: a drafted type did not check at line 1, col 1: duplicate field 'a'
  type Pods = {
  ^
```

You can see exactly which generated field is wrong and fix the sample
(or hand-edit once you `#save`).

The same inference is available as a function outside the REPL:
`sample |> Json.inferShape |> print` (and `Yaml.inferShape`,
`Table.inferShape`) returns the declaration text.

## `#save`: distill the session to a script

`#save <path>` writes the session's reusable definitions to a file.
The session is scratch space; the file is what you keep. You use
`#infer` to explore and `#save` to keep what you found.

The file gets:

- the `type` declarations, including the ones `#infer` added
- the named `let` bindings, with their original multi-line source (so
  a heredoc keeps its newlines). A name defined more than once is
  saved in its latest form.

Expressions that were only echoed are left out. Short unqualified
names are replaced with qualified ones (`map` → `Seq.map`,
`startsWith` → `Str.startsWith`), and the result is formatted.

The file always passes `weir check`. Any statement that depends on
something that only exists in the session (such as `let x = it`) is
dropped, and `#save` prints a note saying how many were dropped.

`#save` also expands [command aliases](#alias-command-head-aliases),
since scripts don't have them. Each saved line's command is rewritten
to the real command: `let pods = k get po` is saved as
`let pods = kubectl get po`, and `kb overlays/prod` as
`kustomize build overlays/prod`. Only the command word itself is
rewritten, so a `k` in a string or an argument is left alone. The
saved script contains no aliases and passes `weir check`.

## The prompt and the colors

The prompt turns red after an entry that fails, and returns to normal
after the next one that succeeds. A nonzero exit that you asked for
as a value (`cmd | exitCode`, `| complete`) is not an error, so the
prompt stays normal. weir has no global `$?`; the color only shows
errors.

Input is colored as you type: keywords, strings, comments, numbers and
the command markers. The command word is colored by what it resolves
to: bold for a known binding or builtin, blue for a program found on
PATH, and red if it would fail. A red command word lets you catch a
typo before pressing Enter. `NO_COLOR` is honored, and piped sessions
are always plain text.

## The init file

`init.weir` beside the [config file](tooling.md#configuration)
(`$XDG_CONFIG_HOME/weir/`, else `~/.config/weir/`; `%APPDATA%\weir\`
on Windows) is loaded before the first prompt. Like a module, it may
contain only declarations (`type` and `let`), plus
[`#alias`](#alias-command-head-aliases) lines and one `#session`
block for settings that a declaration can't express.

The file must start with `#init` on its first line; the REPL refuses
to load it otherwise. Any file starting with `#init` is checked as an
init file *wherever it is*. So you can keep it in a dotfiles repo,
with full editor support (`#session` and `#alias` are recognized and
the declarations are checked), and symlink it into place:

```text
ln -s ~/dotfiles/weir/init.weir ~/.config/weir/init.weir
```

The REPL loads only `<configHome>/weir/init.weir`. Which init file
loads never depends on the current directory, so a stray `init.weir`
in a cloned repo is never loaded automatically. An init file can't
be run as a script: `weir init.weir` refuses it.

An example:

```text
#init

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

The `#session` keys:

- `cwd` — the directory to start in, applied before the first prompt
- `env` — a `seq<string * string>` set into the process environment
  once, so `Env.vars`, every started process, and `within env` all
  see it. An entry adds or overrides a variable; it can't unset one
- `logLevel` — the same levels as `WEIR_LOG`, parsed the same way
- `echoCap` — a permanent `#echo` limit; it takes precedence over the
  config file's `echoElems`
- `prompt` — see below

A misspelled key gets a suggestion for the closest one. The values
can't run commands.

A `let pu () = …` is a function with no arguments, not a command
alias. Functions can take parameters and span several lines, but you
have to call them with `()`, and they don't accept command-line
arguments. When you want a short command name that passes its
arguments straight through, like `k get po`, use `#alias` (below).

Loading is all-or-nothing. If the init file has an error, the REPL
prints the error with its location plus `init: not loaded`, and the
session starts without any of it. This is safe because nothing in the
file runs while loading. A successful load prints nothing (and so does
a missing init file); you'll see your names, settings and prompt
working. Only failures are reported. `#help` on a name from the init
file shows its `///` doc.

### `prompt`: your own prompt

`prompt` takes a string, or the name of a `unit -> string` or
`PromptStatus -> string` function declared in the same file.

`PromptStatus` describes how the last entry went:

- `ok` is `true` when it succeeded. A bare command's nonzero exit
  counts as a failure; an exit you asked for as a value, with
  `| exitCode` or `| complete`, is not an error and counts as success.
- `exit` is that bare command's exit code (`Option<int>`; `None` in
  every other case).

So a custom prompt can color itself red or green where the default
prompt would turn red, and can show the code the way `↳ exit N` does.

Write the parameter without a type and read its fields (`st.ok`,
`st.exit`). The init file has no signatures, so the function's type
is inferred from the fields it uses, and it is accepted as long as
`PromptStatus` has every field it reads. One thing to watch for:
`match st.exit with` doesn't type-check here, because the type of
`st.exit` isn't known yet and matching on `Some`/`None` needs it. Use
the `Option` functions instead
(`st.exit |> Option.map show |> Option.defaultValue ""`).

The function may run commands. It is called once each time the REPL
reads an entry (never on every keystroke), after the previous entry
has finished. Because `prompt` refers to your declarations, it is
checked after they are defined.

An example that shows the current directory and git branch:

```text
let sigil () =
    let dir = pwd |> Path.fileName
    let g = git branch --show-current | complete

    let branch =
        if g.exitCode == 0 then
            g.stdout |> Seq.tryHead |> Option.defaultValue ""
        else
            ""

    let tag = if branch == "" then "" else $" ({branch})"
    $"≋ {dir}{tag}> "

#session {
    prompt = sigil
}
```

That shows `≋ weir (main)> ` inside a repo and `≋ tmp> ` outside
one; `| complete` keeps `git` from raising an error outside a repo.

You can use colors in the prompt. Color escape codes (SGR) don't
count toward the prompt's width, and a reset is added at the end so the color doesn't spill
into what you type. Other escape sequences (window title, clipboard,
hyperlinks) are removed. Newlines and other control characters become
spaces, and the continuation prompt is padded to the same width. If
the function raises an error, the REPL falls back to the default
`weir> ` and says so once on stderr; later failures in the same
session are silent.

Two limits:

- A session with redirected input always uses the default prompt, so
  your function doesn't run there.
- The default prompt's red error color ([above](#the-prompt-and-the-colors))
  doesn't apply to a custom prompt; it chooses its own colors.

## `#alias`: command-head aliases

A `#alias` maps a short name to a program plus some fixed leading
arguments. It only applies where a command name goes. Declare aliases
in `init.weir` (the usual place), one per line:

```text
#alias k  = kubectl
#alias kb = kustomize build
```

Now `k get po -o yaml` runs `kubectl get po -o yaml`, and
`kb overlays/prod` runs `kustomize build overlays/prod`: the fixed
arguments go right after the program, followed by yours. An alias is
not a text substitution. Your arguments are passed exactly as usual,
so `k get $x` still passes `$x` as one argument, and a `k` in a
string, a variable or an argument is left alone. Only the command
word itself is replaced.

Because an alias is just a program plus fixed words, not a command
line, weir syntax in it is **not interpreted**: `$(…)` captures,
pipes, and shell `&&`/`||` become literal arguments and are never
evaluated. For a shortcut that needs those (say, cd to the repo root),
write a function with no arguments in `init.weir` instead, and call
it with `()`:

```text
let root () = cd $(git rev-parse --show-toplevel | line)
```

The alias target *may* be a builtin like `cd`: `#alias up = cd ..`
runs the `cd` builtin, not an external `cd`.

Aliases are looked up **before PATH**, and a `^` prefix skips them:
with `#alias ls = ls --color`, `ls x` runs `ls --color x` and `^ls x`
runs the real `ls x`. An alias always points at a program, **never at
another alias**; an alias of an alias is rejected when the init file
loads. The program doesn't need to exist when you define the alias
(as in bash). A malformed `#alias` line is an init error, so nothing
in the init file is loaded.

Aliases exist **only in the REPL**. Scripts and `-e` never load
`init.weir`, so an alias name there is just an unknown command.
`#save` [expands](#save-distill-the-session-to-a-script) aliases, so a
saved script contains none.

You can also type `#alias` at the prompt (`#alias` on its own lists
all aliases), but `init.weir` is the usual place for them.

## Multi-line editing

Enter submits when the statement is complete, and starts a new line
when it is not. weir uses its own parser to decide, so after
`match x with` you get a new line, while `1 + 1` is submitted. Up and
Down move between lines, and a recalled history entry comes back
whole: a three-line match comes back as three lines that you can
edit.

The key bindings (they can't be customized):

| key | in the buffer |
|---|---|
| <kbd>Enter</kbd> | submit if complete, otherwise start a new line; on an empty last line, submit anyway (a way out of an unfinished statement: the error is shown and your input is kept) |
| <kbd>Alt+Enter</kbd> / <kbd>Ctrl+J</kbd> | insert a newline (for formatting; the entry is still one statement). <kbd>Shift+Enter</kbd> can't be used, because terminals don't distinguish it from <kbd>Enter</kbd>. Windows Terminal uses left-<kbd>Alt+Enter</kbd> for fullscreen: use <kbd>Ctrl+J</kbd> or right-Alt there |
| <kbd>Up</kbd> / <kbd>Down</kbd> | move between lines; <kbd>Up</kbd> on the first line recalls history |
| <kbd>Ctrl+R</kbd> | history search (uses fzf when installed; each entry is shown on one line, joined with ⏎) |
| <kbd>Ctrl+W</kbd> / <kbd>Ctrl+U</kbd> / <kbd>Ctrl+K</kbd> | cut the previous word / to the start of the line / to the end of the line, saving it for <kbd>Ctrl+Y</kbd> |
| <kbd>Ctrl+Y</kbd> | paste the last cut text at the cursor. Cut text is kept for the whole session, so you can cut on one line and paste into a later one. (To copy from *elsewhere*, use your terminal's mouse selection; weir does not read the OS clipboard.) |
| <kbd>Esc</kbd> / <kbd>Ctrl+C</kbd> | abandon the whole buffer |
| <kbd>Ctrl+D</kbd> | EOF on an empty buffer; delete/join otherwise |

A session with redirected input (`printf '…' | weir`) has no editor,
but it still groups multi-line statements the way a script does. It
keeps reading lines while the statement is unfinished (a heredoc
body, a multi-line `type`, an indented `if`/`match` block, a pipeline
continued with `|>` lines), so a pasted or scripted block runs as one
statement. Input with one statement per line works as usual, and a
directive is always a single line.

## weir and fzf

fzf is always optional: everything that uses it has a built-in
fallback, and weir never tells you to install it. When it is on
PATH:

- **<kbd>Ctrl+R</kbd>** history search runs through fzf (fallback: a
  minimal reverse substring search).
- **`#find`** is fuzzy help search with a live doc preview
  (fallback: a substring filter over the same candidate lines).
- **fzf and similar tools work in ordinary pipelines.** An
  interactive picker draws on /dev/tty while its input and output are
  piped, so `git branch | fzf` just works. When you need both the
  selection and the exit code (for a cancel), use `cmd | complete`
  ([guide](GUIDE.md#exit-codes-from-command-to-value)).
- **`finderFlags`** in the [config](tooling.md#configuration) sets the
  fzf options (`--height 40% --reverse` by default) for both uses.

One catch is handled for you: characters common in weir (`^`, `|`,
`$`, `!`) are *operators* in fzf's extended search, so weir passes
`--no-extended` first, for plain fuzzy matching over code and names.
fzf uses the last flag given, so `finderFlags = ["--extended"]` turns
extended search back on.
