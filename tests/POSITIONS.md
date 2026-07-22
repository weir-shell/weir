# Expression-position inventory (the position-matrix checklist)

Written 2026-07-20 (hardening sweep). Origin: the let-RHS sequencing
miss — `;` was wired into N body positions and missed the let-RHS
pair ("wired into N, missed N+1"). New expression forms and new
tokens ship with a sweep over THIS list: each position gets a pin or
an explicit exclusion with a reason, enumerated in the session notes.
Copy the list; do not re-derive it. Update it when a position is
added (sigil interiors were an addition) — a stale inventory is the
same bug one level up.

Expression positions:

- statement (SExpr — the unit rule applies)
- let-RHS, top-level (bare-command form AND expression form)
- let-RHS, block let (assembler-closed)
- let-in value (single-line form)
- then-branch / else-branch
- match scrutinee / match arms / `when` guards
- lambda body
- paren interior
- list-literal elements
- record-literal field values
- indexer interior (`xs[i]`)
- interpolation holes (`$"...{e}..."`)
- command-arg splices (`(expr)` in command mode — scalar-checked)
- sigil interiors are COMMAND grammar, not expression grammar — an
  expression form does not need a sigil pin unless it is also legal
  in command mode
- sequence elements (`e1 ; e2` — both sides)
- REPL line / `-e` line (echo semantics differ from scripts)

NOT expression positions (do not sweep, say why if asked):

- district lines — command grammar only, by design (bind values
  outside the block; districtLineCheck enforces)
- type-declaration bodies — declaration grammar
- `.env` files — data, parsed by Env.fromFile, never evaluated

Pattern positions (added with the Regex pattern, 2026-07-22 — sweep
these for any NEW pattern kind):

- match-arm top level
- nested in a tuple pattern
- constructor payload (parens required, F#-style)
- alongside `when` guards
- binder position (let / lambda params) — refutable kinds REJECTED here
- exhaustiveness interaction (does the kind ever complete a match?)
