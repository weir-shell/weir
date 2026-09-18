# Changelog

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
