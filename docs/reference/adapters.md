# Adapters

`from` reads a wire format into a declared shape; `to` writes one.
Three formats each way (`json`, `jsonl`, `yaml` in; `json`, `jsonl`,
`yaml` out). Neither guesses: `from json T` reads one document
however many lines it spans; `from jsonl T` reads one document per
line and yields `seq<T>`.

```weir
type Peer = { host: string; port: int }

let peer = ["{\"host\": \"a\", \"port\": 9000}"] |> from json Peer
print $"{peer.host}:{peer.port}"

let peers = ["{\"host\": \"a\", \"port\": 1}"; "{\"host\": \"b\", \"port\": 2}"] |> from jsonl Peer
print $"{peers |> Seq.length} peers"
```

The write side mirrors the read: `to json` writes one minified
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
per-document dispatch. An empty stream is zero documents. The write
side mirrors the read exactly: `to yaml` writes one document — a
record is a mapping, a seq a sequence document, a pair-seq one
mapping — and `to yaml stream` writes one document per element, so
every form reads back through its own name (`to yaml |> from yaml
seq<T>`, `to yaml stream |> from yaml stream T`). A multiline
string renders as a block scalar. The `yaml` template
literal itself — checked structure, splices as nodes, `schema=` —
is a language form, taught in the
[guide](../GUIDE.md#commands-and-processes) with vendoring on the
[tooling page](../tooling.md#yaml-schemas).

## XML

`from xml T` reads one XML document into `T` — read-only, over a
subset. The document's root element is the top record; a field name
matches a child element by local name (a default `xmlns`, like
MSBuild's, is stripped so field names stay plain); `[<Attr>]` (or
`[<Attr "Include">]`) reads an attribute; `[<Elem "ProjectReference">]`
names the repeated child a `seq< >` field reads (defaulting to the
element type's name for `seq<record>`, the field name for
`seq<string>`); a nested record reads a child element recursively.

```weir
type Ref  = { [<Attr>] Include: string }
type Pg   = { IsPackable: Option<string> }
type Proj = { [<Elem "PropertyGroup">] groups: seq<Pg>
              [<Elem "ProjectReference">] refs: seq<Ref> }
let proj =
    [ "<Project>"
      "  <PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>"
      "  <ProjectReference Include=\"../Core/Core.csproj\" />"
      "</Project>" ]
    |> from xml Proj
print $"{Seq.length proj.refs} refs, {Seq.length proj.groups} property groups"
```

Every XML leaf is text, so a field is `string`, `Option<string>` (a
present-or-absent element or attribute), a record, or a `seq` of a
string or record — nothing else. A numeric or boolean field is
declared `string` and converted (`Str.toInt`); the checker teaches
this rather than guessing a convention XML does not carry. `[<Attr>]`
fits only `string`/`Option<string>`, `[<Elem>]` only a `seq`, and the
top level is one root element — there is no `from xml seq<T>` (a
repeated child is a `seq< >` field). XML is read-only: there is no
`to xml`.

## Editing YAML: `Yaml.parse` and `yaml patch`

`from yaml T` reads into a declared record — and drops every field you
did not declare, which makes it wrong for read-modify-write. The
typeless pair holds the document whole: `Yaml.parse` reads one
document into `Yaml` nodes (scalars self-type exactly as district
scalars do), and `Yaml.merge` applies a `yaml patch` district whose
*structure* is the address — kustomize's strategic-merge model, with
no path language. Maps upsert recursively; a sequence
appends-if-absent, or upserts by the marker line's `by=<key>`; scalars
replace; the `$-` tombstone removes (in value position, the key it
sits under; as `- $- <content>`, the matching item). Merge is
orderless and idempotent — update-or-insert is the semantics of
`by=`, not a branch you write.

```weir
let doc = ["kind: Kustomization"; "images:"; "    - name: app"; "      newTag: v1"] |> Yaml.parse
let p = yaml patch by=name
    images:
        - name: app
          newTag: v2
doc |> Yaml.merge p |> to yaml |> Seq.iter print
```

A patch types as `YamlPatch`, not `Yaml`, and the type carries the
laws: `to yaml` on a patch refuses at check (a patch is instructions,
not a document — no tombstone can ever reach a file), `$-` outside a
`yaml patch` district refuses, and `patch` does not combine with
`schema=` (a patch is partial; schemas validate whole documents).
`Yaml.parse` can never produce a tombstone — parsed text is data.
There is no in-place file member: the round-trip is composition,
`File.read f |> Yaml.parse |> Yaml.merge p |> to yaml |> File.write f`,
so the one mutation stays visible in the pipeline.

## What does not serialize

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
