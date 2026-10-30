# Patterns

Patterns appear in `match` arms (`| pattern -> …`), `let`, `for`, and
function parameters. A parameter is one of:

- a plain identifier
- `()`
- a tuple pattern in parentheses: `let dist (x, y) = …`
- a record pattern, no parentheses: `let label { names = n } = n`

Outside `match`, a pattern must always succeed. One that can fail
(`Some x`, a literal) is rejected; use `match` or `function`. A
single-case union's constructor always succeeds (`let (Tag t) = tag`),
declared here or imported.

```weir
type Crew = { names: string }
let label { names = n } = n
let swap (a, b) = (b, a)
print (label { names = "kestrel" })
print $"{swap (1, 2)}"
```

## Variables and constructors: lowercase vs uppercase

A lowercase name in a pattern binds a variable. An uppercase name is
a constructor. A mistyped constructor name would therefore bind a
variable and silently match everything, so an arm that becomes
unreachable because of it is an error with a did-you-mean:

```weir-error
type V =
    | Pass
    | Failing
match Pass with
| pass -> print "ok" // 'pass' binds — did you mean 'Pass'?
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

Literal arms alone never make a match complete; end with `_` or a
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

## Tuple patterns

`(a, b)` destructures a pair; the pattern must have as many elements
as the tuple. It works in `let` too:

```weir
let host, port = ("db", 5432)
print $"{host}:{port}"
```

`fst` and `snd` work on pairs only; a longer tuple is a type error:

```weir-error
print (show (fst (1, 2, 3))) // expected 'a2 * 'a3, got int * int * int
```

## Constructors

A case name matches that case, and a nested pattern destructures its
payload, including tuple payloads. Patterns can be nested to any
depth:

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

### Qualified cases

A case of an imported union can be written `A.Case` in any pattern:
match arm, `let`, parameter, lambda, or nested.

- The qualifier is the import's name (its `as` alias, else the
  module's name), the same one you construct with.
- It's checked against the value's type: another import's case is a
  type error naming both.
- Bare cases still work. When two imports declare the same case name
  and the value's type doesn't decide, a bare case is an error listing
  both qualified spellings.
- Cases of a union declared in the same file are written bare.

```weir
["module Shapes"; "type Shape = Circle of int | Square of int"; "type Tag = Tag of string"]
    |> File.write "shapes.weir"
let use = <<<
    import "./shapes.weir"

    let area s =
        match s with
        | Shapes.Circle r -> r * 3
        | Square w -> w * w
    let name (Shapes.Tag t) = t
    print $"{area (Shapes.Circle 2)} {area (Shapes.Square 3)} {name (Shapes.Tag "x")}"
use |> File.write "use-shapes.weir"
weir use-shapes.weir
```

### Matching validated types

A [validated type](types.md#validated-types)'s case never constructs
— `T.create` is the one way in — but it reads like any single-case
union: a pattern destructures it anywhere, bare or qualified, in the
declaring module and outside it. Binder positions take it too, since
one case cannot fail to match.

```weir
["module Mail"; "type Email = Email of string"; "    validate s = if not (Str.contains \"@\" s) then fail \"no @\""]
    |> File.write "mail-v.weir"
let use = <<<
    import "./mail-v.weir"

    let user (Mail.Email raw) = raw |> Str.split "@" |> Seq.head
    let e = Mail.Email.create "ada@example.org"
    let (Email whole) = e
    print $"{user e} {whole}"
use |> File.write "use-mail-v.weir"
weir use-mail-v.weir
```

## Record patterns

A record pattern can name any subset of fields; the rest are
ignored. Field names keep their declared capitalization, while the
names you bind are lowercase:

```weir
type Container = { State: string; Names: string }

let r =
    match { State = "up"; Names = "api" } with
    | { State = "up" } -> "running"
    | _ -> "not running"

print r
```

A field may hold a literal, so one arm can both filter and
destructure. Such a pattern can fail to match, so it never makes a
match complete on its own:

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

There is also no `{| |}` pattern. Plain braces destructure a record
whether its type was declared or anonymous:

```weir-error
let f {| id = i |} = i // no {| |} patterns — the plain brace spelling destructures anonymous shapes too
print (f 1)
```

## Guards

Alternatives share one arm: `| "a" | "b" -> …`, `| Debug | Info -> …`.
Each acts as its own arm with the same guard and body, so they count
toward exhaustiveness.

- Alternatives can't bind names yet: `| (n, 0) | (0, n) -> n` is an
  error. Write one arm each.
- At most 64 per arm. For longer lists, use a guard:
  `| x when Seq.contains x values -> …`.

```weir
type Level = Debug | Info | Warn | Error
let loud l =
    match l with
    | Debug | Info -> false
    | Warn | Error -> true
print $"{loud Info} {loud Error}"
```

`when` adds a condition to any arm. An arm with a `when` guard never
counts toward exhaustiveness:

```weir
let tier =
    match 42 with
    | 0 -> "empty"
    | n when n > 100 -> "huge"
    | _ -> "ordinary"

print tier
```

## The `Regex` pattern

Matches a regex and binds its captures in one arm. The regex must be
a raw string (`@"..."` or `"""..."""`). It is compiled at check time,
so an invalid regex is a check error, and the tuple after it must
have exactly as many names as the regex has capture groups. Groups
are bound as strings:

```weir
match "cache=42" with
| Regex @"(\w+)=(\d+)" (key, count) -> print $"{key} -> {count}"
| _ -> print "unparsed"
```

## `function`

`function` is a lambda that matches on its argument — short for
`fun x -> match x with`:

```weir
["cache=42"; "noise"]
    |> Seq.choose (function
        | Regex @"(\w+)=(\d+)" (k, v) -> Some $"{k}: {v}"
        | _ -> None)
    |> Seq.iter print
```

## Exhaustiveness

Both of these are errors, not warnings:

- a match that doesn't cover every case
- an arm that can't be reached because a catch-all comes before it

Listing every case of a union completes a match. Literal arms and
record arms that can fail never do.

A tuple is complete when its arms cover every combination:
`(true, _)` and `(false, _)`, or all four `Some`/`None` pairs. The
error names one missing combination (`missing: (None, Some _)`).

```weir
let both a b =
    match (a, b) with
    | Some x, Some y -> x + y
    | Some x, None -> x
    | None, Some y -> y
    | None, None -> 0
print (both (Some 1) None)
```
