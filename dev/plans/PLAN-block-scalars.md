# weir — block scalars: multiline strings in yaml districts

Status: EXECUTED (2026-07-31; blessed 2026-07-30, one session).
AMENDED in-flight by the user: chomping is not a feature weir chooses
to support — it is YAML's spelling for a distinction weir's strings
already have ("does this end with a newline"). So there is no policy
and nothing to decide: the form follows the value, both directions.
`|` means the string ends with exactly one newline; `|-` means it
does not. Read side has no option (helm charts use `|-` heavily, and
treating it as `|` hands back a silently wrong value); write side has
no option either (always emitting `|` would break a no-op round
trip). Both forms pinned in both directions.

Session report: all work items landed — see NOTES "block scalars —
the ConfigMap gap closes, and two walls moved" and the
[D:block-scalars] DECISIONS row. The (b) probe's answer: the
multiline splice ESCAPED (bug against session-1 intent); the read
side's `|` gave a misleading both-inline-and-nested error. Two walls
the plan didn't fully see moved: blank lines inside active yaml
districts now ride the sentinel (the assembler's transparency would
have silently dropped them from block content), and the directive
scan narrowed to column-0 (`#!/bin/sh` in a block is content) —
closing the parked full-line-`#`-in-districts bound as a side
effect. The blockness-recording decision: the internal `NBlock` node
case (an int/bool field errors on a block scalar); form preservation
falls out of the render law, no carried flag. The
multiple-trailing-newlines decision: ERROR at render, per the
recommendation. 952 unit (+4), e2e ConfigMap battery, grammars
engine-verified (tree-sitter scanner block mode; TextMate
while-backreference region), full ritual green.

## The original plan

Origin: yaml session 1 left block scalars out with a teaching error
pointing at the `\n`-escaped quoted spelling, noted as
district-session polish; the district landed without them. This
closes the gap — and the ConfigMap case (an embedded script or
config file) is the district's most obvious real workload, so the
gap is load-bearing rather than cosmetic.

### The three cases, and their order

**(b) A SPLICED value that contains newlines — the priority.**

    data:
        config.yaml: $contents        -- contents : string, multiline

Session 1's design intent was "a multiline string becomes a block
scalar automatically", and it may be a one-line renderer branch.
PROBE FIRST and report: today this either escapes to
`"line1\nline2"` (valid YAML, ugly, and *wrong* for something a
human will `kubectl apply` and then read) or it errors. Which it
does decides whether (b) is a bug against stated intent or a missing
feature. This is the common real case — `File.read` a file, splice
it into a ConfigMap — and it is what makes the district useful for
the workload it was designed for.

**(a) A literal block scalar WRITTEN in the template.**

    data:
        setup.sh: |
            #!/bin/sh
            echo hello

A parser extension: recognize the header, consume the indented
block, detect content indentation, dedent, join, apply chomping.

**(c) A spliced `seq<string>`** — needs NO new feature. `seq<string>`
splices as sequence items by the district's existing law; "these
lines are one block scalar" is spelled `Str.join "\n"` upstream,
which reuses (b). State it in the docs; do not add a form.

### THE CENTREPIECE DECISION — block scalar content is LITERAL

No splices, no `for`, no interpretation of any kind inside a block
scalar's content.

The reason is the footgun, and it is severe: embedded shell scripts
and config files are FULL of `$VAR`, and silently substituting weir
values into them would be catastrophic — the `sh -c "echo got-$w"`
fixture bite writ large, but silent and in production. A line
reading `for x in xs` inside a block scalar is likewise literal
text, not a directive: the claiming argument that lets `for` own its
shape in node context does not extend into a region whose whole
meaning is "these bytes, verbatim".

So the rule is one sentence: *a block scalar's content is bytes;
templated content comes from splicing a whole value.* Which is
coherent, resolves the collision, and gives the escape:

    let script = $"#!/bin/sh\necho {name}\n"
    ... yaml
        data:
            setup.sh: $script

The trade — a mostly-literal script with one weir value loses the
paste-a-YAML-block ergonomics — is accepted and stated. Rejected:
an opt-in "templated block" marker, because inventing YAML syntax
is what the subset discipline exists to refuse.

Implementation consequence, deliberate not incidental: the block
scalar must consume its content lines as TEXT *before* the splice
and `for` scanners run over them. Order matters; pin a block scalar
whose content contains `$name`, `$(expr)`, and a `for x in xs` line,
all surviving verbatim.

### Which forms (the subset, widened by exactly two)

- `|` (literal, ends with one newline) and `|-` (literal, ends with
  none) — supported, both directions. These are what real manifests
  use (scripts, certs, embedded configs).
- `>` / `>-` (folded) — REJECTED with a teaching error naming `|`.
  Fold semantics are a genuine trap (blank lines become newlines,
  more-indented lines stay literal) and folded scalars are rare in
  the manifests this serves.
- `|+` (keep all trailing newlines) and explicit indentation
  indicators (`|2`) — REJECTED with teaching errors. Both exist for
  cases the subset does not have.
- Every rejection carries its line (the owned parser's dividend, and
  the bar session 1 set against YamlDotNet's position-less messages).

The form is SEMANTIC, not cosmetic: `|` yields `"a\nb\n"` and `|-`
yields `"a\nb"` — the form and the value agree by construction (the
amendment). Pin both.

### The read side (`from yaml` and the district's check-time parse)

- Content indentation is detected from the first non-empty content
  line; blank lines inside the block become newlines; more-indented
  lines keep their extra indentation (the one fold-adjacent rule
  that literal scalars still have).
- A block header with no indented block following it is a located
  error.
- Blockness is quotedness's sibling: recorded internally (the
  `NBlock` case), a block scalar is unambiguously a string (int/bool
  fields error). Round-trip pins assert VALUE as the contract; form
  preservation falls out of the render law (no carried flag needed).

### The write side (the renderer's rule)

- A string containing `\n` renders as a block scalar; one without
  renders as today (quoted only when the reverse-Norway law demands).
- Form choice is the value: one trailing newline → `|`; none → `|-`.
  Content ending in MULTIPLE newlines cannot be expressed (`|+`'s
  job, rejected) — it ERRORS with a teaching message, because
  silently dropping bytes from a value is the one thing a renderer
  must never do.
- Content indentation is the key's indentation plus one level.
- Interaction with the quoting law: a multiline string is a block
  scalar and therefore NOT quoted; a single-line string that merely
  LOOKS like a number still quotes. Pinned at the boundary (a
  one-line `"007"` quotes; a two-line string starting `007` is a
  block scalar).

### The district's indentation interaction (the sharp pin)

Block scalar content is the most deeply nested text weir has. Three
machineries meet: the assembler (verbatim relative-indent lines —
verified, spans land true; a header error points at the header), fmt
(offsets-within-offsets ride the district law; pinned idempotent,
never re-indents content), and the three editor grammars
(tree-sitter scanner block mode — content never scanned for splices
or `for`; TextMate while-backreference region; micro stays exempt).

### Bars

- Zero movement on existing yaml behavior — except the
  reverse-Norway MULTILINE case, which moved WITH the feature
  (multiline = block, not quoted), deliberately and named.
- The old block-scalar teaching narrowed to the rejected forms.
- Adversarial content pins: `$name`, `$(cmd)`, `for x in xs`,
  key-shaped lines, `#`-shaped lines, blank lines, more-indented
  lines.
- Round-trip pins assert the value contract; form preservation holds
  via the render law.
- The subset is RESTATED wherever docs mention YAML — it grew by
  `|` and `|-` and by nothing else.
