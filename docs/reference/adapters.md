# Adapters

`from` reads a data format into a type you declare; `to` writes one.
Three formats work in both directions (`json`, `jsonl`, `yaml`), and
three more are read-only (`toml`, `xml`, `table`). You always say which format
you mean: `from json T` reads one document however many lines it
spans, while `from jsonl T` reads one document per line and yields
`seq<T>`.

```weir
type Peer = { host: string; port: int }

let peer = ["{\"host\": \"a\", \"port\": 9000}"] |> from json Peer
print $"{peer.host}:{peer.port}"

let peers = ["{\"host\": \"a\", \"port\": 1}"; "{\"host\": \"b\", \"port\": 2}"] |> from jsonl Peer
print $"{peers |> Seq.length} peers"
```

Writing mirrors reading. `to json` writes one minified document: a
record becomes an object and a seq becomes an array (built in memory
all at once, since a single line cannot be streamed). `to jsonl`
writes NDJSON, one document per element, lazily. Each format reads
back what it writes: `to json |> from json T`,
`to jsonl |> from jsonl T`.

```weir
type P = { a: int }
{ a = 1 } |> to json |> Seq.iter print
[{ a = 1 }; { a = 2 }] |> to json |> Seq.iter print
[{ a = 1 }; { a = 2 }] |> to jsonl |> Seq.iter print
```

(prints `{"a":1}`, then `[{"a":1},{"a":2}]` — one array document —
then the two NDJSON lines.)

## Which field types are allowed

A field is one of:

- a scalar (`int`, `float`, `string`, `bool`)
- an `Option` of one of these types
- a record whose fields are all of these types
- a `seq` of one of these types
- a `Map<string, T>`

The rule applies recursively. A self-referential record is a check
error that names the cycle. For a top-level JSON array, say so in the
type: `from json seq<Peer>`. Integer-looking JSON numbers are accepted
by `float` fields (JSON has only one number type).

A missing array is an error, not a silent `[]`; use `Option` for
fields that may be absent. Unknown keys are ignored on read. A key
that is not a legal weir identifier is mapped with `[<Wire "key">]`,
and two fields that map to the same key are an error at the
declaration.

## Documents in several shapes

A **tagged union** reads documents whose shape depends on one
field. `[<Tag "kind">]` on the union names that field. Each case
carries a declared record, or nothing for a document that has only
the tag. The tag value defaults to the case name; `[<Wire "v1">]` on
a case overrides it. Tagged unions work in JSON and YAML, at the top
level or nested, so `from jsonl KDoc` reads mixed NDJSON and
`from json seq<KDoc>` reads a mixed array. Adding one `[<Other>]`
case (`of string`, or with no payload) makes the union accept unknown
tags: they land in that case instead of raising an error. A document
with no tag field is always an error, since that is malformed rather
than unknown. When writing, the tag is written first, and an
`[<Other>]` value cannot be written. Unions without a tag are not
supported; the error suggests `[<Tag>]`.

## Keys that are data

`Map<string, T>` reads an object keyed by IDs, either as a field or
as the whole document. Keys must be strings, entries are iterated in
key order, and for duplicate keys the last one wins. `to json` writes
the object back.

## YAML

`from yaml T` accepts the same shapes as JSON. Quoting decides a
scalar's type in both directions: `rate: 1.5` is a number, `"1.5"` is
a string. A quoted scalar may continue on more deeply indented lines,
as in kubectl's long `message:` values:

- the closing quote ends it
- each line break folds to a single space
- an empty continuation line becomes a newline
- the folded value stays a string

Literal block scalars (`key: |` and `key: |-`) read as strings. An
indentation indicator such as `|2` or `|2-`, which kubeconfigs
sometimes carry, sets the content's indentation relative to the key,
so the first line may keep leading spaces. Folded scalars (`>`) and
`|+` are not supported.

`from yaml stream T` reads a `---`-separated stream of documents,
each as `T`. To read a mixed bundle such as a Kubernetes apply file,
use `from yaml stream KDoc` with a tagged union: `stream` means "many
documents", and the union picks the shape of each one. An empty
stream gives zero documents.

Writing mirrors reading exactly. `to yaml` writes one document: a
record becomes a mapping, a seq a sequence document, and a seq of
pairs one mapping. `to yaml stream` writes one document per element.
Each form reads back with the matching reader
(`to yaml |> from yaml seq<T>`,
`to yaml stream |> from yaml stream T`). A multiline string is
written as a block scalar. The `yaml` template literal (with checked
structure, splices as nodes, and `schema=`) is part of the language
and is covered in the [guide](../GUIDE.md#commands-and-processes);
vendoring schemas is on the [tooling page](../tooling.md#yaml-schemas).

## TOML

`from toml T` reads one TOML document into `T` — a `Cargo.toml`, a
`pyproject.toml`, a tool's config. It reads TOML 1.1, so every TOML 1.0
file too, and it is read-only: there is no `to toml`. The document is
a table, so `T` is a record (or a tagged union); `seq`, `stream` and
`Map` tops are refused. Inside, the field types follow YAML's rules:
tables are records, arrays and arrays of tables (`[[bin]]`) are
seqs, a key that may be missing is an `Option`, and `[<Wire "key">]`
names a key that isn't a weir name, such as `requires-python`.

- Integers in any TOML spelling (`1_000`, `0xff`, `0o7`, `0b1`) read as
  `int`; `inf` and `nan` parse, but reading one into a `float` field
  is an error, since weir floats are finite.
- A date or time is read as its text. An offset date-time such as
  `2024-03-01T10:00:00Z` reads into an `Instant` field marked
  `[<Iso8601>]`.
- Duplicate keys and tables defined twice are errors, reported with
  the line, as the TOML spec requires.

```weir
type Package = { name: string; version: string }
type Cargo = { package: Package; bin: seq<{| name: string |}> }

let cargo = <<<
    [package]
    name = "demo"
    version = "0.3.1"

    [[bin]]
    name = "cli"

let c = cargo |> from toml Cargo
print $"{c.package.name} {c.package.version}: {c.bin |> Seq.map _.name |> Str.join ","}"
```

## XML

`from xml T` reads one XML document into `T`. It is read-only and
supports a subset of XML. The document's root element maps to the
top-level record. A field matches a child element by local name (a
default `xmlns`, like MSBuild's, is stripped so field names stay
plain). `[<Attr>]` (or `[<Attr "Include">]`) reads an attribute.
`[<Elem "ProjectReference">]` names the repeated child element that a
`seq< >` field reads; by default that is the element type's name for
`seq<record>` and the field name for `seq<string>`. A nested record
reads a child element recursively.

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
declared `string` and converted (`Str.toInt`); the checker tells you
this rather than guessing at a convention XML does not define.
`[<Attr>]` fits only `string`/`Option<string>`, `[<Elem>]` only a
`seq`, and the top level is a single root element, so there is no
`from xml seq<T>` (repeated children are read with a `seq< >` field).
There is no `to xml`.

## Aligned tables

`from table T` reads aligned column output, the kind `kubectl` and
`docker` print (one header row followed by aligned data rows), into
declared row records, yielding `seq<T>`. The first non-blank line is
the header. Columns are cut at the *header positions*, not at
whitespace, so a value containing spaces (`Up 2 hours`, a free-text
last column) stays intact. Headers are separated by two or more
spaces; a single space stays inside a header, so `CONTAINER ID` is one
column (both tools pad columns with three spaces).

```weir
type Pod = { name: string; status: string; restarts: int; node: Option<string> }
let pods =
    [ "NAME    STATUS    RESTARTS   NODE"
      "web-1   Running   0          k3d-a"
      "db-0    Pending   3          <none>" ]
    |> from table Pod
pods |> Seq.iter (fun p -> print $"{p.name}: {p.status} ({show p.restarts} restarts)")
```

A field matches its header by name, ignoring case and anything that
is not a letter or digit: `name` reads `NAME`, `podTemplateHash` reads
`POD-TEMPLATE-HASH`. `[<Wire "HEADER">]` matches a header exactly as
written. Cells are trimmed and converted to the field's type
(`string`, `int`, `float`, `bool`). An `Option` field reads an empty
cell, or a cell that is exactly `<none>` (as kubectl prints), as
`None`; an empty cell in a required field is an error that suggests
making it an `Option`. Extra columns are ignored (as with unknown JSON
keys), blank lines are skipped, and a table with only a header is an
empty seq (`docker ps` with nothing running). Errors say where: a
missing column is named along with the headers that were found, and a
cell of the wrong type is reported with its row and column. The
result is already a seq of rows, so you don't write
`seq`/`stream`/`Map` around the type. There is no `to table`.

`az … -o table` (and other `tabulate`-style tools) draw a dashes rule
line under the header (`------  ----------`). `from table` skips that
separator when it is the first data row, so az output reads without a
garbage row and `#infer` types from the real rows. Tables with no
separator (kubectl, docker) are unchanged; only a row that is entirely
dashes and spaces is dropped, and only in first position.

`#infer <src> from table as Pod` (or `sample |> Table.inferShape`)
drafts the row record from a real sample. It infers each column's
type, uses `Option` where a column has empty or `<none>` cells, and
notes that the value reads as `seq<Pod>`.

## Editing YAML: `Yaml.parse` and `yaml patch`

`from yaml T` reads into a declared record and drops every field you
did not declare, so it is the wrong tool for read-modify-write. For
that, use the untyped pair, which keeps the whole document.
`Yaml.parse` reads one document into `Yaml` nodes (scalars get their
type from their text, exactly as in a `yaml` block), and `Yaml.merge`
applies a `yaml patch` block. The patch's *structure* says where each
change goes — kustomize's strategic-merge model, with no path
language. Maps are merged recursively, adding or updating keys. A
sequence item is appended if absent, or, with `by=<key>` on the
marker line, updated by that key (and inserted if no item matches). Scalars are replaced. The `$-` marker
removes things: in value position it removes the key it sits under,
and as `- $- <content>` it removes the matching item. The order of
changes does not matter and applying a patch twice gives the same
result, so with `by=` you never write an "update or insert" branch
yourself.

```weir
let doc = <<<
    kind: Kustomization
    images:
        - name: app
          newTag: v1
|> Yaml.parse
let p = yaml patch by=name
    images:
        - name: app
          newTag: v2
doc |> Yaml.merge p |> to yaml |> Seq.iter print
```

A patch has type `YamlPatch`, not `Yaml`, and the type enforces the
rules. `to yaml` on a patch is a check error, because a patch is a set
of instructions rather than a document, so a `$-` removal marker can
never end up in a file. `$-` outside a `yaml patch` block is an error,
and `patch` cannot be combined with `schema=` (a patch is partial,
while schemas validate whole documents). `Yaml.parse` never produces a
removal marker; parsed text is always plain data. There is no function
that edits a file in place. You write the round-trip as a pipeline,
`File.read f |> Yaml.parse |> Yaml.merge p |> to yaml |> File.write f`,
so the change stays visible.

## Which types serialize automatically

Types with one obvious encoding are serialized automatically. Types
that could be encoded in more than one way are refused, and the error
names the explicit conversion. JSON and YAML follow the same rule in
both directions, with the same messages.

- `int`, `float`, `string`, `bool` and `Uuid` work directly. A `Uuid`
  is written as its lowercase string and read back with any form
  `Uuid.parse` accepts; a malformed string raises an error naming the
  field. A JSON-lines log keyed by `Uuid.v7` stays sortable by id as
  text.
- `Instant`, `Duration` and `Size` are refused unless you say how to
  encode them: an epoch number and a text form are both reasonable,
  and weir won't guess which one the other side expects. Put a codec
  attribute on the field, or convert explicitly; the error message
  offers both.

| Type | Codecs | Written as |
|---|---|---|
| `Instant` | `[<Iso8601>]`, `[<EpochMs>]`, `[<EpochSec>]` | ISO 8601 string; epoch ms; epoch seconds |
| `Duration` | `[<Millis>]`, `[<Seconds>]` | integer ms; integer seconds |
| `Size` | `[<ByteCount>]` | integer bytes |

```weir
type Event = {
    [<Iso8601>] at: Instant
    [<Millis; Wire "elapsed_ms">] took: Duration
}
let line = [{ at = Instant.parse "2026-08-14T12:00:00Z"; took = 1500ms }] |> to jsonl |> Seq.head
print line
let back = [line] |> from jsonl Event |> Seq.head
print (show back.took)
```

One declaration covers both directions, so the writer and the reader
cannot disagree. A codec applies to a `T`, `Option<T>` or `seq<T>`
field, and it must fit the field's type; this is checked where the
record is declared. `[<EpochSec>]` and `[<Seconds>]` raise an error on
a value with sub-second precision rather than silently dropping it.
Codecs affect only `to`/`from` `json`, `jsonl` and `yaml`; `show` and
`==` ignore them.
- `Bytes` is refused; the error suggests `Bytes.toBase64`.
- `Secret` is always refused. Credentials are never serialized, in any
  encoding.

## Anonymous shapes at the boundary

For a shape you read only once, you can write the type inline:

```weir
let n = ["{\"count\": 3, \"noise\": true}"] |> from json {| count: int |} |> _.count
print $"{n}"
```

Declared records are distinct types even when their fields match.
Two anonymous shapes with the same fields are the same type, whatever
the field order.
