# PLAN — hunt-2 backlog, checker/adapter items

Status: LANDED (2026-10-15) — three BACKLOG items ("From the second bug
hunt", ruled daily-driver in PLAN-daily-driver W4).

## Items

1. **Inference order** — `a + b + 0` did not infer while `0 + a + b` did.
   `typeBinOp` refused `+` over two unresolved vars on the spot.
2. **User generics at the wire** — generic records refused at json/yaml;
   `from json seq<B<int>>` a parse error; generics undocumented.
3. **`from json` reading a `Yaml` field** — lets `gen types` keep a
   self-referential field ([D:gen-types-cycles]'s named alternative).

## Decisions

- **[D:plus-defer]** `+` over two unknowns returns a fresh result var and
  parks a retry on the three vars (`Ctx.Adds`); `bind` re-runs it the
  moment one resolves. A resolved operand decides through the existing
  arms; a resolved result decides both operands (each addable type adds
  only to itself), Instant excepted (Instant + Duration vs Duration +
  Instant stays ambiguous). Open at the statement boundary → the
  cannot-infer teaching at the first one written. Open vars stay
  MONOMORPHIC (excluded from let-generalization), so a later use in the
  same statement decides them — `let add x y = x + y in add 1 2` now
  infers int (F#'s behaviour for a local let); a top-level
  `let add x y = x + y` alone still refuses (the statement is the
  inference unit). The `-`/`*`/`/`/comparison int-defaulting is unchanged.
- **[D:wire-generics]** the adapter slot takes `B<int>` (also inside
  `seq< >`/`Map<string, >`), args are the one type grammar. Check
  monomorphizes: every fully instantiated generic record the type reaches
  becomes a def keyed by its rendered type (`B<int>`) in a WIRE-LOCAL env
  (`wireMono`), monomorphic records/tagged unions holding an instance are
  rewritten there too; the existing field law, closures, and readers then
  run unchanged. The def keeps its declared name, so values, renames and
  codecs (now keyed by `def.Name`) are B's own. json/jsonl/yaml only;
  toml/xml/table, Env/Args.load keep the monomorphic refusal. A bare
  generic name teaches the instance spelling; an open instance at a write
  takes the generic-wire teaching.
- **[D:json-yaml-field]** `Yaml` is json-admitted both directions (yaml
  already was). Read: object → `YMap` (document order), array → `YSeq`,
  number by the YAML scalar rule (int64-shaped → `YInt`, else `YFloat`),
  `null` → `YNull` (also for a required `Yaml` field, as `from yaml`
  does); missing stays an error (absence is Option's job). Write: the
  node writes as the JSON it denotes (value-directed arms after the
  tagged-union arm). `gen types` drafts a cyclic field as `Yaml` (with a
  note) instead of omitting it — supersedes [D:gen-types-cycles]'s
  omission; an all-cyclic definition now drafts too.
