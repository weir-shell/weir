# weir — VS Code extension: client glue + TextMate grammar

Status: LANDED 2026-07-22 (blessed same day). One session. The LSP
server is done and protocol-conformant; VS Code needed only a client
extension and a colorization grammar. Marketplace publishing is NOT
this plan (OSS-park-adjacent); local sideloading covers the
single-user reality.

Completion addenda (2026-07-22):
- Zero server changes, zero protocol findings at build time — the
  fsautocomplete question's answer cashed in a second editor. The
  interactive smoke half runs user-side (the build container has no
  VS Code); packaging + the CI protocol probes are the container-side
  proof, SMOKE.md the committed record.
- Plan-premise corrections: layout is editors/vscode/ (plural; the
  plan's editor/ was a typo); the apostrophe "tombstone" is a LIVE
  guarded command-mode single-quote region in micro — it PORTS
  (spec-equivalence), the tombstone was the earlier unguarded
  version.
- Scaffold: by hand (smaller than yo code). Indent rules: NONE
  (keep-previous-indent matches weir continuation style; guessing
  the offside grammar is worse than neutral). autoClosingPairs
  carries @" (multi-char opens work); """ deliberately unpaired.
- Drift guard shipped: micro gained `# rule:` annotations (20), the
  tm repository keys are the same 20 ids, e2e diffs the sets.
- Raw-string regions were ALREADY in micro (the raw-strings session's
  ride-along) — the check item resolved with no extra work.

## Pre-made decisions (as blessed; see the plan message for full text)

- Layout next to micro; package.json + extension.ts + tmLanguage +
  language-configuration + shared README rules.
- vscode-languageclient, stdio, PATH-resolved binary with
  weir.serverPath escape hatch, .weir + shebang firstLine.
- No server changes — client differences are server findings with
  frame-level pins, never client workarounds.
- TextMate ports the micro inventory rule for rule; Oniguruma extras
  only to simplify existing rules; add-to-both-or-neither, mechanized
  by the inventory test.
- Smoke over test-harness: SMOKE.md, version-stamp first line.

## Parked (unchanged)

- Marketplace publishing — with the OSS park.
- Semantic tokens — standing park; a VS Code client STRENGTHENS the
  reopen case (command-mode highlighting is what TextMate cannot do),
  trigger unfired.
- Debug adapter, task provider, snippets; other editors (neovim,
  helix) — config stanzas on demand.
