# weir

Language support for [weir](https://weir.sh) — a typed
shell-scripting language: commands, pipes and files, like bash, with
a checker up front. **Nothing runs until everything checks.**

This extension gives you:

- **Diagnostics as you type** — the whole file is checked, every
  command included; a misspelled external command is underlined
  before anything runs
- **Hover** — types and documentation for every builtin, from the
  same source the REPL's `#help` reads
- **Completion** — members, keywords, and command names
- **Highlighting** — weir's one novel boundary, command versus
  expression, colored from the parse: command heads as callables,
  argv as inert words, splice islands as code

## Requires weir

The language server is the weir binary itself (`weir lsp`) — install
it first:

```
curl -fsSL https://weir.sh/install.sh | sh
```

Windows: `irm https://weir.sh/install.ps1 | iex`

The extension finds `weir` on PATH, then in `~/.local/bin` (GUI
editors often see a shorter PATH than your shell). If it lives
elsewhere, set `weir.serverPath` to the absolute path of the binary
— the path only; the extension runs `<path> lsp` itself.

## Learn weir

- [The guide](https://weir.sh/docs/guide/) — from first script to
  parallelism
- [The reference](https://weir.sh/reference/) — the language and
  every module
- [Coming from bash, PowerShell, Python…](https://weir.sh/docs/coming-from/)

The extension versions independently of weir — a highlighting fix
does not wait for a weir release, and any recent weir works.
