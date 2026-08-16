#!/usr/bin/env bash
# Flake harness for dev/plans/PLAN-concurrency-review.md.
#
#     ci/concurrency-flake.sh /path/to/weir [N]
#
# Concurrency findings do not reproduce on demand: a green run proves the bug
# did not happen THIS TIME. So every probe runs N times and reports a RATE,
# never a verdict, and timing is FORCED rather than awaited — the arm that
# should win is made slow and the loser fast, so a claimed ordering is tested
# against the schedule that would break it.
#
# TWO-LAYER BY NECESSITY: weir cannot referee its own orphans, because a
# process that outlives the script is by definition invisible to the script.
# The orphan ledger therefore runs HERE, after weir exits. This is the same
# shape as the yaml interop referee and for the same reason. (Logged as a
# scripting-policy fallback in dev/NOTES-agent.md.)
#
# INSTRUMENT SAFETY — read before editing. Three separate self-match failures
# were hit while writing this, all of the genus dev/PROCESS.md documents
# ("count processes by name, never by a pattern the measuring command also
# carries"):
#   * `pgrep -fc "sleep 3133"` counted its own shell — baseline 1, not 0.
#   * `ps -eo args= -C sleep` silently IGNORED -C on this box, listed every
#     process, and matched the harness's own argv for two distinct markers.
#   * `pkill -f "sleep 2.31341"` KILLED THE HARNESS SHELL (exit 143), which
#     surfaced as two silent no-output runs and one phantom orphan count that
#     was very nearly reported as a weir defect.
# Hence: ledger() and reap() both filter on comm ($2=="sleep") via awk, so the
# measuring shell is excluded STRUCTURALLY rather than by luck. Do not
# reintroduce `pgrep -f` or `pkill -f` here.

set -u
BIN="${1:?usage: concurrency-flake.sh /path/to/weir [N]}"
N="${2:-200}"
work=$(mktemp -d)
trap 'reap 3134 >/dev/null 2>&1; rm -rf "$work"' EXIT

# ---- the resource ledger (runs AFTER weir exits) ----------------------------

ledger() { # marker -> count of live `sleep` processes carrying it
    ps -eo pid,comm,args 2>/dev/null | awk -v m="$1" '$2=="sleep" && index($0,m)>0' | wc -l
}

reap() { # marker -> kill them, BY NAME
    ps -eo pid,comm,args 2>/dev/null \
        | awk -v m="$1" '$2=="sleep" && index($0,m)>0 {print $1}' \
        | xargs -r kill 2>/dev/null
    return 0
}

# ---- probes -----------------------------------------------------------------

cat > "$work/c1_order.weir" <<'WEOF'
// C1: pmap returns INPUT order. Forced skew: item 1 sleeps LONGEST, so time
// order is the exact reverse of input order.
let out =
    [1; 2; 3; 4; 5]
    |> Seq.pmap (fun n ->
        Duration.sleep (Duration.ms ((6 - n) * 40))
        n)

print (out |> Seq.map show |> Str.join ",")
WEOF

cat > "$work/c2_firsterr.weir" <<'WEOF'
// C2/C6: the first error BY INPUT ORDER wins. Forced skew: arm 1 fails LAST
// in time (250ms), arm 5 fails FIRST. Only tests the claim because time order
// and input order DISAGREE.
[1; 2; 3; 4; 5]
|> Seq.piter (fun n ->
    if n == 1 then
        Duration.sleep 250ms
        fail "arm-1"
    elif n == 5 then
        fail "arm-5"
    else
        print ())
WEOF

cat > "$work/c5_orphan.weir" <<'WEOF'
// C5: a pfirst LOSER's spawned process TREE is killed — including a
// GRANDCHILD, which is the part a direct-child kill would miss.
[1; 2]
|> Seq.pfirst (fun n ->
    if n == 1 then
        !(sh -c "sleep 31337 & sleep 31337")
        0
    else
        Duration.sleep 60ms
        1)
|> show
|> print
WEOF

cat > "$work/ctl_leak.weir" <<'WEOF'
// POSITIVE CONTROL for the ledger: deliberately background a child inside sh
// so it outlives weir. The ledger MUST see this, or its zeros mean nothing.
!(sh -c "sleep 31339 >/dev/null 2>&1 &")
print "leaked"
WEOF

# ---- runners ----------------------------------------------------------------

rate() { # name script N needle [invert]
    local name="$1" script="$2" n="$3" want="$4" inv="${5:-}" pass=0 i=0
    while [ "$i" -lt "$n" ]; do
        out=$("$BIN" "$work/$script" 2>&1)
        echo "$out" | grep -qF -- "$want" && pass=$((pass + 1))
        i=$((i + 1))
    done
    if [ "$inv" = "invert" ]; then
        printf "  %-30s %d/%d  CONTROL: must be 0\n" "$name" "$pass" "$n"
        [ "$pass" -eq 0 ] || { echo "CONTROL FAILED: $name asserted something that cannot fail" >&2; exit 1; }
    else
        printf "  %-30s %d/%d\n" "$name" "$pass" "$n"
    fi
}

echo "weir:    $BIN ($("$BIN" --version))"
echo "machine: $(nproc --all) cores, load $(cut -d' ' -f1 /proc/loadavg), $(uname -sr)"
echo "N:       $N"
echo
echo "ordering under forced skew:"
rate "C1 pmap input order" c1_order.weir "$N" "1,2,3,4,5"
rate "C1 inverted control" c1_order.weir 20 "5,4,3,2,1" invert
rate "C2/C6 first error by input" c2_firsterr.weir "$N" "arm-1"
rate "C2 inverted control" c2_firsterr.weir 20 "arm-5" invert

echo
echo "orphans (ledger runs after weir exits):"
reap 31337 >/dev/null 2>&1
leaked=0
i=0
orphan_n=$((N / 6 + 1))
while [ "$i" -lt "$orphan_n" ]; do
    "$BIN" "$work/c5_orphan.weir" >/dev/null 2>&1
    [ "$(ledger 31337)" -gt 0 ] && { leaked=$((leaked + 1)); reap 31337; }
    i=$((i + 1))
done
printf "  %-30s %d/%d runs leaked\n" "C5 pfirst loser tree-kill" "$leaked" "$orphan_n"

"$BIN" "$work/ctl_leak.weir" >/dev/null 2>&1
sleep 0.4
seen=$(ledger 31339)
printf "  %-30s %s  CONTROL: must be >0\n" "ledger sees a real leak" "$seen"
reap 31339
[ "$seen" -gt 0 ] || { echo "CONTROL FAILED: the ledger is blind — its zeros are meaningless" >&2; exit 1; }

echo
echo "all probes reported with rates; controls held"
