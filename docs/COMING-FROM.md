# Coming from another language

You arrive with a working mental model; this page is the diff. Per
language: what you write today, what you write in weir, and the one
thing that will catch you out. It is not a tutorial
([GUIDE.md](GUIDE.md)), not a reference
(`skills/weir/SKILL.md`), and not a rationale (`SEMANTICS.md`,
`DECISIONS.md`) — those own the details this page points at.

Every fenced `weir` block below runs against the release binary in CI
(`ci/skill-doc.sh`); `weir-error` blocks must fail. A translation
that stops being true fails the build. The harness is POSIX by
design (the doc-test CI is Linux), so blocks may lean on `sh` and
`printf` where the SHOWCASE — which must run on Windows too —
restricts itself to weir and git. The tables are the unverified
surface: cells were hand-checked against the binary (2026-08-05, re-swept
same day after `Size` landed — the `File.*` cells are type-unchanged),
but only the fenced blocks re-verify on every build (the fish
section's cells were probe-checked 2026-08-06).

## Coming from bash / POSIX sh

The headline is the argv law: `$x` splices as ONE argv word, `$@xs`
splices N words (one per element), and nothing is ever re-split or
re-joined. The quoting discipline you have spent years internalizing
(`"$x"`, `"$@"`, `IFS`) is the default and only behavior — word
splitting, and the injection class that rides on it, are not
representable.

| bash | weir |
|---|---|
| `out=$(git branch)` | `let out = git branch` — a `seq<string>`, one element per line |
| `for f in *.txt; do … done` | `for f in Path.glob "*.txt" do …` |
| `if grep -q pat f; then` | `if grep -q pat f \| succeeds then` — inline; bind first (`let hit = …`) when the verdict is reused |
| `cmd > out.txt` | `cmd \|> File.write "out.txt"` — a function on the right takes `\|>` (the pipe rule) |
| `$?` | `cmd \| exitCode` (streams, gives the code as `int`) |
| `cat <<EOF … EOF \| cmd` | `lines \| cmd` — a value pipes into stdin |
| `# comment` | `// comment` — full-line or trailing (needs a preceding space) |

```weir
let msg = "two words"
printf "[%s]" $msg

let branches = $(git branch) |> Seq.length
print $"branches: {branches}"
```

That `printf` receives one argument, brackets and all — no re-split.

**The one thing that will catch you out:** capture is a sequence of
LINES, not a string. `$(cmd)` in bash gives one string with trailing
newlines stripped; in weir it gives `seq<string>`, and when a value
pipes back INTO a command (`expr | cmd`), each element is written as
one line — with its newline. So `["x"] | sha256sum` hashes `x\n`,
which is what `printf 'x\n' | sha256sum` prints and NOT what
`printf 'x' | sha256sum` prints. One of the few places weir surprises
a bash hand in the unfavourable direction; hash deliberately.

`set -e` is unconditional and has no name: a nonzero exit raises when
the stream is forced, stopping the script located at the fault. To
inspect instead of raise, make it data: `cmd | complete` gives
`{ exitCode; stdout; stderr }` — which is also where `2>&1` went.

```weir-error
// set -e is unconditional and unnamed: a nonzero exit raises
sh -c "exit 3"
print "unreached"
```

**Not here, and what to write instead:**

- Glob expansion in argv — `Path.glob "src/**/*.c"` is a function
  returning a typed seq; splat a batch with
  `git add $@(Path.glob "*.txt" |> Seq.force)`.
- Redirects — `>`/`>>` pass through as literal argv words with a
  warning naming the spelling: `cmd |> File.write "out.txt"`
  (`File.append` for `>>`).
- `&&`, `;` chaining — one command per line; a failure already stops
  the script, so sequential lines are the `&&` chain.
- `$VAR` expansion — `Env.get "VAR"` (an `Option<string>`), then
  splice the binding.
- `while` — the bounded loops are
  [`retry`/`poll`](GUIDE.md#retrying-and-polling)
  (`retry attempts=5 delay=30s` + a body block); exhaustion raises,
  and an unbounded loop is unrepresentable.
- Everything else bash — `sh -c "the bash line"` is an ordinary
  command; inside that quoted line `$w` is sh's variable, not weir's
  (interpolate first: `sh -c $"echo got-{w}"`).

## Coming from fish

Fish already fixed the half of bash that weir refuses to represent:
variables are lists, expansion never re-splits on spaces, and `(cmd)`
splits on newlines rather than IFS. weir agrees with all three
instincts — and then types them. A capture is `seq<string>`, a list
element is one argv word by construction, and the string toolbox is a
module instead of a subcommand.

| fish | weir |
|---|---|
| `set out (git branch)` | `let out = git branch` — a `seq<string>`, one element per line (the newline split you already expect) |
| `echo $files` (one word per element) | `echo $@files` — the N-word splat is EXPLICIT; `$x` is always exactly ONE word |
| `set files *.txt` (glob in argv) | `let files = Path.glob "*.txt"` — a function returning a typed seq |
| `string split , $s` / `string trim` | `Str.split "," s` / `Str.trim` |
| `string match -r 'v(\d+)' $s` | `match s with \| Regex @"v(\d+)" v -> v \| _ -> "0"` — the binding is typed, the miss arm is forced |
| `if test -f $path` | `let ok = test -f $path \| succeeds` then `if ok then` |
| `$status` | `cmd \| exitCode` (streams, gives the code as `int`) |
| `count $files` | `files \|> Seq.length` |
| `function deploy; …; end` (`$argv`) | `let deploy target = …` — named, typed params instead of `$argv[1]` |
| `for f in (cat list.txt); …; end` | `for f in File.read "list.txt" do …` |

```weir
let words = ["a b"; "c"]
printf "<%s>" $@words

let lines = $(printf "one\ntwo") |> Seq.map Str.toUpper
lines |> Seq.iter print
```

That `printf` receives `a b` as ONE argument (then `c`) — the fish
list rule, kept. The capture splits on newlines — the fish
substitution rule, kept, and now typed.

**The one thing that will catch you out:** fish expands an unset or
empty variable to ZERO arguments and the command runs anyway; weir
refuses the script before anything runs. `$x` is always exactly one
argv word — an empty seq cannot vanish from argv, an unbound name is
a check error, and a seq splices only through the explicit `$@x`.
Where fish's flexibility silently changes a command's arity, weir
makes the arity part of the program.

```weir-error
// fish: an unset $nope expands to nothing and echo runs bare;
// weir: an unbound name is a check error before line one
echo $nope
```

**Not here, and what to write instead:**

- Universal variables (`set -U`) and interactive config — weir is a
  script language, not your login shell; persistent config is a file
  read at the boundary (`Env.load` / `Args.load` / `from json T`).
- Autoloaded functions (`~/.config/fish/functions`) —
  `import "./lib.weir" as Lib` names the dependency in the script.
- `and` / `or` command chaining — a nonzero exit already raises when
  the stream is forced, so sequential lines ARE the `and` chain; for
  the boolean there is `cmd | succeeds`.
- Abbreviations and `alias` — weir has no rewriting layer; a short
  name is a `let`.
- Globs expanding in argv — `Path.glob` is a function; splat a batch
  with `git add $@(Path.glob "*.txt" |> Seq.force)`.

## Coming from F#

Weir is F#-shaped on purpose — pipelines, records, unions, match
(`function` included),
offside blocks — and it is not F#. The full border is machine-verified
against the real F# compiler: every divergence is a row in
[tests/fidelity/divergences.md](../tests/fidelity/divergences.md).
The short version: no mutation, no exceptions, no OO, no async
(parallelism is `Seq.pmap`/`piter` — the TS section shows the shape), no
computation expressions, no `let rec`, no implicit widening.

| F# | weir |
|---|---|
| `if x = y then` | `if x == y then` — `=` is for `let` and record fields only |
| `printfn "%d files" n` | `print $"{n} files"` — no printf family, interpolation is the mechanism |
| `try … with` | no catching: `fail "msg"` raises; `cmd \| complete` makes failure data |
| `while` / `let rec` | `retry`/`poll` for condition loops (bounded); pipelines/`Seq.fold` to transform/accumulate; `for … do` ≡ `Seq.iter` for effects |
| `open Seq` | no `open` — access is always qualified; `import "./lib/x.weir" as X` shares code |
| `[\| 1; 2 \|]` arrays, `list` | one sequence type, `seq<'a>` — `[1; 2]` literals are eager seqs |
| `{\| ip = "x" \|}` anonymous records | the same `{\| ... \|}` spelling, TYPES only, in the adapter slot: `from json {\| ip: string \|}` — no anonymous literals |
| `(+)` and `(>) 10` | `(+)` works (`Seq.reduce (+)`); partial application refuses — `(>) 10` means `fun x -> 10 > x`, the direction nobody reads right; write the lambda |

```weir
let same x y = x == y
print $"{same "a" "a"} {same 1 2}"
```

Equality, `show`, and sorting are generic through inferred
constraints (a closed, compiler-owned set — no user type classes).

**The one thing that will catch you out:** `==` versus `=`, in minute
one. It has caught the people writing these docs: a claim that F#
accepts `0.1 == 0.2` went to the fidelity oracle and came back
backwards — `==` is weir's equality and an F# error; `=` is F#'s
equality and weir's binding glyph. (In weir,
`0.1 == 0.2` is a check error either way: floats do not join `==` —
`Float.near a b eps` is the idiom, because floats are finite-only
here. See [GUIDE.md](GUIDE.md#rates-and-percentages-floats-finite-only).)

`=` on collections is the other equality surprise, in the opposite
direction: weir's `==` REFUSES seqs at check time, which reads as a
limitation until you learn what F# was doing — `=` on a `seq<'T>`
compiles and its answer depends on the RUNTIME type (structural if the
object happens to be a list or array, REFERENCE equality for a
computed seq: `Seq.map id [1;2] = Seq.map id [1;2]` is `false`).
Refusing beats an answer that changes with provenance; compare a
value you mean — `Seq.length`, a `Str.join`-ed string, or the
element-wise check you actually intend.

F#'s warnings are weir's errors: a non-exhaustive match, a discarded
non-unit value, an unreachable arm below a catch-all, an off-by-one
`|` alignment — all hard errors, before anything runs.

```weir-error
// F# warns FS0025 and runs anyway; weir refuses before line one
let word = match 1 with | 1 -> "one"
print word
```

**Not here, and what to write instead:**

- Computation expressions (`seq { }`, `async { }`) — pipelines are
  the composition story; `[for x in xs -> e]` comprehensions exist.
- Async/task — processes are the concurrency model;
  `Seq.pmap`/`Seq.piter` fan out (bounded, ordered).
- Classes, interfaces, members — records and functions.
- `mutable`, `<-` — copy-and-update: `{ r with F = v }`.
- Type annotations on params, `(e : ty)` ascription — inference plus
  anchoring (`x + 0` to pin int); a bare param cannot be applied as a
  function.
- Anonymous records — every record has a declared, exact field set.

## Coming from PowerShell

The instinct to check first: weir does carry structured values
through pipelines. `ls` yields typed rows, `from json T` yields a
record of your declared shape (`from jsonl T` a stream of them), and `_.name` is field access on a piped record —
row-polymorphic, so it works on any record with the field, which is
what a `Where-Object` hand expects and does not expect from a
statically typed language.

| PowerShell | weir |
|---|---|
| `Get-ChildItem \| Where-Object Length -gt 1kb` | `ls \|> Seq.where (fun f -> f.bytes > 1KiB)` |
| `… \| ForEach-Object { $_.Name }` | `… \|> Seq.map _.name` |
| `ConvertFrom-Json` | `\|> from json T` — against a shape you declared |
| `"$($x.Name) ready"` | `$"{x.name} ready"` |
| `$LASTEXITCODE` | `cmd \| exitCode` |

```weir
git status --porcelain |> Seq.choose (fun l -> match l with | Regex @"^.. (.*)$" path -> Some path | _ -> None) |> print
```

**The one thing that will catch you out:** the pipeline is TWO
channels, not one. PowerShell has a single object pipeline — cmdlets
consume and produce objects uniformly. Weir's `|` carries text
to and from external programs; `|>` carries values between functions;
the right-hand side decides, and a mismatch errors naming the other
spelling. The conversion between them is yours to declare:
PowerShell's cmdlets ARE its adapters, whereas weir orchestrates
programs that emit text, and `from json T` /
`from yaml T` are where text becomes structure — against your
declared shape.

The trade runs both ways, and this reader will weigh it: PowerShell's
typing stops at the cmdlet boundary — a bare `.exe` hands you
strings, and `Where-Object Length` is a runtime property lookup — a
misspelled name yields nothing and silently filters everything out. Weir's typing sits at the
external boundary by construction: `from json T` is checked before
anything runs, and a misspelled field is a check error with a
did-you-mean.

**Not here, and what to write instead:**

- The verb-noun cmdlet space, parameter sets, providers —
  `Seq.*`/`Str.*`/`Path.*`/`File.*` are functions, commands are
  commands.
- `$x` as a variable-that-also-does-properties — `$` in weir is a
  splice (into argv or a string); expressions use bare names.
- `trap`/`try` — the reifier family:
  [GUIDE.md](GUIDE.md#exit-codes-the-reifier-family).
- On Windows: bare names resolve through the full `PATHEXT` list
  (platform parity), and the `.bat`/`.cmd` hazard is a stated
  non-claim — a batch interpreter re-parses its command line, so
  argv word integrity holds up to that hand-off (native executables
  receive words verbatim). See `SECURITY.md`.

## Coming from Python

Lead with what a Python script cannot do. First: the whole weir file
is checked before line one runs — including that the commands you
invoke exist (`weir check` downgrades a missing command to a warning
so scripts for uninstalled tools stay editable; the runner refuses
before any effect). Second: `subprocess.run("… " + arg, shell=True)`
has no analogue because it cannot — argv is data by construction, a
splice is one word, and no shell ever re-parses your line.

| Python | weir |
|---|---|
| `subprocess.run(["git", "add", f])` | `git add $f` |
| `f"{n} files"` | `$"{n} files"` |
| `with tempfile.TemporaryDirectory() as d:` | `within tmp d` + an indented block |
| `os.environ.get("PORT")` | `Env.get "PORT"` — or `Env.load Config`, typed, one error for all fields |
| `argparse` | `Args.load Cli` — the flags DERIVE from a record you declare |
| `json.loads(...)` → dict soup | `\|> from json T` → your declared record |
| `requests.post(url, json=payload)` | `Http.send { Http.defaults with method = Post; url = u; body = Json (payload \|> to json) }` — status is data, `Secret` auth, body byte-exact |

```weir
type Cfg = { name: string; port: int }
let text = """{ "name": "api", "port": 8080 }"""
let cfg = [text] |> from json Cfg
print $"{cfg.name}:{cfg.port}"
```

**The one thing that will catch you out:** whitespace is significant
DIFFERENTLY. There is no `:` opening a block — a block is the deeper
lines under its head (the offside rule), a statement starts at column
0 and ends at the next column-0 line, and blank lines inside a
statement are transparent. Minute one: an `if` body is just indented
lines, and a line back at column 0 is the next statement.

`with` is `within` — and it is the scope form for more than files:
`within tmp d` binds a fresh directory removed on every managed exit
(the raise path included), `within cd`/`within env` scope directory
and child-environment the same way.

```weir
within tmp d
    ["data"] |> File.write $"{d}/f.txt"
    let back = File.read $"{d}/f.txt"
    print (back |> Seq.head)
```

**Not here, and what to write instead:**

- `while` — `retry attempts=… delay=…` / `poll timeout=… interval=…`
  are the bounded loops; `for x in xs do` is the effect loop;
  transformations are pipelines.
- Classes — records and functions; `{ r with F = v }` instead of
  attribute mutation.
- Exceptions and `try/except` — `fail` stops; a fallible step becomes
  data with `| complete` and you branch on it.
- Dicts — there is no `Map` type (the honest gap): declared records
  where the keys are known, `seq<string * string>` pairs where they
  are not, `from json T` at the boundary.
- `os.path`/`pathlib` — `Path.*` functions over plain strings,
  platform-native (`Path.combine`, `Path.stem`, `Path.glob`).

## Coming from TypeScript / Node scripting (zx, execa, bun $)

zx is also "a real language for shell scripts", so the diff is sharp.
Two real differences. Weir checks the whole file before running a
line — types, fields, match coverage, and whether the commands exist
— where zx discovers a typo'd binary at await-time, halfway through
the deploy. And there is no runtime to install: one AOT binary,
millisecond startup, no `node_modules`, no `package.json`.

| zx / Node | weir |
|---|---|
| ``await $`git status` `` | `git status` |
| ``$`git add ${file}` `` (zx escapes) | `git add $file` — one argv word by construction, nothing to escape |
| `$.nothrow` / `.exitCode` | `cmd \| complete` / `cmd \| exitCode` |
| `await Promise.all(xs.map(f))` / `p-map` | `xs \|> Seq.pmap f` — bounded (`Seq.pmapWith n` sets the ceiling), results in input order, first error by input order |
| `await Promise.any(xs.map(f))` | `xs \|> Seq.pfirst f` — first arm to SUCCEED wins, losers' processes tree-killed (`Seq.pfirstWith n` sets the ceiling); losers' failures swallowed |
| `globby`, `fs/promises` | `Path.glob`, `File.*` / `Dir.*` |
| `zod` schema `.parse(...)` at runtime | `from json T` and vendored JSON-schema contracts, at CHECK time |
| `await fetch(url).then(r => r.json())` | `Http.send { Http.defaults with url = u }` then `resp.body \|> from json T`; a plain GET is `curl url \|> from json T` |

```weir
let target = "seed.txt"
git add $target
[1; 2; 3] |> Seq.pmap (fun n -> n * n) |> Seq.map show |> print
```

**The one thing that will catch you out:** the template-literal glue
habit. In zx, `--file=${f}` interpolates into the command string; in
weir a splice is a whole argv word, and gluing one mid-word is a hard
error — spell `--file $f`, or build the word first:
`$"--file={f}"`.

```weir-error
// the zx habit: a mid-word splice is a hard error, not an escape
let f = "seed.txt"
echo --file=$f
```

**Not here, and what to write instead:**

- `async`/`await` — none, and none needed: I/O is synchronous from
  the script's point of view; parallelism lives in `Seq.pmap`/`piter`
  (and a task that truly needs async has outgrown a shell).
- `try/catch` — the reifier family; `| orFail "msg"` is the one-line
  assert.
- npm dependencies — `import "./lib/x.weir"` shares code between
  scripts; external tools are external tools.

## Coming from Make

The honest lead is the thing weir does not have: Make is a dependency
graph with staleness rules, and weir is a script. There is no
`target: prereq`, no timestamp comparison, no incremental skip — a
reader looking for that will not find it. What weir gives instead:
real types, real errors before any effect, and none of the recipe
machinery — no tab-versus-space significance, no per-line shell
invocation, no `$$` escaping, no `.PHONY`.

| Make | weir |
|---|---|
| `$(VAR)` | `$var` |
| `$(shell git rev-parse HEAD)` | `let sha = git rev-parse HEAD \|> Seq.head` |
| `$@`, `$<` automatic variables | ordinary named bindings — see the false friend below |
| `VAR = …` vs `VAR := …` | one evaluation, in order — the distinction has no analogue |
| `.PHONY: deploy` | not needed: a script, not a target namespace |

```weir
let sha = git rev-parse --short HEAD |> Seq.head
print $"building {sha}"
```

**The one thing that will catch you out:** every recipe line in Make
is its own shell — a `cd` on one line is gone on the next. A weir
file is one program: a `cd` persists until scoped (`within cd "dir"`
restores on exit). And `$@` is a genuine false friend: Make's "the
target", weir's argv SPLAT (`$@xs` splices N words).

**Not here, and what to write instead:** dependency-ordered targets —
keep the graph in Make (or a runner) and put weir inside the recipe;
the next section is that story.

## Coming from Just / Task / npm-scripts

Task runners are thin wrappers around shell strings; weir is the
language the recipe body would be written in. So this is not
weir-versus-just: weir replaces the shell INSIDE the recipe, and a
`justfile` whose recipe line is `weir deploy.weir --env prod` is a
reasonable end state.

What weir adds inside a recipe: the whole script checks before any
effect runs; the recipe's own flags are declared, typed, and
`--help`-generating; and argv integrity end to end.

```weir
type Cli = {
    [<Default "dev">]
    /// target environment
    env: string
}
let cli = Args.load Cli
print $"deploying to {cli.env}"
```

**What weir does not do:** task discovery, `--list`, dependency
ordering between tasks, the self-documenting menu. Those stay the
runner's job — this section is deliberately short because the diff is
small and complementary.

## Coming from Ruby

The pleasant surprise first: row polymorphism is the nearest thing to
duck typing, and it is static — `Seq.map _.name` works on any record
with the field, checked before the script runs.

| Ruby | weir |
|---|---|
| `` `git status` ``, `%x{git status}` | `$(git status)` — a `seq<string>` of lines |
| `system("git", "add", f)` | `git add $f` |
| `Dir.glob("**/*.rb")` | `Path.glob "**/*.rb"` |
| `xs.map { \|x\| x.strip }` | `xs \|> Seq.map Str.trim` |
| `OptionParser` | `Args.load Cli` |

```weir
$(git log --oneline -1) |> Seq.map Str.toUpper |> print
```

**The one thing that will catch you out:** there is no trailing-block
convention. A lambda is an ordinary parenthesized argument —
`Seq.each { … }` becomes `Seq.iter (fun x -> …)`, and a multi-line
body is a `(fun x ->` dangling at line end with the block below,
closed by its own `)`. The block-taking ergonomics survive in the
compound forms instead: `retry`/`poll`/`within` each take an indented
body block directly.

**Not here, and what to write instead:**

- Monkey-patching, `method_missing`, classes — records, functions,
  and the closed builtin modules.
- `begin/rescue/ensure` — `within` scopes clean up on the raise path;
  fallible middles become data with `| complete`.
- `rake` — the task-runner section above.

## Coming from Perl

The regex reflexes land well here: weir has a `Regex` pattern that
matches and extracts in one match arm, compiled at CHECK time (an
invalid pattern is a check error) with binder arity checked against
the capture count.

| Perl | weir |
|---|---|
| `if (/pat/)` on `$_` | `if s \|> Str.isMatch @"pat"` — everything is named, no topic variable |
| `($k) = $l =~ m/(\w+)=/` | `match l with \| Regex @"(\w+)=" k -> …` |
| `s/old/new/` (literal) | `Str.replace "old" "new" s` — literal; regex substitution stays `sed`'s job, a command line away |
| `qx/git status/`, backticks | `$(git status)` |
| `@ARGV` | `Self.args`, or `Args.load Cli` for the typed front door |

```weir
let ver = match "release v42 ready" with | Regex @"v(\d+)" n -> n | _ -> "0"
print $"version {ver}"
```

`Str.rmatch` is the Option form, `Str.rmatchAll` the every-match
plural (lazy; absence is the empty seq); `(?s)`/`(?m)` inline flags
cover DOTALL/MULTILINE. **Named groups `(?<name>…)` reject** — weir
names captures at the binder (`(k, v)` above), so the name sits next
to the pattern instead of inside it; lookbehind `(?<=`/`(?<!` works.

**The one thing that will catch you out:** sigils. Perl's `$`/`@`/`%`
denote a variable's TYPE; weir's `$` is a splice — a value entering a
command line or a string — and there is no sigil on ordinary
variables at all. `use strict` is unconditional and unnamed.

```weir-error
// a Regex pattern must be a RAW string (@"a\+") — an ordinary string
// is rejected here, which is what makes the double-escape footgun
// unrepresentable rather than merely avoidable
let m = match "a+b" with | Regex "a\\+" () -> "hit" | _ -> "miss"
print m
```

**Not here, and what to write instead:** implicit `$_`/topicalization
(name the value; `_.field` and `_[0]` are the point-free
shorthands), `tr///` and regex `s///` (pipe through `tr`/`sed`),
`local`/dynamic scope (bindings are lexical, `within env` scopes
child environment).

## The false friends, collected

The highest-value rows on this page — same spelling, different
meaning:

| glyph | there | here |
|---|---|---|
| `$@` | Make: "the target"; bash: all args | the argv splat `$@xs` — N words, one per element |
| `$x` | Perl/PowerShell: variable (with type sigil / property access) | a splice: one argv word, or a string hole |
| `` `${f}` `` | zx: interpolation, escaped for you | mid-word glue is a hard error; a splice is a whole word |
| `$()` | bash: capture as one newline-stripped string | capture as `seq<string>`, one element per line |
| `=` | F#: equality | binding only; equality is `==` |
| `\|` | F#: nothing (`\|>` pipes) | text to/from an external program; `\|>` stays the function pipe — the right-hand side decides |
| `!` | bash: history/negation | DO IT — `!(cmd)` runs-and-streams; negation is the word `not` |
| `//` | C-family: always a comment | a comment only at line start or after whitespace — `http://a` in argv stays data; a bare `//` word needs quoting |

## What nobody arrives knowing

Nine sections of diffs; this one is not a diff. These are the things
no source language prepares you for — each with what it costs.

- **Check-before-effects across the whole file** — types, match
  coverage, discarded values, and whether the commands exist: the
  runner refuses before line one, so a script never dies halfway
  through its side effects. The cost: you cannot run the good half of
  a broken script, and a missing tool blocks the run outright
  (`weir check` keeps it a warning so the script stays editable).
- **The argv law** — a splice is one word, `$@xs` is N words, nothing
  re-splits, so shell injection is unrepresentable rather than
  guarded against. The same law reaches the `yaml` district: splices
  are typed values, never text, so YAML injection does not exist
  either. The cost: a bare `//` argv word reads as a comment (quote
  it), and mid-word glue (`--file=$f`) is refused — build the word
  deliberately.
- **Two modes, decided by resolution** — a bareword head is a command
  if it resolves to one and an expression otherwise; bindings beat
  PATH; `^ls` is the one escape, forcing the external. No syntax
  marks the boundary. The cost: introducing a binding named like a
  command changes what a later line means — resolution order is
  load-bearing, and the editor's head-coloring exists to show it.

```weir
let rows = ls |> Seq.length
print $"typed rows: {rows}"
^ls -a
```

- **`within` scopes** — `tmp`/`cd`/`env` with cleanup on the raise
  path, in a language with no `defer`, no `try/finally`, no
  `IDisposable`. The cost: a hard interrupt is the stated gap —
  SIGINT terminates without running cleanup (the `weir-tmp-` prefix
  keeps leftovers identifiable).
- **The `yaml` district** — a checked block literal: structure errors
  are check errors, ambiguous scalars auto-quote on render (`"no"`
  stays a string — the reverse-Norway law), and
  `yaml schema=<name>` validates against a vendored JSON schema at
  check time. The cost: it is a subset (anchors and flow style
  reject), splices check by TYPE (a string against a `pattern`/`enum`
  constraint does not check), and `for`-generated content is
  structurally unchecked.
- **External contracts** — schemas vendored and pinned
  (`weir add schema <url> --as <name>`) that constrain what the
  checker accepts and contribute nothing at runtime: a type provider
  minus the execution and plus the pinning. The cost: vendored files
  are yours to keep current, and the validation reaches only what the
  checker can see.
- **The reifier family under one law** — output goes where the
  meaning goes: `succeeds` and `complete` are silent/captured because
  their output IS the result; `orFail` and `exitCode` stream because
  their output is for the human. The cost: `succeeds` is
  `exitCode == 0` exactly (grep's no-match counts as false — reach
  for `complete` when codes are data), and `complete` captures in
  memory (~2x the text in RSS, ~2GB per capture — stream gigabyte
  children instead).
- **Finite-only floats** — a result that would be NaN or Infinity
  raises, so ordering is total and `show` never meets a special
  value. The cost: `1.0 / 0.0` is a runtime error, NaN-as-sentinel
  code must carry `Option` instead, and floats do not join `==` at
  all (`Float.near` is the idiom).
- **Integer-backed time** — `Duration` stores integer milliseconds;
  decimals exist only in parsing and rendering (`show 90500ms` is
  `"1m30.5s"`). The cost: literals are single-unit (`2.5s` and
  `1m30s` are teaching errors — the compound shapes live in text, via
  `Duration.parse`), and a Duration crosses JSON only as
  `Duration.toMillis` into an int field.
- **Docs that run** — every fenced block on this page, in the GUIDE,
  and in the skill file executes against the release binary in CI, so
  a translation that rots fails the build. The cost: examples are
  constrained to what a bare CI container can run — which is why they
  lean on `git` and `sh`.

If a section above sent you looking for more: [GUIDE.md](GUIDE.md)
teaches the language, `SEMANTICS.md` rules on it, and
[tests/fidelity/divergences.md](../tests/fidelity/divergences.md) is
the machine-checked F# border.
