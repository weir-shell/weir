# weir — mini-plan: `scriptPath` (the $0 gap)

Status: BLESSED (user 2026-07-24; sequenced BEFORE Path.glob).

(Blessed plan text as delivered; the executed decisions below.)

## Session report (2026-07-24)

- `scriptPath : string` landed: GetFullPath at run() entry — the
  startup cwd, before any evaluation (cd is eval-time), symlinks
  unresolved. Type/value ride baseEnvs beside args/stdin; the
  check/LSP surface gets the scheme via analyzeLines.
- Script-only refusal is a TEACHING error at the unbound site (the
  retired-names hook's sibling): "scriptPath is script-only (the
  running script's absolute path; absent in the REPL and -e)" —
  pinned at typecheck and e2e'd on -e.
- The three-invocation e2e pins one absolute answer
  (relative/dot/absolute) with a cd BEFORE the read; the
  PATH-shebang case pins the SCRIPT's path, not the interpreter's.
- Path.parent verify RESOLVED: the member is the already-shipped
  Path.dir — the idiom is taught (`scriptPath |> Path.dir`),
  nothing built; the GUIDE line carries the realpath spelling for
  symlink-resolvers.
- fuzz.weir deliberately does NOT rewrite (repo root ≠ script
  dir); the $0 friction half closed with the non-adoption named.
- Args.load coexistence pinned; the untyped floor untouched;
  zero-diff elsewhere.

Path.glob unblocks behind this.
