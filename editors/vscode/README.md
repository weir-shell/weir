# weir + VS Code

Client glue + TextMate colorization for `weir lsp` — the same
checked-statement pipeline the runner uses, whole-file per keystroke.
No server changes: protocol disagreements are findings against the
server (frame-level pins in tests/lsp/lsp-e2e.py), never client-side
workarounds.

Build and sideload (marketplace publishing is parked with the OSS
decisions):

```
cd editors/vscode
npm install
npm run compile
npx vsce package --allow-missing-repository
code --install-extension weir-0.1.0.vsix
```

The `weir` binary resolves from PATH; `weir.serverPath` in settings
is the escape hatch. `.weir` files and `#!...weir` shebang scripts
get the language mode.

Verification: editors/vscode/SMOKE.md (the interactive half runs on
a machine with VS Code; the protocol half is CI's lsp-e2e.py).

MAINTENANCE RULE: `syntaxes/weir.tmLanguage.json` is a rule-for-rule
port of `editors/micro/weir.yaml` (the micro file is the SPEC). A
rule existing in one grammar only is drift — add to BOTH or neither;
the e2e inventory test diffs micro's `# rule:` annotations against
this grammar's repository keys and fails on divergence. Oniguruma
extras (lookbehind/lookahead) are used only where they simplify an
existing micro rule, never to add rules micro lacks.
