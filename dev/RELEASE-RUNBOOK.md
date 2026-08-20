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

## The rehearsal — an `rc` prerelease, throwaway release, permanent tag

**Tags are immutable [D:tag-immutability].** A tag is never deleted
and never re-pointed; a failed attempt gets the NEXT rc number. The
teardown deletes the RELEASE (an artifact), and the tag stays as the
history of the attempt. The gates already know: prerelease-suffixed
tags are exempt from `release-published` and the staleness check, so
a kept rc tag holds nothing red.

Two caveats built in: a prerelease is **public** while it exists
(that is why it is a throwaway rc, not a private test), and every
installer generated for it pins a release that will be deleted —
anyone who fetches it during the window holds a script pointing at
nothing. **Keep the window short.**

1. **CHANGELOG first.** Merge the `## v0.0.0-rcN` section to main
   BEFORE cutting the tag — the gate checks out the TAG'S commit, so
   a section merged after tagging cannot save that tag, and tags are
   never re-pointed: the only repair is the next rc number. (rc1's
   tag exists with no release for exactly this reason — the gate's
   first live catch.)
2. **Pre-flight.** CI green on all three platforms at the commit to
   be tagged.
3. **Tag.** `git tag v0.0.0-rcN && git push origin v0.0.0-rcN`.
   release.yml runs: gate (full battery + changelog check) → build
   (six platforms) → draft with binaries, SHA256SUMS, and the two
   generated installers. **Verify the asset list** — a missing
   platform is exactly what the draft step exists to catch.
4. **Publish as prerelease.** Releases page → edit draft → check
   "pre-release" → publish. Now watch, in order:
   - the `site` workflow fires on the publish (the `release:
     published` dispatch — the coupling `push: tags` would have
     missed);
   - the `image` workflow fires too, and `:latest` does NOT move;
   - the site deploys to the **staging** branch (prerelease routing).
5. **Verify staging**, the one-shot facts plus the install paths:
   - `curl -sI https://staging.weir.sh/install.sh` — HTTP 200 and
     `Content-Type: text/plain` (the `_headers` file at the edge; a
     browser must display the script, not download it);
   - `curl -fsSL https://staging.weir.sh/install.sh | sh` on Linux,
     macOS, Windows (`irm .../install.ps1 | iex`) — installs the rc,
     `weir --version` reports `v0.0.0-rcN+<sha>`;
   - the negative paths: a truncated fetch is a syntax error
     (`curl ... | head -c 900 | sh -n` fails), and a corrupted binary
     refuses (`CHECKSUM MISMATCH`) — flip a byte and re-verify.
6. **Tear down.** Delete the RELEASE only
   (`gh release delete v0.0.0-rcN --yes`) and remove the throwaway
   CHANGELOG section. The tag stays. Staging keeps serving the rc
   installer until the next deploy; that is staging's job.

`release-published`'s red direction is NOT rehearsed here — an rc
cannot trip it by design. It shows red in the real release's own
tag→publish window (step 3 below), which every release walks through.

Anything that surprises during 3-6 is what the rehearsal was for:
record it in dev/NOTES.md and fix the chain before the real tag.

## The release — `v0.0.1` and after

1. **CHANGELOG first**: the release's `## <tag>` section is merged
   to main before the tag is cut (the gate checks out the tag's
   commit and refuses without it; the release body IS that section
   [D:changelog]).
2. CI green on all three platforms at the tag commit.
3. Tag and push. release.yml: gate → build → **draft**. In this
   window `release-published check` goes RED on main — a stable tag
   exists, nothing published. That is the gate's failing direction
   observed live; publishing clears it.
4. Review the draft: asset list complete (six binaries, SHA256SUMS,
   install.sh, install.ps1), notes correct.
5. Publish (NOT prerelease). The site deploys to production; the
   post-deploy check asserts weir.sh serves the released tag, and
   `site staleness check` guards it on every CI run thereafter.
6. Verify the user path once by hand:
   `curl -fsSL https://weir.sh/install.sh | sh && weir --version`.
