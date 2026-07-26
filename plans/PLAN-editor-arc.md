# weir — the editor arc: three sessions from LSP-complete to Zed

Status: BLESSED (user 2026-07-26). Three sessions, strictly ordered
only where the dependency is real: Session 1 is independent; Session 3
rides Session 2's artifact. Origin: the editor-config session's sized
finding (formatting unwired) plus the two advisory questions
(definition, Zed) answered against the code on 2026-07-26.

## Session 1 — LSP requests: formatting + go-to-definition v1

One session, both requests — they share the shape ("most-expected
feature, silently missing") and the plumbing. Zero language change.

DECIDED — formatting runs on client-sent text, never the file on
disk: the client's buffer is the truth (unsaved edits), and it keeps
the SECURITY non-claim exact (the server reads what the editor sent).
One whole-document TextEdit; `"documentFormattingProvider":true` joins
the capabilities string (Lsp.fs:451).

- The fmt pipeline is the SAME one `weir fmt` runs — the respace
  guard's refusal contract carries over verbatim: on refusal, return
  null/no-edits, never a partial edit. [Verify in-session what
  formatLines does on a file that does not PARSE — format-on-save
  fires on broken files constantly; the answer must be no-edits, not
  an error response. Pin whichever it is.]
- Editor options (tabSize etc.) in the request are IGNORED — weir fmt
  is canonical, 4 spaces, not negotiable. Say so in docs/editors.md
  (one line) so nobody files it as a bug.

DECIDED — definition v1 is top-level bindings only, null otherwise.
The hover plumbing (analyze → toLogical → nodeAt) hands over the
`TEVar` under the cursor; the reverse lookup is: last statement above
the use whose `KLet`/`KLetPat` binds that name, binder column found
textually in the logical line, `Script.translate` back to physical,
one Location. Top-level shadowing falls out of "last above".

- Params, match binders, block-lets: null, conservatively — the
  checker records no binder spans. Never wrong beats sometimes wrong.
- Builtins/library names: null (no source to go to).
- The boundary is a NAMED park with its criterion: binder spans in
  Check are the prerequisite for full definition AND rename AND
  references — one medium session, reopened when any of the three is
  demanded by a real user. The park entry says all three ride it.

The verification bar: protocol pins in the Lsp unit tests (the
semanticTokensFor precedent), PLUS the nvim headless rig re-run
end-to-end — /tmp/editors still holds the tarball; the drive script
gains `textDocument/formatting` (assert the edit's text ==
`weir fmt` output) and `textDocument/definition` (assert the range
lands on the `let` line). docs/editors.md matrix gains the two
columns. Size: ~60–100 lines formatting, ~60–90 definition, plus pins.

Done when: format-on-save works in the tested editors; definition
jumps to top-level lets and returns null elsewhere (pinned both ways);
the refusal and no-parse paths return no-edits (pinned); capabilities
advertise both; the matrix updated; the binder-span park filed with
its three-customer criterion; all green.

## Session 2 — tree-sitter-weir: the grammar that unlocks three doors

The fast-follow with three customers in one artifact: Zed (hard
requirement — no grammar, no language), Helix colors (its only
highlighting path), GitHub file rendering (gated further, see below).

DECIDED — the grammar is a RENDERER, not a second parser. The one
truth stays `weir check`'s pipeline; the grammar's job is
better-than-grey highlighting. It may over-accept freely; it must
never be cited as the language definition. State this in its README
with a pointer at SEMANTICS — the one-pipeline law, extended to
external tooling.

- Scope the grammar pragmatically: lexical classes first (comments,
  strings + raw strings, interpolation holes, numbers, keywords,
  type/ctor casing, `$`/`$@`/`!` sigils), statement-level shapes
  second (let/type/match heads, pipe operators, command-line heuristic
  — a bareword head after statement start). weir's assembler
  (logical-line reconstruction, offside) is NOT replicated — accept
  the approximation and record where it shows (a continuation line
  may highlight as a fresh statement; acceptable for a renderer).
- [DECIDE at session start, user call flagged now: repo location.
  tree-sitter tooling and every consumer (Zed extensions, Helix
  languages.toml grammar source, linguist) want a DEDICATED git
  repo (tree-sitter-weir), not a subdirectory. If a separate repo
  is unacceptable, Helix/Zed can consume a subdir ref but the
  friction is real and the report should price it.]
- The corpus is the acceptance: run the grammar over every `.weir`
  in examples/ + tools/ + the SKILL/GUIDE doc blocks; report the
  ERROR-node rate. Bar: zero ERROR nodes on the corpus (the grammar
  over-accepts, so this is reachable); highlight queries eyeballed on
  the flagship in-container via helix (the pty rig renders colors —
  the reconstructor already exists).
- Helix lands in the same session: grammar source added to the
  languages.toml block in docs/editors.md + highlight queries shipped
  where helix expects them; the editors matrix's "tokens n/a" cell
  flips to "tree-sitter ✓".
- GitHub rendering is NOT promised: linguist acceptance has
  popularity criteria weir does not meet yet. The session ships the
  grammar linguist would consume and STOPS; the report names the
  criteria so the item re-opens when they're met.

Done when: the grammar parses the corpus with zero ERROR nodes;
highlight queries render the flagship in helix (verified in-container,
screen-reconstructed); the repo-location decision is recorded; the
one-pipeline disclaimer is in the grammar's README; docs/editors.md
helix block updated; GitHub gating named in the report.

## Session 3 — the Zed extension (rides Session 2)

Small, mostly publishing mechanics. Hard dependency: Session 2's
grammar at a fetchable git ref.

- extension.toml + languages/weir/config.toml: file types (weir),
  shebang (weir), `//` comments, indent 4, and the language server —
  `weir lsp`, the one command, same as everywhere.
- The verification honesty: Zed is GUI-only — [verify whether current
  Zed runs in the container at all; expected NO]. If not, the
  extension ships marked UNTESTED-in-container with a 5-step local
  verification script for the user (install dev extension, open
  flagship, error/hover/format check), and the matrix says so — the
  editor-config session's discipline, re-applied.
- Publishing is a decision, not a default: shipping to
  zed-industries/extensions puts weir's name in a public registry —
  the session prepares the PR and STOPS for the user's explicit go.
- docs/editors.md gains the Zed section (config-free once the
  extension is installed; dev-extension path until published).

Done when: the extension builds against the pinned grammar ref;
`weir lsp` is the invocation; the local verification script exists;
the publish PR is prepared but NOT sent; docs updated; the matrix
row added with its tested/untested truth stated.

## Sequencing and the standing queue

1 → independent, any time. 2 → before 3, hard. The VS Code extension
plan (elsewhere) is untouched by all three but Session 1 should land
before it ships format-on-save expectations. After the arc: the
binder-span session (definition/rename/references) sits parked with
its criterion; launch-phase docs items continue in parallel.
