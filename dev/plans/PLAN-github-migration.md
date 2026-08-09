# weir — migration to GitHub (weir-shell org) + the CI matrix

Status: BLESSED (user 2026-07-30). One session, possibly two (the
move and the matrix are separable — the report says which landed).
Destination decided: **`github.com/weir-shell/weir`**, straight to
the org, not incubated under a personal namespace.

## Why now, and why the org

**The CI matrix is the unlock.** Free macOS and Windows runners turn
two currently-STATED blockers into solved ones:
- the **GNU-ism sweep** (flagged after the macOS receipts, explicitly
  un-sweepable without macOS CI, because fixing them blind produces a
  corpus that passes only on the machine that wrote it — the
  masked-failure shape);
- the **Windows spike's** verdict becomes CI-backed rather than
  one-machine-verified.

**Straight to the org**, because moving later is asymmetrically
expensive: the tree-sitter split needs a public home
(`weir-shell/tree-sitter-weir`) and is a hard prerequisite for Zed
publishing — and **the Zed grammar URL is pinned by full SHA inside a
published extension manifest**, so a later namespace move means
re-publishing. Repo name: `weir` (bare; `weir-shell/weir` is what
people will guess).

**VERIFY FIRST, do not trust the advisor**: free macOS/Windows runner
minutes are (as far as I know) for PUBLIC repos; private repos meter
with multipliers, macOS steeply. **Check GitHub's current billing
docs before planning around limits** — my knowledge has a cutoff. If
the free tier requires public, then the CI unlock and the publication
decision arrive together, which is a coupling worth knowing
deliberately rather than discovering.

## Movement 1 — the move

- Push to `weir-shell/weir`; decide and state the Codeberg
  disposition (archive with a pointer / mirror / delete — archive
  with a README pointer is the low-regret choice).
- **The URL tail** — grep and update:
  - GitLab/Codeberg commit URLs embedded in NOTES and DECISIONS
    (there are several — the `3ebdeff3`-style links);
  - `plans/README.md` and any plan-file references;
  - the VS Code extension's `repository` field (it was added for
    vsce; it must point at the new home);
  - `/arquidevio/weir` paths anywhere in docs, SECURITY.md's
    reporting channel, README;
  - the fuzz/tools headers that cite repo paths.
  Report the hit count per surface (the denominator rule).
- **SECURITY.md's reporting channel** must be real at the new home
  (GitHub private vulnerability reporting, or a stated email) — a
  security file with a dead channel is worse than none.

## Movement 2 — the CI matrix (the actual deliverable)

Port `.gitlab-ci.yml` to workflows, and **grow the matrix while
doing it**:

| job | linux-x64 | macos-arm64 | windows-x64 |
|---|---|---|---|
| build + AOT publish | yes | yes | **spike-gated** |
| unit suite | yes | yes | spike-gated |
| e2e | yes | **yes — the point** | spike-gated |
| doc-tests (skill-doc) | yes | yes | spike-gated |
| oracle (dotnet test) | yes | — | — |
| fuzz deep run | yes (scheduled) | — | — |
| timing | yes | note the medians | — |

- **The six-consumer freshness gate must work on every runner** —
  `ci/check-fresh.sh` is shell; macOS is fine, Windows needs an arm
  or those jobs stay Linux/macOS until the spike says otherwise.
  Do NOT let a runner skip the gate silently — that is the uneven-gate
  finding all over again, and this time across platforms.
- **The macOS arm is the immediate value**: run the e2e battery and
  let it FAIL. Every GNU-ism it catches is a finding to fix with a
  real signal, which is what the sweep needed. **Expect red on the
  first run and treat that as the deliverable**, not as a problem —
  the flagged class becoming visible IS the point.
- Windows jobs are **added only if the spike's verdict says the
  runtime builds and runs** — otherwise the matrix has a stated
  Windows gap, not a permanently-red job (a red job nobody can fix
  trains people to ignore CI, the worst outcome).
- **Scheduled vs push**: the fuzz deep run is scheduled (it is 5m47s
  at count=10000 and holds the deep-lock); everything else on push.
  Keep the deep-lock semantics — a scheduled run must not race a
  publish.

## Movement 3 — the tree-sitter split (if it rides here)

`weir-shell/tree-sitter-weir` as its own repo, since the org now
exists and Zed's registry clones anonymously from a repo ROOT (the
finding that hardened this from "preferred" to "prerequisite").
Either do it here or state it as the next session — but do NOT leave
the grammar in a subdirectory and then publish to Zed.

## Bars

- **Zero behavior change** — this is infrastructure. The suite that
  passed on Codeberg passes on GitHub, on Linux, unchanged.
- **Every job's gate is present** (the uneven-gate lesson, now
  per-platform).
- **No red-and-unfixable jobs** — a platform either has a working
  matrix arm or a stated gap.
- The URL sweep reports its denominator per surface.
- SECURITY.md's channel is live at the new home.

## Work items

1. Verify the runner/billing terms; state the public-vs-private
   coupling.
2. The move; the URL sweep with counts; the Codeberg disposition;
   SECURITY's channel.
3. Workflows: the Linux jobs at parity with today (green first),
   then the macOS arm.
4. Triage the macOS reds as findings (sized, fixed if mechanical —
   the `wc -l`/`ps -C` class); this is the GNU-ism sweep finally
   running with a real signal.
5. Windows arm per the spike's verdict, or the stated gap.
6. The tree-sitter split, or its statement as next.
7. Report: the matrix as it stands, the macOS findings, the URL
   counts, what is deliberately not covered.

**Done when:** the repo lives at `weir-shell/weir` with every URL
updated and counted; Linux CI is at parity and green; the macOS arm
runs the e2e battery with its findings triaged; the freshness gate is
present on every job; Windows is either matrixed or a stated gap; the
tree-sitter split is done or scheduled; SECURITY's channel is live.
