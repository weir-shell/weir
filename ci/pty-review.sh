#!/usr/bin/env bash
# Probe harness for the two parked pty items: C14 (TTY contention) and
# the REPL SIGINT split. Findings-shaped: probes report what they
# OBSERVED; signal probes run N times and report a RATE; every zero is
# paired with a non-zero from the same instrument.
#
#     ci/pty-review.sh /path/to/weir [N]
#
# INSTRUMENT SCARS, all caught by this harness's own controls:
#   * pty-run.py discarded an early child's real exit ("EXIT timeout"
#     for a child dead at 1ms) — fixed in the instrument.
#   * The REPL editor ECHOES the typed line with syntax colors, so any
#     needle typed at the prompt matches ITSELF. Every REPL probe here
#     types only neutral text (a helper-script path) or derives its
#     needle by case transform (type "alive", grep ALIVE).
#   * Enter is \r: terminals send CR; \n is Ctrl-J (insert-newline) and
#     leaves the editor at a continuation prompt forever.
# Scenario SEND lines carry control bytes as \xNN ESCAPES decoded by
# the instrument — no literal control bytes in this file. Signal deaths
# report as NEGATIVE exits (-2 = SIGINT, -9 = SIGKILL).
#
# PLATFORM: the tty layer and tree-kill are the BCL's per-platform
# code; this harness reports uname and claims nothing beyond it.

set -u
BIN="${1:?usage: pty-review.sh /path/to/weir [N]}"
N="${2:-10}"
PTY="$(cd "$(dirname "$0")/.." && pwd)/tests/pty/pty-run.py"
work=$(mktemp -d)
trap 'reap; rm -rf "$work"' EXIT

ledger() { ps -eo pid,comm,args 2>/dev/null | awk -v m="$1" '$2=="sleep" && index($0,m)>0' | wc -l | tr -d ' '; }
reap() {
    ps -eo pid,comm,args 2>/dev/null | awk '$2=="sleep" && /31\.41/ {print $1}' | xargs -r kill 2>/dev/null
    ps -eo pid,comm | awk '$2=="gzip" {print $1}' | xargs -r kill -9 2>/dev/null
    return 0
}
say() { printf '%s\n' "$*"; }
pty() { python3 "$PTY" "$@"; }
strip() { sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g'; }

say "== pty review: $(uname -sr), N=$N =="

# ---- helper tools (needles live HERE, never in typed lines) -----------------
printf 'ps -o pid=,pgid=,tpgid= -p $$,$PPID\n' > "$work/pgroups.sh"
printf 'stty -a | tr ";" "\\n" | grep -Ew "isig|icanon" | tr -d " "\n' > "$work/flags.sh"
printf 'sleep 2; stty -a | tr ";" "\\n" | grep -Ew "isig|icanon" | tr -d " "\n' > "$work/flags2.sh"
printf 'read x; printf %%s "$x" | od -An -tx1 | tr -s " "\n' > "$work/datacheck.sh"
cat > "$work/pick.sh" <<'PICK'
#!/bin/sh
printf 'choose> ' > /dev/tty
read choice < /dev/tty
[ "$choice" = q ] && exit 130
echo "PICKED-$choice"
PICK

# ---- instrument controls ----------------------------------------------------
out=$(printf 'SLEEP 300\nSEND \\x03\n' | pty 5 cat)
say "control: cat under the pty + ^C -> EXIT $(echo "$out" | awk '/^EXIT/{print $2}') (the instrument can see a SIGINT death: want -2)"
out=$(printf 'SLEEP 300\nSEND \\x03\n' | pty 5 gzip)
say "control: bare gzip refuses a tty stdout -> EXIT $(echo "$out" | awk '/^EXIT/{print $2}') (the early-exit reap the instrument used to discard)"

# ---- Phase 0 ----------------------------------------------------------------
say ""
say "-- Phase 0: the mechanism --"

out=$(printf "SLEEP 700\nSEND sh $work/pgroups.sh\\\\r\nSLEEP 900\nSEND #quit\\\\r\n" | pty 8 "$BIN" | strip)
rows=$(echo "$out" | grep -oE '[0-9]+ +[0-9]+ +[0-9]+' | head -2)
say "0.1 pgroups (pid pgid tpgid; first row = weir, second = the sh child):"
echo "$rows" | sed 's/^/      /'
pg1=$(echo "$rows" | awk 'NR==1{print $2}'); pg2=$(echo "$rows" | awk 'NR==2{print $2}'); tp=$(echo "$rows" | awk 'NR==1{print $3}')
if [ -n "$pg1" ] && [ "$pg1" = "$pg2" ] && [ "$tp" = "$pg1" ]; then
    say "    one process group, and it IS the tty foreground group — delivery is not the obstacle"
else
    say "    groups differ or probe empty: pg1=$pg1 pg2=$pg2 tpgid=$tp"
fi

isig_of() { case "$1" in *-isig*) echo OFF ;; *isig*) echo ON ;; *) echo unprobed ;; esac; }
repl_now=$(printf "SLEEP 700\nSEND sh $work/flags.sh\\\\r\nSLEEP 700\nSEND #quit\\\\r\n" | pty 8 "$BIN" | strip | grep isig | head -1)
repl_late=$(printf "SLEEP 700\nSEND sh $work/flags2.sh\\\\r\nSLEEP 3200\nSEND #quit\\\\r\n" | pty 10 "$BIN" | strip | grep isig | head -1)
printf 'sh %s/flags.sh\n' "$work" > "$work/flags.weir"
script_now=$(printf 'SLEEP 200\n' | pty 8 "$BIN" "$work/flags.weir" | strip | grep isig | head -1)
say "0.2 isig during a foreground child: REPL at ~0ms: $(isig_of "$repl_now"), REPL at 2s: $(isig_of "$repl_late"), script: $(isig_of "$script_now") — the REPL editor's TreatControlCAsInput footprint (icanon/echo stay on; ISIG alone is cleared, for the whole eval)"

out=$(printf 'SLEEP 700\nSEND \\x03\nSLEEP 300\nSEND print (Str.toUpper "alive")\\r\nSLEEP 500\nSEND #quit\\r\n' | pty 8 "$BIN" | strip)
say "0.2b ^C at the prompt: editor ^C echo seen: $(echo "$out" | grep -q '\^C' && echo yes || echo no); session continues (ALIVE printed): $(echo "$out" | grep -q ALIVE && echo yes || echo no)"

out=$(printf "SLEEP 700\nSEND sh $work/datacheck.sh\\\\r\nSLEEP 500\nSEND \\\\x03\\\\x04hi\\\\r\nSLEEP 500\nSEND print (Str.toUpper \"usable\")\\\\r\nSLEEP 500\nSEND #quit\\\\r\n" | pty 10 "$BIN" | strip)
hexline=$(echo "$out" | grep -oE '03 68 69' | head -1)
say "0.3 a child read under the REPL: line bytes=[$hexline] (03 68 69 = ^C AS DATA + hi; the ^D acted as icanon's partial-line delimiter, not a signal); prompt usable after: $(echo "$out" | grep -q USABLE && echo yes || echo no)"

printf "SLEEP 700\nSEND sleep 31.4172\\\\r\nSLEEP 600\n" | pty 2 "$BIN" >/dev/null
sleep 0.3
say "0.4 bare REPL foreground child after weir is SIGKILLed: $(ledger 31.4172) orphan(s)"
( setsid sleep 31.4179 >/dev/null 2>&1 & ) ; sleep 0.2
say "0.4 control: the ledger sees a deliberately-orphaned survivor: $(ledger 31.4179)"
reap

# ---- Part A: C14, TTY contention -------------------------------------------
say ""
say "-- Part A: interactive tools drawing on /dev/tty --"

# A1 single-arm, script path: the tool prompts on /dev/tty while its
# stdout is a weir pipe; the chosen value must reach the pipeline
cat > "$work/a1.weir" <<'WEIR'
let r = $(sh pick.sh | complete)
print (r.stdout |> Seq.head)
WEIR
out=$( (cd "$work" && printf 'SLEEP 500\nSEND apple\\r\n' | pty 8 "$BIN" a1.weir) | strip)
say "A1 script: prompt drawn on the tty: $(echo "$out" | grep -q 'choose>' && echo yes || echo no); value reached the pipeline: $(echo "$out" | grep -q 'PICKED-apple' && echo yes || echo no); EXIT $(echo "$out" | awk '/^EXIT/{print $2}')"

out=$( (cd "$work" && printf 'SLEEP 700\nSEND let r = $(sh pick.sh | complete)\\r\nSLEEP 500\nSEND pear\\r\nSLEEP 400\nSEND print (r.stdout |> Seq.head)\\r\nSLEEP 400\nSEND #quit\\r\n' | pty 10 "$BIN") | strip)
say "A1 REPL:   prompt drawn on the tty: $(echo "$out" | grep -q 'choose>' && echo yes || echo no); value reached the pipeline: $(echo "$out" | grep -q 'PICKED-pear' && echo yes || echo no)"

# A2a a spontaneous cancel (fzf's Esc): the tool exits 130 with NO
# signal in flight — the doc's raise claim, isolated from delivery
cat > "$work/a2.weir" <<'WEIR'
sh pick.sh
print (Str.toUpper "after")
WEIR
out=$( (cd "$work" && printf 'SLEEP 500\nSEND q\\r\n' | pty 8 "$BIN" a2.weir) | strip)
say "A2a script, tool exits 130 spontaneously: raise names 130: $(echo "$out" | grep -q '130' && echo yes || echo no); statements after the fault ran: $(echo "$out" | grep -q 'AFTER' && echo yes || echo NO); weir EXIT $(echo "$out" | awk '/^EXIT/{print $2}')"

# A2b a REAL ^C mid-pipeline, script path (isig is ON there): SIGINT
# goes to the whole foreground group — weir AND the tool. N runs.
ok=0; aborted=0
for i in $(seq "$N"); do
    out=$( (cd "$work" && printf 'SLEEP 500\nSEND \\x03\n' | pty 6 "$BIN" a2.weir) | strip)
    echo "$out" | grep -q 'AFTER' || aborted=$((aborted+1))
    code=$(echo "$out" | awk '/^EXIT/{print $2}')
    if [ "$code" = "-2" ]; then ok=$((ok+1)); fi
done
say "A2b script, real ^C mid-pipeline: aborted-at-the-fault $aborted/$N; weir died of the group SIGINT (-2) $ok/$N"

# A3 two concurrent pmap arms both opening /dev/tty — UNSTATED in the
# docs; the outcome decides doc sentence vs finding. N runs, classified.
cat > "$work/a3.weir" <<'WEIR'
[1; 2] |> Seq.pmap (fun i -> $(sh pick.sh | complete)) |> Seq.iter (fun r -> print (r.stdout |> Seq.head))
WEIR
both=0; onegotboth=0; hung=0; other=0
for i in $(seq "$N"); do
    out=$( (cd "$work" && printf 'SLEEP 700\nSEND one\\r\nSLEEP 300\nSEND two\\r\n' | pty 8 "$BIN" a3.weir) | strip)
    o=$(echo "$out" | grep -c 'PICKED-one'); t=$(echo "$out" | grep -c 'PICKED-two')
    x=$(echo "$out" | awk '/^EXIT/{print $2}')
    if [ "$x" = "timeout" ]; then hung=$((hung+1))
    elif [ "$o" -ge 1 ] && [ "$t" -ge 1 ]; then both=$((both+1))
    elif echo "$out" | grep -q 'PICKED-onetwo\|PICKED-twoone'; then onegotboth=$((onegotboth+1))
    else other=$((other+1)); echo "$out" | grep -E 'PICKED|error' | head -3 | sed 's/^/    A3 other: /'; fi
done
say "A3 two arms on one tty over $N runs: both-arms-answered $both, one-arm-got-both $onegotboth, hung $hung, other $other (other = a shape printed above)"

if command -v fzf >/dev/null 2>&1; then
    say "A1-fzf: fzf present — probe it manually with the same scenario shape"
else
    say "A1-fzf SKIP: fzf not installed on this runner — the fzf half of the claim stays OPEN (absence is never a pass)"
fi

# ---- Part B: the REPL SIGINT split -----------------------------------------
say ""
say "-- Part B: the REPL split --"

# B1 the original repro: gzip reads the raw-ish pty; three ^C
out=$(printf 'SLEEP 700\nSEND gzip\\r\nSLEEP 400\nSEND \\x03\\x03\\x03\nSLEEP 600\n' | pty 3 "$BIN" | strip)
say "B1 gzip + three ^C at the REPL: weir EXIT $(echo "$out" | awk '/^EXIT/{print $2}') (timeout = still hung, the incident); tty echoed the bytes as data: $(echo "$out" | grep -q '\^C\^C\^C' && echo yes || echo no)"

# B1b the undocumented escape: icanon is ON, so ^D at an empty line is
# EOF — gzip exits and the session returns
out=$(printf 'SLEEP 700\nSEND gzip\\r\nSLEEP 300\nSEND \\x03\\x03\\x03\nSLEEP 200\nSEND \\x04\\x04\nSLEEP 500\nSEND print (Str.toUpper "back")\\r\nSLEEP 400\nSEND #quit\\r\n' | pty 8 "$BIN" | strip)
say "B1b ^D^D ends the child (EOF), session usable after: $(echo "$out" | grep -q 'BACK' && echo yes || echo no)"

# B2 script path, ^C with a child that IGNORES SIGINT: does weir's own
# signal death leave the child running? N runs + a control child that
# does not ignore.
printf 'trap "" INT HUP\necho UP\nexec sleep 31.4173\n' > "$work/ignore.sh"
printf 'echo UP\nexec sleep 31.4174\n' > "$work/noignore.sh"
cat > "$work/b2.weir" <<'WEIR'
sh ignore.sh
WEIR
cat > "$work/b2c.weir" <<'WEIR'
sh noignore.sh
WEIR
orphans=0
for i in $(seq "$N"); do
    (cd "$work" && printf 'SLEEP 600\nSEND \\x03\n' | pty 4 "$BIN" b2.weir) >/dev/null
    sleep 0.2
    [ "$(ledger 31.4173)" != "0" ] && orphans=$((orphans+1))
    reap; sleep 0.1
done
(cd "$work" && printf 'SLEEP 600\nSEND \\x03\n' | pty 4 "$BIN" b2c.weir) >/dev/null
sleep 0.2
say "B2 script ^C, child ignoring INT+HUP: survived weir's death $orphans/$N runs; control (default dispositions): $(ledger 31.4174) survivor (0 = the group SIGINT/pty HUP reaped it)"
reap

# B3 a DIRECT SIGINT to the weir process at the REPL prompt (bypassing
# the tty): the editor never sees it — the PosixSignal path answers
b3died=0; b3lived=0; b3miss=0
for i in $(seq "$N"); do
    (printf 'SLEEP 3000\n' | pty 4 "$BIN" | strip > "$work/b3.out") &
    b3job=$!
    sleep 1.2
    wpid=$(ps -eo pid,comm --sort=-pid 2>/dev/null | awk '$2=="weir"{print $1; exit}')
    if [ -n "$wpid" ]; then kill -INT "$wpid" 2>/dev/null; else b3miss=$((b3miss+1)); fi
    wait "$b3job" 2>/dev/null
    case "$(awk '/^EXIT/{print $2}' "$work/b3.out")" in
        -2) b3died=$((b3died+1)) ;;
        *) b3lived=$((b3lived+1)) ;;
    esac
done
say "B3 kill -INT at the REPL prompt, $N shots: died(-2) $b3died, survived $b3lived, probe-missed-pid $b3miss (the tty ^C key clears a line; a DELIVERED SIGINT kills the session — two fates; NB an inherited SIG_IGN is honoured, the nohup courtesy, which is why the instrument resets dispositions)" 

# B4 ^C mid-stream: the relay keeps flushing; the byte waits in the pty
# queue and hits the EDITOR at the next prompt
printf 'printf partial-; sleep 1; echo done-marker\n' > "$work/stream.sh"
(cd "$work" && printf "SLEEP 700\nSEND sh stream.sh\\\\r\nSLEEP 400\nSEND \\\\x03\nSLEEP 1300\nSEND print (Str.toUpper \"usable\")\\\\r\nSLEEP 400\nSEND #quit\\\\r\n" | pty 10 "$BIN" > b4.raw)
out=$(strip < "$work/b4.raw")
pat=$(grep -oE "^[0-9]+ b'partial-" "$work/b4.raw" | head -1 | awk '{print $1}')
say "B4 ^C mid-stream: partial flushed at ${pat:-?}ms (the 100ms relay, unaffected), stream completed: $(echo "$out" | grep -q 'done-marker' && echo yes || echo no), deferred ^C hit the editor at the NEXT prompt: $(echo "$out" | grep -q '\^C' && echo yes || echo no), prompt usable: $(echo "$out" | grep -q 'USABLE' && echo yes || echo no)"

say ""
say "== done: $(uname -s) only — macOS untested on this runner =="
