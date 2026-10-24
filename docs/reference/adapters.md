# Adapters

`from` reads a data format into a type you declare; `to` writes one.
Three formats work in both directions (`json`, `jsonl`, `yaml`), and
three more are read-only (`toml`, `xml`, `table`). You always say which format
you mean: `from json T` reads one document however many lines it
spans, while `from jsonl T` reads one document per line (blank lines
are skipped) and yields `seq<T>`.

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
writes NDJSON, one document per element, lazily. Strings escape only
what JSON requires (`"`, `\`, control characters); `<`, `&`, `'` and
non-ASCII text are written as-is, characters beyond the BMP as a
`\uXXXX` surrogate pair. Each format reads back what it writes:
`to json |> from json T`, `to jsonl |> from jsonl T`.

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
as the whole document (`from json Map<string, int>`; `T` is anything
a field can hold). Keys must be strings, entries are iterated in
key order, and for duplicate keys the last one wins. `to json` writes
the object back.

```weir
let counts = ["{\"b\": 2, \"a\": 1}"] |> from json Map<string, int>
counts |> Map.pairs |> Seq.iter (fun (k, n) -> print $"{k}={n}")
```

## YAML

`from yaml T` accepts the same shapes as JSON. Quoting decides a
scalar's type in both directions: `rate: 1.5` is a number, `"1.5"` is
a string. An empty value and the plain words `null`, `Null`, `NULL`
and `~` are null (YAML 1.2's core schema): `None` in an `Option`
field, an error in a required one; `"null"` is a string. Indentation
is spaces only: a tab where indentation is read is an error naming
the line, and a tab before `#` starts a comment as a space does. A
plain value trims only spaces and tabs, so a no-break space or U+3000
is part of the value.

Numbers follow YAML 1.2's core schema, which differs from the YAML 1.1
readers many tools still use (PyYAML, kubectl's go-yaml v2):

- `0x1F` and `0o17` are ints, and `.inf`/`.nan` are numbers. Reading
  one into a `Yaml` node (`Yaml.parse`, `yaml patch`) keeps its text,
  so `to yaml` writes it back unchanged. A non-finite number has no
  weir value, so the node holds it as a `YStr` that still writes plain.
- A leading zero (`0644`) is decimal 644 in YAML 1.2 but octal 420 to a
  1.1 reader. A `Yaml` node keeps the text, so a round trip that never
  touches a Pod's `defaultMode: 0644` writes it back as it was. Reading
  a leading-zero literal into an `int` field is an error: write
  `0o644` for octal, `644` for decimal, or quote it for a string.
- A float that weir would write differently (`1e5`, `+.5`, `3.10`)
  keeps its text in a `Yaml` node too. A 1.1 reader takes `1e5` and
  `+.5` as strings (a short git SHA can be `1e5`), so a round trip
  writes them back as they were. A number too large for a float
  (`1e400`) is held as a `YStr` that still writes plain. A `0x`/`0o`
  integer beyond int64 is a `YFloat`, like a decimal one. `to json`
  writes the value: a float as a number, an integer as its decimal
  digits.
- Reading a leading-zero literal into a `float` field is an error too.
- 1.1-only forms (`1_000`, `0b101`, `1:20`) are strings, and `to yaml`
  quotes them so a 1.1 reader keeps them strings too.

A quoted scalar may continue on more deeply indented lines,
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
A record whose fields are all `None` is written as `{}` and an empty
seq as `[]`, so no document is empty. A seq of pairs, or a `YMap`,
that holds a key twice is refused by `to yaml` and `to json`, naming
the key; reading JSON into a seq of pairs keeps the last value of a
repeated key, at the first key's position. A `None` record field is
left out, while a `None` value in a seq of pairs is written as `null`,
so the key reads back. A string that any YAML 1.1 or 1.2 reader would
take as another type is quoted, keys included: number forms such as
`007`, `1e5` and `+.5`, the boolean words (`yes`, `on`, `y`, `N`, …),
the null forms, `.inf`/`.nan`, and the merge and value keys `<<` and
`=`. Keys are strings (`Yaml.parse` reads `200:` as the key "200"), but a
key the document wrote plain is written back plain, so an untouched
round trip keeps `200:`, `true:`, `null:` and `0644:` as they were. A
key built in weir is quoted by the same rule as a value. A float written in exponent form carries a dot and a signed
exponent (`1.0e-05`), which YAML 1.1 readers need to see a number. An
integer beyond int64 read into a `Yaml` node is a `YFloat`
there, but writing that node back gives its original digits (through
`try` too); reading one into an `int` field is an error that suggests
`Yaml`, since a `float` would round it. Each form reads back with the matching reader
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
  `[<Iso8601>]`, lowercase `t`/`z` and TOML 1.1's seconds-less
  `10:00Z` included; a local date-time, date or time (no offset) is an
  error there, since it names no instant.
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

An element's text is read as written, whitespace included:
`<Sep>&#32;</Sep>` reads `" "` and `<Two>  </Two>` reads `"  "`, as
Python's `xml.etree` does; an empty element reads `""`.

Every XML leaf is text, so a field is `string`, `Option<string>` (a
present-or-absent element or attribute), a record, or a `seq` of a
string or record — nothing else. A numeric or boolean field is
declared `string` and converted (`Str.toInt`); the checker tells you
this rather than guessing at a convention XML does not define.
`[<Attr>]` fits only `string`/`Option<string>`, `[<Elem>]` only a
`seq`, and the top level is a single root element, so there is no
`from xml seq<T>` (repeated children are read with a `seq< >` field).
There is no `to xml`.

`from xml` also takes the document's `Bytes`
(`File.readBytes "legacy.xml" |> from xml Proj`), which are decoded
by their byte-order mark, else by the encoding the XML declaration
names, else as UTF-8. UTF-8, UTF-16, UTF-32, US-ASCII and ISO-8859-1
are supported, including the spellings `utf8`, `UTF8` and `Latin-1`.
A plain-ASCII document reads under any declared encoding, so a Visual
Studio 2008 `.vcproj` labelled `Windows-1252` reads. Non-ASCII content
under an encoding weir cannot decode (windows-1252, Shift_JIS, …) is an
error naming it: convert the file to UTF-8 (`iconv`) and change the
declaration to `encoding="UTF-8"`. A malformed encoding name
(`" windows-1252"`, `"ebcdic;"`) is an error, and an undecodable byte
is an error.
Text is already decoded as UTF-8, so a text document whose declaration
names another encoding reads only if it is plain ASCII; otherwise the
error asks for the bytes. A UTF-16 or UTF-32 declaration on a document
with no byte-order mark that starts with a single-byte `<?xml` is an
error saying the declaration contradicts the bytes (usually the file
is UTF-8). Elements nested more than 500 deep are an error, as in YAML.

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
  offers both. `[<Iso8601>]` reads a JSON or YAML time without an
  offset as UTC.

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

## Validated types

A [validated type](types.md#validated-types) crosses every boundary as
its payload, and every boundary runs its `validate` block: a reader
parses the payload as usual, then validates (and keeps the normalised
value); a refusal is the adapter's own located error, naming the field
path. That holds for record fields, seq elements, `Option` fields,
`Map` values, anonymous shapes and the whole document (`from json
Email`, `from json seq<Email>`), in `json`, `jsonl`, `yaml` (which also
names the line) and `toml`. `to json`/`to yaml` write the payload.

```weir
type Email = Email of string
    validate s = if Str.contains "@" s then Ok (Str.toLower s) else Error $"not an email: {s}"
type User = { name: string; email: Email }

let u = ["{\"name\": \"ada\", \"email\": \"Ada@Example.org\"}"] |> from json User
u |> to json |> Seq.iter print
let bad = ["{\"users\": [{\"name\": \"b\", \"email\": \"x\"}]}"] |> from json {| users: seq<User> |} |> try result
print $"{bad}"
```

(prints `{"name":"ada","email":"ada@example.org"}`, then
`Error "from json: users[0].email: not an email: x"`.)

`Args.load` and `Env.load` parse the payload as they would a plain
field (an `int` for `Small of int`), then validate; a refusal joins the
load's one error, naming the flag (`--to: not an email: x`) or the
variable. A `[<Default>]` literal is validated the same way, and
`--help` shows the type's name as the flag's value (`--to <Email>`).

## Anonymous shapes at the boundary

For a shape you read only once, you can write the type inline:

```weir
let n = ["{\"count\": 3, \"noise\": true}"] |> from json {| count: int |} |> _.count
print $"{n}"
```

Declared records are distinct types even when their fields match.
Two anonymous shapes with the same fields are the same type, whatever
the field order.
