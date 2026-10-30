# Editor setup

Every editor talks to the same language server, **`weir lsp`** (LSP
over stdio). It is part of the binary that runs your scripts, so the
editor always understands the weir version you run. The setups below
assume `weir` is on PATH.

weir scripts often have no extension (they start with
`#!/usr/bin/env weir`), so each setup recognizes weir files both by
the `.weir` extension and by the shebang line.

## What the server does

- **Diagnostics as you type**: the same whole-file checks weir runs
  before a script starts.
- **Hover**: types and `///` docs for bindings, builtins, record
  fields and union cases, including imported ones.
  - On a `#sig` command: its signature file and the tool version
    recorded there. Only the signature file is read, so this works
    without the tool installed.
  - On a `schema=` name: the vendored schema file, where it came from,
    and whether it catches unknown fields.
  - On keywords: `within`, `retry`/`poll` (with their keys), `until`,
    and `from`/`to` (with the adapter list). The same word in a string
    or comment shows nothing.
  - Type variables read `'a`, `'b`, …, never internal names; declared
    ones keep their names. The same holds in signature help.
- **Go to definition** for locals, module members, import paths,
  signature files and vendored schemas.
- **Find references**, **highlight** and **rename**: see below.
- **Completion** of module members (type `Lib.`) and of the `within`
  kinds after `within `.
- **Signature help**: a space or `(` after a function shows its
  signature and doc, with the current parameter highlighted. In a `|>`
  stage the last parameter is the piped value. After a `#sig` command,
  the flags you typed show what each takes and means, read from the
  signature file only.
- **Formatting**, identical to `weir fmt`. Your editor's tab settings
  are ignored on purpose: an editor set to 2 spaces still produces
  4-space weir.
- **Outline**: types with their fields and cases, functions with their
  nested functions, imports and `#sig` lines. A signature and its
  implementation are one entry.
- **Folding** for multi-line statements, indented blocks, match arms,
  comment runs and import runs.
- **Inlay hints**: the inferred type after each binder and parameter,
  and a function's return type before its `=`. Signed module members
  get none (the signature is the annotation). Long types are cut at 40
  characters, with the full type in the tooltip. Each class can be
  turned off (see [Inlay hints](#inlay-hints)).
- **Links** on import paths, `schema=` names and `#sig` tools, to the
  module, the vendored schema and the signature file. Only resolved
  targets are links; a missing one gets a diagnostic.
- **Unused binders fade**, and an error on a signed implementation
  points at its signature line, in editors that show diagnostic tags
  and related information.
- **Quick fixes** for the diagnostics weir can repair:
  - replace a misspelled name with the one it meant
  - qualify a bare module member (one or all)
  - add missing match arms, or a missing `else` (body `fail "todo"`)
  - rename an unused binder to `_name`; lowercase a binder
  - align or move a `///` doc
  - call an uncalled function
  - unwrap an `Option`/`Result` with `|> orFail`
  - stub a signature's implementation

Inside a statement with an error, the other lines keep hover and go to
definition; the bad line shows nothing. A type recovered past the error
can be more general than the fixed code's. A name whose value failed to
check, and anything typed from it, shows nothing until the error is
fixed.

### Find references and highlight

These cover a name's uses across the file:

- **Values**: every binder (top-level and block `let`s, params, pattern
  binders, `within`/`until` binders, yaml `for` binders) and its uses
  in expressions, `$name` splices, `$@xs` splats, interpolation holes
  and yaml districts.
- **Types, record fields and union cases** declared in the file: the
  declaration, accesses (`p.id`, `p.a.id`, `_.id`), literal keys,
  `with` updates, patterns, construction, the `from json T` /
  `Env.load T` / `Args.load T` slots, a validated type's `T.create`,
  types inside an anonymous adapter type (`from json {| p: Point |}`),
  and type references in other declarations.
- **Builtins and imported members**: their uses in this file. An
  imported union case's expression and pattern uses are listed
  together.

Comments and strings are never included. A row-typed access (`let
label r = r.id`) belongs to no named field, so it isn't listed.

### Rename

F2 renames a value binder, or a type, record field or union case
declared in the file, over the same sites references lists. The sig
line of a signed module member renames with it.

A field is a wire name. Renaming it changes what `from json`/`to json`
read and write and the `Args.load` flag or `Env.load` variable it
derives, which is what you want when the wire is the truth. A field
with `[<Wire "…">]` keeps its key, and the attribute line is untouched.

Rename either completes or is refused with the reason. It's refused
when:

- the cursor isn't on a value, type, field or case declared in this
  file (a builtin's field or case, or an imported member: rename it in
  its module)
- a statement in the name's scope has an error (for a member: anywhere
  from its declaration to the end of the file)
- the new name isn't a binder name, or starts with `_` (a `_` binder is
  deliberately unused, so weir tracks no uses of it and couldn't see a
  clash)
- the new name is already visible in that scope
- a new field name is already a field or `[<Wire>]` key of its record,
  or a field word anywhere in the file
- a new case name isn't uppercase, or is a case of any visible union (a
  bare use could name either)
- a new type name isn't uppercase, or is a visible type or module
- the field is accessed through a row-typed function (`let label r =
  r.id`). The access could serve any record with that field, so
  annotate the call or rename by hand; the refusal names the site.
- the new name has a non-ASCII digit
- the result wouldn't check. The error is named; most often it's a
  length change breaking a column-aligned layout, since rename doesn't
  re-align other lines. Pick a name of the same length, or rename by
  hand and re-align.

Init files and signature files don't rename. Other files that import a
renamed member show the breakage as diagnostics on their next refresh.

### Inlay hints

Configure them through `initializationOptions` (VS Code sends its
`weir.inlayHints.*` settings for you):

```json
{ "inlayHints": { "bindingTypes": true, "parameterTypes": true,
                  "pipelineTypes": false, "maxLabelLength": 40 } }
```

A key you leave out keeps its default. The server also reads the same
object under `settings.weir.inlayHints` on
`workspace/didChangeConfiguration`.

`pipelineTypes` shows each stage's type at the end of its line in a
multi-line `|>` chain. It's off by default: in a long chain every
stage's type is interesting once and noise after that.

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
  -- optional: the inlay hint settings (these are the defaults)
  init_options = {
    inlayHints = { bindingTypes = true, parameterTypes = true, pipelineTypes = false, maxLabelLength = 40 },
  },
})
vim.lsp.enable 'weir'
-- inlay hints are opt-in on the Neovim side
vim.lsp.inlay_hint.enable(true)

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

**Server not found.** The editor needs `weir` on its PATH. Run
`weir --version` from the environment your editor starts in; GUI
editors often get a shorter PATH than your shell. The VS Code extension
also looks in `~/.local/bin` and tells you what to fix if it still
can't find weir.

A server-path setting takes only the binary's path, because the editor
adds `lsp` itself. Set it to `weir lsp` and the editor looks for a
program literally named `weir lsp`, failing with ENOENT.

**The server doesn't start for a file.** The editor didn't recognize
it as weir. Check the buffer's filetype is `weir`: `:set ft?` in Vim,
`hx --health weir` in Helix, `M-x describe-mode` in Emacs. Scripts
without an extension need the shebang rules above.

**No colors.** Semantic tokens need an editor that supports them and
highlight groups that are actually visible. In Neovim, add the
`nvim_set_hl` links above. Helix and eglot don't use semantic tokens.

**Seeing the server's errors.** The server logs nothing by default.
Watch your editor's LSP log:

| editor | log |
|---|---|
| Neovim | `:LspLog`, or `:lua vim.cmd.e(vim.lsp.get_log_path())` |
| Helix | `hx -v` and the Helix log |
| Emacs | the `*EGLOT ... events*` buffer |

For more, run the server as `weir lsp --debug`: it logs every LSP
message it handles and every diagnostics update to stderr (VS Code
shows it in the Output panel). `weir check <file>` reproduces any
diagnostic on the command line.

## Scope

The server reads the text your editor sends, plus the files those
documents reach through `import` or `#sig`. A file that is open in the
editor is read from its buffer, any other from disk. The server reads
nothing else, and it never runs anything. When you jump to a
definition in another file, it's your editor that opens the file, not
the server. [SECURITY.md](../SECURITY.md) has the full statement.
