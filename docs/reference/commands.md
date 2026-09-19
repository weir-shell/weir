# Commands

## How a line decides

The head word of a statement picks the mode. A name bound in scope
(or a builtin) makes the line an expression — ordinary application.
An unbound bareword runs the external program of that name,
resolved against PATH before anything runs. Builtins shadow PATH;
`^ls` forces the real one; params shadow PATH inside their own
body:

```weir
let greet name = print $"hi {name}"
greet "io"
echo hi io
```

## Argv

Inside command mode every word is inert — nothing expands, nothing
splits, nothing concatenates. A spliced value is exactly one argv
word. Adjacent pieces are refused rather than glued:

```weir-error
let root = "build"
rm -rf $root/* // argv words do not concatenate — write $"{root}/*"
```

What command lines do not do: no glob expansion (`Path.glob` is a
function), no `$VAR` expansion (splice weir bindings), no `&&`
(write two statements), no redirects (`>` passes through as a
literal word, with a warning naming `File.write`). For bash
semantics, run bash: `sh -c "the line"` — and inside that quoted
string, `$w` is sh's variable, not weir's; interpolate first
(`sh -c $"echo {w}"`).

## Splices

`$name` splices a binding; `(expr)` splices an expression; `$@xs`
splats a seq — N elements become N words:

```weir
let marker = "ref"
echo tagged $marker (40 + 2)
echo files: $@(["a.txt"; "b.txt"])
```

A typed value does not splice implicitly — a `Duration` argv slot
is refused with the explicit forms named (`Duration.toMillis d`, or
`show d`). A `Secret` splices in the clear (that is what the type
is for) while refusing interpolation and the wire boundaries.

## Pipes

The right-hand side decides. `|` feeds a program; `|>` applies a
function; using the wrong one is an error naming the other:

```weir
git ls-files | sort | head -1
git ls-files |> Seq.length |> show |> print
```

A value on the left of `|` becomes the child's stdin:

```weir
["b"; "a"] | sort
```

## Streaming, capture, and the markers

A bare command statement streams. A `let` in front captures —
`seq<string>`, one element per line, nothing streamed. A bare command
is also an ordinary statement in any block body and any `match` arm,
so most command lines need no marker at all:

```weir
let files = git ls-files
print $"{files |> Seq.length} tracked"

if 2 > 1 then
    git status --porcelain
    print (($(git rev-parse HEAD) |> Seq.head) |> Str.sub 0 7)
```

Two markers bring command chains into positions bare cannot reach:
`$(...)` captures (a sub-expression — inside a hole, a record, a
splice), `!(...)` runs-and-streams (unit, raises on nonzero). `$()`
appears above, in the hole. `!()`'s own niche is sequencing a command
with an expression on one line — a bare `;` is an argv word inside a
command line, so the marker is what returns to expression land:

```weir
!(git fetch --quiet); print "fetched"
```

An env overlay bound to `e` attaches as `$e(...)` / `!e(...)`. There is
no `!`-negation — negation is the word `not`; `!` means *do it*. To
swap between two known tools, branch the whole command line; for a
program that is genuinely a runtime value, force it external with a
dynamic head.

## Dynamic heads

`^$name` runs the program a string value names — `^`'s force-external
law on a runtime string. The value is one program, never re-lexed: a
head value containing spaces is one (strange) program name, argv after
it splices as typed argv, and nothing globs or word-splits. This is
the plugin/callback dispatch shape without `sh -c`:

```weir
let tool = "printf"
^$tool dyn-head-ok
```

A string-typed capture heads directly (`^$(… |> Seq.exactlyOne)`); a
seq-typed value refuses — one program has to be chosen, and weir never
picks a line implicitly:

```weir-error
^$(git branch) status // which line is the program? bind and pick first
```

Resolution happens at run, not check: `weir check` draws no
cmd-not-found diagnostic for a dynamic head (there is nothing to look
up yet), a missing program is a located run error naming the value,
and `weir check --can` reports the head as not statically known
(`--strict` treats it like the other opaque sites). Reifiers, pipes,
captures and env overlays compose exactly as with a literal head:
`^$tool build | complete` reifies the computed program's exit.

## Exit codes

A failing command raises when its stream is forced. Four forms turn
the exit into a value instead; output goes where the meaning goes:

| form | output | result |
|---|---|---|
| `cmd \| succeeds` | silent | `bool` — `exitCode == 0`, exactly |
| `cmd \| complete` | captured | `{ exitCode; stdout; stderr }` |
| `cmd \| orFail "msg"` | streams | unit; raises `msg (exit N)` on nonzero |
| `cmd \| exitCode` | streams | the code as `int`; never raises |

```weir
let r = sh -c "echo out; exit 3" | complete
print $"exit {r.exitCode}, said {r.stdout |> Seq.head}"
```

`exitCode` refuses capturing and discarding positions with a
teaching error:

```weir-error
sh -c "exit 3" | exitCode // a bare statement discards the code — bind or match it
```

## Signatures

`weir check` resolves every literal command head; a declared signature
(`#sig tool`, generated by `weir add sig`) extends the check to the
tool's flags. The mechanics live on the
[tooling page](../tooling.md#command-signatures).
