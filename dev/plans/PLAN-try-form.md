# PLAN — `|> try`: a failure as a value, for expressions

Status: done — approved (user, 2026-10-09); shipped on feat/try-form for v0.0.70.

## Goal

Commands already default to raising and opt in to a value with a reifier
(`cmd | complete`). Expressions get the same shape: they keep raising by
default (Rust's `?`, propagation), and opt in to a value with a FORM in `|>`
position. `|` stays command-only.

```weir
let cfg = File.read "Cargo.toml" |> from toml Cargo |> try          // Option<Cargo>
match File.read p |> from toml Cargo |> try result with            // Result<Cargo, string>
| Ok c -> print c.package.name
| Error msg -> print $"bad manifest: {msg}"
```

This replaces the per-format "Option-returning reader" idea: one form makes
every builtin and every format non-raising.

## Why a form, not a function

`x |> f` evaluates `x` before `f` runs, so no function can catch a failure on
its left. `try` is a form in `|>` position — the `|> from json T` precedent —
whose one job is to evaluate its left side inside a capture. `|>` groups left
to right, so `a |> f |> g |> try` captures the whole chain, as `a | b |
complete` covers a whole pipeline.

## Semantics

- `e |> try` : `Option<'T>` — `None` if evaluating `e` raised.
- `e |> try result` : `Result<'T, string>` — `Error` carries the message.
- **Captured:** weir runtime errors — `fail`, a raising builtin, a failed
  command inside the expression (`$(…)` capture). **Not captured:** `exit`,
  Ctrl-C, and weir's own internal errors (a weir bug stays loud).
- **Laziness:** the value is fully evaluated inside the capture (sequences
  materialised, recursively), so an error cannot escape after `try` returns.
  An infinite sequence on the left never finishes — documented.
- **No rollback:** effects before the failure have happened (as with
  `| complete`).
- A bare `… |> try` statement: covered by the existing rule for discarded
  non-unit statement values, if any; otherwise a check error.
- `try` anywhere but after `|>` is a check error that teaches the form;
  `try` at a statement head keeps its teaching, updated to point here.

## Result

`Result<'T, 'E> = Ok of 'T | Error of 'E` joins the prelude (F#'s names and
shape), superseding [D:no-result] — `try result` now produces one. Members:
`Result.map`, `Result.defaultValue`, `Result.toOption`, `Result.isOk`.

Constructor collisions: a script's own union case now SHADOWS a prelude case
of the same name (the import rule, "a local declaration takes precedence"),
so an existing `type Status = Ok | Error` keeps compiling. Patterns already
resolve through the scrutinee's type. This also removes the `Move`/`Copy`
clash with `Op`.

## Work

1. Ast: `ETry(body, asResult)`; the transient `ETryForm`.
2. Parser: `try [result]` as a term; `|>` folds `x |> try…` into `ETry`;
   statement-head teaching text.
3. Check: `TETry`; types; stray-form error; prelude-shadowing in
   `ctorOwners`.
4. Eval: capture + deep force; the exclusion list.
5. Prelude `Result` + members (Builtins), docs entries.
6. Tooling: gen-lexical (`try` moves from "reserved for a helpful error" to
   "weir's own"), hover, completion.
7. Tests: unit (types, capture, exclusions, laziness, shadowing), e2e cell.
8. Docs: GUIDE, reference errors.md, SKILL, COMING-FROM (F#, Rust), fidelity
   divergences, CHANGELOG v0.0.70, DECISIONS `try-form`.

Stacked after: `id`, `Str.isEmpty`, `Str.nonEmpty`, and hover doc reflow for
clients that drop newlines (micro).
