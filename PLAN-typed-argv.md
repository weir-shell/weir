# weir — typed argv: `Args.load` over records and unions

Status: BLESSED (user 2026-07-23, shaped across the review thread).
AMENDED 2026-07-23 post-attributes: the attributes session landed
FIRST (a sequencing error, filed in the advisor ledger — this plan's
consumers were written into Args.load before Args.load existed; the
stop-and-report resolved by landing all four registered names
legal-and-inert). This session is therefore the ACTIVATION: it
builds the loader and flips Short/NoShort/Doc/Positional from inert
to bound, inheriting the attributes plan's carried-over done-when
clauses. DEPENDENCY, per the PROCESS rule this error minted: gated
on the attributes SESSION REPORT (landed, committed), not its bless.
EXECUTED 2026-07-23 — see the completion addenda.

Origin: the cmd-args streamline request. The design premise: argv is
the same species of stringly runtime boundary as the environment,
and weir has solved it twice — `Args.load` is the typed-boundary
pattern's SIXTH customer (porcelain, json, env, dotenv, http-parked;
now argv), with `Env.load` as the near-verbatim template. The
script's own front door is the last unchecked boundary in a
fail-before-effects language; this closes it.

## The two declared shapes

    // FLAGS — a record (jira-branch, bicep shape)
    type Cli = { clean: bool;              // --clean, presence
                 port: Option<int>;        // --port N, optional, parsed
                 env: string }             // --env NAME, required
    let cli = Args.load Cli

    // SUBCOMMANDS — a union of records (git-subrepo shape)
    type Cmd =
        | Clone of CloneArgs
        | Pull of PullArgs
        | Status                            // no-payload case = bare word

## Pre-made decisions (abridged; the full text is the blessing message)

- DECIDED — field name → kebab-case flag (`dryRun`/`DryRun` →
  `--dry-run`, `noFF` → `--no-ff`, `useHTTPSNow` →
  `--use-https-now`); hump-style variance collapsing is a
  check-time duplicate-flag error.
- DECIDED — shorts: derivation default (first letter, IFF
  unambiguous; contested letters derive for NOBODY, invocation
  errors list candidates) + `[<Short>]` override (derivation
  yields to declaration — the derived short retires; --help is the
  truth) + `[<NoShort>]` suppression; `h` reserved (never derives;
  `[<Short "h">]` rejects at attachment).
- DECIDED — field typing = the Env.load scalar rules: bool =
  presence (valued booleans rejected); string/int = required;
  Option<string|int> = optional; Option<bool> rejected at check
  with the presence explanation; everything else rejected in the
  Env.load message family.
- DECIDED — unions select subcommands: first token vs lowercased
  constructor names; single-record payloads only; bare cases are
  bare words; unknown → did-you-mean; missing → the case list.
- DECIDED — strict by default; no positionals; no catch-all;
  collect-then-raise (one boundary error, all problems).
- DECIDED — `--help` derives (consuming Doc), pre-validation,
  stdout, exit 0; `[<Positional>]` fires its not-yet.
- DECIDED — mechanism: the Env.load bespoke arm's sibling; the
  union acceptance is the delta. Checker touched ⇒ full ceremony.
- DECIDED — interaction sweep (statement rule, script-only scope,
  args slicing, casing, × Env.load) and the flagships as the e2e.

## Completion addenda (2026-07-23)

### Done-when, discharged

Both declared shapes run. A FOUR-problem invocation (typo'd flag,
unparseable int, stray token, missing required) reports all four,
collected; `--verbos` did-you-means `--verbose`; `--help` prints
derived usage — short truth AND Doc text — on an invalid
invocation, exit 0, before validation; `[<Short "e">]` beats
derivation and visibly retires `--env`'s derived short in --help;
`[<Positional>]` fires "positionals are not yet supported" at
check time; both flagships load typed argv (jira-branch: the Cli
with the `[<Short "c">]` worked example; git-subrepo: the
Status/Fetch/Pull union front door, live repo-pair smoke green,
subcommand did-you-mean demonstrated on `pul`). The attributes
plan's carried clauses discharge here: all four names bound
(Short/NoShort into derivation, Doc into --help, Positional into
the not-yet); the `-h`-reservation pin holds through the loader.
730 unit tests, full e2e, timing unchanged (8ms/11ms).

### Resolutions the plan left open

- Repeated flags REJECT ("'--env' is given twice") rather than
  last-winning — strict-by-default extended; cheap to relax later,
  expensive to regret.
- `--flag=value` spelling is not parsed (two-token only, matching
  the whole-argv law); it lands in the unknown-flag error with
  did-you-mean pointing at the two-token form.
- The one-source-of-truth mechanism: `Check.Argv` (kebabFlag +
  shortTables) is consulted by the checker, the runtime loader,
  and the usage renderer — check-time truth, runtime resolution,
  and --help output cannot drift by construction.

### Notes

- The subrepo subcommands carry `--subdir` as a flag — the
  positional-payload-operand shape the park's sharpened reopen
  vector names (`pull libx` read better than `pull --subdir
  libx`); the first live receipt for that vector, on record.
- Two Parked bullets in the blessing (short-form explicit
  declaration; per-field help) were pre-attributes residue — both
  shipped this session via `[<Short>]`/`[<Doc>]`; the parks are
  closed, not waiting.
- `weir -e` rejects type declarations, so check-side e2e pins run
  through `weir check /dev/stdin`.

## Parked

- **Positionals** — reopen vector SHARPENED: subcommand-payload
  operands (`clone URL DIR` vs `clone --remote URL`); the
  marker-question fight is the reopened session's first section;
  `[<Positional>]` is registered and reserved. First live receipt:
  the subrepo `--subdir` flag (see notes).
- **Trailing file lists** (`mytool --verbose *.txt`) — [prediction,
  on record: the first reopen knock] — reserved additive path:
  an explicit `Args.loadWith rest` variant, never a magic field.
