# Command signatures

Weir checks that a command exists before running a script. A
signature closes the next gap: with one declared, `bicep buidl
--outfil x` is a located check error instead of a 3am failure.

## The cycle

Generate, verify, regenerate:

```text
weir add sig bicep      # probes the installed binary, writes .weir/sigs/bicep.weir + a lock entry
```

Then declare it per script — checking is opt-in, per file:

```text
#sig bicep
bicep build --outfile x.json
```

`weir verify` compares the vendored signature against the installed
binary's verbatim `--version` — an exact match; patch churn is
handled by regenerating, and an empty diff is the useful signal.
When the tool updates, `weir add sig` again.

## Partial by default

A generated signature is PARTIAL — unknown flags warn, not error,
because a scraped surface may be incomplete. Verify the surface by
hand, add `let exhaustive = true` to the signature file, and unknown
flags become errors.

Generation probes, in order: the tool's fish completions, shipped
fish completion files, then `--help`. A tool that yields no flags is
told so — `.weir/sigs/<tool>.weir` can always be written by hand;
it is an ordinary weir file.

## CI posture

`weir check` never runs the tool and never fetches — a signature is
a vendored, check-time artifact ([project layout](project.md)), so
checking works for tools that only exist in CI, and CI checking
works offline. A locked-but-missing signature is `weir restore`'s
job to re-materialize, hash-verified.

The teaching version of this page — why you would want one — is one
paragraph in the [guide](GUIDE.md#declaring-a-tool-command-signatures).
