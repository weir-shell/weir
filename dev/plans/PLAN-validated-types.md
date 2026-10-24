# PLAN: validated types — a type that can only hold valid values

**Status: RULED** (user, 2026-10-24) — the shape below; open items at the end.

## The receipt

Single-case unions stop stringly-typed mix-ups (`Topic` vs a message
body), but anyone can write `Contract.Topic "../../etc"`. The user wants
the F# "private constructor + smart constructor" pattern as a first-class
feature, without F#'s cost: there a `private` case also hides the case
from pattern matching outside the module, so reading the value back needs
an accessor or an active pattern.

## The shape

```weir
module Mail

type Email = Email of string
    validate s =
        if Str.isMatch @"^[^@\s]+@[^@\s]+$" s then Ok (Str.toLower s)
        else Error $"not an email: {s}"

type Domain = Domain of string
    validate s =
        if Str.contains "." s then Ok s else Error $"not a domain: {s}"
```

- **`validate` belongs to the type declaration** — an indented block
  under a single-case union: `validate <binder> = <expr>` with
  `<expr> : Result<payload, string>`. It may normalise (the lowercased
  email) as well as accept or reject; the error text is what users see.
- **`validate` implies the constructor is not available for
  construction** — no `private` keyword. A constructor returns `T`, not
  a `Result`, so it cannot report failure; the only way to make a value
  is the derived `parse`. This holds inside the declaring module too:
  the invariant has exactly one gate.
- **Weir derives `T.parse : payload -> Result<T, string>`** in the
  type's own namespace — the built-ins' shape (`Duration.parse`,
  `Uuid.parse`, `Size.parse`). Two types in one module get `Email.parse`
  and `Domain.parse`; from another module `Mail.Email.parse`, or bare
  `Email.parse` when unambiguous ([D:qualified-type-names]).
- **No `T.create`** (raising). Code that knows a value is good says so
  visibly, with a `match` on the `Result`.
- **Reading is public**: `match e with | Email raw -> …`,
  `let (Email raw) = e`, `let f (Email raw) = …` work anywhere
  (the qualified spelling `Mail.Email raw` too, per fix/pattern-cases).
- **Single-case only.** A multi-case union composes validated payloads
  and needs no validator of its own: `type Contact = Mail of Email |
  Phone of PhoneNumber` can only be built from already-valid values.
  The same holds for records with validated fields.

## Wire boundaries — the reason it is a language feature

Every place weir builds a value from outside runs `validate`, so the
guarantee cannot leak:

- `from json`/`from yaml`/`from jsonl` (fields, whole-document slots,
  anonymous shapes): an `Error` is the adapter's located error with the
  field path ("users[2].email: not an email: x").
- `Args.load`: a usage error (fix/args-help's usage-error form) —
  "journal.weir: --to: not an email: x", exit 2.
- `Env.load`: the env-load error naming the variable.
- `to json`/`to yaml` write the payload (the value is already valid).
- argv splicing, interpolation and `show` render the payload, as for any
  single-case union today.
- Completion: a field whose TYPE name says path/file/dir
  (`ExistingDir`, `ConfigFile`) gets path completion, extending the
  name/doc heuristic ([D:sig-arg-complete]) — this replaces the
  proposed `[<Path>]` attribute (user: superseded by validate).

## Checker rules

- `validate` only on a single-case union with a payload; elsewhere a
  teaching error.
- `validate`'s binder has the payload type; its body must be
  `Result<payload, string>` — a mismatch names both.
- Using the case as a constructor (`Email "x"`) anywhere is an error:
  "Email is validated — build it with Email.parse (it returns a Result)".
- `validate` is pure: commands, file writes and network inside it are
  refused (the `pure` rules), because it runs at every wire boundary,
  including `weir check`-time folding of literals if that ever applies.
  Reads (`Dir.exists`, `File.exists`) — D2 below.
- A recursive or mutually validated payload (a validated type whose
  payload is another validated type) runs the inner `parse` first.

## Open items (to rule)

- **D1 — the validator's name.** `validate` (recommended) vs `check` /
  `where`.
- **D2 — ambient reads in `validate`.** `ExistingDir`'s validator reads
  the filesystem. Allow reads (`readonly` rules) so path types work, at
  the cost of `parse` no longer being pure; or require pure and leave
  existence checks to the caller. *Recommend: allow reads* — the
  `[<Path>]` use case depends on it, and `parse` already returns a
  `Result`.
- **D3 — literal folding.** Should `Email.parse "a@b.c"` on a literal be
  checked at `weir check` time (a pure validator over a constant)?
  *Recommend: not in v1.*
- **D4 — hover and `--help`.** Hover shows the validator's first doc
  line; `--help` shows a flag's validated type name. *Recommend: yes,
  both.*

## Tests

Unit: declaration rules, the constructor refusal (inside and outside the
module), `parse` results, normalisation, reading via patterns, every wire
boundary's error text, composition (multi-case of validated payloads,
records), qualified access from another module. e2e: an Args flag and a
JSON field rejecting a bad value with the right exit code and message.

## Docs

docs/reference/types.md (a "Validated types" section), modules.md,
patterns.md (reading), adapters.md (wire behaviour), SKILL.md, CHANGELOG,
FAQ ("how do I make a type that only holds valid values?"), DECISIONS
row `[D:validated-types]`.
