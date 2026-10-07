# Modules and imports

## What a module is

A file that starts with `module` (alone, or `module Name`) is a
module. It can be imported, it contains only declarations (`type`
and `let`; no commands and no bare expressions), and it cannot be run
on its own. A module's `let` cannot run a command when the module is
imported; wrap the command in a function, and it runs when a script
calls that function. A module's top-level `let` values are evaluated
when the importing program runs, never by `weir check`, which never
evaluates anything.

## Import forms

`import` comes first in the file, before any declarations. How a
path resolves depends only on how it is written, never on which files
exist:

- `import "./lib/x.weir" as X` — a file path, relative to the
  importing file's directory (`lib.weir` and names without an
  extension work too), or an absolute path
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

Imported values are always qualified: `X.helper`. Imported types can
be used by their plain name — `Ctx { field = v }`, `from json Ctx` —
and a record can also be built with the qualified `X.Ctx { field = v }`.
An imported union's cases are qualified when you construct a value
(`X.Red`) but named bare in a pattern (`| Red ->`), and a local
declaration always takes precedence over an imported name. A module exports only its own
declarations, not what it imported.

## The graph

Imports are transitive. A module imported by two others is checked
only once. An import cycle is a check error that shows the loop, and
a module cannot import itself. Paths are resolved at check time, from
the path as written; nothing is looked up at runtime, and a missing
file is an error at the import that shows the resolved absolute
path.

## Script-only

`import` needs a file to resolve paths against, so it is not allowed
in `-e` or the REPL; the error explains why.

## Capabilities travel

`check --can` follows imports transitively: a module's commands,
file writes and network access appear in the importing script's
report, located at the module's own `file:line`.
