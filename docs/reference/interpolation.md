# Interpolation and rendering

## Holes

`$"…{expr}…"` inserts the text of `expr` into the string. A hole
renders anything `show` can, records and seqs included. Functions
cannot be rendered:

```weir
type Score = { Name: string; Points: int }
let s = { Name = "a"; Points = 12 }
print $"{s} and {[1; 2; 3]}"
```

```weir-error
print $"{fun x -> x}" // 'a1 -> 'a1 cannot be shown (functions never render)
```

`{{` and `}}` are literal braces. A comment cannot go inside a hole.
The raw interpolated form `$"""…"""` has holes but no escapes, and
each line of a `$<<<` heredoc block follows the same hole rules, with
`$` still an ordinary character. The `$$<<<` splice heredoc uses a
different marker: `$name`/`${expr}` substitute and braces stay
literal ([Lexical](lexical.md#strings)).

## Generic holes

A hole that uses a parameter makes the function generic, just as
`show` would: the function accepts any value a hole can render, and a
function that reads fields works on any record that has them.

```weir
let dash n = $"-{n}"
print (dash 5 + dash "x")

type Spec = { name: string; port: int }
let addr s = $"{s.name}:{s.port}"
print (addr { name = "a"; port = 1 })
```

The one exception is `Secret`: passing one to such a function is an
error at the call, just as it is in a hole written directly (a Secret
nested inside a record is rendered masked). A hole whose type cannot
be generalized, because the statement it is in does not bind a name,
defaults to `string`.

## `show`

`show x` produces the same text as a hole, as a plain string. Use it
where a hole doesn't fit: as a function value (`Seq.map show`), and
for a `Secret`, which `show` masks as `***` while interpolation
refuses it.

Rendering is meant for reading, not for exchanging data: strings
inside rendered structures are quoted, and long seqs are truncated.
Use `print` for raw output and `to json` for data other programs
will read.

## Two values render as summaries

A `Secret` always renders as `***`. A `Bytes` value renders as its
size (`<12 B>`), never its content, since raw bytes can garble a
terminal. Where either is refused, the error names the function to
use instead (`Secret.reveal`; `Bytes.toBase64` / `File.writeBytes`).
