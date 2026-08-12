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
- Comments are `//`, full-line or TRAILING (`let x = 5 // note`) —
  a comment needs line start or preceding whitespace, so glued `//`
  in command argv (`http://a`, `--format=a//b`) stays data; a bare
  unquoted `//` argv word reads as a comment (quote it: `"//"`).
  `//` inside any string form is data; `$"{1 // 2}"` (a comment
  inside a hole) is a parse error — write it outside the string. A `///` line is a DOC comment: it attaches to the
  declaration immediately below it (a blank line breaks the link; an
  attribute line is transparent — `///` above or below `[<...>]` both
  attach to the field) and
  shows on hover and in completion — at let bindings, `type` decls,
  record fields, and union cases; a doc must align with what it
  describes. On an `Args.load` field the doc's FIRST line is also its
  `--help` text (hover shows the whole doc) — one source, so help and
  hover can't drift. Statements end at column 0 (the next col-0
  line) — blank lines and comment lines are both transparent inside
  a statement, so blocks group freely with gaps.
- Scripts are STRICT: every module member is qualified —
  `Seq.map`, `Str.trim`, `Option.defaultValue`, `File.read` — including in
  command pipelines (`|> Seq.map Str.trim`). Bare names (`map`,
  `where`, `sortBy`) exist only in the REPL and `#loose` scripts, and
  the rule is DERIVED [D:bare-partition]: every single-home member of
  `Seq`/`Str` is bare; a name with two homes is qualified-only on
  BOTH sides — `contains` and `length`, the whole list. If unsure,
  qualify: the qualified spelling always works.
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
  unions, options: AN INTERPOLATION HOLE renders any Show value —
  `print $"{row}"` (only functions reject; a bare string hole stays
  raw/unquoted). `show x` produces the same text as a plain string;
  its niche is the places a hole cannot go — point-free positions
  (`Seq.map show`), Secrets (`show` masks where interpolation
  refuses), and a ROW-TYPED field in a hole (`$"{show c.port}"` keeps
  port polymorphic; a bare hole defaults an unresolved type to
  string). Command-argument splices stay string/int/bool. Lossy debug format (strings come quoted, long
  seqs truncate); `print` remains the raw data channel. The REPL's
  echo is tighter still (10 elements, clipped strings, a hint naming
  the way out): echo = glance, `|> print` = read.
- `xs |> Seq.map print` does nothing (lazy) and is a check error as a
  statement; use `xs |> Seq.iter print`.

```weir
let files = git ls-files
print $"tracked: {files |> Seq.length}"
["a"; "b"] |> Seq.iter print
git ls-files |> Seq.first 1
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
  (`let revParse r = git rev-parse $r |> Seq.head`): params shadow
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
  (non-capturing `(?:...)` does not count). NAMED groups `(?<x>...)`
  REJECT: weir names captures at the BINDER, not in the pattern
  (lookbehind `(?<=`/`(?<!` is fine). Groups bind as STRINGS;
  convert in the arm. Regex arms never complete a match. Computed
  hashing/encoding: `Str.sha256` (lowercase hex of UTF-8 bytes),
  `Str.toBase64` (one unwrapped line), `Str.fromBase64` /
  `Str.tryFromBase64` (raise/None on malformed AND on non-text bytes).
  patterns live on the expression side: `Str.isMatch pat s` (bool),
  `Str.rmatch pat s` (Option<seq<string>>) — any string, and raw
  literals read best: `Seq.where (Str.isMatch @"\.md$")`.
  `Str.rmatchAll pat s : seq<seq<string>>` is the plural — EVERY
  match's groups, lazily; no Option (absence is the empty seq).
  `(?s)`/`(?m)` inline flags cover DOTALL/MULTILINE. The scrape idiom
  is one pipeline: `Str.rmatchAll pat text |> Seq.map Seq.head |>
  Seq.distinct` (all matches → contents → dedup); pipe a match through
  a tool with `| sha256sum`.
- Floats exist and are FINITE-only: `0.5`, `1e5`, `1.5e-3` (digits
  required on both sides of a point — `1.` and `.5` are errors).
  A non-finite result RAISES (`1.0 / 0.0`, overflow); NaN/Infinity
  cannot exist. No implicit widening: `3 / 2` is int division `1`,
  `3 / 2.0` is a type error — wrap with `Float.ofInt`. Floats do
  NOT join `==`/`Seq.distinct`/`Seq.contains` (check error naming
  `Float.near a b eps`); they DO sort (`Seq.sortBy`), print, and
  interpolate. `show 1.0` = `"1.0"` (integral floats keep the
  decimal); `Float.parse`/`tryParse` read what show writes.
  Module: `Float.ofInt/toInt/round/abs/near/parse/tryParse`
  (qualified only). `Duration.toSeconds` gives float seconds losslessly.
  Float fields work at every boundary: json (integer-shaped numbers
  widen — a wire format with one number type; decimals into int
  fields error naming float), yaml (unquoted `1.5` is the number,
  quoted `"1.5"` the string; `.nan`/`.inf` reject — finite-only),
  Args/Env (`--rate 0.5`, `[<Default 0.5>]`).
- Bytes are `Size` (integer bytes inside): literals `512B`/`1KiB`/
  `2MiB`/`1GiB`/`1TiB` — binary units ONLY (`1MB` is a teaching
  error: SI is ambiguous in the wild; `Size.parse "1MB"` reads it as
  10^6 — parse reads foreign text). `show` = binary units, one
  truncated decimal (`1.5 MiB`), bytes plain (`847 B`);
  `Size.toBytes` is the exact exit. `+`/`-` between sizes, `*`/`/`
  by int; `Size / Size` errors naming both alternatives.
  `File.size p : Size` — compare directly (`File.size p > 10MiB`).
  Args/Env fields parse (`--max 1.5GiB`, `[<Default 10MiB>]`);
  json/yaml REJECT (convert via `Size.toBytes`). No `print` (holes
  render: `$"{sz}"`).
- Time is `Duration` (integer ms inside): literals `500ms`/`30s`/`2m`/`1h`
  (single-unit, expression position; in command position `30s` is an
  ordinary argv word). `+`/`-` between durations, `*`/`/` by int;
  `Duration / Duration` is a check error naming `Duration.toMillis`.
  `show 90500ms` = `"1m30.5s"`; `Duration.parse`/`tryParse` read that
  shape (`"1h30m"`, `"2.5s"` — decimals exist only in TEXT; `2.5s` as
  a literal is a teaching error, so is `2d` and compound `1m30s`).
  `Duration.ms/s/m/h` construct; `Duration.sleep 500ms` blocks (bare
  `sleep` stays the coreutils command). `Args.load`/`Env.load` parse
  duration text into `Duration` fields and `[<Default 30s>]` works.
  No JSON: a Duration field at `to json`/`from json` is a check error
  (convert via `Duration.toMillis` into an int field). Interpolation holes
  render Durations directly (`$"took {elapsed}"`); command arguments
  do NOT — pass `Duration.toMillis d` or `show d` deliberately.
- Secrets are `Secret` (a plain string inside — a RENDERING marker, not
  memory protection): `show` renders `***` (including a `Secret` field
  inside a shown record), interpolation REFUSES (`$"tok: {s}"` is a
  check error), `to json`/`to yaml` REFUSE, `print` refuses — each
  naming `Secret.reveal`, the one exit. It DOES splice into argv in the
  clear (`curl -H $auth` — `ps`-visible, a stated non-claim). `Eq`
  compares (`s1 == s2`), `Ord` refuses. Produced by `Env.load`/`Args.load`
  `Secret` fields (env is the CI secret channel), `File.readSecret p`
  (a mounted secret file), and `Secret.of` (assert secrecy, for computed
  secrets — the safe direction). `Secret.map (fun t -> "Bearer " + t) s`
  keeps a derived value secret; `[<Default>]` on a `Secret` field is
  rejected. Qualified-only (no bare `of`/`map`). Not memory-hardening:
  the value is an un-zeroed managed string (see SECURITY.md).
- HTTP is `Http.send : HttpRequest -> HttpResponse` (a record + one
  runner, no new grammar). The common case is a CONSTRUCTOR:
  `Http.send (Http.get u)`, `Http.send { Http.post u with auth = Bearer
  tok; body = Json (payload |> to json) }` — one per method
  (get/post/put/delete/patch/head/options/query), each equal to `{
  Http.defaults with method = M; url = u }`. `Http.fetch u : seq<string>`
  is the raising GET shorthand — a BARE URL in, body out (never a
  request: `Http.get u |> Http.fetch` is a type error naming the repair;
  a built request runs through `Http.send`); raises on non-2xx — the
  pair to send, which binds it. `url |> Http.withQuery [(k, v)]` percent-encodes
  a query string. `Http.query` is the QUERY method (idempotent, so
  `retry` around it is safe by definition). TLS verification is ON;
  `{ req with insecure = true }` disables it for ONE request (a loud
  per-call field for self-signed clusters). `Http.defaults` is the
  template (Get, 30s timeout, secure). `auth` is a UNION
  (`NoAuth`/`Bearer of Secret`/`Basic of string * Secret` — Basic does
  the base64); a `Secret` carries WHOLE (interpolating a token is a
  check error) and `show` masks it. Status is DATA (`if resp.status >=
  400 then fail …`, a 404 binds); only TRANSPORT failure raises. The
  body is `NoBody`/`Json of seq<string>`/`Text of string` — `Json`
  carries the caller's `to json` lines, byte-exact to the wire (the
  curl `-d` mangling this exists to prevent). `resp.body |> from json
  T` reads the response — pretty-printed or minified, one document
  either way; for a plain GET, `curl url |> from json T` is still the
  spelling. `secretHeaders` for credential headers; headers stay
  PAIRS (`seq<string * string>`), never a map — duplicate header
  names are legal HTTP (Set-Cookie), and a map cannot hold them;
  parallel
  fetches are `urls |> Seq.pmap (fun u -> Http.send { Http.defaults with
  url = u })`.
- Params are plain idents OR `()` (a unit param: `let cleanup () =`;
  `cleanup 5` is a type error). Other pattern params stay rejected.
- No async/task/await — processes and pipelines are the concurrency
  model. A task that truly needs async belongs in full F#, not weir.
  For fan-out over items: `xs |> Seq.pmap (fun x -> ...)` (parallel,
  ordered results) / `Seq.piter` for effects — sized for I/O-BOUND
  arms (spawns/waits/sleeps): up to 64 concurrent arms regardless of
  cores; `Seq.pmapWith n` / `piterWith n` set the ceiling explicitly.
  Every arm runs even if one fails; the first error BY INPUT ORDER
  rethrows after the join. Workers fork the session:
  `cd` inside a worker is worker-local and gone at the join — force
  worker output inside the worker (`Seq.head`/`Seq.force`) if its cd
  matters. The RACE is `xs |> Seq.pfirst (fun x -> ...)`: the first
  arm to SUCCEED wins, losers' spawned processes are tree-killed and
  their failures never surface (all-failed rethrows the first by
  input order; empty raises). `Seq.pfirstWith n` sets the ceiling.
- A `let` RHS takes command mode wherever lets go — top level AND
  inside bodies (`let tree = git rev-parse $c |> Seq.head` in a
  function); `$()` covers sub-expression positions. `function | pat -> e | …`
  is the implicit-match lambda (`fun x -> match x with …` exactly;
  first `|` optional, guards work) — the usual spelling for
  `Seq.choose` over lines.
- Bounded loops are `retry`/`poll` compound forms (no `while`):
  `retry attempts=5 delay=30s` + an indented body block, then
  optionally `until r` + an indented predicate block binding the
  body's value. A `bool` body IS the predicate (no `until`, yields
  unit); a value body REQUIRES `until` and yields the value.
  `poll timeout=5m interval=10s` is the time-bounded twin. The head
  desugars over a record — `retry { Retry.defaults with attempts = 5 }`
  is the same form, so options can be computed and shared. Exhaustion
  RAISES; raises inside the body propagate (retry on the predicate,
  not on exceptions — use `| succeeds`/`| complete` to make failure
  data). Key values are atoms: parenthesize compounds
  (`delay=(d * 2)`).
- No `let rec`, no mutation. Iteration: pipelines TRANSFORM
  (`|> Seq.map/where/fold`); `for x in xs do body` EFFECTS (it IS
  `Seq.iter` — desugared, eager, body must be unit). A bare command
  body works and is implicit `!(…)`: `for f in files do git add $f`
  streams and raises per iteration; `do !` opens a command block.
  Comprehension: `[for x in xs -> e]` (eager). No guard clause —
  filter with `Seq.where` upstream.
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
- `Seq.last`/`Seq.tryLast` FORCE the whole source by necessity (an
  infinite source does not return); `Seq.rev`, `Seq.sort`/
  `Seq.sortDescending` (key-less, Ord on the elements), and
  `Seq.countBy` join the forcing family — reversal, ordering, and
  counting need the whole input. `Seq.windowed n` is lazy (short
  source = empty seq, no partial window; n <= 0 raises). `Option.iter` runs a Some-only
  effect; `Option.orElse fallback opt` stays in Option
  (`defaultValue` is the one that unwraps). `Path.tempRoot ()` is the
  pure query; `Path.newTempDir ()` CREATES (cleanup is yours —
  `within tmp` is the scoped-cleanup spelling).
- Filesystem [D:fs-members]: `File.delete/copy/move/size` (copy/move
  = (src, dst), REFUSE existing destinations — delete first to
  overwrite), `Dir.create` (idempotent, makes parents) /`exists`/
  `delete` (empty only)/`deleteAll` (RECURSIVE, destructive)/`list`
  (full paths, sorted, both kinds; `Path.glob "**"` recurses)/`move`.
  Every failure names its path.
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
- The wider Seq surface [D:seq-gaps], all lazy unless said otherwise:
  `collect` (map-and-flatten — F#'s name; there is no flatMap) and
  `concat`; `find` (raises; `tryFind` asks) and `pick`/`tryPick`
  (choose-then-head in one pass); `indexed` (position pairs — mapi is
  `indexed |> map`); `takeWhile`/`skipWhile`; `distinctBy`/`countBy`
  (Eq on the key); `reduce` (fold without a seed, raises on empty) and
  `scan` (fold with intermediates, seed first); `chunkBySize` (the
  batching member); `except` (set difference, exclusions first);
  `replicate`; `max`/`min`/`maxBy`/`minBy` (Ord; raise on empty; one
  pass, no sort); `first`/`take` are RULED synonyms. Sums: `Seq.sum`
  is `seq<int>`; `Float.sum`/`Size.sum`/`Duration.sum` (and their
  `average`s) own the other types — `Seq.average` alone crosses types
  (the mean of ints is a float). `Seq.filter` teaches `where`.
  An OPERATOR can be a value, UNAPPLIED only [D:operator-values]:
  `Seq.reduce (+)`, `Seq.fold (+) 0` — exactly `fun a b -> a + b`, so
  context resolves the overload (`(+)` sums floats/strings/Durations/
  Sizes where the elements say so). Admitted: `+ - * / > < >= <= ==
  <>`. Partial application REFUSES (`(>) 10` reads backwards — the
  message shows both lambda directions); `(&&)`/`(||)` refuse (a value
  cannot short-circuit); the pipes and `>>`/`<<` refuse (grammar /
  already the composed function).
- Match-or-skip over a stream is `Seq.choose` (lazy, qualified-only):
  the arm returns `Some out` or `None`, never a sentinel `""` to
  filter later. The natural pair with the `Regex` pattern:

```weir
let refs = ["sha1 refs/a"; "junk"; "sha2 refs/b"]
refs
    |> Seq.choose (function | Regex @"(\w+) refs" s -> Some s | _ -> None)
    |> Seq.iter print
```
- Lambdas take multiple params: `fun acc x -> ...` desugars to nested
  lambdas exactly like `let f a b =` sugar (same param set — idents,
  `()`, parenthesized irrefutable patterns; duplicates rejected).
- A `(fun ... ->` dangling at line end opens a BODY BLOCK closed by
  its own `)`: ordinary block rules inside (block lets, siblings,
  compounds, blocks, blanks), body lines at or right of the
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
// (the ! district retired [D:district-retirement]: commands are ordinary statements)
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
let defaults = { Host = "h"; Port = 80 }
let tls = { defaults with Port = 443 }
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
  Builtins shadow PATH (`ls` is typed rows — files AND
  subdirectories: name, path, bytes (`0 B` for a directory),
  isDirectory, hidden, readOnly, age (Duration since last write) —
  so `where _.isDirectory` and `where (fun f -> f.age < 1h)` are the
  spellings); `^ls` forces the external.
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
  (redirection is the fenced block below / `File.append`).
  For bash semantics: `sh -c "the bash line"` (a command
  line; streams, completes, pipes like any command). Inside that
  quoted line `$w` is SH's variable, not weir's binding — silently
  empty, not an error; splice weir values by interpolating first
  (`sh -c $"echo got-{w}"`).

```weir
// the redirection idiom — the spelling that failed TWICE in prose
// [D:sized-findings]: a function on the right takes |>, never |
within tmp d
    echo redirected |> File.write $"{d}/out.txt"
    File.read $"{d}/out.txt" |> Seq.iter print
```
- Nonzero exit RAISES when the stream is forced. The exit-code
  reifiers (complete's family, single external segment, one law:
  output goes where the meaning goes): `cmd | succeeds` reifies to
  BOOL (silent — a predicate's output IS its result); `cmd | orFail
  "msg"` STREAMS and raises `msg (exit N)` on nonzero, unit on
  success — THE assert idiom, legal as a statement, in `!()`, and in
  interior lines; `cmd | exitCode` STREAMS and reifies the code as INT,
  never raises — bind it or match it (`| 130 ->` for cancels); a
  bare/`!()`/`$()` position is a teaching error ($() captures — use
  `| complete` there). **`succeeds` is exitCode == 0, exactly** —
  for tools whose nonzero codes AND output are both data (grep,
  fzf), use `| complete` and read the record. An `if`/`elif`
  CONDITION takes the chain inline: `if test -f $p | succeeds then`
  (`then` ends the argv only there — quote `"then"` to pass the word;
  the checker demands bool, so a bare streaming chain errors naming
  it). Bind first when the verdict is used twice.
  Full inspection: `cmd | complete` gives `{ exitCode; stdout;
  stderr }`; a COMPUTED argv splats into the chain —
  `$author(git commit-tree $@argv | complete) |> _.stdout` (literal
  head, splatted argv, sigil env; works with all four reifiers,
  value-headed and interior lines too). `print ()` is silent (unit
  prints nothing — the rule that lets orFail sit in effect
  positions).
- Capture is IN MEMORY: `| complete` holds the whole output as one
  byte buffer + line offsets (~2x the text in RSS; lines decode
  per pull). Unbounded output is still unbounded — for gigabyte or
  endless children STREAM it (`|> Seq.iter`, `| File.write`) instead
  of capturing; the ceiling is the box, and a single capture caps at
  ~2GB. `Seq.force` on decoded lines re-pays string overhead — force
  what you need, not the world.
- Typed output: `... |> from json T` needs
  `type T = { field: ty; ... }` declared first (exact field set) — OR
  the shape written inline: `from json {| ip: string |}` (and
  `seq<{| ... |}>` for a top-level array). Reach for the anonymous
  form to read a FOREIGN shape once; declare a type for your own data
  and anything reused. Same-shape anonymous types are one type; a
  declared record with the same fields stays a different (nominal)
  type. Adapter slot only — no anonymous literals, no nesting (a
  nested object needs a declared record).
  `from json T` reads ONE DOCUMENT -> `T` (any number of lines — a
  pretty-printed body pipes straight in); `from json seq<T>` reads a
  top-level ARRAY document -> `seq<T>` (the list-endpoint shape);
  `from jsonl T` reads one document per element -> `seq<T>` (NDJSON,
  the shape `to json` writes). The DECLARED type decides what the top
  level must be — nothing sniffs the input.
  The field law is RECURSIVE: a field is a scalar (`int`, `float`,
  `string`, `bool`), an `Option` of an admitted type, a record whose
  fields are all admitted, a `seq` of an admitted type, or a
  `Map<string, T>` of one — so `{ entityids: Entity }`,
  `{ items: seq<Item> }`, and an ID-keyed `{ documents: Map<string,
  Doc> }` all read. A `Map`'s keys are DATA, not schema, and strings
  ONLY (JSON object keys ARE strings; `Map<int, …>` teaches). The
  whole document can be the map: `from json Map<string, T>` (the
  adapter slot's third form; `{| … |}` composes in the value slot;
  `jsonl` refuses — a map is ONE object; `yaml` does not take it
  yet). Duplicate keys last-win. `Map` surface: `ofPairs`
  (last-wins) / `pairs` / `keys` / `values` (key-sorted) / `get`
  (raises, naming the key) / `tryGet` / `has` / `add` / `remove` /
  `count`; no `m[k]` indexing — `Map.get` is the spelling; `==` is
  not defined for maps. A self-referential record
  refuses at check, naming its cycle. An `Option` field reads a
  missing key OR an explicit `null` as `None` — at EVERY depth (a
  `null` element under `seq<Option<int>>` is `None`); a required
  field that is missing or `null` errors, naming the fix — a missing
  ARRAY too: absence is `Option`'s job, `[]` is not guessed. `to json`
  OMITS a `None` field's key (matching `gh`/`kubectl`) but writes
  `null` for a `None` array ELEMENT (a slot cannot be omitted), so
  the roundtrip holds for nested shapes too. `Args.load`/`Env.load`
  stay FLAT — a CLI flag or env var is a string; nesting has no
  spelling there. YAML is the TREE boundary: `from yaml T` reads
  ONE document (a mapping) -> `T`; `from yaml seq<T>` reads one
  top-level SEQUENCE document -> `seq<T>` (nested records, seqs,
  `seq<string * string>` for labels, `Option`; bool is EXACTLY
  true/false; anchors/flow rejected). A `---` STREAM teaches — split
  and parse each document (weir cannot type a heterogeneous stream).
  `value |> to yaml` (a seq = multi-doc; `None` fields omit;
  ambiguous strings like `"no"`/`"007"` auto-quote). `Yaml`
  nodes (`YMap [("k", YStr "v")]`, `YSeq`, `YInt`…) render directly —
  `YMap` keeps YOUR key order; record fields render alphabetically.
  Literal block scalars `|`/`|-` are in the subset (folded `>` and
  `|+` reject): `|` MEANS ends-with-one-newline, `|-` ends-with-none —
  the form follows the value both directions, and a multiline string
  renders as a block scalar automatically. A `yaml` BLOCK is a
  checked template: `let d = yaml` + an indented
  YAML block; `$name`/`$(expr)` splice VALUES (never text — no
  injection is possible), a `None` splice omits its entry, and
  `for (k, v) in pairs` under a mapping yields dynamic keys (under a
  sequence, items). `yaml schema=<name>` on the marker line validates
  the district against `.weir/schemas/<name>.json` at CHECK time
  (add one with `weir add schema <url> --as <name>`; structural errors
  like a misspelled field are check errors with did-you-mean; splices
  check by TYPE; a district with no declaration is unvalidated).
  A `key: |` block scalar's content is LITERAL —
  `$VAR` and `for` lines inside it are bytes (embedded scripts stay
  verbatim); templated content interpolates upstream and splices as a
  whole value. Paste a manifest, replace values with `$`.
- `!(…)` runs one command inline in expression position (`!(git
  pull)`). There is NO line-end `!` block — that district was retired
  [D:district-retirement]: commands are ordinary statements inside any
  block, so `if clean then` + indented `git checkout main` /
  `git pull` lines just works [D:interior-arming].
- The glyph law: weir has no `!`-negation — negation is the word
  `not`; `!` means DO IT. And no `\`-escape for commands — `^ls`
  forces the PATH binary.
- Modules & imports (share code between scripts): a file that starts
  with `module` (or `module Name`) is a MODULE — importable,
  declaration-only (`type`/`let` only, no commands or bare
  expressions), not runnable. Import it with `import "./lib/x.weir"`
  (a literal path, first in the file before declarations) or
  `import "./lib/x.weir" as X`. Access is ALWAYS qualified:
  `X.helper`, `X.Ctx` types cross the boundary. Construct an imported
  record with the qualified literal `X.Ctx { field = v; ... }` (or
  `Ctx { ... }` when the name is unambiguous); a bare `{ ... }` still
  works when exactly one record in scope has those fields. The alias
  defaults to the module's declared name (or the capitalized
  filename). Errors are named: running a module, importing a
  non-module, a self-import, a module `let` that runs a command
  (wrap it in a function), or a missing file (the message shows the
  resolved absolute path). Resolution is check-time — nothing loads
  at runtime. `import` is script-only (not `-e`/REPL). Imports are
  transitive (a module may import); a shared module is checked once
  (diamonds collapse) and an import cycle is a named check error.

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
  chain (`let b = git branch |> Seq.head`); sigils are for positions
  bare cannot reach. The block effect idiom:
  `if clean then` + indented `!(...)` lines. Interiors are ordinary
  command chains (splices, pipes, `| complete`). `!` is NOT bash
  history/extglob and `;` still does not chain inside them.
- A top-level `let` RHS takes command lines — param-ful included
  (`let f r = git rev-parse $r |> Seq.head`): `let files = git ls-files`
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
print $"{cli.clean} {cli.port}"
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

git status --porcelain |> Seq.choose (function | Regex @"^.. (.*)$" path -> Some path | _ -> None)
```

## Diagnostics and exiting

- `fail "reason"` raises: the script stops with a located error and
  exit 1. `if bad then fail $"broken: {n}"` is the checking-script
  idiom.
- `printerr` is `print` to stderr (same argument rule) — diagnostics
  there, data on stdout, so `weir script | next` stays clean.
- `Log.trace/debug/info/warn "msg"` — levelled diagnostics, ALWAYS to
  stderr (stdout is data; there is no stream knob). `WEIR_LOG=level`
  selects (`trace|debug|info|warn|off`; default `info`; invalid
  values error at startup). There is NO `Log.error` — an error
  silenced by `WEIR_LOG=off` is the message you needed; unconditional
  messages are `printerr`, stopping is `fail` (never filtered).
  Arguments are EAGER (weir has no lazy positions): in hot loops use
  the thunk twins — `Log.debugWith (fun () -> expensive)` — which
  run only when the level passes (the `Option.defaultWith` shape).
- Operators bind tighter than `|>`: `xs |> Seq.length == 2` is a
  targeted check error; write `(xs |> Seq.length) == 2`.

```weir
printerr "starting up"
print (if ([1; 2] |> Seq.length) == 2 then "ok" else "wrong")
```

```weir
type HomeCfg = { HOME: string; WEIR_ABSENT_ZZ: Option<string> }

let cfg = Env.load HomeCfg
print $"home={cfg.HOME} extra={cfg.WEIR_ABSENT_ZZ}"
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
  feature), write bash — and append one line to dev/NOTES-agent.md:
  date | task shape | the gap that forced bash.
- NEVER invent weir syntax. If unsure a feature exists, check this
  file; if absent here, assume absent, fall back, log. The full
  rejected-vs-pending border with F# is tests/fidelity/divergences.md.
