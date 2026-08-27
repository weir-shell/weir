# Interpolation and rendering

## Holes

`$"…{expr}…"` renders `expr` into the string. A hole renders what
`show` renders — any value in the rendering family, records and
seqs included; functions never render:

```weir
type Score = { Name: string; Points: int }
let s = { Name = "a"; Points = 12 }
print $"{s} and {[1; 2; 3]}"
```

```weir-error
print $"{fun x -> x}" // 'a1 -> 'a1 cannot be shown (functions never render)
```

`{{` and `}}` are literal braces. A comment cannot live inside a
hole. The raw-interpolated form `$"""…"""` keeps holes with escapes
off, and each line of a `$text` block carries these same hole rules
— with `$` still a literal byte there
([Lexical](lexical.md#strings)).

## The bare-hole default

A hole must give its expression a concrete type. When the
expression is an UNRESOLVED parameter, a bare hole defaults it to
`string` — so this function takes a string, and an int argument is
a type error that names the repair:

```weir
let dash n = $"-{n}"
print (dash "5")
```

The error at a mismatched call site says: a typed use in the hole
fixes it (`{n + 0}` makes `n` an int), or pass a string. A
row-typed field keeps its polymorphism through `show` where a bare
hole would default it: `$"{show c.port}"`.

## `show`

`show x` produces the same text as a hole, as a plain string. Its
places are where a hole cannot go: point-free positions
(`Seq.map show`), and `Secret` (`show` masks as `***` where
interpolation refuses outright).

Rendering is a glance, not a wire format: strings inside rendered
structures come quoted, long seqs truncate. `print` is the raw data
channel; `to json` is the wire.

## Two values render as summaries

A `Secret` renders `***` in every renderer. A `Bytes` value renders
a size summary (`<12 B>`), never content — raw bytes wreck
terminals. Each refusal at a boundary names its exit
(`Secret.reveal`; `Bytes.toBase64` / `File.writeBytes`).
