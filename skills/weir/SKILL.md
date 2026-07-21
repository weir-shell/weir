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
  subtyping, no anonymous records): `{ Fst = a; Snd = b }` needs
  `type Pair = { Fst: int; Snd: int }`.
- Union cases take ONE payload: `Case of int`, never `Case of int * int`.
- `let f x y = ...` defines a curried function (desugars to nested
  `fun`). Params are plain idents — no `()`, no patterns, no type
  annotations — and a param-ful let cannot take a command-line RHS.
- `+` on two unknown params cannot infer (int-or-string): anchor one
  side (`x + 0`) or take data in. All single-typing operators
  (`- * / > <`) default to int; `let rec` and `mutable` are reserved
  words with no meaning.
- Literal patterns work (`| 0 ->`, `| "yes" ->`, `| () ->`, nested in
  constructors) but int/string literals NEVER complete a match alone —
  add a `_`/var arm or it is a hard error. Guards remain legal.
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
  Ranges are lazy; `[a; b; c]` lists are eager.
- `if c then a else b` is an expression; `else` is mandatory unless the
  then-branch is unit (`if ok then print "yes"` is a valid statement).
- `match` supports `| true ->` / `| false ->`, `when` guards, and
  constructor patterns. **A non-exhaustive match is a hard error**, and
  guarded arms don't count as coverage — always include an unguarded
  catch-all or cover every case.
- `let x = e in body` inline; in multi-line scripts an indented `let`
  line closes at the next line of the same indent (F# light syntax).
- String/seq ops are data-last for piping: `Seq.where (Str.contains "err")`.
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
  "a && b"). For bash semantics: `sh -c "the bash line"` (a command
  line; streams, completes, pipes like any command).
- Nonzero exit RAISES when the stream is forced. To inspect instead:
  `somecmd args | complete` (command mode, single external segment)
  gives `{ ExitCode; Stdout; Stderr }`; in expression positions use the
  `completed` builtin: `completed "prog" ["arg1"; "arg2"]`.
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
- A top-level `let` RHS takes command lines: `let files = git ls-files`
  binds `seq<string>`; `let r = git status | complete` binds the
  record. Externals only — builtins stay functions there
  (`let w = cd target` applies the BINDING target). NOT in `let ... in`
  or inside expressions — there use `cmd "git" ["status"; "--porcelain"]`
  (prog + argv list). A bareword `in` on a let RHS ends the command
  grammar; quote `"in"` to pass it.
- Tuples: `(a, b)` literals, `int * string` types, `| (x, y) ->`
  patterns (arity 2+). `Seq.pairwise : seq<'a * 'a>`, `Seq.zip`.
  Destructure ANYWHERE irrefutable: `let x, y = pair`,
  `let (k, _) = pair`, `fun (k, v) -> ...` (parens required on
  params). Refutable patterns in binders are errors — use match.
  Bare `a, b` is a tuple at F#'s precedence (`f x, y` is `(f x), y`).
- Iterate with `weir check file.weir` (all errors, located, coded) or
  `weir check --json file.weir` (structured: file/line/col/code/
  severity) BEFORE running — no evaluation happens, by construction.
  Stranded-script reports should cite the error codes.
- Casing law: binding names start LOWERCASE (`let foo`, `fun x ->`);
  uppercase is types/modules/constructors. Record fields keep their
  names: `let region = cfg.AWS_REGION`.
  No tuple ordering (sortBy keys stay scalar). Records remain the
  spelling for anything with NAMES.
- Element access: `xs[0]` (raises; = `Seq.item 0 xs`; F# 6 whitespace
  rule — `f [0]` WITH a space is applying a list) / `Seq.tryItem`
  (Option) / `Seq.skip`; `_[0]` is shorthand for `fun x -> x[0]`.
  Membership: `Seq.contains x xs` (equatable elements),
  `Seq.exists`/`Seq.forall` with predicates.
- Flags: `Args.flag "--clean" "-c"` (bool; `""` short form if none),
  `Args.value "--out"` (Option of the next token). Script-only.
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
- `Exit.code n` exits with code n silently (propagation:
  `if r.ExitCode <> 0 then Exit.code (r.ExitCode)`); `fail "msg"` is
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
