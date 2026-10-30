# Modules and imports

## What a module is

A file that starts with `module` (alone, or `module Name`) is a
module. You import it; you can't run it.

It holds only declarations: `type` and `let`. No commands, no bare
expressions.

A `let` can't run a command at import time. That rules out a reifier
(`| line` and the others), `|> Seq.freeze`, and passing the command to
a function. Wrap the command in a function instead; it runs when a
script calls it.

A plain command value is fine (`let log = git log -n 10`, or a
pipeline of commands). It's lazy: importing runs nothing, and each use
runs it again.

Top-level `let` values are evaluated when the importing program runs.
`weir check` never evaluates anything.

## Import forms

Imports usually come first. A later `import` works too; its alias is
usable from that line down. A path resolves by how it's written, never
by which files exist:

- `import "./lib/x.weir" as X` — a file path, relative to the
  importing file's directory (`lib.weir` and names without an
  extension work too), or an absolute path. If the importing file is
  a symlink, its real location counts: a script linked into `~/bin`
  imports the files beside the original
- `import "weir:name" as N` — a vendored module: weir looks upward
  for a `.weir/` directory and loads `.weir/modules/name.weir`
  ([tooling](../tooling.md#remote-modules))
- `as` is optional — the alias defaults to the module's declared
  name, or the capitalized filename

```weir
let modSrc = <<<
    module RefMod

    /// doubles
    let twice : int -> int

    let twice n = n * 2

let useSrc = <<<
    import "./refmod.weir" as M

    print $"{M.twice 21}"

modSrc |> File.write "refmod.weir"
useSrc |> File.write "use-refmod.weir"
weir use-refmod.weir
```

## The signature is the export

A signature line exports a member: `let twice : int -> int`, no `=`,
above the implementation.

- **No signature, no export.** Unsigned members are private: inferred
  inside the module, invisible to importers. Using one from outside is
  a check error that shows the signature to add, inferred type
  included.
- **The implementation needs no annotations.** It's checked against
  the signature, so a parameter can pattern-match on its declared
  union, and a generic signature (`'a -> 'a`) is enforced: a less
  general implementation is an error.
- **Order.** The signature comes first. A signature with no
  implementation is an error.
- **Docs.** `///` goes on the signature line.
- **Types** are always exported.
- **Scripts** can't use signatures; types there are always inferred.

## Qualified access, and what never leaks

Imported values are always qualified: `X.helper`. A module exports
only its own declarations, never what it imported. A local declaration
takes precedence over an imported name.

### Types

An imported type is `X.Ctx`, in every place a type is written:

- a record field, a union case's payload, a signature
- the `from json` slot: `from json X.Ctx`, `seq<X.Ctx>`,
  `Map<string, X.Ctx>`
- `Env.load` / `Args.load`
- the record literal `X.Ctx { field = v }`

The plain name (`Ctx { field = v }`, `from json Ctx`) works while only
one import declares it.

When two imports declare the same name, the plain name refers to
neither: writing it is an error listing `A.Ctx` and `B.Ctx`. They are
different types even with identical fields, and errors and hovers show
the qualified name.

A type you declare yourself keeps the plain name; an import of the
same name below it is reached qualified. Declaring a type below an
import that already provides the name is an error. The same holds
inside a module, where declaring a type twice is an error too.

Anonymous shapes (`{| k: int |}`) belong to no module: two modules
returning the same shape return one type.

### Union cases

Construct with the qualified case: `X.Red`. In patterns, bare
(`| Red ->`) and qualified (`| X.Red ->`) both work, in every pattern
position: match arms, `let`, parameters, lambdas, nested patterns.

- A bare case resolves against the value's type, as in a match arm.
  `let (Red r) = paint` works for an imported single-case union; a
  multi-case one is refused in a `let` because the pattern can fail.
- A qualified case is checked against the value: `Y.Red` on an `X`
  value is a type error naming both types.
- When two imports declare the same case name and nothing types the
  value, a bare case is an error listing the qualified spellings.

### Validated types

A [validated type](types.md#validated-types) exports with its
`create`, since types always export: `X.Email.create`, or bare
`Email.create` while only one import declares `Email` (with two, the
error names both spellings).

Its case exports for patterns (`| X.Email raw ->`) but not for
construction: `X.Email "a@b"` is refused, naming `X.Email.create`.

```weir
let langA = <<<
    module

    type LangDefaults = { symbol: string }

    let make : string -> LangDefaults

    let make s = { symbol = s }

let langB = <<<
    module

    type LangDefaults = { symbol: string; style: string }

let root = <<<
    import "./lang-a.weir" as A
    import "./lang-b.weir" as B

    let a = A.make "py"
    let b = B.LangDefaults { symbol = "rs"; style = "bold" }
    print $"{a.symbol} {b.style}"

langA |> File.write "lang-a.weir"
langB |> File.write "lang-b.weir"
root |> File.write "use-langs.weir"
weir use-langs.weir
```

```weir
let paintA = <<<
    module

    type Paint = Paint of string

let paintB = <<<
    module

    type Paint = Paint of int

let walls = <<<
    import "./paint-a.weir" as A
    import "./paint-b.weir" as B

    let (A.Paint colour) = A.Paint "red"
    let coats (B.Paint n) = n
    let label = match A.Paint "blue" with | Paint c -> c
    print $"{colour} {coats (B.Paint 2)} {label}"

paintA |> File.write "paint-a.weir"
paintB |> File.write "paint-b.weir"
walls |> File.write "walls.weir"
weir walls.weir
```

## The graph

Imports are transitive. A module imported by two others is checked
only once. An import cycle is a check error that shows the loop, and
a module cannot import itself. Paths are resolved at check time, from
the path as written; nothing is looked up at runtime, and a missing
file is an error at the import that shows the resolved absolute
path.

## At the prompt and in `-e`

In a script or the REPL's [init file](../repl.md#the-init-file), a
relative path resolves from the importing file. At the REPL prompt
it resolves from the session's current directory when the import
runs, and in `-e` from the directory weir was started in. The rules
above all hold there too. At the prompt, running an import again
reloads the file (see
[Importing at the prompt](../repl.md#importing-at-the-prompt)).

## Capabilities travel

`check --can` follows imports transitively: a module's commands,
file writes and network access appear in the importing script's
report, located at the module's own `file:line`.
