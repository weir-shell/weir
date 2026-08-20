# Release runbook

One procedure: the **release** — and the first one doubles as the
rehearsal [D:first-release-rehearsal]. Everything that could be
verified without a release has been; what remains is exercised by
the first release itself, watched, with a stated recovery (delete the
release, never the tag, cut the next number) — a recovery already used
twice before anything shipped: rc1 (changelog section missing from the
tagged commit) and v0.0.1 (the workflow at the tag named a retired
runner; a tag's run is pinned to the tag's own workflow file, so a
main-side fix cannot save it).

## One-time infrastructure (maintainer)

- Cloudflare Pages project `weir-sh`, **direct upload** (the site.yml
  workflow deploys via wrangler; no git integration — a failed build
  never creates a deployment, so the prior deploy stays by construction).
- Repo secrets: `CLOUDFLARE_API_TOKEN` (Pages:Edit), `CLOUDFLARE_ACCOUNT_ID`.
- DNS: `staging.weir.sh` → the `staging` branch alias of the Pages
  project. (`weir.sh` production DNS can wait until the rehearsal has
  passed; staging cannot.)

## The first release IS the rehearsal

There is no separate rc rehearsal [D:first-release-rehearsal]. Under
immutable tags a failed first release costs exactly what a failed rc
would: delete the RELEASE (never the tag), fix, cut the next number.
`0.x` already promises breakage, nobody is watching yet, and every
chain failure is loud — a failed deploy leaves the prior site, a
failed image push leaves no image, a stale installer trips the
staleness gates.

Already verified before any tag (no release needed):

- the changelog gate's red, live (rc1's tag is the receipt)
- staging DNS, the edge content-type, and the 404 posture (the site
  workflow's manual staging channel)
- the production deploy chain, docs-only (weir.sh is live)
- all the offline install pins (truncation, checksums, generation)

What the first release exercises for the first time — watch each:

- the `release: published` dispatch reaching the site AND image
  workflows
- the installer fetch from a real release, and weir.sh serving it
- the image build/push, `:latest` moving (it must — this is a real
  release)
- `release-published check` red in the tag→publish window, green
  after the click

The prerelease→staging routing in site.yml and the `:latest`
withholding in image.yml exist and are UNEXERCISED — stated here
rather than implied tested. They run if an rc is ever cut for a
genuinely risky change; staging remains reachable any time via the
site workflow's manual channel.

## The release

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
6. Verify the user path by hand, the rehearsal checklist run against
   production:
   - `curl -sI https://weir.sh/install.sh` — 200, `text/plain`
   - `curl -fsSL https://weir.sh/install.sh | sh && weir --version`
     on Linux, macOS, Windows (`irm .../install.ps1 | iex`) — the
     stamp reports the tag
   - the negative paths: a truncated fetch is a syntax error, a
     corrupted binary refuses with `CHECKSUM MISMATCH`
   - `docker run --rm ghcr.io/weir-shell/weir:<tag> --version`, and
     `:latest` resolves to the same digest
7. If any of it fails: delete the release, fix, cut the next number.
   The tag stays [D:tag-immutability].
