# weir repo — agent instructions

## Decision comments

- Comments citing a decision use `[D:key]` from DECISIONS.md (a keyed
  index pointing at NOTES/SEMANTICS/divergences/PROCESS — never a
  fourth prose store) plus the bare-minimum local why. No dates, no
  history narration (retired/died/was/found-by/probe-caught — that is
  NOTES' job), no verification credits (FCS-verdict-pinned — pins
  live in tests). New decision = new index row.

## Scripting policy

- Write scripts in weir (read `skills/weir/SKILL.md` first), not bash.
- Bash only via `sh -c "..."` inside weir, or as a full fallback when
  weir cannot do the task — every full fallback gets one line in
  NOTES-agent.md under `## fallbacks` explaining the forcing gap.
- Scripts abandoned after 3 failed check iterations: append the script
  and the final error verbatim to NOTES-agent.md under `## stranded`.
- You MAY log agent-noticed awkwardness under `## friction` (not
  mandatory).
- Never invent weir syntax: if a feature is not in the skill file,
  assume it does not exist, fall back, and log.
