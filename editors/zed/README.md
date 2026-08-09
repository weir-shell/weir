# Weir for Zed

Language support via `weir lsp` (diagnostics, hover, completion,
formatting, go-to-definition) plus tree-sitter highlighting from
[weir-shell/tree-sitter-weir](https://github.com/weir-shell/tree-sitter-weir),
pinned by commit in `extension.toml`. **Bumping that rev and
refreshing `languages/weir/highlights.scm` are one motion** — copy
the queries from the same grammar commit the rev pins (the drift
guard that used to enforce this retired with the repo split).

Verified on a real machine (macOS, dev-extension install,
2026-07-26): highlighting, diagnostics, hover, and formatting all ✓.
Not testable in the repo's verification container (Zed is GUI-only;
dev extensions compile locally with your Rust toolchain).

## Dev-mode grammar (read this first if the install fails)

Zed CLONES the grammar from `[grammars.weir]`'s `repository`@`rev` —
anonymously, even for a dev extension; it does not read the grammar
from this directory. If the weir repo is private (or the rev isn't
pushed), the install fails with "failed to compile grammar 'weir'".

For local installs, point the grammar at your clone before step 2 —
edit `extension.toml`:

```toml
[grammars.weir]
repository = "file:///absolute/path/to/your/tree-sitter-weir/clone"
rev = "<any COMMITTED sha>"
```

(Uncommitted changes are invisible to the clone — the rev must be a
real commit.) After a FAILED attempt, delete the stale cache before
retrying: Zed clones the grammar INTO this directory
(`editors/zed/grammars/`) and refuses to reuse a clone whose origin
doesn't match the new URL — `rm -rf editors/zed/grammars`. (Those
build artifacts — `grammars/`, `extension.wasm`, `target/` — are
gitignored; never commit them.)

For PUBLISHING, the repository must be anonymously
clonable: either the weir repo goes public, or the grammar splits
into a public dedicated `tree-sitter-weir` repo — the priced
decision from the grammar session.

## Local verification (5 steps)

1. `weir --version` in a terminal — the binary must be on PATH
   (Zed launches the server as `weir lsp`).
2. Zed → Extensions → **Install Dev Extension** → select this
   directory (`editors/zed/`). Requires a Rust toolchain
   (`rustup target add wasm32-wasip1`).
3. Open `examples/git-subrepo.weir` — highlighting should render
   (keywords/strings/types/sigils distinct), proving the grammar
   fetched and built.
4. Add `print undefinedName` anywhere — a diagnostic should appear;
   hover a binding — a type should show.
5. Format the buffer — the file should rewrite to `weir fmt`'s
   canonical 4-space form (editor tab settings are ignored by
   design).

If the grammar fails to build with an error about the `path` key,
your Zed predates grammar-in-subdirectory support: the
tree-sitter-weir directory must first be split into its own repo and
`extension.toml`'s `[grammars.weir]` pointed at it.

## Publishing (prepared, NOT sent — a decision, not a default)

Publishing puts weir in a public registry. When explicitly decided:

1. Fork `zed-industries/extensions`.
2. Add this extension as a git submodule under `extensions/weir`
   (Zed's registry references extensions by submodule; the extension
   likely needs its own repo or the weir repo pinned at a rev).
3. Add the entry to `extensions.toml` (id `weir`, version, path).
4. PR; their CI builds the wasm and validates the grammar ref.
