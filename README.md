# weir

A typed shell. F#-shaped expressions, real commands, and a type
checker that runs over the whole file — PATH lookups included —
before anything executes. A script with an error on line 40 runs
none of lines 1–39.

```
#!/usr/bin/env weir
type Cli = {
    [<Short "v">] verbose: bool
    [<Default 30s>] timeout: Duration
}
let cli = Args.load Cli

let staged =
    git status --porcelain           // argv is data — nothing to quote, ever
    |> Seq.choose (fun l ->          // the Regex pattern is arity-typed:
        match l with                 // one group, one binder
        | Regex @"^[^ ?]. (.*)$" path -> Some path
        | _ -> None)

match staged with
| [] -> print "nothing staged"
| files ->
    if cli.verbose then printerr $"{files |> Seq.length} file(s)"
    files
    |> Seq.pmap (fun f -> $(git log -1 --format=%s -- $f) |> Seq.head)
    |> print
```

`--verbose`, `--timeout 90s`, and `--help` all derive from the
record. The `Regex` pattern is checked too — three groups against two
binders is a type error, not a silent `None`.

## What you get

- **Check everything first.** Parse and typecheck the entire file —
  including that every bare command resolves — before any side
  effect. Command signatures (`#sig git`) extend the check to flags.
- **Typed command output.** JSON and YAML adapters turn program
  output into records of a shape you declare; the arity-typed `Regex`
  match pattern covers everything line-shaped.
- **Exit codes are data.** `cmd | succeeds`, `| complete`,
  `| exitCode`, `| orFail "why"` — a failing command raises by
  default, so there is no `set -e` folklore to get wrong.
- **YAML without the string horror.** A `yaml` block is a checked
  literal: structure errors at check time, typed splices,
  `for`-generated entries, optional JSON-schema validation — and no
  YAML injection, for the same reason there is no argv injection.
- **Typed boundaries everywhere.** Flags (`Args.load`) and env vars
  (`Env.load`) load into records; time is a type (`30s`, `1m30s`);
  HTTP is a typed request/response pair; `Secret` renders as `***`
  in every renderer.
- **Parallel fan-out.** `Seq.pmap`/`Seq.piter` with per-worker
  session forks. No async machinery, ever — wanting it is the
  signal to graduate to full F#.
- **Inferred constraints, zero runtime type checks.** `==`, `show`,
  and `Seq.sortBy` are generic; every check is static.
- **Fast.** ~7ms cold start, one AOT binary, shebang-friendly.
- **Cross-platform.** Linux, macOS, and Windows, same argv law on
  all three; on Windows bare commands resolve through PATHEXT and
  `Path` members speak Windows spellings.
- **Tooling from the runner's own pipeline.** `weir check [--json]`,
  an LSP (diagnostics, hover, completion, semantic tokens), and a
  REPL with completion and history. Editor setup for Neovim, Helix,
  Emacs, and VS Code: [docs/editors.md](docs/editors.md).

## Install

`./publish.sh` builds and installs `~/.local/bin/weir` (dotnet 10
SDK + clang). On Windows: `./publish.ps1` (dotnet 10 SDK + VS Build
Tools C++ workload) installs to `%LOCALAPPDATA%\Programs\weir`; in
the REPL, `Ctrl+J` forces a newline. Tests: `dotnet test`; the full
battery is `ci/local.sh`.

## Learn more

Start with [docs/GUIDE.md](docs/GUIDE.md) — every example in it runs
in CI against the release binary, as does the full-language tour in
[examples/showcase.weir](examples/showcase.weir). Arriving from bash,
PowerShell, Python, fish, TypeScript, Make, or F#?
[docs/COMING-FROM.md](docs/COMING-FROM.md) is the per-language diff.
The rulebook with decision rationale is [SEMANTICS.md](SEMANTICS.md);
the exact border with F# (different / rejected / pending,
machine-verified against the F# compiler) is
[tests/fidelity/divergences.md](tests/fidelity/divergences.md); the
project vocabulary ("receipt", "park", "pin") is
[LEXICON.md](LEXICON.md).
