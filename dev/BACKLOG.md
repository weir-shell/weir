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
- **`from xml` on System.Xml — 1.09 MB (parked 2026-10-18):** measured by disabling `XmlReader.Create`/`XDocument.Load` (18.10 → 17.01 MB, ~6%); stubbing the writer and the DTD parser saves only 8 KB, the rest is the reader's settings-driven branches the trimmer cannot prove dead. Options when size matters: a weir-owned reader for the subset (elements, attributes, text, the five entities and character references, no DTD — ~300–400 lines like Yaml.fs/Toml.fs, est. ≤60 KB, ~−1.0 MB net, the existing tests as the pin), or drop `from xml` with a refusal teaching `yq -p xml -o json` / `dotnet msbuild -getProperty` + `from json`. Ruled: park (user). Audit: /output/tmp/h3r4f-audit/.
- **gRPC via grpcurl — doc parked (2026-10-18):** no native gRPC (estimate: MVP unary + server streaming ~4–6k lines, +150–300 KB); the chosen path is a verified guide section on calling services through grpcurl. Blocked on a reachable gRPC server (the sandbox proxy blocks grpcb.in / grpc.postman-echo.com; a local test server needs a download). Verified offline with grpcurl v1.9.3: no compact output mode (`-format json|text`; streamed pretty objects vs `from jsonl` untested); `-expand-headers` takes `${NAME}` from the env so credentials stay out of argv; `-d @` reads the request from stdin; `-import-path`/`-proto` list/describe without reflection; dial failures and `-max-time` deadlines exit 1, readable stderr, `| complete` captures them. Scratch /output/tmp/grpcurl-doc/.
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

## From hunt-3 round 4 (2026-10-18, lows — [D:repro-gates] harness tools/hunt3r4f-repro.weir)

- **network / serve:** NO_PROXY `*` anywhere in the list bypasses all hosts (curl: a lone `*` only; kept per the noproxy-star ruling); `insecure = true` follows a cross-host redirect insecurely (curl `-k` does too; undocumented); serve: CL+TE gives the handler a synthesized `X-Content-Length`, an overflowing chunk size answers 500 not 400, `OPTIONS *` answers 404, `GET a` answers 405 `Allow: CONNECT`, `Content-Length: +3` accepted, `req.query` partly percent-decoded despite "raw", a bad-response-header streamErrors entry lacks the path.
- **lock / restore:** a symlinked vendored file refuses as "tampered"; a `sig` lock entry with an http url is restored from it (docs say sigs can't be restored); kind/path mismatches (a schema under modules/) accepted.
- **rendering:** `print` escapes LRM/RLM (mangles RTL) while ZWSP, a mid-string U+FEFF and the soft hyphen pass; `show` escapes CR not ESC (documented) so `print (show x)` passes SGR; `\x1b`/`\u200E` are rendered but not accepted in string literals.
- **stdin / REPL / tty:** a pfirst prompt-with-timeout's spinner overwrites the `q? ` prompt; a tty Self.prompt keeps a lone ESC / Alt-key / OSC / C1 CSI (only CSI/SS3 dropped, documented); `exit` reached through a function inside `always` passes the check (ends the cleanup early on the clean path, dropped silently while unwinding); Self.prompt reads byte-at-a-time even from a regular file (200k lines ~7 s vs bash 1.2 s); a trailing CR at EOF stays in the answer; backspace without iutf8 leaves U+FFFD; `\x1b[?1h\x1b=` never reset; the spinner line isn't erased on the watchdog/grace exit.
- **signals:** a signal during an EPIPE teardown counts as the first (exit 141); a third signal of another kind keeps the earlier code; concurrency.md's boundary list vs the main-thread Http send residual; a scoped terminal ^C at `sh -c 'trap … INT; cmd & wait'` lets the trap exit before the kill walk, its background `cmd` reparented.
- **formats / schemas:** `patternProperties` refused (compose-spec 68 uses — the biggest blocker), a boolean schema `true`, draft-07 tuple `items`; `{}` drafts string (should be Yaml); `additionalProperties: true` drafts string pairs; `x-kubernetes-preserve-unknown-fields` drafts string; a `pattern` inside a `oneOf` alternative has no note; the oneOf refusal names only "IntOrString"; annotation-only keywords (markdownDescription, deprecationMessage, defaultSnippets, tsType) refuse the whole schema (H3R4-B11, ruled low); the GitHub workflow schema's next blocker is `dependencies` at definitions.step; `from table` cuts a row wider than its header mid-word (H3R4-B8, ruled low); -0.0 → 0.0; an instant past 9999 after offset "not ISO 8601"; a :60 leap second refused; invalid-JSON errors carry no position, JSONL errors don't name the line; yaml keys over 1024 chars written implicit (PyYAML refuses); a leading tab indentation accepted; `a:\tx` "got a scalar"; a BOM in a later stream document becomes key text; `File.append` in a plan refused at run time (not in the SKILL's list); Copy/Move read their source at apply (the snapshot line covers WriteFile only); `[<Wire "">]` refused; `yaml schema=` messages: mixed `[integer, object]` says "expects integer", a seq item error lacks its index.
- **perf:** nested fan-out set-up O(depth) per fan-out (pre-existing); pfirst over 1e6 all-failing arms 1.4x r3; piped REPL superlinear (exp 1.6); fmt 200k lines 46 s / 1.1 GB; `from jsonl |> to json` ~14x input RSS.


## From hunt-3 round 5 (2026-10-19, lows — [D:repro-gates] harness tools/hunt3r5f-repro.weir)

- **trust boundaries / network:** `weir add sig` over 100 hanging subcommands makes no progress for 240 s (each probe now bounded, the walk is not); sig docs drop a CR / double escapes; NO_PROXY malformed entries over-bypass (`127.000.000.001`, `1.2.3.4/`, `::1/`, `/-1`, `/64x` — the 9 corpus diffs vs curl); serve: a charset with control bytes decodes as UTF-8 instead of 415, the 415 body echoes an unbounded charset; URL userinfo silently not sent (curl sends Basic auth); a malformed HTTP(S)_PROXY silently ignored; `add sig`'s "not on PATH" message passes ESC when piped; closed stdout says ".NET: Access to the path is denied", a full-disk stdout error doesn't name stdout; a lone CR in a `//` comment ends the line (the rest runs, `cat` hides it); Args.load prints "missing required flag" right after the bad-value report; a schema behind basic auth gets 401 on `restore` now that the lock drops userinfo ([D:lock-no-userinfo]).
- **plans / paths:** physical `..` diverges from the kernel (dangling link, link loop's lexical fallback, `x/../x` on a file, `new/../w.txt`); Path.under is logical inside `within cd`; File.delete of a dangling symlink says "no such file", of a link to a dir "is a directory"; Dir.deleteAll "LA/.." / "." removes the cwd's contents (rm refuses).
- **formats / schemas:** `$ref "#/$defs/a%2Fb"` lacks RFC 6901 decoding; a wrong `$ref`-sibling note when the sibling is only `required`; a conflicting sibling property type silently wins; misleading YAML flow refusals; data-escape ambiguity (`\` not escaped; U+2028/U+200B pass while U+202E is escaped); the HTTP client refuses charset=windows-1252/cp1252/utf8 even for ASCII bodies (R4 covers only XML); gen types on an empty-string property key says "generator bug"; YAML depth 500 vs JSON 100 (`Yaml.parse |> to json` writes what `from json` refuses); SKILL says Instant fractions past 7 digits truncate — it's past 3; lenient on invalid YAML (quoted scalars with trailing junk, `a: - b`, reserved indicators, raw NUL, a trailing empty `---`, `[a]: b`); valid YAML refused (escapes `\/ \U \_ \e \0 \a \b \v \f`, an escaped line break in double quotes, a comment line between a block scalar's parent and content, `- - a`); JSON 1e400 into int hints float then float refuses; 1.0/1e2 into int refused; a JSONL BOM on line 2 refused invisibly; gen types: boolean schemas refused, `additionalProperties: true` drops extras without a note, `type: object` without properties drafts `seq<string*string>`, an OpenAPI document root refused; a TOML number into a string field normalised (`0x1F` → "31") where JSON refuses; a lone CR in TOML accepted; `#infer` drafts `0644` as int (then the read refuses, teaching) and `0x1F` as string.
- **command signatures:** uutils clap `--x <V>` defeats abbreviation detection (`ls --almost`, `date --dat`), `[alias: --utc]` unrecorded; ambiguous prefixes reported "unknown" without candidates, `--f x` under abbreviations suggests `-f`; did-you-mean ties (`--autosqash` → `--autostash` only); `bat cache --build`; `chmod -x` mode operand read as a flag; `ps --no-headers/--sort`; `sha256sum -b` hidden; awk program text doesn't end options; a splat `$@one` before a typo; duplicate stopAtOperand entries (docker); regeneration moves the lock entry and the `generated … on <date>` line guarantees a diff; LSP hover on `curl --retry` shows weir's retry keyword doc; git Globals after the subcommand pass (FN); did-you-mean costs O(sig flags) per unknown flag.
- **signals / terminal / REPL:** shim'd commands get an absolute argv[0]; a shebang-less executable gets a different message under the shim; SIGHUP shown "(signal 1)" while TERM is named; `\x1b[?1h\x1b=` re-emitted after every child exit; ^C at a scoped weir-in-weir SIGKILLs the inner weir before its cleanups (documented I11); interleaved stderr diagnostics mid-line across arms; the stdin gate isn't FIFO-fair (a second prompt overtakes a waiting sibling; Self.stdin in one arm starves a prompt in another); serve-in-cleanup + exit via a function truncates the cleanup silently; REPL: bindings before a failure are kept (undocumented), a later type error found after earlier statements ran, piped REPL failures don't stop later lines and exit 0, a ^C killing a captured command reports "command failed … 130" (re-check: stage E routes REPL 130 deaths to the interrupt); no scope + one TERM exits at once while an arm is stuck in an uncut call; pid-cgroup exhaustion prints "Failed to create the thread pool" then ignores SIGTERM (R9).
- **perf:** pfirst/pmap over 1e6-2e6 failing arms superlinear (exp 1.4-1.7, ~500 B retained per failed arm); nested fan-out set-up O(depth) per fan-out; NO_PROXY re-parsed per request (~1 µs/entry); within/always per iteration 1.6x round 4, linear.

## From hunt-3 round 6 (2026-10-20, lows — [D:repro-gates] harness tools/hunt3r6f-repro.weir)

- **trust boundaries / network:** URL masking splits userinfo at the first `@` (`http://bob:p@w@h` shows the password's tail; `http://h\@evil/` hides the host — .NET refuses both, nothing is sent); a scheme-relative `//u:pw@h/x` is unmasked in errors; an unremovable subdirectory leaves the probe temp dir behind silently, probe dirs are created 0755; sig generator: a flag with invalid UTF-8 is cut at the bad byte, NUL in a doc comment is double-escaped, a 4 MiB help line becomes one doc comment; `Args.load --port x` reports "not an int" and "missing required flag"; an IDN-rejected host (`-ü.example`) sends raw non-ASCII in the request line through a proxy and quotes .NET text direct; off Linux a malformed proxy variable falls back to the system proxy when one is set, and a malformed `http_proxy` beside a valid `ALL_PROXY` uses `ALL_PROXY` (curl refuses).
- **plans / paths:** `File.copy "g.txt" "g.txt/x"` says "no such directory" for a file; plan symlink chains cost linear but heavy (39 links ~5.8× base); `Path.glob` with hundreds of physical `..` components is slow (400: 0.69 s).
- **formats / schemas:** a typed int read of `00` refuses though 1.1 and 1.2 agree; `0x1F` into a float field says "expected float"; `1e400` into a float field says "expected float", not out of range; `to yaml` writes U+FFFE/U+FFFF raw (PyYAML refuses them); a multi-line quoted scalar followed by `# c` is refused; gen types: `type: string` + `enum [x, y, null]` and `type [string, null]` + `enum [x, y]` draft `Option` with a "required and nullable" note though null is invalid, `type: number` + `format: uint64` drafts float; `from yaml seq<Yaml>` refused as an untagged union; the two-document `Yaml.parse` message; a tab right after `-` refused, `a: |\n\t x` says "needs an indented block"; R3's "write 0o644" is a string to PyYAML (ruled); a `YStr s -> YStr s` walker turns `.inf` into the string ".inf" (documented); `a:\tx` reads as one string; `-0.0` loses its sign; an ASCII document labelled `utf-7` reads `+AOk-` literally; a CR in an `Http.expect` snippet renders as a space; the HTTP client refuses charset=windows-1252/utf8 for ASCII bodies.
- **command signatures:** skill/snice `-KILL`/`-STOP` warn (no dashWords for `skill [signal]`); micro's `-<option> value` form isn't detected as open; gpg accepts unique prefixes but `abbreviations` isn't set (`--detach-sig` warns); unzip `-Z` warns; the cmd-not-found did-you-mean for a missing `fx` suggests `'['`; apt-get/apt/man "found no flags"; git `submodule`/`bisect` get no cases (the completion helper answers neither; missed typos only); a binfmt_misc format a direct spawn would run is refused under the cleanup shim.
- **signals / terminal / REPL:** an inherited SIG_IGN SIGQUIT is kept without a tty (I15 lists INT/TERM/HUP only); shim'd messages carry the absolute argv0; when every pfirst arm in a cleanup is group-killed the race raises "command failed … 143" (I7 as written); a second REPL ^C in a cleanup prints "the cleanup ALSO failed — interrupted"; long strings are clipped with `…` and no hint even with `#echo all`; `#types off` + Alt+Enter swallows the next line as its argument; with types off an anonymous function echoes `<fun>` (repl.md says `<fun> : ty`); config.json errors show raw .NET text; no bracketed paste (a pasted ESC abandons the line); the `[for … ->]` hint suggests `do`; `#types on` in a script says "directives belong at the file head" at the head; an LSP interpolation hole in a script offers REPL directives; on macOS, `Dir.copy` of a socket fails (mknod needs privilege).
- **Windows:** a wide `Seq.pfirst` over `| complete` arms (32 arms, losers sleeping) returns its winner only when the losers end (~32 s; Linux/macOS 29 ms after [D:pipe-threads]); not the pool starvation fixed on Unix — a PeekNamedPipe read did not change it; the e2e D1 cell skips Windows until diagnosed on a Windows runner.
- **perf:** `weir add sig git` probes 2× base after S10's nested probes (duplicates removed in round 6); the cleanup shim's cost grows with huge argv (10k args ~3.5× the body); YAML 1e6 hex/leading-zero round trips 1.2-1.5× round 5, linear.

## From hunt-3 round 7 (2026-10-21, lows — [D:repro-gates] harness tools/hunt3r7f-repro.weir)

- **signals / core:** at the pids/thread limit .NET's own pool can abort the process when it cannot add a worker or its gate thread (~3/20 at a 256-wide race at the limit; pre-existing) — cap the pool or stop relying on pool completions; a SIGHUP handler held >250 ms still reads as a failure, and one held past the exit's 250 ms is dropped (recorded residual); unrelated stuck pool work makes a no-signal 129 wait up to 250 ms; a 32-bit compat ELF is refused by the cleanup shim; the shim's message for a bare garbage-ELF command differs from a direct spawn's (126 vs not found); a ^D typed during an eval is lost (cooked VEOF not drained); `ulimit -n 5` → unhandled IOException rc 141; a failing lazy `$(a | b)` in a cleanup reports the reading line's location.
- **plans / paths / editor:** `Dir.deleteAll "b/L/b"` with `b/L -> ..` reports success without deleting (the only plan-fuzzer discrepancy left); Dir.copy past PATH_MAX → .NET text + a partial copy; a vanished entry mid-copy names the source root; a blank-line-escape entry keeps its trailing indent-only line in history; Home+Backspace join keeps the tail's indent; no smart indent after a trailing operator (`<=`, `==`, `+`); a two-line statement typed during an eval loses auto-indent (the queued heuristic); a CRLF paste submits early; Alt+Backspace (ESC DEL) abandons the buffer; the Delete key (ESC[3~) does nothing; inside a `$$<<<` body `$` completion offers the argv set, not the checker's set there.
- **trust / sigs / formats:** display masking over-masks a credential-free URL whose query holds `@host.tld` (accepted trade-off for never leaking a password with `@`); a transient `.weir.lock` beside `.weir/` during an add on Windows; an add runs unlocked where the filesystem has no flock (the atomic swap still keeps files whole); `from xml` accepts BOM-less UTF-16LE labelled utf-8 (expat refuses); a sig `///` doc turns `d\e[31moc\a` into `doc\\x07`.
