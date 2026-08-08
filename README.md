# weir

A typed shell scripting language inspired by F#. Shell commands and
typed expressions share one syntax — no strings passed to a shell —
and the type checker runs over the whole file, PATH lookups
included, before anything executes: a broken script fails up front
instead of halfway through its side effects.

```
#!/usr/bin/env weir
type Cli = {
    [<Short "v">] verbose: bool
    [<Default 5s>] timeout: Duration
}
let cli = Args.load Cli

// the pattern has two capture groups, so the match binds two names —
// the checker counts them before anything runs
let commits =
    git log -n 3 "--format=%h %s"
    |> Seq.choose (fun l ->
        match l with
        | Regex @"^(\S+) (.+)$" (sha, subject) -> Some $"{sha}  {subject}"
        | _ -> None)

print "latest commits:"
commits |> print

// probe every endpoint in parallel; a bad status is a value to
// branch on, not an exception
let endpoints = ["https://github.com"; "https://gitlab.com"]

let checks =
    endpoints
    |> Seq.pmap (fun url ->
        let r = Http.send { Http.get url with timeout = cli.timeout }
        $"{r.status}  {url}")

if cli.verbose then printerr $"probed {checks |> Seq.length} endpoints"
checks |> print
```

`--verbose`, `--timeout 10s`, and `--help` all derive from the
record. The `Regex` pattern binds one name per capture group — two
groups here, two names — and a miscount is a check error, not a
silent `None`. The `Cli` record is the only type annotation in the
script: everything else is inferred.

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
- **Fast.** ~7ms cold start, one AOT binary, shebang-friendly.
- **Cross-platform.** Linux, macOS, and Windows.
- **Editor and CLI tooling.** `weir check [--json]`, an LSP
  (diagnostics, hover, completion, semantic tokens), and a REPL with
  completion and history — the editor shows exactly the errors the
  runner would raise. Setup for Neovim, Helix, Emacs, and VS Code:
  [docs/editors.md](docs/editors.md).

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
