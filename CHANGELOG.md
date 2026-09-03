# Changelog

## v0.0.15

### New features

- **Anonymous record literals.** `{| key = key; n = 3 |}` builds a
  value with no declaration — the write-side mirror of the anonymous
  TYPE the adapter slot already takes. Heterogeneous fields (where
  `Map.ofPairs` forces one value type), typed by the same canonical
  shape name, so a literal unifies with `from json {| … |}`'s result
  and a declared record with the same fields stays a different type.
  `[{| key = k; value = v |}] |> to json |> File.write "x.json"`
  works in one line; multi-line literals assemble; hover shows the
  canonical shape and dot-completion offers the fields. Fields must
  have concrete types at the literal (the name IS the type); no
  punning, no empty `{||}`, no `{| r with … |}` — each refusal
  teaches its repair.

### Docs

- Redundant `!()` sigils swept from examples — a command is an
  ordinary statement inside any block body, so `if ready then` +
  indented bare command lines is the modeled idiom; the sigil
  appears only where an expression position demands it, and the
  one stale "block effect idiom" sentence now says so.

## v0.0.14

### Bugfixes

- The subcommand walk learns all-caps command banners — jira's
  `MAIN COMMANDS` / `OTHER COMMANDS` carry no trailing colon, so
  v0.0.13's detector saw no subcommands to walk and jira sigs
  stayed at the four global flags. And the walk's outcome is now
  observable either way: a help-sourced sig that probed subcommands
  without profit says so —
  `source: help (walked 12 subcommand help(s), 0 answered, none yielded flags)`.

- Bare-word probes are gated on advertisement — `weir add sig code`
  ran `code completion fish`, which OPENED VS Code on two files.
  `completion fish` and the `version` word now run only when the
  tool's own `--help` advertises that subcommand; flag probes
  (`--version`, `--help`) remain universal.

- The recorded version identity is `--version`'s FIRST line,
  whitespace-collapsed — az's multi-page environment report (with
  machine paths) is not an identity. And az's help dialect parses:
  `--flag --alias -s [Required] : doc` rows record the postfix
  short, each alias as its own flag, and a clean description.

- A path-y tool (`weir add sig ./lib/jp`, an absolute path) mints a
  legal module name and ONE flat sig file under `.weir/sigs/`
  (separators become `_`) — the absolute case had aimed the write
  outside `.weir` entirely, and a leading `/` or `.` broke the
  module line.

- `weir add sig` on a file that exists but will not run (a stray
  `.yaml`) says so, instead of "not on PATH".

- Subcommand tokens complete at every depth — `kustomize ed` offers
  `edit`, `kustomize edit a` offers `add` — in the token spelling,
  never the case or record name; the segments un-glue from the sig's
  own path keys.

- Completion fires on `-` (a declared trigger character now) — a
  bare `--` offers the longs, a single `-` the shorts beside them.

- Go-to-definition reaches into the sig: a flag lands on its field
  declaration (the scoped record the line resolves to), a
  subcommand token on its case's record.

- The walk follows nesting to depth 4 (`kustomize edit add
  resource`), reads gh's colon-suffixed command tables, and the
  not-on-PATH message dropped its aside.

- A sub-less line on a scoped sig checks the GLOBALS — flag-only
  usage (`claude --scop2 --scope`) squiggles again: the flags riding
  every case are the global set, so the case intersection checks it;
  hand-written unions that share nothing keep the partial-surface
  skip.

- **Sig generation scopes to subcommand paths.** The walk keeps
  its provenance to depth 4: a walked surface generates a union
  with a case per subcommand path — the longest match picks it, so
  `jira issue list --summary` warns naming `jira issue list` while
  `jira issue create --summary` checks clean (sibling paths no
  longer share flags; `issue create` and `project create` name
  distinct records by construction). A case checks its own flags
  plus its ancestors' and the globals; a line with no subcommand
  checks the globals; completion offers the matched path only.
  Flat sigs (hand-written or scrape-poor) load unchanged.

- Sig flags complete in the editor — a `-`-word after a sig'd tool
  offers the sig's own longs in kebab spelling (`--s` →
  `--session-id`, not the field name or a comment word; the old
  dropdown was the editor's word-fallback over the sig file).
  claude-style dual spellings (`--allowedTools, --allowed-tools`)
  merge into one field, both accepted.

- broot's box-drawing options table parses — the border glyphs
  strip to spaces and the rows read as standard columns, shorts and
  docs included.

- Two more help dialects generate: micro's Go-flag rows
  (single-dash longs, description on the next line), and BSD grep's
  usage-only page — which exits nonzero, so the dump now feeds the
  harvest. And a surface that recorded no shorts no longer warns on
  short flags: no evidence, no claim.

- Probe output is stripped of ANSI escapes — a tool that colors its
  help when piped (jira on macOS) broke banner detection and leaked
  `[1m` bytes into sig docs; a colored `--version` would have stored
  escapes as the identity.

- A flag whose long is a weir keyword (docker's `--type`, kubectl's
  `--for`) no longer aborts generation — the generator emits
  `[<Wire "type">] typeFlag: bool`, and the sig checker reads the
  Wire spelling for matching and did-you-mean. A keyword long WITH a
  short (jira's `-t, --type`) shares one attr bracket, and when
  walked subcommands reuse a short (docker's `-a` on `all` and
  `all-tags`) the first holder keeps it — longs still check. A
  generator-bug refusal now names the offending sig line.

## v0.0.13

### New features

- **Sig generation walks subcommands.** Cobra-family tools (jira,
  docker, kubectl, kustomize) keep the real flags under
  `tool sub --help` — generation now reads the advertised commands
  sections (grouped headings included), probes each subcommand's
  help breadth-first to depth 4 under a 60-probe budget, and unions
  the flags into the flat surface, labeled `help+subs`. So
  `jira issue list --jql …` stops warning on every flag the
  top-level help never mentioned. Every probe is guarded: null
  stdin, temp cwd, bounded wait with kill.

- A usage-table help with no flag rows (weir's own) yields a
  harvested surface — every `--flag` token, docless, labeled
  `help-scan` — instead of "found no flags".

## v0.0.12

### Bugfixes

- `weir add sig` on a tool that refuses `--version` (jira-style) no
  longer records the error's usage dump — user paths included — as
  the version. The probe reads the exit code and climbs a two-rung
  ladder: `--version`, then the `version` subcommand (`jira version`
  works), with null stdin and a temp cwd so a bare-word probe can
  neither hang nor serve a local VERSION file as the identity. A
  tool answering neither records NO identity, the sig says so in a
  comment, and `weir verify` takes the hash-only arm. `let version`
  is now optional in sig files.

- `#sig "jira"` (a quoted tool name) searched for a file literally
  named `"jira".weir` — it now teaches: drop the quotes.

- The sig help scraper learns fzf's `+s, --no-sort` off-toggle
  spelling, and stops eating the first word of an argless flag's
  description.

- Pattern-let binders hover: `let key, title = …` shows each name's
  own type.

## v0.0.11

### Docs

- Builtin hover/`#help` examples are statement-style — the eleven
  `let … in` one-liners now read as scripts actually read, and a
  gate keeps the form out.

## v0.0.10

### New features

- **`Str.splitOnce` / `Str.trySplitOnce` — split at the first
  separator, tail intact.** Rust's `split_once` shape: `splitOnce
  sep s` yields `(before, after)` and raises when the separator is
  absent; `trySplitOnce` is the Option twin. The KEY=VALUE spelling
  — `Str.split` plus a `[k; v]` pattern silently misses when the
  value contains the separator; `splitOnce` keeps the tail whole.

- **`Seq.exactlyOne` / `Seq.tryExactlyOne` — the cardinality
  assertion.** `head` takes the first and silently accepts more, so
  a wrong-arity command output passes quietly; `exactlyOne` raises
  on none AND on more, with distinct messages (they are different
  bugs). The try twin answers None for both shapes, and stops at
  the second element — an infinite source never hangs it. The guide
  now teaches it for one-line expectations
  (`git rev-parse HEAD |> Seq.exactlyOne`).

- **`Seq.first` is retired; `take` stands.** A preference reversal:
  the synonym's readability reason did not fall — it was outweighed
  by one-name-per-operation, the rule that already retired
  `filter`. The error teaches (`weir's first is 'Seq.take'`), and
  the freed name binds (`let first = …`).

- **Bare `dir` teaches the listing.** The module-qualified redirect
  (`use 'Path.dir'`) now also says "for a directory listing, use
  ls" — DOS muscle memory pointed at the parent-of-a-path function
  was a wrong turn.

## v0.0.9

### New features

- **`%` — integer remainder.** F#'s spelling at `*`/`/`'s
  precedence, TRUNCATED — the sign follows the dividend (`-7 % 3`
  is `-1`, matching F#/.NET; Python's floored `%` gives `2` there).
  A zero divisor raises ("modulo by zero", `/`'s discipline);
  floats are refused with a teach — finite-only floats cannot hold
  IEEE's NaN remainder. In command argv `%` stays a literal byte
  (`echo 50%`, `date +%N`).

- **`Dir.stat` — a directory's entries as rows.** `Dir.list`'s
  `seq<FileRow>` form: `ls`'s own rows over a named directory, same
  order, sorted by name, eager. Three names, one mapping — `Dir.list`
  gives paths, `Dir.stat` gives rows, `File.stat` gives one row. And
  the consequence: `ls` is no longer a reserved binder — with
  `Dir.stat "."` as the escape a shadow leaves open, `let ls = …` is
  a user preference now, not an error.

## v0.0.8

### Bugfixes

- A heredoc block's trailing blank lines clip — the blank line
  separating the block from the next statement is layout, not a
  trailing empty element. Interior blanks stay content, matching
  YAML block scalars (whose keep-trailing `|+` form weir rejects).

### Docs

- The showcase tours the heredoc block, and Showcase joined the
  site's top navigation.

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
