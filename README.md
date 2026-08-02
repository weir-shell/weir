# weir

A typed shell. F#-shaped expressions, real commands, and a type
checker that runs before anything else does — a script with an error
in line 40 executes none of lines 1-39.

```
let changes =
    git status --porcelain
    |> from porcelain
    |> Seq.force

match changes with
| [] -> print "clean"
| _ -> changes |> Seq.where _.staged |> Seq.map _.path |> print
```

- **Check-everything-first**: parse and typecheck the whole file --
  PATH lookups included -- before any side effect.
- **Typed command output**: porcelain, JSON, and YAML adapters turn
  program output into records with fields, not string soup.
- **YAML templates without the string horror**: a `yaml` block is a
  checked literal — structure errors at check time, typed splices,
  `for`-generated entries — and you cannot write a YAML injection in
  weir, for the same reason you cannot write an argv injection.
- **~7ms cold start**: one AOT binary, shebang-friendly.
- **Parallel fan-out**: `Seq.pmap`/`Seq.piter` with per-worker session
  forks; no async machinery, ever -- that want is the graduation
  signal to full F#.
- **Inferred constraints, zero runtime type checks**: `==`, `show`,
  and `Seq.sortBy` are generic (`let same x y = x == y` works at any
  equatable type); every check is static.
- **Tooling from the same pipeline the runner uses**: `weir check
  [--json]` (located, coded diagnostics; no evaluation), `weir lsp`
  (diagnostics/hover/completion/semantic tokens over stdio) — editor
  setup for Neovim/Helix/Emacs/VS Code in
  [docs/editors.md](docs/editors.md) — and a REPL with completion
  and history.

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

Windows (v1, in progress [D:windows-v1]): `./publish.ps1` (or plain
`dotnet publish src/Weir -c Release -r win-x64`) — the stamp is in the
build, so both paths stamp identically; the .ps1 also copies to
`%LOCALAPPDATA%\Programs\weir\weir.exe` (add that directory to PATH
once). Prereqs: .NET 10 SDK + VS Build Tools C++ workload (NativeAOT
links with MSVC). Bare commands resolve through PATHEXT
(`git` finds `git.exe`); `Path` members produce Windows spellings and
accept forward slashes; config lives in `%APPDATA%\weir`, history in
`%LOCALAPPDATA%\weir`. Process-tree cleanup on interrupt is session
2 (job objects) — until then an interrupted script can orphan
grandchildren. Verified by hand pending the CI matrix.
