# weir — scoped processes: `within proc`, and the no-orphan law

Status: BLESSED (user 2026-08-13); LANDED same day — [D:scoped-procs]
is the ledger row. One session, as sized.

## Why, with the receipt

The strongest evidence in the repo: weir's own battery is ~4,800 lines
of bash, and the single largest reason is this gap. Every server block
spells the same five-step ritual — `python3 … &`, `awaitTcp`, use,
`kill`, `wait` — and the CI campaign's worst rounds were exactly this
ritual going wrong (round 28's `$!`-held-the-dir, round 33's unguarded
kills dying silently, the reap-before-rm law). The pattern is not a
harness quirk; it is the devops daily shape: start a
server/port-forward/tunnel, wait until ready, use it, tear it down AND
its tree, always.

weir already owns every hard part separately: `within`'s
guaranteed-cleanup scopes, pfirst's tree-kill (Windows-proven), the
exit hook's registration-not-scanning sweep, `poll`'s bounded waiting.
What's missing is the spelling that composes them.

## The design

    within proc srv = python3 -m http.server 8080
        poll timeout=10s watch=srv
            Net.tcpUp 8080
        let body = Http.fetch "http://127.0.0.1:8080/"
        print body

**The scope IS the lifetime.** At every block exit — normal, raise,
either — the process TREE is killed (idempotent if already dead) and
reaped. `within tmp`'s discipline applied to the resource that
actually bites. Nested scopes release LIFO; a `within proc` inside a
pmap worker is worker-local (cd's precedent).

**The RHS is a command line**, parsed exactly as a bare command
statement (splices, `^`, the argv law — nothing new). The `proc` kind
announces command mode, so this does NOT reopen the parked let-RHS
question.

**The handle is data.** `srv.pid : int`; `Proc.running srv : bool`;
`Proc.wait srv : int` (block until natural exit, code as data — the
way to NOT kill at scope end is to wait first); `Proc.stop srv : unit`
(early teardown; scope exit then a no-op); `Proc.tail srv :
seq<string>` (the last ~100 lines of the child's output).

**`poll` gains `watch=`** — the declarative await, riding poll's
existing options record and indented bool body:

- watched process DIES → poll raises immediately: "watched process
  exited (code 1) after 300ms" + the stderr tail — never a blind
  timeout on a server that crashed at startup.
- plain TIMEOUT → the exhaustion message appends the watched process's
  state and tail (up-but-wrong-port names itself).
- `watch` is orthogonal to the body/`until` machinery — value-yielding
  readiness checks inherit unchanged.

This is NOT a derivable wrapper: poll owns its exhaustion message, so
only poll can put the child's words in it — the reason `watch` is a
key and `Proc.await` is not a member.

**Output spills, never interleaves.** The child's stdout/stderr go to
files under a managed tmp dir (the within-tmp machinery, registered
with the same exit hook). `Proc.tail` reads the tail; watch-errors
carry it automatically. No live streaming into the parent —
capture-or-stream stays a per-child, whole-child decision.

**The exit hook generalizes.** [D:exit-hook]'s registered-cleanup list
gains process entries beside tmp dirs: a hard exit or signal kills
before it rmdirs. Registration stays per-process; two concurrent weirs
never touch each other's children.

## Considered and DECLINED — record these so they stay closed

- **Bare `&` / job control.** An unscoped background process is an
  orphan the language promised not to make; job control is on the
  login-shell out-of-scope list already. The scope is the feature.
- **`Proc.await`.** Two reasons: the readiness wait IS `poll` + a
  probe (composition covers it — the sumBy precedent), and the error
  quality lives better in poll's own `watch=` key, which reaches the
  exhaustion message a wrapper never could.
- **`Proc.detach` (nohup-class).** A child that outlives the script is
  a daemon, and daemons belong to systemd/launchd — a script that
  "starts a service" for real writes a unit file. Re-open only on a
  receipt that is not "I miss nohup."
- **REPL cross-statement handles.** At the prompt a `within proc`
  block ends and the child dies with it — consistent, and the
  multiline editor makes the block form usable. Session-lifetime procs
  are job control by another door.
- **`watch=` on `retry`.** retry is the transient-failure form, not
  wait-for-ready; follows on a receipt, recorded as such. Single
  handle in v1; a seq of handles on receipt.
- **An `Http.await`/`Net.await` family.** `poll` + one boolean probe
  compose; a member per protocol is an inventory with no ceiling.
  `Net.tcpUp` is the one new probe (an HTTP probe is already spelled
  `Http.send |> _.status`).

## Open questions (the session's first probes)

1. **The `watch` field's type** in the Poll options record —
  `Option<handle>` with None default is the honest shape; whether the
  inline `watch=srv` sugar can arrive as `Some srv` under the existing
  key=value rules is the FIRST probe. If the sugar cannot wrap
  cleanly, surface the wrinkle, don't force it.
2. **Spill vs ring buffer** for child output — recommendation: files
  (inspectable after; the tmp-dir machinery already exists), tail
  reads the file.
3. **Natural-exit reporting**: after `Proc.wait`, scope end stays
  silent (the code was offered as data) — confirm or overrule.

## Bars

- **The acceptance**: the snippet above, as a fixture — starts, awaits
  via `poll watch=`, fetches, and at scope exit the tree is dead and
  reaped, PINNED including the no-orphan half (no surviving pid).
- **Died-at-startup fails the poll immediately** with the stderr tail
  — pinned with a child that exits 1 before listening.
- **Timeout names the watched state** — up-but-never-ready appends the
  tail to poll's exhaustion message; pinned.
- **The raise path kills** — an exception mid-scope leaves no process
  (the within-tmp raise pin's twin).
- **Hard exit sweeps**: a SIGTERM'd weir leaves no registered child
  (POSIX; Windows per the exit-hook's stated arms).
- **pmap-worker locality**; LIFO for nested scopes; `Proc.stop` then
  scope-exit is not a double-kill error.
- **New Value case discipline**: `--no-incremental` FS0025 sweep, Show
  arm (`proc(pid=…, running)`), Eq excluded with the message.
- Parser changed ⇒ 3 fresh 10k seeds; tree-sitter zero-ERROR on a
  `within proc` fixture.
- **The dogfood receipt**: at least one real e2e bash server block
  gets a weir twin fixture proving the pattern (the bash battery
  itself stays bash — it tests weir from outside).

## Work items

1. Parser: the `proc` kind in the within header, RHS as command mode.
2. The handle Value + Proc module (running/wait/stop/tail) +
   `Net.tcpUp`; spill wiring.
3. poll's `watch=` option (record field + sugar probe + both error
   enrichments).
4. Scope-exit tree-kill+reap sharing pfirst's machinery; exit-hook
   registration beside tmp dirs.
5. Pins per bars; SKILL/GUIDE (the five-step bash ritual shown next to
   its weir spelling); DECISIONS records the no-orphan law and the six
   declines; NOTES.

**Done when:** start-await-use-teardown is the snippet above; nothing
survives any exit path; a startup crash names itself with the child's
own words; a timeout names the watched state; `&` stays impossible;
the declines are on the ledger.

## Outcome (what landed vs the text above)

Everything above landed as written, with these deltas, all on the row:

- `srv.pid` is `Proc.pid srv` — a MEMBER, not a field. The
  record-shaped handle was asked and ruled back post-landing: a
  record's fields freeze at construction (z.running would be a
  spawn-time lie); operations live in modules by weir's grain. A lone
  `.pid` field stays available on a receipt.
- The open questions resolved: watch= is a HEAD KEY peeled before the
  options desugar (never a record field — the sugar probe dissolved by
  construction); spill FILES won; natural-exit stays silent.
- Findings beyond the plan: the assembler's dangle rule had to learn
  that a proc head's block joins at the machine boundary (three join
  sites, string-blind + word-bounded detector); def-less builtin type
  names (Proc, Map) were silently declarable — now reserved; the
  #help retry/poll/within gap closed via form-head builtinDocs entries
  (within's kinds derived from the table).
