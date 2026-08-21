# YAML schemas

A `yaml` block can name a JSON schema on its marker line, and the
checker validates the template's structure before line one runs:

```text
let svc = yaml schema=k8s-service
    apiVersion: v1
    kind: Service
    ...
```

## Vendoring

```text
weir add schema <url> --as <name>    # fetches into .weir/schemas/<name>.json, locks it
```

The schema is a vendored, pinned, check-time artifact
([project layout](project.md)): `weir check` never fetches, so
checking works offline and in CI. A locked-but-missing schema file
is re-materialized by `weir restore`, hash-verified against the
lock; `weir verify` reports absent or modified schemas.

For Kubernetes, use the `-standalone-strict` schema variants —
their `additionalProperties: false` is what makes unknown-field
checking fire; the plain variants accept any unknown key.

## The validation boundary

Stated plainly so the green check is not over-read:

- a spliced `int` checks against an `integer` constraint;
- a spliced `string` against a `pattern` or `enum` constraint does
  NOT — the value is runtime data;
- `for`-generated content is structurally unchecked.

The schema validates what the checker can see. The teaching version
— templates, block scalars, splices-as-nodes — lives in the
[guide](GUIDE.md#commands-and-processes).
