# weir — multi-area adversarial review

Status: EXECUTED (findings-only, 2026-09-20). Fourteen findings across five
of seven areas, every one reproduced on an AOT binary built from the tree
under review (`0.0.0-dev+27eba9d-dirty`, HEAD `27eba9d`; the dirty marker is
this document, untracked at build time). Nothing in `src/` moved — findings
are for a separate blessed fix session, like the four reviews before this one.

Egress in the review container is ALLOWLISTED, not closed: `github.com`,
`raw.githubusercontent.com` and `api.nuget.org` answer 200, other hosts 403.
An earlier draft of this document said outbound HTTPS was 403 and deferred two
probes on that basis; both were then run, and F14 is what one of them found.

    weir tools/multi-repro.weir --bin ./path/to/weir

The harness exits nonzero while anything reproduces, so it is both the bug
report and the acceptance gate. Probe labels match this document's numbering
exactly (`F1`…`F13`), so a fixer never translates between the two documents.

Scope ruling (user, at plan time): all seven areas in one pass, findings-only.
Areas E (modules/LSP read scope) and G (boundaries) came back with one finding
between them and are recorded in the denominator; area F's tty face is the
only place where a planned probe was replaced mid-run (see NOT RUN).

## Context — what this review was allowed to attack

Four reviews and two safe-by-design passes precede this one, each closing its
ground with a denominator: `PLAN-adversarial-review` (depth, yaml/json
interop, reifier PATH escape, NUL at argv, inference budget),
`PLAN-dx-review`, `PLAN-concurrency-review`, and the two safe-by-design
passes in `dev/NOTES.md` (the four properties; then resource exhaustion, LSP
framing, path handling, the build stamp). None of that was re-run.

The review exists because the surface roughly doubled after those passes —
`src/Weir` went ~30.5k to ~56.9k lines — and the growth was three new KINDS
of surface, not more language:

- **`Serve.fs`** — weir now accepts untrusted input from a socket. Every
  prior review's untrusted input arrived at check time.
- **`Purity.fs` + `Effects.fs`** — `check --can`, `pure`, `readonly`, `plan`:
  a soundness claim (`[D:can-report]`) nothing had tried to launder past.
- **`Contracts.fs`** (now 1351 lines) — `add` / `restore` / `verify` /
  `gen types`, `.weir/lock.json`: the one subsystem that writes files, runs
  `git`, fetches URLs, and consumes a hash.

## Findings

Ordered by severity. Each names the claim sentence it falsifies.

### F1 — `weir restore` writes wherever a checked-in lockfile says (contracts spine, Property 3)

The worst finding in the review, and the only one whose exploit is "clone a
repo and run the documented setup command".

`Contracts.fs` `restore` computes its destination as
`Path.Combine(weirDir, e.Path)` where `e.Path` is a string read verbatim from
`.weir/lock.json` — no validation, in a subsystem whose stated property is
**vendored: checked in under `.weir/`**. weir documents this exact BCL
behaviour in its own SECURITY non-claims ("an absolute second argument WINS…
`..` is not normalized") and ships the confining join (`Path.under`) for it;
`restore` uses `Path.Combine`.

Measured, with the artifact served from loopback so the probe is offline:

| lock `path` | result |
|---|---|
| `schemas/benign.json` | inside `.weir/` (control) |
| `../../../../tmp/…/TRAVERSED.json` | **written outside**, `restore` rc=0, "restored from …" |
| `/tmp/…/ABSOLUTE.json` | **written outside**, absolute path wins |
| a path naming an EXISTING file | **overwritten**, no refusal, no backup |
| `/etc/hostname` (unwritable) | **rc=134**, unhandled .NET exception, core dumped |

Three separate defects ride the one root cause, and the fix has to close all
three or the primitive stays:

1. **The write escapes `.weir/`.** Any path the user can write: `~/.bashrc`,
   `~/.config/…`, a git hook, `~/.local/bin/weir` itself. Arbitrary file
   write is arbitrary code execution one step later.
2. **It overwrites.** weir's own `File.copy`/`File.move` REFUSE existing
   destinations by design (`[D:fs-members]`); `restore` truncates.
3. **An unwritable target is a CRASH, not a diagnostic** — see F2, same
   shape, and note the ordering: the first entry was written BEFORE the crash,
   so a partial application with no rollback is the normal outcome.

The hash gate does not help: the attacker authored the lock, so url, sha256
and path are all theirs. The gate defends against source DRIFT, never against
hostile intent — worth stating because the lock's sha256 reads like a
security boundary and is not one.

`verify` shares the unvalidated join and is a **read** oracle: pointed at
`/etc/hostname` it read the file and printed `sha256 1d1045de4d6e…`, and its
ABSENT-vs-MODIFIED split answers "does this path exist" for any path the user
can read. A hostile lock plus a CI job that runs `weir verify` and logs its
output leaks a confirmable hash prefix of any readable file.

DONE WHEN: every lockfile-derived destination goes through a confining join
(`Path.under weirDir` is weir's own answer) with a located diagnostic naming
the offending entry; `restore` refuses to overwrite a file it did not write,
or states that it does; an unwritable destination is a diagnostic; `verify`
confines its reads the same way. The entries must be rejected at LOCK READ,
once, rather than at each use site — same reasoning that put the NUL guard at
the argv boundary rather than in every producer (F4 of the prior review).

### F2 — an unreadable file on the import path crashes `check` (Property 3)

    weir check       -> rc 134, Unhandled exception, core dumped
    weir check --json -> rc 134
    weir check --can  -> rc 134
    weir fmt          -> rc 0 (fmt resolves no imports)

A script importing a module whose mode is `000` kills the process with a raw
.NET stack trace (`System.UnauthorizedAccessException` … at
`Weir.Script.readImportSource`). No depth, no hostility, no crafted input — a
file permission.

This falsifies SECURITY Property 3 as written: "On any input the checker
returns a located diagnostic rather than silently mis-executing… **No input
crashes the process**; that is a machine-checked invariant, not a prose
promise." And it inherits F1-of-the-prior-review's REACH argument verbatim:
`weir lsp` reads import-reachable files from disk, so an unreadable `.weir`
file in a cloned repo (or one owned by another user) kills the language
server on didOpen, and the editor restarts it into the same death.

The repair shape already exists in the codebase: `File.read` at RUNTIME on
the same file gives `File.read: permission denied: /tmp/rev/e/m.weir` — a
located diagnostic. Only the import reader is unguarded. An unreadable
DIRECTORY on the import path is also fine ("cannot resolve import: no file
at …"), because `File.Exists` answers false — so the gap is exactly
"exists but cannot be opened".

Same root shape as F1's crash face: an IO exception that no one converted.
Treating them as two bugs is the mistake; they are one unguarded-IO defect
with two faces, and a fix session that patches only the import reader leaves
`restore` crashing.

DONE WHEN: every read of a path weir did not choose is guarded, and the
diagnostic is located; the fuzzer or e2e gains a mode-000 fixture on the
import path AND the restore path; `weir lsp` survives didOpen on an importer
of an unreadable module.

### F3 — a CRLF in an HTTP request header value forges a second header (the argv law's HTTP face)

`Http.fs:119` adds caller headers with `TryAddWithoutValidation`, which by
name does not validate. One weir pair:

    headers = [("X-Evil", "a" + crlf + "Injected: yes")]

arrived at the referee as TWO headers — the server's own log, not weir's
view:

    X-Evil: 'a'
    Injected: 'yes'

This is the exact class weir's argv architecture exists to deny, at a
boundary that got no architecture: "there is no weir spelling that turns a
variable's contents into multiple arguments" holds for argv and fails for
headers. A value that reaches a header from anywhere untrusted — a config
file, an API response, a tenant name, a filename — can forge
`X-Forwarded-For`, a second `Authorization`, or anything the upstream service
trusts.

Asymmetric, which is worth recording: a CRLF in the header NAME is silently
DROPPED (the whole pair vanishes), so names are validated and values are not.

DONE WHEN: a header name or value carrying CR, LF or NUL is refused at the
send boundary with a located diagnostic naming the header — the shape F4 of
the prior review chose for NUL at argv (refuse at the one crossing point,
not in every producer). Silent drop is not the answer either; see F11.

### F4 — `secretHeaders` survive a cross-origin redirect (`Secret`, Property 4's spirit)

Measured against a referee that logs what arrives, with a 302 from
`127.0.0.1:8503` to `127.0.0.1:8504`:

| credential channel | crossed to the new origin? |
|---|---|
| `auth = Bearer tok` | **no** — `Authorization` stripped (the BCL does this) |
| `secretHeaders = [("X-Api-Key", tok)]` | **yes** — `X-Api-Key: 'SECRET-TOKEN-ABC'` logged at 8504 |

So the typed `auth` union is protected by .NET's redirect rules, and
`secretHeaders` — the field whose documented purpose is "credential headers",
carrying a `Secret`, the type whose whole promise is careful handling — is
forwarded to whatever host the redirect names. The redirect is invisible in
the script; the destination is chosen by the server being called.

The asymmetry is the finding: two channels documented as equivalent ways to
send a credential have different exfiltration behaviour, and the safer one is
safe by inheritance rather than by weir's decision.

DONE WHEN: `secretHeaders` are dropped on a cross-origin redirect like
`Authorization`, or redirects are not followed for a request carrying one, or
the asymmetry is a stated non-claim naming which channel is safe. Any of the
three is defensible; silence is not.

### F5 — `for … do` is refused inside `pure` / `readonly` / `plan`, and the message leaks `|seqIter`

A `for` loop whose body reaches no effect at all is refused by all three
gates:

    this 'pure' block forbids effects, but '|seqIter' runs a command
    this 'readonly' block forbids external mutation, but '|seqIter' runs a command — reads are allowed
    a command reifier runs a command, and 'proc' is refused inside 'plan'

Two standing rules break at once. First, a false REFUSAL of correct code:
`for` is weir's only iteration statement, so `pure`/`readonly`/`plan` cannot
contain a loop, and the workaround (`xs |> Seq.iter f`) is the very shape the
docs say `for` desugars TO ("it IS `Seq.iter` — desugared to the piped
shape"). The conservatism law licenses refusing a truly-pure body, but its
stated trigger is "a call through a function-typed param or any unknown
callable" — a `for` over a literal list is neither, and the diagnostic blames
a command that does not exist.

Second, `'|seqIter'` is an internal `|`-prefixed desugar key in a
user-facing message, which PROCESS forbids by name: "an internal or synthetic
name (`__hole1`, a `|`-prefixed desugar key, a raw generated tyvar) must
never reach a message [D:user-language-messages]".

DONE WHEN: a `for` body is classified by what its body reaches, not by the
desugar's shape; `pure`/`readonly`/`plan` each carry a `for`-loop fixture;
no desugar key appears in any message (a sweep, not a patch — the vocabulary
rule is mechanical).

### F6 — `readonly` changes the TYPING of a piped unit statement

    let noop x = ()
    let b =
        readonly
            [1] |> Seq.iter noop      // error: the right side of a pipe must be
            "ok"                      // a function taking the piped value; it has
                                      // type ('a1 -> unit) -> seq<'a1> -> unit

The identical statement inside `pure` checks CLEAN. Bound (`let _u = [1] |>
Seq.iter noop`) inside `readonly` checks clean. Only the unbound statement
form inside `readonly` fails, and it fails as a TYPE error: the checker sees
`Seq.iter` unapplied, as though the argument were dropped.

A gate that changes what types is worse than a gate that refuses too much: it
is the legal-parse-wrong-meaning family that PROCESS's behavioral-pins rule
exists for. Isolated 2x2 (gate × pure-bound-or-not) to confirm the gate is
the variable, not the function.

DONE WHEN: `readonly` and `pure` agree on every body that is not an effect
question; the pin is a fixture pair (same body, two gates) rather than a
single-gate test.

### F7 — every `for … do` adds a phantom "runs anything" capability, and `--strict` fires on it

    $ cat b2-print.weir
    for n in ["a"] do print n
    $ weir check --can b2-print.weir
      ⚠ this report is incomplete: 1 opaque site(s) — an interpreter's argument
        or a dynamic head cannot be analyzed statically
      mutations (change the world):
        runs:
          ^$(…) (not statically known — a dynamic head resolves at run)  1:1
    $ weir check --can --strict b2-print.weir ; echo $?
    2

A script that runs no command reports that it can run an arbitrary program,
and fails the documented CI gate. Scoped: both the one-line and the indented
`for` forms; `Seq.iter`, `retry` and `if` bodies are clean. Same root cause as
F5 (the `for` desugar reads as a command site).

Three consequences, in order of how much they cost: the report's own honesty
sentence is FALSE for such a script (no interpreter, no dynamic head);
`--strict` — the mechanism that makes opacity actionable in CI — cries wolf on
ordinary loops, which is how a gate gets switched off; and `runs: ^$(…)`, the
strongest capability in the vocabulary, is diluted.

Over-reporting is licensed by "capability, not behaviour" only for things IN
the program. An untaken branch is in the program; `^$(…)` is not there in any
form.

DONE WHEN: `--can` reports no dynamic head for a `for` whose body contains no
command; the opaque-site COUNT is pinned for a for-only script (it is 0), not
just the text.

### F8 — a `serve` Stream body that raises mid-flight reports success to everyone

The handler returns `Stream` over a lazy producer; the producer raises after
the first element. Read off the socket by an oracle that is not weir:

    STATUS HTTP/1.1 200 OK
    CHUNKED True
    BODY b'd\r\ndata: first\n\n\r\n0\r\n\r\n'
    TERMINATED True          <- a proper zero-length chunk: "the stream ended"

and on the weir side: stderr silent, `Server.running` still true, script exit
code 0. The client believes it received a complete one-element stream; the
script never learns its producer died. **The failure is reported to nobody.**

This is F5-of-the-prior-review's lesson at a new boundary ("no answer is also
a wrong answer") and it contradicts the posture weir sells: "a failing command
raises by default, so there is no `set -e` folklore to get wrong". A
monitoring endpoint that streams SSE truncates silently and looks healthy.

Contrast with `within proc`, where weir already faced this and gave failure a
designated channel (`watch=` / `Proc.wait`, `[D:scoped-procs]`'s surfacing
rule). `serve`'s streaming body has no such channel.

DONE WHEN: a producer raise mid-body either aborts the response so the client
can detect it (no terminating chunk), or is surfaced to the script, or both —
and the choice is a ruling, with a pin asserting the client can TELL. A
proper terminator after a failure is the one outcome that must not survive.

### F9 — an HTTP method weir cannot name silently becomes `Get`

| sent | `req.method` |
|---|---|
| GET/POST/PUT/DELETE/PATCH/HEAD/OPTIONS | themselves |
| **QUERY** | **Get** |
| TRACE | Get |
| FROBNICATE | Get |
| lowercase `get` | Get |

Two faces. A verb outside weir's union is not refused and not carried — it is
misreported as the one verb every handler treats as safe, so
`match req.method with | Get -> …` runs the read path for a request that was
not a GET, and a handler cannot answer 405 for an unknown verb because it
never sees one. And `QUERY` — a method the SHARED type family explicitly
includes on the client side (`Http.query`) — is unreadable on the server
side, so "ONE type family… shared client↔server" is asymmetric in the one
case where the two sides were supposed to meet.

DONE WHEN: an unrepresentable method is either carried (an `Other of string`
case, the `[<Other>]` shape weir already uses for tagged unions) or refused
with a 405 before the handler; `QUERY` reads as `Query`; a pin covers one
verb of each kind.

### F10 — duplicate request headers collapse to the last value

    sent:  X-D: one / X-D: two / X-D: three     -> handler sees  X-D=[three]
    sent:  X-Forwarded-For: 1.1.1.1 / 2.2.2.2   -> handler sees  X-Forwarded-For=[2.2.2.2]

`HttpServerRequest.headers` is documented as "pairs, wire order", and the
pairs-not-a-map decision is justified in the client section precisely because
"duplicate header names are legal HTTP (Set-Cookie), and a map cannot hold
them". Inbound, the pair list is built from something that already lost them.

The failure scenario is the documented deployment: `serve`'s posture is
reverse-proxy, and a proxy chain is exactly what produces repeated
`X-Forwarded-For`. A script reading the client IP gets the last hop only —
silently, with no way to notice.

DONE WHEN: duplicates reach the handler as separate pairs in wire order, with
a three-duplicate fixture (two cannot distinguish last-wins from a merge).

### F11 — a response header weir cannot send is silently DROPPED

A handler-supplied header whose value carries CRLF or LF, or whose name is
empty, does not reach the wire and produces no diagnostic — confirmed absent
by raw socket read, with a benign header as the positive control proving
headers do arrive. The script asked for a header; the client never got it;
nobody was told.

The pair with F3 makes the shape clear: at the same boundary, weir silently
FORGES on the client side and silently DROPS on the server side. One of the
two behaviours is a security bug and the other is a correctness bug, and
neither is the refusal the argv boundary already knows how to do.

HELD alongside it, recorded so the fix does not regress it: no response
header injection was achievable (the CRLF is dropped, not emitted), a
handler-supplied `Content-Length: 99` is overridden to the true length, and
duplicate `Set-Cookie` IS preserved as two wire headers (an ordinary
duplicate name folds to `X-Dup: one, two`, which is RFC-equivalent for
list-valued headers and worth only a doc line).

DONE WHEN: an unsendable header is a located diagnostic naming the header and
the offending byte, not a silent drop.

### F12 — `serve` has no request-body read timeout, so one slow client parks a handler slot

A client that declares `Content-Length: 1000000`, sends ten bytes and holds
the socket:

    maxConcurrent = 1
    control (no liar):            served in 0.00s
    honest client during the lie: served in 4.01s   <- waited out the liar
    after the liar closed:        served in 0.00s

The slot is held during the body READ, before the handler runs, for as long
as the client chooses. With `maxConcurrent = N`, N such sockets are a
complete denial of service, and `{ port; maxConcurrent }` has no timeout knob,
so a script cannot defend itself.

The honest qualifier, because it changes the severity: `serve`'s documented
posture is loopback-only behind a reverse proxy, and a proxy that buffers
request bodies (nginx's default) absorbs this. What remains is that weir's
own surface offers no bound, and the failure mode is invisible — the script
looks alive and serves nobody.

DONE WHEN: a body read is bounded (a `serve` field, or a stated default), or
the exposure and its reverse-proxy dependency are a stated non-claim.
`maxConcurrent` itself HELD cleanly: 6 clients / ceiling 2 / 1s handler ran
in three waves (1.01s, 2.01s, 3.01s), excess queued as documented.

### F13 — terminal escape sequences from data reach the terminal verbatim

Filenames on disk, rendered by the documented spelling
(`ls |> Seq.iter (fun f -> print f.name)`), captured at a REAL tty by the
repo's own pty instrument:

    b'\x1b[2Jcleared.txt\r\n\x1b[31mred.txt\r\n\x1b]0;PWNED-TITLE\x07title.txt\r\na\rb-carriage.txt\r\n'

So a directory of downloaded files can clear the screen (`\x1b[2J`), leave the
terminal coloured (`\x1b[31m` with no reset), **set the window title**
(OSC 0), and — the quiet one — use `\r` so the name the user READS
(`b-carriage.txt`) is not the name on disk (`a\rb-carriage.txt`), which is
then copied into the next command.

No claim covers this, which is the finding: weir has ALREADY decided that
hostile bytes must not wreck a terminal — `[D:binary-echo]`'s guard refuses a
tty echo when the prefix carries NUL, with a redirect hint. That decision is
one byte wide. The `Script.fs` ANSI strip is SGR-only
(`\x1b\[[0-9;]*m`), so every non-SGR sequence passes.

DONE WHEN: either the tty-bound renderers neutralize C0/C1 controls and
escape introducers in DATA (the `ls` table, `show`, interpolation, error text)
while leaving weir's own colouring intact, or SECURITY.md gains the non-claim
and SKILL says so where `ls` is taught. The `\r` case decides which: a
non-claim that reads "weir does not sanitize" still leaves a user reading a
name that is not the file's name.

### F14 — `weir add schema --as <name>` vendors outside `.weir/`, and `gen types` follows it

F1's add-time face, and the reason a hostile `.weir/lock.json` needs no hand
editing: the documented CLI writes one. `--as <name>` reaches the filesystem
unvalidated.

    $ weir add schema <url> --as /tmp/…/c6-ABS
    added schema /tmp/…/c6-ABS (1db2c4fecb34…) from https://raw.githubusercontent.com/…
    $ weir add schema <url> --as ../../../../tmp/rev/c6-ESCAPE
    added schema ../../../../tmp/rev/c6-ESCAPE (1db2c4fecb34…) from …

| `--as` | lock `path` | file written |
|---|---|---|
| `k8scm` (control) | `schemas/k8scm.json` | inside `.weir/` |
| `/tmp/…/c6-ABS` | `/tmp/…/c6-ABS.json` | **outside**, absolute wins |
| `../../../../tmp/rev/c6-ESCAPE` | `schemas/../../../../tmp/rev/c6-ESCAPE.json` | **outside** (`/tmp/tmp/rev/…`) |
| `..` | `schemas/...json` | a file named `...json` |
| `sub/nested` | `schemas/sub/nested.json` | a subdirectory, undocumented |
| `a$(id)b` | `schemas/a$(id)b.json` | literal — word integrity holds |

Three faces, and the third is the worst:

1. **The add writes outside** the vendor directory it exists to fill.
2. **The lock keeps the escaping path**, so every later `restore` and `verify`
   re-targets it — F1's write primitive and read oracle, seeded by a command a
   README or Makefile can legitimately contain. A hostile lock is therefore
   not evidence of tampering.
3. **`weir gen types --schema <name>` inherits it and writes weir SOURCE.**
   With the escaping entry in the lock, `gen types` wrote
   `/tmp/rev/c6-ABS.weir` (rc=0) and printed `import it: import
   "weir:/tmp/rev/c6-ABS"`. Generated `.weir` files are content a script
   imports, which is a better primitive for an attacker than a schema blob.

**The guard already exists, one kind over.** `add module` refuses the same
name outright:

    weir add module: '--as /tmp/…' must be a plain name (a letter, then
    letters/digits/_) — it becomes the vendored file and the import alias

So this is not a missing concept, it is an unapplied one — `[D:add-module]` is
the newer command and carries the check the older `add schema` lacks. `add
sig` is safe only incidentally (the name must resolve on PATH).

DONE WHEN: every `--as` name goes through `add module`'s plain-name rule (one
shared validator, not a third copy), `gen types` refuses a lock entry whose
path is not under `.weir/`, and a pin covers the absolute AND the traversing
name for each kind. Note the ordering against F1: fixing F14 alone still
leaves hand-written locks live, and fixing F1 alone still lets `add` write
outside — the two are one boundary, and the boundary is "a lockfile path is
confined to `.weir/`".

## Doc-level observations

- **`Http.send` honours the ambient proxy environment.** A request to a host
  outside `NO_PROXY` went through `HTTP_PROXY` (observed as the proxy's 403,
  which cost this review an invalid cross-host probe — see NOT RUN).
  Undocumented in SKILL and adjacent to Property 2's spirit: the source does
  not fully decide where a request goes.
- **Ordinary duplicate response headers fold** (`X-Dup: one, two`) while
  `Set-Cookie` stays split. RFC-equivalent for list-valued headers; worth one
  line so nobody re-derives it.
- **`add module` fetches over HTTPS; it does not clone.** `[D:contracts-spine]`
  describes the module kind as "clone at a ref", and the implementation
  resolves the ref through the GitHub API and fetches the single file from
  raw. Better than cloning (no `git` argv, no hooks, no submodules) — the word
  is just stale, and it mattered to this review: C3 went looking for a `git`
  injection that cannot exist on that path.
- **`restore` skips the hash check for `generated:` entries that are
  PRESENT** (`Contracts.fs:903`), reporting "present" for a locally modified
  signature. `verify` does check it, so the pair is sound — but `restore`'s
  own drift-repair promise has a hole, and "present" overstates what was
  checked.

## DENOMINATOR — what was attacked and HELD

Recorded so the next review starts past it.

- **Response header injection**: not achievable — CRLF in a value or name is
  dropped, never emitted (the bug is the silence, F11). `Content-Length`
  supplied by a handler is overridden to the truth.
- **A raising handler leaks nothing**: body is exactly `internal error`, status
  500; no message text, no path, no `Secret`, no stack. The accept loop
  survives every raise and keeps serving.
- **`serve` socket lifetime**: the port frees and immediately rebinds after a
  raise inside the block, after SIGTERM, and after SIGINT. A second bind on a
  live port refuses with a worded error ("Address already in use"). `stop` is
  idempotent as claimed.
- **`maxConcurrent`**: a real ceiling, excess queued (three clean waves).
- **Request path normalization**: `/a/../../etc/passwd` and
  `/a/%2e%2e/%2e%2e/etc/passwd` both reach the handler as `/etc/passwd` —
  decoded and dot-segment-normalized before the script sees them, so
  `%2e%2e` cannot smuggle past a script's own check. `query` is raw as
  documented.
- **`--can` SOUNDNESS — the claim under test — held across a 9-carrier
  laundering matrix**: a lambda, a `pmap` worker, a `for` body, an imported
  module function, a function-typed RECORD FIELD, a function-typed PARAM, a
  `Graph.reach` step function, a captured `plan` applied later, and a `retry`
  body. Declared set = 9 write sites (including the module's own file:line);
  observed set = 9 canaries. Nothing was laundered past the report. The area's
  defects (F5–F7) are all over-reporting or mis-classification, never
  under-reporting.
- **Check-time-only**: with a hostile lock present, `weir check`, `weir
  <script>`, `weir check --can` and `weir fmt` fetched nothing and wrote
  nothing; the traversal target never appeared. The property holds — F1 needs
  the user to run `restore`, which is what the docs tell them to do.
- **Check-before-effects across entry points**: a canary write followed by a
  type error produced ZERO effects through `weir <script>`, `weir -e` with
  multiple statements, `weir check --can`, and `weir fmt`. A module with a
  top-level effect is refused at check.
- **Import resolution**: a non-module import, a symlink pointing out of tree,
  and an import CYCLE all give located diagnostics naming the resolved path
  (the cycle names its ring, with an `imported-here` note). An unreadable
  DIRECTORY on the import path is a clean "cannot resolve import".
- **`Str.fromBase64` refuses NUL** (the prior review's F4 fix holds), and
  weir has no `\r` escape at all — so a CR-bearing string needs a decode or a
  read, which is why F3's payload arrives the way a real one would.
- **`add sig` on a hostile tool**: 200KB of ANSI-laden multiline `--version`
  output produced a clean refusal ("found no flags to record… write
  .weir/sigs/hostiletool.weir by hand"), no artifact, no lock entry. A
  path-shaped tool name is refused too, though incidentally — the name must
  resolve on PATH, not because it was validated (F14's note).
- **`add module` validates its `--as` name** against a plain-name rule and
  refuses an absolute or traversing one. This is the row that makes F14 a
  consistency bug rather than a design gap.
- **Contract acquisition fails CLOSED with nothing written**: a schema outside
  the supported subset, a module file that is not a module, a bad ref, an
  unknown host — each refuses with a located or named diagnostic, exit 1, and
  no partial artifact or lock entry. (Watch the exit code with a bare command,
  not through a pipe: an earlier reading here said rc=0 because `$?` was
  `head`'s — see instrument honesty.)
- **The REPL history file is mode 0600.**
- **The build stamp marks a dirty tree** (`0.0.0-dev+27eba9d-dirty`) — the
  gap flagged by safe-by-design pass two is closed.

## NOT RUN — stated rather than implied

- **C5's accepted path**: `add sig` against a tool that DOES yield flags with
  hostile `--version` text (the refusal path is what got probed). Needs a fake
  tool with a fish completion or `--help` weir will parse.
- ~~**C6 `add schema <url>`**~~ — RUN after the egress reading was corrected;
  it produced F14. The happy path is clean: a subset-compliant schema fetched
  from `raw.githubusercontent.com` landed at `.weir/schemas/<name>.json` with
  the hash the repo's own `examples/.weir/lock.json` records, and a schema
  OUTSIDE the supported subset is refused with the offending keyword located
  (`at definitions.//.: 'explainer' is outside the schema subset`),
  `nothing was written`, exit 1.
- ~~**C3 `add module` flag injection**~~ — RUN, and HELD for an informative
  reason: the github/gitlab shorthand never invokes `git`. It resolves the ref
  through `api.github.com/repos/…/commits/<ref>` and fetches from
  `raw.githubusercontent.com`, so a flag-shaped ref becomes a URL segment (422
  UnprocessableEntity), a flag-shaped host is refused by the host allowlist
  with a teaching message, and a traversing file component 404s with
  `nothing was written`. No `git` argv to inject into on this path. The
  untested remainder is the full-raw-URL form for a non-shorthand host.
- **D1 (TLS fails closed)**: a self-signed loopback peer was planned; the
  session spent its TLS budget on F3/F4 instead. `insecure` is untested here.
- **E1's LSP face**: the read-scope claim ("never reads a file the open
  documents do not name") was probed through `check`, not through a live LSP
  session with canaries. F2 reaches the LSP by argument, not by measurement.
- **F13 at the REPL table**: the pty probe of the REPL's own table renderer
  was inconclusive — the REPL under `pty-run.py` emitted only its echo within
  the window, and the CONTROL (ordinary filenames) produced the same output,
  so the instrument could not tell. F13 is established for `print` at a tty,
  which is the same renderer path for data, and NOT established for the table.
- **G1 persistence**: whether a prompt-typed `Secret` lands in the history
  file. The REPL did not flush history in the probe window (file unchanged);
  only the 0600 mode is established. Absence of evidence, recorded as such.

## Instrument honesty

Stated rather than presumed, per the vacuous-probe bar.

- **Every wire claim is refereed by something that is not weir**: a raw-socket
  client for the server's response bytes, a logging HTTP referee for what
  weir's client sends, `python3 hashlib` for hashes, the repo's own
  `tests/pty/pty-run.py` for tty bytes. This was not decoration — the raw
  socket CORRECTED a wrong reading of mine: `curl -i` displayed duplicate
  `Set-Cookie` folded, and the socket showed two headers on the wire, so a
  finding I had half-written was withdrawn.
- **Positive controls, independent of every finding's payload**: a benign
  header proves headers reach the wire (without it, "the CRLF header is
  absent" could mean headers never work); a benign lock entry proves `restore`
  works at all; ordinary filenames are the control for F13; `pure` is the
  control for F6; `Seq.iter` is the control for F5/F7.
- **Two instrument defects were found and fixed mid-run, both recorded
  because both are recurring shapes.** (a) The slow-client probe sent
  `Host: x`, so its requests were 404'd by HttpListener's prefix match and
  never reached the handler — the "honest client is still served" reading was
  vacuous until the Host was corrected, and the corrected run is what produced
  F12's 4.01s. (b) A `pkill -f 'http.server 8500'` matched the probe's OWN
  shell and killed it — the exact trap `dev/NOTES.md` recorded from the
  earlier resource audit (`pgrep -f` matches the prober). Recorded again
  because it was rediscovered, not remembered.
- **A third instrument defect, same family as the first two: `$?` after a
  PIPE is the pipe's tail.** `weir add schema … | head` reported rc=0 for a
  refusal that really exits 1, and the first reading of that probe nearly
  shipped as a finding ("a refusal exits 0"). Every exit code in this document
  is now read from a bare command with its output redirected to a file. The
  verify rule the repo already has ("run the battery for its EXIT CODE first,
  then grep the log") exists for exactly this and I did not follow it.
- **The egress reading was wrong and cost two deferrals.** This review first
  recorded "outbound HTTPS is 403" from a single `example.com` probe and
  deferred C3 and C6 on it. Egress is ALLOWLISTED: github and nuget answer
  200. Both probes were then run and one produced F14 — a finding that a
  one-host generalization had written off. Same shape as the predecessor's
  "concluding absence from a partial search", applied to the environment
  rather than to the grammar.
- **One probe was invalidated by the environment, not by weir**: the
  cross-HOST redirect test (127.0.0.1 → 127.0.0.2) returned 403 from the
  container's egress proxy, because `NO_PROXY` covers `127.0.0.1` and not
  `127.0.0.2`. F4 was re-established across two PORTS, both inside
  `NO_PROXY`, with the referee's log proving what arrived. A reader should
  know that F4's "cross-origin" is port-differing, not host-differing.
- **Clocks where a hang is the failure**: F12 is measured in elapsed seconds
  against a no-liar control, never by an exit code.
- The harness is weir per the scripting policy; python referees enter through
  `sh -c`, the stated escape.

## The failure mode this review had

The predecessor named *concluding absence from a partial search*, and this
review's version was **concluding presence from a display**: `curl -i` showed
`X-Dup: one, two` and I read it as weir folding headers, when the fold was
curl's. Only the raw socket settled it. The same reflex nearly cost the
opposite error in F12, where a 404 from the listener's prefix match looked
like a healthy served request.

Both were caught by asking what the instrument could actually see — which is
the argument for the per-area referee rule above, and for stating what was NOT
run rather than letting coverage be inferred from a green harness.
