# PLAN — hunt-2 backlog: runtime, checker, LSP, XML

Status: LANDED — all five items (every one ruled daily-driver by the user,
dev/BACKLOG.md "From the second bug hunt"; PLAN-daily-driver W4).

## Items

1. **Secret in "command failed" errors.** Every message weir renders
   from a command's argv (nonzero exit, `| line`'s count error, the
   chain forms) masks a word spliced from a `Secret` as `***`; the argv
   the process receives is unchanged. Mechanism: the argv builder
   registers each Secret-spliced word's clear value; the one argv
   renderer (`Proc.showArgv`) masks any word equal to a registered
   value. Value-equality over-masks only a word that IS the secret's
   text — the safe direction. SECURITY.md: the non-claim becomes a
   scoped claim [D:secret-argv-mask].
2. **Re-enumeration through a function, a loop, a derived binding.**
   A function or seq binding that reads a tracked binding carries its
   pulls: each use of the carrier replays them at the use site; a read
   inside a function passed to a `Seq.*` function (a `for` body) counts
   twice; the alias gap closes (an alias is a derived binding). The
   corpus is compared before/after [D:reenum-indirect].
3. **Runtime column inside a one-line form.** An application that
   raises (`fail`, a builtin) is blamed at its function's column, not
   the line's first column [D:runtime-position-call].
4. **LSP analysis cache.** One analysis per (file, text), shared by
   hover/completion/definition/tokens; any document notification drops
   the cache (a module edit reaches importers) [D:lsp-analysis-cache].
5. **`from xml` mixed content.** A text field read from an element with
   child elements refuses, naming the element and the field
   [D:from-xml-mixed].

## Decisions

All within the user's "fix all" ruling; none needed a new ruling.
