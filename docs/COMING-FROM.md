# Coming from another language

You already know how to write scripts; this page shows what changes.
For each language: what you write today, what you write in weir, and
the one thing most likely to trip you up. The [guide](GUIDE.md)
teaches weir from scratch; this page translates.

## Coming from bash / POSIX sh

The biggest change: `$x` always passes exactly one argument, and
`$@xs` passes one argument per element. Nothing is ever split on
spaces or joined back together. The quoting habits you spent years
building (`"$x"`, `"$@"`, `IFS`) are simply how weir always behaves,
so word splitting, and the injection bugs that come with it, can't
happen.

| bash | weir |
|---|---|
| `out=$(git branch)` | `let out = git branch` gives a `seq<string>`, one element per line |
| `for f in *.txt; do … done` | `for f in Path.glob "*.txt" do …` |
| `if grep -q pat f; then` | `if grep -q pat f \| succeeds then` |
| `cmd > out.txt` | `cmd \|> File.write "out.txt"` |
| `$?` | `cmd \| exitCode` (the output still prints; you get the code as an `int`) |
| `a && b` / `a \|\| b` | `a \| and b` / `a \| or b` |
| `FOO=1 cmd` | `FOO=1 cmd`, same as bash |
| `$(pwd)` | `pwd`, already a string |
| `cat <<EOF … EOF \| cmd` | `let lines = <<<` + an indented block, then `lines \| cmd` |
| `# comment` | `// comment`, on its own line or after code (with a space before it) |
| `cmd a \` + newline | indent the next line instead; a trailing `\` is an error that tells you so |
| `find … -exec cmd {} \;` | `find … -exec cmd {} ";"` (no backslash escapes in commands, so quote the word) |
| `\ls` (skip the alias) | `^ls` runs the program from PATH |
| `[ "$a" != "$b" ]` | `a <> b` (`!=` is an error that points you to `<>`) |
| `cp "$f" "$f.bak"` | `cp $f $"{f}.bak"` |

A few notes on those rows:

- `|` feeds a command; when the right-hand side is a function, use `|>`.
- `| succeeds` goes inline in an `if`. If you need the answer twice,
  bind it first: `let hit = grep -q pat f | succeeds`.
- A heredoc is an ordinary value. `$<<<` fills in `{holes}`; `$` is
  plain text in both forms.
- `$f.bak` reads a field named `bak` (like `$cli.tag` on a record), so
  on a string it's an error that suggests `$"{f}.bak"`.

```weir
let msg = "two words"
printf "[%s]" $msg

let branches = $(git branch) |> Seq.length
print $"branches: {branches}"
```

That `printf` gets one argument, brackets and all. Nothing is re-split.

**The one thing that will catch you out:** a capture gives you lines,
not a string. In bash, `$(cmd)` is one string with trailing newlines
stripped. In weir it's a `seq<string>`, one element per line. When you
pipe a value back into a command (`expr | cmd`), each element is
written as a line, newline included. So `["x"] | sha256sum` hashes
`x\n`: the same as `printf 'x\n' | sha256sum`, not
`printf 'x' | sha256sum`. This is one of the few places where weir is
less convenient than bash, so take care when hashing. When you want a
single string, use `cmd | text` (all of stdout) or `cmd | line`
(exactly one line).

`set -e` is always on and has no name. A nonzero exit raises an error
(once the command's output has been read to the end) and stops the script,
pointing at the failing line. To inspect a failure instead, turn it
into data: `cmd | complete` gives `{ exitCode; stdout; stderr }`. That
record is also how you get at stderr where bash would use `2>&1`.

```weir-error
// set -e is unconditional and unnamed: a nonzero exit raises
sh -c "exit 3"
print "unreached"
```

**Not here, and what to write instead:**

- Glob expansion in commands. `Path.glob "src/**/*.c"` is a function
  that returns a seq. To pass the matches as arguments, splat them:
  `git add $@(Path.glob "*.txt" |> Seq.freeze)`. (`~` does expand.)
- Redirects. `>` and `>>` are passed to the command as plain
  arguments, with a warning that suggests `cmd |> File.write "out.txt"`
  (or `File.append` for `>>`).
- `;` chaining. Write one command per line. A `;` in a command line is
  passed as a plain argument (with a warning). Since a failure already
  stops the script, consecutive lines behave like an `&&` chain. For a
  one-liner, `&&` and `||` are `cmd | and next` and `cmd | or next`.
  A chain repeats one word (`a | and b | and c`, `a | or b | or c`);
  mixing them is an error. Mixed, it would not mean what bash means:
  weir's right side is the rest of the line, so `a | and b | or c`
  would group as `a | and (b | or c)` where bash reads
  `(a && b) || c`, and a failed `a` stops a weir script where bash
  carries on. bash's `a && b || c` is a trap there too (`c` also runs
  when `b` fails); its intent, "if a then b else c", is
  `if a | succeeds then b else c`.
- `$VAR` expansion. Use `Env.get "VAR"` (an `Option<string>`) and
  splice the result. To set a variable for one command,
  `NAME=value cmd` works as in bash.
- `while`. weir's loops are bounded:
  [`retry` and `poll`](GUIDE.md#retrying-and-polling)
  (for example `retry attempts=5 delay=30s` followed by a block).
  Running out of attempts raises an error, and there is no way to
  write an unbounded loop.
- Anything else from bash. `sh -c "the bash line"` is an ordinary
  command. Inside that quoted line, `$w` is sh's variable, not weir's,
  so interpolate first: `sh -c $"echo got-{w}"`.

## Coming from fish

Fish already fixed the parts of bash that weir rules out: variables
are lists, expansion never splits on spaces, and `(cmd)` splits on
newlines rather than `IFS`. weir keeps all three habits and adds
types. A capture is a `seq<string>`, each list element is exactly one
argument, and string functions live in a module (`Str`) rather than
the `string` command.

| fish | weir |
|---|---|
| `set out (git branch)` | `let out = git branch`, a `seq<string>` with one element per line (the newline split you already expect) |
| `echo $files` (one word per element) | `echo $@files`; spreading a list into arguments is explicit, and `$x` is always exactly one argument |
| `set files *.txt` (glob in argv) | `let files = Path.glob "*.txt"`, a function that returns a seq |
| `string split , $s` / `string trim` | `Str.split "," s` / `Str.trim` |
| `string match -r 'v(\d+)' $s` | `match s with \| Regex @"v(\d+)" v -> v \| _ -> "0"` (`v` is typed, and the no-match arm is required) |
| `if test -f $path` | `let ok = test -f $path \| succeeds` then `if ok then` |
| `$status` | `cmd \| exitCode` (the output still prints; you get the code as an `int`) |
| `count $files` | `files \|> Seq.length` |
| `function deploy; …; end` (`$argv`) | `let deploy target = …`, with named, typed parameters instead of `$argv[1]` |
| `for f in (cat list.txt); …; end` | `for f in File.read "list.txt" do …` |

```weir
let words = ["a b"; "c"]
printf "<%s>" $@words

let lines = $(printf "one\ntwo") |> Seq.map Str.toUpper
lines |> Seq.iter print
```

That `printf` gets `a b` as one argument, then `c`: fish's list rule,
kept. The capture splits on newlines, like fish's command
substitution, but now it has a type.

**The one thing that will catch you out:** in fish, an unset or empty
variable expands to zero arguments and the command runs anyway. weir
rejects the script before anything runs. `$x` is always exactly one
argument: an empty seq can't silently drop out of a command line, an
unbound name is a check error, and a seq only spreads into several
arguments through `$@x`. Where fish would quietly change how many
arguments a command gets, weir makes that count visible in the code.

```weir-error
// fish: an unset $nope expands to nothing and echo runs bare;
// weir: an unbound name is a check error before line one
echo $nope
```

**Not here, and what to write instead:**

- Universal variables (`set -U`) and interactive config. weir is not
  your login shell, but its REPL has an init file:
  `~/.config/weir/init.weir` (or `$XDG_CONFIG_HOME/weir/`, and
  `%APPDATA%\weir\` on Windows). The file starts with `#init` and holds
  only declarations (`let`/`type`, for example prompt helpers), `#alias`
  lines, and one `#session { … }` block with the settings `cwd`, `env`,
  `logLevel`, `echoCap` and `prompt`. The `env` setting sets
  environment variables once per session, and `Env.vars`, every
  started process and `within env` all see them. That's the closest
  thing to `set -U`. A *script's* configuration is a separate matter:
  read it with `Env.load`, `Args.load` or `from json T`, which give it
  a type as it comes in.
- Autoloaded functions (`~/.config/fish/functions`). Use
  `import "./lib.weir" as Lib`, so the script names what it depends on.
- `and` / `or` command chaining. Write `cmd | and next` /
  `cmd | or next`. A nonzero exit already raises an error, so
  consecutive lines chain by default. For a true/false answer, use
  `cmd | succeeds`.
- Abbreviations and `alias`. In a script, a short name is a `let`;
  nothing rewrites your command lines. In the REPL, `#alias k = kubectl`
  gives you a command alias.
- Globs expanding in argv. `Path.glob` is a function; splat a batch
  with `git add $@(Path.glob "*.txt" |> Seq.freeze)`.

## Coming from F#

weir looks like F# on purpose (pipelines, records, unions, `match`
including `function`, indentation-based blocks), but it is not F#.
Every difference is listed in
[tests/fidelity/divergences.md](../tests/fidelity/divergences.md).
The short version: no mutation, no exceptions, no classes or objects,
no async (parallelism is `Seq.pmap`/`piter`; the TypeScript section
shows how), no computation expressions, no `let rec`, and no implicit
widening.

| F# | weir |
|---|---|
| `if x = y then` | `if x == y then`; `=` is only for `let` and record fields |
| `printfn "%d files" n` | `print $"{n} files"`; there's no printf family, so use interpolation |
| `try … with` | `e \|> try` (an `Option`) or `e \|> try result` (a `Result<'T, string>`); for a command, `cmd \| complete` |
| `try … finally` | a bare `within` + `always` block |
| `while` / `let rec` | neither exists; `let rec` is a parse error (`'rec' is a keyword`) |
| `open Seq` | no `open`; names are always qualified. Share code with `import "./lib/x.weir" as X` |
| `.fsi` / `.mli` signature files | write the signature inline, `let f : int -> int` (no `=`), above the definition |
| `\| MyMod.Case v ->` qualified cases in patterns | write the case bare: `\| Case v ->` (`\| X.Case v ->` is a parse error) |
| `$@"…"` / `$$"""…"""` | only `$"""…{hole}…"""`; a literal brace in `$"…"` is `{{` |
| `[\| 1; 2 \|]` arrays, `list` | one sequence type, `seq<'a>`; `[1; 2]` literals are eager |
| `{\| ip = "x" \|}` anonymous records | the same syntax and types, with a few limits (below) |
| `(+)` and `(>) 10` | `(+)` works (`Seq.reduce (+)`), but partially applied operators don't; write the lambda |

The longer story behind some of those rows:

- `within` + `always` runs the cleanup however the block exits:
  normally, by error, by `exit`, or on a signal. For resources, use a
  `within` kind instead (`within tmp`, `within proc`, `within lock`).
- Instead of `while` and recursion: `retry`/`poll` for loops that wait
  on a condition (always bounded), pipelines and `Seq.fold` to
  transform or accumulate, and `for … do` (the same as `Seq.iter`) for
  effects.
- The signature line is also what exports a function: members without
  one are private to the module. The definition itself stays free of
  type annotations.
- In a pattern, the type of the value being matched tells weir which
  union a case belongs to, imported unions included.
- There's one raw interpolated string form; F#'s scheme of multiple
  `$` signs to change the brace count doesn't exist. Every string
  literal is single-line (multi-line text uses a `<<<` block).
- An anonymous record literal needs concrete field types:
  `fun x -> {| a = x |}` is rejected, though F# accepts it. There's
  also no empty `{||}` (F# allows it), no punning, and no
  `{| r with … |}`.
- `(>) 10` means `fun x -> 10 > x`, which almost everyone reads the
  wrong way round, so weir doesn't allow partially applied operators.

```weir
let same x y = x == y
print $"{same "a" "a"} {same 1 2}"
```

Equality, `show` and sorting work generically, through constraints
that weir infers. The set of constraints is fixed by the compiler; you
can't define your own type classes.

**The one thing that will catch you out:** `==` versus `=`, from the
first minute. `==` is weir's equality and an error in F#; `=` is F#'s
equality and weir's binding. (In weir, `0.1 == 0.2` is a check error
either way: floats can't be compared with `==`, because they're
finite-only here. Use `Float.near a b eps`. See
[GUIDE.md](GUIDE.md#rates-and-percentages-floats-finite-only).)

Equality on collections surprises you in the other direction. weir's
`==` rejects seqs at check time. That looks like a limitation until
you see what F# does: `=` on a `seq<'T>` compiles, but the answer
depends on the runtime type. It's structural if the value happens to
be a list or array, and reference equality for a computed seq, so
`Seq.map id [1;2] = Seq.map id [1;2]` is `false`. weir would rather
refuse than give an answer that depends on where the value came from.
Compare what you actually mean: `Seq.length`, a `Str.join`ed string,
or an element-by-element check.

What F# only warns about, weir treats as an error, reported before
anything runs: a non-exhaustive match, a discarded non-unit value, an
unreachable arm below a catch-all, and a `|` that's off by one column.

```weir-error
// F# warns FS0025 and runs anyway; weir refuses before line one
let word = match 1 with | 1 -> "one"
print word
```

**Not here, and what to write instead:**

- Computation expressions (`seq { }`, `async { }`). Use pipelines;
  `[for x in xs -> e]` comprehensions do exist.
- Async/task. Processes are the way to run things concurrently.
  `Seq.pmap`/`Seq.piter` run work in parallel (bounded, results in
  order), on threads inside weir rather than as child processes.
- Classes, interfaces, members. Use records and functions.
- `mutable`, `<-`. Use copy-and-update: `{ r with F = v }`.
- Type annotations on parameters, `(e : ty)` ascription. weir infers
  types; to pin one, use an expression that forces it (`x + 0` makes
  `x` an int). Higher-order parameters need no annotation either
  (`let apply f x = f x`).

## Coming from PowerShell

The first thing to know: weir does carry structured values through
pipelines. `ls` yields typed rows, `from json T` gives a record of a
shape you declared (`from jsonl T` gives a stream of them), and
`_.name` reads a field of the piped value. It works on any record that
has the field, which is what you'd expect from `Where-Object`, and not
what you'd expect from a statically typed language.

| PowerShell | weir |
|---|---|
| `Get-ChildItem \| Where-Object Length -gt 1kb` | `ls \|> Seq.where (fun f -> f.bytes > 1KiB)` |
| `… \| ForEach-Object { $_.Name }` | `… \|> Seq.map _.name` |
| `ConvertFrom-Json` | `\|> from json T`, against a shape you declared |
| `"$($x.Name) ready"` | `$"{x.name} ready"` |
| `$LASTEXITCODE` | `cmd \| exitCode` |

```weir
git status --porcelain |> Seq.choose (fun l -> match l with | Regex @"^.. (.*)$" path -> Some path | _ -> None) |> print
```

**The one thing that will catch you out:** there are two pipes, not
one. PowerShell has a single object pipeline: cmdlets take and
produce objects. In weir, `|` carries text to and from external
programs, and `|>` carries values between functions. What's on the
right decides which one you need, and using the wrong one gives an
error that names the other. Turning text into values is up to you.
PowerShell's cmdlets do that conversion for you, but weir runs
ordinary programs that print text, and `from json T` / `from yaml T`
are where that text becomes typed data, in the shape you declared.

The trade-off cuts both ways. PowerShell's types stop at the cmdlet
boundary: a plain `.exe` gives you strings, and `Where-Object Length`
looks up the property at run time, so a misspelled name matches
nothing and silently filters out everything. In weir, types start
right at the boundary with external programs: `from json T` is checked
before anything runs, and a misspelled field is a check error with a
did-you-mean.

**Not here, and what to write instead:**

- The verb-noun cmdlets, parameter sets and providers. `Seq.*`,
  `Str.*`, `Path.*` and `File.*` are plain functions, and commands are
  just commands.
- `$x` as a variable that also gives you properties. In weir, `$`
  only splices a value into a command or a string; expressions use the
  bare name.
- `trap`/`try`. See the exit-code forms in
  [GUIDE.md](GUIDE.md#exit-codes-from-command-to-value).
- On Windows, bare command names are looked up through the full
  `PATHEXT` list, as the platform itself does. `.bat` and `.cmd` files
  are a known limit: the batch interpreter re-parses its command line,
  so the one-splice-one-argument guarantee holds only up to that
  hand-off. Native executables receive their arguments exactly. See
  `SECURITY.md`.

## Coming from Nushell

Nushell is weir's closest neighbour. A structured, typed pipeline
where `ls | where size > 1kb | length` carries real values rather than
text to re-parse is exactly the idea weir shares. Most of your habits
carry over, safety included: on both, a spliced variable is one
argument (`^echo $x` with `$x = "a b"` passes a single word, and so
does weir's `echo $x`), so neither has word-splitting injection bugs.

| Nushell | weir |
|---|---|
| `ls \| where size > 1kb` | `ls \|> Seq.where (fun f -> f.bytes > 1KiB)` |
| `$rows \| each { \|r\| $r.name }` | `rows \|> Seq.map _.name` |
| `'…' \| from json \| get host` | `… \|> from json T`, then `r.host` |
| `^cmd \| complete \| get exit_code` | `cmd \| complete`, then `.exitCode` |
| `$"(…)"` | `$"{…}"` |

```weir
ls |> Seq.where (fun f -> f.bytes > 1KiB) |> Seq.length |> print
```

**Where the two part company: when the check happens.** Nushell
parses and type-checks before running. A bad operation like `"s" + 5`
is a parse error even in a branch that never runs, which is more than
most shells do. But the check doesn't cover commands or data shapes.
An external command is looked up only when execution reaches it, so a
script can run its first half and then fail on a missing command
(`print "before"; frobnicate` prints `before`, then errors). Parameter
types are advisory (`def greet [name: string]` accepts `greet 5`). And
`from json` gives a dynamic record, so a missing field is a run-time
`column_not_found` error at the line that reads it.

weir moves all three to check time. Every command name is resolved
before the first line runs, including those in branches that never
run, and `weir check --can` lists every command the script could run.
So a missing tool never lets the first half run: the same
`print "before"` / missing-command file is refused with nothing
printed. `from json T`, `Args.load T` and `yaml schema=` name the
shape, so a missing field, an unknown flag or a misspelled manifest
key is a check error with a did-you-mean, not a surprise at run time.

**The trade, honestly:** nu is an interactive shell first, with a REPL
you live in, a plugin system and a large library of structured
commands. weir isn't trying to be that (its REPL is for trying out
weir code, not for living in). What weir adds is the up-front check.
When a nu one-liner grows into a script, it fails at the line that
breaks; the weir version fails when you check it, before any side
effect. If you want a shell, use nu. If you want the script it turned
into, checked as a whole, use weir.

## Coming from Python

Start with what a Python script can't do. First, the whole weir file
is checked before the first line runs, including that every command
you call exists. (`weir check` only warns about a missing command, so
you can keep editing scripts for tools you haven't installed; running
the script refuses before doing anything.) Second,
`subprocess.run("… " + arg, shell=True)` has no equivalent, and can't
have one: arguments are data, a splice is one argument, and no shell
ever re-parses your command line.

| Python | weir |
|---|---|
| `subprocess.run(["git", "add", f])` | `git add $f` |
| `f"{n} files"` | `$"{n} files"` |
| `with tempfile.TemporaryDirectory() as d:` | `within tmp d` + an indented block |
| `os.environ.get("PORT")` | `Env.get "PORT"`, or `Env.load Config` for typed settings (one error reports every bad field) |
| `argparse` | `Args.load Cli`; the flags come from a record you declare |
| `json.loads(...)` → dict soup | `\|> from json T` → your declared record |
| `requests.post(url, json=payload)` | `Http.send { Http.defaults with method = Post; url = u; body = Json (payload \|> to json) }` |
| `-7 % 3 == 2` (floored) | `-7 % 3 == -1` (truncated, as in F#, .NET and C) |

With `Http.send`, the response status is data you inspect, auth can be
a `Secret`, and the body is sent byte for byte. weir's `%` takes the
sign of the left operand; it agrees with Python whenever both operands
are positive.

```weir
type Cfg = { name: string; port: int }
let text = """{ "name": "api", "port": 8080 }"""
let cfg = [text] |> from json Cfg
print $"{cfg.name}:{cfg.port}"
```

**The one thing that will catch you out:** indentation matters, but
differently. No `:` opens a block; a block is the lines indented under
the line that starts it. A statement starts at column 0 and runs until
the next line at column 0, and blank lines inside a statement don't
end it. In practice: an `if` body is just indented lines, and the next
line back at column 0 is the next statement.

Python's `with` is weir's `within`, and it covers more than files.
`within tmp d` creates a fresh directory and removes it however the
block exits, errors included. `within cd` and `within env` scope the
working directory and the child processes' environment the same way.

```weir
within tmp d
    ["data"] |> File.write $"{d}/f.txt"
    let back = File.read $"{d}/f.txt"
    print (back |> Seq.head)
```

**Not here, and what to write instead:**

- `while`. `retry attempts=… delay=…` and `poll timeout=… interval=…`
  are the bounded loops, `for x in xs do` runs an effect per item, and
  transformations are pipelines.
- Classes. Use records and functions, and `{ r with F = v }` instead of
  changing attributes.
- Exceptions and `try/except`. `fail` stops the script. Turn an
  expression that may fail into data with `|> try` (an Option) or
  `|> try result` (`Ok`/`Error`), and a command with `| complete`.
- Dicts. `Map<string, T>` is for keys that are data (JSON objects keyed
  by ID, counters): `Map.ofPairs`/`get`/`tryGet`/`pairs`, string keys
  only, and `from json Map<string, T>` to read one. When you know the
  keys while writing the script, declare a record instead.
- `os.path`/`pathlib`. `Path.*` functions work on plain strings, using
  the platform's own path rules (`Path.combine`, `Path.stem`,
  `Path.glob`).

## Coming from TypeScript / Node scripting (zx, execa, bun $)

zx also calls itself "a real language for shell scripts", so the
differences are easy to see. There are two. weir checks the whole file
before running a line: types, fields, match coverage, and whether the
commands exist. zx finds a misspelled binary only when it reaches that
`await`, halfway through the deploy. And there's no runtime to
install: one static binary, millisecond startup, no `node_modules`,
no `package.json`.

| zx / Node | weir |
|---|---|
| ``await $`git status` `` | `git status` |
| ``$`git add ${file}` `` (zx escapes) | `git add $file`; always one argument, nothing to escape |
| `$.nothrow` / `.exitCode` | `cmd \| complete` / `cmd \| exitCode` |
| `await Promise.all(xs.map(f))` / `p-map` | `xs \|> Seq.pmap f` |
| `await Promise.any(xs.map(f))` | `xs \|> Seq.pfirst f` |
| `globby`, `fs/promises` | `Path.glob`, `File.*` / `Dir.*` |
| `zod` schema `.parse(...)` at runtime | `from json T` and vendored JSON schemas, checked before the script runs |
| `await fetch(url).then(r => r.json())` | `Http.get u \|> Http.expect \|> from json T` (raises on an error status); for the status as data, use `Http.send` |

`Seq.pmap` runs a bounded number of calls at once (`Seq.pmapWith n`
sets the limit) and returns results in input order; if calls fail, you
get the first failure in input order. `Seq.pfirst` returns the first
call to succeed. The rest are stopped (their process trees killed) and
their failures ignored; `Seq.pfirstWith n` sets the limit.

```weir
let target = "seed.txt"
git add $target
[1; 2; 3] |> Seq.pmap (fun n -> n * n) |> Seq.map show |> print
```

**The one thing that will catch you out:** the habit of gluing values
into template literals. In zx, `--file=${f}` interpolates into the
command string. In weir a splice is a whole argument, and gluing one
into the middle of a word is an error. Write `--file $f`, or build the
word first: `$"--file={f}"`.

```weir-error
// the zx habit: a mid-word splice is a hard error, not an escape
let f = "seed.txt"
echo --file=$f
```

**Not here, and what to write instead:**

- `async`/`await`. None, and none needed: I/O is synchronous from the
  script's point of view, and parallelism is `Seq.pmap`/`piter`. A task
  that truly needs async has outgrown a shell script.
- `try/catch`. End an expression with `|> try` (an Option) or
  `|> try result` (`Ok`/`Error`); for commands use the exit-code forms,
  and `| orFail "msg"` is the one-line assert.
- npm dependencies. `import "./lib/x.weir"` shares code between
  scripts; external tools stay external tools.

## Coming from Make

Start with what weir doesn't have. Make is a dependency graph with
rules for what's out of date; weir is a script. There's no
`target: prereq`, no timestamp comparison, no skipping work that's up
to date, so if that's what you're after, it isn't here. What you get
instead: real types, errors before any side effect, and none of the
recipe quirks: no tabs-versus-spaces, no new shell per line, no `$$`
escaping, no `.PHONY`.

| Make | weir |
|---|---|
| `$(VAR)` | `$var` |
| `$(shell git rev-parse HEAD)` | `let sha = git rev-parse HEAD \| line` |
| `$@`, `$<` automatic variables | ordinary named bindings (and see the false friend below) |
| `VAR = …` vs `VAR := …` | each `let` is evaluated once, in order, so there's no difference to make |
| `.PHONY: deploy` | not needed: a weir file is a script, not a set of targets |

```weir
let sha = git rev-parse --short HEAD | line
print $"building {sha}"
```

**The one thing that will catch you out:** in Make, every recipe line
runs in its own shell, so a `cd` on one line is gone on the next. A
weir file is one program: a `cd` lasts until you scope it
(`within cd "dir"` changes back when the block ends). And `$@` is a
true false friend: in Make it's the target, in weir it spreads a list
into arguments (`$@xs` passes one argument per element).

**Not here, and what to write instead:** targets ordered by
dependency. Keep the graph in Make (or a task runner) and call weir
from the recipe; the next section covers that.

## Coming from Just / Task / npm-scripts

Task runners are thin wrappers around shell strings, and weir is a
language you'd write the recipe body in. So it isn't weir versus just:
weir replaces the shell inside the recipe, and a `justfile` whose
recipe line is `weir deploy.weir --env prod` is a perfectly good end
state.

What weir adds inside a recipe: the whole script is checked before
anything runs; the script's own flags are declared, typed, and get a
generated `--help`; and arguments stay intact from start to finish.

```weir
type Cli = {
    [<Default "dev">]
    /// target environment
    env: string
}
let cli = Args.load Cli
print $"deploying to {cli.env}"
```

**What weir does not do:** task discovery, `--list`, ordering tasks by
their dependencies, a self-documenting menu. Those stay the runner's
job. This section is short on purpose: the two tools overlap little
and work well together.

## Coming from Ruby

A pleasant surprise first: weir's row polymorphism is the nearest
thing to duck typing, but static. `Seq.map _.name` works on any record
that has a `name` field, and it's checked before the script runs.

| Ruby | weir |
|---|---|
| `` `git status` ``, `%x{git status}` | `$(git status)`, a `seq<string>` of lines |
| `system("git", "add", f)` | `git add $f` |
| `Dir.glob("**/*.rb")` | `Path.glob "**/*.rb"` |
| `xs.map { \|x\| x.strip }` | `xs \|> Seq.map Str.trim` |
| `OptionParser` | `Args.load Cli` |

```weir
$(git log --oneline -1) |> Seq.map Str.toUpper |> print
```

**The one thing that will catch you out:** there are no trailing
blocks. A lambda is an ordinary argument in parentheses:
`xs.each { |x| … }` becomes `xs |> Seq.iter (fun x -> …)`. For a
multi-line body, end the line with `(fun x ->`, put the body in an
indented block below, and close it with `)`. The block-passing feel
survives in `retry`, `poll` and `within`, which each take an indented
block directly.

**Not here, and what to write instead:**

- Monkey-patching, `method_missing`, classes. Use records, functions,
  and the built-in modules (which you can't extend).
- `begin/rescue/ensure`. `within` scopes clean up when an error is
  raised too; turn a step that may fail into data with `| complete`.
- `rake`. See the task-runner section above.

## Coming from Perl

Your regex habits carry over well. weir's `Regex` pattern matches and
extracts in a single match arm. The pattern is compiled at check time,
so an invalid regex is a check error, and the number of names you bind
must match the number of capture groups.

| Perl | weir |
|---|---|
| `if (/pat/)` on `$_` | `if s \|> Str.isMatch @"pat"` (no `$_`; you always name the value) |
| `($k) = $l =~ m/(\w+)=/` | `match l with \| Regex @"(\w+)=" k -> …` |
| `s/old/new/` (literal) | `Str.replace "old" "new" s` (literal text; for regex substitution, use `sed`) |
| `qx/git status/`, backticks | `$(git status)` |
| `@ARGV` | `Self.args`, or `Args.load Cli` for typed flags |

```weir
let ver = match "release v42 ready" with | Regex @"v(\d+)" n -> n | _ -> "0"
print $"version {ver}"
```

`Str.rmatch` returns an `Option`, and `Str.rmatchAll` returns every
match (lazily; no matches gives an empty seq). The inline flags `(?s)`
and `(?m)` turn on DOTALL and MULTILINE. **Named groups `(?<name>…)`
are rejected**: you name captures in the binder instead
(`Regex @"(\w+)=(\d+)" (k, v)`), so the name sits next to the pattern
rather than inside it. Lookbehind (`(?<=`, `(?<!`) works.

**The one thing that will catch you out:** sigils. In Perl, `$`/`@`/`%`
mark what kind of variable you have. In weir, `$` splices a value into
a command line or a string, and ordinary variables have no sigil at
all. `use strict` is always on, with no name.

```weir-error
// a Regex pattern must be a raw string (@"a\+") — an ordinary string
// is rejected here, which is what makes the double-escape footgun
// unrepresentable rather than merely avoidable
let m = match "a+b" with | Regex "a\\+" () -> "hit" | _ -> "miss"
print m
```

**Not here, and what to write instead:**

- Implicit `$_`. Name the value; `_.field` and `_[0]` are the short
  forms for "the piped value's field / first element".
- `tr///` and regex `s///`. Pipe through `tr` or `sed`.
- `local` and dynamic scope. Bindings are lexical; `within env` sets
  environment variables for child processes within a block.

## The false friends, collected

The most useful rows on this page: the same spelling with a different
meaning.

| glyph | there | here |
|---|---|---|
| `$@` | Make: the target; bash: all args | spreads a list into arguments: `$@xs` passes one per element |
| `$x` | Perl/PowerShell: a variable (sigil for its kind / property access) | a splice: one argument, or a hole in a string |
| `` `${f}` `` | zx: interpolation, escaped for you | gluing a splice into a word is an error; a splice is a whole argument |
| `$()` | bash: capture as one string, trailing newlines stripped | capture as `seq<string>`, one element per line |
| `=` | F#: equality | binding only; equality is `==` |
| `\|` | F#: not a pipe (`\|>` is) | sends text to or from an external program; `\|>` is still the function pipe |
| `!` | bash: history / negation | not used: negation is `not`, and a command runs just by being written as a statement |
| `//` | C-family: always a comment | a comment only at line start or after whitespace; a standalone `//` argument needs quotes |

With `|` and `|>`, what's on the right decides which one you need. A
`//` inside a word, as in `http://a`, stays plain text.

## What nobody arrives knowing

Everything above is a translation. These are the things no other
language prepares you for, each with what it costs you.

- **The whole file is checked before anything runs**: types, match
  coverage, discarded values, and whether the commands exist. A
  broken script is refused before its first line, so it never dies
  halfway through its side effects. The cost: you can't run the
  working half of a broken script, and a missing tool blocks the run
  entirely (`weir check` reports it only as a warning, so you can keep
  editing).
- **One splice, one argument.** A splice is one argument, `$@xs` is
  one per element, and nothing is ever re-split, so shell injection
  can't be written at all, rather than being guarded against. The same
  goes for `yaml` blocks: splices go in as typed values, never as text,
  so YAML injection can't happen either. The cost: a standalone `//`
  argument reads as a comment (quote it), and gluing a splice into a
  word (`--file=$f`) is refused, so build the word explicitly.
- **Name lookup decides between command and expression.** A bare name
  at the start of a line is a command if it names one, and an
  expression otherwise. Your own bindings win over PATH, and `^ls` is
  the one way to force the external program. No syntax marks the
  difference. The cost: adding a binding with a command's name changes
  what a later line means. The lookup order matters, and the editor
  colors the first word of each line to show you which one you got.

```weir
let rows = ls |> Seq.length
print $"typed rows: {rows}"
^ls -a
```

- **`within` scopes.** Six kinds (`tmp`, `cd`, `env`, `lock`, `proc`,
  `serve`) plus a bare `within` with `always` for your own cleanup,
  in a language with no `defer`, no `try/finally` and no
  `IDisposable`. `within proc` replaces a backgrounded `&`: when the
  block ends, its process tree is killed and reaped. `within lock`
  replaces `flock`. Cleanup runs on normal exit, `fail`, `exit n`,
  SIGINT and SIGTERM alike; the only exception is `kill -9`, which
  nothing can catch (a `within lock` still frees, since the kernel drops
  the lock). The cost is the flip side of that guarantee: the
  process tree is killed with SIGKILL, children included, so a scoped
  child's own SIGTERM handler never runs. A process that must flush on
  shutdown should be a daemon, not a `within proc`.
- **`yaml` blocks** are literals that get checked like code. A
  structural error is a check error. Values YAML would misread are
  quoted when rendered, so `"no"` stays a string instead of becoming
  `false` (the "Norway problem"). And `yaml schema=<name>` validates
  against a vendored JSON schema at check time. The cost: only a
  subset of YAML is supported (anchors and flow style are rejected),
  splices are checked by type only (a string spliced where the schema
  has a `pattern` or `enum` isn't checked against it), and content
  generated with `for` isn't checked structurally.
- **External contracts.** `weir add schema <url> --as <name>` saves a
  schema into your project with its version locked. It constrains what
  the checker accepts and does nothing at run time: think of an F#
  type provider that runs no code and never changes underneath you.
  The cost: keeping the saved copies current is up to you, and
  validation only reaches what the checker can see.
- **Where a command's output goes depends on the reifier.** When the
  result contains the output, it's captured: `complete` gives you the
  output in a record. When you only get the exit status (`orFail`,
  `exitCode`), the output streams to your terminal, because it's meant
  for a person. `succeeds` discards it, because the true/false is the
  whole answer. The cost: `succeeds` means exactly `exitCode == 0`
  (grep's "no match" counts as false; use `complete` when exit codes
  carry meaning), and `complete` holds the output in memory (about
  twice the text's size, at most ~2GB per capture), so stream huge
  outputs instead.
- **Floats are finite-only.** A calculation that would give NaN or
  Infinity raises an error, so floats always sort consistently and
  `show` never prints a special value. The cost: `1.0 / 0.0` is a
  run-time error, code that used NaN as a "missing" marker must use
  `Option` instead, and floats can't be compared with `==` at all
  (use `Float.near`).
- **Time is stored as integers.** A `Duration` is a whole number of
  milliseconds; decimals appear only when parsing and printing
  (`show 90500ms` is `"1m30.5s"`). The cost: a literal uses one unit
  (`2.5s` and `1m30s` are errors that show the right spelling; parse
  compound text with `Duration.parse`), and a `Duration` field goes
  into JSON only once you pick its encoding (`[<Millis>]` or
  `[<Seconds>]`) or convert it yourself with `Duration.toMillis`.
- **The REPL init file** looks like a shell rc file, but it's checked
  before it runs. `~/.config/weir/init.weir` loads before the first
  prompt. It starts with `#init` and holds only declarations
  (`let`/`type`, for example prompt helpers; no commands run at
  startup), `#alias` lines for command aliases, and one
  `#session { … }` block for settings a declaration can't express
  (`cwd`, `env`, `logLevel`, `echoCap`, `prompt`). It is not a login
  shell: there's no job control. The cost: loading is all-or-nothing.
  If the file has an error, the REPL shows it with its location and
  starts with *none* of the file loaded (safe, because nothing in it
  runs while loading). So a helper you rely on is missing, with the
  error reported, rather than half-loaded.
- **The docs run.** Every code block on this page, in the GUIDE and in
  the skill file runs against the release binary in CI, so an example
  that goes stale fails the build. That's why examples lean on `git`
  and `sh`: they must run in a bare CI container.

If a section above sent you looking for more: [GUIDE.md](GUIDE.md)
teaches the language, [the reference](reference/lexical.md) describes
it precisely, and
[tests/fidelity/divergences.md](../tests/fidelity/divergences.md)
lists every difference from F#, each checked against the real F#
compiler.
