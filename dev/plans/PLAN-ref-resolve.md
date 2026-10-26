# PLAN: ref-resolve — the ref→sha step over git's own ref listing

**Status: BUILT (2026-10-26, branch feat/ref-resolve) — user-ruled; session report at the end.**

Sits under PLAN-lib: the bare-name add expands to the github shorthand,
and the shorthand's one network step before the raw fetch is the one
this plan replaces.

## The receipt

`weir add module <host>/<org>/<repo>//<path>@<ref>` resolves `<ref>` to
a full commit sha before building the raw URL the lock stores. Today
that step is host-specific REST: github.com asks
`api.github.com/repos/{repo}/commits/{ref}` (`sha`), gitlab.com asks
`gitlab.com/api/v4/projects/{repo}/repository/commits/{ref}` (`id`).
It is the only rate-limited step in the chain — GitHub's unauthenticated
budget is 60 requests/hour per IP, shared across every CI runner behind
one NAT — and the only place the two host arms differ in anything but
a URL template. The e2e battery tolerates `rate limit|answered 403` on
its live probe for exactly this reason, and CI hands the probe a token
to dodge it.

Every git host serves git's own ref advertisement over smart HTTP:

```text
GET https://<host>/<org>/<repo>.git/info/refs?service=git-upload-pack
```

No token for a public repo, no API rate limit, one shape for every
host (`git ls-remote` is this request). The ruling: resolve the ref
there. The raw-file URL scheme stays per host (it is not generic), so
the host list stays closed; the lock, `restore`, `verify` and the
bare-name expansion never see the change.

## Anchors (verified)

- `src/Weir/Contracts.fs` `resolveModuleSpec` ~1018–1130: the
  `"github.com"` and `"gitlab.com"` arms each fetch the API with
  `fetchBytesWith`, read the sha with `shaFrom` (~957), then build
  `Url`/`Root` from the raw template. `hostToken` (~931) feeds the API
  auth (GitHub `Bearer`, GitLab `PRIVATE-TOKEN`) and the raw auth
  (GitHub `token`, GitLab `PRIVATE-TOKEN`); `hintPrivate` (~939) appends
  the token hint on `answered 404`/`403`.
- `fetchBytesWith` (~814): the contract client — auto-redirect off,
  explicit bounded follow, credential headers dropped on a cross-origin
  hop [D:contract-redirect]. The listing request goes through it, so
  the drop covers the new leg as it covered the API leg.
- Pins naming the API: `ci/e2e.sh` ~14279 (the live library-host probe
  greps `api.github.com/repos/weir-shell/lib/commits/v0`); ~14273 (the
  live shorthand chain tolerates `rate limit|answered 403`);
  `.github/workflows/ci.yml` ~330 (the token comment names
  api.github.com). `tests/Weir.Tests/Tests.fs` has no pin on the arms
  (`vendorClosureTests` exercises raw URLs only — the arms were not
  pure). PLAN-lib and the NOTES lib story mention the API URL as
  history; they stand.
- The live shape today (github.com/weir-shell/weir.git): `001e# service=
  git-upload-pack`, `0000`, `<sha> HEAD\0multi_ack … symref=HEAD:refs/
  heads/main …`, then `<sha> refs/heads/…`, `<sha> refs/tags/…`;
  gitlab.com serves the same at the same path. weir-shell/lib.git lists
  `refs/heads/main` and a lightweight `refs/tags/v0.1.0` (no `^{}`
  line). Both hosts answer **401** for a repo that does not exist or is
  private without a token — not 404 — so `hintPrivate` gains 401.

## The shape

Two pure functions in Contracts.fs ahead of `resolveModuleSpec`, then
one fetch inside it:

```fsharp
/// (sha, refname) pairs from a smart-HTTP ref advertisement: pkt-line
/// framed (4 hex digits of self-inclusive length, `0000` a flush), the
/// `# service=` line skipped, the first ref line's `\0`+capabilities
/// dropped. Error: not an advertisement.
let parseRefListing (bytes: byte[]) : Result<(string * string) list, string>

/// the sha a ref name resolves to: a 40-hex name is its own sha (no
/// listing needed); else refs/tags/<name>^{} (an annotated tag's peeled
/// commit) over refs/tags/<name> over refs/heads/<name>
let pickRef (refs: (string * string) list) (name: string) : string option
```

`resolveModuleSpec`'s two arms collapse to one path parameterized by
the host: the listing URL (same template on both), the listing auth
(the Basic pair git over HTTPS sends — GitHub `x-access-token:<token>`,
GitLab `oauth2:<token>`), the raw base and raw auth (unchanged), the
tags page (`github.com/{repo}/tags`, `gitlab.com/{repo}/-/tags`).
`pickRef [] ref` short-circuits a full sha before any fetch. `shaFrom`
goes (dead).

Messages (the author's language):

- ref absent from the listing — `no tag or branch 'v0' at
  github.com/weir-shell/lib — the tags: https://github.com/weir-shell/lib/tags`
- the listing refused — `listing refs:
  https://github.com/acme/nope.git/info/refs?service=git-upload-pack
  answered 401 (Unauthorized) — or the repo is private: set
  WEIR_TOKEN_GITHUB_COM` (the existing `hintPrivate`, now also on 401;
  the URL names the repo)
- not an advertisement — `unexpected answer listing refs at
  https://…/info/refs?service=git-upload-pack`

All three ride the existing `weir add module: …` frame in Program.fs.

## Pins

Unit (`refListingTests`, beside `libSpecTests`): `parseRefListing` on
the real GitHub shape (service line, flush, the NUL capability line,
heads and tags), an annotated+peeled pair, a flush-only body (`Ok []`),
garbage (HTML, a truncated pkt) → Error; `pickRef`: peeled beats tag
beats head, a tag beats a same-named branch, a 40-hex passes through
with no listing, missing → None. `libSpecTests` untouched.

e2e (`[D:add-module]` block): the host list is closed, so the offline
truth is the unit list; the battery keeps ONE live probe per arm that
is reachable — github: the shorthand chain on weir-shell/weir
(tolerance dropped: there is no rate limit to tolerate now), the
library host `nosuch@v0` → the no-such-ref message, and the positive
resolve `semver@v0.1.0` → the output names
`raw.githubusercontent.com/weir-shell/lib/<40-hex>/modules/semver.weir`
(the tag resolved to a sha; whether the add then succeeds is the
library's layout at the tag — see the session report). gitlab.com has
no public repo of ours to probe; its arm is the same code path with
different templates, pinned by the unit list.

## Risks

1. **The listing is the whole ref set.** A busy repo advertises every
   ref (gitlab-org/gitlab-runner: 4.6 MB, 45k lines) where the API
   answered one object. One request, no rate limit; acceptable for an
   `add`, noted here.
2. **A short sha no longer resolves.** The API accepted `@0123abcd`; a
   listing has no prefix lookup. A full 40-hex sha passes through
   without a fetch; a prefix reads as "no tag or branch" — the docs say
   "the full commit sha".
3. **Protocol v0 only.** The request sends no `Git-Protocol: version=2`
   header, so the host answers the v0 advertisement parsed here; a
   host that answered v2 unasked would hit "unexpected answer".

## Non-goals

- Generic hosts: the listing is host-generic but the raw-file URL is
  not, so the host list stays github.com/gitlab.com ([D:add-module]'s
  flavor design stands for a self-hosted user).
- The lock shape, `restore`, `verify` (sha-only raw URL, unchanged).
- Short-sha prefixes; protocol v2; a tags-page lookup on the no-such-ref
  path (the message points at it).

## Docs

- docs/tooling.md "Remote modules": how the ref is resolved (git's own
  ref listing, no API, no token for a public repo; the full sha).
- CHANGELOG `## v0.0.79` `### Changed`.
- docs/DECISIONS.md row `ref-resolve-smart-http`; dev/NOTES.md story
  "Refs over smart HTTP"; dev/plans/README.md line.

## Session report (2026-10-26, feat/ref-resolve)

**Landed.** `Contracts.parseRefListing` and `Contracts.pickRef` (pure,
ahead of `resolveModuleSpec`); the two host arms share one path
(`hostArm`) parameterized by listing auth, raw base, raw auth and the
tags page; `hintPrivate` also fires on 401 (both hosts answer 401 for
a missing or private repo on the listing); `shaFrom` removed. Messages
as above. Pins: `refListingTests` (parse: real GitHub shape, annotated
+peeled pair, flush-only, HTML, a truncated pkt; pick: peel > tag >
head, tag over same-named branch, 40-hex pass-through, missing);
e2e: the shorthand chain's rate-limit tolerance dropped, the library
probe asserts the no-such-ref message, a new positive probe asserts
`semver@v0.1.0` resolved to a 40-hex raw URL. CI's token comment
updated (the token rides so the authenticated listing is exercised).

**By hand.** `weir add module semver@v0.1.0 --as semver`: the tag
resolves (`1f2633f7…`) and the raw fetch answers 404 — weir-shell/lib
at v0.1.0 (and on main) keeps `semver.weir` at the repo ROOT, while
the binary's expansion (feat/lib's last commit) reads
`//modules/<name>.weir`. The two disagree; not this plan's to settle —
the user picks the layout (move the file under modules/ and tag, or
revert the expansion to the root).
