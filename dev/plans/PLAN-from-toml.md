# PLAN — `from toml T`

Status: done — approved (user, 2026-10-08), stacked on `fix/letpat-cond-yaml-indent`, ships in v0.0.69.

Outcome: all 681 toml-test 1.1.0 files pass with no exclusions; `inf`/`nan` were
moved from parse-time refusal to read-time refusal (only a float field that reads
one fails), so the document never fails on an unread value.

## Goal

A read-only, typed TOML boundary — `File.read "Cargo.toml" |> from toml T` —
with an in-house parser: no new runtime dependency (Tomlyn was measured and
declined again: +450 KB, a second dependency, and it silently accepts duplicate
keys — see the probe in this session and the July spike in dev/NOTES.md).

## Scope

- **TOML 1.1.0**, a strict superset of 1.0 (every valid 1.0 document is valid
  1.1): multi-line inline tables with trailing commas, `\e` and `\xHH` escapes,
  optional seconds in times.
- **Datetimes are recognised, validated, and read as text.** A datetime value
  becomes a string; an offset date-time binds to `Instant` through the existing
  `[<Iso8601>]` codec. weir has no local-date/local-time types, so nothing
  further. Recognising them matters: an unrecognised value would fail the whole
  document (the yaml indentation-indicator lesson).
- **`inf` / `nan` are refused** with weir's finite-floats teaching.
- Top level is always a table: `from toml T` for a record or a tagged union.
  `seq<T>`, `stream` and `Map<string, T>` refuse with a teaching message.
- No `to toml` (a writer is a separate decision).

## Design

1. `src/Weir/Toml.fs` (after Yaml.fs): a hand-written recursive-descent
   parser over the whole text, with a depth limit (the yaml `maxDepth`
   posture) and line numbers on every node. It builds a mutable table tree
   and enforces TOML's definition rules as it goes:
   - a key is defined once; a table is defined once by a header;
   - dotted keys create tables that a header may not redefine, but a header
     may add sub-tables beneath them;
   - dotted keys may not reopen a table created by a header;
   - inline tables and static arrays are closed — nothing extends them;
   - `[[x]]` appends to an array of tables and never to a static array;
     a header path through an array of tables enters its last element.
2. The tree converts to `Yaml.Node` (table → `NMap` in source order, arrays
   and arrays of tables → `NSeq`, strings and datetimes → quoted `NScalar`,
   numbers and booleans → unquoted `NScalar` in normalised decimal text).
3. `from toml T` reuses the whole yaml binder: the checker builds the same
   `Yaml.Shape` (`TEFromToml`), the evaluator runs `yamlConvert`. Records,
   `Option`, `seq`, tagged unions, `[<Wire>]` keys, codecs and line-numbered
   errors come for free; error text says `from toml:`.
4. Wiring: adapter name in Builtins (`allAdapterNames`, builtinDocs form),
   the grammar manifest + in-repo grammars (micro, tmLanguage) + the
   generated lexical table, LSP hover.

## Tests

- **toml-test v2.2.0** (MIT), the TOML 1.1.0 file list, vendored under
  `tests/toml-test/` with its licence: every valid document must parse and
  match the expected JSON (datetime values compared as text, `inf`/`nan`
  documents listed as expected refusals); every invalid document must be
  refused. Any other exclusion is listed with a reason.
- Robustness: truncations of every valid document must return a result or an
  error, never crash or hang.
- Unit pins for the binder path (records, Option, seq, Wire, Iso8601 Instant,
  error lines) and an e2e cell reading a Cargo.toml and a pyproject.toml.

## Docs

adapters.md TOML section, SKILL (adapter list and the wire boundary),
GUIDE mention, CHANGELOG v0.0.69, DECISIONS row `from-toml`.

## Cross-repo step (user)

tree-sitter-weir hardcodes the adapter list (`grammar.js` `adapter` rule):
add `toml`, push, and bump the zed pin before tagging v0.0.69 — the release
runbook's "grammar before tag" order. The editor-currency check is scheduled,
not per-PR, so the weir PR is not blocked by it.
