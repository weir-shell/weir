# Diagnosis: `#save` dumps non-checking garbage

Ruling: **option B — distill definitions**. A REPL session is scratch;
`#save` crystallizes the reusable definitions and GUARANTEES the output
`weir check`s clean.

## Repro

A realistic exploratory session, piped:

```
let raw = ["{\"host\": \"h\", \"port\": 8080}"]
#infer raw from json as Endpoint
let manifest = <<<
    apiVersion: v1
    kind: Pod
    name: web
[1; 2; 3]
type Color = Red | Green
type Color = Red | Green | Blue
let gobeldy = it
manifest
#save out.weir
```

The old `#save` writes (and it does NOT check):

```
let raw = ["{\"host\": \"h\", \"port\": 8080}"]
type Endpoint = {
    host: string
    port: int
}
let manifest = <<<<GS>apiVersion: v1<GS>kind: Pod<GS>name: web
let _r1 = [1; 2; 3]
type Color = Red | Green
type Color = Red | Green | Blue
let gobeldy = it
let _r2 = manifest
```

`weir check` errors:

- **flatten** `[assembly] illegal control character` — the heredoc's
  physical lines were joined by the assembler's group-separator sentinel
  (`ll.Text`); the recorder saved the JOINED logical line, not the source.
- **dup-type** `type 'Color' is already declared` — the redefinition is
  dumped verbatim; a script errors where the REPL replaced.
- **it unbound** `let gobeldy = it` — `it` is REPL-only; in a script it
  resolves as an (absent) command.
- noise: `let raw` (only fed `#infer`) and `let _r2 = manifest` are
  scratch echoes with no reuse value.

## Fix (option B)

1. Recorder carries the PHYSICAL source of multi-line statements (unify
   the 1931/1994 `ll.Text` paths with the 1740 physical path).
2. `#save` KEEPS `type` decls + named `let` bindings; DROPS discards and
   exploration echoes.
3. DEDUP by name/type, keep LAST, preserve survivor order.
4. CHECK GUARANTEE: assemble candidate, drop any statement that still
   doesn't check (e.g. `let gobeldy = it`), print a dropped-count note.
