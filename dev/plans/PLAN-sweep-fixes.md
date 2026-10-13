# weir — fixing the 2026-10-13 bug sweep

Status: APPROVED (user, 2026-10-13) — R1–R8, R10 blessed as recommended (R1
sharpened: fmt never re-spaces argv, no resolver check at all); R9 pending.

Source: a four-area black-box sweep (runtime, parser/fmt, checker, stdlib/CLI)
against HEAD 92789e3. Every item below was reproduced at least twice; the
high-severity ones were re-run independently. Scratch repros live in
`/tmp/probe-{run,parser,check,lib}/` (ephemeral — the minimal repro is inlined
per item so the plan stands alone).

Three stages, each one branch stacked on the previous, each green on its own:

| Stage | Branch | Theme | Items |
|---|---|---|---|
| 1 | `fix/sweep-silent-harm` | data loss, silent wrong results, soundness holes | B1–B10 |
| 2 | `fix/sweep-wrong-behavior` | wrong behavior that is loud or narrow | B11–B26 |
| 3 | `fix/sweep-diagnostics` | messages, raw .NET leaks, small inconsistencies | B27–B40 |

## Acceptance instrument (first commit of stage 1)

`tools/sweep-repro.weir --bin <path>` — one probe per item, labelled `B1`…`B40`,
each printing `REPRODUCES` or `OK`, plus controls for neighbouring behavior that
must not move. A stage is done when its probes print `OK` and every other probe
prints what it printed before — a fix that flips a neighbour is a
stop-and-report. Each fix also gets a pinned test (`tests/Weir.Tests` for
checker/builtin units, `ci/e2e.sh` for process/CLI behavior).

Stage gates: `dotnet test`, `ci/e2e.sh`, `ci/skill-doc.sh`, and `weir fmt` over
every repo `.weir` file is a byte-for-byte no-op.

## Rulings owed BEFORE implementation

Each carries a recommendation; the bless is the maintainer's. Rulings that
land get a `docs/DECISIONS.md` row.

- **R1 (B2) fmt and command lines.** RECOMMEND: fmt never re-spaces inside a
  command's argv words — the spacing check must agree under the *command*
  resolver, not "either". Rewriting argv is never a formatting concern.
- **R2 (B4) stdout closed by the reader.** RECOMMEND: treat EPIPE on weir's
  stdout like bash treats SIGPIPE — tear down the scope (children killed,
  `always` runs), exit 141, no diagnostic.
- **R3 (B5) integers past int32 in count/index positions.** RECOMMEND: one
  checked helper. Positional args (`Seq.item`, indexing, `Str.sub` start)
  raise "out of range"; count-like args (`take`, `truncate`, `replicate`,
  `padLeft`, `chunkBySize`) raise "too large" rather than saturate — silent
  saturation is the same class of bug.
- **R4 (B7) what `pure`/`readonly` refuse.** RECOMMEND: `cd` is a process-state
  write → refused by both. `pwd`, `Path.home*`, `Term.*`, `Color.*` are ambient
  reads → refused by `pure`, allowed by `readonly`.
- **R5 (B8) YAML null.** RECOMMEND: YAML 1.2 core schema — plain `null`,
  `Null`, `NULL`, `~` are null; quoted forms stay strings. A `string` field
  receiving null errors like a missing field.
- **R6 (B10) HOME.** RECOMMEND: `$HOME` verbatim when set (bash semantics,
  even if the dir is missing); `GetFolderPath` only when unset; empty result
  → `~` raises rather than expanding to `""`.
- **R7 (B11) signal teardown vs main thread.** RECOMMEND: the handler sets a
  cancellation flag and kills children; the *main* thread observes it at the
  next statement/wait boundary and unwinds through `always`. No user code
  runs after the signal.
- **R8 (B19) `Seq.groupBy` keys.** RECOMMEND: widen the runtime to any
  Eq key (reuse `countBy`'s structural key), matching the doc.
- **R9 (B28) `to json` escaping.** RECOMMEND: extend
  [D:json-relaxed-escaping] from the CLI to user `to json` / `--can --json`.
- **R10 (B36) deep `Seq.append`/`Seq.map` chains.** RECOMMEND: make the
  lazy-seq walk iterative per [D:eq-depth]; if not cheap, defer to backlog
  with a documented limit — it is the only item allowed to slip a stage.

---

## Stage 1 — silent harm (`fix/sweep-silent-harm`)

**B1 — `plan` + `Dir.delete` deletes a non-empty tree.**
`plan` / `Dir.delete "keep"` / `Plan.apply` removes `keep/sub/d`; direct
`Dir.delete` refuses. Same op confusion: plan `File.copy <dir>` copies
recursively, plan `Dir.move <file>` moves a file.
Fix: distinct ops (`DeleteDir` vs `DeleteTree`, file vs dir copy/move), and
`Plan.apply` performs exactly the direct call's checks. `Builtins.fs:4327-4362`.
Test: every plan op ≡ its direct call on the same fixture (property-style
table: empty dir, non-empty dir, file, missing).

**B2 — `fmt` rewrites command arguments.** `chmod u+x f` → `chmod u + x f`,
`echo a+b` → `echo a + b`, `c&&d` → `c && d`. Fix per R1. `Fmt.fs:442,487`.
Test: fmt round-trip asserts identical *parsed argv* (not just same shape),
fixtures for `+ - * / && || = :` inside words.

**B3 — producer error piped into a command is swallowed.**
`[1..10] |> Seq.map (fun i -> if i == 3 then fail "boom" else $"{i}") | sha256sum | line`
→ hash of `1\n2\n`, exit 0. `exit 5` in the producer is lost too.
Fix: the stdin-writer task catches only broken-pipe IO; any other exception
(and `exit`) is captured and re-raised on the main thread after the child is
reaped, at the producer's position. `Proc.fs:141-153`.
Test: `| sha256sum`, `| complete`, `| cat`, `exit 5` producer; control: a
child that closes stdin early (`| head -1`) still succeeds.

**B4 — `weir s.weir | head -1` never stops** (endless child spins at 100% CPU,
finite scripts run to completion). Fix per R2: detect write failure on
stdout (the .NET Unix console stream drops EPIPE — write via a raw fd-1
stream that surfaces it). `Session.fs:299` comment is false today.
Test: e2e `yes` and a 2M-line print loop under `| head -1` return in <2s,
exit 141, `always` ran.

**B5 — integer args truncated to int32.** `Seq.item 4294967296 [10;20]` → `10`;
`[10;20][4294967297]` → `20`; `Seq.take`, `Str.sub`, `Str.replicate`,
`Str.padLeft`, slicing likewise; `Seq.chunkBySize` leaks raw .NET. Fix per R3:
one `toIntArg name n` helper replacing every `int n` on user input
(`Builtins.fs` 151, 279, 1057, 1564, 1665, 1803 + `Str.sub`/`padLeft`/slice).
Test: a grep-gate in CI for bare `int ` casts on builtin arguments, plus a
table test at 2^31, 2^32, 2^32+1, -1.

**B6 — non-exhaustive match passes check on an unresolved payload.**
```
let f o =
    match o with
    | Some 0 -> "zero"
    | None -> "none"
print (f (Some 5))
```
check rc 0; run: "unreachable: the checker guarantees totality". Same with
`Some (Some n)`, `Ok (Some n)`, record/`[]` payloads, user generic unions.
Fix: pass `finalTy` (deep resolve) to `exhaustive`; in `missingCases` a
type-variable payload with a refutable sub-pattern is *not* covered
(`Check.fs:4503`, ~`:2364`). Test: each variant above is a check error.

**B7 — `pure`/`readonly` accept effects.** `let pure go d = cd d`,
`let pure here () = pwd`, `readonly` + `cd` all check clean. Fix per R4:
classify `cd`/`pwd`, `Path.home`/`configHome`, `Term`, `Color`
(`Effects.fs:46-75`). Test: each name under `pure` and `readonly`.

**B8 — YAML `null`/`~` read as strings.** `a: null` into `Option<string>` →
`Some "null"`; `{ ys = [Some 1; None] } |> to yaml |> from yaml T` fails.
Fix per R5 in `scalarCore` (`Yaml.fs:154-252`). Test: `to yaml |> from yaml`
round-trip property over Option/list/record shapes.

**B9 — mis-indented YAML lines dropped.** `b:\n  c: 2\n d: 3\ne: 5` →
`{b={c=2}; e=5}`. Fix: a line indented between the parent and child level
is an error, not an early return (`Yaml.fs:773`). Test: the PyYAML-rejected
inputs error with the line number.

**B10 — `~`/`Path.home` empty when HOME is a missing dir.**
`HOME=/nonexistent` → `echo ~/x` prints `/x`; `Path.configHome ()` →
relative `.config`. Fix per R6 (`Builtins.fs:2576`). Test: e2e with
HOME unset, set-missing, set-empty.

## Stage 2 — wrong behavior (`fix/sweep-wrong-behavior`)

Runtime / process
- **B11 — code runs after SIGTERM.** `within proc srv = sh -c "sleep 100"` /
  `Proc.wait srv` / print / `File.write` → prints `wait returned 137`, marker
  written 1/3 runs. Fix per R7 (`Session.fs:375-381`).
- **B12 — `Seq.pmap`/`piter`/`pfirst` worker errors reported at 1:1.** Attach
  the worker's `RunPos` as blame when marshalling the exception
  (`Eval.fs:22-28`, `Script.fs` `runtimeErrorAt`).
- **B13 — `Self.prompt` swallows stdin meant for later children.**
  `Console.In.ReadLine()` buffers ahead; read fd 0 unbuffered up to `\n`
  (`Builtins.fs:5163`). Test: `printf 'a\nb\nc\n' | weir s.weir` → `cat`
  sees `b c`, both pipe and regular-file stdin.

Parser / fmt
- **B14 — `fmt` crashes on a comment outdented within a heredoc/yaml block**
  (`count = -4`, `Fmt.fs:250`). Clamp and keep the comment's relative indent.
- **B15 — `fmt` moves a `///` line into heredoc content**, changing output.
  The doc canonicalizer must skip every line inside a heredoc/yaml block
  (`Fmt.fs:34-52`).
- **B16 — comment scanner ignores interpolation holes.**
  `print $"{"a" + " // x"}"` fails; `print $"{"\""}" // c` keeps the comment.
  Give `stringScan` hole nesting (`Script.fs:15`; shared by `inStringMask`,
  `endsInsideString`).
- **B17 — malformed `${…}` in `$$<<<` becomes literal text** (`v=${1 +}`,
  unclosed `${name`). Commit after `${` — a parse error, not fallback
  (`Parser.fs:3437`).
- **B18 — `|>` after a multi-line `match` closed inside parens rejected**;
  the arm group outlives the closing paren (`Script.fs` ~1940-1956).
  Also give that error a column.

Checker
- **B19 — `Seq.groupBy` non-scalar keys pass check, fail at runtime.** Per R8
  (`Builtins.fs:1131`, `:1978`).
- **B20 — over-application through a returned function rejected.**
  `id (fun x -> x + 1) 2`, `(snd t) 5`, `Map.get "k" handlers x`. In
  `checkSpine`, when args remain after the declared arity, unify the result
  with a fresh arrow and continue (`Check.fs:4589-4640`).
- **B21 — tuple pattern on an unresolved type rejected.**
  `let f = function | (a, b) -> a + b`. Bind the scrutinee to a fresh tuple
  type of the pattern's arity (`Check.fs:2085-2118`).
- **B22 — `plan` command refusal not enforced through helpers.**
  `let mk () = touch /tmp/x | complete` called in `plan` checks clean, fails
  at run. Use the effect summary of the callee, as `pure` does.
- **B23 — types a module gets from its own imports are unusable by importers**
  (`p.x` "unknown type 'Pt'", can't show, can't compare). Export the
  transitive type environment for types reachable from exported signatures.
- **B24 — unit-suffixed literals overflow silently** (`9999999999TiB`,
  `9999999999999999h`). Checked multiply → parse error at the literal
  (`Parser.fs:682-690`).

Stdlib / data
- **B25 — quoted YAML keys skip escape handling;** `to yaml` writes `"": v`
  that the reader rejects. Unescape in `splitKey` (`Yaml.fs:310-315`) and
  round-trip test keys with `\t \n " ""`.
- **B26 — `Dir.copy` follows symlinks and recurses into itself.** Copy links
  as links, refuse a destination inside the source, dangling links copied as
  links (`Builtins.fs:3529-3537`). Also `Instant.parse`: accept >7 fractional
  digits (truncate to ticks) and a space separator, so TOML offset datetimes
  read (`Types.fs:773-778`) — rides here as the same "valid input refused" class.

## Stage 3 — diagnostics and small inconsistencies (`fix/sweep-diagnostics`)

Data / CLI
- **B27 — `from jsonl` fails on a blank line** (trailing newline). Skip empty lines.
- **B28 — `to json` escapes non-ASCII and HTML characters.** Per R9
  (`Eval.fs:1147`, `Can.fs:609`).
- **B29 — `inferShape` drafts `int` for integers beyond int64.** Draft `float`
  or refuse with a note.
- **B30 — JSON error paths number array elements from 1** (`b[2]` for the
  2nd element). Zero-based (`Eval.fs:1372`).
- **B31 — `Args.load`:** `--name=x` error should teach the space form (or
  accept `=`); a `help: bool` field is a check error ("reserved").
- **B32 — `weir fmt --check` with no script** says "no such script: --check";
  unknown flags get a usage error.
- **B33 — `File.exists` true on a dangling symlink then `File.read` leaks .NET;
  `File.size`/`File.delete` on a directory say "no such file".** Precise
  messages ("dangling symlink", "is a directory").

Messages / raw .NET leaks
- **B34 — `xs[^n]` out of range always says "index 0".** Report the original
  `^n` and the length.
- **B35 — raw .NET messages:** Duration/Size overflow (`1h * 9223372036854775807`),
  `Duration.sleep` > 24.8 days, Instant ± Duration past range,
  `Instant.ofEpochMs`, `Str.replace ""`, `Str.replicate 1000000000`. One
  wrapper per family with weir wording.
- **B36 — deep `Seq.append`/`Seq.map` chains overflow the stack** (rc 134,
  `always` skipped). Per R10.
- **B37 — misleading parse messages:** `0x10` / `1_000` → "not a function";
  `1.5.2` → "need a digit before the point"; unit-suffix alternatives leak
  into "Expecting" lists; heredoc/yaml splice errors truncated to the last
  FParsec line (`Parser.fs:2881` — keep the whole Expecting list).
- **B38 — misleading checker messages:** `Some 1 < None` reports a type
  mismatch instead of "'<' not defined for Option" (`Check.fs:1209`);
  Show/Ord leftovers called "equality" requirements (`Check.fs:5503/5550/5955`
  — use `p.Cls`); budget error says "annotate" (`Check.fs:5436`); built-in
  opaque types (`Proc`, `YamlPatch`) get "functions never render" /
  "unknown type"; a failed import adds a spurious "no module named" listed
  first.
- **B39 — heredoc tab rule only on the first content line**
  (`Script.fs:1527-1531`) — apply uniformly (decide: refuse everywhere, per
  "tabs never indent").
- **B40 — a `plan` body ending in a non-unit value silently discards it.**
  Apply the discard rule (`Check.fs:2621`, `:4957`).

## Not in scope — triage list (suspected, unconfirmed, or possibly by design)

Each gets a one-line ruling (by design → DECISIONS row; bug → BACKLOG):
UTF-16 `Str.length`/`sub` splitting astral chars (and `from table` widths);
`exit 256` → 0; `-9223372036854775808` literal unwritable; `Seq.take -1`
silent vs `replicate -1` raising; `Env.get` inside `within env`;
`kill -TERM <pid>` (not group) orphaning a foreground child; module
`let v = helper ()` running a command at import; script-declared `Ok` case
shadowing breaks `Error` arm; `to json` in a generic helper defaulting to
`seq<string>`; `from json` on a `Secret` field using write-side wording;
>500-term flat `+`/`|>` chains "nested too deeply"; `@"…"` / `"""…"""` in
argv refused as concatenation; `Path.glob "**/"` returning files; `-0.0`
not preserved; YAML `f: a: b` accepted / `-   x:` and `...` refused;
`from xml` undefined entity dropped, first-of-duplicates; regex with no
backtracking timeout; outdented `//` inside heredoc silently dropped;
`/// doc` misalignment is a check error but `run` executes.

## Delivery

- One commit per item (or per tightly coupled pair), subject prefixed via
  `ci/commit-area.weir`; CHANGELOG entry per user-visible fix.
- Stage branches stacked: 2 on 1, 3 on 2; each handed off checked out in
  `/output/weir` when green. Commit only on ask.
