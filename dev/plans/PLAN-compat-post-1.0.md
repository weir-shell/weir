# PLAN: the compatibility story — parked until 1.0

**Status: PARKED** (user, 2026-10-27) — "worth parking it for when 1.0
happens". Reopens when a 1.0 date is set; nothing here is scheduled.

## The receipt

Raised 2026-10-27 while ruling per-module library tags
[PLAN-lib-module-tags]: "weir doesn't have a code compatibility story.
It is fine for now but what about post 1.0?"

What exists today: vendoring. `weir add` copies a module into the
project and the lock pins its sha, so a weir upgrade and a module
upgrade are decoupled and nothing floats. What does not exist: the
module-to-weir axis — a module written against a newer weir vendored
into an older one fails at `weir check` with the newer feature's error
(an unknown attribute, a parse error), never with "this needs weir
X.Y".

## The shape, recommended at the time of parking

1. **The language promise, Go's shape.** A script that checks on 1.0
   checks and means the same on every 1.x: additions only (members,
   attributes, adapters). Removals and meaning changes wait for 2.0
   and carry retirement hints — the machinery the ledger parks as
   phase-scoped until the first external user ([D:drop-reify-builtins],
   [D:builtin-fields-lowercase]); 1.0 is that user. Editions (Rust)
   are a second checker to maintain and are not planned unless a 2.0
   is actually on the table.
2. **A `#weir <version>` header line in modules** — the minimum weir a
   module needs, machine-read in three places: `weir check` refuses an
   older weir naming the version; `weir add module <name>` picks the
   latest tag THIS weir can take and says when that is not the latest;
   the library's CI tests each module on its declared minimum, not
   only on the latest release. The lib README states the number in
   prose per module today; this makes it the one source. Cheap
   pre-1.0; the natural second step of the bare add.
3. **Module API versions ride the per-module tag** (`semver/v2.0.0`
   when the module's signature breaks), independent of the weir
   version it needs. Two axes, one home each.
4. **File formats carry a version field before 1.0**: the lock, sig
   files, `check --json`'s fix output — the things that would break
   silently otherwise.

## Reopen criterion

A 1.0 date, or an external user hitting the module-to-weir gap (item 2
alone is worth doing then, as its own mini plan behind
[PLAN-lib-module-tags]).
