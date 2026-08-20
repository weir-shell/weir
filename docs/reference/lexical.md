# Lexical

Comments, identifiers, string forms, numbers, and the unit literals.

## Comments

`//` runs to the end of the line. Full-line and trailing forms both
work:

```weir
let retries = 3 // trailing
// full-line
print $"{retries}"
```

A `//` needs a preceding space or the start of the line. Glued into a
word, it is data — `http://a` in a command line stays one word.

A bare, unquoted `//` as its own argv word reads as a comment. Quote
it (`"//"`) to pass the two characters to a command.

`//` inside any string form is data. Inside an interpolation hole it
is a parse error — write the comment outside the string:

```weir-error
print $"{1 // 2}" // a comment cannot live inside a hole
```

`///` lines are doc comments. A doc comment attaches to the
declaration directly below it; a blank line breaks the attachment,
and an attribute line (`[<...>]`) is transparent to it. The text
shows on hover and in completion. On an `Args.load` field, the first
line is also the field's `--help` text.

## Identifiers

An identifier starts with a letter or `_` and continues with letters,
digits, `_`, or `'`.

Binding names start lowercase. Uppercase names are for types,
modules, and constructors:

```weir-error
let Foo = 1 // binding names start lowercase
print $"{Foo}"
```

`rec` and `mutable` are reserved words with no meaning:

```weir-error
let rec f x = x // 'rec' is reserved; weir has no let rec
print "y"
```

## Strings

Four string forms. Every one is single-line.

**Ordinary** — `"..."` with escapes `\"`, `\\`, `\n`, `\t`:

```weir
print "a\tb"
```

A line break inside an ordinary string is a parse error:

```weir-error
let s = "a
b" // strings close before end of line
print s
```

**Verbatim** — `@"..."`. Backslashes are literal; `""` embeds one
quote:

```weir
print @"C:\temp\x"
```

**Triple-quoted** — `"""..."""`. No escapes at all; a bare `"` is
fine inside. The string closes at the first `"""`.

**Interpolated** — `$"..."`. A `{expr}` is a hole; `{{` and `}}` are
literal braces:

```weir
let n = 2
print $"n={n} with {{literal braces}}"
```

**Raw interpolated** — `$"""..."""`. Escapes off, holes on: a
backslash, a bare quote, and a splice can share one literal.

There is no `$@"..."`:

```weir-error
print $@"x" // no verbatim-interpolated form; use $"""...""" instead
```

## Numbers

Integer literals are digit runs. A prefix minus binds at operand
positions:

```weir
print $"{-5} {2 * -3}"
```

`x-1` and `x - 1` are both subtraction.

Float literals carry a decimal point (`1.5`). Nothing widens
implicitly — mixing int and float is a type error naming
`Float.ofInt`:

```weir-error
print $"{3 / 2.0}" // no implicit widening; use Float.ofInt 3
```

A float is always finite. A result that would be `NaN` or `Infinity`
raises:

```weir-error
print $"{1.0 / 0.0}" // float division by zero raises
```

Integer overflow raises rather than wrapping.

## Duration literals

Single-unit: `500ms`, `30s`, `2m`, `1h`.

```weir
print $"{90s + 45s}"
```

Compound literals do not exist — `1m30s` is a parse error:

```weir-error
let d = 1m30s // one unit per literal; write 90s, or add 1m + 30s
print $"{d}"
```

`show` renders the compound form (`90500ms` shows as `1m30.5s`), and
`Duration.parse` reads that form back.

## Size literals

Binary units only: `10MiB`, `4KiB`, `1GiB`.

```weir
print $"{1KiB + 512B}"
```

The SI suffixes are not literals:

```weir-error
let s = 10MB // binary units only in literals; Size.parse reads SI text
print $"{s}"
```

`Size.parse` accepts SI suffixes in foreign text, as powers of ten.

## In command argv

None of the above applies inside a command's argv. `30s`, `10MiB`, a
glued `//`, and a `{brace}` are all ordinary words there — argv is
the program's to interpret.
