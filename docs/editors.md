# Editor setup

One binary, one command: every editor runs **`weir lsp`** — the
language server over stdio, a subcommand of the same binary that runs
your scripts, so it can never go out of sync with the language. All
blocks below assume `weir` is on PATH.

What you get, in any LSP editor:

- **Diagnostics as you type** — the same checks the runner makes,
  whole file, every keystroke
- **Hover** — types and `///` docs for bindings, builtins, record
  fields and union cases, across files and imports; a `#sig`-signed
  command's head hovers its identity and recorded version (no spawn
  — works with the tool off PATH); a `schema=` name hovers its
  vendored file's facts
- **Go to definition** — locals, module members, import paths,
  signature files, vendored schemas
- **Completion** — module members (`Lib.` lists them), and the
  `within` kinds after `within `
- **The form keywords answer** — `within`, `retry`/`poll` (with
  their keys), `until`, the adapters — and never inside a string or
  comment
- **Formatting** — `weir fmt`'s canonical pipeline; editor tab
  settings are ignored by design, so a 2-space editor still writes
  canonical 4-space weir

weir scripts are often extensionless (`#!/usr/bin/env weir`), so
each setup below registers BOTH the `.weir` extension and shebang
detection.

Debugging: `weir lsp --debug` logs every dispatched method and
diagnostics publish to stderr — add `--debug` to the client's server
argv (VS Code shows it in the Output panel).

## Neovim (0.11+)

```lua
-- filetype: .weir files, and extensionless scripts with a weir shebang
vim.filetype.add {
  extension = { weir = 'weir' },
  pattern = {
    ['.*'] = {
      function(_, bufnr)
        local first = (vim.api.nvim_buf_get_lines(bufnr, 0, 1, false)[1] or '')
        if first:match '^#!.*weir' then
          return 'weir'
        end
      end,
      { priority = -math.huge },
    },
  },
}

-- the server
vim.lsp.config('weir', {
  cmd = { 'weir', 'lsp' },
  filetypes = { 'weir' },
})
vim.lsp.enable 'weir'

-- comment + indent, matching `weir fmt`
vim.api.nvim_create_autocmd('FileType', {
  pattern = 'weir',
  callback = function()
    vim.bo.commentstring = '// %s'
    vim.bo.shiftwidth = 4
    vim.bo.expandtab = true
  end,
})

-- semantic tokens use weir's own token types; link them to see color
vim.api.nvim_set_hl(0, '@lsp.type.weirCommandHead', { link = 'Function' })
vim.api.nvim_set_hl(0, '@lsp.type.weirArgv', { link = 'String' })
vim.api.nvim_set_hl(0, '@lsp.type.weirSplice', { link = 'Special' })
```

On Neovim 0.10 or with nvim-lspconfig, the equivalent is a custom
server entry with the same `cmd`/`filetypes`; the filetype block is
identical.

Verified (Neovim 0.11.3, headless, in-container): attach ✓,
diagnostic ✓, hover ✓, semantic tokens ✓ (the highlight links above
are required for the colors to be visible — the token types are
weir's own, not standard names), formatting ✓ (the applied edit is
byte-identical to `weir fmt`'s output; the editor's tabSize was
ignored as designed), go-to-definition ✓ (a use jumps to its
top-level `let`), `.weir` and shebang detection ✓.

## Helix

`~/.config/helix/languages.toml`:

```toml
[language-server.weir]
command = "weir"
args = ["lsp"]

[[language]]
name = "weir"
scope = "source.weir"
file-types = ["weir"]
shebangs = ["weir"]
comment-token = "//"
indent = { tab-width = 4, unit = "    " }
language-servers = ["weir"]
```

Colors come from the tree-sitter grammar
([weir-shell/tree-sitter-weir](https://github.com/weir-shell/tree-sitter-weir)) — add its
source and build it:

```toml
# languages.toml, alongside the blocks above
[[grammar]]
name = "weir"
source = { git = "https://github.com/weir-shell/tree-sitter-weir" }
```

then `hx --grammar fetch && hx --grammar build`, and copy
the grammar repo's `queries/highlights.scm` to
`~/.config/helix/runtime/queries/weir/highlights.scm`.
`hx --health weir` should then show server, parser, and highlights
all ✓.

Verified (Helix 25.01.1, in-container): attach ✓, diagnostic ✓
(gutter marker + statusline count), hover ✓ (`space k`), formatting ✓
(`:format` rewrites the buffer to `weir fmt`'s output), definition ✓
(`gd` on a use lands on its top-level `let`), `.weir` and shebang
detection ✓, tree-sitter highlighting ✓ (keywords, strings, types,
binder names, and the `$`/`$@`/`!` sigil family each render
distinctly on the flagship). LSP semantic tokens remain unsupported
by Helix — the grammar is the coloring path.

## Emacs (eglot)

Emacs needs a major mode to hang the server association on. The repo
ships a minimal one — [`editors/emacs/weir-mode.el`](../editors/emacs/weir-mode.el):
comment syntax, `.weir` + shebang association
(`interpreter-mode-alist`), and the eglot entry for `weir lsp`.

```elisp
(load "/path/to/weir/editors/emacs/weir-mode.el")
;; then, in a weir buffer:
;;   M-x eglot
```

UNTESTED: Emacs could not be installed in the verification container.
The mode is ~20 lines of standard associations; treat it as a
starting point and report friction. Note eglot has no built-in
semantic-tokens support — expect diagnostics/hover/completion only.

## VS Code

Install **weir** from the
[VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=weir-shell.weir)
— or from [Open VSX](https://open-vsx.org/extension/weir-shell/weir)
for VSCodium, Cursor and friends. The client wraps the same
`weir lsp` server and adds highlighting; no manual wiring. If the
binary lives off PATH, set `weir.serverPath` to it (the binary path
only — the client runs `<path> lsp` itself).

## Zed

Extension, not config — [`editors/zed/`](../editors/zed/): the same
`weir lsp` server plus tree-sitter highlighting. Until it lands in
the Zed extension registry, install as a dev extension (Extensions →
Install Dev Extension → the `editors/zed/` directory; needs a local
Rust toolchain). A failed install attempt leaves a stale `grammars/`
clone in the extension's work dir — delete it before retrying.

## Troubleshooting

- **Server not found**: `weir lsp` assumes `weir` is on PATH — run
  `weir --version` from the same environment your editor starts in
  (GUI editors often see a shorter PATH than your shell; the VS Code
  client also probes `~/.local/bin` and reports an actionable error).
  Where a server-path setting exists, it takes the BINARY path only —
  the client adds `lsp` itself; `weir lsp` in the setting is the
  spawn-ENOENT trap.
- **No attach**: the filetype/language didn't match — confirm the
  buffer's filetype is `weir` (`:set ft?` in vim; `hx --health weir`;
  `M-x describe-mode`). Extensionless scripts need the shebang rules
  above.
- **No colors**: semantic tokens need client support AND visible
  highlight groups — Neovim needs the `nvim_set_hl` links above;
  Helix and eglot don't consume semantic tokens at all.
- **Seeing the server's own errors**: the server logs nothing by
  default; watch the client's LSP log (`:LspLog`/`:lua
  vim.cmd.e(vim.lsp.get_log_path())` in Neovim, `hx -v` + the helix
  log, `*EGLOT ... events*` buffer in Emacs). `weir check <file>`
  reproduces any diagnostic from the CLI.

## Scope

The server analyzes the text the client sends, plus the files those
documents reach by `import` or `#sig` (an open dependency from its
buffer, an unopened one from disk) — never anything else. A
cross-file definition jump OPENS a file only by the client's own
action. See [SECURITY.md](../SECURITY.md)'s non-claims for the
boundary as stated.
