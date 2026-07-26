# Weir for Zed

Language support via `weir lsp` (diagnostics, hover, completion,
formatting, go-to-definition) plus tree-sitter highlighting from
[`editors/tree-sitter-weir`](../tree-sitter-weir/), pinned by commit
in `extension.toml`.

**UNTESTED in the repo's verification container** — Zed is GUI-only
and dev extensions compile locally (Zed uses your Rust toolchain).
Verify on a real machine with the five steps below and report
friction.

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
