# Commands

## How a line decides

The first word of a statement decides how the line is read. If it is
a name bound in scope (or a builtin), the line is an expression — an
ordinary function application. If it is an unbound bare word, weir
runs the external program of that name, resolved against PATH before
anything runs. Builtins take precedence over PATH, and `^ls` forces
the real program. Inside a function body, its parameters also take
precedence over PATH:

```weir
let greet name = print $"hi {name}"
greet "io"
echo hi io
```

## Argv

On a command line every word is passed as written: nothing expands,
splits or concatenates. A spliced value becomes exactly one argv word.
Pieces written next to each other are an error rather than being
glued together:

```weir-error
let root = "build"
rm -rf $root/* // argv words do not concatenate — write $"{root}/*"
```

A command line continues onto indented lines, like every other
statement. There is no `\` line continuation:

```weir
printf "%s %s\n" first
    second
```

What command lines do not do:

- no backslash escapes — a `\` word is an error that shows the
  replacement (`\;` is `";"`, a literal backslash argument is `"\\"`,
  and `\ls` is `^ls`)
- no glob expansion — `Path.glob` is a function
- no `$VAR` expansion — splice weir bindings instead
- `~` is the one exception, and only when typed directly: an unquoted
  word that is `~` or starts with `~/` means your home directory,
  resolved when the line runs (`cat ~/.bashrc`, `~/bin/tool`, `cd ~`).
  A quoted `"~/x"`, an interpolation and a spliced value stay literal,
  because weir never re-reads data as syntax. `~user` is not expanded.
  In an expression, build the path with `Path.home ()`, or with
  `Path.configHome`/`Path.stateHome`/`Path.cacheHome` for the XDG
  directories: `File.read $"{Path.home ()}/.bashrc"`. Each is a pure
  `unit -> string`. On Windows they use `%APPDATA%`/`%LOCALAPPDATA%`;
  on POSIX, the `$XDG_*` variables with the usual fallbacks such as
  `~/.config`
- `cd dir` changes the session's directory (relative to the current
  one; `cd` alone goes home) and returns the absolute path it moved
  to. `pwd` is the current directory as a `string`, read at the moment
  it is used
- no `&&` — write two statements, or chain them with
  [`| and` / `| or`](#exit-codes)
- no redirects — `>` is passed as a literal argument, with a warning
  that suggests `File.write`

When you need bash behaviour, run bash: `sh -c "the line"`. Inside
that quoted string, `$w` is a shell variable, not a weir binding — to
pass a weir value, interpolate it (`sh -c $"echo {w}"`).

## Splices

`$name` splices a binding, `(expr)` splices an expression, and `$@xs`
splats a seq, so N elements become N words:

```weir
let marker = "ref"
echo tagged $marker (40 + 2)
echo files: $@(["a.txt"; "b.txt"])
```

A splice can also read a record field path — `$cli.tag`,
`$cli.out.dir`, `$@cli.files`, or a dynamic head `^$cli.bin`. It is
still one word (or one program, for a head). Only an identifier
extends the path, so `$x.` and `$x/y` are refused as glued words. This
means bash's `$f.bak` reads a field; on a string it is an error that
suggests `$"{f}.bak"`.

```weir
type Cli = { tag: string; bin: string }
let cli = { tag = "v1"; bin = "echo" }
^$cli.bin release $cli.tag
```

Typed values do not convert to argv implicitly. Splicing a `Duration`
is an error that suggests the explicit forms (`Duration.toMillis d` or
`show d`). A `Secret` does splice in the clear — passing it to a
command is what the type is for — but it is refused in interpolation
and at the serialization boundaries.

## Pipes

The operator depends on what is on the right: `|` feeds a program,
`|>` applies a function. Using the wrong one is an error that names
the right one:

```weir
git ls-files | sort | head -1
git ls-files |> Seq.length |> show |> print
```

A value on the left of `|` becomes the program's stdin:

```weir
["b"; "a"] | sort
```

## Streaming, capture, and the markers

A command written as a statement on its own streams its output. With
a `let` in front, the output is captured instead, as a `seq<string>`
with one element per line, and nothing is streamed. A bare command is
an ordinary statement in any block and any `match` case, so most
command lines need no marker at all:

```weir
let files = git ls-files
print $"{files |> Seq.length} tracked"

if 2 > 1 then
    git status --porcelain
    print (($(git rev-parse HEAD) |> Seq.head)[..6])
```

`$(...)` captures a command chain where a bare command can't go:
inside an expression such as an interpolation hole, a record or a
splice (as in the example above). A command line runs to the end of
the line — `;` there is just an argv word — so each command needs a
line of its own.

Environment variables for a single command go before it, as in bash:
`EDITOR=nano git commit`. Each stage of a pipeline takes its own; after
the program name, `CC=gcc` is an ordinary argument. An env overlay
bound to `e` applies to a block of commands with `within env e`, to a
capture with `$e(...)`, and combines with a prefix — where both set the
same name, the prefix wins. There is no `!` negation; use the word
`not`. To choose between two known tools, branch on the whole command
line. When the program really is a runtime value, run it with a
dynamic head.

## Dynamic heads

`^$name` runs the program named by a string value — the same
force-external `^`, applied to a runtime string. The value is always
one program name and is never re-parsed: a value containing spaces is
one (odd) program name, the arguments after it splice as usual, and
nothing is globbed or word-split. Use it to dispatch to plugins or
callbacks without `sh -c`:

```weir
let tool = "printf"
^$tool dyn-head-ok
```

A string-typed capture works directly as a head (`^$(… | line)`). A
seq-typed value is refused, because one program has to be chosen and
weir never picks a line for you:

```weir-error
^$(git branch) status // which line is the program? bind and pick first
```

The program is looked up when the line runs, not at check time.
`weir check` reports no command-not-found for a dynamic head, since
there is nothing to look up yet. A missing program is a run-time error
that points at the line and names the value, and `weir check --can`
reports the head as not statically known (`--strict` treats it like
other sites it cannot see through). Reifiers, pipes, captures and env
overlays work exactly as with a literal head:
`^$tool build | complete` gives you the computed program's exit.

## Exit codes

A failing command raises an error when its output is read. A **reifier** —
a `|` stage after the command — turns the run into a value instead.
Where the command's output goes follows from what you get back: it is
captured when the value contains it, streams to the terminal when you
only get the exit status, and is discarded by `succeeds`:

| form | output | result |
|---|---|---|
| `cmd \| succeeds` | silent | `bool` — `exitCode == 0`, exactly |
| `cmd \| complete` | captured | `{ exitCode; stdout; stderr }` |
| `cmd \| orFail "msg"` | streams | unit; raises `msg (exit N)` on nonzero |
| `cmd \| exitCode` | streams | the code as `int`; never raises |
| `cmd \| line` | captured | the one trimmed stdout line, a `string`; raises on nonzero or 0-or-2+ lines |
| `cmd \| text` | captured | the whole stdout as one `string`, trailing blank lines dropped; raises on nonzero |
| `cmd \| exec` | the child's | never returns — the command replaces the weir process |

```weir
let r = sh -c "echo out; exit 3" | complete
print $"exit {r.exitCode}, said {r.stdout |> Seq.head}"
```

A reifier can also follow a whole chain, `a | b | c | line`. The
chain's exit code is that of the leftmost failing stage (where the
problem began), or 0. A `complete` record holds the last stage's stdout
and every stage's stderr, in stage order. When a later stage stops
reading early (`git log | head -1`), the earlier stage is told to stop,
and its exit does not count as a failure. Only `exec` needs a single
command, because a pipeline cannot replace the process (use
`sh -c "a | b" | exec`).

```weir
let newest = sh -c "printf 'b\na\n'" | sort | head -1 | line
print newest
```

`exitCode` is an error where its result would be captured or thrown
away, and the error explains what to do:

```weir-error
sh -c "exit 3" | exitCode // a bare statement discards the code — bind or match it
```

The last three rows use the same pipe-stage spelling but are not
about the exit code. `cmd | line` captures a command's single trimmed
stdout line as a `string` —
`let sha = git rev-parse HEAD | line` replaces the
`$(cmd) |> Seq.exactlyOne` capture; it raises on a nonzero exit and
on zero or two-plus lines, and works with an env overlay
(`$e(cmd | line)`) and with a value piped in (`xs | grep foo | line`).
`cmd | text` is its multi-line sibling: the whole stdout as one
`string` (lines joined with newlines, trailing blank lines dropped,
as bash's `$(…)` does) — `let notes = git log -1 --format=%B | text`.
`cmd | exec` replaces the weir process with the command
(POSIX `execve`; Windows spawns, waits, and exits with the child's
code). The command keeps weir's pid, so a container entrypoint
receives signals directly. Like `fail` and `exit` it never returns, so
it is allowed as a bare statement. It accepts a literal or dynamic
(`^$cmd`) head and an env overlay (`$e(cmd | exec)`). It refuses piped
stdin, since no parent would remain to feed the new process, and it is
not allowed inside a `plan` block.

Chaining on the exit is `| and` / `| or` — bash's `&&`/`||`.
`cmd | and next` runs `next` only if `cmd` succeeded; `cmd | or next`
runs it only if `cmd` failed (there the nonzero exit is the branch,
not a raise). Both stream and yield unit, and the right-hand side is
a full command line, so they chain (`mkdir d | and cd d | and build`)
and a builtin like `cd` works on the right. They are right-associative:
`a | or b | or c` is `a | or (b | or c)`. That differs from bash, which
is left-associative, for chains that *mix* `and` and `or`; split mixed
logic across lines when precedence matters. The left side of `| or`
must be a single external command, because a builtin raises an error
rather than returning an exit code.

```weir
sh -c "exit 1" | or echo "fell back"
echo built | and echo linked
```

## Signatures

`weir check` looks up every command named literally in a script. A
declared signature (`#sig tool`, generated by `weir add sig`) extends
the check to the tool's flags. The details are on the
[tooling page](../tooling.md#command-signatures).
