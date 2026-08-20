# Changelog

## v0.0.2

First release.

### New features

- The language: F#-shaped expressions and real commands in one syntax,
  the whole file typechecked — PATH lookups included — before anything
  runs.
- Typed command output: `from json` / `from jsonl` / `from yaml` into
  declared records, `Regex` match patterns for everything line-shaped.
- Typed boundaries: `Args.load` (flags with derived `--help`),
  `Env.load`, `Secret`, `Duration`, `Size`, `Instant`.
- Exit codes as data: `| succeeds`, `| complete`, `| exitCode`,
  `| orFail` — a failing command raises by default.
- Parallel fan-out (`Seq.pmap`/`piter`/`pfirst`), retry/poll with
  deadlines, scoped processes, typed HTTP and YAML.
- The tooling: REPL with completion and live coloring, `weir check
  [--json]`, `weir fmt`, an LSP, command signatures (`#sig`).
- One static AOT binary per platform (Linux/macOS/Windows, x64 and
  arm64), millisecond start, no runtime. Installers verify checksums
  before installing.

### Bugfixes

- Nothing — first release.

### Chores

- Nothing — first release.

### Checks clean, behaves differently

- Nothing — first release, no "before" to differ from. (This section
  tracks changes where a script that passed `weir check` still passes
  but does something else at runtime.)
