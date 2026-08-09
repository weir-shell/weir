# weir — mini-plan: the spawn-spec refactor + `feed`

Status: EXECUTED (landed 2026-07-24) — as blessed: BLESSED (user 2026-07-24). One session. Origin: the
command-value family audit at the miner-plan review — six names over
a 3-axis product (output × env × stdin), with `feed`/`feedEnv` about
to be minted as seven and eight.

(Blessed plan text as delivered; the executed decisions below.)

## Session report (2026-07-24)

- Movement 1 (behavior-preserving): the internal Spec
  (Prog/Args/Env/Input) + ONE starter (psi/env/cwd/not-found/stdin
  writer) + the reap tail; the output axis became consumer functions
  (linesOf/streamCodeOf/completedOf). Public wrappers unchanged.
  THE CONTRACT HELD: zero pin edits across the full battery + e2e;
  both byte-identity pins re-ran unchanged (run ≡ cmd |> print,
  !() ≡ orFail-default) — now structural. One preserved subtlety,
  noted: complete disposes WITHOUT the kill tail (it has read both
  pipes to completion), exactly as before.
- Movement 2: feed/feedEnv as constructors 7 and 8. Input pulls
  lazily on the writer task — pull-count pinned (1M-line counted
  source into `head -1`: pulls bounded by the pipe buffer, asserted
  ≪ total) and e2e'd on the binary (huge range + head -1
  terminates). EOF-on-exhaustion pinned (sort). The miner's sha256
  shape is the e2e. DECISIONS row carries the family line.
- The park banked verbatim (user-facing spec = EXPOSE, not build;
  reopen: ninth name or a new axis — Cwd next, the Session seam).
- SKILL gained the feed lines; PLAN-miner-receipts amends its
  sequencing on unstash (feed landed here).
