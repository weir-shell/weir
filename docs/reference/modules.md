# Modules and imports

## What a module is

A file that starts with `module` (bare, or `module Name`) is a
module: importable, declaration-only — `type` and `let`, no
commands and no bare expressions — and not runnable itself. A
module `let` cannot run a command at import; wrap it in a function
and the command runs when a script calls it. Evaluation of a
module's eager `let`s happens when the importing program runs —
never at check, which never evaluates.

## Import forms

`import` comes first in the file, before declarations. Three path
shapes, decided by shape, never by what exists:

- `import "./lib/x.weir" as X` — file-relative (any string not
  matching the other two shapes resolves against the importing
  file's directory; `lib.weir` and extensionless names included)
- `import "weir:name" as N` — the vendored namespace: resolves via
  the upward `.weir/` walk to `.weir/modules/name.weir`
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

A member exports by declaring a signature — `let twice : int -> int`
with no `=` — above its implementation. Unsigned members are
module-private: fully inferred inside the module, invisible to
importers, and importing one is a check error naming the signature
to add (inferred type included). The implementation stays
annotation-free; the signature's types flow into its checking, so a
param can pattern-match its declared union and a generic signature
(`'a -> 'a`) is honoured — an implementation that pins a signature
variable refuses as less-general. A signature precedes its
implementation; one without an implementation errors at the
signature. `///` docs attach to the signature line (the API's one
home); `type` declarations stay auto-exported. Scripts refuse the
form — scripts infer.

## Qualified access, and what never leaks

Access is always qualified: `X.helper`, `X.Ctx` for types,
`X.Ctx { field = v }` to construct. An imported union's cases are
not in scope bare, and a local declaration always wins over an
imported name. A module exports its own declarations only — no
re-export of what it imported.

## The graph

Imports are transitive; a module shared by two importers is checked
once (diamonds collapse); an import cycle is a named check error
rendering the loop; a self-import is refused. Resolution is
check-time against the literal path — nothing loads at runtime, and
a missing file is a located error naming the resolved absolute
path.

## Script-only

`import` needs a file to resolve against — it is refused in `-e`
and the REPL with that teaching.

## Capabilities travel

`check --can` walks imports transitively; a module's commands,
writes and network access appear in the importer's report at the
module's own `file:line`.
