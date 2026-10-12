# weir

**weir is a typed shell-scripting language.** Commands, pipes and files,
like bash, with F#-shaped expressions and a checker that reads the whole
script — every command included — before anything runs.

```weir
let big = ls |> Seq.where (fun f -> f.bytes > 10MiB)
for f in big do print f.name

git status --porcelain |> Seq.where (Str.startsWith "M ") |> Seq.iter print
```

## Install

```
curl -fsSL https://weir.sh/install.sh | sh
```

Windows: `irm https://weir.sh/install.ps1 | iex`. Other options and how
the installer verifies downloads: [docs/INSTALL.md](docs/INSTALL.md).

## Documentation

The docs are the source of truth: **[weir.sh](https://weir.sh)**.

- [Guide](https://weir.sh/docs/guide/) — start here
- [Reference](https://weir.sh/reference/) — the language and every module
- [Coming from bash, PowerShell, Python…](https://weir.sh/docs/coming-from/)
- [Tooling](https://weir.sh/docs/tooling/) and
  [editor setup](https://weir.sh/docs/editors/)
- [CHANGELOG.md](CHANGELOG.md) — what changed in each release

## Developing

`./publish.sh` builds and installs `~/.local/bin/weir` (`./publish.ps1`
on Windows); `dotnet test` runs the unit tests and `ci/local.sh` the
full battery. See [CONTRIBUTING.md](CONTRIBUTING.md).

weir is written almost entirely by an AI agent working from
human-approved plans, with every claim pinned against the built binary;
[CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md) have
the full account.

## License

[LICENSE](LICENSE)
