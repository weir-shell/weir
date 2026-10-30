# Types

## Scalars

- `int`: 64-bit. Overflow raises instead of wrapping. A range stops at
  the type's limit, so every value it yields is correct.
- `float`: always finite. An operation that would give `NaN` or
  `Infinity` raises. `==` on floats is a check error suggesting
  `Float.near`.
- `string`: positions and lengths count Unicode code points
  (`Str.length`, `Str.sub`, slices, `Str.padLeft`, and regex
  positions, lengths and captures). `Str.width` counts terminal
  columns instead: two for a wide CJK character or an emoji, none for
  a combining mark or a colour escape.
- `bool`
- `unit`: what an effect returns, written `()`.

Nothing converts implicitly. `3 / 2` is integer division, and mixing
int and float is a type error suggesting `Float.ofInt`:

```weir-error
print $"{3 / 2.0}" // no implicit widening; use Float.ofInt 3
```

`%` is integer remainder, truncated: the sign follows the dividend.
`-7 % 3` is `-1`, as in F# and .NET (Python's floored `%` gives `2`).
Dividing by zero raises, as with `/`. Floats are refused: weir floats
are always finite, and IEEE remainder can produce NaN.

```weir
print $"{7 % 3} {-7 % 3}"
```

```weir-error
print $"{7.5 % 2.0}" // '%' is integer-only — Float.toInt one side
```

## The unit-bearing scalars

`Duration`, `Size` and `Instant` carry their unit in the value.
Durations and sizes have literals ([Lexical](lexical.md#duration-literals));
instants come from `Instant.now ()`, `Instant.parse` or
`Instant.parseWith`.

Arithmetic works within a type where it makes sense. Across types it
doesn't exist, and the errors name the conversion (`Duration.toMillis`,
`Size.parse`, `Instant.epochMs`).

## `Uuid`

A 128-bit identifier with its own type, not a string.

| make | gives |
|---|---|
| `Uuid.v7 ()` | time-ordered, strictly increasing within the process: sorts by creation as uuid and as text |
| `Uuid.v4 ()` | random |
| `Uuid.v5 ns name` | name-based, deterministic |
| `Uuid.parse s` | reads hex-and-dash, 32 bare hex digits, or a `urn:uuid:` prefix, any case; raises on anything else |
| `Uuid.tryParse s` | the same, as an `Option` |

```weir
let id = Uuid.v7 ()
print $"{id} is version {Uuid.version id}"
print $"{Uuid.v5 Uuid.ns.url "https://example.com"}"
```

Uuids support `==` and sort (`Seq.sort`, `Seq.min`) by their
big-endian bytes, which is also their text order. There is no `<`:
identifiers are not quantities. That holds through generic code too
(`let before a b = Some a < Some b` called with Uuids is a check
error).

JSON and YAML write them as the lowercase string, and argv splices
get the same text.

## `seq`

A `seq` is lazy: pipelines compute only what they need, and ranges
generate as they go. `[a; b; c]` literals are eager. Capturing a
command gives a `seq<string>`, one element per line.

### Re-reading runs it again

Iterating a bound pipeline a second time runs its effects again,
external commands included. `Seq.freeze` computes once and keeps the
values; it's the usual fix.

The checker warns (`possible re-enumeration`; `weir check` still exits
0) when a command-backed binding is read in a second place without a
freeze. Indirect reads count:

- two calls of a function that reads it
- a read inside a `for` body or a `Seq.map` lambda
- two uses of a lazy binding derived from it (`let ys = xs |> Seq.where …`)

### Lazy `let`

A `let` bound to a command, a pipeline of commands, `ls` or
`Path.glob` runs nothing when bound. Each read runs it, in the
directory and environment current at that read. Only an enclosing
`within cd`/`within env` where it was written is kept, however late
the read: `let top = ls`, then `cd "src"`, and `top` lists `src`.

These run at once instead:

- the reifiers: `| line`, `| text`, `| complete`, `| succeeds`,
  `| exitCode`, `| orFail`, `| and`/`| or`
- `Seq.freeze`, and any function that reads the whole sequence
- `$(…)` or `$@xs` in a command argument (argv is built immediately)

By context ([guide](../GUIDE.md#lazy-values-a-let-is-a-recipe)):

| context | lazy `let` |
|---|---|
| script | runs when the value is used |
| REPL | the echo shows `name : type (command-backed — runs on each use; type name to run it)` and never runs it |
| init file, modules | a command, a pipeline of commands, or a value piped into a command is allowed; eager forms and passing the command to a function are refused, since nothing may run at load |

### List literals

Items are separated by `;` or line breaks. A comma builds a tuple, so
`[1, 2]` is a one-item list holding `(1, 2)`. The checker warns about
that shape and suggests `[1; 2]`, or `[(1, 2)]` if you meant the
tuple. A list of pairs, `["a", 1; "b", 2]`, is fine.

### Indexing and slicing

- `xs[i]` is `Seq.item i xs`; it raises when out of range. The bracket
  must touch the name: `f [0]` is a function call. There is no dotted
  `xs.[i]`.
- `xs[a..b]` slices, **inclusive and clamped**: an out-of-range or
  reversed range gives an empty result, not an error. Either end can
  be open: `xs[..b]`, `xs[a..]`.
- A slice has the type of what you slice: `seq` gives `seq`, `string`
  gives `string` (`"weir"[1..2]` is `"ei"`, in code points), `Bytes`
  gives `Bytes`.
- A bounded slice stops reading a lazy source, so
  `(Seq.initInfinite id)[0..4]` terminates.
- `^n` counts from the end (length − n): `xs[^1]` is the last element,
  `xs[..^2]` all but the last, `xs[^2..]` the last two, `xs[^3..^1]`
  both bounds from the end. A `^` bound needs the length, so it reads
  the whole seq; slices from the start stay lazy.

## Tuples

`(a, b)`: two or more elements. The type is just the element types; no
declaration needed. Once the parts need names, use a record. `fst` and
`snd` work on pairs only.

## Records

Records are nominal with a fixed field set. A literal names every
field; `with` copies with some fields changed but can't add new ones:

```weir
type Score = { Name: string; Points: int }

let s = { Name = "a"; Points = 12 }
let s2 = { s with Points = 13 }
print $"{s2.Points}"
```

```weir-error
type P = { N: int }
let p = { N = 1 }
let q = { p with Extra = 2 } // record update cannot add fields
print $"{q}"
```

Two declared records with the same fields are different types.
Anonymous records like `{| ip: string |}` are the exception: same
fields, same type, whatever the order. Use them to read an external
shape once (`from json {| ip: string |}`); declare a record for your
own data.

Fields take attributes ([Lexical](lexical.md#attributes)), read at
check time with no runtime effect. A JSON/YAML key that isn't a legal
identifier goes in `[<Wire "key">]`.

### Ordering

A record sorts and compares once its fields carry `[<Ord n>]`
([Constraints](#constraints)):

- Order is by numbered fields, ascending `n`: position 1 decides,
  position 2 breaks ties. The numbers count, never declaration order.
- Unnumbered fields take no part. Two values equal on the key but
  different elsewhere are neither `<`, `>` nor `==`, and a sort keeps
  their input order.
- Each numbered field must itself order (a scalar,
  `Duration`/`Size`/`Instant`/`Uuid`, an `Option` of one, another
  numbered type), or the declaration is refused, naming the field.

```weir
type SemVer = { [<Ord 1>] major: int; [<Ord 2>] minor: int; [<Ord 3>] patch: int; build: Option<string> }

let top = [{ major = 1; minor = 0; patch = 0; build = None }; { major = 0; minor = 9; patch = 2; build = Some "x" }] |> Seq.sort |> Seq.last
print $"{top.major}.{top.minor}.{top.patch}"
print $"{top < { major = 2; minor = 0; patch = 0; build = None }}"
```

## Unions

Each case can carry a payload; several values make a tuple payload.
Write the case name to construct. An imported union needs the module
name to construct (`X.Red`), but a pattern names the case bare
(`| Red ->`):

```weir
type Verdict =
    | Pass of int
    | Fail

let v = Pass 12

match v with
| Pass n -> print $"{n}"
| Fail -> print "no"
```

`[<Ord n>]` on every case orders values by case position, then by
payload: `Pre "alpha" < Release` below. A union numbers every case or
none. A tuple payload (`C of int * string`) can't order; name the
parts in a record.

```weir
type Pre = [<Ord 1>] Pre of string | [<Ord 2>] Release

print $"{Pre "alpha" < Release}"
print $"{[Release; Pre "b"; Pre "a"] |> Seq.sort}"
```

## Validated types

A single-case union can carry an indented `validate <binder> = <expr>`
block under its declaration. The type then holds only values the
block accepted.

- The binder takes the payload. The body only checks, returning
  `unit`.
- It refuses by raising: `fail` with the message users see, or any
  other raise (a failing `Str.toInt`, an `|> orFail`), whose message
  becomes the refusal. The idiom is `if … then fail …` with no `else`.
- It can't change the value. An accepted payload is stored exactly as
  given, so a wire round trip is byte-identical and `==` compares what
  was written. Normalising is the caller's job:
  `Email.create (Str.toLower input)`.

```weir
type Email = Email of string
    /// one @, no spaces
    validate s =
        if not (Str.isMatch @"^[^@\s]+@[^@\s]+$" s) then fail $"not an email: {s}"

let (Email raw) = Email.create "Ada@Example.org"
print raw
let (Email low) = Email.create (Str.toLower "Ada@Example.org")
print low
print $"{Email.create "nope" |> try result}"
```

(prints `Ada@Example.org`, then `ada@example.org`, then
`Error "not an email: nope"`.)

- **`T.create` is the only way to build a value.** Weir derives
  `T.create : payload -> T` in the type's own name (`Email.create`;
  from another module `Mail.Email.create`, or bare `Email.create`
  while one import declares `Email`). When `validate` refuses,
  `create` raises with its message, located at the call — an ordinary
  raise, like `Uuid.parse`'s: `|> try result` turns it into
  `Error "not an email: nope"`, `|> try` into `None`
  ([errors](errors.md)). There is no `tryCreate`.
- **The case does not construct**, anywhere — the declaring module
  included, so the check has exactly one gate:

```weir-error
type Email = Email of string
    validate s = if not (Str.contains "@" s) then fail "no @"
let e = Email "x" // 'Email' is validated — build it with Email.create (it raises when validate refuses; `|> try result` for a Result)
```

- **Reading is public.** Patterns destructure the value anywhere —
  `match`, `let (Email raw) = e`, `let f (Email raw) = …`, bare or
  qualified ([Patterns](patterns.md#matching-validated-types)).
- **Single-case only.** A union of several cases composes validated
  payloads (`type Contact = Mail of Email | Call of Phone` can only be
  built from valid values) and needs no validator of its own; so does
  a record with validated fields. `validate` on a record, a
  multi-case union or a case with no payload is a check error that
  says so. A validated type is monomorphic and untagged.
- **A validated payload is already valid.** `type Work = Work of Email`
  with its own `validate` takes an `Email`, so `Work.create` expects an
  `Email` value and never re-runs `Email`'s check.
- **`validate` is pure** (the [`pure`](scopes.md) rules): no file,
  environment, command, network, clock or console touch. A type
  promises a fact about the value, and the block runs unseen wherever
  a value is built; whether a path exists is a question for the place
  that uses it (`Dir.exists`, or the failing `File.read`). Raising is
  not an effect, so `fail` is fine; `exit` is refused. It cannot call
  its own `T.create` (weir has no recursion).
- **Every boundary runs it.** `from json`/`jsonl`/`yaml`/`toml`,
  `Args.load` and `Env.load` read the payload and then validate,
  failing with the field path, flag or variable
  ([Adapters](adapters.md#validated-types-at-the-boundaries)); `to json`/`to yaml`
  write the payload. `show`, interpolation and `==` treat the value as
  any single-case union. `Args.load` and `Env.load` refuse a validated
  field over an optional payload (`Port of Option<int>`): one flag or
  variable cannot tell an absent field from an absent payload, so
  declare the field `Option<Port>` over `Port of int`.
- Hover on the type or on `T.create` shows the `///` doc above
  `validate`; an `Args.load` flag of a validated type shows the type's
  name in `--help` (`--to <Email>`).

## `Option`

`Some x` / `None`: a value that may be missing. The `try` functions
return them (`Seq.tryFind`, `Str.tryToInt`, `Bytes.tryFromBase64`).
Handle them with `match` or the `Option` module (`defaultValue`,
`map`, `orFail`).

Options order, `None` first, so they sort and can be numbered fields
of an ordered record:

```weir
print $"{[Some 2; None; Some 1] |> Seq.sort}"
```

## `Map`

`Map<K, V>` takes any key with equality: `int`, `string`, `bool`,
`Duration`, `Size`, `Instant`, `Uuid`, `Bytes`, `Secret`, and unions,
tuples and records of those. Not allowed: `float` keys, or records
holding a float (floats have no equality, see
[Constraints](#constraints)); seqs, functions and maps.

| function | |
|---|---|
| `ofPairs` | last duplicate key wins |
| `get` | raises on a missing key, naming it as `show` would |
| `tryGet`, `has`, `add`, `remove`, `count` | |
| `pairs`, `keys`, `values` | sorted by key |

There is no `m[k]` indexing, and `==` is not defined.

### Key order

The same keys always give the same order:

- scalars as `Seq.sort` sorts them
- tuples element by element
- records field by field, in field-name order
- union cases by case name (ordinal), then payload
- `Bytes` and `Secret` by content

Union keys order by case name, not declaration order as in F#: a union
value carries only its case name, and name order needs no hidden
state. So `Large` sorts before `Small` below, and `Error` before `Ok`
in a `Map<Result<int, string>, V>`.

```weir
type Tier = Small | Medium | Large

let counts = [Large; Small; Large] |> Seq.countBy id |> Map.ofPairs
counts |> Map.pairs |> Seq.iter (fun (s, n) -> print $"{s}: {n}")
print $"{Map.ofPairs [((2, "b"), 1); ((1, "z"), 2)]}"
```

### On the wire

JSON object keys are strings, so only `Map<string, V>` crosses
`to json`/`from json`. A `Map<int, V>` there is an error showing the
conversion: `Map.pairs |> Seq.map (fun (k, v) -> (show k, v)) |>
Map.ofPairs`. YAML doesn't take a `Map` yet.

```weir-error
let a = Map.ofPairs [("k", 1)]
print (show (a == a)) // '==' is not defined for Map<string, int>
```

```weir-error
let m = Map.ofPairs [(0.5, "half")] // a float key: floats have no equality
```

## `Bytes` and `Secret`

`Bytes` is binary data ([guide](../GUIDE.md#binary-data-bytes)):

- Never converted to or from text implicitly. Read and write it with
  `File.readBytes`, `File.writeBytes` and similar; wherever it would
  render as text, the error names the function to use.
- Slices like a seq: `b[1..3]`, `b[..2]`, `b[3..]`, inclusive and
  clamped, giving `Bytes`.
- No single-element `b[i]`. `Bytes.sub start len` takes a range,
  including one byte (`Bytes.sub i 1`).

`Secret` marks a credential so it's never shown by accident:

- `show` masks it; interpolation and serialization refuse it.
- `Secret.reveal` is the only way to get the text.
- Spliced into argv, it passes as-is.

## Functions

`let f x y = …` is curried; any function can be partially applied.
Bindings are generic where possible: `let id x = x` takes any type,
and function-typed parameters are inferred, so `let apply f x = f x`
is `('a -> 'b) -> 'a -> 'b`:

```weir
let apply f x = f x
print (apply (fun n -> n + 1) 1)
```

One gap: `+` on two values of unknown type can't be inferred (int or
string?). Pin one side, e.g. `x + 0`.

### Function types

You can write a function type in a union payload, a record field or a
generic argument: `Custom of (string -> bool)`,
`{ matches: string -> bool }`.

- `->` is right-associative and binds looser than `*` and generics:
  `int * string -> bool` is `(int * string) -> bool`.
- A function-typed argument needs parentheses:
  `(unit -> int) -> string`.
- You can construct the value and call the function inside it, but
  `==`, `to json`, `to yaml` and `show` reject any type containing a
  function, naming the field. A record of plain scalars works
  everywhere.

## Constraints

Equality, rendering and ordering each work through a built-in
constraint. Constraints are inferred, never written, and you can't
define your own (no user type classes).

`let same x y = x == y` works on any type with equality; any other
type is an error at the call. Functions and seqs can't be compared;
for floats the error suggests `Float.near`.

```weir
let same x y = x == y
print $"{same 1 1} {same "a" "b"}"
```

Ordering is the one you opt into for your own types. A record or
union sorts (`Seq.sort`, `sortBy` keys, `min`/`max`) and compares
(`<`, `>`, `<=`, `>=`) only by `[<Ord n>]` positions on its fields or
cases. The order is written down, never read off the declaration.

What orders, both for `Seq.sort` and the operators:

- scalars: `int`, `float`, `string` (ordinal), `bool`, `Duration`,
  `Size`, `Instant`
- `Option` of them
- numbered types

Tuples don't. Name the parts in a record and number them with
`[<Ord n>]`:

```weir
print ("a" < "b")
print (None < Some "x")
```

```weir-error
[(1, "b"); (2, "a")] |> Seq.sort |> Seq.iter (fun p -> print (show p)) // tuples have no order
```
