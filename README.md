# weir

A typed shell scripting language inspired by F#. Shell commands and
typed expressions share one syntax — no strings passed to a shell —
and the type checker runs over the whole file, PATH lookups
included, before anything executes: a broken script fails up front
instead of halfway through its side effects.

```
#!/usr/bin/env weir
type Cli = {
    [<Default ".">] root: string
    [<Short "a">] all: bool
}
let cli = Args.load Cli

// every clone under root, swept IN PARALLEL — the for-loop with
// cd side effects, xargs -P quoting, and $? checked in the wrong
// shell, replaced by a function over values
let repos =
    Path.glob $"{cli.root}/*/.git"
    |> Seq.map Path.dir
    |> Seq.force

let swept =
    repos
    |> Seq.pmap (fun r ->
        let changed = $(git -C $r status --porcelain) |> Seq.length
        let branch =
            $(git -C $r branch --show-current)
            |> Seq.tryHead
            |> Option.defaultValue "detached"
        (Path.fileName r, branch, changed))
    |> Seq.force

swept
    |> Seq.where (fun (_, _, n) -> cli.all || n > 0)
    |> Seq.sortByDescending (fun (_, _, n) -> n)
    |> Seq.iter (fun (name, branch, n) -> print $"{name} [{branch}]  {n} changed")

let dirty = swept |> Seq.where (fun (_, _, n) -> n > 0) |> Seq.length
print $"{repos |> Seq.length} repos, {dirty} dirty"
```

Every clone under a directory, checked in parallel. The bash
version of this is a `for` loop with `cd` side effects, `xargs -P`
quoting, and `$?` checked in the wrong shell; here the fan-out is
`Seq.pmap` over a function, a command's output is a value
(`$(git -C $r …)`), and a repo with no branch is an `Option` with a
default, not a crash at 2am. `--root`, `-a`, and `--help` derive
from the `Cli` record — the only type written anywhere; the rest,
including everything flowing through the pipeline, is inferred and
checked before line one runs.

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

Windows: `irm https://raw.githubusercontent.com/weir-shell/weir/main/install.ps1 | iex`

One binary, no runtime — both installers verify checksums before
installing. Or download your platform's binary from
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

