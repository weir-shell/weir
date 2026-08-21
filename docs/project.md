# Project layout: `.weir/`

A project that uses [signatures](signatures.md) or
[schemas](schemas.md) grows one directory:

```text
.weir/
  lock.json          # the lock: exact identity + hash for every vendored artifact
  sigs/<tool>.weir   # command signatures (weir add sig)
  schemas/<name>.json# JSON schemas (weir add schema)
```

A script finds its `.weir/` by walking UP from its own directory to
the first one — the walk stops at a `.git` boundary (directory or
file, so worktrees behave) or the filesystem root, and the error
names both what was looked for and where the walk stopped. One
`.weir/` at the repo root serves every script under it.

## Commit all of it

The directory is designed to be checked in — lock, signatures and
schemas alike. Vendoring is the point: `weir check` never fetches,
so what CI checks against is exactly what you committed. After a
fresh clone with a missing artifact, `weir restore` re-materializes
what the lock records, hash-verified — but a committed `.weir/`
never needs it.

## Four properties, each load-bearing

- **Vendored** — checked in, never fetched during check.
- **Pinned** — exact identity, no ranges; comparisons are pairwise,
  not a dependency graph.
- **Check-time only** — deleting every contract leaves every script
  running identically; contracts constrain what the CHECKER accepts
  and contribute nothing at run time.
- **Declared, not discovered** — a `.weir/` directory's mere
  existence never changes how a file checks; a script opts in with
  `#sig` / `schema=`.
