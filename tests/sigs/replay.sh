#!/bin/sh
# A sample tool for tools/sig-score.weir: <sample>/exe/<tool> links here and
# answers an argv from <sample>/answers.txt — sections headed `=== <argv>`
# (the words joined by single spaces), an optional `@exit N` line, then the
# output lines. An argv with no section exits 97 and is appended to
# $SIGFX_MISSES, so a probe the sample lacks — a phantom word included —
# fails the gate instead of passing as a refusal. Shell builtins only: a
# sample may be named like a tool on PATH, so the replayer calls none.
f=${0%/*}/../answers.txt
want="=== $*"
state=0
code=0
while IFS= read -r line || [ -n "$line" ]; do
    case $state in
        0) [ "$line" = "$want" ] && state=1 ;;
        1) case $line in
               "@exit "*) code=${line#@exit }; state=2 ;;
               "=== "*) exit 0 ;;
               *) printf '%s\n' "$line"; state=2 ;;
           esac ;;
        2) case $line in
               "=== "*) exit "$code" ;;
               *) printf '%s\n' "$line" ;;
           esac ;;
    esac
done < "$f"
if [ $state = 0 ]; then
    [ -n "$SIGFX_MISSES" ] && printf '%s\n' "$*" >> "$SIGFX_MISSES"
    echo "sig sample: no recorded answer for: $*" >&2
    exit 97
fi
exit "$code"
