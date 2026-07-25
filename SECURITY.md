# Security

weir is a typed shell: it parses and runs `.weir` scripts. This file
states what weir defends **by design**, the one thing it deliberately
does **not** defend, and how to report a problem.

## Scope — the one line that matters

**weir is not a sandbox.** Running a `.weir` script is exactly as
trusted as running a bash script: the script can do anything your user
account can do. "Untrusted input" here means malformed or hostile
*script text* handed to the parser/checker — not hostile *code* you
have chosen to run. weir's job on hostile text is to reject it with a
diagnostic, never to execute it safely. Do not run a `.weir` file you
would not run as a shell script.

## What weir defends by design

These are properties of the language, verified the way the rest of
weir is verified — with pins, the fuzzer, and the F# oracle. See the
verification report in `NOTES.md` (the "safe-by-design review" entry)
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

Secrets passed to a child via `runEnv`/`cmdEnv` are not echoed: a
failed command's error renders only the program and its arguments, and
a `complete` record carries no environment field. A secret leaks only
if the script itself places it in argv or interpolates it — the
author's choice, not weir's default.

## Reporting a vulnerability

Report privately via a **confidential issue** on the GitLab project
(`gitlab.com/arquidevio/weir` → New issue → check "This issue is
confidential"), or by email to the maintainer
(`5586272-queil@users.noreply.gitlab.com`). Please do not open a
public issue for a security problem before it is addressed.

## Provenance

weir is authored primarily by an AI agent working from human-blessed
plan documents, under a discipline that trades unusual authorship for
above-usual machine-checkable evidence: every boundary behavior is
pinned against the compiled binary (`ci/e2e.sh`), fidelity to F# is
refereed by the real F# compiler as an oracle (`tests/Weir.Fidelity`),
and grammar totality/soundness is patrolled by a metamorphic fuzzer
(`tests/Weir.Fuzz`). The development history is the `plans/` directory,
one blessed plan per session. Read the evidence, not the byline.
