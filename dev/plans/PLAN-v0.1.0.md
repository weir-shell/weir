# PLAN — the road to v0.1.0

Status: PARKED until v0.0.74 (the second bug hunt's fixes) is pushed
(user, 2026-10-14). The definition below is the coordinator's proposal and
is the first thing to confirm when this plan is picked up.

## What v0.1.0 means (to confirm)

**The first version we tell people to use.** Rough edges are expected; known
serious bugs are not. Concretely: no known high- or medium-severity bug, every
part of the surface is deliberate and documented, the docs match the binary,
and breaking changes from here on arrive with a migration note.

Not a stability promise — that is 1.0. Pre-1.0 breaking changes stay allowed
without shims; v0.1.0 only adds the notes (W5).

## Exit criteria

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
- `dev/BACKLOG.md` — each open item tagged "0.1.0" or "later".
"In" items get their own plans and land before the release.

### W5 — Breaking-change policy (small, alongside W1)
A DECISIONS row: from v0.1.0, every breaking change gets a `### Breaking`
CHANGELOG section entry with a one-line migration; still no shims pre-1.0.

### W6 — Regression automation (1 session)
- Run `tools/sweep-repro.weir` and `tools/hunt2-repro.weir` in CI (a
  Linux job; each must print all OK).
- Add the invariants hunt 2 found to `tools/fuzz.weir` / Weir.Fuzz: no
  "internal error" output, no exit code ≥ 128, `fmt` idempotent, `fmt` never
  changes whether a file checks, no line:col past the end of its line.

### W7 — Release
v0.1.0 notes that say what the version means; README/site wording ("first
version we recommend"); editor extensions' versions checked against the
runbook; then the usual runbook.

## Estimate

About 4–6 sessions of the current kind, if W3 converges in one or two rounds.
The swing factors are what W1 turns up and how much of W4 is "in".

## Memory constraint

Until the new host arrives: at most two heavy dotnet agents at a time with
the memory flags (no node reuse, `-m:1`, capped test heap), scratch and
binaries under /output/wt, never /tmp.
