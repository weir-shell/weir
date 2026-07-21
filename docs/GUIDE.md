# The weir guide

Weir is a typed shell: F#-shaped expressions, real commands, and a
type checker that runs before anything else does. Every fenced `weir`
block in this guide is executed against the release binary in CI —
if an example here stops working, the build fails.

## Why weir

Three properties, in the order they matter:

1. **The whole script typechecks before line one runs.** A typo, a
   wrong field, a discarded value, a missing match case — all of them
   stop the script with `file:line:col` and a hint, before any side
   effect. Bash tells you about your mistake halfway through making it.
2. **Command output is typed data.** `git status --porcelain | from
   porcelain` is a sequence of records with `Path`/`Staged` fields,
   not a string soup.
3. **It starts in ~7ms** — a single AOT binary, fine for shebangs.

## Running weir

- `weir` — the REPL (bare names allowed, values echo back).
- `weir -e '1 + 2'` — one expression.
- `weir script.weir args...` — run a script; `#!/usr/bin/env weir`
  works. Scripts are STRICT: library calls are module-qualified
  (`Seq.map`, `Str.trim`, `Option.defaultTo`, `File.read`).
- `weir fmt script.weir` — canonical formatter (`--check` for CI).

## First script

Command lines stream, like any shell. Everything else is a value:
bind it with `let`, or print it. A value you drop on the floor is a
check error, not silent output.

```weir
git status --porcelain

let files = cmd "git" ["ls-files"]
print $"tracked: {files |> Seq.length}"
```

`print` takes strings, ints, bools, or `seq<string>` (one line per
element — `weir script | grep x` composes). For anything else there is
`show`:

```weir
let row = ls |> Seq.head
print (show row)
```

## Values and pipelines

Sequences are lazy; pipelines pull only what they need. Ranges are
lazy generators; `[a; b; c]` literals are eager values.

```weir
let big =
    ls
    |> Seq.where (fun f -> f.Bytes > 1024)
    |> Seq.map (fun f -> f.Name)

big |> print

[1..10] |> Seq.where (fun n -> n > 7) |> Seq.iter (fun n -> print $"{n}")
```

Records and unions are declared with exact field sets; union cases
carry one payload:

```weir
type Verdict =
    | Pass of int
    | Fail

type Score = { Name: string; Points: int }

let s =
    { Name = "a"
      Points = 12 }

let v = if s.Points > 10 then Pass s.Points else Fail

print (show v)
```

## Functions

`let f x y = ...` defines a curried function (it desugars to nested
lambdas). Bindings generalize: `id` below is genuinely polymorphic.

```weir
let double n = n * 2
let id x = x

print $"{double 21} and {id "strings too"}"
```

Two deliberate limits you will meet: a bare parameter cannot be
*applied* as a function (`let apply f x = f x` is rejected —
polymorphism flows from typed builtins, not lambda guessing), and `+`
on two unknowns cannot infer (int or string?) — anchor one side:
`x + 0`.

## Branching

`if` is an expression; `else` is optional only when the then-branch is
unit. `match` has literal patterns (`| 0 ->`, `| "yes" ->` — int/string
literals never complete a match alone; close with `_` or a var),
bool patterns, constructor patterns, and `when`
guards — and a non-exhaustive match is a hard error, not a warning.

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

Blocks read like F#: a line at the same indent as an `if` (or `match`)
is a sibling, not part of its body — so a guard line before a block's
result works the way it looks:

```weir
type Target = { Name: string }

let target =
    let stack = "web"
    if stack == "" then fail "usage"
    { Name = stack }

print target.Name
```

## Commands and processes

Bareword heads run externals; builtins shadow PATH (`^ls` forces the
real one). Splice values with `$name` or `(expr)` — always single argv
entries, never re-split, so there is no injection class. No globs, no
`&&`, no `$VAR` expansion — for bash semantics, run bash:
`sh -c "the bash line"`.

```weir
let marker = "guide"
echo tagged $marker (40 + 2)
sh -c "echo one && echo two"
```

Nonzero exit raises when the stream is forced. To inspect instead of
raise, reify the run:

```weir
let r = git log --oneline -1 | complete
print $"exit {r.ExitCode}"
```

Multi-line scripts: a statement starts at column 0, indented lines
continue it, a blank line ends it (comment lines are transparent). An
indented `let` closes at the next line of the same indent — F# light
syntax.

## Per-child environment

`runEnv` / `cmdEnv` inject variables into ONE child process — an
overlay on the inherited environment (set those names, keep the rest,
parent untouched). `Env.fromFile` reads the dotenv subset: `KEY=VALUE`,
optional quotes, `#` comments — no `export`, no `$VAR` references
(sourcing is shell evaluation; for that, `sh -c "set -a; . file; ..."`
remains the honest spelling). The house idiom is partial application —
name the env-carrying runner once, use it like `run`:

```weir
["GREETING=hello"] |> File.write "demo.env"

let child = runEnv (Env.fromFile "demo.env") "sh"

child ["-c"; "echo child: $GREETING"]
child ["-c"; "echo again: $GREETING"]

print (Env.get "GREETING" |> Option.defaultTo "parent stays clean")
```

For command chains the env slot goes INSIDE the sigil — `$e(...)` /
`!e(...)` with the name glued to the glyph — and a line-end `!name`
turns a whole command block into an env-carrying district:

```weir
["STAGE=prod"] |> File.write "stage.env"

let e = Env.fromFile "stage.env"
let ready = 1 > 0

!e(sh -c "echo inline: $STAGE")

if ready then !e
    sh -c "echo block one: $STAGE"
    sh -c "echo block two: $STAGE"
```

Multi-value options idiom, while we are near CLI shapes: weir's `Args`
has no two-token options — reshape `--app stack env` as one flag per
value (`--stack X --env Y`, two `Args.value` calls). The reshape is
usually clearer than the positional pair.

## Parallelism

`Seq.pmap` / `Seq.piter` fan out over a seq: parallel execution,
results in input order, first failure rethrown. Workers fork the
session — `cd` inside a worker is worker-local and gone at the join.
There is no async/await and never will be: processes and pipelines are
the concurrency model, and a task that truly needs async belongs in
full F#.

```weir
["/"; "/tmp"] |> Seq.pmap (fun d ->
    let x = cd d
    pwd |> Seq.head) |> print
```

## Failing and diagnosing

`fail "reason"` stops the script with a located error and exit 1.
`Exit.code n` exits with a specific code, silently — the propagation
spelling for a child's failure. There is no try/finally: to clean up
whether a step failed or not, reify the fallible middle with
`| complete`, run the cleanup, then propagate:

```weir
let r = sh -c "exit 0" | complete
sh -c "echo cleanup runs either way"
if r.ExitCode <> 0 then Exit.code (r.ExitCode)
```

`printerr` is `print` to stderr — diagnostics there, data on stdout.
Effect steps sequence inside blocks — same-indent lines, each but the
last unit-typed. Command sigils bring full command chains into
expressions: `$(...)` captures output, `!(...)` runs-and-streams
(unit, raises on nonzero):

```weir
let ready = 1 > 0

if ready then !
    sh -c "echo preparing"
    sh -c "echo prepared"

if ready then
    !(sh -c "echo inline-form")
    print "mixed with expressions"

let latest = git log -1 "--format=%h" | Seq.head
print $"at {latest}"

let tagged = $"at {$(git log -1 "--format=%h") |> Seq.head}"
```

A top-level `let` RHS takes a bare command chain directly
(`let branch = git rev-parse HEAD | Seq.head`) — prefer that where it
is legal; `$()` is for everywhere bare cannot go (inside expressions,
holes, nested splices). `run`/`cmd` remain the spellings when the
program NAME is computed. And do not bind an `if`-effect block to a
`let`: the binding is eagerly evaluated unit — a bare `if` statement
says what it means.

```weir
printerr "starting"

if 1 > 2 then fail "impossible"

print "done"
```

## Where weir ends

The complete border with F# — what is deliberately different, what is
rejected by design, what is merely pending — lives in
`tests/fidelity/divergences.md`, machine-verified against the real F#
compiler in CI. The short version: no tuples (records), no mutation,
no exceptions (values and `fail`), no OO, no async. When a task
outgrows a shell, the graduation path is full F# — weir points there
on purpose.

For the language rulebook with rationale, read `SEMANTICS.md`. For the
compressed agent rules, `skills/weir/SKILL.md`.
