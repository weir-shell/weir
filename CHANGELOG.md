# Changelog

## v0.0.82

### Fixed

- **A second, hand-written parser takes the common statements.** The
  grammar library parses a statement through a closure per grammar
  rule and a reply per step; most of a check's time went there. A
  scanner-driven parser now handles the statements real scripts are
  made of — bindings, expressions, types, signatures, imports — and
  hands anything else, and anything a command could be, to the
  grammar unchanged, which still produces every diagnostic. The two
  are checked against each other: `WEIR_PARSE_ORACLE=1` runs both on
  every statement and fails on any difference, and CI runs its
  batteries that way. Same diagnostics, same trees: the starship
  import goes from 178 ms to 123 ms pinned to one core and from 131 ms
  to 102 ms on 15 (medians of 21), a direct check of its largest
  module from 76 ms to 65 ms.
- **The checker allocates less.** Every walk over a statement's
  tree built a list of each node's children before looking at them,
  the type finalizer allocated a closure per node, the unused-binding
  pass unioned a set of free names at every node, and every scope
  exit rebuilt its map whether or not the name was in it. The walks
  now visit children in place, the finalizer and the usage pass are
  single passes with explicit state, removals of absent keys are
  skipped, and the per-statement substitution is a dictionary. Same
  diagnostics, same trees: the starship import goes from 126 ms to
  105 ms pinned to one core (medians of 31), allocation from 84 MB to
  73 MB.
- **Line assembly allocates far less.** Before parsing, every physical
  line was trimmed, split into its comment and code and re-scanned by
  the district mask, the doc attachment and the binder-head checks,
  each building its own strings; the string scanner allocated its
  helpers on every call. The scans now count and index in place, and
  the marker checks look for their keyword before doing the full test.
  Same diagnostics, same `fmt` output: the starship import's assembly
  allocates 9 MB instead of 35 MB, and the import goes from 216 ms to
  200 ms pinned to one core and from 176 ms to 160 ms on 15 (medians
  of 21).
- **The checker looks constructors and record shapes up in a table.**
  Every constructor use scanned all known types for the unions declaring
  that case, every record literal scanned them for a record with its
  fields, and each `let` walked its expression for anonymous record
  shapes twice. The case owners and record shapes are now indexed once
  per set of types, and the walk runs once. Same diagnostics: the
  check's own allocation drops from about 42 MB to 28 MB, and with the
  assembly change above the starship import goes from 216 ms to 195 ms
  pinned and from 176 ms to 155 ms on 15.
- **Less garbage per statement.** The check's command-head rule rebuilt
  the set of every known union case for each statement; parse failures
  that only steer the grammar built a fresh error each time; every
  expression position read the source position before seeing whether a
  number could start there; and the post-parse depth probe allocated a
  tuple and a child list per node. Each is now built once or not at
  all. Same diagnostics: the starship import goes from 196 ms to 182 ms
  pinned and from 159 ms to 153 ms on 15.
- **Source positions are values.** Every syntax node's span held two
  heap-allocated positions; a position is now two plain numbers inside
  the span. With the change above, the starship import allocates 137 MB
  instead of 163 MB and goes from 196 ms to 178 ms pinned and from
  159 ms to 141 ms on 15 (medians of 21).
- **The prelude is built once per process.** Every imported module
  parsed and checked the prelude's type declarations again for its own
  base environment; the result is now computed once and shared, each
  caller getting its own scratch table. Same diagnostics: bare startup
  goes from 10.0 ms to 9.3 ms pinned, a direct check of the starship
  port's largest module from 95 ms to 90 ms.
- **Words, numbers and plain strings skip the operand search.** Each
  operand tried some two dozen kinds of literal, bracket and word in
  turn, merging every refusal into an error that was then thrown away.
  An identifier, a number or a non-interpolated string now goes straight
  to its own parser; anything that can nest goes through the full list
  as before, and a failure always replays it, so error messages are
  unchanged. The starship import goes from 178 ms to 176 ms pinned and
  from 137 ms to 133 ms on 15 (medians of 21).
- **`echo key: 'it // works'` parses again.** v0.0.81 read a quoted
  word after `:` (or `,` `*` `>` `(` `<` `|`) as a type variable's
  tick, so `//` started a comment. A tick is now a type variable only
  on a type line: a `type` line, a `let` header, a union case, a `fun`
  parameter, a record field.
- **`Http.expect` of a GET runs inside `plan` again.** A request held
  in a variable, built from `Http.defaults` or passed to a helper was
  refused as "a mutating method". Only a method known to mutate is a
  check error now; one check cannot determine is refused at run if it
  mutates.
- **A misspelled flag before a subcommand no longer eats it.**
  `--verbos deploy --tok x` printed seven errors; it now prints the
  did-you-mean and the usage pointer.
- **A misspelled flag never prints the next argument, in any
  spelling.** v0.0.81 covered `--tokn hunter2`; `-tok hunter2`,
  `-zz hunter2`, `--x=hunter2` and a token after a subcommand that
  takes nothing (`status --tok hunter2`) still echoed it. All now
  report `unexpected argument after '<flag>'` and hide attached text
  (`--x=…`). A flag where a command belongs says `unexpected flag
  '--x=…' — the command comes first, one of:` and lists the commands.
- **`[<Default -x>]` gets a message.** It, `-"a"`, `- 1` and
  `[<Wire -a>]` gave an empty `error [parse]: `; a `-` in an attribute
  argument must now be followed by a number, and the error says so.
- **`Uuid` has no `<`, even through a generic function.**
  `let before a b = Some a < Some b` called with Uuids passed check; it
  is now refused as the direct form is.
- **`Dir.move` and `Dir.copy` keep directory permissions.** The
  cross-filesystem move and the copy reset `0700` to `0755`, so a
  private tree became world-listable. Both keep each directory's mode,
  as `mv` and `cp -R` do; the move also keeps directory mtimes.
- **`File.move` moves a symlink as a link across filesystems.** It
  copied the target (same-filesystem moves kept the link) and leaked
  .NET text for a dangling link.
- **`#save` keeps a shadowing chain.** `let xs = [1; 2]` then
  `let xs = xs |> …` saved nothing, a redefined function's callers
  vanished, and a redeclared type's old constructors were saved and
  failed check. The chain is saved whole; values of a superseded type
  are dropped with a note. A user-bound `it` read after an echoed line
  is no longer saved against the wrong value.
- **`#save "x y.weir"` writes `x y.weir`.** The quotes became part of
  the name, and a directory or unwritable path printed .NET text; the
  refusals now read `#save: <p> is a directory`, `no such directory`,
  `permission denied`.
- **A capture past 2 GiB is an error.** `| complete` on more than
  2 GiB of output returned nothing or a truncated prefix with exit 0.
  It now stops with a located error naming the command and teaching
  streaming (iterate the lines, or write them to a file). `| succeeds`
  holds no output and has no cap.
- **`Proc.tail` on a large spill.** It read the whole spill into
  memory (6.4 GB for a 1.2 GB spill) and returned `[]`; it now reads
  back at most 1 MiB from the end, marking a cut first line with `…`.
- **`exec` is refused wherever it would skip cleanup, however it is
  reached.** v0.0.81 refused `cmd | exec` written directly inside a
  `within`; through a helper function, in a `pfor` or `Seq.pmap` arm,
  or in an `always` cleanup it still ran and orphaned the scope. Check
  now follows helpers, and the run refuses `exec` while any scope or
  parallel arm is open (helpers passed as arguments or imported).
- **A failed `exec` leaves the session as it was.** Its env prefix
  and cwd stayed applied for the rest of the session.
- **An init file loads all or nothing.** A refused init left its
  `#session` `cwd`, `env` and `logLevel` applied while saying "not
  loaded". Every field is checked first (all errors print); any
  failure, including a later one, leaves the session as it started.
- **A session `let` shadows an alias.** `let find = 3` did not shadow
  `#alias find`, and an alias beat a parameter, lambda, match or
  block-let binding of the same name (`let f g = g + 1` ran the alias).
  Bindings now win, and `#alias` over a live binding is refused.
- **YAML reads `- - x` as PyYAML does.** A sequence nested on the dash
  line was read as the string `"- x"`; `- 'it''s: here'` was split
  into a mapping; `"..."` was written unquoted and read back as a
  document end; non-breaking and other Unicode spaces were trimmed, so
  weir couldn't read back its own output. A multi-line quoted value
  whose first line ends in `''` or `\"` now reads. `yaml` blocks take
  flush-left (kubectl) and 4-space sequences, as `Yaml.parse` does.
- **Two modules returning the same anonymous record shape agree.**
  They produced two incompatible types (`expected {| k: int |}, got
  A2.{| k: int |}`).
- **Regex never splits an emoji.** `Str.rreplace "." "*" "😀"` gave
  `**` and `Str.isMatch "^.$" "😀"` false. Regex members now count code
  points, as every other `Str` member does: `.` and negated classes
  match a whole character, and positions, lengths and captures never
  split one.
- **Numbers at the edges.** `Float.round -0.4` is `0.0`, not `-0.0`;
  `Float.toInt` takes the minimum int; `Duration.parse` reads back
  `show` of the minimum `Duration`; `Size.parse` reads the minimum
  size; `Instant` reads a zero-padded fraction of 19 or more digits
  exactly.
- **`Instant.tryParseWith` reports a bad format whatever the input.**
  A format defect (an unknown directive, a lone trailing `%`) was hidden
  unless the input reached it; it now raises, as `parseWith` does.
- **`Env.fromFile` reads its file once.** It re-read the file on every
  use and reported a bad line wherever the list was first used. It now
  reads at the call and reports a bad line there.
- **No more .NET exception text** from `weir fmt` on a yaml or heredoc
  block whose first line is a deeper comment, a module that declares a
  type twice (and the REPL after a type redeclaration), non-ASCII
  digits in `Duration`/`Instant` parsing (the `try` forms return
  `None`), strings too long for `Str.toBase64`, `Bytes.toBase64`,
  `show`, `to json` or `to yaml` (they name the limit; `to json` now
  writes strings past ~166M characters), an explicit `Content-Length`
  request header (refused before send, as `Transfer-Encoding` is), and
  an argument list too long for the OS (it said "command not found").
- **`weir check` agrees with the run.** An `if`/`match` with command
  branches ending a `for`/`pfor` body, a `Seq.iter` lambda or an
  `always` failed both with `expected unit, got seq<string>`; it now
  runs. A path head that does not exist yet (`./gen.sh`, created
  earlier in the script) is a `cmd-not-found` warning at check, not a
  parse error; at run a still-missing one fails before any statement.
  `let y =  yaml`, a tab before `yaml` and `let y= yaml` open a yaml
  block instead of running a command. `<value> | and <cmd>` is refused
  at check, not only at run. `weir check` on an init file refuses the
  `#session` values the REPL refuses (bad `logLevel`, `echoCap` ≤ 0, an
  env key with `=` or empty, NUL); a NUL env value is refused at load
  instead of truncated. A module declaring a type twice, or below an
  import that provides it, is refused at the import.
- **`weir fmt` and the LSP split lines as check does.** A bare CR
  ended a line for check and run but not for fmt and the LSP, so fmt
  could break a running file. fmt also accepts `let r = match x with`
  arms continued by `|>`, init `#session` fields at any indent (it
  seats them at 4), and `else` between `if` and a deeper then-body; a
  heredoc line outdented below the block is a located refusal.
- **The piped REPL keeps the line before a heredoc on its own.** It
  was glued into the heredoc's entry.
- **LSP rename and references.** Rename covers `T.create` of a
  validated type and types inside anonymous adapter types, refuses
  non-ASCII digits, and refuses a rename that would leave an error —
  a length change that breaks a column-aligned layout among them.
  References on an imported union case find both expression and
  pattern uses. A module's parse error is published once, not twice.
- **Clearer messages.** More parser expectation dumps replaced by
  located teaching: a directive in `-e` or an init file, `find .` and
  `$(date)` with `date` bound at the prompt, a reifier inside an
  expression (`print (echo a | line)`, `match … | exitCode with`),
  `$(ls)`, `echo ()`, `import` inside a block, `ls /`, `1 + ~x`, a bare
  `repeat` with no body, computed range endpoints (`[0 .. -n]` gave an
  empty message), `3--1`, `- -5`, `(e : T)`, and a lowercase type after
  `from json`. Hover, signature help and diagnostics show `'a`, `'b`
  where `'a1`, `'a2` leaked. `[<Short "_">]` says "a letter or digit";
  `Some 1 < 2` names the operand mismatch; `"a\<TAB>b"` and `"\😀"` name
  the character; `ls`/`pwd` bound to a value say so; a record-table
  echo aligns wide characters and shows TAB as `\t`.
- **Docs match the binary.** `#echo`'s limit applies to piped
  sessions too; a long string is cut at the echo with `…` and no hint;
  string slices count code points, not UTF-16 units; and the v0.0.81
  notes below no longer claim that `weir restore` repairs a modified
  signature or that `--help` quotes every string default.

### Changed

- **`-x` negates in `x`'s type.** Prefix minus on a float, `Duration`
  or `Size` name was a type error (`let x = 1.5 in -x`); it now works
  as on literals. A lambda parameter whose type is not yet known
  (`fun x -> -x` applied to a float) still types as int.
- **`#save path` is relative to the session's directory**, not the
  directory weir started in, so it follows `cd`.
- **A YAML quoted value followed by more text is refused.** `'a'b'`
  was read silently; it now says `content after the closing '`.
- **`exec` under a scope fails at run when check can't see it.** The
  error names the parallel arm or the open scope.

### Chores

- The F# sources are formatted by fantomas 8.1.0, pinned in
  `.config/dotnet-tools.json` with the house Stroustrup bracket style in
  `.editorconfig`; CI fails on formatting drift. To reformat:
  `dotnet tool restore && dotnet fantomas src tests`.

## v0.0.81

### Fixed

- **Launch is faster for scripts that import large modules.** The
  check-time walk that decides which module bindings are command
  helpers asked, for every variable it met, whether the name was a
  reifier's desugar spelling, and built 28 strings to answer each time.
  The answer is now one table lookup. A script importing the 6,400-line
  starship port goes from about 750 ms to about 400 ms before its first
  line runs; bare startup was and is about 12 ms.
- **The check itself is leaner on large modules.** The module loader
  re-derived its command-helper table from every earlier binding on
  each statement; the assembler, the district mask and doc attachment
  each rescanned every physical line for its comment several times;
  the load-target scan re-parsed the whole file; constructor lookup
  rebuilt the set of imported types per use; and the command grammar
  rebuilt its resolver-independent parsers on every attempt. Each now
  does its work once. Same diagnostics, same trees: the starship import
  goes from about 400 ms to about 250 ms (CPU-pinned medians 373 → 254
  ms), a direct check of its largest file from 147 ms to 95 ms; bare
  startup stays about 12 ms.
- **Imported modules check in parallel.** The import graph is read
  before any module checks, and a module whose imports are done checks
  on a worker thread (as many as processors, at most 8) while the
  importing file's check walks toward its import line; each result
  surfaces at that line in load order, so diagnostics are unchanged to
  the byte (an 80-file parity corpus, the starship port checked 50
  times). On a 15-core box the starship import goes from 263 ms to 215
  ms (medians of 9); pinned to one core it is the sequential loader,
  249 ms as before; bare startup stays about 12 ms. What remains is the
  collector's: the workstation GC marks on one thread, and the module
  checks spend most of their parallel phase stopped for it.
- **The collector has less to do.** Merging an imported module into the
  importer rebuilt its set of taken type names once per type and walked
  every type origin through an enumerator per lookup; it now keeps both
  as it goes. Finalizing a statement's types and typed tree copied every
  node even where nothing changed, and renaming an import's types copied
  ones no rename touched; unchanged nodes are now shared. Same
  diagnostics, same trees: the starship import allocates 254 MB instead
  of 287 MB, promotes 13.3 MB instead of 15.7 MB out of the youngest
  generation, and spends about 50 ms instead of 57 ms in GC pauses; it
  goes from 246 ms to 225 ms pinned to one core and from 206 ms to 184
  ms on 15 (medians of 25), a direct check of its largest file from
  91 ms to 86 ms; bare startup stays about 10 ms.
- **The parser builds its grammar once.** The command grammar was
  rebuilt for every statement, and for every command line nested in a
  let, an `if` or a value-headed pipe, because it held that statement's
  name resolver; it now asks the current one as it parses. A postfix
  chain rebuilt its index, slice and field parsers at every step, the
  expression grammar's teaching alternatives read the source position
  before checking the next character, and every node's span allocated
  two positions on the way. Same diagnostics, same trees: the starship
  import's parse allocates 79 MB instead of 151 MB, and the import goes
  from 225 ms to 202 ms pinned to one core and from 192 ms to 167 ms on
  15 (medians of 21); bare startup stays about 9 ms pinned.
- **A misspelled flag no longer prints the next argument.** After an
  unknown flag, `Args.load` reported the following word as an
  unexpected argument and quoted it, so `--tokn hunter2` for a
  `Secret` flag printed the secret. The word is still reported, as
  `unexpected argument after '--tokn'`, without its text.
- **`cmd | exec` honours the current directory, and is refused where
  it would skip cleanup.** The replaced process started in the
  script's start directory whatever `cd` or `within cd` said; it now
  starts in the session's. Inside any `within` block (`tmp`, `proc`,
  `lock`, `serve`, `cd`, `env`, or a bare `within … always`) `exec` is
  now a check error: replacing the process left the temp dir behind,
  orphaned the scoped process and never ran `always`.
- **`let x = retry …` runs each body line on its own.** With the loop
  head on the `let` line, same-indent body lines were glued into one
  command (`echo first echo second`) with only a warning; they now
  sequence as they do without the `let`.
- **An explicit `Content-Type` replaces the body's.** A request with
  a `Content-Type` header and a `Text`/`Json`/`Form` body sent both,
  comma-joined (`text/plain, text/csv`), which servers reject. The
  explicit header now wins and exactly one is sent, as for
  `User-Agent`.
- **`#save` keeps what it promises.** A line that used a name later
  redeclared was saved above the redeclaration and failed at run, and
  an alias over a builtin (`#alias ls = ls --color=never`) dropped the
  aliased `let` while keeping its dependents. Both saved files now
  check and run.
- **A top-level YAML `null` writes `null`.** `to yaml` wrote empty
  text for it, which does not read back, and a `null` document
  vanished from a `to yaml stream`.
- **`Server.port` reports the bound port.** See Changed.
- **`Dir.move` crosses filesystems.** A move to another mount failed
  with "cannot access … Invalid cross-device link"; it now copies then
  deletes, as `mv` and `File.move` do, and its errors name the paths.
  A symlinked source moves as the link, exactly as on one filesystem.
- **`weir restore` reports a locally modified signature.** It said
  `present` and exited 0 while `verify` said MODIFIED; it now reports
  MODIFIED and fails. A signature is generated, not downloaded, so
  `restore` does not repair it: restore the checked-in copy or run
  `weir add sig` again.
- **`Instant.tryParseWith` raises for a format without a full date.**
  Such a format can never match, so returning `None` hid the bug; it
  now raises as `parseWith` and an unknown directive do.
- **`Http.withQuery` puts the query before a `#fragment`.** It
  appended after it (`h/p#frag?a=1`), so the parameter was never sent.
- **`[<Wire "name">]` matches a table header verbatim** even when it
  is spelled like the field.
- **`///` above a one-line `[<Attr>] field: T` reaches `--help`.**
  The own-line attribute form worked; the inline one dropped the doc.
- **`weir -e` and the REPL echo share one renderer.** Piped, both
  write a value's control characters byte-faithfully; at a terminal,
  both show them visibly (`\x00`, `\x1b`). They used to disagree.
- **No more .NET exception text** from `show` of the minimum
  `Duration`, strings past the length limit (`Str.join`, `+`,
  interpolation, `Str.replace`, `Bytes.toHex` now name the limit),
  `Json.inferShape` on bad JSON (and the message names
  `Json.inferShape`, not `#infer`), an init file's bad `#session` env
  key (now a check error, NUL included), an out-of-range serve port,
  an `Other` method that is not a token, an invalid or non-http URL, a
  `Transfer-Encoding` request header (refused before send), a garbled
  status line, `weir add schema` given a local path (`add schema takes
  an http(s) URL`), and a malformed `.weir/lock.json`.
- **`weir check` agrees with the run.** It refused spec-valid code —
  `let q = 3 in q + 1` inside an indented block, `cmd | and cd d` in a
  block — and passed code the run refused: `Seq.pmap`/`piter`/
  `pfirst`/`pfor` and a mutating `Http.expect` inside `plan`,
  `[<Short "-">]` (a short is now one letter or digit). A misplaced
  or unknown directive gets the run's message from check too, an
  ambiguous imported case is named as such, env-key refusals are
  located where the key is built, and a shared module's error is
  reported once, not once per import.
- **`weir fmt` no longer refuses a match arm continued by `|>`** under
  its body, a layout the checker accepts.
- **Clearer messages.** Fewer internal names (`'__hole1`, `'a1`) and
  parser expectation dumps in diagnostics (most of the rest went in
  the next release); one shape for a bad flag or env value (`--rate:
  not a float: 'abc'`); `--` is refused as an end-of-options marker
  instead of getting a did-you-mean; `--help` keeps two spaces between
  a long flag and its text (wrapping when it must), shows an empty
  string default as `""` and escapes a newline in one; a TLS handshake with a plain
  HTTP port is no longer called an untrusted certificate; and a dozen
  more located or reworded errors.
- **Docs match the binary.** The reference and the skill file no
  longer contradict behaviour: `Seq.groupBy` forces its source,
  `Seq.take` past the end truncates while `skip` raises, `Size`'s
  `show` does not parse back, identifiers take no `'`, `from yaml`
  refuses `Map` fields, a yaml patch's sequence appends depend on
  patch order, `File.exists` is false for a directory, command output
  starting with a UTF-16/32 byte-order mark decodes as that encoding,
  and the echo shows 100 elements in `-e` and piped as at a terminal.

### Changed

- **`ServerConfig.port` is an `Option<int>`.** `Some 8080` binds that
  port; `None` asks the kernel for a free one, and `Server.port`
  reports the port actually bound (it said 0 for `port = 0`). Migrate
  `{ port = 8080; … }` to `{ port = Some 8080; … }`.
- **`<`, `>`, `<=` and `>=` compare strings, bools and Options.**
  Whatever `Seq.sort` orders, the operators now compare (strings
  ordinally, `false` before `true`, `None` first); `Uuid` still has no
  `<`.
- **The `-e` and piped REPL echo shows 100 elements of a lazy seq**,
  as the terminal REPL does and `#echo` reports (it was 10).

## v0.0.80

### Added

- **`weir add module semver` vendors the latest release.** The
  official library tags each module on its own, `<name>/v<semver>`
  (`semver/v0.1.0`), and a bare name picks the highest stable
  `semver/v*` tag by semver precedence from the repo's own tag list
  (a prerelease only when nothing stable exists) and says which:
  `semver: latest is semver/v0.1.0`. `semver@v0.1.0` pins
  one; a full sha after the `@` is taken as is. `semver@0.1.0` (no
  `v`) and `semver@` are refused naming the spelling.
- **Rename, references and highlight reach types, fields and cases.**
  F2 on a type name, a record field or a union case declared in the
  file renames its declaration word and every use the checker
  resolves: accesses (`p.id`, `p.a.id`, `_.id`, `$p.id`, interpolation
  holes), literal keys (`{ id = id }` renames the key only), `with`
  updates, patterns, construction, the `from json T` / `Env.load T` /
  `Args.load T` slots, type references in other declarations and a
  module's signature lines. A field is a wire name, so renaming it
  renames what the json/yaml boundary and `Args.load`/`Env.load`
  read — the thing to do when the wire is the truth; a `[<Wire>]` on
  the field keeps its key. The rename refuses with a reason where it
  cannot be complete: a row-typed access of the same field name
  (`let label r = r.id` could serve any record with an `id` — the
  site is named), an error anywhere after the declaration, a new
  field name that is a field or wire key of the record, a new case
  name that any visible union already declares, a new type name that
  is a type or module. Shift+F12 and highlight answer the same sites.
  The "nothing to rename here" refusal on a field, and the "is a type
  or a union case" refusal on an uppercase field, are gone with it:
  the fallback now says what the cursor must be on.

### Changed

- **`semver@v0.1.0` now means the tag `semver/v0.1.0`.** The
  repo-wide tag spelling (`v0.1.0` on the whole library) no longer
  resolves through the short form; the github shorthand and the
  raw-URL form are unchanged, and the lock still holds the raw URL
  at the full commit sha.

## v0.0.79

### Added

- **The editor keeps working inside a broken statement.** A type
  error on one line of a multi-line `let` no longer blanks hover and
  go to definition for the whole statement: the lines above and below
  the bad one answer as before, the bad line shows nothing, and a
  name whose value failed (and anything typed from it) shows nothing
  until it is fixed. `weir check`, the runner and the REPL report
  exactly what they did before — the recovery is the editor's only.
- **Find references, highlight and rename in the editor.** `weir lsp`
  answers `textDocument/references`, `documentHighlight`,
  `prepareRename` and `rename` for value binders: top-level and block
  `let`s, params, pattern binders, `within`/`until` binders and yaml
  `for` binders, with uses in expressions, `$name` splices, `$@xs`
  splats, interpolation holes and yaml districts; a signed module
  member's signature line renames with it. Rename is complete or
  refused with a reason the editor shows (a builtin, an imported
  member, a field, a case or a type name under the cursor; an error in
  the name's scope; a new name that is not a binder name or is already
  visible in the scope; an init or signature file). References and
  highlight on a builtin or an imported member list this file's uses.

- **The editor session fills out: outline, folding, inlay hints,
  links.** `weir lsp` now answers document symbols (types with their
  fields and cases, functions with their nested functions, imports and
  `#sig` lines; a signature and its implementation are one entry, and
  a `let`/`type` that failed to check still shows by name), folding
  ranges (multi-line statements, indented blocks, match arms, `///`
  and `//` runs, import runs), inlay hints (the inferred type after
  each binder and parameter, the return type before a function's `=`;
  none on signed module members; long types cut at 40 characters with
  the full type in the tooltip; each class a setting, sent as
  `initializationOptions` or `workspace/didChangeConfiguration`;
  multi-line pipeline stages opt-in) and document links (import paths,
  `schema=` names, `#sig` tools — resolved targets only). Hints and
  outline details are the hover's own text, and nothing lands on a
  rewritten `#session` field.
- **Diagnostic tags and related information.** An unread binder is
  tagged unnecessary (editors fade it; the bare `_` discard is not —
  it is misnamed, not unnecessary), so is an unreachable arm. An
  implementation that mismatches its signature points at the
  signature line, an imported module's error at the import, a
  shadowed unread binder at its rebinding — as LSP related
  information when the editor declares it, and as a `related` array in
  `weir check --json`. The `imported-here` note is still published.
- **VS Code: run and check buttons.** `weir: Run script`, `Check
  script` and `Check script (--can)` in the command palette and as
  editor-title buttons, running the same binary the language server
  uses in a reused `weir` terminal; `weir.inlayHints.*` settings. No
  code lens: the server never runs anything.
- **Quick fixes in the editor, and `fix` in `check --json`.** A
  diagnostic weir knows how to repair now carries the repair: the
  editor's lightbulb offers it, and `weir check --json` prints it as
  `fix` (a title and the edits, so an agent can apply it). The set:
  replace a misspelled variable, field, case, attribute, flag or
  command with the name it meant; qualify a bare module member, or
  all of them at once; add the missing match arms or a missing
  `else`, each with a `fail "todo"` body; rename an unused binder to
  `_name`; lowercase a binder; align a `///` doc, or move one from a
  signed implementation to its signature; call an uncalled function;
  unwrap an `Option`/`Result` with `|> orFail`; add a stub
  implementation under a signature.
- **Two new teachings.** A statement that is a `unit -> X` function
  says so ("that is the function itself, not its result: call it with
  ()"), and a mismatch where the value is an `Option<T>`/`Result<T, _>`
  of the wanted `T` names the unwrap (`|> orFail`). A non-exhaustive
  match now names a payload case as the pattern to write (`Push _`).
- **Signature help in the editor.** A space or `(` after a function
  shows `Seq.fold (f: 'a -> 'b -> 'a) (init: 'a) (xs: seq<'b>) : 'a`
  with the parameter you are on highlighted and the `///` or builtin
  doc below; in a `|>` stage the last parameter is marked as filled by
  the piped value. A function of your own shows the names from its
  definition, an imported one the names from its module. After a
  command declared with `#sig`, the flags you have typed show what each
  takes and means, read from the signature file alone (the tool need
  not be installed). Nothing pops inside a string or comment, after a
  keyword, or once every parameter is supplied.

- **`[<Ord n>]` derives an ordering.** Number a record's fields or a
  union's cases with `[<Ord n>]` and the type sorts (`Seq.sort`,
  `sortBy` keys, `min`/`max`) and compares (`<`, `>`, `<=`, `>=`) by
  the numbered members in ascending `n` — a union by case position,
  then payload. The key is the numbers, never the declaration order,
  so reordering fields cannot change a sort. Unnumbered record fields
  take no part (two values equal on the key but different elsewhere
  are neither `<` nor `>` nor `==`; sorts keep their input order); a
  union numbers every case or none. A numbered member must itself
  order or the declaration says which one does not; tuples still
  refuse, and the error now says to name the parts in a record and
  number them. `Option` values sort too. Hover on the type shows the
  key in order, on a member its position.

- **`weir add module <name>@<tag>` vendors an official library
  module.** A bare name resolves to
  [github.com/weir-shell/lib](https://github.com/weir-shell/lib) —
  `weir add module semver@v0.1.0` is `weir add module
  github.com/weir-shell/lib//modules/semver.weir@v0.1.0 --as semver`, and
  `--as` still renames. The lock, `verify` and `restore` see the raw
  URL at the full sha, as for the long spelling. The `@tag` is
  required: `weir add module semver` refuses and names the spelling.
  The missing-module error for `import "weir:x"` now names the bare
  spelling first.

- **The library's first module, `semver`.** `weir add module
  semver@v0.1.0` vendors Semantic Versioning 2.0.0: `parse`/`tryParse`,
  `toString`, `isPrerelease`, `core` and the three bumps, with
  `[<Ord n>]` on the parsed record's key so `Seq.sort`, `<` and
  `Seq.max` follow the spec's precedence (`1.0.0-alpha < 1.0.0-alpha.1
  < 1.0.0-beta.11 < 1.0.0`, `1.9.0 < 1.10.0`); build metadata is the
  record's unnumbered `build` field — kept, rendered back, ignored for
  precedence. Every member is `let pure`. See docs/tooling.md "The
  library".
- **`Completed.ok`.** `cmd | complete` now carries `ok`, exactly
  `exitCode == 0` — the `| succeeds` law as a field — so
  `let r = … | complete` then `if r.ok then …` reads. It leads the
  record (`{ ok; exitCode; stdout; stderr }`) in `show`, the REPL echo
  and `to json`. A tool whose nonzero codes mean something (grep's 1)
  reads the code.

### Changed

- **`weir add module` resolves a tag or branch without the host's
  API.** The `github.com/…` and `gitlab.com/…` short forms (and the
  bare library name) now read the repo's ref list the way `git
  ls-remote` does — git's own protocol, served by every host, no token
  for a public repo and no API rate limit, so a shared CI address can
  no longer hit GitHub's 60-requests-an-hour budget. An annotated tag
  resolves to its commit. The lock still holds the raw URL at the full
  sha; `restore` and `verify` are unchanged. A tag or branch the repo
  does not have now says so and links the tags page (`no tag or branch
  'v0' at github.com/weir-shell/lib — the tags: …`); a `@sha` must be
  the full 40 hex digits (a prefix was the API's lookup).

- **Receipts are read-only (breaking).** `Completed`, `FileRow` and
  `HttpResponse` — the records only the runtime mints (`| complete`,
  `ls`/`Dir.stat`/`File.stat`, `Http.send`) — can no longer be written
  as a record literal (`{ exitCode = 3; … }`, `Completed { … }`) or
  changed with `{ r with … }`. Each is a check error naming where the
  value comes from and the anonymous record a test passes instead (a
  function over the fields is row-typed, so `{| exitCode = 3 |}` works).
  A script that built a `Completed` by hand breaks here: run a command
  (`sh -c "exit 3" | complete`) or pass an anonymous record. A row-typed
  updater applied to a receipt raises at run time with the same message.
  Side effect: a bare `{ status = …; headers = …; body = … }` literal now
  resolves to `HttpServerResponse` instead of reporting an ambiguity
  with `HttpResponse`.
### Fixed

- **Go to definition on a `within` or `until` binder's use** jumped to
  a same-named top-level binding (or nowhere) — the two binders now
  carry their spans, so the use inside the scope lands on the binder.

- An attribute on its own line above a multi-line record body
  (`[<Tag "kind">]` then `type R = {` with the fields below) was a
  parse error on the first field; it now assembles like the one-line
  form, so the error is the attribute's own.

## v0.0.78

Many of the changes and fixes below were found porting starship to weir.

### Added

- **Validated types: a type that only holds valid values.** Give a
  single-case union an indented `validate s = …` block that checks the
  payload and refuses with `fail "msg"` (any raise refuses, with its
  message; the block returns nothing and the value is stored exactly as
  given — normalise before `create` if you need to), and weir derives
  `Email.create : string -> Email` — the only way to build a value,
  inside the declaring module too (`Email "x"` is an error naming
  `Email.create`). When the block refuses, `create` raises its message
  at the call, like `Uuid.parse`; `Email.create s |> try result` gives
  `Error "not an email: x"`. Patterns still read the value anywhere.
  Every boundary runs the check: `from json`/`jsonl`/`yaml`/`toml` fail
  with the field path (`from json: users[0].email: not an email: x`),
  `Args.load`/`Env.load` name the flag or variable, and `to json`/`to
  yaml` write the payload. The block must be pure. Hover shows its `///`
  doc, `--help` shows `--to <Email>`, and a signature field whose type
  name says path, file or dir (`ConfigFile`) completes paths.
- **Tab asks the tool for values: branches, pods, remotes.** A
  signature with `let dynamic = "git"` lists refs and remotes where git
  takes them (`git checkout <Tab>`, `git push <Tab>`, and with `#alias
  co = git checkout`, `co <Tab>`), read by weir's own read-only git
  commands. `let dynamic = "cobra"` asks a Cobra tool (kubectl, helm,
  gh, docker) through its `__complete` endpoint, but only from a
  signature in your user directory (`weir add sig --global <tool>`): a
  project's signature is read without asking, so it never gets to run
  a program on Tab, and the REPL says once how to install it globally.
  `weir add sig` writes the line for a Cobra tool and for git. The tool
  is asked only for an operand or a flag's value, never for subcommands
  or flag names. It gets 300 ms and 1 MiB, and pressing another key
  stops it (the key is kept); with no answer you get what the signature
  alone gives. REPL only; the editor is unchanged.
- **`weir add module` vendors a module together with the files it
  imports.** A module whose relative imports (`./lib/config.weir`,
  `../fmt.weir`) used to be refused now vendors with them, every file
  fetched at the same commit, into `.weir/modules/<name>/` with the
  repo's layout, so the imports work unchanged:
  `weir add module github.com/weir-shell/ports//starship/starship.weir@main --as starship`,
  then `import "weir:starship"`. The lock records each file's hash;
  `restore` refetches a missing or changed file, `verify` checks every
  file, and running `add` again prints the old and new hash of each
  file and removes files the module no longer imports. A `weir:` import,
  an absolute path, or a path outside the repo inside a vendored file is
  refused, naming the file and line. A module without imports vendors
  exactly as before, to `.weir/modules/<name>.weir`. From any other
  host, `weir add module https://host/repo/<sha>/ --path lib/x.weir
  --as x` vendors imports too: the URL is the repo root and `--path` the
  entry file in it. A raw URL without `--path` is still one file,
  fetched as written. A lock with such a module is `schemaVersion` 2,
  which older weir refuses to read rather than misread.
- **An indented line that starts with a command is flagged.** In
  `git push origin a` followed by an indented `git push origin b`, the
  second line's words are more arguments of the first `git push`: one
  command ran, and failed only when it ran. `weir check` and the editor
  now warn (`continued-command`) at the second `git`, a script prints
  the warning and runs as before, and the REPL refuses the entry before
  anything runs. Flags, quoted words, paths, `|` lines and commands
  that take a command (`sudo`, `env`, `xargs`, `ssh`, …) are not
  flagged.
- **`repeat interval=1s` — a loop that runs until the program is
  asked to stop.** It runs its block, waits the interval and runs it
  again. The first Ctrl+C, `kill -TERM` or SIGHUP does not cut it: the
  iteration in hand finishes, the code after the loop runs, and the
  script exits 0. A second signal stops it at once, as before. A
  command running in the iteration is left to finish on a `kill -TERM`;
  one a terminal Ctrl+C kills ends the loop as the stop, not as a
  failure. Every `repeat` in a `Seq.piter` arm stops the same way. A
  `bool` block, `until` and `timeout=` work as in `poll`. At the
  prompt, the first Ctrl+C stops a `repeat` and the prompt returns; a
  second cuts it as before.
- **`until stopped` holds a `within` scope until the program is asked
  to stop.** Written after the block of any `within` (`serve`, `proc`,
  `tmp`, the bare one…), it keeps the scope open once the block is
  done; the first Ctrl+C, `kill -TERM` or SIGHUP closes it the normal
  way (a server answers the requests in flight, `always` runs whole),
  the code after it runs, and the script exits 0. An `always` cleanup
  goes after `until stopped`.
- **`|> orFail`, the inverse of `|> try`.** `Map.tryGet "port" cfg |>
  orFail "no port"` gives the value inside an `Ok` or a `Some`, and
  stops on `Error msg` (with `no port: msg`) or `None` (with `no
  port`). Without a message, `Error msg` stops with `msg` and `None`
  with `orFail: no value (None)`. Anything other than a
  `Result<'T, string>` or an `Option<'T>` is a check error. The error
  points at the `orFail`, and `|> try` catches it like any other.
  After a command the same word is still the reifier, `cmd | orFail
  "msg"`. `orFail` is now a keyword, so it can't be used as a name.
- **`Path.isName` and `Path.toName` for file names.** `Path.isName s`
  is true when `s` is one file name valid on Linux, macOS and Windows:
  not empty, `.` or `..`, no `/`, `\`, control character or
  `< > : " | ? *`, no trailing space or dot, not a Windows device name
  (`CON`, `nul.txt`, `COM1`…), at most 255 UTF-8 bytes. `Path.toName s`
  turns any text into such a name: forbidden characters become `_`,
  trailing spaces and dots go, a device name gets a `_` prefix, and the
  cut to 255 bytes never splits a character. A valid name comes back
  unchanged. Both work inside `pure`.
- **`File.readFrom path offset` reads a growing file in steps.** It
  returns a `FileChunk`: `lines`, the complete lines from that byte
  offset up to the file's size when the read starts, and `next`, the
  offset to pass next time. A last line still missing its newline is
  left for the next call, so a reader polling a file that `File.append`
  writes to gets each line once and whole, and no line is returned
  twice. A record still missing its newline has no line boundary to
  stop at, so each call re-examines it until the newline lands; gate a
  poll on `File.size` growth to keep it cheap. Lines decode as
  `File.read`'s do. An offset past the end (the
  file was truncated or replaced) or not at the start of a line
  raises. `FileChunk` is now a built-in type name.
- **`import` works at the REPL prompt and in `weir -e`.** A relative
  path resolves against the session's current directory when the
  import runs (in `-e`, where weir was started), and `weir:<name>`
  finds the nearest `.weir/` above it. The module rules are a script's.
  Running the import again after editing the module reloads it.
  `#help Alias.member` shows the member's `///` doc, and `#save` keeps
  the import with its path written out absolute.
- **`pfor x in xs do …`, the parallel `for`.** It is exactly
  `xs |> Seq.piter (fun x -> …)` written as a loop: the same binder
  patterns and indented body as `for`, and everything else from
  `Seq.piter` — up to 64 arms at once (fewer when nested; use
  `Seq.piterWith n` to pick a number), a unit body, arms finishing in
  any order with their output interleaved, the first error by input
  order raised after every arm has run, refused inside a `plan`, and
  the same Ctrl+C/SIGTERM cleanup. It runs effects only:
  `pfor x in xs -> e` and a `yield` body are refused with a pointer to
  `Seq.pmap`. `pfor` is now a keyword, so it can no longer be a name.
- **`--help` shows what each subcommand does.** A `///` doc on a union
  case is printed beside its subcommand, aligned, and `tool.weir pub
  --help` shows it as the description (a case with no doc uses its
  payload record's doc). A `///` doc on the `Args.load` type itself is
  the program's description, shown under the usage line (its first
  paragraph).
- **A warning when a script acts before reading its arguments.** `weir
  check` (and the editor, and a run) warns about a top-level statement
  above the first `Args.load` or `Env.load` that writes files, runs a
  command or uses the network: it would still run on `--help` or a bad
  argument. Reads, printing, function definitions and a `within` that
  wraps the load are fine.
- **`PromptStatus.duration`: how long the last entry took.** A
  `PromptStatus -> string` prompt can show it the way starship's
  `cmd_duration` does — `if st.duration > 2s then $"took
  {st.duration} " else ""`. It is `0ms` before the first entry.
- **`\e` and `\u{…}` string escapes.** `"\e[1m"` is the ESC that starts
  a terminal escape sequence, and `"\u{e9}"`/`"\u{1F600}"` write any
  character by its code point (1 to 6 hex digits), in `$"…"` strings
  too. A UTF-16 surrogate (`\u{D800}`) or a value past `\u{10FFFF}` is
  a parse error that says so, and an unknown escape's error now lists
  every escape.
- **`Str.rreplace pattern replacement s` replaces every regex match.**
  In the replacement, `$1` or `${1}` is a capture group, `$0` the whole
  match and `$$` a literal `$`: `Str.rreplace @"(\w+)@(\w+)" "$2 at $1"`.
  Any other `$`, or a group the pattern doesn't have, is an error.
  `Str.replace` stays literal.
- **SHA-1: `Str.sha1`, `Bytes.sha1` and `File.sha1`,** beside the
  `sha256` members and with the same output as `sha1sum` (lowercase
  hex; `File.sha1` streams the file). For protocols that use SHA-1,
  such as git object ids; it is not safe as a security hash.
- **`Path.which name`** gives the full path of the program a command
  name would run (`Some "/usr/bin/git"`), or `None`. It looks the name
  up the way running it would, so inside `within env PATH=…` it
  searches that `PATH`, and on Windows it applies `PATHEXT`.
- **`Str.width s`** is the number of terminal columns `s` takes when
  printed: 2 for a wide CJK or fullwidth character or an emoji, 0 for a
  combining mark, a control character or a colour escape, and one per
  emoji however many code points build it (a family, a flag). Use it to
  line up text that `Str.length` (code points) would misalign. The REPL
  measures a custom prompt with the same table.

### Changed

- **A `Map` takes any key with equality.** `Map<int, string>`, a map
  keyed by a union (`ls |> Seq.countBy _.kind |> Map.ofPairs`), a
  tuple or a record now work, with every `Map` function. A `float`
  key, or a record holding one, is refused as `==` refuses it. Keys
  come out in a fixed order: scalars as `Seq.sort` orders them, union
  cases by case name (not declaration order, as F# has it). JSON
  still takes string keys only: `to json`/`from json` of a
  `Map<int, …>` is an error that shows the `show k` conversion.
- **Two imports can declare the same type name.** Each is reached by
  its qualified name, `A.LangDefaults` or `B.LangDefaults`, in every
  place a type is written: record fields, union cases, signatures, the
  `from json`/`from yaml` slot (`from json A.Config` used to be refused),
  `Env.load`/`Args.load` and record literals. The two are different
  types even with the same fields. The plain name still works while
  only one import declares it; when two do, using it is an error that
  lists both qualified names. Before, importing the second module failed
  with "cross-module same-name types are not yet distinguishable". A
  type you declare above an import of the same name now keeps the plain
  name instead of failing, and at the REPL a session `type` over an
  init import's type leaves the import's type reachable qualified.
- **A bad command line is a usage error for whoever runs the script.**
  `Args.load`'s errors no longer look like a crash in the script: no
  `journal.weir:14:1: error: Args.load Cli:` prefix, but the script's
  name, one line per problem, the list of commands or the help lines
  of the flags involved, and `run 'journal.weir --help' for usage`.
  The exit code is 2 (it was 1). A bad value is reported once, no
  longer also as a missing flag, and `--mode pub` for the subcommand
  says to write the command bare.
- **`--help` leaves out empty sections.** A script with subcommands but
  no shared flags prints `usage: j3.weir <command>`, with no empty
  `global options:` and no `[global flags]`.
- **A record field written bare points at the field.** `mode` where
  `args.mode` was meant now says "unbound 'mode' — did you mean
  'args.mode'? ('mode' is a field of 'Cli')" instead of suggesting
  `File.mode`; with no value of the record bound yet, it says to load
  it first.
- **The waiting spinner is the REPL's alone.** A script (or `weir -e`)
  at a terminal no longer draws it while weir waits in
  `Duration.sleep`, `Http.send` or a `retry`/`poll` delay, so its
  stderr carries only what the script writes. The REPL still shows it.
- **`def` is an ordinary name.** `let def = 1` and a field called `def`
  work. A line shaped like a definition, `def double x = …`, still gets
  "'def' is not a weir word — define with 'let f x = ...'".
- **`Path.home`, a typed `~` and `Path.configHome`/`stateHome`/
  `cacheHome` follow `within env`.** Inside `within env HOME=/x` they
  give `/x` (and the `XDG_*` variables the block sets), as `Env.get`
  already did.
- **A command runs from the `PATH` it is given.** Inside `within env
  PATH=…`, or with a `PATH=… cmd` prefix, the program is looked up on
  that `PATH`, as the child itself would look it up, so a wrapper put
  first is the one that runs. This covers `^$tool` too, which used to
  report "command not found" for a tool only the overlay's `PATH` had.
  `weir check` still checks literal command names against your own
  `PATH`.
- **`weir gen types` drafts a string-or-list `anyOf` as `Yaml`.** A
  schema field that takes a string or an array of strings (starship's
  `custom.shell`, `python.python_binary`) used to draft as `string`
  and reject every list. It is now `Yaml`, with a note saying how each
  kind reads.
- **A case of an imported union can be written qualified in a
  pattern.** `| Contract.Ghost x ->`, `let (Contract.Ghost g) = …`,
  `let f (Contract.Ghost y) = …`, lambdas and nested patterns all take
  it; before, a match arm refused it ("a pattern names a case bare") and
  a `let` pattern failed with "Expecting: identifier". The qualifier is
  the import's name and must match the value's type: a case of another
  import's union is an error naming both types. With two imports that
  declare unions with the same case name, the qualifier says which one
  you mean; a bare case there, with nothing typing the value, is an
  error that lists both qualified spellings.
- **Error codes say what they mean.** The code in `error [code]:` and
  in `weir check --json` is renamed where it was weir's internal
  vocabulary: `casing-law` is now `lowercase-binding`, `seq-unit`
  `non-unit-statement`, `ord-key` `unsortable-key`, `eq`
  `not-equatable`, `show-fn` `unrenderable-function`,
  `ambiguous-constraint` `undetermined-type`, `regex-arity`
  `regex-binder-count`, `assembly` `layout`, `sig-orphan`
  `missing-implementation`, `sig-doc` `doc-on-implementation`,
  `session-decl-ref` `session-names-declaration` and `module-rule`
  `module-declares-only`. If a script or editor setup matches on a code,
  update it.

### Fixed

- **A signal no longer leaves a `within proc` scratch directory
  behind.** A second SIGTERM arriving while parallel `within proc` arms
  were starting could leave a `weir-proc-…` directory holding only
  `err.log` in the temp dir; both log files are now created with the
  directory, before any teardown can sweep it.
- **An imported union's case binds in `let` and parameter patterns.**
  `let (Ghost g) = ghost` and `let f (Ghost y) = y`, with `Ghost` from an
  imported module, failed with "binding names start lowercase"; they now
  resolve the case as `match` does. A multi-case imported union is
  refused there as a pattern that can fail, as a local one is.
- **`| line`'s error shows a newline in the command line as `\x0a`.**
  `printf "a\nb\n" | line` used to print the argument's raw newlines
  inside the quoted command; it now escapes them, as every other error
  that quotes data does.
- **A module function whose signature returns `unit` but whose body
  ends in a command gets a hint.** The command's output is the
  function's value, so the mismatch error now says "its last line is a
  command … end the body with `()`".
- **`#help` shows the `///` doc of a module member imported by
  init.weir.** `#help Git.refresh` printed only the signature; it now
  prints the doc written above the member's signature, as it already
  did for init.weir's own declarations.
- **A lambda closes when a list or record closes on the same line.**
  `Seq.map (fun r ->` … `])` or `})` left the lambda open ("'(' is
  still open"); the `)` after the closing bracket now closes it, as on
  a line of its own.
- **A tuple match that covers every case needs no catch-all.** Arms
  for all four `Some`/`None` pairs, or all four `bool` pairs, now
  complete a match on `(a, b)`. When a case is missing, the error
  names one, such as `missing: (None, Some _)`.
- **A module checks the same through an import as on its own.** A
  module using a command that is not installed checked clean alone,
  but failed when the importing script was checked.
- **`weir check` warns about a missing command inside `$(cmd |
  complete)`** (and the other reifiers), as it does for a plain
  command.
- **A missing command gets the usual "unknown command" message at run
  time** inside `$(… | complete)` and in a function body, suggesting
  `^$tool`, instead of a parser expecting-list or "unbound variable".
- **`from yaml` reads a string that holds several lines,** as `from
  json` and `from toml` do (`[Str.join "\n" lines] |> from yaml T`).
  `Yaml.parse` and `Yaml.inferShape` too; error line numbers count the
  lines of the joined text.
- **A bare `split`, `map` or `where` after a command chain gets the
  bare-name message.** In `git … | line |> split "/"` the error said
  "'|>' applies functions; feed a program with '|'"; it now says
  "'split' is a bare module member — spell it 'Str.split'", as it does
  after a value. `weir check` and the run now agree on a two-module
  name such as `xs |> contains x` ("'contains' is module-qualified"),
  and a file whose first line is such a slip no longer opens with "this
  does not look like a weir script".
- **Python's `(?P<name>…)` named group gets weir's named-group
  message** (name captures in the binder) instead of .NET's
  "Unrecognized grouping construct".

## v0.0.77

### Added

- **`Term.title` sets the terminal window title.** Call it from the
  init.weir `prompt` function to show the current directory (or
  anything else) in the title on every prompt:
  `Term.title $"weir: {pwd}"`. Control characters, escape sequences and
  bidi controls in the text are dropped and it is cut to 256
  characters; with no terminal it does nothing. On Windows it sets the
  console title.
- **A command's signature completes its arguments.** In the REPL and
  in the editor, `git com<Tab>` offers `commit`, `git commit --am<Tab>`
  offers `--amend`, and after a flag that takes a value (`--file <Tab>`,
  `--color=<Tab>`) you get paths for a file, the choices the signature
  lists, or nothing. A negatable tool's `--no-x` forms appear once you
  type `--no-`. The REPL looks a signature up by the command name:
  the project's `.weir/sigs/`, then `$XDG_CONFIG_HOME/weir/sigs/`. A
  session alias completes as what it expands to: with `#alias co = git
  checkout`, `co -<Tab>` offers checkout's flags.
- **`weir add sig --global <tool>`** writes the signature to
  `$XDG_CONFIG_HOME/weir/sigs/`, so the REPL completes that tool in
  every directory. It gets no lock entry; `weir verify`/`restore` and
  `#sig` never read it.

### Changed

- **No `(F1: keys)` hint beside the first continuation line, and no
  indentation note under `#keys`/F1** — the list is just the bindings.

### Fixed

- **The REPL colours the code inside a `$"…{hole}…"` string as code,**
  not as part of the string, and a hole in a command's arguments is not
  dimmed like a literal argument.
- **REPL Tab completes across the lines of an entry.** In a multi-line
  entry, a name bound on an earlier line (`for p in … do`, `fun x ->`,
  `let rows = ls`) now completes its fields on a later line, also inside
  an interpolation hole: `print $"THIS: {p.<Tab>}"` offers `name`,
  `kind`, `path` and the rest. The editor used to look at the current
  line only.
- **`_.<Tab>` as an argument offers the element's fields** when the pipe
  follows a `for … in` header or a `let x =` (`for p in ls |> map _.<Tab>`),
  and when the source is bound on an earlier line or the pipeline spans
  lines (`|> Seq.map _.<Tab>`). A bare `|> _.` stage over a seq
  (`ls |> _.<Tab>`) now offers nothing: it can never type-check. The
  editor (LSP) does the same.

## v0.0.76

### Added

- **`#keys` lists the REPL editor's key bindings** (Ctrl+Y, Ctrl+R,
  Alt+Enter and the rest), so you don't have to look them up.
- **F1 shows the key bindings in the middle of an edit.** The list
  prints below the buffer, then the buffer comes back as it was, every
  line and the cursor. The first multi-line entry of a session shows a
  one-time `(F1: keys)` hint.
- **The REPL editor indents for you.** A new line (Enter on an
  unfinished statement, Alt+Enter, Ctrl+J) starts at the indentation of
  the line above it, one level deeper after a line that opens a body
  (`let x =`, `->`, `then`, `else`, `do`, `with`, `within …`, `always`),
  and Backspace in leading spaces goes back one level (4 columns).
  Pasted text keeps its own indentation.

### Fixed

- **A URL password with a raw `/`, `?`, `#` or `@` no longer prints.**
  `show`, plans, "cannot reach" errors, `weir add` errors and the
  bad-proxy message masked only up to the first of those characters, so
  `http://deploy:Tr0ub/4dor@host` printed whole. They now mask through
  the last `@` before the host (`http://***@host/…`), for `//user:pw@host`
  and a scheme-less `user:pw@host` proxy value too — including a password
  that starts with digits (`http://u:2024/pw@intranet`, which reads like
  `host:port`). The cost: a credential-free URL with a port and an `@` in
  its path (`http://localhost:3000/users/@me`) also shows masked.
- **A malformed proxy variable is an error even when another one is
  set.** `http_proxy=http://h:99999` beside a working `ALL_PROXY` sent
  the request through `ALL_PROXY`; it now raises, naming `http_proxy`.
- **`weir add` can no longer tear or lose `.weir/lock.json`.** A signal
  during the write could leave a truncated lock, and parallel `weir add
  sig` runs dropped each other's entries. The lock and a generated
  signature are now replaced whole (temp file, then rename), and adds
  take turns.
- **Messages show control characters in the data they quote, piped
  too:** `weir gen types`/`fmt`/`check` usage errors, the `#sig`
  unknown-flag warning, `cannot resolve import: no file at …`,
  `command not found: …`, and a remote schema's unresolvable `$ref` in
  `weir add schema`.
- **`weir gen types` leaves out a `""` property with a note**, as
  `#infer` does, instead of failing as a "generator bug". A property
  name with a bidi control character is left out the same way.
- **`weir verify` says `weir add sig '<tool>'` for a missing
  signature** instead of `weir restore`, which cannot bring a signature
  back. A generated signature no longer lists a subcommand twice in
  `stopAtOperand`.
- **A plan treats a moved symlink as a link.** After `Dir.move` or
  `File.move` of a link, the plan treated the new path as a real
  directory or file. So a plan that then deleted the link's target was
  captured and failed at apply, after its earlier steps had run. And
  `Dir.copy b b/b/L`, where `b/b` was a moved link to a directory
  outside `b`, was refused as a copy into itself. Both now behave as
  the direct calls do.
- **A plan that writes through a dangling symlink can copy it next.**
  `File.write "b"` with `b -> nowhere` creates `nowhere`, but a
  following `File.copy "b" "c"` in the same plan was refused as a copy
  from a dangling link. It now applies.
- **A write through a symlink loop (`ln -s b b`) is refused when the
  plan captures it**, not when it applies, with `Too many levels of
  symbolic links`. The direct call gives the same message.
- **`$` completion offers nothing where `$` is plain text**: in `//`
  comments, `<<<` and `$<<<` text blocks, and, inside yaml blocks,
  quoted values, `#` comments and `|` block text. This applies to the
  editor (LSP) and to REPL Tab.

- **A SIGHUP sent to weir's process group under heavy CPU load is the
  teardown, not "command failed … 129".** Its handler could start late
  enough that the dying child read as a failure — a cleanup stopped, or
  `|> try` swallowed the signal and the script went on.
- **A wide `Seq.pfirst` over `| complete` arms uses one thread per arm
  again,** and a thread the OS refuses (a pids/`ulimit -u` limit) is a weir
  error naming the limit instead of .NET's "TaskScheduler" text.
- **A cleanup command at a terminal refuses a `#!` script whose
  interpreter chain does not end in a runnable program,** as the same
  command does in the body.

- **Tab on a continuation line no longer offers `#` directives.** A
  directive is only valid as an entry's first line, so an empty or
  indented continuation line completed `#help` & co. for nothing.

## v0.0.75

### Added

- **`#types [on|off]` shows or hides types in the REPL echo.** Bare
  `#types` reports the setting; `"types": "on"` in the REPL config turns
  them on at startup, and the directive overrides it for the session.
- **`Env.need` and `Env.getOr` read one variable as a string.**
  `Env.need "TOKEN"` fails at that line with `Env.need: TOKEN is not
  set` when it is unset; `Env.getOr "PORT" "8080"` gives the default
  when unset. A set-but-empty variable gives `""` in both, as with
  `Env.get`, and `within env` applies.
- **`Env.load` and `Args.load` take an inline type:**
  `Env.load {| TOKEN: string; PORT: int; CI: Option<string> |}` — no
  `type` declaration needed for a small script. An anonymous type takes
  no attributes; `{| [<Default 3>] a: int |}` now says to declare a
  `type` instead of failing with a generic parse error.

### Removed

- **`nats` is gone; use `Seq.initInfinite`.** F#'s name: `Seq.initInfinite f`
  is the endless sequence `f 0, f 1, …`, read lazily. `nats` is
  `Seq.initInfinite id`; `nats |> Seq.map show` is `Seq.initInfinite show`.
  The name `nats` is no longer reserved.
- **`weir run <script>` is gone** — it was undocumented; `weir <script>`
  does the same.

### Changed

- **A URL's `user:pass@` is sent as Basic auth.** `Http.send` and the
  `weir add schema`/`add module` fetches send it as `Authorization:
  Basic`, percent-decoded, as curl does; it was silently dropped. A
  request that sets Authorization itself keeps its own. Displays still
  mask it, and the lock still leaves it out, so `restore` sends none.
- **`add`, `restore` and `verify` messages escape control bytes piped
  too.** A URL, name or version carrying ESC, BEL or CR now shows them
  as `\x1b`, `\x07`, `\x0d` in a pipe or log, as at a terminal; `print`,
  `to json` and file writes stay byte-for-byte.
- **The REPL echo no longer shows types by default.** `2` echoes `2`
  (was `2 : int`), `let x = 5` echoes `x = 5` (was `x : int = 5`), a
  seq's lines end without the `: seq<string>` line (truncation notes
  still show), and `print "a"` is no longer followed by `() : unit`.
  `#types on` restores the old echo exactly. Piped REPL output is
  unchanged.
- **`weir add sig` signatures stop warning on valid flags.** Generation
  reads each tool's own self-description (Cobra's `__complete`, git's
  completion helper, fish completions, `--help all`, `--help-all`,
  `--v8-options`, `--dump-options`), walks subcommands only from the
  tool's command tables, and records which flags take a value, the
  tool's flag convention (GNU, Go, find) and where its own options end.
  The check reads a flag's value word, checks short bundles letter by
  letter, stops at a subcommand the signature does not know, and an
  older signature checks exactly as before. On the survey's twenty
  common tools the scored lines went from 76 false warnings to none. npm
  (and npx) take any config key as a flag, so their signatures are
  generated with no flag checked. Wrappers (`timeout`, `flock`, `stdbuf`,
  `watch`, `sudo`, `systemd-run`, `run0`, `chrt`, `strace`, `uvx`, `bunx`,
  `bash script.sh`) stop checking at the command they run; kill's
  `-TERM` (while `kill -x` still warns), perl's `-MJSON::PP`, unzip's
  two-column help, shfmt's aligned rows, Go flags shown only in
  examples, node's `--deprecation`, systemctl's `--no-legend` (from
  `--legend=BOOL`) and git's `remote add`/`stash push`-style
  subcommands are read; `git config`, `git stash` and `git worktree`
  check their own flags, and `git stash show` passes diff options. A
  generated signature says `let bundles = true`, so `tr -dQ` warns on
  `-Q`.

- **`serve` runs on Kestrel** (ASP.NET Core's server, part of the .NET
  SDK) instead of `HttpListener` — one server on Linux, macOS and
  Windows, and the binary is about 1.7 MB larger. What a client can
  notice: responses carry `Date` and no `Server` header; request
  headers over 32 KiB get 431 (was 400); a NUL byte in the request
  target gets 400; a request whose `Host` is not `127.0.0.1`,
  `localhost` or `[::1]` gets a plain `404 unknown host`; a repeated
  header's values keep their order, but different header names may
  reach the handler in another order than sent. Closing the block
  frees the port at once and gives running handlers two seconds,
  together, to finish.
- **`weir add schema` accepts validation keywords.** `minLength`,
  `maxLength`, `pattern`, `minimum`, `maximum`, `exclusiveMinimum`,
  `exclusiveMaximum`, `multipleOf`, `minItems`, `maxItems`,
  `uniqueItems`, `minProperties`, `maxProperties`, `readOnly`,
  `writeOnly`, `deprecated` and `$comment` no longer refuse the whole
  schema (real GitHub workflow and Kubernetes schemas use them). They
  are not enforced; `weir gen types` writes one
  `// note: not enforced: …` line per keyword.
- **`weir gen types` drafts an int-or-string field as `Yaml`** (a
  Kubernetes port, `x-kubernetes-int-or-string`), so `from json` reads
  `8080` and `to yaml` writes it back unquoted; it was `string`. An
  enum of integers drafts `int` instead of `string`.
- **`File.writeAtomic` inside a `plan` records a `WriteFileAtomic` op**
  and `Plan.apply` replaces the file atomically, as the direct call
  does; it recorded `WriteFile` and applied an in-place write, so a
  hard link saw the new content.
- **`from toml` refuses a date-time without an offset into an
  `[<Iso8601>] Instant`** — `start = 2024-03-01T10:00:00` read as
  10:00 UTC. Add an offset, or read it into a `string` field.
- **The init file refuses bare names.** It is a script, not typing at
  the prompt: `let evens () = xs |> where …` loaded before and now
  needs `Seq.where`, as in any script. Bare names still work at the
  prompt, including for names the init file defines.
- **An alias target refuses `|`, `|>`, `&&`, `||` and `;`.** An alias
  is one program plus fixed words, so `#alias inf = git add . | and
  git commit …` passed `|`, `and`, `git`… to `git add` as arguments.
  The error names the fix: `let inf () = …`.
- **At the REPL, Ctrl+C stops any wait, not only a running command.**
  `Duration.sleep`, a `poll` or `retry` wait, a `within lock` that is
  waiting for the lock, `Proc.wait` and an Http request are cut, and
  the prompt returns with `↳ exit 130 (SIGINT — interrupted/cancelled)`.
  Before, the session was stuck until the wait ended. An `always`
  cleanup runs unless Ctrl+C is pressed again; pure computation still
  runs to its next wait.
- **`pfirst` stops the losers and waits for them.** After the winner, a
  loser stops at its next builtin call, command, sleep or Http send (a
  request in flight is cut), and `pfirst` returns only once every loser
  has stopped — so no loser's write lands after the race. A loser busy
  in pure computation now delays the return until it reaches one.
- **An env key that is empty or contains `=` is an error**, naming the
  key, in `Env.pair`, `Env.ofPairs`, `within env` and `$e(…)`. The child
  read `FOO=BAR` as the variable `FOO` (so `PATH=…` replaced `PATH`).

### Fixed
- **A REPL line typed while an entry is still running submits.** Its
  Enter arrived as a newline key, so the line, and every line after it,
  grew a `…` continuation and the session could no longer submit.
- **`Args.load` no longer repeats a Secret given as `-t=value` or
  `-tvalue`.** The error now reads `'-t***' — … write '-t <value>'`; an
  unknown letter shows as `'-x…'`.
- **A malformed `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` is an error.**
  `http://h:99999` was ignored and the request went out directly; it
  now raises, naming the variable, as curl refuses.
- **A YAML round trip keeps a float as written.** `Yaml.parse |> to
  yaml` rewrote `1e5` as `100000.0` and `+.5` as `0.5` (a YAML 1.1
  reader such as PyYAML reads both as strings, and a short git SHA can
  be `1e5`), `3.10` as `3.1`, and quoted `1e400`. They now come back
  unchanged; `to json` still writes the numbers.
- **`to json` of a YAML integer beyond int64 writes its digits.**
  `012345678901234567890` failed with .NET text, and `0x8000000000000000`
  / `0o1000000000000000000000` came out as strings; all write decimal
  digits, as a decimal one does.
- **A YAML round trip keeps plain keys plain.** `Yaml.parse |> to yaml`
  wrote `200:`, `true:`, `null:` and `0644:` back as `"200":` and so on,
  turning a 1.1/1.2 reader's int, bool or null key into a string. A key
  the document wrote plain now comes back plain; a key built in weir is
  still quoted when a reader would type it.
- **`from yaml`: a value of only a no-break space (or U+3000) is that
  string**, not null; a **`float` field refuses `0644`** as an `int`
  field does, instead of reading 644.0.
- **`Yaml.inferShape` and `#infer` draft a type that reads its own
  sample.** `mode: 0644` drafted `int`, which then refused it; it now
  drafts `Yaml` (which keeps the text), with a note. `0x1F`/`0o17`
  draft `int`, `.inf` drafts `Yaml`, `+.5` drafts `float`.
- **`from xml` of UTF-8 bytes labelled `utf-16` says the declaration
  is wrong** instead of failing with garbled text, and a document nested
  deeper than 500 elements is refused at once instead of taking seconds
  to minutes.
- **`weir gen types` handles any property name.** A name with `"` or `\`
  failed as a "generator bug"; it now drafts an escaped `[<Wire>]`. A
  name with a control character (CR, ESC, …), which no weir string can
  spell, is left out with a note, and notes show such characters
  visibly. `format: uint64` beside `null` in an `anyOf`/`oneOf` drafts
  `Option<Yaml>`, which keeps a value past int64.
- **Windows: a `Seq.pfirst` loser's `| complete` command no longer
  outlives the race.** A loser waiting in `sh -c "…" | complete` was
  stopped without killing its command, so `sh` and what it started ran
  on after the winner returned.
- **Ctrl+C at the REPL between two statements of an entry stops it.**
  Landing just after one statement ended (often right as a `| exitCode`
  command exited), it was read as a typed key: the entry ran to its end
  and `^C` appeared at the next prompt.
- **A pasted multi-line REPL entry no longer loses its first lines.**
  When Alt+Enter's two bytes arrived apart, the Esc was read alone and
  threw the buffer away; an Esc whose Enter is already waiting (a
  paste, a real Alt+Enter) is now the newline. Esc then Enter typed by
  hand still abandons the buffer.
- **Ctrl+C at the REPL on a command inside a `within` body shows
  `↳ exit 130`.** It printed `error: command failed with exit code 130`.
- **A group-directed SIGTERM or SIGHUP no longer cuts an `always`
  cleanup short.** Without a terminal it killed the cleanup's running
  command too, which failed the cleanup ("command failed with exit code
  143") and skipped its remaining steps; that death is now the signal's,
  the cleanup finishes, then weir dies by the signal.
- **A second signal while a cleanup's pager has the terminal restores
  the terminal.** The hard exit killed the pager and left the terminal
  without echo.
- **REPL: Tab after `$` offers only what can be spliced there.** `git
  tag $t<Tab>` listed every Prelude function (`take`, `toInt`,
  `tryHead`, …) beside your own bindings. After `$` in command argv
  Tab now offers just the bindings a `$x` argument accepts (string,
  int, bool, Secret, Uuid; `$r.<Tab>` offers a record's fields that
  qualify); after `$@`, `seq<string>` bindings. Functions and bare
  records are never offered, nor anything inside `"…"` or glued after
  `--x=`, where `$` does not splice. The same holds after a builtin head
  (`ls`, `cd`), a `^`-forced head, a let's command RHS, and inside
  `$(…)` and `if`/`for`/`match` bodies. In an editor, `$` inside a yaml
  block offers what a yaml splice accepts.
- **`weir add sig` cannot hang or run out of memory on a tool's help.**
  A help that leaves a daemon holding its output, or prints without
  end, hung generation or grew it to gigabytes. Each probe now gets 15s,
  then 1s to drain after the tool exits, and reads at most 4 MiB per
  stream (the signature notes a cut, or a probe stopped at 15s — what
  it printed by then is still read); the probe's process group is
  killed. Probes run in a fresh empty temp dir, not in the shared temp
  dir itself. Ctrl+C or a signal to `weir add sig` kills the running
  probe and removes its temp dir, and each probe runs once.
- **Signature file names no longer collide.** `#sig /a/b/cob` and
  `#sig _a_b_cob` (or `.weir` and `weir`, `a b` and `a_b`) wrote the
  same `.weir/sigs/` file. Characters outside letters, digits, `-`,
  `_` and a non-leading `.` are now written as `%XX`; a plain name's
  file is unchanged, and a signature written under the old name is
  still found through the lock. A name too long for the file system is
  shortened with a hash, and a file the lock records as another tool's
  old name is never read by `#sig` for this one; `weir add sig` moves
  it to its own tool's name instead of overwriting it.
- **`serve` answers 415 to a request body in a charset weir cannot
  decode.** It was decoded as UTF-8, a wrong byte becoming U+FFFD, and
  handed to the handler; now no handler runs, as the HTTP client
  refuses such a response.
- **A `plan` refuses an op that would fail at apply, where it is
  captured.** A copy onto an existing (or planned) file, a delete of a
  missing (or already deleted) file, a copy or move from a missing
  source, `Dir.delete` of a directory the plan filled and a write
  under a missing directory (or through a symlink into one) were
  captured, previewed as ordinary steps and failed midway through
  `Plan.apply`. Each now raises at capture with the same error the
  direct call gives. `Dir.copy` into a missing parent directory now
  refuses like `File.copy` and `Dir.move`, in a plan and outside one.
- **A `plan` deletes or moves a dangling symlink itself,** as the
  direct call does; it refused with "no such file". A plan writing
  deep under a copied directory, or through a symlink, no longer slows
  with the path's depth squared.
- **`from xml` reads a document in its declared encoding.** Pipe the
  bytes: `File.readBytes "legacy.xml" |> from xml Proj` decodes by
  BOM, else the XML declaration (UTF-16/32, US-ASCII, ISO-8859-1), else
  UTF-8. A text document declaring a non-UTF-8 encoding read `café` as
  `caf�`; it now refuses unless it is ASCII, and any other encoding
  (windows-1252, Shift_JIS, …) refuses naming it.
- **`File.*` and `Dir.*` resolve `..` after a symlink as the kernel
  does.** With `L -> real/sub`, `File.read "L/../x"` read `./x` while
  `cat L/../x` read `real/x`. `cd` keeps the shell's logical `..`.
- **`to yaml` quotes every string a YAML reader would type.** `+.5`
  and `+.5e3` read back as floats (weir's own reader too), `y`/`n`
  as booleans in sigs.k8s.io/yaml, and `<<`/`=` broke PyYAML. Keys
  follow the same rule.
- **`to yaml` writes a `None` value in a seq of pairs as `null`.** The
  key was dropped, so it did not read back.
- **An integer beyond int64 in a `Yaml` node keeps its digits** when
  written back to JSON or YAML (it became `1.8446744073709552e+19`).
- **`from toml` and `Instant.parse` accept lowercase `t`/`z` and a
  time without seconds** (`1979-05-27t07:32z`, TOML 1.1's
  `07:32Z`).
- **A YAML `"\ud800"` escape is refused,** as JSON refuses it; it was
  read as U+FFFD.
- **`weir gen types` and `weir add schema`: `anyOf`/`oneOf` of a
  schema and `null`, in either order, drafts `Option<…>`** of that
  schema (a `$ref`, array or object included); an enum containing
  `null` drafts `Option<…>` too, and its note says `null`.
- **A `$ref` with sibling `properties`/`required` (JSON Schema
  2020-12) is merged** into one record; the siblings were dropped.
- **Deep in-document `$ref` pointers and `#` resolve** (tsconfig,
  package.json, the GitHub workflow schema).
- **A `oneOf` whose `$ref` side is a string drafts like the inline
  form** (GitHub Actions' `cancel-in-progress`), and a `oneOf` with one
  alternative per JSON kind drafts `Yaml`.
- **The piped REPL no longer runs a `Self.prompt` answer as a
  command** after an indented `|>` line, a `match` arm or a second
  `if` body line.
- **A second Ctrl+C or signal exits at once.** It ran the remaining
  `always` cleanups on top of the one already running (out of order,
  sometimes waiting out a `sleep 30` and needing a third signal); now
  no cleanup runs after it, every child is killed, and weir dies by
  the first signal.
- **A script stopped by a signal dies by it.** After its cleanups a
  scoped script exited 130/143/129; it now dies by the SIGINT,
  SIGTERM or SIGHUP itself, as an unscoped one did, so a bash loop
  around it stops at one Ctrl+C. `$?` is unchanged.
- **Ctrl+C no longer kills the commands an `always` cleanup runs.** At
  a terminal they start with Ctrl+C and Ctrl+\ ignored, so the cleanup
  finishes; they keep the terminal, so a pager in a cleanup works, and a
  program that handles Ctrl+C itself still can.
- **Ctrl+\ (SIGQUIT) runs the cleanups.** It dumped core, skipped the
  outer cleanups and left the `within tmp` directory; now it unwinds as
  SIGTERM does and weir dies by SIGQUIT, with no core dump.
- **A full or closed stderr no longer aborts weir.** With `2>/dev/full`
  or `2>&-` the final error line (or a `Log.*` line) aborted with a core
  dump, exit 134; stderr writes that fail are now dropped and the exit
  code is the script's own.
- **The REPL survives a terminal hangup and restores the terminal.** A
  hangup while an entry ran a command reported `internal error:
  IOException`; it now dies by the SIGHUP. SIGTERM or SIGHUP at the
  prompt left the terminal raw (no echo); it is now restored.
- **Ctrl+C at the REPL stops a multi-statement entry even when nothing
  was waiting** — during a cleanup, pure computation, or a child whose
  exit code is the value (`| exitCode`). The rest of the entry ran.
- **`File.sha256`, `File.copy`, `File.write`, `File.append` and
  `File.writeBytes` on a FIFO are cut** by a race win, a fan-out's exit
  and the first signal, as `File.read` is (Linux). They waited for the
  other end forever. `File.writeAtomic` on a FIFO refuses (an atomic
  replace has no meaning there; use `File.write`); it blocked.
- **`Proc.wait` returns when the process exits.** It woke every 50 ms,
  so a loop of short processes ran ~70x slower.
- **A command killed by a group-wide SIGHUP, SIGQUIT or SIGTERM is not
  reported as `command failed`** when the same signal is stopping weir.
- **A cleanup that fails during a signal's 1s backstop says so** ("the
  cleanup ALSO failed"), as it does on the script's own unwind.
- **`exit n` in a `serve` handler exits n.** It was reported as a 500
  (`failed: ExitRequest`) and the script went on; now the scope stops,
  its handlers finish their cleanups, and the script exits n.
- **Ctrl+C or a failure ends a whole multi-statement REPL entry.** The
  entry's later statements, commands included, still ran.
- **Commands run inside a `pmap`/`piter`/`pfirst` arm no longer grow
  memory.** Each one stayed registered until the arm ended (about
  2.5KB a command).
- **A `pfirst` arm reading a pipe no longer slows the read.** 10
  million lines through `cat` in a race arm took 4s instead of under
  one; every read is cut by its flow without a per-line cost.
- **A detached weir that never runs a command dies by `kill -INT`.** An
  inherited ignored SIGINT was reset only at the first command.
- **A `Self.stdin` race loser that read nothing no longer uses up
  stdin**, and a `Self.prompt` cut mid-line no longer glues its half
  line onto the next one a child or a later prompt read.
- **`weir gen types` drafts number-or-string fields and `format:
  int-or-string` as `Yaml`.** Kubernetes `resources.limits`, an HPA
  `target.value` and swagger's IntOrString drafted `string`, which
  refused `cpu: 1` on read.
- **`weir gen types` drafts an enum as its type.** An enum under
  `type: boolean` or `number` drafted `string` and refused `true` and
  `0.5`; it now drafts `bool`/`float`. An enum mixing kinds drafts
  `Yaml`, with a note.
- **`weir add schema` accepts a root `$id`, `const`,
  `contentEncoding` and `contentMediaType`.** Each refused the whole
  schema; `$id` is ignored, and the others (and `format`) get a
  `// note: not enforced:` line.
- **`to yaml` writes an all-`None` record as `{}` and an empty
  top-level seq as `[]`.** Both wrote nothing, so `to yaml stream`
  dropped the document and the text read back as something else.
- **Duplicate mapping keys.** A repeated key read into a
  `seq<string * T>` from JSON kept both pairs; it now last-wins at its
  first position. `to json` and `to yaml` refuse a pair-seq or `YMap`
  holding a key twice, naming the key, instead of writing a document
  weir then refuses.
- **A `plan`'s stale-read guard follows symlinks and nested plans.**
  A read through a symlink alias of a captured target (either way
  round) saw stale state; on macOS a different letter case did too.
  An inner `plan` now also refuses a read an enclosing plan's ops
  changed.
- **A `plan` read no longer slows with every captured op.** A
  30,000-file write-if-absent loop took 31 s; it is linear again.
- **Refusing JSON nested past 100 levels is linear.** A 100,000-deep
  document took 11 s to refuse (`from json`, `from jsonl`,
  `inferShape`); it now takes milliseconds.
- **`Path.normalize` treats a Windows drive root as a root.** On
  Windows, `Path.normalize "C:/../y"` gave `C:/../y`; it is now `C:/y`,
  as `/../y` is `/y`.
- **`Path.glob` takes a native Windows directory prefix.** On Windows,
  `Path.glob (Path.combine d "*.txt")` (backslashes) found nothing, and
  a `C:/…` pattern was walked from the drive's current directory; `\`
  now separates and a drive or UNC prefix roots the pattern. Matches
  are shown with `/`.
- **A `plan`'s stale-read guard sees Windows paths spelled either way.**
  On Windows a read under a captured target escaped the
  known-after-apply refusal when the two spelled separators or letter
  case differently; both now compare as the filesystem does.
- **`serve` answers pipelined requests.** Two or three requests sent
  back to back on one connection got one answer; each now gets its
  own, in order.
- **`serve` keeps every value of a repeated request header.** Three
  `X-Forwarded-For` lines reached the handler as the last one only;
  they now arrive as three pairs, in the order sent.
- **`serve` sends one response per request.** After refusing a request
  (a foreign `Host`, an absolute URL, `HTTP/9.9`) it also sent an
  unasked-for `200 OK` that a keep-alive client paired with its next
  request.
- **`serve` answers requests to `[::1]`.** They all got `400 Bad
  Request (Invalid url …)` and never reached the handler.
- **A slow request body no longer holds a `serve` handler slot.** The
  body is read before a slot is taken, so `maxConcurrent` slow uploads
  cannot shut out other clients for the whole `bodyTimeout`.
- **A `serve` handler still running when the block closes no longer
  crashes weir** (exit 134); its response is discarded. Closing waits
  two seconds in total, not two seconds per running handler.
- **A raising `serve` handler is recorded.** It answered `500 internal
  error` with no trace for the script; it, and a status outside
  100–999, now also add a line to `Server.streamErrors`.
- **`serve` drops a queued request whose client already left** instead
  of running the handler for it, and a `Stream` whose client
  disconnects frees its slot at once.
- **`cd /<Tab>` completes like a shell.** It left `cd "/` — an unclosed
  string. `cd` at the start of a line now completes its path bare, as a
  command argument (`cd /usr/`; quoted only for a space or quote), and
  where a path is quoted (`File.read ./<Tab>`) several matches extend
  inside a closed literal (`File.read "./s"`, cursor inside).
- **`printerr`, `Log.*`, the `Self.prompt` message and the `-e` echo
  neutralize terminal escapes at a tty**, as `print` does: a value
  carrying an OSC 52 (clipboard write), a title change or a screen
  clear shows as visible `\x1b…` text instead of driving the terminal.
  Redirected output is unchanged.
- **weir's own error messages neutralize terminal escapes at a tty.**
  `weir check` and the runner echoed a script's bytes raw — the source
  line under a parse or type error, an unresolved import's path, an
  invalid regex, a `#sig` name, `no such script: …` — so checking an
  untrusted script could write the clipboard (OSC 52) or set the
  window title. They now show as visible `\x1b…` text; colour and
  redirected output are unchanged.
- **A carriage return passes `print`, `printerr`, `Log.*` and the
  prompt at a tty** instead of showing as `\x0d`, so progress lines
  overwrite themselves again. Other control characters stay visible.
- **A `from json`/`jsonl`/`toml` error no longer prints a `Secret`.**
  When the type holds a `Secret` anywhere, `field 'ttl' expected int,
  got string in: …` cites `<excerpt withheld: the type holds a
  Secret>` instead of the document, and a TOML parse error keeps its
  line but withholds the detail.
- **A function failing inside `Secret.map` no longer prints the
  secret.** `Secret.map Str.fromBase64 s` on bad input said
  `invalid base64: "<the secret>"`; it now says `fromBase64: failed
  inside Secret.map — the message is withheld (it may carry the
  secret)`.
- **A `Self.prompt` cut short no longer eats a line.** A `pfirst` arm
  waiting in `Self.prompt` (the "prompt with a timeout default" shape)
  held the race at a terminal and, piped, took the next line when it
  arrived; the race now returns at once and the next prompt gets the
  line. Ctrl+C during `Self.prompt` at the REPL returns to the prompt
  instead of taking the next typed line as the answer. A `pfirst` arm
  reading `Self.stdin` or a FIFO no longer holds the race.
- **A signal waits for running `pmap`, `piter` and `pfirst` arms.**
  After SIGINT, SIGTERM or SIGHUP, arms kept writing files after a
  `within lock` was released and an `always` had begun; each now stops
  at its next builtin call, command or sleep, and the script waits for
  them before closing any scope.
- **A `pfirst` loser's `always` commands run.** They were killed as
  they started (exit 137) and the rest of the cleanup was skipped.
- **Ctrl+C at the REPL stops a `pmap`/`piter` whose arms run
  commands.** Every remaining arm ran and the prompt came back only
  after all of them; no new arm starts now.
- **A signal during `pfirst` races leaves no stopped processes.** A
  loser's child could be left SIGSTOPped, orphaned and holding the
  terminal.
- **A `serve` handler still running after the block exits stops.** Past
  the two-second drain it is stopped at its next builtin call, command
  or sleep, so it no longer writes files or runs commands after
  `within serve` returned.
- **`Args.load` no longer echoes a secret given as `--token=VALUE`.**
  The message reads `unknown flag '--token=***' — … write '--token
  <value>'` for a `Secret` field, and an unknown `--name=value` flag is
  shown as `'--name=…'` (it may be a misspelled secret).
- **A non-ASCII or control character in a secret header value is no
  longer printed.** For `auth` and `secretHeaders`, the refusal names
  the header and the byte offset (`at byte offset 8 of the secret
  value`); plain headers still name the character.
- **Commands inside `always` run on Ctrl+C and SIGTERM.** They were
  refused ("no new command starts"), so cleanups were skipped. The
  body's commands still stop, and a second signal still exits at once.
- **`pmap`/`piter` stop taking new items after Ctrl+C, SIGTERM or a
  closed stdout.** Workers kept starting thousands of items during the
  teardown, and each `within proc` in them left a `weir-proc-*` temp
  dir behind. Items already running finish.
- **A `plan` no longer sends `Http.send` with `Other "PURGE"`** (or any
  other `Other` verb): it is captured as an `HttpSend` op like `Post`.
  Only GET, HEAD, OPTIONS, QUERY and TRACE (any casing) count as reads.
- **A `plan` refuses every stale read.** `File.exists`, `File.sha256`,
  `File.readBytes`, `Path.glob`, `ls` and the rest of the file reads
  now refuse after a captured change they would not see, as
  `File.read` did; a listing refuses after a write inside it, a read
  under a captured `Dir.deleteAll` or move refuses, and `a.txt/` is
  `a.txt`.
- **A `plan` refuses an op inside a `within tmp` opened in it.** That
  directory is deleted before `Plan.apply`, which then failed midway
  after earlier ops had run.
- **`from json` reads documents nested up to 100 levels** (it stopped
  at 64 and said "not valid JSON"); deeper documents say so by depth.
  `weir add schema` likewise.
- **A `Yaml` field read from JSON keeps one of a repeated key** — the
  last value, at the first position — so `to yaml` no longer writes
  YAML weir refuses.
- **An indented `within … always` followed by another statement
  parses**; it failed with "dedents below the open block".
- **No re-enumeration warning on the result of a function that
  freezes** (`let f () = xs |> Seq.freeze` then reading `f ()` twice).
- **`Env.load {| A: string; A: int |}` refuses the duplicate field** at
  check, as a declared record does; `Args.load` too.
- **`Instant.now () + d` infers `d` as a Duration** — `let at d =
  Instant.now () + d` checks and `at 5m` works; it said "two points
  don't add" about a side nobody wrote as an Instant.
- **SIGHUP closes every scope**, as Ctrl+C and SIGTERM do: closing the
  terminal or `kill -HUP` now runs `always`, removes `within tmp` dirs
  and kills `within proc` children, then exits 129. It killed weir
  outright. `nohup weir …` still ignores it.
- **`exit n` inside a `pfirst` arm exits**, as it does in `pmap`. The
  race swallowed it as a failed arm and went on.
- **`within lock "f"` no longer blocks weir's own `File.read`/`File.write`
  of `f`** on Linux and macOS ("used by another process", even in the
  block holding the lock). The lock is still advisory and still excludes
  other holders. On Windows, lock a separate file such as `f.lock`.
- **Temp dirs are owner-only.** `within tmp`, `Path.newTempDir` and the
  `within proc` spill dir are created 0700 and the spill logs 0600; they
  were 0755/0644, so other users could read a child's output.
- **`File.writeAtomic` keeps a 0600 file private while writing.** The
  temp file was created 0644 and only took the target's mode just
  before the rename.
- **`NO_PROXY=::1` bypasses the proxy for `http://[::1]`**, as curl
  does; the entry `[::1]` no longer does. `Http.send` and `weir add
  schema` both follow it.
- **`check --can` reports a `Secret` in the argv of a reified command**
  (`… | complete`, `line`, `succeeds`, `exitCode`, `text`, `orFail`,
  `exec`), as it already did for a plain command and `$(…)`.
- **`weir fmt` is fast on long statements again.** A 5,000-line heredoc
  took 9 s (time grew with the square of the line count; yaml blocks and
  multi-line lists too, and editor formatting); it takes 0.2 s.
- **`weir check`, `weir <script>` and editor diagnostics are fast on
  long scripts.** Each top-level `let` cost time in proportion to the
  lets before it: checking 30,000 lets took 12 s and now takes 2 s, and
  an edit in a 40,000-line file re-checks in 2 s instead of 21 s.
  Formatting such a file takes 7 s instead of 22 s.
- **`Seq.distinct` is fast**, and so are `Seq.distinctBy`, `countBy`,
  `groupBy` and `except` on tuple or record keys. 25,000 distinct
  strings took 11 s through `Seq.distinct`; 20,000 tuple keys took 44 s
  through `distinctBy` plus `countBy`; both now take well under a
  second. Results and their order are unchanged.
- **`weir fmt` keeps a file's line endings.** It wrote the platform
  newline, so reformatting turned an LF file CRLF on Windows and a CRLF
  file LF elsewhere; a CRLF file now stays CRLF and an LF file LF.

- **`from json Map<string, int>` reads a whole document.** The value
  type can be anything a `Map` field can hold — `int`, `string`,
  `seq<string>`, `Option<…>`, another `Map`, a record — where it had to
  be a record (anything else was a parse error). `from json Map<int, …>`
  now says keys must be strings, as a declared `Map<int, …>` field does.
- **Indentation errors give a column:** `s.weir:2:5: this let needs a
  body …` where the runner printed `s.weir: line 2: …`. `weir check`
  and the editor drop the repeated `line 2:` from the message,
  `weir fmt` says `line 2:5:`, and `-e` and the REPL show the line with
  a caret.
- **Docs:** an `import` below other statements works, from its line on
  (the docs said imports must come first); a one-line
  `let n = echo a b in n` runs the command (the docs said it could not).

- **`weir fmt` refuses a file that does not parse** — a yaml-block error
  or an ordinary parse error is reported with its line and the file is
  left as it was (exit 3), as `weir check` would report it. Editor
  formatting makes no edits on such a file.
- **`weir fmt` no longer respaces `$<<<`/`$$<<<` block content** (`{a}`
  became `{ a }`) or a `$"…{ … }"` hole that spans lines.
- **`weir fmt` ends every file in exactly one newline** — trailing blank
  lines are removed, as the docs always said.
- **The REPL no longer rewrites invalid UTF-8 in the history file** as
  `�`. Such a line is left out of history (one note names it) and
  written back byte for byte whenever the file is rewritten.
- **`a + b + 0` infers.** `+` on two unknown values now waits for the
  rest of the statement: the other operand, a later `+ 0`, or how the
  result is used decides it, so the order you write it in no longer
  matters. Nothing deciding it is still the "cannot infer" error.
- **Generic records cross json, jsonl and yaml.** Name the instance in
  the slot — `from json Page<string>`, `from json seq<Page<int>>` (was
  a parse error) — and a record holding one, like
  `{ first: Page<int> }`, reads and writes too.
- **`from json` reads a `Yaml` field,** keeping any subtree whole, and
  `to json` writes it back. `weir gen types` uses it: a field that refers
  back to its own definition is now `Yaml` instead of left out.
- **Errors on a misaligned line point at the line as written.** On a
  line indented off its siblings — typically after a `[` or `{` stayed
  open and swallowed the lines below it — `weir check` and the editor
  put further errors at the column the line would have had once lined
  up, often past the end of the real line. They now point at the
  offending token.

- **`#alias` completes and gets a did-you-mean**, like the other
  directives; `#help`'s directive list now comes from the same table.
- **Hover and `#help` explain `fst`, `snd`, `ls`, `pwd`, `[<Attr>]` and
  `[<Elem>]`**, and `cd`'s text says what it returns: the directory it
  moved to (it said the previous one).
- **The site reference lists every prelude type** with its cases and
  fields.
- **Docs:** `#schema` was documented but never existed (a `yaml` block
  names its schema with `yaml schema=<name>`); `[<Positional>]` exists
  for command signatures; retry's `timeout=` option is documented.

- **`weir check` on an init file refuses what the REPL refuses.** Most
  of the loader's rules passed check and then stopped the init from
  loading: a `#session` value that runs a command
  (`$(cat /etc/hostname | line)`), an unknown, repeated or mistyped key,
  a statement or an eager command `let`, a bad `#alias`, a malformed
  `#session` block. Check, the editor and the REPL now share one set of
  rules, with the same messages. Only what needs the value stays
  load-time: a `cwd` that doesn't exist, an unknown `logLevel`, an
  `echoCap` out of range.
- **The REPL reports every error in the init file**, not just the first.
- **A `#session` value that names one of the file's declarations says
  why it can't.** Every key but `prompt` is read before the
  declarations bind; the REPL said "unbound variable", and check
  accepted it. Both now explain the rule and point at the name.
- **A tuple alone on its own line in a multi-line list no longer warns.**
  `env = [` / `"A", "b"` / `]` is one pair per line, not the `[1, 2]`
  comma habit; a single-line `["a", 1]` still warns.
- **A directive right after a multi-line statement runs in a piped
  REPL.** After match arms or a `|>` chain, `#echo` (any `#` line) was
  glued onto the statement and reported as a parse error.
- **A mistyped `--help`, `check` flag or `fmt` flag gets a "did you
  mean"** (`weir --hlep`, `weir check --cna`); only other top-level
  options got one.
- Docs: `-e` echoes its result with the type (`3 : int`).
- **`weir s.weir > log 2>&1` keeps all of stdout.** Stdout written to a
  regular file shared with stderr overwrote stderr's lines and lost its
  own; output now lands in order.
- **A missing dashed command is named at run.** `no-such-tool a` said
  `unbound variable 'no'` where `weir check` warned about the command;
  it now says `unknown command 'no-such-tool'`.
- **A reifier in a single-line `let … in` teaches `$()`.** It said
  `unknown command 'complete' … Did you mean the reifier 'complete'?`.
- **Docs:** SKILL.md prose corrected against the binary (`print` and argv
  splices take `Uuid`, `isEmpty` is qualified-only in the REPL, `$HOME`
  in argv is a splice, `&&` warns, `Path.glob` sorts per level, and
  smaller wording fixes).
- **A `serve` `Stream` body that raises partway now looks cut off to the
  client.** It ended with a clean chunked terminator, so the client read
  a complete stream; the connection now closes without it (curl exits
  18). `Server.streamErrors` still records the error.
- **A header weir can't send is an error naming it, never dropped.**
  `Http.send` skipped a header with a bad name (`"Bad Name"`) and a
  `Content-Type`/`Expires` on a `NoBody` request; it now refuses before
  sending, and says why (a non-ASCII value said "cannot reach"). A
  `serve` response header the listener rejects (a bad name, a control or
  non-ASCII byte) was dropped from a 200; the client now gets a 500 and
  `Server.streamErrors` names the header. A control byte in a request
  header value (e.g. BEL), sent before, is refused too.
- **A `Secret` in a failed command's error shows as `***`.** `curl -H
  $auth …` exiting nonzero printed the token in "command failed … :
  curl -H <token>" — in CI logs too. Every error that shows a command
  line (statements, `| text`/`| line`/…, pipelines, output read later)
  now masks it; the command still receives the real value.
- **The re-enumeration warning sees indirect reads**: two calls of a
  function that reads a command-backed binding, a read inside a `for`
  body or a `Seq.map` lambda, and two uses of a lazy binding derived
  from it (`let ys = xs |> Seq.where …`, or `let ys = xs`) each run the
  command again, and now warn.
- **A runtime error in a one-line form points at the failing call.**
  `let y = if x > 2 then fail "boom" else 1` reported column 1; it now
  points at `fail` (and `xs |> Seq.item 5` at `Seq.item`).
- **The LSP no longer re-analyzes the file on every request.** Hover
  and completion in a 20k-line file took seconds each; the analysis is
  now reused until the next edit.
- **`from xml` refuses mixed content in a text field.** `<n>a<b/>c</n>`
  read as `"ac"`; it is now an error naming `<n>`, `<b>` and the field.
- **A YAML round trip keeps `defaultMode: 0644`.** weir reads YAML 1.2,
  where `0644` is decimal 644; YAML 1.1 readers such as kubectl read it
  as octal 420. `Yaml.parse |> Yaml.merge p |> to yaml` rewrote it as
  `644`, which kubectl then applied as a different mode. A `Yaml` node
  now keeps the text of a leading-zero number, `0x1F`, `0o17` and
  `.inf`, and writes it back unchanged. Reading `0644` into an `int`
  field is now an error: write `0o644`, `644`, or quote it.
- **`to yaml` writes exponent floats as `1.0e-05`.** `1e-05` and
  `1e+21` read as strings to YAML 1.1 readers.
- **Tabs in YAML.** A tab used as indentation flattened a nested
  mapping without an error; it now refuses, naming the line. A tab
  before `#` starts a comment (`port: 8080<TAB># http` read the comment
  into the value).
- **YAML keeps no-break spaces.** A plain value or key trimmed NBSP and
  U+3000; only spaces and tabs trim now.
- **A block-scalar line starting with `%` is content.** An Apache
  `LogFormat` ConfigMap (`%h %l %u`) was refused as a directive, and so
  was weir's own `to yaml` of such a string.
- **`to yaml` never writes U+2028 raw in a block scalar.** Other YAML
  readers take it as a line break; such a string is written quoted.
- **`from xml` reads ASCII documents under any declared encoding.** A
  Visual Studio 2008 `.vcproj` labelled `Windows-1252` was refused; so
  were `UTF8` and `Latin-1`. Non-ASCII content under an encoding weir
  cannot decode still refuses, and the hint now also says to change the
  declaration. A malformed label such as `" windows-1252"` refuses
  instead of being read as UTF-8.
- **`from xml` keeps whitespace-only text** (`&#32;`, `&#9;`, two
  spaces), as Python's `xml.etree` reads it; it read as `""`.
- **`try` keeps a `Yaml` node's digits past int64**, as a plain `let`
  does.
- **An integer past int64 into an `int` field suggests `Yaml`**, not
  `float`, which would round it. `gen types` drafts `format: uint64` as
  `Yaml`.
- **`gen types` notes a required and nullable field** — its `Option`
  writes `None` as an omitted key, not `null` — and an enum's note lists
  `null` only when the enum holds it and the type allows it.
- **Errors escape the data they quote, piped too.** `Http.expect`'s
  body snippet, a header name with a CR, and a bad `Env.load` /
  `Args.load` value (or unknown flag, or stray argument) passed ESC,
  BEL and CR raw when stderr was not a tty; they now render as `\x1b`,
  `\x07`, `\x0d`.
- **`weir add schema` keeps a URL's credentials out of
  `.weir/lock.json`.** `http://user:pass@host/s.json` was written to
  the committed lock as given; the lock now records
  `http://host/s.json` and add says so. `add module` does the same.
- **`show` of a request and a plan's `http send` preview mask the URL's
  credentials** as `http://***@host/…`. The request still sends them.
- **`NO_PROXY` matches a non-ASCII host by its punycode**, as curl
  does: `NO_PROXY=xn--bcher-kva.example` bypasses the proxy for
  `http://bücher.example`; an entry is matched as written.

## v0.0.74

### Removed

- **`into` is gone.** The undocumented `xs |> into "cmdline"` ran `sh -c`
  past every effect check (`pure`, `readonly`, `plan`, module loading,
  `--can`). Pipe instead: `xs | sh -c "…"`, or better `xs | prog args`.

### Changed

- **A lazy value runs where it is used, unless it was written inside a
  `within`.** `let here = ls` then `cd src` then `here` lists `src/`
  (it listed the old directory). A value written inside `within cd` /
  `within env` still keeps that scope however late it runs. An init
  file's `let gl = git log …` now logs the repo you are in.
- **The REPL echo of a command-backed `let` never runs it.** It shows
  `gl : seq<string> (command-backed — runs on each use; type gl to run
  it)`; `let cleanup = rm -rf build` no longer deletes on Enter. A
  frozen or reified value still echoes its data.
- **A lazy command `let` is allowed in the init file and in modules**
  (`let gl = git log --oneline -n 10`); eager forms (`| line`, `$(…)`
  read at once, `Seq.freeze`) stay refused, with a message that says
  why.
- **A module's body runs once per program**, shared by every import of
  it (diamonds, aliases, symlinks — identity is the real path). It ran
  once per import site, so nested imports could take exponential time.
- **No command runs while a module is imported** — refused at run time
  too, so a lambda or a helper passed as a value can't slip through.
- **`serve` bounds request bodies**: `maxBody` (a `Size`, default
  16 MiB), answered 413 past it.
- **`Uuid` parsing refuses surrounding whitespace**, in `Uuid.parse`
  and in JSON fields alike.
- **A source file must be valid UTF-8**: an invalid byte is a located
  error, not a silent U+FFFD.
- **Statements in a block don't count toward the nesting limit**, so a
  function or `within` body can be any length.

### Fixed

- **A long flat script no longer overflows the stack** (6,000 lines
  crashed with exit 134; 700 on a 1 MB stack).
- **A `Secret` argument works in captured commands** — `curl -H $auth …
  | complete`, `| line`, `$e(…)`, `NAME=$s cmd`, `within env A=$s`.
- **Module members named like a builtin are exported** (`Doc.Text`).
- **SIGTERM stops every child tree once**, including `pmap` workers'
  grandchildren, which could be orphaned.
- **`weir restore`** sends the host token as `add` does, never writes
  `lock.json`, and names the entry and field of a malformed lock entry
  instead of crashing.
- **HTTP client:** one header pair per received line (`Set-Cookie` no
  longer merged, values never rewritten); an unknown charset is a
  precise error; `auth` plus an explicit `Authorization` is refused;
  `Authorization`/`Cookie` are dropped on a cross-origin redirect; a
  redirect loop is a named error.
- **`serve`:** no body on HEAD, 1xx, 204 or 304; framing and hop-by-hop
  headers from a handler are dropped; a `Stream` element is split into
  one `data:` line per line, so a newline can't forge an SSE event.
- **`gen types`** leaves a self-referential field out (with a note), so
  the type reads with `from json`.
- **REPL:** `HOME=` no longer crashes it; `#save` writes a script that
  runs; the line editor moves and deletes an emoji whole; path
  completion quotes spaces and quotes; typed-ahead input and Enter
  before the first prompt work; a piped blank line ends an unfinished
  entry; anonymous-record fields complete; `#quit now`, `#alias let`,
  `#help map`, piped `#history` and destructuring `let` echoes behave.
- **One formatter for the CLI and the LSP:** the shebang stays
  verbatim, error lines count from line 1, `fmt` refuses rather than
  flip-flopping, and trims only what the parser treats as whitespace.
- **LSP:** every request gets an answer, with the JSON-RPC error codes;
  `exit` without `shutdown` exits 1; header names are case-insensitive.
- **Checker:** copy-and-update fields align under the first field;
  imported unions type an unannotated parameter; a union reached only
  through another module doesn't make a local constructor ambiguous;
  `let un (W x) = x` works for a single-case union.
- **Weir-worded errors** for an unreadable or directory script,
  importing a directory or device, `within lock` on a directory, a
  negative or zero `poll interval`, a lone surrogate in JSON, `from json`
  type mismatches, a comma between record fields, `cmd |> try`, `-e ''`.
- **Smaller fixes:** run time is linear in a statement's lines (a 64k-
  line heredoc took 53 s); `Path.glob` bracket classes never reach the
  regex engine raw; `show` prints `Some (1, 2)`; a timed-out `retry`
  reports the attempts it made; float-holding `==` names `Float.near`;
  `build --help` on a union-only `Args.load` is scoped; error columns
  stay on their line.

## v0.0.73

### Fixed

- **`Plan.apply` runs the same checks as the direct call.** In a plan,
  `Dir.delete` of a non-empty directory deleted the whole tree, and
  `File.copy`/`Dir.move` accepted the wrong kind of path. The plan
  `Op` union gains `DeleteTree`, `CopyDir` and `MoveDir`; an exhaustive
  match on `Op` needs the three new cases.
- **`weir fmt` never re-spaces a command's arguments.** `chmod u+x f`
  became `chmod u + x f` and `echo a+b` became `echo a + b`.
- **A raise in a producer piped into a command fails the script.**
  `xs |> Seq.map (… fail …) | sha256sum` hashed the truncated input and
  exited 0; `exit` inside the producer was lost too.
- **A closed stdout ends the script.** `weir s.weir | head -1` ran the
  whole script (or spun forever on an endless child); it now tears down
  like `SIGPIPE` — children killed, `always` runs — and exits 141.
- **No statement runs after SIGINT/SIGTERM.** Code after `Proc.wait`
  could keep running while the scope tore down. The main thread now
  unwinds; if it is blocked where it cannot notice (a stdin read, a
  network call), the old teardown takes over after one second.
- **Int arguments past 32 bits raise instead of wrapping.**
  `Seq.item 4294967296 [10;20]` returned `10`; `Seq.take`, `Str.sub`,
  `Str.replicate`, `Str.padLeft` and indexing wrapped the same way.
- **A match on a not-yet-resolved payload is checked for totality.**
  `match o with | Some 0 -> … | None -> …` passed `weir check` and
  failed at run time with "no arm matched".
- **`pure` refuses `cd`, `pwd`, `Path.home*`, `Term.*` and `Color.*`;
  `readonly` refuses `cd`.**
- **YAML reads `null`, `Null`, `NULL` and `~` as null** (quoted forms stay
  strings), so `to yaml |> from yaml` round-trips an `Option`.
- **A mis-indented YAML line is an error,** not silently dropped.
- **`~` and `Path.home` use `$HOME` as set,** even when the directory is
  missing; `HOME=/nonexistent` made `~/x` expand to `/x`. An empty
  `HOME` raises.
- **A raise inside a `Seq.pmap`/`piter`/`pfirst` arm is reported at the
  arm's line,** not at the parallel call.
- **`Self.prompt` reads only its own line from stdin,** so a command run
  afterwards gets the rest (`printf 'a\nb\n' | weir s.weir`).
- **`weir fmt` fixes:** no crash on a comment outdented inside a heredoc
  or yaml block; a `///` line inside a block is never moved into its
  content.
- **`//` inside a string inside an interpolation hole is data,** and a
  trailing comment after such a string is stripped.
- **A malformed `${…}` in a `$$<<<` heredoc is a parse error,** not
  literal text.
- **`|>` after a multi-line `match` closed inside parens pipes the
  paren,** instead of failing with an indentation error.
- **`Seq.groupBy` accepts any key that supports equality,** as
  `countBy` does; tuple, record and option keys failed at run time.
- **Applying a returned function works:** `id (fun x -> x + 1) 2`,
  `(snd t) 5`, `Map.get "k" handlers x`.
- **A tuple pattern types a value whose type isn't known yet**
  (`let f = function | (a, b) -> a - b`).
- **`plan` refuses a command reached through a helper at check time,**
  not only at run time.
- **Types a module gets from its own imports work for its importers**
  (fields, `show`, `==`). A local type of the same name takes
  precedence; the imported one is then shown qualified (`A.Pt`).
- **A unit-suffixed literal too large for its unit is a parse error**
  (`9999999999TiB` wrapped silently).
- **Quoted YAML keys unescape,** and an empty key written by `to yaml`
  reads back.
- **`Dir.copy` copies symlinks as links** and refuses a destination
  inside the source.
- **`Instant.parse` reads a space separator and nanosecond fractions,**
  so TOML offset date-times load.
- **`from jsonl` skips blank lines,** so a file ending in an extra
  newline reads.
- **`Json.inferShape`/`Yaml.inferShape` draft `float` for an integer
  past int64,** which the readers already typed that way.
- **`Args.load` teaches `--name value` for `--name=value`,** and a field
  that would derive `--help` is a check error.
- **`weir fmt` reads no flag as a script path:** an unknown flag or a
  missing script is a usage error (exit 2).
- **Clearer errors:** a dangling symlink and a directory passed to
  `File.size`/`File.delete` are named as such; `xs[^n]` out of range
  names `^n` and the length; Duration/Size/Instant overflow and
  `Str.replace ""` fail in weir's words instead of .NET's;
  `0x10`, `1_000` and `1.5.2` get their own messages, and unit suffixes
  no longer clutter "Expecting" lists; `Some 1 < None`, Show/Ord
  leftovers, opaque built-in types and a failed import are reported as
  what they are.
- **`Duration.sleep` past 24.8 days sleeps** instead of failing.
- **Deep `Seq.append`/`Seq.map` chains no longer overflow the stack**
  (100k nested); deep `where`/`choose` chains still recurse.
- **A `plan` body ending in a non-unit value is an error,** like any
  other discarded value.
- **`weir fmt` keeps the layout inside an open bracket.** A multi-line
  value inside a `{ … }` entry was flattened to the entry's column, and
  lines inside an open `(` were re-indented level by level (`else`
  landing under `then`'s body). Inside a bracket fmt now only shifts a
  line by as much as its opener moved.
- **Bidi controls are shown at a terminal.** Data printed to a tty
  renders U+202A–202E, U+2066–2069, LRM/RLM and ALM as a visible
  `\uXXXX`, as C1 controls already were, so displayed text matches the
  bytes. `weir check --json` and the `--can` report (both forms) get the
  same treatment.
  Pipes and files stay byte-for-byte.
- **Deep lazy `Seq` chains don't overflow the stack:** `where`, `choose`,
  `take`, `skip`, `collect`, `distinct`, `scan` and the other lazy
  combinators now walk iteratively, like `map`/`append` already did.
- **Long waits are honoured.** Retry/poll delays and Http timeouts past
  ~24.8 days wait instead of silently timing out or failing.
- **`exit n` outside 0..255 raises** (a literal is a check error);
  `exit 256` exited 0.
- **`SIGTERM` to weir's pid also stops the foreground child,** which
  used to keep running after weir exited.
- **Regex matches time out after 5 seconds** with a named error;
  catastrophic backtracking (`^(a+)+$`) hung the script.
- **`from table` aligns columns with emoji and other non-BMP text.**
- **A module `let` that calls the module's own command-running helper
  is refused,** like a direct command; it ran at import.
- **Match arms resolve by the union that owns all their cases,** so a
  script-declared `Ok` no longer breaks an `Error` arm.
- **`"""…"""` is one command argument;** `@"…"` in arguments is refused
  with its own explanation (`@` is meaningful argv text).
- **An outdented `//` inside a heredoc is an error,** not silently
  dropped.
- **`weir run` refuses a file `weir check` refuses,** including a
  misaligned `///`.
- **Smaller fixes:** `-9223372036854775808` is a valid literal; the
  literal `-0.0` reads as `0.0`; `Instant.parse` refuses `12:34:56.Z`;
  a quoted yaml-block key `"$x":` is literal; `Seq.take`/`Seq.skip`
  with a negative count raise; `Path.glob` with a trailing `/` matches
  directories only; a generic `to json`/`to yaml` and an over-long flat
  chain get errors that say what to do.
- **YAML:** a plain value containing `: ` is refused ("quote the
  value"); `-   x:` with a wide gap after the dash reads; `...` ends a
  document.
- **XML:** DTDs are refused (an external entity silently became `""`);
  a repeated element read into a single-value field is refused
  ("declare `seq<string>`").

### Changed

- **`to json`, `to jsonl` and `weir check --can --json` write non-ASCII
  text raw.** `"é <b> & \"q\""` was written as
  `"\u00E9 \u003Cb\u003E \u0026 \u0022q\u0022"`; it is now
  `"é <b> & \"q\""`. Characters outside the BMP (emoji) stay an
  escaped pair. Readers are unaffected; a script comparing JSON bytes
  sees the new form.
- **Field paths in `from json`/`from xml` errors index from 0**
  (`refs[0].Include`), as weir indexing does.
- **Indentation is spaces only, inside heredoc and yaml blocks too.** A
  tab in a content line's leading whitespace is an error on every line
  (it was refused only on the first). A tab after the first
  non-whitespace character is still content.
- **Strings count Unicode code points.** `Str.length`, `Str.sub`,
  slicing and padding no longer count UTF-16 units, so they can't split
  an emoji; `Str.length "🎉"` is now 1 (was 2).
- **`Env.get`, `Env.vars` and `Env.load` see the `within env` overlay,**
  as `pwd` sees `within cd`; they read the outer value before.
- **Reading data into a `Secret` field is allowed** (`from json`/`yaml`/
  `toml`); writing a Secret out stays refused.

## v0.0.72

### Fixed

- **An error inside `within … always` is reported at the body's line.**
  The cleanup runs while the error unwinds, and v0.0.71 reported the
  error at the cleanup's line — even when the cleanup itself succeeded.
- **A module `let` that runs a command through a reifier is refused.**
  `let v = cmd | line` (or `| text`, `| succeeds`, `| complete`) in a
  module passed `weir check` and ran the command at import; it now gets
  the same error as `$(cmd)`: wrap it in a function.
- **An unknown program at the start of a line gets the right message.**
  `nosuchtool arg` failed at run time with "unbound variable … wrap it:
  $(…)"; it now says the program isn't on PATH and how to name one that
  only exists at run time (`^$tool`), as `let x = nosuchtool` did.
- **`M.f` with no module `M` suggests modules,** not a constructor
  ("Did you mean 'Ok'?"), and says how to import one.
- **`[<Positional>]` on an `Args.load` field is an error.** It only
  describes a tool's operands in a command signature; on a script's own
  arguments it silently did nothing.
- **Smaller fixes:** a command inside `plan` is reported as "commands
  are refused" (it said "'proc' is refused"); `weir check --can` reports
  each command of a pipeline at its own column; parse errors no longer
  list the removed `!` as an expected token.

## v0.0.71

### Fixed

- **Runtime errors name the line that failed.** A command failing
  inside an `if`, `match`, `for`, `within`, `retry` body, a pipeline or
  a lambda was reported at the block's first line (a deploy script's
  `az` failure on line 15 blamed the `if` on line 11). The error now
  points at the failing statement as `file:line:col`, and a command
  whose output is only read later is still blamed on the command
  itself. A failure inside a function reports the function's line,
  then one `called from file:line:col` line per call, innermost first —
  across files, so a module's function names the module's own line.

## v0.0.70

### Added

- **`|> try` turns a failure into a value.** End any expression with
  `|> try` to get `Some value`, or `None` if it failed — a missing file,
  a malformed document, a `fail`:
  `File.read "Cargo.toml" |> from toml Cargo |> try`. `|> try result`
  gives `Ok value` or `Error message` instead, with `Result.map`,
  `defaultValue`, `toOption` and `isOk`. It's the expression twin of
  `| complete`: everything to the left is captured and fully evaluated
  inside, `exit` still exits, and nothing that already happened is
  undone.

- **`id`, the identity function** (F#'s): `xs |> Seq.choose id`. It is
  not reserved, so `let id = …` and `for id in ids` still bind.
- **`Str.isEmpty` and `Str.nonEmpty`**, so a filter reads point-free:
  `lines |> Seq.where Str.nonEmpty`. (`isEmpty` now has two homes, so
  in the REPL write `Seq.isEmpty` or `Str.isEmpty`.)

### Changed

- **A script's own union cases shadow the prelude's.** With `Result`
  in the prelude, a script declaring its own `Ok` or `Error` case keeps
  constructing it; the same now holds for `Move` and `Copy` (from `Op`).
- **Hover text reads as paragraphs.** A `///` comment wrapped over
  several lines shows as one paragraph, and each hover line ends with a
  space, so editors that show hovers on one line (micro's info bar)
  no longer glue words together.
- **`try` is a keyword with a meaning.** It was reserved only to say
  weir had no `try`; the teaching now points at `|> try`.

## v0.0.69

### Added

- **`from toml T` reads TOML.** `File.read "Cargo.toml" |> from toml Cargo`
  reads a TOML 1.1 document (so any 1.0 file too) into your types, with
  the same field rules as `from yaml`: tables are records, arrays and
  `[[arrays of tables]]` are seqs, `[<Wire "requires-python">]` names an
  awkward key. Dates and times read as text, or into an `Instant` with
  `[<Iso8601>]`. Duplicate keys and tables defined twice are errors with
  the line. The parser is weir's own (no new dependency) and passes the
  official toml-test suite for TOML 1.1.
- **The REPL init file can import modules.** `import "./prompt.weir"`
  in `init.weir` loads the module before the first prompt, and its
  members are available in the session as `Prompt.member`. This keeps
  a large prompt in its own file.
- **`from yaml` reads indentation indicators.** A block scalar header
  like `token: |2` (written by some kubeconfig tools) made the whole
  read fail; `|2`, `|2-` and `|-2` now read as YAML defines them, with
  the content indented relative to the key.

### Changed

- **Imports resolve from a symlinked file's real location.** A script
  or init file linked from a dotfiles repo or into `~/bin` imports the
  files beside the original. `Self.scriptPath` still reports the link.

### Fixed

- **`| or` after a command with a `$@` splat crashed.** `echo $@xs | or
  echo no` raised an internal error at check and at run; it now works
  like `| and` and the other reifiers.
- **`| line`, `| text` and `| exec` followed by a word teach.** `cmd |
  line foo` reported "command not found: line"; it now says `line` is a
  reifier and how to run a program of that name (`^line`), as `| complete
  foo` already did.
- **A name from a pattern `let` is a binding in an `if` condition.**
  After `let a, b = …` in a function body, `if a == "" then` treated
  `a` as a command. Names bound by tuple and record patterns now work
  like a plain `let`'s.

## v0.0.68

### Changed

- **`| and` and `| or` can't be mixed in one chain.** `a | and b | or c`
  grouped from the right — `a | and (b | or c)` — unlike bash's
  `a && b || c`, and quietly did something else. It's now an error that
  points at `if a | succeeds then b else c`. Chains of one word
  (`a | and b | and c`, `a | or b | or c`) work as before.
- **The `&&` and `||` warnings suggest `| and` and `| or`.**

### Added

- **A warning for `[1, 2]`.** That's a one-item list holding the tuple
  `(1, 2)`, which is almost never what's meant; `weir check` now says
  to separate list items with `;`. A list of several pairs
  (`["a", 1; "b", 2]`) and `[(1, 2)]` don't warn.

## v0.0.67

### Changed

- **Clearer errors around `| and`, `| or` and misspelled reifiers.**
  `git status | or ls` now says `ls` is weir's own name, not a program,
  and to write `^ls` for the program, instead of a parser dump. A
  misspelled reifier after `|` gets a suggestion (`| success` → did you
  mean `succeeds`?). For a command missing from PATH, the error now
  suggests a dynamic head (`let tool = "x"` then `^$tool`) for tools
  that only exist when the script runs, instead of `sh -c`.

### Added

- **`| or` takes a pipeline on its left.** `a | b | or c` runs `c` when
  the pipeline fails — its exit is the leftmost failing code, as for
  `| exitCode`.

### Fixed

- **`(%)` works as a function value,** like `(+)` and `(*)`:
  `xs |> Seq.reduce (%)`. `(=)`, `(!=)` and `(<|)` now say what to write
  instead (`(==)`, `(<>)`, `|>`) rather than showing a parser dump.
- **Tab completes the command after a `|`.** In the REPL,
  `git status | s<Tab>` offered only files from the current directory.
  It now offers programs from PATH, and right after `|` also the
  reifiers and `and`/`or`; a stage's command is colored like any other
  command, `and`/`or` are colored as keywords, and an unknown command
  after `|` is red like one at the start of the line.
- **A reifier can't end a `| and`/`| or` chain.** In
  `let r = a | and b | complete`, `complete` applied only to `b`, and the
  record was silently discarded, so `r` was `()`. It's now an error
  that says so (`| exec` still works there). Errors in a pipeline's
  structure are no longer replaced by a parser dump when the line is a
  `let`.
- **A missing program inside a captured pipeline is reported.** In
  `let c = a | missing | exitCode`, `weir check` gave no warning and
  running it printed a pipe hint; both now name `missing`.
- **`| and`/`| or` can't follow a reifier.** In
  `a | succeeds | and b`, the bool was dropped and `b` ran anyway. It's
  now an error that points at `if a | succeeds then … else …`.
- **No false "command not found" after `NAME=value`.** When a line
  with an environment prefix failed for another reason, the error
  claimed the command didn't exist; the real error is reported now.

## v0.0.66

### Changed

- **Plainer help text.** The hover help, the website reference and the
  reference pages now explain things in ordinary sentences instead of
  terse slogans like "the reifier law: the meaning is the code".
  "Reifier" is now a defined term — a `|` stage after a command that
  turns its run into a value (`complete`, `succeeds`, `exitCode`,
  `orFail`, `line`, `text`, `exec`) — and the docs say plainly where
  each one sends the command's output.

## v0.0.65

### Added

- **A reifier follows a whole pipeline.** `git log | head -1 | line`,
  `a | b | complete`, `| text`, `| succeeds`, `| exitCode` and
  `| orFail` now work after a chain of commands, not just one. The code
  is the first failing stage's (counting from the left, where the fault
  began), or 0; `complete` carries the last stage's output and every
  stage's error output in order. `exec` still takes a single command.

### Changed

- **`weir check --can` lists each file once.** A program run from many
  places used to repeat the full path for every site; now it reads
  `git × 10  init.weir:11:72, 15:5, 17:14, …`. The second heading is
  just `mutations`.
- **`weir check` takes its flags in any order.** `--json --can` works as
  well as `--can --json`. A missing script, an unknown flag, or
  `--strict` without `--can` is now a usage error instead of "no such
  script: check".
- **`check --can` names the variables `NAME=value` sets** — for one
  command and for `within env A=1 B=$x` — instead of reporting them as
  not statically known.

### Fixed

- **A pipeline whose last command stops reading early no longer hangs.**
  `seq 1 1000000 | head -1`, `git log | head -1` and similar waited
  forever once the producer filled the pipe; they now end as in a shell,
  and the producer's exit is not reported as a failure. Commands run by
  weir also stop quietly when their reader goes away, instead of
  printing "write error: Broken pipe".

## v0.0.64

### Added

- **A typed `~` is your home directory in a command line.** `cat
  ~/.config/weir/init.weir`, `~/bin/tool` and `cd ~` now work as in
  bash — and as Tab completion already suggested: an unquoted word
  that is `~` or starts with `~/` expands when the line runs. Quoted
  strings, interpolations and spliced values stay literal, `~user` is
  not expanded, and `$HOME` still never expands; in an expression,
  `Path.home ()` remains the spelling. Tab completes a leading `~` to
  the literal home path, so the line shows what will run, and `cd`
  takes a string argument literally (`cd "~/x"` no longer expands).

- **`NAME=value cmd` sets an environment variable for one command.**
  `EDITOR=nano git commit` works as in bash: one or more `NAME=value`
  words before the program name (a word, a quoted string, `$x` or an
  interpolation; `NAME=` is empty), each pipeline stage taking its own.
  The program is still resolved at check time, and after the program
  name `CC=gcc` is an ordinary argument. A lone `FOO=1` teaches `let`.

- **Declared codecs: `Instant`, `Duration` and `Size` fields cross
  JSON and YAML.** Name the encoding on the field —
  `[<Iso8601>] at: Instant`, `[<EpochMs>]`, `[<EpochSec>]`,
  `[<Millis>]`/`[<Seconds>]` for a `Duration`, `[<ByteCount>]` for a
  `Size` — and `to`/`from json`, `jsonl` and `yaml` all read the one
  declaration, so a record round-trips without a hand-written wire
  twin. An unannotated field still refuses, now naming the attributes
  first. A codec that would drop sub-second precision raises instead
  of truncating; `show` and `==` are unaffected.

- **Or-patterns in `match`.** `| "a" | "b" -> …` and
  `| Debug | Info -> …` share one arm — F#'s spelling. Each alternative
  counts toward exhaustiveness and shares the arm's guard. An
  alternative cannot bind a name yet (`| (n, 0) | (0, n) -> n` asks you
  to split the arm), and an arm takes at most 64 alternatives — for a
  longer list, test membership in a guard (`| x when Seq.contains x
  values ->`).

- **Record fields splice into commands: `$cli.tag`.** `git tag
  $cli.tag`, `$cli.out.dir`, `$@cli.files` and a dynamic head
  `^$cli.bin` each read the field path as one word — the `(cli.tag)`
  detour retires, and a head held in a record no longer needs its own
  `let`. Only a name continues the path (`$x.` and `$x/y` still refuse
  as glued words), so bash's `$f.bak` is an error on a string that
  names `$"{f}.bak"`.

- **`cmd | text` captures the whole output as one string.** The
  multi-line sibling of `| line`: lines joined with newlines, trailing
  blank lines dropped, and a nonzero exit raises (`| complete` keeps the
  code) — `let notes = git log -1 --format=%B | text`.

- **`within env A=1 B=$x`.** The `NAME=value` words that set
  variables for one command now scope a block too, without binding an
  `Env.ofPairs` list first. A `within env e` with a bound list works as
  before.

- **`always` after any `within`.** `within tmp d`, `cd`, `env`,
  `lock`, `proc` and `serve` all take a trailing `always` block. The
  cleanup runs while the resource is still held — the temp directory
  still exists, the lock is still held, the process is still alive —
  and the release follows, so `cp $"{d}/report.html" .` works there.
  It used to need a bare `within` nested inside the scope.

### Changed

- **`pwd` is a `string`.** It was a one-line `seq<string>`, so every use
  needed `pwd |> Seq.head`; now `let here = pwd` is the current
  directory, read where it is used. Replace `pwd |> Seq.head` and
  `pwd |> Seq.exactlyOne` with `pwd`. `cd dir` still returns the
  absolute directory it moved to.

- **`!(cmd)` and `!e(cmd)` are gone.** Every statement position runs a
  bare command, so write `git pull` instead of `!(git pull)`, and give a
  command its own line instead of `!(a); b`. For an environment, use
  `NAME=value cmd` or `within env e`. `$(…)` and `$e(…)` still capture.
  A block that ends in a command now runs it when more lines follow,
  even inside a `let` (`let x =` + a `within tmp d` block + the
  value), which is where `!()` used to be needed.

- **`<<` is gone — weir composes left to right.** `f << g` was exactly
  `g >> f`; it is now an error that says so, as is F#'s back-pipe `<|`
  (write `f (x)` or `x |> f`).

- **An interpolation hole is generic.** `let addr s =
  $"{s.name}:{s.port}"` used to make `port` a string, so
  `addr { name = "a"; port = 1 }` failed; `let dash n = $"-{n}"` took
  only strings. A hole on a parameter or field now accepts anything a
  hole can render, as `show` does — the `$"{show x}"` workaround is no
  longer needed. A `Secret` passed to such a function is refused at
  the call. A hole nothing generalizes still defaults to `string`.

- **A lambda body takes command `let`s.** `xs |> Seq.iter (fun f ->`
  followed by `let r = git log $f | complete` used to ask for `$( )`
  unless the lambda sat on a top-level `let`'s right-hand side; a
  lambda body now works like a `for` body wherever it sits. Paren
  interiors still need `$( )`.

### Fixed

- **A cleanup ran twice when Ctrl-C interrupted a failing command.**
  When the signal reached weir just as the interrupted child made the
  body fail, both the signal handler and the scope could run the
  `always` block. Exactly one of them runs it now.

- **A match arm and an `else` take a text block on their own line.**
  `| 1 -> <<<` and `else <<<` failed with a parser dump although
  `then <<<` worked — and `weir fmt` turned the working next-line form
  into the failing one. Both arm their block now. A block written as a
  trailing argument (`File.write p <<<`) is an error that names the
  spelling that works: start with the block and pipe it on its closing
  line, `|> File.write p`.

- **Shell and C reflexes teach instead of dumping a parser error.**
  A trailing `\` (bash line continuation) used to pass a literal `\` to
  the program — a command line continues by indentation, and a `\`
  word is now an error saying so; `\;` names the quoted spelling and
  `\ls` names `^ls`. `!=` teaches `<>`, `!x` teaches `not x`, a type
  annotation (`(x: int)`, `let x: int =`) teaches inference, and
  `for i in 1..3` teaches `[1..3]`; `String.trim` names `Str.trim`,
  and a `list<string>` field type names `seq<string>`. A `let` that fails to parse no
  longer causes a second "unbound variable" error at its uses.

- **Editors and pagers get the terminal wherever a command runs as a
  statement.** Only a top-level bare command used to inherit the
  terminal; `!(cmd)`, `!e(cmd)`, a command inside a `within`, `match`
  or function block, and every pipeline handed the child a pipe — so
  `!e(vi f)` warned "Output is not to a terminal" and `git log | less`
  printed like `cat`. All of those now inherit at a terminal (a
  pipeline's last stage does), in scripts and the REPL; redirected
  output is unchanged. A command whose output becomes a value
  (`let x = …`, `$( )`, `| complete`) still captures, so an editor
  belongs in statement position.

- **A command in an `if` branch ends at `else`/`elif`.** `if ok then
  echo hi else echo bye` ran a single `echo` with argv `hi else echo
  bye`, and `if ok then git pull else ()` failed with "unbound variable
  'git'" — only an `if` without `else` took commands. Each branch is
  now its own command, on one line or indented; quote `"else"` to pass
  the word to a command there.

- **`print` takes a `Uuid`.** `Uuid.v7 () |> print` was refused; a
  `Uuid` now prints its canonical text, like the other types with one
  text form (`printerr` too). `Instant`, `Duration` and `Size` still
  print through `show` or an interpolation.

- **`Uuid` crosses YAML, and `Instant`'s YAML refusal names the fix.**
  A `Uuid` field reads and writes through `to yaml`/`from yaml` as it
  already did through JSON. An `Instant` field at the YAML boundary now
  teaches the conversion (`Instant.epochMs`, or `show`) instead of the
  generic "cannot cross" error, and every refusal reads the same in
  JSON and YAML, in both directions, naming the field.

## v0.0.63

### Added

- **`Uuid`: a first-class identifier type and module.** `Uuid.v7 ()`
  is time-ordered and strictly increasing within the process — ids
  generated in the same millisecond still sort in creation order, as
  uuids and as text — so it keys an append-only log. `Uuid.v4 ()` is
  random, `Uuid.v5 ns name` is name-based (`Uuid.ns.dns`, `.url`,
  `.oid`, `.x500`), and `Uuid.v7At t` stamps a given instant.
  `Uuid.parse`/`tryParse` read the hex-and-dash form, 32 bare hex
  digits, or `urn:uuid:`, in any case; `Uuid.version`, `Uuid.instant`
  (v7's creation time), `Uuid.toString`, `Uuid.nil`/`max` and
  `Uuid.toBytes`/`fromBytes` (big-endian) inspect and convert. Uuids
  compare, sort, show, splice into argv, and cross JSON as their
  canonical string, so records with `Uuid` fields round-trip.
  `Uuid.v4`/`v7`/`v7At` are effects like `Instant.now`; `v5` is pure.

## v0.0.62

### Fixed

- **The LSP sends Private-Use glyphs raw, not `\u`-escaped.** A
  Powerline/Nerd-Font icon (U+E000–U+F8FF) in a `textDocument/formatting`
  edit arrived `\u`-escaped, and a client that mis-decodes the escape
  (micro's lsp plugin) dropped it on format-on-save. System.Text.Json
  escapes the Private-Use Area under a hardcoded policy no built-in
  encoder overrides, so the LSP/CLI JSON writer now uses a thin encoder
  that emits every BMP non-ASCII scalar raw; mandatory `"`/`\`/control
  escapes are untouched, and astral chars stay a valid surrogate pair.
  `weir fmt` on the CLI was never affected.

- **An alias head submits on one Enter.** `co -` (alias `co = git
  checkout`) and `cb burnt*` no longer need a double Enter — the
  line-completeness check parsed without the alias table, so an alias
  head read as a bare name and a trailing `-`/`*` looked like an
  unfinished expression. The completeness resolver now carries the
  alias table, like the eval path.

- **A short custom prompt keeps its continuation marker.** The REPL
  continuation prompt is now one dim ellipsis (`…`), right-aligned to
  the prompt width. A custom prompt narrower than four columns (a
  `❯ ` whose status line is printed separately above) used to fall to
  blank spaces; it shows the hint at any width now, and the marker is
  dimmed at a colour tty.

- **A `#session` field key hovers as the key, not the rewritten
  binder.** `env` reported `_nv : seq<string * string>` and `prompt`
  reported `_rompt : { ok: bool; .. } -> string` — the LSP's internal
  field rewrite leaking. A field key now hovers as the key and its
  `#session` schema type (`env : seq<string * string>`,
  `prompt : string | unit -> string | PromptStatus -> string`).

- **`ls -la` teaches the force-external escape.** Applying a
  command-name builtin value (`ls`, `pwd`) to arguments reported a
  bare "not a function" error; it now points at `^ls -la` (or an
  alias). `ls` stays weir's typed listing — it is not silently rerouted
  to the external.

- **An unquoted path in an expression teaches quoting.** `let x = cd
  /work` parses as `cd / work` — a division whose RHS `work` is
  unbound. The "unbound variable" error now appends a hint to quote the
  path (`cd "/work"`); an unquoted path is an argv word only in a
  command. A glued `total/coutn` typo keeps its plain did-you-mean.

## v0.0.61

<!-- v0.0.60 was tagged but never published (its pinned commit still
     carried two ci scripts on the retired Http.fetch, so the release
     gate was red at the tag); its changes ship here under v0.0.61.
     Tags stay immutable — the v0.0.60 tag is burned, not re-pointed. -->

### Added

- **`Http.expect` — the raising read.** Request in, body out:
  `Http.get url |> Http.expect |> from json T` for the bare read, and
  `{ Http.get url with auth = Bearer t } |> Http.expect` the moment it
  needs `with`. A non-2xx raises naming the method, the redacted url,
  the status and a capped snippet of the error body — when the error
  body is data to inspect, `Http.send` still binds status and body as
  values. Per-method effect class like `send`'s (a GET expect is legal
  in `readonly`); a mutating-method expect inside a `plan` refuses —
  there is no response to return, and `Http.send` captures the Op.

- **Breaking: `Http.fetch` is retired.** `Http.get url |> Http.expect`
  is the spelling — one raising read, bare or built, and `--can` still
  names a literal url through the pipe. Pre-1.0: no retirement shim;
  the ledger carries the ruling [D:fetch-retired].

- **`Form` — the urlencoded body.** `body = Form [("grant_type",
  "client_credentials")]` percent-encodes each pair at send (the
  `withQuery` escaper) and sets `application/x-www-form-urlencoded` —
  the token-endpoint shape, never a hand-built `k=v&…`. Shared with
  `serve` responses like the rest of `HttpBody`.

- **The prompt provider carries the status.** `prompt` in the init
  file's `#session` block now also takes a `PromptStatus -> string`
  function: `ok` is the red-prompt tint — `true` when the last entry
  ran clean; a bare command's nonzero exit counts as not clean, a
  reified exit (`| exitCode`, `| complete`) stays data — and `exit`
  carries that bare command's code (`Option<int>`). A custom prompt
  owns its colors, so the status arrives as data instead of a paint:
  `let prompt st = if st.ok then Color.green "❯ " else Color.red "❯ "`
  — and the record grows without breaking a consumer. Records are
  nominal and init takes no signatures, so a provider types as a row;
  the loader accepts any field set `PromptStatus` satisfies.

### Changed

- **A `Json` body takes the value.** `body = Json { name = "w"; count =
  3 }` checks by `to json`'s own law and renders at the boundary — the
  `Json (x |> to json)` mouthful retires from the common path. Not
  breaking: a `seq<string>` payload is read as the pre-rendered
  document, so the explicit spelling means what it always did.

- **`weir fmt` pads the unambiguous operators.** `let x =dirty ||
  staged||stashed` formats to `let x = dirty || staged || stashed` —
  one space around `=`, `==`, `<>`, `<=`, `>=`, `&&`, `||`, `|>` and
  `+` where it was missing. Never `-` (adjacency is meaning: `f -1`
  passes an argument) and never bare `<`/`>`/`*`/`/` (types, globs,
  paths); command argv stays byte-inert under the existing parse-shape
  guard, which gained an expression-only twin so a spaced ident chain
  is not mistaken for argv.

- **`File.writeAtomic` — the whole-file swap.** Same signature as
  `File.write`: the payload lands in a same-directory temp, is
  fsynced, and renames over the target (the directory fsynced after) —
  a concurrent reader sees the complete old contents or the complete
  new, never a window, and a write that dies mid-way leaves the
  original intact, no temp litter. A symlink's target is replaced
  (the link survives); the target's mode and an existing BOM are
  preserved. The trade against `File.write` is the inode change:
  hardlinks split, held handles and `tail -f` keep the old file, and
  a Docker single-file bind mount breaks. In a plan it captures the
  same `WriteFile` op. A temp stranded by a kill is swept by the next
  `writeAtomic` to the same target — age-keyed (ten minutes), never
  pid-keyed. On Windows the rename retries briefly while a reader
  without delete-sharing holds the target.

- **A `match` types its scrutinee from the constructor patterns.**
  `let f x = match x with | Some n -> … | None -> …` now checks with
  no annotation: a case naming a single union types the scrutinee as
  that union, nested payloads included (`Some (Ok n)` resolves the
  inner union too). An ambiguous case — one name two unions share —
  still asks you to type the value, and exhaustiveness is unchanged
  (a missing case is still named). This narrows a standing divergence
  from F#, which infers the same.

### Fixed

- **weir's file readers no longer block Windows writers.**
  `File.read`/`readSecret`/`readBytes`/`File.sha256` (and
  `Env.fromFile`) open with `ReadWrite|Delete` sharing — a Windows
  share grant cuts both ways, so the old read default denied any live
  writer and a held reader broke `writeAtomic`'s rename. Unix
  locking is unchanged (every non-`None` share is the same shared
  lock).

- **`File.write` cannot destroy a file it failed to write.** The old
  shape truncated first and streamed lines after, so a payload that
  raised mid-enumeration — a lazy seq whose element fails, a command
  that dies mid-stream — left the old contents destroyed and the file
  partial, and a concurrent reader could watch the empty window. The
  whole payload now encodes (LF, BOM decision included) before the
  truncate, and lands in one write — a failing payload leaves the file
  byte-identical. `Plan.apply`'s `WriteFile` performs the same way.

- **`File.append` is safe under concurrent appenders.** .NET's
  `FileMode.Append` never opens with `O_APPEND` — it seeks in
  userspace and writes with `pwrite` at offsets computed from a stale
  end, so two appenders overwrote each other's bytes (a stress of
  8 writers lost lines and corrupted tails; .NET 10's own
  `File.AppendAllBytes` carries the same race). The fd now opens
  `O_WRONLY|O_APPEND|O_CLOEXEC` via libc on Unix and the whole call
  goes out in one unbuffered `write(2)` — every call lands contiguous
  and intact, and no lock ever blocks a concurrent reader. On Windows
  the handle opens append-only (`FILE_APPEND_DATA`, full sharing) —
  the same kernel-atomic append, no writer mutual exclusion. The
  payload encodes once — `utf8Strict`, LF, no BOM. The atom is one
  completed append (a short write retries, so disk-full edges can
  split a call), and the guarantee is local-filesystem: same-host
  bind mounts hold, a network filesystem does not.

- **The one-line echo's type tail recedes.** `() : unit` after a unit
  entry printed as normal text while every other footer dimmed; it is
  metadata and now recedes, and a scalar echo's ` : int` tail dims the
  same way (the value itself stays plain). Piped bytes unchanged.

- **The init file declares itself with `#init`.** A required head
  directive (first line) replaces path-based detection: a file
  carrying `#init` is checked as an init *wherever it lives*, so the
  config file can be authored in a dotfiles repo — editor support and
  all — and symlinked into `<configHome>/weir/init.weir`. The loader
  refuses the config file without the marker (pre-1.0, no shim). What
  the REPL *loads* is unchanged — only `<configHome>/weir/init.weir`,
  so which init runs never depends on the working directory; `#init`
  is the authoring marker, the symlink is the apply step. Running a
  marked file as a script refuses: an init is loaded, not run.

- **A session binding shadows a same-named alias.** The alias table
  hijacked a bound head: `#alias hi = echo` next to `let hi () = …`
  made the function uncallable — `hi ()` was even a parse error — and
  both definitions were accepted silently, live and from `init.weir`.
  Resolution now follows the line-decider's law: a bound name heads an
  expression; the alias table resolves unbound heads (still above
  builtins and PATH, so `#alias ls = ls --color` keeps working).
  Defining an alias over an existing binding refuses with a teaching —
  at the prompt, and as a located all-or-nothing error at init load.

- **The editor no longer flags an init declaration as unused.** An
  init file declares names *for the session* — the session is the
  importer of every top-level name — and the loader never judges
  unused-ness. The LSP ran the whole-script law there, so every prompt
  helper wore an `[unused-binding]` error the loader would never raise.
  That judgement now stays out of any `#init` file; an ordinary script
  keeps it.

- **A heredoc's leading `//` line is content, not a comment.** A
  comment-only line as a block's *first* content line was swallowed by
  comment transparency before the district activated — a
  `// <auto-generated/>` header silently vanished from a `<<<` value.
  It now arms the district as its first byte line, matching the
  stated law (every byte below the marker is content) and the
  mid-block behavior. `<<<`, `$<<<` and `$$<<<` alike; a `yaml`
  template is untouched — there `//` lines are the template's own
  comments, and a leading one stays transparent.

## v0.0.59

### Changed

- **The dynamic-head teaching names `| line`.** A seq-typed `^$(…)`
  head's refusal now reads `bind and pick (let tool = cmd | line),
  then run ^$tool` — the `$(…) |> Seq.exactlyOne` spelling it taught
  predates the `| line` reifier. The guide, the commands reference
  and the skill carry the same spelling.

## v0.0.58

### Added

- **From-the-end indexing and slicing (`^n`).** `xs[^1]` is the last
  element, `xs[..^2]` is all-but-last, `xs[^2..]` the last two, and
  `xs[^3..^1]` counts from the end at both bounds — F#'s spelling, where
  `^n` = length − n. Works on strings, seqs, and `Bytes`. This reverses
  the earlier decline: `^` is the command-force sigil only at a command
  head, and a bracket interior is unambiguous. A `^`-bound resolves
  against the length, so a from-end *sequence* slice forces the sequence
  (forward open-ended slices stay lazy).

## v0.0.57

### Added

- **Command chaining: `| and` and `| or` (bash `&&`/`||`).** `cmd | and
  next` runs `next` only if `cmd` succeeded; `cmd | or next` runs `next`
  only if `cmd` failed. Both stream their output and chain
  (`mkdir d | and cd d | and build`), and the right-hand side is a full
  command line so a builtin like `cd` works as an operand. They fill the
  one-liner gap the statement rule left — especially in the REPL — where
  before you needed separate lines or `if … | succeeds then …`.
  Right-associative (differs from bash's left-associativity for *mixed*
  `and`/`or` chains); `| or`'s left must be a single external command.

## v0.0.56

### Fixed

- **An alias to the `cd` builtin runs the builtin.** `#alias up = cd ..`
  now performs weir's `cd`, not the external `/usr/sbin/cd` (which failed
  with "too many arguments"). Aliases remain resolution-table entries, not
  macros — `$(…)`, pipes and `&&`/`||` in a body are literal argument words;
  for a shortcut that needs those, write a nullary function in `init.weir`
  (`let root () = cd $(git rev-parse --show-toplevel | line)`).

- **`weir fmt` aligns a parenthesized match's arms under the `m`.** A
  `(match … )` nested in an arm body had its arms indented to the block
  depth instead of under the `match` keyword (the head detector missed the
  leading `(`). Now `(match` — and the arms below it — line up as a
  top-level match does.

- **REPL history dedupes properly (erasedups, keep-last).** A re-entered
  command now moves to the most-recent position and its earlier copy is
  erased, so history is a clean recency-ordered set — Up-arrow, `#history`
  and the Ctrl+R finder each show a command once. Previously only *adjacent*
  duplicates were dropped (readline `ignoredups`), and the finder fed every
  entry to fzf so re-runs listed repeatedly. Duplicate lines already in the
  history file are collapsed on load. Off with `historyDedup = false`.

- **Tab completion works inside an interpolation hole.** `$"{Path.<TAB>`
  now completes `Path`'s members — the `{ … }` interior is an ordinary
  expression. It had offered nothing: the opaque `$"…"` made the completer
  read the line as a command and try a filesystem completion of `Path.`.

- **`ls`, `pwd` and a relative `Path.glob` capture the directory where
  they're written, not where they're forced.** A `within cd "dir"` block
  whose trailing `ls`/`pwd`/`glob` was echoed (or otherwise forced) after
  the block exited had listed the *outer* directory — the lazy seq read
  the cwd at force time, after the scope restored it. They now snapshot
  the cwd at evaluation (the same closure rule command values already
  follow, and matching the eager `Dir.list`/`Dir.stat`). A user
  `let ls = …` shadow is untouched.

- **A parse error's `Expecting:` list no longer doubles a token.**
  `within tmp d -> …` reported `Expecting: the scope's block or ';' or ';'`;
  the alternatives are de-duplicated now (`… or ';'`).

## v0.0.55

### Added

- **The LSP understands `init.weir`.** The REPL's own
  `<configHome>/weir/init.weir` no longer flags `#session`/`#alias` as
  parse errors, and inside the `#session` block the field values complete
  and check as ordinary expressions (`prompt = <TAB>` offers your declared
  functions) while a field-name slot completes the keys (`cwd`, `env`,
  `logLevel`, `echoCap`, `prompt`). An unknown key and a value of the wrong
  type for its key are flagged, and a `#alias` command completes against
  PATH. A real declaration error still shows, and a stray `init.weir`
  elsewhere stays a normal script.

- **`Term.width` — the terminal columns, live.** `Term.width () : int`,
  queried each call (a resize shows), falling back to 80 when there is no
  terminal (piped/redirected) rather than raising. The `tput cols`
  primitive for sizing output.

- **`Table.render` — a seq of records to aligned column lines.** The
  table layout the REPL echoes, exposed for `print`: `rows |> Table.render
  |> Seq.iter print`. Display, not a wire format (no `to table` back out).
  Tracks the terminal width interactively, unclamped when piped; an empty
  seq prints nothing, a non-record seq is a located error.

- **A `Color` module for terminal color.** `Color.red`/`green`/`yellow`/
  `blue`/`magenta`/`cyan`/`gray` and `bold`/`dim`/`underline` (each
  `string -> string`, nesting to combine), plus `Color.sgr code s` for
  256-color/truecolor. Context-aware: emits the escape only at a terminal
  with color on, plain when piped or `NO_COLOR` — so `print (Color.green x)`
  colors interactively and stays plain in a pipe, no guarding.

### Changed

- **`print` keeps color at a terminal.** The tty data-sanitizer now lets
  SGR (color/attribute) escapes through — display-only, so they can't
  drive the terminal — while still neutralizing every other family (OSC
  title/clipboard, DCS, cursor/screen moves, carriage-return overwrites)
  to visible `\xNN`. Colored output (the `Color` module, a colored
  prompt banner) reaches the screen; untrusted data still can't hijack it.

### Fixed

- **A quoted directory completion keeps the caret inside the quotes.**
  Completing `cd /wor<TAB>` to `cd "/work/"` now leaves the cursor before
  the closing quote, so the next segment continues the same string literal
  and a re-`TAB` nests (`cd "/work/sub…"`). It had landed past the quote,
  where the next keystroke fell outside the string and re-`TAB` completed
  against the whole environment.

## v0.0.54

### Added

- **`#infer let`'s RHS colors and completes as a statement.** In
  `#infer let x = <src> …`, the source now gets the same syntax
  coloring and tab-completion (bindings, command heads, members) a
  bare `let x = <src>` would — the leading `#infer` had been hiding
  the statement context.

- **A successful init load is silent.** `init.weir` loading clean no
  longer prints `init: N name(s) from …` — the session opens straight
  on the prompt. Only failure still reports (`init: not loaded` plus the
  located error); a missing init stays silent as before.

- **Parked library members, unparked.** `Str.rsplitOnce`/`tryRsplitOnce`
  (split at the last separator — `user@host:port`, `[::1]:port`),
  `Option.orElseWith` (orElse with a lazy `unit -> Option` fallback), and
  `Seq.tryReduce` (reduce's Option twin — `None` on empty). Each fills in
  next to its existing sibling.

- **`Bytes` now slices — `b[a..b]`, `b[..b]`, `b[a..]`.** The range
  slice operator was string/seq only; `Bytes` joins as the third
  target, same inclusive-clamping contract, slicing to `Bytes`. A
  single `b[i]` still isn't a bracket form (use `Bytes.sub start len`);
  it now teaches that instead of leaking a `seq` type error.

## v0.0.53

### Added

- **`#infer let` — draft, declare, and bind in one shot.**
  `#infer let z = <src> |> from json as Pods` evaluates the source once,
  drafts and injects the types, and binds `z` to the value parsed from
  the same sample — no `Seq.freeze`, no re-run, no third statement.
  `as _` auto-names the root (`Type1`), in the classic form too; a
  top-level array binds as `seq<Name>`. `#save` distills the honest
  explicit spelling (`type` decls + `let z = src |> from json Name`),
  never the directive line. `from … as <Name>` in a file now teaches
  the `#infer` spelling instead of a bare parse error.

- **A custom REPL prompt: `prompt` in the init file's `#session` block.**
  A string, or the name of a `unit -> string` function declared in
  `init.weir` — it may run commands (`git branch --show-current | line`)
  and is called once per entry read, never per keystroke. Colors work
  (SGR is zero-width for the column math, a reset is appended) and only
  SGR passes — every other escape family in the output is stripped; the
  continuation prompt pads to the same width; a raising provider falls
  back to `weir> ` with one stderr note. Redirected sessions keep the
  fixed default.

### Changed

- **Breaking: `prompt` is now `Self.prompt`.** The interactive read moves
  in with `Self.stdin`; the bare name is unbound (no alias, pre-1.0). The
  REPL and `-e` now carry a `Self` module with `prompt` alone — the
  script facts (`args`, `stdin`, `scriptPath`…) stay script-only.

## v0.0.52

### Added

- **`$$<<<` — a splice-interpolated heredoc for templating JSON/config.**
  A third block form alongside `<<<` (literal) and `$<<<` (`{expr}` holes):
  in `$$<<<`, `$name` and `${expr}` substitute, `$$` is a literal `$`, and
  braces/quotes are literal — so a pasted JSON blob templates directly, no
  brace-doubling:

  ```
  $$<<<
      { "server": "$sqlFqdn", "type": "AzureSqlMI" }
  |> File.write "ls.json"
  ```

  Like any text template it substitutes raw (a value with a `"`/newline can
  break the output — use `to json` when a value is untrusted).

### Fixed

- A `Ctrl+R`/`#find` selection made while candidates were still streaming to
  fzf was silently discarded as a cancel: the finder's exit breaks the feed
  pipe, and the broken-pipe error swallowed the selection. The feed now wears
  its own guard — the selection is read regardless.

## v0.0.51

<!-- v0.0.50 was tagged but never published (its release build was red); its
     changes ship here under v0.0.51. Tags stay immutable — the v0.0.50 tag is
     burned, not re-pointed. -->

### Added

- **`exec`: process replacement.** `cmd | exec` replaces the running weir
  process with the command (POSIX `execve`; Windows spawns, waits, and
  exits with the child's code). weir keeps its pid, so as a container
  entrypoint the application receives signals directly — no forwarding or
  reaping layer. It is a reifier like `complete`/`orFail`, diverging like
  `fail`/`exit` (never returns), so it is a legal bare statement; it takes
  a literal or dynamic (`^$cmd`) head and an env-sigil overlay
  (`$e(cmd | exec)`), but refuses a piped stdin — there is no parent left
  to feed the replacement.

- **`cmd | line` — the single-line capture reifier.** Reads a one-value
  command (`az … --query X -o tsv`, `git rev-parse`, `id -un`) as its one
  trimmed line of stdout, as a `string` — replacing the
  `$"{$(cmd) |> Seq.exactlyOne}"` mouthful with `let x = cmd | line`. It
  joins the reifier family (`complete`/`succeeds`/`exitCode`/`orFail`/`exec`),
  raises on a nonzero exit or on 0-or-2+ lines, and composes with the env
  sigil (`$e(cmd | line)`) and a value head (`xs | grep foo | line`).

- **`Path.home` and the XDG directory trio (`Path.configHome`,
  `Path.stateHome`, `Path.cacheHome`).** The typed stand-in for `~`/`$HOME`,
  which never expand in argv — build a path with an interpolation instead:
  `cat $"{Path.home ()}/.bashrc"`, `cat $"{Path.stateHome ()}/weir/history"`.
  Each is a pure `unit -> string` query with platform-native output:
  `configHome`/`stateHome`/`cacheHome` resolve `%APPDATA%`/`%LOCALAPPDATA%`
  on Windows and `$XDG_CONFIG_HOME`/`$XDG_STATE_HOME`/`$XDG_CACHE_HOME`
  (falling back to `~/.config`, `~/.local/state`, `~/.cache`) on POSIX —
  the same resolution the REPL uses for its own history file.

- **`#history` REPL directive.** Shows the session's history with the
  **file path in the header** (a quick way to find where history lives,
  since `~` never expands): bare `#history` dumps every entry numbered,
  `#history N` shows the last N. Entries render one per line, matching the
  history search's display.

- **REPL kill-ring: `Ctrl+W` / `Ctrl+Y`.** `Ctrl+W` kills the previous word,
  `Ctrl+Y` yanks (pastes) the last killed text, and `Ctrl+U` / `Ctrl+K`
  (kill to line start / end) now feed the same ring. The ring is
  session-wide, so a kill on one line yanks into a later one. (Copying from
  elsewhere remains your terminal's own mouse selection — there is no
  shift-arrow selection, matching every terminal line editor.)

- **`from table` reads `az … -o table` output.** Azure CLI (and other
  tabulate-style tools) draw a `------  ----------` dashes rule under the
  header; `from table` now skips that separator when it is the first data
  row, so both `from table T` and `#infer … from table` work on az output.
  kubectl/docker tables (no separator) are unchanged, and a real row that
  merely contains a lone `-` cell is kept — only an all-dashes row is
  dropped.

- **A checker warning for the `Path.newTempDir` cleanup footgun.** A
  `Path.newTempDir ()` binding that is later `Dir.delete`/`Dir.deleteAll`'d
  in the same scope now draws an advisory warning pointing at `within tmp`,
  which cleans up on scope exit *and* on Ctrl+C/kill (a manual delete
  misses the signalled case). `newTempDir` remains the right tool for a
  directory that must outlive its scope, so a binding with no in-scope
  delete stays silent. Warning severity — `check` still exits 0.

### Changed

- **A failed command in the REPL is now a quiet exit-code status, not a red
  error.** When a bare command you type at the prompt exits nonzero, the
  REPL shows a dim `↳ exit N` instead of `error: command failed with exit
  code N: …` — the output already streamed and the session continues, so
  it reads like a shell's `$?` (and since weir has no `$?`, showing the
  code is the only way to see it). The raise itself is unchanged: scripts
  still fail-fast, and a command failure inside a value (`$(…)`, a reifier,
  a binding) still raises the full error, because there it aborted a
  computation.

### Fixed

- **A top-level `if … then … else …` block now parses.** The block form at
  column 0 (`if c then` + an indented body, then a dedented `else`/`elif`)
  was rejected as a stray `else` keyword — the assembler treated the
  dedented `else` as a new statement. A col-0 `else`/`elif` now continues
  its `if`, the same way a dedented `|`/`until`/`always` already did. (The
  indented form inside a function body always worked.)

- **`#infer` no longer mis-drafts a heterogeneous object as a homogeneous
  map.** When sibling objects in a sample array carried different keys whose
  values only *coincidentally* agreed in an early pair (e.g. a Kubernetes
  `securityContext` with a `bool` field, then an `int` field two elements
  later), the pairwise merge committed to `seq<string * bool>` and a later
  `from json` rejected the int (`expected bool, got Number`). The open-map
  verdict is now decided over the *whole* set of siblings, so an object with
  mixed value types stays a typed record; genuinely uniform data-keyed
  objects (a ConfigMap's `data`) are still drafted as an open mapping.

- **Tab-completing a `$name` splice in a command argument now offers session
  bindings.** `az … --location $l<TAB>` completes to `$location` (any
  binding whose name starts with `l`); before, an argv position offered only
  filesystem paths, so a splice never completed. A bare (non-`$`) argument
  still completes paths only.

- **`exec` is now refused inside a `plan` block.** A helper that invokes
  `cmd | exec` inside a `plan` is refused at runtime, matching the guard on
  every other spawn — `execve` does not funnel through the spawn path, so
  the guard is applied in `Proc.exec` directly (it could otherwise replace
  the process inside a dry run).

## v0.0.49

### Fixed

- **A deeply-nested value echoed at a terminal no longer crashes the
  process.** The tty binary-echo probe (which decides whether to sanitize
  a value's bytes at a terminal) walked record/tuple/union/map structure
  with no depth bound — the last value walk that wasn't iterative or
  depth-capped. A checker-accepted program producing a deeply-nested
  recursive-record value, echoed under `weir -e` at a tty or in the REPL,
  aborted with an uncatchable stack overflow. The probe is now bounded at
  depth 100, matching the `show` renderer.

- **A heavily-documented module no longer stalls `weir check`.** The
  doc-comment attachment pass appended each `///` line to a growing list
  one at a time, which is O(N²) over a contiguous run — a 50,000-line
  module pinned a core for ~20s. The run now conses in O(1) and is
  reversed once at the point of use, restoring linear time.

- **The top-level "internal error" fallback now sanitizes its message at a
  terminal.** The CLI's residual-exception guard printed a .NET exception's
  message verbatim to stderr; a message interpolating attacker-influenced
  data (a filename, URL, or value) could emit terminal escape/control bytes,
  the one error-echo site that had missed the tty-data neutralization.

## v0.0.48

### Added

- **`HttpMethod` gains an `Other of string` case** for a well-formed but
  unlisted request verb. A `within serve` handler can now route on a verb
  the union does not name (`match req.method with | Other v -> …`) and
  answer 405 by its own choice, instead of the verb being silently
  reported as `Get`. On the client side a request built with `Other "…"`
  sends that verb verbatim.

- **`within serve`'s config gains an optional `bodyTimeout` field** that
  bounds the request-body read (default 30s). A slow client dribbling its
  body is refused with a 408 instead of parking a handler slot. Existing
  `{ port; maxConcurrent }` configs are unchanged — omitting the field
  keeps the 30s default.

- **`Server.streamErrors`** — the designated channel for a streaming
  failure. When a `Stream` response body's producer raises mid-flight,
  its message is recorded on the handle rather than raised out of the
  handler, so a monitoring loop can read `Server.streamErrors srv` and
  learn its producer died (the failure used to be reported to nobody).

### Fixed

- **An HTTP request method `within serve` cannot name no longer becomes
  `Get`.** `TRACE`, `QUERY`, `FROBNICATE` and any other verb outside the
  `HttpMethod` union all arrived at the handler as `Get`, so the read
  path ran for a request that was not a GET and a handler could never
  answer 405 for an unknown verb. `QUERY` now reads as `Query` (a case
  the client side already had), a well-formed unlisted verb reads as
  `Other v`, and a malformed method token (control chars, whitespace,
  non-token bytes) is refused at the boundary with a 400 — while a
  well-formed unknown verb a proxy may forward is preserved.

- **A `Stream` response body whose producer raises mid-flight no longer
  reports success to everyone.** The producer's failure used to still
  emit a proper terminating chunk and a 200, so the client read a
  complete stream and the script exited 0 — a silent truncation. The
  failure is now surfaced to the script through `Server.streamErrors`
  (the designated channel, not a raise out of the handler), and the
  response is aborted. (The managed HttpListener emits the chunk
  terminator even on abort and offers no AOT-safe way to suppress it, so
  the wire-level truncation is best-effort on this platform; the
  script-observable channel is the reliable signal.)

- **`within serve` bounds the request-body read.** A client that declares
  a large `Content-Length`, sends a few bytes and holds the socket used
  to park a handler slot indefinitely. The read is now bounded by the new
  `bodyTimeout` config field (default 30s) and exhaustion refuses the
  request with a 408.

- **Terminal escape sequences from data no longer reach the terminal
  verbatim.** A filename or field carrying ANSI/OSC escapes, a bare ESC,
  or other control bytes could clear the screen, set the window title,
  leave the terminal coloured, or use a carriage return to hide the real
  name. Tty-bound data renderers — `print`, command streaming, the REPL
  echo and table, and error text — now render those bytes as a visible
  `\xNN` escape. weir's own colouring is unaffected, and redirected
  output (a pipe or file) stays byte-faithful.

- **A `plan` now captures filesystem paths as absolute, bound to the
  directory where they were captured.** A plan stored the caller's
  *relative* path and `Plan.apply` re-resolved it against the
  apply-time working directory — so a `File.write "marker.txt"`
  captured under `within cd "planning"` and applied under
  `within cd "application"` wrote to `application/marker.txt`, not the
  previewed `planning/marker.txt`. Capture now resolves every
  filesystem path to absolute at capture time and `Plan.apply` writes
  that exact path, so what `Plan.preview` shows is exactly what apply
  performs, regardless of the working directory at apply. This covers
  every captured filesystem op — `File.write`/`delete`,
  `Dir.create`/`delete`/`deleteAll`, and the two-path `File`/`Dir`
  `copy`/`move` (both source and destination are bound). Plans built
  from *absolute* paths are unchanged (they already resolved to
  themselves), so plan equality and `Plan.ops` comparisons on absolute
  paths behave as before.

- **A CRLF (or NUL) in an HTTP header no longer forges a second header
  or is silently dropped.** A header name or value carrying CR, LF or
  NUL is now refused at both HTTP crossings. On the `Http` client an
  outbound request header with such a byte (from a config file, an API
  response, a tenant name) used to arrive at the server as two headers —
  `X-Evil: a` plus a forged `Injected: yes` — the response-splitting
  class. `Http.send` now refuses before sending, with a located error
  naming the header, the byte, and whether it sat in the name or value.
  On the `within serve` side a handler-returned header with such a byte
  used to be silently dropped; it is now refused too — the response is a
  500 without the injecting header (never a silent drop), and the located
  message reaches the script through `Server.streamErrors` so a handler
  bug is surfaced rather than swallowed. A benign control header is
  unaffected in both directions.

- **`secretHeaders` no longer leak across a cross-origin redirect.**
  `Http.send` following a redirect to a different origin used to re-send
  `secretHeaders` — a credential channel carrying a `Secret` — to the new
  host, while the typed `auth` union was protected only because .NET
  happens to drop `Authorization`. weir now drops `secretHeaders` (and
  `auth`) on a cross-origin redirect exactly as the BCL drops
  `Authorization`, so the two credential channels agree: the destination
  a redirect names can no longer harvest a credential the caller sent to
  the original host. A same-origin redirect keeps the credential, and a
  non-sensitive header still crosses (the drop is credential-specific).

- **The contract-fetch client no longer leaks credentials across a
  cross-origin redirect.** `weir add module`/`add schema`/`restore`/
  `verify` download over a separate HTTP client from `Http.send`, and it
  was a bare `HttpClient` with automatic redirects that re-sent the
  credential (a GitLab `PRIVATE-TOKEN`, a GitHub `Authorization: token`)
  to a redirect target — the BCL strips only `Authorization`, so
  `PRIVATE-TOKEN` in particular leaked. The contract client now disables
  automatic redirects and follows them explicitly, dropping the
  credential headers on a cross-origin change, across both the
  API-resolution request and the artifact download. (The leak mechanism
  is closed; whether a real provider can be induced to redirect to an
  attacker host is an unproven precondition, recorded in the ledger.)

- **An HTTP error no longer prints a URL's credentials in the clear.**
  `Http.fetch`'s non-2xx error quoted the whole URL (`http://…answered
  500`) and `Http.send`'s transport error did the same when the URL
  could not be parsed — so a credential in `http://user:pass@host`
  printed verbatim to the terminal, a CI log, or the REPL. Both now
  redact the userinfo: `http://***@host answered 500`. A URL with no
  credentials is still shown in full, so the target is named. (Display
  only — a credential still rides the wire and is `ps`-visible in argv,
  unchanged.)

- **Parse and decode errors no longer echo a huge invalid input back.**
  `Str.toInt`, `Str.fromBase64`, `Bytes.fromBase64`, `Bytes.fromHex`,
  `Duration.parse`, `Size.parse`, `Float.parse` and `Instant.parse`
  embedded the caller's whole input in the error message, so a 500KB
  invalid value produced ~500KB of stderr. They now excerpt: an input of
  64 characters or fewer is quoted in full (unchanged — a short typo
  stays readable), a longer one shows a 64-character head and names the
  true length (`… (200000 chars)`), so a multi-megabyte invalid input
  yields a bounded error. (A residual short-prefix leak for a value
  derived from a revealed `Secret` is recorded in the ledger.)

- **A NUL byte in a path no longer crashes weir — it is refused with a
  located error.** `Path.GetFullPath` throws a raw
  `ArgumentException: Null character in path` on a NUL-bearing argument,
  and every File/Proc/completion builtin plus the parser's
  command-head classifier reach it through `Session.resolve`. A `.weir`
  line like `./a<NUL>b c` aborted the process (SIGABRT, exit 134) with
  no diagnostic, because the parse-time head resolution runs outside the
  runner's exception handler; a NUL-bearing *path value* at run time
  (e.g. a command line or file line carrying a NUL) leaked the raw .NET
  message. `Session.resolve` now rejects a NUL-bearing path with a
  located weir error (`path contains a NUL byte — paths are NUL-free;
  NUL-bearing data is binary, not a path`), closing the whole run-time
  class, and `Extern.exists` treats a NUL-bearing program head as
  not-found so the parser emits its ordinary missing-command diagnostic
  instead of aborting. NUL-free paths are unchanged.

- **An out-of-range attribute integer is now a located error, never a
  crash.** The tiny literal lexer behind attribute arguments
  (`[<Default n>]` and friends) cast the digits to `int64` with a raw
  cast that throws `OverflowException` past the 64-bit range — and
  nothing above the parser caught it, so `[<Default 99999999999999999999>]`
  on any field, union case, or type aborted `check`/`run`/`fmt`/`-e`/the
  REPL with a raw stack trace (exit 134). The digits are now parsed with
  `Int64.TryParse`, and an out-of-range value is a located parse error —
  `attribute argument out of range (64-bit): <digits>` — mirroring the
  integer-literal parser. The duration/size unit arms (`…TiB`, `…h`)
  additionally bound their scaling multiply, so a large-but-in-range base
  can no longer silently wrap negative. In-range attributes (`30s`,
  `10MiB`, `42`, `0.5`) are unchanged.

- **A bareword `;`-spine no longer parses in O(N²).** A `let x = b;b;b;…`
  line (N barewords) cost quadratic time: a bare `;` is a literal argv
  word in command position, so the doomed command-mode attempt's
  head-word scanner consumed the whole remaining line before failing,
  once per bareword. The command head-word scan is now bounded (a
  resolvable command head is a PATH entry, ≤255 bytes), which caps the
  doomed attempt and makes the line linear — 20k barewords fell from
  ~27s to ~5s. Resolvable command heads and the diagnostic on the spine
  are byte-identical; argv words longer than the bound still parse as
  arguments (no grammar change).

- **The top-level CLI verb dispatch has a residual-exception guard.**
  `check`/`run`/`fmt`/`-e` run weir's front end, but the verb dispatch
  had no top-level `try`/`with`, so any residual front-end exception
  aborted with a raw stack trace and exit 134. A residual exception now
  becomes a located `internal error: …` diagnostic on stderr with a
  non-zero exit, mirroring the LSP's per-document guard. Kept narrow: a
  legitimate `exit`/`fail` is never swallowed. This backstops the whole
  front-end crash class behind the two point fixes above.

- **`weir check` / `--can` / the LSP no longer multiply a per-statement
  inference-budget burn across a whole file.** The type checker's
  inference budget bounds one statement, but the whole-file analysis
  (`analyzeLines`) collects errors and continues after each — so a file
  with several independent budget-exhausting statements multiplied the
  single-statement cost with no whole-file ceiling (linear in blocks,
  re-paid on every LSP keystroke: a CPU-exhaustion DoS). A budget-
  exhaustion diagnostic is now a stop-and-fix, exactly as the runner and
  module loader already abort on the first error: the fold stops at the
  first budget diagnostic and checks no further statement, so one burn
  bounds the file. Ordinary type errors are unaffected — a file with
  three plain type errors still reports all three. `[D:budget-stop-first]`

- **`weir lsp` no longer allocates for a client-supplied `Content-Length`
  before reading the body.** The framing layer allocated a buffer of
  exactly the declared length, so `Content-Length: 2147483647` (26 bytes)
  triggered an `OutOfMemoryException` that killed the editor session (an
  OOM DoS). The transport now caps accepted messages at 64MB and never
  allocates for a larger declared length: an oversized body is drained in
  small fixed chunks to keep the stream synced and dropped as an id-less
  no-op, so the server keeps serving. The header-line accumulator is
  independently capped at 64KB so an unbounded header line cannot grow
  memory. `[D:lsp-transport-caps]`

- **The line assembler no longer scales quadratically on hostile inputs
  (algorithmic-DoS) [D:assemble-quadratic].** Four O(N²) paths in the
  logical-line assembler let a crafted hundreds-of-KB source stall
  `weir check`/`fmt`/REPL/LSP for tens of seconds to minutes:
  1. the pending statement's text was rebuilt (`text + sep + piece`,
     plus a full-text `TrimEnd`) on every continuation join — now a
     single growing `StringBuilder` is mutated in place and materialized
     to the immutable `LogicalLine.Text` exactly once at statement close;
  2. the bracket-continuation dangle predicates called `TrimEnd()` on the
     whole accumulated text per line — now answered from an
     incrementally-tracked last-non-white index;
  3. the `within proc`/`within serve` binder-head scan re-`LastIndexOf`'d
     and re-scanned the whole growing text per join — now it reads an
     incrementally-tracked last-segment start (and a `Contains` fast
     reject skips the per-char scan on marker-free segments);
  4. the record-update (`{ … with`) header check allocated a
     whole-remaining-line substring per opening bracket, so a
     bracket-heavy single line was quadratic — now an index-based,
     allocation-free check.
  The emitted diagnostics and the assembled span tables are byte-
  identical (a pure performance fix, no grammar change): the join
  arithmetic derives `joinedStart` from the buffer length at exactly the
  point the old code read the string length, and the same separator
  literals apply. Measured `weir check` on the standing fixtures:
  continuation-line flood 80k lines 9.3s → 1.3s; the bareword
  `within proc` marker case 80k lines 24.1s → 3.6s; a bracket-heavy
  single line 400k record openers 29s → 0.5s — quadratic to linear.

- **A NUL byte can no longer silently truncate a value at any process
  boundary.** weir's word-integrity guarantee (SECURITY.md) refuses a
  NUL-bearing value at the spawn hand-off — argv and env are
  NUL-terminated C strings, so an unrefused NUL would truncate the word
  at the child, which runs the prefix and exits 0 with no diagnostic.
  The refusal lived only in the evaluator's command-statement and pipe
  constructors; four other spawn paths assembled argv/env downstream and
  skipped it, so a NUL entering as external data (a command whose stdout
  carries a NUL, `File.read`, base64-to-bytes) leaked through: the
  exit-code reifiers (`| complete`/`succeeds`/`exitCode`/`orFail`), the
  ambient `within env` overlay and its `$e(...)` twin, `into`, and — the
  worst — the dynamic command head `^$name`, where a NUL-bearing head
  resolved through PATH to the *prefix* program and ran it with the
  remaining words as argv. The NUL refusal now lives at `Proc.spawn`, the
  single point every process start funnels through, validating the
  program name, every argument, and every environment key and value — so
  all four downstream paths inherit the boundary and refuse with the same
  located diagnostic (exit 1, no child spawned). Two adjacent shapes gain
  weir-shaped diagnostics too: an empty program name, and a NUL-bearing
  path-like program name (which previously leaked a raw
  "Null character in path" platform exception through `Path.GetFullPath`).
  The pre-existing statement-path refusal is unchanged.
  `[D:spawn-nul-funnel]`

- **A deeply-nested YAML document no longer hangs the parser.**
  `Yaml.parse` (and `from yaml T`, `#infer`, `Yaml.inferShape`, and
  `weir check` on a deep-ladder yaml district) recursed once per
  mapping/sequence nesting level with no limit, and each mapping level
  re-scanned its remaining extent — so a hostile `a:` ladder was cubic
  in depth and hung for tens of seconds to hours. The parser now caps
  nesting at 500 and fails with a located diagnostic ("yaml nesting is
  too deep (limit 500) — the subset reads real manifests, not
  adversarial ladders"). Real manifests nest far below the cap and are
  unaffected. External command output piped into a yaml adapter is the
  realistic hostile source this closes.

- **A deeply-nested value no longer crashes `to yaml`.** The YAML
  emitter recursed per nesting level with no limit, so a value built
  iteratively (e.g. a `Seq.fold` nesting a `YSeq` ~100k deep) crashed
  the process with an uncatchable stack overflow (exit 134) — hostile
  or malformed input must never crash the tool. The emitter now caps
  nesting at 1000 and fails with a clean located error ("to yaml: the
  value nests deeper than 100 — the emitter needs finite trees").
  Finite, real-depth trees render unchanged.

- **`==` on a deeply-nested value no longer crashes the process with an
  uncatchable stack overflow.** A legally-built recursive-record value —
  an `Option`-linked record folded ~100k deep via `Seq.fold`, which the
  checker accepts — crashed the whole `weir` process (StackOverflow, exit
  139/134) when compared with `==`, because value equality recursed one
  stack frame per nesting level. Equality now walks an explicit heap
  work-list of pending value pairs instead: records (order-insensitive),
  union payloads, tuples, and map entry values queue their children,
  seqs compare lockstep via enumerators (short-circuiting at the first
  mismatch), and a mismatch drains the list. Every existing equality
  semantic is unchanged — closures/builtins/procs/servers still compare
  by reference, bytes structurally, floats by value. The `show` /
  interpolation renderer shared the same crash class and now carries a
  finite depth bound (100, past every real value; deeper nesting renders
  a teaching ellipsis), matching the REPL echo's existing bound.

## v0.0.47

### Fixed

- **A parallel or race combinator inside a `plan` now refuses instead of
  escaping capture.** `plan`'s mutation capture is thread-local, but
  `Seq.pmap`/`pmapWith`/`piter`/`piterWith`/`pfirst`/`pfirstWith` run
  their callbacks on worker threads that did not inherit the capture
  frame — so a `File.write` (or any native mutation) inside a parallel
  callback under `plan` executed for real while the plan reported empty.
  The combinators now refuse on the calling thread before any worker is
  scheduled, with a located error naming the combinator. The same
  combinators outside a plan are unchanged, and a native mutation reached
  through a *serial* helper still captures normally.

- **An indirect process started inside a `plan` now refuses at runtime.**
  The static plan check does not follow helper-function references, so a
  helper that forced a command (e.g. `sh -c "…"`) spawned the process
  for real during plan construction while the plan reported empty. The
  process spawn point now refuses when a plan is capturing on the same
  thread, with the same "proc refused inside plan" message. The direct
  `sh -c` form still refuses at check time (belt and suspenders), and a
  weir-native `File`/`Dir`/`Http` mutation reached through a helper still
  captures — only process spawns are refused.

- **`within serve` reaches the handler over `localhost`, not just
  `127.0.0.1`.** The scoped listener registered a prefix for
  `127.0.0.1` only, so a client addressing the server as `localhost`
  got .NET's built-in `404` before the weir handler ran (the
  `Host: localhost` request did not match the `127.0.0.1` prefix). The
  listener now registers the common loopback names beside each other on
  the one port — `127.0.0.1`, `localhost`, and `[::1]` — so all three
  reach the handler. The posture stays loopback: no `+`/`*`
  all-interfaces bind, so the server is not exposed beyond loopback.
  The `[::1]` name is guarded — a host without IPv6 loopback still
  serves on the two IPv4 names. Bind, teardown, port-free, and
  `Server.port`/`Server.running` are unchanged.

- **`File.isExecutable ""` raises instead of returning `true`.** An
  empty string resolved to the current directory (which exists and
  carries a directory's execute bit), so the query answered `true` for
  a path that is not a path. `""` now raises the same located
  `no such path` error a missing path raises. A real missing path still
  raises, unchanged.

- **`restore` and `verify` confine a lock entry's path to `.weir/`.** A
  hand-edited or tampered lockfile whose entry recorded a path outside
  the vendor directory (absolute, or `../`) made `restore` write and
  `verify` read outside `.weir/` — `restore` crashed on the write. Each
  entry's path is now confined per entry: a hostile one is a located
  refusal naming the entry, while its benign siblings still restore.
  `restore` overwrites only its own artifacts inside `.weir/`.

- **Contract-path confinement now resolves symlinks.** The path check
  above was purely lexical, so a lock path with no `../` and no absolute
  root still escaped `.weir/` when a component on the way — an
  intermediate directory like `.weir/schemas`, or the final file itself —
  was a symlink pointing outside. `restore` followed it and wrote outside
  the vendor directory. Confinement now resolves the real filesystem
  object (symlinks chased on every existing component) and requires it
  under the real `.weir/` root, across every sink: `restore`, `verify`,
  `gen types`, `add schema`, and `add module`. The write opens the final
  component without following a symlinked leaf (`O_NOFOLLOW` on Unix), so
  the check and the write refer to the same object, then `fchmod`s that
  open descriptor to `0o644` (libc `open(2)` is variadic, and its mode
  argument was unreliable on ARM64 macOS — a vendored file could land
  unreadable, so a later `verify` failed with a permission error). The
  full `open(2)` flag set (`O_CREAT`/`O_TRUNC`/`O_NOFOLLOW`) is now
  branched per OS: the Linux values had been hardcoded, so on macOS
  `O_TRUNC` was never set and a re-write over a longer existing file
  (repairing a tampered vendored file) left stale trailing bytes and
  failed the post-repair hash. A user-chosen `gen types --out <path>`
  stays unconfined by design.

- **`add schema --as` refuses a name that is not a plain name.** An
  absolute or traversal `--as` name vendored the schema outside `.weir/`.
  The plain-name rule `add module` already enforced is now shared by
  `add schema`, `add module`, and `gen types`, refused before the fetch
  with nothing written.

- **An unreadable import is a located diagnostic, not a crash.** An
  imported module that existed but could not be read (permissions) crashed
  `check`, `check --json`, `check --can`, and the LSP. The import source
  is now read once into a result: an unreadable file gives a located
  `cannot read import` diagnostic (a missing file still says `no file
  at …`), and the previous read-twice shape no longer races a file
  deleted mid-check.

- **A `for` loop no longer trips `pure`, `readonly`, `plan`, or
  `--strict`.** The `for` desugar targets a library member (`Seq.iter`),
  but the effect classifiers treated every `|`-prefixed internal name as
  a command — so a `for` body that reached no effect was refused inside
  a `pure`/`readonly`/`plan` block, the internal desugar key leaked into
  the refusal message, and each `for` added a phantom dynamic-head
  capability that failed `--strict`. The classifiers now distinguish a
  command reifier from a library desugar and read the latter as its
  target member; no internal `|`-name reaches a user-facing message.

- **A multi-statement `readonly` body parses.** A `readonly` block whose
  body ran a unit statement before its result value space-joined the
  statement onto its successor and mis-parsed (a spurious arity error),
  because `readonly` was missing from the block-sentinel registration
  that `pure` and `plan` have. `readonly` now sequences its statements
  like `pure`; its mutation ceiling is unchanged.

## v0.0.46

### Added

- **`within serve` — the scoped HTTP listener, weir's first server
  surface.** `within serve srv = { port = 8080; maxConcurrent = 4 }
  handler` opens a loopback listener for the block, runs a plain
  synchronous handler (`HttpServerRequest -> HttpServerResponse`) per
  request, and closes the socket on every exit — normal, raise,
  SIGINT and SIGTERM — so the port frees (a second bind on it then
  succeeds). Same no-orphan discipline as `within proc`, port edition:
  the scope IS the lifetime. Three primitives, deliberately no more —
  scoped by the ring protocol study, which proved this surface needs
  no WebSockets (commands are request→response, pushes are SSE):
    - **A scoped listener with a sync handler.** The handler matches
      on `req.path` (weir's union/`match`, not a routing framework) and
      returns a response record. Async-under-sync: the runtime
      multiplexes connections on tasks; the handler surface stays
      synchronous.
    - **A streaming response body: `Stream of seq<string>`,** a new
      case on the shared `HttpBody` union beside `NoBody`/`Json`/`Text`.
      The runtime pulls and flushes each element as it is produced —
      chunked, SSE-shaped `data:` lines — so a slow producer streams
      incrementally (the client sees early elements before the seq
      ends). The lazy-seq-streams-out precedent `print` sets, server
      side.
    - **A handler concurrency ceiling: `maxConcurrent` on the config,**
      the `Seq.pmapWith` concurrency law on the scope — N handlers run
      at once, excess queues.
  One type family: `HttpMethod`, header pairs and the body union are
  shared with the `Http` client; the server records differ only where
  the wire differs (`HttpServerRequest` carries `path` + `query`, not a
  `url`; no `auth`/`timeout` — client concerns; `HttpServerResponse`'s
  `body` is an `HttpBody`, so `Stream` is expressible). The handle
  surface is `Server.port` and `Server.running` (the scope is the
  teardown — there is no `stop`). Bounded out of v1, each a stated
  non-goal: TLS (a reverse-proxy posture), a routing DSL, WebSockets,
  request-body streaming, HTTP/2.

- **Dynamic command heads: `^$name` / `^$(…)`.** The `^`
  force-external head gains a `$`-splice alternative beside the
  literal — a runtime string names the program, and argv stays typed
  argv, so the computed-command shape that used to force
  `sh -c "$path …"` (plugin callbacks, asdf-style `exec` dispatch,
  lookup-table tools) runs injection-safe: `^$tool $userArg` passes
  one entry per argument and a head value with spaces is one program,
  never re-lexed. The value must be a `string`; a seq capture refuses
  with a bind-and-pick teaching (`let tool = $(…) |> Seq.exactlyOne`,
  then `^$tool`), while a string-typed capture
  (`^$(… |> Seq.exactlyOne) args`) heads directly. Resolution moves
  to run for exactly this head: `weir check` draws no cmd-not-found
  diagnostic (there is nothing to look up yet), a missing program is
  a located run error naming the value, and `weir check --can`
  reports the head as not statically known, counted with the opaque
  sites (`--strict` exits 2). Pipes, `$()` captures, the
  `| complete` family, value-headed stdin and env sigils compose
  exactly as with a literal head; `^$@xs` and `^$"…"` refuse with
  teachings.

- **Width members: `Str.replicate`, `Str.padLeft`, `Str.padRight`.**
  `Str.replicate n s` concatenates n copies (0 → empty; a negative
  count raises — Seq.replicate's convention). `padLeft`/`padRight`
  pad with spaces to a TOTAL width, .NET Pad semantics: an
  already-longer string is unchanged, a negative width raises. The
  primitives every columnar CLI port hand-rolled (asdf's `current`,
  `printf "%-15s"`). `replicate` now has two homes (Seq and Str), so
  the bare REPL name retires — qualified-only, the derived
  bare-partition rule.

- **`Seq.equal : seq<'a> -> seq<'a> -> bool`.** Element-wise,
  length-sensitive, Eq-constrained like `Seq.contains` (functions,
  seqs and floats refuse at the use site, floats naming
  `Float.near`). Lockstep and short-circuiting — never pulls beyond
  need, so a finite seq compares safely against an infinite one. The
  honest spelling of output-vs-expected; a join-then-compare is lossy
  when elements contain the separator.

- **Hex, slicing and HMAC on `Bytes`: `fromHex`/`toHex`, `sub`,
  `hmacSha256`.** `toHex` emits lowercase (sha256 parity); `fromHex`
  reads either case and refuses odd-length and non-hex input with
  located teachings (fromBase64's posture). `Bytes.sub start len b`
  is `Str.sub`'s exact shape on bytes, out-of-range raising with the
  same detail. `Bytes.hmacSha256 key msg : Bytes` is .NET's
  HMAC-SHA256 (key first, then message; RFC 4231 vectors pinned) — a
  Secret key exits via `Secret.reveal |> Str.toUtf8`, deliberately.
  Erases the ~70 hand-rolled hex/codec lines of the acme.sh port and
  its External-Account-Binding openssl fallback.

- **`File.isExecutable : string -> bool`.** The mode's OWNER execute
  bit as a bool — replaces the stringly
  `File.mode |> Str.contains "x"`. Follows a symlink and raises on a
  missing path like `File.mode`; on Windows (no execute bit) it
  answers by extension (`.exe`/`.bat`/`.cmd`/`.com`) — a stated
  posture, never a guess.

### Changed

- **BREAKING: `Seq.force` is renamed `Seq.freeze` (bare alias
  `force` → `freeze`); the old spelling is gone, no compatibility
  alias.** "force" named the moment (evaluate now) and misleadingly
  suggested repeatability; the durable property of the result is a
  frozen snapshot — pulls never re-run. Migration:
  `s/Seq.force/Seq.freeze/`, bare `force` → `freeze`. A stray old
  spelling teaches the new name at both lookup sites.

### Fixed

- **Detached SIGINT now tears scopes down.** A `kill -INT` on a weir
  backgrounded in a non-interactive session (`setsid`, a CI runner, a
  systemd unit) was a no-op: scopes did not unwind, `always` cleanups
  were skipped, `within proc` children orphaned, and only SIGKILL
  stopped it. The cause: such a shell sets SIGINT to `SIG_IGN` before
  exec (the job-control nohup convention) and .NET honours an
  inherited `SIG_IGN`, so weir's signal handler never installed. A
  detached weir (no controlling terminal) now resets SIGINT to its
  default disposition, so SIGINT runs the same scope-unwind and
  child-reap SIGTERM and a tty Ctrl+C always ran, exiting 130. A
  second signal during teardown hard-exits (the double-Ctrl+C escape).
  A `weir … &`/`nohup` in an interactive shell keeps its controlling
  terminal and its ignored SIGINT unchanged — a terminal Ctrl+C still
  does not reach it (send SIGTERM, or `kill -INT` the pid). SIGTERM
  was already correct and is unchanged; POSIX only (Windows keeps
  `Console.CancelKeyPress`).

- **The operator partial-application teach no longer offers two
  different functions as interchangeable.** `(op) v` still refuses;
  for a commutative operator (`==`, `<>`, `*`) the message keeps
  both lambda spellings as alternatives, and for every other
  operator it now says the directions differ (`fun x -> x - v and
  fun x -> v - x differ`) instead of the `(or …)` form. `+` takes
  the differ-form too: it concatenates strings by context, and the
  operand type is not known at the refusal site.

- **A qualified case in pattern position teaches the bare law.**
  `| Core.System ->` errored with a bare expecting-list; it now names
  the repair — write `System`, not `Core.System`: the scrutinee's
  type resolves the case, imported unions included.

- **A qualified type in the adapter slot teaches the flat-import
  law.** `from json Acme.Pod` errored unhelpfully; it now says write
  `Pod`, not `Acme.Pod` — imported types resolve by their plain name
  (the module alias qualifies values, not types in this slot).

- **A capture sigil composed with a reifier names the spelling that
  works.** `$e(cmd) | complete` still refuses (the sigil closes its
  chain at the `)`), but the error now names the composition that
  exists: `$e(cmd | complete)`.

- **A block-local binder piped into inside the same statement is no
  longer a phantom command.** Under check's assume-command rule,
  `… |> pad` where `pad` is a `let` earlier in the same function body
  mis-read as feeding a program (`'|>' applies functions; feed a
  program with '|'`). The pipe-target walk now tracks
  statement-local binders (lets, params, match/for/within binders) —
  bindings beat PATH, block-locals included.

- **A helper ending in `exit`/`fail` used as a statement names its
  cause.** Such a helper infers a polymorphic return (diverging calls
  are never unit), so using it as a statement was a bare
  "computes a 'a1 and discards it". The discard error now explains
  the polymorphism and both repairs: return the exit code and exit at
  the call site, or bind the value.

## v0.0.45

### Added

- **The checker warns on possible re-enumeration.** A `let` whose RHS
  is a command-backed seq with no visible force (`|> Seq.force`, an
  applied `Seq.force`, an eager literal or a comprehension) re-runs
  its command on every pull. When such a binding is enumerated at two
  or more sites, `weir check` now warns at the second and later
  sites — naming the command and the one-line repair
  (`snapshot one run: let pods = kubectl get po -A |> Seq.force`).
  Advisory: warning severity, exit stays 0, and the LSP shows it as a
  squiggle. Conservative and stated: any read of the name counts as a
  possible pull (argv splats included); a plain alias
  (`let y = pods`) does not.

- **The REPL binding echo states the bound seq's state.** At a tty,
  `let pods = kubectl get po -A` now echoes
  `pods : seq<string> (command-backed — re-runs on each use)`, and a
  materialized bind echoes `(frozen)` — the same forcedItems probe
  the echo and completion already share. A pure lazy seq stays
  unannotated. Display only; the piped surface is unchanged.

- **`weir gen types --schema <name>` — generate weir types from a
  locked JSON Schema.** The locked `.weir/schemas/<name>.json` (from
  `weir add schema`) becomes a declaration-only weir module at
  `.weir/types/<name>.weir`, imported as `import "weir:<name>" as …`
  (the `weir:` namespace now resolves vendored modules first, then
  generated types). `#infer` drafts from a sample and can only see
  what the sample had; the schema carries the facts no sample can —
  a member of `required` generates a plain field, anything else
  `Option<…>`, the nullable spellings (`type: [.., "null"]` /
  `nullable: true`) fold into `Option`, `additionalProperties`
  generates the `seq<string * V>` mapping, `$ref` definition names
  become type names (`io.k8s.api.core.v1.PodSpec` → `PodSpec`,
  parent-segment prefix on collision), an `enum` generates `string`
  plus a `//` note listing the values, `anyOf` takes the first
  variant with a verify note, and a self-referential definition
  stays opaque (`Yaml`) with a note — never a silent guess. Field
  names ride `#infer`'s own `[<Wire>]` sanitizer; the emitter is
  shared, only the frontend differs. The generated file is
  user-owned and deliberately unlocked: the provenance header
  records the schema name and lock hash, regeneration is an
  explicit re-run (`--as` names the top type, `--out` moves it,
  `--out -` streams to stdout), output is byte-deterministic, and
  the emitted module runs through the real checker before anything
  lands — a schema producing unrepresentable weir refuses with the
  reason.

- **The schema subset reads the raw k8s OpenAPI shape.** `weir add
  schema` (and `schema=` district validation) now accept in-document
  `$ref` (`#/definitions/…`, `#/$defs/…`, `#/components/schemas/…`,
  resolved against the root holders — a dangling ref refuses at
  add), `allOf` of one schema plus annotations (the k8s
  $ref-with-description idiom), `anyOf` (all-scalar folds like
  `oneOf`'s IntOrString; mixed alternatives validate as unchecked),
  and `nullable: true`. Cross-file `$ref` still teaches the
  standalone variants; `not`/`if`/`then`/`else` stay outside the
  subset.

- **`from json`/`from yaml` missing-field errors teach the Option
  repair.** `missing field 'env' …` now carries the from-table
  spelling: if the field is sometimes absent, declare it
  `Option<…>` — a type drafted from a sample only sees what the
  sample had.

## v0.0.44

### Added

- **`Str.fields` and `Str.rsplit` — the shell's field splitting, as
  members.** `Str.fields s` splits on whitespace runs and never
  yields an empty piece (leading/trailing whitespace produces
  nothing; a blank or empty string is the empty seq) — POSIX field
  splitting with trim's whitespace class, so the kubectl-column
  idiom is `line |> Str.fields |> Seq.item 1` instead of
  `Str.rmatchAll @"(\S+)" line |> Seq.map Seq.head`. `Str.rsplit
  pat s` splits on every regex match, pattern-first like the rest
  of the r-family: `Str.split`'s empties law verbatim (adjacent
  matches and edges yield `""`), capture groups never add pieces,
  and a bad pattern raises like `rmatch`.

- **Tab completes a bound map's keys.** With open maps first-class,
  keys are data — no static pool can offer them. The REPL now reads
  them from the value you already hold: inside the open key literal
  of a pipe-form `Map.get`/`Map.tryGet`/`Map.has`,
  `d |> Map.tryGet "Cor<TAB>` completes the bound value's own keys,
  prefix-filtered, inside the quotes (no closing quote is added —
  the path-in-quotes convention). The receiver must be a bare session
  binding (`it` counts) whose value is already materialized — a map
  or a forced pair-seq. Completion still never runs anything: a
  pipeline receiver would need evaluating, so it completes nothing
  (bind first — `let d = …` — and the keys complete), and an
  unforced seq is never pulled.

- **`from table T` reads aligned column output.** The shape `kubectl`
  and `docker` print — one header row, aligned data rows — reads into
  declared row records: `kubectl get po |> from table Pod` yields
  `seq<Pod>`. Columns slice at header offsets, never whitespace runs,
  so a spaced value (`Up 2 hours`) survives; a header boundary is a
  run of two or more spaces, so `CONTAINER ID` stays one column (both
  tools pad columns with three spaces). Fields match headers by
  normalized name, case-insensitively (`podTemplateHash` reads
  `POD-TEMPLATE-HASH`); `[<Wire "HEADER">]` matches a raw header
  verbatim. Cells trim and type by the declared field
  (string/int/float/bool); an `Option` field reads an empty or
  `<none>` cell as `None`, and a required field refuses an absent
  cell naming the `Option` repair. Extra columns are ignored, blank
  lines skip, a header-only table is the empty seq, and every error
  is located — a missing column lists the headers seen, a bad cell
  carries its row line and column. Read-only: there is no `to table`.
  `table` stays an ordinary identifier outside adapter position.

- **`#infer … from table as Pod` drafts the row record.** The REPL
  directive (and its composable core, `Table.inferShape`) scans a live
  sample per column — all-int columns draft `int`, float/bool by
  token, else `string`; a column with empty/`<none>` cells drafts
  `Option` with a note — and prints that the value reads as
  `seq<Pod>`. Headers sanitize to field names with `[<Wire>]` where
  the spelling needs it, the taken-name guard included.

  Note for the release flow: the external tree-sitter-weir grammar
  needs the matching `table` adapter bump before the next release —
  the grammar-currency gate will go red until it lands.

### Changed

- **`#infer` merges array elements.** A drafted element type was the
  first element's shape only, so real kubectl lists died on read
  (`kubectl get cm -o json` → `missing field 'items[2].data.networkYml'`
  — every ConfigMap's `data` carries its own keys). The element type
  is now the UNION of all elements: a key absent in some elements
  drafts `Option<T>`; a genuine type conflict for one key keeps the
  first element's type with a printed verify note. Differing key sets
  across elements are ordinary now — no note.

- **`#infer` drafts data-keyed objects as open mappings.** An object
  whose entries carry one value shape and whose keys look like data —
  a majority of non-identifier keys (k8s `labels`/`annotations`), or
  key sets that differ across the array's sibling elements (a
  ConfigMap's `data`) — drafts as `seq<string * V>` with a note
  instead of a `[<Wire>]`-riddled record. Mapping keys are data, so
  no wire attribute is drafted; `metadata`-shaped objects (identifier
  keys, identical across elements) stay records. An empty `{}` is an
  open map with zero entries and drafts `seq<string * string>` with a
  note — the old opaque `Yaml` draft could not cross the json
  boundary, so a json-sourced draft carrying `resources: {}` checked
  but never read.

- **`from json`/`to json` speak the `seq<string * T>` mapping.** The
  yaml boundary's pair-seq law, now on json too: a field (or nested
  position) typed `seq<string * T>` reads a JSON object as pairs in
  document order and writes back as one object, so the drafted
  mappings read and roundtrip on both wire formats. An empty mapping
  writes `[]` and reads back empty — the roundtrip holds; `Map`,
  `jsonl`, and every existing shape are unchanged.

- **`it` follows F# Interactive: expressions and commands always
  rebind it; `let` never does.** Every expression and command
  statement now binds `it` — unit included: a streamed bare command
  binds `it := ()` (weir never held the bytes, so unit is the truth)
  and the tty echoes `() : unit` for unit statements; a `let` binds
  its name and nothing else (`let o = 10` binds no `it`, FSI's own
  rule — previously the `let` RHS bound it and unit statements left
  it alone). Directives still leave `it` untouched and a fresh
  session's `it` stays unbound. The v0.0.43 streamed parenthetical
  (`(streamed — not bound to 'it'; let x = … captures)`) reverts to
  the plain `: seq<string>` — a standing teach on every command was
  noise. The teach now fires only at misuse: using the unit-bound
  `it` where unit fails the check (`#infer it from yaml`,
  `it |> map …`) appends the capture repair to the located type
  error, with the recorded command verbatim — `to capture: let x =
  kubectl get po -A -o yaml`. The piped REPL's byte surface is
  unchanged (a piped bare command still binds the value; unit stays
  invisible there).

- **A named function value echoes a mini-help.** A bare expression
  evaluating to a function no longer echoes the opaque `<builtin> :
  ty` / `<fun> : ty`. A builtin (a bare alias like `find` included —
  it names its home, `Seq.find`) echoes the qualified signature plus
  the doc's first line, composed from the exact `#help` sources; a
  session-defined function echoes `name : scheme` plus a dim line
  with its definition's first physical line (a redefinition shows
  the last accepted; multi-line definitions clip with an ellipsis).
  Anonymous and composed closures keep `<fun> : ty`. Echo type
  metadata also normalizes type-variable display — `'a1 -> 'a2`
  reads `'a -> 'b` (the REPL echo and the `-e` echo; error messages
  keep the checker's names).

### Fixed

- **The `let`-RHS is a head slot — tint, completion, and aliases.**
  `let svc = kubect` typed at the prompt got no live head tint and no
  head completion, though the RHS has been a first-class command
  position all along. Both surfaces now read ONE position predicate:
  the RHS head tints by the session verdict exactly as a statement
  head does (known bold, PATH bold-blue with dim argv, unresolved
  red — an unknown uppercase RHS head stays casing-yellow: `let n =
  Some 42` is a constructor, not a failing command), and Tab at the
  RHS offers the statement head's pool (PATH executables, the
  command-callable builtins, bindings and keywords), with argv after
  the RHS head completing as directory entries like any command argv.
  The `^` force-PATH sigil now completes too — PATH names only, at
  the statement head and the RHS alike. Folded in from a live report:
  session `#alias` names were missing from the known-head membership
  everywhere — after `#alias k = kubectl`, `k get po` painted `k`
  red; alias heads are now known (tint) and offered (Tab) at both
  head slots, and `^k` still bypasses the table on both surfaces.

## v0.0.43

### Changed

- **The help tint covers the signature line and the example block.**
  `#help`'s signature renders structurally at a tty — name bold, types
  in the colorizer's own yellow, punctuation dim — and the example
  block goes through the live prompt's colorizer itself, so the two
  can never drift. Piped output, `--repl-doc`, and `NO_COLOR` keep
  the plain bytes.

### Fixed

- **A streamed REPL statement no longer looks like it bound `it`.** A
  bare command at a tty streams straight to the terminal (the
  colour-inherit path — weir never holds the bytes), so `it` does not
  bind — but the meta line read a bare `: seq<string>`, as if a value
  landed, and the next `it` errored with the generic unbound message.
  The meta now says `: seq<string> (streamed — not bound to 'it';
  let x = … captures)`, and an `it` right after a streamed statement
  gets a targeted teach naming the repair with the command verbatim:
  `to capture (and bind 'it'): let x = kubectl get po -A -o yaml`. A
  fresh session's `it` keeps the ordinary error; the piped REPL's
  byte surface is unchanged (a piped bare command still binds `it`).

- **`#infer` derived names dodge taken type names.** A drafted type
  landing on a name the session already resolves was
  injected-and-shadowed: a k8s volume's `secret:` sub-object drafted
  `type Secret`, every `secret: Secret` field resolved to the builtin
  redaction type instead, and `from json`/`from yaml` refused with
  "a Secret must not cross" (a prelude name like `Yaml` died earlier,
  at injection). A derived name colliding with any in-scope type —
  primitive spelling, prelude/builtin nominal, or session-declared —
  now parent-prefixes (`VolumeSecret`) with a printed note naming the
  rename; same-shape dedup between inferred types is unchanged. The
  `as` name stays the user's: one that collides with a builtin
  refuses with a teaching instead of being renamed silently.

- **Multi-line quoted scalars read.** `kubectl get po
  -A -o yaml` evicted-pod `message:` values — a quoted scalar whose
  closing quote sits on a later, deeper-indented line — errored
  `'message' has both an inline value and a nested block` (the
  continuation lines were taken for a nested block). The yaml subset
  now reads multi-line single- and double-quoted scalars with YAML's
  flow folding, PyYAML-refereed: a line break folds to one space,
  each empty continuation line contributes a newline, continuation
  indentation strips, and trailing space before the closing quote is
  content. `''` and the double-quote escape set work across lines (a
  `\`-escaped line break is not in the subset); the continuation
  lines belong to the scalar — map-value and sequence-item position,
  any depth, zero-indent sequences included. Unterminated quotes
  error at the opening line in the unclosed family; content after the
  closing quote errors at its own line. Quotedness stays load-bearing
  across the fold (a folded `no` is still a string), and the write
  side is untouched — weir never emits the multi-line quoted form.

## v0.0.42

### Changed

- **Help renders its code spans at a tty.** `#help`/`#find` docs'
  `` `code` `` spans now print tinted (the colorizer's cyan) with the
  backticks themselves dropped — the span reads as code, not markdown
  source. One renderer covers every REPL help surface. Piped output
  and `NO_COLOR`/`TERM=dumb` keep the literal backticks: the piped
  byte surface (and `--repl-doc` equality) is unchanged, and a
  stripped terminal never loses the span boundary.
- **Help copy drops uppercase emphasis.** Doc strings shouted for
  stress (`It DRAFTS what the sample has`, `NOT check-time
  inference`, `READ-ONLY`, …); the corpus — builtin docs, module
  blurbs, attribute docs, the directive list — is rewritten in normal
  case, syntax depictions (`KEY=value`) moved into code spans, and a
  unit pin now rejects any non-acronym all-caps word in user-rendered
  prose (real acronyms live on a commented allowlist beside the pin).
  The `init: NOT loaded` teaching line is now `init: not loaded`.

## v0.0.41

### Fixed

- **`#infer` sanitizes KEYWORD keys and the empty-string key** — the
  two [D:infer-wire-sanitize] residuals closed. A keyword JSON/YAML
  key (`{"in": 2}` — `let`, `match`, `fun`, …) passed the
  char-class-only identifier test and drafted `in: int`, which the
  parser rejects in field position (probe-pinned: EVERY keyword is);
  it now rides `[<Wire "in">]` over the parser's own repair spelling
  (`inField`), collision-deduped, and the draft checks AND reads. The
  reserved set is the parser's own `Parser.keywords`, threaded in as a
  parameter — the hand-copied three-word set is gone, so the set
  cannot drift. An empty-string key (`{"": 1}`) drafted `[<Wire "">]`,
  which the checker refuses ("expects the wire key as a string") and
  no field name can spell — it is now DROPPED with a printed note,
  and the drafted type still reads the sample (the readers tolerate
  an undeclared key). The e2e drafted-type diagnostic recast onto the
  remaining un-checkable class: a DUPLICATE JSON key.
- **The release pipeline uploads with retry and refuses incomplete
  releases.** v0.0.40 published with 5 of 10 assets: `gh release
  create` hit a transient HTTP 500 mid-asset-upload, a manual recovery
  re-created the release with what had survived, the draft review
  missed it — and a published release is immutable, so v0.0.40 stays
  incomplete forever (missing `weir-v0.0.40-win-arm64.exe`,
  `SHA256SUMS`, `install.sh`, `install.ps1`, `grammar-manifest.json`;
  install from v0.0.39 or this release). Now the draft is created with
  NO assets, uploads run separately with 3 retries and `--clobber` (a
  500-ghost cannot block a retry), and `ci/release-assets.weir` — the
  one copy of the expected-asset list — fails the publish job on any
  missing, part-uploaded, or empty asset before a human sees the
  draft. `ci/release-published.weir` runs the same completeness check
  against the newest published release on every CI run, so an
  incomplete published release stays red on main until the next
  release supersedes it.
  A permanently-incomplete release is acknowledged in
  `ci/release-known-incomplete.yaml` (reason required, swept: a
  listed-but-complete release fails as stale), so the standing CI
  check cannot stay red blocking its own supersede.

## v0.0.40

### Added

- **`#help` is glanceable.** `#help <Module>` lists one member per
  line — the name plus the FIRST LINE of its doc (the same builtinDocs
  source hover and `#help Module.member` read, so the glance cannot
  drift), clipped to the terminal. Bare `#help` gives every module a
  one-line blurb (`Seq — lazy sequence pipeline ops: map, where, fold,
  pmap`) from a new one-source table (`moduleBlurbs`), completeness
  unit-pinned two ways against the derived module list — a new module
  without a blurb fails loud.
- **`#find [query]` — fuzzy help search.** Every module (`Seq — blurb`)
  and every member (`Seq.map — glance`) feeds fzf at a tty, with a
  LIVE PREVIEW of the highlighted name's full doc; Enter prints the
  exact `#help` answer for the selection, Esc returns quietly. The
  preview runs the session's own binary headlessly — a new
  `weir --repl-doc <name>` prints the exact `#help <name>` bytes
  (builtin docs only, nothing evaluated; byte-equality e2e-pinned).
  Without fzf, or piped, `#find query` is a deterministic
  case-insensitive substring filter over the same candidate lines —
  never an "install fzf" message. `--no-extended` leads the fzf argv
  as in Ctrl+R (weir glyphs are fzf operators; `finderFlags` can
  restore `--extended`), `#find` Tab-completes with the other session
  directives, and a typo'd directive now gets a did-you-mean from the
  same one-source list. `docs/reference/lexical.md` gained the
  consolidated directive table (script file / init.weir / prompt —
  the three contexts), gated in e2e so a new directive cannot skip it.
- **`#alias name = cmd [args...]` — command-head aliases, REPL-only.**
  A short head maps to a real program and a fixed prefix of arguments,
  consulted ONLY in command-head position and BEFORE PATH. Declare them
  in `init.weir` (the canonical place), one per line:

  ```text
  #alias k  = kubectl
  #alias kb = kustomize build
  ```

  Now `k get po -o yaml` runs `kubectl get po -o yaml` and `kb
  overlays/prod` runs `kustomize build overlays/prod` — the fixed
  prefix is inserted after the exe, your argv appended. It is a
  RESOLUTION-table entry, not a textual macro: your argv stays typed
  argv, so `k get $x` passes `$x` as ONE argument (the injection law
  holds), and only the HEAD token in command-head position is rewritten
  — a `k` in a string, a variable, or an argument is untouched. The
  resolution order is **alias table → PATH**, and the `^` force-PATH
  sigil skips the table, so a shadowing alias (`#alias ls = ls
  --color`) is bypassable with `^ls`. Aliases are **single-hop** (the
  target is a program, never another alias — an alias-of-alias is
  rejected at load) and **REPL-only**: scripts and `-e` never load
  `init.weir`, so their command resolution is unchanged and an alias
  name there is an ordinary unknown command. The target need not exist
  when defined (like bash); a malformed `#alias` line is a loud init
  error (init stays all-or-nothing). A live `#alias` works at the
  prompt too (bare `#alias` lists the table), but `init.weir` is the
  canonical home.
- **`#save` desugars aliases.** The saved script has no alias table, so
  each kept line's command HEAD is span-rewritten back to the real
  invocation — `let pods = k get po` saves as `let pods = kubectl get
  po`, a kept `kb overlays/prod` as `kustomize build overlays/prod`.
  The rewrite is head-only (a `k` in a string is untouched), and the
  output is alias-free and `weir check` clean.

### Changed

- **The unknown-directive message dropped its `#sig`/`#schema`
  parenthetical.** It was noise for an unrelated typo (a bare `#` or
  `#time` does not care about file directives). Now `#sig`/`#schema`
  at the REPL get their own "file directive, read at check time — no
  effect in the REPL" redirect (mirroring `#session`'s), and the
  generic catch-all is just `unknown directive '…' — #help lists them`.

## v0.0.39

### Added

- **REPL path completion quotes in an expression, stays bare as a
  command argument.** Tab-completing a filesystem path after a function
  head — `File.read ./weir-pods.yaml<TAB>` — used to insert the bare
  path (`File.read ./weir-pods.yaml`), which then failed to parse on
  Enter: a bare path is not a valid weir expression. The completion is
  now a string literal — `File.read "./weir-pods.yaml"` — which parses.
  The distinction is the slot's: a COMMAND-argv path (`cat ./x`,
  `ls ./dir`) stays BARE, the way argv wants it. If you already opened
  the quote (`File.read "./x<TAB>`), the completion lands inside it —
  no second quote is added; a directory keeps its trailing `/` within
  the quotes. Completion still runs nothing — a directory read at most.

### Fixed

- **A truncated `let`-bound seq echo now shows the same unforced
  teaching as a bare echo — visibly.** Echoing a `seq<string>` value
  showed a footer `: seq<string> (first 100 of an unforced seq —
  Seq.force to echo everything)` at the BOTTOM, where you'd see it. But
  `let xs = <over-100-line command>` printed that footer FIRST and the
  100 lines after it, so the teaching scrolled off the top and the bind
  looked like it had silently dropped data — and the footer rendered a
  dangling ` =` before the parenthetical. The `let` echo now prints the
  lines (or table) first and the `name : type (hint)` footer last,
  identical in shape and ordering to the bare-expression echo. A forced
  seq still echoes whole with no teaching; the piped/`-e` surface is
  unchanged.
- **A failing `#infer` drafted type now points at the bad field.** When
  a type `#infer` drafts from a sample fails to check for any reason,
  the message was opaque — `#infer: a drafted type did not check:
  Expecting: ':'` with no location and no snippet. It now reports the
  `line:col` within the drafted text and prints the offending drafted
  line with a caret under the column (the same rendering weir uses for
  ordinary parse and type errors), so you can see which generated field
  is wrong.

- **The owned YAML subset now reads kubectl's zero-indent block
  sequences.** A block sequence written at the SAME column as its parent
  mapping key — the form `kubectl get -o yaml` (and most k8s tooling)
  emits, `items:` / `- apiVersion: v1` flush-left under the key — failed
  with `'- apiVersion' has both an inline value and a nested block`: the
  parser only consumed more-indented lines as a valueless key's value,
  so a same-column sequence was reparsed as sibling mapping keys. A
  valueless key followed by a `- ` sequence at its own indent now takes
  that sequence as its value, top-level and nested (a sequence item's
  map containing its own same-column sequence, e.g. `metadata:` →
  `ownerReferences:`). The classic indented-dash style still reads, and
  the two styles read to the same value. A genuinely malformed inline
  value plus a more-indented nested block still errors.
- **Empty flow collections `{}` and `[]` are now read as values.** Real
  kubectl output is full of `resources: {}`, `securityContext: {}`,
  `emptyDir: {}`, `lastState: {}` (and occasionally `[]`); the block-only
  subset rejected all flow style, so a real List would not `from yaml`.
  The two EMPTY forms (inner whitespace tolerated) are now the narrow
  exception — unambiguous at zero elements — in both map-value and
  sequence-item position: `{}` reads the empty mapping, `[]` the empty
  sequence, and both render back so they round-trip. POPULATED flow
  (`{a: 1}`, `[1, 2]`) still rejects with the block-only teaching. An
  opaque `Yaml` field now reads structure whole, so `#infer`'s empty-`{}`
  fallback is readable.
- **`#infer` sanitizes keys weir cannot spell as field names.** JSON/YAML
  keys emitted verbatim as field names — k8s labels like `k8s-app`,
  `pod-template-hash`, `node.kubernetes.io/os`, `app.kubernetes.io/instance` —
  drafted a `type` that would not parse (`Expecting: ':'`). A key that is
  not a legal, non-reserved weir identifier now camelCases into a valid
  identifier (`k8s-app` → `k8sApp`, `node.kubernetes.io/os` →
  `nodeKubernetesIoOs`) and rides a `[<Wire "original-key">]` attribute
  the readers honor; a clean key stays bare with no attribute; two keys
  colliding on one identifier disambiguate deterministically
  (`aB`/`aB2`/`aB3`). The drafted type now CHECKS and READS the real
  data, JSON and YAML alike.

## v0.0.38

### Fixed

- **A command line ending in `yaml` is argv, not a district marker.**
  `kubectl get po -o yaml`, `docker … --format yaml` — everyday ops
  commands whose last word is `yaml` — failed at assembly with
  "line-end 'yaml' needs an indented block below it", misread as arming
  a `yaml` district. The marker now arms token-precisely: only a bare
  `yaml` (the next-line form) or a `= yaml` assignment RHS
  (`let d = yaml`) arms; `yaml` preceded by argv (`-o yaml`,
  `--format yaml`, a trailing bare word) stays a command. `weir check`
  and `weir <file>` agree — the check gap is closed.
- **`#save` now distills a session to its checkable definitions.** The
  old `#save` dumped the raw transcript: a multi-line heredoc came out
  flattened onto one line by the assembler's join sentinel (an `illegal
  control character` parse error), a redeclared `type` was written twice
  (`dup-type`), and `let x = it` was saved though `it` is REPL-only
  (unbound in a script) — so the "runnable" file did not `weir check`.
  `#save` now DISTILLS (option B — a session is scratch; `#save`
  crystallizes definitions): it keeps `type` decls and self-contained
  named `let` bindings with their REAL multi-line source, DEDUPS a
  redeclared name to its last form (survivor order preserved), and DROPS
  the bare-echo scratch. The written file is GUARANTEED to check clean —
  any surviving statement that still references session-only state (a
  `let x = it`) is removed and `#save` prints a dropped-count note.
- **`#help <TAB>` now completes session types (and modules and forms).**
  `#help Patatas` documented an `#infer`'d type fine, but `#help Pat<TAB>`
  offered only `Path` and `Patch` — never `Patatas`, because the argument
  completed against the general pool, which never surfaces `env.Types`
  names. The `#help` argument now completes the same set `#help` can
  document, so an injected type is completable, not just documentable.

## v0.0.37

### Fixed

- **REPL Tab completes record fields through a pipe.** A record piped
  into `_.` or a lambda param — `x |> from yaml T |> _.`, `r |> _.`,
  `x |> … |> (fun row -> row.` — offered no fields (or, for the lambda,
  every declared record's fields, unioned). The completer only unwrapped
  a `seq` element and gave up on a scalar record, but `from yaml T`
  yields one document, not a sequence. It now types the value flowing
  into the position — a `seq` still unwraps to its element, a scalar
  record surfaces its own fields — so `#infer`'s payoff (a typed record
  → field completion) lands in the pipe positions people actually use.
- **Tab on an empty prompt no longer dumps the universe.** A fresh Tab
  used to return over a thousand candidates — every PATH executable plus
  every module, keyword and constructor, sorted — because the empty
  prefix matched everything. It now offers the session directives,
  `#help` first, so a bare Tab teaches the REPL's affordances instead.
  A prefix (`Fi`, `Wr`) filters as before, and a Tab in argument
  position still lists the directory.
- **Union-case constructors are no longer offered as statement heads.**
  `WriteFile`, `Some`, `Get`, `Bearer` and the other constructors leaked
  into head completion, but a constructor as a statement head is a
  discarded value (a check error). They are subtracted from the head
  pool while staying available in expression and argument positions,
  where they are valid.

## v0.0.36

### Added

- **`#infer` — draft named types from a JSON/YAML sample, in the
  REPL.** `#infer <source> from <json|jsonl|yaml> as <Name>` evaluates
  the source ONCE (a binding, a `$(…)` capture, or a bare command),
  parses it by the named adapter, walks it to a set of named `type`
  declarations, and INJECTS them into the session — so `from json
  <Name>` checks and field completion lights up. Auto-naming: a nested
  record takes its field name capitalised (`metadata` → `Metadata`), a
  seq-of-record element the field name singularised best-effort
  (`items` → `Item`, `data` stays `Data`), a same-name-different-shape
  collision a parent prefix (`PodSpec`/`ContainerSpec`), a same-shape
  dedup to one type. A single sample cannot see optional/absent fields,
  so absent/null/empty-array/heterogeneous cases PRINT a note rather
  than guess. Scaffolding, not a language change: `check` stays
  evaluation-free and `from json` never sniffs — the `weir add schema`
  category (external structure → a declaration you own and edit). The
  source defaults to `it` (the last result) when omitted. The same
  inference is a composable builtin — `Json.inferShape`/`Yaml.inferShape
  : seq<string> -> string` return the declaration text outside the REPL.
- **`#save <path>` — dump the session to a runnable script.** Writes
  the accepted statement lines to a `.weir` file, auto-qualifying bare
  aliases (`map` → `Seq.map`, `startsWith` → `Str.startsWith`) via the
  same map the checker's did-you-mean reads, then formatting the result.
  Errored lines drop; injected `#infer` types come out as ordinary
  `type` decls; a bare non-unit expression echo is saved as a `let _rN
  = …` discard so the strict unused-binding law holds. The saved file
  `weir check`s clean. `#infer` to explore, `#save` to keep.
- **`it` — the last result, bound at the prompt.** Every REPL line that
  produces a value (an expression, a command, or a `let` RHS) also binds
  it to `it` (ghci's convention). REPL-only; a unit statement or a
  directive leaves it untouched. Lets a just-built pipeline feed the
  next line (and the no-source `#infer`).

### Fixed

- **`$<<<` heredocs now dedent byte-identically to plain `<<<`.** The
  interpolated form over-stripped leading whitespace, flattening
  deeper-indented lines to column 0, while `<<<` correctly preserved
  indentation relative to the first content line. The two forms now
  differ ONLY in whether `{holes}` interpolate — indentation, interior
  blank lines, deeper indentation and trailing-blank clipping match
  exactly, as the docs always promised.
- **A piped REPL (`printf '…' | weir`) now assembles multi-line
  statements.** The redirected-stdin loop read one physical line per
  prompt, so a statement spanning lines (a heredoc body, a multi-line
  `type`, an offside `if`/`match` block, a leading-`|>` pipeline) never
  assembled — `let block = <<<` / indented body errored "unbound
  variable". It now accumulates the way a script does, reusing the
  interactive editor's completeness rule and the assembler's own
  statement boundary (no second parser); a single statement per line is
  unchanged, and directives stay one line.

## v0.0.35

### Added

- **`readonly` — a read-only assertion, one tier up from
  `pure`.** A bare `readonly` head + an indented block asserts
  the body reaches no EXTERNAL MUTATION; unlike `pure` (which forbids
  every effect), AMBIENT READS are fine — `fs.read`, `Env`/`Args`,
  the clock, the query HTTP methods (`Http.fetch`/`Http.query` and
  `Http.send` of a GET/HEAD/OPTIONS/QUERY request), and `Self.stdin`.
  A reachable mutation (`File.write`, a command, a mutating HTTP
  method, any `within` resource) is a located check error naming the
  offender AND its class ("this 'readonly' block forbids external
  mutation, but 'File.write' writes the filesystem — reads are
  allowed"). `readonly == only ambient-input`, so a `pure` body
  (only ∅) is trivially read-only. Opt-in only, effect-normal
  outside, its own head — never `within readonly`. `readonly`
  is a new keyword (the grammar-manifest and lexical table gain it; the
  tree-sitter-weir grammar owes the addition, the `pure`/`xml` posture).
- **The ambient/mutation partition, consultable at check AND eval
  time.** Every effect label ({fs.read, fs.write, net, proc, env,
  clock}) now classifies as ambient-input (reads the world) or
  external-mutation (changes it), with the `net` split resolved
  per-METHOD from the request (`Http.send` reads its HttpMethod case).
  The classification drives the `readonly` ceiling, `--can`'s
  new class grouping, and — resolvable where the interpreter runs — the
  reads-run / mutations-capture rule the plan/apply build consumes.
- **`plan`/`apply` — effects reified into an inspectable Plan (dry-run
  as a language primitive).** A `plan` block runs its body but
  CAPTURES its external mutations as `Op` values instead of performing
  them, while ambient reads still RUN — yielding a `Plan`, an
  equatable and showable value over a `seq<Op>` you inspect, diff,
  confirm, then apply. Two plans compare directly (both capture,
  neither performs), so `plan <actual> == plan <expected>` is the
  mock-free test. The members: `Plan.ops` (the raw ops), `Plan.preview`
  (human
  lines; wrote nothing), `Plan.isEmpty`, and `Plan.apply` (perform, in
  capture order). The `Op` arms are the mutation surface: `WriteFile
  of string * seq<string>`, `DeleteFile of string`, `Copy`/`Move of
  string * string` (File/Dir), `MakeDir of string`, `DeleteDir of
  string` (Dir.delete/deleteAll), and `HttpSend of HttpRequest` (a
  mutating HTTP method only — `show` masks the auth Secret). Content
  is snapshotted at plan time, so `preview == apply`. The refusals:
  `proc` inside a plan (a spawned binary is uncapturable — plan covers
  weir-native mutation only), `apply` inside a plan (a capture cannot
  be captured), and a known-after-apply read (reading a path an
  earlier captured mutation targets is a located error). `apply` is
  NOT transactional — it stops at the first failing op with prior ops
  done, no rollback. `plan` is a new keyword, the third standalone
  head beside `pure`/`readonly` (the grammar-manifest and lexical
  table gain it; the tree-sitter-weir grammar owes the addition, the
  `pure`/`readonly` posture). Consumes the ambient/mutation
  partition [D:pure-stage2].

### Changed

- **`weir check --can` groups its report by class.** The capabilities
  now render under two headings — ambient reads (inform, change
  nothing) and mutations (change the world) — so "what does this
  script CHANGE?" is answerable, not just "what can it touch". The
  `--json` output gains a `class` field per capability.

## v0.0.34

### Changed

- **A `let` in any statement body takes command lines, exactly like
  top level.** `within` bodies (every kind, `always` blocks
  included), if/elif/else bodies, `for` bodies, and match arms now
  give a block `let` the full command-RHS law — bare chains,
  reifiers, splices, param-ful lets, bindings-beat-PATH — so
  `let r = npx … | complete` inside `within tmp`/`within cd` no
  longer needs its `$()` wrapper. `pure` blocks admit the grammar
  and the purity checker refuses at the command, its own located
  teaching. The expression positions keep the refusal, with a
  hardened teaching that names `$()` and the actual context
  ("inside a lambda body, a command needs `$(…)`") and fires even
  when the RHS argv would have died at a `--flag` before the pipe:
  paren interiors, single-line `let … in`, and lambda bodies off a
  top-level let's spine.

### Fixed

- **An HTTP response body reads under the same line law as everything
  else.** `resp.body` split on `\n` raw, so a body ending in a
  newline carried a trailing empty line (a one-line file came back as
  two elements), a CRLF body left a stray `\r` on each line, and an
  empty body was one empty line rather than none. It now reads the
  way `File.read` and command output do — a trailing newline
  terminates rather than separates, `\r\n` normalizes, an empty body
  is zero lines — so a URL fetched with `curl url` and with
  `Http.fetch url` agree exactly. Bodies piped into `from json` are
  unaffected.

## v0.0.33

### Added

- **Breaking: an unread `let` binder is a hard check error.** The
  strictness family grows its next member (statement rule,
  exhaustiveness, unreachable arms — weir has no warnings): binding is
  exactly how weir silences raise-on-nonzero, so a
  `let r = cmd | complete` nothing ever reads is a swallowed failure,
  and the checker now refuses it — scripts and module bodies,
  top-level and block-local, destructured names each judged, and a
  name rebound before its earlier binding was read errors at the
  earlier binder. A module's unsigned member unread at home is dead
  private code and errors with the signature repair. The escape is a
  `_`-prefixed name (`let _r = …` — deliberately unused, never
  errors, still readable); a bare `_` let binder now refuses (name
  the discard). Exempt: function params, match-arm binders,
  `for`/`until`/`within` binders, signed module members (the
  signature is the use), and `#sig` contract files (the sig loader is
  their reader). Errors are collected and located at the binder;
  every consumer agrees — `check`, run, import, the LSP, and the
  fidelity oracle. F# accepts unread binders silently by default
  (FS1182 is opt-in), recorded as the `unused-binding` divergence.

### Fixed

- **`weir check` accepts every patch district `weir run` accepts.**
  Check's assume-resolver read the `yaml patch [by=<key>]` marker line
  as a command head (`yaml` is command-shaped), so the district body
  then failed as statements — any `$name` splice or multi-line mapping
  item inside a `yaml patch` district checked red while the same file
  ran green (a kustomize-shaped port hit both). The command grammar now
  refuses the patch marker face glued to the district sentinel, exactly
  as it already did for plain `yaml` and `schema=`, so check == run.
- **Block-let params shadow PATH in their own RHS.** A param heading an
  if-condition inside a nested `let f pairs = if pairs |> … then …`
  resolved as a PATH command (cmd-not-found warnings plus bogus type
  errors) until the condition was parenthesized — the block-let RHS
  never extended the resolver with its params, though top-level lets
  and lambdas did. Bindings-beat-PATH now reaches block-let depth;
  a genuine external head in that condition position
  (`if test -f $p | succeeds then`) still chains.
- **A `)` on its own line closes a multi-line application anywhere.**
  `YMap(` with arguments on deeper lines and the close paren alone at
  the body indent was a parse error at the paren inside if/match arm
  bodies (and everywhere else the closer sat at the sibling level) —
  the assembler sequenced the `)` line as a block statement. While a
  plain paren is open a `)`-headed line now continues the statement,
  the rule multiline lambdas already had; a stray `)` with no paren
  open still refuses.
- **A returning match arm after a multi-statement body assembles.**
  An arm body ending in an if/else (after a `let`) left the if
  compound open, and the next arm then died with "this arm sits left
  of its match (head at column N)" pointing at the if — healthy code.
  A pipe line landing exactly on its own arm group's column is a
  returning arm (the deeper compound offside-closes as it always did);
  genuinely misaligned or left-of-match arms keep their errors.
- **Module signatures can name every builtin type.** `let mk : string ->
  YamlPatch` (and `Proc`) refused as "unknown type" — the def-less
  builtin nominals were unnameable in signatures, even though the
  module-privacy error itself suggested exactly that signature. Both
  now validate in signatures and field types (arity 0), like `Map`;
  the suggested signature round-trips.
- **A qualified type name teaches the bare-name law.** `let f : M.Spec
  -> string` died with a bare parse error at the dot; every type
  position now refuses with "a signature names types bare — an
  imported type resolves by its plain name".

- **`pure` no longer admits `Self.stdin`.** Reading it drains the
  process's live input stream — an effect the classifier missed because
  the value is injected per run rather than called through a builtin.
  A pure region now refuses it with a located teaching ("'Self.stdin'
  reads the process's input stream"), and a function touching it loses
  the `(pure)` hover badge. The per-run constants
  (`Self.args`/`pid`/`scriptPath`/`entryPath`) stay pure-admissible.
- **`pure` accepts union constructors.** A data constructor is pure by
  construction, but a payload constructor's function type fell into the
  unknown-callable bucket and refused as an unknown callable. Applied
  and partially applied constructors now pass in pure regions, and a
  constructor-building function keeps its `(pure)` badge.

### Changed

- **The ctor-pattern refusal teaches its repairs.** A constructor
  pattern on an unresolved param (`let step (m, wd) = match m with
  | Ctor …`) now says params are not typed from patterns and names
  both ways out — inline the lambda at its use site (a typed pipe
  position types the binder there), or match on already-typed data —
  instead of "needs a union value; this one has type 'a1".

## v0.0.32

> First release published since v0.0.29: this ships everything under
> `## v0.0.31` below as well — that tag was cut but never published
> (see its note).

### Added

- **`prompt` — interactive input as a builtin.** `prompt "msg?"` writes
  the message to stderr (piped stdout stays data) and reads one line
  from stdin; EOF refuses rather than inventing phantom input. And
  `Self.stdin` now states its law: it is a live stream read once — a
  second enumeration used to silently yield empty and now raises,
  naming both repairs (bind one enumeration, or `prompt` per
  interaction).

### Changed

- **`weir fmt` puts district markers on the binding line.** Canonical
  layout is `let x = <<<` / `let p = yaml patch by=name` — never the
  marker alone on a continuation line. Both spellings still parse and
  run identically (district content is relative to its first line, so
  the rewrite is lossless); `fmt` now merges the next-line spelling up,
  outdenting a district-pipe close (`|> f`) with the marker, and
  `fmt --check` flags it. A binding line with a trailing comment keeps
  the next-line spelling.

## v0.0.31

> Neither v0.0.30 nor v0.0.31 was ever published. v0.0.30's tag was cut
> but its release run failed on the zed-pin/grammar-main race (the pin
> bump had not landed when the gate ran). v0.0.31's tag was then cut and
> its run died on the same race, re-armed by a publish-wait that trusted
> a bare 200 from releases/latest — so nothing shipped under it either.
> Everything below first reached users in v0.0.32.

### Added

- **Breaking: `Seq.groupBy` returns `(key, items)` pairs.** F#'s own
  shape, replacing the `Group { key; items }` record — `countBy`, `zip`,
  and `pairwise` already speak tuples, and string keys now feed
  `Map.ofPairs` directly. Destructure with `fun (k, g) ->` (or
  `fst`/`snd`); code that pattern-matched the record updates
  mechanically. The `Group` type is retired.

- **`pure` regions are enforced — Stage 1 of the effects plan.** A bare
  `pure` head + indented block asserts the body reaches no effect
  (filesystem, commands, network, environment, console, clock, any
  `within` resource); a reachable effect is a located check error
  naming the offender (`this 'pure' block forbids effects, but
  'File.write' writes the filesystem`). `let pure f x = …` — weir's
  first post-let modifier — asserts a whole binding and keeps the
  `(pure)` hover badge; an impure body is a check error. Judged by the
  Stage 0 classifier: transitive through earlier bindings, conservative
  at unknown callables (a refusal can be over-careful, an acceptance is
  never wrong). Opt-in only — nothing outside a `pure` region is
  gated, and `pure` is its own head, never `within pure`. `pure` is now
  a reserved keyword.

- **The `within` kind is a union.** A long-deferred internal
  restructure: Check/Eval dispatch kind-first on
  `Ast.WithinKindId`, so a new kind (like `pure` above) is a build
  failure at every consumer instead of a silent gap. No surface change.

- **`fail` (and `exit`) now diverge: `string -> 'a`.** A failing arm
  sits opposite a value arm — `match o with | Some v -> v | None ->
  fail "absent"` types as the value, and `if bad then fail "no" else 5`
  likewise; the throwaway `fail "x" ; 0` scaffolding is no longer
  needed (it stays legal). A constraint nothing determines still
  refuses, and the else-less `if bad then fail "usage"` guard is
  untouched. Supersedes the earlier unit-typing ruling on its own
  revisit trigger.

- **Fixed: every deeper line continues a multi-line application.** An
  application with arguments on their own deeper-indented lines used to
  join only the first — the rest sequenced as block statements ("a
  sequenced expression must be unit"). The sibling floor is now the
  statement's start column, in scripts and module bodies alike; a block
  statement after a deeper continuation also sequences instead of dying
  at the dedent floor.

- **Module signatures — a signature *is* the export.** In a module,
  `let name : int -> int` with no `=` declares a member's type and
  exports it; an unsigned member is module-private (full inference,
  invisible to importers — importing one names the exact signature to
  add, inferred type included, with did-you-mean over the signed
  members). Implementations stay annotation-free: the signature's
  types flow into their checking, so params can pattern-match their
  declared unions and generic signatures (`'a -> 'a`) are honoured or
  refused as less-general. Signature without implementation errors at
  the signature; `///` docs live on the signature line (hover and
  definition read the sig); `type` declarations stay auto-exported;
  scripts refuse the form (scripts infer). Breaking: previously every
  module member was public — export now requires the signature; the
  import error teaches the migration.

- **`Path.normalize` — lexical `..`/`.` collapse.** No filesystem touch,
  no cwd, symlinks never followed. `Path.combine` keeps `..` (paths you
  control) and `Path.under` refuses an escape (paths you do not);
  `normalize` is the third spelling, for the legitimate escape both
  siblings decline — a project reference leaving its own directory.
  Relative paths keep their leading `..`s; at an absolute root `..`
  swallows.

- **Fixed: a lazy command escaping a `within` scope now spawns under the
  scope it was written in.** Command values are lazy; one bound inside
  `within cd`/`within env` and forced after the block used to spawn under
  the restored outer ambient. The ambient (cwd + env overlay) is now
  captured at the expression's written site, closure-style, and replayed
  at spawn. (The related, different defect — a `xs | cmd` pipe written as
  a block's tail line associating to the outside of the block — is fixed
  below.)

- **Fixed: a pipe on a `within` body line belongs to the body.** A
  value-headed pipe written wholly on a block's body line
  (`["x"] | sh -c "…"` under `within cd`/`within env`) used to associate
  to the whole block expression — `(within …) | cmd` — so the command
  spawned after the scope restored: the cd invisible, the env overlay
  invisible. It is now part of the body statement and sees the scope; a
  non-final body pipe (previously a bare parse error at the statement
  boundary) works too. The offside law is unchanged: a pipe dedented to
  the block's head column still closes the block and pipes its value.

- **Command pipes carry bytes.** A command→command hop is now a raw byte
  pipe — no decode, no line split, no appended newline — so binary flows
  through pipelines untouched (`gzip -c f | sh -c "cat > out"` is
  byte-identical to bash; previously every non-UTF-8 byte was silently
  replaced with U+FFFD). Only the ends of a chain are text edges: a value
  head feeds stdin as UTF-8 lines, and output becomes `seq<string>` where
  a value is made (`$()`, reifiers, `|>`). A failing stage raises at the
  leftmost fault, as before.

- **Pure functions are visibly pure: the `(pure)` hover badge.** A
  top-level function whose body can reach no effect — no
  file/dir/env/process/network/console touch, no command, no clock,
  transitively through the bindings it calls — hovers as
  `slug (s) : string  (pure)`. The display is deliberately asymmetric:
  weir is effect-normal, so the rare pure function is surfaced and
  effectful code stays exactly as it was (effect detail remains in
  `weir check --can`). The badge is conservative — a call through a
  function-typed parameter or any unknown callable forfeits it — so a
  missing badge is possible, a lying badge is not. Display only:
  nothing is gated on purity.

- **`yaml patch` + `Yaml.parse`/`Yaml.merge` — edit YAML you did not
  fully declare.** `Yaml.parse` reads one document into `Yaml` nodes
  (typeless: structure held whole, undeclared keys included — where
  `from yaml T` would drop them on a rewrite), and `Yaml.merge` applies
  a `yaml patch` district whose *structure* is the address, kustomize's
  strategic-merge model with no path language: maps upsert recursively;
  a sequence appends-if-absent, or upserts by the marker line's
  `by=<key>`; scalars replace; the `$-` tombstone removes (in value
  position, its key; as `- $- <content>`, the matching item).
  Update-or-insert is the semantics of the keyed merge, not a branch you
  write, and merging is orderless and idempotent. A patch types as
  `YamlPatch` and does not render — `to yaml` on it is a check error, so
  a tombstone can never leak into a file; `$-` outside a patch district
  and `patch schema=` both refuse with teachings. Editing a file is the
  visible round-trip:

  ```
  let p = yaml patch by=name
      images:
          - name: app
            newTag: v2
  File.read f |> Yaml.parse |> Yaml.merge p |> to yaml |> File.write f
  ```

- **Graph and tree walks without recursion: `Graph.reach`, `Tree.walk`,
  `Frontier.fold`.** The frontier/visited worklist as builtins, bounded by
  construction — the visited set caps a finite graph, and a 100000-step
  budget turns a non-finite walk into an error, never a hang.
  `Graph.reach keyOf neighbors start` yields every node reachable from a
  start node, breadth-first, each exactly once (cycles and diamonds are
  safe); the neighbor function is the graph. `Tree.walk step root` walks a
  tree for effects, parent before children — the step runs each node's
  effect and returns its children, so a child may depend on its parent's
  effect (create the directory, then the files inside it). Both derive
  from `Frontier.fold keyOf seed step frontier`, whose step folds an
  accumulator and discovers children at once
  (`fun acc n -> (acc', children)`); a `""` key opts a node out of dedup.

  ```
  let deps = Map.ofPairs [("app", ["core"; "util"]); ("util", ["core"])]
  let neighbors p = Map.tryGet p deps |> Option.defaultValue []
  Graph.reach (fun p -> p) neighbors "app"   // ["app"; "core"; "util"]
  ```
- **`Option.bind` and `Option.flatten`.** `bind` applies a function that
  itself returns an `Option`, flattening as it goes — the chain reaches
  through nested optionals (`user.address |> Option.bind _.zip`);
  `flatten` collapses `Option<Option<T>>` to `Option<T>`.
- **`from xml T` reads XML into a declared record.** A read-only, typed
  boundary over an XML subset — point it at a `.csproj`/`.slnx` (or any
  XML document) and walk the result as ordinary weir data. The document's
  root element is the top record; a field name matches a child element by
  local name (a default `xmlns` is stripped, so field names stay plain);
  `[<Attr>]` (optionally `[<Attr "Include">]`) reads an attribute;
  `[<Elem "ProjectReference">]` names the repeated child a `seq< >` field
  reads (defaulting to the element type's name for `seq<record>`, the
  field name for `seq<string>`); a nested record reads a child element
  recursively. Every leaf is text, so fields are `string`,
  `Option<string>` (present-or-absent), a record, or a `seq` of either —
  numbers and booleans are declared `string` and converted (`Str.toInt`),
  which the checker teaches. There is no `to xml`: XML is read-only.

  ```
  type Ref  = { [<Attr>] Include: string }
  type Pg   = { IsPackable: Option<string> }
  type Proj = { [<Elem "PropertyGroup">] groups: seq<Pg>
                [<Elem "ProjectReference">] refs: seq<Ref> }
  File.read "App.csproj" |> from xml Proj
  ```

## v0.0.29

### Fixed

- **A bare lambda field/element no longer swallows the next one.** In a
  multi-line record literal or list, an unparenthesized lambda value —
  `{| act = fun () -> () <newline> name = "x" |}` — used to have its body
  run past the field separator and consume the following field (`error:
  unbound variable 'name'`); the same bit list elements. Record and list
  fields are now separated by a dedicated sentinel the assembler inserts
  for newline joins, which a field value's own `;`-sequencing cannot
  cross — so the fields split cleanly with no parentheses. (An explicit
  one-line `;` between a lambda field and the next still needs the lambda
  parenthesized.)
- **`File.write` preserves an existing file's UTF-8 BOM.** Reading a file
  that starts with a UTF-8 BOM, transforming its lines, and writing it
  back no longer silently strips the BOM (config files like `.csproj`
  that MSBuild saves with one were being churned). `File.write` now
  re-emits the BOM when it is overwriting a file that already had one; a
  new file, or an existing file without a BOM, stays bare — so this
  never *adds* a BOM. Byte-exact round-trips still use
  `File.readBytes`/`File.writeBytes`.

### Changed

- **The over-application error teaches the dangling block form.** When a
  statement is slurped as an indented continuation of the line above
  (`'f' takes at most N argument(s)`), the hint no longer says only
  "start at that line's indent, not deeper" — it now adds that a block
  body written inline (after `->` or `then`) takes no second statement
  below it, so multiple statements start on their own line.
- **A value-bound lambda hovers as its type, not a partial signature.**
  `let fun2 = fun () -> fun () -> 1` now hovers as `fun2 : unit -> unit
  -> int` (the flat value type, like F#'s `val fun2 : …`) instead of
  `fun2 () : unit -> int`. The rule: a binding with a named parameter
  still hovers as its signature (`apply (f) (x) : …`); a binding whose
  parameters are all `()` (unit) — or none — hovers as the flat type,
  since `()` names nothing. This makes the binding hover agree with the
  use-site hover.

## v0.0.28

### Fixed

- **Each match arm keeps its own body column.** A regression in v0.0.26:
  a continuation `|>` in one arm was judged against an *earlier* arm's
  body column, so a later arm with a shallower body (e.g. a `| None ->`
  whose pipeline sits left of a preceding `| Some p -> …`'s inline body)
  was wrongly rejected as "left of the arm body". Each arm's body offside
  is now tracked independently; `ci/grammar-currency.weir` and the like
  parse again.

## v0.0.27

_Tag burned — released from the wrong commit. Version number consumed; no artifacts._

## v0.0.26

### Changed

- **The match-arm pipe floor is the arm body's column.** Corrects
  v0.0.25: a continuation `|>` now extends an arm only when it lines up
  **at or under the arm body**; anywhere left of the body (but right of
  the `|`) is rejected with a message naming the body column. v0.0.25
  used the arm's *pattern* column as the floor, which quietly swallowed
  a `|>` sitting at the pattern — well left of the body — into the arm.
  `|>` at the `|` still closes the whole match. The floor is cleaner
  than F#'s relaxed offside (which tolerates a `|>` hanging a few
  columns left of the body); weir is stricter there — line the pipe up
  under the body.

## v0.0.25

### Changed

- **A `|>` under a match arm continues that arm.** Completing v0.0.24's
  offside work: a `|>` indented at or past an arm's pattern now extends
  that arm's body — the same whether the body is inline or on its own
  line — instead of being rejected. The full rule: `|>` at the arm's
  `|` closes the whole match; under the arm body continues the arm; in
  the gap between the `|` and the pattern it's rejected with a message
  naming both fixes. This replaces v0.0.24's reject-the-deeper-form
  behavior, which was inconsistent (it depended on whether the arm body
  was inline or dangling) and diverged from F#.

## v0.0.24

### Changed

- **A trailing `|>` closes the match, F#'s offside.** A `|>` dedented
  to the arm column now closes the whole `match` and pipes it —
  `match x with | … | _ -> v` then `|> f` on its own line is
  `(match …) |> f`, not `f` buried in the last arm (which used to make
  the arms disagree). This matches F#: a `|>` indented past the arm
  body still extends the arm, and — the one kept divergence — weir
  rejects that deeper form rather than F#'s warn-accept, so write the
  one-line `| _ -> v |> f` to pipe inside an arm.

## v0.0.23

### New features

- **Range slicing: `x[a..b]`.** Inclusive at both ends and clamping —
  an out-of-range or reversed range gives the empty result, never a
  raise — on strings and sequences alike, with open ends `x[..b]` and
  `x[a..]`. `"weir"[1..2]` is `"ei"`; `xs[3..100]` truncates to what
  is there; an infinite sequence stays usable (`nats[2..5]` returns).
  `x[i]` remains the single-element accessor (raises out of range),
  and from-the-end (`x[^1]`) is not supported — `^` is the
  command-force sigil, so `Seq.last`/`Seq.rev` reach the end.
- **Higher-order parameters infer.** `let apply f x = f x` and
  `fun f -> f 1` now typecheck: a parameter applied in a body is
  inferred as a function, exactly as F# does. This was previously
  refused as "a bare parameter cannot be applied."
- **A function type is writable.** An arrow may now be written in any
  type position — a union payload (`Custom of (string -> bool)`), a
  record field (`{ matches: string -> bool }`), a generic argument —
  with F#'s precedence: `->` is right-associative and looser than `*`
  and generics, and a function domain parenthesises
  (`(unit -> int) -> string`). Such a value constructs and its
  functions call; the data boundaries (`==`, `to json`, `to yaml`,
  `show`) refuse it, naming the offending field, so a scalar-only
  record still serialises while a function-bearing one is turned back
  at the wire.

### Changed

- A `yaml`/`<<<` district written with a trailing `do`
  (`for x in xs do`) now names the mistake — a district `for` is
  bodyless, the entries sit indented below it — instead of a bare
  parser expecting-list.

### Docs

- A **Coming from Nushell** section — weir's closest neighbour —
  written and checked against a real `nu`. The **Emacs** editor
  section is validated on Emacs 30.2 (it was marked untested). The
  reference page's duplicate section anchors are fixed and gated, and
  the showcase gains an "On this page" navigation. Smaller
  corrections: `within` cleanup runs on SIGINT/SIGTERM (it was
  described as a leak), the `within` family is five kinds, and the
  homepage leads with the check reaching a tool's flags and a
  manifest's schema, not just a program name.

## v0.0.22

### New features

- **A heredoc or `yaml` block pipes like any `seq<string>`.**
  `<<<` … then `|> Seq.length` on the closing line now composes with
  the whole block, and the block may sit anywhere an expression can —
  piped, bound in a body, followed by a sibling. (A `|>` deeper than
  the content is still content; one at the marker's column closes the
  block and pipes it.) Previously only a top-level `let` binding was
  accepted, and a trailing pipe was silently glued into the last
  content line — that corruption is gone.

### Fixed

- `--help` shows a value placeholder for every value-taking flag —
  `--timeout <duration>`, `--max <size>`, `--rate <float>`,
  `<instant>`, `<secret>` — where only `<int>` and `<string>`
  rendered one before. `bool` flags stay presence-only.
- `usage:` names the invoked script: `usage: deploy.weir [flags]`
  instead of the bare `usage: [flags]`, in every form (records,
  subcommands, shared flags, case-scoped help).
- `#help print` and the reference no longer leak an internal type
  sentinel (`'__print`); the accepted set is stated in the member's
  own text. Unit-parameter members render uniformly
  (`Instant.now () : Instant`, `Path.newTempDir () : string`).

### Docs

- Function parameters do take patterns — `let dist (x, y) = …`,
  `let label { names = n } = n` — and three documents said
  otherwise; corrected, with running examples. The guide and
  reference each state the statement-layout and heredoc shape
  rules once, in one home; the heredoc-built fixtures now read as
  the files they write.
- The homepage leads with a script that ends in a real command, and
  its first section shows the check reaching three surfaces at once
  — a tool's flag (against a generated signature), a manifest's key
  (against a vendored schema), and a program name (against PATH) —
  in one report. The reference's duplicate anchors are fixed and
  gated.

## v0.0.21

### Editors

- Go-to-definition and hover reach into declarations. A type name
  written in a declaration — the payload after `of`, a field's type
  in a record — now jumps to its definition and hovers its shape;
  before, only expression-position names resolved. The attribute
  names (`[<Tag>]`, `[<Other>]`, `[<Wire>]`, `[<Short>]`…) hover a
  one-line explanation, and the `stream` cardinality word both
  hovers its meaning and colours as an adapter keyword instead of a
  plain identifier (in every editor grammar — VS Code, micro, and
  now tree-sitter/Zed).

### Docs

- The website was rebuilt: the fold leads with a worked example, the
  three-bar mark and its hues carry the identity, quoted outputs sit
  in terminal frames captioned with the command that produced them,
  and the prose is set in Iosevka Aile — the sans sibling of the
  code font. (Deploys independently of the release.)

## v0.0.20

_Tag burned — the release gate went red when a grammar push landed
between the tag and its build, and the tag's pinned grammar rev can
no longer match the moved grammar main. No artifacts; version
consumed. The content shipped as v0.0.21._

## v0.0.19

### New features

- **Breaking: `to yaml` writes one document; `to yaml stream`
  writes the `---` bundle.** The write side now mirrors the read
  side exactly, completing the grid json got in v0.0.17:
  `[1; 2; 3] |> to yaml` renders one sequence document (`- 1`,
  `- 2`, `- 3` — json's array, one format over), a record seq
  renders a sequence of mappings, and the multi-document bundle
  takes the same word the reader uses: `docs |> to yaml stream`.
  Every form now reads back through its own name — `to yaml |>
  from yaml seq<T>` and `to yaml stream |> from yaml stream T`
  both roundtrip (the first never did before). A pair-seq still
  renders one mapping document.

### Docs

- The docs pages finished what v0.0.18 started in the help text:
  the guide, the reference, and the translation tables dropped
  their emphasis caps — "the process TREE is killed" reads "the
  process tree is killed" now. Acronyms and file names stand, and
  `!` still means *do it*, in italics.

### Checks clean, behaves differently

- `xs |> to yaml` on a seq previously wrote `---`-separated
  documents; it now writes one sequence document. Scripts that
  meant the bundle should say `to yaml stream` — same bytes as
  before.

## v0.0.18

### Docs

- Help text reads as prose. The hover/`#help` docs (and the
  reference pages built from them) dropped their emphasis caps and
  in-house shorthand: `to json` now says "render one value as one
  JSON document" rather than "ONE value as ONE JSON document", and
  phrases like "more algorithms on receipt" or "the X/tryX rule"
  became plain statements ("sha256 only for now"; "Seq.tryLast
  answers None instead"). Same facts, said normally. The
  `weir check --can` secrets line joined in: a Secret in argv is now
  reported as "visible in ps — weir does not hide argv".

- The homepage shows the language above the fold — beat 1's refusal
  sits beside the install lines — and shared links unfurl with a
  preview card (the mark, the tagline, and that same genuine
  refusal).

### Chores

- The installer distinguishes "gh not authenticated" from "network
  or attestation unavailable" when verifying build provenance —
  `gh attestation` requires auth even for public repos, and the note
  now says `gh auth login` is the repair. The embedded checksum
  remains the primary verification either way.

## v0.0.17

### New features

- **`from yaml stream T` — multi-document yaml.** A `---`-separated
  stream reads as `seq<T>`, each document one `T`. Point it at a
  tagged union and a kubernetes apply file types end to end:

  ```weir
  bundle |> from yaml stream KDoc |> Seq.iter (fun d -> ...)
  ```

  A homogeneous stream of one record type works too; an empty
  stream is zero documents. `from yaml T` still reads exactly one
  document — its multi-document error now names the stream form
  instead of telling you to split by hand. There is no `to … stream`
  word: `to yaml` on a seq already writes the stream, and that
  write reads back through the stream form.

- **Tagged unions cross the wire.** Documents that come in several
  shapes discriminated by a field — kubernetes kinds, webhook event
  types — now read into a union: `[<Tag "kind">]` names the
  discriminator, each case carries the record for one kind, and
  reading dispatches on the tag at both boundaries (JSON and YAML,
  top level or nested — so `from jsonl KDoc` reads mixed NDJSON and
  `from json seq<KDoc>` a mixed array):

  ```weir
  [<Tag "kind">]
  type KDoc =
      | Deployment of DepSpec
      | Service of SvcSpec
      | [<Other>] Unknown of string
  ```

  One `[<Other>]` case opts into open-world reading — kinds you
  didn't declare land there carrying the raw tag, and your `match`
  decides their fate; without it an unknown tag errors naming the
  declared cases. Writers reinsert the tag first, so the roundtrip
  holds; a case's tag value defaults to its name, `[<Wire "v1">]`
  overrides. Untagged unions stay off the wire, the error naming
  the attribute.

- **Attributes on unions.** Union declarations and union cases now
  host attributes, F#'s syntax — `[<Tag "kind">]` above a `type`, `|
  [<Other>] Unknown of string` on a case. The registry stays closed
  and is now position-aware: a registered attribute in the wrong
  position says where it belongs (`'Tag' attaches to a union
  declaration`), an unknown one keeps the did-you-mean.

- **Breaking: `to json` writes one document; `to jsonl` writes
  NDJSON.** The write side now mirrors the read side: `value |> to
  json` renders one minified document — a record becomes an object,
  a seq becomes an array — and the new `to jsonl` does what `to
  json` did before, one document per element, lazily. No more
  wrapping a single value in a list to serialize it
  (`payload |> to json` replaces `[payload] |> to json`), and every
  adapter now pairs with its own name: `to json |> from json T`,
  `to jsonl |> from jsonl T`. `to yaml` is unchanged: a seq still
  writes a `---`-separated stream — YAML's plural is the stream,
  JSON's is the array.

### Bugfixes

- A `for` loop's binder now types from its source, so
  `for d in docs do match d with | Deployment s -> …` works — it
  used to refuse with "constructor patterns need a union value"
  while the equivalent `docs |> Seq.iter (fun d -> match d …)`
  passed. The loop desugars to the piped shape now (the spelling it
  was always sugar for), and the comprehension form is fixed the
  same way.

### Checks clean, behaves differently

- `xs |> to json` on a seq previously wrote one JSON document per
  element (NDJSON); it now writes one array document. Scripts that
  meant NDJSON should say `to jsonl` — same bytes as before. This is
  the section's first real entry since v0.0.2 defined it.

## v0.0.16

### New features

- **Anonymous record literals.** `{| key = key; n = 3 |}` builds a
  record value without declaring a type first — handy for one-off
  shapes on their way out the door:
  `[{| key = k; value = v |}] |> to json |> File.write "x.json"`.
  Fields can mix types (where `Map.ofPairs` needs one value type),
  multi-line literals work, and hover and dot-completion know the
  fields. A literal has the same type as the matching anonymous type
  (so it unifies with `from json {| … |}`'s result), while a declared
  record with the same fields stays a distinct type. No field
  punning, no empty `{||}`, no `{| r with … |}` — each error says
  what to write instead.

- **Bare commands in match arms.** A match arm now runs a command
  directly, no sigil needed:

  ```weir
  match target with
  | "build" -> sh -c "make"
  | "test" -> dotnet test
  | other -> print $"unknown: {other}"
  ```

  In statement position the chosen arm streams its output; on a
  `let` right-hand side it captures as `seq<string>`, like any
  command. The command ends at the next `| pattern ->`, so an argv
  word that happens to spell `x ->` needs quotes. With this,
  commands run bare in every block position — `!()` remains only
  for sequencing a command with an expression on one line
  (`!(setup); print "done"`).

### Docs

- Examples no longer wrap commands in `!()` where a bare command
  works — inside an `if` body or any other block, a command is just
  a statement. The docs now show the sigil only where it is actually
  required.

## v0.0.15

_Tag burned — released from the wrong commit. Version number consumed; no artifacts._

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
  ran `code completion fish`, which opened VS Code on two files.
  `completion fish` and the `version` word now run only when the
  tool's own `--help` advertises that subcommand; flag probes
  (`--version`, `--help`) remain universal.

- The recorded version identity is `--version`'s first line,
  whitespace-collapsed — az's multi-page environment report (with
  machine paths) is not an identity. And az's help dialect parses:
  `--flag --alias -s [Required] : doc` rows record the postfix
  short, each alias as its own flag, and a clean description.

- A path-y tool (`weir add sig ./lib/jp`, an absolute path) mints a
  legal module name and one flat sig file under `.weir/sigs/`
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

- A sub-less line on a scoped sig checks the globals — flag-only
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
  Wire spelling for matching and did-you-mean. A keyword long with a
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
  tool answering neither records no identity, the sig says so in a
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
  on none *and* on more, with distinct messages (they are different
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
  precedence, truncated — the sign follows the dividend (`-7 % 3`
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
- `weir restore` now repairs a present-but-modified vendored
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
