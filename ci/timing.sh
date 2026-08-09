#!/usr/bin/env bash
# Startup-time regression guard. Pinned medians (Session 4, dev container):
# expression line ~6ms, command-mode spawn ~14ms. Thresholds are generous
# (>2x pinned, plus CI-runner headroom) — this catches regressions like the
# +10ms PATH-enumeration tax, not scheduler noise.
set -euo pipefail

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"
EXPR_MAX_MS="${WEIR_MAX_EXPR_MS:-18}"
CMD_MAX_MS="${WEIR_MAX_CMD_MS:-42}"

# stale-binary guard [D:masking-mechanized]: the ONE shared gate — timing
# a stale binary measures the wrong build
"$(dirname "$0")/check-fresh.sh" "$BIN"

# the gates are PINNED ON LINUX (dev-container medians); BSD date has no
# %N, and a subprocess ms-clock's overhead would swamp the 6-14ms medians
# being measured — an explicit STATED skip, never a silent pass
# [D:vacuous-probe-audit]
if [ "$(date +%N)" = "N" ] || [ -z "$(date +%N)" ]; then
    echo "timing: SKIPPED — no nanosecond clock on this platform; the gates are pinned on Linux (ci/local.sh runs the clean-room numbers)" >&2
    exit 0
fi

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

EXPR_SNIPPET='ls |> where (fun f -> f.bytes > 1MiB) |> first 5'
CMD_SNIPPET='echo hi |> Seq.first 1'

# pre-flight each snippet OUTSIDE the timing substitution
# [D:masking-mechanized]: set -e is disabled inside $(...), so a snippet
# the binary rejects would otherwise time the error path 15 times
$BIN -e "$EXPR_SNIPPET" > /dev/null
$BIN -e "$CMD_SNIPPET" > /dev/null

expr_ms=$(median "$EXPR_SNIPPET")
cmd_ms=$(median "$CMD_SNIPPET")

# whole-file check on a representative script — this median is the LSP's
# per-keystroke budget (chain 2/3, 2026-07-21): no-incrementality is
# licensed by this number staying single-digit-ish
$BIN check "$(dirname "$0")/../examples/repo-report.weir" > /dev/null
check_ms=$(for _ in $(seq 1 15); do
    start=$(date +%s%N)
    $BIN check "$(dirname "$0")/../examples/repo-report.weir" > /dev/null
    end=$(date +%s%N)
    echo $(((end - start) / 1000000))
done | sort -n | awk '{a[NR]=$1} END {print a[int(NR/2)+1]}')

CHECK_MAX_MS="${WEIR_MAX_CHECK_MS:-40}"

echo "expression line median: ${expr_ms}ms (max ${EXPR_MAX_MS}ms)"
echo "command-mode median:    ${cmd_ms}ms (max ${CMD_MAX_MS}ms)"
echo "whole-file check median: ${check_ms}ms (max ${CHECK_MAX_MS}ms)"

status=0

if [ "$check_ms" -gt "$CHECK_MAX_MS" ]; then
    echo "timing FAIL: whole-file check ${check_ms}ms > ${CHECK_MAX_MS}ms" >&2
    status=1
fi

if [ "$expr_ms" -gt "$EXPR_MAX_MS" ]; then
    echo "timing FAIL: expression line ${expr_ms}ms > ${EXPR_MAX_MS}ms" >&2
    status=1
fi

if [ "$cmd_ms" -gt "$CMD_MAX_MS" ]; then
    echo "timing FAIL: command-mode line ${cmd_ms}ms > ${CMD_MAX_MS}ms" >&2
    status=1
fi

exit $status
