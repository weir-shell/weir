# weir + micro

1. Syntax highlighting: copy `weir.yaml` to `~/.config/micro/syntax/`.
2. LSP (micro >= 2.0 with the `lsp` plugin: `micro -plugin install lsp`):
   add to `~/.config/micro/settings.json`:

```json
{
    "lsp.server": "weir=weir lsp"
}
```

Diagnostics (with weir error codes), hover types, and completion
(names, `Module.` members, record fields after a dot, PATH commands at
line head) come from `weir lsp` — the same checked-statement pipeline
the runner uses, whole-file per keystroke (the pinned ~10ms check
makes incrementality unnecessary).
