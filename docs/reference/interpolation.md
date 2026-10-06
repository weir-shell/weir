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
off, and each line of a `$<<<` heredoc block carries these same
hole rules — with `$` still a literal byte there. The `$$<<<`
splice heredoc swaps the marker instead: `$name`/`${expr}`
substitute and braces stay literal
([Lexical](lexical.md#strings)).

## Generic holes

A hole on a parameter is generic, exactly where `show` would be: the
function takes any value a hole can render, and a row-typed field
keeps its polymorphism.

```weir
let dash n = $"-{n}"
print (dash 5 + dash "x")

type Spec = { name: string; port: int }
let addr s = $"{s.name}:{s.port}"
print (addr { name = "a"; port = 1 })
```

A hole renders what `show` renders, minus a `Secret`: passing one
to such a function is an error at the call, as it is in a hole
written directly (a Secret nested in a record renders masked). A
hole whose type nothing generalizes — inside a statement that binds
no name — defaults to `string`.

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
