# Changelog

The user-facing record, one section per release, newest first. Authored,
not generated — the ledger (`docs/DECISIONS.md`) has the material but its
audience is maintainers; this file's audience is someone deciding whether
to upgrade.

Each release carries a **"checks clean, behaves differently"** section:
changes where a script that passed `weir check` before still passes but
does something else at runtime. It is the category an upgrading user most
needs and the one release notes usually lack — it accumulates here across
releases, and it is present even when empty so that "empty" is a statement
rather than an omission.

The release workflow extracts the tagged section below as the GitHub
release body, and refuses to release a tag that has no section here.

## v0.0.1

The first release: one static AOT binary per platform, no runtime.

weir is `0.x` in the honest semver sense: **anything can break between
releases**, and this file says what did. `1.0` happens when the language
stops moving under its users — not on a date.

### Platform gaps, stated

- `flock(1)`-dependent behaviour is exercised on Linux only.
- The pty test cells run where util-linux `script -e -c` exists — Linux;
  macOS/BSD `script` differs and those cells are stated skips there.
- The PyYAML interop referee runs on Linux and macOS, not Windows.
- Windows: a set of stated unit-test skips (POSIX-tool fixtures), and the
  HTTP end-to-end surface has never been run there.

Unsigned binaries: macOS quarantine and Windows SmartScreen will warn on
first run — [docs/INSTALL.md](docs/INSTALL.md) has the two-step answers.

### Checks clean, behaves differently

Nothing yet — this is the first release, so there is no "before" to
differ from. The section exists from the start so its absence in a
future release is a claim, not a gap.
