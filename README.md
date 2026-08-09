# weir

A typed shell scripting language inspired by F#. Shell commands and
typed expressions share one syntax — no strings passed to a shell —
and the type checker runs over the whole file, PATH lookups
included, before anything executes: a broken script fails up front
instead of halfway through its side effects.

```
#!/usr/bin/env weir
type Cli = {
    [<Default 8080>] port: int
    tag: Option<string>
}
let cli = Args.load Cli
let port = cli.port
let tag = cli.tag

// a yaml block is a CHECKED literal: structure errors at check time,
// splices are typed values, and a None splice omits its entry — a
// YAML injection cannot be written, same as argv
let manifest name = yaml
    apiVersion: v1
    kind: Service
    metadata:
        name: $name
        tag: $tag
    spec:
        selector:
            app: $name
        ports:
            - port: $port

manifest "web" |> to yaml |> File.write "svc.yaml"

// a failing command raises; | orFail names YOUR reason
kubectl apply -f svc.yaml | orFail "apply failed"

// the wait everyone hand-rolls — bounded, cancellable, typed
let up = poll timeout=30s interval=1s
    let r = curl -sf $"localhost:{port}/health" | complete
    r
until r
    r.exitCode == 0

print (up.stdout |> Seq.head)
```

`--port`, `--tag`, and `--help` all derive from the record. The
`yaml` block is an expression the checker owns: misindent it and the
script refuses to run, splice a value of the wrong shape and that is
a type error — and because splices are typed values, not string
pasting, a YAML injection cannot be written. `poll` is the retry
loop everyone hand-rolls, with a deadline and a typed result. The
`Cli` record is the only type written anywhere; the rest is
inferred.

## What you get

- **Check everything first.** Parse and typecheck the entire file —
  including that every bare command resolves — before any side
  effect. Command signatures (`#sig git`) extend the check to flags.
- **Typed command output.** JSON and YAML adapters turn program
  output into records of a shape you declare; the `Regex` match
  pattern covers everything line-shaped.
- **Exit codes are data.** `cmd | succeeds`, `| complete`,
  `| exitCode`, `| orFail "why"` — a failing command raises by
  default, so there is no `set -e` folklore to get wrong.
- **YAML without the string horror.** A `yaml` block is a checked
  literal: structure errors at check time, typed splices,
  `for`-generated entries, optional JSON-schema validation — and no
  YAML injection, for the same reason there is no argv injection.
- **Typed boundaries everywhere.** Flags (`Args.load`) and env vars
  (`Env.load`) load into records; `Duration` is a type (`30s`, `1m30s`), not a bare number;
  HTTP is a typed request/response pair; `Secret` renders as `***`
  in every renderer.
- **Parallel fan-out.** `Seq.pmap`/`Seq.piter` with per-worker
  session forks — asynchronous underneath (.NET tasks), synchronous
  in your script: no async/await surface to thread through.
- **Generics without ceremony.** `==`, `show`, and `Seq.sortBy` work
  on any type that supports them — no annotations to write, and a
  type that doesn't support them is a check error, not a runtime
  surprise.
- **Fast.** One static binary, millisecond start.
- **Cross-platform.** Linux, macOS, and Windows.
- **Editor and CLI tooling.** `weir check [--json]`, an LSP
  (diagnostics, hover, completion, semantic tokens), and a REPL with
  completion and history — the editor shows exactly the errors the
  runner would raise. Setup for Neovim, Helix, Emacs, and VS Code:
  [docs/editors.md](docs/editors.md).

## Install

```
curl -fsSL https://raw.githubusercontent.com/weir-shell/weir/main/install.sh | sh
```

One binary, no runtime — the installer verifies checksums and drops
`weir` in `~/.local/bin`. Or download your platform's binary from
[releases](https://github.com/weir-shell/weir/releases); Windows and
the unsigned-binary first-run dialogs are covered in
[docs/INSTALL.md](docs/INSTALL.md). weir is `0.x` in the semver
sense: anything can break between releases, and the notes say what
did.

## Developing

Build from source: `./publish.sh` builds and installs
`~/.local/bin/weir` (dotnet 10 SDK + clang). On Windows,
`./publish.ps1` (dotnet 10 SDK + VS Build Tools C++ workload)
installs to `%LOCALAPPDATA%\Programs\weir`. Tests: `dotnet test`;
the full battery is `ci/local.sh`. In the Windows REPL, `Ctrl+J`
forces a newline.

## Learn more

- [docs/GUIDE.md](docs/GUIDE.md) — start here; every example runs in
  CI against the release binary.
- [examples/showcase.weir](examples/showcase.weir) — the
  full-language tour, also CI-run.
- [docs/COMING-FROM.md](docs/COMING-FROM.md) — the per-language diff
  for arrivals from bash, PowerShell, Python, fish, TypeScript,
  Make, or F#.
- [SEMANTICS.md](SEMANTICS.md) — the rulebook, with decision
  rationale.
- [tests/fidelity/divergences.md](tests/fidelity/divergences.md) —
  the exact border with F# (different / rejected / pending),
  machine-verified against the F# compiler.
- [LEXICON.md](LEXICON.md) — the project vocabulary ("receipt",
  "park", "pin").

