#!/usr/bin/env bash
# The surface gate [D:skill-surface]: every module member the binary
# SHIPS appears in skills/weir/SKILL.md — the qualified spelling
# (`Module.member`) or a backticked bare name (`member`) — or is listed
# in ci/skill-omitted.txt as omitted ON PURPOSE with a reason. #help
# enumerates the real surface (the derived sources, machine-readable by
# construction), so the doc's completeness is a CHECKED property, not a
# hope — the fallback protocol ("if a feature is not in the skill file,
# assume it does not exist") silently depends on it. The omitted list
# is swept both ways: a documented or vanished entry there is stale and
# fails too.
set -euo pipefail

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"
"$(dirname "$0")/check-fresh.sh" "$BIN"

SKILL="$(dirname "$0")/../skills/weir/SKILL.md"
OMIT="$(dirname "$0")/skill-omitted.txt"

surface=$(mktemp)
trap 'rm -f "$surface"' EXIT

mods=$(printf '#help\n#quit\n' | "$BIN" 2>/dev/null | sed 's/^weir> //' \
    | awk '/^Modules:/{sub(/^Modules:/,""); f=1} f{print; if (!/[A-Za-z]$/ && NR>1) exit}' \
    | tr -s ' \n' '  ')

for m in $mods; do
    printf '#help %s\n#quit\n' "$m" | "$BIN" 2>/dev/null | sed 's/^weir> //' \
        | awk -v m="$m" '
            /^'"$m"' \([0-9]+ members\):/ { f=1; next }
            f && /^  / { for (i=1;i<=NF;i++) print m "." $i; next }
            f { exit }'
done > "$surface"

count=$(wc -l < "$surface")
[ "$count" -gt 100 ] || { echo "skill-surface FAIL: extractor found only $count members — the parse broke, not the doc" >&2; exit 1; }

python3 - "$surface" "$SKILL" "$OMIT" <<'PYSURF'
import re, sys
surface = [l.strip() for l in open(sys.argv[1]) if l.strip()]
skill = open(sys.argv[2]).read()
omitted = {}
for line in open(sys.argv[3]):
    line = line.strip()
    if not line or line.startswith('#'):
        continue
    name, _, reason = line.partition(' ')
    omitted[name] = reason

# covered = the qualified spelling anywhere, or the bare name backticked
backticked = set(re.findall(r'`([A-Za-z][A-Za-z0-9]*)`', skill))
missing, covered_omits = [], []
for qual in surface:
    mod, _, mem = qual.partition('.')
    hit = qual in skill or mem in backticked
    if qual in omitted:
        if hit:
            covered_omits.append(qual)
        continue
    if not hit:
        missing.append(qual)

stale_omits = [q for q in omitted if q not in surface]

bad = False
if missing:
    bad = True
    print(f"skill-surface FAIL: {len(missing)} shipped member(s) absent from SKILL.md and not omitted-on-purpose:", file=sys.stderr)
    for q in missing:
        print(f"  {q}", file=sys.stderr)
if covered_omits:
    bad = True
    print(f"skill-surface FAIL: omitted-on-purpose but actually documented (stale omit): {covered_omits}", file=sys.stderr)
if stale_omits:
    bad = True
    print(f"skill-surface FAIL: omitted-on-purpose but no longer shipped: {stale_omits}", file=sys.stderr)
if bad:
    sys.exit(1)
print(f"skill-surface: {len(surface)} members across the module table — every one documented or omitted-on-purpose ({len(omitted)} omits)")
PYSURF
