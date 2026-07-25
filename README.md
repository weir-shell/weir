# weir

A typed shell. F#-shaped expressions, real commands, and a type
checker that runs before anything else does — a script with an error
in line 40 executes none of lines 1-39.

```
let changes =
    git status --porcelain
    | from porcelain
    | Seq.force

match changes with
| [] -> print "clean"
| _ -> changes |> Seq.where _.Staged |> Seq.map _.Path |> print
```

- **Check-everything-first**: parse and typecheck the whole file --
  PATH lookups included -- before any side effect.
- **Typed command output**: porcelain and JSON adapters turn program
  output into records with fields, not string soup.
- **~7ms cold start**: one AOT binary, shebang-friendly.
- **Parallel fan-out**: `Seq.pmap`/`Seq.piter` with per-worker session
  forks; no async machinery, ever -- that want is the graduation
  signal to full F#.
- **Inferred constraints, zero runtime type checks**: `==`, `show`,
  and `Seq.sortBy` are generic (`let same x y = x == y` works at any
  equatable type); every check is static.
- **Tooling from the same pipeline the runner uses**: `weir check
  [--json]` (located, coded diagnostics; no evaluation), `weir lsp`
  (diagnostics/hover/completion over stdio), a micro syntax +
  LSP config in `editors/`, and a REPL with completion and history.

Start with [docs/GUIDE.md](docs/GUIDE.md) -- every example in it is
executed in CI against the release binary. The language rulebook with
decision rationale is [SEMANTICS.md](SEMANTICS.md); the exact border
with F# (different / rejected / pending, machine-verified against the
F# compiler) is [tests/fidelity/divergences.md](tests/fidelity/divergences.md).
New to the project's vocabulary — "receipt", "park", "pin", weir's own
sense of "type class" or "erasure"? [LEXICON.md](LEXICON.md) defines
the terms the other docs use.

Build: `./publish.sh` installs `~/.local/bin/weir` (dotnet 10 SDK +
clang required). Tests: `dotnet test`; the full battery is
`ci/local.sh`.
