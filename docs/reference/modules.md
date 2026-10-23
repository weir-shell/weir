# Modules and imports

## What a module is

A file that starts with `module` (alone, or `module Name`) is a
module. It can be imported, it contains only declarations (`type`
and `let`; no commands and no bare expressions), and it cannot be run
on its own. A module's `let` cannot run a command when the module is
imported (`| line` and the other reifiers, `|> Seq.freeze`, passing
the command to a function); wrap the command in a function, and it
runs when a script calls that function. A plain command value
(`let log = git log -n 10`, or a pipeline of commands) is allowed: it
is lazy, so importing runs nothing and each use runs it again. A module's top-level `let` values are evaluated
when the importing program runs, never by `weir check`, which never
evaluates anything.

## Import forms

Imports usually come first in the file. A later `import` works too:
its alias can be used from that line on, not above it. How a
path resolves depends only on how it is written, never on which files
exist:

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

A member is exported by declaring its signature —
`let twice : int -> int`, with no `=` — above its implementation.
Members without a signature are private to the module: their types
are inferred inside the module, importers can't see them, and using
one from an importer is a check error that shows the signature to add
(with the inferred type). The implementation needs no type
annotations, because it is checked against the signature. A parameter
can therefore pattern-match on its declared union type, and a generic
signature (`'a -> 'a`) is enforced: an implementation that is less
general than its signature is an error. The signature must come before
the implementation, and a signature with no implementation is an
error. Put `///` docs on the signature line, where the API is
documented. `type` declarations are always exported. Scripts can't
use signatures; in a script, types are always inferred.

## Qualified access, and what never leaks

Imported values are always qualified: `X.helper`. An imported type has
a qualified name too, `X.Ctx`, and it works in every place a type is
written: a record field, a union case's payload, a signature, the
`from json` slot (`from json X.Ctx`, `seq<X.Ctx>`, `Map<string, X.Ctx>`),
`Env.load`/`Args.load`, and the record literal `X.Ctx { field = v }`.
The plain name works too — `Ctx { field = v }`, `from json Ctx` — as long
as only one import declares a type of that name.

Two imports may declare the same type name. Then the plain name refers
to neither: writing it is an error that lists the qualified names to
choose from, and each type is reached as `A.Ctx` or `B.Ctx`. The two are
different types even when their fields are the same, and error messages
and hovers show the qualified name. A type you declare yourself keeps
the plain name, and an import of the same name declared below it is
reached qualified; a declaration below an import that already provides
the name is an error.

An imported union's cases are qualified when you construct a value
(`X.Red`). In a pattern you may name them bare (`| Red ->`) or
qualified (`| X.Red ->`). That holds in every pattern position: match
arms, `let` patterns, parameters, lambdas and nested patterns. A bare
case resolves against the value's type, as a match arm does, so
`let (Red r) = paint` works for an imported single-case union and a
multi-case one is refused in a `let` as a pattern that can fail. A
qualified case names its import, and the import is checked against the
value: `Y.Red` on an `X` value is a type error naming both types. When
two imports declare unions with the same case name and nothing types
the value, a bare case is an error that lists the qualified spellings.

A [validated type](types.md#validated-types) exports with its
`create`: whenever the type is exported (always — types export with
their cases), `X.Email.create` is reachable, or bare `Email.create`
while one import declares `Email` (two imports name both spellings).
Its case still exports for patterns (`| X.Email raw ->`), but not for
construction: `X.Email "a@b"` refuses, naming `X.Email.create`.
A local declaration always takes precedence over an imported name. A
module exports only its own declarations, not what it imported.

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
