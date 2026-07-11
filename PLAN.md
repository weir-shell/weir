Typed F# Shell — Exploration Plan for Claude Code
Scope: learning spikes, not a product build. Each spike is a self-contained session with a concrete deliverable and a kill/continue question. Ordered by risk. Drop this into a repo as PLAN.md and work spike by spike.
Setup (one session)

dotnet F# console project, xUnit or Expecto test project, FParsec dependency.
Constraint from day one: no printf-family (use interpolation), no reflection — keeps AOT viable later.

Spike 0 — Toy interpreter (~1–2 sessions)
Build: AST as a DU (literals, let, lambda, app, pipe), recursive-descent or FParsec parser, eval/apply tree-walk. No types yet. 1 + 2 |> double end to end.
Answers: does the eval/apply shape click; is FParsec workable.
Done when: REPL loop evaluates expressions with tests over the examples.
Spike 1 — Bidirectional checker, nominal only (~3–5 sessions) ← THE GATE
Build: Ty (int/str/bool/fn/seq/record-by-name/UoM), TypeEnv split into Values + Types, infer/check pair, source spans on every AST node (bake in now — retrofitting is miserable).
Acceptance test: ls | where (fun f -> f.Size > 1<mb>) | first 5 type-checks against a declared FileRow; f.Sze (typo) rejected with a span-accurate expected-vs-actual error.
Answers: is the checker tractable for you personally — this is the component where you must understand the output, not just accept it. If this spike stalls, the whole project recalibrates.
Done when: check : TypeEnv -> Expr -> Result<TypedExpr, TypeError> with resolved type on every node, ~10ms per line.
Spike 2 — Typed interpreter over checked AST (~1–2 sessions)
Build: full Value domain (incl. VRecord, VUnion, VSeq), eval over TypedExpr, failwith "unreachable" on type-impossible arms.
Answers: does checker→interpreter handoff hold; do runtime type errors actually disappear.
Done when: every checked example evaluates; deliberately broken programs are caught at check time, never eval time.
Spike 3 — Type declarations (~1–2 sessions)
Build: DType decl node for records + DUs, constructor table, pattern-match checking with exhaustiveness, session persistence of decls across REPL lines.
Done when: declare type Proc = Running of int | Stopped at the prompt, construct, match, get exhaustiveness warnings.
Spike 4 — Streaming pipelines (~2–3 sessions)
Build: lazy VSeq, evalPipe passing enumerators (not materialized lists), lazy builtins (where/map/first/take).
Acceptance test: infinite generator | first 5 terminates; a side-effecting source proves only 5 elements were pulled.
Answers: does laziness survive crossing eval boundaries.
Spike 5 — External command boundary (~2–4 sessions)
Build: process spawn → VSeq<VStr> lines fallback; from json (NDJSON) → typed rows checked against a declared record; one hand-written adapter (git status --porcelain); outbound serialization of VSeq → NDJSON into a process stdin.
Answers: how painful is typed↔bytes really; is explicit | from <fmt> ergonomic enough.
Done when: git status | from porcelain | where (fun c -> c.Staged) works on a real repo.
Spike 6 — REPL ergonomics (~1–2 sessions)
Build: line editor (existing lib), history, prompt completion from TypeEnv (names in scope + fields of current pipeline element type). No LSP — prompt-side only.
Answers: does the checker-powered completion feel like the payoff it's supposed to be.
Spike 7 — AOT reality check (~1 session)
Build: PublishAot=true + trim on whatever exists; measure cold start; fix trimmer warnings.
Answers: is the no-FCS, no-reflection discipline actually paying off (~5–20ms target).
Parked — revisit only after Spike 5

Row polymorphism (biggest checker-complexity jump; nominal first)
Adapter automation (schema codegen, completion-file scraping, LLM-synthesized adapters from samples)
LSP server for script files
Daemon architecture (only if AOT startup disappoints)

Session hygiene for Claude Code

One spike per session/branch; each ends with passing tests + a short NOTES.md entry: what was learned, what surprised, kill/continue.
Tests are the contract between spikes — Spike 1's acceptance expressions become Spike 2's eval fixtures, Spike 5's sample outputs become adapter regression tests.
You review the checker code line-by-line (Spike 1); everything else can be reviewed by behavior/tests. The checker is the one place LLM output can be plausibly wrong in ways tests won't catch (unsoundness), so that's where your attention goes.
Total: roughly 12–20 sessions to have every hard question answered and a usable skeleton.

Kill criteria worth writing down now: if Spike 1 takes >3x estimate or you can't confidently review the checker, stop and do the type-theory reading (Dunfield & Krishnaswami) before continuing — pushing through with code you can't verify defeats the purpose.
