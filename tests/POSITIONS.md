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
- lambda body (single-line AND the multiline block form
  [D:multiline-lambda] — the dangling `(fun ->` opener arms in
  statement heads, pipeline stages, and let-RHS continuations;
  bracket-continuation and district interiors do NOT arm it)
- paren interior
- list-literal elements
- record-literal field values
- indexer interior (`xs[i]`)
- interpolation holes (`$"...{e}..."`)
- command-arg splices (`(expr)` in command mode — scalar-checked)
- argv splat `$@name`/`$@(expr)` [D:argv-splat] — every command-arg
  position (statement, param-ful/block-let RHS, sigil interiors,
  districts, env chains); REJECTED at the command head (the head is
  ONE program — `^$name` is the dynamic-head spelling) and mid-word
  (N words can't build one word)
- dynamic command head `^$name`/`^$(…)` [D:dynamic-head] — the head
  VALUE position: string-exactly checked (checkDynHead; a seq capture
  refuses with bind-and-pick), every command position (statement,
  let-RHS, sigil interiors, proc slot, reified chains via the erased
  EDynProg wrapper); `^$@`, `^$"…"`, and bare `^$` REJECTED with
  teachings guarded ahead of the head attempt
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
