# PLAN — hunt2 backlog: fmt items

Status: LANDED — items 1–4, item 3's history half on the user's ruling
(option b, skip) [D:history-utf8].

Source: dev/BACKLOG.md "From the second bug hunt" (user: every item
daily-driver, fix all).

## Items

1. **fmt formats a file that does not parse** (a yaml-district parse error;
   ordinary parse errors too — `let x = (1 +` was rewritten). fmt only
   proved the file *assembles*. FIX: `Fmt.formatSource` (the one entry point
   the CLI and the LSP share) takes the path and refuses when the checker's
   own analysis (`Script.analyzeLines`) reports a `parse` error in that
   file: "cannot format: line L:C: …", exit 3, file byte-identical; the LSP
   returns no edits. Same parser as `weir check`, so fmt never formats what
   check calls unparseable. New row [D:fmt-parse-gate].
2. **fmt respaces inside interpolation holes on multi-line content**:
   respace ran over `$<<<`/`$$<<<` heredoc (and yaml) content lines
   (`{a}` → `{ a }` passes the sexpr guard — the hole's expression is
   unchanged) and over lines that begin inside an open `$"…{` hole spanning
   lines (the per-line string mask starts outside a string). FIX: respace
   skips district content lines ([D:content-bytes]) and lines that begin
   inside a string; re-indenting a hole-continuation line stays (its
   leading whitespace is hole whitespace, normalized by the assembler's
   join). Cites [D:fmt-respace] [D:content-bytes].
3. **History file and fmt rewrite invalid UTF-8 as U+FFFD.**
   - fmt: already refused since H47 (`readEntryScript`, located
     `file:L:C: invalid UTF-8`, exit 2, untouched) — e2e pins it; add the
     byte-identical assertion.
   - history: NOT settled by [D:source-utf8] (a source-file ruling; the
     backlog says so). RULED (b) skip, LANDED: invalid lines stay out of
     recall and are written back byte-for-byte on every rewrite, one
     located note [D:history-utf8].

4. **fmt ends every file in exactly ONE newline** (user ruling 2026-10-15,
   option a): trailing blank lines go, in `formatSource`, guarded by an
   assembly-equality check (blanks never end a statement [D:body-blanks]).
   The tracked corpus was already clean — no fixture regenerated.

## Ruling owed — history and invalid UTF-8

The REPL loads history with `File.ReadAllLines` (U+FFFD substitution) and
REWRITES the whole file on a load-dedup or a re-entered line, so an
invalid byte is silently replaced on disk. Options:

- (a) RECOMMEND — refuse, located: a history file that is not UTF-8 turns
  history off for the session with the existing one-line note, naming
  `file:L:C` of the first bad byte; the file is never read into memory nor
  rewritten. The same shape as an unreadable history file today
  [D:repl-history], and [D:source-utf8]'s "located, never silent" law.
- (b) skip: load the valid entries, drop the invalid lines from recall,
  and keep them byte-exact on any rewrite (lines held as raw bytes) — no
  data loss, history keeps working; one note per session.
- (c) preserve fully: treat history as bytes (Latin-1 round-trip for bad
  lines) and recall them with U+FFFD displayed but written back verbatim
  — most code, recall of a line that cannot be typed back faithfully.

## Decisions

- [D:fmt-parse-gate] (new) — fmt refuses a file `weir check` cannot parse.
- [D:history-utf8] (new) — history skips invalid-UTF-8 lines and keeps them
  byte-for-byte.
