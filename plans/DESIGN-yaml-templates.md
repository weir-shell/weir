# weir — YAML templates: the district, the data path, the adapters

Status: PROPOSED (drafted 2026-07-30, awaiting bless) — the design for
"stringless YAML templates" (the Yzl want, re-thought for weir) plus
`from yaml T` / `to yaml`. Direction chosen by the user: A + B +
adapters, with in-district iteration (`for x in xs`). Implementation
is an arc of its own sessions once blessed; this file is the decision
record in the making.

Origin: the config-format spike proved the strict-YAML subset
(scalars, block maps, block sequences, `#` comments) is SMALL — small
enough to OWN. That flips the dependency question: weir does not need
YamlDotNet; it needs ~a few hundred lines on machinery it already has
(the assembler's indentation capture, the splice discipline, the
renderer conventions). One owned parser, weir's own error positions,
zero dependency bytes.

## The pitch, one sentence

Helm templates without the string horror: YAML you can paste, splices
that are typed, loops that produce NODES — so indentation, quoting,
and injection are the renderer's problem and cannot be wrong.

## The three pieces

### A. The `yaml` district — a checked block literal

A third district species (after `!` command blocks): `yaml` at
line end opens an indented block that IS the strict-YAML subset,
parsed AT CHECK TIME into a typed node tree.

    let deployment name replicas image = yaml
        apiVersion: apps/v1
        kind: Deployment
        metadata:
            name: $name
            labels:
                app: $name
        spec:
            replicas: $replicas
            containers:
                for c in image |> containers
                    - name: $(c.name)
                      image: $(c.ref)

- **Structure errors are check errors**: bad indentation, duplicate
  keys, tabs, anchors/aliases (outside the subset — a teaching
  error), all located with weir's own line:col.
- **Splices are typed**: `$name` / `$(expr)` in VALUE position —
  a scalar type renders as a scalar node (quoting/escaping is the
  renderer's job; a multiline string becomes a block scalar
  automatically); a `Yaml` node splices as a subtree; a `seq<Yaml>`
  splices as sequence items; an `Option<scalar>` OMITS the whole
  `key: value` line on `None` (the json-option precedent). KEY
  position: `$ident` of type string only (computed keys exist via
  `for` over pairs — see below); no `$()` expressions in key
  position (injection-adjacent, and the `for` form covers the want).
- **The injection story is the argv story transplanted**: spliced
  values are NODES, never text — you cannot write a YAML injection
  in weir, for the same reason you cannot write an argv injection.
- The block evaluates to a `Yaml` node (composable: pass subtrees to
  and from functions), not to a string — rendering is `to yaml`.

### The `for` directive (in-district iteration — the user's ask)

PREREQUISITE: `for`/`do` land first as weir's GENERAL iteration form
(PLAN-for-do — statement `for p in xs do body` desugaring to
`Seq.iter`, comprehension `[for p in xs -> e]` to `Seq.map`; `for`
and `do` reserved). The district's `for` is then that form
SPECIALIZED to node context — this section defines only the
specialization, not the form.

    containers:
        for img in images
            - name: $(img.name)
              ports:
                  for p in img.ports
                      - containerPort: $p
    labels:
        for (k, v) in pairs
            $k: $v

- **In a district, `for` has no `do` and no expression body** — its
  body is the indented SUB-TEMPLATE (YAML-subset lines), instantiated
  per element; the head (`for <binderPat> in <expr>`) is the general
  form's head, same binder patterns, same plain-expression-after-`in`
  rule.
- **Claimable without ambiguity**: in the subset every line is
  `key:`, `key: value`, `- item`, or a comment; a bare
  `for x in ...` line parses as none of them, so the directive owns
  the shape (as `#` owns comments).
- **The body is parsed ONCE at check time**; the loop variable is
  typed as the sequence's element type and every splice of it
  type-checks. Helm/Jinja loops are textual and unchecked; these
  produce nodes — instantiation indentation is the renderer's
  problem.
- **Context decides the yield**: under a SEQUENCE, N items; under a
  MAPPING, N key-value pairs — which is the open-map answer
  (labels/annotations with dynamic keys: `for (k, v) in pairs` →
  `$k: $v`, typed). The context rule is the district's ONLY addition
  to the general form.
- Nesting composes (sub-templates all the way down).
- DECIDE IN-SESSION: an `if cond` sibling directive (same claiming
  argument) — or park it; `Option`-splice omission plus
  `Seq.where` upstream covers much of the want.

### B. Plain data — records ARE templates

`to yaml` accepts plain weir values with a TREE law (richer than
json's flat-row law, because YAML is a document format, not a row
stream): nested records, `seq<'a>` → block sequence, scalars,
`Option` omitting on `None`; `seq<string * 'a>` renders as a
mapping (the pair-seq is the open-map spelling in data land).

Modules (just landed) make schemas shareable and the named-literal
form makes construction read like a template:

    import "./k8s.weir"
    K8s.Deployment {
        apiVersion = "apps/v1"
        metadata = K8s.Meta { name = name; labels = [("app", name)] }
        ...
    } |> to yaml

A and B COMPOSE: `to yaml` maps records → the same `Yaml` nodes the
district builds; a district splice can embed a record; a record field
can hold a district-built node.

### The adapters

- **`from yaml T`** — mirrors `from json T` with the TREE field law
  (nested records + `Option` + `seq` fields + scalars); a multi-doc
  stream (`---`) yields `seq<T>` (the k8s case); anchors/aliases
  REJECTED with a teaching error naming the subset. Input is
  `seq<string>` (lines), the boundary-family convention.
- **`to yaml`** — records/values/nodes → YAML text (`seq<string>`
  lines); a `seq` at the top level renders `---`-separated documents.
  Rendering rules stated once: block style always, strings quoted
  only when needed (the Norway problem in REVERSE: `"no"`, `"1.0"`,
  `"on"` get quotes so a YAML reader cannot mis-type them), multiline
  strings as block scalars.

## The engineering scope

- **One owned subset parser** (no YamlDotNet — the spike's numbers
  are the receipt: the node API costs +677 KB for a subset weir can
  hand-roll on the assembler's indentation machinery). Three
  customers: the district (check time), `from yaml` (runtime,
  per-document), and nothing else — config stays TOML.
- **A `Yaml` node union** — the value-domain questions must be
  answered like Group/Completed were: `show`, equality, what the
  checker's field law says. Likely prelude-declared
  (`type Yaml = ...` with Str/Int/Bool/Seq/Map cases), builtin-owned.
- **The district is the grammar-heavy half**: a new district species
  in the assembler + a check-time template parser + the `for`
  binding in the checker (a scoped lambda-param-style binding).
- **The renderer** is shared by `to yaml` and district evaluation;
  the quoting rules are ITS one law (stated once, pinned).

## The arc (each session its own bless)

0. **PLAN-for-do** (its own mini-plan, not this arc's): the general
   `for`/`do` statement + comprehension, desugar-to-Seq.iter/map,
   the reserved words. Lands before session 2 below.
1. **The `Yaml` node union + `to yaml` (data path) + `from yaml T`**
   — the subset parser, the renderer, the tree field law, multi-doc,
   the reverse-Norway quoting pins. No grammar change.
2. **The `yaml` district + splices + the `for` specialization** —
   the assembler species, check-time template parsing, typed
   splices, the two `for` contexts, Option-omission. The grammar
   session (depends on 0).
3. (rider or session 3) editor support — the district needs the
   colorizer/tree-sitter treatment the `!` district got (the drift
   rule applies across the three grammars).

## Bars

- Zero movement on everything existing; every named error pinned
  with exact text; the subset STATED in docs wherever YAML is
  mentioned (so "weir speaks YAML" always means the subset).
- The renderer's quoting law pinned adversarially (`no`, `1.0`,
  `on`, `null`, strings-with-colons, multiline).
- Round-trip pins: `from yaml` ∘ `to yaml` on the fixture corpus.
- The district's check-time errors carry weir positions (the
  YamlDotNet message-lacks-position finding is the bar to beat).

Parked with triggers: flow-style (`{a: 1}`) parsing (trigger: a real
manifest that needs it); anchors (probably never — a teaching error
is the feature); `if` directive (decide in session 2); JSON tree
emission via the same node union (`to json` on `Yaml` — trigger: a
consumer).
