# weir — adversarial review of the upgrade story

Status: EXECUTED (findings-shaped; not blessed, nothing fixed). Third in the
series after the security and DX reviews, aimed at the surface neither
touched: what happens to a working script when the binary underneath it
changes.

    weir tools/canary-corpus.weir --bin ./new/weir --against ./old/weir

The corpus exits nonzero when any canary lands in `silent`.

THIS REVIEW DOES NOT ARGUE FOR STABILITY. Pre-0.1.0 is exactly when breaking
changes should be taken freely, and this project has been right to take them.
The question is not whether the language changes — it is whether a user can
tell that it did. A change that makes a checking script STOP checking honours
"nothing runs until everything checks": the user is told, before anything
runs. A change that leaves a script checking clean while it DOES SOMETHING
DIFFERENT satisfies the letter and violates the spirit. That second class is
the risk surface, and nothing patrols it today.

## Phase 0 — the inventory, and two corrections to this plan's premises

Answered from the code and the repo, not from memory. Two of the plan's own
assumptions did not survive.

**1. Version surface.** `weir --version` prints a bare git hash (`e961984`) —
no semver, no tag, nothing ordered. Nothing else carries a version:
`check --json` emits a flat diagnostic array with no envelope, and the LSP's
`initialize` returns `serverInfo: {"name": "weir"}` with **no `version`
field**, though the LSP spec provides one. So an editor cannot display or
compare the language server's version, and a `--json` consumer cannot record
which weir produced a diagnostic.

**2. Required-version directive.** None. The directive set is `#loose` and
`#sig`. As expected — whether one is wanted is a Phase 2 question, below.

**3. Skew.** Nothing detects or reports CLI/LSP disagreement. `ci/check-fresh.sh`
gates the build stamp against git HEAD, but its own header scopes it to the
test harness ("every binary consumer runs this") — it is a developer-side
masked-failure guard, not a user-facing skew check. An old LSP against a new
file, or the likelier reverse, produces no signal at either end.

**4. The install path — CORRECTION, the plan had this backwards.** The plan
says "the install script is generated per release and installs a pinned
version". It is not generated, and it is not pinned. `install.sh` is checked
in and resolves the tag **at runtime**:

    tag=$(curl -fsSL ".../releases/latest" | grep -m1 '"tag_name"' | ...)

So `curl … | sh` is ROLLING: it installs whatever is latest at that moment,
and re-running it is the upgrade command. There is no pinned form, and no
"you are N versions behind" signal anywhere in `install.sh`, `README.md` or
`docs/INSTALL.md`.

**5. And there is nothing to install — the whole upgrade surface is
unexercised.** Verified against the public API rather than inferred: **zero
releases, zero tags**, and the `releases/latest` endpoint `install.sh` calls
returns **HTTP 404**. The README's headline install command cannot succeed
today; it exits at `could not resolve the latest release tag`.

This is KNOWN AND DELIBERATE, not an oversight — `[D:launch-hygiene]` records
"the site is item 2, GATED on the first tag — the install command is its most
valuable line and no release exists yet", and `[D:releases]` makes a `v*` tag
produce a DRAFT release for human review. Two consequences worth stating
plainly rather than treating as a defect:

- No user has ever upgraded weir, because no user has ever installed a
  release. Every finding below is PRE-EMPTIVE. That is the best time to do
  this review and the worst time to trust its priorities.
- `install.sh` calls `releases/latest`, which GitHub **excludes drafts from**.
  With `[D:releases]` producing drafts, the first `v*` tag will not by itself
  make the install command work — it works when a human publishes the draft.
  That coupling is not written down anywhere the install path can see.

**6. Does anything assert output? — CONFIRMED, with a counterweight the plan
did not have.** `ci/skill-doc.sh` is exactly as the plan supposed: its header
says every fenced block "must run clean", and the code captures output only to
PRINT it on failure (`if ! out=$(… "$BIN" "$file" …)` / `echo "$out" >&2`). It
never compares. It covers `SKILL.md`, `GUIDE.md` and `COMING-FROM.md` — the
project's largest body of executable examples, pinned for EXECUTABILITY and
not for behaviour.

But the project is not unpinned overall, and the review would be dishonest to
imply it:

| harness | count | strength |
|---|---|---|
| `tests/Weir.Tests/Tests.fs` | 822 `Expect.equal` | equality — strong |
| `ci/e2e.sh` | 218 `expect` sites | `grep -qF` — SUBSTRING containment, not equality |
| `ci/skill-doc.sh` | 0 | executability only |

The middle row is the one to notice: `expect` asserts a NEEDLE IS PRESENT, so
a change that alters surrounding output while preserving the needle passes.
That is weaker than the diff I2 performs, which is why I2 found something 218
`expect`s did not.

## I1 — the loud/silent census

Window: the last 60 `[D:]` rows (of 253). No tags exist, so the plan's tag
window was unavailable.

CLASSIFICATION RULE, stated so the count is auditable: *additive* = new
surface only, no existing valid script changes meaning; *loud* = an existing
valid script now fails to check; *silent* = an existing valid script still
checks and behaves differently; *unknown* = the row does not say.

| bucket | count |
|---|---|
| additive | 48 |
| loud | 4 |
| silent | 4 |
| unknown | 4 |

LOUD (4) — the user is told at check time: `[D:yaml-seq]` (multi-document
streams retire, with a teaching that names the split); `[D:no-named-groups]`
(`(?<x>…)` was in by accident and now rejects); `[D:bare-partition]` (a
two-home name becomes qualified-only on both sides, demoting names that
previously resolved); `[D:instant]` (the `FileRow.age` → `modified` rename —
commit `d47e9bb` recovers it explicitly).

SILENT (4) — checks clean before and after, behaves differently:

- `[D:ls-truth]` — `ls` previously omitted directories (`GetFiles()`
  semantics). A script's `ls |> Seq.length` returns a different number.
- `[D:ls-sort]` — `ls` rows became sorted ordinal. `ls |> Seq.head` returns a
  DIFFERENT FILE. If the script then deletes or overwrites it, the upgrade
  changed which file was destroyed.
- `[D:lf-output]` — captured output is LF on every platform. On Windows a
  previously-CRLF capture now compares differently against a literal.
- `[D:filerow-size]` — `FileRow.bytes` became a `Size`. MIXED: loud for
  arithmetic against an int, silent for rendering (`$"{f.bytes}"` moved from
  `3000` to `2.9 KiB`). Counted silent because the silent half is the risk.

UNKNOWN (4): `[D:record-keys]`, `[D:bare-rule]`, `[D:echo-once]`,
`[D:surface-capture]` — rows whose text does not say whether an existing
script's behaviour moved. Per the plan this is itself a finding: **a ledger
row that does not state whether it broke anything cannot be audited at
upgrade time.** Four in sixty is a low rate, and the fix is a habit, not
machinery — a row that changes behaviour says so in a clause.

## I2 — the canary corpus

24 canaries over documented surfaces, run under `e961984` (2026-08-15) and
`55131fb` (2026-08-09), a one-week window.

    same 22 / additive 1 / loud 0 / silent 1 / both-error 0   (n=24)

THE SILENT ONE, and it confirms two ledger rows at once:

    ls-count-and-order: SILENT
        old: 2 | a.txt | B.txt
        new: 3 | B.txt | a.txt | sub

Both `[D:ls-truth]` (the directory now counted) and `[D:ls-sort]` (ordinal
order, uppercase first) land in a single three-line script that checks clean
on both builds. `ls |> Seq.head` is a different file after the upgrade.

Everything else held: record field order at all three renderers (`show`,
`to json`, `to yaml`) is unchanged across the window, as are `Size`/`Duration`/
float rendering, the seq ordering members, sha256/base64, `Secret` masking,
`complete`'s record, path functions, the `Regex` binder, and json round-trip.

### The instrument defect this corpus found in itself

Recorded because it invalidates the obvious way to build this tool, and
because the first run reported **0 silent** — a clean bill of health that was
an artifact.

**A canary must be written in the language the OLD build understands.** The
first `ls` canary read `f.isDirectory`, a field added mid-window. On the old
binary that is a check error, so the canary bucketed `additive` — and a canary
in `additive` can never report `silent`. It had silently stopped measuring,
on the exact surface it was written for, while reporting a bucket that reads
like a result.

The rule is now in the tool's header: when a canary turns `additive`, check
whether that is the finding or the instrument giving up. This is the same
genus as `#loose`'s vacuous e2e fixture from the DX review and the borrowed
oracle control from the security review — three instruments in three reviews,
each of which passed while measuring nothing.

## Phase 2 — design questions, framed not answered

Each belongs in a bless note.

**1. Should the canary corpus gate CI?** It turns every silent change into a
review-time conversation, which is the whole value; the cost is fixture churn
on every deliberate change. The project priced this once (the `VRecord`
fixture estimate) and found it smaller than feared. Note the corpus needs a
STORED BASELINE rather than an old binary to be practical in CI — comparing
against a committed expected-output file is cheap; building the previous
release every run is not.

**2. Is a version pragma wanted?** Probably not — this project has been
removing surface, and a pragma is surface. But the alternative is that nothing
in a script says what it was written against, and with `--version` being a
bare git hash there is nothing ordered to compare even if a script did say.
The cheaper half of this question is whether `--version` should carry the tag
once tags exist, and whether `serverInfo.version` should be populated — both
are one line and neither is a pragma.

**3. What does pre-0.1.0 mean out loud?** Once the site exists, a visitor
seeing an LSP, three-platform binaries and a full doc set will infer more
stability than is on offer. The README says "anything can break between
releases, and the notes say what did" — which is good, and which the silent
class quietly contradicts, because a silent change is precisely the one the
notes will not think to mention. Same posture as the `Secret` and `kill -9`
non-claims: state the limit.

**4. Does a silent change deserve a changelog category?** The census says yes,
there is a real list. Readers hear "breaking" as "it will fail loudly", so the
four silent rows above would be filed under a heading that actively misleads.
A separate section — "checks clean, behaves differently" — is the honest
shape, and `ls` is the worked example for what an entry looks like.

## Denominator — what holds

Confirmed rather than assumed, as the plan asked.

- **Check-before-run makes most type-level change loud by construction.** The
  4 loud rows are all type/name-level, and each produced a diagnostic rather
  than a behaviour shift.
- **The retired-name registry makes renames loud AND teaching** — `Seq.filter`
  → "weir's filter is `Seq.where`" rather than "unbound".
- **`ci/skill-surface.sh` means a removed member cannot ship silently** — the
  doc's completeness is a checked property.
- **822 `Expect.equal` unit assertions** pin a large amount of behaviour. The
  gap is not "nothing is pinned"; it is that the DOC corpus is unasserted and
  e2e asserts needles rather than whole output.
- Record field ordering — the plan's headline example of a silent class — is
  **unchanged** across this window at all three renderers.

The honest summary is close to the good outcome the plan anticipated: **four
silent changes in sixty rows, all in output shape or ordering, none dangerous
in isolation, one (`ls` ordering) with a real hazard if a script acts on
`Seq.head`.** All four are correct changes. All four owe the release notes a
line, and the release notes do not exist yet — which is the actionable half.

## Instrument honesty

- I1's classification is a judgement call per row, and was made from each
  row's opening ~150 characters rather than its full text. That is a real
  limit: rows in this ledger are long, and a behaviour note late in a row
  would have been missed. `unknown` was preferred to a guess, but the 48
  `additive` count is the one most likely inflated, which is the comfortable
  direction and should be treated as suspect.
- I2's window is one week (2026-08-09 → 2026-08-15), chosen because the
  working checkout is a `blob:none` partial clone whose promisor remote is
  unreachable — `git worktree` at an older commit fails to fetch. The old
  build came from a fresh HTTPS clone instead. A wider window is available to
  anyone with a full clone and is the obvious next measurement.
- The corpus was written FROM THE DOCS (SKILL.md's surface inventory and
  GUIDE.md), not from the implementation, so it can in principle catch the
  docs and the binary disagreeing. It found no such case in this window.
- 24 canaries is thin for a language this size. The corpus is a starting
  artifact, not a covering one; `same 22` means "22 canaries saw nothing", not
  "the language is 92% stable".
- The reviewer has read the ledger. Counts are a FLOOR on what a real
  upgrading user would hit.
- **The measured HEAD is stale.** Upstream is at `3c494b5` (`weir check --can`,
  #51); this review measured `e961984` (#48). Three merged PRs are outside the
  window, including a new CLI surface, and are unexamined here.
