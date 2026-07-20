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
// a discarded value is a check error, before anything runs
"this string goes nowhere"
```

## Syntax that differs from your priors

- Equality is `==` (never `=`). `=` is for `let` and record fields only.
- No tuples — wrap in records: `{ Fst = a; Snd = b }` needs a declared
  `type Pair = { Fst: int; Snd: int }` (exact field set, no width subtyping).
- Union cases take ONE payload: `Case of int`, never `Case of int * int`.
- `let f x y = ...` defines a curried function (desugars to nested
  `fun`). Params are plain idents — no `()`, no patterns, no type
  annotations — and a param-ful let cannot take a command-line RHS.
- `+` on two unknown params cannot infer (int-or-string): anchor one
  side (`x + 0`) or take data in. All single-typing operators
  (`- * / > <`) default to int; `let rec` and `mutable` are reserved
  words with no meaning.
- No literal patterns: `match n with | 0 -> ...` does not parse; use a
  guard (`| x when x == 0 -> ...`) or bool match.
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

```weir-error
// tuples do not exist
let p = (1, 2)
print "unreached"
```

```weir-error
// literal patterns do not exist; use a when-guard
let v = match 1 with | 0 -> 0 | _ -> 1
print "unreached"
```

## Commands and processes

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
- A top-level `let` RHS takes command lines: `let files = git ls-files`
  binds `seq<string>`; `let r = git status | complete` binds the
  record. Externals only — builtins stay functions there
  (`let w = cd target` applies the BINDING target). NOT in `let ... in`
  or inside expressions — there use `cmd "git" ["status"; "--porcelain"]`
  (prog + argv list). A bareword `in` on a let RHS ends the command
  grammar; quote `"in"` to pass it.
- `Seq.pairwise` gives adjacent pairs as `{ Fst; Snd }` records:
  `xs |> Seq.pairwise |> Seq.map (fun p -> p.Snd - p.Fst)`.
- Blank lines END statements — never leave one inside an indented
  block (the error will say so).

```weir
let files = git ls-files
print $"tracked: {files |> Seq.length}"

let deltas =
    [10; 13; 11]
    |> Seq.pairwise
    |> Seq.map (fun p -> $"{p.Snd - p.Fst}")

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
