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

`Path.which name` answers which file a command name would run: `Some`
its full path, or `None` when nothing on PATH has that name. It looks
the name up exactly as running it would, so inside `within env
PATH=…` it searches that PATH (and PATHEXT on Windows):

```weir
match Path.which "git" with
| Some p -> print $"git is {p}"
| None -> print "git is not installed"
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

What command lines don't do:

| bash | weir |
|---|---|
| `\` escapes | an error showing the fix: `\;` is `";"`, a literal backslash is `"\\"`, `\ls` is `^ls` |
| glob expansion | `Path.glob` |
| `$VAR` | splice a weir binding |
| `&&` | two statements, or [`\| and` / `\| or`](#chaining) |
| `>` redirects | passed as a literal argument, with a warning suggesting `File.write` |

The one expansion is `~`, typed directly. An unquoted word that is `~`
or starts with `~/` is your home directory, resolved when the line
runs: `cat ~/.bashrc`, `~/bin/tool`, `cd ~`. Quoted `"~/x"`,
interpolations and spliced values stay literal (weir never re-reads
data as syntax). `~user` is not expanded.

In an expression, use `Path.home ()`: `File.read $"{Path.home
()}/.bashrc"`. For the XDG directories there are `Path.configHome`,
`Path.stateHome` and `Path.cacheHome`, each `unit -> string`. On
POSIX they read `$XDG_*` with the usual fallbacks (`~/.config`); on
Windows, `%APPDATA%`/`%LOCALAPPDATA%`.

`cd dir` changes the session's directory (relative to the current one;
`cd` alone goes home) and returns the absolute path. `pwd` is the
current directory as a `string`, read when used.

If one command's arguments and environment exceed the OS limit
(Linux's argument-list limit), it fails with an error giving their
size. Pass big data on stdin (`xs | cmd`) or through a file.

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

A splice can read a record field path: `$cli.tag`, `$cli.out.dir`,
`$@cli.files`, or a dynamic head `^$cli.bin`. It is still one word (one
program, for a head). Only an identifier extends the path, so `$x.`
and `$x/y` are refused as glued words, and bash's `$f.bak` on a string
is an error suggesting `$"{f}.bak"`.

```weir
type Cli = { tag: string; bin: string }
let cli = { tag = "v1"; bin = "echo" }
^$cli.bin release $cli.tag
```

Typed values don't convert to argv implicitly: splicing a `Duration` is
an error suggesting `Duration.toMillis d` or `show d`. A `Secret` does
splice, in the clear, since that's what it's for; interpolation and
serialization refuse it.

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

A command as a statement streams its output. With `let` in front, the
output is captured as a `seq<string>`, one element per line, and
nothing streams. A bare command works in any block and any `match`
case, so most command lines need no marker:

```weir
let files = git ls-files
print $"{files |> Seq.length} tracked"

if 2 > 1 then
    git status --porcelain
    print ($(git rev-parse HEAD | line)[..6])
```

`$(...)` captures a command chain inside an expression, where a bare
command can't go: an interpolation hole, a record, a splice (as
above).

A command line runs to the end of the line; `;` there is just an argv
word. One command per line.

A capture holds the whole output in memory and stops with an error
past about 2 GiB. Stream bigger output instead: iterate its lines, or
`File.write` them and read back what you need. `| succeeds` keeps no
output, so it has no limit.

### Environment

Variables for one command go before it, as in bash: `EDITOR=nano git
commit`. Each pipeline stage takes its own. After the program name,
`CC=gcc` is an ordinary argument.

An overlay bound to `e` applies to a block with `within env e` and to
a capture with `$e(...)`. It combines with a prefix; where both set a
name, the prefix wins.

### Choosing a program

There is no `!` negation; use `not`. To pick between two known tools,
branch on the whole command line. When the program is a runtime value,
use a dynamic head.

## Dynamic heads

`^$name` runs the program named by a string: the force-external `^`
applied to a runtime value. The value is one program name, never
re-parsed. Spaces in it are part of the name; nothing is globbed or
split. Arguments after it splice as usual. Use it for plugins or
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

The program is looked up when the line runs:

- `weir check` reports no command-not-found for it.
- A missing program is a run-time error at the line, naming the value.
- `weir check --can` reports the head as not statically known;
  `--strict` treats it like other opaque sites.

Reifiers, pipes, captures and env overlays work as with a literal
head: `^$tool build | complete` gives the computed program's exit.

A literal path head that doesn't exist yet (`./gen.sh`) is a
command-not-found warning at check time and an error at run time,
before anything runs. If the script creates the file itself, use a
value: `let tool = "./gen.sh"`, then `^$tool`.

<!-- the section's earlier name; kept so published #exit-codes links land here -->
<a id="exit-codes"></a>

## Reifiers and chaining

A failing command raises when its output is read. A **reifier**, a `|`
stage after the command, turns the run into a value instead. The
output is captured when the value contains it, streams when you only
get the exit status, and is discarded by `succeeds`:

| form | output | result |
|---|---|---|
| `cmd \| succeeds` | silent | `bool` — `exitCode == 0`, exactly |
| `cmd \| complete` | captured | `{ ok; exitCode; stdout; stderr }` — `ok` is `exitCode == 0`, exactly |
| `cmd \| orFail "msg"` | streams | unit; raises `msg (exit N)` on nonzero |
| `cmd \| exitCode` | streams | the code as `int`; never raises |
| `cmd \| line` | captured | the one trimmed stdout line, a `string`; raises on nonzero or 0-or-2+ lines |
| `cmd \| text` | captured | the whole stdout as one `string`, trailing blank lines dropped; raises on nonzero |
| `cmd \| exec` | the child's | never returns — the command replaces the weir process |

```weir
let r = sh -c "echo out; exit 3" | complete
print $"exit {r.exitCode}, said {r.stdout |> Seq.head}"
if r.ok then print "clean" else print "a tool whose nonzero codes mean something reads the code"
```

Only `| complete` makes a `Completed`. A record literal or `{ r with …
}` of one is a check error naming where the value comes from. Same for
`FileRow` (from `ls`, `Dir.stat`, `File.stat`) and `HttpResponse`
(from `Http.send`). Functions over their fields are row-typed, so a
test passes an anonymous record with just the fields read:

```weir
let codeOf { exitCode = c } = c
print (codeOf {| exitCode = 3 |})
```

```weir-error
let r = { exitCode = 3; stdout = []; stderr = [] }
print (show r.exitCode)
```

A reifier can follow a whole chain, `a | b | c | line`:

- The exit code is the leftmost failing stage's (where the problem
  began), or 0.
- `complete` holds the last stage's stdout and every stage's stderr,
  in stage order.
- When a later stage stops reading early (`git log | head -1`), the
  earlier one is told to stop, and its exit isn't a failure.
- `exec` needs a single command; a pipeline can't replace the process.
  Use `sh -c "a | b" | exec`.

```weir
let newest = sh -c "printf 'b\na\n'" | sort | head -1 | line
print newest
```

`exitCode` is an error where its result would be captured or thrown
away, and the error explains what to do:

```weir-error
sh -c "exit 3" | exitCode // a bare statement discards the code — bind or match it
```

### `line`, `text` and `exec`

The last three rows use the same `|` spelling but aren't about the exit
code:

- `cmd | line` — the single stdout line, trimmed, as a `string`. It
  raises on a nonzero exit, or if there are no lines or more than one:
  `let sha = git rev-parse HEAD | line`.
- `cmd | text` — all of stdout as one `string`, with trailing blank
  lines dropped, like bash's `$(…)`. It raises on a nonzero exit:
  `let notes = git log -1 --format=%B | text`.
- `cmd | exec` — replaces the weir process with the command (`execve`;
  on Windows weir runs it, waits, and exits with its code). See below.

All three accept an env overlay (`$e(cmd | line)`), and `line` and
`text` accept piped input (`xs | grep foo | line`).

#### `exec`

The command keeps weir's pid, so as a container entrypoint it gets
signals directly. It never returns, so it can stand alone as a
statement. It starts in the session's current directory (a `cd`
before it counts).

It is refused:

- with piped input
- inside `plan`
- inside any `within` block (`tmp`, `proc`, `lock`, `serve`, `cd`,
  `env`, bare `within … always`), at check time: the replaced process
  would skip the cleanup. Close the scope first.
- in a function that execs, called from a scope, an `always` cleanup
  or a parallel arm (`pfor`, `Seq.pmap`, `piter`, `pfirst`); the check
  follows calls.

A helper the check can't see (passed as an argument, or imported) is
refused when it runs, with `'exec' while a scope is open (a within or
always block) is refused` or `'exec' inside a parallel arm (pfor,
Seq.pmap, Seq.piter, Seq.pfirst) is refused`.

If the exec itself fails (command not found), its env prefix and
directory are undone and the script carries on as before.

### Chaining

- `cmd | and next` is bash's `&&`: `next` runs only if `cmd`
  succeeded. If `cmd` fails, that's an error and the script stops.
- `cmd | or next` is `||`: `next` runs only if `cmd` failed. The
  failure picks the branch instead of raising.

Both stream and return unit. The right side is a whole command line,
so a builtin like `cd` works there:

```weir
sh -c "exit 1" | or echo "fell back"
echo built | and echo linked
```

- **One word per chain.** `a | and b | and c` and `a | or b | or c` are
  fine; `a | and b | or c` is an error. For "if a, then b, else c",
  write `if a | succeeds then b else c` (why mixing is refused:
  [coming from bash](../COMING-FROM.md#coming-from-bash--posix-sh)).
- **The left of `| or`** is an external command or a pipeline, which
  fails with its leftmost failing code. A builtin can't be there: it
  raises instead of returning an exit code.
- **No reifier at either end.** `a | and b | complete` would capture
  only `b`, and `a | succeeds | and b` has no exit left to branch on;
  capture on a separate line instead. `| exec` may end a chain, since
  it hands over.

```weir
if sh -c "exit 1" | succeeds then echo "took the then" else echo "took the else"
```

## Signatures

`weir check` looks up every command named literally in a script. A
declared signature (`#sig tool`, generated by `weir add sig`) extends
the check to the tool's flags. The details are on the
[tooling page](../tooling.md#command-signatures).
