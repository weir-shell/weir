# Adapters

`from` reads a data format into a type you declare; `to` writes one.

| format | read | write |
|---|---|---|
| `json`, `jsonl`, `yaml` | yes | yes |
| `toml`, `xml`, `table` | yes | no |

You always name the format. `from json T` reads one document, however
many lines it spans. `from jsonl T` reads one document per line
(skipping blank lines) and yields `seq<T>`.

```weir
type Peer = { host: string; port: int }

let peer = ["{\"host\": \"a\", \"port\": 9000}"] |> from json Peer
print $"{peer.host}:{peer.port}"

let peers = ["{\"host\": \"a\", \"port\": 1}"; "{\"host\": \"b\", \"port\": 2}"] |> from jsonl Peer
print $"{peers |> Seq.length} peers"
```

Writing mirrors reading:

- `to json` writes one minified document. A record is an object, a seq
  an array, built in memory (one line can't be streamed).
- `to jsonl` writes one document per element, lazily.

Strings escape only what JSON requires (`"`, `\`, control characters).
`<`, `&`, `'` and non-ASCII are written as-is; characters beyond the
BMP as a `\uXXXX` surrogate pair. Each format reads back what it
writes: `to json |> from json T`, `to jsonl |> from jsonl T`.

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
error naming the cycle.

- A top-level JSON array is spelled in the type: `from json seq<Peer>`.
- `float` fields accept integer-looking numbers (JSON has one number
  type).
- A missing array is an error, not a silent `[]`. Use `Option` for
  fields that may be absent.
- Unknown keys are ignored on read.
- `[<Wire "key">]` maps a key that isn't a legal weir identifier. Two
  fields mapping to the same key are an error at the declaration.

## Documents in several shapes

A **tagged union** reads documents whose shape depends on one field.
`[<Tag "kind">]` on the union names that field.

- Each case carries a declared record, or nothing for a tag-only
  document.
- The tag value is the case name; `[<Wire "v1">]` on a case overrides
  it.
- Works in JSON and YAML, top level or nested: `from jsonl KDoc` reads
  mixed NDJSON, `from json seq<KDoc>` a mixed array.
- One `[<Other>]` case (`of string`, or no payload) catches unknown
  tags instead of raising. Without it, an unknown tag is an error.
- A document with no tag field is always an error: it's malformed, not
  unknown.
- Writing puts the tag first. An `[<Other>]` value can't be written.

Unions without a tag aren't supported; the error suggests `[<Tag>]`.

## Keys that are data

`Map<string, T>` reads an object keyed by IDs, as a field or as the
whole document (`from json Map<string, int>`; `T` is anything a field
can hold). `to json` writes the object back.

- Keys are strings at the JSON boundary. A `Map<int, T>` is fine
  elsewhere in the program; convert its keys with `show` to write it.
- Entries iterate in key order.
- For duplicate keys, the last one wins.

```weir
let counts = ["{\"b\": 2, \"a\": 1}"] |> from json Map<string, int>
counts |> Map.pairs |> Seq.iter (fun (k, n) -> print $"{k}={n}")
```

## YAML

`from yaml T` takes the same shapes as JSON, except `Map<string, T>`
fields, which yaml refuses both ways. Use a seq of pairs,
`seq<string * T>`, instead.

### Scalars and layout

- Quoting decides the type, both ways: `rate: 1.5` is a number,
  `"1.5"` a string.
- Null is an empty value or plain `null`, `Null`, `NULL`, `~` (YAML
  1.2 core schema): `None` in an `Option` field, an error in a required
  one. `"null"` is a string.
- Indentation is spaces only. A tab where indentation is read is an
  error naming the line; a tab before `#` starts a comment, like a
  space.
- A plain value trims only spaces and tabs, so a no-break space or
  U+3000 stays in the value.
- A block sequence may be indented under its key or sit at the key's
  column (kubectl's `items:` then `- apiVersion: v1`).
- Sequences may nest on the dash line (`- - x`, any depth), read as
  PyYAML reads them. A `yaml` block takes the same forms.
- Text after a quoted value's closing quote (`'a'b`) is an error.

### Numbers

Numbers follow YAML 1.2's core schema, which differs from the YAML 1.1
readers many tools still use (PyYAML, kubectl's go-yaml v2). A `Yaml`
node (`Yaml.parse`, `yaml patch`) keeps a number's original text where
the two disagree, so `to yaml` writes it back unchanged.

| input | weir reads | in a `Yaml` node |
|---|---|---|
| `0x1F`, `0o17` | int | text kept |
| `.inf`, `.nan` | number | a `YStr` that writes plain (no finite value) |
| `0644` | decimal 644 (1.1 reads octal 420); into an `int` or `float` field it's an error | text kept, so a Pod's `defaultMode: 0644` survives |
| `1e5`, `+.5`, `3.10` | float | text kept (1.1 reads `1e5`, `+.5` as strings; a short git SHA can be `1e5`) |
| `1e400` | too large for a float | a `YStr` that writes plain |
| `0x`/`0o`/decimal integer beyond int64 | — | a `YFloat` |
| `1_000`, `0b101`, `1:20` | string (1.1-only forms) | a string; `to yaml` quotes it so 1.1 readers agree |

For a leading zero in an `int` field, write `0o644` (octal), `644`
(decimal), or quote it (string). `to json` writes the value: a float
as a number, an integer as its decimal digits.

### Multi-line strings

A quoted scalar may continue on deeper-indented lines, as in kubectl's
long `message:` values:

- the closing quote ends it
- each line break folds to a single space
- an empty continuation line becomes a newline
- the folded value stays a string

Literal block scalars (`key: |`, `key: |-`) read as strings. An
indentation indicator (`|2`, `|2-`, which kubeconfigs sometimes carry)
sets the content's indentation relative to the key, so the first line
can keep leading spaces. Folded scalars (`>`) and `|+` are not
supported.

### Streams

`from yaml stream T` reads a `---`-separated stream, each document as
`T`. An empty stream gives zero documents. For a mixed bundle such as
a Kubernetes apply file, use `from yaml stream KDoc` with a tagged
union: `stream` means many documents, the union picks each one's
shape.

### Writing

- `to yaml` writes one document: a record is a mapping, a seq a
  sequence document, a seq of pairs one mapping.
- `to yaml stream` writes one document per element.
- An all-`None` record is `{}`, an empty seq `[]`, so no document is
  empty.
- A `None` record field is left out; a `None` value in a seq of pairs
  is written as `null`, so the key reads back.
- A seq of pairs or `YMap` holding a key twice is refused by `to yaml`
  and `to json`, naming the key. Reading JSON into a seq of pairs keeps
  the last value of a repeated key, at the first key's position.
- A multiline string is written as a block scalar.
- A float in exponent form gets a dot and a signed exponent
  (`1.0e-05`), which 1.1 readers need to see a number.
- An integer beyond int64, held as a `YFloat`, writes back its
  original digits (through `try` too). Read into an `int` field it's an
  error suggesting `Yaml`, since a `float` would round it.

Strings that any YAML 1.1 or 1.2 reader would type differently are
quoted, keys included:

- number forms: `007`, `1e5`, `+.5`
- booleans: `yes`, `on`, `y`, `N`, …
- the null forms, `.inf`, `.nan`
- the merge and value keys `<<` and `=`

Keys are strings (`Yaml.parse` reads `200:` as the key "200"). A key
the document wrote plain is written back plain, so an untouched round
trip keeps `200:`, `true:`, `null:` and `0644:`. A key built in weir is
quoted by the same rule as a value.

Each form reads back with its reader: `to yaml |> from yaml seq<T>`,
`to yaml stream |> from yaml stream T`.

The `yaml` template literal (checked structure, splices as nodes,
`schema=`) is covered in the [guide](../GUIDE.md#commands-and-processes);
vendoring schemas is on the [tooling page](../tooling.md#yaml-schemas).

## TOML

`from toml T` reads one TOML document — a `Cargo.toml`, a
`pyproject.toml`, a tool's config. It reads TOML 1.1 (so 1.0 too). It
is read-only: there is no `to toml`.

The document is a table, so `T` is a record or tagged union; `seq`,
`stream` and `Map` tops are refused. Fields follow YAML's rules:
tables are records, arrays and arrays of tables (`[[bin]]`) are seqs,
a key that may be missing is an `Option`, and `[<Wire "key">]` names a
key that isn't a weir name (`requires-python`).

- Integers in any spelling (`1_000`, `0xff`, `0o7`, `0b1`) read as
  `int`.
- `inf` and `nan` parse, but are an error in a `float` field (weir
  floats are finite).
- A date or time reads as its text. An offset date-time
  (`2024-03-01T10:00:00Z`; lowercase `t`/`z` and TOML 1.1's
  seconds-less `10:00Z` included) reads into an `[<Iso8601>]` `Instant`
  field. A local date-time, date or time has no offset, so it's an
  error there.
- Duplicate keys and tables defined twice are errors with the line, as
  the spec requires.

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

`from xml T` reads one XML document into `T`. Read-only, and a subset
of XML.

- The root element is the top-level record.
- A field matches a child element by local name. A default `xmlns`
  (MSBuild's) is stripped, so field names stay plain.
- `[<Attr>]` or `[<Attr "Include">]` reads an attribute.
- `[<Elem "ProjectReference">]` names the repeated child a `seq< >`
  field reads. The default is the element type's name for
  `seq<record>`, the field name for `seq<string>`.
- A nested record reads a child element.

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

### Field types

Every XML leaf is text, so a field is `string`, `Option<string>`
(element or attribute present or absent), a record, or a `seq` of a
string or record. Nothing else.

- Numbers and booleans are declared `string` and converted
  (`Str.toInt`). The checker says so; XML defines no convention to
  guess.
- `[<Attr>]` fits only `string`/`Option<string>`; `[<Elem>]` only a
  `seq`.
- The top level is one root, so there is no `from xml seq<T>`; read
  repeated children with a `seq< >` field.
- There is no `to xml`.

Element text is read as written, whitespace included, as Python's
`xml.etree` does: `<Sep>&#32;</Sep>` reads `" "`, `<Two>  </Two>`
reads `"  "`, an empty element reads `""`.

### Encodings

`from xml` also takes `Bytes` (`File.readBytes "legacy.xml" |> from
xml Proj`). They're decoded by the byte-order mark, else the encoding
the XML declaration names, else UTF-8.

- Supported: UTF-8, UTF-16, UTF-32, US-ASCII, ISO-8859-1, including the
  spellings `utf8`, `UTF8`, `Latin-1`.
- A plain-ASCII document reads under any declared encoding, so a
  Visual Studio 2008 `.vcproj` labelled `Windows-1252` reads.
- Non-ASCII under an encoding weir can't decode (windows-1252,
  Shift_JIS, …) is an error naming it. Convert the file to UTF-8
  (`iconv`) and change the declaration to `encoding="UTF-8"`.
- A malformed encoding name (`" windows-1252"`, `"ebcdic;"`) is an
  error; so is an undecodable byte.
- Text input is already decoded as UTF-8, so a text document declaring
  another encoding reads only if it's plain ASCII; otherwise the error
  asks for the bytes.
- A UTF-16 or UTF-32 declaration on bytes with no byte-order mark that
  start with a single-byte `<?xml` is an error: the declaration
  contradicts the bytes (usually the file is UTF-8).

Elements nested more than 500 deep are an error, as in YAML.

## Aligned tables

`from table T` reads the aligned column output `kubectl` and `docker`
print — one header row, then aligned data rows — into row records,
yielding `seq<T>`.

The first non-blank line is the header. Columns are cut at the
*header positions*, not at whitespace, so a value with spaces
(`Up 2 hours`, a free-text last column) stays whole. Headers are
separated by two or more spaces; a single space stays inside a header,
so `CONTAINER ID` is one column (both tools pad with three spaces).

```weir
type Pod = { name: string; status: string; restarts: int; node: Option<string> }
let pods =
    [ "NAME    STATUS    RESTARTS   NODE"
      "web-1   Running   0          k3d-a"
      "db-0    Pending   3          <none>" ]
    |> from table Pod
pods |> Seq.iter (fun p -> print $"{p.name}: {p.status} ({show p.restarts} restarts)")
```

- A field matches its header ignoring case and anything not a letter
  or digit: `name` reads `NAME`, `podTemplateHash` reads
  `POD-TEMPLATE-HASH`. `[<Wire "HEADER">]` matches a header exactly.
- Cells are trimmed and converted to the field's type (`string`,
  `int`, `float`, `bool`).
- An `Option` field reads an empty cell, or exactly `<none>` (as
  kubectl prints), as `None`. An empty cell in a required field is an
  error suggesting `Option`.
- Extra columns are ignored (like unknown JSON keys); blank lines are
  skipped.
- A header-only table is an empty seq (`docker ps` with nothing
  running).
- A missing column is an error listing the headers found; a bad cell
  is reported with its row and column.
- The result is already a seq of rows: no `seq`/`stream`/`Map` around
  the type. There is no `to table`.

`az … -o table` and other `tabulate`-style tools draw a dashes rule
under the header (`------  ----------`). It's skipped when it's the
first data row, so az output reads without a garbage row and `#infer`
types from the real rows. Only a row of nothing but dashes and spaces,
in first position, is dropped; kubectl and docker output is unaffected.

`#infer <src> from table as Pod` (or `sample |> Table.inferShape`)
drafts the row record from a real sample: each column's type, `Option`
where a column has empty or `<none>` cells, and a note that the value
reads as `seq<Pod>`.

## Editing YAML: `Yaml.parse` and `yaml patch`

`from yaml T` drops every field you didn't declare, so it's the wrong
tool for read-modify-write. Use the untyped pair, which keeps the whole
document:

- `Yaml.parse` reads one document into `Yaml` nodes. Scalars get their
  type from their text, as in a `yaml` block.
- `Yaml.merge` applies a `yaml patch` block.

The patch's *structure* says where each change goes — kustomize's
strategic merge, no path language.

| in the patch | effect |
|---|---|
| a map | merged recursively, keys added or updated |
| a sequence item | appended if absent |
| a sequence item, with `by=<key>` on the marker line | updated by that key, inserted if none matches |
| a scalar | replaced |
| `$-` as a value | removes the key it sits under |
| `- $- <content>` | removes the matching item |

Applying a patch twice equals applying it once. Map merges and `by=`
upserts don't depend on patch order; only append-if-absent does — two
patches appending to the same sequence land in the order applied. With
`by=` you never write an update-or-insert branch yourself.

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
rules:

- `to yaml` on a patch is a check error: a patch is instructions, not a
  document, so a `$-` can never end up in a file.
- `$-` outside a `yaml patch` block is an error.
- `patch` can't be combined with `schema=`: a patch is partial, schemas
  validate whole documents.
- `Yaml.parse` never produces a `$-`; parsed text is plain data.

There is no in-place edit function. Write the round trip as a pipeline
so the change stays visible:
`File.read f |> Yaml.parse |> Yaml.merge p |> to yaml |> File.write f`.

## Which types serialize automatically

A type with one obvious encoding is serialized automatically. A type
that could be encoded several ways is refused, and the error names the
explicit conversion. JSON and YAML follow the same rule both ways, with
the same messages.

| type | |
|---|---|
| `int`, `float`, `string`, `bool` | direct |
| `Uuid` | direct: written as its lowercase string, read in any form `Uuid.parse` accepts. A malformed string is an error naming the field. A JSON-lines log keyed by `Uuid.v7` stays sortable by id as text |
| `Instant`, `Duration`, `Size` | need a codec attribute, or an explicit conversion; the error offers both |
| `Bytes` | refused; the error suggests `Bytes.toBase64` |
| `Secret` | always refused: credentials are never serialized, in any encoding |

Epoch numbers and text are both reasonable encodings for time, and
weir won't guess which the other side expects. So you pick a codec:

| type | codecs | written as |
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

- One declaration covers both directions, so writer and reader can't
  disagree.
- A codec applies to a `T`, `Option<T>` or `seq<T>` field and must fit
  the field's type, checked at the declaration.
- `[<Iso8601>]` reads a JSON or YAML time without an offset as UTC.
- `[<EpochSec>]` and `[<Seconds>]` raise on sub-second precision rather
  than drop it.
- Codecs affect only `to`/`from` `json`, `jsonl` and `yaml`; `show` and
  `==` ignore them.

## Validated types at the boundaries

A [validated type](types.md#validated-types) crosses every boundary as
its payload, and every boundary runs its `validate` block. A reader
parses the payload as usual, validates it, and keeps it exactly as
read, so a round trip writes back the same bytes. A refusal (whatever
`validate` raised) becomes the adapter's own located error, naming the
field path.

This holds for record fields, seq elements, `Option` fields, `Map`
values, anonymous shapes and the whole document (`from json Email`,
`from json seq<Email>`), in `json`, `jsonl`, `yaml` (which also names
the line) and `toml`. `to json`/`to yaml` write the payload.

```weir
type Email = Email of string
    validate s = if not (Str.contains "@" s) then fail $"not an email: {s}"
type User = { name: string; email: Email }

let u = ["{\"name\": \"ada\", \"email\": \"Ada@Example.org\"}"] |> from json User
u |> to json |> Seq.iter print
let bad = ["{\"users\": [{\"name\": \"b\", \"email\": \"x\"}]}"] |> from json {| users: seq<User> |} |> try result
print $"{bad}"
```

(prints `{"name":"ada","email":"Ada@Example.org"}`, then
`Error "from json: users[0].email: not an email: x"`.)

`Args.load` and `Env.load` parse the payload like a plain field (an
`int` for `Small of int`), then validate. A refusal joins the load's
one error, naming the flag or the variable; for `Args.load` that's the
usage error (`mail.weir: --to: not an email: x`, exit 2). A
`[<Default>]` literal is validated too, and `--help` shows the type's
name as the flag's value (`--to <Email>`).

## Anonymous shapes at the boundary

For a shape you read only once, write the type inline:

```weir
let n = ["{\"count\": 3, \"noise\": true}"] |> from json {| count: int |} |> _.count
print $"{n}"
```

Declared records are distinct types even when their fields match. Two
anonymous shapes with the same fields are the same type, whatever the
field order.
