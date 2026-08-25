# Patterns

Where patterns bind: `match` arms, `let`, and `for` binders.
Function parameters are not pattern positions — a param is a plain
identifier, or `()`.

## Variables, and the casing law

A lowercase name in pattern position binds. An uppercase name is a
constructor. A typo'd constructor would therefore bind — and
silently match everything — so an arm made unreachable by it is a
hard error with a did-you-mean:

```weir-error
type V =
    | Pass
    | Failing
match Pass with
| pass -> print "ok" // 'pass' BINDS — did you mean 'Pass'?
| Failing -> print "no"
```

## Literals

Int, string, and bool literals match by equality:

```weir
let word =
    match 2 with
    | 0 -> "zero"
    | 2 -> "two"
    | _ -> "other"

print word
```

Literal arms never complete a match alone — close with `_` or a
variable:

```weir-error
let t =
    match 1 with
    | 0 -> "zero"
    | 1 -> "one" // literals never complete a match: add a _ or var arm
print t
```

## Wildcard

`_` matches anything and binds nothing. `_` also has two expression
shorthands — `_.field` and `_[i]` — which are lambdas, not
patterns.

## Tuples

`(a, b)` destructures a pair; arity must agree with the value's.
The binder form works in `let` too:

```weir
let host, port = ("db", 5432)
print $"{host}:{port}"
```

`fst` and `snd` project pairs only — a wider tuple is a type error:

```weir-error
print (show (fst (1, 2, 3))) // expected 'a2 * 'a3, got int * int * int
```

## Constructors

A case name matches its case; a payload binds through a nested
pattern, tuple payloads included. Patterns nest freely:

```weir
type C = { names: string }

type W =
    | Wrap of C
    | Empty

let n =
    match Wrap { names = "api" } with
    | Wrap { names = n } -> n
    | Empty -> "-"

print n
```

## Records

A record pattern names any SUBSET of fields — unnamed fields are
ignored. Fields keep their declared case; binders are lowercase:

```weir
type Container = { State: string; Names: string }

let r =
    match { State = "up"; Names = "api" } with
    | { State = "up" } -> "running"
    | _ -> "not running"

print r
```

A field may hold a literal, which makes the pattern REFUTABLE —
filter and destructure in one arm. A refutable record pattern never
completes a match alone:

```weir-error
type St = { state: string }
match { state = "up" } with
| { state = "up" } -> print "x" // a refutable record arm needs a catch-all below it
```

There is no punning — `{ names = n }`, never `{ names }`:

```weir-error
type Pn = { names: string }
let { names } = { names = "x" } // no punning: bind explicitly, { names = n }
print "unreachable"
```

And there is no `{| |}` pattern form — a pattern matches a value,
and the brace spelling is the same whether the value's type was
declared or anonymous:

```weir-error
let f {| id = i |} = i // no {| |} patterns; params are plain idents besides
print (f 1)
```

## Guards

`when` refines any arm; a guarded arm never counts toward
exhaustiveness:

```weir
let tier =
    match 42 with
    | 0 -> "empty"
    | n when n > 100 -> "huge"
    | _ -> "ordinary"

print tier
```

## The `Regex` pattern

Matches and captures in one arm. The literal must be raw (`@"..."`
or `"""..."""`), it is compiled at check time — an invalid regex is
a check error — and the binder must carry exactly as many names as
the pattern has capture groups. Groups bind as strings:

```weir
match "cache=42" with
| Regex @"(\w+)=(\d+)" (key, count) -> print $"{key} -> {count}"
| _ -> print "unparsed"
```

## `function`

The implicit-match lambda — `fun x -> match x with` in one word:

```weir
["cache=42"; "noise"]
    |> Seq.choose (function
        | Regex @"(\w+)=(\d+)" (k, v) -> Some $"{k}: {v}"
        | _ -> None)
    |> Seq.iter print
```

## Exhaustiveness

A non-exhaustive match is a hard error, not a warning. Its dual
holds too: an arm made unreachable by a catch-all above it is a
hard error. For union scrutinees, naming every case completes the
match; literal and refutable-record arms never do.
