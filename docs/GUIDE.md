# The weir guide

Weir is a typed shell-scripting language: F#-shaped expressions,
real commands, and a type checker that runs before anything else
does. Every fenced `weir`
block in this guide is executed against the release binary in CI —
if an example here stops working, the build fails. (Blocks needing a
live endpoint or a real token are marked demo and are the exception;
everything else runs.)

## Why weir

Three properties, in the order they matter:

1. **The whole script typechecks before line one runs.** A typo, a
   wrong field, a discarded value, a missing match case — all of them
   stop the script with `file:line:col` and a hint, before any side
   effect. Bash tells you about your mistake halfway through making it.
2. **Command output is typed data.** A JSON document — pretty-printed
   or not — pipes through `|> from json T` into a record with the
   fields YOU declared, not string soup; `|> from jsonl T` reads
   NDJSON streams, and the `Regex` match pattern covers everything
   line-shaped.
3. **It starts in ~7ms** — a single AOT binary, fine for shebangs.

## Running weir

- `weir` — the interactive REPL; see [The REPL](#the-repl) at
  the end of this guide.
- `weir -e '1 + 2'` — a program whose LAST statement is an
  expression (newlines are statement boundaries, as in a file); the
  result is echoed. A lone declaration is refused — `-e` evaluates
  something and shows you the result. Strict like files.
- `weir script.weir args...` — run a script; `#!/usr/bin/env weir`
  works. Scripts are STRICT: library calls are module-qualified
  (`Seq.map`, `Str.trim`, `Option.defaultValue`, `File.read`).
- `weir check script.weir` — every diagnostic, located and coded, no
  evaluation; `--json` for tools and agent loops. Commands missing
  from PATH are warnings here (the runner treats them as errors), so
  scripts for uninstalled tools stay editable.
- `weir fmt script.weir` — canonical formatter (`--check` for CI).
- `weir lsp` — the language server (see [editors.md](editors.md)).

## First script

Save a file, run it with `weir file.weir` — `#!/usr/bin/env weir`
makes it a program. Three kinds of line cover most scripts:

```weir
echo checking...

let files = git ls-files
print $"{files |> Seq.length} tracked file(s)"
```

A bare command STREAMS, like any shell — the `echo` writes straight
through. A `let` in front of a command CAPTURES instead: nothing
streams, and `files` is a `seq<string>`, one element per line. And
everything that is not a command must be USED — bind it or print
it; a value dropped on the floor is a check error, not silent
output:

```weir-error
ls |> Seq.length // computes an int and discards it — bind it, or pipe it to print
```

Before any of it runs, the whole file is checked — and
`weir check file.weir` gives you every finding at once without
running line one.

`print` takes strings, ints, bools, or `seq<string>` (one line per
element — `weir script | grep x` composes). For anything else there
is a hole — interpolation renders any `Show` value, records
included:

```weir
let row = ls |> Seq.head
print $"{row}"
```

`show` produces the same text as a plain string; its niche is the
places a hole cannot go — point-free positions (`Seq.map show`) and
Secrets (`show` masks where interpolation refuses).

## Comments

`//` runs to the end of the line, full-line or trailing — command
lines included:

```weir
let retries = 3 // why: registry flakes under load
echo done // trailing works on command lines too
```

`///` is the doc comment, and it pays its way: it attaches to the
declaration below and surfaces on hover — and on a CLI record, its
first line becomes the flag's `--help` text. One source; help and
hover cannot drift. Watch the `///` lines come back out:

```weir
["type Cli = {"; "    /// run without uploading"; "    dryRun: bool"; ""; "    /// where the bundle goes"; "    target: string"; "}"; ""; "let cli = Args.load Cli"; "print $\"dry={cli.dryRun} target={cli.target}\""] |> File.write "tool.weir"
weir tool.weir --help
```

The edge rules (why `http://a` survives as an argv word, why a
comment cannot live inside an interpolation hole) are on
[Lexical](reference/lexical.md#comments).

## Values and pipelines

Sequences are lazy; pipelines pull only what they need. Ranges are
lazy generators; `[a; b; c]` literals are eager values.

```weir
let big =
    ls
    |> Seq.where (fun f -> f.bytes > 1KiB)
    |> Seq.map (_.name >> Path.stem)

big |> print

[1..10] |> Seq.where (fun n -> n > 7) |> Seq.iter (fun n -> print $"{n}")
```

`_.name` is field-access shorthand; compose it with a `Path` helper
(`extension`, `fileName`, `stem`, `dir`, `combine`) for filename
surgery in a pipeline.

The `Seq` module is the F# core you'd expect, lazy by default:

- transform: `map`, `where`, `collect`, `fold`, `reduce`, `scan`
- order: `sort`, `sortBy`, `max`, `minBy`
- group: `groupBy`, `countBy`, `distinctBy`
- slice and search: `indexed`, `chunkBySize`, `takeWhile`, `find`,
  `pick`, and their `try` variants

One deliberate split: `Seq.sum` is for ints, and `Float`/`Size`/
`Duration` each own their `sum` and `average`
(`ls |> Seq.map _.bytes |> Size.sum`).

## Records, unions, and tuples

Tuples cover transient pairs: `(a, b)` literals, `int * string`
types, `| (x, y) ->` patterns, and destructuring binders
(`let host, port = target`). The moment a shape needs NAMES, declare
a record — `p.Host` says what it is where `let (h, _) = p` makes the
reader re-derive it. Records and unions are declared with exact field
sets, and union cases carry tuple payloads when multi-value:

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

Derive, don't re-literal: `{ s with Points = 13 }` copies with the
named fields changed (multi-field with `;`, nested `{ o with I.X = v }`),
never adds fields, and leaves the source untouched. An updater
function — `let bump r = { r with N = r.N + 1 }` — works on any
record that has the field.

```weir-error
type P = { N: int }
let p = { N = 1 }
let q = { p with Extra = 2 } // record update cannot add fields
print $"{q}"
```

Record fields also take attributes, F#'s syntax:

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

They are check-time data, fully erased at runtime — `cli` above is
indistinguishable from a bare `Cli`. The names are a closed registry
(`Short`, `NoShort`, `Default`); a typo like `[<Shrot "c">]`
is a check error with a did-you-mean. Their consumer is typed argv —
`Args.load` derives `-C` from the declaration above; see
[The script's own front door](#the-scripts-own-front-door). (Help
text is not an attribute: the `///` doc's first line feeds `--help`
directly — same section.)

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
record (`Seq.fold (fun c x -> { c with Total = c.Total + x }) initial` —
derive, don't mutate). Lambdas take several params (`fun acc x ->`),
desugaring exactly like `let f a b =`.

Multi-statement lambdas read best MULTILINE. A `(fun ... ->` dangling
at line end opens a body block — any statements are legal inside,
nested `let`s and commands included — and the block closes at its own
`)`, attached to the last body line or alone. A single line with
`;` between the statements is also legal.

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
is the point-free form. One precedence rule to know (it is F#'s):
`|>` and `>>` share a level, so `xs |> f >> g` is `(xs |> f) >> g` —
parenthesize the composition: `xs |> (f >> g)`. The whole operator
table is on [Lexical](reference/lexical.md#operators).

Equality, rendering, and sorting are GENERIC through inferred
constraints — the classic helper shapes just work, and reject at the
use site when they cannot:

```weir
let same x y = x == y

print $"{same 1 1} {same "a" "b"}"
```

Binding names start lowercase; uppercase is for types, modules, and
constructors. Two deliberate limits you will
meet: a bare parameter cannot be *applied* as a function
(polymorphism flows from typed builtins, not lambda guessing), and
`+` on two unknowns cannot infer (int or string?) — anchor one side:
`x + 0`.

```weir-error
let apply f x = f x // a bare parameter cannot be applied as a function
print (apply (fun n -> n) 1)
```

Unit params make thunks: `let cleanup () = ...` runs at
`cleanup ()`, and `cleanup 5` is a type error:

```weir-error
let cleanup () = print "done"
cleanup 5 // expected unit, got int
```

## Branching

`if` is an expression; `else` is optional only when the then-branch is
unit; `elif` chains (it is short for `else if`). `match` has literal patterns (`| 0 ->`, `| "yes" ->` — int/string
literals never complete a match alone; close with `_` or a var),
bool patterns, constructor patterns, record patterns, and `when`
guards — and a non-exhaustive match is a hard error, not a warning.

```weir-error
let t =
    match 1 with
    | 0 -> "zero"
    | 1 -> "one" // literals never complete a match: add a _ or var arm
print t
```

So is its dual, an arm made unreachable by a catch-all above it:
remember a lowercase pattern *binds*, so a typo'd constructor is a
catch-all, and weir stops it with a did-you-mean instead of silently
matching everything.

```weir-error
type V =
    | Pass
    | Failing
match Pass with
| pass -> print "ok" // 'pass' BINDS — did you mean 'Pass'? the next arm is unreachable
| Failing -> print "no"
```

Record patterns destructure by field name — in a `match` arm, a
`let`, or a `for` binder (never a function param — params stay plain
idents). Fields keep their declared case, binders are lowercase, and
there is no punning: `{ names = n }`, never `{ names }`. A field
pattern may hold a literal, which makes the arm REFUTABLE — filter
and destructure in one motion:

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

A refutable record pattern never completes a match alone — the same
rule literal arms have; close with `_` or a var:

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

### How a line decides

Weir has two modes, and the HEAD WORD of a statement picks one: a
name bound in scope (or a builtin) makes the line an expression —
ordinary application; an unbound bareword runs the external program
of that name. Builtins shadow PATH; `^ls` forces the real one, and
params shadow PATH inside their own body.

```weir
let greet name = print $"hi {name}"
greet "io"
echo hi io
```

Inside command mode everything is an inert argv word — nothing
expands, nothing splits — and islands of expression open only at
the splice markers (`$name`, `(expr)`) and close again. Expression
mode is everywhere else: right of a `let`, inside interpolation
holes, inside `$()`/`!()`. The two never blend mid-word (glued
pieces are refused outright), and the [editor colors](#what-the-editor-colors-mean)
paint exactly this boundary from the parse.

**The pipe glyph: the right-hand side decides.** `|` feeds a program
(`git log | grep x`); `|>` applies a function
(`git log |> Seq.head`). Read `|` as "fed to a program" and `|>` as
"transformed by a function". Using the wrong one is an error naming
the other operator; the operator table lives on
[Lexical](reference/lexical.md#operators), the command-mode rules on
[Commands](reference/commands.md).

A `let` takes a bare command on its right-hand side anywhere a
`let` goes — top level or inside a function body, plain
(`let tree = git rev-parse HEAD |> Seq.exactlyOne`) or with params,
whose values splice like any binding
(`let commitOf r = git rev-parse $r |> Seq.exactlyOne`). Params
shadow PATH inside their own body, so `let f x = x` stays the
identity whatever happens to be installed. When the expectation is
ONE line, `Seq.exactlyOne` says so — `Seq.head` takes the first and
silently accepts more, hiding a wrong-arity output; save `head` for
"the first of many".

One rule to know about `!`: weir has no `!`-negation. Negation is the
word `not`; `!` means DO IT. Two markers bring full command chains
into expressions — `$(...)` captures the output, `!(...)`
runs-and-streams (unit, raises on nonzero). Prefer the bare `let`
form when the whole right-hand side is the chain; `$()` is for
everywhere the command is a sub-expression — inside records, holes,
and nested splices:

```weir
let ready = 1 > 0

if ready then
    sh -c "echo preparing"
    sh -c "echo prepared"

if ready then
    !(sh -c "echo inline-form")
    print "mixed with expressions"

let latest = git log -1 "--format=%h" |> Seq.exactlyOne
print $"at {latest}"

let tagged = $"at {$(git log -1 "--format=%h") |> Seq.exactlyOne}"
```

There is no syntax for a computed program NAME — branch the whole
command line instead (`if hot then rg pat else grep pat`). And do
not bind an `if`-effect block to a `let`: the binding is eagerly
evaluated unit, and a bare `if` statement says what it means.

Splice values into argv with `$name` or `(expr)`. A spliced value is
always exactly one argv entry, never re-split — which is why there is
no injection class to defend against. And argv pieces never
concatenate: `$root/*` and `--flag="value"` are refused outright,
because the glued halves would otherwise be separate arguments. One
word is built one way — interpolation:

```weir-error
let root = "build"
rm -rf $root/* // argv words do not concatenate — write $"{root}/*"
```

What weir's command lines do NOT do:

- no glob expansion — use the function `Path.glob`
- no `&&` — write two statements
- no `$VAR` expansion — splice weir bindings instead
- no redirects — `>` and `>>` pass through as literal argv, with a
  warning naming what to use instead (`cmd |> File.write "out.txt"`,
  or `File.append`)

For bash semantics, run bash: `sh -c "the bash line"`.

```weir
// redirection: a function on the right takes |> (the pipe rule)
within tmp d
    echo redirected |> File.write $"{d}/out.txt"
    File.read $"{d}/out.txt" |> Seq.iter print
```

One footgun rides along with that escape hatch: inside the quoted
line, `$w` is SH'S variable, not weir's binding. Weir passes the
string verbatim (a string means the same thing everywhere), sh
expands its own — usually empty — `w`, and the answer is silently
wrong rather than an error. To splice a weir value into a bash line,
interpolate it in before sh ever sees it (`sh -c $"echo got-{w}"`);
when you don't need bash at all, the bare argv splice (`echo $w`)
was what you wanted all along.

```weir
let marker = "guide"
echo tagged $marker (40 + 2)
sh -c "echo one && echo two"
sh -c $"echo interpolated-{marker}"
```

The effect loop is `for … do` — the shell shape, typed. A bare
command body streams and raises per iteration. Commands are legal
statements inside any block, so a multi-line body just works — two
git lines under an `if`, `fun f -> git add $f` under `Seq.iter`, a
fetch line above a value in a block-let. The loop variable splices
like any binding.

Pipelines remain the way to transform values (`|> Seq.map …`);
`for` is for doing something N times. Underneath they are the same
machine — `for` desugars to `Seq.iter`.

```weir
for greeting in ["hello"; "again"] do
    sh -c $"echo {greeting}"

let squares = [for x in [1..5] -> x * x]
squares |> Seq.map show |> print
```

YAML templates are a checked block literal — paste a manifest,
replace values with splices; the structure is parsed at check time
and spliced values are nodes, never text:

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

When the block is not YAML — a config fragment, an embedded
script, any literal lines — `<<<` is the heredoc block, the plain
multiline literal: every byte below the marker is content (`$` and
`{` included), interior blank lines and relative indentation
survive — trailing blanks clip, the block-scalar rule — and the
value is `seq<string>`, one element per line — ready for
`File.write`, a pipe, or the `Seq` module. `$<<<` is its
interpolated twin with exactly the string forms' hole rules:
`{expr}` substitutes, `{{` and `}}` are literal braces, and `$`
STILL stays a byte — shell text passes through untouched. A glyph,
not a word: no binding is reserved, and the marker can never read
as a splice.

```weir
let host = "db.example"
let conf = $<<<
    server {host}
    retries {2 + 1}
    literal $HOME and {{braces}}

conf |> File.write "app.conf"
File.read "app.conf" |> Seq.iter print
```

A scratch directory is a SCOPE, not a chore:
`within tmp <name>` binds a fresh directory for the block and removes
it on every exit — including the raise path, which is the half that
matters. The block is an ordinary expression block: commands run,
the last expression is the value.

```weir
let digest = within tmp dir
    ["payload"] |> File.write $"{dir}/f.txt"
    Str.sha256 (File.read $"{dir}/f.txt" |> Str.join "-")
print (Str.sub 0 12 digest)
```

An `if`/`elif` condition takes a command chain directly:
`if test -f $path | succeeds then …` — the chain's
argv stops at `then` (only there; `then` stays an ordinary argv word
everywhere else — quote `"then"` to pass the literal word to a command
from a condition). The checker still demands `bool`, so a streaming
chain is refused with the fix named (`expected bool, got
seq<string>` — add `| succeeds`).
Bind first when the verdict is used twice: `let ok = cmd | succeeds`
then `if ok then …`.

Statement position works too (`within tmp d` + effects, unit by the
ordinary discard rule). The other kinds CONSUME an argument instead
of producing one: `within cd "build"` runs its block there and
restores on every exit (a missing path errors before the block runs,
naming the absolute path); `within env vars` overlays child spawns
for the block (weir's own env is untouched) — nested overlays
compose, inner keys winning on collision.

```weir
let vars = [Env.pair "GIT_AUTHOR_NAME" "weir-bot"]
within env vars
    sh -c "echo committing as $GIT_AUTHOR_NAME"
```

Two more kinds complete the discipline. A bare `within` holds no
resource at all — just the body and a trailing `always` block that
runs on EVERY exit (normal, raise, `exit n`, SIGINT/SIGTERM; `kill
-9` is the one exception). When both the body
and the cleanup fail, the ORIGINAL error propagates and the cleanup's
failure goes to stderr with a marker; teardown always continues
outward. And `within lock "path"` holds an advisory file lock for the
block — blocking by default, `timeout=30s` raises on
exhaustion, safe across processes and `pmap` arms alike, and released
by the kernel on any death, `kill -9` included:

```weir
within tmp d
    within lock $"{d}/demo.lock" timeout=10s
        within
            print "one holder at a time"
        always
            print "released either way"
```

The whole family at a glance (`within proc`, the background-process
form, is covered under Parallelism):

| form | holds | on every exit |
|---|---|---|
| `within tmp d` | a fresh directory | removes it |
| `within cd "path"` | the working directory | restores it |
| `within env vars` | an env overlay for child spawns | drops it |
| `within` … `always` | nothing — a body plus cleanup | runs the `always` block |
| `within lock "path"` | an advisory file lock | releases it — the kernel does, even on `kill -9` |
| `within proc h = cmd` | a background process | kills and reaps its tree |

A scratch TREE composes the family: `Dir.create` for
structure, `Path.glob` to find, `Dir.deleteAll` (the visibly-named
destructive one) to end it — all inside `within tmp`, whose exit
tolerates a block that already removed its own directory:

```weir
within tmp d
    Dir.create $"{d}/build/out"
    ["artifact"] |> File.write $"{d}/build/out/a.txt"
    print $"{Path.glob $"{d}/**/*.txt" |> Seq.length} artifact(s)"
```

Copies and moves take (src, dst) and REFUSE an existing
destination — `File.delete` first if you mean to overwrite. `Dir.create` alone
is idempotent: an existing directory is the post-condition it was
asked for.

Secret data is where base64 comes in: `Str.toBase64`
encodes UTF-8 bytes as ONE unwrapped line (no 76-column MIME wrap, no
`-w0` tax), so a token splices straight into the template:

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

Decoding is honest both ways: `Str.fromBase64` raises on malformed
input AND on valid base64 of non-text (a PNG's bytes are not a
string — corruption must not wear a success); `Str.tryFromBase64` is
the `Option`-returning variant, for API- or attacker-supplied input. `Str.sha256`
digests the UTF-8 bytes as lowercase hex, `sha256sum`-parity.

Block scalars are what a ConfigMap needs: a `key: |` (or `|-`)
header opens LITERAL content — `$VAR` and `for` lines inside it are
bytes, because embedded scripts are full of `$` and silently
substituting into them is the one thing a template must never do.
Templated content interpolates upstream and splices as a whole
value. `|` means the string ends with one newline, `|-` with none —
the form follows the value in both directions, and a multiline
splice renders as a block scalar automatically:

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

A `yaml` block can name a vendored JSON schema on its marker line
(`yaml schema=k8s-service`), and the checker validates the
template's structure before line one runs — within a stated
boundary: the schema validates what the checker can see, and
`for`-generated content is structurally unchecked. Vendoring
(`weir add schema`), the lock, and the full boundary live in
[schemas.md](tooling.md#yaml-schemas).

Nonzero exit raises when the stream is forced. To inspect instead of
raise, make the run data:

```weir
let r = git log --oneline -1 | complete
print $"exit {r.exitCode}"
```

Multi-line scripts: a statement starts at column 0, indented lines
continue it, and the NEXT column-0 line ends it — blank lines and
comment lines are transparent, so blocks group freely with gaps. An
indented `let` closes at the next line of the same indent — F# light
syntax.

Doc comments: a `///` line attaches to the declaration right below it
(a blank line breaks the link; an attribute line is transparent, so
`///` above or below a field's `[<...>]` both attach) and renders on
hover and in completion
— on let bindings, `type` declarations, record fields, and union
cases. The editor shows the type first, then the doc. A doc must sit at
its declaration's indent; `weir fmt` keeps it there. On an `Args.load`
field the doc does double duty: its FIRST line is the field's `--help`
text (hover still shows the whole doc). One source — help and hover
cannot drift.

## Exit codes: from command to value

One rule: **output goes where the meaning goes.**

| form | output | result |
|---|---|---|
| `cmd \| succeeds` | silent | `bool` (`exitCode == 0`, exactly) |
| `cmd \| complete` | captured | `{ exitCode; stdout; stderr }` |
| `cmd \| orFail "msg"` | streams | unit; raises `msg (exit N)` on nonzero |
| `cmd \| exitCode` | streams | the code as `int`; never raises |

Predicates and inspectors are quiet/captured because their output IS
the result; asserts and control flow stream because their output is
for the human. `succeeds` means `exitCode == 0` exactly — grep's
no-match counts as false; when codes are data, use `| complete`. A watched build that decides:

```weir
let rc = sh -c "echo building...; exit 130" | exitCode

match rc with
| 0 -> print "deployed"
| 130 -> print "cancelled"
| c -> fail $"build: exit {c}"
```

`exitCode` refuses capturing/discarding positions with a teaching
error (`$()` captures — use `| complete` there; a bare statement
discards — bind or match). When you need the code AND the captured
output — fzf's selection and its cancel code — use `complete`.

```weir-error
sh -c "exit 3" | exitCode // a bare statement discards the code — bind or match it
```

## What the editor colors mean

With the LSP attached, editors color weir's one novel boundary — the
line between command and expression — from the checker's own verdict.
- command heads color as callables
- their argv colors as inert words, string-like
- splice markers (`$name`, the parens of an `(expr)` splice) color as
  operators — and everything inside a splice keeps ordinary code
  coloring, the visual message being "this island is code"

If text you meant as an expression renders argv-colored, or a bound
name you meant as a command doesn't, the coloring is not wrong: it is
showing you the parse, before the checker gets a word in. The REPL
carries the same three-way tint.

Per-editor setup — Neovim, Helix, Emacs, VS Code, micro — lives in
[editors.md](editors.md); any LSP-capable editor works, since
`weir lsp` speaks stdio JSON-RPC and serves diagnostics, hover and
completion from the same pipeline the runner uses. For agent loops
and CI, `weir check --json file.weir` is the no-editor route.

## Per-child environment

A command can run with extra environment variables — an overlay on
the inherited environment: those names set, the rest kept, the
parent untouched. `Env.fromFile` reads the dotenv subset: `KEY=VALUE`,
optional quotes, `#` comments. It does not read `export` lines or
expand `$VAR` references — those need a shell to evaluate them. If a
file genuinely needs sourcing, run it in one:

`sh -c "set -a; . ./file.env; your-command"`

Bind the env once, then attach its name to the command marker —
`!e(...)` runs for effect, `$e(...)` captures the output:

```weir
["GREETING=hello"] |> File.write "demo.env"

let e = Env.fromFile "demo.env"

!e(sh -c "echo child: $GREETING")
!e(sh -c "echo again: $GREETING")

print (Env.get "GREETING" |> Option.defaultValue "parent stays clean")
```

The env name attaches directly to the marker: `$e(...)` or
`!e(...)`. A `!name` at the end of a line does the same
for a whole command block — every command in the indented block below
it runs with that environment:

```weir
["STAGE=prod"] |> File.write "stage.env"

let e = Env.fromFile "stage.env"
let ready = 1 > 0

!e(sh -c "echo inline: $STAGE")

within env e
    sh -c "echo block one: $STAGE"
    sh -c "echo block two: $STAGE"
```

## Matching and scraping text

Raw strings carry patterns and paths without escape noise — F#'s two
kinds, single-line: `@"..."` (backslashes literal, `""` embeds a
quote) and `"""..."""` (no escapes at all, bare `"` fine inside).
Rawness is a property of the literal kind, never of position; all
five string forms live on [Lexical](reference/lexical.md#strings).

`Str.isMatch` is the yes/no test — pipe the subject in, so the line
reads subject-first:

```weir
let name = "test_parser"

if name |> Str.isMatch @"^test" then print "a test"
```

For extraction, the `Regex` pattern matches and captures in one arm.
The literal is checked before line one runs: an invalid regex is a
check error, and the binder must carry exactly as many names as the
pattern has capture groups. In F#, that mismatch is a silent runtime
non-match; here it is a located check error.

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
    |> Seq.choose (function | Regex @"(\w+)=(\d+)" (k, v) -> Some $"{k}: {v}" | _ -> None)
    |> Seq.iter print
```

`function` is the implicit-match lambda — `fun x -> match x with`
in one word. Groups bind as strings; convert explicitly
(`Str.tryToInt`).

The `Regex` literal is raw-only: `@"..."`, or `"""..."""` for
patterns containing quotes. An ordinary escaped string there is a
check error, so the double-escape footgun cannot be written. Computed
patterns live on the expression side — `Str.isMatch`, `Str.rmatch`
take any string expression.

Pulling structure out of text is one pipeline. `Str.rmatchAll`
yields every match's capture groups — lazily, and with no `Option`:
no match is simply an empty seq. The `(?s)` and `(?m)` inline flags
cover DOTALL and MULTILINE. Map each match to what you want,
`Seq.distinct` to dedupe, and pipe a value through an external tool
(`| sha256sum`) when you need one:

```weir
let src = "let a = 1\nlet b = 2\nlet a = 1"

Str.rmatchAll @"let (\w+) = (\d+)" src
|> Seq.map (fun g -> Str.join "=" g)
|> Seq.distinct
|> Seq.iter print
```

And for structured command output, check the typed adapters first:
`|> from json T` / `|> from yaml T` beat hand-rolled parsing every
time.

## File batches: glob is a function, not an expansion

`Path.glob` returns matches as a typed seq. Nothing ever expands
inside argv — a bare `*.txt` in a command stays a literal word, glob
or no glob. Discovery composes like any seq:

```weir
match Path.glob "*.md" with
| [] -> print "no docs here"
| docs -> docs |> Seq.sortBy (fun s -> s) |> Seq.iter print

let pinned = Path.glob "*.md" |> Seq.force
```

A batch splats into a command with `$@` — N files become N argv
words, nothing re-split:

`git add $@(Path.glob "*.txt" |> Seq.force)`

The seq is lazy, so relative patterns resolve against the cwd at the
moment the seq is READ — if a `cd` happens in between, `Seq.force`
the batch first to fix it in place. For paths relative to the script
itself rather than the cwd:

`Path.glob $"{Self.scriptPath |> Path.dir}/fixtures/**/*.txt"`

## Typed values: durations, instants, sizes, floats

Four scalar types, one story: a type instead of a bare int, so a
unit lives in the value rather than a comment — each with its own
literals or parser, `show` round-trips, and boundary rules.

### Time: durations as values

Time is a type, not a bare int whose unit lives in a comment.
A `Duration` stores integer milliseconds; the literals are
single-unit (`500ms`, `30s`, `2m`, `1h`) and `show` renders the
compound shape (`90500ms` shows `1m30.5s`) that `Duration.parse`
reads back. The common shape — a timeout flag that reads naturally
at the call site and in `--help`:

```weir
type Fetch = {
    [<Default 30s>]
    /// give up after this long
    timeout: Duration
}

let cli = Args.load Fetch
print $"budget {cli.timeout}, half {cli.timeout / 2}"
```

`weir fetch.weir --timeout 90s` parses the text. Absent, the field
rests at `30s`, and `--help` shows `default: 30s`.

Durations add, subtract, and scale by ints. `Duration / Duration` is
rejected; the error shows how to get a ratio:
`Duration.toMillis a / Duration.toMillis b`.

`Duration.sleep 500ms` blocks. It is module-qualified so that bare
`sleep 5` keeps meaning coreutils sleep.

In command position, `30s` stays an ordinary argv word —
`timeout 30s cmd` passes the text through untouched. A SPLICED
duration is rejected, and the error names the explicit forms
(`Duration.toMillis d`, or `show d`): what a program wants on its
argv is the program's business, not weir's guess.

### Absolute time

`Instant` is a point on the UTC timeline — deliberately the boring
subset. No local zones, no calendar arithmetic ("add a month" is where
timezone hell lives, and scripts don't need it); shifting by a
`Duration` is exact physical time, so DST does not exist here by
construction. Two points subtract to the `Duration` between them,
which is most of what operations work needs:

```weir
let expiry = Instant.parseWith "notAfter=%b %e %H:%M:%S %Y" "notAfter=Aug 14 12:00:00 2027 GMT"
if expiry - Instant.now () > 24h * 30 then print "cert healthy" else print "renew soon"
```

`Instant.parse` reads ISO 8601 (offsets normalize to UTC on the way
in; a bare date is midnight UTC); `parseWith`/`tryParseWith` read
named formats for log lines — `%Y %m %d %e %b %H %M %S %f %z`, with
prefix semantics so a log line's tail rides free:

```weir
let cutoff = Instant.parse "2026-08-14T10:00:00Z"
["2026-08-14 09:00:01 boot"; "2026-08-14 11:30:00 ready"]
    |> Seq.choose (fun l ->
        match Instant.tryParseWith "%Y-%m-%d %H:%M:%S" l with
        | Some t -> (if t > cutoff then Some l else None)
        | None -> None)
    |> Seq.iter print
```

Instants sort and compare (Ord/Eq); `Args.load`/`Env.load` parse ISO
into `Instant` fields (`--since 2026-08-01`); JSON refuses with the
conversions named (`Instant.epochMs` for an epoch int, `show` for the
ISO string) — timestamps have no single wire convention, so weir does
not guess one.

### Size thresholds: bytes as a type

`File.size` returns a `Size`, so a threshold reads as written and
renders as meant:

```weir
["payload"] |> File.write "guide-sz.bin"
let sz = File.size "guide-sz.bin"
if sz > 4B then print $"large: {sz}"
File.delete "guide-sz.bin"
```

Literals are binary units only: `10MiB`, never `10MB` — the SI
suffixes are ambiguous in the wild, weir refuses to guess, and the
error names the fix. `Size.parse` reads foreign text and
accepts SI as powers of ten there, because the writer of that text
chose the unit.

### Rates and percentages: floats, finite-only

A weir float is always finite — a result that would be `NaN` or
`Infinity` raises instead (`1.0 / 0.0` is an error, like `1 / 0`).
Nothing widens implicitly: `3 / 2` stays integer division, and
mixing sides (`3 / 2.0`) is a type error naming the fix,
`Float.ofInt`. The percentage shape:

```weir
let passed = 7
let total = 8
let pct = 100.0 * Float.ofInt passed / Float.ofInt total
print $"pass rate {pct}%"
```

A float crosses every boundary: a `--rate 0.5` flag with
`[<Default 0.5>]`, an `Env.load` field, a JSON `number` (integer-
shaped values widen on read — JSON has one number type), a YAML
scalar (`rate: 1.5` unquoted is the number; `"1.5"` quoted is a
string — quoting is what tells them apart, in both directions).

Floats render shortest-form and round-trip through `Float.parse`;
an integral float keeps its decimal (`show 1.0` is `"1.0"`).
Equality is the one thing floats do not do: `==` is a check error
(0.1 + 0.2 is not 0.3), and the error tells you what to use instead —
`Float.near a b 1e-9`, or compare after `Float.round`. Sorting
works (`Seq.sortBy` takes float keys); timing ratios read naturally
with `Duration.toSeconds`:

```weir
let ratio = Duration.toSeconds 90s / Duration.toSeconds 1m
print $"{ratio}x the budget"
```

## Binary data: `Bytes`

`Bytes` is the non-text value — an in-memory byte array, opt-in at
both ends: nothing becomes byte-typed by default (commands still
produce `seq<string>`, `File.read` still decodes text). In:
`File.readBytes` (no decode, no line split), `Bytes.fromBase64` /
`tryFromBase64` (malformed raises / `None`), `Str.toUtf8`. Out:
`File.writeBytes`, `Bytes.toBase64`, and `Str.fromUtf8` /
`tryFromUtf8` — the gate back to text, where non-UTF-8 or
NUL-bearing bytes raise or yield `None` rather than corrupting a
string.

```weir
let b = Str.toUtf8 "hello"
print (Bytes.sha256 b)
print (Bytes.toBase64 b)
print $"{b}"

let png = Bytes.fromBase64 "iVBORw0KGgo="
print (show (Bytes.length png))
print (show (Str.tryFromUtf8 png))
```

The boundaries refuse raw bytes, each naming the exit: `print`,
`to json`/`to yaml`, argv splices and `Args.load`/`Env.load` all
point at `Bytes.toBase64` or `File.writeBytes`; a hole or `show`
renders a summary (`<12 B>` above), never content — raw bytes wreck
terminals. `Bytes.length` is a `Size`; `==` is byte equality; there
is no ordering. And to hash a file without loading it,
`File.sha256 path` streams internally.

## Making requests: `Http`

A typed body reaching the wire through `curl` is one flag away from
silent corruption — `-d @-` strips newlines, `--data-binary @-`
preserves them, and nothing errors between. `Http` closes that: the
request is a record, `Http.send` runs it, and a `Json` body carries
the caller's `to json` output byte-exact.

```weir-demo
type Item = { name: string; count: int }

let created =
    Http.send (Http.get $"{api}/items/1") |> fun r -> r.body |> from json Item

let resp =
    Http.send { Http.post $"{api}/items" with
                  auth = Bearer token
                  body = Json ([{ name = "widget"; count = 3 }] |> to json) }

if resp.status >= 400 then fail $"api said {resp.status}"
```

The common case is a CONSTRUCTOR — `Http.get url`, `Http.post url` —
with `with` for the optional part, which is how records are meant to
be used. `Http.get url` equals `{ Http.defaults with method = Get; url
= url }` byte-identically; the constructors just stop you naming the
record. All eight methods have one (`get`/`post`/`put`/`delete`/
`patch`/`head`/`options`/`query`).

For the simplest read — a GET whose body is all you want — `Http.fetch`
is the raising shorthand (the `curl -sf` / JS `fetch(url)` analogue):
it takes a **bare URL** — never a request; `Http.get url |> Http.fetch`
reads like a pipeline and is a type error that names the repair (a
built request runs through `Http.send`). It returns the body and raises
on a non-2xx naming the status, where `Http.send` binds the same
status as data.

```weir-demo
let item = Http.fetch $"{api}/items/1" |> from json Item
```

When the shape belongs to a foreign API and you read it once, write
the type inline — an **anonymous record type**:

```weir-demo
let ip = Http.fetch "https://api.ipify.org?format=json" |> from json {| ip: string |} |> _.ip
```

`seq<{| ... |}>` covers the top-level-array case. The rule of reach:
an anonymous shape is for *reading a foreign shape once*; declare a
record for *your own data and anything reused*. Two anonymous shapes
with the same fields are the same type (field order canonicalizes);
a declared record with the same fields is deliberately a different
type — weir's records stay nominal.

Two adapters, one distinction: `from json T` reads ONE document —
across as many lines as the server felt like using — and gives you a
`T`; `from jsonl T` reads one document per line (NDJSON, the shape
`to json` writes) and gives you a `seq<T>`. Neither inspects its
input to guess which it is.

```weir
type Peer = { host: string; port: int }

let body = ["{"; "  \"host\": \"a.example\","; "  \"port\": 9000"; "}"]
let peer = body |> from json Peer
print $"{peer.host}:{peer.port}"

let peers = ["{\"host\": \"a\", \"port\": 1}"; "{\"host\": \"b\", \"port\": 2}"] |> from jsonl Peer
peers |> Seq.iter (fun p -> print $"{p.host}:{p.port}")
```

A top-level JSON *array* — the list-endpoint shape — declares
itself: `from json seq<Peer>` reads it as one `Peer` per element.

```weir
type Peer2 = { host: string }

let hosts = ["[{\"host\": \"a\"}, {\"host\": \"b\"}]"] |> from json seq<Peer2> |> Seq.map _.host
hosts |> print
```

Fields nest: the rule is recursive. A field is one of:

- a scalar — `int`, `float`, `string`, `bool`
- an `Option` of an admitted type
- a record whose fields are all admitted
- a `seq` of an admitted type

So a real API response types directly:

```weir
type Entity = { entityid: string }
type Doc = { id: string; entityids: Entity; tags: seq<string> }

let doc = ["{\"id\": \"11831032\", \"entityids\": {\"entityid\": \"0033x\"}, \"tags\": []}"] |> from json Doc
print doc.entityids.entityid
```

A self-referential record refuses at check, naming its cycle — the
boundary needs finite trees. A missing array is an error, not a
silent `[]`: absence is `Option`'s job.

ID-keyed objects — keys that are DATA, not schema — read as
`Map<string, T>`: as a field, or as the whole document in the
adapter slot:

```weir
type WDoc = { id: string }

let docs = ["{\"aaa\": {\"id\": \"1\"}, \"bbb\": {\"id\": \"2\"}}"] |> from json Map<string, WDoc>
docs |> Map.pairs |> Seq.iter (fun (k, d) -> print $"{k}={d.id}")
```

Keys are strings only — JSON object keys ARE strings, and a
`Map<int, …>` declaration is refused with that explanation. Pairs
walk key-sorted; duplicate keys last-win; `to json` writes the
object back. The `Map` members:

- `ofPairs` (last key wins), `pairs`, `keys`, `values` (key-sorted)
- `get` (raises, naming the key), `tryGet`, `has`
- `add`, `remove`, `count`

There is no `m[k]` indexing — use `Map.get` — and `==` is not
defined for maps.

`Http.query` is the QUERY method (RFC 10008). QUERY is idempotent by
definition, so `retry attempts=5` around an `Http.query` is safe by
the method's own guarantee — the same wrapper around a POST is a
correctness question you must answer yourself. (Almost nothing serves
QUERY yet, so expect 405 from most endpoints.)

For query strings, `base |> Http.withQuery [("q", term)]`
percent-encodes each key and value — a space or `&` cannot break the
url. The PATH half of the url is still yours to build carefully.

TLS verification is on by default. `Http.send { … with insecure =
true }` turns it off for ONE request (self-signed clusters) — a loud,
per-call field, never a global switch.

**Status is data.** A 404 binds and you branch on it
(`if resp.status >= 400`) exactly as `| complete` treats an exit
code — only a
transport failure (unreachable, TLS, timeout) raises. A health check
is one line:

```weir-demo
let up = (Http.send { Http.defaults with url = $"{api}/health" }).status == 200
```

**Auth is a union carrying a `Secret` whole** — `Bearer token`,
`Basic ("user", pass)` (which does the base64 for you). Interpolating a
token into a string is a check error, and `show` on the request masks
it as `***`. A shared base config is the record's own case:

```weir-demo
let github = { Http.defaults with
                 auth = Bearer token
                 headers = [("Accept", "application/vnd.github+json")] }

let user = Http.send { github with url = $"{api}/user" }
```

For a plain GET with no body or auth, `curl url |> from json T`
stays a fine read — `Http` earns its keep on the request side.
Parallel fetches compose from parts you already know:

`urls |> Seq.pmap (fun u -> Http.send { Http.defaults with url = u })`

The timeout defaults to 30s — a request with no timeout is the
classic CI hang. And `Http` types your request but does not vet your
endpoint: SSRF and URL construction are yours
([SECURITY.md](../SECURITY.md)).

## Secrets: tokens that cannot leak into logs

A `Secret` is a marker the renderers respect:

- `show` gives `***`
- interpolation and the wire boundaries refuse it outright
- `Secret.reveal` is the one place the value comes back out It controls where a value
can flow at the boundaries weir itself renders; it is not storage
and not memory protection ([SECURITY.md](../SECURITY.md) states
those non-claims). The point is coverage: a token cannot slip into a
log line or a shown record by accident.

The primary producer is `Env.load` — env is the standard CI secret
channel (`secrets.GITHUB_TOKEN` becomes an env var), so a `Secret`
field is the main way a token enters:

```weir-demo
type Cfg = { GITHUB_TOKEN: Secret }
let cfg = Env.load Cfg
git push https://$(Secret.reveal cfg.GITHUB_TOKEN)@github.com/…
```

`Args.load` takes a `Secret` field too — though anything passed as
a flag is visible in the process list, which weir does not hide —
and `File.readSecret` reads a mounted k8s/docker secret file. Every USE of the value is a deliberate `Secret.reveal`, so the
audit is the call site. To keep a derived value secret, `Secret.map`
stays inside the wrapper — `"Bearer " + reveal` would launder it:

```weir
let token = Secret.of "s3cr3t"
let header = Secret.map (fun t -> "Bearer " + t) token
print (show token)
print (show header)
print (Secret.reveal header)
```

A `Secret` splices into a command in the clear (`curl -H $header` is
what the type exists for), refuses interpolation (`$"tok: {token}"`
is a check error naming `reveal`), and refuses `to json`/`to yaml`.

## The script's own front door

Argv is the same species of stringly boundary as the environment, and
it loads the same way — declare the shape, load once, typed
thereafter:

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

Field names derive kebab-case flags (`dryRun` becomes `--dry-run`)
and unambiguous first-letter shorts; `[<Short "C">]` pins a short
explicitly, `[<NoShort>]` suppresses one, and `--help` prints the
derived usage — flags, types, optionality, the short truth, and each
field's `///` first line — even on otherwise-invalid invocations. `bool`
fields are presence flags, `string`/`int` are required, `Option`
makes them optional. Loading is strict and collected — all of these
arrive together in one boundary error, before any effect runs:

- unknown flags (with a did-you-mean)
- unexpected arguments
- missing required flags
- unparseable values

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
libx`), for the same reason records beat tuples: a name at the call
site beats a position you have to count. `[<Positional>]` is not a
registered attribute. What is dropped is the typed, declared,
help-generating path, not the ability to read operands: for
hand-rolled shapes, `Args.flag "--clean" "-c"` and
`Args.value "--out"` scan the raw `Self.args` seq, and a multi-value
option reshapes as one flag per value (`--stack X --env Y`).

### Shared flags: containment, not inheritance

Flags every subcommand carries are declared ONCE, on a record that
contains the subcommand union — containment does the sharing, so
nothing is repeated per payload:

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

### Defaults, and declared value sets

An env value with a fixed set of legal values declares that set as a
union of bare cases. A typo then fails at the boundary, with the
candidates listed — instead of selecting a wrong branch three
functions later:

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

`[<Default v>]` on an `Args.load` field fills in the value when the
flag is absent. The field stays non-`Option` — your code reads it
directly — and `--help` shows the default.

On a bool, `[<Default true>]` also mints the opposite flag: the
field rests at true, `--no-x` turns it off, and passing both
polarities is an error naming both. `[<Default false>]` is rejected
on an `Args.load` bool, because a presence flag already rests at
false without help:

```weir-error
type Cli = {
    [<Default false>]
    clean: bool
}
let c = Args.load Cli // [<Default false>] is redundant — presence already rests at false
print $"{c.clean}"
```

`Env.load` reads the same attribute: an absent variable fills in the
default, and any set variable wins. Env bools are text
(`FLAG=false`), not presence, so `[<Default false>]` is legal there.

The attribute takes literals only. A default you have to compute
keeps the field `Option` plus one line of code — the record below
carries both shapes:

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

`Self` groups what a running script knows about itself: `Self.args`
(the arguments), `Self.stdin` (the input stream), `Self.pid` (the
process id), and `Self.scriptPath`.

`Self.scriptPath` is the running script's absolute path. It is
resolved when the script starts, against the directory you invoked it
from, so a later `cd` does not change it. Symlinks are left
unresolved — the path is where the script was *invoked*, not where
the file lives.

For the script's own directory:

```weir
let dir = Self.scriptPath |> Path.dir
print $"has a directory: {Str.length dir > 0}"
```

To resolve symlinks:
`$(realpath $"{Self.scriptPath}") |> Seq.head`

Available in scripts only — the REPL and `-e` refuse each `Self`
member by name, since neither has a file.

## Sharing code: modules and `import`

The moment a script becomes a tool is the moment two scripts want
the same helper. A file that STARTS with `module` (bare, or
`module Name`) is a module: importable and declaration-only —
`type` and `let` definitions, no commands and no bare expressions —
and not runnable itself. Import it by literal path, first in the
file:

```weir
["module Greet"; ""; "/// the shared helper"; "let hello name = $\"hi {name}\""] |> File.write "greet.weir"
["import \"./greet.weir\" as G"; ""; "print (G.hello \"weir\")"] |> File.write "use-greet.weir"
weir use-greet.weir
```

Access is always qualified — `G.hello`, `G.Ctx` for an imported
type, `G.Ctx { field = v }` to construct its records. Without `as`,
the alias is the module's declared name (or the capitalized
filename). Nothing leaks bare: an imported union's cases are
reached qualified too, and a local declaration always wins over an
imported name.

Resolution happens at CHECK time against the literal path — nothing
loads at runtime, and a missing file is a located error naming the
resolved absolute path. Imports are transitive; a module two
importers share is checked once (diamonds collapse), and an import
cycle is a named check error. `import` is script-only — not `-e`,
not the REPL. The wrong-kind errors are named as well:

```weir
["print 1"] |> File.write "plain.weir"
["import \"./plain.weir\""; "print \"unreachable\""] |> File.write "use-plain.weir"
let r = weir use-plain.weir | complete
print (if r.exitCode <> 0 then "importing a non-module is a named check error" else "unexpected")
```

(The child's message: `plain.weir is not a module; add module at the
top, or invoke it as a command`.) A module `let` that runs a command
is refused too — wrap it in a function; a module declares, a script
runs.

A module can also come from another repo, vendored and committed:
`weir add module github.com/org/repo//lib/x.weir@v1.2.0 --as x`
fetches it into `.weir/modules/`, pins it in the lock, and it
imports by name from anywhere under the project —
`import "weir:x" as X`. The mechanics (pinning, updates, private
repos, what `add` validates) live in
[tooling.md](tooling.md#remote-modules).

## Retrying and polling

The bounded loops share one shape:

- a `key=value` head
- a block body whose last statement is the value
- an `until` section that binds the value for the condition

A `bool` body is its own predicate, and the form then yields unit:

```weir
retry attempts=3 delay=100ms
    weir -e "print 1" | succeeds
```

To keep the successful attempt's output, yield a value and bind it
in `until`:

```weir
let out = retry attempts=3 delay=100ms
    let r = weir -e "print 42" | complete
    r
until r
    r.exitCode == 0
print (out.stdout |> Seq.head)
```

`poll` is the same shape bounded by time instead of attempts
(`poll timeout=5m interval=10s` — wait-for-ready loops). Exhaustion
raises naming the attempts and elapsed time; an unbounded loop is
unrepresentable. Options are a record underneath — compute and share
them: `let fast = { Retry.defaults with attempts = 3 }` then
`retry fast`.

## Parallelism

`Seq.pmap` / `Seq.piter` fan out over a seq: parallel execution,
results in input order, first failure rethrown. Workers fork the
session — `cd` inside a worker is worker-local and gone at the join.
There is no async/await, and there never will be. Processes and
pipelines are weir's concurrency model. Under the hood the fan-out
members run each arm on its own thread inside the weir process —
worth knowing when you reason about shared state, or about where a
raise lands. A task that truly needs async belongs in full F#.

A background process gets a SCOPE, never a `&`: `within proc` binds a
handle, and at every block exit — normal or raise — the process tree
is killed and reaped. The five-step shell ritual (`server &`, poll
the port, use it, `kill`, `wait`) collapses to:

```weir
within proc srv = python3 -u -c "import socketserver,http.server as h; s=socketserver.TCPServer(('127.0.0.1',8617),h.SimpleHTTPRequestHandler); s.serve_forever()"
    poll timeout=15s interval=100ms watch=srv
        Net.portOpen 8617
    print $"server {Proc.pid srv} answered"
print "scope closed, server gone"
```

(In real scripts `python3 -m http.server` is the usual choice; this
block's inline server skips the reverse-DNS lookup `http.server` does
at bind — on locked-down CI hosts that lookup can hang under privacy
gating, a platform behavior worth knowing when a server is
mysteriously up-but-not-listening.)

`watch=` is worth reaching for every time. A child that crashes at
startup fails
the poll at the next interval tick, and the error carries the
child's own last output. A plain timeout also reports whether the
watched process was still running when time ran out.

The child's streams spill to files — bounded by disk, not memory —
and never reach the parent's terminal or its stdout data channel, so
a chatty server cannot break `weir script | next`. `Proc.tail` reads
the last ~100 spill lines. Pass the child's unbuffered flag
(python's `-u`) when you want those lines live: children
block-buffer stdout when it is a pipe.

A scoped child's own exit is DATA — the one place raise-by-default
does not apply. Failure surfaces through `watch=` or `Proc.wait`
(which yields the exit code), nowhere else. `Proc.stop` tears a
scope down early, and nested scopes release last-in, first-out.

The teardown guarantee, precisely: normal exit, raise, SIGINT and
SIGTERM all close every scope; `kill -9` of weir itself cannot, by
definition. A process that must outlive the script is a daemon —
that belongs to systemd or launchd, and weir deliberately has no
`nohup`.

```weir
Dir.create "wa"
Dir.create "wb"
["wa"; "wb"] |> Seq.pmap (fun d ->
    let x = cd d
    pwd |> Seq.head) |> print
```

## Declaring a tool: command signatures

Weir checks that `bicep` exists; a signature closes the next gap —
`bicep buidl --outfil x` becomes a check-time catch instead of a 3am
failure. Generate one from the installed binary, then declare it per
script (prose here because signatures need a `.weir/` tree; the e2e
battery holds the runnable truth):

    weir add sig bicep        # probes the tool, writes .weir/sigs/bicep.weir + the lock

    #sig bicep                # in each script that wants the checking
    bicep build --outfile x.json

A generated signature is PARTIAL — unknown flags warn, because a
scraped surface may be incomplete; verified by hand and marked
`exhaustive`, unknown flags become errors. `weir check` never runs
the tool, so checking works for tools that only exist in CI. The
full cycle — generate, verify, regenerate — and the `.weir/` tree it
lives in are [signatures.md](tooling.md#command-signatures) and
[project.md](tooling.md#project-layout-weir).

## What a script can do, before it runs

`weir check --can` extends check-before-run one step: because command
heads are literal and nothing expands in argv, the set of commands a
script can reach is statically knowable — so weir reports it, with a
site for every line:

```text
deploy.weir can (capability, not behaviour — an untaken branch still counts):
  ⚠ this report is incomplete: 1 opaque site(s) — an interpreter's argument cannot be analyzed
  runs:
    git  deploy.weir:3:1
    sh  deploy.weir:5:1
  opaque:
    sh takes a program as its argument — not analyzed  deploy.weir:5:1
  writes:
    File.write out.txt  deploy.weir:7:10
  network:
    Http.fetch https://api.example.com/items  deploy.weir:8:12
  secrets:
    loads token (Env.load Cfg)  deploy.weir:2:11
    a Secret reaches the argv of curl (ps-visible — the stated non-claim)  deploy.weir:9:9
```

Three honesty rules:

1. It reports **capability, never behaviour** — a command inside a
   branch that never runs still counts.
2. `sh -c` and the other interpreters are first-class unknowns,
   counted in the header. `--strict` exits 2 when any exist, so a CI
   gate can refuse unanalysable scripts.
3. The claim covers what weir itself does — any external can do
   anything, and no static report closes that.

Imports are walked transitively, and a module's capabilities carry
the module's own file:line. `--json` emits the same facts for
machines.

## Failing and diagnosing

`fail "reason"` stops the script with a located error and exit 1.
`exit n` exits with a specific code, silently — how you pass a
child's failure along. There is no general try/finally.
Cleanup that guards a resource belongs to the `within` family, which
releases on every exit, raise included. For a fallible middle step
that is not a resource, make it data with `| complete`, run the
cleanup, then propagate:

```weir
let r = sh -c "exit 0" | complete
sh -c "echo cleanup runs either way"
if r.exitCode <> 0 then exit (r.exitCode)
```

For the two commonest exit-code shapes there is sugar —
`cmd | succeeds` (the bool) and `cmd | orFail "msg"` (the one-line
assert: unit on success, `msg (exit N)` raised on failure); the full
table lives in [Exit codes](#exit-codes-from-command-to-value):

```weir
let onBranch = git symbolic-ref -q HEAD | succeeds
sh -c "true" | orFail "sanity failed"
print (if onBranch then "on a branch" else "detached")
```

`Log.info $"starting {n}"` (and `trace`/`debug`/`warn`) writes
levelled diagnostics to stderr. `WEIR_LOG=debug weir script.weir`
turns the detail on for one run without editing anything;
`WEIR_LOG=off` silences the log. `printerr` and `fail` still reach
you at every level — deliberately, there is no `Log.error`, because
an error an env var can silence is the one message you needed.
Stdout stays byte-identical at every level: logging never touches
the data channel.

`printerr` writes to stderr like `print` writes to stdout —
diagnostics there, data on stdout:

```weir
printerr "starting"

if 1 > 2 then fail "impossible"

print "done"
```

## The REPL

`weir` with no arguments starts the REPL. Bare names work here
(`map`, `where` — scripts require the qualified names), values echo
back with their type, and a multi-line entry grows as you type —
weir asks its own parser whether the statement is complete. `Ctrl+C`
abandons the line; `Ctrl+D` exits, and typing `#quit` does the same.

`#help` lists the directives and the modules. `#help Seq` lists one
module's members — a question FSI cannot answer. `#help Seq.collect`
shows one member's doc, rendered from the same source hover uses, so
the two cannot disagree.

The `#` prefix marks a line addressed to the tooling rather than the
language. File directives (`#sig`, `#schema`) are read at check
time; session directives (`#help`, `#quit`) run now. One glyph, two
lifetimes.

The rest of the manual — what the echo shows and its cap, the
prompt's colors, the multi-line keybindings, and the init file
(declarations for the prompt, `#session` for settings) — lives in
[repl.md](repl.md).

## Where weir ends

The complete border with F# — what is deliberately different, what is
rejected by design, what is merely pending — lives in
[tests/fidelity/divergences.md](../tests/fidelity/divergences.md),
machine-verified against the real F#
compiler in CI. The short version:

- no mutation
- no exceptions — values, `fail` and `exit` instead
- no OO
- no async
- no user type classes — the three built-in constraint families are
  closed

When a task outgrows a shell, the graduation path is full F# — weir
points there on purpose.

Where to next:

- [The reference](reference/lexical.md) — the language rulebook,
  page by page
- [skills/weir/SKILL.md](../skills/weir/SKILL.md) — the exhaustive,
  agent-oriented rule file: every shipped member, every rule,
  CI-executed
- [COMING-FROM.md](COMING-FROM.md) — per-language translation tables
  for arrivals from bash, PowerShell, Python, or Make
