# toml-test (vendored)

The TOML 1.1.0 file list of [toml-test](https://github.com/toml-lang/toml-test)
v2.2.0 (MIT, see LICENSE), the conformance suite for `from toml`
[D:from-toml]. `valid/` documents must parse and match their `.json`;
`invalid/` documents must be refused. The harness lives in
tests/Weir.Tests ("from toml" list). The files are byte-exact
(`.gitattributes` exempts them from line-ending conversion).

Refresh: download a newer release, copy the files its `tests/files-toml-1.1.0`
lists, and rerun the unit tests.
