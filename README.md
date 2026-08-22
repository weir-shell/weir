# weir

**weir is a typed shell-scripting language.** Commands, pipes and
files, like bash — with a checker up front: the whole file is
checked, every command included, and nothing runs until everything
checks. Expressions are F#-shaped, commands are first-class in the
same pipelines, and no string is ever passed to a shell.

**[weir.sh](https://weir.sh)** · [Guide](https://weir.sh/docs/guide/) ·
[Reference](https://weir.sh/reference/) · [Tooling](https://weir.sh/docs/tooling/)

```
weir> ls |> Seq.sortByDescending _.bytes
name       kind          bytes  modified  hidden  path
─────────  ─────────  ────────  ────────  ──────  ────────────────
core.dump  Regular       4 MiB  just now  false   /tmp/w/core.dump
build.log  Regular    88.7 KiB  just now  false   /tmp/w/build.log
logs       Directory       0 B  just now  false   /tmp/w/logs
: seq<FileRow>

weir> ls |> where (fun f -> f.bytse > 10MiB)
type error: FileRow has no field 'bytse'. Did you mean 'bytes'?
```

Two lines, the whole idea: rows are typed records — `bytes` is a
`Size` (`f.bytes > 10MiB`), `age` is a `Duration`
(`Instant.now () - f.modified < 1h`), directories are rows too — the REPL tabulates any
seq of records, and the second line is refused before anything runs — in a script, before *any*
line runs. External commands are first-class in the same pipelines
(`git status --porcelain |> Seq.where (Str.startsWith "M ")`), scripts
take real flags derived from a record, and everything is one static
binary. The rest — parallel fan-out, retries with deadlines, typed
HTTP and YAML — is in [docs/GUIDE.md](docs/GUIDE.md).

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
  runner would raise. The VS Code extension is on the
  [Marketplace](https://marketplace.visualstudio.com/items?itemName=weir-shell.weir)
  and [Open VSX](https://open-vsx.org/extension/weir-shell/weir);
  Neovim, Helix, Emacs and Zed:
  [editor setup](https://weir.sh/docs/editors/).

## Install

```
curl -fsSL https://weir.sh/install.sh | sh
```

Windows: `irm https://weir.sh/install.ps1 | iex`

One binary, no runtime. These scripts are generated per release and
served from `weir.sh` — a different origin than the binaries, with the
release's checksums baked in, so verification is tamper-evident, not
just an integrity check ([docs/INSTALL.md](docs/INSTALL.md) has the
details). Or download your platform's binary from
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

## Provenance

weir is written almost entirely by an AI agent working from
human-blessed plans, under an evidence discipline that trades unusual
authorship for unusual verifiability — every claim is pinned against
the compiled binary, doc examples execute in CI, and an F# compiler
oracle referees the fidelity claims. The full account is in
[CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

## Learn more

- [The guide](https://weir.sh/docs/guide/) — start here; every
  example runs in CI against the release binary.
- [The reference](https://weir.sh/reference/) — the language and
  every module, on one page.
- [Coming from bash, PowerShell, Python…](https://weir.sh/docs/coming-from/)
  — the per-language diff.
- [Tooling](https://weir.sh/docs/tooling/) — the CLI, signatures,
  schemas, `.weir/`, configuration.
- [examples/showcase.weir](examples/showcase.weir) — the
  full-language tour, also CI-run.
- [tests/fidelity/divergences.md](tests/fidelity/divergences.md) —
  the exact border with F# (different / rejected / pending),
  machine-verified against the F# compiler.

