# Statements

## The statement rule

There are two kinds of statement:

- a command line streams its output as the program produces it;
- every other statement must have type unit — bind its value
  (`let x = …`) or print it (`expr |> print`).

A value that is computed and then ignored is a check error, not
silent output:

```weir-error
ls |> Seq.length // computes an int and discards it — bind it, or pipe it to print
```

## Layout

A statement starts at column 0. Indented lines continue it, and the
next line at column 0 ends it. Blank lines and comment lines are
ignored for layout, so you can put gaps inside a block.

An indented `let` ends at the next line with the same indentation.
An open bracket keeps the statement going, but the closing bracket
must not be at column 0 while the statement is unfinished:

```weir
let xs = [
    1
    2
    ]

print $"{xs |> Seq.length}"
```

One layout form is triggered by how a line ends. A line ending in the
`yaml` marker or a heredoc marker (`<<<`/`$<<<`/`$$<<<`) opens a
block: the indented lines below are that literal's content, not weir
statements, and the first less-indented line ends it. The heredoc
forms are covered in [Lexical](lexical.md#strings), the `yaml`
template in [the guide](../GUIDE.md#commands-and-processes) and
[Adapters](adapters.md). A marker with no indented block below it is
an error that names the marker.

```weir
let motd = $<<<
    one line, {1 + 1} holes, $literal bytes

motd |> Seq.iter print
```

## Blocks

Lines at the same indentation inside a block run in order. Each one
except the last must be unit, and the last expression is the block's
value. A check before the result, such as `if … then fail …`, works
as you would expect:

```weir
type Target = { Name: string }

let target =
    let stack = "web"
    if stack == "" then fail "usage"
    { Name = stack }

print target.Name
```

## Sequencing with `;`

`;` puts several statements on one line. After an `if` or `match`,
everything following the `;` still belongs to the body, as if it were
an indented block. Both statements below are in the then-branch, so
nothing prints:

```weir
if 1 > 2 then print "a" ; print "b"
print "after"
```

To run something after an `if`, put it on its own line (or wrap the
`if` in parentheses). In a command line, `;` is a literal argument; it
does not chain commands, so put one command per line.

## `match` arms and a trailing `|>`

What a `|>` on its own line after a match arm does depends on its
column. Lined up with the arm's `|`, it **ends the match and pipes the
whole match** — no parentheses needed:

```weir
match 5 with
| 5 -> 50
| _ -> 0
|> print
```

That prints `50`: the whole match is piped to `print`, not just the
last arm's `0`. Put the `|>` **at or under the start of the arm's
body** and it continues *that arm* instead, whether inline or on its
own line:

```weir
match 5 with
| n -> n
       |> print
```

Any column in between — left of the body but right of the `|` — is
an error. The error tells you the body's column and suggests either
moving back to the `|` (to pipe the whole match) or under the body (to
continue the arm). weir does not guess which one you meant.

## `if` / `elif` / `else`

`if` is an expression. `else` is optional only when the then-branch
is unit, and `elif` is short for `else if`. The condition can be a
command chain written directly: its arguments stop at `then`, and the
condition must still be a `bool`:

```weir
if git rev-parse HEAD | succeeds then print "in a repo"
```

## `let`

`let` binds a value, defines a function (`let f x y = …`, curried),
or destructures (`let host, port = target`,
`let { names = n } = row`). The right-hand side can be a command
chain written directly, wherever a `let` appears:

```weir
let tree = git rev-parse HEAD | line
print tree[..6]
```

## `for … in … do`

`for` runs a block for each element of a seq, for its effects. The
seq goes on the right of `in`, a pattern on the left. The body
streams output and raises errors as each iteration runs. It is the
same as `Seq.iter`; to transform values, use pipelines. The
comprehension form `[for … -> …]` builds a seq:

```weir
for greeting in ["hello"; "again"] do
    print greeting

let squares = [for x in [1..5] -> x * x]
squares |> Seq.map show |> print
```

## Declarations

`type` and `module` are statements too; `import` must come first in
the file. A file that starts with `module` is a module: it contains
only declarations, can be imported, and cannot be run. Directives
(`#sig`, `#schema`) go at the top of the file and are read at check
time.
