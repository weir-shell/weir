#!/usr/bin/env bash
# Startup-time regression guard. Pinned medians (Session 4, dev container):
# expression line ~6ms, command-mode spawn ~14ms. Thresholds are generous
# (>2x pinned, plus CI-runner headroom) — this catches regressions like the
# +10ms PATH-enumeration tax, not scheduler noise.
set -euo pipefail

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"
EXPR_MAX_MS="${WEIR_MAX_EXPR_MS:-18}"
CMD_MAX_MS="${WEIR_MAX_CMD_MS:-42}"

median() {
    local expr="$1"
    for _ in $(seq 1 15); do
        start=$(date +%s%N)
        $BIN -e "$expr" > /dev/null
        end=$(date +%s%N)
        echo $(((end - start) / 1000000))
    done | sort -n | awk '{a[NR]=$1} END {print a[int(NR/2)+1]}'
}

$BIN -e '1 + 1' > /dev/null # warm the fs cache

expr_ms=$(median 'ls |> where (fun f -> f.Size > 1<mb>) |> first 5')
cmd_ms=$(median 'echo hi | first 1')

echo "expression line median: ${expr_ms}ms (max ${EXPR_MAX_MS}ms)"
echo "command-mode median:    ${cmd_ms}ms (max ${CMD_MAX_MS}ms)"

status=0

if [ "$expr_ms" -gt "$EXPR_MAX_MS" ]; then
    echo "timing FAIL: expression line ${expr_ms}ms > ${EXPR_MAX_MS}ms" >&2
    status=1
fi

if [ "$cmd_ms" -gt "$CMD_MAX_MS" ]; then
    echo "timing FAIL: command-mode line ${cmd_ms}ms > ${CMD_MAX_MS}ms" >&2
    status=1
fi

exit $status
