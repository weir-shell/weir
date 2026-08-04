# tree-sitter-weir

A tree-sitter grammar for [weir](../../README.md) — **a renderer, not
a second parser**. The one truth for what weir accepts is `weir
check`'s pipeline ([SEMANTICS.md](../../SEMANTICS.md)); this
grammar's only job is better-than-grey highlighting in tree-sitter
consumers (Helix, Zed, code forges). It over-accepts freely, does not
replicate the assembler's logical-line reconstruction (a continuation
line may highlight as a fresh statement), and must never be cited as
the language definition.

Generated `src/` is committed so consumers (Helix `--grammar build`,
Zed) need no Node.js.

## Helix

```toml
# languages.toml
[[grammar]]
name = "weir"
source = { git = "https://gitlab.com/arquidevio/weir", subpath = "editors/tree-sitter-weir" }
```

then `hx --grammar fetch && hx --grammar build`, and copy
`queries/highlights.scm` to `~/.config/helix/runtime/queries/weir/`.
Add `grammar = "weir"` (implied by the language name) to the
`[[language]]` block from [docs/editors.md](../../docs/editors.md).

## Regenerating

`tree-sitter generate` (needs the tree-sitter CLI and Node). The
corpus acceptance: `tree-sitter parse` over every `.weir` in
`examples/` and `tools/` must produce zero ERROR nodes.

## Known nits

- `$"... {{literal braces}} ..."` — the interp rules mis-lex a
  `{{`-escape adjacent to a closing quote (ONE ERROR node,
  examples/showcase.weir line 81 as of 2026-08-04 — the line moved
  with the file and the node count was re-verified against the
  pre-retry-poll grammar; an earlier entry said two, which
  double-counted the parse summary line. Present since before the
  block-scalars session). The corpus-acceptance rule tolerates it
  until a coloring session takes it; the fix likely lives in the
  `interp_text`/`interp_escape` token ordering.
