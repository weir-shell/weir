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

## Editor extensions — a separate cadence [D:ext-publish]

Extensions version independently of weir: a highlighting fix never
waits for a weir release, and the READMEs state the requirement
("weir on your PATH") instead of a version lockstep. Registry
versions are PERMANENT — no unpublish, no number reuse — so neither
pipeline rides `release: published`; each has its own tag.

One-time setup (human, before the first publish):

1. VS Code Marketplace: create the `weir-shell` publisher
   (marketplace.visualstudio.com/manage), mint an Azure DevOps PAT
   with Marketplace→Manage scope → repo secret `VSCE_PAT`.
2. Open VSX: eclipse.org account, sign the publisher agreement,
   create the `weir-shell` namespace (`npx ovsx create-namespace`)
   → repo secret `OVSX_TOKEN`. Then CLAIM OWNERSHIP — creating a
   namespace only makes you a contributor, and an unclaimed
   namespace shows every version with a ⚠ unverified banner. THE
   CLAIM ORDER IS FIXED BY THEIR TEMPLATE: the only self-served
   route for `weir-shell` is Option 1 (VS Code publisher WITH a
   published extension naming its repo), so publish to the VS Code
   Marketplace FIRST, then open the claim issue at
   github.com/EclipseFdn/open-vsx.org — tick "repo owned by the
   GitHub ID making this request (same org)" filing from an org
   member, or link a commit you authored in weir-shell/weir.
   (Option 3 does not fit: the namespace matches the ORG, not your
   user ID, and not the weir.sh domain.) The Open VSX listing wears
   the banner between its publish and the grant; that window is
   theirs, not ours. Verified = claimed owner exists AND the
   publishing account is a namespace member (a bot token belongs to
   a contributor account). A fresh publish also lands DEACTIVATED on
   Open VSX — their post-publish gate for new publishers; v0.1.0
   activated within minutes, so treat the state as a queue, not a
   failure. The Marketplace's "verifying" is the same shape.
3. Zed: fork zed-industries/extensions as weir-shell/extensions;
   a CLASSIC PAT with `public_repo` scope (fine-grained tokens
   cannot open PRs on repos you do not own, and the PR targets
   zed-industries/extensions) → repo secret `ZED_EXTENSIONS_TOKEN`.
4. Before anything permanent: `workflow_dispatch` ext-vscode (a DRY
   RUN — package + artifact only), download the .vsix, install it on
   a clean profile (`code --install-extension`), check highlighting,
   the LSP against the SHIPPED weir, and the no-weir case (clear
   PATH: the error must offer the Install-weir button). Load
   editors/zed via zed: install dev extension, same checks.
5. A screenshot of a diagnostic underlined in the editor belongs on
   the marketplace listing — one capture, the most persuasive thing
   on the page (open item; the listing ships without it).

Publishing:

- VS Code + Open VSX: bump `editors/vscode/package.json` version,
  tag `ext-vscode-v<version>` (the workflow refuses a mismatched
  tag), push. One .vsix is built and published to BOTH registries
  and attached to a GitHub release on the tag.
- Zed: bump `editors/zed/extension.toml` version, tag
  `ext-zed-v<version>`, push. The workflow opens a PR against
  zed-industries/extensions (submodule + `path = "editors/zed"` —
  subdirectories verified accepted). A green run means PR OPENED;
  the merge is Zed's review, not ours.
- After the FIRST publish, install from each registry (not the local
  .vsix) — the path users take is the one nobody tests.
