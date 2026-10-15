# PLAN — daily driver: a day of use without a crash or surprise

Status: READY — v0.0.74 landed (2026-10-15); not started. Definition confirmed by the user (below).

## The goal (user, 2026-10-14)

**A weir a human can use extensively for a day without a crash or
unintended behaviour.** Not tied to a version number: no release is due
when this plan is done — the user releases when they choose. The test is usage, not a feature list: a full day
of real work — writing and running scripts, the REPL, an editor on the LSP,
automation in CI — with no crash and nothing that surprises. "Unintended
behaviour" includes the doc saying one thing and the binary doing another.

Not a stability promise. Breaking changes stay allowed without shims; W5
only adds migration notes.

## Exit criteria

0. **The day test (W8) passes:** simulated day-long sessions, then a real
   day of the user's own work, end with zero crashes and zero unintended
   behaviours (anything surprising is a finding until ruled intended).
1. A bug hunt over the targeted areas (W3) reports no high- or
   medium-severity findings.
2. Every builtin is documented in SKILL.md (and the reference) or deleted.
3. The docs-vs-binary audit finds no drift in prose claims.
4. Every parked design is decided in or out, and the "in" ones have landed.
5. The repro harnesses and the new fuzz invariants run in CI.
6. A DECISIONS row and a CHANGELOG convention for breaking changes exist.

## Workstreams, in order

### W1 — Surface audit: every builtin documented or deleted (1 session)
`into` showed the builtin table can hold things nobody remembers. Diff the
builtin/module member tables (`Builtins.fs` member lists, `moduleTable`,
reserved names, reifier desugar targets) against SKILL.md, docs/ and
`site/src/data/reference.json`. Each undocumented member: document it or
delete it (a user ruling per item, batched in one table). Each documented
member that doesn't exist: fix the doc. Deliverable: a CI check that fails on
an undocumented public builtin, so the gap cannot reopen.

### W2 — Docs vs binary (same session as W1, or the next)
`ci/skill-doc.sh` checks fenced blocks only; prose claims drift (hunt 2 found
several). Walk SKILL.md and GUIDE.md section by section; every concrete claim
gets a probe. Mismatches become "binary wrong" fixes or "doc wrong" edits.
Probes worth keeping go into the e2e battery.

### W3 — Bug hunts until convergence (1–2 rounds)
Report medium and above only; lows go straight to the backlog. Target areas
not yet swept:
- Windows and macOS behavior (every hunt so far ran on Linux; the macOS
  `timeout` polyfill bug hid there) — via CI runs of targeted probes;
- TOML, `plan`/`Plan.*` in depth, `weir add schema` / `gen types` beyond
  cycles;
- concurrency under stress: `pmap` + `within lock` + `within proc` +
  signals together;
- performance cliffs (the two found so far were quadratic: positions and
  wide tables) — inputs scaled 10×/100×, timed;
- the trust boundaries, security-minded (argv, env, HTTP, contracts,
  module loading, tty output).
Swept areas are re-probed only for regressions from the latest changes. The
loop stops at the first round with no high/medium findings (exit criterion 1).

### W4 — Parked designs: decide in or out (1 session of rulings)
- Env sugar (`dev/plans/PLAN-env-sugar.md`) — ready to execute.
- `within … always` any + `!()` removal (`feat/within-always-any`,
  `dx/retire-bang`) — check what's left against main, then rule.
- Syntax DX batch (`dx/*` branches) — verify everything landed; delete
  merged branches.
- `dev/BACKLOG.md` — each open item tagged "daily-driver" or "later".
"In" items get their own plans and land before the release.

### W5 — Breaking-change policy (small, alongside W1)
A DECISIONS row: from the point the user picks, every breaking change gets a `### Breaking`
CHANGELOG section entry with a one-line migration; still no shims pre-1.0.

### W6 — Regression automation (1 session)
- Run `tools/sweep-repro.weir` and `tools/hunt2-repro.weir` in CI (a
  Linux job; each must print all OK). First harden their free-port
  picking: H14's probe once lost its port between pick and bind
  (`Address already in use`).
- Make the local gate match CI: run the unit suite in Debug (CI's
  configuration — Release hid the Debug-frame stack overflow) and run
  `tools/dead-privates.weir` (H1 left `seqStr` behind; only CI caught it).
- Pin down the Linux-CI-only fuzz failure at `--seed 4242 --count 10`
  (one property of 46; passes locally) — the e2e cell now prints the
  failing property and its input.
- Add the invariants hunt 2 found to `tools/fuzz.weir` / Weir.Fuzz: no
  "internal error" output, no exit code ≥ 128, `fmt` idempotent, `fmt` never
  changes whether a file checks, no line:col past the end of its line.

### W0 — The bash-habit sweep (first in W8; 1–2 sessions)
The user's real REPL-as-shell use shows the biggest class of "unintended
behaviour" is bash muscle memory: most recent fixes were reflexes (`~`,
`NAME=v cmd`, `&&`/`||`, `!=`, `else` ending a command, unknown heads).
Rule: **every common interactive idiom either works or teaches — nothing
falls through silently.** A probe of 19 idioms on main @ 16cc7c6 found:

| typed | today | class |
|---|---|---|
| `echo hi; echo yo` | prints `hi; echo yo` | silent wrong |
| `sleep 1 &` | passes `&` to sleep | silent wrong |
| `echo hi 2>&1` | literal arg (`>` alone warns) | silent wrong |
| `cat <<< hi` | literal args | silent wrong |
| `[ -f x ] && echo y` | "unbound 'f'. Did you mean 'cd'?" | misleading |
| `ls *.txt` | raw "Expecting: …" | no teach (Path.glob) |
| `echo $?` | raw "Expecting: identifier" | no teach (`\| exitCode`) |
| `for f in a b; do …; done` | raw "Expecting: …" | no teach (`for … do`) |
| `echo $HOME` | "unbound variable 'HOME'" | no teach (Env.get / `~`) |
| `cd -` | "no such directory …/-" | support or teach (ruling) |
| `export` / `source` / `pushd` | "unknown command … PATH" | teach (within env, Env.fromFile/import, within cd) |
| bare `exit` in the REPL | prints `int -> 'a` | quit or teach (ruling) |

Already right: `>` warns toward `File.write`; `which`, `cmd | grep` work.
Work: extend the probe to a catalog (~50 idioms: job control `&`/`fg`/
`jobs`, `!!`/`!$`, `$(…)` vs backticks, `[[ ]]`, `test`, `$((…))`,
`set -e`, `trap`, `alias x=…`, `unset`, `read`, `printf -v`, `2>/dev/null`,
`|&`, `&>`, brace expansion, `~user`, `cd` with no arg, `..` as a command,
Ctrl-R) run in both script and REPL mode; rulings batched in one table
(support vs teach per idiom); every row pinned in e2e so a future change
can't reopen a silent fall-through.

### W8 — The day test (the gate; 1–2 sessions, repeated until clean)
Usage-shaped, not area-shaped — the hunts find bugs by attacking; this finds
what a working day hits. Agents each run a scripted "day" end to end,
logging every crash, internal error, hang, misleading message and surprise:
- port three real bash scripts of different shapes to weir (a CI job, a
  deploy helper, a data-wrangling one-off) and run them;
- **the REPL as a login shell for a whole day** — the user already does
  this in containers, so it is the primary scenario: navigation (`cd`,
  `~`, completion), long-running and interactive children, Ctrl-C/Ctrl-D,
  history across sessions, aliases, pasting multi-line text, mistakes and
  recovery, `#save` of what worked;
- an editor session over the LSP: open/edit/format/hover/complete for an
  hour of edits on a growing file, multi-file with imports;
- operations: `within` scopes, signals (Ctrl-C mid-run), pipes, `serve` +
  `Http` talking to each other, a `plan` reviewed then applied;
- install-to-first-script from the release assets on a clean machine.
Every finding is fixed or ruled intended (DECISIONS) before the next day.
The user's own REPL-as-shell use is the running real-world day: whatever it
hits is a W8 finding as it happens, and the release gate is a clean stretch
of it on a build from main.

### W7 — Afterwards (no version implied)
When the exit criteria hold, report it. Whether and when that becomes a
named release is the user's call; nothing in this plan triggers one.

## Estimate

About 4–6 sessions of the current kind, if W3 converges in one or two rounds.
The swing factors are what W1 turns up and how much of W4 is "in".

## Memory constraint

Until the new host arrives: at most two heavy dotnet agents at a time with
the memory flags (no node reuse, `-m:1`, `DOTNET_GCHeapHardLimit=3GB` on
every .NET process — that cap is what stopped the OOM restarts), scratch and
binaries under /output/wt, never /tmp.
