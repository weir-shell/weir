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
   porcelain` is a sequence of records with `path`/`staged` fields,
   not a string soup.
3. **It starts in ~7ms** — a single AOT binary, fine for shebangs.

## Running weir

- `weir` — the REPL: bare names allowed, values echo back, tab
  completion, history, `Ctrl+C` cancels the line, `Ctrl+D` exits.
  Input colors as you type (lexical: keywords, strings, comments,
  numbers, sigils) and the HEAD word colors by live resolution —
  bold = known binding/builtin, blue = found on PATH, red = would
  fail; a red head is the typo caught before Enter. `NO_COLOR`
  honored; piped sessions are plain text always.
  Three output roles: the echo is a bounded GLANCE (10 elements,
  clipped strings, a hint naming the rest), the READ is `|> print`
  for string seqs and `|> Seq.map show |> print` for the rest
  (everything, line per element), and a bare command statement is
  the STREAM (live, as the child produces it).
- `weir -e '1 + 2'` — one expression.
- `weir script.weir args...` — run a script; `#!/usr/bin/env weir`
  works. Scripts are STRICT: library calls are module-qualified
  (`Seq.map`, `Str.trim`, `Option.defaultValue`, `File.read`).
- `weir check script.weir` — every diagnostic, located and coded, no
  evaluation; `--json` for tools and agent loops. Commands missing
  from PATH are warnings here (the runner treats them as errors), so
  scripts for uninstalled tools stay editable.
- `weir fmt script.weir` — canonical formatter (`--check` for CI).
- `weir lsp` — the language server (see Editor setup below).

## First script

Command lines stream, like any shell. Everything else is a value:
bind it with `let`, or print it. A value you drop on the floor is a
check error, not silent output.

```weir
git status --porcelain

let files = git ls-files
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
    |> Seq.where (fun f -> f.bytes > 1024)
    |> Seq.map (_.name >> Path.stem)

big |> print

[1..10] |> Seq.where (fun n -> n > 7) |> Seq.iter (fun n -> print $"{n}")
```

`_.name` is field-access shorthand, and composing it with a `Path`
helper (`extension`, `fileName`, `stem`, `dir`, `combine`) is the
house spelling for filename surgery in a pipeline.

Tuples cover transient pairs — `(a, b)` literals (bare `a, b` works
at F#'s precedence), `int * string` types, `| (x, y) ->` patterns,
destructuring binders (`let host, port = target`,
`Seq.map (fun (k, v) -> ...)`) — but the moment a shape needs NAMES,
declare a record: `p.Host` reads, `let (h, _) = p` re-derives
(`fst`/`snd` project pairs point-free). Records and unions are
declared with exact field sets; union cases
carry tuple payloads when multi-value:

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

print (show v)
```

Derive, don't re-literal: `{ s with Points = 13 }` copies with the
named fields changed (multi-field with `;`, nested `{ o with I.X = v }`),
never adds fields, and leaves the source untouched. An updater over
an open row — `let bump r = { r with N = r.N + 1 }` — generalizes to
any record carrying the field.

Record fields also take attributes, F#'s syntax:

```weir
type Cli = {
    [<Short "C"; Doc "clean first">]
    Clean: bool
    Target: string
}

let cli = { Clean = true; Target = "prod" }
print cli.Target
```

They are check-time data, fully erased at runtime — `cli` above is
indistinguishable from a bare `Cli`. The names are a closed registry
(`Short`, `NoShort`, `Doc`, `Default`); a typo like `[<Shrot "c">]`
is a check error with a did-you-mean. Their consumers — typed argv
deriving `-C` and `--help` text from the declaration — are a coming
feature; until then attributes are legal-and-inert documentation the
checker validates.

## File batches: glob is a function, not an expansion

`Path.glob` returns matches as a typed seq — nothing ever expands
inside argv (a bare `*.txt` in a command stays a literal word; that
law is unchanged). Discovery composes like any seq:

```weir
match Path.glob "*.md" with
| [] -> print "no docs here"
| docs -> docs |> Seq.sortBy (fun s -> s) |> Seq.iter print

let pinned = Path.glob "*.md" |> Seq.force
```

A batch splats into a command with `$@` — N files, N words, nothing
re-split: `git add $@(Path.glob "*.txt" |> Seq.force)`. Relative
patterns resolve against the cwd at ENUMERATION — the lazy seam:
`|> Seq.force` pins the batch before a `cd`. Script-relative batches
ride `Self.scriptPath`:
`Path.glob $"{Self.scriptPath |> Path.dir}/fixtures/**/*.txt"`.

## A script's own facts — the `Self` module

`Self` groups what a running script knows about itself: `Self.args`,
`Self.stdin`, `Self.pid : int` (the process id — `acquire $"{Self.pid}"`
replaces the `$(sh -c 'echo $PPID')` shell-out), and
`Self.scriptPath : string`.

`Self.scriptPath` is the running script's absolute path —
resolved at startup against the invocation cwd, before any `cd`
runs, symlinks left unresolved (bash's `$0` behavior). The
dirname-$0 idiom is `Self.scriptPath |> Path.dir`; if you need
symlinks resolved, the command spelling is
`$(realpath $"{Self.scriptPath}") |> Seq.head`. Script-only, like the
rest of `Self` —
the REPL and `-e` refuse it by name.

## Defaults: the resting point moves

An ENUMERATED env value declares its set as a 0-arity union — the
typo becomes a boundary error with candidates instead of a wrong
branch three functions later:

```weir
type Level =
    | Debug
    | Info
    | Warn

type LogCfg = { WEIR_GUIDE_LOG_LEVEL: Level }

["WEIR_GUIDE_LOG_LEVEL=debug"] |> File.write "guide-log.env"
let e = Env.fromFile "guide-log.env"
!e(sh -c "echo layered")

print "declared sets beat stringly config"
```

Matching is case-insensitive (`=DEBUG`, `=debug`, `=Debug` all
select `Debug`) because env convention is uppercase; a miss reports
`expected one of: Debug, Info, Warn` with a did-you-mean.

`[<Default v>]` on an `Args.load` field keeps the field non-Option
and fills the literal when the flag is absent — `--help` shows it.
On a bool, `[<Default true>]` mints the `--no-x` twin: resting
point on, `--no-x` turns it off, and giving both polarities is an
error naming both. `Env.load` consumes the same attribute — an absent env var fills
the literal, any set var wins (the resting point sits below the
whole overlay stack), and because env bools are TEXT rather than
presence, `[<Default false>]` is legal there while Args rejects it
— the same attribute, each consumer's own law. The boundary:
LITERAL defaults take the attribute; COMPUTED defaults keep
`Option` and a line of code — fuzz.weir carries both shapes:

```weir
type Cli = {
    [<Doc "replay seed (fresh when omitted)">]
    seed: Option<int>
    [<Default 10000; Doc "cases per invariant">]
    count: int
}

let cli = Args.load Cli
print $"count={cli.count} seed={show cli.seed}"
```

## Exit codes: the reifier family

One law: **output goes where the meaning goes.**

| spelling | output | result |
|---|---|---|
| `cmd \| succeeds` | silent | `bool` (`exitCode == 0`, exactly) |
| `cmd \| complete` | captured | `{ exitCode; stdout; stderr }` |
| `cmd \| orFail "msg"` | streams | unit; raises `msg (exit N)` on nonzero |
| `cmd \| exitCode` | streams | the code as `int`; never raises |

Predicates and inspectors are quiet/captured because their output IS
the result; asserts and control flow stream because their output is
for the human. A watched build that decides:

```weir
let rc = sh -c "echo building...; exit 130" | exitCode

match rc with
| 0 -> print "deployed"
| 130 -> print "cancelled"
| c -> fail $"build: exit {c}"
```

`exitCode` refuses capturing/discarding positions with a teaching
error (`$()` captures — use `| complete` there; a bare statement
discards — bind or match). For codes-with-captured-output (fzf's
selection AND its cancel code), `complete` is the cell.

## What the editor colors mean

With the LSP attached, editors render weir's one novel boundary — the
command/expression mode line — from the checker's own verdict:
command heads color as callables, their argv as inert words
(string-family), and splice markers (`$name`, the parens of an
`(expr)` splice) as operators, while everything inside a splice keeps
ordinary code coloring — the visual message "this island is code".
If text you meant as an expression renders argv-colored (or a bound
name you meant as a command doesn't), the coloring is not wrong — it
is the parse, and it just told you before the checker did. The REPL
carries the same three-way tint.

## Shared CLI flags: containment, not inheritance

Flags every subcommand carries are declared ONCE, on a record that
contains the subcommand union — where Argu spells `[<Inherit>]` per
payload, weir deletes the repetition structurally:

```weir
type CloneArgs = { remote: string }

type Cmd =
    | Clone of CloneArgs
    | Status

type Cli = { quiet: bool; cmd: Cmd }
```

The union field's NAME is immaterial — no flag derives from it.
`tool --quiet clone --remote X`,
`tool clone --quiet --remote X`, and `tool clone --remote X --quiet`
all parse: shared flags float, the case token anchors, payload flags
bind after it. One access (`cli.quiet`), no extraction match.

## Functions

`let f x y = ...` defines a curried function (it desugars to nested
lambdas). Bindings generalize: `id` below is genuinely polymorphic.

```weir
let double n = n * 2
let id x = x
let quad = double >> double

print $"{double 21} and {id "strings too"} and {quad 10}"
```

Running totals fold: `xs |> Seq.fold (fun state x -> state + x) 0` —
the folder takes the STATE first, and multi-accumulator loops carry a
record (`Seq.fold (fun c x -> { c with Total = c.Total + x }) c0` —
derive, don't mutate). Lambdas take several params (`fun acc x ->`),
desugaring exactly like `let f a b =`.

Multi-statement lambdas read best MULTILINE: a `(fun ... ->` dangling
at line end opens a body block — block lets, siblings, compounds and
districts all legal inside — closed by its own `)` (attached to the
last body line, or alone). The single-line `;`-joined spelling is
also legal.

```weir
let sizes =
    [("a", 1); ("b", 2)]
    |> Seq.map (fun (name, n) ->
        let doubled = n * 2
        $"{name}={doubled}"
    )

sizes |> Seq.iter print
```

`>>` / `<<` compose functions — `Seq.map (Str.trim >> Str.toLower)`
is the point-free spelling. One precedence rule to know (it is F#'s):
`|>` and `>>` share a level, so `xs |> f >> g` is `(xs |> f) >> g` —
parenthesize the composition: `xs |> (f >> g)`.

Equality, rendering, and sorting are GENERIC through inferred
constraints — the classic helper shapes just work, and reject at the
use site when they cannot:

```weir
let same x y = x == y

print $"{same 1 1} {same "a" "b"}"
```

Binding names start lowercase (the casing law): uppercase is for
types, modules, and constructors. Two deliberate limits you will
meet: a bare parameter cannot be *applied* as a function
(`let apply f x = f x` is rejected — polymorphism flows from typed
builtins, not lambda guessing), and `+` on two unknowns cannot infer
(int or string?) — anchor one side: `x + 0`. Unit params make thunks:
`let cleanup () = ...` runs at `cleanup ()`, and `cleanup 5` is a
type error.

## Branching

`if` is an expression; `else` is optional only when the then-branch is
unit; `elif` chains (it is spelling for `else if`). `match` has literal patterns (`| 0 ->`, `| "yes" ->` — int/string
literals never complete a match alone; close with `_` or a var),
bool patterns, constructor patterns, and `when`
guards — and a non-exhaustive match is a hard error, not a warning.
So is its dual, an arm made unreachable by a catch-all above it:
remember a lowercase pattern *binds*, so a typo'd constructor is a
catch-all, and weir stops it with a did-you-mean instead of silently
matching everything.

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

## Matching text

Raw strings carry patterns and paths without escape noise — F#'s two
kinds, single-line: `@"..."` (backslashes literal, `""` embeds a
quote) and `"""..."""` (no escapes at all, bare `"` fine inside).
Rawness is a property of the literal kind, never of position.

`Str.isMatch` is the condition idiom — pipe the subject so the
sentence reads subject-first:

```weir
let name = "test_parser"

if name |> Str.isMatch @"^test" then print "a test"
```

For extraction, the `Regex` pattern matches and captures in one arm.
The literal is checked before line one runs: an invalid regex is a
check error, and the binder must carry exactly as many names as the
pattern has capture groups — the mismatch that is a silent runtime
non-match in F#'s ParseRegex idiom is a located check error here.

```weir
let line = "cache=42"

match line with
| Regex @"(\w+)=(\d+)" (key, count) -> print $"{key} -> {count}"
| _ -> print "unparsed"
```

Over a stream, the same arm shape pairs with `Seq.choose` — return
`Some out` to keep a line, `None` to skip it, and the sentinel-empty
detour (map to `""`, filter empties later) never happens:

```weir
["cache=42"; "noise"; "hits=7"]
    |> Seq.choose (fun l -> match l with | Regex @"(\w+)=(\d+)" (k, v) -> Some $"{k}: {v}" | _ -> None)
    |> Seq.iter print
```

Groups bind as strings (convert explicitly — `Str.tryToInt`). The
`Regex` literal is RAW-ONLY — `@"..."`, or `"""..."""` for patterns
containing quotes; an ordinary escaped string there is a check error,
so the double-escape footgun cannot be written. Computed patterns
live on the expression side (`Str.isMatch`, `Str.rmatch` — any
string expression). And before reaching for regex on command output,
check the typed adapters: `| from porcelain` beats a hand-rolled
porcelain regex every time.

## Commands and processes

Bareword heads run externals; builtins shadow PATH (`^ls` forces the
real one). A `let` takes a bare command RHS everywhere lets go —
top level and inside bodies alike (`let tree = git rev-parse $c | Seq.head`
works in a function body now); `$()` remains the spelling for
sub-expression positions (inside records, arguments, parens). Splice values with `$name` or `(expr)` — always single argv
entries, never re-split, so there is no injection class. No glob
expansion (`Path.glob` is the function spelling), no
`&&`, no `$VAR` expansion, no redirects — `>` and `>>` pass through as
literal argv with a warning naming the weir spelling
(`cmd | File.write "out.txt"` / `File.append`). For bash semantics,
run bash: `sh -c "the bash line"`.

```weir
let marker = "guide"
echo tagged $marker (40 + 2)
sh -c "echo one && echo two"
```

Nonzero exit raises when the stream is forced. To inspect instead of
raise, reify the run:

```weir
let r = git log --oneline -1 | complete
print $"exit {r.exitCode}"
```

Multi-line scripts: a statement starts at column 0, indented lines
continue it, and the NEXT column-0 line ends it — blank lines and
comment lines are transparent, so blocks group freely with gaps. An
indented `let` closes at the next line of the same indent — F# light
syntax.

## Per-child environment

The env sigil injects variables into a child process — an overlay on
the inherited environment (set those names, keep the rest, parent
untouched). `Env.fromFile` reads the dotenv subset: `KEY=VALUE`,
optional quotes, `#` comments — no `export`, no `$VAR` references
(sourcing is shell evaluation; for that, `sh -c "set -a; . file; ..."`
remains the honest spelling). Bind the env once, then glue it to the
sigil — `!e(...)` runs for effect, `$e(...)` captures:

```weir
["GREETING=hello"] |> File.write "demo.env"

let e = Env.fromFile "demo.env"

!e(sh -c "echo child: $GREETING")
!e(sh -c "echo again: $GREETING")

print (Env.get "GREETING" |> Option.defaultValue "parent stays clean")
```

The env slot goes INSIDE the sigil — `$e(...)` /
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

## The script's own front door

Argv is the same species of stringly boundary as the environment, and
it loads the same way — declare the shape, load once, typed
thereafter:

```weir
type Cli = {
    [<Short "C"; Doc "clean the target first">]
    clean: bool

    port: Option<int>
}

let cli = Args.load Cli
print $"{show cli.clean} {show cli.port}"
```

## Scraping text

Pulling structure out of text is one pipeline. `Str.rmatchAll` yields
every match's groups (lazily, no Option — the absence is the empty
seq); `(?s)`/`(?m)` inline flags handle DOTALL/MULTILINE. Map each
match to what you want, `Seq.distinct` to dedupe, and pipe a value
through an external tool (`| sha256sum` — the bare spelling of `feed`)
when you need one:

```weir
let text = "let a = 1\nlet b = 2\nlet a = 1"

Str.rmatchAll @"let (\w+) = (\d+)" text
|> Seq.map (fun g -> Str.join "=" g)
|> Seq.distinct
|> Seq.iter print
```

Field names derive kebab-case flags (`dryRun` becomes `--dry-run`)
and unambiguous first-letter shorts; `[<Short "C">]` pins a short
explicitly, `[<NoShort>]` suppresses one, and `--help` prints the
derived usage — flags, types, optionality, the short truth, and
`[<Doc>]` text — even on otherwise-invalid invocations. `bool`
fields are presence flags, `string`/`int` are required, `Option`
makes them optional. Loading is STRICT and collected: unknown flags
(with did-you-mean), unexpected arguments, missing requireds, and
unparseable values all arrive in one boundary error, before any
effect runs.

Subcommands are a union of record-payload cases — the first token
picks the case, the rest parse as its flags, and the dispatch match
is exhaustiveness-checked, so adding a subcommand makes every
non-updated match a check-time obligation instead of a runtime
`die "unknown command"`:

```weir-error
type CloneArgs = { remote: string; force: bool }
type Cmd = Clone of CloneArgs | Status
match Args.load Cmd with // no argv here: "missing subcommand; one of: clone, status"
| Clone a -> print a.remote
| Status -> print "status"
```

There are no positionals — spell operands as flags (`pull --subdir
libx`); named-over-positional is the house aesthetic (the
records-over-tuples ruling, extended to argv). `[<Positional>]` is not
a registered attribute; what is dropped is the typed, declared,
help-generating path, NOT the ability to read operands. The untyped
floor remains for hand-rolled shapes: `Args.flag "--clean" "-c"` and
`Args.value "--out"` scan the raw `Self.args` seq, and multi-value options
reshape as one flag per value (`--stack X --env Y`).

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
`exit n` exits with a specific code, silently — the propagation
spelling for a child's failure. There is no try/finally: to clean up
whether a step failed or not, reify the fallible middle with
`| complete`, run the cleanup, then propagate:

```weir
let r = sh -c "exit 0" | complete
sh -c "echo cleanup runs either way"
if r.exitCode <> 0 then exit (r.exitCode)
```

For the two commonest exit-code shapes there is sugar:
`cmd | succeeds` is the bool (`succeeds` means exitCode == 0 exactly —
grep's no-match counts as false; use `| complete` when codes are
data), and `cmd | orFail "msg"` is the one-line assert — unit on
success, `msg (exit N)` raised on failure, at home as a statement or
inside `!()` blocks:

```weir
let onBranch = git symbolic-ref -q HEAD | succeeds
sh -c "true" | orFail "sanity failed"
print (if onBranch then "on a branch" else "detached")
```

`printerr` is `print` to stderr — diagnostics there, data on stdout.
Effect steps sequence inside blocks — same-indent lines, each but the
last unit-typed. The glyph law: weir has no `!`-negation — negation
is the word `not`; `!` means DO IT. Command sigils bring full
command chains into expressions: `$(...)` captures output, `!(...)`
runs-and-streams (unit, raises on nonzero):

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

A top-level `let` RHS takes a bare command chain directly — with or
without params (`let branch = git rev-parse HEAD | Seq.head`,
`let revParse r = git rev-parse $r | Seq.head`; params shadow PATH in
their own RHS, so `let f x = x` stays the identity whatever is
installed) — prefer bare when the whole RHS is the chain; `$()` is
for everywhere the command is a SUB-expression (inside bodies, holes,
nested splices). `run`/`cmd` remain the spellings when the
program NAME is computed. And do not bind an `if`-effect block to a
`let`: the binding is eagerly evaluated unit — a bare `if` statement
says what it means.

```weir
printerr "starting"

if 1 > 2 then fail "impossible"

print "done"
```

## Editor setup

VS Code: `editors/vscode/` holds a sideloadable extension (client
glue for `weir lsp` + a TextMate grammar ported rule-for-rule from
the micro file; see its README for the build-and-install three-liner
and the grammar maintenance rule).
Syntax highlighting for micro lives in `editors/micro/weir.yaml`; the
same directory's README wires `weir lsp` into micro's lsp plugin. Any
LSP-capable editor works: `weir lsp` speaks stdio JSON-RPC and serves
diagnostics (same codes as `weir check --json`), hover types, and
completion from the same pipeline the runner uses. For agent loops
and CI, `weir check --json file.weir` is the no-editor spelling.

## Where weir ends

The complete border with F# — what is deliberately different, what is
rejected by design, what is merely pending — lives in
`tests/fidelity/divergences.md`, machine-verified against the real F#
compiler in CI. The short version: no mutation, no exceptions (values
and `fail`/`exit`), no OO, no async, no user type classes (the three
built-in constraint families are closed). When a task
outgrows a shell, the graduation path is full F# — weir points there
on purpose.

For the language rulebook with rationale, read `SEMANTICS.md`. For the
compressed agent rules, `skills/weir/SKILL.md`.
