# Adapters

`from` reads a wire format into a declared shape; `to` writes one.
Three formats each way (`json`, `jsonl`, `yaml` in; `json`, `jsonl`,
`yaml` out). Neither guesses: `from json T` reads ONE document
however many lines it spans; `from jsonl T` reads one document per
line and yields `seq<T>`.

```weir
type Peer = { host: string; port: int }

let peer = ["{\"host\": \"a\", \"port\": 9000}"] |> from json Peer
print $"{peer.host}:{peer.port}"

let peers = ["{\"host\": \"a\", \"port\": 1}"; "{\"host\": \"b\", \"port\": 2}"] |> from jsonl Peer
print $"{peers |> Seq.length} peers"
```

The write side mirrors the read: `to json` writes ONE minified
document — a record is an object, a seq an array (built whole; one
line cannot stream) — and `to jsonl` writes NDJSON, one document per
element, lazily. Every adapter pairs with its own name across the
arrow: `to json |> from json T`, `to jsonl |> from jsonl T`.

```weir
type P = { a: int }
{ a = 1 } |> to json |> Seq.iter print
[{ a = 1 }; { a = 2 }] |> to json |> Seq.iter print
[{ a = 1 }; { a = 2 }] |> to jsonl |> Seq.iter print
```

(prints `{"a":1}`, then `[{"a":1},{"a":2}]` — one array document —
then the two NDJSON lines.)

## The admitted shapes

A field is one of: a scalar (`int`, `float`, `string`, `bool`), an
`Option` of an admitted type, a record whose fields are all
admitted, a `seq` of an admitted type, or a `Map<string, T>`. The
rule is recursive; a self-referential record refuses at check,
naming its cycle. A top-level JSON array declares itself:
`from json seq<Peer>`. Integer-shaped JSON numbers widen into
`float` fields (JSON has one number type).

A missing array is an error, not a silent `[]` — absence is
`Option`'s job. Unknown wire keys are ignored on read. A wire key
that is not a legal identifier maps with `[<Wire "key">]`; two
fields resolving to one wire key refuse at the declaration.

## Documents in several shapes

A **tagged union** reads documents discriminated by a field:
`[<Tag "kind">]` on the union names the discriminator, each case
carries a declared record (or nothing — a tag-only document), and
the tag value defaults to the case name (`[<Wire "v1">]` on a case
overrides). Admitted at both formats, top level or nested — so
`from jsonl KDoc` dispatches mixed NDJSON and `from json seq<KDoc>`
a mixed array. One `[<Other>]` case (`of string`, or nullary) makes
the union open-world: unmatched tags land there instead of
erroring. A missing tag field always errors — malformed is not
unknown. Writers reinsert the tag first; an `[<Other>]` value
refuses to write. Untagged unions stay refused, the error naming
`[<Tag>]`.

## Keys that are data

`Map<string, T>` reads an ID-keyed object — as a field or as the
whole document. Keys are strings only; pairs walk key-sorted;
duplicate keys last-win; `to json` writes the object back.

## YAML

`from yaml T` reads with the same admission rules; quoting
disambiguates scalars (`rate: 1.5` is a number, `"1.5"` a string —
both directions). `from yaml stream T` reads a `---`-separated
stream — N documents, each as `T`, so the heterogeneous bundle (a
kubernetes apply file) is `from yaml stream KDoc` over a tagged
union: the stream word is the cardinality, the union the
per-document dispatch. An empty stream is zero documents. `to yaml`
renders a `yaml` block's value; a multiline string renders as a
block scalar; a seq writes the `---` stream (there is no
`to … stream` word — the seq already means it), and the write reads
back through the stream form. The `yaml` template
literal itself — checked structure, splices as nodes, `schema=` —
is a language form, taught in the
[guide](../GUIDE.md#commands-and-processes) with vendoring on the
[tooling page](../tooling.md#yaml-schemas).

## Refusals with named exits

`Instant` has no wire convention, so JSON refuses it naming
`Instant.epochMs` and `show`. `Bytes` refuses naming
`Bytes.toBase64`. `Secret` refuses outright — a credential does not
serialize.

## Anonymous shapes at the boundary

For a foreign shape read once, the type goes inline:

```weir
let n = ["{\"count\": 3, \"noise\": true}"] |> from json {| count: int |} |> _.count
print $"{n}"
```

Declared records stay nominal; two anonymous shapes with the same
fields are the same type (field order canonicalizes).
