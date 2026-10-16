# PLAN — fixing the third bug hunt (H3-*)

Status: APPROVED (user, 2026-10-16) — R1–R10 all as recommended.

Source: PLAN-hunt3.md round 1 — briefs B (formats/plan/gen), C (concurrency),
D (perf), E (trust boundaries), F (regressions since v0.0.74), A (Linux half
of cross-platform) against the AOT binary of main @ 80cbdf6. The brief agents
reproduced every finding twice; the coordinator re-ran every medium+ from its
repro: **37 confirmed (8 high, 29 medium), none merged, none dropped**.
Concurrency/signal items hit 3/3 runs each (C1 TERM+INT, C2, C4a, C4b, C5,
C6, E8 tty-close and direct HUP); timing items confirmed once at the largest
size on a quiet machine (D1 distinct 30k 12.9 s vs distinctBy 0.05 s; D2 fmt
of a 10k-line heredoc 28.3 s vs 0.40 s on v0.0.74; D3 check of 40k lets
29.8 s). Lows went to dev/BACKLOG.md ("From the third bug hunt").

Five confirmed items live in `serve` and move with the Kestrel migration
(→ feat/kestrel, below); they keep a probe in the harness.

## Gates

Per stage, stacked branches, each green on its own: unit, fidelity,
skill-doc, fuzz ×3, e2e (under `flock /output/wt/e2e.lock`), e2e with the
macOS `timeout` polyfill forced, and the acceptance harness
`tools/hunt3-repro.weir --bin <aot>` — written first, every probe
REPRODUCES at 80cbdf6 (37/37, 27 controls OK, three runs). A stage is done
when its own items' probes print OK and every control stays OK; the serve
probes (E5 E6 E7 A3 C3) stay REPRODUCES until feat/kestrel lands. The
harness joins ci.yml / ci/local.sh beside hunt2-repro once all 37 are OK.
At most three heavy agents at once (pids cap 2048); D items are measured
alone.

## Rulings (all ruled as recommended, user 2026-10-16)

- **R1 — REPL `^C` during a child-less wait (H3-C6).** Today `^C` at the
  REPL interrupts only a foreground child; `Duration.sleep 20s`, a `poll`
  interval, a `retry` delay, `within lock` acquire and `Proc.wait` cannot be
  stopped, and the session is stuck. RECOMMEND: `^C` cancels any WAIT the
  runtime owns — each of those becomes cancellable on a session token the
  REPL's SIGINT handler trips — raising the script path's exit-130 message
  and returning to the prompt; pure CPU work stays uninterruptible (the
  documented posture, narrowed). Rewrite [D:repl-isig] and SKILL "At the
  REPL, ^C…".
- **R2 — `pfirst` losers' effects (H3-C2).** Losers keep running after the
  winner returns; their File/Http effects land afterwards (the probe's file
  ends up holding the loser's write). RECOMMEND: losers are cancelled
  cooperatively — after a winner, a loser's next effect boundary (every
  builtin effect, spawn, sleep, Http send) raises a silent cancel, in-flight
  Http requests are cancelled, children tree-killed as now — and `pfirst`
  joins the losers before returning, so "after pfirst returns, no loser
  effect runs" becomes a stated guarantee. Alternative: keep today's
  behaviour as a SKILL non-claim.
- **R3 — `weir add schema` and validation keywords (H3-B7).** 4/4 real
  schemas (GitHub workflow, k8s) are refused whole over `minLength`,
  `maxLength`, `pattern`, `minimum`, `maxItems`, `uniqueItems`, `readOnly`,
  `deprecated`, `$comment`. RECOMMEND: admit-and-ignore every keyword that
  only VALIDATES values (string/number/array/object bounds, `pattern`,
  `multipleOf`, `readOnly`/`writeOnly`, `deprecated`, `$comment`,
  `examples`, `title`/`description`), one `// note:` per keyword kind in the
  generated module ("not enforced: minLength on name"); keep refusing
  keywords that change SHAPE or that weir cannot represent faithfully.
  Alternative: refuse, but list every offending keyword at once.
- **R4 — int-or-string fields in `gen types` (H3-B5; rides H3-B6).** An
  `anyOf [integer, string]` / `x-kubernetes-int-or-string` field drafts as
  `string`: `from json` refuses `8080`, and a yaml round trip writes
  `"8080"` (breaks k8s). RECOMMEND: draft as `Yaml` (the opaque node crosses
  json and yaml both ways, keeps `8080` an int and `"25%"` a string) with a
  note naming `YInt`/`YStr`; an integer `enum` (H3-B6) drafts `int` with the
  values in the note. Alternatives: a builtin `IntOrString` union (new
  prelude surface), or `string` plus a codec that reads numbers (writes stay
  lossy).
- **R5 — SIGHUP semantics (H3-E8).** HUP (terminal closed, `kill -HUP`)
  kills weir outright: `always` does not run, `within tmp` dirs stay,
  `within proc` children are orphaned (3/3, both a pty close and a direct
  HUP). RECOMMEND: SIGHUP joins SIGINT/SIGTERM — the same unwind, exit 129,
  a second signal hard-exits; an inherited SIG_IGN (nohup) is honoured as it
  is for INT. Extend [D:within-always]'s signal list and the SKILL non-claim.
- **R6 — `=` in environment keys (H3-E3).** `Env.ofPairs [("FOO=BAR",
  "x")]` silently becomes `FOO=BAR=x` in the child — another variable, and
  `PATH=…` overrides the child's PATH. RECOMMEND: refuse at construction
  (`Env.pair`, `Env.ofPairs`, `within env`, `$e(…)`) a key that is empty or
  contains `=` or NUL, naming the key; weir's own reads (`Env.get`) already
  disagree with the child today, so silence is never right.
- **R7 — a TOML local date-time into `[<Iso8601>] Instant` (H3-B9).**
  `start = 2024-03-01T10:00:00` reads as 10:00 UTC. RECOMMEND: refuse in
  `from toml` — TOML types a local date-time as offset-less and the doc
  already says "an offset date-time binds to Instant" — naming the fix (an
  offset, or a `string` field). Note for the ruling: `Instant.parse` and the
  json/yaml codec also read an offset-less time as UTC; leave those (their
  inputs carry no type distinction) but state it in SKILL.
- **R8 — `within lock` on the data file itself (H3-A2).** Inside `within
  lock f`, weir's own `File.read f`/`File.write f` fail "used by another
  process", even in the holder: .NET's FileShare emulation takes its own
  flock on every open and collides with the lock's. RECOMMEND: disable
  .NET's advisory file locking for weir (`System.IO.DisableFileLocking` in
  the runtimeconfig) so `within lock` is the only flock weir takes — the
  advisory semantics SKILL states. Windows sharing modes are real and stay;
  SKILL says to lock a sidecar file there. Alternative: document "lock a
  sidecar file" everywhere.
- **R9 — `within tmp` inside a `plan` (H3-B4).** Ops captured against a
  `within tmp` dir target a directory deleted before `apply`, which then
  fails midway after earlier ops ran. RECOMMEND: refuse at run time a
  captured op whose path lies under a `within tmp` scope opened inside the
  plan, naming the repair (build the content in memory, or do the tmp work
  outside the plan). Alternative: refuse `within tmp` inside `plan`
  outright.
- **R10 — duplicate keys reaching a `Yaml` field from JSON (H3-F2).** The
  node keeps both keys and `to yaml` writes YAML weir itself refuses.
  RECOMMEND: last-wins at the JSON reader, as records and `Map` values
  already do (the documented "duplicate keys last-win"). Alternative:
  refuse the document.

## Stage 1 — safety, secrets, crashes (`fix/hunt3-safety`)

- **H3-E1 (high) printerr, Log.\*, Self.prompt and the `-e` echo write
  terminal escapes raw to a tty** — OSC 52 (clipboard), OSC 0 (title),
  `ESC[2J`; `print`, the REPL echo and error text already neutralize. One
  sanitizer for every console sink. `printerrImpl` Builtins.fs:7490, the Log
  members (Builtins.fs:5712), Self.prompt, Program.fs `-e` echo.
- **H3-E2 (high) Args.load echoes a Secret field's value** for
  `--token=VALUE` ("unknown flag '--token=hunter2' — write '--token
  hunter2'"). Mask the value when the flag names a Secret field (and never
  echo a value at all for an unknown `--x=…`). Eval.fs:3004.
- **H3-F1 (high, regression) refusing a non-ASCII header value prints the
  offending character of a Secret** (`secretHeaders`, `auth = Bearer`).
  `describeChar` Http.fs:431–455: name the header and the byte offset only
  when the value is secret.
- **H3-C1 (high) on SIGINT/SIGTERM, commands inside `always` are refused**
  ("no new command starts") — cleanups silently skipped. `spawningStopped`
  Session.fs:291–314 must reopen for the always block (teardown spawns are
  the point of always), keeping the H8 rule that the body's spawns stop and
  every live tree is killed once; a second signal still hard-exits.
- **H3-C4 (high) after SIGINT/SIGTERM/exit 141, pmap/piter workers keep
  taking new arms** (3,100+ arms after TERM; hundreds to thousands of leaked
  `weir-proc-*` dirs per run). `runParallelWith` Builtins.fs:1578: workers check the
  session's stop flag before taking an arm; in-flight arms finish or are
  killed; scoped spill dirs are removed on that path.
- **H3-B1 (high) a plan dry-run SENDS Http with `Other "PURGE"`** (any
  `Other` except post/POST) — `readonly` refuses the same request, so the
  classes disagree. `Effects.httpMethodClass` Effects.fs:162: classify by
  verb case-insensitively, `Other` = mutation unless it is a known safe verb
  (GET/HEAD/OPTIONS/QUERY/TRACE); plan capture follows (Builtins.fs:4453).

## Stage 2 — wrong behaviour (`fix/hunt3-behavior`)

Signals and concurrency
- **H3-E8 SIGHUP skips all teardown.** Per R5. Session.fs:648/740 register
  INT/TERM only; add HUP.
- **H3-C2 pfirst losers' effects land after the race.** Per R2.
  Builtins.fs:1637, Session.fs:209.
- **H3-C5 `exit n` inside a pfirst arm is swallowed** (exit 0, the race
  continues); pmap/piter propagate it. Builtins.fs:1637.
- **H3-C6 REPL `^C` cannot interrupt a child-less wait.** Per R1.
  Repl.fs:1853.

Trust boundaries
- **H3-E3 `=` in env keys.** Per R6. Builtins.fs:3248 (Env.pair/ofPairs),
  the overlay apply in Proc.fs.
- **H3-E4 `check --can` omits "Secret reaches argv"** for a command ending
  in a reifier (`| complete/line/succeeds/exitCode/orFail/text/exec`) — all
  seven unreported; the plain statement and `$(…)` forms are. Can.fs:649 and the reifier arms of the walk.
- **H3-E9 `within tmp`, `Path.newTempDir` and `within proc` spill dirs are
  0755, the logs 0644** (world-readable child output). Create dirs 0700,
  spill files 0600 (mkdtemp's convention, umask-independent).
  Builtins.fs:3004, Eval.fs:4522, Eval.fs:4171.
- **H3-E10 File.writeAtomic's temp file is 0644 for a 0600 target** — the
  secret sits world-readable until the rename. Create the temp with the
  target's mode (0600 when new). Builtins.fs:2645–2649.
- **H3-A1 the HTTP client sends `http://[::1]` through HTTP_PROXY although
  NO_PROXY lists `::1`** (and bypasses for `[::1]`, which curl does not).
  Match NO_PROXY entries against the bracket-less host. Http.fs:191.
- **H3-A2 `within lock` on the data file blocks weir's own reads.** Per R8.
  Builtins.fs:2574/3599, Eval.fs:4069.

Plan
- **H3-B2 the known-after-apply guard misses stale reads**: `File.exists`,
  `File.sha256`, `File.readBytes`, `Path.glob` and `Dir.list` never check,
  and the exact-path set misses reads under a captured `Dir.deleteAll` or
  move. One guard on every fs read, matching by path prefix for directory
  ops. Builtins.fs:3377, :3719.
- **H3-B3 `File.writeAtomic` inside a plan applies as an in-place write**
  (a hardlink sees the new content). Capture an atomic flag on WriteFile
  (or a distinct op) and apply via the writeAtomic path. `applyOp`
  Builtins.fs:4829.
- **H3-B4 ops captured inside `within tmp` target a deleted dir.** Per R9.
  Eval.fs:4522.

Formats and generators
- **H3-B5 (high) gen types drafts int-or-string as string.** Per R4.
  SchemaTypes.fs:103.
- **H3-B6 an integer `enum` drafts as string**, so `from json` refuses
  `{"level": 2}`. Per R4. SchemaTypes.fs:81.
- **H3-B7 add schema refuses whole schemas over validation keywords.** Per
  R3. Contracts.fs:871.
- **H3-B8 valid JSON nested past 64 levels reads as "not valid JSON"**
  (`from json`; `add schema` says "the response is not JSON"). Read with
  `MaxDepth` at weir's own 100-level value bound (Eval.fs:344) and name the
  depth past it. Eval.fs:1440, Contracts.fs:1189/1241.
- **H3-B9 a TOML local date-time reads as UTC.** Per R7. Eval.fs:1216,
  Toml.fs.
- **H3-F2 (regression) a Yaml field read from JSON keeps duplicate keys.**
  Per R10. Eval.fs:1231.

Checker, parser
- **H3-B10 an indented `within … always` followed by a statement fails to
  parse** with "dedents below the open block but aligns with no enclosing
  statement" — the top-level form works. Script.fs:2377–2391 (assembly of
  `always` inside a block).
- **H3-F3 (regression) the re-enumeration warning fires through a function
  that already freezes** (`let f () = xs |> Seq.freeze`; reads of its
  result warn — false advice). Check.fs:7319 carriers: a call whose body
  ends materialized carries nothing.
- **H3-F4 (regression) inline `Env.load {| … |}` accepts duplicate field
  names** (the checker takes the first type; the runtime validates both).
  Refuse at check, as declared records do. Check.fs:4556.
- **H3-F5 `Instant + <unknown>` teaches "two points don't add"** — the
  operand is unknown, not an Instant; f5b is a [D:plus-defer] regression.
  Say the other side cannot be inferred and to anchor it as a Duration.
  Check.fs:1356.

## Stage 3 — performance cliffs (`fix/hunt3-perf`, measured alone)

- **H3-D1 `Seq.distinct` is quadratic in the distinct count** (30k → 12.9 s;
  `distinctBy id` 0.05 s). `distinctImpl` Builtins.fs:1834 uses
  `ResizeArray.Contains`; use the hash set distinctBy uses (structural
  equality on Value).
- **H3-D2 (regression) `weir fmt` is quadratic in a statement's physical
  line count** (10k-line heredoc 28 s vs 0.40 s on v0.0.74; yaml blocks and
  multi-line lists too; LSP formatting shares it). Fmt.fs ~565–576 rescans
  the frozen-mask prefix per line; index once.
- **H3-D3 check/run/LSP diagnostics quadratic in top-level let count**
  (40k lines 29.8 s; LSP ~150 s per edit at 200k). `envFreeVars`
  Check.fs:680 recomputed per `generalizeLet` (Check.fs:2613); keep the
  environment's free-variable set incrementally. Profile first — the brief
  did not.

The probe judges growth at 10× input (≤ 25× the small run passes), so a
linear fix passes on a slow runner and a quadratic one fails on a fast one.

## → feat/kestrel (out of scope here; probes stay in the harness)

- **H3-C3 (high)** a serve handler still running >2 s after scope close
  crashes weir (exit 134, ObjectDisposedException on a SemaphoreSlim,
  `Eval.worker@4386`); `always` never runs; scope close joins handlers
  serially (2 s × N).
- **H3-E5** one slow request body holds a serve slot for the whole
  `bodyTimeout`; `maxConcurrent` slow clients deny service.
- **H3-E6** after a listener 404 (foreign Host, absolute-form URI) or 505,
  the same connection also gets an unsolicited `200 OK`.
- **H3-E7** a raising handler (or status 0/-1/99/1000) answers 500 with no
  trace for the script — not in `Server.streamErrors`, nothing on stderr.
- **H3-A3** serve registers `[::1]` but every `[::1]` request gets the
  listener's `400 Invalid url`.
- Serve lows (BACKLOG, "→ PLAN-kestrel"): status 600 and 101 passed to the
  wire; close still starts a queued request whose client left; a Stream
  handler keeps its slot after a client abort, the abort unrecorded.
