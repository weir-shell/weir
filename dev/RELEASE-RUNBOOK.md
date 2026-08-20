# Release runbook

Two procedures: the **rehearsal** (a throwaway prerelease against
staging — run once before the first real tag, and again whenever the
release chain itself changes) and the **release**. The rehearsal exists
so the real tag is never the first time the chain runs
[D:site-deploy]: the dispatch firing, the content-type at the edge, and
the staging alias are one-shot facts no local test can establish.

## One-time infrastructure (maintainer)

- Cloudflare Pages project `weir-sh`, **direct upload** (the site.yml
  workflow deploys via wrangler; no git integration — a failed build
  never creates a deployment, so the prior deploy stays by construction).
- Repo secrets: `CLOUDFLARE_API_TOKEN` (Pages:Edit), `CLOUDFLARE_ACCOUNT_ID`.
- DNS: `staging.weir.sh` → the `staging` branch alias of the Pages
  project. (`weir.sh` production DNS can wait until the rehearsal has
  passed; staging cannot.)

## The rehearsal — `v0.0.0-rc1`, throwaway

Two caveats built in: a prerelease is **public** while it exists (that
is why it is a throwaway rc, not a private test), and every installer
generated for it pins a tag that will be deleted — anyone who fetches
it during the window holds a script pointing at a deleted release.
**Keep the window short.**

1. **Pre-flight.** CI green on all three platforms at the commit to be
   tagged. Add a throwaway `## v0.0.0-rc1` section to CHANGELOG.md
   (one line: "rehearsal, will be deleted") — the changelog gate
   refuses a tag without one, which this step also rehearses.
2. **Tag.** `git tag v0.0.0-rc1 && git push origin v0.0.0-rc1`.
   release.yml runs: gate (full battery + changelog check) → build
   (six platforms) → draft with binaries, SHA256SUMS, and the two
   generated installers. **Verify the asset list** — a missing
   platform is exactly what the draft step exists to catch.
3. **Observe the gate red.** The next CI run on main now FAILS at
   `release-published check`: a v* tag exists, no published release.
   This is the gate's untested direction — seeing it red here is the
   point; a gate that has only ever been green is unverified where it
   matters.
4. **Publish as prerelease.** Releases page → edit draft → check
   "pre-release" → publish. Now watch, in order:
   - the `site` workflow fires on the publish (the `release: published`
     dispatch — the coupling `push: tags` would have missed);
   - it deploys to the **staging** branch (prerelease routing);
   - `release-published check` STAYS red — `releases/latest` excludes
     prereleases, the same exclusion it guards for drafts. Expected;
     it rehearses the coupling.
5. **Verify staging**, the three one-shot facts plus the install paths:
   - `curl -sI https://staging.weir.sh/install.sh` — HTTP 200 and
     `Content-Type: text/plain` (the `_headers` file at the edge; a
     browser must display the script, not download it);
   - `curl -fsSL https://staging.weir.sh/install.sh | sh` on Linux,
     macOS, Windows (`irm .../install.ps1 | iex`) — installs the rc,
     `weir --version` reports `v0.0.0-rc1+<sha>`;
   - the negative paths: a truncated fetch is a syntax error
     (`curl ... | head -c 900 | sh -n` fails), and a corrupted binary
     refuses (`CHECKSUM MISMATCH`) — flip a byte and re-verify.
6. **Delete.** Delete the release AND the tag
   (`gh release delete v0.0.0-rc1 --yes`,
   `git push origin :v0.0.0-rc1`), remove the throwaway CHANGELOG
   section. Confirm `release-published check` returns GREEN on the
   next run — which also proves the gate reads current state rather
   than caching a verdict. Staging keeps serving the rc installer
   until the next deploy; that is staging's job.

Anything that surprises during 2-6 is what the rehearsal was for:
record it in dev/NOTES.md and fix the chain before the real tag.

## The release — `v0.0.1` and after

1. CHANGELOG.md has the release's `## <tag>` section (the gate refuses
   otherwise; the body IS that section [D:changelog]).
2. CI green on all three platforms at the tag commit.
3. Tag and push. release.yml: gate → build → **draft**.
4. Review the draft: asset list complete (six binaries, SHA256SUMS,
   install.sh, install.ps1), notes correct.
5. Publish (NOT prerelease). The site deploys to production; the
   post-deploy check asserts weir.sh serves the released tag, and
   `site staleness check` guards it on every CI run thereafter.
6. Verify the user path once by hand:
   `curl -fsSL https://weir.sh/install.sh | sh && weir --version`.
