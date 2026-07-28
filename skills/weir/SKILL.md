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
- Comments are `//`. A `///` line is a DOC comment: it attaches to the
  declaration immediately below it (a blank line breaks the link) and
  shows on hover and in completion — at let bindings, `type` decls,
  record fields, and union cases; a doc must align with what it
  describes. On an `Args.load` field the doc's FIRST line is also its
  `--help` text (hover shows the whole doc) — one source, so help and
  hover can't drift. Statements end at column 0 (the next col-0
  line) — blank lines and comment lines are both transparent inside
  a statement, so blocks group freely with gaps.
- Scripts are STRICT: every library name is module-qualified —
  `Seq.map`, `Str.trim`, `Option.defaultValue`, `File.read` — including in
  command pipelines (`| Seq.map Str.trim`). Bare names (`map`, `where`)
  exist only in the REPL and `#loose` scripts. If unsure, qualify.
- The `Self` module groups a script's own facts, script-only (absent
  in the REPL and `-e`): `Self.args : seq<string>`,
  `Self.stdin : seq<string>`, `Self.pid : int` (the process id), and
  `Self.scriptPath : string` (the script's own ABSOLUTE path, resolved
  at startup before any `cd`; symlinks unresolved like bash's `$0`).
  `Self.scriptPath |> Path.dir` is the dirname-$0 idiom.

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
  seqs truncate); `print` remains the raw data channel. The REPL's
  echo is tighter still (10 elements, clipped strings, a hint naming
  the way out): echo = glance, `|> print` = read.
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
  continues the same element; blank lines are transparent here as
  everywhere inside a statement). House style is Stroustrup: opener dangles at the
  head line's end, entries one level in, closer alone at the head's
  indent — `type Cli = {` / four-space fields / `}`. The aligned
  style (`{ x` with column-aligned fields) stays accepted.
- Record fields take attributes, F#'s syntax: `[<Short "c">]`,
  `[<NoShort>]`, `[<Default v>]` — `;`-separated
  lists, literal args only (string/int/bool). Attributes are
  check-time data, fully erased: an attributed record is the same
  type as a bare one. The name set is CLOSED — an unregistered name
  (`[<Positional>]` among them: dropped, scripts take flags) is a
  check error with a did-you-mean. Attributes attach to record
  fields only.

```weir
type Cli = {
    [<Short "C">]
    /// clean first
    Clean: bool
    Target: string
}
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
  statement boundary. Splices are WHOLE argv entries — a mid-word
  splice like `--file=$f` is a hard error (the prefix can't glue to
  the value); spell `--file $f` or an interp arg `$"--file={f}"`.
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
  `Str.rmatchAll pat s : seq<seq<string>>` is the plural — EVERY
  match's groups, lazily; no Option (absence is the empty seq).
  `(?s)`/`(?m)` inline flags cover DOTALL/MULTILINE. The scrape idiom
  is one pipeline: `Str.rmatchAll pat text |> Seq.map Seq.head |>
  Seq.distinct` (all matches → contents → dedup); pipe a match through
  a tool with `| sha256sum`.
- Params are plain idents OR `()` (a unit param: `let cleanup () =`;
  `cleanup 5` is a type error). Other pattern params stay rejected.
- No async/task/await — processes and pipelines are the concurrency
  model. A task that truly needs async belongs in full F#, not weir.
  For fan-out over items: `xs |> Seq.pmap (fun x -> ...)` (parallel,
  ordered results) / `Seq.piter` for effects. Workers fork the session:
  `cd` inside a worker is worker-local and gone at the join — force
  worker output inside the worker (`Seq.head`/`Seq.force`) if its cd
  matters.
- A `let` RHS takes command mode wherever lets go — top level AND
  inside bodies (`let tree = git rev-parse $c | Seq.head` in a
  function); `$()` covers sub-expression positions. `function` is
  reserved (write `fun x -> match x with`).
- No `let rec`, no loops, no mutation. Iteration is pipelines over seqs;
  `[1..10] |> Seq.iter (fun i -> print $"{i}")` for counted repetition.
  Ranges are lazy; `[a; b; c]` lists are eager. Running totals are
  `Seq.fold`: `xs |> Seq.fold (fun state x -> ...) init` — STATE FIRST
  in the folder (bash/JS `reduce` priors put it second), STRICT
  (consumes the source; not for infinite seqs), and the piped
  spelling anchors the folder's types (prefer it). Multi-accumulator
  loops fold over a record: `Seq.fold (fun c x -> { c with ... }) c0`.
- Seq patterns, F#'s spelling on seqs: `[]`, `x :: rest` (right-assoc
  chains), `[a; b]` fixed arity; element positions nest full
  patterns. `[]` + an irrefutable cons is a COMPLETE match; fixed
  arity never completes alone (add `_`). The match probes a bounded
  prefix ONCE (memoized): effects run once total, `rest` continues
  the same enumeration, infinite seqs are safe. Refutable → match
  only, never `let`. Param scrutinees need a resolved seq type
  (pipe through a Seq op first — the ctor-pattern rule).

```weir
let first =
    match $(printf 'a\nb\nc') with
    | [] -> "none"
    | x :: rest -> $"{x}+{rest |> Seq.length}"

print first
```
- `Seq.force` materializes (consume to completion, eager in-memory;
  STRICT — not for infinite seqs). When to force, four customers:
  REUSE (a command-backed seq re-runs its process per enumeration —
  force once, consume twice); TIMING (a lazy `ls` enumerated after a
  `cd` sees the new directory; force pins the data NOW); GLOB's cd
  seam (`Path.glob` resolves relative patterns at ENUMERATION —
  force pins the batch before a `cd`); and the non-customer: a
  SPLAT (`$@xs`) forces at spawn by necessity — argv is finite, no
  explicit force needed at the splice.
- Concatenation is `Seq.append` (lazy; piped spelling puts the TAIL
  in the pipe: `tail |> Seq.append head`).
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
- A `(fun ... ->` dangling at line end opens a BODY BLOCK closed by
  its own `)`: ordinary block rules inside (block lets, siblings,
  compounds, districts, blanks), body lines at or right of the
  opener's indent, the `)` attached to the last body line or alone on
  its own line. The single-line `;`-joined spelling stays legal.

```weir
["r1"; "r2"]
    |> Seq.iter (fun r ->
        let tag = $"repo-{r}"
        !(echo fetching $tag)
        print $"done {r}")
```
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
// the exit-code idioms: succeeds is exitCode == 0 EXACTLY — grep's
// no-match (1) is FALSE here; reach for | complete when codes are data
let found = grep -c NOPE_XYZ /etc/hostname | succeeds
let detail = grep -c NOPE_XYZ /etc/hostname | complete
print (if found then "?" else $"no match is false; code {detail.exitCode}")
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
- Splice values into commands: `$x` is ONE word; `$@xs` (and
  `$@(expr)`) is N words — the argv splat, each `seq<string>` element
  one word, never re-split, never re-joined (no injection either
  way). `$@xs` is to `$x` what `yield!` is to `yield`. An empty seq
  contributes ZERO words (`git fetch $@qf origin` with `qf = []`
  drops nothing) — the typed replacement for bash's conditional-flag
  idiom. `$@` demands `seq<string>` exactly (a scalar or `seq<int>`
  is a check error naming the fix); it cannot head a command
  (computed heads park) or join a word mid-construction
  (`--flag=$@xs` — map the prefix on, or separate args).
- No glob EXPANSION (`Path.glob` is the typed spelling — a
  function, not argv magic), no redirects, no `&&`, no `$VAR` env
  expansion — those
  characters pass through as literal argv (`echo a && b` prints
  "a && b"); `>`/`>>` additionally WARN with the File spelling
  (redirection is `cmd | File.write "out.txt"` / `File.append`).
  For bash semantics: `sh -c "the bash line"` (a command
  line; streams, completes, pipes like any command).
- Nonzero exit RAISES when the stream is forced. The exit-code
  reifiers (complete's family, single external segment, one law:
  output goes where the meaning goes): `cmd | succeeds` reifies to
  BOOL (silent — a predicate's output IS its result); `cmd | orFail
  "msg"` STREAMS and raises `msg (exit N)` on nonzero, unit on
  success — THE assert idiom, legal as a statement, in `!()`, and in
  districts; `cmd | exitCode` STREAMS and reifies the code as INT,
  never raises — bind it or match it (`| 130 ->` for cancels); a
  bare/`!()`/`$()` position is a teaching error ($() captures — use
  `| complete` there). **`succeeds` is exitCode == 0, exactly** —
  for tools whose nonzero codes AND output are both data (grep,
  fzf), use `| complete` and read the record.
  Full inspection: `cmd | complete` gives `{ exitCode; stdout;
  stderr }`; a COMPUTED argv splats into the chain —
  `$author(git commit-tree $@argv | complete) |> _.stdout` (literal
  head, splatted argv, sigil env; works with all four reifiers,
  value-headed and districts too). `print ()` is silent (unit
  prints nothing — the rule that lets orFail sit in effect
  positions).
- Capture is IN MEMORY: `| complete` holds the whole output as one
  byte buffer + line offsets (~2x the text in RSS; lines decode
  per pull). Unbounded output is still unbounded — for gigabyte or
  endless children STREAM it (`|> Seq.iter`, `| File.write`) instead
  of capturing; the ceiling is the box, and a single capture caps at
  ~2GB. `Seq.force` on decoded lines re-pays string overhead — force
  what you need, not the world.
- Typed output: `git status --porcelain | from porcelain` gives rows
  with `path`/`staged`/`unstaged`/`status`; `... | from json T` needs
  `type T = { field: ty; ... }` declared first (exact field set).
- `!` runs commands: parens for one inline (`!(git pull)`), LINE-END
  `!` for a block below — indented bare command lines, one per line
  (no expressions, no `let`, no nested `!()` inside; leading `|`
  continues a pipeline):

  `if clean then !`
  + indented `git checkout main` / `git pull` lines.
- The glyph law: weir has no `!`-negation — negation is the word
  `not`; `!` means DO IT. And no `\`-escape for commands — `^ls`
  forces the PATH binary.

```weir
let clean = not (1 == 2)
if clean then !(sh -c "echo acting")
```

```weir-error
// no \-escape for commands; ^ls is the force spelling
\ls
```

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
  (`let w = cd target` applies the BINDING target). BLOCK lets inside
  bodies (and lambda bodies) take the same command RHS along a
  top-level let's spine; the single-line `let ... in` spelling stays
  expression-only — there use `$(git status)`. A
  bareword `in` on a let RHS ends the command grammar; quote `"in"`
  to pass it.
- Tuples: `(a, b)` literals, `int * string` types, `| (x, y) ->`
  patterns (arity 2+). `Seq.pairwise : seq<'a * 'a>`, `Seq.zip`.
  Destructure ANYWHERE irrefutable: `let x, y = pair`,
  `let (k, _) = pair`, `fun (k, v) -> ...` (parens required on
  params). Refutable patterns in binders are errors — use match.
  Bare `a, b` is a tuple at F#'s precedence (`f x, y` is `(f x), y`).
  `fst`/`snd` project PAIRS (wider tuples are a type error, as F#).
- Paths: `Path.extension` (keeps the dot; `""` when none),
  `Path.fileName`, `Path.stem`, `Path.dir` (`""` at the top),
  `Path.combine dir name` — System.IO semantics, with the two BCL
  gotchas that come with them: an ABSOLUTE second arg WINS
  (`Path.combine "/safe" "/etc/x"` = `/etc/x`, not nested), and `..`
  is NOT normalized. Path functions don't confine — building a path
  from hostile data can escape a directory you imagined as a bound;
  that check is the script's. And `File.*`/explicit paths FOLLOW
  symlinked dirs that `Path.glob`'s `**` deliberately skips — each is
  correct in isolation (glob skips for loop-immunity, explicit access
  follows as a shell does), but they are not the same rule.
- `Path.glob "src/**/*.fs" : seq<string>` — typed discovery
  (nothing expands in argv, ever): `*` within-segment, `**`
  cross-segment (never through symlinked dirs — bash globstar),
  `?`, `[abc]`/`[!abc]`. Bash's dotfile law: `*` skips dotfiles, a
  `.`-leading segment matches them. Sorted; relative patterns
  echo relative and resolve against the cwd AT ENUMERATION —
  `|> Seq.force` pins the answer before a `cd`. No matches = the
  empty seq (`match ... with | [] -> fail "no matches"`).
  Unreadable dirs skip (discovery, not assertion).
- Editor mode-coloring (LSP semantic tokens) is for humans — agents
  read `weir check`; colors carry no information the checker does
  not already report.
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
  `Seq.exists`/`Seq.forall` with predicates. Dedupe is
  `Seq.distinct` (lazy, first occurrence wins, equatable elements —
  functions/seqs rejected at the use site).
- Argv: `Args.load T` (script-only) — the typed front door. Three
  shapes: a RECORD of flags (`bool` = presence; `string`/`int` =
  required valued; `Option<string|int>` = optional valued;
  `Option<bool>` rejected — presence is already optional); a
  UNION of record-payload cases = subcommands (first token matches
  the lowercased case name; bare cases take no flags); or a record
  CONTAINING exactly one union-typed field — shared flags by
  containment: the scalar siblings are global flags recognized
  ANYWHERE on the line, the union field is the subcommand slot (its
  name derives no flag), payload flags bind only after the case
  token. A flag name declared in both tiers is a check error
  (shared flags are declared once); a short contested across tiers
  derives for neither in that case's scope. `--help` prints the
  two-tier usage; `tool <case> --help` scopes it. Field names
  derive kebab flags (`dryRun` → `--dry-run`) and unambiguous
  first-letter shorts (contested letters derive for nobody;
  `[<Short "C">]` overrides, `[<NoShort>]` suppresses, `-h` is
  help). `[<Default v>]` moves the resting point: the field stays
  non-Option, an absent flag fills the literal, help shows it;
  `[<Default true>]` on bool mints the `--no-x` twin (`--no-*`
  never derives a short). COMPUTED defaults keep Option + code —
  literals only in the attribute. Default on Option/false-on-bool/
  the subcommand slot are check errors. STRICT: unknown flags (did-you-mean), unexpected
  arguments, repeats, missing requireds — all collected into ONE
  boundary error. `--help` prints derived usage (short truth +
  the `///` doc's first line), exit 0, even on invalid invocations. No
  positionals — spell operands as flags. The untyped floor:
  `Args.flag "--clean" "-c"` (bool), `Args.value "--out"`
  (Option of the next token).

```weir
type Cli = {
    [<Short "C">]
    /// clean first
    clean: bool
    port: Option<int>
}
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
  (field names = env-var names VERBATIM; scalars, 0-arity-case enum
  unions, + Option of these; bool is exactly true/false), load once,
  typed thereafter; every missing/garbage field reported in ONE
  error. An ENUM field (`type Lvl = Debug | Info` + `LOG_LEVEL:
  Lvl`) converts the var like int/bool does — matching is
  CASE-INSENSITIVE (`=DEBUG`, `=debug`, `=Debug` all select
  `Debug`; the CLI subcommand rule stays lowercase-verbatim — two
  conventions, two rules), and a miss errors with the full case
  list + a hint. Payload-carrying cases reject at check time; an
  enum's resting point spells `Option<Lvl>` + `Option.defaultValue`
  (Default takes literals only). `[<Default v>]` fills an
  ABSENT var (the field stays non-Option; any set var wins — the
  resting point sits below the whole overlay stack). No twin mints
  here: env bools are text, so `[<Default false>]` is legal
  ("absent → false" is a real statement, unlike argv presence). `Env.vars` lists everything.
  No `$NAME` expansion in commands — interpolate: `-H $"token {key}"`.
- `//` mid-token is NOT a comment: bareword URLs (`https://...`) pass
  through; comments need line start or a preceding space.
- Every command head is a LITERAL program name, resolved at check
  time — there is no computed-head tier. Command lines run from
  STATEMENT position; for a program in EXPRESSION position, capture
  with `$(git status)` or a reifier (`| complete` etc.). To swap tools
  by a runtime condition, branch the whole command line — `if hot then
  rg pat else grep pat`.
- `xs | prog args` [D:value-headed-pipe] pipes a weir seq into an
  external command's STDIN (data-last; stdout streams back as
  `seq<string>`; input pulls lazily, stdin closes at exhaustion):
  `snips | sha256sum`. Resolution decides: an EXTERNAL head after `|`
  is the pipe; a binding/library head keeps the `|`-chains-commands
  teaching (spell `|>`). LHS must be `seq<string>`. Reifiers compose
  on the tail — `files | grep -c foo | complete` reifies the (single
  external) segment WITH the value as stdin (`| succeeds`/`| exitCode`/
  `| orFail` too); a MULTI-external chain still needs one segment.
- Env sigils `$e(...)`/`!e(...)` (ident GLUED to glyph and paren)
  inject child-env into every spawn in the chain (overlay: set those
  names, inherit the rest; parent untouched). `Env.fromFile "x.env"`
  loads the dotenv SUBSET (KEY=VALUE, optional quotes, # comments — NO
  export/$VAR; those lines error, naming the `sh -c "set -a; . file;
  ..."` escape). Bind once, glue to the sigil: `let e = Env.fromFile p`
  then `!e(az ...)`. Line-end `!name` = env district
  (distributes over the block); a literal `!word` as a final command
  arg must be quoted. Bare `!(...)`/`!` districts and command lines
  stay env-less.
- Multi-line record literals separate fields by newline, F#-style
  (trailing `;` also fine); blank lines inside brackets are
  transparent. Braces ignore indentation.
- Blocks are offside, F#-style: a line at an `if`/`match`'s OWN indent
  is a sibling (runs after, unconditionally); only deeper lines are
  the body. Guard lines before a block result work:
  `if x == "" then fail "usage"` then the result line at same indent.
- `exit n` exits with code n silently (propagation:
  `if r.exitCode <> 0 then exit (r.exitCode)`); `fail "msg"` is
  the message-carrying exit-1. No try/finally — for cleanup-always,
  reify with `| complete`, clean up, then propagate.
- Blank lines are TRANSPARENT while a statement is open — bodies,
  arms, brackets, districts group freely with gaps. A statement ends
  at the next column-0 line (or EOF), nowhere else.

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

let r = sh -c "exit 3" | complete
print $"exit was {r.exitCode}"

git status --porcelain | from porcelain | Seq.map _.path
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
