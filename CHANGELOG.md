# Changelog

## v0.0.7

### New features

- **`<<<` / `$<<<` — the heredoc block.** A line-end `<<<` marker
  opens a plain multiline literal: every byte below is content
  (`$` and `{` included), blank lines and relative indentation
  survive, and the value is `seq<string>` — one element per line,
  ready for `File.write`, a pipe, or the `Seq` module. `$<<<` is
  the interpolated twin with exactly the string forms' hole rules:
  `{expr}` substitutes, `{{`/`}}` are literal braces, `$` still
  stays a byte. The markers are glyphs, so no identifier is
  reserved and the interpolated form can never read as a splice;
  the arming law is yaml's, and the errors teach.

### Bugfixes

- A parse error inside a yaml `$(…)` splice or a `$<<<` hole now
  reports its message and exact column instead of an empty error
  (the extraction landed on FParsec's "Other error messages:"
  trailer).

### Editors

- Heredoc highlighting everywhere the grammars reach: VS Code
  (Marketplace/Open VSX) and micro ship rules with this release;
  the tree-sitter grammar gained the heredoc district (byte-verbatim
  body lines, whole-`{expr}` hole tokens) and the Zed extension pins
  it.

## v0.0.6

### New features

- **`weir add module` — remote modules, vendored.** Share code
  across repos as a fetch, not a package manager: no registry, no
  resolver, no version ranges.
  `weir add module github.com/org/repo//lib/x.weir@v1.2.0 --as x`
  resolves the ref to a full commit sha, fetches, validates (the
  file must be a `module`, must typecheck, and must not `import` —
  vendored modules are leaves for now), and vendors it into
  `.weir/modules/` with a content-hashed lock entry. The `//`
  separates repo from in-repo path; an explicit `@ref` is required;
  the shorthand knows github.com and gitlab.com, and any host takes
  the full raw URL. Import from anywhere under the project with
  `import "weir:x" as X` — a new, distinct spelling: both existing
  import forms resolve exactly as before. A re-add updates and
  prints the old and new sha. Private repos: set
  `WEIR_TOKEN_GITHUB_COM` / `WEIR_TOKEN_GITLAB_COM` (needed only at
  add/restore — the committed file needs neither). And
  `check --can` reports a vendored module's commands, writes and
  network access in your own report, at the module's `file:line`.

### Bugfixes

- A REPL init `let` whose evaluation raises (`File.read` on a
  missing path) no longer crashes the REPL with a raw .NET stack
  trace — it reports the located error, prints `init: NOT loaded`,
  and the session starts with none of the init's names.
- `weir restore` now repairs a present-but-MODIFIED vendored
  artifact by refetching it (schemas and modules alike) — the lock
  is the intent. Previously it only materialized absent files,
  leaving local drift in place; a deliberate local edit is a
  re-add, not an edit-in-place.

### Chores

- `.weir/lock.json` now carries `"schemaVersion": 1`. Locks without
  the field read as version 1; a lock newer than the binary
  understands is refused with an upgrade teach.

### Checks clean, behaves differently

- Nothing — the two import spellings that existed keep their exact
  resolution (pinned); `weir:` is new surface only.

## v0.0.5

### New features

- **The REPL init file.** `init.weir` beside the REPL config
  (`$XDG_CONFIG_HOME/weir/`, `%APPDATA%\weir\` on Windows) loads
  before the first prompt: declaration-only `type`/`let` bindings for
  the prompt (aliases are functions — `let pu () = git push …`), plus
  one `#session` directive for four settings: `cwd`, `env`
  (`seq<string * string>`, set into the process environment once —
  visible to `Env.vars`, every spawn, and layered under `within
  env`/sigils), `logLevel` (the `WEIR_LOG` levels, same parsing), and
  `echoCap`. Loading is all-or-nothing: a broken init prints its
  located error and the session starts with none of it — safe because
  nothing in the file can run. `#help` on an init name shows its
  `///` doc. A missing init is silent.
- `weir --help` now lists `add sig`, `check --can`, and `--version` —
  three arms the usage string had silently omitted.

### Bugfixes

- `weir docs-json` emits LF on every platform (its dump is a
  generated, diffed artifact; Windows emitted CRLF).

### Chores

- `#session` outside its home teaches: in a script it names the init
  file; typed at the prompt it says edit-and-restart.

### Checks clean, behaves differently

- Nothing — scripts are untouched; the init file is REPL-only.

## v0.0.4

### New features

- Nothing.

### Bugfixes

- **Breaking:** adjacent argv pieces no longer silently split into
  separate arguments — they are refused at check time. Previously,
  `rm -rf $root/*` ran as *two* arguments (`$root`, then `/*` — the
  Steam-bug shape, reproduced), and `--flag="value"` passed `--flag=`
  and `value` separately. Both now fail with an error naming the
  repairs (an interpolated arg, quoting the whole word, or
  `Path.under`). Scripts that relied on glued adjacency were already
  silently wrong; they now fail loudly instead.
- Error messages that suggest building a filesystem path now name
  `Path.under` (which refuses a result that escapes its base) instead
  of `Path.combine` (which follows it).

### Chores

- Nothing.

### Checks clean, behaves differently

- Nothing — the argv change above is the inverse: scripts that
  previously checked clean may now be refused, loudly.

## v0.0.3

### New features

- Nothing.

### Bugfixes

- The installer served at weir.sh no longer prints six harmless
  `not found` errors before downloading — a generator defect planted a
  stray copy of the release checksums at the top of the script. The
  install itself was never affected; it was noise.

### Chores

- The site's post-release check waits out edge propagation instead of
  failing on a seconds-old deploy.

### Checks clean, behaves differently

- Nothing.

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
