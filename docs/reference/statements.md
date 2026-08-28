# Statements

## The statement rule

Two kinds of statement produce output or effects on their own:

- a COMMAND LINE streams its output as the child produces it;
- every other statement must be unit — bind a value (`let x = …`)
  or print it (`expr |> print`).

A value dropped on the floor is a check error, not silent output:

```weir-error
ls |> Seq.length // computes an int and discards it — bind it, or pipe it to print
```

## Layout

A statement starts at column 0. Indented lines continue it. The
next column-0 line ends it. Blank lines and comment lines are
transparent, so blocks group freely with gaps.

An indented `let` closes at the next line of the same indent. A
bracket left open holds the statement — but the closer must not
fall to column 0 mid-statement:

```weir
let xs = [
    1
    2
    ]

print $"{xs |> Seq.length}"
```

One layout form is armed by the line's END: a statement line ending
in the `yaml` marker or the `<<<`/`$<<<` heredoc glyph opens a
district — the indented block below is that literal's content, not
weir statements, and the first shallower line closes it. The
heredoc forms are in [Lexical](lexical.md#strings); the `yaml`
template in [the guide](../GUIDE.md#commands-and-processes) and
[Adapters](adapters.md). A marker with no indented block below it
is an error naming the marker.

```weir
let motd = $<<<
    one line, {1 + 1} holes, $literal bytes

motd |> Seq.iter print
```

## Blocks

Same-indent lines under a block head are siblings and run in
order; each but the last must be unit; the last expression is the
block's value. A guard line before the result works the way it
reads:

```weir
type Target = { Name: string }

let target =
    let stack = "web"
    if stack == "" then fail "usage"
    { Name = stack }

print target.Name
```

## Sequencing with `;`

`;` sequences statements on one line — and it binds INTO an `if` or
`match` body, block-shaped. Both statements below belong to the
then-branch; nothing prints:

```weir
if 1 > 2 then print "a" ; print "b"
print "after"
```

To sequence AFTER an `if`, put the next statement on its own line
(or parenthesize the `if`). In a command line, `;` is a literal
argv word — it does not chain commands; one command per line.

## `if` / `elif` / `else`

`if` is an expression. `else` is optional only when the then-branch
is unit; `elif` is short for `else if`. The condition takes a
command chain directly — its argv stops at `then`, and the checker
still demands `bool`:

```weir
if git rev-parse HEAD | succeeds then print "in a repo"
```

## `let`

Binds a value, defines a function (`let f x y = …`, curried),
destructures (`let host, port = target`,
`let { names = n } = row`), and takes a bare command chain on its
right-hand side anywhere a `let` goes:

```weir
let tree = git rev-parse HEAD |> Seq.head
print (Str.sub 0 7 tree)
```

## `for … in … do`

The effect loop: a typed seq on the right, a pattern binder on the
left, a block body that streams and raises per iteration. It
desugars to `Seq.iter`; pipelines remain the way to transform
values. The comprehension form builds a seq:

```weir
for greeting in ["hello"; "again"] do
    print greeting

let squares = [for x in [1..5] -> x * x]
squares |> Seq.map show |> print
```

## Declarations

`type` and `module` are statements too; `import` must come first in
the file. A file that starts with `module` is a module —
declaration-only, importable, not runnable. Directives (`#sig`,
`#schema`) sit at the file head and are read at check time.
