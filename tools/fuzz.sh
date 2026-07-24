#!/usr/bin/env bash
# Deep fuzz run [D:fuzz-harness]: fresh seeds, 10k+ cases per invariant.
# CI never runs fresh seeds (flaky-CI is a masked-failure factory) — the
# smoke there uses the pinned seed. A failure here prints the seed; per
# the incident protocol its shrunk repro ships as a named pin.
# WEIR_FUZZ_STRICT_SPANS=1 turns on invariant 3's positional assertion
# (red today on the two pinned span classes — the pressure instrument).
#
#   tools/fuzz.sh [seed] [count]
set -euo pipefail

cd "$(dirname "$0")/.."

seed="${1:-$((RANDOM * 32768 + RANDOM))}"
count="${2:-10000}"

echo "deep fuzz: seed=$seed count=$count (WEIR_FUZZ_STRICT_SPANS=${WEIR_FUZZ_STRICT_SPANS:-0})"

WEIR_FUZZ_SEED="$seed" WEIR_FUZZ_COUNT="$count" \
    dotnet test tests/Weir.Fuzz/Weir.Fuzz.fsproj || {
    echo "deep fuzz FAILED — reproduce with: tools/fuzz.sh $seed $count" >&2
    exit 1
}

echo "deep fuzz clean: seed=$seed count=$count"
