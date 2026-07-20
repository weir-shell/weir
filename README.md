# weir

A typed shell. F#-shaped expressions, real commands, and a type
checker that runs before anything else does — a script with an error
in line 40 executes none of lines 1-39.

```
let changes =
    git status --porcelain
    | from porcelain

if changes |> Seq.isEmpty then print "clean" else
    changes |> Seq.where _.Staged |> Seq.map _.Path |> print
```

- **Check-everything-first**: parse and typecheck the whole file --
  PATH lookups included -- before any side effect.
- **Typed command output**: porcelain and JSON adapters turn program
  output into records with fields, not string soup.
- **~7ms cold start**: one AOT binary, shebang-friendly.
- **Parallel fan-out**: `Seq.pmap`/`Seq.piter` with per-worker session
  forks; no async machinery, ever -- that want is the graduation
  signal to full F#.

Start with [docs/GUIDE.md](docs/GUIDE.md) -- every example in it is
executed in CI against the release binary. The language rulebook with
decision rationale is [SEMANTICS.md](SEMANTICS.md); the exact border
with F# (different / rejected / pending, machine-verified against the
F# compiler) is [tests/fidelity/divergences.md](tests/fidelity/divergences.md).

Build: `./publish.sh` installs `~/.local/bin/weir` (dotnet 10 SDK +
clang required). Tests: `dotnet test`; the full battery is
`ci/local.sh`.
