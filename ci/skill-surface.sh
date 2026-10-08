#!/usr/bin/env bash
# The surface gate [D:skill-surface]: every module member the binary
# ships appears in skills/weir/SKILL.md — the qualified spelling
# (`Module.member`) or a backticked bare name (`member`) — or is listed
# in ci/skill-omitted.txt as omitted on purpose with a reason. #help
# enumerates the real surface (the derived sources, machine-readable by
# construction), so the doc's completeness is a checked property, not a
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

# the glance rendering [D:help-glance] is one name per line, name first —
# $1 is the module (bare #help) or the member (#help Module); the blurb
# and glance text after it never enter the surface
mods=$(printf '#help\n#quit\n' | "$BIN" 2>/dev/null | sed 's/^weir> //' \
    | awk '/^Modules:$/{f=1; next} f && /^  [A-Za-z]/{print $1} f && !/^  /{exit}')

for m in $mods; do
    printf '#help %s\n#quit\n' "$m" | "$BIN" 2>/dev/null | sed 's/^weir> //' \
        | awk -v m="$m" '
            /^'"$m"' \([0-9]+ members\):/ { f=1; next }
            f && /^  / { print m "." $1; next }
            f { exit }'
done > "$surface"

count=$(wc -l < "$surface")
[ "$count" -gt 100 ] || { echo "skill-surface FAIL: extractor found only $count members — the parse broke, not the doc" >&2; exit 1; }

python3 - "$surface" "$SKILL" "$OMIT" <<'PYSURF'
import re, sys
surface = [l.strip() for l in open(sys.argv[1], encoding='utf-8') if l.strip()]
skill = open(sys.argv[2], encoding='utf-8').read()
omitted = {}
for line in open(sys.argv[3], encoding='utf-8'):
    line = line.strip()
    if not line or line.startswith('#'):
        continue
    name, _, reason = line.partition(' ')
    omitted[name] = reason

# covered = listed on ITS module's inventory line ("- `Mod`: `a` `b` — …",
# the names before the first " — "). A bare name backticked anywhere in
# the file used to count, so a member could be missing from the list
# agents read while another module's prose carried the word.
inventory = {}
section = skill[skill.index('## Surface inventory'):]
for line in section.splitlines():
    m = re.match(r'^- `([A-Za-z]+)`: (.*)$', line)
    if m:
        head = m.group(2).split(' — ')[0]
        inventory[m.group(1)] = set(re.findall(r'`([A-Za-z][A-Za-z0-9]*)`', head))
missing, covered_omits = [], []
for qual in surface:
    mod, _, mem = qual.partition('.')
    hit = mem in inventory.get(mod, set())
    if qual in omitted:
        if hit:
            covered_omits.append(qual)
        continue
    if not hit:
        missing.append(qual)

stale_omits = [q for q in omitted if q not in surface]
shipped = set(surface)
phantom = sorted(f"{mod}.{mem}" for mod, mems in inventory.items() for mem in mems if f"{mod}.{mem}" not in shipped)

bad = False
if missing:
    bad = True
    print(f"skill-surface FAIL: {len(missing)} shipped member(s) missing from their module's Surface inventory line and not omitted-on-purpose:", file=sys.stderr)
    for q in missing:
        print(f"  {q}", file=sys.stderr)
if covered_omits:
    bad = True
    print(f"skill-surface FAIL: omitted-on-purpose but actually documented (stale omit): {covered_omits}", file=sys.stderr)
if phantom:
    bad = True
    print(f"skill-surface FAIL: listed in the inventory but not shipped: {phantom}", file=sys.stderr)
if stale_omits:
    bad = True
    print(f"skill-surface FAIL: omitted-on-purpose but no longer shipped: {stale_omits}", file=sys.stderr)
if bad:
    sys.exit(1)
print(f"skill-surface: {len(surface)} members across the module table — every one documented or omitted-on-purpose ({len(omitted)} omits)")
PYSURF
