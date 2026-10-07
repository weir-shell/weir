# Editor setup

Every editor talks to the same language server: **`weir lsp`**,
which speaks LSP over stdio. It is a subcommand of the binary that
runs your scripts, so the editor always understands exactly the
version of weir you run. The setups below assume `weir` is on PATH.

In any editor with LSP support you get:

- **Diagnostics as you type.** The same checks weir makes before
  running a script, over the whole file, on every keystroke.
- **Hover.** Types and `///` docs for bindings, builtins, record
  fields and union cases, including ones from other files and
  imports. On a command declared with `#sig`, hover shows its
  signature file and the tool version recorded there. It reads only
  the signature file, so it works even when the tool isn't installed.
  On a `schema=` name, hover shows the vendored schema file, where it
  came from, and whether it catches unknown fields.
- **Go to definition** for locals, module members, import paths,
  signature files and vendored schemas.
- **Completion** of module members (type `Lib.` to list them), and of
  the `within` kinds after `within `.
- **Hover on keywords.** `within`, `retry`/`poll`
  (with their keys), `until`, and `from`/`to` (with the list of
  adapters) explain themselves. The same word inside a string or
  comment shows nothing.
- **Formatting**, with the same output as `weir fmt`. Your editor's
  tab settings are ignored on purpose, so an editor set to 2 spaces
  still produces standard 4-space weir.

weir scripts often have no extension (they start with
`#!/usr/bin/env weir`), so each setup below recognizes weir files both
by the `.weir` extension and by the shebang line.

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

On Neovim 0.10, or if you use nvim-lspconfig, add a custom server
entry with the same `cmd` and `filetypes`; the filetype block stays
the same.

The `nvim_set_hl` links at the end are what make the colors visible.
weir's semantic tokens use their own type names rather than the
standard ones, so Neovim has no colors for them until you link them.

Tested with Neovim 0.11.3: the server attaches, and diagnostics,
hover, colors, formatting (identical to `weir fmt`, whatever your tab
size) and go to definition all work, as does recognizing `.weir` files
and weir shebangs.

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

Helix colors code with tree-sitter, so for highlighting add the weir
grammar
([weir-shell/tree-sitter-weir](https://github.com/weir-shell/tree-sitter-weir))
and build it:

```toml
# languages.toml, alongside the blocks above
[[grammar]]
name = "weir"
source = { git = "https://github.com/weir-shell/tree-sitter-weir" }
```

Then run `hx --grammar fetch && hx --grammar build`, and copy the
grammar repo's `queries/highlights.scm` to
`~/.config/helix/runtime/queries/weir/highlights.scm`.
`hx --health weir` should now show the language server, the parser
and the highlights all ✓.

Helix doesn't support LSP semantic tokens, so the grammar is the only
source of colors.

Tested with Helix 25.01.1. Diagnostics show as a gutter marker and a
count in the status line, hover is `space k`, `:format` rewrites the
buffer to `weir fmt`'s output, and `gd` goes to the definition.
`.weir` files and weir shebangs are recognized. Keywords, strings,
types, the names being bound, and the `$`/`$@`/`!` sigils each get
their own color.

## Emacs (eglot)

eglot attaches a server to a major mode, so Emacs needs a weir mode
first. The repo ships a minimal one,
[`editors/emacs/weir-mode.el`](../editors/emacs/weir-mode.el). It sets
the comment syntax, uses `weir-mode` for `.weir` files and weir
shebangs (through `interpreter-mode-alist`), and tells eglot to run
`weir lsp`.

```elisp
(load "/path/to/weir/editors/emacs/weir-mode.el")
;; then, in a weir buffer:
;;   M-x eglot
```

Tested with Emacs 30.2: `.weir` files and `#!/usr/bin/env weir`
scripts open in `weir-mode`, and `M-x eglot` connects to `weir lsp`
with diagnostics, hover, completion, go to definition and formatting.

eglot doesn't use semantic tokens, so commands, their arguments and
`$` splices aren't colored. Tree-sitter highlighting for Emacs is
planned. For now, expect the LSP features but not the colors.

## VS Code

Install **weir** from the
[VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=weir-shell.weir),
or from [Open VSX](https://open-vsx.org/extension/weir-shell/weir)
for VSCodium, Cursor and similar editors. The extension runs the same
`weir lsp` server and adds syntax highlighting, so there is nothing
to set up. If `weir` isn't on PATH, set `weir.serverPath` to the
binary. Give it only the binary's path: the extension runs
`<path> lsp` itself.

## Zed

Zed is set up with an extension rather than config:
[`editors/zed/`](../editors/zed/). It runs the same `weir lsp` server
and adds tree-sitter highlighting. It isn't in the Zed extension
registry yet, so install it as a dev extension: Extensions → Install
Dev Extension → pick the `editors/zed/` directory. This needs a local
Rust toolchain.

If an install attempt fails, it leaves a stale `grammars/` clone in
the extension's work directory. Delete it before you try again.

## Troubleshooting

- **Server not found.** The editor needs `weir` on its PATH. Run
  `weir --version` from the same environment your editor starts in:
  GUI editors often get a shorter PATH than your shell. (The VS Code
  extension also looks in `~/.local/bin`, and tells you what to fix
  if it still can't find weir.) If your editor has a server-path
  setting, give it only the binary's path, because the editor adds
  `lsp` itself. With `weir lsp` in the setting, the editor looks for
  a program literally named `weir lsp` and fails with ENOENT.
- **The server doesn't start for a file.** The editor didn't
  recognize the file as weir. Check that the buffer's filetype is
  `weir` (`:set ft?` in Vim, `hx --health weir` in Helix,
  `M-x describe-mode` in Emacs). Scripts without an extension need
  the shebang rules above.
- **No colors.** Semantic tokens need an editor that supports them
  and highlight groups that are actually visible. In Neovim, add the
  `nvim_set_hl` links above. Helix and eglot don't use semantic tokens
  at all.
- **Seeing the server's own errors.** The server logs nothing by
  default; watch your editor's LSP log (`:LspLog` or
  `:lua vim.cmd.e(vim.lsp.get_log_path())` in Neovim, `hx -v` and the
  Helix log, the `*EGLOT ... events*` buffer in Emacs). For more
  detail, add `--debug` to the server's arguments (`weir lsp --debug`):
  the server then logs every LSP message it handles and every
  diagnostics update to stderr. VS Code shows this in the Output
  panel. `weir check <file>` reproduces any diagnostic on the command
  line.

## Scope

The server reads the text your editor sends, plus the files those
documents reach through `import` or `#sig`. A file that is open in the
editor is read from its buffer, any other from disk. The server reads
nothing else, and it never runs anything. When you jump to a
definition in another file, it's your editor that opens the file, not
the server. [SECURITY.md](../SECURITY.md) has the full statement.
