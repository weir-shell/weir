# Writing weir scripts (weir is NOT F#, NOT bash, NOT Nushell)

Every fenced `weir` block below is executed against the AOT binary in CI
(`ci/skill-doc.sh`); `weir-error` blocks must FAIL. A line here that
stops being true fails the build.

## Files and running

- Shebang `#!/usr/bin/env weir`; extension `.weir`; run `weir file.weir [args]`.
- `weir fmt file.weir` canonicalizes indentation (4 per block depth);
  `--check` for CI; `--qualify` converts `#loose` scripts to strict.
- The whole file typechecks before ANY line runs. A check error = zero
  side effects. Iterate until it checks.
- Comments are `//`. A blank line ends a statement (F# divergence);
  comment lines are transparent and safe inside blocks.
- Scripts are STRICT: every library name is module-qualified —
  `Seq.map`, `Str.trim`, `Option.defaultTo`, `File.read` — including in
  command pipelines (`| Seq.map Str.trim`). Bare names (`map`, `where`)
  exist only in the REPL and `#loose` scripts. If unsure, qualify.
- `args : seq<string>` and `stdin : seq<string>` exist in scripts only.

## The statement rule (most important)

- Command lines (`git add -A`, `tar xf x.tgz | ...`) stream their output.
- EVERY other statement must be unit: bind values (`let x = ...`) or
  print them (`expr |> print`). A discarded value is a check error.
- Effect blocks: same-indent lines sequence (each but the last must be
  unit) — `if clean then` + indented `run`/`print`/`fail` lines works.
  Explicit form: `e1 ; e2`. `;` binds INTO if/match bodies (block-
  shaped): to sequence AFTER an if, parenthesize it.
- `;` does NOT chain commands: in a command line it is a literal argv
  word (you get a warning). One command per line.
- `print` takes string, int, bool, or `seq<string>`. For records,
  unions, options and debugging: `show x` renders ANY value (except
  functions) as a REPL-shaped string — `print (show row)`,
  `$"got: {show r}"`. Lossy debug format (strings come quoted, long
  seqs truncate); `print` remains the raw data channel.
- `xs |> Seq.map print` does nothing (lazy) and is a check error as a
  statement; use `xs |> Seq.iter print`.

```weir
let files = git ls-files
print $"tracked: {files |> Seq.length}"
["a"; "b"] |> Seq.iter print
git ls-files | Seq.first 1
```

```weir-error
// the casing law: binders start lowercase
let Total = 1 + 2
print "unreached"
```

```weir-error
// a discarded value is a check error, before anything runs
"this string goes nowhere"
```

```weir
let ok = 1 > 0

if ok then
    !(sh -c "echo step-one")
    print "step-two"

let branches = $(git branch) |> Seq.length
print $"branches: {branches}"
```

## Syntax that differs from your priors

- Equality is `==` (never `=`). `=` is for `let` and record fields only.
- Records need a declared type with the exact field set (no width
  subtyping, no anonymous records): `{ Host = h; Port = p }` needs
  `type Endpoint = { Host: string; Port: int }`. For a transient
  pair with no names, use a tuple instead. Copy-and-update derives:
  `{ r with F = v }` (multi-field `;`-separated; nested `I.X` sugar;
  the source may be an expression — bare match/if need parens).
  Update never ADDS fields, and a row-typed updater
  (`let bump r = { r with N = r.N + 1 }`) generalizes to any record
  with the field. A comma between fields is a parse error (F#
  silently makes the field a TUPLE there; weir refuses the trap).
- Type declarations and list literals continue across lines inside
  the open bracket (line break = separator; a dangling operator
  continues the same element; blank lines inside are errors naming
  the bracket). House style is Stroustrup: opener dangles at the
  head line's end, entries one level in, closer alone at the head's
  indent — `type Cli = {` / four-space fields / `}`. The aligned
  style (`{ x` with column-aligned fields) stays accepted.
- Record fields take attributes, F#'s syntax: `[<Short "c">]`,
  `[<Doc "text">]`, `[<NoShort>]`, `[<Positional>]` — `;`-separated
  lists, literal args only (string/int/bool). Attributes are
  check-time data, fully erased: an attributed record is the same
  type as a bare one. The name set is CLOSED — an unregistered name
  is a check error with a did-you-mean; consumers (typed argv's
  shorts/help) are a coming feature, so today every attribute is
  legal-and-inert. Attributes attach to record fields only.

```weir
type Cli = { [<Short "C"; Doc "clean first">] Clean: bool; [<Positional>] Target: string }
let args = { Clean = true; Target = "prod" }
print args.Target
```

```weir-error
type T = { [<Shrot "c">] A: int } // unknown attribute: did you mean 'Short'?
```

- Union cases carry tuple payloads for multi-value: `Case of int * string`;
  match with `| Case (n, s) ->`.
- `let f x y = ...` defines a curried function (desugars to nested
  `fun`). Params are idents, `()`, or PARENTHESIZED irrefutable
  patterns (`let dist (x, y) = ...`) — no type annotations. A
  param-ful let TAKES a command RHS
  (`let revParse r = git rev-parse $r | Seq.head`): params shadow
  PATH inside their own RHS (bindings-beat-PATH's scope; `^x` still
  forces the binary), and a spliced param defaults to string at the
  statement boundary. Splices are WHOLE argv entries — `--file=$f`
  passes literally; spell `--file $f` or an interp arg.
- `+` on two unknown params cannot infer (int-or-string): anchor one
  side (`x + 0`) or take data in. All single-typing operators
  (`- * / > <`) default to int; `let rec` and `mutable` are reserved
  words with no meaning. But `==`, `show`, and `Seq.sortBy` ARE
  generic (inferred constraints): `let same x y = x == y` works at any
  equatable type — rejected only at functions/seqs, at the USE site.
- Literal patterns work (`| 0 ->`, `| "yes" ->`, `| () ->`, nested in
  constructors) but int/string literals NEVER complete a match alone —
  add a `_`/var arm or it is a hard error. Guards remain legal.
- Raw strings, F#'s two kinds, both SINGLE-LINE: `@"..."` verbatim
  (backslashes literal; `""` = one embedded quote) and `"""..."""`
  (no escapes at all; bare `"` fine inside). Rawness belongs to the
  literal KIND, never to position — a string means the same thing
  everywhere.
- The `Regex` pattern matches and extracts in one arm:
  `| Regex @"(\w+)=(\d+)" (k, v) ->`. The literal is RAW-ONLY
  (`@"..."` or `"""..."""` — an ordinary string there is a check
  error; the double-escape footgun is unrepresentable). Compiled at
  CHECK time (invalid regex = check error) and the binder arity must
  equal the capture count — `()` for 0, one name for 1, a tuple for n
  (non-capturing `(?:...)` does not count). Groups bind as STRINGS;
  convert in the arm. Regex arms never complete a match. Computed
  patterns live on the expression side: `Str.isMatch pat s` (bool),
  `Str.rmatch pat s` (Option<seq<string>>) — any string, and raw
  literals read best: `Seq.where (Str.isMatch @"\.md$")`.
- Params are plain idents OR `()` (a unit param: `let cleanup () =`;
  `cleanup 5` is a type error). Other pattern params stay rejected.
- No async/task/await — processes and pipelines are the concurrency
  model. A task that truly needs async belongs in full F#, not weir.
  For fan-out over items: `xs |> Seq.pmap (fun x -> ...)` (parallel,
  ordered results) / `Seq.piter` for effects. Workers fork the session:
  `cd` inside a worker is worker-local and gone at the join — force
  worker output inside the worker (`Seq.head`/`Seq.toList`) if its cd
  matters.
- No `let rec`, no loops, no mutation. Iteration is pipelines over seqs;
  `[1..10] |> Seq.iter (fun i -> print $"{i}")` for counted repetition.
  Ranges are lazy; `[a; b; c]` lists are eager. Running totals are
  `Seq.fold`: `xs |> Seq.fold (fun state x -> ...) init` — STATE FIRST
  in the folder (bash/JS `reduce` priors put it second), STRICT
  (consumes the source; not for infinite seqs), and the piped
  spelling anchors the folder's types (prefer it). Multi-accumulator
  loops fold over a record: `Seq.fold (fun c x -> { c with ... }) c0`.
- Match-or-skip over a stream is `Seq.choose` (lazy, qualified-only):
  the arm returns `Some out` or `None`, never a sentinel `""` to
  filter later. The natural pair with the `Regex` pattern:

```weir
let refs = ["sha1 refs/a"; "junk"; "sha2 refs/b"]
refs
    |> Seq.choose (fun l -> match l with | Regex @"(\w+) refs" s -> Some s | _ -> None)
    |> Seq.iter print
```
- Lambdas take multiple params: `fun acc x -> ...` desugars to nested
  lambdas exactly like `let f a b =` sugar (same param set — idents,
  `()`, parenthesized irrefutable patterns; duplicates rejected).
- `if c then a else b` is an expression; `else` is mandatory unless the
  then-branch is unit (`if ok then print "yes"` is a valid statement).
  `elif` chains as in F# (`if / elif / elif / else`) — pure spelling
  for `else if`.
- `match` supports `| true ->` / `| false ->`, `when` guards, and
  constructor patterns. **A non-exhaustive match is a hard error**, and
  guarded arms don't count as coverage — always include an unguarded
  catch-all or cover every case. The dual is also a hard error: an
  unguarded catch-all with arms below it (a lowercase name like
  `| clean ->` BINDS — a typo'd constructor swallows the match).
  Constructor patterns need a scrutinee whose type is already KNOWN —
  params are not typed FROM patterns (`let f x = match x with
  | A -> ...` is a check error; match on typed data).
- `let x = e in body` inline; in multi-line scripts an indented `let`
  line closes at the next line of the same indent (F# light syntax).
- String/seq ops are data-last for piping: `Seq.where (Str.contains "err")`.
- `>>`/`<<` compose functions (`Seq.map (Str.trim >> Str.toLower)`).
  `|>` and `>>` SHARE precedence (F#'s rule): `xs |> f >> g` is
  `(xs |> f) >> g` — parenthesize the composition, `xs |> (f >> g)`.
  A non-function left of `>>` is a type error with a File.append hint
  (bash-append muscle memory).
- Interpolation `$"text {expr}"`; `{{ }}` escape braces. No `printfn`,
  no `sprintf`, no `%d` (the checker will suggest `print`).

```weir
type Verdict =
    | Pass of int
    | Fail

let double n = n * 2

let grade =
    let doubled = [1; 2; 3] |> Seq.map double |> Seq.sum
    if doubled > 10 then Pass doubled else Fail

let text =
    match grade with
    | Pass n when n > 100 -> "outstanding"
    | Pass n -> $"pass ({n})"
    | Fail -> "fail"

print text
print (if 1 == 1 then "eq" else "ne")
```

```weir-error
// non-exhaustive: missing false — hard error, not a warning
let x = match 1 == 1 with | true -> 1
print $"{x}"
```

```weir
// tuples landed 2026-07-21 (flipped from must-fail; extractor proved it)
let p = (1, 2)

match p with
| (a, b) -> print $"{a + b}"
```

```weir
// literal patterns landed 2026-07-21 (this block flipped from
// must-fail — the doc-test extractor proves the edit)
let v = match 1 with | 0 -> 0 | _ -> 1
let cleanup () = printerr "cleaning"
cleanup ()
print $"{v}"
```

```weir-error
// district lines are commands only — bind values outside the block
if 1 > 0 then !
    let x = 1
```

```weir
// raw strings: @ verbatim ("" = one quote) and triple-quoted (bare ")
let path = @"a\raw\path"
let quoted = """say "hi" ok"""
print path
print quoted
```

```weir
// the Regex pattern: raw literal, arity-typed binder
let ver = match "v2 ready" with | Regex @"v(\d+)" v -> v | _ -> "0"
print ver
```

```weir-error
// the Regex literal is raw-only — an ordinary string is rejected
let x = match "a" with | Regex "(a)" v -> v | _ -> ""
print x
```

```weir
// the exit-code idioms: succeeds is ExitCode == 0 EXACTLY — grep's
// no-match (1) is FALSE here; reach for | complete when codes are data
let found = grep -c NOPE_XYZ /etc/hostname | succeeds
let detail = grep -c NOPE_XYZ /etc/hostname | complete
print (if found then "?" else $"no match is false; code {detail.ExitCode}")
```

```weir
// copy-and-update: derive, don't re-literal
type Cfg = { Host: string; Port: int }
let base0 = { Host = "h"; Port = 80 }
let tls = { base0 with Port = 443 }
print $"{tls.Host}:{tls.Port}"
```

```weir-error
// update never ADDS fields
type Cfg2 = { Host: string }
let c = { Host = "h" }
let bad = { c with Hosts = "x" }
print bad.Host
```

```weir-error
// binder arity must equal the group count — a CHECK error, not a
// silent runtime non-match
let x = match "a" with | Regex @"(\d+)-(\d+)" a -> a | _ -> ""
print x
```

## Commands and processes

- Interactive TTY tools (fzf-class) work in command pipelines — they
  draw on /dev/tty while stdio pipes; a user cancel (exit 130) RAISES
  like any nonzero exit, aborting the script at the fault.
- Bareword heads run externals: `git status` works at a statement head.
  Builtins shadow PATH (`ls` is typed rows); `^ls` forces the external.
- Splice values into commands: `$name` for bindings, `(expr)` for
  expressions — always single argv entries, never re-split, no injection.
- No globs, no redirects, no `&&`, no `$VAR` env expansion — those
  characters pass through as literal argv (`echo a && b` prints
  "a && b"); `>`/`>>` additionally WARN with the File spelling
  (redirection is `cmd | File.write "out.txt"` / `File.append`).
  For bash semantics: `sh -c "the bash line"` (a command
  line; streams, completes, pipes like any command).
- Nonzero exit RAISES when the stream is forced. The exit-code
  reifiers (complete's family, single external segment, one rule):
  `cmd | succeeds` reifies to BOOL (never raises; output discarded —
  a predicate is silent); `cmd | orFail "msg"` raises `msg (exit N)`
  on nonzero and is unit on success — THE assert idiom, legal as a
  statement, in `!()`, and in districts. **`succeeds` is
  ExitCode == 0, exactly** — for tools whose nonzero codes are data
  (grep's no-match is 1), use `| complete` and match the code.
  Full inspection: `cmd | complete` gives `{ ExitCode; Stdout;
  Stderr }`; in expression positions use the `completed` builtin:
  `completed "prog" ["arg1"; "arg2"]`. `print ()` is silent (unit
  prints nothing — the rule that lets orFail sit in effect
  positions).
- Typed output: `git status --porcelain | from porcelain` gives rows
  with `Path`/`Staged`/`Unstaged`/`Status`; `... | from json T` needs
  `type T = { field: ty; ... }` declared first (exact field set).
- `!` runs commands: parens for one inline (`!(git pull)`), LINE-END
  `!` for a block below — indented bare command lines, one per line
  (no expressions, no `let`, no nested `!()` inside; leading `|`
  continues a pipeline):

  `if clean then !`
  + indented `git checkout main` / `git pull` lines.
- Command sigils work ANYWHERE in expressions: `$(git branch)` captures
  output (`seq<string>`, pipes onward); `!(git push)` runs-and-streams
  (unit, raises on nonzero). On a top-level `let` RHS prefer the bare
  chain (`let b = git branch | Seq.head`); sigils are for positions
  bare cannot reach. The block effect idiom:
  `if clean then` + indented `!(...)` lines. Interiors are ordinary
  command chains (splices, pipes, `| complete`). `!` is NOT bash
  history/extglob and `;` still does not chain inside them.
- A top-level `let` RHS takes command lines — param-ful included
  (`let f r = git rev-parse $r | Seq.head`): `let files = git ls-files`
  binds `seq<string>`; `let r = git status | complete` binds the
  record. Externals only — builtins stay functions there
  (`let w = cd target` applies the BINDING target). NOT in `let ... in`
  or in BLOCK lets inside bodies — there use
  `cmd "git" ["status"; "--porcelain"]` (prog + argv list) or `$()`. A bareword `in` on a let RHS ends the command
  grammar; quote `"in"` to pass it.
- Tuples: `(a, b)` literals, `int * string` types, `| (x, y) ->`
  patterns (arity 2+). `Seq.pairwise : seq<'a * 'a>`, `Seq.zip`.
  Destructure ANYWHERE irrefutable: `let x, y = pair`,
  `let (k, _) = pair`, `fun (k, v) -> ...` (parens required on
  params). Refutable patterns in binders are errors — use match.
  Bare `a, b` is a tuple at F#'s precedence (`f x, y` is `(f x), y`).
  `fst`/`snd` project PAIRS (wider tuples are a type error, as F#).
- Paths: `Path.extension` (keeps the dot; `""` when none),
  `Path.fileName`, `Path.stem`, `Path.dir` (`""` at the top),
  `Path.combine dir name` — System.IO semantics.
- Iterate with `weir check file.weir` (all errors, located, coded) or
  `weir check --json file.weir` (structured: file/line/col/code/
  severity) BEFORE running — no evaluation happens, by construction.
  check WARNS on commands missing from PATH (cmd-not-found, exit 0)
  where run ERRORS — scripts for uninstalled tools stay editable.
  Stranded-script reports should cite the error codes.
- Casing law: binding names start LOWERCASE (`let foo`, `fun x ->`);
  uppercase is types/modules/constructors. Record fields keep their
  names: `let region = cfg.AWS_REGION`.
  No tuple ordering (sortBy/sortByDescending keys stay scalar).
  Records remain the spelling for anything with NAMES. Prefix minus
  follows F#'s adjacency rule: `-n`, `2 * -3`, and `f -1` (passes -1
  as the ARGUMENT) are prefix; `x-1` and `x - 1` are subtraction.
- Element access: `xs[0]` (raises; = `Seq.item 0 xs`; F# 6 whitespace
  rule — `f [0]` WITH a space is applying a list) / `Seq.tryItem`
  (Option) / `Seq.skip`; `_[0]` is shorthand for `fun x -> x[0]`.
  Membership: `Seq.contains x xs` (equatable elements),
  `Seq.exists`/`Seq.forall` with predicates.
- Argv: `Args.load T` (script-only) — the typed front door. Two
  shapes: a RECORD of flags (`bool` = presence; `string`/`int` =
  required valued; `Option<string|int>` = optional valued;
  `Option<bool>` rejected — presence is already optional), or a
  UNION of record-payload cases = subcommands (first token matches
  the lowercased case name; bare cases take no flags). Field names
  derive kebab flags (`dryRun` → `--dry-run`) and unambiguous
  first-letter shorts (contested letters derive for nobody;
  `[<Short "C">]` overrides, `[<NoShort>]` suppresses, `-h` is
  help). STRICT: unknown flags (did-you-mean), unexpected
  arguments, repeats, missing requireds — all collected into ONE
  boundary error. `--help` prints derived usage (short truth +
  `[<Doc>]` text), exit 0, even on invalid invocations. No
  positionals — spell operands as flags. The untyped floor:
  `Args.flag "--clean" "-c"` (bool), `Args.value "--out"`
  (Option of the next token).

```weir
type Cli = { [<Short "C"; Doc "clean first">] clean: bool; port: Option<int> }
let cli = Args.load Cli
print $"{show cli.clean} {show cli.port}"
```

```weir-error
type Cli = { env: string }
let cli = Args.load Cli // no argv: "missing required flag '--env'" — collected, strict
print cli.env
```

```weir-error
type Cmd = Go of string | Stop // payload must be a record: check error
let c = Args.load Cmd
```

- Environment: `Env.get "NAME"` (Option<string>) for one var;
  `Env.load Config` for typed config — declare
  `type Config = { PORT: int; DEBUG: bool; TOKEN: Option<string> }`
  (field names = env-var names VERBATIM; scalars + Option only; bool
  is exactly true/false), load once, typed thereafter; every missing/
  garbage field reported in ONE error. `Env.vars` lists everything.
  No `$NAME` expansion in commands — interpolate: `-H $"token {key}"`.
- `//` mid-token is NOT a comment: bareword URLs (`https://...`) pass
  through; comments need line start or a preceding space.
- `run "git" ["push"]` runs a program from expression positions:
  streams like a command line, raises on nonzero, returns unit.
- `runEnv vars "az" [...]` / `cmdEnv vars ...` inject child-env
  (overlay: set those names, inherit the rest; parent untouched).
  `Env.fromFile "x.env"` loads the dotenv SUBSET (KEY=VALUE, optional
  quotes, # comments — NO export/$VAR; those lines error, naming the
  `sh -c "set -a; . file; ..."` escape). House idiom: partially apply
  — `let az = runEnv (Env.fromFile p) "az"` then `az [...]`. In
  sigils: `$e(...)`/`!e(...)` (ident GLUED to glyph and paren) injects
  into every spawn in the chain. Line-end `!name` = env district
  (distributes over the block); a literal `!word` as a final command
  arg must be quoted. Bare `!(...)`/`!` districts and command lines
  stay env-less.
- Multi-line record literals separate fields by newline, F#-style
  (trailing `;` also fine); a blank line inside an open `{` is an
  error. Braces ignore indentation.
- Blocks are offside, F#-style: a line at an `if`/`match`'s OWN indent
  is a sibling (runs after, unconditionally); only deeper lines are
  the body. Guard lines before a block result work:
  `if x == "" then fail "usage"` then the result line at same indent.
- `exit n` exits with code n silently (propagation:
  `if r.ExitCode <> 0 then exit (r.ExitCode)`); `fail "msg"` is
  the message-carrying exit-1. No try/finally — for cleanup-always,
  reify with `| complete`, clean up, then propagate.
- Blank lines END statements — never leave one inside an indented
  block (the error will say so).

```weir
let files = git ls-files
print $"tracked: {files |> Seq.length}"

let deltas =
    [10; 13; 11]
    |> Seq.pairwise
    |> Seq.map (fun p -> match p with | (a, b) -> $"{b - a}")

deltas |> print

let marker = "skill-doc"
echo tagged $marker (40 + 2)
sh -c "echo via-posix && echo second"

let r = completed "sh" ["-c"; "exit 3"]
print $"exit was {r.ExitCode}"

git status --porcelain | from porcelain | Seq.map _.Path
```

## Diagnostics and exiting

- `fail "reason"` raises: the script stops with a located error and
  exit 1. `if bad then fail $"broken: {n}"` is the checking-script
  idiom.
- `printerr` is `print` to stderr (same argument rule) — diagnostics
  there, data on stdout, so `weir script | next` stays clean.
- Operators bind tighter than `|>`: `xs |> Seq.length == 2` is a
  targeted check error; write `(xs |> Seq.length) == 2`.

```weir
printerr "starting up"
print (if ([1; 2] |> Seq.length) == 2 then "ok" else "wrong")
```

```weir
type HomeCfg = { HOME: string; WEIR_ABSENT_ZZ: Option<string> }

let cfg = Env.load HomeCfg
print $"home={cfg.HOME} extra={show cfg.WEIR_ABSENT_ZZ}"
```

```weir-error
// fail halts with exit 1 before this script's later lines matter
fail "deliberate"
print "unreached"
```

## Errors and the fallback protocol

- Read the error: spans are `file:line:col`; hints name the fix
  ("Did you mean ...", module homes, `add an else`).
- If weir structurally cannot do the task (interactive tools, missing
  feature), write bash — and append one line to NOTES-agent.md:
  date | task shape | the gap that forced bash.
- NEVER invent weir syntax. If unsure a feature exists, check this
  file; if absent here, assume absent, fall back, log. The full
  rejected-vs-pending border with F# is tests/fidelity/divergences.md.
