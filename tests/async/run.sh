#!/usr/bin/env bash
# One async-flow fixture's transcript, normalized [D:async-flows]: arms
# race, so stdout and stderr are each sorted; temp names and elapsed
# times are masked. ci/e2e.sh compares it to <name>.expected.
#   tests/async/run.sh <weir binary> <name>
set -u
bin=$1
name=$2
cd "$(dirname "$0")"
errf=$(mktemp)
out=$("$bin" "$name.weir" 2>"$errf")
code=$?
mask() {
    sed -E 's/weir-tmp-[0-9a-f]+/weir-tmp-X/g; s/(over|after) [0-9.]+m?s/\1 Nms/g' | LC_ALL=C sort
}
echo "exit=$code"
echo "--stdout--"
printf '%s\n' "$out" | mask
echo "--stderr--"
mask <"$errf"
rm -f "$errf"
