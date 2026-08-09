# weir — the LSP chain: one pipeline, check --json, weir lsp

Status: EXECUTED (2026-07-21; branches pipeline-one, weir-lsp — the
blessed plan's full text is in the conversation record).

Session 1: Script.checkStatement owns parse/dispatch/check/gate with
physical spans inside; runner/REPL/-e/oracle-mirror rebased; gateExprs
switch (scripts gate, REPL/-e echo — caught in-session); ZERO pin
edits; incident pin annotated as regression guard; reported deltas:
-e reports real RHS errors on non-expr forms, REPL casing errors
gained the standard underline; dead tryRun deleted.

Session 2: weir check [--json]; statement-level recovery (multi-error
files fully reported); codes seeded from message families; AOT-safe
hand-rolled JSON; warnings exit 0 (decided); check median pinned 10ms
(the LSP budget).

Session 3: weir lsp v1 (diagnostics/hover/completion, stdio).
Hand-rolled JSON-RPC framing/writing BY PREDICTION per the gate
(reflection serializers banned); READER corrected to JsonDocument on
user review (DOM is AOT-safe; the hand-rolled reader's surrogate-pair
bug is the archaeology — unicode probe pinned); no incrementality (10ms license); text-only
server state; Complete.suggest re-plumbed + PATH commands at line
head; protocol integration probes against the AOT binary; micro
config in editors/micro/README.md; GUIDE editor-setup section.

Parked (unchanged): go-to-def/rename/references; formatting->fmt
bridge; VS Code shell; semantic tokens; incremental checking (reopens
only if the timing pin degrades).
