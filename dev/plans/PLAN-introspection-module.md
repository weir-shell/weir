# weir — the introspection module (+ `pid`)

Status: BLESSED (user 2026-07-27), EXECUTING. RULINGS: D1 = `Self`
(accumulation argument); D1b = `Self.scriptPath` not `Self.path`
(disambiguation + the name matches the script-only boundary; `path`/
`sourcePath` rejected); D2 = clean break, no bare aliases (the
did-you-mean teaches the migration). Added pins: `Args.load`/`Env.load`
re-verified with a PIN (the one finding that would turn a rename into a
behavior change); `Self.pid` stable across reads (the property
fuzz.weir's lock liveness depends on). Groups the script/process
introspection globals under `Self` and adds `pid`. Origin: live dogfooding — a
script needs its own PID and today writes
`let mypid = $(sh -c 'echo $PPID') |> Seq.head`, a shell-out for a
value the runtime already knows; and the bare globals `args`/`stdin`/
`scriptPath` occupy names a user might want. Pre-publication is the
right time for the rename (the casing-rename reasoning).

## Two OPEN DECISIONS — for the user/advisor, NOT pre-decided

### D1. the umbrella NAME  (the one the advisor should weigh)

Which module name groups "facts about this running script/process"?
All are uppercase (the module convention — `Seq`/`Str`/`Env`/`Args`):

| name | reads as | for | against |
|---|---|---|---|
| `Self` | `Self.pid`, `Self.args` | evocative — "the script itself"; short | slightly abstract; no prior art in weir |
| `Script` | `Script.pid`, `Script.args` | concrete — these ARE script facts | `pid` is a PROCESS fact, not strictly the script's |
| `Proc` | `Proc.pid`, `Proc.args` | pid/stdin are process-level | `scriptPath`/`args` are more "script" than "process" |
| `Runtime` | `Runtime.pid` | catch-all, unambiguous | long; generic |

AGENT LEAN: `Self` (the umbrella covers script AND process facts, and
it is the shortest). But this is exactly the call to make in the open —
`Script` is the runner-up if "self" reads too abstract. Bless a name
and the plan proceeds; the whole rename keys off it.

### D2. clean break vs bare aliases

- **Clean break** (remove bare `args`/`stdin`/`scriptPath`) — one
  honest spelling; a stale bare `args` becomes an unbound-var error and
  the did-you-mean points at `<Name>.args`. AGENT LEAN, pre-publication.
- **Bare aliases kept** — the `bareAliasHomes` mechanism (Builtins.fs:
  1470) already supports a member available both qualified and bare;
  softer migration, but two spellings for one thing.

STATUS UPDATE: EXECUTED (2026-07-27). All rulings implemented; the
re-verified finding (Args.load's script-only gate keyed on the bare
`args` binding, not just Session.ScriptArgs) was caught by pin and
fixed (gate on the `Self` module's presence). `pid` int → command
splices use `show Self.pid` (argv is strings). Corpus/docs migrated;
898 unit, e2e (Self.pid stable-positive + scriptPath), 60 doc, 156
oracle, lsp-e2e, fuzz 4000, freshness — all green.

## Feasibility — CONFIRMED (diagnosed before drafting)

- Module members resolve to VALUES via mangled `"Module.member"` keys
  in the value env (Builtins.fs:1475-1477), so a value member
  (`<Name>.pid : int`) is no different from a function member
  (`Seq.head`). No new eval machinery.
- The introspection values are per-run (scriptPath/args/pid vary), so
  they inject in `baseEnvs` (Script.fs ~1340/1360, and the strict-mode
  copy ~1845) — where the bare globals already live — now registered as
  a module (`env.Modules`) plus mangled value bindings, instead of bare
  `env.Values`.
- `Args.load`/`Env.load` read `Session.ScriptArgs` DIRECTLY (Eval.fs:
  881), NOT the `args` binding — so the flag machinery is untouched by
  the rename. (Confirm in-session, but the source says so.)

## `pid` specifics

- `<Name>.pid : int` (weir int64), value = `System.Environment.ProcessId`
  — the current process, exactly what `$PPID` of the `sh` child
  returned. Stable for the process lifetime (correct for the fuzz.weir
  liveness/lock use that motivated this).

## Migration surface

- The four members: two type sites + one value site in `baseEnvs`
  (bare `Values` → a `Self`-or-whatever `Modules` entry + mangled
  `Values`). The strict-mode env copy too.
- Docs: SKILL.md (the `args : seq<string>` / `scriptPath` block, ~21-24,
  and the introspection mentions), GUIDE.md (~161-171, ~577).
- Corpus/examples: `repo-report.weir` (`match args`), `tools/fuzz.weir`
  (the `$PPID` idiom → `<Name>.pid`), and a grep sweep for every bare
  `args`/`stdin`/`scriptPath` use.

## Work items

1. Resolve D1 (name) and D2 (break/aliases) — blocked on bless.
2. Register the module in `baseEnvs` (both envs + strict copy): the
   four members, type + mangled value; add `pid`.
3. Migrate docs + corpus + examples; the `fuzz.weir` idiom simplified.
4. Pins: `<Name>.pid` types int and evals to the process id (exact);
   `<Name>.scriptPath`/`args`/`stdin` type + eval unchanged in VALUE
   (only the spelling moved) — pin one of each; `Args.load`/`Env.load`
   byte-identical (they never read the binding); the did-you-mean on a
   stale bare `args` (if clean break) points at the qualified form.
5. SEMANTICS/SKILL: the introspection set stated as a module in ONE
   place; NOTES line; DECISIONS row (the name + break/alias choices,
   with reasons — and pid).

## Bars

- Semantics unchanged — the four values compute exactly what they do
  today; only the spelling and namespace move. Pin values before/after.
- `Args.load`/`Env.load` zero-diff (they read Session.ScriptArgs).
- The whole battery + a corpus/doc sweep green (the doc-tests are the
  completeness proof for the rename, the casing-rename precedent).

## Done when

The introspection values live under the blessed module name with `pid`
added; `let mypid = <Name>.pid` replaces the shell-out; the flag
machinery is byte-identical; the break/alias decision is implemented as
chosen; docs and corpus are migrated; all green; the name and the
break/alias choices are recorded with their reasons.
