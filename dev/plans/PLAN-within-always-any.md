# PLAN: `always` after any `within`

**Status: PARKED** — resume once both 2026-10-06 stacks (parser:
armed-inherit → if-else-commands → tilde → env-prefix → tilde-literal;
boundary: wire-table → wire-codecs → print-canonical) land on main.

## The receipt

Nesting. A scoped resource plus an exit action needs two `within`s today:

```weir
within tmp d
    within
        build-into $d
    always
        cp $"{d}/report.html" .
```

`always` is accepted only after a bare `within`; after any other kind it
is a parse error whose expecting-list points into the body, not at the
rule. [D:within-always] made this deliberate and named its own reopen
trigger: "Bare-only: `within proc … always` waits for the nesting receipt
(ordering vs tree-kill unruled)". This is that receipt.

## Rejected first: a body-less `within` as a declaration

F#'s `use` shape (a `within` without a body scoping to the end of the
enclosing block) was considered and dropped: the scope's edge is no
longer visible, and a blank line or a refactor silently splits one scope
into two — worst of all for a body-less `always`.

## The proposal

A trailing `always` after **any** `within` kind:

```weir
within tmp d
    build-into $d
always
    cp $"{d}/report.html" .
```

**The ordering ruling** (the open question): `always` runs **inside** the
scope — while the resource is still held — and the resource releases
after. Defined as a desugar, so no new runtime:

```
within K b            within K b
    BODY        ≡         within
always                        BODY
    C                     always
                              C
```

Per kind:

| Kind | Consequence |
|---|---|
| `tmp` | the cleanup still sees the directory (copy artifacts out) |
| `cd`, `env` | the cleanup runs in the same directory / overlay |
| `lock` | the cleanup runs under the lock — no race with the next holder |
| `proc`, `serve` | the cleanup runs while the process is alive (dump a log, graceful stop); the tree-kill follows — the ordering [D:within-always] left unruled |

The binder (`d`, `p`) is in scope inside `always`. Everything else is
[D:within-always] unchanged: normal exit, raise, `exit n`, SIGINT/SIGTERM;
the original error wins when both fail; teardown continues outward;
`exit` inside `always` stays refused.

## Work

1. Parser: accept a trailing `always` after every `within` kind (the
   `until` machinery the bare form already rides); desugar as above.
2. Check: nothing new if the desugar happens at parse; confirm binder
   scoping and the `exit`-in-`always` refusal through the nesting.
3. Ledger: a new row superseding [D:within-always]'s bare-only clause.
4. Docs: GUIDE scoped-resources section, docs/reference/scopes.md, SKILL.
5. Tests: unit parse shapes per kind; e2e matrix — every kind × the four
   exit paths, pinning "cleanup before release" (tmp dir present inside
   `always`, proc alive inside `always`, lock held inside `always`).

## Open

- Value position: does `let x = within tmp d … always …` carry the
  body's value through (the bare form's rule) — confirm, don't assume.
- `within serve`: confirm a handler in flight when `always` starts is
  not cut short by the cleanup itself.
