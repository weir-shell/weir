# PLAN — env sugar: inline-typed `Env.load` and one-var helpers

Status: PARKED — approved in direction (user, 2026-10-13: "plan A+B, do not
execute yet"). Not scheduled.

## Problem

Reading the environment has two spellings today, and both are heavy for a
small script:

```weir
// one var: an Option, so every use pays the unwrap
let token = Env.get "TOKEN" |> Option.defaultWith (fun () -> fail "TOKEN is not set")
let port  = Env.get "PORT" |> Option.defaultValue "8080" |> Str.toInt

// typed: the right tool, but a named type for a three-line script
type Cfg = { TOKEN: string; [<Default 8080>] PORT: int; CI: Option<string> }
let cfg = Env.load Cfg
```

`from json {| a: int |}` already takes an inline anonymous type; `Env.load
{| … |}` is a parse error (`':' spells the anonymous TYPE …`). The typed path
lacks the inline form, and the one-var path lacks a required/default form.

## A — `Env.load` (and `Args.load`) take an inline anonymous type

```weir
let env = Env.load {| TOKEN: string; PORT: int; CI: Option<string> |}
curl -H $"Authorization: Bearer {env.TOKEN}" $"https://api:{env.PORT}"
```

- Same loader, same rules as `Env.load T`: field names are env names
  verbatim, the scalar/enum/Option field set, conversions, every missing or
  garbage var in ONE error, `--can` sees the literal names.
- `Args.load {| … |}` gets the same form for symmetry (flags derived from
  field names, as with a named record).
- The anonymous type is the one `from json {| … |}` already parses — no new
  type grammar.

**Parser.** `Env.load`/`Args.load` are ordinary member calls whose argument
is a type NAME in expression position (Check.fs ~3120: `EField(EVar "Env",
"load")` with an `EVar tyName` argument). The argument `{| a: T |}` is
currently refused by `anonRecordLit`'s `typeFence` (Parser.fs ~1021). Teach
the call site to accept an anonymous TYPE there — either a dedicated
argument parser for these two members, or an `EAnonType` expression node the
checker accepts only in these positions (anywhere else: the existing
`':' spells the anonymous TYPE` teaching). Keep it off the expression
recursion path ([D:parser depth budget]: measure under `ulimit -s 1024`).

**Checker.** Route the anonymous type through the same validation as a
named record (`namedRecord`/the Env field-set check), so messages match.

**Defaults — RULED (user, 2026-10-13): no attributes on anonymous types.**
F# rejects attributes on anonymous-record fields, for the same reason weir
should: an anonymous type is for ad-hoc, quick shapes. Inline, an optional
var is `Option<T>` (+ `Option.defaultValue` at the use site); `[<Default>]`,
`[<Wire "NAME">]` and every other field attribute need a named record. The
existing `{| [<Default 3>] a: int |}` parse error becomes a teaching one:
"field attributes need a named type — declare `type T = { … }`".

## B — two one-var helpers

```weir
let token = Env.need "TOKEN"          // string; raises "TOKEN is not set" at this line
let port  = Env.getOr "PORT" "8080"   // string, with a default
```

- `Env.need : string -> string` — raises a located, weir-worded error naming
  the variable when it is unset (empty-but-set returns "", as `Env.get` does
  — decide whether empty counts as unset; RECOMMEND: set-but-empty is set,
  matching `Env.get` and `[<Default>]`).
- `Env.getOr : string -> string -> string` — the default when unset.
- Plain functions, string-typed on purpose: anything typed goes through A.
- `--can` reports literal names for both, like `Env.get`.
- Both see the `within env` overlay ([D:env-overlay-read]).
- Naming check at execution time: confirm `need`/`getOr` fit the stdlib's
  existing verbs (e.g. compare with `Map`/`Option` members) before landing.

## Not doing

- **`env.NAME` member access** on a built-in value: a new magic value that
  looks like a record and isn't; optional vars have no clean spelling; A
  already gives `env.X` after one line.
- **`$HOME` in commands:** refused by design ("No `$NAME` expansion in
  commands") — `$name` splices weir values, so `$HOME` would be ambiguous.

## Work (when scheduled)

1. Ruling on empty-is-set (B). (Defaults in A: ruled — none inline.)
2. B first (small, independent): two builtins, `--can` coverage, unit tests,
   SKILL.md env bullet, reference.json regen.
3. A: parser acceptance + checker routing for `Env.load` and `Args.load`,
   error-message parity tests against the named-record form, `--can` test,
   SKILL.md env and Args bullets, depth measurement.
4. DECISIONS rows: `env-inline-type`, `env-need-getor`. CHANGELOG under the
   next version's Added.
