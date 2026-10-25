# Frequently asked questions

Short answers to questions that come up while writing weir and
living in the REPL. Each answer links to the page that covers the
topic in full. The [guide](GUIDE.md) teaches weir from scratch; this
page is for when you know what you want and need the spelling.

## Commands and values

### Why does `ls` print a table instead of running the `ls` program?

`ls` is a weir builtin: it lists the current directory as typed rows
(`name`, `kind`, `path`, …), so you can filter it like any other
value. `^ls` runs the program from PATH, and `Dir.stat` gives the
same rows for another directory.

```weir
ls |> Seq.where (fun f -> f.kind == Directory) |> Seq.iter (fun f -> print f.name)
^ls -a
Dir.stat "/" |> Seq.where (fun f -> f.kind == Directory) |> Seq.iter (fun f -> print f.name)
```

See [How a line decides](GUIDE.md#how-a-line-decides).

### How do I get a command's output as a string?

A command's output is a `seq<string>`, one element per line. End the
command with `| line` when it prints exactly one line, or `| text`
for the whole output as one string. `| line` is a check as well as a
conversion: no output, or more than one line, is an error ("expected
exactly one line of output, got 2"), so a surprise never slips through
as the wrong string.

```weir
let branch = git branch --show-current | line
let log = git log --oneline | text
print $"{branch}: {log |> Str.length} chars"
```

See [Exit codes: from command to value](GUIDE.md#exit-codes-from-command-to-value).

### How do I check whether a command's output contains some text?

Take the output with `| text` and pipe it to `Str.contains`. A bare
`contains` is refused even at the prompt, because both `Seq` and
`Str` have one; write the module you mean.

```weir
let hasOrigin = git remote -v | text |> Str.contains "origin"
print $"{hasOrigin}"
```

See [the short-names question](#ls--map-_name-works-at-the-prompt-why-not-in-my-script-or-initweir).

### How do I run a command in every subdirectory, and ask first?

Loop over `ls` with `for`, and use `within cd` to run the body inside
each directory; you are back where you started afterwards, even if a
command fails. `Self.prompt` asks a question and returns the answer.

```text
for r in ls |> Seq.where (fun f -> f.kind == Directory && not f.hidden) do
    if Self.prompt $"Push {r.name}? (y/n) " == "y" then
        within cd r.name
            git push --set-upstream origin HEAD
```

See [Scoped resources: `within` and `always`](GUIDE.md#scoped-resources-within-and-always).

### How do I keep a loop going when one command fails?

A failing command raises and stops the script. Add `| exitCode` to
get the exit code as an `int` instead (the output still prints), then
decide what to do with it.

```weir
for d in ["a"; "b"] do
    let code = sh -c $"test {d} = b" | exitCode
    if code <> 0 then printerr $"{d}: exit {code}"
```

See [Exit codes: from command to value](GUIDE.md#exit-codes-from-command-to-value).

### How do I write a loop that runs until I stop it?

Use `repeat interval=…`. It runs the block, waits the interval and
runs it again. The first Ctrl+C (or `kill -TERM`) lets the iteration
in hand finish, then runs the code after the loop, and the script
exits 0; a second one stops it at once. A `bool` block ends it when
true:

```weir
repeat interval=10ms
    print "tick"
    true
print "stopped"
```

A `for` over `Seq.initInfinite` runs forever too, but a signal cuts it
mid-iteration. To keep a server running until it is stopped, end its
`within serve` with `until stopped` after the block
([scopes](reference/scopes.md#until-stopped)).

See [`repeat`](reference/loops.md#repeat).

### How do I check that a string is a valid file name?

`Path.isName` checks it and `Path.toName` makes one from any text. A
valid name is one file name that works on Linux, macOS and Windows
alike: not empty, `.` or `..`, no `/`, `\`, control character or
`< > : " | ? *`, no trailing space or dot, not a Windows device name
such as `CON` or `nul.txt`, and at most 255 bytes in UTF-8.

```weir
let title = "Q3: draft/final?"
let file = if Path.isName title then title else Path.toName title
print file
```

`toName` replaces each forbidden character with `_`, drops trailing
spaces and dots, prefixes a device name with `_` and cuts at 255 bytes
without splitting a character; a name that is already valid comes back
unchanged. To join a name you did not choose onto a directory, use
`Path.under`. See `#help Path.toName` at the prompt.

## Functions

### My function ends with a command; why does it return `seq<string>`?

A function returns the value of its last line, and a command there is
a `seq<string>` of its output. End the body with `()` to return
`unit`; the commands above it still run in order and print as usual.
In a module, sign it `let status : unit -> unit`.

```weir
let status () =
    git status --short
    git log --oneline -n 1
    ()

status ()
```

See [The statement rule](reference/statements.md#the-statement-rule).

### How do I fail fast on an Error, like Rust's `?`?

End the expression with `|> orFail`. It gives the value inside an
`Ok` or a `Some`, and stops with an error on `Error` or `None`; add a
message to say what was missing. Errors propagate on their own, so
there is nothing to thread back up: the caller that wants to handle
the failure ends its call with `|> try result` and gets `Ok` or
`Error` with the message.

```weir
let portOf cfg =
    Map.tryGet "port" cfg |> orFail "no port" |> Str.tryToInt |> orFail "port is not a number"

let good = Map.ofPairs [("port", "8080")]
let bad = Map.ofPairs [("port", "http")]

for cfg in [good; bad] do
    match portOf cfg |> try result with
    | Ok p -> print $"port {p}"
    | Error msg -> printerr $"skipped: {msg}"
```

See [Raising](reference/errors.md#raising).

## Names: scripts and the prompt

### `ls |> map _.name` works at the prompt; why not in my script or init.weir?

Short names like `map` and `where` exist only for typing at the
prompt. Scripts, `-e` and the init file follow the script rules, so
there you write `Seq.map`. The qualified name works everywhere.

```weir
ls |> Seq.map _.name |> Seq.iter print
```

```weir-error
ls |> map _.name |> Seq.iter print // 'map' is module-qualified here; use … Seq.map
```

See [The REPL](GUIDE.md#the-repl).

## Types

### Can a `Map` have keys that aren't strings?

Yes. In a program, `Map<K, V>` takes any key type that supports
equality — `int`, a union, a tuple, a record — but not `float`, which
has no equality. Only JSON is limited to string keys: `to json` and
`from json` take a `Map<string, V>`, so turn other keys into strings
with `show` first.

Union keys sort by case name, then payload, unlike F#'s declaration
order: a union value carries only its case name, and name order needs
no hidden state. `Error` sorts before `Ok`.

```weir
let byLength =
    ["a"; "bb"; "cc"]
    |> Seq.groupBy Str.length
    |> Seq.map (fun (n, g) -> (n, g |> Seq.length))
    |> Map.ofPairs

print $"{Map.get 2 byLength}"
byLength |> Map.pairs |> Seq.map (fun (k, v) -> (show k, v)) |> Map.ofPairs |> to json |> Seq.iter print
```

See [`Map`](reference/types.md#map).

### How do I make a type that only holds valid values?

Give a single-case union a `validate` block that checks the payload
and refuses with `fail` (it returns nothing and cannot change the
value; normalise before calling `create` if you need to). Weir
derives `T.create`, which raises with that message when the block
refuses (`|> try result` gives you `Error msg`), and the case stops
constructing — so every value went through the check, including those read from JSON/YAML/TOML,
argv and the environment, which run it too. Patterns still read the
value anywhere.

```weir
type Port = Port of int
    /// 1–65535
    validate p = if p < 1 || p > 65535 then fail $"{p} is not a port"

let ports = [80; 0] |> Seq.map (fun p -> Port.create p |> try result) |> Seq.freeze
ports |> Seq.iter (fun r -> print $"{r}")
```

(prints `Ok (Port 80)`, then `Error "0 is not a port"`.)

See [Validated types](reference/types.md#validated-types).

## Modules and imports

### Can a module have a top-level `let z = ls` or `let v = git --version`?

Yes. Both are lazy: importing runs nothing, and each use runs them
again in the current directory. What a module can't do is run a
command at import, such as `git branch |> Seq.length`; make that a
function. As with any member, a signature line exports it.

```text
module

let z : seq<FileRow>
let z = ls

let v : seq<string>
let v = git --version

let branches : unit -> int
let branches () = git branch |> Seq.length
```

See [What a module is](reference/modules.md#what-a-module-is).

### Why do I have to write `X.hi` for a module my init.weir imports?

That's by design: imported members are always qualified, at the
prompt too, so `X.hi "a"` works and a bare `hi "a"` is an error
naming `X.hi`. To use one name bare, bind it in init.weir.

```text
#init
import "./tools.weir" as X

let hi = X.hi
```

See [The init file](repl.md#the-init-file).

### Can I `import` a module at the prompt or in `weir -e`?

Not today: there it is refused with "import is script-only", because
it needs a file to resolve its path against. Put the import in
init.weir; its members are then available in every session as
`X.member`.

```text
#init
import "./tools.weir" as X
```

See [Script-only](reference/modules.md#script-only).

## The REPL and init.weir

### What can a `#session` value contain?

Any expression, with two limits:

- **No commands.** Nothing in init.weir runs a program while it loads,
  so `"HOST", $(hostname)` is refused. Reading a file is fine; it
  isn't running a program.
- **No names from the same file** (except in `prompt`). Settings are
  read before the file's `let`s are bound, so `"EDITOR", ed` with a
  `let ed = …` in the file has nothing to name. Write the value inline.

```text
#init
#session {
    env = [
        "HOST", File.read "/etc/hostname" |> Seq.exactlyOne
        "EDITOR", "micro"
    ]
}
```

See [The init file](repl.md#the-init-file).

### Can an `#alias` run a pipeline or a chain?

No. An alias is one program plus fixed words, so `|`, `|>`, `&&`,
`||` and `;` in its target are refused. Write a function in init.weir
and call it with `()`.

```text
#init
#alias co = git checkout

let amend () = git add . | and git commit --amend --no-edit
```

See [`#alias`: command-head aliases](repl.md#alias-command-head-aliases).

### How do I see the type of what I typed?

At a terminal the echo leaves types out: `2` echoes `2`, and a
command's lines come without a `: seq<string>` line. `#types on`
adds types for the session, and `"types": "on"` in the config turns
them on at startup.

```text
weir> #types on
types: on
weir> 2
2 : int
```

See [What the echo shows](repl.md#what-the-echo-shows).

### How do I make a multi-line prompt?

The prompt function returns one line. `print` the lines above it from
the same function: they stay on screen while you edit, and the
returned string becomes the prompt line.

```text
#init
let sigil () =
    print $"{pwd}"
    "> "

#session {
    prompt = sigil
}
```

See [`prompt`: your own prompt](repl.md#prompt-your-own-prompt).

### How do I set the terminal window title?

Call `Term.title` from your prompt function in init.weir, and the
title follows every `cd`. With no terminal attached it does nothing.

```text
#init
let sigil () =
    Term.title $"weir: {pwd}"
    "> "

#session {
    prompt = sigil
}
```

See [`prompt`: your own prompt](repl.md#prompt-your-own-prompt).

## Completion

### Does weir complete command arguments, like bash completions?

Yes, for commands that have a signature. `weir add sig git` writes
one for the project (`--global` for every directory), and Tab then
offers git's subcommands and flags, and the values a flag lists.
Without a signature, arguments complete as paths. Values that only
the tool knows, such as branch names, aren't completed.

```text
weir add sig --global git
```

See [Signatures drive Tab completion](tooling.md#signatures-drive-tab-completion).

### Does completion work through an `#alias`?

Yes. An alias completes as what it expands to: with
`#alias co = git checkout` and a signature for git, `co --qu<Tab>`
gives `co --quiet`.

See [Tab completion](repl.md#tab-completion).

### What does Tab offer after `$` in a command?

Your session bindings, and nothing else: with `let tag = "v1"`,
`echo $ta<Tab>` gives `echo $tag`. An argument without `$` completes
paths.

See [Tab completion](repl.md#tab-completion).
