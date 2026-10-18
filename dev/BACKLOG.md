# Backlog

(Moved from SEMANTICS.md at its retirement — planning residue never
belonged in a semantics document.)

0. ~~**Block effect-sequencing**~~ — done, and was already done when this
   note moved here from SEMANTICS.md: the sibling-sentinel ESeq spine
   ([D:seq-commit]/[D:sibling-sentinel]) sequences any unit expression
   mid-block, a non-unit line refuses with `[seq-unit]` ("bind it or
   print it" — the statement rule's discipline inside blocks, exactly as
   priced), and the fuzz invariant "block siblings and single-line ';'
   agree" pins it. Probe-confirmed 2026-08-28.
1. ~~**Measure algebra**~~ — superseded: **measures were removed
   entirely** (2026-07-18; see the tombstone section and the NOTES arc).
   The 2026-07-17 drop decision and the `no_unit_algebra` tripwire
   retired with them *and* the `*`/`/`-defaulting rule above.
(Done: backlog #1 and the exit-code policy — old #3 — landed as
`Seq.force` (né `collect`) and `complete`; see "Processes and the
session".)

(Done: comparison/boolean completeness — landed with `<>` inheriting `==`'s
equatability and short-circuit `&&`/`||`, as pre-committed.)

## From the fuzzer's hunt-2 invariants (2026-10-15, W6, [D:fuzz-hunt2-invariants])

Invariant 8 was red on deep runs (green on the pinned smoke and the
e2e `--seed 4242 --count 10` cell). Found over 6,500 generated
programs x 5 neighbors; two classes, nothing else (no crash, no
"internal error", no fmt idempotence break). Both closed 2026-10-16.

- ~~**check positions past the end of their line** (23 of 6,500)~~ —
  done: assembly recovery's re-indent of a misaligned line no longer
  moves any position; segments carry the source indent, so assembly,
  parse, check and LSP columns land on the line as written
  [D:recovery-physical-indent]. Minimal (was `4:12` on a 9-wide line):

      let xs = [ 1
                 2
                3
      print "a"

- ~~**fmt repairs a `doc-align` error** (14 of 6,500)~~ — ruled
  (user, 2026-10-16): the law narrows to "fmt never makes a checking
  file fail and never masks a non-layout error"; repairing a pure
  layout error is fmt's job [D:fmt-repairs-layout].

## From the second bug hunt (2026-10-14, PLAN-hunt2-fixes.md)

Ruled 2026-10-15 (PLAN-daily-driver W4): **every item below is daily-driver — fix all.**

- ~~**`from json` reading a `Yaml` field**~~ — done: `Yaml` crosses json
  both ways and `gen types` drafts a cyclic field as `Yaml`
  [D:json-yaml-field].
- ~~**`serve` answers only the first of pipelined requests**~~ — done:
  `serve` runs on Kestrel, which answers them in order (F10 and the F8
  reflection went with it) [D:serve-kestrel].
- ~~**Inference order:**~~ — done: `+` over two unknowns defers to any
  later anchor in the statement [D:plus-defer].
- ~~**Re-enumeration warning** misses indirect pulls (through a function, a
  loop, a derived binding).~~ — done: carriers replay their pulls, a
  loop read counts twice [D:reenum-indirect].
- ~~**User generic types at the wire**~~ — done: `from json B<int>` /
  `seq<B<int>>` and instances inside records cross json/jsonl/yaml;
  SKILL documents user generics [D:wire-generics].
- ~~**Runtime error positions** are statement-granular inside a one-line
  `if … then fail … else …`.~~ — done: a raising call reports its
  callee's column [D:runtime-position-call].
- ~~**LSP re-analyzes a large file per hover** (20k lines ≈ 3s).~~ — done:
  one analysis per (file, text), dropped on any document event
  [D:lsp-analysis-cache].
- ~~**A Secret spliced into argv shows in clear in "command failed" errors**
  (SECURITY.md non-claim; CI logs make it worth revisiting).~~ — done:
  argv-rendering messages mask it as `***` [D:secret-argv-mask].
- ~~**`from xml` mixed content** (`<N>a<b/>c</N>`) is silently concatenated.~~
  — done: a text field over mixed content refuses [D:from-xml-mixed].
- ~~**fmt** formats a file with a yaml-district parse error, and spaces inside
  multi-line interpolation holes.~~ done: fmt refuses any file check cannot
  parse; respace skips district content and lines inside a hole [D:fmt-parse-gate].
- ~~**History file and fmt rewrite invalid UTF-8** as U+FFFD ([D:source-utf8]
  covers source files only).~~ — done: fmt refuses located and leaves the
  file untouched; history skips an invalid line from recall, writes it back
  byte-for-byte on every rewrite and says so once [D:history-utf8].

## From the third bug hunt (2026-10-16, PLAN-hunt3.md)

Round 1 lows, one line each; the medium+ items are PLAN-hunt3-fixes.md.

Formats, plan, generators (brief B)
- **TOML non-string values bind to string fields** — `0xff` → `"255"`, `inf` → `"inf"`, `kind = 5` → an `[<Other>]` case `"5"`.
- **`f = 1e400` in TOML refuses "expected float"** — name the overflow.
- **toml-test invalid files accepted via File.read/stdin (11)** — bare CR, bad UTF-8 decoded to U+FFFD, a UTF-16 BOM; "681/681" holds only for the unit harness's byte path.
- **`[<Iso8601>]` refuses lowercase `t`/`z` and seconds-less `07:32Z`**; an out-of-range year says "not an ISO 8601 instant".
- **`-0.0` round-trips as `0.0`.**
- **A schema property named `""` makes gen types report a "generator bug"** (`[<Wire "">]` refused).
- **Unconstrained `{}`, `additionalProperties: true` and an items-less array draft `string`** where `Yaml` fits.
- **A required+nullable field drafts `Option`**, so `to json` omits a required key.
- **`gen types --as Thing` colliding with a def name silently makes `Thing2`** (said on stdout only).
- **A top-level array schema generates only the element type**, with no note.
- **A captured `Http.send` returns a status-0 placeholder inside a plan** — undocumented.
- **`Path.newTempDir` inside a plan really creates a dir.**
- **`weir add schema file://…` or a plain path says "cannot reach  —"** (empty host).
- **An empty mapping writes `[]`** (documented), so a schema's empty object round-trips as an array.

Regressions sweep (brief F)
- **`from json seq<Map<string,int>>` says "unknown type 'Map'. Did you mean 'Op'?"**
- **`Env.need ""` double-spaces its message**; `--can` prints `reads  (Env.getOr)`.
- **Deferred `+` errors leak inference variables** (`'a3`).
- **A JSON int beyond int64 becomes a float in a `Yaml` field.**
- **`Env.load` of `Box<int>` shows a raw parse error** (old).
- **The argv renderer shows `sh -c exit 3 x` without quoting words** (old).

Trust boundaries (brief E)
- **An HTTP response > 2 GB says "cannot reach … Cannot write more bytes"**; a 600 MB body costs 2.5 GB RSS; SECURITY.md states no response-size bound.
- **Http follows `http(s)_proxy`/`no_proxy`** — undocumented.
- **`insecure = true` persists through a cross-host redirect.**
- **A Basic user containing `:` is sent silently.**
- **A header-name error echoes raw CR/LF.**
- **serve passes status 600 and 101 to the wire** → PLAN-kestrel.
- **`Path.under` with a NUL gives a raw .NET message; `Path.glob` with a NUL is silently empty.**
- **`Args.load` typo `--tokn SECRET` echoes the value** (inherent: the field is unknown).
- **SECURITY.md cites `$@tokens` but `$@` refuses `seq<Secret>`.**
- **A symlinked `.weir` makes add/restore follow it** (writes stay inside the target).
- **restore on a symlinked artifact blames "tampered or hand-edited".**
- **At a tty weir emits `ESC[?1h ESC=` and never resets it.**

Cross-platform, Linux half (brief A)
- **`weir fmt` rewrites `within lock lf timeout=t` to `timeout = t`** for identifier values (and after an interpolated-string path, `within lock $"{d}/x" timeout=5s`).

Concurrency (brief C)
- **A signal during a clean-exit `always` aborts that block and runs the next outer one.**
- **serve close still starts a queued request whose client left** → PLAN-kestrel.
- **A Stream handler keeps its slot after a client abort; the abort is not recorded** → PLAN-kestrel.
- **Parser: a one-line `if … then 0 else 1)` followed by a `|> Seq.sum` line says "this arm sits left of its match"** with no match in sight (old).

Performance (brief D)
- **REPL paste has no bracketed paste and redraws the full line per key** — terminal output is L² (a 30 KB line writes 451 MB, 25 s).
- **REPL history is O(size) per entry**; the in-session image grows past `historySize`.
- **Wide REPL table echo grows with exponent 1.84** (2,000 fields 2 s).
- **A 10k-case union match checks in 4.7 s.**
- **Adapter RSS is 19–46× the input; `from table` doesn't stream from stdin.**
- **`File.write` buffers the whole seq** (10M lines 649 MB).
- **`Seq.fold` string concatenation is quadratic** (inherent; name `Str.join` in the docs).
- **LSP `semanticTokens/full` re-analyzes after formatting** (the cache is skipped).
- **`fmt` on realistic files is 2.7× slower than v0.0.74** — the parse gate; same root as H3-D2/D3.
- **e2e flake, F3-serve cell (once, 2026-10-16):** `echo "$out" | grep -qF "FORGED=False"` failed while the printed `$out` held `FORGED=False`; the re-run on the same binary was green (882). Unexplained — watch for a recurrence before chasing (a pipefail/SIGPIPE race on `echo | grep -q` is the first suspect).

## From the binary-size study (2026-10-16, PLAN-binary-size.md)

- **`builtinDocs` as data** — the ~400-entry docs table compiles into one
  287 KB method plus 118 KB / 57 KB static initializers (Builtins, Parser);
  moving it to an embedded data resource saves ~0.25–0.35 MB. A mechanical
  refactor of ~1,580 lines; ruled "only if size starts to matter".
- **e2e port flake (2026-10-16):** `Http: the echo server never came up on 21652` (`OSError: [Errno 98] Address already in use`) while another agent's tests ran alongside — the Http echo-server cell picks a fixed-range port without the harness-ports retry; give it the [D:harness-ports] claim-and-retry.
- **Windows tree-kill misses MSYS grandchildren (2026-10-17):** the Windows e2e `pfirst waited for the loser (took 2096ms; marker=absent)` — `Process.Kill(true)` took the loser's `sh` but its `sleep` lived on holding the pipe (inferred from timing; not reproduced on Windows). The race no longer waits on such a pipe ([D:pfirst-join] pipe-read cut), but the orphan still leaks until it exits; a Job Object per spawned tree (`TerminateJobObject`) would make the kill complete. Applies to every `Kill true` site (RaceGroup, teardown sweep, reap).

## From hunt-3 round 2 (2026-10-17, lows — [D:repro-gates] harness tools/hunt3r2-repro.weir)

- **formats:** yaml's 500-level depth vs json's 100 (`from yaml |> to json` writes JSON weir refuses); `[<Iso8601>]` read truncates to ms vs SKILL's 7 digits; an empty nested seq/Option mapping writes `key:` and reads back `None`; `x-kubernetes-preserve-unknown-fields`/`{}`/`additionalProperties: true` draft string pairs; general oneOf/patternProperties/cross-file `$ref` refuse the whole schema; TOML inf/nan/1e400 into a Yaml field become strings; -0.0 → 0.0; property `""` → "generator bug"; 11 invalid toml-test files accepted; a pair-seq `None` value writes `null` in json but omits in yaml; a mixed-enum note lists values unquoted (`"1"` vs `1`); `within tmp` in-plan capture compares paths as written (R9 without B4's link/case resolution); `from yaml` keeps refusing duplicate keys (R6 applied to JSON reads).
- **serve:** chunk-size ≥2^31 answers a bare 500; the request body is BOM-sniffed/charset-honoured and invalid UTF-8 becomes U+FFFD; lowercase `get` → Get and a 10 KB method token → Other; an empty response header value is dropped (vs "refused, never dropped"); `TE: gzip, chunked` accepted undecoded, `Content-Length: +3`, chunked on HTTP/1.0; the Host port is unvalidated; a declared chunk past maxBody waits out bodyTimeout (408) not 413; streamErrors carry raw CR/LF from attacker header names (log forging); pipe reads inside handlers take the slower cuttable path.
- **secrets / tty:** REPL directive and alias messages echo typed input raw on stdout; the REPL diagnostic sanitizing has no pty pin; TtyErrWriter sanitizes per write (SGR written a char at a time would show); `Http.expect` redacts userinfo only, query-string tokens print; `weir add sig ..t` writes `sigs/t.weir`; Args.load reports "not an int" and "missing required" for one flag; the contract fetch's HTTP-status failure after a redirect names the original URL.
- **cancellation:** a pmap arm stuck on a pipe an orphan holds open holds the teardown join until a second signal; a teardown that cuts a command's lazy stdin producer can surface an odd message (exit code correct); a cancelled File.write loser leaves an empty new file; a pfirst loser's failing always says "the cleanup ALSO failed … (the original error wins)" with no original; fast-typed multi-line REPL input split mid-`always`; a detached weir with SIGINT inherited ignored and no scope ignores `kill -INT`; a scope-less script dies by the signal (wait status) not 130/143; re-acquiring one's own lock says "(held elsewhere)"; a signal during a clean-exit `always` aborts it (round-1 low).
- **perf:** the pfirst pipe-read cut costs ~5x per line inside a race (1e7 lines 2.0 s vs 0.4 s), Self.stdin ~50 ns/line more; the piped REPL is ~n^1.4 in entries; history costs O(size) per entry; serve peaks ~10x the request body in RSS, Str.join ~9x; a 10k-case union match takes 4.8 s; CLI fmt at 20k lines 1.8x slower than v0.0.74; F# lists still walk for length/index (D3 covered arrays/buffers); `Str.tryIndexOf` on a long surrogate-bearing string still walks.
- **older, surfaced again:** a lazy `Seq.head` error is located at the producer's binding; `(.)` on an emoji yields a lone surrogate; LSP completion at a negative position answers "Index was outside the bounds"; Args `--help` glues a long flag to "optional"; `Instant.now () - d` infers `d` as Instant; `Env.ofPairs [("=X", …)]` names the variable `''`; `from jsonl` refusal echoes the whole offending line (600 KB).

## From hunt-3 round 3 (2026-10-17, lows — [D:repro-gates] harness tools/hunt3r3-repro.weir)

- **tty / secrets:** REPL `#help` prints the init file's `///` raw; the source-echo caret misaligns once escapes expand; union case docs missing from `--help`; gen types writes raw ESC from schema property names into generated comments/Wire strings; `show` of a request and `Plan.preview` print URL userinfo and query tokens (strings, not Secrets); `show` now prints `\r` but string literals have no `\r` escape (not round-trippable); stderr diagnostics still pass a bare CR (tty-cr-pass tradeoff); invalid JSON under a Secret-holding type reports no position.
- **network:** `NO_PROXY=foo,*` bypasses everything (curl: a lone `*` only); a blackholed (timing-out) proxy still names the target; `--can` says "url not statically known" for `{ Http.get "lit" with … }`; an HTTP/1.0 request with no Host reaches the handler; `add` accepts plain `http://` sources; the UTF-16 charset body decode could slip a body past a WAF (closest round-2 serve low).
- **stdin / REPL:** Self.prompt keeps a trailing bare CR at EOF; without `iutf8` backspace erases one byte of a multibyte char; Self.stdin at a tty needs a third ^D after a partial line; Self.prompt ~2.5x v0.0.74 per byte; a child after a partial Self.stdin loses 64 KiB (stated residual); the REPL spinner of a racing arm or serve handler draws over a pending Self.prompt; REPL ^C inside a `within serve` body command prints the raw "command failed with exit code 130".
- **cancellation:** a pfirst loser's raising cleanup still says "cleanup ALSO failed (original wins)" with no original; `exit n` in a piter arm lets siblings finish first (ruled for change in round 4 stage 0, #10); a group SIGINT leaves `sh -c "… &"` background grandchildren (bash parity); a hung cleanup or slow serve-handler cleanup now holds exit/scope on the clean path (pfirst parity, intended); a child that exits 130 by itself while weir is the foreground group delays weir up to 250 ms; FIFO polling and the /proc tree walk are Linux-only (macOS: an abandoned FIFO reader can take one later write; tree kills still scan); `piterWith 1000` under the pid cap fails "An exception was thrown by a TaskScheduler."
- **formats / plan:** empty pair-seq writes JSON `[]` (labels: {} → []); `type: object` with no properties drafts string pairs (Argo/datreeio metadata); `const` object/array drafts string with a false note; `format: int-or-string` without type → string, with type integer → int silently; enum notes render 1 and "1" alike, empty enum "an enum of  —"; a `$ref` against the root `$id` URL refused as cross-document; a definitions-only root "not a schema"; Json.inferShape on invalid JSON deeper than 64 prints .NET's "depth of 64"; File.stat/File.mode of a link refused after a write to its target, listing any ancestor of a captured write refused (conservative); one-line `--- {}` reads as a scalar; the jsonl depth error doesn't name the line; anyOf mixing scalar and container still drafts the first variant; the hardlink check covers the read path only (not Dir.list of another link's dir, not bind mounts); File.writeAtomic deliberately not inode-keyed; the plan guard at depth 10 is still ~3x v0.0.74 and ~1.4x its RSS (one lstat per new path, trie nodes).
- **perf / older:** the pipe-read cut in a race ~6x v0.0.74 per line (linear); Self.stdin from a file 1.5-2.2x; the piped REPL superlinear (exp 1.5-1.8, pre-existing); emoji-bearing long strings still keep a cache entry each; glob unclosed `a[`/`[x` match nothing (bash: literal), no `[[:alpha:]]`; `Str.tryIndexOf lo "😀"` → Some 1 (a split position); `\x1b[?1h\x1b=` emitted and never reset.
