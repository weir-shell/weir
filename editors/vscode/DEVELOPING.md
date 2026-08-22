# Developing the VS Code extension

(README.md is the MARKETPLACE listing — different audience; repo and
maintenance material lives here and does not ship in the vsix.)

Client glue + TextMate colorization for `weir lsp` — the same
checked-statement pipeline the runner uses, whole-file per keystroke.
No server changes: protocol disagreements are findings against the
server (frame-level pins in tests/lsp/lsp-e2e.py), never client-side
workarounds.

Build and sideload:

```
cd editors/vscode
npm install
npm run package
code --install-extension weir-0.1.0.vsix
```

`npm run package` runs the LOCAL vsce (a devDependency, current
major) and bundles via esbuild — the vsix is the bundle + grammar +
metadata (8 files, ~106 KB), no node_modules, no flags needed. Every
runtime dependency compiles into `out/extension.js`, so the shipped
surface has zero third-party modules and `npm audit` is clean at pin
time. Requires VS Code ≥ 1.91 (the languageclient v10 floor).

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
