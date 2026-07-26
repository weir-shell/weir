#!/usr/bin/env bash
# Doc-tests for skills/weir/SKILL.md: every fenced `weir` block must run
# clean against the AOT binary; every `weir-error` block must fail.
# A skill-file line that stops being true fails the build.
set -euo pipefail

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"

# stale-binary guard [D:masking-mechanized]: the ONE shared gate (was a
# warn-only mtime check here — a doc-test validating against a stale
# binary is exactly the masked failure the gate exists to prevent)
"$(dirname "$0")/check-fresh.sh" "$BIN"
DOCS=("$(dirname "$0")/../skills/weir/SKILL.md" "$(dirname "$0")/../docs/GUIDE.md")

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
(
    cd "$work"
    git init -q
    echo seed > seed.txt
    git add -A
    git -c user.email=ci@ci -c user.name=ci commit -qm seed
)

# extract blocks from every doc: emit "<kind>\t<file>" pairs
# (while-read, not mapfile: macOS ships bash 3.2)
blocks=()
while IFS= read -r line; do blocks+=("$line"); done < <(cat "${DOCS[@]}" | awk -v out="$work" '
    /^```weir-error$/ { kind="err"; n++; f=out"/block-"n".weir"; printf "" > f; inblock=1; print kind"\t"f; next }
    /^```weir$/       { kind="ok";  n++; f=out"/block-"n".weir"; printf "" > f; inblock=1; print kind"\t"f; next }
    /^```$/           { inblock=0; next }
    inblock           { print $0 >> f }
')

[ "${#blocks[@]}" -gt 0 ] || { echo "skill-doc FAIL: no blocks extracted" >&2; exit 1; }

i=0
for entry in "${blocks[@]}"; do
    kind="${entry%%$'\t'*}"
    file="${entry#*$'\t'}"
    i=$((i + 1))

    if [ "$kind" = "ok" ]; then
        if ! out=$(cd "$work" && "$BIN" "$file" 2>&1); then
            echo "skill-doc FAIL: block $i should run clean:" >&2
            cat "$file" >&2
            echo "--- output:" >&2
            echo "$out" >&2
            exit 1
        fi
        echo "skill-doc ok: block $i runs"
    else
        if (cd "$work" && "$BIN" "$file" >/dev/null 2>&1); then
            echo "skill-doc FAIL: block $i should be rejected:" >&2
            cat "$file" >&2
            exit 1
        fi
        echo "skill-doc ok: block $i rejected as documented"
    fi
done

echo "skill-doc: all $i blocks hold"
