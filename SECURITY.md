# Security

weir is a typed shell: it parses and runs `.weir` scripts. This file
states what weir defends **by design**, the one thing it deliberately
does **not** defend, and how to report a problem.

## Scope

**weir is not a sandbox.** Running a `.weir` script is exactly as
trusted as running a bash script: the script can do anything your user
account can do. "Untrusted input" here means malformed or hostile
*script text* handed to the parser/checker — not hostile *code* you
have chosen to run. weir's job on hostile text is to reject it with a
diagnostic, never to execute it safely. Do not run a `.weir` file you
would not run as a shell script.

## Non-claims — what weir deliberately does NOT defend

The scope line above is the headline; these are its corollaries,
each verified (a script CAN do the risky thing — weir does not stop
it, by design):

- **Path functions do not confine.** `Path.glob "../../**"` escapes
  any directory a script imagined as a boundary, and `Path.combine`
  follows .NET's rule — an absolute second argument WINS
  (`Path.combine "/safe" "/etc/x"` → `/etc/x`), and `..` is not
  normalized away. Confinement is the script's job, not weir's.
- **Word integrity is not flag safety.** weir guarantees a value —
  including `Path.glob` output — reaches a command as exactly one
  argv word. It does NOT guarantee the command won't interpret that
  word as a flag: a file named `-rf` globbed into `rm` is still
  `-rf` to `rm`. The `--` separator is the script author's tool.
- **Word integrity holds up to the hand-off** [D:windows-s2] — the
  same category, Windows face: a batch file's interpreter RE-PARSES
  the command line it receives (the BatBadBut class), so on Windows
  weir's one-word guarantee does not extend past `cmd`'s own parsing
  of a `.bat`/`.cmd` target's arguments. Native executables are
  unaffected (they receive weir's argv join verbatim — verified by
  the runbook's injection probe).
- **The LSP reads client text plus import-reachable files.** `weir
  lsp` analyzes the document text the editor sends over the protocol
  AND, since user modules landed [D:modules-v1], the files reachable
  by `import` from an open document — an open dependency from its
  editor buffer, an unopened one from disk (buffer-over-disk,
  decision 14) — plus the signature files an open document's `#sig`
  lines declare [D:command-signatures]. Cross-file hover and
  definition [D:lsp-cross-file] read the SAME set, nothing wider. It
  never reads a file the open documents do not name;
  resolution is the same check-time path the CLI uses, so the
  server evaluates nothing.
- **The REPL records typed lines to a history file** [D:repl-quality]
  at `$XDG_STATE_HOME/weir/history` (Windows:
  `%LOCALAPPDATA%\weir\history`). A REPL line can carry a secret
  (`Secret.of "hunter2"` — the literal lands in the file as typed), so treat it as you would a
  shell's history — on POSIX the file is created `0600`. Windows has
  no chmod [D:windows-v1]: the file inherits the user profile's ACLs,
  which already deny other non-administrator accounts — equivalent
  protection by inheritance, not by mode bits. An administrator can
  read it on either platform (root can too). Scripts never write it
  (only the REPL does).
- **Temp-dir cleanup ends at SIGKILL** [D:exit-hook]. `within tmp`
  directories are removed by the scope's own `finally`, and a
  process-exit hook sweeps the registered leftovers on normal exit,
  SIGINT, and SIGTERM (per-process registration — never a scan of the
  temp root, so concurrent weirs cannot delete each other's dirs).
  SIGKILL / `TerminateProcess` runs no user-mode code, so a killed-9
  weir leaves its live temp dirs behind; the `weir-tmp-` prefix keeps
  them identifiable for external cleanup. `Dir.newTempDir` is exempt
  by contract (it exists to outlive the scope).

- **Capture is unbounded by design.** `| complete` and `Seq.force`
  materialize their whole input in memory (`complete` holds one byte
  buffer + line offsets — ~2x the raw text in RSS, measured; a
  single capture caps at the ~2GB array bound). A child emitting
  gigabytes will exhaust memory; the failure is a located error, not
  a crash, but the ceiling is the box's memory. Stream (don't
  capture) for large or unbounded output.

- **Contracts constrain what weir ACCEPTS, not what runs**
  [D:contracts-spine]. A vendored schema (and, later, a command
  signature) makes the checker stricter; it does nothing at run time.
  A hostile `bicep` earlier on PATH still runs. A lying schema cannot
  execute code — contracts are inert data read by weir's own parser —
  but it can grant WRONG CONFIDENCE, which for a check-before-effects
  language is the specific poison. Hence the posture: pinned by
  sha256 in a checked-in lockfile, vendored as reviewable source,
  fetched only by an explicit `weir add`/`weir restore`, and never during
  `check`, completion, or run.

- **`Secret` is a rendering marker, not memory protection** [D:secret].
  A `Secret` makes weir's own renderers refuse to print a value —
  `show` gives `***`, interpolation and the wire boundaries refuse,
  the REPL echo is safe. That is its entire enforcement surface, and
  the honest claim is narrow: **weir gives you a way to SAY a value is
  secret and then respects it consistently, at the boundaries weir
  owns.** It is NOT a secrets manager and NOT memory hardening. What
  it does NOT protect:
  - a secret the author `Secret.reveal`s and then prints, or splices
    into **argv** — visible in `ps` on many systems (allowed by
    design: `curl -H $auth` is the point; `ps` visibility is the
    platform's property, not weir's);
  - a secret in a command's OUTPUT, a response body, or anything
    written to a file — weir cannot know a `Completed`'s stdout holds
    one;
  - a secret carried in an **env var** — process inheritance and
    `/proc/<pid>/environ` make it visible to children and to anyone
    who can read the process, and `within env` propagates it to
    children by design (env is still THE standard CI secret channel,
    so `Env.load` with a `Secret` field is the main producer — the
    non-claim is about what the platform exposes, not a weakness in
    the type);
  - **the in-memory value itself** — a `Secret` wraps a plain string
    in a managed, GC'd heap: not zeroed, not pinned, not encrypted. A
    core dump, a debugger, or swap sees it. This is deliberate — see [D:secret] for why `SecureString`/zeroing was
    pre-refused (in short: `SecureString` is unencrypted off .NET
    Framework and Microsoft advise against it for new code, immutable
    strings cannot be zeroed, and the value must be plaintext at every
    use anyway);
  - **anything the author never marked.** `Secret` is OPT-IN: declare
    `GITHUB_TOKEN: string` and you get a plain string with no
    protection, and weir does not notice, warn, or infer. A heuristic
    on field names would be exactly the guessing this type replaces.

- **`Http` types your request; it does not vet your endpoint**
  [D:http]. `Http.send` fetches whatever URL it is given — including
  `localhost`, link-local, and cloud-metadata addresses. What it does
  NOT defend:
  - **SSRF is the caller's problem.** A script that takes a URL from
    `Args.load` and fetches it is an SSRF if it runs anywhere
    privileged; weir does not restrict the target. Same family as
    word-integrity-is-not-flag-safety: weir carries the value
    faithfully, it does not judge it.
  - **URL construction is the author's job.** There is no shell, so
    there is no injection to make unrepresentable — but
    `$"{base}/{userInput}"` can still escape a path with `../` or a
    query. `url |> Http.withQuery [(k, v)]` percent-encodes the QUERY
    half (a space or `&` cannot break the url); the PATH half stands —
    weir builds no path for you, and a `Http.url base [segments]`
    helper is parked.
  - **`insecure` disables TLS verification for ONE request** and is a
    loud opt-in [D:http-s2]. TLS verification is ON by default; a
    `Http.send { … with insecure = true }` accepts any certificate
    (self-signed clusters) — but ONLY that request, and the field is
    visible at the call site (a record field, never a subtle global or
    an env switch). `insecure = true` means "I have verified this
    endpoint some other way"; a `show` of the request displays it, so
    a review sees it. There is no way to disable verification globally,
    by design.
  - **`Secret` reaches the socket in the clear at `send`** — the one
    deliberate reveal (auth headers, `secretHeaders`), exactly
    analogous to argv. `show` masks it up to that point; the wire sees
    it. And a credential the author puts in a URL as a plain string is
    not a `Secret` at all — error messages redact userinfo
    (`user:pass@`), but a token in a query string is unprotected.
  - **TLS verification is ON** and there is no `insecure` in v1.
  - **`Http` shares the BCL `HttpClient` with the contracts fetch** —
    deliberate and stated: the contracts fetch has a fetch-only fence
    (never `check`, completion, or run), `Http.send` is an ordinary
    runtime builtin with no such fence, and neither runs during
    `check` or in the LSP (a runtime builtin is never evaluated
    there).

## What weir defends by design

These are properties of the language, verified the way the rest of
weir is verified — with pins, the fuzzer, and the F# oracle. See the
verification report in `dev/NOTES.md` (the "safe-by-design review" entry)
for the fixtures behind each claim.

### 1. Injection safety — a value is one argument, always

A spliced value contributes **exactly one** argv word; `$@xs`
contributes N words, each itself one word. There is no weir spelling
that turns a variable's contents into multiple arguments, a flag, or a
subcommand by accident. A value containing spaces, newlines, quotes,
glob characters, `;`, `&&`, or `$(...)` text arrives at the child
process byte-for-byte as a single argument.

This is architectural, not defensive coding: weir spawns via the argv
vector (`Proc.Spec` holds `Prog` + `Args`), never by building a shell
command line. The one `"/bin/sh" ["-c"; …]` call in the codebase is
the explicit `into` builtin (you asked for a shell); it is never on
the implicit path.

### 2. Resolution integrity — the source decides what runs

What a script runs is decided by its source and scope, not by ambient
`PATH`. A `let git = …` binding shadows a like-named binary; `^git` is
the only escape to `PATH`; the resolution a reader sees is the
resolution that happens. A hostile `git` planted earlier in `PATH`
does not change which binary a script with a `git` binding runs.
Check-time resolution matches run-time resolution.

### 3. Untrusted-text robustness — reject, don't misexecute

On any input the checker returns a located diagnostic rather than
silently mis-executing. Totality is patrolled by the fuzzer
(invariant 2, including an adversarial-depth axis) with an always-on
strict-span floor, and a parse-depth guard (limit 500) converts what
were three stack/time blowups — extreme nesting depth, very long
operator chains, deeply nested brackets — into located "expression
nested too deeply" diagnostics. No input crashes the process; that is
now a machine-checked invariant (unit `Depth guard` pins plus the
fuzzer's depth seeds), not a prose promise.

### 4. Deployment — one binary, no runtime

weir ships as a single ahead-of-time-compiled binary. The shipped
binary links exactly one third-party library:

- **FParsec** 1.1.1 — parser combinators (Simplified BSD license).

`FSharp.Compiler.Service` and `FsCheck` are **test-side only** (the
oracle and the fuzzer); they are not referenced by `src/Weir` and are
not in the shipped binary. Each published binary is stamped with the
source revision it was built from (`weir --version`), and the test
batteries refuse to run against a stale binary.

Secrets passed to a child via the env sigil (`!e(...)`/`$e(...)`) are not echoed: a
failed command's error renders only the program and its arguments, and
a `complete` record carries no environment field. A secret leaks only
if the script itself places it in argv or interpolates it — the
author's choice, not weir's default.

## Reporting a vulnerability

Report privately via **GitHub private vulnerability reporting** on
the repository (`github.com/weir-shell/weir` → Security → Report a
vulnerability), or by email to the maintainer
(`4584075+queil@users.noreply.github.com`). Please do not open a
public issue for a security problem before it is addressed.

## Provenance

weir is authored primarily by an AI agent working from human-blessed
plan documents, under a discipline that trades unusual authorship for
above-usual machine-checkable evidence: every boundary behavior is
pinned against the compiled binary (`ci/e2e.sh`), fidelity to F# is
refereed by the real F# compiler as an oracle (`tests/Weir.Fidelity`),
and grammar totality/soundness is patrolled by a metamorphic fuzzer
(`tests/Weir.Fuzz`). The development history is the `dev/plans/` directory,
one blessed plan per session. Read the evidence, not the byline.

## Signatures check your invocations, not your binaries

A command signature (`#sig tool`) constrains what weir ACCEPTS as a
command line — an unknown flag is caught at check time. It says
nothing about what runs: a hostile `tool` earlier on PATH still runs,
and `weir verify`'s version comparison fingerprints identity only as
far as `--version` output can (a wrapper that forwards `--version`
passes it). Same genre as word-integrity-is-not-flag-safety: the
guarantee is about the command line weir constructs, never about the
binary the OS resolves.
