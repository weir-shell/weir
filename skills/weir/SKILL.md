# Writing weir scripts (weir is NOT F#, NOT bash, NOT Nushell)

Every fenced `weir` block below is executed against the AOT binary in CI
(`ci/skill-doc.sh`); `weir-error` blocks must FAIL. A line here that
stops being true fails the build.

## Files and running

- Shebang `#!/usr/bin/env weir`; extension `.weir`; run `weir file.weir [args]`.
- `weir -e '<program>'` evaluates a PROGRAM (newlines are statement
  boundaries, exactly as in a file) and echoes its LAST statement's
  value — so declarations may precede the expression, but a lone
  declaration is refused (`-e` shows you a result, by design — a
  deliberate divergence from `python -c`).
- `weir fmt file.weir` canonicalizes indentation (4 per block depth);
  `--check` for CI.
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
- Scripts and `-e` are STRICT: every module member is qualified —
  `Seq.map`, `Str.trim`, `Option.defaultValue`, `File.read` — including in
  command pipelines (`|> Seq.map Str.trim`). Bare names (`map`,
  `where`, `sortBy`) exist in the REPL session ONLY, and the REPL's
  set is DERIVED [D:bare-partition]: every single-home member of
  `Seq`/`Str` is bare there; a name with two homes is qualified-only
  everywhere — `contains` and `length`, the whole list. The qualified
  spelling always works. A bare name in a file errors naming the
  qualified spelling, and the LSP offers the rewrite as a code action.

```weir-error
let xs = [1; 2; 3] |> map (fun n -> n * 2)
print (show (Seq.length xs))
```

```weir-error
#loose
print (show 1)
```
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
  echo is tighter still (100 unforced elements — `#echo` moves it,
  forced seqs echo whole; clipped strings, a hint naming the way
  out): echo = glance, `|> print` = read. BINARY output (a NUL in
  the prefix) refuses the tty echo with a redirect hint — a compressed
  stream never wrecks the terminal; `print` remains deliberate.
- `xs |> Seq.map print` does nothing (lazy) and is a check error as a
  statement; use `xs |> Seq.iter print`.

```weir
let files = git ls-files
print $"tracked: {files |> Seq.length}"
["a"; "b"] |> Seq.iter print
git ls-files |> Seq.take 1
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
    sh -c "echo step-one"
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
  `[<NoShort>]`, `[<Default v>]`, `[<Wire "type">]` — `;`-separated
  lists, literal args only (string/int/bool). Attributes are
  check-time data, fully erased: an attributed record is the same
  type as a bare one. `[<Wire "…">]` names a field's ADAPTER key
  [D:wire-keys]: reserved words (`type`, `to`, `from` — ordinary
  JSON/YAML keys) and future illegal identifiers ride it; `from`/`to
  json`/`jsonl`/`yaml` all read and write the wire key (the roundtrip
  holds), `Env.load` HONOURS it too (an env var name is no more the
  author's to choose than a JSON key — a leading digit, a dash or a
  reserved word are all legal there), `Args.load` REJECTS it (argv is
  weir's own boundary, so there is no wire to match — flags DERIVE, and
  `[<Short>]`/`[<NoShort>]` are the naming controls), and
  two fields resolving to one wire key refuse at the declaration. The name set is CLOSED — an unregistered name
  (`[<Positional>]` among them: dropped, scripts take flags) is a
  check error with a did-you-mean. Attributes attach to record
  fields, union cases, and union type declarations (an attribute
  line above `type` binds to it), and the registry is
  POSITION-SCOPED — a registered name in the wrong position names
  its home (`'Tag' attaches to a union declaration`).

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
  match with `| Case (n, s) ->`. A case name declared by TWO in-scope unions
  is AMBIGUOUS at any bare use — a check error naming both types, never
  last-wins [D:ambiguous-ctor]; rename one. Arity does not disambiguate
  (applying it does not pick an overload), and PATTERNS are unaffected: a
  case in a pattern resolves against the scrutinee's type. Imported unions
  never collide — their cases are not in scope bare.

```weir-error
type Level = Warn
type Signal = Warn
let w = Warn
print $"{w}"
```
- `let f x y = ...` defines a curried function (desugars to nested
  `fun`). Params are idents, `()`, or PARENTHESIZED irrefutable
  patterns (`let dist (x, y) = ...`) — no type annotations. A
  param-ful let TAKES a command RHS
  (`let revParse r = git rev-parse $r |> Seq.exactlyOne`): params shadow
  PATH inside their own RHS (bindings-beat-PATH's scope; `^x` still
  forces the binary), and a spliced param defaults to string at the
  statement boundary. Splices are WHOLE argv entries — a mid-word
  splice like `--file=$f` is a hard error (the prefix can't glue to
  the value); spell `--file $f` or an interp arg `$"--file={f}"`. A
  PATH-shaped word (`./dir/$f`) leads you to interpolation or
  `Path.under` instead — a space there would split one path into two
  arguments. The SUFFIX side is equally fatal: argv pieces do not
  CONCATENATE, so `$root/*`, `--flag="v"`, `"x"y` and `pre(x)` all
  refuse ("argv words do not concatenate") — adjacent pieces would
  each become their own argument, never one glued word.

```weir-error
let f = "x"
echo --file=$f // a splice cannot join a word under construction
```

```weir-error
let build = "b"
echo ./tt3/$build // a path: the hint leads with interpolation / Path.under
```

```weir-error
let root = "r"
echo $root/* // argv words do not concatenate — the tail would be its own argument
```

```weir-error
echo --flag="quoted v" // the quoted part would be its own argument; quote the whole word
```
- `+` on two unknown params cannot infer (int-or-string): anchor one
  side (`x + 0`) or take data in. All single-typing operators
  (`- * / % > <`) default to int; `let rec` and `mutable` are reserved
  words with no meaning. `%` is integer remainder, TRUNCATED like
  F#/.NET (`-7 % 3` is `-1`, NOT Python's `2`); zero divisor raises
  ("modulo by zero"); floats are refused (finite-only floats cannot
  hold IEEE's NaN remainder) — `Float.toInt` one side. But `==`, `show`, and `Seq.sortBy` ARE
  generic (inferred constraints): `let same x y = x == y` works at any
  equatable type — rejected only at functions/seqs, at the USE site.
- Literal patterns work (`| 0 ->`, `| "yes" ->`, `| () ->`, nested in
  constructors) but int/string literals NEVER complete a match alone —
  add a `_`/var arm or it is a hard error. Guards remain legal.
- Raw strings, F#'s two kinds, both SINGLE-LINE: `@"..."` verbatim
  (backslashes literal; `""` = one embedded quote) and `"""..."""`
  (no escapes at all; bare `"` fine inside), and the RAW INTERPOLATED
  `$"""…{hole}…"""` [D:interp-raw] — escapes off AND holes on, the
  generating-weir-from-weir spelling (a backslash, a quote, and a
  splice in one literal; no `$@"…"` — one spelling, and the attempt
  teaches). A literal brace has no raw spelling ({{ teaches the
  ordinary form); the Regex position refuses it (patterns are
  check-time literals); Secrets refuse the raw hole exactly as the
  ordinary one. An unterminated string of ANY kind now names its
  opener. Rawness belongs to the
  literal KIND, never to position — a string means the same thing
  everywhere.

```weir
let v = "1.2.3"
print $"""say "{v}" with a \ backslash"""
```

```weir-error
let s = Secret.of "x"
print $"""tok {s}"""
```

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
  `Str.tryFromBase64` (raise/None on malformed AND on non-text bytes —
  NUL included: a NUL-bearing decode is binary, and binary's home is
  `Bytes.fromBase64`).

```weir-error
let v = Str.fromBase64 "YQBi"
print v
```

- `Bytes` — the non-text value [D:bytes], an in-memory byte array,
  OPT-IN at both ends: nothing becomes byte-typed by default (commands
  still produce `seq<string>`, `File.read` still decodes). Sources:
  `File.readBytes` (no decode, no line split — the byte-faithful
  read), `Bytes.fromBase64`/`tryFromBase64` (malformed raises/None; NO
  text gate — that is the point), `Str.toUtf8`. Sinks:
  `File.writeBytes`, `Bytes.toBase64`, `Str.fromUtf8`/`tryFromUtf8`
  (the encoding law's gate: non-UTF-8 or NUL-bearing bytes raise/None).
  Operations: `Bytes.sha256`, `Bytes.length : Size`, and
  `File.sha256 path` (streams internally — hash a file without loading
  it). The capture law scopes the type: bounded and in-memory (~2GB
  cap); a gigabyte artifact is not a value. Laws: `==` is byte
  equality; no ordering; `print`, `to json`/`to yaml`, argv splices
  and `Args.load`/`Env.load` all REFUSE, each naming `Bytes.toBase64`
  or `File.writeBytes` as the exit; a hole or `show` renders a SUMMARY
  (`<1.4 MiB>`), never content — raw bytes wreck terminals.

```weir
let b = Str.toUtf8 "hello"
print (Bytes.sha256 b)
print (Bytes.toBase64 b)
print $"{b}"
let png = Bytes.fromBase64 "iVBORw0KGgo="
print (show (Bytes.length png))
print (show (Str.tryFromUtf8 png))
```

```weir-error
print (Str.toUtf8 "x") // print refuses Bytes; Bytes.toBase64 is the exit
```
  patterns live on the expression side: `Str.isMatch pat s` (bool),
  `Str.rmatch pat s` (Option<seq<string>>) — any string, and raw
  literals read best: `Seq.where (Str.isMatch @"\.md$")`.
  `Str.rmatchAll pat s : seq<seq<string>>` is the plural — EVERY
  match's GROUPS, lazily; no Option (absence is the empty seq). Groups
  ONLY: a pattern with no capturing group yields an EMPTY inner seq per
  match, so `|> Seq.map Seq.head` raises — wrap what you want to read.
  `(?s)`/`(?m)` inline flags cover DOTALL/MULTILINE. The scrape idiom
  is one pipeline: `Str.rmatchAll pat text |> Seq.map Seq.head |>
  Seq.distinct` (all matches → contents → dedup); pipe a match through
  a tool with `| sha256sum`.
- Split at the FIRST separator, tail INTACT [D:split-once]:
  `Str.splitOnce sep s : (string, string)` — Rust's split_once shape;
  raises when the separator is absent; `Str.trySplitOnce` is the
  Option twin (`Some (before, after)` / `None`). This is the
  KEY=VALUE / host:port spelling — `Str.split` + a `[k; v]` pattern
  silently MISSES when the value contains the separator; splitOnce
  keeps the tail whole. Empty separator refused on both. Destructures
  straight into names:

```weir
let (key, value) = Str.splitOnce "=" "PATH=/usr/bin:/bin"
print $"{key} -> {value}"
```

- Cardinality [D:exactly-one]: `Seq.exactlyOne` asserts ONE element —
  raises on none AND on more, with distinct messages (a source that
  produced nothing and one that produced extra are different bugs);
  `Seq.tryExactlyOne` is the Option twin (None for both failure
  shapes; never hangs — it stops at the second element). THE SPELLING
  for command output expected to be one line
  (`git rev-parse HEAD |> Seq.exactlyOne`) — `Seq.head` takes the
  first and silently accepts more; save it for "the first of many".
  `Seq.first` is RETIRED [D:first-retired]: one name per operation —
  `Seq.take` (pairs with `skip`).

```weir-error
// retired: one name per operation (take pairs with skip)
[1; 2; 3] |> Seq.first 2 |> Seq.iter print
```

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
- Absolute time is `Instant` [D:instant] — a POINT on the UTC
  timeline, the boring subset: no local zones, no calendar arithmetic
  (`t + 1d`-style "next day same wall time" does not exist; a shift is
  exact physical time — `t + 24h` means exactly 24 hours). `Instant.now ()`;
  `Instant.parse` reads ISO 8601 (`Z` or numeric offsets, normalized
  to UTC; a bare date is midnight UTC); `Instant.parseWith`/`tryParseWith`
  read NAMED formats for log lines and cert dates — `%Y %m %d %e %b
  %H %M %S %f %z`, prefix semantics (the line's tail rides free), no
  `%z` means UTC. `instant - instant` IS a `Duration`
  (`expiry - Instant.now () > 24h * 300`); `instant ± duration`
  shifts; two points never add (teaching error). Ord/Eq admit —
  instants sort and compare. `Args.load`/`Env.load` parse ISO into
  `Instant` fields (`--since 2026-08-01`). No JSON (convert via
  `Instant.epochMs` or `show`); command argv likewise deliberate.
  `show` renders ISO UTC.
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
  template (Get, 30s timeout, secure). Every request sends
  `User-Agent: weir/<stamp>` (the `--version` string) unless the
  caller sets one — an explicit `User-Agent` WINS (via `headers` or
  `secretHeaders`, any casing) and exactly one is ever sent. The
  default is applied at SEND time, not a field of `Http.defaults` —
  so a shown/pinned request stays stable across releases, and `show
  req` does NOT display the UA the wire will carry [D:http-ua]. `auth` is a UNION
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
  arms (spawns/waits/sleeps): the DEFAULT ceiling is PER FAN-OUT and
  shrinks with nesting — 64 at top level, 8 inside a worker, and a
  fan-out nested twice runs serially — so nested fan-outs cannot multiply into a width
  nobody chose (64 outer x 8 inner caps the product at 512).
  `Seq.pmapWith n` / `piterWith n` / `pfirstWith n` set the ceiling
  explicitly and are NEVER reduced: a written number is the author's
  decision, nesting included.
  Every arm runs even if one fails; the first error BY INPUT ORDER
  rethrows after the join. Workers fork the session:
  `cd` inside a worker is worker-local and gone at the join — force
  worker output inside the worker (`Seq.head`/`Seq.force`) if its cd
  matters. The RACE is `xs |> Seq.pfirst (fun x -> ...)`: the first
  arm to SUCCEED wins, losers' spawned processes are tree-killed and
  their failures never surface (all-failed rethrows the first by
  input order; empty raises). `Seq.pfirstWith n` sets the ceiling.
- A SCOPED background process is `within proc srv = <command>` +
  an indented block [D:scoped-procs]: the scope IS the lifetime —
  at every exit (normal or raise) the process TREE is killed and
  reaped; there is no `&` — an unscoped BACKGROUND child is
  unrepresentable. A bare FOREGROUND command is synchronous (weir
  waits for it), so the no-orphan claim's one gap is weir dying WHILE
  it waits: the group signal or the controlling terminal's HUP reaps
  the child then, and a child that refuses INT and HUP outlives weir
  — the `kill -9` carve-out's register [D:pty-review].
  The readiness
  wait is `poll timeout=10s watch=srv` + `Net.portOpen <port>` as the
  body — `watch=` fails at the next interval tick if the child dies (its last output rides
  the error) and stamps the child's state on a timeout. The handle is
  data: `Proc.pid`/`running`/`tail`; `Proc.wait` lets it finish
  naturally (exit code as data); `Proc.stop` tears down early. BOTH
  the child's streams spill to files in a managed tmp dir — never the
  parent's terminal or its stdout DATA channel (a server logging to
  stdout cannot break `weir script | next`); the spill is bounded by
  disk, not memory, and `Proc.tail` reads its last ~100 lines. THE
  SURFACING RULE: a scoped child's own exit is DATA, the one place
  raise-by-default does not apply — failure becomes visible through
  `watch=` or `Proc.wait`, nowhere else. Nested scopes release LIFO.
  The command position takes splices and env sigils like any command;
- A bare `within` (no kind) + `always` is the exit discipline alone
  [D:within-always]: the indented body runs, then the `always` block
  runs on EVERY exit — normal, `fail`, `exit n`, SIGINT/SIGTERM (not
  `kill -9`, the standing carve-out). The rulings: a cleanup raise on
  a CLEAN exit propagates (always is never where a raise disappears);
  when already unwinding, the ORIGINAL error wins and the cleanup's
  own failure goes to stderr with a marker; a failed inner cleanup
  never strands the outer scopes (teardown continues LIFO). `exit`
  inside `always` is a check error (teardown must finish); retry/poll
  inside are fine. There is no kinded `within proc … always` yet —
  nest a bare within inside the proc scope.
- `within lock "path"` holds an ADVISORY file lock for the block
  [D:within-lock]: created if missing, nothing bound (there is
  nothing to ask a lock). Blocking by default; `timeout=30s` bounds
  the wait and exhaustion raises, retry-style. Excludes across
  processes AND across `pmap` arms (flock semantics, per open file —
  probe-pinned), and interoperates with `flock(1)`. The one scope
  whose guarantee survives `kill -9`: the KERNEL releases the lock on
  any death. Advisory means a non-cooperating process can ignore it;
  on NFS and network filesystems advisory locking is unreliable — use
  a local path. Re-acquiring a lock you already hold (nested, or via
  a function) waits like any contender: give it a timeout.
  pipelines and reifiers refuse (`| complete` WAITS — the opposite of
  backgrounding; compose inside `sh -c`); the block starts on the
  NEXT line (the command owns the rest of its own). THE NON-CLAIM,
  stated: normal exit, raise, SIGINT and SIGTERM all close every
  scope; `kill -9` of weir itself cannot, by definition — no
  userspace design closes that hole. AND THE MIRROR non-claim: the
  tree-kill weir performs lands as SIGKILL on the CHILDREN too
  (measured: wait = 137), so `within proc` does not run your child's
  cleanup — a child that traps SIGTERM to flush state, remove a
  lockfile or deregister never gets the chance. A child that must
  OUTLIVE the script is a daemon — write a unit file; weir declines
  nohup.
- A `let` RHS takes command mode wherever lets go — top level AND
  inside bodies (`let tree = git rev-parse $c |> Seq.exactlyOne` in a
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
        echo fetching $tag
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
  An arm body takes BARE COMMANDS [D:match-arm-commands] — the
  case-runner idiom, no sigil: `| "build" -> sh -c "make"`. In
  statement position each arm streams; a value-position match (a `let`
  RHS) captures the last arm's chain as `seq<string>`. The chain ends
  at the next `| <pattern> ->`, so an argv word spelling `x ->` needs
  quoting to stay an argument.
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

let label =
    match grade with
    | Pass n when n > 100 -> "outstanding"
    | Pass n -> $"pass ({n})"
    | Fail -> "fail"

print label
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
- A bare STATEMENT command at a tty INHERITS stdout
  [D:colour-inherit]: the child sees the terminal (isatty true), so
  tools that colour for a tty colour under weir — and weir never
  holds the bytes (no guard or cap on that path: the bytes are the
  child's own, bash's posture; a child ending mid-line leaves its
  wart, also bash's). Every VALUE form keeps the pipe — `cmd |>
  print`, `$()`, `| complete`, reifiers — a value must DECODE and
  decoding requires a pipe: the one deliberate path divergence,
  chosen for colour. A redirected weir keeps the pipe everywhere
  (byte laws intact — and the child would see a pipe in bash too).
  For colour in a CAPTURED form, most tools honour their own env
  convention: `let c = Env.ofPairs [("FORCE_COLOR", "1")]` then
  `$c(cmd | complete)` — per-tool foreign conventions, deliberately
  not a weir feature.
- At the REPL, `^C` during a foreground child is a group SIGINT
  [D:repl-isig]: the child dies, the error names exit 130 (the script
  path's exact message), and the SESSION returns to the prompt. At an
  idle prompt `^C` clears the line. `^C` during a pure computation (no
  child) interrupts nothing — the byte waits and clears the next
  prompt.
- A REPL foreground child hung reading stdin ends with `^D^D`: the
  first `^D` delivers the partial line (icanon's delimiter — a
  mid-line `^D` is NOT EOF), the second at the now-empty line is EOF.
  Works on every build.
- Bareword heads run externals: `git status` works at a statement head.
  Builtins shadow PATH (`ls` is typed rows — files AND
  subdirectories: name, kind (`Regular | Directory | Symlink` — a
  fact, not an answer; `where (fun f -> f.kind == Directory)`),
  target (`Some` for a symlink — the one fact no `File.*` query
  answers; `None` otherwise, and the table hides an all-`None`
  column), bytes (`0 B` for a directory), modified (the last-write
  `Instant` — the file's own fact, stable under binding; the TABLE
  renders it relatively ("a week ago"), `show`/interpolation keep
  ISO), hidden, path — so
  `where (fun f -> Instant.now () - f.modified < 1h)` is the
  spelling). Narrow facts are QUERIES, not columns:
  `File.mode p` gives `Some "rw-r--r--"` (`None` on Windows — the
  platform limit stated, never invented), so the 0600 check that
  should precede `File.readSecret` is
  `File.mode p == Some "rw-------"`. The mode READ follows a symlink
  (the `File.*` rule); existence does not — a dangling link raises
  naming the dangle, agreeing with `File.stat`/`ls` about what
  exists. Rows come back SORTED by name — ordinal, like
  `Dir.list`/`Path.glob` (case-sensitive, uppercase first; never the
  locale — coreutils inherits LC_COLLATE, weir does not), and
  `Env.vars` sorts the same way. `name` is for MATCHING and display,
  `path` for handing to `File.*` — name derives from path, never the
  reverse (a later `cd` makes name→path ambiguous), which is why both
  ride the row. `^ls` forces the external. Builtins WITHOUT a
  qualified spelling (`cd`, `pwd`, `print`, `printerr`, `show`,
  `exit`, `fail`, `into`, `not`, `fst`, `snd`, `nats`) are RESERVED
  binder names [D:reserve-builtins] — a binding would shadow them for
  the whole file with no way back, so the checker refuses; bare
  aliases (`max`, `find`, `item`…) stay bindable because the
  qualified member survives. `ls` LEFT the reserved set with
  `Dir.stat` [D:dir-stat]: `Dir.stat "."` is the escape a shadow
  leaves open, so `let ls = …` is now a user preference, not an
  error.

```weir
// ls binds since Dir.stat exists — the qualified escape survives
let ls = "shadowed"
print ls
let n = Dir.stat "." |> Seq.length
print $"{n >= 0}"
```
```weir-error
// params reserve too
[1; 2] |> Seq.map (fun print -> print)
```

```weir-error
// no range indexing — accessors are offset-and-length
let xs = [1; 2; 3]
print $"{xs[1..2]}"
```

```weir-error
// weir indexes without the dot
let xs = [1; 2; 3]
print $"{xs.[0]}"
```

```weir-error
// a Map is not indexable — Map.get is the spelling
let m = Map.ofPairs [("a", 1)]
print $"{m["a"]}"
```

```weir-error
// isDirectory retired with the kind reshape — the error teaches
// the replacement, not "unknown field"
ls |> Seq.where _.isDirectory |> Seq.iter (fun f -> print f.name)
```
```weir
// a symlink carries its target; everything else says None. The link
// step's exit is DATA (ln cannot link everywhere), and relative
// paths keep the command line platform-clean
within tmp d
    within cd d
        File.write "plain.txt" ["x"]
        let linked = $(sh -c "ln -s plain.txt link 2>/dev/null" | complete)
        ls |> Seq.where (fun f -> f.kind == Symlink) |> Seq.iter (fun f -> print $"{f.name} -> {f.target |> Option.defaultValue "?"}")
        let plain = ls |> Seq.find (fun f -> f.name == "plain.txt")
        print (show plain.target)
```

```weir
["x"] |> File.write "lssort-B.txt"
["x"] |> File.write "lssort-a.txt"
ls |> Seq.where (fun f -> f.name |> Str.startsWith "lssort-") |> Seq.iter (fun f -> print f.name)
```

  (prints `lssort-B.txt` then `lssort-a.txt` — ordinal order, the
  uppercase name first.)
- Discovery: `ls` gives the cwd's rows, `Dir.stat p` a NAMED
  directory's rows (same rows, same order — ls's own enumeration; two
  names because weir is curried with no optional-parameter spelling,
  so one name cannot serve both arities), `Dir.list p` its paths
  (`seq<string>` — what `File.*` and argv want), `Path.glob` pattern
  strings. `File.stat p` is the one-path bridge — `ls`'s OWN
  row, so `Path.glob ... |> Seq.map File.stat` filters a
  glob on any row field. It describes a symlink ITSELF (`kind ==
  Symlink`, `target Some` — a dangling link is a row, not an absence),
  matching `ls` — a stated third position beside `**`'s
  skip-symlinked-dirs law and `File.*`'s follow-as-a-shell-does.
  Raises when absent, naming the resolved path — and a glob hit can
  vanish before `stat` reaches it, the same TIMING seam `Seq.force`
  documents for glob.

```weir
// the bridge: glob's strings become ls's rows — filter on any field
let d = Path.newTempDir ()
["x"] |> File.write $"{d}/a.md"
Path.glob $"{d}/*.md" |> Seq.map File.stat |> Seq.where (fun f -> Instant.now () - f.modified < 1h) |> Seq.iter (fun f -> print f.name)
Dir.deleteAll d
```

```weir-error
// 'File' alone is a module, not the row-maker — that is File.stat
Path.glob "*" |> Seq.map (File)
```
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
```weir
// the exit discipline alone, and a held lock — both release on every
// exit path (the lock even on kill -9: the kernel lets go)
within tmp d
    within lock $"{d}/demo.lock" timeout=10s
        within
            print "guarded work"
        always
            print "teardown, every path"
```
- Nonzero exit RAISES when the stream is forced. The exit-code
  reifiers (complete's family, single external segment, one law:
  output goes where the meaning goes): `cmd | succeeds` is a
  BOOL (silent — a predicate's output IS its result); `cmd | orFail
  "msg"` STREAMS and raises `msg (exit N)` on nonzero, unit on
  success — THE assert idiom, legal as a statement, in `!()`, and in
  interior lines; `cmd | exitCode` STREAMS and gives the code as INT,
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
  type. The shape NESTS — anywhere a type is written, including
  fields of `{| … |}` and of declared records
  (`{| a: {| b: int |} |}`, `{| rows: seq<{| id: string |}> |}`) —
  so a REPL session can sketch a whole foreign payload anonymously
  before declaring anything. The teaching stands: once a shape is
  YOUR data model, name it. The LITERAL writes the same shape:
  `{| key = k; n = 3 |}` builds a value typed by its canonical name —
  heterogeneous fields, unlike a `Map` — and it unifies with the
  adapter shape of the same fields. Every field needs a CONCRETE type
  at the literal (a name cannot hold a type variable — declare a
  record for the generic case). No punning (`{| key |}`), no
  `{| r with … |}`, no empty `{||}` — each refusal teaches.

```weir
let x = ["{\"a\": {\"b\": 1}}"] |> from json {| a: {| b: int |} |}
print (show x.a.b)
```

```weir
// the write-side mirror: a one-off object needs no declaration
let key = "xxxx-111"
{| key = key; n = 3 |} |> to json |> Seq.iter print
```
```weir-error
// no punning — a field is spelled out
let k = 1
let x = {| k |}
```

```weir
// a reserved word is an ordinary wire key — the field carries it
type Blob = {
    [<Wire "type">]
    kind: string
}
let b = ["{\"type\": \"user\"}"] |> from json Blob
print b.kind
b |> to json |> Seq.iter print
```
```weir-error
// bare, the keyword refuses — and the error names the attribute
type Bad = { type: string }
```
  `from json T` reads ONE DOCUMENT -> `T` (any number of lines — a
  pretty-printed body pipes straight in); `from json seq<T>` reads a
  top-level ARRAY document -> `seq<T>` (the list-endpoint shape);
  `from jsonl T` reads one document per element -> `seq<T>` (NDJSON).
  The DECLARED type decides what the top level must be — nothing
  sniffs the input. The write side mirrors it [D:to-jsonl]:
  `value |> to json` writes ONE minified document (a record is an
  object, a seq an ARRAY — building the one line forces the seq);
  `xs |> to jsonl` writes one document per element, lazily — the
  streaming form. Every adapter pairs with its own name across the
  arrow: `to json |> from json T`, `xs |> to json |> from json
  seq<T>`, `xs |> to jsonl |> from jsonl T`.
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
  `YMap` keeps YOUR key order; record fields render in DECLARATION
  order [D:record-order] (wire order for an anonymous shape).
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
- `<<<` / `$<<<` heredoc blocks [D:text-block]: line-end `<<<` opens
  the PLAIN multiline literal — every byte below the marker is
  content (`$` and `{` included), interior blank lines and deeper
  indentation survive (relative to the first line; TRAILING blanks
  clip — the block-scalar rule), and the value is
  `seq<string>`, one element per line (compose with `File.write`,
  pipes, `Seq`). `$<<<` is the interpolated twin with EXACTLY the
  string forms' hole rules: `{expr}` substitutes, `{{`/`}}` are
  literal braces, `$` STILL stays a byte (shell `$VAR` text passes
  through untouched). The markers are GLYPHS, not words — no
  identifier is reserved and `$<<<` can never read as a splice; any
  line ENDING in the glyph arms a block (no indented block below is
  an error), and nothing else may legally end in `<<<`. YAML
  `key: |` scalars stay fully literal — a `$<<<` block is the
  interpolated spelling.

```weir
let region = "eu-1"
let conf = $<<<
    endpoint {region}.example.com
    retries {2 + 1}
    literal $HOME and {{braces}}

conf |> Seq.iter print
```

- `!(…)` runs one command inline in expression position (`!(git
  pull)`). There is NO line-end `!` block — that district was retired
  [D:district-retirement]: commands are ordinary statements inside any
  block, so `if clean then` + indented `git checkout main` /
  `git pull` lines just works [D:interior-arming], and a match arm
  body takes them too [D:match-arm-commands]. `!()` is left for the
  positions bare cannot reach: a command sequenced with an expression
  on ONE line (`!(setup); print "done"` — `;` is argv inside a bare
  command line).
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
if clean then sh -c "echo acting"
```

```weir-error
// no \-escape for commands; ^ls is the force spelling
\ls
```

- Command sigils work ANYWHERE in expressions: `$(git branch)` captures
  output (`seq<string>`, pipes onward); `!(git push)` runs-and-streams
  (unit, raises on nonzero). On a top-level `let` RHS prefer the bare
  chain (`let b = git branch |> Seq.head`); sigils are for positions
  bare cannot reach — block bodies are NOT one of them:
  `if clean then` + indented bare command lines just works, no
  sigil. Interiors are ordinary command chains (splices, pipes,
  `| complete`). `!` is NOT bash history/extglob and `;` still does
  not chain inside them.
- A top-level `let` RHS takes command lines — param-ful included
  (`let f r = git rev-parse $r |> Seq.exactlyOne`): `let files = git ls-files`
  binds `seq<string>`; `let r = git status | complete` binds the
  record. Externals only — builtins stay functions there
  (`let w = cd target` applies the BINDING target). BLOCK lets inside
  bodies take the same command RHS ONLY along a top-level let's spine —
  a lambda body qualifies exactly when the lambda itself sits on that
  spine; off the spine (a statement-level `Seq.iter`, for example) a
  reifier on a block-let RHS is a teaching error, never a PATH lookup.
  The single-line `let ... in` spelling stays expression-only — there
  use `$(git status)`. A bareword `in` on a let RHS ends the command
  grammar; quote `"in"` to pass it.

```weir
let codes = [1] |> Seq.map (fun _ ->
    let r = sh -c "echo ok" | complete
    r.exitCode)
print (codes |> Seq.head)
```

```weir-error
[1] |> Seq.iter (fun _ ->
    let r = sh -c "echo x" | complete
    print r.exitCode)
```
- Tuples: `(a, b)` literals, `int * string` types, `| (x, y) ->`
  patterns (arity 2+). `Seq.pairwise : seq<'a * 'a>`, `Seq.zip`.
  Destructure ANYWHERE irrefutable: `let x, y = pair`,
  `let (k, _) = pair`, `fun (k, v) -> ...` (parens required on
  params). Refutable patterns in binders are errors — use match.
- Irrefutable RECORD patterns [D:record-patterns] destructure at every
  binding position — `let { names = n } = c`, params (bare, no parens
  needed), lambdas, match arms (the `when` guard does the testing),
  `for` binders. The literal's spelling exactly; PARTIAL field mention
  is the point. A param destructuring types by ROW: it accepts ANY
  record carrying the fields (copy-and-update's generality). Match
  scrutinees do NOT need a known record type — a match arm emits the
  same row a binder does, so `let h p = match p with | { name = n } -> n`
  types and generalises like `let g { name = n } = n`, and arms
  ACCUMULATE their fields (two arms naming different fields require
  both). CONSTRUCTOR patterns still need the nominal type: `Some` names
  a case from a closed set, which is the requirement a field name does
  not carry [D:record-pattern-rows]. Anonymous shapes destructure
  with the same spelling — there is deliberately NO `{| |}` pattern
  form, and `{ id = id }` over `{| id: string |}` is legal if
  odd-reading (fields and binders are different namespaces; punning
  does not exist). Field patterns may be REFUTABLE
  in a match (`{ state = "up" }`, `{ t = Some "x" }`, `{ items = h :: t }`)
  — the pattern is then refutable too and never completes the match, so
  a catch-all is owed exactly as for a bare literal arm; binder
  positions (`let`, params, `for`) still demand irrefutable children.
  Duplicate and unknown fields teach,
  `{ }` refuses (it binds nothing). `until`/`within` binders stay
  plain names.

```weir
// one destructuring serves ANY record carrying the field — the row
type Crew = { names: string; size: int }
type Fleet = { names: string; flag: bool }
let label { names = n } = n
let { names = c; size = k } = { names = "kestrel"; size = 3 }
print (label { names = c; size = k } + label { names = "gull"; flag = true })
for { names = n } in [{ names = "a"; size = 1 }] do print n
```

```weir
// a field pattern may be REFUTABLE — the docker-ps shape, which had no
// spelling before: filter and destructure in one arm
type Container = { State: string; Names: string }
let running =
    [{ State = "running"; Names = "api" }; { State = "exited"; Names = "old" }]
    |> Seq.choose (fun c ->
        match c with
        | { State = "running"; Names = n } -> Some n
        | _ -> None)
print (running |> Seq.force |> Seq.length)
```

```weir-error
// a refutable record pattern NEVER completes a match — the same rule a
// bare literal arm has; the catch-all is not optional
type St = { state: string }
match { state = "up" } with
| { state = "up" } -> print "x"
```

```weir-error
// no punning: fields keep their declared case, binders are lowercase
type Pn = { names: string }
let { names } = { names = "x" }
print "unreachable"
```

```weir-error
// no {| |} PATTERN form — a pattern matches a VALUE, and the shape is
// the same brace spelling whether the type was declared or anonymous
let f {| id = i |} = i
print (f 1)
```
  Bare `a, b` is a tuple at F#'s precedence (`f x, y` is `(f x), y`).
  `fst`/`snd` project PAIRS (wider tuples are a type error, as F#).
- Paths: `Path.extension` (keeps the dot; `""` when none),
  `Path.fileName`, `Path.stem`, `Path.dir` (`""` at the top),
  `Path.combine dir name` — System.IO semantics, with the two BCL
  gotchas that come with them: an ABSOLUTE second arg WINS
  (`Path.combine "/safe" "/etc/x"` = `/etc/x`, not nested), and `..`
  is NOT normalized. `Path.combine` does not confine — building a path
  from hostile data can escape a directory you imagined as a bound.
  **`Path.under base name` is the confining join**: same shape, so it
  substitutes at the call site, but it normalizes and then requires the
  result to be inside `base`, RAISING otherwise (an absolute or
  drive/UNC-shaped `name` never joins; interior `..` like `a/../b` is
  fine; the boundary is segment-wise, so `/safe/uploads-evil` is not
  under `/safe/uploads`). One line to choose between them: **combine for
  paths you control, under for paths you do not.** `under` is purely
  TEXTUAL — it confines the PATH, never the resolved target, so a
  symlink inside `base` pointing out is textually under and is NOT
  confined; following links would mean touching the disk, which makes
  the check impure, racy, and dependent on the path existing. And `File.*`/explicit paths FOLLOW
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
  Accessors are **offset-and-length**, never ranges: `Str.sub start len`
  for strings, `Seq.skip`/`Seq.take` for sequences, `xs[i]` for one
  element. `..` builds sequences and never indexes, so `xs[1..4]` is
  refused with that rule; `xs.[i]` (F#'s dotted indexer) is refused
  naming the dotless spelling [D:accessor-teaching].
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

```weir-error
// argv is weir's OWN boundary — there is no wire to match, so a flag is
// DERIVED and [<Short>]/[<NoShort>] are the naming controls
type Cli = { [<Wire "MY_FLAG">] flag: string }
let c = Args.load Cli
print c.flag
```

- Environment: `Env.get "NAME"` (Option<string>) for one var;
  `Env.load Config` for typed config — declare
  `type Config = { PORT: int; DEBUG: bool; TOKEN: Option<string> }`
  (field names = env-var names VERBATIM unless `[<Wire "NAME">]` says
  otherwise — weir never case-maps the name, since only enum VALUES are
  case-insensitive; note the WINDOWS environment block is itself
  case-insensitive, so a differently-cased name resolves there and not
  on POSIX — that difference is the platform's, not weir's; scalars, 0-arity-case enum
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
  on the tail — `files | grep -c foo | complete` applies to the (single
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
- `weir check --can script.weir` reports what a script CAN do before
  anything runs [D:can-report] — capability, not behaviour (an untaken
  branch still counts): every command it can run (heads are literal by
  the argv law, so the set is static), filesystem reads/writes (literal
  paths named; otherwise "not statically known"), network members with
  literal urls, environment reads/writes, Secret loads AND any Secret
  reaching a command's argv (the ps-visible non-claim, surfaced), and
  scoped processes. `sh -c` and the other interpreters are FIRST-CLASS
  UNKNOWNS — the report's header says "incomplete: N opaque sites" and
  `--strict` exits 2 on any, so CI chooses whether unanalysable means
  failure. `--json` for machines. The boundary, stated: the report
  covers what WEIR does; any external can itself do anything.
- `exit n` exits with code n silently (propagation:
  `if r.exitCode <> 0 then exit (r.exitCode)`); `fail "msg"` is
  the message-carrying exit-1. No GENERAL try/finally — cleanup-always
  is SCOPED: resource kinds release on every exit
  (`within tmp/cd/env/proc/lock`, raise included), and a bare
  `within` + `always` carries the exit discipline with no resource
  [D:within-always] (see the scopes section). For a fallible middle
  that is neither, make it data with `| complete`, clean up, then
  propagate.
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

## Surface inventory

The complete member table, kept honest by `ci/skill-surface.sh`: a
shipped member missing from this file fails CI, so "not in the skill
file" MEANS "does not exist". Semantics live in the sections above
and in `#help Module.member`; this list is the completeness contract,
not the teaching.

- `Args`: `flag` `load` `value`
- `Dir`: `copy` `create` `delete` `deleteAll` `exists` `list` `move` `stat`
- `Duration`: `average` `h` `m` `ms` `parse` `s` `sleep` `sum` `toMillis` `toSeconds` `tryParse`
- `Env`: `fromFile` `get` `load` `ofPairs` `pair` `vars`
- `File`: `append` `copy` `delete` `exists` `move` `read` `readBytes` `readSecret` `sha256` `size` `write` `writeBytes`
- `Float`: `abs` `average` `near` `ofInt` `parse` `round` `sum` `toInt` `tryParse`
- `Instant`: `epochMs` `now` `ofEpochMs` `parse` `parseWith` `tryParse` `tryParseWith`
- `Http`: `defaults` `delete` `fetch` `get` `head` `options` `patch` `post` `put` `query` `send` `withQuery`
- `Log`: `debug` `debugWith` `info` `infoWith` `trace` `traceWith` `warn` `warnWith`
- `Map`: `add` `count` `get` `has` `keys` `ofPairs` `pairs` `remove` `tryGet` `values`
- `Net`: `portOpen`
- `Option`: `defaultValue` `defaultWith` `iter` `map` `orElse`
- `Path`: `combine` `dir` `extension` `fileName` `glob` `newTempDir` `stem` `tempRoot` `under`
- `Poll`: `defaults`
- `Proc`: `pid` `running` `stop` `tail` `wait`
- `Retry`: `defaults`
- `Secret`: `map` `of` `reveal`
- `Seq`: `append` `average` `choose` `chunkBySize` `collect` `concat` `contains` `countBy` `distinct` `distinctBy` `except` `exactlyOne` `exists` `find` `fold` `forall` `force` `groupBy` `head` `indexed` `isEmpty` `item` `iter` `last` `length` `map` `max` `maxBy` `min` `minBy` `pairwise` `pfirst` `pfirstWith` `pick` `piter` `piterWith` `pmap` `pmapWith` `range` `reduce` `replicate` `rev` `scan` `skip` `skipWhile` `sort` `sortBy` `sortByDescending` `sortDescending` `sum` `take` `takeWhile` `tryExactlyOne` `tryFind` `tryHead` `tryItem` `tryLast` `tryPick` `where` `windowed` `zip`
- `Bytes`: `fromBase64` `length` `sha256` `toBase64` `tryFromBase64`
- `Size`: `average` `bytes` `parse` `sum` `toBytes` `tryParse`
- `Str`: `contains` `endsWith` `fromBase64` `isMatch` `join` `length` `replace` `rmatch` `rmatchAll` `sha256` `split` `splitOnce` `startsWith` `sub` `toBase64` `toInt` `toLower` `toUpper` `toUtf8` `trim` `trimEnd` `trimStart` `tryFromBase64` `tryFromUtf8` `tryIndexOf` `trySplitOnce` `tryToInt` `fromUtf8`
