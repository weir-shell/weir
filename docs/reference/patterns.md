# Patterns

Patterns appear in `match` arms (the `| pattern -> …` lines), `let`,
`for`, and function parameters. A parameter is a plain identifier,
`()`, a parenthesized tuple pattern (`let dist (x, y) = …`), or a
record pattern (without parentheses: `let label { names = n } = n`).
In these binding positions the pattern must always succeed; a pattern
that can fail (`Some x`, a literal) is rejected there — use `match`
or `function` instead.

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
Each alternative behaves as its own arm with the same guard and body,
so a union's alternatives count toward exhaustiveness. An alternative
cannot bind a name yet — `| (n, 0) | (0, n) -> n` is an error; write one
arm per alternative. An arm takes at most 64 alternatives; for a longer
list, test membership in a guard: `| x when Seq.contains x values -> …`.

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

A match that doesn't cover every case is an error, not a warning.
Likewise, an arm that can never be reached because a catch-all comes
before it is an error. When matching on a union, listing every case
makes the match complete; literal arms and record arms that can fail
never do.
