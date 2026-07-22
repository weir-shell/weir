# VS Code extension smoke (re-runnable)

The LSP integration probes (tests/lsp/lsp-e2e.py) prove the protocol;
this smoke proves the CLIENT wiring. Steps 1-3 run anywhere; steps
4-9 need a machine with VS Code (`code` on PATH) — the build
container has none, so the interactive half is run on the user's
machine and this checklist is the record of what to do.

1. **Stale-binary guard first** (the standing lesson):
   `weir --version` — must match HEAD (`./publish.sh` if not).
2. Build: `cd editors/vscode && npm install && npm run compile`.
3. Package: `npx vsce package --allow-missing-repository` — produces
   `weir-0.1.0.vsix`.
4. Install: `code --install-extension weir-0.1.0.vsix`.
5. Open `examples/bicep-deploy.weir`:
   - cmd-not-found warnings appear on the az/bicep lines (squiggles,
     code `cmd-not-found`) unless the tools are installed.
6. Hover `targetEnv` — the inferred scheme appears.
7. Type `Seq.` — member completion; accept one — no double-insert
   (the textEdit pin, client-side confirmation).
8. On a new line type `git ` — PATH command completion at line head.
9. Break the file (`let x = 1 + "a"`): error squiggle with code
   `check`; fix it: the diagnostic clears without saving.

Also confirm colorization sanity on the same file: comments one
color (including after code), raw strings `@"..."`/`"""..."""` as
string regions, district `!e` markers highlighted, `https://` URLs
NOT comment-colored.

Protocol disagreements found here are FINDINGS against the server —
frame-level pin in tests/lsp/lsp-e2e.py, fixed server-side; the
extension does not absorb workarounds.
