# The weir guide

Weir is a typed shell-scripting language. You write F#-style
expressions and run real commands, and a type checker reads the whole
script before anything runs.

The examples in this guide are tested. Every `weir` block runs as
shown, and every block marked as an error really fails. The only
exceptions are a few demo blocks that need a live server or a real
token.

## Why weir

Five reasons, most important first:

1. **The whole script is checked before the first line runs.** A
   typo, a wrong field name, an ignored value, a missing match case:
   each one stops the script with `file:line:col` and a hint, before
   anything has happened. Bash tells you about your mistake halfway
   through making it.
2. **Data coming in is typed.** Pipe command output through
   `|> from json T` and you get a record with the fields you declared,
   not a blob of text. There are also:
   - `from jsonl`, `from yaml`, `from xml` and `from table` for other
     formats
   - `Args.load` for command-line arguments, with `--help` generated
     for you
   - `Env.load` for environment variables
   - the `Regex` match pattern for anything line-based

   YAML you haven't declared a type for can still be edited by
   structure (`Yaml.parse` and a `yaml patch` block) rather than with
   sed.
3. **Side effects are kept under control.** There is no `&`: a
   background process always belongs to a scope. When a `within proc`
   block exits, for any reason, its process and all its children are
   killed and cleaned up. `within tmp`, `cd`, `env` and `lock` clean up
   the same way, even when an error is raised. Values you put into a
   command are passed as whole arguments, never pasted in as text, so
   command injection can't happen. A `Secret` won't print, interpolate
   or serialize; `Secret.reveal` is the only way to get at it.
4. **You can ask what a script does before you run it.**
   `weir check --can` lists every command, file path, URL and
   environment variable a script *can* touch. It can work this out
   without running anything because command names are written
   literally; a name computed at runtime (`^$tool`) is reported as opaque. Interpreters like `sh -c` show up as opaque, with a clear
   warning. And `pure` marks a block that the checker guarantees has
   no side effects.
5. **It starts in about 6ms** for a one-line expression. It's a single
   static binary, so it's fine in a shebang.

## Running weir

- `weir` starts the interactive REPL. See [The REPL](#the-repl) at
  the end of this guide.
- `weir -e '1 + 2'` runs a small program and prints the value of its
  last statement, which must be an expression. Newlines separate
  statements, as in a file. A declaration on its own is rejected,
  since there would be nothing to show. The same strict rules as
  scripts apply.
- `weir script.weir args...` runs a script, and
  `#!/usr/bin/env weir` works as a shebang. In scripts, library calls
  always name their module: `Seq.map`, `Str.trim`,
  `Option.defaultValue`, `File.read`.
- `weir check script.weir` reports every problem, with location and
  error code, without running anything. Add `--json` for tools and
  agents. A command missing from PATH is only a warning here (running
  the script treats it as an error), so you can still edit scripts
  for tools you haven't installed.
- `weir fmt script.weir` formats a script (`--check` for CI).
- `weir lsp` starts the language server (see [editors.md](editors.md)).

## First script

Save a file and run it with `weir file.weir`, or add
`#!/usr/bin/env weir` to make it a program. Three kinds of line cover
most scripts:

```weir
echo checking...

let files = git ls-files
print $"{files |> Seq.length} tracked file(s)"
```

A command on its own line streams its output, as in any shell, so
`echo` writes straight to the terminal. Put `let` in front of a
command and its output is captured instead: nothing is printed, and
`files` is a `seq<string>` with one element per line. Anything that
isn't a command must be used, either bound with `let` or printed. A
value you compute and then ignore is an error, not silent output:

```weir-error
ls |> Seq.length // computes an int and discards it — bind it, or pipe it to print
```

The whole file is checked before any of it runs, and
`weir check file.weir` shows you every problem at once without
running anything.

`print` takes strings, ints, floats, bools, or a `seq<string>`, which
it prints one element per line (so `weir script | grep x` works as
you'd expect). For anything else, use an interpolated string: a
`{...}` hole can show any value that supports `Show`, records
included:

```weir
let row = ls |> Seq.head
print $"{row}"
```

`show` gives you the same text as a plain string. Use it where a
hole won't fit: passing a function directly (`Seq.map show`), or
with a Secret (`show` prints a mask, while interpolation is an error).

## Comments

`//` starts a comment that runs to the end of the line. It can fill
the whole line or follow code, including on command lines:

```weir
let retries = 3 // why: registry flakes under load
echo retrying $retries times // trailing works on command lines too
```

`///` is a doc comment. It documents the declaration directly below
it, and your editor shows it on hover and in completion. It works on
let bindings, `type` declarations, record fields and union cases.
A few details:

- A blank line between the doc and the declaration disconnects them.
- An attribute line doesn't, so `///` can go above or below a
  field's `[<...>]`.
- The doc must be indented to match its declaration; `weir fmt`
  keeps it there.

On a field read by `Args.load`, the first line of the doc also
becomes that flag's `--help` text (hover still shows the whole doc).
You write it once, so the help and the hover text always agree. Here
the `///` lines come back out as help:

```weir
let src = <<<
    type Cli = {
        /// run without uploading
        dryRun: bool

        /// where the bundle goes
        target: string
    }

    let cli = Args.load Cli
    print $"dry={cli.dryRun} target={cli.target}"

src |> File.write "tool.weir"
weir tool.weir --help
```

The finer points, such as why `http://a` isn't cut off as a comment
in a command argument, or why a comment can't go inside an
interpolation hole, are in
[Lexical](reference/lexical.md#comments).

## Statement layout

A statement starts at column 0. Indented lines below it continue it,
and the next line at column 0 starts a new one. Blank lines and
comment lines don't count, so you can space out blocks freely. An
indented `let` ends at the next line with the same indent, as in F#.
The same rule applies everywhere, commands included; the details are
in [Statements](reference/statements.md).

That means a long command continues onto the next line by
indenting it. There is no `\` line continuation in weir, and a lone
`\` gets an error that explains this:

```weir
printf "%s %s\n" first
    second
```

## Values and pipelines

Sequences are lazy: a pipeline only computes the elements it
actually needs. Ranges like `[1..10]` are lazy too, while a list
literal like `[a; b; c]` is an ordinary value, built up front.

```weir
let big =
    ls
    |> Seq.where (fun f -> f.bytes > 1KiB)
    |> Seq.map (_.name >> Path.stem)

big |> print

[1..10] |> Seq.where (fun n -> n > 7) |> Seq.iter (fun n -> print $"{n}")
```

`_.name` is shorthand for `fun x -> x.name`. Combine it with a
`Path` helper (`extension`, `fileName`, `stem`, `dir`, `combine`) to
work with file names inside a pipeline.

The `Seq` module has the F# functions you'd expect, and they're lazy
by default:

- transform: `map`, `where`, `collect`, `fold`, `reduce`, `scan`
- order: `sort`, `sortBy`, `max`, `minBy`
- group: `groupBy`, `countBy`, `distinctBy`
- slice and search: `indexed`, `chunkBySize`, `takeWhile`, `find`,
  `pick`, and their `try` variants

One difference from F#: `Seq.sum` only works on ints. `Float`,
`Size` and `Duration` each have their own `sum` and `average`
(`ls |> Seq.map _.bytes |> Size.sum`).

### Indexing and slicing

Use brackets to get a single element: `xs[0]` is the same as
`Seq.item 0 xs`, and raises an error for a negative or out-of-range
index. The bracket has to touch the value (`xs[0]`). With a space,
`f [0]` means calling `f` with the list `[0]`, as in F#. `_[0]` is
shorthand for `fun x -> x[0]`.

Put a range in the brackets to take a slice. Slices **include both
ends, and never fail**: if the range runs past the end, it's cut
short, and if it's empty or backwards you get an empty result:

```weir
let xs = [10; 20; 30; 40; 50]
print (show (xs[1..3]))   // [20; 30; 40] — inclusive
print (show (xs[..2]))    // [10; 20; 30] — open start
print (show (xs[3..]))    // [40; 50] — open end
print (show (xs[3..100])) // [40; 50] — clamped, no raise
print ("weir"[1..2])      // ei — strings slice the same way
print (show (xs[^1]))     // 50 — from the end: ^n = length − n
print (show (xs[..^2]))   // [10; 20; 30; 40] — all but the last
print (show (xs[^2..]))   // [40; 50] — the last two
```

You can slice **sequences, strings and `Bytes`**, and the result has
the same type you started with. A sequence slice stays lazy, so a
bounded slice of an infinite sequence still finishes. `Bytes` can be
sliced (`b[1..3]`), but you can't index a single byte with `b[i]`;
use `Bytes.sub start len` instead.

To count from the end, write `^n`, as in F#. It means length − n, so
`xs[^1]` is the last element. It works in slices too: `xs[^3..^1]`
counts from the end on both sides. Because `^n` needs the length, a
slice that counts from the end has to read the whole sequence (like
`Seq.last` does). Slices that only count from the start stay lazy.

Two things that don't exist: `xs.[i]` (weir indexes without the
dot), and bracket indexing on a `Map` (use `Map.get k m`).

## Records, unions, and tuples

Tuples are for short-lived pairs. You write them as `(a, b)`, their
type as `int * string`, match them with `| (x, y) ->`, and unpack
them with `let host, port = target`. Once the parts need names,
declare a record instead: `p.Host` tells the reader what it is,
where `let (h, _) = p` makes them work it out. Records and unions
are declared with a fixed set of fields, and a union case that
carries several values takes a tuple:

```weir
type Verdict =
    | Pass of int
    | Fail

type Score = { Name: string; Points: int }

let s = {
    Name = "a"
    Points = 12
}

let s2 = { s with Points = 13 }

let v = if s2.Points > 10 then Pass s2.Points else Fail

print $"{v}"
```

To change a record, copy it rather than writing it out again:
`{ s with Points = 13 }` is a copy of `s` with the named fields
changed, and `s` itself is left alone. You can change several fields
(separate them with `;`) or a nested one (`{ o with I.X = v }`), but
you can't add fields. A helper like
`let bump r = { r with N = r.N + 1 }` works on any record that has
the field.

```weir-error
type P = { N: int }
let p = { N = 1 }
let q = { p with Extra = 2 } // record update cannot add fields
print $"{q}"
```

Record fields can have attributes, written as in F#:

```weir
type Cli = {
    [<Short "C">]
    /// clean first
    Clean: bool
    Target: string
}

let cli = { Clean = true; Target = "prod" }
print cli.Target
```

Attributes only matter to the checker; at runtime they're gone, and
`cli` above is just a plain `Cli`. The set of attributes is fixed;
the ones for `Args.load` are `Short`, `NoShort` and `Default`. A typo like `[<Shrot "c">]` is an error
with a "did you mean" hint. They're used by `Args.load`, which turns
the declaration above into a `-C` flag; see
[The script's own front door](#the-scripts-own-front-door). Help text
isn't an attribute: the first line of the `///` doc becomes the
`--help` text, as that section explains.

## Functions

`let f x y = ...` defines a curried function, the same as nested
`fun x -> fun y -> ...`. Functions are generic where they can be:
`id` below works on any type.

```weir
let double n = n * 2
let id x = x
let quad = double >> double

print $"{double 21} and {id "strings too"} and {quad 10}"
```

For running totals, use a fold:
`xs |> Seq.fold (fun state x -> state + x) 0`. The function gets the
state first. To keep several totals at once, use a record as the
state and copy it each step:
`Seq.fold (fun c x -> { c with Total = c.Total + x }) initial`.
Lambdas can take several parameters (`fun acc x ->`), just like
`let f a b =`.

A lambda with several statements reads best over several lines. End
a line with `(fun ... ->` and the indented lines below are its body.
Any statements can go there, including nested `let`s and commands.
The closing `)` can go at the end of the last body line or on its
own line. You can also put everything on one line with `;` between
the statements.

```weir
let sizes =
    [("a", 1); ("b", 2)]
    |> Seq.map (fun (name, n) ->
        let doubled = n * 2
        $"{name}={doubled}"
    )

sizes |> Seq.iter print
```

`>>` composes functions left to right, so you can pass a chain of
steps as one function: `Seq.map (Str.trim >> Str.toLower)`. One
precedence rule to know, the same as in F#: `|>` and `>>` have equal
precedence, so `xs |> f >> g` means `(xs |> f) >> g`. Put the
composition in parentheses: `xs |> (f >> g)`. The full operator
table is in [Lexical](reference/lexical.md#operators).

Equality, printing and sorting work on any type that supports them,
and weir works out what each function needs. So the usual small
helpers just work, and you get an error where you call one with a
type that doesn't fit:

```weir
let same x y = x == y

print $"{same 1 1} {same "a" "b"}"
```

Names you bind start with a lowercase letter; uppercase is for
types, modules and union cases. A parameter that is a function needs
no annotation; weir infers it from how you call it:

```weir
let apply f x = f x
print (apply (fun n -> n + 1) 1)
```

There's one case where inference gives up: `+` between two values of
unknown type, since it could be int or string addition. Pin one side
down, for example `x + 0`.

A `()` parameter makes a function that takes no input:
`let cleanup () = ...` runs when you call `cleanup ()`, and
`cleanup 5` is a type error:

```weir-error
let cleanup () = print "done"
cleanup 5 // expected unit, got int
```

## Branching

`if` is an expression. You can leave out `else` only when the `then`
branch returns unit. `elif` is short for `else if`.

`match` supports:

- literal patterns, like `| 0 ->` and `| "yes" ->`
- bool patterns
- union case patterns
- record patterns
- `when` guards

A match that doesn't cover every case is an error, not a warning.
Int and string literals can never cover every case on their own, so
end with `_` or a variable:

```weir-error
let t =
    match 1 with
    | 0 -> "zero"
    | 1 -> "one" // literals never complete a match: add a _ or var arm
print t
```

The opposite mistake is an error too: a case that can never be
reached because a catch-all above it matches everything. Watch out
for this one, because a lowercase name in a pattern is a new
variable. If you mistype a union case in lowercase, it quietly
matches everything. Weir stops you with a "did you mean" instead.

```weir-error
type V =
    | Pass
    | Failing
match Pass with
| pass -> print "ok" // 'pass' binds — did you mean 'Pass'? the next arm is unreachable
| Failing -> print "no"
```

A match case can run commands directly, with no special syntax. When
the match is a statement on its own, the chosen case's output
streams to the terminal. If you put the match on the right of a
`let`, its output is captured instead:

```weir
let mode = "build"

match mode with
| "build" ->
    sh -c "echo compiling"
    sh -c "echo linking"
| "test" -> sh -c "echo testing"
| _ -> print $"unknown: {mode}"
```

Each case's commands run up to the next `| pattern ->`. So if a
command argument looks like `x ->`, quote it so it stays an
argument.

Record patterns pull fields out by name. You can use them in a
`match` case, a `let`, a `for` loop variable, or a function
parameter. A parameter needs no parentheses:
`let label { names = n } = n` accepts any record that has a `names`
field. Field names keep their declared case, the variables are
lowercase, and there's no shorthand: write `{ names = n }`, not
`{ names }`. A field pattern can also hold a literal, so the case
only matches some records. That lets you filter and unpack in one
step:

```weir
type Container = { State: string; Names: string }

let running =
    [{ State = "running"; Names = "api" }; { State = "exited"; Names = "old" }]
    |> Seq.choose (fun c ->
        match c with
        | { State = "running"; Names = n } -> Some n
        | _ -> None)

running |> Seq.iter print
```

Like literals, a record pattern containing a literal can't cover
every case on its own, so end with `_` or a variable:

```weir-error
type St = { state: string }
match { state = "up" } with
| { state = "up" } -> print "x" // a refutable record arm needs a catch-all below it
```

```weir
let n = [1; 2; 3] |> Seq.length

if n > 2 then print "big"

let tier =
    match n with
    | 0 -> "empty"
    | x when x > 100 -> "huge"
    | x when x > 2 -> "medium"
    | _ -> "small"

print tier
```

Blocks work as in F#: a line at the same indent as an `if` or
`match` comes after it; it isn't part of it. So an early-exit check
before a block's result does what it looks like:

```weir
type Target = { Name: string }

let target =
    let stack = "web"
    if stack == "" then fail "usage"
    { Name = stack }

print target.Name
```

## Commands and processes

### How a line decides

Each line is either an expression or a command, and the first word
decides which:

- If it's a name you've defined, or a builtin, the line is an
  expression: a normal function call.
- Otherwise, weir runs the external program with that name.

Builtins win over programs on PATH with the same name. Write `^ls`
to run the real `ls` program.

```weir
let greet name = print $"hi {name}"
greet "io"
echo hi io
```

On a command line, every word is passed to the program exactly as
written. Nothing is expanded and nothing is split on spaces. You
drop back into weir values only with `$name` or `(expr)`. Everywhere
else is expression code: the right side of a `let`, interpolation
holes, and inside `$()`. A single word can't mix the two (gluing
them together is an error), and the
[editor colors](#what-the-editor-colors-mean) show you exactly
where each part is.

**Which pipe: it depends on what's on the right.** `|` sends output
to a program (`git log | grep x`). `|>` passes a value to a function
(`git log |> Seq.head`). Using the wrong one is an error that tells
you to use the other. The operator table is in
[Lexical](reference/lexical.md#operators), and the full rules for
command lines are in [Commands](reference/commands.md).

You can put a command on the right of any `let`, at the top level or
inside a function: `let tree = git rev-parse HEAD |> Seq.exactlyOne`.
A function's parameters can be spliced into the command like any
other value: `let commitOf r = git rev-parse $r |> Seq.exactlyOne`.
Inside a function, its parameters take priority over programs on
PATH, so `let f x = x` always returns `x`, whatever is installed.

If you expect exactly one line, say so with `Seq.exactlyOne`.
`Seq.head` takes the first line and silently ignores any others,
which hides output you didn't expect; keep it for when you really
want the first of many. And if one line is all the command is for,
use the `| line` reifier: `let tree = git rev-parse HEAD | line`
gives you a `string`. (A reifier is a `|` stage that turns a
command's run into a value; see
[Exit codes](#exit-codes-from-command-to-value).)

Weir has no `!` for negation; write `not`. To use a command inside
an expression, wrap it in `$(...)`, which gives you its output. To
just run a command, you need nothing extra: a command is an ordinary
statement, at the top level or inside any block. When the whole
right side of a `let` is a command, use the plain `let` form. Use
`$()` when the command is only part of an expression, such as inside
a record, a hole, or another splice:

```weir
let ready = 1 > 0

if ready then
    sh -c "echo preparing"
    sh -c "echo prepared"
    print "mixed with expressions"

let latest = git log -1 "--format=%h" |> Seq.exactlyOne
print $"at {latest}"

let tagged = $"at {$(git log -1 "--format=%h") |> Seq.exactlyOne}"
print tagged
```

To choose between two tools, branch over the whole command line:
`if hot then rg pat else grep pat`. And don't bind an `if` that only
runs commands to a `let`. It runs right away and the `let` just holds
unit; a plain `if` statement says what you mean.

Sometimes the program name is only known at runtime: a plugin's
callback script, a tool picked from a lookup table, or dispatch like
asdf's `exec`. Write `^$tool` to run the program named by a value.
The value is a single program, and the arguments stay separate
arguments. In shell you'd end up writing `sh -c "$path …"`, with the
injection risk that brings; in weir you never need to:

```weir
let plugin = "sh"
let arg = "two words"
^$plugin -c "printf 'dispatched %s\n' \"$1\"" cb $arg
```

The value after `^$` must be a `string`. A captured `seq<string>` is
an error that tells you to take one line first:
`let tool = cmd | line`, then `^$tool`. The program is looked up when
the line runs, so `weir check` doesn't warn about it. If it's
missing, you get a runtime error at that line that names the value.

To pass a value as an argument, write `$name` or `(expr)`. A value
always becomes exactly one argument and is never split on spaces,
which is why there's no injection to defend against. Arguments also
never join together: `$root/*` and `--flag="value"` are errors,
because the two halves would otherwise end up as separate arguments.
To build one word from several parts, use interpolation:

```weir-error
let root = "build"
rm -rf $root/* // argv words do not concatenate — write $"{root}/*"
```

Things weir command lines don't do:

- No glob expansion. Use the `Path.glob` function.
- No `&&`. Write two statements, or chain with
  [`| and` / `| or`](#exit-codes-from-command-to-value).
- No `$VAR` expansion. Splice weir values instead.
- No `$HOME` expansion. But `~` typed directly is your home
  directory: an unquoted word that is `~` or starts with `~/` is
  expanded when the line runs (`cat ~/.bashrc`). Quoted strings and
  spliced values are never expanded, so data is never treated as
  syntax. In an expression, build the path with `Path.home ()`:
  `File.read $"{Path.home ()}/.bashrc"`. `Path.configHome`,
  `Path.stateHome` and `Path.cacheHome` give the XDG directories. All
  four are pure `unit -> string` functions that use the platform's
  native locations.
- No redirects. `>` and `>>` are passed to the program as plain
  arguments, with a warning telling you what to use instead
  (`cmd |> File.write "out.txt"`, or `File.append`).

When you need bash behavior, run bash: `sh -c "the bash line"`.

```weir
// redirection: a function on the right takes |> (the pipe rule)
within tmp d
    echo redirected |> File.write $"{d}/out.txt"
    File.read $"{d}/out.txt" |> Seq.iter print
```

Watch out for one trap with `sh -c`: inside the quoted line, `$w` is
a shell variable, not your weir value. Weir passes the string through
unchanged (a string means the same thing everywhere), so sh expands
its own `w`, which is usually empty. You get a wrong answer and no
error. To put a weir value into a bash line, interpolate it before sh
sees it: `sh -c $"echo got-{w}"`. And if you don't actually need
bash, a plain argument (`echo $w`) is what you wanted.

```weir
let marker = "guide"
echo tagged $marker (40 + 2)
sh -c "echo one && echo two"
sh -c $"echo interpolated-{marker}"
```

To run something once per item, use `for … do`, the familiar shell
loop, with types. A command in the body streams its output, and a
failing command raises an error at that iteration, which stops
the script unless something catches it. Commands work as
statements inside any block, so multi-line bodies just work: two git
lines under an `if`, `fun f -> git add $f` inside `Seq.iter`, or a
fetch line before the value of a `let` block. You can splice the
loop variable like any other value.

To transform values, keep using pipelines (`|> Seq.map …`); `for` is
for doing something once per item. They're the same thing
underneath: `for` is `Seq.iter`.

```weir
for greeting in ["hello"; "again"] do
    sh -c $"echo {greeting}"

let squares = [for x in [1..5] -> x * x]
squares |> Seq.map show |> print
```

For YAML, write a `yaml` block: paste a manifest and replace values
with splices. The YAML structure is checked before the script runs,
and each spliced value goes in as a YAML value, never as raw text:

```weir
let pod name pairs = yaml
    kind: Pod
    metadata:
        name: $name
        labels:
            for (k, v) in pairs
                $k: $v

pod "web" [("app", "web")] |> to yaml |> print
```

For any other block of text, such as a config snippet, an embedded
script or just some lines, use a `<<<` heredoc. Everything in the
indented block below the marker is taken literally, `$` and `{`
included. Blank lines inside the block and relative indentation are
kept; blank lines at the end are dropped. The value is a
`seq<string>` with one element per line, ready for `File.write`, a
pipe, or the `Seq` module.

`$<<<` is the same thing with interpolation, using the same rules as
interpolated strings:

- `{expr}` inserts a value.
- `{{` and `}}` are literal braces.
- `$` is still just a character, so shell text passes through
  untouched.

The marker is a symbol rather than a keyword, so it doesn't take any
name away from you, and it can't be mistaken for a splice. It can go
at the end of a `let` line, or on its own indented line below. The
block is an ordinary `seq<string>`, so you can pipe it or bind it
like any value. A `|>` on the line right after the block applies to
the whole block:

```weir
let s = <<<
    host db.example
    retries 3
s |> File.write "conf.txt"

<<<
    one
    two
|> Seq.length |> print
```

```weir
let host = "db.example"
let conf = $<<<
    server {host}
    retries {2 + 1}
    literal $HOME and {{braces}}

conf |> File.write "app.conf"
File.read "app.conf" |> Seq.iter print
```

The third form, `$$<<<`, is for text full of braces, like JSON or
config files. Here `$name` and `${expr}` insert values, `$$` is a
literal `$`, and braces and quotes are left alone. So you can paste
a JSON blob and add values without doubling any braces. Values are
inserted as plain text, as in any text template, so a value
containing a `"` or a newline can break the output. Use `to json`
when you can't trust the value:

```weir
let host = "srv.example"
let port = 5432
$$<<<
    { "server": "$host", "port": ${port}, "lit": "$$" }
|> File.write "srv.json"
File.read "srv.json" |> Seq.iter print
```

### Scoped resources: `within` and `always`

You don't have to clean up scratch directories yourself.
`within tmp <name>` creates a fresh directory, names it for the
block, and removes it however the block exits, including when an
error is raised (the case that usually gets forgotten). The block is
an ordinary block: commands run, and the last expression is its
value.

```weir
let digest = within tmp dir
    ["payload"] |> File.write $"{dir}/f.txt"
    Str.sha256 (File.read $"{dir}/f.txt" |> Str.join "-")
print digest[..11]
```

`within tmp d` also works as a plain statement whose body just runs
commands; the result is unit and is dropped as usual. The other
kinds take an argument rather than giving you one:

- `within cd "build"` runs its block in that directory and changes
  back however the block exits. If the directory doesn't exist,
  you get an error naming its absolute path before the block runs.
- `within env vars` adds environment variables for every command the
  block runs. Weir's own environment isn't changed. Nested `within env`
  blocks combine, and the inner one wins when both set the same
  variable.

```weir
let vars = [Env.pair "GIT_AUTHOR_NAME" "weir-bot"]
within env vars
    sh -c "echo committing as $GIT_AUTHOR_NAME"
```

Two more kinds round out the set.

A plain `within` holds nothing. It's just a body plus an `always`
block that runs however the body exits: normally, by an error, by
`exit n`, or on SIGINT/SIGTERM. Only `kill -9` skips it. If both the
body and the cleanup fail, you get the body's error, and the
cleanup's failure is printed to stderr with a marker. Cleanup of any
enclosing scopes still runs.

`within lock "path"` holds an advisory file lock for the block:

- By default it waits until the lock is free. With `timeout=30s` it
  raises an error if it can't get the lock in time.
- It works across processes and across parallel `pmap` workers.
- The operating system releases it if the process dies, even with
  `kill -9`.

```weir
within tmp d
    within lock $"{d}/demo.lock" timeout=10s
        within
            print "one holder at a time"
        always
            print "released either way"
```

Every kind of `within` can take an `always` block. It runs while the
resource is still there: the directory still exists, you still hold
the lock, the process is still alive. The resource is released after
it:

```weir
within tmp d
    ["report"] |> File.write $"{d}/report.txt"
always
    cp $"{d}/report.txt" report.txt
```

Here they all are together (`within proc`, for background
processes, is covered under Parallelism):

| form | holds | on every exit |
|---|---|---|
| `within tmp d` | a fresh directory | removes it |
| `within cd "path"` | the working directory | restores it |
| `within env vars` | an env overlay for child spawns | drops it |
| `within` … `always` | nothing; just a body plus cleanup | runs the `always` block |
| `within lock "path"` | an advisory file lock | releases it (the OS does this even on `kill -9`) |
| `within proc h = cmd` | a background process | kills and reaps its tree |
| `within serve s = cfg handler` | an HTTP listener | closes the socket, freeing the port |

To work with a scratch tree of files, use `Dir.create` to make
directories, `Path.glob` to find files, and `Dir.deleteAll` to
remove a tree (its name makes clear it's destructive). Do it all
inside `within tmp`, which doesn't mind if the block already deleted
its own directory:

```weir
within tmp d
    Dir.create $"{d}/build/out"
    ["artifact"] |> File.write $"{d}/build/out/a.txt"
    print $"{Path.glob $"{d}/**/*.txt" |> Seq.length} artifact(s)"
```

Copy and move take the source first, then the destination, and fail
if the destination already exists. To overwrite, `File.delete` it
first. `Dir.create` is the exception: if the directory already
exists, that's fine, since that's what you asked for.

Kubernetes Secret data needs base64. `Str.toBase64` encodes the
string's UTF-8 bytes as a single line (no 76-column MIME wrapping,
so no `-w0` needed), so a token can go straight into the template:

```weir
let tok = Str.toBase64 "s3cr3t-token"
let secret = yaml
    apiVersion: v1
    kind: Secret
    metadata:
        name: api-token
    data:
        token: $tok
secret |> to yaml |> print
```

`Str.fromBase64` raises an error on invalid base64, and also on valid
base64 that doesn't decode to text (a PNG's bytes aren't a string,
and you'd rather hear about it than get garbage).
`Str.tryFromBase64` returns an `Option` instead, for input from an
API or anyone you don't trust. `Str.sha256` hashes the UTF-8 bytes
and returns lowercase hex, the same as `sha256sum`.

A ConfigMap needs YAML block scalars. After a `key: |` (or `|-`)
header, the indented content is taken literally: `$VAR` and `for`
lines inside it are just text. That matters because embedded scripts
are full of `$`, and quietly substituting into them would break
them. If you want templated content, build the string first and
splice it in as a whole value. `|` means the string ends with one
newline, and `|-` means it has none. This works both ways: a spliced
string with several lines is written out as a block scalar
automatically, using `|` or `|-` according to whether it ends with a
newline:

```weir
let hook = $"#!/bin/sh\necho deploying web\n"
let cm = yaml
    kind: ConfigMap
    data:
        static.sh: |
            #!/bin/sh
            echo $HOME stays literal

            for f in *; do echo $f; done
        hook.sh: $hook

cm |> to yaml |> print
```

A `yaml` block can name a JSON schema that you've added to the
project (`yaml schema=k8s-service`). The checker then validates the
template's structure before the script runs. It can only check what
it can see, so content generated by `for` lines isn't checked.
Adding schemas (`weir add schema`), the lock file (`.weir/lock.json`), and exactly what
is and isn't checked are covered in
[schemas.md](tooling.md#yaml-schemas).

A command that exits nonzero raises an error when its output is
read. To look at the exit code instead, turn the run into a value
with a reifier; they're covered, with the full table,
[two sections down](#exit-codes-from-command-to-value).

## Exit codes: from command to value

A **reifier** is a `|` stage after a command that turns the command's
run into a value: whether it succeeded, its exit code, or its output.
Each one decides where the command's output goes. When the value you
get back contains the output (`complete`, `line`, `text`), the
command's stdout is captured into it (`complete` captures stderr too;
with `line` and `text` stderr still reaches your terminal); when you only get the exit status (`exitCode`,
`orFail`), the output streams to your terminal while the command runs;
and `succeeds`, a plain yes/no, discards it.

| form | output | result |
|---|---|---|
| `cmd \| succeeds` | silent | `bool` (`exitCode == 0`, exactly) |
| `cmd \| complete` | captured | `{ exitCode; stdout; stderr }` |
| `cmd \| orFail "msg"` | streams | unit; raises `msg (exit N)` on nonzero |
| `cmd \| exitCode` | streams | the code as `int`; never raises |
| `cmd \| line` | captured | the one trimmed stdout line, a `string`; raises on nonzero or 0-or-2+ lines |
| `cmd \| text` | captured | the whole stdout as one `string`, trailing blank lines dropped; raises on nonzero |
| `cmd \| exec` | the child's | never returns — the command replaces the weir process |

The rule of thumb: if you're asking a question about the command
(`succeeds`, `complete`, `line`, `text`), its output is part of the
answer or not wanted, so its stdout doesn't reach the terminal. If you only
use the exit status to stop or to branch (`orFail`, `exitCode`), the
output is for you to watch, so it streams. `succeeds` means exactly
`exitCode == 0`, so grep finding no match counts as false. When you
need to tell exit codes apart, use `| complete`. Here's a build you
can watch, which then acts on its exit code:

```weir
let rc = sh -c "echo building...; exit 130" | exitCode

match rc with
| 0 -> print "deployed"
| 130 -> print "cancelled"
| c -> fail $"build: exit {c}"
```

`exitCode` is an error in two places, and the message tells you what
to do. Inside `$()`, the output is captured, so use `| complete`
there. As a statement on its own, the code would be thrown away, so
bind it or match on it. When you need both the code and the output,
like fzf's selection and its cancel code, use `complete`.

```weir-error
sh -c "exit 3" | exitCode // a bare statement discards the code — bind or match it
```

The last few rows are written the same way but aren't really about
exit codes. `cmd | line` is for commands that print one value, like
`git rev-parse HEAD`, `az … --query X -o tsv` or `id -un`. It gives
you that one line of stdout, trimmed, as a `string`. It's a shorter
way to write `$(cmd) |> Seq.exactlyOne`. It raises an error if the
command exits nonzero or prints zero lines or more than one. It also
works with an environment (`$e(cmd | line)`) and at the end of a
pipeline that starts from a value (`xs | grep foo | line`):

```weir
let scope = printf "id-abc" | line
print $"scope {scope}"
```

To get all the output as one string, use `| text`. It joins the lines
and drops trailing blank lines, as bash's `$(…)` does:

```weir
let notes = printf "first\nsecond\n" | text
print notes
```

`cmd | exec` replaces the running weir process with the command,
using POSIX `execve`. The command keeps weir's pid. So when weir is a
container's entrypoint, the application gets signals directly, with
nothing in between to forward them or reap processes. (On Windows,
weir starts the command, waits, and exits with its exit code.)

Like `fail` and `exit`, `| exec` never returns, so it can stand alone
as a statement. The program can be written literally or come from a
value (`^$cmd`), and you can add an environment
(`$e(cmd | exec)`). You can't pipe input into it, since there's no
weir process left to feed it.

For bash's `&&`/`||` one-liners, chain on the exit status directly:

- `cmd | and next` runs `next` only if `cmd` succeeded.
- `cmd | or next` runs `next` only if `cmd` failed. Here a nonzero
  exit just picks the branch; it doesn't raise an error.

Both stream their output. The right side is a full command line, so
you can keep chaining, and builtins like `cd` work there too:

```weir
sh -c "exit 1" | or echo "fell back"
echo built | and echo linked
```

Two differences from bash:

- A chain can repeat one word (`a | and b | and c`, or fallbacks
  `a | or b | or c`) but can't mix them. bash's `a && b || c` ("if a,
  then b, else c") is an `if` in weir:
  `if a | succeeds then b else c`.
- The left side of `| or` is an external command or a pipeline of
  them. A builtin raises an error instead of returning an exit code.

An `if` or `elif` condition can be a command directly:
`if test -f $path | succeeds then …`. The command's arguments end at
`then`. That only happens in a condition; anywhere else `then` is an
ordinary argument. To pass the word `then` to a command inside a
condition, quote it: `"then"`. The condition must still be a `bool`,
so a command without a reifier is an error that shows the fix
(`expected bool, got seq<string>`; add `| succeeds`). If you need the
result twice, bind it first: `let ok = cmd | succeeds`, then
`if ok then …`.

## What the editor colors mean

With the language server running, your editor colors code to show
the one thing that's new in weir: where command lines end and
expressions begin. The colors come from the checker itself.

- Command names are colored like functions.
- Their arguments are colored like strings, since they're passed as
  plain text.
- Splice markers (`$name`, and the parentheses of an `(expr)`
  splice) are colored like operators. Everything inside a splice is
  colored as normal code, so you can see it's code.

If something you meant as an expression is colored as arguments, or
a name you meant to run as a command isn't, the coloring isn't wrong.
It's showing you how weir actually read the line, before the checker
has said anything. The REPL uses the same colors.

Setup for Neovim, Helix, Emacs, VS Code and micro is in
[editors.md](editors.md). Any editor with LSP support works:
`weir lsp` talks JSON-RPC over stdio, and its errors, hover and
completion come from the same checker that runs your scripts.
Without an editor, as in CI or an agent loop, use
`weir check --json file.weir`.

## Per-child environment

You can run a command with extra environment variables. They're
added on top of the inherited environment: the names you give are
set, everything else is kept, and weir's own environment isn't
changed. For a single command, write the variables before it, as in
bash:

```weir
GREETING=hello sh -c "echo child: $GREETING"
```

Each `NAME=value` goes before the program name. The value can be a
single word, a quoted string, a spliced `$x`, or an interpolated
string, and `NAME=` sets an empty value. In a pipeline, each stage
gets its own. After the program name, `CC=gcc` is an ordinary
argument, as in bash. There's no way to *unset* a variable for one
command; use `env -u NAME cmd`.

`Env.fromFile` reads the simple part of the dotenv format:
`KEY=VALUE` lines, optional quotes, and `#` comments. It doesn't
handle `export` lines or expand `$VAR` references, because those
need a shell. If a file really has to be sourced, run it in a shell:

`sh -c "set -a; . ./file.env; your-command"`

Bind the environment once, then run commands with it using
`within env e`. Or put its name in a capture: `$e(...)` runs the
command with `e` and captures the output:

```weir
["GREETING=hello"] |> File.write "demo.env"

let e = Env.fromFile "demo.env"

within env e
    sh -c "echo child: $GREETING"
    sh -c "echo again: $GREETING"

let got = $e(sh -c "echo captured: $GREETING") |> Seq.head
print got

print (Env.get "GREETING" |> Option.defaultValue "parent stays clean")
```

## Matching and scraping text

Raw strings let you write regex patterns and paths without escaping
everything. Weir has F#'s two kinds, each written on one line:

- `@"..."`: backslashes are literal, and `""` stands for a quote.
- `"""..."""`: no escapes at all, and a plain `"` is fine inside.

A string is raw because of how you write it, not where you use it.
All five kinds of string literal are in
[Lexical](reference/lexical.md#strings).

`Str.isMatch` tests whether a string matches. Pipe the string in, so
the line reads in order:

```weir
let name = "test_parser"

if name |> Str.isMatch @"^test" then print "a test"
```

To pull values out, use the `Regex` pattern in a `match`. It matches
and captures in one step. The regex is checked before the script
runs: an invalid regex is an error, and you must give exactly as
many names as the regex has capture groups. In F#, getting that count
wrong just means the pattern silently never matches; in weir you get
an error pointing at the line.

```weir
let line = "cache=42"

match line with
| Regex @"(\w+)=(\d+)" (key, count) -> print $"{key} -> {count}"
| _ -> print "unparsed"
```

For many lines, use the same pattern with `Seq.choose`. Return
`Some out` to keep a line and `None` to skip it. No need to map
misses to `""` and filter them out later:

```weir
["cache=42"; "noise"; "hits=7"]
    |> Seq.choose (function | Regex @"(\w+)=(\d+)" (k, v) -> Some $"{k}: {v}" | _ -> None)
    |> Seq.iter print
```

`function` is shorthand for `fun x -> match x with`. Captured groups
are strings; convert them yourself (`Str.tryToInt`).

The `Regex` pattern only accepts raw strings: `@"..."`, or
`"""..."""` if the regex contains quotes. An ordinary string with
escapes is an error, so you can't fall into the double-escaping
trap. If you need to build a regex at runtime, use `Str.isMatch` or
`Str.rmatch`, which take any string.

To pull every match out of a text, use `Str.rmatchAll`. It gives you
the capture groups of each match, lazily. There's no `Option`: no
matches just means an empty sequence. The inline flags `(?s)` and
`(?m)` turn on DOTALL and MULTILINE. Map each match to what you want,
use `Seq.distinct` to remove duplicates, and pipe into an external
tool (`| sha256sum`) if you need one:

```weir
let src = "let a = 1\nlet b = 2\nlet a = 1"

Str.rmatchAll @"let (\w+) = (\d+)" src
|> Seq.map (fun g -> Str.join "=" g)
|> Seq.distinct
|> Seq.iter print
```

If a command can print structured output, use that instead:
`|> from json T` or `|> from yaml T` beats parsing text by hand.

## File batches: glob is a function, not an expansion

`Path.glob` returns the matching paths as a sequence. Nothing in a
command line is ever expanded, so `*.txt` in a command is passed as
the literal text `*.txt`. You work with the matches like any other
sequence:

```weir
match Path.glob "*.md" with
| [] -> print "no docs here"
| docs -> docs |> Seq.sortBy (fun s -> s) |> Seq.iter print

let pinned = Path.glob "*.md" |> Seq.freeze
print $"pinned: {pinned |> Seq.length}"
```

To pass a whole sequence to a command, use `$@`. Ten files become ten
arguments, and none of them is split on spaces:

`git add $@(Path.glob "*.txt" |> Seq.freeze)`

The sequence is lazy, so a relative pattern is matched against
whatever the current directory is when the sequence is read. If the
directory might change in between, `Seq.freeze` the result first to
pin it down. For paths relative to the script itself rather than the
current directory:

`Path.glob $"{Self.scriptPath |> Path.dir}/fixtures/**/*.txt"`

## Typed values: durations, instants, sizes, floats

These four types all exist for the same reason: the unit belongs in
the value's type, not in a comment next to a bare int. Each has its
own literals or parser, `show` output that parses back to the same
value, and its own rules for how it goes into and out of a script.

### Time: durations as values

A `Duration` stores a whole number of milliseconds. Each literal uses
one unit (`500ms`, `30s`, `2m`, `1h`). `show` combines units
(`90500ms` shows as `1m30.5s`), and `Duration.parse` reads that
back. A typical use is a timeout flag that reads naturally both in
code and in `--help`:

```weir
type Fetch = {
    [<Default 30s>]
    /// give up after this long
    timeout: Duration
}

let cli = Args.load Fetch
print $"budget {cli.timeout}, half {cli.timeout / 2}"
```

`weir fetch.weir --timeout 90s` parses the flag's text. Without the
flag, the field is `30s`, and `--help` shows `default: 30s`.

You can add and subtract durations, and multiply or divide them by
ints. `Duration / Duration` is an error, which shows how to get a
ratio: `Duration.toMillis a / Duration.toMillis b`.

`Duration.sleep 500ms` pauses the script. It has the module name in
front so that plain `sleep 5` still runs the coreutils `sleep`.

In a command line, `30s` is just text: `timeout 30s cmd` passes it
through unchanged. Splicing a `Duration` value into a command is an
error, and the message suggests `Duration.toMillis d` or `show d`.
Weir can't know what format the program expects, so you choose.

### Absolute time

`Instant` is a point in time, in UTC. It deliberately keeps things
simple. There are no local time zones and no calendar arithmetic
("add a month" is where time zone pain starts, and scripts rarely
need it). Adding a `Duration` moves by exactly that much real time,
so daylight saving time never comes into it. Subtracting two
instants gives the `Duration` between them, which covers most of
what scripts need:

```weir
let expiry = Instant.parseWith "notAfter=%b %e %H:%M:%S %Y" "notAfter=Aug 14 12:00:00 2027 GMT"
if expiry - Instant.now () > 24h * 30 then print "cert healthy" else print "renew soon"
```

`Instant.parse` reads ISO 8601. Times with an offset are converted to
UTC, and a date on its own means midnight UTC. For log lines,
`parseWith` and `tryParseWith` take a format built from
`%Y %m %d %e %b %H %M %S %f %z`. The format only has to match the
start of the text, so the rest of a log line is ignored:

```weir
let cutoff = Instant.parse "2026-08-14T10:00:00Z"
["2026-08-14 09:00:01 boot"; "2026-08-14 11:30:00 ready"]
    |> Seq.choose (fun l ->
        match Instant.tryParseWith "%Y-%m-%d %H:%M:%S" l with
        | Some t -> (if t > cutoff then Some l else None)
        | None -> None)
    |> Seq.iter print
```

Instants can be compared and sorted. `Args.load` and `Env.load`
parse ISO text into `Instant` fields (`--since 2026-08-01`). JSON
won't take an `Instant` directly, because there's no single standard
way to write a timestamp in JSON. The error suggests
`Instant.epochMs` for an epoch number or `show` for the ISO string.

### Size thresholds: bytes as a type

`File.size` returns a `Size`, so a size limit reads the way you'd
write it, and prints in readable units:

```weir
["payload"] |> File.write "guide-sz.bin"
let sz = File.size "guide-sz.bin"
if sz > 4B then print $"large: {sz}"
File.delete "guide-sz.bin"
```

Size literals use binary units only: `10MiB`, not `10MB`. Out in the
world `MB` can mean either 1000² or 1024² bytes, so weir doesn't
guess, and the error tells you what to write. `Size.parse`, which
reads text from elsewhere, does accept SI units as powers of ten,
since whoever wrote that text chose the unit.

### Rates and percentages: floats, finite-only

A weir float is always a finite number. A result that would be `NaN`
or `Infinity` raises an error instead (`1.0 / 0.0` is an error, like
`1 / 0`). Ints are never converted to floats for you: `3 / 2` is
integer division, and mixing the two (`3 / 2.0`) is a type error
that suggests `Float.ofInt`. A percentage looks like this:

```weir
let passed = 7
let total = 8
let pct = 100.0 * Float.ofInt passed / Float.ofInt total
print $"pass rate {pct}%"
```

Floats work everywhere data comes in or goes out:

- a `--rate 0.5` flag, with `[<Default 0.5>]`
- an `Env.load` field
- a JSON `number` (whole numbers are read as floats too, since JSON
  has only one number type)
- a YAML value: unquoted `rate: 1.5` is a number, and quoted `"1.5"`
  is a string, both when reading and when writing

Floats print in their shortest form, and `Float.parse` reads that
back exactly. A whole-number float keeps its decimal point
(`show 1.0` is `"1.0"`). The one thing floats can't do is `==`: it's
an error, because 0.1 + 0.2 isn't exactly 0.3. The error suggests
`Float.near a b 1e-9`, or comparing after `Float.round`. Sorting
works (`Seq.sortBy` accepts float keys), and `Duration.toSeconds`
makes timing ratios easy:

```weir
let ratio = Duration.toSeconds 90s / Duration.toSeconds 1m
print $"{ratio}x the budget"
```

## Binary data: `Bytes`

`Bytes` holds binary data: a byte array in memory. You only get
`Bytes` when you ask for it. Commands still produce `seq<string>`,
and `File.read` still reads text.

To get `Bytes`:

- `File.readBytes` reads a file as-is, without decoding or splitting
  lines.
- `Bytes.fromBase64` decodes base64 and raises on bad input;
  `tryFromBase64` returns `None` instead.
- `Str.toUtf8` encodes a string.

To get data back out:

- `File.writeBytes` writes a file.
- `Bytes.toBase64` encodes as base64.
- `Str.fromUtf8` and `tryFromUtf8` turn bytes back into text. If the
  bytes aren't valid UTF-8, or contain a NUL, they raise or return
  `None` rather than producing a broken string.

```weir
let b = Str.toUtf8 "hello"
print (Bytes.sha256 b)
print (Bytes.toBase64 b)
print $"{b}"

let png = Bytes.fromBase64 "iVBORw0KGgo="
print (show (Bytes.length png))
print (show (Str.tryFromUtf8 png))
```

You can't pass raw bytes to:

- `print`
- `to json` / `to yaml`
- command arguments, `Args.load` or `Env.load`

Each of these is an error that points you to `Bytes.toBase64` or
`File.writeBytes`. An interpolation hole or `show` prints only a
summary (`<12 B>` above), never the bytes themselves, because raw
bytes can mess up your terminal. `Bytes.length` returns a `Size`.
`==` compares byte by byte, and there's no ordering. To hash a file
without loading it into memory, use `File.sha256 path`, which reads
it as a stream.

## Data in and out: `Http` and the adapters

Sending typed data through `curl` is one flag away from silently
corrupting it: `-d @-` strips newlines, `--data-binary @-` keeps
them, and nothing warns you. `Http` avoids that. A request is a
record and `Http.send` sends it. A `Json` body is turned into JSON
exactly as `to json` would, byte for byte, with no flag to get
wrong.

```weir-demo
type Item = { name: string; count: int }

let created =
    Http.send (Http.get $"{api}/items/1") |> fun r -> r.body |> from json Item

let resp =
    Http.send { Http.post $"{api}/items" with
                  auth = Bearer token
                  body = Json { name = "widget"; count = 3 } }

if resp.status >= 400 then fail $"api said {resp.status}"
```

Usually you start from a constructor like `Http.get url` or
`Http.post url`, and use `with` to set anything optional, as with any
record. `Http.get url` is exactly the same as
`{ Http.defaults with method = Get; url = url }`; the constructor
just saves you writing out the record. Every method has one:
`get`, `post`, `put`, `delete`, `patch`, `head`, `options` and
`query`.

`Json` takes any value that `to json` accepts, with the same checks.
If you give it a `seq<string>`, it's treated as JSON you've already
written, so `Json (x |> to json)` works too. `Form` is the
URL-encoded equivalent, for token endpoints and older servers:
`body = Form [("grant_type", "client_credentials")]` percent-encodes
each pair and sets the content type, so you never build `k=v&…` by
hand.

When all you want is the response body, use `Http.expect`. It's like
`curl -sf`: request in, body out. A non-2xx response raises an error
that names the method, the URL and the status, and includes the
start of the error body. If you need to inspect the error body
itself, such as a structured API error, use `Http.send`, which gives
you the status and body as values. `expect`'s snippet is only meant
for a person reading the error.

```weir-demo
let item = Http.get $"{api}/items/1" |> Http.expect |> from json Item

let mine = { Http.get $"{api}/items/1" with auth = Bearer token } |> Http.expect |> from json Item
```

To retry after a temporary 5xx, you don't configure the client; you
use the ordinary `retry` loop. Retrying a read-only request like GET
or QUERY is safe, since by definition it doesn't change anything on
the server:

```weir-demo
let health = retry attempts=3 delay=2s
    Http.send (Http.get $"{api}/items/1")
until r
    r.status < 500
```

When the data comes from someone else's API and you only read it in
one place, write its type inline as an **anonymous record type**:

```weir-demo
let ip = Http.get "https://api.ipify.org?format=json" |> Http.expect |> from json {| ip: string |} |> _.ip
```

If the response is a JSON array, use `seq<{| ... |}>`. The same
syntax also **builds** values: an anonymous record literal lets you
write a one-off value without declaring a type. Unlike a `Map`, its
fields can have different types:

```weir
let key = "xxxx-111"
{| key = key; n = 3 |} |> to json |> Seq.iter print
```

When to use which: use an anonymous record for *someone else's data,
or a one-off value, that you read or write in one place*. Declare a
record for *your own data and anything you reuse*. Two anonymous
record types with the same fields are the same type, whatever order
the fields are written in, so a literal fits wherever a matching
anonymous type is expected. A declared record with the same fields is
a different type on purpose: declared records are matched by name,
not by shape.

`from json` and `from jsonl` differ in one way. `from json T` reads
one document, however many lines it spans, and gives you a `T`.
`from jsonl T` reads one document per line (NDJSON) and gives you a
`seq<T>`. Neither looks at the input to guess which one it is.
Writing works the same way: `value |> to json` writes one compact
document (a record becomes an object, a sequence an array), and
`xs |> to jsonl` writes one document per element, lazily. Each
reader matches the writer of the same name:
`to json |> from json T`, `to jsonl |> from jsonl T`.

```weir
type Peer = { host: string; port: int }

let body = <<<
    {
      "host": "a.example",
      "port": 9000
    }

let peer = body |> from json Peer
print $"{peer.host}:{peer.port}"

let ndjson = <<<
    {"host": "a", "port": 1}
    {"host": "b", "port": 2}

let peers = ndjson |> from jsonl Peer
peers |> Seq.iter (fun p -> print $"{p.host}:{p.port}")
```

If the whole document is a JSON *array*, as list endpoints usually
return, say so in the type: `from json seq<Peer>` reads one `Peer`
per element.

```weir
type Peer2 = { host: string }

let arr = <<<
    [{"host": "a"}, {"host": "b"}]

let hosts = arr |> from json seq<Peer2> |> Seq.map _.host
hosts |> print
```

Sometimes documents come in *several* shapes, told apart by one
field, like Kubernetes kinds or webhook event types. For those,
declare a **tagged union**:

- `[<Tag "kind">]` names the field that says which shape it is.
- Each case holds the record for one shape.
- Reading picks the case by that field's value.

Add an `[<Other>]` case to accept kinds you didn't declare. They end
up in that case, holding the raw tag value, instead of causing an
error, and your `match` decides what to do with them. Nothing is
silently dropped. Without an `[<Other>]` case, an unknown tag is an
error that lists the cases you declared.

```weir
type PushEv = { branch: string }
type IssueEv = { title: string }

[<Tag "event">]
type Hook =
    | Push of PushEv
    | Issue of IssueEv
    | [<Other>] Ignored of string

["{\"event\":\"Push\",\"branch\":\"main\"}"; "{\"event\":\"Star\"}"]
    |> from jsonl Hook
    |> Seq.iter (fun h ->
        match h with
        | Push p -> print $"push to {p.branch}"
        | Issue i -> print $"issue: {i.title}"
        | Ignored e -> print $"ignored {e}")
```

The same union can read a mixed array (`from json seq<Hook>`), a YAML
document (`from yaml Hook`), a record field, or a `Map` value. When
you write it back, the tag is added as the first field:
`Push { branch = "x" } |> to json` gives
`{"event":"Push","branch":"x"}`. By default a case's tag value is
its name; put `[<Wire "pull_request">]` on a case to change it. The
`[<Other>]` case can't be *written*: it only records something weir
didn't understand, so there's nothing accurate to write out.

A YAML **stream** of documents separated by `---`, like a Kubernetes
apply file, is read with `from yaml stream T`. Each document is read
as a `T`. `stream` only means "several documents"; they can all be
the same record type. Use a tagged union and a mixed bundle is typed
all the way through:

```weir
type DeploySpec2 = { replicas: int }

[<Tag "kind">]
type K8s =
    | Deployment of DeploySpec2
    | [<Other>] Skipped of string

for d in ["kind: Deployment"; "replicas: 3"; "---"; "kind: CronJob"] |> from yaml stream K8s do
    match d with
    | Deployment s -> print $"deploy x{s.replicas}"
    | Skipped k -> print $"skip {k}"
```

Writing matches: `to yaml` on a sequence writes one document
containing a YAML list (the YAML version of a JSON array, which
reads back with `from yaml seq<T>`). `to yaml stream` writes the
`---`-separated bundle that `from yaml stream T` reads.

Fields can nest. A field can be any of these:

- a simple value: `int`, `float`, `string`, `bool`
- an `Option` of an allowed type
- a record whose fields are all allowed types
- a `seq` of an allowed type

So you can type a real API response directly:

```weir
type Entity = { entityid: string }
type Doc = { id: string; entityids: Entity; tags: seq<string> }

let doc = ["{\"id\": \"11831032\", \"entityids\": {\"entityid\": \"0033x\"}, \"tags\": []}"] |> from json Doc
print doc.entityids.entityid
```

A record that contains itself is an error, and the message shows the
loop; data read this way has to have a fixed depth. A missing array
is an error rather than an empty `[]`. If a field may be missing,
make it an `Option`.

When an object's keys are data rather than fixed field names, like
objects keyed by ID, read it as a `Map<string, T>`. That works for a
field, or for the whole document:

```weir
type WDoc = { id: string }

let docs = ["{\"aaa\": {\"id\": \"1\"}, \"bbb\": {\"id\": \"2\"}}"] |> from json Map<string, WDoc>
docs |> Map.pairs |> Seq.iter (fun (k, d) -> print $"{k}={d.id}")
```

Keys can only be strings, since JSON object keys are strings. A
`Map<int, …>` is an error that explains this. Pairs come out sorted
by key, if a key appears twice the last one wins, and `to json`
writes the object back. The `Map` functions:

- `ofPairs` (last key wins), `pairs`, `keys`, `values` (sorted by
  key)
- `get` (raises an error naming the key), `tryGet`, `has`
- `add`, `remove`, `count`

There's no `m[k]` indexing (use `Map.get`), and you can't compare
maps with `==`.

**XML is read the same way, but can't be written.** `from xml T`
turns one XML document into a record, so you can open a
`.csproj`/`.slnx` and work with it as data:

- The root element is the top-level record.
- A field matches a child element by its name without a namespace
  prefix. A default `xmlns`, like MSBuild's, is ignored, so field
  names stay plain.
- `[<Attr>]` reads an attribute.
- `[<Elem "ProjectReference">]` on a `seq< >` field says which
  repeated child element to collect.
- A nested record reads a child element.

```weir
type Ref  = { [<Attr>] Include: string }
type Proj = { [<Elem "PropertyGroup">] groups: seq<{| IsPackable: Option<string> |}>
              [<Elem "ProjectReference">] refs: seq<Ref> }

let proj =
    [ "<Project Sdk=\"Microsoft.NET.Sdk\">"
      "  <PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>"
      "  <ProjectReference Include=\"../Core/Core.csproj\" />"
      "  <ProjectReference Include=\"../Util/Util.csproj\" />"
      "</Project>" ]
    |> from xml Proj

proj.refs |> Seq.iter (fun r -> print r.Include)
```

Everything in XML is text, so a field is a `string`, an
`Option<string>` (for something that may be missing), a record, or a
`seq` of strings or records. For a number, declare the field as
`string` and convert it (`Str.toInt`). The checker tells you this,
rather than guessing a number format that XML doesn't define. An XML
document has a single root, so there's no `from xml seq<T>` (use a
`seq< >` field for repeated elements). There's also no `to xml`.

**Aligned tables can be read with types too.** `from table T` reads
the kind of output `kubectl` and `docker` print, with one header row
and aligned rows below, into a `seq<T>` of records:

- Columns are cut at the *positions of the headers*, not at spaces,
  so `Up 2 hours` stays one cell.
- Headers are separated by two or more spaces, so `CONTAINER ID` is
  one column.
- Field names match headers loosely: `podTemplateHash` reads
  `POD-TEMPLATE-HASH`. Use `[<Wire "HEADER">]` to match a header
  exactly.
- Each cell is parsed as its field's type, and an `Option` field
  reads an empty or `<none>` cell as `None`.

```weir
type Pod = { name: string; status: string; restarts: int }

let pods =
    [ "NAME    STATUS    RESTARTS   AGE"
      "web-1   Running   0          2d1h"
      "db-0    Pending   3          5h" ]
    |> from table Pod

pods |> Seq.where (fun p -> p.restarts > 0) |> Seq.iter (fun p -> print p.name)
```

In a real script that's `kubectl get po |> from table Pod`, and
`#infer it from table as Pod` in the REPL writes the record type for
you. `az … -o table` output works too: the line of dashes az prints
under the header is skipped when it's the first row after the
header, so you don't get a junk row
([Adapters](reference/adapters.md#aligned-tables)). If you read the
result more than once, add `|> Seq.freeze` at the end. Otherwise
each read runs `kubectl` again, and the checker warns you about it.
Extra columns are ignored, and errors include the line and column.
There's no `to table`; like XML, it's read-only.

Here's a real REPL session that cleans up every evicted pod:

```text
weir> let pods = kubectl get po -A |> Seq.freeze
weir> #infer pods from table as Pod
weir> pods |> from table Pod
       |> where (fun p -> p.status == "Evicted")
       |> map (fun p -> p.name, p.namespace)
       |> iter (fun (name, ns) -> kubectl delete po $name -n $ns)
```

The `Seq.freeze` matters here. A `let` bound to a command stores the
lazy sequence, not the output. Without `Seq.freeze`, `#infer` would
run `kubectl` once and `from table` would run it again, so you'd
infer from one cluster state and delete from another. With it,
`kubectl` runs once, and the sample, the filter and the pods you
delete all come from the same output. Then the typed rows go back
out as command arguments: `$name` and `$ns` are passed to a real
command as exactly one argument each.

**Editing YAML you haven't fully declared.** `from yaml T` drops any
fields you didn't declare. That's right for reading, but it would
lose data if you wrote the result back. Instead, work with the
document without a type:

- `Yaml.parse` reads one document into `Yaml` nodes, keeping
  everything.
- `Yaml.merge` applies a `yaml patch` block. The patch's *structure*
  says where each change goes: nest keys to reach the place you want.
- To match list items by a key, put `by=` on the marker line.
- To remove something, mark it with `$-`.

With `by=`, an item that matches is updated and one that doesn't is
added, so you don't write that check yourself. Applying the same
patch twice changes nothing the second time:

```weir
let doc = <<<
    namespace: prod
    images:
        - name: app
          newTag: v1
|> Yaml.parse

let p = yaml patch by=name
    images:
        - name: app
          newTag: v2

doc |> Yaml.merge p |> to yaml |> Seq.iter print
```

Notice that `namespace: prod` comes through untouched; that's the
point of reading without a type. A patch has the type `YamlPatch`
and can't be written out (`to yaml` on it is an error), so a `$-`
removal marker can never end up in a file. To edit a file, spell out
the whole round trip:
`File.read f |> Yaml.parse |> Yaml.merge p |> to yaml |> File.write f`.

`Http.query` sends the QUERY method (RFC 10008). QUERY is idempotent
by definition, so wrapping `Http.query` in `retry attempts=5` is
always safe. Wrapping a POST the same way may not be, and you have to
decide that yourself. (Almost no servers support QUERY yet, so expect
405 from most of them.)

For query strings, `base |> Http.withQuery [("q", term)]`
percent-encodes each key and value, so a space or `&` can't break the
URL. You still have to build the path part of the URL carefully
yourself.

TLS certificates are verified by default.
`Http.send { … with insecure = true }` turns that off for one
request (for clusters with self-signed certificates). It's a visible
setting on each request, never a global switch.

**The status code is just data.** A 404 doesn't raise; you get the
response and branch on it (`if resp.status >= 400`), just as
`| complete` gives you an exit code. Only a failure to talk to the
server at all (unreachable, TLS error, timeout) raises. A health
check is one line:

```weir-demo
let up = (Http.send { Http.defaults with url = $"{api}/health" }).status == 200
```

**Auth is a union that holds a `Secret`**: `Bearer token`, or
`Basic ("user", pass)`, which does the base64 for you. Interpolating
a token into a string is an error, and `show` on the request prints
it as `***`. For settings shared by several requests, make a base
record and copy it with `with`:

```weir-demo
let github = { Http.defaults with
                 auth = Bearer token
                 headers = [("Accept", "application/vnd.github+json")] }

let user = Http.send { github with url = $"{api}/user" }
```

For a plain GET with no body or auth, `curl url |> from json T` is
still fine. `Http` is most useful when you're sending something.
Parallel requests use pieces you already know:

`urls |> Seq.pmap (fun u -> Http.send { Http.defaults with url = u })`

The timeout defaults to 30s, because a request with no timeout is
the classic way to hang a CI job. And `Http` checks the types of your
request, not where it goes: guarding against SSRF and building URLs
safely is up to you ([SECURITY.md](../SECURITY.md)).

### Serving: `within serve`

This is the server side. `within serve` runs an HTTP listener for
the duration of a block, and closes the socket however the block
exits, so the port is freed. It works like `within proc`. The
handler is a plain synchronous function that picks a response by
matching on `req.path`. The response `body` uses the same `HttpBody`
union as the client, plus `Stream of seq<string>` for a lazy body
that's sent in chunks as it's produced:

```weir-demo
let handler = fun req ->
    match req.path with
    | "/health" -> HttpServerResponse { status = 200; headers = []; body = Text "ok" }
    | "/events" -> HttpServerResponse { status = 200; headers = []; body = Stream (nats |> Seq.map show) }
    | _ -> HttpServerResponse { status = 404; headers = []; body = Text "not found" }

within serve srv = { port = 8080; maxConcurrent = 8 } handler
    print $"serving on {Server.port srv}"
    Duration.sleep 30s
```

`maxConcurrent` limits how many requests are handled at once, the
same way `Seq.pmapWith` limits parallel work. The server handle has
only `Server.port` and `Server.running`. There's no `stop`: the
server stops when the block ends. TLS, routing libraries and
WebSockets are deliberately left out. Put a reverse proxy in front,
`match` on the path, and use `Stream` (server-sent events) where
you'd want to push data to the client.

## Secrets: tokens that cannot leak into logs

A `Secret` wraps a value so weir won't print it:

- `show` gives `***`.
- Interpolation, `to json` and `to yaml` reject it.
- `Secret.reveal` is the only way to get the value back out.

This controls where the value can go in output that weir itself
produces. It isn't secure storage and doesn't protect memory
([SECURITY.md](../SECURITY.md) spells out those limits). What it
gives you is that a token can't end up in a log line or a printed
record by accident.

Secrets usually come from `Env.load`. Environment variables are how
CI systems pass secrets (`secrets.GITHUB_TOKEN` becomes an env var),
so a `Secret` field is the main way a token gets into a script:

```weir-demo
type Cfg = { GITHUB_TOKEN: Secret }
let cfg = Env.load Cfg
git push https://$(Secret.reveal cfg.GITHUB_TOKEN)@github.com/…
```

`Args.load` accepts a `Secret` field too, but anything passed as a
flag shows up in the process list, and weir can't hide that.
`File.readSecret` reads a secret file mounted by Kubernetes or
Docker. Every use of the value goes through an explicit
`Secret.reveal`, so to audit where a secret goes, search for those
calls. To build a new value from a secret and keep it secret, use
`Secret.map`. Writing `"Bearer " + reveal` would give you an
ordinary, unprotected string:

```weir
let token = Secret.of "s3cr3t"
let header = Secret.map (fun t -> "Bearer " + t) token
print (show token)
print (show header)
print (Secret.reveal header)
```

A `Secret` spliced into a command is passed as its real value;
`curl -H $header` is exactly what it's for. Interpolating it
(`$"tok: {token}"`) is an error that mentions `reveal`, and
`to json`/`to yaml` reject it.

## The script's own front door

Command-line arguments are plain strings, just like environment
variables, and you load them the same way: declare a record type,
load it once, and work with typed values from then on:

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

How fields become flags:

- Field names become kebab-case flags: `dryRun` becomes `--dry-run`.
- Each flag gets a one-letter short form from its first letter, if no
  other flag starts with the same letter. `[<Short "C">]` sets the
  short form yourself, and `[<NoShort>]` turns it off.
- A `bool` field is a flag that's either present or not.
- `string` and `int` fields are required; wrap them in `Option` to
  make them optional.

`--help` prints the usage: each flag, its type, whether it's
optional, its short form, and the first line of its `///` doc. It
works even if the rest of the command line is invalid. Loading is
strict, and it reports every problem at once in a single error,
before the script does anything:

- unknown flags (with a "did you mean")
- unexpected arguments
- missing required flags
- values that can't be parsed

For subcommands, declare a union whose cases hold records. The first
argument picks the case, and the rest are parsed as that case's
flags. Your `match` on the result has to cover every case, so when
you add a subcommand, the checker points at every match you still
need to update. No `die "unknown command"` at runtime:

```weir-error
type CloneArgs = { remote: string; force: bool }
type Cmd = Clone of CloneArgs | Status
match Args.load Cmd with // no argv here: "missing subcommand; one of: clone, status"
| Clone a -> print a.remote
| Status -> print "status"
```

There are no positional arguments. Pass everything as a flag
(`pull --subdir libx`), for the same reason records beat tuples: a
name is easier to read than a position you have to count. There is
no `[<Positional>]` attribute. You only lose the typed,
help-generating route, though. To read arguments your own way,
`Args.flag "--clean" "-c"` and `Args.value "--out"` search the raw
`Self.args` sequence. An option with several values becomes one flag
per value (`--stack X --env Y`).

### Shared flags: containment, not inheritance

To give every subcommand the same flags, declare them once, on a
record that contains the subcommand union. The outer record holds
the shared flags, so you don't repeat them in each case:

```weir
type CloneArgs = { remote: string }

type Cmd =
    | Clone of CloneArgs
    | Status

type Cli = { quiet: bool; cmd: Cmd }
```

The union field's name doesn't matter; it doesn't become a flag.
`tool --quiet clone --remote X`, `tool clone --quiet --remote X` and
`tool clone --remote X --quiet` all work. Shared flags can go
anywhere, and the subcommand's own flags come after its name. You
read the shared flag with `cli.quiet`, with no `match` needed.

### Defaults, and declared value sets

If an environment variable has a fixed set of allowed values,
declare them as a union with plain cases. A typo then fails right
when the variable is loaded, with the allowed values listed, instead
of taking the wrong branch three functions later:

```weir
type Level =
    | Debug
    | Info
    | Warn

type LogCfg = { WEIR_GUIDE_LOG_LEVEL: Level }

["WEIR_GUIDE_LOG_LEVEL=debug"] |> File.write "guide-log.env"
let e = Env.fromFile "guide-log.env"
within env e
    sh -c "echo layered"

print "declared sets beat stringly config"
```

Matching ignores case (`=DEBUG`, `=debug` and `=Debug` all select
`Debug`), since environment values are often uppercase. A value that
doesn't match reports `expected one of: Debug, Info, Warn` with a
"did you mean".

`[<Default v>]` on an `Args.load` field supplies the value when the
flag isn't given. The field doesn't need to be an `Option`, so your
code reads it directly, and `--help` shows the default.

On a bool, `[<Default true>]` also adds the opposite flag: the field
is true unless you pass `--no-x`, and passing both `--x` and
`--no-x` is an error that names both. `[<Default false>]` is an
error on an `Args.load` bool, because a bool flag is already false
when it's not given:

```weir-error
type Cli = {
    [<Default false>]
    clean: bool
}
let c = Args.load Cli // [<Default false>] is redundant — presence already rests at false
print $"{c.clean}"
```

`Env.load` uses the same attribute: if the variable isn't set, you
get the default, and if it is set, its value wins. In the
environment a bool is written as text (`FLAG=false`) rather than
being present or absent, so `[<Default false>]` is allowed there.

The default must be a literal. If you need to compute a default,
make the field an `Option` and add one line of code. The record
below shows both:

```weir
type Cli = {
    /// replay seed (fresh when omitted)
    seed: Option<int>
    [<Default 10000>]
    /// cases per invariant
    count: int
}

let cli = Args.load Cli
print $"count={cli.count} seed={cli.seed}"
```

## A script's own facts — the `Self` module

`Self` holds what a running script knows about itself:

- `Self.args`: the arguments
- `Self.stdin`: the input stream
- `Self.pid`: the process id
- `Self.scriptPath`: the script's path

`Self.prompt` is here too. It writes a message to stderr and reads
one line of input. Unlike the others, it also works in the REPL and
with `-e`.

`Self.scriptPath` is the absolute path of the running script. It's
worked out when the script starts, from the directory you ran it
from, so a later `cd` doesn't change it. Symlinks aren't resolved:
it's the path the script was *run as*, not necessarily where the
file really is.

For the script's own directory:

```weir
let dir = Self.scriptPath |> Path.dir
print $"has a directory: {Str.length dir > 0}"
```

To resolve symlinks:
`let real = realpath $"{Self.scriptPath}" | line`

These members only work in scripts. In the REPL and with `-e` there
is no script file, so using one is an error that names it.

## Sharing code: modules and `import`

Sooner or later two scripts need the same helper. A file that starts
with `module` (on its own, or `module Name`) is a module. A module:

- can be imported
- contains only `type` and `let` definitions, with no commands and
  no bare expressions
- can't be run on its own

Import it with a literal path, at the top of the file:

```weir
let greetSrc = <<<
    module Greet

    /// the shared helper
    let hello : string -> string

    let hello name = $"hi {name}"

let useSrc = <<<
    import "./greet.weir" as G

    print (G.hello "weir")

greetSrc |> File.write "greet.weir"
useSrc |> File.write "use-greet.weir"
weir use-greet.weir
```

The line `let hello : string -> string`, a `let` with a type and no
`=`, is a *signature*. Signatures decide what the module exports:
only members with a signature can be used by importers. A `let`
without one is private to the module, and its type is inferred as
usual. Importing a private member is an error that tells you the
exact signature to add, including the inferred type. The
implementation itself needs no type annotations, because the
signature supplies them. Put `///` docs on the signature line, so
the API is documented in one place. `type` declarations are already
explicit, so they're exported as they are. Signatures are only
allowed in modules: in a script, types are inferred, and only a
module's API is declared.

(Note the plain `<<<`, not `$<<<`. The module body contains
`$"hi {name}"`, and `$<<<` would fill in `{name}` when the file is
*written*, instead of leaving it as weir code. A plain `<<<` keeps
everything as written, which is what you want for generated weir
code, just as for shell text.)

You always use imported names with the prefix: `G.hello`, `G.Ctx`
for an imported type, `G.Ctx { field = v }` to build one of its
records. Without `as`, the prefix is the module's declared name, or
else the file name with a capital first letter. Nothing is imported
without a prefix, not even a union's cases, and a local declaration
always takes priority over an imported name.

Imports are resolved by the checker, from the literal path. Nothing
is loaded at runtime, and a missing file is an error that shows the
full absolute path it looked for. Imports of imports work too. A
module imported by two others is checked only once, and a cycle of
imports is an error that names it. `import` only works in scripts,
not with `-e` or in the REPL. Importing the wrong kind of file is
also a clear error:

```weir
["print 1"] |> File.write "plain.weir"
let src = <<<
    import "./plain.weir"
    print "unreachable"

src |> File.write "use-plain.weir"
let r = weir use-plain.weir | complete
print (if r.exitCode <> 0 then "importing a non-module is a named check error" else "unexpected")
```

(The error printed by the inner script is: `plain.weir is not a
module; add module at the top, or invoke it as a command`.) A module
`let` that runs a command is an error too; wrap it in a function.
Modules only define things, and scripts run them.

A module can also come from another repo, copied into your project
and committed.
`weir add module github.com/org/repo//lib/x.weir@v1.2.0 --as x`
downloads it into `.weir/modules/` and records the exact version in
the lock file. You can then import it by name from anywhere in the
project: `import "weir:x" as X`. The details (versions, updates,
private repos, and what `add` checks) are in
[tooling.md](tooling.md#remote-modules).

## Retrying and polling

`retry` and `poll` are loops with a limit, and both are written the
same way:

- options as `key=value` after the keyword
- an indented body whose last statement is the result
- an `until` section that names the result and tests it

If the body returns a `bool`, that's the test, and the loop returns
unit:

```weir
retry attempts=3 delay=100ms
    weir -e "print 1" | succeeds
```

To keep the output of the attempt that succeeded, return a value
from the body and name it in `until`:

```weir
let out = retry attempts=3 delay=100ms
    let r = weir -e "print 42" | complete
    r
until r
    r.exitCode == 0
print (out.stdout |> Seq.head)
```

`poll` works the same way, but is limited by time instead of attempts
(`poll timeout=5m interval=10s`), which suits waiting for something
to be ready. When the limit runs out, it raises an error with the
number of attempts and the time taken. There's no way to write a loop
without a limit. The options are a record underneath, so you can
build them once and reuse them:
`let fast = { Retry.defaults with attempts = 3 }`, then `retry fast`.

## Parallelism

`Seq.pmap` and `Seq.piter` process a sequence in parallel. Results
come back in the same order as the input, and the first failure is
raised again. Each worker gets its own copy of the session state, so
a `cd` inside a worker only affects that worker and is gone when the
workers finish. There's no async/await, and there won't be: in weir,
you get concurrency from processes and pipelines. Under the hood,
each worker runs on its own thread inside the weir process. That's
worth knowing when you think about shared state, or about which
thread an error is raised on. A task that really needs async belongs
in full F#.

A background process always belongs to a scope; there's no `&`.
`within proc` gives you a handle to the process, and when the block
exits, normally or by an error, the process and all its children
are killed and cleaned up. The usual five shell steps (`server &`,
wait for the port, use it, `kill`, `wait`) become:

```weir
within proc srv = python3 -u -c "import socketserver,http.server as h; s=socketserver.TCPServer(('127.0.0.1',8617),h.SimpleHTTPRequestHandler); s.serve_forever()"
    poll timeout=15s interval=100ms watch=srv
        Net.portOpen 8617
    print $"server {Proc.pid srv} answered"
print "scope closed, server gone"
```

(In real scripts you'd usually use `python3 -m http.server`. The
inline server here skips the reverse-DNS lookup that `http.server`
does when it starts. On locked-down hosts that lookup can hang
because of privacy restrictions, which is worth knowing if a server
seems to be up but isn't listening.)

Use `watch=` every time. If the process crashes while starting, the
poll fails at the next interval, and the error includes the last
lines the process printed. If the poll simply times out, the error
also says whether the watched process was still running.

The process's output goes to files on disk, so it isn't limited by
memory. It never reaches your terminal or the script's own stdout,
so a noisy server can't break `weir script | next`. `Proc.tail`
reads about the last 100 lines. To see those lines as they're
written, pass the program's unbuffered flag (python's `-u`), since
programs buffer stdout when it isn't a terminal.

A background process's exit code is just data. This is the one place
where a failing exit doesn't raise an error by default. You only see
a failure through `watch=` or `Proc.wait` (which returns the exit
code). `Proc.stop` shuts a scope down early, and nested scopes are
closed innermost first.

Exactly when cleanup is guaranteed: a normal exit, an error, SIGINT
and SIGTERM all close every scope. `kill -9` of weir itself can't,
since nothing gets to run. A process that has to outlive the script
is a daemon, which is a job for systemd or launchd; weir deliberately
has no `nohup`.

```weir
Dir.create "wa"
Dir.create "wb"
["wa"; "wb"] |> Seq.pmap (fun d ->
    let _cd = cd d
    pwd) |> print
```

## Declaring a tool: command signatures

Weir already checks that `bicep` exists. A signature goes one step
further: it describes the tool's flags, so the checker catches the
typo in `bicep build --outfil x` before the script runs. Generate a
signature from the installed tool, then turn it on in each script
that uses it (signatures need a `.weir/` directory, so there's no
runnable example here):

    weir add sig bicep        # probes the tool, writes .weir/sigs/bicep.weir + the lock

    #sig bicep                # in each script that wants the checking
    bicep build --outfile x.json

A generated signature may be incomplete, since it's worked out by
probing the tool, so an unknown flag is only a warning. Once you've
checked a signature by hand and marked it `exhaustive`, unknown flags
become errors. `weir check` never runs the tool, so checking works
even for tools that are only installed in CI. Generating, checking
and regenerating signatures, and the `.weir/` directory they live
in, are covered in [signatures.md](tooling.md#command-signatures) and
[project.md](tooling.md#project-layout-weir).

## What a script can do, before it runs

`weir check --can` goes one step beyond checking. Command names are
written literally (a name computed at runtime, `^$tool`, is reported
as opaque) and nothing in a command line is expanded,
so weir can tell from the source alone which commands a script can
run. It lists them, with the location of each one:

```text
deploy.weir can (capability, not behaviour — an untaken branch still counts):
  ⚠ this report is incomplete: 1 opaque site(s) — an interpreter's argument or a dynamic head cannot be analyzed statically
  ambient reads (inform, change nothing):
    environment:
      reads token (Env.load Cfg)  deploy.weir:2:11
    secrets:
      loads token (Env.load Cfg)  deploy.weir:2:11
  mutations:
    runs:
      git × 2  deploy.weir:3:1, 6:33
      sh (opaque)  deploy.weir:5:1
      curl  deploy.weir:9:1
    writes:
      File.write out.txt  deploy.weir:7:11
    network:
      Http.expect https://api.example.com/items  deploy.weir:8:13
    secrets:
      a Secret reaches the argv of curl (visible in ps — weir does not hide argv)  deploy.weir:9:9
```

What the report does and doesn't tell you:

1. It shows what the script **can** do, not what it will do. A
   command in a branch that never runs is still listed.
2. `sh -c` and other interpreters are listed as unknowns, and counted
   at the top. With `--strict`, the check exits with code 2 if there
   are any, so CI can reject scripts that can't be fully analyzed.
3. It only covers what weir itself does. An external program can do
   anything, and no report can see inside it.

Imported modules are included, all the way down, and each entry
from a module shows the module's own file and line. `--json` gives
the same information in machine-readable form.

## Pure islands: `pure` and `let pure`

In weir, side effects are normal: you can run commands and touch
files anywhere, without ceremony. `pure` lets you ask for the
opposite. It marks code that must have **no** side effects at all:
no files, commands, network, environment, console, clock, or
`within` resources. The checker verifies this before the script
runs:

```weir
let pure slug s = s |> Str.toLower |> Str.replace " " "-"

let name =
    pure
        slug "Release Notes"
print name
```

`let pure f x = …` marks a whole function or value as pure, and
`pure` on its own line marks the indented block below it. Any side
effect inside is an error that points at the line and names what
does it:

```text
deploy.weir:4:9: error [check]: this 'pure' block forbids effects, but 'File.write' writes the filesystem
```

This check is the same one behind the `(pure)` label you see on
hover, and it errs on the side of caution. Calling a function you
defined earlier is fine if that function is pure. But if weir can't
see what a function does, such as a function passed in as a
parameter or a member of an imported module, it doesn't count as
pure. So the check may reject code that is actually pure, but it
never accepts code that isn't. Code outside a `pure` block is never
restricted.

## Read-only islands: `readonly`

`readonly` is less strict than `pure`. `pure` forbids every side
effect. A `readonly` block only forbids **changing things outside the
script**:

- writing files
- running commands
- HTTP methods that change things (POST, PUT, DELETE, PATCH)
- any `within` resource

**Reading is allowed**:

- reading files
- `Env` and `Args`
- the clock
- read-only HTTP requests (`Http.query`, or `Http.send`/`Http.expect`
  with a GET, HEAD, OPTIONS or QUERY request)
- stdin

So a `readonly` block can look at the world but can't change it. It
doesn't promise the same result every time, though: it can read the
clock and stdin. That's why it's called read-only rather than pure.

```weir
let cfg =
    readonly
        let home = Path.home ()
        $"{home}/config"
print cfg
```

Anything inside that changes the world is an error that points at
the line and says what kind of change it makes:

```text
deploy.weir:3:9: error [check]: this 'readonly' block forbids external mutation, but 'File.write' writes the filesystem — reads are allowed
```

So anything allowed in `pure` is also allowed in `readonly`. Like
`pure`, you only get the restriction where you ask for it, and the
rest of the script is unaffected. It's written as `readonly` on its
own, never `within readonly`. `weir check --can` uses the same split
between reading and changing: it groups what a script can do into
reads (which change nothing) and changes. So you can read "what
does this script change?" straight off the report.

## Dry-run as a primitive: `plan` and `apply`

A `plan` block runs code but **records** the changes it would make,
instead of making them. It's Terraform's plan/apply cycle, built
into the language. `readonly` *forbids* changes; `plan` *records*
them. The same `File.write` that `readonly` rejects becomes a
pending `WriteFile` operation in a `plan`.

    let changes =
        plan
            File.write "out.json" rendered
            Dir.copy "templates" "site"

    changes |> Plan.preview |> Seq.iter print   // render; wrote nothing
    if changes |> Plan.isEmpty then print "no changes"
    changes |> Plan.apply                        // now perform them

The block returns a `Plan`: a list of operations (a `seq<Op>`) that
you can compare with `==` and print. That means you can test code
without mocks. `plan <actual> == plan <expected>` is a plain
comparison, and *neither block does anything*, since both only
record. For a single operation,
`plan <block> |> Plan.ops |> Seq.exactlyOne == WriteFile("out.json", rendered)`
checks it. Preview and diff come for free.

These are the operations a plan can hold (the `Op` union):
`WriteFile`, `DeleteFile`, `Copy`, `Move`, `MakeDir`, `DeleteDir`,
and `HttpSend` (only for HTTP methods that change things; `show`
masks the auth Secret). Reads still **happen** inside a plan, since a
script often reads things to decide what to write. File contents are
captured when the plan is made, so **what you preview is what
`apply` does**, even if a file changes in between.

Where the guarantee stops:

- **`proc` isn't allowed in a plan.** Weir can't see what a separate
  program reads or writes, so `plan` is a dry run for *file and
  config work*, not for any script. `cmd | exec` isn't allowed
  either, since replacing the process can't be recorded.
- **`apply` isn't allowed inside a plan**, since that change couldn't
  be recorded. Build the plan in the block and apply it outside.
  Nested `plan` blocks are fine; each returns its own plan.
- **Reading something the plan has already changed is an error**,
  pointing at the line. The change hasn't been made yet, so the read
  would see old data. Rearrange the code so the read doesn't depend
  on a recorded write.
- **`apply` is not a transaction.** It performs operations in the
  order they were recorded and **stops at the first failure, leaving
  earlier operations done. There's no rollback.** Weir deliberately
  doesn't go that far into infrastructure-as-code territory; use
  `always`/`within` for cleanup.

Asking for confirmation is just your own code. A plan is a value, so
you branch on it (`if approved then changes |> Plan.apply`); there's
no builtin `confirm`. Like `pure` and `readonly`, `plan` only applies
where you write it, and it's written on its own, never `within plan`.

## Terminal output: color, tables and width

The `Color` module wraps a string in a color or style: `Color.red`,
`green`, `yellow`, `blue`, `magenta`, `cyan`, `gray`, plus `bold`,
`dim` and `underline`. Each one is `string -> string`, and you nest
them to combine:

```weir
print (Color.green "ok")
print (Color.bold (Color.red "failed"))
print (Color.sgr "38;5;208" "256-color orange")
```

Color is only added when it makes sense. You get the color codes
only when writing to a terminal with color enabled. When output is
piped or `NO_COLOR` is set, you get the plain string, so
`weir script | grep` stays plain without any checks on your part. At
a terminal, `print` lets color through, but turns every *other*
escape sequence in a value into visible `\xNN`: window titles,
clipboard access, cursor movement, and carriage-return overwrites.
So untrusted data can't mess with your terminal. `Color.sgr` takes a
raw code, for 256-color and truecolor.

`Table.render` lays out a sequence of records of the same type as
aligned columns, the same table the REPL shows, and returns lines
for `print`:

```weir
ls |> Seq.take 5 |> Table.render |> Seq.iter print
```

It's for display, not a data format: there's no `to table`, and a
table can't be read back reliably. Use `to json` or `to yaml` for
data. At a terminal it fits the table to the terminal width; when
piped, it doesn't limit the width.

`Term.width ()` gives you that width: the number of terminal columns
as an `int`. It checks every time you call it, so it sees resizes.
When there's no terminal (output is piped or redirected), it returns
80 instead of raising an error. It's the equivalent of `tput cols`,
for laying out your own output.

## Failing and diagnosing

`fail "reason"` stops the script with an error showing where it
happened, and exit code 1. `exit n` exits with a specific code and no
message, which is how you pass on a child process's failure. There's
no general try/finally. Cleanup for a resource belongs in a `within`
block, which cleans up however the block exits, including errors.
For a step that might fail but isn't a resource, turn its result
into a value with `| complete`, run the cleanup, then pass the
failure on:

```weir
let r = sh -c "exit 0" | complete
sh -c "echo cleanup runs either way"
if r.exitCode <> 0 then exit (r.exitCode)
```

The two most common cases have short forms. `cmd | succeeds` gives
you a bool. `cmd | orFail "msg"` is a one-line check: it returns unit
on success and raises `msg (exit N)` on failure. The full table is in
[Exit codes](#exit-codes-from-command-to-value):

```weir
let onBranch = git symbolic-ref -q HEAD | succeeds
sh -c "true" | orFail "sanity failed"
print (if onBranch then "on a branch" else "detached")
```

`Log.info $"starting {n}"` (and `trace`, `debug`, `warn`) writes log
messages with a level to stderr. `WEIR_LOG=debug weir script.weir`
shows more detail for one run without editing anything, and
`WEIR_LOG=off` turns the log off. `printerr` and `fail` always get
through, whatever the level. That's why there's no `Log.error`: an
error you could hide with an environment variable would be exactly
the message you needed to see. Stdout is exactly the same at every
level, because logging never writes to it.

`printerr` writes to stderr the way `print` writes to stdout. Use
stderr for messages to the user and stdout for data:

```weir
printerr "starting"

if 1 > 2 then fail "impossible"

print "done"
```

## The REPL

`weir` with no arguments starts the REPL. Here you can use short names
like `map` and `where` (scripts need `Seq.map` and so on). Each value
is echoed back with its type. An entry can span several lines: weir
checks whether what you've typed so far is a complete statement and
keeps reading if it isn't. `Ctrl+C` throws away the current entry,
and `Ctrl+D` or `#quit` exits.

`#help` lists the directives and the modules. `#help Seq` lists the
members of one module, which F#'s FSI can't do. `#help Seq.collect`
shows one member's docs, from the same source as editor hover, so
the two always match.

A line starting with `#` is an instruction to weir's tools rather than
code. There are two kinds. File directives (`#sig`, `#schema`) are
read when the script is checked. Session directives (`#help`,
`#quit`) run immediately.

Everything else about the REPL is in [repl.md](repl.md): what the
echo shows and how much of it, the prompt's colors, the keys for
multi-line editing, and the init file (declarations to load at
startup, and `#session` for settings).

## Where weir ends

Every difference from F#, whether deliberate, ruled out by design,
or just not done yet, is listed in
[tests/fidelity/divergences.md](../tests/fidelity/divergences.md),
and checked against the real F# compiler. In short, weir has:

- no mutation
- no unbounded loops and no recursion. `retry` and `poll` are loops
  with a limit, and `Graph.reach` and `Tree.walk` walk graphs and
  trees safely even with cycles. A loop without a limit can't be
  written.
- no exceptions; use values, `fail` and `exit` instead
- no OO
- no async
- no user-defined type classes and no SRTP; the three built-in kinds
  of constraint are all there is

When a task outgrows a shell script, the next step is full F#, and
weir points you there on purpose.

Where to next:

- [The reference](reference/lexical.md): the language rules, page by
  page
- [skills/weir/SKILL.md](../skills/weir/SKILL.md): the complete rule
  file written for AI agents, covering every member and every rule
- [COMING-FROM.md](COMING-FROM.md): translation tables for people
  coming from bash, PowerShell, Python, or Make
