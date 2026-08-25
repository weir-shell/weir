# Types

## Scalars

`int` (64-bit; arithmetic overflow raises rather than wrapping,
while a range TERMINATES at the type boundary — every yielded value
is correct), `float`
(always finite — a would-be `NaN` or `Infinity` raises; `==` on
floats is a check error naming `Float.near`), `string`, `bool`,
and `unit` — the value of an effect, written `()`.

Nothing widens implicitly: `3 / 2` is integer division, and mixing
sides is a type error naming `Float.ofInt`:

```weir-error
print $"{3 / 2.0}" // no implicit widening; use Float.ofInt 3
```

## The unit-bearing scalars

`Duration`, `Size`, and `Instant` put the unit in the value.
Durations and sizes have literals ([Lexical](lexical.md#duration-literals));
instants enter through `Instant.parse` only. Same-type arithmetic
works where it means something; cross-type arithmetic does not
exist, and the errors name the explicit conversions
(`Duration.toMillis`, `Size.parse`, `Instant.epochMs`).

## `seq`

Lazy. Pipelines pull what they need; ranges are lazy generators;
`[a; b; c]` literals are eager values. Re-enumerating a bound
pipeline RE-RUNS its effects — external commands included;
`Seq.force` materializes once and is the standard escape. Command
capture is `seq<string>`, one element per line.

## Tuples

`(a, b)` — arity two and up, structural. The moment a shape needs
names, declare a record. `fst`/`snd` project pairs only.

## Records

Nominal, with exact field sets — a literal names every field, and
update syntax derives without adding:

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
Anonymous record types — `{| ip: string |}` — are the exception:
structural, field-order canonicalized, for reading a foreign shape
once at a boundary (`from json {| ip: string |}`); your own data
declares a record. Fields take check-time attributes
([Lexical](lexical.md#attributes)), fully erased at runtime; a
field whose wire key is not a legal identifier names it with
`[<Wire "key">]`.

## Unions

Cases carry optional payloads; multi-value payloads are tuples.
Construction is the case name; an imported union's cases stay
qualified:

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

`Some x` / `None` — absence as a value. Produced by the `try`
variants (`Seq.tryFind`, `Str.tryToInt`, `Bytes.tryFromBase64`),
consumed by `match` or the `Option` module
(`defaultValue`, `map`, `orFail`).

## `Map`

`Map<string, T>` — keys are DATA, and JSON object keys are
strings, so string keys only. `ofPairs` (last key wins), `get`
(raises, naming the key), `tryGet`, `has`, `pairs`/`keys`/`values`
(key-sorted). No `m[k]` indexing, and `==` is not defined:

```weir-error
let a = Map.ofPairs [("k", 1)]
print (show (a == a)) // '==' is not defined for Map<string, int>
```

## `Bytes` and `Secret`

`Bytes` is the non-text value — opt-in at both ends, refused at
every rendering boundary with the exit named
([the guide](../GUIDE.md#binary-data-bytes)). `Secret` is the
rendering marker for credentials: `show` masks, interpolation and
the wire refuse, `Secret.reveal` is the one exit, argv splices pass
it whole.

## Functions

`let f x y = …` is curried; partial application is first-class.
Bindings generalize — a polymorphic `let id x = x` stays
polymorphic. Two deliberate limits: a bare parameter cannot be
APPLIED as a function, and `+` on two unknowns cannot infer — anchor
one side:

```weir-error
let apply f x = f x // a bare parameter cannot be applied as a function
print (apply (fun n -> n) 1)
```

## Constraints

Equality, rendering, and ordering flow through three built-in
constraint families — inferred, never annotated, and CLOSED: no
user type classes. A helper like `let same x y = x == y` works on
any type in the family and rejects at the use site otherwise
(functions and seqs do not compare; floats teach `Float.near`).

```weir
let same x y = x == y
print $"{same 1 1} {same "a" "b"}"
```
