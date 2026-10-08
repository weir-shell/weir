# Types

## Scalars

- `int` — 64-bit. Arithmetic overflow raises an error rather than
  wrapping around, while a range simply stops at the type's limit, so
  every value it yields is correct
- `float` — always finite: an operation that would produce `NaN` or
  `Infinity` raises an error, and `==` on floats is a check error that
  suggests `Float.near`
- `string`
- `bool`
- `unit` — the value an effect returns, written `()`

Nothing converts implicitly: `3 / 2` is integer division, and mixing
int and float is a type error that suggests `Float.ofInt`:

```weir-error
print $"{3 / 2.0}" // no implicit widening; use Float.ofInt 3
```

`%` is integer remainder, truncated, so the sign follows the dividend
(`-7 % 3` is `-1`, as in F# and .NET; Python's floored `%` gives `2`).
Dividing by zero raises an error, as with `/`. Floats are refused,
because weir floats are always finite and IEEE remainder can produce
NaN:

```weir
print $"{7 % 3} {-7 % 3}"
```

```weir-error
print $"{7.5 % 2.0}" // '%' is integer-only — Float.toInt one side
```

## The unit-bearing scalars

`Duration`, `Size`, and `Instant` carry their unit in the value.
Durations and sizes have literals ([Lexical](lexical.md#duration-literals));
instants come from `Instant.now ()`, `Instant.parse` or `Instant.parseWith`. Arithmetic between values of
the same type works where it makes sense. Arithmetic across types
doesn't exist, and the errors name the explicit conversions
(`Duration.toMillis`, `Size.parse`, `Instant.epochMs`).

## `Uuid`

A 128-bit identifier with its own type, not just a string. `Uuid.v7`
is time-ordered and strictly increasing within the process, so v7 ids
sort by creation both as uuids and as their text; `Uuid.v4` is random;
`Uuid.v5` is name-based and deterministic. `Uuid.parse` reads the
hex-and-dash form, 32 bare hex digits, or a `urn:uuid:` prefix, in any
case, and raises on anything else; `Uuid.tryParse` returns an `Option`.

```weir
let id = Uuid.v7 ()
print $"{id} is version {Uuid.version id}"
print $"{Uuid.v5 Uuid.ns.url "https://example.com"}"
```

Uuids compare for equality and sort (`Seq.sort`, `Seq.min`), by their
big-endian bytes, which is the same order as their text. There is no
`<`, since identifiers are not quantities. In JSON and YAML they are
written as the lowercase string, and they splice into a command's argv
as that same text.

## `seq`

A `seq` is lazy. Pipelines compute only what they need, and ranges
are generated lazily, while `[a; b; c]` literals are ordinary eager
values. Iterating a bound pipeline a second time runs its effects
again, including external commands. `Seq.freeze` computes the values
once and keeps them, and is the usual fix. The checker warns when a
binding backed by a command is iterated in a second place without
being frozen (`possible re-enumeration`; this is only a warning, and
`weir check` still exits 0). Capturing a command gives a
`seq<string>`, one element per line.

List items are separated by `;` (or by line breaks). A comma builds a
tuple, so `[1, 2]` is a one-item list holding `(1, 2)`; the checker
warns about that shape and suggests `[1; 2]`, or `[(1, 2)]` if the
tuple was meant. A list of several pairs, `["a", 1; "b", 2]`, is fine.

`xs[i]` is `Seq.item i xs` and raises an error when out of range. The
bracket must directly follow the name, because `f [0]` is a function
call. `xs[a..b]` slices, **inclusive and clamped** to the available
range (an out-of-range or reversed range gives an empty result rather
than an error), and either end can be left open: `xs[..b]` / `xs[a..]`.
A bounded slice stops reading a lazy source, so `nats[0..4]`
terminates. A slice has the type of what you slice: a `seq` gives a
`seq`, a `string` a `string` (`"weir"[1..2]` is `"ei"`, counted in
UTF-16 chars), and `Bytes` gives `Bytes`. `^n` counts from the end
(`^n` = length − n): `xs[^1]` is the last element, `xs[..^2]` all but
the last, `xs[^2..]` the last two, `xs[^3..^1]` uses the end for both
bounds. A `^` bound needs the length, so slicing a seq from the end
reads the whole sequence; slices that count from the start stay lazy.
There is no dotted `xs.[i]`.

## Tuples

`(a, b)` — two or more elements. A tuple type is just its element types; it needs no declaration. As soon as
the parts need names, declare a record instead. `fst`/`snd` work on
pairs only.

## Records

Records are nominal and have a fixed set of fields: a record literal
must name every field, and `with` copies a record with some fields
changed but cannot add new ones:

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
Anonymous record types like `{| ip: string |}` are the exception: two
with the same fields are the same type, whatever the field order.
They are meant for reading an external shape once
(`from json {| ip: string |}`); for your own data, declare a record.
Fields can take attributes ([Lexical](lexical.md#attributes)), which
are read at check time and have no effect at runtime. A field whose
JSON/YAML key is not a legal identifier gives the key with
`[<Wire "key">]`.

## Unions

Each case can carry a payload; a payload of several values is a
tuple. You construct a value by writing the case name. For an
imported union, constructing a value needs the module name (`X.Red`),
but a pattern names the case bare (`| Red ->`):

```weir
type Verdict =
    | Pass of int
    | Fail

let v = Pass 12

match v with
| Pass n -> print $"{n}"
| Fail -> print "no"
```

## `Option`

`Some x` / `None` represent a value that may be missing. The `try`
functions return them (`Seq.tryFind`, `Str.tryToInt`,
`Bytes.tryFromBase64`), and you handle them with `match` or the
`Option` module (`defaultValue`, `map`, `orFail`).

## `Map`

`Map<string, T>` only allows string keys, matching JSON object keys.
The functions are `ofPairs` (the last duplicate key wins), `get`
(raises an error naming a missing key), `tryGet`, `has`, and
`pairs`/`keys`/`values` (sorted by key). There is no `m[k]` indexing,
and `==` is not defined:

```weir-error
let a = Map.ofPairs [("k", 1)]
print (show (a == a)) // '==' is not defined for Map<string, int>
```

## `Bytes` and `Secret`

`Bytes` holds binary data. It is never converted to or from text
implicitly — you read and write it with functions like
`File.readBytes` and `File.writeBytes` — and anywhere it would be rendered as text the error
names the function to use instead
([the guide](../GUIDE.md#binary-data-bytes)). It slices like a
sequence — `b[1..3]`, `b[..2]`, `b[3..]`, inclusive and clamped,
giving `Bytes` — but has no single-element `b[i]`; use
`Bytes.sub start len` to take a range, including a single byte
(`Bytes.sub i 1`). `Secret` marks a
credential so it is never shown by accident: `show` masks it,
interpolation and serialization refuse it, `Secret.reveal` is the
only way to get the text out, and splicing it into argv passes it
as-is.

## Functions

`let f x y = …` is curried, and you can partially apply any function.
Bindings are generic where possible: `let id x = x` works on any type,
and a function-typed parameter is inferred too, so
`let apply f x = f x` has type `('a -> 'b) -> 'a -> 'b` and can be
called like any function:

```weir
let apply f x = f x
print (apply (fun n -> n + 1) 1)
```

The one place inference falls short: `+` on two values of unknown
type can't be inferred (int or string?), so pin one side down, for
example with `x + 0`.

You can also **write** a function type — in a union payload, a record
field, or a generic argument: `Custom of (string -> bool)`,
`{ matches: string -> bool }`. `->` is right-associative and binds
more loosely than `*` and generics (`int * string -> bool` is
`(int * string) -> bool`); a function-typed argument needs
parentheses (`(unit -> int) -> string`). You can construct such a
value and call the function inside it, but `==`, `to json`, `to yaml`
and `show` reject any type that contains a function anywhere, and the
error names the field. So whether a record can be serialized depends
on its fields: a record of plain scalars works everywhere.

## Constraints

Equality, rendering and ordering each work through a built-in
constraint. Constraints are inferred, never written out, and you
cannot define your own (there are no user type classes). A helper
like `let same x y = x == y` works on any type that supports
equality, and using it with any other type is an error where it is
called. Functions and seqs can't be compared, and for floats the
error suggests `Float.near`.

```weir
let same x y = x == y
print $"{same 1 1} {same "a" "b"}"
```
