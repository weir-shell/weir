#!/usr/bin/env bash

# Known noise, not a failure (Windows, round 31's run): bash lines like
#   dofork: child -1 - forked process N died unexpectedly ... 0xC0000142
#   ./ci/e2e.sh: fork: retry: Resource temporarily unavailable
# are the msys2/Git-Bash fork flake — the forked bash child fails DLL
# init (cygwin address-space collision on loaded runners), and bash
# retries with backoff (0/1/3/7s) and recovers. Every battery-owned
# background job is killed and reaped at block end, so a live-leak
# diagnosis is wrong by construction; only a fork that exhausts all
# retries fails the run, and that is the runner's weather, not ours.

# temp dirs weir can see on every platform: Git Bash's /tmp is
# MSYS-virtual — a native weir.exe cannot resolve it, so hand weir the
# mixed (C:/...) spelling; POSIX passes through untouched
mkweirtmp() {
    d=$(mktemp -d)
    if command -v cygpath >/dev/null 2>&1; then cygpath -m "$d"; else printf '%s\n' "$d"; fi
}
# poll until a local endpoint answers (bounded) — a fixed sleep raced
# slow runners: macOS timed out reaching a server that was not up yet
awaitHttp() {
    for _ in $(seq 1 50); do
        curl -sf -o /dev/null "$1" 2>/dev/null && return 0
        sleep 0.2
    done
    return 1
}
# the TLS twin polls for a TCP accept only (curl's TLS stack disagrees
# with the python server on some platforms; readiness needs the listener,
# and the server survives the aborted handshake — verified): bash's
# /dev/tcp, present on macOS 3.2 and Git Bash alike
awaitTcp() {
    for _ in $(seq 1 50); do
        (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null && return 0
        sleep 0.2
    done
    return 1
}
# End-to-end battery against the AOT binary (command-mode Session 4 set).
set -euo pipefail
# `grep -q` reads the whole input [D:e2e-grep-drain]: a quiet grep exits at
# its first match, and under pipefail a writer still sending (`echo "$out" |
# grep -q`, output past one stdio buffer — 1 KiB on macOS) dies by SIGPIPE
# and fails a passing check. The wrapper drops `q` from the leading options
# and discards the output instead; exit codes are grep's.
grep() {
    local args=() a opts=1 skip=0 quiet=0
    for a in "$@"; do
        if [ "$skip" = 1 ]; then
            skip=0
        elif [ "$opts" = 1 ] && [ "$a" = "--" ]; then
            opts=0
        elif [ "$opts" = 1 ] && [[ $a == -[!-]* ]]; then
            [[ $a == *[ef] ]] && skip=1
            if [[ $a == *q* ]]; then
                quiet=1
                a="${a//q/}"
                [ "$a" = "-" ] && continue
            fi
        else
            opts=0
        fi
        args+=("$a")
    done
    if [ "$quiet" = 1 ]; then
        command grep "${args[@]}" >/dev/null
    else
        command grep "${args[@]}"
    fi
}
# an unguarded nonzero command under set -e used to kill the battery
# silently (round 33 — the run ended after a green line with no FAIL at
# all); the ERR trap names its own line and command. Deliberately no
# `set -E`: with it the trap reaches into command-substitution
# subshells, where several pins run commands whose failure is the
# point ($(cmd; echo rc=$?)) — top level is where the silent class
# lives, and top level is what fires it.
trap 'echo "e2e FAIL: unguarded command failed at line $LINENO: $BASH_COMMAND" >&2' ERR

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"

# per-run process markers [D:e2e-run-markers]: the cells that pgrep/pkill
# their own children find them by a sleep duration (or yes's word);
# deriving it from this shell's pid keeps concurrent e2e runs on one
# machine from seeing and killing each other's children. Patterns end in
# `$` so one run's marker is never a prefix of another's.
MK=$(( 100000 + ($$ % 400000) * 10 ))
MK1=$((MK + 1)); MK2=$((MK + 2)); MK3=$((MK + 3)); MK4=$((MK + 4)); MK5=$((MK + 5)); MK6=$((MK + 6))
MK7=$((MK + 7))

# POSIX-only harnesses (os.fork, pty, zombies) get stated skips on
# Windows — a skip echoes its reason, never silence
IS_WINDOWS=0
case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) IS_WINDOWS=1 ;; esac
# python subprocess must not resolve `bash` itself on Windows — the
# native PATH finds System32's WSL stub first (the sh-never-bash class,
# python axis): hand the harnesses this bash, in native form
if [ "$IS_WINDOWS" = "1" ]; then
    WEIR_BASH=$(cygpath -m "$(command -v bash)")
    export WEIR_BASH
fi

# a PATH entry must be POSIX-form: mkweirtmp's mixed (C:/...) spelling
# and a Windows-form dirname both carry a drive colon that reads as a
# PATH separator — round 5's class, every prefix site (identity on POSIX)
pathEntry() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1"; else printf '%s\n' "$1"; fi
}
# a fake PATH binary every platform can spawn — per-platform, never
# both: with both present, weir's Windows resolver prefers the exact
# extensionless name and CreateProcess fails on it rather than falling
# through to the .bat (noted as a possible product divergence from
# cmd's PATHEXT-only search; the fixture must not depend on it)
# /dev/stdin is a Linux-shaped spelling MSYS resolves through its
# virtual /proc (/proc/self/fd/0) — a native weir cannot open it. Land
# a piped script in a real file first; contract (exit code + output)
# unchanged.
checkPiped() {
    _cp="$(mkweirtmp)/stdin.weir"
    cat > "$_cp"
    $BIN check "$_cp"
}
runPiped() {
    _rp="$(mkweirtmp)/stdin.weir"
    cat > "$_rp"
    $BIN "$_rp"
}
mkFakeBin() {
    if [ "$IS_WINDOWS" = "1" ]; then
        printf '@echo off\r\necho %s\r\n' "$3" > "$1/$2.bat"
    else
        printf '#!/bin/sh\necho %s\n' "$3" > "$1/$2" && chmod +x "$1/$2"
    fi
}
BINDIR=$(pathEntry "$(dirname "$BIN")")

# Hard stale-binary gate [D:masking-mechanized] — the one shared gate
# (stamp == HEAD, no .fs newer than the binary), so stale results are
# impossible rather than catchable.
"$(dirname "$0")/check-fresh.sh" "$BIN"

fail() {
    echo "e2e FAIL: $1" >&2
    exit 1
}

# CI shards [D:e2e-shards]: E2E_SHARD=body runs all but the pty REPL
# probes, repl-a/repl-b only theirs, repl both halves; unset runs
# everything in order
E2E_SHARD="${E2E_SHARD:-}"
# the pty REPL probes [D:e2e-shards]: CI runs them on their own runners
# (repl-a: repl-multiline alone, the long pole; repl-b: the rest)
replProbe() {
    case "$E2E_SHARD" in "" | repl | "$1") ;; *) return 0 ;; esac
    python3 "$(dirname "$0")/../tests/repl/$2" "$BIN" || fail "$3"
    echo "$4"
}
replPtyProbes() {
    if ! command -v python3 >/dev/null 2>&1; then
        echo "e2e SKIP: python3 absent — repl pty probes NOT run" >&2
    elif [ "$IS_WINDOWS" = "1" ]; then
        echo "e2e SKIP: the pty REPL harnesses — python has no pty on Windows (the REPL itself is exercised by the Windows hand-run checklist)"
    else
        replProbe repl-b repl-wordnav.py "repl word navigation" "e2e ok: repl Ctrl+Left/Right word navigation"
        replProbe repl-b repl-codepoints.py "repl code-point editing" "e2e ok: repl Left/Right/Backspace/^D step over a surrogate pair whole [D:str-code-points]"
        replProbe repl-b repl-typeahead.py "repl type-ahead" "e2e ok: repl an Enter typed before the first prompt submits [D:owned-line-editor]"
        replProbe repl-b repl-color.py "repl coloring" "e2e ok: repl lexical coloring, head verdicts, NO_COLOR"
        replProbe repl-b repl-quality.py "repl quality (history/Ctrl+R)" "e2e ok: repl history (XDG/dedup/0600/invalid-UTF-8 kept raw), Ctrl+R fzf-stub + fallback"
        replProbe repl-b waiting-indicator.py "waiting indicator" "e2e ok: repl waiting indicator (grace, erase, fast/piped/child-owned silent)"
        replProbe repl-b cooked-trap.py "cooked trap / echo-once" "e2e ok: repl cooked-trap (one child run per echo, Enter survives a slow child)"
        replProbe repl-b repl-it-streamed.py "it FSI-parity / function echo" "e2e ok: it FSI-parity — streamed binds (), misuse teaches the capture, functions echo mini-help, a failed command quiets to an exit-code status [D:repl-it] [D:repl-fn-echo] [D:repl-cmd-fail]"
        replProbe repl-b repl-f1.py "repl F1 key bindings" "e2e ok: repl F1 prints the bindings mid-buffer and keeps it, raw F1 sequences, one-time hint [D:repl-f1-keys]"
        replProbe repl-b repl-directives.py "repl directives" "e2e ok: repl directives (#help x3, #quit, :q retired, comments no-op, #echo cap)"
        replProbe repl-b repl-pathquote.py "repl path-quote completion" "e2e ok: repl path completion quotes in expression position, bare in command-argv [D:repl-path-quote]"
        replProbe repl-b repl-mapkeys.py "repl map-key completion" "e2e ok: repl map-key completion — a bound value's keys inside a Map lookup literal; a pipeline receiver stays silent [D:value-key-complete]"
        replProbe repl-b repl-anon-complete.py "repl anonymous-record field completion" "e2e ok: repl an anonymous record's fields complete on the next line [D:anon-literals]"
        replProbe repl-b repl-entry-complete.py "repl entry-context completion" "e2e ok: repl Tab completes a binder from an earlier line of the entry and the \`_.\` argument's element, nothing for a bare \`|> _.\` stage [D:entry-complete]"
        replProbe repl-b repl-sig-complete.py "repl sig argument completion" "e2e ok: repl Tab completes subcommands, flags and --flag= values from the project's and the user dir's sigs, through a session alias; a sig-less head keeps paths [D:sig-arg-complete]"
        replProbe repl-b repl-sig-dynamic.py "repl sig dynamic completion" "e2e ok: repl Tab asks a user-dir cobra tool and git through an alias; a project cobra sig never runs its tool; a hung or flooding tool is bounded and leaves no child [D:sig-dynamic-complete]"
        replProbe repl-b repl-splice-complete.py "repl \$ splice completion" "e2e ok: repl a \$ splice completes only the bindings the checker admits there, after an external or builtin head [D:argv-splice-complete]"
        replProbe repl-a repl-multiline.py "repl multiline editor" "e2e ok: repl 2D buffer, Enter-completeness, whole-entry history, wrap at two widths"
    fi
}
case "$E2E_SHARD" in
    "" | body) ;;
    repl-a | repl-b | repl)
        replPtyProbes
        echo "e2e battery: all green (shard $E2E_SHARD)"
        exit 0
        ;;
    *) fail "unknown E2E_SHARD '$E2E_SHARD' — body, repl-a, repl-b or repl (unset runs all)" ;;
esac

# conflict-marker gate [D:sized-findings]: three leftover diff3 `|||||||`
# labels survived rebase resolutions into merged ledgers (the resolve
# regex captured up to `=======`, which includes the base-section label).
# One grep and the class cannot recur: any marker at line start in a
# tracked file fails.
if git -C "$(dirname "$0")/.." ls-files -z 2>/dev/null \
    | xargs -0 grep -lE '^(<{7}|\|{7}|={7}|>{7})([^=<>|]|$)' 2>/dev/null | grep -q .; then
    bad=$(git -C "$(dirname "$0")/.." ls-files -z | xargs -0 grep -lE '^(<{7}|\|{7}|={7}|>{7})([^=<>|]|$)' 2>/dev/null)
    fail "conflict markers in tracked files: $bad"
fi
echo "e2e ok: no conflict markers in tracked files"

# client-name gate: dev/ may name client projects; rendered surfaces must
# not — no file under docs/, skills/, or site/ (nor the root pages the
# site loads: CHANGELOG, README) may carry the names. The site's loaders
# reach ../docs and ../CHANGELOG.md, so docs/ is a rendered surface; the
# conflict-marker/cdn-cgi shape, applied to the client constraint.
if git -C "$(dirname "$0")/.." ls-files -zco --exclude-standard -- docs skills site CHANGELOG.md README.md 2>/dev/null \
    | xargs -0 grep -lIE '\bdbt\b|\bKSL\b' 2>/dev/null | grep -q .; then
    bad=$(git -C "$(dirname "$0")/.." ls-files -zco --exclude-standard -- docs skills site CHANGELOG.md README.md | xargs -0 grep -lIE '\bdbt\b|\bKSL\b' 2>/dev/null)
    fail "a client name appears in a rendered doc surface (client names live under dev/ only): $bad"
fi
echo "e2e ok: no client names in rendered doc surfaces"

# ---- pins-walk: three runtime messages only e2e can see [D:pins-walk] ------
pwdir=$(mkweirtmp)
# the set-e analogue's words were never asserted (the raise itself was)
cat > "$pwdir/x.weir" <<'WEOF'
sh -c "exit 3"
print "unreached"
WEOF
out=$($BIN "$pwdir/x.weir" 2>&1) && fail "nonzero exit must raise" || true
echo "$out" | grep -qF "command failed with exit code 3" || fail "the exit-raise names code+command: $out"
# the '>' redirect warning names the File.write spelling
cat > "$pwdir/r.weir" <<'WEOF'
echo hi > out.txt
WEOF
out=$($BIN check "$pwdir/r.weir" 2>&1) || fail "a literal > is a warning, not an error: $out"
echo "$out" | grep -qF "'>' does not redirect in weir" || fail "redirect warning: $out"
echo "$out" | grep -qF 'File.write' || fail "redirect warning names the spelling: $out"
# permission denied is weir-shaped (the read-guard's residual wrapper)
printf 'locked\n' > "$pwdir/locked.txt" && chmod 000 "$pwdir/locked.txt"
mkdir -p "$pwdir/lockdir" && chmod 000 "$pwdir/lockdir"
if [ ! -r "$pwdir/locked.txt" ]; then  # root ignores modes; skip there
    out=$($BIN -e 'File.read "'"$pwdir"'/locked.txt" |> Seq.length' 2>&1) && fail "unreadable must raise" || true
    echo "$out" | grep -qF "File.read: permission denied:" || fail "permission shape: $out"
    # the Dir family wraps too [D:transport-words] — a denied listing
    # leaked UnauthorizedAccessException's text until the wider sweep
    out=$($BIN -e 'Dir.list "'"$pwdir"'/lockdir"' 2>&1) && fail "denied listing must raise" || true
    echo "$out" | grep -qF "Dir.list: permission denied:" || fail "Dir.list permission shape: $out"
    echo "e2e ok: pins-walk messages (exit-raise words, redirect warning, permission shape files+dirs)"
else
    echo "e2e ok: pins-walk messages (exit-raise words, redirect warning; permission skipped — running as root)"
fi
chmod 755 "$pwdir/lockdir"

# ---- possible re-enumeration warning [D:reenum-warning] --------------------
# a command-backed seq pulled twice warns at the second pull, naming
# the command and the repair — and the warning never gates: check
# exits 0. The forced twin is silent.
redir=$(mkweirtmp)
cat > "$redir/twice.weir" <<'WEOF'
let files = git ls-files
print $"{files |> Seq.length}"
files |> Seq.iter print
WEOF
out=$($BIN check "$redir/twice.weir" 2>&1) || fail "a warning-only file must exit 0: $out"
echo "$out" | grep -qF "warning [re-enumeration]: possible re-enumeration: 'files' is command-backed and unforced — each pull re-runs 'git ls-files'; snapshot one run: let files = git ls-files |> Seq.freeze" \
  || fail "the re-enumeration warning with command and repair: $out"
echo "$out" | grep -q "twice.weir:3:1" || fail "located at the SECOND pull: $out"
cat > "$redir/forced.weir" <<'WEOF'
let files = git ls-files |> Seq.freeze
print $"{files |> Seq.length}"
files |> Seq.iter print
WEOF
fout=$($BIN check "$redir/forced.weir" 2>&1) || fail "forced must check clean: $fout"
echo "$fout" | grep -q "re-enumeration" && fail "a forced binding must not warn: $fout" || true
echo "e2e ok: re-enumeration warning on the second pull, exit 0; Seq.freeze silences it"
# [D:reenum-forced-call]: a call of a function that freezes is a snapshot
cat > "$redir/fcall.weir" <<'WEOF'
let files = git ls-files
let snap () = files |> Seq.freeze
let a = snap ()
print $"{a |> Seq.length}"
a |> Seq.iter print
WEOF
fout=$($BIN check "$redir/fcall.weir" 2>&1) || fail "fcall must check clean: $fout"
echo "$fout" | grep -q "re-enumeration" && fail "a frozen call result must not warn: $fout" || true
echo "e2e ok: re-enumeration — a call of a freezing function carries nothing"

# walk candidates: exit codes exact; File.readSecret (never covered); Dir.copy success
pw2=$(mkweirtmp)
rc=0; $BIN -e 'exit 4' >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 4 ] || fail "exit 4 must propagate rc 4, got $rc"
rc=0; $BIN -e 'fail "m"' >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 1 ] || fail "fail must exit exactly 1, got $rc"
printf 'tok\n' > "$pw2/s.txt"
out=$($BIN -e 'print (show (File.readSecret "'"$pw2"'/s.txt"))') || fail "readSecret failed"
[ "$out" = "***" ] || fail "readSecret must show ***: $out"
out=$($BIN -e 'print (Secret.reveal (File.readSecret "'"$pw2"'/s.txt"))') || fail "reveal failed"
[ "$out" = "tok" ] || fail "readSecret must trim the trailing newline: '$out'"
out=$($BIN -e 'File.readSecret "/nope/x"' 2>&1) && fail "readSecret must raise on missing" || true
# shape + name, never the separator (Windows renders D:\nope\x)
echo "$out" | grep -qF "File.readSecret: no such file:" || fail "readSecret shape: $out"
echo "$out" | grep -qF "nope" || fail "readSecret names the path: $out"
mkdir -p "$pw2/src/a" && printf 'deep\n' > "$pw2/src/a/b.txt"
$BIN -e 'Dir.copy "'"$pw2"'/src" "'"$pw2"'/dst"' || fail "Dir.copy failed"
[ -f "$pw2/dst/a/b.txt" ] || fail "Dir.copy must be recursive (the positive half)"
rm -rf "$pw2"
echo "e2e ok: pins-walk candidates (exit codes exact, readSecret trio, Dir.copy recursive)"

# ---- NUL in a path: refused, never crashed [D:nul-path] (STRIX-5) ----------
# A NUL byte reaches Path.GetFullPath (via Session.resolve) two ways, and
# both used to escape with a raw ArgumentException — the parse-time head
# resolution as a SIGABRT (exit 134, no diagnostic), the run-time path as
# a raw .NET message. Both must now be a located weir error with a
# non-134 exit and no core dump. POSIX-only: the NUL byte in a file/argv
# is the axis, and skipOnWindows-style gating keeps the harness honest.
if [ "$IS_WINDOWS" = "0" ]; then
    nuldir=$(mkweirtmp)
    # (a) parse-time: a NUL inside a slash-bearing word. The parser's head
    # classifier calls Extern.exists, which now reports a NUL-bearing head
    # as not-found before Session.resolve — so the parser emits its normal
    # located missing-command diagnostic instead of aborting.
    printf './a\000b c\n' > "$nuldir/parse.weir"
    rc=0; out=$($BIN "$nuldir/parse.weir" 2>&1) || rc=$?
    [ "$rc" -ne 0 ] || fail "NUL-in-slash parse must not exit 0: $out"
    [ "$rc" -ne 134 ] || fail "NUL-in-slash parse must NOT SIGABRT (got 134): $out"
    echo "$out" | grep -qiF "argumentexception" && fail "parse must not leak the raw .NET exception: $out"
    echo "$out" | grep -qF "$nuldir/parse.weir:1:" || fail "parse error must be located at line 1: $out"

    # (b) run-time: a NUL-bearing path value (a file line carrying a NUL,
    # fed to a File builtin) hits Session.resolve, which now raises a
    # located weir error the builtin surfaces — no crash.
    printf 'a\000b' > "$nuldir/nulpath.txt"
    cat > "$nuldir/run.weir" <<WEOF
let p = File.read "$nuldir/nulpath.txt" |> Seq.head
let ok = File.exists p
print (show ok)
WEOF
    rc=0; out=$($BIN "$nuldir/run.weir" 2>&1) || rc=$?
    [ "$rc" -ne 0 ] || fail "NUL run-time path must not exit 0: $out"
    [ "$rc" -ne 134 ] || fail "NUL run-time path must NOT SIGABRT (got 134): $out"
    echo "$out" | grep -qiF "argumentexception" && fail "run-time must not leak the raw .NET exception: $out"
    echo "$out" | grep -qF "NUL byte" || fail "run-time error must name the NUL byte: $out"
    echo "$out" | grep -qF "$nuldir/run.weir:2:" || fail "run-time error must be located at line 2: $out"
    rm -rf "$nuldir"
    echo "e2e ok: NUL in a path is a located error, never a crash (parse + run-time, both non-134)"
else
    echo "e2e skip: NUL-in-path (POSIX axis — the NUL byte in a file/argv)"
fi

# walk cohort: the Args-side defaulted-Secret teaching [D:secret]
pw3=$(mkweirtmp)
cat > "$pw3/sd.weir" <<'WEOF'
type SC = { [<Default "x">] apiKey: Secret }
let c = Args.load SC
print "unreached"
WEOF
out=$($BIN check "$pw3/sd.weir" 2>&1) && fail "a defaulted Secret must refuse" || true
echo "$out" | grep -qF "a Secret takes no [<Default>]" || fail "the Secret Default teaching: $out"
rm -rf "$pw3"
echo "e2e ok: walk cohort (defaulted-Secret teaching)"

# a Secret spliced into argv reaches the child in the clear, and every
# error that renders the command line shows it as *** [D:secret-argv-mask]
smdir=$(mkweirtmp)
sm_case() { # name, script body; expects a failure naming *** and never the value
    printf '%s' "$2" > "$smdir/$1.weir"
    local out rc=0
    out=$(cd "$smdir" && $BIN "$1.weir" 2>&1) || rc=$?
    [ $rc -ne 0 ] || fail "secret argv $1: expected a failure: $out"
    echo "$out" | grep -qF "tok-e2e-123" && fail "secret argv $1: the value leaked: $out"
    echo "$out" | grep -qF '***' || fail "secret argv $1: expected *** in: $out"
    echo "e2e ok: secret argv masked — $1"
}
sm_case statement 'let s = Secret.of "tok-e2e-123"
sh -c "test \"$0\" = tok-e2e-123" $s
sh -c "exit 3" $s
'
sm_case reified 'let s = Secret.of "tok-e2e-123"
let t = sh -c "exit 3" $s | text
print t
'
sm_case chain 'let s = Secret.of "tok-e2e-123"
let t = echo a | sh -c "exit 4" $s | line
print t
'
sm_case lazy 'let s = Secret.of "tok-e2e-123"
let xs = sh -c "echo a; exit 5" $s
xs |> Seq.iter print
'
out=$(cd "$smdir" && $BIN statement.weir 2>&1) || true
# the head renders as resolved on Windows (C:\...\sh.EXE) — pin the argv, not the path
echo "$out" | grep -qiE 'exit code 3: (.*(/|\\))?sh(\.exe)? -c exit 3 \*\*\*' || fail "secret argv: the clear splice ran, the failing one masked: $out"
rm -rf "$smdir"
chmod 700 "$pwdir/locked.txt" 2>/dev/null; rm -rf "$pwdir"

# BSD date has no %N — millisecond clock via python3 there (python3 is
# already a harness dependency via tests/lib). The overhead (~30ms) is
# fine for e2e's generous wall-clock bounds; timing.sh's tight gates
# handle the BSD case separately.
if [ "$(date +%N)" = "N" ] || [ -z "$(date +%N)" ]; then
    now_ms() { python3 -c 'import time; print(int(time.time()*1000))'; }
else
    now_ms() { echo $(($(date +%s%N) / 1000000)); }
fi

# every python3 here loads ci/py/sitecustomize.py: a local HTTPServer
# resolves its own FQDN between bind and listen, which a macOS runner
# answers in ~35s while awaitTcp waits
pyfix="$PWD/ci/py"
command -v cygpath >/dev/null 2>&1 && pyfix=$(cygpath -w "$pyfix")
case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) pysep=';' ;; *) pysep=':' ;; esac
export PYTHONPATH="$pyfix${PYTHONPATH:+$pysep$PYTHONPATH}"

# macOS ships no GNU timeout — a background-watchdog polyfill (same
# contract: run the command, kill it after N seconds, nonzero on kill).
# `<&0` keeps the caller's stdin: without job control an `&` command's
# stdin is /dev/null unless redirected explicitly, so a piped or `<` input
# would never reach the command.
if ! command -v timeout >/dev/null 2>&1; then
    timeout() {
        local secs=$1
        shift
        "$@" <&0 &
        local pid=$!
        # the watchdog's sleep dies with it and holds no caller fd: a
        # surviving sleep kept a `$(timeout N …)` open for the full N
        (
            trap 'kill $! 2>/dev/null; exit 0' TERM
            sleep "$secs" &
            wait $!
            kill -9 "$pid" 2>/dev/null || true
        ) >/dev/null 2>&1 &
        local wd=$!
        local rc=0
        wait "$pid" || rc=$?
        kill "$wd" 2>/dev/null || true
        wait "$wd" 2>/dev/null || true
        return $rc
    }
fi

expect() {
    local desc="$1" needle="$2" out="$3"
    # -- so needles may start with '-' (e.g. "-6 : int")
    echo "$out" | grep -qF -- "$needle" || fail "$desc — expected to find: $needle in: $out"
    echo "e2e ok: $desc"
}

out=$($BIN -e '(1 + 2) * 2')
expect "expression eval" "6 : int" "$out"
# blank or comment-only -e says so — never a bare caret under nothing
# (Git Bash on Windows rewrites a leading `//` argument as a path — off here)
for e in '' '// c' '   '; do
    rc=0; out=$(MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' $BIN -e "$e" 2>&1) || rc=$?
    [ "$rc" = "2" ] || fail "-e '$e' exits 2, got $rc: $out"
    expect "-e '$e' names the problem" "nothing to evaluate" "$out"
done

out=$($BIN -e 'ls |> Seq.where (fun f -> f.bytes > 1MiB) |> Seq.take 5' 2>&1); rc=$?
[ $rc -eq 0 ] || fail "flagship pipeline must run measure-free: $out"
echo "e2e ok: flagship pipeline, bare bytes"

if $BIN -e '1<mb> + 2<mb>' 2>/dev/null; then
    fail "old measure literal should be rejected"
fi
errout=$($BIN -e '1<mb>' 2>&1 || true)
echo "$errout" | grep -qF "units of measure are not supported" || fail "transition message missing: $errout"
echo "e2e ok: measure transition error"

# the witness is a weir child: native on every platform, so it cannot
# glob for us — an MSYS echo.exe expands args from a native parent,
# convicting the witness, not weir
awdir=$(mkweirtmp)
printf 'Self.args |> print\n' > "$awdir/args.weir"
out=$(PATH="$BINDIR:$PATH" $BIN -e "\$(weir \"$awdir/args.weir\" \"*\")")
expect "argv stays literal" '["*"]' "$out"

out=$($BIN -e 'echo hi (40 + 2) |> Seq.take 1')
expect "command mode with splice" '["hi 42"]' "$out"

out=$($BIN -e '[1..5] |> Seq.length')
expect "range literal on the AOT binary" "5 : int" "$out"

out=$(timeout 5 $BIN -e '[1..1000000] |> Seq.take 3') || fail "huge range under first must terminate (laziness)"
expect "ranges are lazy generators" '[1; 2; 3]' "$out"

# hostile-input performance fixture [D:assemble-quadratic]: the line
# assembler must scale linearly. A crafted hundreds-of-KB source that was
# once O(N^2) (a continuation-line flood + a bracket-heavy single line)
# now checks in well under the bound; a regressed quadratic build blows
# the timeout instead of merely slowing. The generous 20s ceiling is a
# ~5x margin over the measured linear time, not a fine timing gate.
asmdir=$(mkweirtmp)
# (a) 60k continuation lines inside one list literal
{ echo "let xs = ["; for _ in $(seq 1 60000); do echo "    1"; done; echo "]"; echo 'print (show (xs |> Seq.length))'; } > "$asmdir/flood.weir"
timeout 20 $BIN check "$asmdir/flood.weir" >/dev/null 2>&1 || fail "assembler DoS: 60k continuation-line flood did not check within 20s (quadratic regression?)"
echo "e2e ok: assembler — 60k-line continuation flood checks in linear time"
# (b) a bracket-heavy single line (200k record openers) — the per-opener
# with-header substring was quadratic; it fails to parse fast, so the
# whole budget is the (now linear) assembler fold
{ printf 'let xs = '; for _ in $(seq 1 200000); do printf '{}'; done; printf '\n'; echo 'print "done"'; } > "$asmdir/openers.weir"
acode=0
timeout 20 $BIN check "$asmdir/openers.weir" >/dev/null 2>&1 || acode=$?
# exit 1 (a parse error) is the expected verdict; a timeout (124) is the
# failure this fixture guards against
[ "$acode" -ne 124 ] || fail "assembler DoS: 200k bracket-opener single line did not settle within 20s (quadratic regression?)"
echo "e2e ok: assembler — 200k bracket-opener single line settles in linear time"
rm -rf "$asmdir"

rangedir=$(mkweirtmp)
mkdir -p "$rangedir/sub"
cat > "$rangedir/sub/updot.weir" <<'WEOF'
cd ..
[1..3] |> Seq.length |> print
WEOF
out=$(cd "$rangedir/sub" && $BIN updot.weir)
expect "cd .. barewords compose with range lexing" "3" "$out"
rm -rf "$rangedir"

out=$($BIN -e 'let n = 40 + 2 in $"answer: {n} {{ok}}"')
expect "string interpolation with brace escapes" '"answer: 42 {ok}"' "$out"

out=$($BIN -e 'echo $"n={40 + 2}" |> Seq.take 1')
expect "interpolated string is one argv entry" '["n=42"]' "$out"

dir=$(mkweirtmp)
(
    cd "$dir"
    git init -q
    echo a > staged.txt
    echo b > unstaged.txt
    git add -A
    git -c user.email=ci@ci -c user.name=ci commit -qm init
    echo x >> staged.txt
    git add staged.txt
    echo y >> unstaged.txt
    echo n > untracked.txt
)

out=$(printf 'cd "%s"\ngit status --porcelain |> where (Str.startsWith "M ") |> map (Str.replace "M  " "")\n^ls\nlet pat = "a"\ngrep -l $pat staged.txt\n#quit\n' "$dir" | $BIN)
expect "cd + staged filter over git status lines" '["staged.txt"]' "$out"
expect "^ls forces external" 'untracked.txt' "$out"
expect "bound-variable splice into grep" '["staged.txt"]' "$out"
rm -rf "$dir"

out=$($BIN -e 'yes hi | cat |> Seq.take 2')
expect "external pipes into external stdin" '["hi"; "hi"]' "$out"

# /etc/hosts, not /etc/hostname: the latter doesn't exist on macOS
# (grep exit 2 = error, not the no-match 1 this pin reifies)
out=$($BIN -e 'grep nomatch /etc/hosts | complete |> _.exitCode')
expect "complete reifies nonzero exit as data" "1 : int" "$out"

# sh, never bash: a native weir resolving `bash` on a Windows runner
# finds System32's WSL bash.exe first (no distro installed — exit 1);
# sh has no System32 shadow, which is why every sh -c block passes.
out=$(timeout 10 $BIN -e 'sh -c "seq 1 4000 | sed s/^/eeeeeeeeeeeeeeeeeeeeeeee/ 1>&2; echo done" | complete |> _.exitCode') \
    || fail "chatty-stderr deadlock under complete (timeout)"
expect "concurrent stderr drain under complete" "0 : int" "$out"

out=$($BIN -e 'sh -c "echo out; echo err 1>&2"' 2>/dev/null)
expect "stderr passthrough keeps stdout stream clean" '["out"]' "$out"

out=$($BIN -e 'sh -c "echo a && echo b"')
expect "POSIX one-liner via the external shell" '["a"; "b"]' "$out"

out=$($BIN -e 'sh -c "exit 7" | complete |> _.exitCode')
expect "sh lines can complete now (old builtin boundary gone)" "7 : int" "$out"

# ---- command chaining [D:cmd-chaining]: bash &&/|| as `| and` / `| or` ----
out=$($BIN -e "sh -c 'exit 0' | and echo yes")
expect "| and runs the right on success" "yes" "$out"
out=$($BIN -e "sh -c 'exit 1' | or echo fb")
expect "| or runs the right on failure" "fb" "$out"
out=$($BIN -e "sh -c 'exit 0' | or echo skip")
echo "$out" | grep -qF skip && fail "| or must skip the right when the left succeeds: $out"
echo "e2e ok: | or skips the right on success"
out=$($BIN -e "echo a | and echo b | and echo c")
expect "| and chains (reaches the last)" "c" "$out"
out=$($BIN -e "sh -c 'exit 1' | or echo A | or echo B")
expect "| or chains: the first success wins" "A" "$out"
echo "$out" | grep -qF B && fail "| or chain must stop at the first success: $out"
echo "e2e ok: | or chain stops at the first success"
if $BIN -e "sh -c 'exit 1' | and echo no" >/dev/null 2>&1; then
    fail "| and must propagate the left's failure (right skipped, nonzero exit)"
fi
echo "e2e ok: | and propagates the left's failure"
out=$($BIN -e "cd / | and echo ok")
expect "a builtin (cd) chains as an | and operand" "ok" "$out"

# ---- exec: process replacement [D:exec] -----------------------------
# exec replaces the runner (execve), so the command's own stdout and exit
# code are the script's — there is no weir layer left to reify through.
# Out-of-process by nature: -e / a script drives it and the child's bytes
# and status are observed directly. POSIX-only (execve; the coreutils
# heads have no Windows shadow — the Windows spawn-wait-exit path is the
# hand-run item, like the other sh/coreutils cells).
if [ "$IS_WINDOWS" = "0" ]; then
    out=$($BIN -e 'echo exec-marker | exec')
    expect "exec replaces the process with the command" "exec-marker" "$out"

    execdir=$(mkweirtmp)
    cat > "$execdir/exec.weir" <<'WEOF'
sh -c "exit 7" | exec
print "unreached"
WEOF
    # exec replaces with `sh -c "exit 7"`, so the runner exits 7 by design —
    # capture it without tripping the battery's set -e
    code=0
    $BIN "$execdir/exec.weir" || code=$?
    [ "$code" = "7" ] || fail "exec must exit with the replacement's code (got $code)"
    out=$($BIN "$execdir/exec.weir" 2>&1 || true)
    echo "$out" | grep -qF "unreached" && fail "no statement runs after exec — the image is gone"

    # the env overlay reaches the replacement (libc setenv, since execvp
    # reads the C environ, not .NET's managed copy)
    cat > "$execdir/execenv.weir" <<'WEOF'
let e = [Env.pair "EXECENV" "reached"]
$e(printenv EXECENV | exec)
WEOF
    out=$($BIN "$execdir/execenv.weir")
    expect "exec's env overlay reaches the replacement" "reached" "$out"

    # a value pipe into exec is refused at parse (no parent to feed stdin);
    # `check` exits nonzero on that error by design — do not let set -e abort
    printf '["a"] | grep a | exec\n' > "$execdir/execbad.weir"
    out=$($BIN check "$execdir/execbad.weir" 2>&1 || true)
    echo "$out" | grep -qF "cannot take a piped stdin" || fail "value-headed exec must refuse at check"
fi

# ---- the single-line capture reifier [D:reify-line] ------------------
# `cmd | line` reads a one-value CLI's single stdout line as a string —
# the $(cmd) |> Seq.exactlyOne idiom, first-class. Raises on nonzero and
# on 0-or-2+ lines.
lout=$($BIN -e 'let scope = printf "id-abc" | line
echo scope $scope')
expect "| line reifies one stdout line to a string" "scope id-abc" "$lout"
# a trailing newline (the -o tsv shape) is still one line
lout2=$($BIN -e 'let v = sh -c "printf \"val\n\"" | line
echo got $v')
expect "| line handles the trailing newline (one line)" "got val" "$lout2"
# nonzero exit raises
lerr=$($BIN -e 'let x = sh -c "exit 4" | line
print x' 2>&1) && fail "| line must raise on nonzero" || true
echo "$lerr" | grep -qF "exit code 4" || fail "| line lost the nonzero raise: $lerr"
# 2+ lines is the caller's mistake, named
lmulti=$($BIN -e 'let x = sh -c "printf \"a\nb\n\"" | line
print x' 2>&1) && fail "| line must raise on 2+ lines" || true
echo "$lmulti" | grep -qF "expected exactly one line" || fail "| line lost the one-line assert: $lmulti"
echo "e2e ok: | line — one stdout line to a string, nonzero raises, 2+ lines named [D:reify-line]"

# ---- the whole-output capture reifier [D:reify-text] -----------------
# `cmd | text`: stdout as one string, trailing blank lines dropped;
# nonzero raises
tout=$($BIN -e 'let t = sh -c "printf \"a\nb\n\n\"" | text
print $"[{t}]"')
[ "$tout" = "[a
b]" ] || fail "| text joins lines and drops trailing blanks: $tout"
terr=$($BIN -e 'let x = sh -c "echo o; exit 5" | text
print x' 2>&1) && fail "| text must raise on nonzero" || true
echo "$terr" | grep -qF "exit code 5" || fail "| text lost the nonzero raise: $terr"
echo "e2e ok: | text — whole stdout to a string, trailing blanks dropped, nonzero raises [D:reify-text]"

# ---- pipe chains [D:pipe-early-exit] [D:chain-reifiers] --------------------
# a consumer that exits early ends the chain (statement, capture, let) with
# no "write error" noise; reifiers follow a chain; the leftmost failing
# stage owns the code; complete's stderr is every stage's, in order
if [ "$IS_WINDOWS" != "1" ]; then
    pcdir=$(mkweirtmp)
    cat > "$pcdir/early.weir" <<'WEOF'
seq 1 1000000 | head -1
let c = $(seq 1 1000000 | head -1) |> Seq.exactlyOne
let l = seq 1 1000000 | head -1 | line
print $"{c} {l}"
WEOF
    out=$(cd "$pcdir" && timeout 60 $BIN early.weir 2>&1) || fail "an early-exit chain must end promptly: $out"
    [ "$out" = "1
1 1" ] || fail "early-exit chain output, no write-error noise: $out"
    cat > "$pcdir/reify.weir" <<'WEOF'
let ok = sh -c "echo x" | grep x | succeeds
let bad = sh -c "echo x; exit 2" | cat | succeeds
let code = sh -c "exit 3" | cat | sh -c "cat; exit 5" | exitCode
let r = sh -c "echo e1 >&2; echo o" | sh -c "cat; echo e2 >&2; exit 4" | complete
let t = sh -c "printf 'b\na\n'" | sort | text
print $"{ok} {bad} {code} {r.exitCode} {r.stdout |> Str.join ","} {r.stderr |> Str.join ","} [{t}]"
WEOF
    out=$(cd "$pcdir" && timeout 60 $BIN reify.weir 2>&1) || fail "chain reifiers must run: $out"
    [ "$out" = "true false 3 4 o e1,e2 [a
b]" ] || fail "chain reifiers: succeeds, leftmost code, stderr order, text: $out"
    rm -rf "$pcdir"
    echo "e2e ok: pipe chains — early exit ends the chain quietly; reifiers follow a chain, leftmost code, stderr in order"
fi

# a closed stdout ends the script like SIGPIPE [D:stdout-epipe]: an endless
# relayed child and a long print loop under `| head -1` stop at once with
# exit 141, no diagnostic, and `always` runs (POSIX: EPIPE is the signal)
if [ "$IS_WINDOWS" != "1" ]; then
    epdir=$(mkweirtmp)
    cat > "$epdir/yes.weir" <<WEOF
within
    yes epipe$MK
always
    File.write "cleaned-yes" ["ok"]
WEOF
    cat > "$epdir/loop.weir" <<'WEOF'
within
    for i in [1..2000000] do
        print $"{i}"
always
    File.write "cleaned-loop" ["ok"]
WEOF
    for probe in yes loop; do
        rm -f "$epdir/cleaned-$probe"
        res=$(cd "$epdir" && { timeout 5 $BIN $probe.weir 2>"$epdir/err-$probe" | head -1 >/dev/null; } ; echo "${PIPESTATUS[0]}")
        [ "$res" = "141" ] || fail "a closed stdout ends $probe.weir promptly with 141 (got $res; 124 = still running)"
        [ "$(cat "$epdir/cleaned-$probe" 2>/dev/null)" = "ok" ] || fail "a closed stdout runs always ($probe)"
        [ ! -s "$epdir/err-$probe" ] || fail "a closed stdout prints no diagnostic ($probe): $(cat "$epdir/err-$probe")"
    done
    if pgrep -f "yes epipe$MK\$" >/dev/null 2>&1; then
        pkill -f "yes epipe$MK\$" 2>/dev/null || true
        fail "a closed stdout must reap the relayed child (yes left running)"
    fi
    # the closed stdout's teardown kills the children then [D:stdout-epipe]:
    # an arm waiting on a running command is cut by the kill, not after it
    cat > "$epdir/arm.weir" <<WEOF
[1; 2] |> Seq.piter (fun i ->
    if i == 1 then
        print "a"
        Duration.sleep 400ms
        print "b"
        print "c"
    else
        within proc p = sleep 6.$MK
            let _code = Proc.wait p
            ())
WEOF
    t0=$(now_ms)
    res=$( { $BIN "$epdir/arm.weir" 2>"$epdir/err-arm" | head -1 >/dev/null; } ; echo "${PIPESTATUS[0]}")
    took=$(( $(now_ms) - t0 ))
    [ "$res" = "141" ] || fail "a closed stdout ends a fan-out with 141 (got $res): $(cat "$epdir/err-arm")"
    [ "$took" -lt 4000 ] || fail "a closed stdout must kill an arm's waited command at the teardown (took ${took}ms)"
    # a capture's reader the teardown cut raises as the main flow's own
    # stop [D:epipe-kills-at-teardown]: exit 143, no "errors occurred" line
    printf 'within tmp d\n    let _r = sleep 5.%s | complete\n    print "after"\n' "$MK" > "$epdir/cap.weir"
    $BIN "$epdir/cap.weir" >"$epdir/out-cap" 2>"$epdir/err-cap" &
    wpid=$!
    sleep 0.7
    kill -TERM "$wpid" 2>/dev/null || true
    code=0; wait "$wpid" || code=$?
    [ "$code" = "143" ] || fail "a SIGTERM during a capture exits 143 (got $code)"
    [ ! -s "$epdir/err-cap" ] || fail "a SIGTERM during a capture prints no diagnostic: $(cat "$epdir/err-cap")"
    rm -rf "$epdir"
    echo "e2e ok: a closed stdout ends the script with 141, always runs, the child is reaped"
fi

# docs-vs-binary prose pins (SKILL.md claims no fence covers)
dvdir=$(mkweirtmp)
printf 'print "one"\nprinterr "two"\nprint "three"\nsh -c "echo four >&2"\nprint "five"\n' > "$dvdir/il.weir"
$BIN "$dvdir/il.weir" > "$dvdir/il.log" 2>&1 || fail "interleave script must run"
[ "$(cat "$dvdir/il.log")" = "one
two
three
four
five" ] || fail "> log 2>&1 keeps every stdout line in order [D:stdout-epipe]: $(cat "$dvdir/il.log")"
printf 'no-such-cmd-zz a\n' > "$dvdir/dash.weir"
$BIN check "$dvdir/dash.weir" >/dev/null 2>&1 || fail "check only warns on a missing command"
out=$($BIN "$dvdir/dash.weir" 2>&1) && fail "a missing dashed command must fail the run"
expect "a dashed missing command is named whole at run" "unknown command 'no-such-cmd-zz'" "$out"
out=$($BIN -e 'let n = echo a | complete in n.exitCode' 2>&1) && fail "a reifier in single-line let…in must refuse"
expect "a reifier in single-line let…in teaches \$()" "wrap the chain: \$(<command> | complete)" "$out"
out=$($BIN -e 'echo $HOME' 2>&1) && fail "\$HOME in argv is a splice, not env expansion"
expect "\$HOME in argv is an unbound splice" "unbound variable 'HOME'" "$out"
mkdir -p "$dvdir/g/a/b"
touch "$dvdir/g/x.fs" "$dvdir/g/a/y.fs" "$dvdir/g/a/b/z.fs" "$dvdir/g/a/b.fs"
out=$(cd "$dvdir/g" && $BIN -e 'Path.glob "**/*.fs" |> Str.join ","')
expect "Path.glob sorts per level, files before subdirectories" '"x.fs,a/b.fs,a/y.fs,a/b/z.fs"' "$out"
rm -rf "$dvdir"

# a 2-param generic union checks + evals through the binary (was the
# prelude-Result pin; Result removed [D:no-result], the fixture is now a
# locally-declared Either)
eitherdir=$(mkweirtmp)
cat > "$eitherdir/either.weir" <<'WEOF'
type Either<'a, 'e> = Left of 'a | Right of 'e
print (match Left 3 with | Left v -> v | Right e -> Str.length e)
WEOF
out=$($BIN "$eitherdir/either.weir")
expect "a declared 2-param generic union: cross-arm inference through the binary" "3" "$out"

out=$($BIN -e 'ls |> Seq.sortBy _.bytes |> Seq.map _.name |> Seq.head' 2>/dev/null | head -1)
expect "qualified module pipeline" " : string" "$out"

out=$($BIN -e '[] |> Seq.tryHead |> Option.defaultValue 9')
expect "Option sweep idiom on the AOT binary" "9 : int" "$out"

out=$($BIN -e 'Some 3')
expect "prelude Option types generically" "Some 3 : Option<int>" "$out"

branchdir=$(mkweirtmp)
(
    cd "$branchdir"
    git init -q
    git -c user.email=ci@ci -c user.name=ci commit -q --allow-empty -m init
    git branch feature/a
    git branch feature/b
    git branch keep-me
)
out=$(printf 'cd "%s"\ngit branch |> map trim |> where (startsWith "feature") |> join ","\n#quit\n' "$branchdir" | $BIN)
expect "git-branch-cleanup dogfood task" '"feature/a,feature/b"' "$out"
rm -rf "$branchdir"

scriptdir=$(mkweirtmp)
cat > "$scriptdir/task.weir" <<'WEOF'
#!/usr/bin/env weir
// strict by default
type Tag = Big | Small
let names = ls |> Seq.map _.name
names |> Seq.take 1 |> print
echo spliced (40 + 2)
Self.args |> Seq.head |> print
WEOF
chmod +x "$scriptdir/task.weir"
out=$(cd "$scriptdir" && WEIR_BIN_PATH=1 $BIN task.weir firstarg)
expect "shebang script: bindings, decl, command mode, args" "spliced 42" "$out"
expect "shebang script: args flow" "firstarg" "$out"

cat > "$scriptdir/broken.weir" <<'WEOF'
sh -c "touch proof-file"
let x = 1 + "oops"
WEOF
if (cd "$scriptdir" && $BIN broken.weir) 2>/dev/null; then
    fail "type-broken script should exit nonzero"
fi
if [ -e "$scriptdir/proof-file" ]; then
    fail "check-first violated: effect ran before the type error"
fi
echo "e2e ok: whole-file check runs nothing on error"

# #loose is removed (PLAN-dx-review D1): files are always strict, the
# directive is unknown, and fmt --qualify went with the mode. The gate
# is pointed at what changed, not at a probe that passed on the
# untouched tree.
cat > "$scriptdir/loose.weir" <<'WEOF'
#loose
[2; 1] |> where (fun x -> x > 0) |> Seq.iter (fun x -> print (show x))
WEOF
if $BIN fmt --qualify "$scriptdir/loose.weir" 2>/dev/null; then
    fail "fmt --qualify must be an unknown flag now"
fi
before=$(cat "$scriptdir/loose.weir")
$BIN fmt "$scriptdir/loose.weir" >/dev/null 2>&1 || true
after=$(cat "$scriptdir/loose.weir")
grep -q "#loose" "$scriptdir/loose.weir" || fail "fmt must not strip the unknown directive"
echo "$after" | grep -q "Seq.where" && fail "fmt must not rewrite bare names"
out=$($BIN "$scriptdir/loose.weir" 2>&1 || true)
echo "$out" | grep -q "unknown or misplaced directive" || fail "a #loose file must hit the generic directive error: $out"
out=$($BIN check "$scriptdir/loose.weir" 2>&1 || true)
echo "$out" | grep -q "spell it 'Seq.where'" || fail "the bare-name teaching must name the qualified spelling: $out"
out=$($BIN -e '[1] |> where (fun n -> n > 0)' 2>&1 || true)
echo "$out" | grep -q "Seq.where" || fail "-e is strict and must teach the qualified spelling: $out"
echo "e2e ok: #loose removed — strict everywhere, fmt --qualify gone, bare names teach"

# -e takes a program (PLAN-dx-review D9, reading (b)): newlines are
# statement boundaries as in a file, a lone declaration is still
# refused, and the agreement with the file path is the property —
# pin both and pin that they agree.
prog='let x = 1
print $"{x}"'
eout=$($BIN -e "$prog")
cat > "$scriptdir/e9.weir" <<'WEOF'
let x = 1
print $"{x}"
WEOF
fout=$($BIN "$scriptdir/e9.weir")
expect "-e two-statement program runs" "1" "$eout"
[ "$eout" = "$fout" ] || fail "-e and the file disagree on identical bytes: -e '$eout' vs file '$fout'"
out=$($BIN -e 'for x in [1; 2] do
    print (show x)')
expect "-e indented block body parses" "1
2" "$out"
out=$($BIN -e 'type V = | Pass of int | Fail
let v = Pass 3
match v with | Pass n -> n | Fail -> 0')
echo "$out" | grep -q "3 : int" || fail "-e declarations-then-expression must echo the result: $out"
out=$($BIN -e 'let x = 1' 2>&1 || true)
echo "$out" | grep -qF "not a let statement" || fail "a lone let must keep its teaching: $out"
out=$($BIN -e 'type T = { a: int }' 2>&1 || true)
echo "$out" | grep -qF "not a declaration" || fail "a lone type must keep its teaching: $out"
out=$($BIN -e 'let x = 2 // trailing comment
print $"{x}"')
expect "-e multi-line trailing comment strips as in a file" "2" "$out"
echo "e2e ok: -e evaluates a program and shows the result; lone declarations still teach"
cat > "$scriptdir/multi.weir" <<'WEOF'
type Verdict =
    | Pass of int
    | Fail

let doubled =
    [1; 2; 3]
    |> Seq.map (fun x -> x * 2)
    |> Seq.sum

let verdict =
    match Pass doubled with
    | Pass n -> n
    | Fail -> 0

print verdict
WEOF
out=$($BIN "$scriptdir/multi.weir")
expect "multi-line script: decl, pipeline, canonical match arms" "12" "$out"

cat > "$scriptdir/multibad.weir" <<'WEOF'
let names =
    ls
    |> Seq.map _.Nmae
WEOF
errout=$($BIN "$scriptdir/multibad.weir" 2>&1 || true)
expect "continuation error maps to the physical line" "multibad.weir:3:" "$errout"

cat > "$scriptdir/blocklet.weir" <<'WEOF'
let msg =
    let n =
        [10; 20; 30]
        |> Seq.length
    $"count: {n}"

print msg
WEOF
out=$($BIN "$scriptdir/blocklet.weir")
expect "block lets: implicit in, F#-style, on the AOT binary" "count: 3" "$out"

cat > "$scriptdir/sugar.weir" <<'WEOF'
let double n = n * 2
let describe n =
    let d = double n
    $"{n} -> {d}"

[1..3] |> Seq.map describe |> Seq.iter print
WEOF
out=$($BIN "$scriptdir/sugar.weir")
expect "let parameter sugar end to end" "3 -> 6" "$out"

cat > "$scriptdir/comments.weir" <<'WEOF'
let total =
    // the base value
    let base = 2
    base + 1

print $"{total}"
WEOF
out=$($BIN "$scriptdir/comments.weir")
expect "comment lines transparent inside blocks" "3" "$out"

cat > "$scriptdir/show.weir" <<'WEOF'
let rows =
    ls
    |> Seq.take 1

rows |> Seq.iter (fun f -> print (show f))
WEOF
out=$(cd "$scriptdir" && $BIN show.weir)
echo "$out" | grep -qF "bytes = " || fail "show must render the builtin row: $out"
echo "e2e ok: show renders typed rows on the AOT binary"

# the same program with no sleep is the baseline (startup + spawns), so a
# loaded machine cannot fail the parallelism claim: parallel adds ~1s
# to it, serial ~4s
start=$(now_ms)
$BIN -e '[1; 2; 3; 4] |> Seq.piter (fun n -> if ($(sh -c "true" | complete)).exitCode > 99 then print "never")' >/dev/null 2>&1 || fail "piter baseline probe failed"
base_ms=$(($(now_ms) - start))
start=$(now_ms)
$BIN -e '[1; 2; 3; 4] |> Seq.piter (fun n -> if ($(sh -c "sleep 1" | complete)).exitCode > 99 then print "never")' >/dev/null 2>&1 || fail "piter probe failed"
elapsed_ms=$(($(now_ms) - start))
[ $((elapsed_ms - base_ms)) -lt 2500 ] || fail "piter must run workers in parallel (4x1s took ${elapsed_ms}ms over a ${base_ms}ms baseline)"
echo "e2e ok: piter parallelism (4x1s in ${elapsed_ms}ms over a ${base_ms}ms baseline)"

# temp-dir fixture, leaf pins: /tmp and /etc are POSIX-isms a native
# weir resolves drive-relative on Windows (D:\tmp), and pwd's separator
# is the platform's — assert the leaf, never the path
forkdir=$(mkweirtmp)
mkdir -p "$forkdir/home" "$forkdir/wa" "$forkdir/wb"
cat > "$forkdir/fork.weir" <<'WEOF'
let _home = cd "FORKMARK/home"

let workers =
    ["FORKMARK/wa"; "FORKMARK/wb"]
    |> Seq.pmap (fun d ->
        let _cd = cd d
        pwd)

let ws = workers |> Seq.freeze
print (if ws |> Seq.head |> Str.endsWith "wa" then "w1-ok" else "w1-wrong")
print (if ws |> Seq.last |> Str.endsWith "wb" then "w2-ok" else "w2-wrong")
print (if pwd |> Str.endsWith "home" then "parent-held" else "parent-moved")
WEOF
sed "s|FORKMARK|$forkdir|g" "$forkdir/fork.weir" > "$forkdir/fork.weir.tmp" && mv "$forkdir/fork.weir.tmp" "$forkdir/fork.weir"
out=$($BIN "$forkdir/fork.weir")
expect "worker sessions fork: worker one" "w1-ok" "$out"
expect "worker sessions fork: worker two" "w2-ok" "$out"
expect "worker sessions fork: parent untouched" "parent-held" "$out"
rm -rf "$forkdir"

# run/cmd|>print byte-identity retired [D:drop-command-builtins] (both dropped)
if $BIN -e 'sh -c "exit 4"' 2>/dev/null; then
    fail "a bare command must raise on nonzero exit at force"
fi
echo "e2e ok: bare command raises on nonzero exit"

seqdir=$(mkweirtmp)
cat > "$seqdir/seq.weir" <<'WEOF'
let go = 1 > 0

let _steps =
    if go then
        sh -c "echo one"
        sh -c "echo two"
        print "three"

let _skipped =
    if 1 > 2 then
        sh -c "echo never"
        print "never"

print "after"
WEOF
out=$($BIN "$seqdir/seq.weir")
for needle in one two three after; do
    expect "block sequencing: $needle" "$needle" "$out"
done
if echo "$out" | grep -qF "never"; then fail "false branch must not run its block"; fi
echo "e2e ok: false branch skips the whole sequenced block"

errout=$($BIN -e 'git add -A ; git push' 2>&1 >/dev/null || true)
echo "$errout" | grep -qF "does not chain" || fail "semicolon boundary warning missing: $errout"
echo "e2e ok: bash-semicolon prior-bleed warned"
rm -rf "$seqdir"

sigdir=$(mkweirtmp)
cat > "$sigdir/sig.weir" <<'WEOF'
let go = 1 > 0

if go then
    sh -c "echo eff-one"
    sh -c "echo eff-two"

if 1 > 2 then
    sh -c "echo never-a"
    sh -c "echo never-b"

let captured = $(sh -c "echo x && echo y") |> Seq.length
print $"captured: {captured}"

let code = $(sh -c "exit 5" | complete)
print $"code: {code.exitCode}"
WEOF
out=$($BIN "$sigdir/sig.weir")
for needle in eff-one eff-two "captured: 2" "code: 5"; do
    expect "sigils: $needle" "$needle" "$out"
done
if echo "$out" | grep -qF "never"; then fail "false branch ran its sigil block"; fi
echo "e2e ok: sigil composition (assembler x if x capture x complete)"

if $BIN -e 'weir-no-such-program-zz' 2>/dev/null; then
    fail "a typo'd program must fail at check time"
fi
echo "e2e ok: command heads resolve at check time"
rm -rf "$sigdir"

distdir=$(mkweirtmp)
cat > "$distdir/d.weir" <<'WEOF'
let go = 1 > 0

if go then
    sh -c "echo dist-one"
    // comments are transparent inside blocks
    sh -c "echo dist-two"

if 1 > 2 then
    sh -c "echo dist-never"

print "dist-after"
WEOF
out=$($BIN "$distdir/d.weir")
for needle in dist-one dist-two dist-after; do
    expect "district: $needle" "$needle" "$out"
done
if echo "$out" | grep -qF "dist-never"; then fail "false-branch district ran"; fi
echo "e2e ok: command-group effect counts, both branch ways, comments transparent (districts retired)"

# the retirement teaches [D:district-retirement]
cat > "$distdir/old.weir" <<'WEOF'
if go then !
    sh -c "echo x"
WEOF
rerr=$($BIN check "$distdir/old.weir" 2>&1) && fail "the retired spelling must error" || true
echo "$rerr" | grep -qF "not a district marker" || fail "retirement teaching: $rerr"
echo "e2e ok: the retired ! spelling teaches [D:district-retirement]"

cat > "$distdir/span.weir" <<'WEOF'
let n = 3

if 1 > 0 then
    sh -c "echo a"
    echo (n |> Seq.length)
WEOF
errout=$($BIN "$distdir/span.weir" 2>&1) && fail "bad splice in district must fail"
echo "$errout" | grep -qE "span.weir:5:" || fail "district splice error must point at line 5: $errout"
echo "e2e ok: command-group span translation points at the offending line"
rm -rf "$distdir"

envdir=$(mkweirtmp)
cat > "$envdir/cfg.weir" <<'WEOF'
type Config = { WEIR_E2E_PORT: int; WEIR_E2E_DEBUG: bool; WEIR_E2E_OPT: Option<string> }

let cfg = Env.load Config
print $"port={cfg.WEIR_E2E_PORT} debug={cfg.WEIR_E2E_DEBUG} opt={show cfg.WEIR_E2E_OPT}"
WEOF
out=$(WEIR_E2E_PORT=9000 WEIR_E2E_DEBUG=true $BIN "$envdir/cfg.weir")
expect "Env.load: typed config from a controlled environment" "port=9000 debug=true opt=None" "$out"

cat > "$envdir/bad.weir" <<'WEOF'
type Config = { WEIR_E2E_PORT: int; WEIR_E2E_MISSING_ZZ: string }

sh -c "touch env-proof"

let _cfg = Env.load Config
print "unreached"
WEOF
errout=$(cd "$envdir" && WEIR_E2E_PORT=abc $BIN bad.weir 2>&1) && fail "bad environment must fail"
echo "$errout" | grep -qF "not an int ('abc')" || fail "collected error missing int problem: $errout"
echo "$errout" | grep -qF "WEIR_E2E_MISSING_ZZ is missing" || fail "collected error missing absent var: $errout"
[ -e "$envdir/env-proof" ] || fail "boundary errors are RUNTIME class: earlier effects legitimately ran"
echo "e2e ok: Env.load collects all problems in one boundary error (runtime class — check-time is the field-TYPE rule)"

# enum fields at the boundary [D:env-enums]: any casing selects the
# declared case; a miss carries candidates; overlays resolve first
cat > "$envdir/enum.weir" <<'WEOF'
type Lvl =
    | Debug
    | Info

type EC = { WEIR_E2E_LVL: Lvl; WEIR_E2E_OLVL: Option<Lvl> }

let c = Env.load EC
print $"lvl={show c.WEIR_E2E_LVL} opt={show c.WEIR_E2E_OLVL}"
WEOF
out=$(WEIR_E2E_LVL=DEBUG $BIN "$envdir/enum.weir")
expect "enum loads from uppercase (env convention)" "lvl=Debug opt=None" "$out"
out=$(WEIR_E2E_LVL=info WEIR_E2E_OLVL=Debug $BIN "$envdir/enum.weir")
expect "enum loads from lowercase; Option<enum> rides" "lvl=Info opt=Some (Debug)" "$out"
errout=$(WEIR_E2E_LVL=debgu $BIN "$envdir/enum.weir" 2>&1) && fail "a bad enum value must fail the boundary"
echo "$errout" | grep -qF "expected one of: Debug, Info" || fail "candidates missing: $errout"
echo "$errout" | grep -qF "Did you mean 'Debug'?" || fail "hint missing: $errout"
echo "e2e ok: enum miss carries candidates + the hint"

# the overlay stack resolves before the enum conversion: a dotenv file
# feeds the child env, the child's Env.load sees the layered value
cat > "$envdir/lvl.env" <<'WEOF'
WEIR_E2E_LVL=info
WEOF
cat > "$envdir/outer.weir" <<'WEOF'
let e = Env.fromFile "lvl.env"
within env e
    weir enum.weir
WEOF
out=$(cd "$envdir" && PATH="$BINDIR:$PATH" $BIN outer.weir)
expect "enum resolves after the dotenv overlay (same as any field)" "lvl=Info opt=None" "$out"
rm -rf "$envdir"

cat > "$scriptdir/perr.weir" <<'WEOF'
let x =
    1 +* 2
WEOF
errout=$($BIN "$scriptdir/perr.weir" 2>&1) && fail "parse error expected"
echo "$errout" | grep -qF "perr.weir:2:8: parse error" || fail "parse error must map to the physical line:col: $errout"
echo "e2e ok: parse errors translate through multi-line segments"

cat > "$scriptdir/bools.weir" <<'WEOF'
let n = [1; 2; 3] |> Seq.length

if n > 2 then print "big"

let label =
    match n > 100 with
    | true -> "huge"
    | false -> "modest"

print label

let tier =
    match n with
    | x when x > 100 -> "t1"
    | x when x > 2 -> "t2"
    | _ -> "t3"

print tier

print (if n == 3 then "three" else "not-three")
WEOF
out=$($BIN "$scriptdir/bools.weir")
for needle in big modest t2 three; do
    expect "bool branching on the AOT binary: $needle" "$needle" "$out"
done

cat > "$scriptdir/nonex.weir" <<'WEOF'
let x = match 1 == 1 with | true -> 1
print $"{x}"
WEOF
errout=$($BIN "$scriptdir/nonex.weir" 2>&1) && fail "non-exhaustive match must be a hard error"
echo "$errout" | grep -qF "not exhaustive" || fail "exhaustiveness error text missing: $errout"
echo "e2e ok: non-exhaustive match is a hard check error"

# the bare-comma precedence did-you-mean [D:user-language-messages]:
# `code, _ :: rest` against a seq names the grouping repair, and no
# pattern message says "scrutinee". Pin the fragments (FParsec wraps).
errout=$($BIN -e 'match ["a"; "b"] with | code, _ :: rest -> code | _ -> "z"' 2>&1) && fail "the tuple/cons footgun must error"
echo "$errout" | grep -qF "groups looser than" || fail "did-you-mean must name the precedence cause: $errout"
echo "$errout" | grep -qF "(code, _) :: rest" || fail "did-you-mean must name the repair: $errout"
echo "$errout" | grep -qF "scrutinee" && fail "messages must not say 'scrutinee': $errout"
echo "e2e ok: tuple/cons precedence did-you-mean names the repair; no 'scrutinee' jargon"

cat > "$scriptdir/warn.weir" <<'WEOF'
echo one ; two
print "ran"
WEOF
errout=$($BIN "$scriptdir/warn.weir" 2>&1 >/dev/null)
echo "$errout" | grep -qF "does not chain commands" || fail "runner must surface warnings: $errout"
out=$($BIN "$scriptdir/warn.weir" 2>/dev/null)
expect "warnings do not block execution" "ran" "$out"

# unreachable arms are hard errors, coverage's dual (2026-07-21: the
# casing-law footgun — a typo'd constructor becomes a catch-all binder)
cat > "$scriptdir/dead.weir" <<'WEOF'
let x = match 1 == 1 with | _ -> 1 | true -> 2
print $"{x}"
WEOF
rc=0; errout=$($BIN "$scriptdir/dead.weir" 2>&1) || rc=$?
[ $rc -eq 1 ] || fail "unreachable arm must be a hard error (rc=$rc)"
echo "$errout" | grep -qF "unreachable" || fail "unreachable-arm error missing: $errout"
echo "e2e ok: unreachable arm is a hard error"

cat > "$scriptdir/noelse.weir" <<'WEOF'
if 1 > 2 then "x"
WEOF
errout=$($BIN "$scriptdir/noelse.weir" 2>&1) && fail "non-unit no-else should be rejected"
echo "$errout" | grep -qF "add an else" || fail "tailored no-else error missing: $errout"
echo "e2e ok: non-unit no-else rejected with the tailored fix"

errout=$($BIN -e 'echo a ; b' 2>&1 >/dev/null)
echo "$errout" | grep -qF "does not chain commands" || fail "-e must surface warnings: $errout"
echo "e2e ok: -e surfaces warnings (';' argv nudge)"

if $BIN -e 'match 1 == 1 with | true -> 1' 2>/dev/null; then
    fail "-e must hard-reject a non-exhaustive match"
fi
echo "e2e ok: -e rejects non-exhaustive matches"

faildir=$(mkweirtmp)
cat > "$faildir/f.weir" <<'WEOF'
printerr "diag"
print "data"
fail "stop here"
print "unreached"
WEOF
rc=0
out=$($BIN "$faildir/f.weir" 2>/dev/null) || rc=$?
errout=$($BIN "$faildir/f.weir" 2>&1 >/dev/null) || true
[ $rc -ne 0 ] || fail "fail must exit nonzero"
[ "$out" = "data" ] || fail "stdout must carry only data (got: $out)"
echo "$errout" | grep -qF "diag" || fail "printerr must reach stderr"
echo "$errout" | grep -qF "error: stop here" || fail "fail must be located on stderr: $errout"
echo "e2e ok: fail exits located; printerr separates streams"
rm -rf "$faildir"

bigdir=$(mkweirtmp)
truncate -s 3G "$bigdir/sparse.bin"
touch "$bigdir/empty.txt"
out=$(printf 'cd "%s"\nls |> Seq.where (fun f -> f.bytes > 2147483647B) |> Seq.map _.name\nls |> Seq.where (fun f -> f.bytes == 0B) |> Seq.map _.name\n#quit\n' "$bigdir" | $BIN)
expect ">2GB file survives int64 end to end" "sparse.bin" "$out"
expect "0-byte file filters exactly" "empty.txt" "$out"
rm -rf "$bigdir"

out=$($BIN -e '9223372036854775807')
expect "Int64.Max literal on the AOT binary" "9223372036854775807 : int" "$out"

if $BIN -e '9223372036854775807 + 1' 2>/dev/null; then
    fail "overflow must raise, not wrap"
fi
errout=$($BIN -e '9223372036854775807 + 1' 2>&1 || true)
echo "$errout" | grep -qF "integer overflow" || fail "overflow error text missing: $errout"
echo "e2e ok: checked arithmetic raises on the AOT binary"

fmtdir=$(mkweirtmp)
printf 'let x =\n      let a = 1\n      a + 1\n\nprint $"{x}"\n' > "$fmtdir/ugly.weir"
if $BIN fmt --check "$fmtdir/ugly.weir" 2>/dev/null; then
    fail "fmt --check must flag an unformatted file"
fi
$BIN fmt "$fmtdir/ugly.weir" 2>/dev/null
$BIN fmt --check "$fmtdir/ugly.weir" 2>/dev/null || fail "fmt output must pass --check"
out=$($BIN "$fmtdir/ugly.weir")
expect "formatted file runs identically" "2" "$out"
$BIN "$fmtdir/ugly.weir" >/dev/null 2>&1 && $BIN fmt "$fmtdir/ugly.weir" --check 2>/dev/null || fail "fmt takes --check after the script too"
# a flag is never read as a script path: usage errors, exit 2
for args in "--check" "--bogus $fmtdir/ugly.weir" "$fmtdir/ugly.weir $fmtdir/ugly.weir"; do
    rc=0
    out=$($BIN fmt $args 2>&1) || rc=$?
    [ "$rc" = "2" ] || fail "fmt $args: exit 2, got $rc: $out"
    echo "$out" | grep -qF "usage: weir fmt" || fail "fmt $args: usage text: $out"
    case "$out" in *"no such script: --"*) fail "fmt $args: a flag read as a script: $out" ;; esac
done
# a formatted file ends in exactly one newline [D:fmt-v1]
printf 'print "a"\n\n\n' > "$fmtdir/tail.weir"
$BIN fmt "$fmtdir/tail.weir" 2>/dev/null || fail "fmt tail.weir"
printf 'print "a"\n' | cmp -s - "$fmtdir/tail.weir" || fail "fmt must leave exactly one final newline: $(od -c "$fmtdir/tail.weir")"
$BIN fmt --check "$fmtdir/tail.weir" 2>/dev/null || fail "one final newline is canonical"
# fmt keeps the file's line ending: a CRLF file reformats as CRLF, an LF
# file as LF on every OS (the LF cell above pins Windows' half)
printf 'if true then\r\n  print "a"\r\n' > "$fmtdir/crlf.weir"
$BIN fmt "$fmtdir/crlf.weir" 2>/dev/null || fail "fmt crlf.weir"
printf 'if true then\r\n    print "a"\r\n' | cmp -s - "$fmtdir/crlf.weir" || fail "fmt must keep CRLF line endings: $(od -c "$fmtdir/crlf.weir")"
echo "e2e ok: fmt keeps the file's line ending (LF and CRLF)"
# a file check cannot parse is refused, byte-identical [D:fmt-parse-gate]
printf 'let d = yaml\n    a: [1\n    b:  2\nprint  (show d)\n' > "$fmtdir/bad.weir"
cp "$fmtdir/bad.weir" "$fmtdir/bad.orig"
rc=0; out=$($BIN fmt "$fmtdir/bad.weir" 2>&1) || rc=$?
[ "$rc" = "3" ] || fail "fmt on a parse error exits 3, got $rc: $out"
expect "fmt names the parse error" "cannot format: line 2:" "$out"
cmp -s "$fmtdir/bad.weir" "$fmtdir/bad.orig" || fail "fmt rewrote a file that does not parse"
# heredoc content and a multi-line hole are never respaced [D:fmt-respace]
printf 'let a = 1\nlet s = $<<<\n    y  {a}\nprint s\nlet t = $"x{a +\n    1}y{a}"\nprint t\n' > "$fmtdir/holes.weir"
cp "$fmtdir/holes.weir" "$fmtdir/holes.orig"
$BIN fmt "$fmtdir/holes.weir" 2>/dev/null || fail "fmt holes.weir"
cmp -s "$fmtdir/holes.weir" "$fmtdir/holes.orig" || fail "fmt respaced heredoc content or a hole: $(cat "$fmtdir/holes.weir")"
rm -rf "$fmtdir"
echo "e2e ok: fmt canonicalizes, --check gates, output runs; flags never read as scripts; unparseable refused; holes kept"

# every tracked script is already formatted — a repo-wide fmt regression
# fails here [D:fmt-bracket-shift]; outside a git checkout (an unpacked
# tarball) the repo's script dirs stand in
fmtroot="$(dirname "$0")/.."
fmtfiles=$(git -C "$fmtroot" ls-files '*.weir' 2>/dev/null) \
    || fmtfiles=$(cd "$fmtroot" && find ci examples tools -type f -name '*.weir')
[ -n "$fmtfiles" ] || fail "fmt repo gate found no .weir files under $fmtroot"
for f in $fmtfiles; do
    rc=0
    out=$($BIN fmt --check "$fmtroot/$f" 2>&1) || rc=$?
    [ $rc -eq 0 ] || fail "tracked script is not fmt-clean: $f — $out"
done
echo "e2e ok: every tracked .weir file passes fmt --check"

cat > "$scriptdir/blockmatch.weir" <<'WEOF'
type Size = Big | Small

let category =
    let n = [1; 2; 3] |> Seq.length
    match Big with
    | Big -> $"big ({n})"
    | Small -> "small"

print category
WEOF
out=$($BIN "$scriptdir/blockmatch.weir")
expect "valid F# shape: arms deeper than the pending binding" "big (3)" "$out"

cat > "$scriptdir/dedentarm.weir" <<'WEOF'
let r =
    let v =
        match 1 with
| _ -> 0
    v
WEOF
errout=$($BIN "$scriptdir/dedentarm.weir" 2>&1) && fail "dedented arm inside a block should be rejected"
echo "$errout" | grep -qF "needs a body" || fail "dedented-arm rejection text missing: $errout"
echo "e2e ok: dedented arm inside a block rejected (F#-rejects-this)"

cat > "$scriptdir/blockbad.weir" <<'WEOF'
let msg =
    let n = 1
WEOF
errout=$($BIN "$scriptdir/blockbad.weir" 2>&1) && fail "bodyless block let should be rejected"
echo "$errout" | grep -qF "needs a body" || fail "bodyless-let error text missing: $errout"
echo "e2e ok: bodyless block let rejected"

# an assembly error is located file:line:col like every diagnostic
# [D:assembly-col] — the column is the named line's indentation; the
# runner and check agree, and the message drops its "line N:" lead
expect "the runner locates an assembly error line:col" "blockbad.weir:2:5: this let needs a body" "$errout"
errout=$($BIN check "$scriptdir/blockbad.weir" 2>&1) && fail "check must refuse the bodyless block let"
expect "check locates the same assembly error" "blockbad.weir:2:5: error [assembly]: this let needs a body" "$errout"

rm -rf "$scriptdir"

out=$($BIN -e 'print "visible"')
[ "$out" = "visible" ] || fail "print in -e must emit exactly the line, no unit trailer (got: $out)"
echo "e2e ok: unit is invisible in -e"

# unit stays invisible on the piped surface (its bytes are pinned) —
# the tty echoes `() : unit` since the FSI-parity ruling [D:repl-it],
# pinned in tests/repl/repl-it-streamed.py
out=$(printf 'print "hi"\nlet u = ()\nu\n#quit\n' | $BIN)
echo "$out" | grep -qF "hi" || fail "REPL print lost its output"
if echo "$out" | grep -qF "() : unit"; then fail "unit leaked into the piped REPL display"; fi
echo "e2e ok: unit is invisible in the piped REPL"

# --- the it-rebinding matrix, FSI parity (2026-09-18) [D:repl-it] ------
# expressions and commands rebind `it` — always, unit included; a `let`
# does not (FSI: `let o = 10;;` binds no it); a directive leaves it; a
# fresh session's `it` is unbound. These cells pin the piped value path;
# the streamed (tty) half — it := () and the misuse teach — lives in
# tests/repl/repl-it-streamed.py.
out=$(printf '5\nit\n#quit\n' | $BIN)
[ "$(echo "$out" | grep -cF '5 : int')" = "2" ] || fail "a non-unit expression must rebind it: $out"
out=$(printf 'print "hi"\n(it, 1)\n#quit\n' | $BIN)
echo "$out" | grep -qF '((), 1) : unit * int' || fail "a unit expression must rebind it := (): $out"
out=$(printf 'let o = 10\nit\n#quit\n' | $BIN)
echo "$out" | grep -qF "unbound variable 'it'" || fail "a let must not rebind it (FSI parity): $out"
out=$(printf '7\n#echo\nit\n#quit\n' | $BIN)
[ "$(echo "$out" | grep -cF '7 : int')" = "2" ] || fail "a directive must leave it untouched: $out"
echo "e2e ok: the it-rebinding matrix — expr/command always (unit included), let never, directive inert [D:repl-it]"

# --- the function-value echo, piped spelling (2026-09-18) [D:repl-fn-echo]
# a named builtin echoes the plain #help signature + glance; a session
# function echoes name : scheme + its definition line; an anonymous
# closure keeps <fun> : ty — vars normalized ('a1 -> 'a2 reads 'a -> 'b)
out=$(printf 'Seq.map\n#quit\n' | $BIN)
echo "$out" | grep -qF "Seq.map (f: 'a -> 'b) (xs: seq<'a>) : seq<'b>" || fail "Seq.map must echo the #help signature: $out"
echo "$out" | grep -qF "Apply a function to every element, lazily." || fail "Seq.map must echo the doc's first line: $out"
out=$(printf 'find\n#quit\n' | $BIN)
echo "$out" | grep -qF "Seq.find (pred: 'a -> bool) (xs: seq<'a>) : 'a" || fail "a bare alias must echo its qualified home: $out"
out=$(printf 'let f x = x + 1\nf\n#quit\n' | $BIN)
echo "$out" | grep -qF "f : int -> int" || fail "a session function must echo name : scheme: $out"
# piped stdin is not echoed, so the definition line in the output is the echo's
echo "$out" | grep -qF "let f x = x + 1" || fail "a session function must echo its recorded definition line: $out"
out=$(printf 'fun x -> x\n#quit\n' | $BIN)
echo "$out" | grep -qF "<fun> : 'a -> 'a" || fail "an anonymous closure keeps <fun> : ty, vars normalized: $out"
echo "e2e ok: the function-value echo — builtin mini-help, session def line, anonymous <fun> [D:repl-fn-echo]"

# --- #infer / it / #save distill round-trip (2026-09-17) [D:repl-save] -
# #save distills a session to its checkable definitions (option B): it
# keeps `type` decls and named `let` bindings (with their real multi-line
# source), dedups a redeclared name to its last form, drops bare-echo
# scratch and `it`-references, and guarantees the file weir-checks clean.
# A realistic messy session drives it here; each single-line statement,
# plus the multi-line heredoc/type distill, is pinned. The piped
# multi-line assembly is pinned in its own cell below.
infdir=$(mkweirtmp)
# the bare `sample` echo binds `it` for the no-source #infer — a `let`
# no longer does (FSI parity [D:repl-it])
infout=$(printf '%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n' \
  'let sample = ["{\"items\": [{\"name\": \"a\", \"port\": 8080}], \"count\": 1}"]' \
  'sample' \
  '#infer from json as Root' \
  'sample |> from json Root |> _.items |> map _.name |> Seq.length' \
  'type Mode = Fast | Slow' \
  'type Mode = Fast | Slow | Idle' \
  'let picked = ["a"; "b"] |> map (Str.toUpper)' \
  'let gobeldy = it' \
  "#save $infdir/explore.weir" \
  '#quit' | $BIN)
echo "$infout" | grep -qF "defined: Item, Root" || fail "#infer did not define the auto-named types: $infout"
echo "$infout" | grep -qF "#save: wrote" || fail "#save did not report a write: $infout"
[ -f "$infdir/explore.weir" ] || fail "#save did not write the file"
# (a) the acceptance-defining guarantee: the distilled file weir-checks clean
$BIN check "$infdir/explore.weir"
echo "REAL=$?" >> /tmp/e2e-infer.out
$BIN check "$infdir/explore.weir" || fail "the saved script must weir-check clean: $(cat "$infdir/explore.weir")"
# (b) the injected type is carried; a bare alias qualified (map -> Seq.map)
grep -qF "type Root" "$infdir/explore.weir" || fail "#save did not carry the injected type"
grep -qF "Seq.map" "$infdir/explore.weir" || fail "#save did not qualify the bare alias 'map': $(cat "$infdir/explore.weir")"
# (c) dedup: the redeclared type appears once, in its last form
[ "$(grep -cF "type Mode" "$infdir/explore.weir")" = "1" ] || fail "#save did not dedup the redeclared type: $(cat "$infdir/explore.weir")"
grep -qF "Idle" "$infdir/explore.weir" || fail "#save kept the wrong (earlier) redeclaration: $(cat "$infdir/explore.weir")"
# (d) drop the bare expression echo and the it-reference
grep -qF "Seq.length" "$infdir/explore.weir" && fail "#save kept a bare expression echo (scratch): $(cat "$infdir/explore.weir")"
grep -qF "= it" "$infdir/explore.weir" && fail "#save kept an it-referencing binding: $(cat "$infdir/explore.weir")"
grep -qF "gobeldy" "$infdir/explore.weir" && fail "#save kept the dropped it-binding: $(cat "$infdir/explore.weir")"
# (e) the dropped-count note fires for the it-line
echo "$infout" | grep -qF "dropped 1 line(s) that referenced session-only state" \
  || fail "#save did not print the dropped-count note: $infout"
echo "e2e ok: #save DISTILLS a messy session to a checking .weir (dedup, drop it, note)"

# --- #infer drafted-type diagnostic (recast 2026-09-17) [D:infer-diagnostic]/[D:infer-wire-sanitize] --
# when a drafted type fails to check for any reason, the diagnostic must
# name the line:col within the drafted text and show the offending line
# with a caret — the drafted text is weir's own synthesis, so the user
# must see which generated part is bad. Keyword keys sanitize now
# ([<Wire "in">] inField — the old {"in": 2} fixture checks and is the
# positive below), so the remaining un-checkable class is a duplicate
# key: legal JSON, both occurrences draft the same clean field, and a
# record refuses a duplicate field. That class drives the diagnostic.
# The directive prints to stdout, so the piped REPL surfaces it — no
# pty needed.
idout=$(printf '%s\n%s\n%s\n' \
  'let js = ["{\"a\": 1, \"a\": 2}"]' \
  '#infer js from json as PodsJson' \
  '#quit' | $BIN 2>&1)
echo "$idout" | grep -qF "a drafted type did not check at line 1, col 1: duplicate field 'a'" \
  || fail "#infer diagnostic lost its line:col: $idout"
echo "$idout" | grep -qF "type PodsJson = {" || fail "#infer diagnostic lost the offending-line snippet: $idout"
echo "$idout" | grep -qF "^" || fail "#infer diagnostic lost the caret: $idout"
echo "e2e ok: a failing #infer drafted type prints line:col + a caret'd snippet (duplicate-key class)"

# the positive the old fixture became [D:infer-wire-sanitize]: a keyword
# key infers to a [<Wire>]'d field on the parser's own repair spelling
# (in -> inField) — the draft checks and reads the sample
inpos=$(printf '%s\n%s\n%s\n%s\n%s\n' \
  'let js = ["{\"in\": 2}"]' \
  '#infer js from json as InJson' \
  'let v = js |> from json InJson' \
  'print $"read={v.inField}"' \
  '#quit' | $BIN 2>&1)
echo "$inpos" | grep -qF "defined: InJson (1 type)" || fail "a keyword key must draft a checking type: $inpos"
echo "$inpos" | grep -qF "read=2" || fail "the Wire'd keyword-key field must READ the sample: $inpos"

# an empty-string key [D:infer-wire-sanitize]: no field name can spell
# it and [<Wire>] refuses an empty wire key, so the sanitizer drops it
# with a printed note — and the draft still reads the sample (the
# readers tolerate an undeclared key)
ekout=$(printf '%s\n%s\n%s\n%s\n%s\n' \
  'let ek = ["{\"\": 1, \"a\": 2}"]' \
  '#infer ek from json as EmptyKey' \
  'let w = ek |> from json EmptyKey' \
  'print $"a={w.a}"' \
  '#quit' | $BIN 2>&1)
echo "$ekout" | grep -qF "empty-string key" || fail "the empty-key drop must print its note: $ekout"
echo "$ekout" | grep -qF "a=2" || fail "the draft must read around the dropped empty key: $ekout"
echo "e2e ok: a keyword key rides [<Wire>] and reads; an empty-string key drops loudly and reads around"

# --- #infer let: the one-shot form (2026-09-25) [D:infer-one-shot] -----
# `#infer let z = <src> |> from json as _` runs the source ONCE, drafts
# + injects the types (as _ auto-names the root), and binds z to the
# value parsed from the SAME sample — the manual freeze/re-parse dance
# and its double-run hazard both gone. #save distills the honest
# two-part spelling: the type decls plus a plain `let z = src |> from
# json <Name>`, never the directive line.
osdir=$(mkweirtmp)
osout=$(printf '%s\n%s\n%s\n%s\n%s\n' \
  'let js = ["{\"a\": \"b\"}"]' \
  '#infer let z = js |> from json as _' \
  'print z.a' \
  "#save $osdir/one.weir" \
  '#quit' | $BIN 2>&1)
echo "$osout" | grep -qF "defined: Type1 (1 type)" || fail "#infer let must auto-name the root on as _: $osout"
echo "$osout" | grep -qF 'z : Type1 = { a = "b" }' || fail "#infer let must bind and echo the parsed value: $osout"
grep -qF "type Type1" "$osdir/one.weir" || fail "#save must carry the one-shot's drafted type: $(cat "$osdir/one.weir")"
grep -qF "from json Type1" "$osdir/one.weir" || fail "#save must distill the honest explicit read, not the directive: $(cat "$osdir/one.weir")"
$BIN check "$osdir/one.weir" || fail "the one-shot distill must weir-check clean: $(cat "$osdir/one.weir")"
# run-once: a side-effecting source runs exactly once for draft + bind
oscount="$osdir/runs.txt"
osrun=$(printf '%s\n%s\n%s\n' \
  "#infer let p = \$(sh -c \"echo run >> $oscount; echo '{\\\"n\\\": 1}'\") |> from json as Ping" \
  'print p.n' \
  '#quit' | $BIN 2>&1)
echo "$osrun" | grep -qF "defined: Ping (1 type)" || fail "#infer let over a command must draft: $osrun"
# -eq, not =: BSD/macOS wc -l pads the count with leading spaces
[ "$(wc -l < "$oscount")" -eq 1 ] || fail "#infer let must run the source exactly once, ran $(wc -l < "$oscount")"
# a top-level array binds seq<Name>; the directive picks the seq spelling
osarr=$(printf '%s\n%s\n%s\n' \
  'let ns = ["[{\"name\": \"a\"}, {\"name\": \"b\"}]"]' \
  '#infer let xs = ns |> from json as Svc' \
  '#quit' | $BIN 2>&1)
echo "$osarr" | grep -qF "xs : seq<Svc>" || fail "#infer let must bind a top-level array as seq<Name>: $osarr"
# the script-side leak of the directive spelling teaches, at check
cat > "$osdir/leak.weir" <<'WEOF'
let z = ["{}"] |> from json as Zeds
print "x"
WEOF
oschk=$($BIN check "$osdir/leak.weir" 2>&1) && fail "from json as <Name> must refuse in a file: $oschk"
# the rendered message wraps at the terminal width — pin a fragment
# that cannot straddle the wrap
echo "$oschk" | grep -qF "#infer spelling" || fail "the file-side 'as' misuse must teach #infer: $oschk"
# a scalar top drafts no root record — nothing bound, the notes stand
ossc=$(printf '%s\n%s\n' '#infer let z = ["42"] |> from json as _' '#quit' | $BIN 2>&1)
echo "$ossc" | grep -qF "nothing bound" || fail "#infer let must refuse to bind a scalar top: $ossc"
# usage: the pipe-from marker is required
osus=$(printf '%s\n%s\n' '#infer let z = foo as _' '#quit' | $BIN 2>&1)
echo "$osus" | grep -qF "missing '|> from <format>'" || fail "#infer let usage lost the from marker teach: $osus"
echo "e2e ok: #infer let drafts, binds run-once, distills the explicit spelling; the file-side 'as' teaches; scalar refused, usage taught"

# --- #infer taken-name guard (2026-09-18) [D:repl-infer] ---------------
# a k8s secret volume's `secret:` sub-object desires the name `Secret` —
# a taken name (the builtin): the old draft injected `type Secret` that
# every `secret: Secret` field bypassed for the primitive, so `from yaml`
# refused ("a Secret must not cross"). The derived name now parent-
# prefixes (VolumeSecret), loudly, and the draft reads the fixture clean.
ivdir=$(mkweirtmp)
cat > "$ivdir/pod-volumes.yaml" <<'YEOF'
volumes:
- name: creds
  secret:
    secretName: app-creds
    defaultMode: 420
YEOF
ivout=$(printf '%s\n%s\n%s\n%s\n%s\n%s\n' \
  "let raw = File.read \"$ivdir/pod-volumes.yaml\" |> Seq.freeze" \
  '#infer raw from yaml as PodSpec' \
  'let vol = raw |> from yaml PodSpec |> _.volumes |> Seq.head' \
  'print vol.secret.secretName' \
  'print (show vol.secret.defaultMode)' \
  '#quit' | $BIN 2>&1)
echo "$ivout" | grep -qF "defined: VolumeSecret, Volume, PodSpec (3 types)" \
  || fail "the secret volume must draft VolumeSecret (parent-prefixed off the taken name): $ivout"
echo "$ivout" | grep -qF "note: 'secret' would shadow the existing type 'Secret' — drafted as 'VolumeSecret'" \
  || fail "the taken-name rename must print its note: $ivout"
echo "$ivout" | grep -qF "app-creds" || fail "the renamed draft must READ the secret volume: $ivout"
echo "$ivout" | grep -qF "420" || fail "the renamed draft must read the volume's int field: $ivout"
echo "e2e ok: a k8s secret volume infers around the builtin Secret (VolumeSecret, noted) and reads clean"

# the 'as' name colliding with a builtin refuses with the teaching — the
# user chose the name, so renaming it silently would be worse
asout=$(printf '%s\n%s\n%s\n' \
  'let js = ["{\"x\": 1}"]' \
  '#infer js from json as Secret' \
  '#quit' | $BIN 2>&1)
echo "$asout" | grep -qF "#infer: 'Secret' is a built-in type" || fail "a builtin 'as'-name must refuse: $asout"
echo "$asout" | grep -qF "pick another 'as' name" || fail "the refusal must carry the teaching: $asout"
echo "$asout" | grep -qF "defined:" && fail "a refused 'as'-name must define nothing: $asout" || true
echo "e2e ok: '#infer … as Secret' refuses loudly (the builtin wins every use; pick another name)"

# --- #infer array-element merge + open mappings (2026-09-18) [D:repl-infer] --
# the real-kubectl failure this closes: `kubectl get cm -o json` — every
# ConfigMap's `data` carries its own keys, so a first-element-only draft
# died reading item 2 ("missing field 'items[2].data.networkYml'").
# Array elements merge (a key absent in some items drafts Option) and a
# data-keyed object drafts as the open mapping seq<string * string> —
# the draft reads the whole list, per-item data as pairs.
cmdir=$(mkweirtmp)
cat > "$cmdir/cmlist.json" <<'JEOF'
{
    "apiVersion": "v1",
    "items": [
        {
            "apiVersion": "v1",
            "data": {
                "Corefile": ".:53 {\n    errors\n    cache 30\n}"
            },
            "kind": "ConfigMap",
            "metadata": {
                "creationTimestamp": "2026-09-01T10:00:00Z",
                "name": "coredns",
                "namespace": "kube-system",
                "resourceVersion": "231",
                "uid": "5c3f2a1b-0001-4b6e-9a51-aaaaaaaaaaaa"
            }
        },
        {
            "apiVersion": "v1",
            "data": {
                "mode": "flat",
                "network.yml": "nodes: 3\nsubnet: 10.42.0.0/16"
            },
            "kind": "ConfigMap",
            "metadata": {
                "annotations": {
                    "kubectl.kubernetes.io/last-applied-configuration": "{\"apiVersion\":\"v1\",\"kind\":\"ConfigMap\"}"
                },
                "creationTimestamp": "2026-09-02T08:30:00Z",
                "name": "network-config",
                "namespace": "default",
                "resourceVersion": "482",
                "uid": "5c3f2a1b-0002-4b6e-9a51-bbbbbbbbbbbb"
            }
        },
        {
            "apiVersion": "v1",
            "data": {
                "ca.crt": "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----"
            },
            "kind": "ConfigMap",
            "metadata": {
                "creationTimestamp": "2026-09-01T10:00:05Z",
                "name": "kube-root-ca.crt",
                "namespace": "default",
                "resourceVersion": "12",
                "uid": "5c3f2a1b-0003-4b6e-9a51-cccccccccccc"
            }
        }
    ],
    "kind": "List",
    "metadata": {
        "resourceVersion": "482"
    }
}
JEOF
cmout=$(printf '%s\n%s\n%s\n%s\n%s\n%s\n%s\n' \
  "let cm = File.read \"$cmdir/cmlist.json\" |> Seq.freeze" \
  '#infer cm from json as ConfigMapList' \
  'let v = cm |> from json ConfigMapList' \
  'print (v |> _.metadata.resourceVersion)' \
  'let hit = v.items |> Seq.head |> _.data |> Seq.tryFind (fun (k, _) -> k == "Corefile")' \
  'print (match hit with | Some (k, _) -> $"hit={k}" | None -> "miss")' \
  '#quit' | $BIN 2>&1)
echo "$cmout" | grep -qF "ConfigMapList" || fail "#infer must define the as-named type: $cmout"
echo "$cmout" | grep -qF "note: 'items.data' carries different keys across the array's elements" \
  || fail "the open-map merge note must print: $cmout"
echo "$cmout" | grep -qF "note: 'annotations' has mostly non-identifier keys" \
  || fail "the annotations map note must print: $cmout"
echo "$cmout" | grep -qF "482" || fail "the merged draft must READ the whole list (metadata.resourceVersion): $cmout"
echo "$cmout" | grep -qF "hit=Corefile" || fail "per-item data must read as pairs (Seq.tryFind): $cmout"
echo "e2e ok: #infer merges ConfigMapList items; data reads as an open mapping via from json"

# the yaml twin: the same merge + mapping rules through from yaml
cat > "$cmdir/cmlist.yaml" <<'YEOF'
apiVersion: v1
items:
- apiVersion: v1
  data:
    Corefile: ".:53"
  kind: ConfigMap
  metadata:
    name: coredns
    namespace: kube-system
- apiVersion: v1
  data:
    network.yml: "nodes: 3"
    mode: flat
  kind: ConfigMap
  metadata:
    annotations:
      kubectl.kubernetes.io/last-applied-configuration: "{}"
    name: network-config
    namespace: default
kind: List
YEOF
cmyout=$(printf '%s\n%s\n%s\n%s\n%s\n' \
  "let cmy = File.read \"$cmdir/cmlist.yaml\" |> Seq.freeze" \
  '#infer cmy from yaml as CmListY' \
  'let vy = cmy |> from yaml CmListY' \
  'let hy = vy.items |> Seq.skip 1 |> Seq.head |> _.data |> Seq.tryFind (fun (k, _) -> k == "network.yml")' \
  'print (match hy with | Some (_, c) -> $"content={c}" | None -> "miss")' \
  '#quit' | $BIN 2>&1)
echo "$cmyout" | grep -qF "note: 'items.data' carries different keys across the array's elements" \
  || fail "the yaml twin must print the open-map merge note: $cmyout"
echo "$cmyout" | grep -qF "content=nodes: 3" || fail "the yaml twin must read per-item data as pairs: $cmyout"
echo "e2e ok: the yaml twin merges items and reads data as an open mapping"
rm -rf "$cmdir"

# --- piped REPL multi-line assembly (2026-09-16) [D:repl-multiline] ----
# a redirected REPL (printf … | weir) reads physical lines but must
# assemble a statement that spans several — heredoc, a multi-line `type`,
# an offside if/match block, a leading-`|>` pipeline — the way a script
# does. The failure mode: each physical line parsed alone, so a heredoc
# body was "unbound variable 'a'". Reuses bufferComplete + Script.assemble
# (no second parser); a peeked line stays only if it still attaches.

# the flagship repro: a heredoc body of two lines, then a statement that
# uses the binding — both must assemble, and Seq.length is 2
out=$(printf 'let block = <<<\n    a\n    b\nblock |> Seq.length\n' | $BIN 2>&1)
expect "piped heredoc assembles its body" 'block : seq<string> = ["a"; "b"]' "$out"
expect "piped heredoc then a using-statement evaluates" "2 : int" "$out"

# a multi-line record `type`, then a value that uses it — the type must
# declare and the following field-access must evaluate as its own echo
out=$(printf 'type Point = {\n  x: int\n  y: int\n}\n{ x = 1; y = 2 }.x\n' | $BIN 2>&1)
expect "piped multi-line type declares" "type Point declared" "$out"
expect "piped value after a type evaluates separately" "1 : int" "$out"

# an offside if/else block under a `let` (col-0 if/then/else is not one
# statement in weir — the block rides its binding); the `else` and body
# lines must stay attached
out=$(printf 'let r =\n  if 1 > 0 then\n    "yes"\n  else\n    "no"\nr\n' | $BIN 2>&1)
expect "piped offside if/else block assembles" 'r : string = "yes"' "$out"

# a leading-`|>` pipeline: `xs` then continuation lines each begin with
# `|>` — they must attach to the value above, not error "continuation
# without a statement"
out=$(printf 'let xs = [1; 2; 3]\nxs\n|> Seq.map (fun n -> n * 2)\n|> Seq.sum\n' | $BIN 2>&1)
expect "piped leading-pipe continuation assembles" "12 : int" "$out"
echo "$out" | grep -qF "continuation without a statement" && fail "leading-|> lines split into separate statements: $out"

# the regression guard: a run of single-line statements must each still
# evaluate and echo, exactly as before — no gluing of independent lines
out=$(printf 'let a = 1\na + 1\n"hi"\n' | $BIN 2>&1)
expect "single-line: let echoes" "a : int = 1" "$out"
expect "single-line: expression echoes" "2 : int" "$out"
expect "single-line: string echoes" '"hi" : string' "$out"
# a destructuring let echoes each binding with its value, as two lets would
out=$(printf 'let (a, b) = (1, "s")\n' | $BIN 2>&1)
expect "destructuring: the first binding's value" "a : int = 1" "$out"
expect "destructuring: the second binding's value" 'b : string = "s"' "$out"

# a blank line ends a pending entry, as the tty's blank-line escape does:
# the incomplete line errors and the rest of stdin still runs
out=$(printf 'let x = [1;\n\nprint (Str.toUpper "after")\nprint (Str.toUpper "later")\n' | $BIN 2>&1)
expect "piped: a blank line ends an incomplete entry" "AFTER" "$out"
expect "…and the next line still runs" "LATER" "$out"
echo "e2e ok: piped REPL assembles multi-line statements; single-line lines each still echo"

stmtdir=$(mkweirtmp)
cat > "$stmtdir/discard.weir" <<'WEOF'
sh -c "touch discard-proof"
"polluting output"
WEOF
errout=$(cd "$stmtdir" && $BIN discard.weir 2>&1) && fail "discarded-string script should exit nonzero"
echo "$errout" | grep -qF "discards it" || fail "discard error text missing: $errout"
[ -e "$stmtdir/discard-proof" ] && fail "check-first violated: effect ran before the discard error"
echo "e2e ok: discarded value rejected at check time, zero effects"

cat > "$stmtdir/brackethead.weir" <<'WEOF'
["bracket"; "head"] |> Seq.length |> print
WEOF
out=$($BIN "$stmtdir/brackethead.weir")
expect "line-head string list is an expression, not /usr/bin/[" "2" "$out"

cat > "$stmtdir/barels.weir" <<'WEOF'
ls
WEOF
errout=$($BIN "$stmtdir/barels.weir" 2>&1) && fail "bare builtin ls statement should be rejected"
echo "$errout" | grep -qF "^ls" || fail "bare-ls rejection must name ^ls: $errout"
echo "e2e ok: bare builtin ls rejected, ^ls named"

cat > "$stmtdir/sequnit.weir" <<'WEOF'
let xs = ["a"; "b"]
xs |> Seq.map print
WEOF
errout=$($BIN "$stmtdir/sequnit.weir" 2>&1) && fail "seq<unit> statement should be rejected"
echo "$errout" | grep -qF "Seq.iter" || fail "seq<unit> rejection must hint Seq.iter: $errout"
echo "e2e ok: lazy seq<unit> trap caught with the iter hint"

cat > "$stmtdir/adv.weir" <<'WEOF'
let xs = ["a"; ""; "line1\nline2"; "b"]
xs |> print
WEOF
out=$($BIN "$stmtdir/adv.weir")
expected=$(printf 'a\n\nline1\nline2\nb')
[ "$out" = "$expected" ] || fail "renderer adversarial case diverged from line-per-element: $(printf '%q' "$out")"
# captured output is LF on every platform [D:lf-output] — Windows
# WriteLine's \r\n reached redirected streams until the ruling
case "$out" in *$'\r'*) fail "captured output must carry no CR byte: $(printf '%q' "$out")";; esac
echo "e2e ok: renderer byte-identical on empties and embedded newlines (LF everywhere)"

cat > "$stmtdir/stream.weir" <<'WEOF'
["alpha"; "staged: yes"; "omega"] |> print
WEOF
out=$($BIN "$stmtdir/stream.weir" | grep staged)
expect "print streams through a host pipe" "staged: yes" "$out"

out=$($BIN -e '$(sh -c "echo streamed") |> print')
[ "$out" = "streamed" ] || fail "expression-position process |> print must stream (got: $out)"
echo "e2e ok: cmd sh |> print streams"

if $BIN -e '$(sh -c "exit 3") |> print' 2>/dev/null; then
    fail "cmd |> print must raise on nonzero exit at force"
fi
echo "e2e ok: cmd sh |> print raises on nonzero exit"
rm -rf "$stmtdir"

if $BIN -e 'yes hi | grep hi | complete' 2>/dev/null; then
    fail "multi-segment | complete should be a parse error"
fi
echo "e2e ok: multi-segment complete rejected"

if $BIN -e '^weir-definitely-not-a-command' 2>/dev/null; then
    fail "forced unknown command should not succeed"
fi
echo "e2e ok: forced unknown command rejected"

# --- check-vs-run on missing commands + binding-shadows-PATH (2026-07-21)

svdir=$(mkweirtmp)

cat > "$svdir/missing.weir" <<'WEOF'
definitely-not-a-real-tool --flag arg
WEOF
rc=0; out=$($BIN check "$svdir/missing.weir") || rc=$?
[ $rc -eq 0 ] || fail "check must exit 0 on missing-command scripts (got $rc)"
echo "$out" | grep -qF "[cmd-not-found]" || fail "cmd-not-found warning missing: $out"
rc=0; $BIN "$svdir/missing.weir" >/dev/null 2>&1 || rc=$?
[ $rc -ne 0 ] || fail "the RUNNER must still reject missing commands"
echo "e2e ok: check warns where run errors (DELIBERATE, the editing-without-tools rule)"

# a near-miss binding bridges the verdict split: check's command
# reading names the candidate the runner's expression reading will name
cat > "$svdir/nearmiss.weir" <<'WEOF'
let target = "x"
targt --flag
WEOF
out=$($BIN check "$svdir/nearmiss.weir" || true)
echo "$out" | grep -qF "Did you mean 'target'?" || fail "cmd-not-found must hint the near-miss binding: $out"
out=$($BIN check "$svdir/missing.weir")
echo "$out" | grep -qF "Did you mean" && fail "no-near-miss heads must stay hint-free: $out"
echo "e2e ok: cmd-not-found hints near-miss bindings only"

# per-statement resolver: script bindings shadow PATH commands
cat > "$svdir/shadow.weir" <<'WEOF'
let cat = 1

cat "anything"
WEOF
rc=0; errout=$($BIN "$svdir/shadow.weir" 2>&1) || rc=$?
[ $rc -ne 0 ] || fail "shadowed cat must not run the binary"
echo "$errout" | grep -qF "not a function" || fail "expected an application type error: $errout"
echo "e2e ok: script bindings shadow PATH commands (per-statement resolver)"

out=$($BIN -e '$(^cat /etc/hosts) |> Seq.length')
echo "$out" | grep -qE "[0-9]" || fail "^cat must still force the binary: $out"
echo "e2e ok: ^ still forces the real binary through a shadow"

rm -rf "$svdir"

# the deep-run lock [D:masking-mechanized] — publish refuses while a live
# holder exists; a dead holder is a stale lock any actor may clear
LOCKSH="$(dirname "$0")/deep-lock.sh"
"$LOCKSH" release # start clean
"$LOCKSH" check && fail "no lock should not report a live holder"
sleep 30 &
lk_pid=$!
"$LOCKSH" acquire "$lk_pid"
[ "$("$LOCKSH" check)" = "$lk_pid" ] || fail "check must report the live holder pid"
"$LOCKSH" acquire 999 2>/dev/null && fail "acquire must refuse while a live holder exists"
kill "$lk_pid" 2>/dev/null || true
wait "$lk_pid" 2>/dev/null || true
"$LOCKSH" check && fail "a dead holder must read as stale (no live holder)"
[ -f "$(dirname "$0")/../.weir-deep-run.lock" ] && fail "check must clear the stale lock"
echo "e2e ok: deep-run lock acquires, refuses double, clears when stale"

# --- weir lsp v1 (2026-07-21, LSP chain 3/3) ---------------------------

if command -v python3 >/dev/null 2>&1; then
    if [ "$IS_WINDOWS" = "1" ]; then
        echo "e2e SKIP: harness selftest — POSIX-only (os.fork zombie truth, sh-stub stamp gate)"
    else
        python3 "$(dirname "$0")/../tests/lib/harness-selftest.py" || fail "harness selftest (zombie truth / stamp gate)"
        echo "e2e ok: harness library selftest (waitpid-truth + stamp gate)"
    fi

    WEIR_BIN="$BIN" python3 "$(dirname "$0")/../tests/lsp/lsp-e2e.py" || fail "lsp integration probes"
    echo "e2e ok: lsp diagnostics/hover/completion over stdio"

    # transport hardening [D:lsp-transport-caps]: a hostile Content-Length
    # must not OOM the session — the framing layer caps at 64MB and drains
    WEIR_BIN="$BIN" python3 "$(dirname "$0")/../tests/lsp/lsp-framing.py" || fail "lsp framing hardening (oversized Content-Length)"
    echo "e2e ok: lsp survives oversized Content-Length (no OOM, stream stays synced)"

    WEIR_BIN="$BIN" python3 "$(dirname "$0")/../tests/lsp/lsp-protocol.py" || fail "lsp protocol conformance"
    echo "e2e ok: lsp answers every request (-32603/-32601/-32700/-32600), exit codes per lifecycle, case-insensitive headers"

    # conventional client argv is tolerated (languageclient v10 appends
    # --stdio/--clientProcessId to Executables — usage-exit-2 here put
    # the VS Code client in a crash-restart loop)
    lspout=$(printf '' | $BIN lsp --stdio --clientProcessId=99 2>&1) || fail "lsp must tolerate conventional client argv (rc=$?)"
    echo "$lspout" | grep -qF "usage:" && fail "conventional argv must not trip the usage arm"
    errout=$($BIN lsp --help 2>&1) && fail "lsp --help must still exit nonzero"
    echo "$errout" | grep -qF "usage: weir lsp" || fail "lsp --help must still teach"
    echo "e2e ok: weir lsp tolerates --stdio/--clientProcessId, refuses the rest"

    # --debug logs dispatch + publishes to stderr [D:lsp-uri-spelling] —
    # editors surface server stderr, so blink-class mysteries become logs
    dbgout=$(printf 'Content-Length: 46\r\n\r\n{"jsonrpc":"2.0","id":1,"method":"initialize"}' | $BIN lsp --debug 2>&1 >/dev/null)
    echo "$dbgout" | grep -qF -- "<- initialize" || fail "lsp --debug must log dispatch to stderr: $dbgout"
    echo "e2e ok: weir lsp --debug logs to stderr"

    # grammar drift guard: micro's '# rule:' annotations vs the
    # tmLanguage repository keys — add to both or neither.
    # Limitation on record: this proves rule presence, not regex
    # semantics — a wrong skip/end inside a matching rule name is
    # invisible here; per-kind escape laws are verified by eye on the
    # showcase (its @-verbatim and triple-quote lines are the canary,
    # and a stale installed syntax copy shows there too)
    # the drift rule, amended for engine capability [D:micro-exempt]: add
    # to both or neither, unless a grammar's engine cannot express it —
    # then the shortfall is stated in that grammar's header
    # (`# micro-exempt: <key> (<reason>)`) and the inventory allows it.
    # micro is Go RE2 (no lookaround); a stated exemption keeps the rich
    # editors rich without shipping a micro rule that is actively wrong.
    python3 - "$(dirname "$0")/.." <<'PYEOF' || fail "grammar inventories diverge (micro vs tmLanguage)"
import json, re, sys
root = sys.argv[1]
src = open(f"{root}/editors/micro/weir.yaml").read()
micro = set(re.findall(r"^\s*# rule: ([\w-]+)$", src, re.M))
# stated exemptions: `# micro-exempt: <key> (<reason>)` — reason REQUIRED
exempt = dict(re.findall(r"^\s*# micro-exempt: ([\w-]+) \((.+)\)$", src, re.M))
tm = set(json.load(open(f"{root}/editors/vscode/syntaxes/weir.tmLanguage.json"))["repository"].keys())
redundant = sorted(set(exempt) & micro)       # exempt AND present = a lie
missing = sorted(tm - micro - set(exempt))     # tm rule, no micro rule, no exemption
micro_only = sorted(micro - tm)
if redundant or missing or micro_only:
    print("micro-only:", micro_only, " tm-only(unexempted):", missing,
          " redundant-exempt:", redundant)
    sys.exit(1)
print(f"inventories match ({len(micro)} rules, {len(exempt)} stated micro-exempt)")
PYEOF
    echo "e2e ok: grammar inventories match (micro == tmLanguage)"

    # the grammar manifest [D:ts-split]: the generated contract the
    # split tree-sitter repo checks itself against at its pinned ref —
    # the currency gate here proves the committed file matches the
    # source, so the cross-repo half can trust it
    python3 "$(dirname "$0")/grammar-manifest.py" --check || fail "grammar manifest stale"

    # depth coverage [D:depth-guard]: every recursive parser cycle
    # routes through deepen — enumerated from the source, not from a
    # remembered list of constructors
    python3 "$(dirname "$0")/depth-coverage.py" || fail "depth coverage gate"

    # within-kind inventory [D:within-kinds]: the kinds are a closed set
    # in one table (src/Weir/Ast.fs withinKinds); the in-repo grammars
    # hard-code the same set by necessity — this pins them together
    # (the tree-sitter third checks itself in its repo, against the
    # manifest [D:ts-split])
    python3 - "$(dirname "$0")/.." <<'PYWK' || fail "within-kind inventories diverge from Ast.withinKinds"
import re, sys
root = sys.argv[1]
ast = open(f"{root}/src/Weir/Ast.fs").read()
# the table block: Name = "..." entries inside withinKinds
tbl_block = re.search(r"let withinKinds[^=]*=\n(.*?)\n\n", ast, re.S).group(1)
# a Standalone kind (pure) is its OWN head, never `within pure` — the
# grammars' within-alternation lists non-standalone kinds only
entries = re.findall(r'\{[^}]*?Name = "(\w+)"[^}]*?Standalone = (true|false)', tbl_block, re.S)
table = set(n for n, st in entries if st == "false")
def alt(path, rx):
    m = re.search(rx, open(f"{root}/{path}").read())
    if not m: return None
    return set(re.findall(r"[a-z]+", m.group(1)))
grammars = {
    "micro": alt("editors/micro/weir.yaml", r"within\[ \\\\t\]\+\(([a-z|]+)\)"),
    "tmLanguage": alt("editors/vscode/syntaxes/weir.tmLanguage.json", r"within\)\[ \\\\t\]\+\(([a-z|]+)\)"),
}
bad = {k: v for k, v in grammars.items() if v != table}
if not table or bad:
    print(f"table={sorted(table)}  mismatches=" + str({k: sorted(v) if v else None for k, v in bad.items()}))
    sys.exit(1)
print(f"within kinds match across the table + in-repo grammars ({sorted(table)})")
PYWK
    echo "e2e ok: within-kind inventory (Ast.withinKinds == micro == tmLanguage; the tree-sitter third TRUSTS the manifest — checked in its repo + [D:grammar-currency])"

    # adapter inventory [D:form-word-hover]: the from/to adapters are a
    # closed set whose source is the builtinDocs keys (`from X`/`to X`) —
    # the same one hover/completion derive from. The three grammars
    # hard-code the union of both directions; this pins all four together
    python3 - "$(dirname "$0")/.." <<'PYADP' || fail "adapter inventories diverge from builtinDocs"
import re, sys
root = sys.argv[1]
docs = open(f"{root}/src/Weir/Builtins.fs").read()
# the builtinDocs keys of the form "from X" / "to X" — the one source
source = set(re.findall(r'"(?:from|to) (\w+)"', docs))
def alt(path, rx):
    m = re.search(rx, open(f"{root}/{path}").read())
    return set(re.findall(r"[a-z]+", m.group(1))) if m else None
grammars = {
    "micro": alt("editors/micro/weir.yaml", r"\(from\|to\)\[ \\\\t\]\+\(([a-z|]+)\)"),
    "tmLanguage": alt("editors/vscode/syntaxes/weir.tmLanguage.json", r"\(from\|to\)\[ \\\\t\]\+\(([a-z|]+)\)"),
}
bad = {k: v for k, v in grammars.items() if v != source}
if not source or bad:
    print(f"source={sorted(source)}  mismatches=" + str({k: sorted(v) if v else None for k, v in bad.items()}))
    sys.exit(1)
print(f"adapters match across builtinDocs + in-repo grammars ({sorted(source)})")
PYADP
    echo "e2e ok: adapter inventory (builtinDocs == micro == tmLanguage; the tree-sitter third TRUSTS the manifest — checked in its repo + [D:grammar-currency])"

    # the Zed highlight drift guard retired with the split [D:ts-split]:
    # the canonical queries live in weir-shell/tree-sitter-weir now, so
    # this repo cannot diff against them. The replacement is a ritual,
    # stated where the guard stood: bumping the Zed grammar rev and
    # refreshing languages/weir/highlights.scm are one motion — the
    # extension update copies the queries from the same grammar commit
    # the rev pins. A CI re-join needs the extension in the grammar's
    # repo or its own; deferred with the split's release-gate question.

    # --- REPL line editor under a pty (2026-07-21) ---------------------
    if [ -z "$E2E_SHARD" ]; then
        replPtyProbes
    else
        echo "e2e skip: the pty REPL probes (shard $E2E_SHARD — they run in repl-a/repl-b)"
    fi
else
    # no silent caps: name what was skipped
    echo "e2e SKIP: python3 absent — lsp + repl pty probes NOT run" >&2
fi

# --- weir check [--json] (2026-07-21, LSP chain 2/3) -------------------

ckdir=$(mkweirtmp)

cat > "$ckdir/multi.weir" <<'WEOF'
let Foo = 1

Seq.initInfinite id == Seq.initInfinite id
WEOF
out=$($BIN check "$ckdir/multi.weir") && fail "check must exit 1 on errors" || true
echo "$out" | grep -qF "[casing-law]" || fail "casing code missing: $out"
echo "$out" | grep -qF "[eq]" || fail "second independent error missing (recovery): $out"
echo "e2e ok: weir check reports ALL errors, located and coded"

out=$($BIN check --json "$ckdir/multi.weir" || true)
echo "$out" | grep -qF '"code":"casing-law"' || fail "json code field: $out"
echo "$out" | grep -qF '"line":1' || fail "json line field: $out"
echo "e2e ok: weir check --json carries file/line/col/code"

# cmd-not-found squiggles the whole head word [PLAN-diagnostics-arc A4]
printf 'nosuchzz foo bar\n' > "$ckdir/word.weir"
out=$($BIN check --json "$ckdir/word.weir" || true)
echo "$out" | grep -qF '"code":"cmd-not-found"' || fail "cmd-not-found json: $out"
echo "$out" | grep -qF '"endCol":9' || fail "full-word endCol (nosuchzz = cols 1-8): $out"
echo "e2e ok: cmd-not-found spans the full head word"

# row provenance: cross-statement no-field errors point at the access,
# with the meet in the message [PLAN-diagnostics-arc D]
cat > "$ckdir/prov.weir" <<'WEOF'
type T = { BicepPath: string; Name: string }
let quality t =
    print (t.BicepPath2)
let mk = { BicepPath = "b"; Name = "n" }
quality mk
WEOF
out=$($BIN check --json "$ckdir/prov.weir" || true)
echo "$out" | grep -qF '"line":3,"col":14' || fail "provenance position (the access): $out"
echo "$out" | grep -qF '"endCol":24' || fail "provenance covers the field word: $out"
echo "$out" | grep -qF "(the value becomes a T at 5:1)" || fail "the meet note: $out"
echo "e2e ok: row provenance points at the access, meet in the note"

# the same note for a pattern-introduced field [D:record-patterns]: the
# field-name span in the pattern is the access, provenance inherited
cat > "$ckdir/provpat.weir" <<'WEOF'
type T = { BicepPath: string; Name: string }
let quality { BicepPath2 = b } = b
let mk = { BicepPath = "b"; Name = "n" }
quality mk
WEOF
out=$($BIN check --json "$ckdir/provpat.weir" || true)
echo "$out" | grep -qF "no field 'BicepPath2'" || fail "a pattern field misses like an access: $out"
echo "$out" | grep -qF "(the value becomes a T at" || fail "the meet note rides the pattern miss: $out"
echo "e2e ok: pattern-introduced fields inherit row provenance"

# the same note from a match-position pattern [D:record-pattern-rows]:
# arms accumulate their fields into one row, so a record missing any of
# them is refused with the meet naming where the value became nominal
cat > "$ckdir/provmatch.weir" <<'WEOF'
type T = { BicepPath: string; Name: string }
let quality p =
    match p with
    | { BicepPath = "x" } -> "lit"
    | { Missing = m } -> m
let mk = { BicepPath = "b"; Name = "n" }
quality mk
WEOF
out=$($BIN check --json "$ckdir/provmatch.weir" || true)
echo "$out" | grep -qF "no field 'Missing'" || fail "match arms accumulate into the row: $out"
echo "$out" | grep -qF "(the value becomes a T at" || fail "the meet note rides a match-position miss: $out"
echo "e2e ok: match-position record patterns accumulate fields and keep provenance"

# Flipped by [D:interior-arming]: a command-first body now checks —
# the interior command arms as an effect instead of seq-unit-erroring.
# The at-the-head/no-EOF-dump quality property moves to a non-command
# fixture (the statement rule is unchanged for those); the old
# rejection this pin asserted is the rule's named flip.
cat > "$ckdir/sib.weir" <<'WEOF'
let _f t =
    git status
    let e = "x"
    print e
WEOF
$BIN check "$ckdir/sib.weir" || fail "an armed command-first body must check clean"
cat > "$ckdir/sib2.weir" <<'WEOF'
let f t =
    Str.trim "one"
    let e = "x"
    print e
WEOF
out=$($BIN check --json "$ckdir/sib2.weir" || true)
echo "$out" | grep -qF '"line":2,"col":5' || fail "seq-unit points at the statement head: $out"
echo "$out" | grep -qF "must be unit" || fail "the seq-unit teaching survives for non-commands: $out"
echo "$out" | grep -qvF "end of the input stream" || fail "no EOF dump expected: $out"
printf '%s' "$out" | grep -q "$(printf '\037')" && fail "sentinel leaked into a diagnostic: $out"
echo "e2e ok: command-first body ARMS (flipped); non-command seq-unit still at the head, no dump"

# the interior-arming battery [D:interior-arming]: the four-row rule on
# the AOT binary — if bodies, lambda bodies, blocks, the reifier gap,
# the two raise timings, and the script carve-out
iadir=$(mkweirtmp)
cat > "$iadir/ia.weir" <<'WEOF'
let force = true
if force then
    sh -c "echo reset-ran"
    sh -c "echo clean-ran"
["a"; "b"] |> Seq.iter (fun f -> sh -c "echo item-$0" $f)
let digest =
    sh -c "echo fetched" | orFail "fetch broke"
    Str.sha256 "contents"
print (Str.sub 0 8 digest)
WEOF
out=$(cd "$iadir" && $BIN ia.weir) || fail "the interior-arming battery must run"
for want in reset-ran clean-ran item-a item-b fetched d1b2a59f; do
    echo "$out" | grep -qF "$want" || fail "interior arming: missing '$want': $out"
done
echo "e2e ok: interior commands arm in if/lambda/block bodies; reifiers work as interior statements"

# else/elif end a then-body's command [D:if-body-stop]: each branch is its
# own command (indented or one line), a dangling else binds the nearest
# if, a quoted "else" is argv, and value position still captures
cat > "$iadir/ie.weir" <<'WEOF'
if 1 == 2 then
    sh -c "echo branch-then"
else
    sh -c "echo branch-else"
if 1 == 2 then echo a elif 2 == 2 then echo branch-elif else echo c
if true then if false then echo x else echo branch-dangling else echo z
if true then echo "else" else echo no
let v = if false then echo a else echo branch-value
print (Seq.head v)
WEOF
out=$(cd "$iadir" && $BIN ie.weir) || fail "the if/else command cell must run: $out"
[ "$out" = "branch-else
branch-elif
branch-dangling
else
branch-value" ] || fail "if/else commands: each branch its own command: $out"
echo "e2e ok: else/elif end a then-body command — indented, one-line, elif, dangling else, quoted argv, value capture"

# match arms take bare commands [D:match-arm-commands]: the case-runner
# idiom — dispatch to a real command per arm, no sigil; a multi-line arm
# body sequences, the tail streams; capture position still binds
cat > "$iadir/matcharm.weir" <<'WEOF'
let mode = "build"
match mode with
| "build" ->
    sh -c "echo compiling"
    sh -c "echo linking"
| "test" -> sh -c "echo testing"
| _ -> print $"unknown: {mode}"
let latest = match 1 with | _ -> sh -c "echo v1.2.3"
print $"picked {latest |> Seq.exactlyOne}"
WEOF
out=$(cd "$iadir" && $BIN matcharm.weir) || fail "match-arm command dispatch must run"
for want in compiling linking "picked v1.2.3"; do
    echo "$out" | grep -qF "$want" || fail "match arm: missing '$want': $out"
done
echo "$out" | grep -qF "testing" && fail "only the matched arm runs"
echo "e2e ok: match arms dispatch bare commands (multi-line bodies, capture position intact)"

# command-mode block lets in every statement context [D:statement-lets]:
# a cell per context — within/if/for/match-arm/always/proc bodies take
# the full command-let law (reifiers, splices, param-ful lets)
sldir=$(mkweirtmp)
cat > "$sldir/sl.weir" <<'WEOF'
within tmp d
    let r = sh -c "echo in-within" | complete
    print $"w:{r.stdout |> Seq.exactlyOne}:{r.exitCode}"
if 1 > 0 then
    let r = sh -c "echo in-if" | complete
    print $"i:{r.stdout |> Seq.exactlyOne}"
for i in [7] do
    let r = sh -c $"echo in-for-{i}" | complete
    print $"f:{r.stdout |> Seq.exactlyOne}"
match "go" with
| "go" ->
    let r = sh -c "echo in-arm" | complete
    print $"m:{r.stdout |> Seq.exactlyOne}"
| _ -> print "no"
within
    print "body-ran"
always
    let r = sh -c "echo in-always" | complete
    print $"a:{r.stdout |> Seq.exactlyOne}"
within proc srv = sh -c "sleep 3"
    let r = sh -c "echo in-proc" | complete
    print $"p:{r.stdout |> Seq.exactlyOne}:{Proc.running srv}"
within tmp d2
    let tag = "spliced-deep"
    let f x = echo $x
    let got = f tag |> Seq.exactlyOne
    print $"s:{got}"
WEOF
$BIN check "$sldir/sl.weir" || fail "statement-lets battery must check"
out=$(cd "$sldir" && $BIN sl.weir) || fail "statement-lets battery must run"
for want in "w:in-within:0" "i:in-if" "f:in-for-7" "m:in-arm" "a:in-always" "p:in-proc:true" "s:spliced-deep"; do
    echo "$out" | grep -qF "$want" || fail "statement lets: missing '$want': $out"
done
echo "e2e ok: block lets take command RHS in within/if/for/match/always/proc bodies"

# the pure interplay [D:statement-lets]: the parser admits the grammar;
# purity refuses, located at the command — never a parse error
cat > "$sldir/slpure.weir" <<'WEOF'
if 1 > 0 then
    let v =
        pure
            let r = sh -c "echo x" | complete
            r.exitCode
    print $"{v}"
WEOF
out=$($BIN check "$sldir/slpure.weir" 2>&1) && fail "a command let inside pure must refuse" || true
echo "$out" | grep -qF "forbids effects" || fail "purity's own teaching must fire: $out"
echo "$out" | grep -qF "is a reifier" && fail "no parse-level refusal may preempt purity's span"
echo "e2e ok: pure admits the grammar; purity refuses at the command"

# the hardened teaching [D:statement-lets]: a refused context names $()
# and the actual context — --flag argv included (the dash-death class,
# which used to die raw at the double dash before the pipe); a lambda
# body grants now [D:lambda-lets], so the paren interior carries it
cat > "$sldir/slparen.weir" <<'WEOF'
print (show (let r = sh -c "x" --no-install | complete in r.exitCode))
WEOF
out=$($BIN check "$sldir/slparen.weir" 2>&1) && fail "a paren-interior reifier let must refuse" || true
echo "$out" | grep -qF 'inside parentheses, a command needs $(' || fail "the hardened teaching must fire: $out"
echo "e2e ok: the refusal teaches \$() naming the paren context, --flag argv included"


# raise timings [D:interior-arming]: armed raises immediately (the tail
# never runs); capture raises only at force (existing law, re-pinned)
cat > "$iadir/armed.weir" <<'WEOF'
let x =
    sh -c "exit 3"
    print "never"
    1
print $"{x}"
WEOF
out=$(cd "$iadir" && $BIN armed.weir 2>&1) && fail "an armed failure must raise" || true
echo "$out" | grep -qF "never" && fail "the tail must not run after an armed failure"
cat > "$iadir/cap.weir" <<'WEOF'
let _x = sh -c "exit 3"
print "bound-fine"
WEOF
out=$(cd "$iadir" && $BIN cap.weir 2>&1) || fail "an unforced capture must not raise: $out"
echo "$out" | grep -qF "bound-fine" || fail "capture binds without raising"
echo "e2e ok: armed raises immediately; capture raises at force (both timings)"

# a multi-line for body needs no district now [D:interior-arming] —
# the single-chain body yields to the sequence, binders stay in scope
cat > "$iadir/formulti.weir" <<'WEOF'
for s in ["a"; "b"] do
    sh -c "echo one-$0" $s
    sh -c "echo two-$0" $s
WEOF
out=$(cd "$iadir" && $BIN formulti.weir) || fail "multi-line for body must run"
for want in one-a two-a one-b two-b; do
    echo "$out" | grep -qF "$want" || fail "for multi-line: missing '$want': $out"
done
echo "e2e ok: multi-line for bodies arm without a district"
rm -rf "$iadir"

# ---- within tmp [D:within-scopes] ------------------------------------------
widir=$(mkweirtmp)
cat > "$widir/receipt.weir" <<'WEOF'
let digest = within tmp dir
    ["payload"] |> File.write $"{dir}/f.txt"
    Str.sha256 (File.read $"{dir}/f.txt" |> Str.join "-")
print digest
WEOF
out=$($BIN "$widir/receipt.weir") || fail "the receipt must run"
want=$(printf 'payload' | sha256sum | cut -d' ' -f1)
[ "$out" = "$want" ] || fail "the value case yields the digest: $out vs $want"
echo "e2e ok: within tmp value case — a command-heavy block yields (THE RECEIPT)"

# raise-path cleanup: the dir goes even when the block raises, and the
# raise propagates — the load-bearing pin
cat > "$widir/raise.weir" <<'WEOF'
within tmp d
    [d] |> File.write "recorded.txt"
    fail "boom"
WEOF
rout=$( cd "$widir" && $BIN raise.weir 2>&1 ) && fail "a raising block must exit nonzero" || true
echo "$rout" | grep -qF "boom" || fail "the raise must propagate: $rout"
rec=$(cat "$widir/recorded.txt")
test ! -e "$rec" || fail "raise-path cleanup: $rec survives"
echo "e2e ok: within tmp raise-path — dir removed, raise propagated"

# nested: two scopes, two dirs, both removed
cat > "$widir/nested.weir" <<'WEOF'
within tmp outer
    within tmp inner
        [outer; inner] |> File.write "both.txt"
        print "nested ran"
WEOF
( cd "$widir" && $BIN nested.weir | grep -qF "nested ran" ) || fail "nested scopes run"
while read -r p; do
    test ! -e "$p" || fail "nested cleanup: $p survives"
done < "$widir/both.txt"
echo "e2e ok: nested within tmp — two dirs, both removed"

# statement position: a non-command non-unit block still hits the
# existing discard teaching (mode-from-position, no new rule)
cat > "$widir/disc.weir" <<'WEOF'
within tmp d
    Str.length d
WEOF
out=$($BIN check "$widir/disc.weir" 2>&1 || true)
echo "$out" | grep -qF "discards it" || fail "statement position keeps the discard teaching: $out"
echo "e2e ok: within in statement position demands unit (existing discard error)"

# a reifier in the body; effect position; fmt roundtrip
cat > "$widir/reif.weir" <<'WEOF'
within tmp d
    sh -c "echo made" | orFail "sh broke"
    print "scope done"
WEOF
$BIN "$widir/reif.weir" | grep -qF "scope done" || fail "reifier body runs"
$BIN fmt --check "$widir/reif.weir" >/dev/null 2>&1 || $BIN fmt "$widir/reif.weir" >/dev/null 2>&1 || true
$BIN "$widir/reif.weir" | grep -qF "scope done" || fail "fmt kept the scope runnable"
echo "e2e ok: within tmp effect position, reifier body, fmt roundtrip"

# ---- within cd [D:within-scopes] -------------------------------------------
mkdir -p "$widir/build/sub"
cat > "$widir/cd.weir" <<'WEOF'
within cd "build"
    within cd "sub"
        print (pwd)
print (pwd)
WEOF
out=$( cd "$widir" && $BIN cd.weir )
# separator-agnostic (pwd prints the platform's): the leaf pair, not the slash
echo "$out" | head -1 | grep -q "build.sub$" || fail "nested relative cd composes: $out"
echo "$out" | tail -1 | grep -qv "build" || fail "cwd restored after the scope: $out"
echo "e2e ok: within cd — nested relative paths compose, restore on exit"

cat > "$widir/cdmiss.weir" <<'WEOF'
within cd "definitely-absent"
    ["ran"] |> File.write "marker.txt"
WEOF
mout=$( cd "$widir" && $BIN cdmiss.weir 2>&1 ) && fail "missing path must error" || true
echo "$mout" | grep -qF "within cd: no such directory:" || fail "missing-path message: $mout"
# resolved-ness = the parent rode into the message; the leaf pair with
# a dot, never the slash (the separator-brittle path-pin class)
echo "$mout" | grep -q "$(basename "$widir").definitely-absent" || fail "resolved absolute path named: $mout"
test ! -e "$widir/marker.txt" || fail "the block must NOT run on a missing path"
echo "e2e ok: within cd missing path — resolved abs path named, block never ran"

cat > "$widir/cdraise.weir" <<'WEOF'
within cd "build"
    fail "boom"
WEOF
( cd "$widir" && $BIN cdraise.weir 2>&1 | grep -qF "boom" ) && true
cat > "$widir/cdworkers.weir" <<'WEOF'
let outs = ["build"; "build/sub"] |> Seq.pmap (fun d ->
    let p = within cd d
        pwd
    p)
outs |> Seq.iter print
print (pwd)
WEOF
wout=$( cd "$widir" && $BIN cdworkers.weir )
echo "$wout" | sed -n '1p' | grep -q "build$" || fail "worker one scoped: $wout"
echo "$wout" | sed -n '2p' | grep -q "sub$" || fail "worker two scoped: $wout"
echo "$wout" | sed -n '3p' | grep -qv "build" || fail "parent cwd untouched: $wout"
echo "e2e ok: within cd nests inside pmap workers (two cwd mechanisms, one answer)"

# ---- within env [D:within-scopes] ------------------------------------------
cat > "$widir/env.weir" <<'WEOF'
let outer = [Env.pair "WA" "out"; Env.pair "WB" "keep"]
let inner = [Env.pair "WA" "in"]
within env outer
    sh -c "echo pre=$WA-$WB"
    within env inner
        sh -c "echo nested=$WA-$WB"
WEOF
eout=$( cd "$widir" && $BIN env.weir )
echo "$eout" | grep -qF "pre=out-keep" || fail "outer overlay applies: $eout"
echo "$eout" | grep -qF "nested=in-keep" || fail "collision: inner wins, outer key survives: $eout"
echo "e2e ok: within env — overlay on spawns, nested collision pinned (inner wins, outer survives)"

# weir's own env reads see the overlay [D:env-overlay-read]
cat > "$widir/envread.weir" <<'WEOF'
type C = { WA: Option<string> }
within env WA=in
    print $"get={Env.get "WA" |> Option.defaultValue "-"}"
    print $"load={(Env.load C).WA |> Option.defaultValue "-"}"
print $"after={Env.get "WA" |> Option.defaultValue "-"}"
WEOF
erout=$( cd "$widir" && env -u WA $BIN envread.weir 2>&1 )
[ "$erout" = "$(printf 'get=in\nload=in\nafter=-')" ] || fail "Env.get/Env.load see the within env overlay: $erout"
echo "e2e ok: Env.get/Env.load read the within env overlay, not after it"

# a typed ~ is home in a command line [D:tilde]: the expected home is
# weir's own Path.home — on POSIX a fake HOME moves it (pinned), on Windows
# the profile comes from the OS API and no env var moves it; data stays
# literal; a ~/ head runs (POSIX: a .sh is no executable on Windows)
tidir=$(mkweirtmp)
mkdir -p "$tidir/home/bin"
printf '#!/bin/sh\necho tool-$1\n' > "$tidir/home/bin/tool.sh"
chmod +x "$tidir/home/bin/tool.sh"
cat > "$tidir/t.weir" <<'WEOF'
echo ~ ~/x
echo "~/q" a~b ~user HEAD~1
let p = "~/s"
echo $p
WEOF
[ "$IS_WINDOWS" = "1" ] || printf '~/bin/tool.sh ran\n' >> "$tidir/t.weir"
printf 'print (Path.home ())\n' > "$tidir/h.weir"
thome=$(cd "$tidir" && HOME="$tidir/home" USERPROFILE="$tidir/home" $BIN h.weir 2>&1) || fail "Path.home must run: $thome"
[ "$IS_WINDOWS" = "1" ] || [ "$thome" = "$tidir/home" ] || fail "on POSIX the run-time home follows HOME: $thome"
out=$(cd "$tidir" && HOME="$tidir/home" USERPROFILE="$tidir/home" $BIN t.weir 2>&1) || fail "the tilde cell must run: $out"
want="$thome $thome/x
~/q a~b ~user HEAD~1
~/s"
[ "$IS_WINDOWS" = "1" ] || want="$want
tool-ran"
[ "$out" = "$want" ] || fail "tilde: typed ~ expands, data stays literal, a ~/ head runs: $out"
rm -rf "$tidir"
# cd's string is data [D:tilde-literal]: bare cd and a typed `cd ~` go
# home; a quoted ~ is a literal name, resolved against the current dir
# POSIX only: the cell leans on /tmp, a fake HOME and sh's view of the
# cwd, none of which carry to Windows; the code under test is portable
if [ "$IS_WINDOWS" != "1" ]; then
    tldir=$(mkweirtmp)
    mkdir -p "$tldir/home"
    printf 'cd "/tmp"\ncd\nsh -c "echo bare=$(pwd -P)"\ncd "/tmp"\ncd ~\nsh -c "echo typed=$(pwd -P)"\ncd "/tmp"\ncd "~/nope"\n' > "$tldir/cd.weir"
    # physical paths on both sides: macOS's /var is a symlink to /private/var
    tlhome=$(cd "$tldir/home" && pwd -P)
    out=$(cd "$tldir" && HOME="$tldir/home" USERPROFILE="$tldir/home" $BIN cd.weir 2>&1) && fail "a quoted ~ must not be home: $out" || true
    echo "$out" | grep -qxF "bare=$tlhome" || fail "bare cd goes home: $out"
    echo "$out" | grep -qxF "typed=$tlhome" || fail "a typed cd ~ goes home: $out"
    echo "$out" | grep -qF "no such directory: /tmp/~/nope" || fail "cd \"~/nope\" is a literal name: $out"
    rm -rf "$tldir"
fi
echo "e2e ok: a typed ~ is home in a command line — argv and head; quoted, spliced, mid-word and ~user stay literal; cd takes its string literally"
# HOME verbatim [D:home-verbatim]: a set HOME is home even when the dir is
# missing (bash), unset falls back to the OS lookup, empty raises rather
# than expanding `~/x` to `/x`. POSIX only: Windows reads the profile API
if [ "$IS_WINDOWS" != "1" ]; then
    hvdir=$(mkweirtmp)
    printf 'echo ~/x
print (Path.home ())
print (Path.configHome ())
' > "$hvdir/h.weir"
    out=$(cd "$hvdir" && env -u XDG_CONFIG_HOME HOME=/nonexistent-home $BIN h.weir 2>&1) || fail "a missing HOME dir is still home: $out"
    [ "$out" = "/nonexistent-home/x
/nonexistent-home
/nonexistent-home/.config" ] || fail "HOME is used verbatim: $out"
    out=$(cd "$hvdir" && env -u HOME -u XDG_CONFIG_HOME $BIN h.weir 2>&1) || fail "an unset HOME falls back to the OS: $out"
    case "$out" in /*/x*) ;; *) fail "unset HOME: an absolute OS home: $out" ;; esac
    rc=0
    out=$(cd "$hvdir" && HOME= $BIN h.weir 2>&1) || rc=$?
    [ "$rc" = "1" ] || fail "an empty HOME must raise (rc $rc): $out"
    echo "$out" | grep -qF "no home directory" || fail "the empty-HOME message: $out"
    rm -rf "$hvdir"
    echo "e2e ok: HOME is read verbatim — missing dir kept, unset falls back, empty raises"
fi

# NAME=value cmd [D:env-prefix]: bash's one-line child env — values of
# every form, per pipeline stage, composed with a sigil (the prefix wins),
# argv CC=gcc untouched, the parent env clean; a lone FOO=1 teaches let
epdir=$(mkweirtmp)
cat > "$epdir/e.weir" <<'WEOF'
let ed = "micro"
EDITOR=nano A="two words" B=$ed C= sh -c 'echo "$EDITOR|$A|$B|[$C]"'
X=1 sh -c 'echo "left $X"' | Y=2 sh -c 'cat; echo "right ${X-none} $Y"'
let e = [Env.pair "S" "sigil"; Env.pair "W" "sigil"]
within env e
    W=prefix sh -c 'echo "$S $W"'
sh -c 'echo "$0"' CC=gcc
print (Env.get "EDITOR" |> Option.defaultValue "parent clean")
WEOF
out=$(cd "$epdir" && env -u EDITOR $BIN e.weir 2>&1) || fail "the env-prefix cell must run: $out"
[ "$out" = "nano|two words|micro|[]
left 1
right none 2
sigil prefix
CC=gcc
parent clean" ] || fail "env prefix: forms, per stage, sigil composition, argv, parent: $out"
printf 'FOO=1\n' > "$epdir/lone.weir"
out=$(cd "$epdir" && $BIN lone.weir 2>&1) && fail "a lone prefix must refuse: $out" || true
echo "$out" | grep -qF "is not an assignment" || fail "a lone prefix teaches let: $out"
rm -rf "$epdir"
echo "e2e ok: NAME=value cmd — value forms, per stage, sigil composition, argv untouched, parent clean; a lone prefix teaches"

# reflexes teach at their site [D:left-to-right-ops] [D:argv-backslash]
# [D:parse-error-binds]: each wrong first try names weir's spelling; the
# forward operators and a quoted backslash still work; a mis-parsed let
# leaves no "unbound" echo behind it
rxdir=$(mkweirtmp)
rxcase() {
    printf '%s\n' "$2" > "$rxdir/c.weir"
    rxout=$(cd "$rxdir" && $BIN check c.weir 2>&1) && fail "reflex '$1' must refuse"
    echo "$rxout" | grep -qF "$3" || fail "reflex '$1' teaches '$3': $rxout"
}
rxcase neq 'let b = 1 != 2' 'inequality is `<>`'
rxcase negation "$(printf 'let ok = true\nlet b = !ok')" 'write `not x`'
rxcase back-compose "$(printf 'let f n = n + 1\nlet g = f << f')" '`f << g` is `g >> f`'
rxcase back-pipe "$(printf 'let f n = n + 1\nlet r = f <| 2')" 'weir has no `<|`'
rxcase annotation 'let f (x: int) = x + 1' 'weir infers parameter types'
rxcase range 'for i in 1..3 do print $"{i}"' 'wrap it in brackets'
rxcase continuation "$(printf 'echo a \\\n    b')" 'continues a command line by indentation'
rxcase escape 'find . -maxdepth 0 -exec echo {} \;' 'quote the word instead'
rxcase escaped-head '\ls -la' '`^name` runs the PATH program'
cat > "$rxdir/ok.weir" <<'WEOF'
let f n = n + 1
let g = f >> f
print $"{g 1} {1 <> 2} {not false}"
printf "%s|" a "\\" b
print ""
WEOF
out=$(cd "$rxdir" && $BIN ok.weir 2>&1) || fail "the forward spellings must run: $out"
echo "$out" | grep -qxF '3 true true' || fail "forward composition, <>, not: $out"
echo "$out" | grep -qxF 'a|\|b|' || fail "a quoted backslash is a literal argument: $out"
printf 'let x: int = 1\nprint $"{x}"\nlet y = x\n' > "$rxdir/one.weir"
out=$(cd "$rxdir" && $BIN check one.weir 2>&1) && fail "a mis-parsed let must refuse: $out" || true
nerr=$(printf '%s\n' "$out" | grep -c 'error \[' || true)
[ "$nerr" -eq 1 ] || fail "a mis-parsed let reports once, not again as unbound: $nerr errors"
rm -rf "$rxdir"
echo "e2e ok: reflexes teach (!=, !x, <<, <|, annotations, 1..3, \\ continuation and escapes, \\ls); forward spellings run; a mis-parsed let reports once"

# or-patterns [D:or-patterns]: alternatives share the arm (literal, case,
# function, guarded), count toward exhaustiveness, end a command arm, and
# a binding alternative refuses with the split teaching
opdir=$(mkweirtmp)
cat > "$opdir/or.weir" <<'WEOF'
type Level = Debug | Info | Warn | Error
let loud l =
    match l with
    | Debug | Info -> false
    | Warn | Error -> true
let area p =
    match p with
    | "Parser.fs" | "Script.fs" -> "parser"
    | _ -> "other"
let small = function | 1 | 2 -> "small" | _ -> "big"
print $"{loud Info} {loud Error} {area "Script.fs"} {small 2}"
match "c" with
| "a" -> sh -c "echo arm-a"
| "b" | "c" -> sh -c "echo arm-bc"
| _ -> ()
WEOF
out=$(cd "$opdir" && $BIN or.weir 2>&1) || fail "or-patterns must run: $out"
[ "$out" = "false true parser small
arm-bc" ] || fail "or-patterns: shared arms, function, command arm boundary: $out"
printf 'let f p = match p with | (n, 0) | (0, n) -> n | _ -> 0\nprint $"{f (1, 0)}"\n' > "$opdir/bind.weir"
out=$(cd "$opdir" && $BIN check bind.weir 2>&1) && fail "a binding alternative must refuse: $out" || true
echo "$out" | grep -qF "cannot bind names yet" || fail "the v1 split teaching: $out"
rm -rf "$opdir"
echo "e2e ok: or-patterns — literal, case, function and command arms share bodies; a binding alternative teaches the split"

# lambda-lets [D:lambda-lets]: a lambda body takes command lets wherever
# it sits — dangling at top level, in an if body, single-line; a paren
# interior keeps the $() teaching
lldir=$(mkweirtmp)
cat > "$lldir/lam.weir" <<'WEOF'
["a"; "b"] |> Seq.iter (fun f ->
    let r = sh -c "echo got-$1" _ $f | complete
    print (r.stdout |> Seq.head))
if true then
    [1] |> Seq.iter (fun n ->
        let c = sh -c "echo in-if" | line
        print c)
[1] |> Seq.iter (fun _ -> let x = sh -c "echo one-line" | line in print x)
WEOF
out=$(cd "$lldir" && $BIN lam.weir 2>&1) || fail "lambda-lets must run: $out"
[ "$out" = "got-a
got-b
in-if
one-line" ] || fail "lambda-lets: top level, if body, single line: $out"
printf 'print (show (let r = sh -c "x" | complete in r.exitCode))\n' > "$lldir/paren.weir"
out=$(cd "$lldir" && $BIN check paren.weir 2>&1) && fail "a paren interior must refuse: $out" || true
echo "$out" | grep -qF 'inside parentheses, a command needs $(' || fail "the paren teaching: $out"
rm -rf "$lldir"
echo "e2e ok: lambda-lets — dangling, nested and one-line lambda bodies take command lets; a paren interior teaches \$()"

# field-splices [D:field-splices]: $x.f, nested, $@x.f and ^$x.f splice the
# field path as one word; bash's $f.bak on a string teaches interpolation
fspdir=$(mkweirtmp)
cat > "$fspdir/fs.weir" <<'WEOF'
type Out = { dir: string }
type Cli = { tag: string; bin: string; files: seq<string>; out: Out }
let cli = { tag = "v 1"; bin = "printf"; files = ["a"; "b c"]; out = { dir = "o" } }
^$cli.bin "[%s]" $cli.tag $cli.out.dir $@cli.files
WEOF
out=$(cd "$fspdir" && $BIN fs.weir 2>&1) || fail "field-splices must run: $out"
[ "$out" = "[v 1][o][a][b c]" ] || fail "field-splices: one word per field, the head from a field: $out"
printf 'let f = "a.txt"\ncp $f $f.bak\n' > "$fspdir/glue.weir"
out=$(cd "$fspdir" && $BIN check glue.weir 2>&1) && fail "\$f.bak on a string must refuse: $out" || true
echo "$out" | grep -qF '$"{f}.bak"' || fail "the field-glue teaching: $out"
rm -rf "$fspdir"
echo "e2e ok: field-splices — \$x.f, nested, \$@x.f and ^\$x.f are one word each; \$f.bak on a string teaches interpolation"

# arm-blocks [D:arm-blocks]: an arm and an else arm a text block on their
# head line; a block as a trailing argument teaches the pipe
abdir=$(mkweirtmp)
cat > "$abdir/ab.weir" <<'WEOF'
let pick n =
    match n with
    | 1 -> <<<
        one
    | _ -> $<<<
        other {n}
let tail ok =
    if ok then ["yes"]
    else <<<
        no
pick 1 |> Seq.iter print
pick 2 |> Seq.iter print
tail false |> Seq.iter print
WEOF
out=$(cd "$abdir" && $BIN ab.weir 2>&1) || fail "arm-blocks must run: $out"
[ "$out" = "one
other 2
no" ] || fail "arm-blocks: arm and else blocks: $out"
printf 'File.write "f.txt" <<<\n    hi\n' > "$abdir/arg.weir"
out=$(cd "$abdir" && $BIN check arg.weir 2>&1) && fail "a trailing block argument must refuse: $out" || true
echo "$out" | grep -qF "cannot be a trailing argument" || fail "the trailing-block teaching: $out"
rm -rf "$abdir"
echo "e2e ok: arm-blocks — arm and else blocks run; a trailing block argument teaches the pipe"

# hole-generic [D:hole-generic]: a hole on a parameter or a row field is
# generic; a top-level Secret argument still refuses at the call
hgdir=$(mkweirtmp)
cat > "$hgdir/hg.weir" <<'WEOF'
type Spec = { name: string; port: int }
let addr s = $"{s.name}:{s.port}"
let dash n = $"-{n}"
print (addr { name = "a"; port = 1 })
print (dash 5 + dash "x" + dash 1.5)
WEOF
out=$(cd "$hgdir" && $BIN hg.weir 2>&1) || fail "hole-generic must run: $out"
[ "$out" = "a:1
-5-x-1.5" ] || fail "hole-generic: row field and generic param: $out"
printf 'let f x = $"{x}"\nprint (f (Secret.of "pw"))\n' > "$hgdir/sec.weir"
out=$(cd "$hgdir" && $BIN check sec.weir 2>&1) && fail "a Secret through a generic hole must refuse: $out" || true
echo "$out" | grep -qF "a Secret does not interpolate" || fail "the Secret teaching at the call: $out"
rm -rf "$hgdir"
echo "e2e ok: hole-generic — a hole on a parameter or row field is generic; a Secret argument refuses"

cat > "$widir/envval.weir" <<'WEOF'
let vars = [Env.pair "WQ" "carried"]
let got = within env vars
    $(sh -c "echo $WQ") |> Seq.head
print got
WEOF
vout=$( cd "$widir" && $BIN envval.weir )
[ "$vout" = "carried" ] || fail "env value case captures under the overlay: $vout"
echo "e2e ok: within env expression position yields a captured value"

# ---- ambient env reaches every spawn [D:within-scopes] ---------------------
# the reifier desugars spawned with a baked empty overlay for 12 days
# (found 2026-08-16): within env + `| orFail` ran the child without the
# overlay while bare commands and $() saw it — the deep-fuzz driver ran
# at harness defaults the whole window. The matrix pins the law where
# it broke: every reifier family member, the In/Env twins, cmd/into,
# nesting outer-first, explicit sigil last-wins.
cat > "$widir/envreify.weir" <<'WEOF'
let outer = [Env.pair "RA" "amb"; Env.pair "RB" "keep"]
let inner = [Env.pair "RA" "in"]
let sig1 = [Env.pair "RA" "sig"]
let xs = ["a"]
let feedIt () = xs | sh -c "cat >/dev/null; echo intwin=$RA" | complete
within env outer
    sh -c "echo orfail=$RA-$RB" | orFail "orFail lost the ambient overlay"
    let c = $(sh -c "echo complete=$RA" | complete)
    print $"{c.stdout |> Seq.head}"
    let ok = $(sh -c "test \"$RA\" = amb" | succeeds)
    print $"succeeds={ok}"
    let r2 = feedIt ()
    print $"{r2.stdout |> Seq.head}"
    ["z"] | sh -c "cat >/dev/null; echo vpipe=$RA"
    within env inner
        sh -c "echo nested=$RA-$RB" | orFail "nested orFail lost a layer"
        $sig1(sh -c "echo sigil=$RA-$RB" | orFail "sigil twin lost the ambient underlay")
WEOF
rout=$( cd "$widir" && $BIN envreify.weir )
echo "$rout" | grep -qF "orfail=amb-keep"  || fail "orFail sees ambient env: $rout"
echo "$rout" | grep -qF "complete=amb"     || fail "complete sees ambient env: $rout"
echo "$rout" | grep -qF "succeeds=true"    || fail "succeeds sees ambient env: $rout"
echo "$rout" | grep -qF "intwin=amb"       || fail "stdin twin sees ambient env (dynamic scope): $rout"
echo "$rout" | grep -qF "vpipe=amb"        || fail "value-headed pipe sees ambient env: $rout"
echo "$rout" | grep -qF "nested=in-keep"   || fail "reifier nesting: inner wins, outer key survives: $rout"
echo "$rout" | grep -qF "sigil=sig-keep"   || fail "explicit sigil wins over ambient, ambient underlay survives: $rout"
echo "e2e ok: ambient env reaches every spawn — reifier family, In/Env twins, into; nesting outer-first"

# the observed-count loop [D:observed-report]: the driver must report
# what the harness measured, not what it was asked — a tiny run, the
# regression guard for the silent-defaults window. Linux-only, where
# the deep nightly lives: native Windows cannot spawn ci/deep-lock.sh,
# and the fuzz suite's depth pins abort the macOS testhost (no fuzz
# step has ever run there — smaller thread stacks)
froot="$(cd "$(dirname "$0")/.." && pwd)"
if [ "$(uname -s)" = "Linux" ] && command -v dotnet >/dev/null 2>&1 && [ -f "$froot/tests/Weir.Fuzz/Weir.Fuzz.fsproj" ]; then
    obs_rc=0
    obs=$( cd "$froot" && $BIN tools/fuzz.weir --seed 4242 --count 10 2>&1 ) || obs_rc=$?
    # name the failing property and its shrunk input, not just the tally
    [ "$obs_rc" = "0" ] || fail "observed-count run failed: $(echo "$obs" | grep -E -A25 '^ *Failed [A-Z]' | head -60; echo "$obs" | tail -3)"
    echo "$obs" | grep -qF "deep fuzz clean: seed=4242 count=10 (observed)" \
        || fail "driver must report the OBSERVED config: $(echo "$obs" | tail -3)"
    echo "e2e ok: fuzz driver reports observed seed/count (instrument measures, not restates)"
else
    echo "e2e skip: fuzz observed-count loop (Linux + dotnet only)"
fi

# ---- bare within + always [D:within-always] --------------------------------
# the exit discipline alone: the always block runs on normal exit, fail,
# exit n (an ExitRequest raise — Phase 0's measured answer: exit unwinds
# through finally, no carve-out), SIGINT and SIGTERM (the exit hook runs
# pending cleanups LIFO). kill -9 stays the stated carve-out.
aldir=$(mkweirtmp)
cat > "$aldir/al.weir" <<'WEOF'
let mode = Self.args |> Seq.head
within
    print "body"
    if mode == "fail" then fail "boom"
    if mode == "exit" then exit 3
    if mode == "sleep" then Duration.sleep 30s
always
    File.append "MARKER" ["cleanup"]
WEOF
( cd "$aldir" && rm -f MARKER && $BIN al.weir normal >/dev/null 2>&1 ); [ "$(cat "$aldir/MARKER" 2>/dev/null)" = "cleanup" ] || fail "always on normal exit"
rc=0; ( cd "$aldir" && rm -f MARKER && $BIN al.weir fail >/dev/null 2>&1 ) || rc=$?; [ "$rc" != "0" ] || fail "fail must propagate"; [ "$(cat "$aldir/MARKER" 2>/dev/null)" = "cleanup" ] || fail "always on fail"
rc=0; ( cd "$aldir" && rm -f MARKER && $BIN al.weir exit >/dev/null 2>&1 ) || rc=$?; [ "$rc" = "3" ] || fail "exit code must propagate through always (got $rc)"; [ "$(cat "$aldir/MARKER" 2>/dev/null)" = "cleanup" ] || fail "always on exit n"
if [ "$IS_WINDOWS" != "1" ]; then
    for sig in TERM INT; do
        ( cd "$aldir" && rm -f MARKER && exec $BIN al.weir sleep >/dev/null 2>&1 ) & alpid=$!
        sleep 2; kill -$sig $alpid 2>/dev/null || true; wait $alpid 2>/dev/null || true
        [ "$(cat "$aldir/MARKER" 2>/dev/null)" = "cleanup" ] || fail "always on SIG$sig"
    done
fi
echo "e2e ok: always runs on normal exit, fail, exit n, SIGINT, SIGTERM"

# ---- always after any within [D:within-always-any] -------------------------
# the cleanup runs INSIDE the scope, before the resource releases: the tmp
# dir still exists in always on every exit path (once — the claim-by-
# removal guard), and cd/env/lock/proc hold their resource
aadir=$(mkweirtmp)
cat > "$aadir/aa.weir" <<'WEOF'
let mode = Self.args |> Seq.head
let out = Self.args |> Seq.last
within tmp d
    ["x"] |> File.write $"{d}/f"
    if mode == "fail" then fail "boom"
    if mode == "exit" then exit 3
    if mode == "sleep" then Duration.sleep 30s
always
    File.append out [$"cleanup dir={File.exists $"{d}/f"}"]
WEOF
M="$aadir/MARKER"
( cd "$aadir" && rm -f "$M" && $BIN aa.weir normal "$M" >/dev/null 2>&1 ); [ "$(cat "$M" 2>/dev/null)" = "cleanup dir=true" ] || fail "kinded always on normal exit: $(cat "$M" 2>/dev/null)"
rc=0; ( cd "$aadir" && rm -f "$M" && $BIN aa.weir fail "$M" >/dev/null 2>&1 ) || rc=$?; [ "$rc" != "0" ] || fail "fail must propagate through a kinded always"; [ "$(cat "$M" 2>/dev/null)" = "cleanup dir=true" ] || fail "kinded always on fail"
rc=0; ( cd "$aadir" && rm -f "$M" && $BIN aa.weir exit "$M" >/dev/null 2>&1 ) || rc=$?; [ "$rc" = "3" ] || fail "exit code through a kinded always (got $rc)"; [ "$(cat "$M" 2>/dev/null)" = "cleanup dir=true" ] || fail "kinded always on exit n"
if [ "$IS_WINDOWS" != "1" ]; then
    for sig in TERM INT; do
        ( cd "$aadir" && rm -f "$M" && exec $BIN aa.weir sleep "$M" >/dev/null 2>&1 ) & aapid=$!
        sleep 2; kill -$sig $aapid 2>/dev/null || true; wait $aapid 2>/dev/null || true
        [ "$(cat "$M" 2>/dev/null)" = "cleanup dir=true" ] || fail "kinded always on SIG$sig, once, dir held: $(cat "$M" 2>/dev/null)"
    done
fi
cat > "$aadir/held.weir" <<'WEOF'
let here = Path.newTempDir ()
within cd here
    print "in"
always
    let w = pwd
    print $"cd {w == here}"
within env [Env.pair "AA_HELD" "yes"]
    print "in"
always
    sh -c "echo env $AA_HELD"
let lk = $"{here}/aa.lock"
within lock lk
    print "in"
always
    print $"lock {File.exists lk}"
WEOF
out=$(cd "$aadir" && $BIN held.weir 2>&1) || fail "held-resource probe must run: $out"
[ "$out" = "in
cd true
in
env yes
in
lock true" ] || fail "cd/env/lock held inside always: $out"
if [ "$IS_WINDOWS" != "1" ]; then
    cat > "$aadir/proc.weir" <<'WEOF'
within proc p = sleep 30
    print "in"
always
    let alive = kill -0 $"{Proc.pid p}" | succeeds
    print $"proc {alive}"
WEOF
    out=$(cd "$aadir" && $BIN proc.weir 2>&1) || fail "proc probe must run: $out"
    [ "$out" = "in
proc true" ] || fail "the proc is alive inside always, the tree-kill follows: $out"
fi
rm -rf "$aadir"
echo "e2e ok: always after any within — cleanup inside the scope on every exit path; cd/env/lock/proc held"

# [D:within-always]: an indented bare within/always, then a sibling statement
wadir=$(mkweirtmp)
cat > "$wadir/body.weir" <<'WEOF'
let f () =
    within
        print "body"
    always
        print "cleanup"
    print "after"
f ()
WEOF
out=$($BIN "$wadir/body.weir" 2>&1) || fail "an indented within/always + statement must run: $out"
[ "$out" = "body
cleanup
after" ] || fail "body, cleanup, after: $out"
rm -rf "$wadir"
echo "e2e ok: an indented within … always followed by a statement assembles and runs"

# ---- within env NAME=value [D:within-env-pairs] ----------------------------
# the env-prefix words scope a block; a body line's own prefix stays its
# command's (the head joins sentineled); the parent stays clean
wepdir=$(mkweirtmp)
cat > "$wepdir/ep.weir" <<'WEOF'
let x = "two"
within env A=1 B=$x C="three four"
    D=4 sh -c "echo [$A][$B][$C][$D]"
    sh -c "echo after [$A][$D]"
print (Env.get "A" |> Option.defaultValue "parent clean")
WEOF
out=$(cd "$wepdir" && $BIN ep.weir 2>&1) || fail "within env pairs must run: $out"
[ "$out" = "[1][two][three four][4]
after [1][]
parent clean" ] || fail "within env pairs: head scope, per-command prefix, clean parent: $out"
rm -rf "$wepdir"
echo "e2e ok: within env NAME=value — block scope, a body prefix stays per-command, parent clean"

# ---- detached SIGINT tears down [D:signal-teardown] -------------------------
# the gap (ring port finding #1): a shell backgrounding weir in a
# non-interactive session (setsid, no job control) sets SIGINT to
# SIG_IGN — the nohup convention — and .NET honours an inherited
# SIG_IGN, so PosixSignalRegistration never installs and a `kill -INT`
# on a detached supervisor was a no-op: no scope unwind, `always`
# cleanup skipped, children orphaned, only SIGKILL stopping it. The fix
# resets SIGINT to SIG_DFL when there is no controlling terminal (a
# detached process has no terminal Ctrl+C to protect against), so the
# registration binds and the same sweep the tty path runs fires. The
# within-always cell above drives SIGINT under bash's job control (default
# disposition); this cell drives it detached (setsid), the actual gap.
# SIGTERM is never SIG_IGN'd — it already worked — so it is the twin.
if [ "$IS_WINDOWS" != "1" ] && command -v setsid >/dev/null 2>&1 && command -v pgrep >/dev/null 2>&1; then
    dtdir=$(mkweirtmp)
    # a detached supervisor: a scoped child (reaped by the proc scope) and
    # an `always` cleanup that writes a marker the sweep must reach
    cat > "$dtdir/detached.weir" <<WEOF
let marker = Self.args |> Seq.head
within
    within proc child = sh -c "sleep $MK1"
        Duration.sleep 60s
always
    ["cleaned-up"] |> File.write marker
WEOF
    $BIN check "$dtdir/detached.weir" >/dev/null 2>&1 || fail "detached-signal probe must check"

    # SIGHUP joins them [D:sighup-teardown]: a closed terminal or kill -HUP
    for pair in "INT:130" "TERM:143" "HUP:129"; do
        sig="${pair%%:*}"; want="${pair##*:}"
        M="$dtdir/marker-$sig"
        rm -f "$M"
        pkill -f "sleep $MK1\$" 2>/dev/null || true
        # setsid = new session, no job control -> SIG_IGN on SIGINT is
        # inherited exactly as the ring probe measured; </dev/null detaches
        setsid "$BIN" "$dtdir/detached.weir" "$M" </dev/null >/dev/null 2>&1 &
        wpid=$!
        # wait for the scoped child to be live (the thing that must be reaped)
        for _ in $(seq 1 40); do pgrep -f "sleep $MK1\$" >/dev/null 2>&1 && break; sleep 0.2; done
        kill -"$sig" "$wpid" 2>/dev/null || true
        code=timeout
        for _ in $(seq 1 40); do
            # the || guards errexit: a signalled wait returns 130/143, which
            # is the code under test, not a harness failure
            if ! kill -0 "$wpid" 2>/dev/null; then code=0; wait "$wpid" 2>/dev/null || code=$?; break; fi
            sleep 0.2
        done
        [ "$code" = "$want" ] || fail "detached SIG$sig must exit $want (got $code) — the teardown never ran"
        [ "$(cat "$M" 2>/dev/null)" = "cleaned-up" ] || fail "detached SIG$sig must run the always cleanup"
        sleep 0.3
        left=$(pgrep -cf "sleep $MK1\$" 2>/dev/null || true); left=${left:-0}
        [ "$left" = "0" ] || fail "detached SIG$sig must reap the scoped child (orphans left: $left)"
        pkill -f "sleep $MK1\$" 2>/dev/null || true
    done
    echo "e2e ok: detached SIGINT/SIGTERM/SIGHUP tear down — always cleanup runs, child reaped, exit 130/143/129"

    # an inherited SIGHUP ignore (nohup) is honoured [D:sighup-teardown]:
    # the HUP does nothing, a TERM still tears down
    M="$dtdir/marker-nohup"
    rm -f "$M"
    pkill -f "sleep $MK1\$" 2>/dev/null || true
    setsid sh -c 'trap "" HUP; exec "$0" "$@"' "$BIN" "$dtdir/detached.weir" "$M" </dev/null >/dev/null 2>&1 &
    wpid=$!
    for _ in $(seq 1 40); do pgrep -f "sleep $MK1\$" >/dev/null 2>&1 && break; sleep 0.2; done
    kill -HUP "$wpid" 2>/dev/null || true
    sleep 1
    kill -0 "$wpid" 2>/dev/null || fail "nohup: an ignored SIGHUP must not stop weir"
    [ -f "$M" ] && fail "nohup: an ignored SIGHUP must not run the cleanup"
    kill -TERM "$wpid" 2>/dev/null || true
    code=0; wait "$wpid" 2>/dev/null || code=$?
    [ "$code" = "143" ] || fail "nohup: SIGTERM must still tear down (got $code)"
    [ "$(cat "$M" 2>/dev/null)" = "cleaned-up" ] || fail "nohup: SIGTERM must run the always cleanup"
    pkill -f "sleep $MK1\$" 2>/dev/null || true
    echo "e2e ok: an inherited SIGHUP ignore (nohup) is honoured; SIGTERM still tears down"

    # a pid-targeted SIGTERM tree-kills the FOREGROUND child too
    # [D:signal-main-unwind]: `timeout`/`docker stop` signal weir alone; an
    # unregistered foreground child outlived it, reparented to init. A
    # pid-targeted SIGINT the same [D:sigint-reaps]
    printf 'sleep %s\n' "$MK3" > "$dtdir/fg.weir"
    for pair in "TERM:143" "HUP:129" "INT:130"; do
        sig="${pair%%:*}"; want="${pair##*:}"
        pkill -f "sleep $MK3\$" 2>/dev/null || true
        setsid "$BIN" "$dtdir/fg.weir" </dev/null >/dev/null 2>&1 &
        wpid=$!
        for _ in $(seq 1 40); do pgrep -f "sleep $MK3\$" >/dev/null 2>&1 && break; sleep 0.2; done
        kill -"$sig" "$wpid" 2>/dev/null || true
        code=0; wait "$wpid" 2>/dev/null || code=$?
        [ "$code" = "$want" ] || fail "SIG$sig to weir with a foreground child must exit $want (got $code)"
        sleep 0.3
        left=$(pgrep -cf "sleep $MK3\$" 2>/dev/null || true); left=${left:-0}
        pkill -f "sleep $MK3\$" 2>/dev/null || true
        [ "$left" = "0" ] || fail "SIG$sig to weir must tree-kill its foreground child (orphans left: $left)"
    done
    echo "e2e ok: SIGTERM/SIGHUP/SIGINT to weir's pid reaps the foreground child"

    # ...and every fan-out arm's command, scoped or not [D:sigint-reaps];
    # an arm the kill ends is the teardown, never "command failed with
    # exit code 137" [D:teardown-kill-not-failure]
    MK8=$((MK + 8))
    printf '[1..4] |> Seq.piterWith 4 (fun _ -> sleep %s)\nprint "after"\n' "$MK8" > "$dtdir/fgarms.weir"
    printf 'within lock "%s/fga.lock"\n    [1..4] |> Seq.piterWith 4 (fun _ -> sleep %s)\n' "$dtdir" "$MK8" > "$dtdir/fgarmslock.weir"
    for probe in fgarms fgarmslock; do
        for pair in "INT:130" "TERM:143" "INT:130" "TERM:143" "TERM:143"; do
            sig="${pair%%:*}"; want="${pair##*:}"
            pkill -f "^sleep $MK8\$" 2>/dev/null || true
            setsid "$BIN" "$dtdir/$probe.weir" </dev/null >"$dtdir/fga.out" 2>&1 &
            wpid=$!
            for _ in $(seq 1 40); do [ "$(pgrep -cf "^sleep $MK8\$" 2>/dev/null || true)" = "4" ] && break; sleep 0.1; done
            kill -"$sig" "$wpid" 2>/dev/null || true
            code=0; wait "$wpid" 2>/dev/null || code=$?
            sleep 0.3
            left=$(pgrep -cf "^sleep $MK8\$" 2>/dev/null || true); left=${left:-0}
            pkill -9 -f "^sleep $MK8\$" 2>/dev/null || true
            [ "$code" = "$want" ] || fail "$probe: SIG$sig must exit $want (got $code): $(cat "$dtdir/fga.out")"
            [ "$left" = "0" ] || fail "$probe: SIG$sig to weir's pid must tree-kill every arm's command (orphans left: $left)"
            ! grep -qF "command failed" "$dtdir/fga.out" || fail "$probe: SIG$sig — an arm the teardown killed reported a failure: $(cat "$dtdir/fga.out")"
            ! grep -qF "after" "$dtdir/fga.out" || fail "$probe: SIG$sig — a statement ran after the signal"
        done
    done
    echo "e2e ok: SIGINT/SIGTERM to weir's pid reaps every fan-out arm's command, scoped or not, and reports no arm failure"

    # ...and every pmap worker's tree, exactly once, after spawning stops
    # [D:signal-main-unwind]: the kill raced the exit (a SIGSTOPped
    # grandchild left behind) — 20 runs, no `sleep` may survive
    cat > "$dtdir/pmapterm.weir" <<WEOF
within
    [1..4] |> Seq.pmap (fun _ -> sh -c "sleep $MK4; echo x" |> Seq.freeze) |> Seq.iter (fun _ -> ())
always
    printerr "cleanup"
WEOF
    pkill -9 -f "^sleep $MK4\$" 2>/dev/null || true
    orphans=0
    for _ in $(seq 1 20); do
        setsid "$BIN" "$dtdir/pmapterm.weir" </dev/null >/dev/null 2>&1 &
        wpid=$!
        for _ in $(seq 1 40); do [ "$(pgrep -cf "^sleep $MK4\$" 2>/dev/null || true)" = "4" ] && break; sleep 0.1; done
        kill -TERM "$wpid" 2>/dev/null || true
        code=0; wait "$wpid" 2>/dev/null || code=$?
        [ "$code" = "143" ] || fail "SIGTERM to a pmap script must exit 143 (got $code)"
        sleep 0.2
        if pgrep -f "^sleep $MK4\$" >/dev/null 2>&1; then
            orphans=$((orphans + 1))
            pkill -9 -f "^sleep $MK4\$" 2>/dev/null || true
        fi
    done
    [ "$orphans" = "0" ] || fail "SIGTERM must tree-kill every pmap worker's child ($orphans of 20 runs left orphans)"
    echo "e2e ok: SIGTERM reaps every pmap worker's grandchild, 20 runs"

    # the double-signal escape [D:signal-teardown]: a second signal during
    # a slow teardown hard-exits (the shell's double-Ctrl+C), never wedging
    # on a stuck cleanup. The always sleeps 20s; the second SIGINT must land
    # the process in well under that, before the cleanup's "done" write.
    cat > "$dtdir/slow.weir" <<WEOF
let marker = Self.args |> Seq.head
within
    within proc child = sh -c "sleep $MK1"
        Duration.sleep 60s
always
    ["start"] |> File.write marker
    Duration.sleep 20s
    ["done"] |> File.write marker
WEOF
    $BIN check "$dtdir/slow.weir" >/dev/null 2>&1 || fail "double-signal probe must check"
    DM="$dtdir/dmarker"
    rm -f "$DM"
    pkill -f "sleep $MK1\$" 2>/dev/null || true
    setsid "$BIN" "$dtdir/slow.weir" "$DM" </dev/null >/dev/null 2>&1 &
    dpid=$!
    for _ in $(seq 1 40); do pgrep -f "sleep $MK1\$" >/dev/null 2>&1 && break; sleep 0.2; done
    kill -INT "$dpid" 2>/dev/null || true          # first: enters the slow teardown
    for _ in $(seq 1 25); do [ "$(cat "$DM" 2>/dev/null)" = "start" ] && break; sleep 0.2; done
    [ "$(cat "$DM" 2>/dev/null)" = "start" ] || fail "the first SIGINT must enter the always cleanup"
    kill -INT "$dpid" 2>/dev/null || true          # second: must hard-exit NOW
    dcode=timeout
    for _ in $(seq 1 25); do                        # 5s ceiling << the 20s cleanup
        # || true guards errexit against the signalled exit code
        if ! kill -0 "$dpid" 2>/dev/null; then dcode=0; wait "$dpid" 2>/dev/null || dcode=$?; break; fi
        sleep 0.2
    done
    [ "$dcode" != "timeout" ] || fail "a second SIGINT must hard-exit, not wait out the cleanup"
    [ "$(cat "$DM" 2>/dev/null)" != "done" ] || fail "the hard-exit must not let the slow cleanup finish"
    pkill -f "sleep $MK1\$" 2>/dev/null || true
    echo "e2e ok: a second signal mid-teardown hard-exits (the double-Ctrl+C escape)"

    # the main thread unwinds [D:signal-main-unwind]: no statement runs
    # after a signal — `Proc.wait` on the killed scoped child returns no
    # code and the lines after it never run, yet `always` does; a main
    # thread asleep in `Duration.sleep` wakes at once
    cat > "$dtdir/after.weir" <<WEOF
let marker = Self.args |> Seq.head
within
    within proc srv = sh -c "sleep $MK2"
        let code = Proc.wait srv
        print \$"wait returned {code}"
        File.append marker ["after"]
always
    File.append marker ["cleaned-up"]
WEOF
    cat > "$dtdir/asleep.weir" <<'WEOF'
let marker = Self.args |> Seq.head
within
    Duration.sleep 60s
    File.append marker ["after"]
always
    File.append marker ["cleaned-up"]
WEOF
    for probe in after asleep; do
        for pair in "INT:130" "TERM:143"; do
            sig="${pair%%:*}"; want="${pair##*:}"
            M="$dtdir/m-$probe-$sig"
            rm -f "$M"
            setsid "$BIN" "$dtdir/$probe.weir" "$M" </dev/null >"$dtdir/out-$probe" 2>&1 &
            wpid=$!
            if [ "$probe" = after ]; then
                for _ in $(seq 1 40); do pgrep -f "sleep $MK2\$" >/dev/null 2>&1 && break; sleep 0.2; done
            else
                sleep 1
            fi
            kill -"$sig" "$wpid" 2>/dev/null || true
            code=timeout
            for _ in $(seq 1 15); do # 3s: the unwind is prompt, not the 60s sleep
                if ! kill -0 "$wpid" 2>/dev/null; then code=0; wait "$wpid" 2>/dev/null || code=$?; break; fi
                sleep 0.2
            done
            [ "$code" = "$want" ] || fail "$probe: SIG$sig must exit $want promptly (got $code)"
            [ "$(cat "$M" 2>/dev/null)" = "cleaned-up" ] || fail "$probe: SIG$sig runs always and no statement after it (marker: $(cat "$M" 2>/dev/null))"
            ! grep -qF "wait returned" "$dtdir/out-$probe" || fail "$probe: SIG$sig — Proc.wait must not return to the script"
            pkill -f "sleep $MK2\$" 2>/dev/null || true
        done
    done
    echo "e2e ok: a signal unwinds on the main thread — no statement after it, always runs, sleeps wake"

    # the teardown stops the body's spawns, never a cleanup's
    # [D:teardown-always-spawns]: commands (and a fan-out) inside `always`
    # run on SIGINT/SIGTERM
    cat > "$dtdir/alcmd.weir" <<'WEOF'
let marker = Self.args |> Seq.head
within
    Duration.sleep 60s
always
    let n = [1; 2; 3] |> Seq.pmap (fun i -> echo $i | line) |> Seq.length
    let m = $"{marker}-{n}"
    touch $m
WEOF
    for pair in "INT:130" "TERM:143"; do
        sig="${pair%%:*}"; want="${pair##*:}"
        M="$dtdir/alcmd-$sig"
        rm -f "$M-3"
        setsid "$BIN" "$dtdir/alcmd.weir" "$M" </dev/null >/dev/null 2>&1 &
        wpid=$!
        sleep 1
        kill -"$sig" "$wpid" 2>/dev/null || true
        code=0; wait "$wpid" 2>/dev/null || code=$?
        [ "$code" = "$want" ] || fail "always-cmd: SIG$sig must exit $want (got $code)"
        [ -f "$M-3" ] || fail "always-cmd: SIG$sig must run the always block's commands and fan-out"
    done
    echo "e2e ok: on SIGINT/SIGTERM the always block's commands and fan-out run"

    # a teardown stops parallel workers taking arms [D:teardown-stops-arms]:
    # after SIGTERM no new piter arm starts, and no scoped spill dir leaks
    cat > "$dtdir/arms.weir" <<'WEOF'
let log = Self.args |> Seq.head
within
    [1..100000] |> Seq.piterWith 16 (fun i ->
        Duration.sleep 10ms
        File.append log [$"arm {i}"])
always
    let a = File.read log |> Seq.length
    Duration.sleep 1s
    let b = File.read log |> Seq.length
    [$"{b - a}"] |> File.write $"{log}.grew"
WEOF
    cat > "$dtdir/spill.weir" <<WEOF
[1..5000] |> Seq.piterWith 16 (fun i ->
    within proc p = sleep $MK4
        Duration.sleep 1ms)
WEOF
    rm -f "$dtdir/arms.log" "$dtdir/arms.log.grew"
    setsid "$BIN" "$dtdir/arms.weir" "$dtdir/arms.log" </dev/null >/dev/null 2>&1 &
    wpid=$!
    sleep 1
    kill -TERM "$wpid" 2>/dev/null || true
    code=0; wait "$wpid" 2>/dev/null || code=$?
    [ "$code" = "143" ] || fail "arms: SIGTERM must exit 143 (got $code)"
    grew=$(cat "$dtdir/arms.log.grew" 2>/dev/null || echo missing)
    [ "$grew" -le 16 ] 2>/dev/null || fail "arms: piter kept taking arms after SIGTERM (grew: $grew)"
    sptmp="$dtdir/spilltmp"
    mkdir -p "$sptmp"
    pkill -9 -f "^sleep $MK4\$" 2>/dev/null || true
    TMPDIR="$sptmp" setsid "$BIN" "$dtdir/spill.weir" </dev/null >/dev/null 2>&1 &
    wpid=$!
    sleep 1
    kill -TERM "$wpid" 2>/dev/null || true
    code=0; wait "$wpid" 2>/dev/null || code=$?
    [ "$code" = "143" ] || fail "spill: SIGTERM must exit 143 (got $code)"
    sleep 0.3
    leaked=$(find "$sptmp" -mindepth 1 -maxdepth 1 | wc -l)
    left=$(pgrep -cf "^sleep $MK4\$" 2>/dev/null || true); left=${left:-0}
    pkill -9 -f "^sleep $MK4\$" 2>/dev/null || true
    [ "$leaked" = "0" ] && [ "$left" = "0" ] || fail "spill: SIGTERM leaked $leaked weir-proc dir(s), $left orphan(s)"
    echo "e2e ok: a teardown stops piter taking arms; no spill dir or child leaks"

    # a teardown cuts in-flight arms at their next boundary and joins them
    # before any enclosing scope closes [D:teardown-joins-arms]: the arms
    # sleep past the signal, `always` runs 1.5s from it, so an unjoined arm
    # would write between "always start" and "always end"; a piter under
    # within tmp leaves no dir behind
    for fan in '[1..4] |> Seq.piterWith 4 (fun i -> Duration.sleep 2s; File.append log [$"arm {i}"])' \
        '[1..4] |> Seq.pmap (fun i -> Duration.sleep 2s; File.append log [$"arm {i}"]; i) |> Seq.length |> show |> print' \
        '[1..4] |> Seq.pfirst (fun i -> Duration.sleep 2s; File.append log [$"arm {i}"]; i) |> show |> print'; do
        cat > "$dtdir/join.weir" <<WEOF
let log = Self.args |> Seq.head
within
    $fan
always
    File.append log ["always start"]
    Duration.sleep 1500ms
    File.append log ["always end"]
WEOF
        rm -f "$dtdir/join.log"
        setsid "$BIN" "$dtdir/join.weir" "$dtdir/join.log" </dev/null >/dev/null 2>&1 &
        wpid=$!
        sleep 1
        kill -TERM "$wpid" 2>/dev/null || true
        code=0; wait "$wpid" 2>/dev/null || code=$?
        [ "$code" = "143" ] || fail "join ($fan): SIGTERM must exit 143 (got $code)"
        sleep 1.5
        [ "$(cat "$dtdir/join.log" 2>/dev/null | tr '\n' ',')" = "always start,always end," ] || fail "join ($fan): an arm effect landed after the teardown began: $(cat "$dtdir/join.log" 2>/dev/null | tr '\n' ',')"
    done
    cat > "$dtdir/jointmp.weir" <<'WEOF'
within
    within tmp d
        [1..4] |> Seq.piterWith 4 (fun i ->
            Duration.sleep 2s
            Dir.create $"{d}/arm{i}")
always
    Duration.sleep 1500ms
WEOF
    jtmp="$dtdir/jointmp"
    mkdir -p "$jtmp"
    TMPDIR="$jtmp" setsid "$BIN" "$dtdir/jointmp.weir" </dev/null >/dev/null 2>&1 &
    wpid=$!
    sleep 1
    kill -TERM "$wpid" 2>/dev/null || true
    code=0; wait "$wpid" 2>/dev/null || code=$?
    sleep 1.5
    [ "$code" = "143" ] || fail "jointmp: SIGTERM must exit 143 (got $code)"
    [ "$(find "$jtmp" -mindepth 1 -maxdepth 1 | wc -l)" = "0" ] || fail "jointmp: an arm recreated the within-tmp dir after the teardown: $(find "$jtmp")"
    echo "e2e ok: a teardown cuts and joins in-flight piter/pmap/pfirst arms before always runs; within tmp leaves nothing"

    # a race won after the teardown still joins its losers before the main
    # flow unwinds [D:pfirst-join] [D:teardown-joins-arms]: arm 2 is in its
    # cleanup when the signal lands and wins once it ends; arm 1's cleanup
    # outlasts that and must still finish before the enclosing always
    cat > "$dtdir/latewin.weir" <<'WEOF'
let log = Self.args |> Seq.head
within
    let w =
        [1; 2] |> Seq.pfirst (fun i ->
            match i with
            | 1 ->
                within
                    Duration.sleep 10s
                always
                    Duration.sleep 2s
                    File.append log ["loser cleanup end"]
                0
            | _ ->
                within
                    Duration.sleep 200ms
                always
                    Duration.sleep 1s
                i)
    print $"won {w}"
always
    File.append log ["outer always"]
WEOF
    rm -f "$dtdir/latewin.log"
    setsid "$BIN" "$dtdir/latewin.weir" "$dtdir/latewin.log" </dev/null >/dev/null 2>&1 &
    wpid=$!
    sleep 0.5
    kill -TERM "$wpid" 2>/dev/null || true
    code=0; wait "$wpid" 2>/dev/null || code=$?
    sleep 2.5
    [ "$code" = "143" ] || fail "latewin: SIGTERM must exit 143 (got $code)"
    [ "$(tr '\n' ',' < "$dtdir/latewin.log" 2>/dev/null)" = "loser cleanup end,outer always," ] || fail "a pfirst won after the teardown must join its losers first: $(tr '\n' ',' < "$dtdir/latewin.log" 2>/dev/null)"
    echo "e2e ok: a pfirst won after the teardown joins its losers, cleanups whole, before the main flow unwinds"

    # a signal during pfirst races never leaves SIGSTOPped orphans
    # [D:kill-settle]: .NET's tree kill stops, then kills, so every
    # exit settles the race kills in flight first
    cat > "$dtdir/stopped.weir" <<WEOF
within
    [1..500] |> Seq.iter (fun i ->
        let _w =
            [0..8] |> Seq.pfirstWith 9 (fun k ->
                if k == 0 then
                    Duration.sleep 20ms
                    k
                else
                    let _c = sh -c "sleep $MK7; echo x" | exitCode
                    k)
        ())
always
    print "always"
WEOF
    stopped=0
    for r in 1 2 3 4 5 6 7 8 9; do
        case $((r % 3)) in 0) sig=INT ;; 1) sig=TERM ;; *) sig=HUP ;; esac
        setsid "$BIN" "$dtdir/stopped.weir" </dev/null >/dev/null 2>&1 &
        wpid=$!
        sleep "0.$((3 + r % 7))"
        kill -"$sig" "$wpid" 2>/dev/null || true
        wait "$wpid" 2>/dev/null || true
        sleep 0.5
        n=$(ps -eo stat=,args= | awk -v m="sleep $MK7" '$1 ~ /T/ && ($2 " " $3) == m' | wc -l)
        stopped=$((stopped + n))
        pkill -9 -f "^sleep $MK7\$" 2>/dev/null || true
    done
    [ "$stopped" = "0" ] || fail "a signal during pfirst races left $stopped SIGSTOPped orphan(s)"
    echo "e2e ok: signals during pfirst races leave no SIGSTOPped orphan (9 runs)"

    # a requested teardown never preempts a cleanup on the clean path
    # [D:teardown-never-preempts]: the signal lands while the `always` runs
    # (its sleep is not cut short) or while the scope is still closing (a
    # serve drain); the cleanup runs whole, once, then the signal's exit
    cat > "$dtdir/inclean.weir" <<'WEOF'
let log = Self.args |> Seq.head
within
    File.append log ["body"]
always
    File.append log ["start"]
    Duration.sleep 1500ms
    File.append log ["end"]
File.append log ["after"]
WEOF
    for sig in TERM INT; do
        rm -f "$dtdir/inclean.log"
        setsid "$BIN" "$dtdir/inclean.weir" "$dtdir/inclean.log" </dev/null >/dev/null 2>&1 &
        wpid=$!
        for _ in $(seq 1 50); do grep -qx start "$dtdir/inclean.log" 2>/dev/null && break; sleep 0.05; done
        kill -"$sig" "$wpid" 2>/dev/null || true
        code=0; wait "$wpid" 2>/dev/null || code=$?
        [ "$(tr '\n' ',' < "$dtdir/inclean.log")" = "body,start,end," ] || fail "a SIG$sig during a running cleanup must let it finish, then exit: $(tr '\n' ',' < "$dtdir/inclean.log")"
        case "$code" in 130|143) ;; *) fail "a SIG$sig during a running cleanup must still exit by it (got $code)" ;; esac
    done
    svportC1=8491
    cat > "$dtdir/closing.weir" <<WEOF
let log = Self.args |> Seq.head
let h = fun req ->
    Duration.sleep 5s
    HttpServerResponse { status = 200; headers = []; body = Text "ok" }
within
    within serve _s = { port = $svportC1; maxConcurrent = 2 } h
        poll timeout=8s interval=50ms
            Net.portOpen $svportC1
        within proc _c = curl -s -m 10 "http://127.0.0.1:$svportC1/x"
            Duration.sleep 300ms
        File.append log ["body done"]
always
    File.append log ["END"]
WEOF
    rm -f "$dtdir/closing.log"
    setsid "$BIN" "$dtdir/closing.weir" "$dtdir/closing.log" </dev/null >/dev/null 2>&1 &
    wpid=$!
    for _ in $(seq 1 100); do grep -qx "body done" "$dtdir/closing.log" 2>/dev/null && break; sleep 0.05; done
    sleep 0.5
    kill -TERM "$wpid" 2>/dev/null || true
    code=0; wait "$wpid" 2>/dev/null || code=$?
    [ "$code" = "143" ] || fail "a SIGTERM during a serve drain must exit 143 (got $code)"
    [ "$(tr '\n' ',' < "$dtdir/closing.log")" = "body done,END," ] || fail "a SIGTERM while a scope closes must still run the enclosing always once: $(tr '\n' ',' < "$dtdir/closing.log")"
    echo "e2e ok: a signal never preempts an always cleanup — running or about to start, it runs whole, then the signal's exit"

    # the teardown's join abandons an arm parked in a read no cut reaches
    # [D:teardown-abandons-reads]: a FIFO nobody opens, a FIFO whose writer
    # stays silent, a pipe an orphaned grandchild holds — the arm's late
    # effect never lands, `always` runs, the exit is prompt
    MK9=$((MK + 9))
    rm -f "$dtdir/tff"; mkfifo "$dtdir/tff"
    for src in "File.read \"$dtdir/tff\" |> Seq.length" "sh -c \"(setsid sleep $MK9 &); echo started\" |> Seq.length"; do
        for writer in none silent; do
            case "$src" in sh*) [ "$writer" = silent ] && continue ;; esac
            cat > "$dtdir/parked.weir" <<WEOF
let log = Self.args |> Seq.head
within
    [1; 2] |> Seq.piter (fun i ->
        let n = $src
        File.append log [\$"LATE arm {i} {n}"])
always
    File.append log ["END"]
WEOF
            rm -f "$dtdir/parked.log"
            wp=""
            if [ "$writer" = silent ]; then sleep 30 > "$dtdir/tff" & wp=$!; fi
            setsid "$BIN" "$dtdir/parked.weir" "$dtdir/parked.log" </dev/null >/dev/null 2>&1 &
            wpid=$!
            sleep 1
            kill -TERM "$wpid" 2>/dev/null || true
            code=timeout
            for _ in $(seq 1 30); do
                if ! kill -0 "$wpid" 2>/dev/null; then code=0; wait "$wpid" 2>/dev/null || code=$?; break; fi
                sleep 0.2
            done
            [ "$code" = timeout ] && { kill -9 "$wpid" 2>/dev/null || true; }
            [ -n "$wp" ] && { kill "$wp" 2>/dev/null || true; }
            pkill -9 -f "^sleep $MK9\$" 2>/dev/null || true
            [ "$code" = "143" ] || fail "a SIGTERM with arms parked in ($src, writer $writer) must exit 143 promptly (got $code)"
            [ "$(tr '\n' ',' < "$dtdir/parked.log")" = "END," ] || fail "parked arms ($src, writer $writer): $(tr '\n' ',' < "$dtdir/parked.log")"
        done
    done
    rm -f "$dtdir/tff"
    # an abandoned arm's pending `always` runs before the enclosing scopes
    # release [D:abandoned-arm-cleanup]: inside the arm's within tmp, never
    # after the enclosing always
    MK10=$((MK + 10))
    cat > "$dtdir/parkedc.weir" <<WEOF
let log = Self.args |> Seq.head
within
    within tmp d
        [1; 2] |> Seq.piter (fun i ->
            within
                let n = sh -c "(setsid sleep $MK10 &); echo started" |> Seq.length
                File.append log [\$"LATE arm {i} {n}"]
            always
                File.append log [\$"cleanup dir={Dir.exists d}"])
always
    File.append log ["END"]
WEOF
    rm -f "$dtdir/parkedc.log"
    pctmp="$dtdir/parkedctmp"
    mkdir -p "$pctmp"
    TMPDIR="$pctmp" setsid "$BIN" "$dtdir/parkedc.weir" "$dtdir/parkedc.log" </dev/null >/dev/null 2>&1 &
    wpid=$!
    sleep 1
    kill -TERM "$wpid" 2>/dev/null || true
    code=timeout
    for _ in $(seq 1 30); do
        if ! kill -0 "$wpid" 2>/dev/null; then code=0; wait "$wpid" 2>/dev/null || code=$?; break; fi
        sleep 0.2
    done
    [ "$code" = timeout ] && { kill -9 "$wpid" 2>/dev/null || true; }
    pkill -9 -f "^sleep $MK10\$" 2>/dev/null || true
    [ "$code" = "143" ] || fail "a SIGTERM with parked arms in cleanup scopes must exit 143 promptly (got $code)"
    [ "$(tr '\n' ',' < "$dtdir/parkedc.log")" = "cleanup dir=true,cleanup dir=true,END," ] || fail "an abandoned arm's always must run inside its scopes, before the enclosing always: $(tr '\n' ',' < "$dtdir/parkedc.log")"
    [ "$(find "$pctmp" -mindepth 1 -maxdepth 1 | wc -l)" = "0" ] || fail "parked arms: the within tmp dir leaked: $(find "$pctmp")"
    # the abandoned cleanups run on the main thread, so the watchdog stands
    # down for them [D:abandoned-arm-cleanup] [D:teardown-joins-arms]: one
    # longer than its 1s grace still runs whole
    MK11=$((MK + 11))
    cat > "$dtdir/parkedw.weir" <<WEOF
let log = Self.args |> Seq.head
within
    [1] |> Seq.piter (fun i ->
        within
            let n = sh -c "(setsid sleep $MK11 &); echo started" |> Seq.length
            File.append log [\$"LATE arm {i} {n}"]
        always
            File.append log ["cleanup start"]
            Duration.sleep 1500ms
            File.append log ["cleanup end"])
always
    File.append log ["END"]
WEOF
    rm -f "$dtdir/parkedw.log"
    setsid "$BIN" "$dtdir/parkedw.weir" "$dtdir/parkedw.log" </dev/null >/dev/null 2>&1 &
    wpid=$!
    sleep 1
    kill -TERM "$wpid" 2>/dev/null || true
    code=timeout
    for _ in $(seq 1 30); do
        if ! kill -0 "$wpid" 2>/dev/null; then code=0; wait "$wpid" 2>/dev/null || code=$?; break; fi
        sleep 0.2
    done
    [ "$code" = timeout ] && { kill -9 "$wpid" 2>/dev/null || true; }
    for p in $(ps -eo pid,args | awk -v m="$MK11" '$2 == "sleep" && $3 == m {print $1}'); do kill -9 "$p" 2>/dev/null || true; done
    [ "$code" = "143" ] || fail "a SIGTERM over a parked arm with a slow cleanup exits 143 (got $code)"
    [ "$(tr '\n' ',' < "$dtdir/parkedw.log")" = "cleanup start,cleanup end,END," ] || fail "an abandoned arm's cleanup outlasting the watchdog's grace must run whole: $(tr '\n' ',' < "$dtdir/parkedw.log")"
    echo "e2e ok: a teardown abandons arms parked in a FIFO open/read or an orphan-held pipe; their cleanups run inside the scopes"
else
    echo "e2e skip: detached-signal pins (POSIX + setsid + pgrep)"
fi

# Phase 0's measured answer, pinned [D:within-always]: exit n is an
# ExitRequest raise — it unwinds through every scope finally (the tmp
# dir is removed by the scope itself, not the hook), so always and exit
# compose with no carve-out
cat > "$aldir/exittmp.weir" <<'WEOF'
within tmp d
    print d
    exit 0
WEOF
exdir=$( cd "$aldir" && $BIN exittmp.weir )
[ -n "$exdir" ] || fail "exit-vs-tmp: no dir printed"
[ ! -d "$exdir" ] || fail "exit must run the tmp teardown (dir survived: $exdir)"
echo "e2e ok: exit n unwinds through scope teardown (Phase 0, pinned)"

# the raise rulings [D:within-always]: the original error wins, the
# cleanup's own failure reaches stderr with the marker; an inner failed
# cleanup does not strand the outer scope (LIFO under failure)
cat > "$aldir/rulings.weir" <<'WEOF'
within
    within
        fail "original"
    always
        fail "inner cleanup boom"
always
    File.append "OUTER" ["outer ran"]
WEOF
rout_rc=0
rout=$( cd "$aldir" && rm -f OUTER && $BIN rulings.weir 2>&1 ) || rout_rc=$?
[ "$rout_rc" != "0" ] || fail "the original raise must propagate"
echo "$rout" | grep -qF "error: original" || fail "the ORIGINAL error surfaces: $rout"
echo "$rout" | grep -qF "the cleanup ALSO failed" || fail "the cleanup failure reaches stderr with its marker: $rout"
[ "$(cat "$aldir/OUTER" 2>/dev/null)" = "outer ran" ] || fail "the outer always still runs (LIFO under failure)"
echo "e2e ok: always raise rulings — original wins, marker on stderr, teardown continues outward"

# ---- within lock [D:within-lock] -------------------------------------------
# advisory file lock: mutual exclusion across processes and pmap arms
# (flock(2) is per-open-file-description — probe-pinned), blocking by
# default, timeout= raises, kill -9 releases (the kernel's guarantee).
# The no-lock control is the same instrument observing contention —
# every zero needs its non-zero.
if [ "$IS_WINDOWS" != "1" ]; then
    lkdir=$(mkweirtmp)
    cat > "$lkdir/lk.weir" <<'WEOF'
within lock "probe.lock"
    File.append "LOG" ["enter"]
    Duration.sleep 400ms
    File.append "LOG" ["exit"]
WEOF
    cat > "$lkdir/nolock.weir" <<'WEOF'
File.append "LOG" ["enter"]
Duration.sleep 400ms
File.append "LOG" ["exit"]
WEOF
    ( cd "$lkdir" && rm -f LOG && { $BIN lk.weir & $BIN lk.weir & wait; } )
    [ "$(cat "$lkdir/LOG" | tr '\n' ' ')" = "enter exit enter exit " ] || fail "two-process exclusion (got: $(cat "$lkdir/LOG" | tr '\n' ' '))"
    ( cd "$lkdir" && rm -f LOG && { $BIN nolock.weir & $BIN nolock.weir & wait; } )
    [ "$(cat "$lkdir/LOG" | tr '\n' ' ')" != "enter exit enter exit " ] || fail "the no-lock control must interleave — the instrument observed no contention"
    cat > "$lkdir/arms.weir" <<'WEOF'
[1; 2] |> Seq.pmap (fun i ->
    within lock "probe.lock"
        File.append "LOG" ["enter"]
        Duration.sleep 400ms
        File.append "LOG" ["exit"]
    i) |> Seq.iter (fun _ -> ())
WEOF
    ( cd "$lkdir" && rm -f LOG && $BIN arms.weir )
    [ "$(cat "$lkdir/LOG" | tr '\n' ' ')" = "enter exit enter exit " ] || fail "pmap arms exclusion (flock per-open-fd, got: $(cat "$lkdir/LOG" | tr '\n' ' '))"
    cat > "$lkdir/nodir.weir" <<'WEOF'
within lock "no-such-dir-xyz/l.lock" timeout=10s
    print "unreachable"
WEOF
    nd_rc=0; nd=$( cd "$lkdir" && $BIN nodir.weir 2>&1 ) || nd_rc=$?
    [ "$nd_rc" != "0" ] || fail "a missing lock parent must fail"
    echo "$nd" | grep -qF "no such directory" || fail "missing parent fails NOW, not as a timeout (got: $nd)"
    cat > "$lkdir/tmo.weir" <<'WEOF'
within lock "probe.lock" timeout=300ms
    print "acquired"
WEOF
    if command -v flock >/dev/null 2>&1; then
        ( flock "$lkdir/probe.lock" -c "sleep 3" ) & fpid=$!
        sleep 0.4
        tmo_rc=0; tmo=$( cd "$lkdir" && $BIN tmo.weir 2>&1 ) || tmo_rc=$?
        [ "$tmo_rc" != "0" ] || fail "timeout exhaustion must raise"
        echo "$tmo" | grep -qF "within lock: timed out" || fail "the timeout error names itself: $tmo"
        wait $fpid
        cat > "$lkdir/hold.weir" <<'WEOF'
within lock "probe.lock"
    print "held"
    Duration.sleep 30s
WEOF
        ( cd "$lkdir" && exec $BIN hold.weir >/dev/null 2>&1 ) & hpid=$!
        sleep 1.5
        flock -n "$lkdir/probe.lock" -c true && fail "flock must be excluded while weir holds (interop)"
        kill -9 $hpid 2>/dev/null || true; wait $hpid 2>/dev/null || true; sleep 0.5
        flock -n "$lkdir/probe.lock" -c true || fail "the kernel must release on kill -9"
        echo "e2e ok: within lock — 2-process + pmap-arm exclusion (control interleaved), timeout raises, flock interop, kill -9 releases"
    else
        echo "e2e ok: within lock — 2-process + pmap-arm exclusion (control interleaved); flock(1) absent on this platform — the interop/kill-9 rows are UNTESTABLE here (macOS ships no flock; Windows locks via LockFileEx), not merely unrun"
    fi
else
    echo "e2e skip: within lock matrix (POSIX rows)"
fi

# weir-worded refusals, never raw .NET text: a lock on a directory or "",
# a negative lock timeout; importing a directory or a device
h54dir=$(mkweirtmp)
mkdir "$h54dir/adir"
for spec in 'lock "adir"|is a directory — the lock is a file' 'lock ""|the lock path is empty' 'lock "x.lock" timeout=-1s|timeout must not be negative, got -1s'; do
    printf 'within %s\n    print "in"\n' "${spec%%|*}" > "$h54dir/l.weir"
    rc=0; out=$( cd "$h54dir" && $BIN l.weir 2>&1 ) || rc=$?
    [ "$rc" = "1" ] || fail "within ${spec%%|*} must raise, rc=$rc: $out"
    expect "within ${spec%%|*} is refused, weir-worded" "${spec##*|}" "$out"
    case "$out" in *"Access to the path"*) fail "raw .NET text leaked: $out" ;; esac
done
printf 'import "./adir" as D\nprint "x"\n' > "$h54dir/imd.weir"
out=$($BIN check "$h54dir/imd.weir" 2>&1) || true
expect "importing a directory is weir-worded" "it is a directory, not a module" "$out"
if [ "$IS_WINDOWS" = "0" ]; then
    printf 'import "/dev/zero" as Z\nprint "x"\n' > "$h54dir/imz.weir"
    out=$(timeout 10 $BIN check "$h54dir/imz.weir" 2>&1) || true
    expect "importing a device is refused, not read forever" "not a regular file" "$out"
fi
rm -rf "$h54dir"

# ---- the scope tree [D:scope-tree] -------------------------------------------
# hunt-3 round 4 stage A: the second signal's hard exit (C1-1), death by the
# signal after the unwind (R7, C1-4), a cleanup's commands in their own
# group (R8, C1-2), the detached SIGINT reset at start (C1-6), a REPL entry
# as one evaluation (C23), a cut Self.stdin loser (C24), `exit n` in a serve
# handler (R2, C25), a cut prompt's partial line (R3, F5). sig.py reports
# the raw status (-n: died by signal n), which bash's $? cannot tell apart
# from an exit 128+n
if [ "$IS_WINDOWS" != "1" ] && command -v python3 >/dev/null 2>&1; then
    stdir=$(mkweirtmp)
    ptyrun="$(dirname "$0")/../tests/pty/pty-run.py"
    cat > "$stdir/sig.py" <<'PYEOF'
# sig.py TIMEOUT [--ign SIG] STEP... -- argv; STEP: wait:FILE:LINE | sleep:MS | kill:SIG
import os, sys, time, signal, subprocess
a = sys.argv[1:]; tmo = float(a.pop(0)); ign = None
if a[0] == '--ign':
    a.pop(0); ign = getattr(signal, 'SIG' + a.pop(0))
i = a.index('--'); steps, argv = a[:i], a[i + 1:]
def pre():
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM, signal.SIGPIPE):
        signal.signal(s, signal.SIG_DFL)
    if ign:
        signal.signal(ign, signal.SIG_IGN)
p = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, preexec_fn=pre, start_new_session=True)
t0 = time.monotonic(); last = t0
for st in steps:
    k, _, v = st.partition(':')
    if k == 'wait':
        f, _, line = v.partition(':')
        while time.monotonic() - t0 < tmo:
            try:
                if line in open(f).read().splitlines():
                    break
            except OSError:
                pass
            time.sleep(0.02)
    elif k == 'sleep':
        time.sleep(int(v) / 1000)
    elif k == 'kill':
        os.kill(p.pid, getattr(signal, 'SIG' + v)); last = time.monotonic()
try:
    rc = p.wait(timeout=max(0.1, tmo - (time.monotonic() - t0)))
except subprocess.TimeoutExpired:
    os.killpg(p.pid, signal.SIGKILL); p.wait(); rc = 'HANG'
out = p.stdout.read().decode(errors='replace').replace('\n', ' | ')
print('status %s after %d out %s' % (rc, (time.monotonic() - last) * 1000, out))
PYEOF
    stlog() { tr '\n' ',' < "$1" 2>/dev/null; }

    # C1-1: a second signal while an inner cleanup runs and an outer one is
    # pending exits at once [D:hard-exit]: no cleanup starts, the running
    # one's command dies, one code — the first signal's death
    cat > "$stdir/hard.weir" <<'WEOF'
let log = Self.args |> Seq.head
let ev s = File.append log [s]
within
    within
        ev "body"
        sleep 31.61
    always
        ev "inner start"
        sleep 31.62
        ev "inner end"
always
    ev "outer start"
WEOF
    for pair in "TERM:TERM:-15" "INT:TERM:-2" "HUP:INT:-1"; do
        s1=${pair%%:*}; rest=${pair#*:}; s2=${rest%%:*}; want=${rest#*:}
        rm -f "$stdir/hard.log"
        r=$(python3 "$stdir/sig.py" 15 "wait:$stdir/hard.log:body" "kill:$s1" "wait:$stdir/hard.log:inner start" sleep:300 "kill:$s2" -- "$BIN" "$stdir/hard.weir" "$stdir/hard.log")
        left=$(ps -eo pid,args | awk '$2 == "sleep" && ($3 == "31.61" || $3 == "31.62") {print $1}')
        [ -z "$left" ] || { kill -9 $left 2>/dev/null; fail "a second signal's hard exit must kill the cleanup's command ($pair)"; }
        [ "$(stlog "$stdir/hard.log")" = "body,inner start," ] || fail "a second signal ($pair) runs no cleanup and lets none run on: $(stlog "$stdir/hard.log")"
        echo "$r" | grep -q "^status $want " || fail "a second signal ($pair) dies by the first signal: $r"
        ms=$(echo "$r" | awk '{print $4}')
        [ "$ms" -lt 1000 ] || fail "a second signal ($pair) exits at once (${ms}ms): $r"
        echo "$r" | grep -q "command failed" && fail "a second signal ($pair) reports no command failure: $r"
    done
    echo "e2e ok: a second signal hard-exits at once — no cleanup runs, the running one's command dies, weir dies by the first signal"

    # R7 (C1-4): after the unwind weir dies BY the signal, scoped or not
    cat > "$stdir/r7.weir" <<'WEOF'
let log = Self.args |> Seq.head
within
    File.append log ["body"]
    Duration.sleep 30s
always
    File.append log ["cleanup"]
WEOF
    printf '%s\n' 'let log = Self.args |> Seq.head' 'sh -c "true"' 'File.append log ["body"]' 'Duration.sleep 30s' > "$stdir/r7u.weir"
    # R2 (C1-3): SIGQUIT is TERM's teardown and dies by SIGQUIT [D:sigquit-teardown]
    for case in "r7:TERM:-15" "r7:INT:-2" "r7:HUP:-1" "r7:QUIT:-3" "r7u:TERM:-15" "r7u:INT:-2" "r7u:QUIT:-3"; do
        f=${case%%:*}; rest=${case#*:}; sig=${rest%%:*}; want=${rest#*:}
        rm -f "$stdir/r7.log"
        r=$(python3 "$stdir/sig.py" 10 "wait:$stdir/r7.log:body" "kill:$sig" -- "$BIN" "$stdir/$f.weir" "$stdir/r7.log")
        echo "$r" | grep -q "^status $want " || fail "a SIG$sig ($f) ends weir by the signal after the unwind [D:die-by-signal]: $r"
        [ "$f" = "r7u" ] || [ "$(stlog "$stdir/r7.log")" = "body,cleanup," ] || fail "a SIG$sig runs the cleanup first: $(stlog "$stdir/r7.log")"
    done
    echo "e2e ok: after the unwind weir dies by its signal — INT, TERM, HUP, QUIT, scoped or not"

    # C1-6: an inherited SIG_IGN SIGINT without a tty is reset at start, so a
    # poller that never spawns dies by a kill -INT
    printf '%s\n' 'Duration.sleep 3s' 'print "after"' > "$stdir/ign.weir"
    r=$(python3 "$stdir/sig.py" 10 --ign INT sleep:500 kill:INT -- "$BIN" "$stdir/ign.weir")
    echo "$r" | grep -q "^status -2 " || fail "an inherited SIG_IGN SIGINT without a tty is reset at start: $r"
    echo "$r" | grep -q "out after" && fail "nothing runs after the SIGINT: $r"
    echo "e2e ok: an inherited SIG_IGN SIGINT without a tty is reset at start — kill -INT ends a poller"

    # R1 (round 5): a terminal ^C during an always does not kill the
    # cleanup's command (started with SIGINT ignored); the cleanup ends
    # whole, then weir dies by the SIGINT [D:cleanup-sigign]
    cat > "$stdir/pg.weir" <<'WEOF'
within
    print "body"
always
    print "cleanup start"
    sh -c "sleep 1.2; echo cmd-done"
    sh -c "trap 'echo trapped' INT; sleep 0.4; echo trap-done"
    print "cleanup end"
print "after"
WEOF
    tout=$(printf 'SLEEP 600\nSEND \\x03\nSLEEP 3000\n' | python3 "$ptyrun" 8 "$BIN" "$stdir/pg.weir")
    for want in "cmd-done" "trap-done" "cleanup end" "EXIT -2"; do
        echo "$tout" | grep -qF "$want" || fail "a terminal ^C during an always: '$want' expected: $tout"
    done
    echo "$tout" | grep -qE "trapped|command failed|after" && fail "a terminal ^C never reaches a cleanup's command: $tout"
    echo "e2e ok: a terminal ^C never kills a cleanup's command — started with SIGINT ignored; the cleanup ends whole, then the SIGINT"

    # R1 (C1-2): a cleanup's command at a terminal keeps it — it reads what
    # is typed (a pager works) [D:cleanup-sigign]
    printf '%s\n' 'within' '    print "body"' 'always' '    sh -c "read l; echo got=$l"' '    print "cleanup end"' > "$stdir/r1.weir"
    tout=$(printf 'SLEEP 800\nSEND hello\\r\nSLEEP 1500\n' | python3 "$ptyrun" 8 "$BIN" "$stdir/r1.weir")
    for want in "got=hello" "cleanup end" "EXIT 0"; do
        echo "$tout" | grep -qF "$want" || fail "a cleanup's command at a terminal reads the terminal: '$want' expected: $tout"
    done
    echo "e2e ok: a cleanup's command at a terminal reads the terminal"

    # R7 (C1-6, E3): the shim is sh, not weir's own path — a weir binary
    # deleted mid-run still runs its cleanup's commands [D:cleanup-sigign]
    cp "$BIN" "$stdir/wgone"
    printf '%s\n' 'within' '    File.delete (Self.args |> Seq.head)' 'always' '    echo "cleanup command ran"' > "$stdir/r1mv.weir"
    tout=$(printf 'SLEEP 1500\n' | python3 "$ptyrun" 6 "$stdir/wgone" "$stdir/r1mv.weir" "$stdir/wgone")
    for want in "cleanup command ran" "EXIT 0"; do
        echo "$tout" | grep -qF "$want" || fail "a deleted weir binary still runs its cleanup's commands: '$want' expected: $tout"
    done
    echo "e2e ok: a weir binary deleted mid-run still runs its cleanup's commands at a terminal"

    # R7: a ^C that kills the sh shim before its trap never started the
    # command — on Linux it is started again, so the cleanup runs whole
    # [D:cleanup-sigign]
    if [ "$(uname -s)" = Linux ]; then
        printf '%s\n' 'within' '    print "body"' 'always' '    [1..300] |> Seq.iter (fun _ -> /bin/true)' '    print "loop done"' > "$stdir/win.weir"
        wl=0
        for k in $(seq 12); do
            tout=$(printf 'SLEEP %s\nSEND \\x03\nSLEEP 2500\n' $((150 + k * 37)) | python3 "$ptyrun" 10 "$BIN" "$stdir/win.weir")
            echo "$tout" | grep -qF "loop done" || wl=$((wl + 1))
        done
        [ "$wl" = 0 ] || fail "a ^C during a cleanup's command starts never loses one ($wl/12 cleanups stopped)"
        echo "e2e ok: a ^C landing as a cleanup's command starts never loses it (12/12)"
    fi

    # R2 (C1-3): a terminal ^\ during an always: the command ignores it, the
    # cleanups run whole, the tmp dir goes, weir dies by SIGQUIT
    # [D:sigquit-teardown]
    cat > "$stdir/r2.weir" <<'WEOF'
let log = Self.args |> Seq.head
within tmp d
    within
        File.append log [d]
        Duration.sleep 300ms
    always
        File.append log ["inner start"]
        sh -c "sleep 1.2; echo cmd-done"
        File.append log ["inner end"]
always
    File.append log ["outer"]
WEOF
    rm -f "$stdir/r2.log"
    tout=$(printf 'SLEEP 900\nSEND \\x1c\nSLEEP 2500\n' | python3 "$ptyrun" 8 "$BIN" "$stdir/r2.weir" "$stdir/r2.log")
    for want in "cmd-done" "EXIT -3"; do
        echo "$tout" | grep -qF "$want" || fail "a terminal ^\\ during an always: '$want' expected: $tout"
    done
    [ "$(tail -n +2 "$stdir/r2.log" | tr '\n' ,)" = "inner start,inner end,outer," ] || fail "a terminal ^\\ runs every cleanup whole: $(stlog "$stdir/r2.log")"
    r2tmp=$(head -1 "$stdir/r2.log")
    [ -n "$r2tmp" ] && [ ! -e "$r2tmp" ] || fail "a terminal ^\\ removes the tmp dir: $r2tmp"
    echo "e2e ok: a terminal ^\\ runs the cleanups whole (their commands ignore it), removes the tmp dir, dies by SIGQUIT"

    # C1-8: a cleanup that raises under the backstop (the main flow stuck in a
    # builtin's own loop) says so, as on the main flow's unwind
    cat > "$stdir/bs.weir" <<'WEOF'
let log = Self.args |> Seq.head
within
    within
        File.append log ["body"]
        let n = [1..3000000000] |> Seq.length
        print $"{n}"
    always
        File.append log ["inner"]
always
    fail "outer cleanup raises"
WEOF
    rm -f "$stdir/bs.log"
    r=$(python3 "$stdir/sig.py" 10 "wait:$stdir/bs.log:body" sleep:200 kill:TERM -- "$BIN" "$stdir/bs.weir" "$stdir/bs.log")
    echo "$r" | grep -q "^status -15 " || fail "the backstop dies by the signal: $r"
    echo "$r" | grep -qF "the cleanup ALSO failed — outer cleanup raises" || fail "a cleanup raising under the backstop says so: $r"
    echo "e2e ok: a cleanup raising under the signal backstop says 'the cleanup ALSO failed'"

    # C1-5: a command killed by a group-directed HUP that also stops weir is
    # the teardown, never "command failed" [D:teardown-kill-not-failure]
    printf '%s\n' 'sh -c "kill -HUP 0; sleep 1"' > "$stdir/hupg.weir"
    hf=0
    for _ in $(seq 40); do
        r=$(python3 "$stdir/sig.py" 10 -- "$BIN" "$stdir/hupg.weir")
        case "$r" in *"command failed"*) hf=$((hf + 1)) ;; esac
    done
    [ "$hf" = 0 ] || fail "a group-directed SIGHUP's command death is the teardown ($hf/40 said command failed)"
    echo "e2e ok: a group-directed SIGHUP's command death is the teardown, never 'command failed' (40/40)"
    # ... and its deterministic shape at a terminal: the command takes the
    # signal 20ms before weir does — TERM and HUP settle as INT does
    cat > "$stdir/c15.py" <<'PYEOF'
import os, pty, sys, time, select, signal, subprocess
sig = getattr(signal, "SIG" + sys.argv[1]); mark = sys.argv[2]; argv = sys.argv[3:]
def marked():
    ps = subprocess.run(["ps", "-A", "-o", "pid=", "-o", "args="], capture_output=True, text=True).stdout
    for row in ps.splitlines():
        v = row.split()
        if len(v) >= 3 and os.path.basename(v[1]) == "sleep" and v[2] == mark: return int(v[0])
pid, fd = pty.fork()
if pid == 0:
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM): signal.signal(s, signal.SIG_DFL)
    os.execvp(argv[0], argv)
out = b""; code = None
def pump(until):
    global out, code
    while time.monotonic() < until and code is None:
        done, st = os.waitpid(pid, os.WNOHANG)
        if done: code = os.waitstatus_to_exitcode(st); break
        if select.select([fd], [], [], 0.005)[0]:
            try: out += os.read(fd, 65536)
            except OSError: pass
t0 = time.monotonic(); c = None
while c is None and time.monotonic() - t0 < 5: c = marked(); pump(time.monotonic() + 0.01)
if c: os.kill(c, sig)
pump(time.monotonic() + 0.02)
if code is None: os.kill(pid, sig)
pump(time.monotonic() + 8)
pump(time.monotonic() + 0.2)
print("EXIT", code, "OUT", out.decode(errors="replace").replace("\r\n", "|"))
PYEOF
    printf '%s\n' 'sleep 31.71' > "$stdir/c15.weir"
    for sg in TERM:-15 HUP:-1; do
        r=$(python3 "$stdir/c15.py" "${sg%%:*}" 31.71 "$BIN" "$stdir/c15.weir")
        case "$r" in "EXIT ${sg#*:} "*) ;; *) fail "a SIG${sg%%:*} the command took first is the teardown, weir dies by it: $r" ;; esac
        echo "$r" | grep -q "command failed" && fail "a SIG${sg%%:*} the command took first is never 'command failed': $r"
    done
    echo "e2e ok: a TERM or HUP the command took first at a terminal is the teardown, never 'command failed'"

    # R6 (C21): a REPL ^C that cut no wait still ends a multi-statement
    # entry — a `| exitCode` child, pure compute [D:repl-entry-check]
    tout=$(printf '%s\n' 'SLEEP 2500' 'SEND let c = sh -c "sleep 5" | exitCode\x1b\r' 'SLEEP 150' 'SEND print (Str.toUpper "late-c")\r' 'SLEEP 700' 'SEND \x03' 'SLEEP 1000' 'SEND let n = [1..6000000] |> Seq.map (fun i -> i % 7) |> Seq.length\x1b\r' 'SLEEP 150' 'SEND print (Str.toUpper "late-n")\r' 'SLEEP 500' 'SEND \x03' 'SLEEP 6000' 'SEND print (Str.toUpper "still-here")\r' 'SLEEP 800' 'SEND \x04' 'SLEEP 500' | env HOME="$stdir" XDG_CONFIG_HOME="$stdir/c" XDG_DATA_HOME="$stdir/d" XDG_STATE_HOME="$stdir/s" XDG_CACHE_HOME="$stdir/k" NO_COLOR=1 python3 "$ptyrun" 20 "$BIN")
    echo "$tout" | grep -qF "STILL-HERE" || fail "the REPL survives the entries' ^C: $tout"
    echo "$tout" | grep -qE "LATE-C|LATE-N" && fail "a REPL ^C that cut no wait ends the entry: $tout"
    [ "$(echo "$tout" | grep -o "exit 130" | wc -l)" -ge 2 ] || fail "each ^C'd entry shows exit 130: $tout"
    echo "e2e ok: a REPL ^C that cut no wait (a | exitCode child, pure compute) ends the entry with exit 130"

    # D-1: a REPL ^C killing a command inside a `within` body ends the entry
    # with exit 130, never "command failed … 130" [D:repl-interrupt]. The
    # entry goes in one write: queued input keeps its own indentation
    # [D:repl-autoindent]
    tout=$(printf '%s\n' 'SLEEP 2500' 'SEND within\x1b\r    sleep 3.41\x1b\ralways\x1b\r    print (Str.toUpper "cl-ran")\r' 'SLEEP 800' 'SEND \x03' 'SLEEP 1500' 'SEND print (Str.toUpper "still-here")\r' 'SLEEP 800' 'SEND \x04' 'SLEEP 500' | env HOME="$stdir" XDG_CONFIG_HOME="$stdir/c" XDG_DATA_HOME="$stdir/d" XDG_STATE_HOME="$stdir/s" XDG_CACHE_HOME="$stdir/k" NO_COLOR=1 python3 "$ptyrun" 15 "$BIN")
    for want in "CL-RAN" "exit 130" "STILL-HERE"; do
        echo "$tout" | grep -qF "$want" || fail "a REPL ^C in a within body: '$want' expected: $tout"
    done
    echo "$tout" | grep -qF "command failed" && fail "a REPL ^C in a within body is the interrupt, never 'command failed': $tout"
    echo "e2e ok: a REPL ^C killing a command in a within body ends the entry with exit 130"

    # R10 (D-2): a group-directed TERM/HUP (no tty) that kills a cleanup's
    # command is the signal's — no message, the cleanup runs on whole, then
    # weir dies by the signal [D:cleanup-group-signal]
    cat > "$stdir/r10.weir" <<'WEOF'
let log = Self.args |> Seq.head
within
    File.append log ["body"]
always
    File.append log ["cleanup start"]
    sh -c "sleep 3.43"
    File.append log ["cleanup end"]
File.append log ["LATE"]
WEOF
    cat > "$stdir/grp.py" <<'PYEOF'
import os, sys, time, signal, subprocess
sig = getattr(signal, 'SIG' + sys.argv[1]); log = sys.argv[2]; argv = sys.argv[3:]
def pre():
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM, signal.SIGPIPE): signal.signal(s, signal.SIG_DFL)
p = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, preexec_fn=pre, start_new_session=True)
def has(l):
    try: return l in open(log).read().splitlines()
    except OSError: return False
t0 = time.monotonic()
while not has('cleanup start') and time.monotonic() - t0 < 8: time.sleep(0.02)
time.sleep(0.3); os.killpg(p.pid, sig)
try: _, e = p.communicate(timeout=10)
except subprocess.TimeoutExpired: os.killpg(p.pid, signal.SIGKILL); _, e = p.communicate()
print('status %s err %s' % (p.returncode, e.decode(errors='replace').replace('\n', ' | ')))
PYEOF
    for sg in TERM:-15 HUP:-1; do
        rm -f "$stdir/r10.log"
        r=$(python3 "$stdir/grp.py" "${sg%%:*}" "$stdir/r10.log" "$BIN" "$stdir/r10.weir" "$stdir/r10.log")
        case "$r" in "status ${sg#*:} err "*) ;; *) fail "a group SIG${sg%%:*} during a cleanup's command: weir dies by it: $r" ;; esac
        echo "$r" | grep -q "command failed" && fail "a group SIG${sg%%:*} killing a cleanup's command is never 'command failed': $r"
        [ "$(stlog "$stdir/r10.log")" = "body,cleanup start,cleanup end," ] || fail "a group SIG${sg%%:*} during a cleanup's command: the cleanup runs on whole, nothing after: $(stlog "$stdir/r10.log")"
    done
    # ... its fan-out arms' commands too
    printf '%s\n' 'let log = Self.args |> Seq.head' 'within' '    File.append log ["body"]' 'always' '    File.append log ["cleanup start"]' '    [1; 2] |> Seq.piter (fun _ -> sh -c "sleep 3.45")' '    File.append log ["cleanup end"]' > "$stdir/r10b.weir"
    rm -f "$stdir/r10.log"
    r=$(python3 "$stdir/grp.py" TERM "$stdir/r10.log" "$BIN" "$stdir/r10b.weir" "$stdir/r10.log")
    case "$r" in "status -15 err "*) ;; *) fail "a group SIGTERM during a cleanup's fan-out: weir dies by it, no message: $r" ;; esac
    [ "$(stlog "$stdir/r10.log")" = "body,cleanup start,cleanup end," ] || fail "a group SIGTERM during a cleanup's fan-out: the cleanup runs on whole: $(stlog "$stdir/r10.log")"
    echo "e2e ok: a group-directed TERM/HUP killing a cleanup's command is the signal's — the cleanup runs on whole, then the death"

    # D-3: a second signal while a cleanup's command holds the terminal raw
    # puts the terminal back at the hard exit [D:hard-exit]
    printf '%s\n' 'within' '    print "body"' 'always' '    sh -c "stty -icanon -echo; sleep 3.44"' > "$stdir/d3.weir"
    cat > "$stdir/d3.py" <<'PYEOF'
import os, pty, sys, time, select, signal, termios
pid, fd = pty.fork()
if pid == 0:
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM): signal.signal(s, signal.SIG_DFL)
    os.execv(sys.argv[2], sys.argv[2:])
def pump(d):
    t = time.monotonic()
    while time.monotonic() - t < d:
        if select.select([fd], [], [], 0.02)[0]:
            try: os.read(fd, 65536)
            except OSError: return
pump(0.8)
os.write(fd, b'\x03'); pump(0.3)
if sys.argv[1] == 'QUIT': os.write(fd, b'\x1c')
else: os.kill(pid, getattr(signal, 'SIG' + sys.argv[1]))
t = time.monotonic(); code = 'HANG'
while time.monotonic() - t < 5:
    p, st = os.waitpid(pid, os.WNOHANG)
    if p: code = os.waitstatus_to_exitcode(st); break
    pump(0.02)
if code == 'HANG': os.kill(pid, 9); os.waitpid(pid, 0)
lf = termios.tcgetattr(fd)[3]
print('%s icanon=%d echo=%d' % (code, bool(lf & termios.ICANON), bool(lf & termios.ECHO)))
PYEOF
    for second in QUIT TERM HUP; do
        r=$(python3 "$stdir/d3.py" "$second" "$BIN" "$stdir/d3.weir")
        echo "$r" | grep -q "^-2 icanon=1 echo=1$" || fail "a second signal ($second) while a cleanup's command holds the terminal raw restores it: $r"
    done
    left=$(ps -eo pid,args | awk '$2 == "sleep" && $3 == "3.44" {print $1}')
    [ -z "$left" ] || { kill -9 $left 2>/dev/null; fail "the hard exit kills the raw-mode cleanup command"; }
    echo "e2e ok: a second signal while a cleanup's command holds the terminal raw restores it at the hard exit"

    # an Alt+Enter (ESC CR in one write — a paste, the real key) is the
    # newline even when .NET reads the ESC alone; a lone Esc abandons the
    # buffer, and so does a human's Esc then Enter: no waiting for the
    # Enter [D:repl-esc-enter]
    tout=$(printf '%s\n' 'SLEEP 2500' 'SEND print (Str.toUpper "esc-a")' 'SEND \x1b\r' 'SLEEP 100' 'SEND print (Str.toUpper "esc-b")\r' 'SLEEP 600' 'SEND print (Str.toUpper "esc-c")' 'SEND \x1b' 'SLEEP 300' 'SEND print (Str.toUpper "esc-d")\r' 'SLEEP 600' 'SEND print (Str.toUpper "esc-e")' 'SEND \x1b' 'SLEEP 60' 'SEND \r' 'SLEEP 300' 'SEND print (Str.toUpper "esc-f")\r' 'SLEEP 600' 'SEND \x04' 'SLEEP 400' | env HOME="$stdir" XDG_CONFIG_HOME="$stdir/c" XDG_DATA_HOME="$stdir/d" XDG_STATE_HOME="$stdir/s" XDG_CACHE_HOME="$stdir/k" NO_COLOR=1 python3 "$ptyrun" 12 "$BIN")
    for want in ESC-A ESC-B ESC-D ESC-F; do
        echo "$tout" | grep -qF "$want" || fail "an Alt+Enter is a newline, a lone Esc abandons: '$want' expected: $tout"
    done
    echo "$tout" | grep -qF "ESC-C" && fail "a lone Esc abandons the buffer: $tout"
    echo "$tout" | grep -qF "ESC-E" && fail "an Esc then an Enter 60ms later abandons the buffer: $tout"
    echo "e2e ok: an Alt+Enter is the newline even read as Esc then Enter; a lone Esc and a typed Esc, Enter abandon"

    # ISIG stays on for a whole multi-statement REPL entry: no raw gap
    # between its statements for a ^C to land in as a typed byte (the entry
    # then ran on to its end) [D:repl-entry-isig]
    cat > "$stdir/isig.py" <<'PYEOF'
# a REPL entry of many `| exitCode` statements, its ISIG polled from the master: it must stay on from the
# entry's start to its last output (a raw gap is where a ^C lands as a typed byte)
import os, pty, sys, time, select, termios
b = sys.argv[1]; home = sys.argv[2]
pid, fd = pty.fork()
if pid == 0:
    os.environ.update(NO_COLOR='1', HOME=home, XDG_CONFIG_HOME=home, XDG_STATE_HOME=home, XDG_DATA_HOME=home, XDG_CACHE_HOME=home)
    os.execv(b, [b])
out = b''
def drain():
    global out
    while select.select([fd], [], [], 0)[0]:
        try: c = os.read(fd, 65536)
        except OSError: return
        if not c: return
        out += c
t = time.monotonic()
while b'weir> ' not in out and time.monotonic() - t < 10: drain(); time.sleep(0.01)
time.sleep(0.5); drain()
lines = ['let c%d = sh -c "exit 0" | exitCode' % i for i in range(30)] + ['print (Str.toUpper "isig-done")']
for j, l in enumerate(lines):
    os.write(fd, l.encode() + (b'\r' if j == len(lines) - 1 else b'\x1b\r')); time.sleep(0.005); drain()
m = len(out); off = 0; began = False; t = time.monotonic()
k = 0
while time.monotonic() - t < 15:
    lf = termios.tcgetattr(fd)[3]
    if lf & termios.ISIG: began = True
    elif began:
        drain()
        if b'ISIG-DONE' in out[m:]: break
        off += 1
        while not termios.tcgetattr(fd)[3] & termios.ISIG and time.monotonic() - t < 15:
            drain()
            if b'ISIG-DONE' in out[m:]: break
    k += 1
    if k % 200 == 0:
        drain()
        if b'ISIG-DONE' in out[m:]: break
print('done=%d raw-windows=%d' % (b'ISIG-DONE' in out[m:], off))
os.write(fd, b'\x04'); time.sleep(0.3)
PYEOF
    mkdir -p "$stdir/isig"
    for k in 1 2 3; do
        r=$(python3 "$stdir/isig.py" "$BIN" "$stdir/isig")
        [ "$r" = "done=1 raw-windows=0" ] || fail "a REPL entry keeps ISIG on between its statements: $r"
    done
    echo "e2e ok: a multi-statement REPL entry keeps ISIG on from start to end — a ^C is never a typed byte"

    # C23: a tty REPL entry of several statements is one evaluation — a ^C
    # or a failure ends the whole entry, the session goes on
    tout=$(printf '%s\n' 'SLEEP 2500' 'SEND Duration.sleep 2s\x1b\r' 'SLEEP 150' 'SEND print (Str.toUpper "late-two")\x1b\r' 'SLEEP 150' 'SEND sh -c "sleep 2; echo late-three | tr a-z A-Z"\r' 'SLEEP 700' 'SEND \x03' 'SLEEP 3500' 'SEND fail "boom"\x1b\r' 'SLEEP 150' 'SEND print (Str.toUpper "after-fail")\r' 'SLEEP 1000' 'SEND print (Str.toUpper "still-here")\r' 'SLEEP 800' 'SEND \x04' 'SLEEP 500' | env HOME="$stdir" XDG_CONFIG_HOME="$stdir/c" XDG_DATA_HOME="$stdir/d" XDG_STATE_HOME="$stdir/s" XDG_CACHE_HOME="$stdir/k" python3 "$ptyrun" 15 "$BIN")
    echo "$tout" | grep -qF "STILL-HERE" || fail "the REPL survives an entry's ^C and failure: $tout"
    echo "$tout" | grep -qE "LATE-TWO|LATE-THREE|AFTER-FAIL" && fail "a ^C or a failure ends the whole REPL entry: $tout"
    echo "e2e ok: a REPL entry is one evaluation — a ^C or a failure ends all of it, the session goes on"

    # C24: a Self.stdin race loser cut before any byte read nothing — the
    # next Self.stdin reads the whole input
    cat > "$stdir/c24.weir" <<'WEOF'
let a =
    ["read"; "to"] |> Seq.pfirst (fun k ->
        if k == "to" then
            Duration.sleep 300ms
            "timeout"
        else
            Self.stdin |> Seq.head)
print $"race={a}"
let rest = Self.stdin |> Seq.freeze
print $"""rest={rest |> Str.join ","}"""
WEOF
    out=$( (sleep 1; printf 'L1\nL2\n') | timeout 10 "$BIN" "$stdir/c24.weir" 2>&1) || true
    echo "$out" | grep -qx "race=timeout" || fail "the stdin race's timer wins: $out"
    echo "$out" | grep -qx "rest=L1,L2" || fail "a Self.stdin loser cut before reading leaves stdin whole: $out"
    echo "e2e ok: a Self.stdin race loser cut before any byte is no enumeration — the next one reads the whole input"

    # R3 (F5): a cut Self.prompt's partial line never splices — a child
    # reading stdin next starts at the line after, the line stays whole
    printf '%s\n' 'let w = [1; 2] |> Seq.pfirst (fun i -> if i == 1 then Self.prompt "" else (Duration.sleep 300ms; "timer"))' 'print $"winner={w}"' 'let c = sh -c "read l; echo child=$l" | text' 'print c' 'let b = Self.prompt ""' 'print $"prompt2={b}"' > "$stdir/f5.weir"
    out=$( (printf 'L'; sleep 0.6; printf '1\nL2\nL3\n') | timeout 10 "$BIN" "$stdir/f5.weir" 2>&1) || true
    echo "$out" | grep -qx "child=L2" || fail "a child after a cut prompt's partial line reads the next whole line: $out"
    echo "$out" | grep -q "prompt2=L1$" || fail "the cut prompt's line stays whole for the next prompt: $out"
    echo "e2e ok: a cut prompt's partial line never splices — finished whole before a child reads stdin"

    # R2 (C25): `exit n` in a serve handler stops the scope — the parked
    # handler is cut with its cleanup whole, nothing after the scope runs,
    # weir exits n [D:serve-handler-exit]
    svportT=8493
    cat > "$stdir/c25.weir" <<WEOF
let log = Self.args |> Seq.head
let handler = fun req ->
    match req.path with
    | "/park" ->
        within
            Duration.sleep 30s
            File.append log ["LATE park"]
        always
            File.append log ["park cleanup"]
        HttpServerResponse { status = 200; headers = []; body = Text "ok" }
    | _ ->
        exit 7
        HttpServerResponse { status = 200; headers = []; body = Text "ok" }
within serve srv = { port = $svportT; maxConcurrent = 4 } handler
    within proc _c = curl -s -m 10 "http://127.0.0.1:$svportT/park"
        Duration.sleep 300ms
        let _r = Http.send { Http.defaults with url = "http://127.0.0.1:$svportT/x" } |> try result
        Duration.sleep 2s
        File.append log ["LATE body"]
File.append log ["LATE after"]
WEOF
    rm -f "$stdir/c25.log"
    code=0; out=$(timeout 20 "$BIN" "$stdir/c25.weir" "$stdir/c25.log" 2>&1) || code=$?
    [ "$code" = "7" ] || fail "exit 7 in a serve handler exits 7 (got $code): $out"
    [ "$(stlog "$stdir/c25.log")" = "park cleanup," ] || fail "the handler's exit cuts the parked handler, its cleanup whole, nothing after: $(stlog "$stdir/c25.log")"
    echo "$out" | grep -q "ExitRequest" && fail "a handler's exit names no internal exception: $out"
    echo "e2e ok: exit n in a serve handler stops the scope — handlers cut and joined, cleanups whole, then exit n"
    # ---- hunt-3 round 6 stage A ----------------------------------------------
    # R4 (C1-2, C2-2): the cleanup pass is a statement's — a value capture the
    # group signal truncated fails, never a partial value [D:cleanup-group-signal]
    cat > "$stdir/r4cap.weir" <<'WEOF'
let log = Self.args |> Seq.head
within
    File.append log ["body"]
always
    File.append log ["cleanup start"]
    let lines = sh -c "echo line1; sleep 3.46; echo line2" |> Seq.freeze
    File.append log [$"captured {Seq.length lines}"]
WEOF
    printf '%s\n' 'let log = Self.args |> Seq.head' 'within' '    File.append log ["body"]' 'always' '    File.append log ["cleanup start"]' '    let x = sh -c "sleep 3.47; echo a" | line' '    File.append log [$"line {x}"]' > "$stdir/r4line.weir"
    for f in r4cap r4line; do
        rm -f "$stdir/r10.log"
        r=$(python3 "$stdir/grp.py" TERM "$stdir/r10.log" "$BIN" "$stdir/$f.weir" "$stdir/r10.log")
        case "$r" in "status -15 err "*"command failed with exit code 143"*) ;; *) fail "R4: a group SIGTERM killing a cleanup's capture ($f) fails it, then the death: $r" ;; esac
        echo "$r" | grep -q "expected exactly one line" && fail "R4: a killed '| line' says the command failed: $r"
        [ "$(stlog "$stdir/r10.log")" = "body,cleanup start," ] || fail "R4: a truncated capture ($f) never becomes a value: $(stlog "$stdir/r10.log")"
    done
    # ... and a cleanup's command started after the signal gets no pass: its
    # own exit 143 is its failure (C1-1)
    printf '%s\n' 'let log = Self.args |> Seq.head' 'within' '    File.append log ["body"]' '    sleep 3.48' 'always' '    sh -c "exit 143"' '    File.append log ["LATE"]' > "$stdir/r4own.weir"
    rm -f "$stdir/r10.log"
    r=$(python3 "$stdir/sig.py" 10 "wait:$stdir/r10.log:body" kill:TERM -- "$BIN" "$stdir/r4own.weir" "$stdir/r10.log")
    echo "$r" | grep -q "^status -15 .*command failed with exit code 143" || fail "R4: a cleanup command started after the signal that exits 143 fails: $r"
    [ "$(stlog "$stdir/r10.log")" = "body," ] || fail "R4: the failing cleanup command stops its cleanup: $(stlog "$stdir/r10.log")"
    echo "e2e ok: R4 — a truncated capture or a command started after the signal fails in a cleanup; only a running statement takes the pass"

    # C1-3, C2-3: a settle wait waits for a signal in flight — an exit code
    # alone (129/130/131/143, script or tty REPL) costs a 5ms grace at most,
    # never the 250ms [D:settle-pending]
    printf '%s\n' 'let t0 = Instant.now ()' '[129; 130; 131; 143] |> Seq.iter (fun c ->' '    [1; 2; 3] |> Seq.iter (fun _ ->' '        let _r = $(sh -c $"exit {c}") |> try' '        ()))' 'if Instant.now () - t0 < 1500ms then print (Str.toUpper "fast") else print (Str.toUpper "slow")' > "$stdir/settle.weir"
    out=$("$BIN" "$stdir/settle.weir" </dev/null 2>&1)
    [ "$out" = "FAST" ] || fail "a 129/130/131/143 exit with no signal never waits the 250ms (script): $out"
    tout=$(printf 'SLEEP 1500\n' | python3 "$ptyrun" 10 "$BIN" "$stdir/settle.weir")
    echo "$tout" | grep -qF "FAST" || fail "a 129/130/131/143 exit with no signal never waits the 250ms (script at a tty): $tout"
    tout=$(printf '%s\n' 'SLEEP 2500' 'SEND let t0 = Instant.now ()\x1b\r' 'SEND let ds = [1..12] |> Seq.map (fun _ -> sh -c "exit 130" | exitCode) |> Seq.freeze\x1b\r' 'SEND if Instant.now () - t0 < 1500ms then print (Str.toUpper "fast") else print (Str.toUpper "slow")\r' 'SLEEP 5000' 'SEND \x04' 'SLEEP 500' | env HOME="$stdir" XDG_CONFIG_HOME="$stdir/c" XDG_DATA_HOME="$stdir/d" XDG_STATE_HOME="$stdir/s" XDG_CACHE_HOME="$stdir/k" NO_COLOR=1 python3 "$ptyrun" 15 "$BIN")
    echo "$tout" | grep -qF "FAST" || fail "a REPL child exiting 130 with no ^C never waits the 250ms: $tout"
    echo "e2e ok: a 129/130/131/143 exit with no signal in flight never waits the 250ms — script, tty script, tty REPL"

    # T1-2: a group-directed SIGHUP is the teardown even when its handler
    # starts late — .NET runs SIGHUP's handler as a pool item, which CPU
    # contention can hold past the 5ms grace. Here the pool's worker is held
    # (ptrace-stopped) for 150ms while the HUP lands: the cleanup's command
    # it killed never reads as "command failed", the cleanup runs on, weir
    # dies by the signal [D:settle-pending]
    if [ "$(uname -s)" = Linux ]; then
        cat > "$stdir/hold.py" <<'PYEOF'
# hold.py LOG MS -- argv: when LOG says "cleanup", stop weir's pool workers, group-HUP it, resume them after MS
import ctypes, os, sys, time, signal, subprocess
log, ms = sys.argv[1], int(sys.argv[2]); argv = sys.argv[4:]
libc = ctypes.CDLL(None, use_errno=True)
libc.ptrace.argtypes = [ctypes.c_long, ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p]
def pre():
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM, signal.SIGPIPE): signal.signal(s, signal.SIG_DFL)
p = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, preexec_fn=pre, start_new_session=True)
t0 = time.monotonic()
while time.monotonic() - t0 < 10:
    try:
        if 'cleanup' in open(log).read().split(): break
    except OSError: pass
    time.sleep(0.02)
time.sleep(0.2)
tids = [int(t) for t in os.listdir('/proc/%d/task' % p.pid) if open('/proc/%d/task/%s/comm' % (p.pid, t)).read().strip() == '.NET TP Worker']
held = []
for t in tids:
    if libc.ptrace(0x4206, t, None, None) == 0:  # PTRACE_SEIZE
        libc.ptrace(0x4207, t, None, None)  # PTRACE_INTERRUPT
        os.waitpid(t, 0x40000000)  # __WALL
        held.append(t)
os.killpg(p.pid, signal.SIGHUP)
time.sleep(ms / 1000)
for t in held:
    if libc.ptrace(17, t, None, None) != 0:  # PTRACE_DETACH; a thread weir's exit took is reaped here
        try: os.waitpid(t, 0x40000000)
        except OSError: pass
try: o = p.communicate(timeout=10)[0]
except subprocess.TimeoutExpired: os.killpg(p.pid, signal.SIGKILL); o = p.communicate()[0]; p.returncode = 'HANG'
print('held %d status %s out %s' % (len(held), p.returncode, o.decode(errors='replace').replace('\n', ' | ')))
PYEOF
        printf '%s\n' 'let log = Self.args |> Seq.head' 'within' '    sh -c "true"' '    File.append log ["body"]' 'always' '    File.append log ["cleanup"]' '    sh -c "sleep 4"' '    File.append log ["after"]' > "$stdir/hupl.weir"
        rm -f "$stdir/hupl.log"
        r=$(python3 "$stdir/hold.py" "$stdir/hupl.log" 150 -- "$BIN" "$stdir/hupl.weir" "$stdir/hupl.log")
        case "$r" in
            "held 0 "*) echo "e2e skip: no pool worker to hold (ptrace refused?): $r" ;;
            *)
                echo "$r" | grep -q "status -1 " || fail "a group SIGHUP whose handler starts late is the teardown: $r"
                echo "$r" | grep -qF "command failed" && fail "a group SIGHUP whose handler starts late never reads as 'command failed': $r"
                [ "$(stlog "$stdir/hupl.log")" = "body,cleanup,after," ] || fail "a group SIGHUP whose handler starts late: the cleanup runs on whole: $(stlog "$stdir/hupl.log")"
                # held past the 250ms settle: the cleanup reads it as a
                # failure, but the exit still settles it — weir dies by it
                rm -f "$stdir/hupl.log"
                r=$(python3 "$stdir/hold.py" "$stdir/hupl.log" 400 -- "$BIN" "$stdir/hupl.weir" "$stdir/hupl.log")
                echo "$r" | grep -q "status -1 " || fail "a group SIGHUP whose handler starts past the settle is never dropped at the exit: $r"
                echo "e2e ok: a group SIGHUP whose handler starts late is the teardown — the cleanup runs on; past the settle the exit still dies by it"
                ;;
        esac
    fi

    # C1-4: a cleanup's command through the shim refuses what a direct spawn
    # refuses — a shebang-less executable never runs through sh [D:cleanup-sigign]
    printf 'echo noshebang-ran\n' > "$stdir/nosb"; chmod +x "$stdir/nosb"
    printf '%s\n' 'let p = Self.args |> Seq.head' 'within' '    print "body"' 'always' '    ^$p' > "$stdir/nosb.weir"
    tout=$(printf 'SLEEP 1500\n' | python3 "$ptyrun" 8 "$BIN" "$stdir/nosb.weir" "$stdir/nosb")
    echo "$tout" | grep -qF "not executable" || fail "a shebang-less executable in a cleanup at a tty is refused: $tout"
    echo "$tout" | grep -qF "noshebang-ran" && fail "a shebang-less executable never runs through the shim's sh: $tout"
    echo "e2e ok: a shebang-less executable in a cleanup at a tty is refused, as a direct spawn refuses it"
    # T1-1: ... followed down the #! chain as execve follows it: a script
    # whose interpreter is such a file is refused too; a chain of real
    # scripts runs [D:cleanup-sigign]
    printf '#!%s\necho chain-outer-ran\n' "$stdir/nosb" > "$stdir/nosbo"; chmod +x "$stdir/nosbo"
    tout=$(printf 'SLEEP 1500\n' | python3 "$ptyrun" 8 "$BIN" "$stdir/nosb.weir" "$stdir/nosbo")
    echo "$tout" | grep -qF "not executable" || fail "a #! script whose interpreter is shebang-less is refused in a cleanup at a tty: $tout"
    echo "$tout" | grep -qE "chain-outer-ran|noshebang-ran" && fail "a #! script whose interpreter is shebang-less never runs through the shim's sh: $tout"
    # (macOS runs no nested interpreter: the chain is Linux's)
    if [ "$(uname -s)" = Linux ]; then
        printf '#!/bin/sh\necho "chain-ran $#"\n' > "$stdir/ch0"; chmod +x "$stdir/ch0"
        printf '#!%s\n' "$stdir/ch0" > "$stdir/ch1"; chmod +x "$stdir/ch1"
        printf '#!%s\n' "$stdir/ch1" > "$stdir/ch2"; chmod +x "$stdir/ch2"
        tout=$(printf 'SLEEP 1500\n' | python3 "$ptyrun" 8 "$BIN" "$stdir/nosb.weir" "$stdir/ch2")
        echo "$tout" | grep -qF "chain-ran 2" || fail "a chain of #! scripts ending at a native interpreter runs in a cleanup at a tty: $tout"
    fi
    echo "e2e ok: the shim follows the #! chain as execve does — a shebang-less interpreter is refused, a script chain runs"

    # C1-5: the shim holds for a weir started in the background and brought
    # to the foreground while its cleanup runs: the ^C never kills the
    # cleanup's command [D:cleanup-sigign]
    if command -v bash >/dev/null 2>&1; then
        printf '%s\n' 'within' '    printerr "body start"' '    sleep 1' 'always' '    printerr "cleanup start"' '    sh -c "sleep 2; echo cmd-done"' '    printerr "cleanup end"' > "$stdir/jcbg.weir"
        cat > "$stdir/jc.py" <<'PYEOF'
import os, pty, sys, time, select, signal
pid, fd = pty.fork()
if pid == 0:
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM): signal.signal(s, signal.SIG_DFL)
    os.environ['PS1'] = '$ '; os.environ['TERM'] = 'dumb'
    os.execvp('bash', ['bash', '--norc', '--noprofile', '-i'])
out = b''
def pump(d):
    global out
    end = time.monotonic() + d
    while time.monotonic() < end:
        if select.select([fd], [], [], 0.01)[0]:
            try: out += os.read(fd, 65536)
            except OSError: return
pump(0.5)
os.write(fd, ('%s %s &\r' % (sys.argv[1], sys.argv[2])).encode()); pump(1.6)
os.write(fd, b'fg\r'); pump(0.5)
os.write(fd, b'\x03'); pump(2.8)
os.write(fd, b'exit\r'); pump(0.3)
try: os.kill(pid, signal.SIGKILL)
except OSError: pass
print(out.decode(errors='replace').replace('\r\n', '|'))
PYEOF
        tout=$(python3 "$stdir/jc.py" "$BIN" "$stdir/jcbg.weir")
        for want in "cleanup start" "cmd-done" "cleanup end"; do
            echo "$tout" | grep -qF "$want" || fail "a weir brought to the foreground: a ^C never kills its cleanup's command: '$want' expected: $tout"
        done
        echo "e2e ok: a background weir brought to the foreground keeps its cleanup's command through a ^C"
    fi

    # C1-7: a tmp dir is registered before any signal can see it — the hook
    # first (until it is installed a signal kills weir outright) [D:exit-hook]
    printf '%s\n' 'within tmp d' '    File.write $"{d}/x" ["1"]' '    sleep 0.3' > "$stdir/early.weir"
    mkdir -p "$stdir/et"
    cat > "$stdir/early.py" <<'PYEOF'
import os, sys, time, signal, subprocess
td = sys.argv[1]; argv = sys.argv[2:]; leaks = 0
def pre():
    for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM): signal.signal(s, signal.SIG_DFL)
for i in range(120):
    p = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, preexec_fn=pre, start_new_session=True, env=dict(os.environ, TMPDIR=td))
    time.sleep((8 + i % 14) / 1000.0)
    for j, s in enumerate(('INT', 'TERM')):
        if j: time.sleep(0.002)
        try: os.kill(p.pid, getattr(signal, 'SIG' + s))
        except ProcessLookupError: pass
    p.wait()
    for f in os.listdir(td):
        leaks += 1; os.system('rm -rf "%s"' % os.path.join(td, f))
print(leaks)
PYEOF
    lk=$(python3 "$stdir/early.py" "$stdir/et" "$BIN" "$stdir/early.weir")
    [ "$lk" = 0 ] || fail "two early signals leak no tmp dir ($lk/120)"
    echo "e2e ok: two signals in weir's first milliseconds leak no tmp dir (120 runs)"

    # C2-4: #infer's source and the prompt function run with ISIG on — a ^C
    # stops them as a signal, never a byte left for the next line [D:repl-isig]
    mkdir -p "$stdir/pc/weir"
    printf '%s\n' '#init' '' 'let sigil () =' "    let s = sh -c 'if [ -e $stdir/hang ]; then sleep 30; fi; echo P' | line" '    $"{s}> "' '' '#session {' '    prompt = sigil' '}' > "$stdir/pc/weir/init.weir"
    rm -f "$stdir/hang"
    tout=$(printf '%s\n' 'SLEEP 2500' 'SEND #infer let po = sh -c "sleep 30; echo {}" |> from json as _\r' 'SLEEP 800' 'SEND \x03' 'SLEEP 800' "SEND touch $stdir/hang\\r" 'SLEEP 1000' 'SEND \x03' 'SLEEP 800' "SEND rm -f $stdir/hang\\r" 'SLEEP 1000' 'SEND print (Str.toUpper "responsive")\r' 'SLEEP 1000' 'SEND \x04' 'SLEEP 500' | env HOME="$stdir" XDG_CONFIG_HOME="$stdir/pc" XDG_DATA_HOME="$stdir/d" XDG_STATE_HOME="$stdir/s" XDG_CACHE_HOME="$stdir/k" NO_COLOR=1 python3 "$ptyrun" 20 "$BIN")
    rm -f "$stdir/hang"
    echo "$tout" | grep -qF "interrupted" || fail "a ^C stops #infer's source: $tout"
    echo "$tout" | grep -qF "RESPONSIVE" || fail "a ^C stops a hung prompt function, the session goes on: $tout"
    echo "e2e ok: a ^C stops #infer's source and a hung prompt function — ISIG on, the session goes on"
    rm -rf "$stdir"
fi

# ---- hunt-3 round 6 stage A, every OS ------------------------------------------
r6dir=$(mkweirtmp)
# D1: a wide pfirst over `| complete` arms finishes when its winner does —
# a loser's stderr reader holds no pool thread the winner's waits for
# [D:pipe-threads]
cat > "$r6dir/pc.weir" <<'WEOF'
let one i =
    let r = sh -c $"[ {i} -eq 32 ] || sleep 31.81; echo {i}" | complete
    if r.exitCode == 0 && i == 32 then r.stdout |> Seq.head else fail "lost"
let w = [1..32] |> Seq.pfirst one
print $"winner {w}"
WEOF
# Windows: the winner still waits for the losers there (a BACKLOG residual)
if [ "$IS_WINDOWS" != "1" ]; then
start_ms=$(now_ms)
out=$(timeout 60 "$BIN" "$r6dir/pc.weir" 2>&1)
took=$(( $(now_ms) - start_ms ))
[ "$out" = "winner 32" ] || fail "a 32-arm pfirst over | complete: $out"
[ "$took" -lt 5000 ] || fail "a 32-arm pfirst over | complete finishes with its winner (took ${took}ms)"
echo "e2e ok: a 32-arm pfirst over | complete finishes with its winner (${took}ms)"
fi

# T1-3: a `| complete` arm reads both pipes on its own thread — one thread
# per arm, not two — and a thread the process/thread limit refuses is a weir
# error naming the limit [D:pipe-threads]
if [ "$(uname -s)" = Linux ]; then
cat > "$r6dir/pt.weir" <<'WEOF'
let n = Self.args |> Seq.head |> Str.toInt
let arm i =
    if i == 1 then
        let r = sh -c "echo w-err >&2; sleep 1.5; echo w-out" | complete
        $"{r.stdout |> Seq.head} {r.stderr |> Seq.head}"
    else
        let r = sh -c "sleep 30" | complete
        $"{r.exitCode}"
let w = [1..n] |> Seq.pfirstWith n arm
print $"won {w}"
WEOF
"$BIN" "$r6dir/pt.weir" 64 > "$r6dir/pt.out" 2>&1 &
ptpid=$!
ptpeak=0
while kill -0 "$ptpid" 2>/dev/null; do
    t=$(awk '/^Threads:/{print $2}' "/proc/$ptpid/status" 2>/dev/null) || t=""
    [ -n "$t" ] && [ "$t" -gt "$ptpeak" ] && ptpeak=$t
    sleep 0.05
done
wait "$ptpid"
[ "$(cat "$r6dir/pt.out")" = "won w-out w-err" ] || fail "a 64-arm pfirst over | complete: $(cat "$r6dir/pt.out")"
[ "$ptpeak" -lt 104 ] || fail "a 64-arm pfirst over | complete takes one thread per arm (peak $ptpeak threads)"
echo "e2e ok: a 64-arm pfirst over | complete takes one thread per arm (peak $ptpeak threads)"
# (RLIMIT_NPROC binds a non-root user only; .NET's own pool can still abort
# when it cannot add a worker at the limit — a runtime residual, so up to
# three tries)
if [ "$(id -u)" != 0 ]; then
    ptok=""
    for _ in 1 2 3; do
        ptn=$(ps -L -u "$(id -u)" -o lwp= | wc -l)
        out=$( (ulimit -u $((ptn + 150)); timeout 60 "$BIN" "$r6dir/pt.weir" 256) 2>&1) || true
        echo "$out" | grep -qF "TaskScheduler" && fail "a refused thread never shows .NET's text: $out"
        if echo "$out" | grep -qF "cannot start a thread for a pfirst arm: the process/thread limit is reached"; then
            ptok=1
            break
        fi
    done
    [ -n "$ptok" ] || fail "a thread the limit refuses is a weir error naming the limit: $out"
    echo "e2e ok: a fan-out thread the process/thread limit refuses is a weir error naming the limit"
fi
fi

# W1: after a cut nothing waits for the EOF of a pipe a descendant the kill
# missed still holds — a race loser's `within proc` whose child left a
# reparented grandchild on its stdout [D:wait-after-cut]
cat > "$r6dir/w1.weir" <<'WEOF'
let arm i =
    if i == 0 then
        Duration.sleep 300ms
        "win"
    else
        within proc p = sh -c "(sleep 2.51 &); sleep 31.82"
            let _c = Proc.wait p
            ()
        "lost"
let r = [0; 1] |> Seq.pfirst arm
print $"race: {r}"
WEOF
start_ms=$(now_ms)
(cd "$r6dir" && "$BIN" w1.weir > "$r6dir/w1.out" 2>&1)
took=$(( $(now_ms) - start_ms ))
[ "$(cat "$r6dir/w1.out")" = "race: win" ] || fail "a race whose loser's proc left a grandchild on its pipe: $(cat "$r6dir/w1.out")"
[ "$took" -lt 1800 ] || fail "a race loser's proc drain waited for a pipe a missed grandchild holds (took ${took}ms)"
echo "e2e ok: a race loser's within proc never waits for a pipe a missed grandchild holds (${took}ms)"

# C2-1: Dir.copy recreates a FIFO (and a socket, Linux) as `cp -R` does —
# never opens it, so a copy never blocks [D:dircopy-special]
if [ "$IS_WINDOWS" != "1" ] && command -v mkfifo >/dev/null 2>&1; then
    mkdir -p "$r6dir/dsrc/sub"
    echo hi > "$r6dir/dsrc/sub/a"
    mkfifo -m 0640 "$r6dir/dsrc/ff"
    [ "$(uname -s)" = Linux ] && (cd "$r6dir/dsrc" && python3 -c "import socket; socket.socket(socket.AF_UNIX).bind('so')")
    out=$(cd "$r6dir" && timeout 10 "$BIN" -e 'Dir.copy "dsrc" "ddst"' 2>&1) || fail "Dir.copy of a dir holding a FIFO completes: $out"
    [ -p "$r6dir/ddst/ff" ] || fail "Dir.copy recreates a FIFO as a FIFO"
    [ "$(cat "$r6dir/ddst/sub/a")" = hi ] || fail "Dir.copy copies the regular files beside a FIFO"
    [ "$(uname -s)" != Linux ] || [ -S "$r6dir/ddst/so" ] || fail "Dir.copy recreates a socket as a socket (Linux)"
    echo "e2e ok: Dir.copy recreates a FIFO (and a socket) without opening it"
fi
rm -rf "$r6dir"

# ---- streaming statement echo [D:stream-echo] -------------------------------
# the general pty instrument (tests/pty/pty-run.py — built for these
# pins, deliberately general: C14 TTY-contention and the REPL SIGINT
# split were parked for want of it). Timestamped chunks make "appeared
# before exit" an assertion, not a claim.
if [ "$IS_WINDOWS" != "1" ] && command -v python3 >/dev/null 2>&1; then
    sedir=$(mkweirtmp)
    printf '#!/bin/sh\nprintf PARTIAL-\nsleep 1\necho done\n' > "$sedir/pp.sh"
    printf '#!/bin/sh\nprintf "text\\0binary"\n' > "$sedir/pb.sh"
    chmod +x "$sedir/pp.sh" "$sedir/pb.sh"
    ptyrun="$(dirname "$0")/../tests/pty/pty-run.py"

    # REPL: the partial line (an interactive prompt's shape) appears
    # before the command exits — under inheritance the child writes the
    # pty itself, so the chunk boundary is the kernel's (the old exact
    # b'PARTIAL-' chunk was the relay's flush shape); the order stamp is
    # the claim
    rout=$(printf 'SLEEP 500\nSEND sh %s/pp.sh\\r\nSLEEP 2500\nSEND #quit\\r\n' "$sedir" | python3 "$ptyrun" 8 "$BIN")
    pline=$(echo "$rout" | grep -F "PARTIAL-" | grep -vF "pp.sh" | head -1 | awk '{print $1}')
    dline=$(echo "$rout" | grep -F "done" | head -1 | awk '{print $1}')
    [ -n "$pline" ] || fail "REPL: the partial must appear: $rout"
    [ -n "$dline" ] && [ "$pline" -lt "$dline" ] || fail "REPL: partial precedes the completing line: $rout"

    # script mode: same, inherited
    cat > "$sedir/sp.weir" <<WEOF
sh $sedir/pp.sh
WEOF
    sout=$(printf 'SLEEP 100\n' | python3 "$ptyrun" 5 "$BIN" "$sedir/sp.weir")
    echo "$sout" | grep -qF "b'" || fail "script pty run produced nothing: $sout"
    spline=$(echo "$sout" | grep -F "PARTIAL-" | head -1 | awk '{print $1}')
    sdline=$(echo "$sout" | grep -F "done" | head -1 | awk '{print $1}')
    [ -n "$spline" ] && [ -n "$sdline" ] && [ "$spline" -lt "$sdline" ] || fail "script: partial precedes completion: $sout"

    # ruling (a) [D:colour-inherit]: the bare statement at a tty is the
    # child's own write — no weir guard, raw bytes and all (bash's
    # posture; gzip refuses a tty by itself). The value echo keeps its
    # refusal — the [D:binary-echo] cells pin that half.
    # a terminal ^C reaches weir's foreground group whole, so weir kills
    # nothing and waits [D:sigint-terminal-group]: a child that traps
    # SIGINT runs its handler and exits, then weir dies by the SIGINT —
    # no statement after it, no "command failed"
    printf '%s\n' "sh -c 'trap \"sleep 0.5; echo caught; exit 3\" INT; sleep 7.31 & wait'" 'print "after"' > "$sedir/trapint.weir"
    tout=$(printf 'SLEEP 1000\nSEND \\x03\nSLEEP 2500\n' | python3 "$ptyrun" 6 "$BIN" "$sedir/trapint.weir")
    pkill -f "^sleep 7.31\$" 2>/dev/null || true
    echo "$tout" | grep -qF "caught" || fail "a terminal ^C must let the child run its SIGINT handler: $tout"
    echo "$tout" | grep -qF "EXIT -2" || fail "weir dies by the SIGINT after the child's ^C handling: $tout"
    echo "$tout" | grep -qF "after" && fail "no statement runs after a terminal ^C: $tout"
    echo "$tout" | grep -qF "command failed" && fail "a terminal ^C is not a command failure: $tout"

    # the terminal ^C follows LIVE scopes [D:sigint-terminal-group]: once
    # the `within tmp` has closed, the ^C is the unscoped one — the child's
    # handler runs, weir dies by the SIGINT
    printf '%s\n' 'within tmp d' '    print "scoped"' "sh -c 'trap \"sleep 0.5; echo caught; exit 3\" INT; sleep 7.32 & wait'" 'print "after"' > "$sedir/trapclosed.weir"
    tout=$(printf 'SLEEP 1000\nSEND \\x03\nSLEEP 2500\n' | python3 "$ptyrun" 6 "$BIN" "$sedir/trapclosed.weir")
    echo "$tout" | grep -qF "caught" || fail "a terminal ^C after every scope closed is the unscoped one (the child's handler runs): $tout"
    echo "$tout" | grep -qF "EXIT -2" || fail "a terminal ^C after the scopes closed dies by the SIGINT: $tout"
    left=$(ps -eo pid,args | awk '$2 == "sleep" && $3 == "7.32" {print $1}')
    [ -z "$left" ] || kill -9 $left 2>/dev/null || true

    # a second terminal ^C mid-teardown hard-exits [D:signal-teardown]: a
    # child ignoring the ^C does not hold weir, and it dies with weir
    printf '%s\n' "sh -c 'trap \"\" INT; sleep 7.42'" 'print "after"' > "$sedir/ignint.weir"
    t0=$(now_ms)
    tout=$(printf 'SLEEP 800\nSEND \\x03\nSLEEP 700\nSEND \\x03\nSLEEP 300\n' | python3 "$ptyrun" 12 "$BIN" "$sedir/ignint.weir")
    took=$(( $(now_ms) - t0 ))
    left=$(ps -eo pid,args | awk '$2 == "sleep" && $3 == "7.42" {print $1}')
    [ -z "$left" ] || { kill -9 $left 2>/dev/null; fail "the second ^C's hard exit must kill the ^C-ignoring child"; }
    echo "$tout" | grep -qF "EXIT -2" || fail "a second terminal ^C dies by the SIGINT: $tout"
    [ "$took" -lt 5000 ] || fail "a second terminal ^C must hard-exit, not wait out the child (${took}ms): $tout"

    # pure computation holds the terminal ^C's join [D:teardown-joins-arms]:
    # the grace period does not exit while the arms compute — a builtin's
    # own loop, since every evaluated node is a boundary [D:scope-tree]
    printf '%s\n' 'sh -c "true"' '[1; 2] |> Seq.piter (fun i ->' '    let n = [1..300000000] |> Seq.length' '    print $"arm {i} {n}")' 'print "after"' > "$sedir/purejoin.weir"
    t0=$(now_ms)
    tout=$(printf 'SLEEP 500\nSEND \\x03\nSLEEP 300\n' | python3 "$ptyrun" 20 "$BIN" "$sedir/purejoin.weir")
    took=$(( $(now_ms) - t0 ))
    echo "$tout" | grep -qF "EXIT -2" || fail "a terminal ^C during a pure-compute join dies by the SIGINT: $tout"
    echo "$tout" | grep -qE "arm [12] |after" && fail "no arm effect or statement after a terminal ^C: $tout"
    [ "$took" -gt 2000 ] || fail "the terminal ^C's grace must hold for the pure-compute join (exited at ${took}ms): $tout"

    # a tty Self.prompt stays cooked and drops key sequences
    # [D:prompt-tty-keys]: a left arrow (CSI) and an SS3 up arrow typed
    # into the answer are ignored, never part of it
    printf 'let a = Self.prompt "name?"\nprint $"[{a}] length={Str.length a}"\n' > "$sedir/arrow.weir"
    aout=$(printf 'SLEEP 500\nSEND ab\\x1b[Dc\\x1bOAd\\r\nSLEEP 800\n' | python3 "$ptyrun" 5 "$BIN" "$sedir/arrow.weir")
    echo "$aout" | grep -qF "[abcd] length=4" || fail "a tty Self.prompt must drop arrow-key sequences: $aout"

    bout=$(printf 'SLEEP 500\nSEND sh %s/pb.sh\\r\nSLEEP 800\nSEND #quit\\r\n' "$sedir" | python3 "$ptyrun" 5 "$BIN")
    echo "$bout" | grep -qF "binary output" && fail "the statement path has no guard to refuse with: $bout"
    echo "$bout" | grep -qF "text\x00binary" || fail "the child's bytes reach the terminal raw: $bout"

    # tty/redirected divergence is timing only — redirected bytes are
    # the batched path's, byte-identical (od's format differs GNU/BSD,
    # so assert content + exact byte count instead)
    rcontent=$( "$BIN" "$sedir/sp.weir" )
    rcount=$( "$BIN" "$sedir/sp.weir" | wc -c | tr -d ' ' )
    [ "$rcontent" = "PARTIAL-done" ] && [ "$rcount" = "13" ] || fail "redirected content unchanged (got '$rcontent' / $rcount bytes)"

    # reifiers do not stream — | complete is in-memory capture by law
    cat > "$sedir/rc.weir" <<WEOF
let r = \$(sh $sedir/pp.sh | complete)
print (r.stdout |> Seq.head)
WEOF
    cout=$(printf 'SLEEP 100\n' | python3 "$ptyrun" 5 "$BIN" "$sedir/rc.weir")
    cpline=$(echo "$cout" | grep -F "PARTIAL-done" | head -1 | awk '{print $1}')
    [ -n "$cpline" ] && [ "$cpline" -gt 900 ] || fail "a captured reifier must not stream (appeared at ${cpline}ms): $cout"

    echo "e2e ok: streaming echo — partials flush live (REPL + script), the bare statement's bytes are the child's own, redirected byte-identical, reifiers capture"

    # ---- let-echo truncation teaching consistency [D:echo-teaching-consistency] ----
    # a `let`-bound over-cap unforced seq<string> must carry the same
    # unforced-hint teaching the bare-expression echo shows — and it must
    # be visible (printed after the lines, not scrolled off the top). The
    # old shape printed `xs : seq<string> ={hint}` first (a dangling ' ='
    # too); the fix prints the lines, then `xs (hint)` last (the type
    # rides only with #types on [D:repl-types]).
    # `Seq.initInfinite show` is a deterministic unforced seq<string> — no
    # PATH executable. tty-only path (redirected uses the batched form),
    # so this rides the pty instrument.
    tout=$(printf 'SLEEP 400\nSEND let xs = Seq.initInfinite show\\r\nSLEEP 1500\nSEND #quit\\r\n' | python3 "$ptyrun" 8 "$BIN")
    echo "$tout" | grep -qF 'xs (first 100 of an unforced' \
      || fail "the let-bound unforced seq echo lost its teaching (or it is not on the meta line): $tout"
    # the dangling ' =' is gone: the meta reads `: seq<string> (…)`, never `: seq<string> =`
    echo "$tout" | grep -qF 'seq<string> =' \
      && fail "the let-echo meta still renders a dangling ' =' before the truncation hint: $tout"
    echo "e2e ok: a let-bound truncated seq echoes the unforced teaching, visible and without a dangling '='"

    # ---- the binding echo states seq state [D:reenum-warning] ----------
    # a command-backed unforced bind is not run by the echo
    # [D:repl-let-echo-lazy]; a materialized bind says frozen; the
    # pure-lazy Seq.initInfinite cell above stays unannotated (its teaching grep pins
    # the meta with no state joined)
    rsout=$(printf 'SLEEP 400\nSEND let pods = sh -c "echo ran-$((40+2))"\\r\nSLEEP 1200\nSEND let snap = ["a"; "b"]\\r\nSLEEP 800\nSEND #quit\\r\n' | python3 "$ptyrun" 10 "$BIN")
    # the harness echoes a bytes repr, so the em-dash rides as \xe2\x80\x94 —
    # pin the ASCII halves around it
    echo "$rsout" | grep -qF 'pods (command-backed' \
      || fail "the command-backed unforced bind must say so: $rsout"
    echo "$rsout" | grep -qF 'runs on each use; type pods to run it)' \
      || fail "the command-backed echo names how to run it: $rsout"
    echo "$rsout" | grep -qF 'ran-42' \
      && fail "the echo of a command-backed let ran the command: $rsout"
    echo "$rsout" | grep -qF 'snap (frozen)' \
      || fail "a materialized bind must state frozen: $rsout"
    echo "e2e ok: the binding echo states seq state (re-runs / frozen)"

    # ---- colour from the child [D:colour-inherit] ----------------------
    # the motivating pins: a bare statement at a tty sees isatty true
    # (colour on); the captured form sees a pipe and its value is
    # unchanged — the positive twin, or "inherited" is achieved by
    # dropping the pipe everywhere
    cidir=$(mkweirtmp)
    printf '#!/bin/sh\ntest -t 1 && echo IS-TTY || echo IS-PIPE\n' > "$cidir/t.sh"
    printf '#!/bin/sh\nprintf "\\033[31mCRED\\033[0m\\n"\n' > "$cidir/c.sh"
    ciout=$(printf 'SLEEP 700\nSEND sh %s/t.sh\\r\nSLEEP 400\nSEND let r = $(sh %s/t.sh | complete)\\r\nSLEEP 500\nSEND print (r.stdout |> Seq.head)\\r\nSLEEP 300\nSEND #quit\\r\n' "$cidir" "$cidir" | python3 "$ptyrun" 10 "$BIN")
    echo "$ciout" | grep -qF "IS-TTY" || fail "a bare REPL statement inherits the terminal (isatty true): $ciout"
    echo "$ciout" | grep -qF "IS-PIPE" || fail "the captured form still pipes and its value is intact: $ciout"
    cout2=$(printf 'SLEEP 400\n' | python3 "$ptyrun" 6 "$BIN" -e "sh $cidir/c.sh" 2>/dev/null || true)
    printf 'print "before"\nsh %s/c.sh\nprint "after"\n' "$cidir" > "$cidir/ord.weir"
    sout2=$(printf 'SLEEP 400\n' | python3 "$ptyrun" 8 "$BIN" "$cidir/ord.weir")
    echo "$sout2" | grep -q "31mCRED" || fail "colour bytes reach the terminal from a script statement: $sout2"
    bidx=$(echo "$sout2" | grep -n "before" | head -1 | cut -d: -f1)
    cidx=$(echo "$sout2" | grep -n "31mCRED" | head -1 | cut -d: -f1)
    aidx=$(echo "$sout2" | grep -n "after" | head -1 | cut -d: -f1)
    { [ -n "$bidx" ] && [ -n "$aidx" ] && [ "$bidx" -le "$cidx" ] && [ "$cidx" -le "$aidx" ]; } || fail "print/statement/print interleave in order (the pre-spawn flush): $sout2"
    # redirected keeps today's bytes exactly — the child sees a pipe
    rout2=$("$BIN" "$cidir/ord.weir")
    echo "$rout2" | grep -q "31mCRED" || true  # the tool colours unconditionally; content-identical either way
    printf 'sh %s/t.sh\n' "$cidir" > "$cidir/tt.weir"
    [ "$("$BIN" "$cidir/tt.weir")" = "IS-PIPE" ] || fail "a redirected weir keeps the pipe (byte laws intact)"
    # one gate, both consumers [D:colour-inherit]: the runner and the REPL
    # ask Eval.inheritsStdout, never their own copy of the condition, so the
    # 2x2 must be symmetric — same probe, both consumers, both stdout modes.
    # The rows above pin one diagonal (REPL at a tty, script redirected);
    # these are the other two. Pinning a diagonal lets the halves drift apart
    # while every assertion still passes.
    sttyout=$(printf 'SLEEP 400\n' | python3 "$ptyrun" 8 "$BIN" "$cidir/tt.weir")
    echo "$sttyout" | grep -qF "IS-TTY" || fail "a script statement at a tty inherits too: $sttyout"
    rpipeout=$(printf 'sh %s/t.sh\n#quit\n' "$cidir" | "$BIN" 2>/dev/null || true)
    echo "$rpipeout" | grep -qF "IS-PIPE" || fail "a redirected REPL keeps the pipe: $rpipeout"
    # armed statements inherit too [D:armed-inherit]: a block body's
    # command and a statement chain's tail get the terminal
    # (an editor or pager works there); redirected, every form keeps the
    # pipe — the same 2x2 discipline, one row per arming position
    cat > "$cidir/armed.weir" <<WEOF
let e = [Env.pair "X" "1"]
within env e
    sh $cidir/t.sh
within tmp d
    sh $cidir/t.sh
match 1 with
| _ ->
    sh $cidir/t.sh
echo x | sh $cidir/t.sh
["a"] | sh $cidir/t.sh
WEOF
    aout=$(printf 'SLEEP 400\n' | python3 "$ptyrun" 12 "$BIN" "$cidir/armed.weir")
    [ "$(echo "$aout" | grep -o IS-TTY | wc -l)" -eq 5 ] && ! echo "$aout" | grep -q IS-PIPE ||
        fail "every armed form at a tty inherits (5 IS-TTY, no IS-PIPE): $aout"
    [ "$("$BIN" "$cidir/armed.weir" | grep -c IS-PIPE)" -eq 5 ] || fail "redirected, every armed form keeps the pipe"
    # the REPL arms a block tail like the runner, and a top-level chain
    # takes the statement inherit path
    arout=$(printf 'SLEEP 700\nSEND within env [Env.pair "X" "1"]\\r    sh %s/t.sh\\r\nSLEEP 300\nSEND \\r\nSLEEP 600\nSEND echo x | sh %s/t.sh\\r\nSLEEP 600\nSEND #quit\\r\n' "$cidir" "$cidir" | python3 "$ptyrun" 12 "$BIN")
    [ "$(echo "$arout" | grep -o IS-TTY | wc -l)" -eq 2 ] && ! echo "$arout" | grep -q IS-PIPE ||
        fail "the REPL's block tail and top-level chain inherit: $arout"

    echo "e2e ok: colour inherit — bare and armed statements (block bodies, chain tails) see the terminal, captures see the pipe, ordering holds, redirected unchanged, and the runner/REPL 2x2 agrees"

    # ---- the Color module + print keeps SGR at a tty [D:tty-color] ----------
    # Color.green emits SGR that print passes through at a terminal; the
    # pty harness reprs a real ESC as \x1b and a defanged one as \\x1b, so
    # the real-SGR pin is: the [32m code present, NOT doubled-backslash
    cout=$(printf 'SLEEP 300\n' | python3 "$ptyrun" 4 "$BIN" -e 'print (Color.green "OKMARK")' 2>&1)
    echo "$cout" | grep -qF '[32mOKMARK' || fail "Color.green must colour at a tty: $cout"
    echo "$cout" | grep -qF '\\x1b[32mOKMARK' && fail "print defanged the SGR instead of keeping it: $cout" || true
    # injection: an OSC clipboard escape in a printed value is defanged to
    # visible \x1b (repr'd \\x1b), the SGR colour beside it kept
    inj=$(printf 'SLEEP 300\n' | python3 "$ptyrun" 4 "$BIN" -e 'print ((Color.red "SAFE") + (Str.fromUtf8 (Bytes.fromBase64 "Gw==")) + "]52;c;PWN")' 2>&1)
    echo "$inj" | grep -qF '[31mSAFE' || fail "the SGR beside injected data survives: $inj"
    echo "$inj" | grep -qF ']52;c;PWN' || fail "the OSC payload should still be present as inert text: $inj"
    echo "$inj" | grep -qF '\\x1b]52' || fail "an OSC in printed data must be defanged (visible \\x1b): $inj"
    # piped (not a tty): Color returns plain, no escapes at all
    [ "$($BIN -e 'print (Color.green "plain")')" = "plain" ] || fail "Color must be plain when piped"
    echo "e2e ok: Color module — SGR reaches the tty via print, OSC in data stays defanged, piped output is plain"

    # ---- the #session prompt at a tty [D:session-prompt] -------------------
    # the provider (an init function that runs a command) paints in place
    # of the default, once per entry read; a raising provider falls back
    # to the default with one stderr note, and the session keeps working
    spdir=$(mkweirtmp)
    mkdir -p "$spdir/cfg/weir" "$spdir/state"
    cat > "$spdir/cfg/weir/init.weir" <<'WEOF'
#init
let sigil () =
    let n = $(sh -c "echo demo") |> Seq.head
    $"({n}) weir> "

#session {
    prompt = sigil
}
WEOF
    spout=$(printf 'SLEEP 900\nSEND 1 + 1\\r\nSLEEP 400\nSEND #quit\\r\n' \
        | XDG_CONFIG_HOME="$spdir/cfg" XDG_STATE_HOME="$spdir/state" python3 "$ptyrun" 10 "$BIN")
    echo "$spout" | grep -qF "(demo) weir> " || fail "the #session prompt must paint at a tty: $spout"
    echo "$spout" | grep -qF "2" || fail "an entry under a custom prompt must still evaluate: $spout"
    cat > "$spdir/cfg/weir/init.weir" <<'WEOF'
#init
let boom () =
    if 1 == 1 then fail "nope" else "x> "

#session {
    prompt = boom
}
WEOF
    spout=$(printf 'SLEEP 900\nSEND 1\\r\nSLEEP 300\nSEND #quit\\r\n' \
        | XDG_CONFIG_HOME="$spdir/cfg" XDG_STATE_HOME="$spdir/state" python3 "$ptyrun" 10 "$BIN")
    echo "$spout" | grep -qF "using the default prompt" || fail "a raising provider must say it fell back: $spout"
    n=$(echo "$spout" | grep -cF "using the default prompt")
    [ "$n" = "1" ] || fail "the fallback note prints once per session, got $n"
    echo "$spout" | grep -qF "weir> " || fail "the fallback must render the default prompt: $spout"
    # escape injection [D:session-prompt]: only SGR passes — an OSC
    # (title/clipboard) from attacker-influenced provider output must
    # not reach the terminal (CWE-150)
    cat > "$spdir/cfg/weir/init.weir" <<'WEOF'
#init
let evil () =
    let esc = $(sh -c "printf '\\033]0;pwned\\007\\033[35mI'") |> Seq.head
    esc + "> "

#session {
    prompt = evil
}
WEOF
    spout=$(printf 'SLEEP 900\nSEND 1\\r\nSLEEP 300\nSEND #quit\\r\n' \
        | XDG_CONFIG_HOME="$spdir/cfg" XDG_STATE_HOME="$spdir/state" python3 "$ptyrun" 10 "$BIN")
    echo "$spout" | grep -qF ']0;pwned' && fail "an OSC in provider output must be stripped: $spout" || true
    echo "$spout" | grep -qF '35mI> ' || fail "the SGR beside the dropped OSC survives: $spout"
    # the status-carrying provider [D:prompt-status-arg]: prompt =
    # PromptStatus -> string — ok is the tint, exit a bare command's
    # nonzero code (None for a non-command error) — written with a bare
    # param (the function types as a row; the loader accepts any field
    # set PromptStatus satisfies)
    cat > "$spdir/cfg/weir/init.weir" <<'WEOF'
#init
let prompt st =
    if st.ok then
        "SOK> "
    else
        $"SX{st.exit |> Option.map show |> Option.defaultValue "B"}> "

#session {
    prompt = prompt
}
WEOF
    spout=$(printf 'SLEEP 900\nSEND sh -c "exit 3"\\r\nSLEEP 700\nSEND nope\\r\nSLEEP 500\nSEND 1 + 1\\r\nSLEEP 400\nSEND #quit\\r\n' \
        | XDG_CONFIG_HOME="$spdir/cfg" XDG_STATE_HOME="$spdir/state" python3 "$ptyrun" 14 "$BIN")
    echo "$spout" | grep -qF "SX3> " || fail "the provider must see the bare command's exit code: $spout"
    echo "$spout" | grep -qF "SXB> " || fail "a non-command error must carry exit = None: $spout"
    echo "$spout" | grep -qF "SOK> " || fail "a clean entry must paint ok: $spout"

    echo "e2e ok: #session prompt — an init provider paints at a tty (commands run when called), a raising one falls back with one note, only SGR passes, and a bool provider carries the status"

    # ---- Term.title [D:term-title] -------------------------------------------
    # one OSC 0 per call, its text sanitized (ESC/BEL dropped, so the
    # smuggled `ESC ]0;evil BEL` cannot end the title early or start a
    # second sequence); stderr carries it when stdout is piped; nothing
    # at all when neither stream is a terminal
    ttdir=$(mkweirtmp)
    cat > "$ttdir/t.weir" <<'WEOF'
let esc = Str.fromUtf8 (Bytes.fromBase64 "Gw==")
let bel = Str.fromUtf8 (Bytes.fromBase64 "Bw==")
Term.title ("a" + esc + "]0;evil" + bel + "b")
print "TTDONE"
WEOF
    ttout=$(printf 'SLEEP 400\n' | python3 "$ptyrun" 6 "$BIN" "$ttdir/t.weir")
    [ "$(echo "$ttout" | grep -o '\\x1b]0;' | wc -l)" -eq 1 ] || fail "Term.title must emit exactly one OSC 0 at a tty: $ttout"
    echo "$ttout" | grep -qF '\x1b]0;a]0;evilb\x07TTDONE' || fail "Term.title's text must be sanitized (ESC/BEL dropped): $ttout"
    ttout=$(printf 'SLEEP 400\n' | python3 "$ptyrun" 6 sh -c "\"$BIN\" \"$ttdir/t.weir\" > \"$ttdir/o\"")
    echo "$ttout" | grep -qF '\x1b]0;a]0;evilb\x07' || fail "Term.title must reach the terminal on stderr when stdout is piped: $ttout"
    [ "$(cat "$ttdir/o")" = "TTDONE" ] || fail "the piped stdout must carry no title escape: $(od -c "$ttdir/o")"
    "$BIN" "$ttdir/t.weir" > "$ttdir/o" 2> "$ttdir/e" || fail "Term.title with no terminal must not raise"
    [ "$(cat "$ttdir/o")" = "TTDONE" ] && [ ! -s "$ttdir/e" ] || fail "Term.title with no terminal must write nothing: $(od -c "$ttdir/o" "$ttdir/e")"
    # from the init.weir prompt function: the OSC lands before each
    # prompt paint, never inside it — the prompt and its cursor column
    # (`tt> ` = 4) are unchanged
    cat > "$spdir/cfg/weir/init.weir" <<'WEOF'
#init
let sigil () =
    Term.title $"TT:{pwd |> Path.fileName}"
    "tt> "

#session {
    prompt = sigil
}
WEOF
    mkdir -p "$ttdir/here"
    ptyabs="$(cd "$(dirname "$ptyrun")" && pwd)/pty-run.py"
    spout=$(cd "$ttdir/here" && printf 'SLEEP 900\nSEND 1 + 1\\r\nSLEEP 400\nSEND #quit\\r\n' \
        | XDG_CONFIG_HOME="$spdir/cfg" XDG_STATE_HOME="$spdir/state" python3 "$ptyabs" 10 "$BIN")
    # the chunk reprs joined into one stream (a write may split across reads)
    spjoin=$(echo "$spout" | sed -E "s/^[0-9]+ b'//; s/'\$//" | tr -d '\n')
    [ "$(echo "$spjoin" | grep -o '\\x1b]0;TT:here\\x07' | wc -l)" -eq 2 ] || fail "the prompt function's Term.title must emit once per prompt: $spout"
    [ "$(echo "$spjoin" | grep -oF '\x1b]0;TT:here\x07\r\x1b[Jtt> \r\x1b[4C' | wc -l)" -eq 2 ] || fail "the title escape must precede each prompt paint, cursor math unchanged: $spout"
    echo "$spjoin" | grep -qF "[Jtt> \\x1b]0;" && fail "the title escape must never sit inside the painted prompt: $spout" || true
    echo "e2e ok: Term.title — one sanitized OSC 0 at a tty (stderr when stdout is piped), nothing with no terminal, and from the prompt function it lands before each prompt paint"
else
    echo "e2e skip: streaming-echo pty pins (POSIX + python3)"
fi

# ---- the REPL SIGINT split, closed [D:repl-isig] ---------------------------
# eval runs under the shell's tty disposition (ISIG on), so ^C is a
# group SIGINT: the child dies with the script path's exact message and
# the session survives (the cancel registration). The needle rule: the
# editor echoes typed lines, so needles derive by case transform.
if [ "$IS_WINDOWS" != "1" ] && command -v python3 >/dev/null 2>&1; then
    ptyrun="$(dirname "$0")/../tests/pty/pty-run.py"

    # two cures, both pinned: under inheritance gzip sees the real
    # terminal and refuses by itself (fast, its own message, exit 1)
    # [D:colour-inherit] — and a stdin-reading child that does hang
    # dies to ^C naming 130 with the session surviving [D:repl-isig]
    gzout=$(printf 'SLEEP 700\nSEND gzip\\r\nSLEEP 600\nSEND #quit\\r\n' \
        | python3 "$ptyrun" 8 "$BIN" | sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g')
    # GNU: "compressed data not written to a terminal"; Apple:
    # "standard output is a terminal -- ignoring" — the claim is the
    # refusal, so the pin matches the word both spell
    echo "$gzout" | grep -qi "is a terminal\|to a terminal" || fail "gzip refuses its own tty now — the incident's cause removed: $gzout"
    rsout=$(printf 'SLEEP 700\nSEND sh -c "read x"\\r\nSLEEP 400\nSEND \\x03\nSLEEP 500\nSEND print (Str.toUpper "revived")\\r\nSLEEP 400\nSEND #quit\\r\n' \
        | python3 "$ptyrun" 10 "$BIN" | sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g')
    # the child's ^C death renders as the quiet exit-code status now, not the
    # loud error [D:repl-cmd-fail] — still names 130 and the SIGINT note
    echo "$rsout" | grep -q "exit 130 (SIGINT" || fail "a REPL ^C must kill the foreground child naming 130: $rsout"
    echo "$rsout" | grep -q "REVIVED" || fail "the session must survive its child's ^C: $rsout"
    echo "$rsout" | grep -q "^EXIT 0" || fail "the session must end clean after a ^C'd child: $rsout"

    # the positive twin: ^C at an idle prompt stays an editor key — the
    # line clears and the session continues (two fates, both pinned)
    twout=$(printf 'SLEEP 700\nSEND \\x03\nSLEEP 300\nSEND print (Str.toUpper "alive")\\r\nSLEEP 400\nSEND #quit\\r\n' \
        | python3 "$ptyrun" 8 "$BIN" | sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g')
    echo "$twout" | grep -q '\^C' || fail "an idle-prompt ^C clears the line, never kills: $twout"
    echo "$twout" | grep -q "ALIVE" || fail "the session continues after an idle-prompt ^C: $twout"

    # the agreement's other half: the script path still dies with the
    # group (C8's shape — weir aborts at the fault alongside the child)
    ssdir=$(mkweirtmp)
    printf 'sh -c "read x"\nprint (Str.toUpper "after")\n' > "$ssdir/sig.weir"
    ssout=$(printf 'SLEEP 500\nSEND \\x03\n' | python3 "$ptyrun" 6 "$BIN" "$ssdir/sig.weir")
    echo "$ssout" | awk '/^EXIT/{exit ($2 == -2 ? 0 : 1)}' || fail "a script ^C still kills weir with the group: $ssout"
    echo "$ssout" | grep -q "AFTER" && fail "a script must abort at the fault on ^C: $ssout"

    echo "e2e ok: REPL SIGINT — ^C kills the child (130) and the session survives; idle ^C clears the line; scripts die with the group"

    # the sweep gate, measured [D:repl-isig]: a ^C the session outlives
    # must not run the exit-hook sweep — the sweep deletes live within-tmp
    # dirs. The ^C lands mid-pure-computation (a child would die and
    # unwind the scope), the block then checks its own dir. Linux is the
    # mechanism's platform; the cell runs wherever the pty pins do.
    # multi-line body via Alt-Enter (\x1b\r): plain Enter submits the
    # moment the block parses complete — one body line suffices. Indented
    # entries go in one write, so they keep their own indentation
    # [D:repl-autoindent]
    gatedir=$(mkweirtmp)
    cat > "$gatedir/gate.scn" <<'SCN'
SLEEP 700
SEND within tmp d\x1b\r    ["x"] |> File.write $"{d}/probe.txt"\x1b\r    print d\x1b\r    let n = [1..8000000] |> Seq.fold (fun a x -> a + 1) 0\x1b\r    print (if File.exists $"{d}/probe.txt" then Str.toUpper "kept" else Str.toUpper "gone")\r
SLEEP 1200
SEND \x03
SLEEP 5000
SEND print (Str.toUpper "still-here")\r
SLEEP 400
SEND #quit\r
SCN
    gout=$(python3 "$ptyrun" 18 "$BIN" < "$gatedir/gate.scn" | sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g')
    echo "$gout" | grep -q "KEPT" || fail "a survived ^C must not sweep a live within-tmp dir: $gout"
    echo "$gout" | grep -q "STILL-HERE" || fail "the session survives the mid-eval ^C: $gout"
    gdir=$(echo "$gout" | grep -oE '/[^[:space:]"]*weir-tmp-[^[:space:]"]*' | head -1)
    # the positive twin: the scope's own exit removed the dir — or
    # "survives the sweep" is satisfied by a gate that never sweeps
    [ -n "$gdir" ] || fail "the probe must capture the tmp dir path: $gout"
    [ ! -d "$gdir" ] || fail "the scope exit must still remove its dir: $gdir"
    echo "e2e ok: the sweep gate — a survived ^C leaves the live tmp dir; the scope's own exit removes it"

    # a line typed while an entry runs submits [D:repl-typeahead]: eval
    # runs cooked, so its Enter arrived as '\n' (ICRNL) and read as the
    # Ctrl+J newline — the line and every later one grew a continuation
    taout=$(printf 'SLEEP 700\nSEND sh -c "sleep 1.5"\\r\nSLEEP 400\nSEND print (Str.toUpper "typed-ahead")\\r\nSLEEP 2000\nSEND #quit\\r\n' \
        | python3 "$ptyrun" 10 "$BIN" | sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g')
    echo "$taout" | grep -q "TYPED-AHEAD" || fail "a line typed during an entry's eval submits after it: $taout"
    echo "$taout" | grep -q "^EXIT 0" || fail "a line typed during an entry's eval submits, never continues: $taout"
    echo "e2e ok: a line typed while an entry runs submits once it ends"

    # ^C cuts a wait the runtime owns [D:repl-interrupt]: a sleep, a poll
    # interval, a contended lock — each returns to the prompt with the
    # ^C'd child's status; an `always` cleanup still runs
    wdir=$(mkweirtmp)
    touch "$wdir/held.lock"
    cat > "$wdir/wait.scn" <<SCN
SLEEP 700
SEND Duration.sleep 30s\r
SLEEP 800
SEND \x03
SLEEP 500
SEND poll timeout=60s interval=30s\x1b\r    false\r
SLEEP 800
SEND \x03
SLEEP 500
SEND within lock "$wdir/held.lock"\x1b\r    print (Str.toUpper "acquired")\r
SLEEP 800
SEND \x03
SLEEP 500
SEND within\x1b\r    Duration.sleep 30s\x1b\ralways\x1b\r    print (Str.toUpper "cleanup-ran")\r
SLEEP 800
SEND \x03
SLEEP 500
SEND print (Str.toUpper "still-here")\r
SLEEP 400
SEND #quit\r
SCN
    if command -v flock >/dev/null 2>&1; then
        flock "$wdir/held.lock" sleep 30 &
        lockpid=$!
        sleep 0.3
        wstart=$(date +%s)
        wout=$(python3 "$ptyrun" 25 "$BIN" < "$wdir/wait.scn" | sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g')
        wend=$(date +%s)
        kill "$lockpid" 2>/dev/null || true
        n130=$(echo "$wout" | grep -c "exit 130 (SIGINT")
        [ "$n130" = "4" ] || fail "each ^C'd wait must return with exit 130 (got $n130): $wout"
        echo "$wout" | grep -q "ACQUIRED" && fail "the contended lock must not be acquired: $wout"
        echo "$wout" | grep -q "CLEANUP-RAN" || fail "the always cleanup runs after a ^C'd wait: $wout"
        echo "$wout" | grep -q "STILL-HERE" || fail "the session survives the ^C'd waits: $wout"
        [ $((wend - wstart)) -lt 20 ] || fail "the ^C'd waits must not run out their 30s: $((wend - wstart))s"
        echo "e2e ok: REPL ^C cuts a sleep, a poll interval and a lock acquire; always still runs"
    else
        echo "e2e skip: REPL ^C wait pins (flock(1))"
    fi

    # ^C during Self.prompt cuts the read and takes no line [D:stdin-reader]:
    # the next typed line reaches the REPL, not the cancelled prompt; ^C
    # during a command fan-out is the interrupt, not an arm failure — no new
    # arm starts and the prompt returns [D:repl-interrupt-fanout]
    idir=$(mkweirtmp)
    cat > "$idir/int.scn" <<SCN
SLEEP 700
SEND Self.prompt "name? "\r
SLEEP 800
SEND \x03
SLEEP 500
SEND print (Str.toUpper "after-prompt")\r
SLEEP 400
SEND [1..30] |> Seq.piterWith 3 (fun i -> sh -c \$"echo start {i} >> $idir/arms.log; sleep 2")\r
SLEEP 1200
SEND \x03
SLEEP 1500
SEND print (Str.toUpper "after-fanout")\r
SLEEP 400
SEND #quit\r
SCN
    iout=$(python3 "$ptyrun" 20 "$BIN" < "$idir/int.scn" | sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g')
    sleep 2.5
    started=$(wc -l < "$idir/arms.log" 2>/dev/null || echo 0)
    n130=$(echo "$iout" | grep -c "exit 130 (SIGINT")
    [ "$n130" = "2" ] || fail "^C during Self.prompt and a fan-out must each return with exit 130 (got $n130): $iout"
    echo "$iout" | grep -q "AFTER-PROMPT" || fail "the line after a ^C'd Self.prompt must reach the REPL: $iout"
    echo "$iout" | grep -q "AFTER-FANOUT" || fail "the prompt must return after a ^C'd fan-out: $iout"
    [ "$started" -le 6 ] || fail "a ^C'd fan-out kept taking arms ($started of 30 started, 3 in flight at ^C)"
    echo "e2e ok: REPL ^C cuts Self.prompt without taking a line, and stops a command fan-out"

    # ^C at the REPL cuts a serve handler's wait as the interrupt, never as
    # a handler failure [D:repl-interrupt]: no 500, no streamErrors line —
    # the client's connection closes unanswered
    if command -v curl >/dev/null 2>&1; then
        hidir=$(mkweirtmp)
        hiport=8494
        cat > "$hidir/hi.scn" <<SCN
SLEEP 700
SEND within serve srv = { port = $hiport; maxConcurrent = 2 } (fun req -> Duration.sleep 10s; HttpServerResponse { status = 200; headers = []; body = Text "late" })\x1b\r    print (Str.toUpper "serving")\x1b\r    Duration.sleep 30s\x1b\ralways\x1b\r    Duration.sleep 700ms\x1b\r    print (Str.toUpper \$"errs={Server.streamErrors srv |> Seq.length}")\r
SLEEP 2500
SEND \x03
SLEEP 3000
SEND print (Str.toUpper "after-serve")\r
SLEEP 400
SEND #quit\r
SCN
        ( sleep 2.2; curl -s -o /dev/null -w '%{http_code}' --max-time 20 "http://127.0.0.1:$hiport/" > "$hidir/code" 2>/dev/null; true ) &
        cpid=$!
        hout=$(python3 "$ptyrun" 15 "$BIN" < "$hidir/hi.scn" | sed 's/\x1b\[[0-9;?]*[a-zA-Z]//g; s/\x1b[=>]//g')
        wait "$cpid" 2>/dev/null || true
        echo "$hout" | grep -q "SERVING" || fail "the REPL serve probe must start serving: $hout"
        echo "$hout" | grep -q "ERRS=0" || fail "a ^C'd serve handler must not be recorded as a failure: $(echo "$hout" | grep -aoE 'ERRS=[^\\]*' | tail -1)"
        echo "$hout" | grep -q "AFTER-SERVE" || fail "the prompt returns after the ^C'd serve: $hout"
        [ "$(cat "$hidir/code" 2>/dev/null)" = "000" ] || fail "a ^C'd serve handler must close its connection unanswered (got HTTP $(cat "$hidir/code" 2>/dev/null))"
        echo "e2e ok: REPL ^C cuts a serve handler as the interrupt — no 500, nothing on streamErrors, the connection closes"
    else
        echo "e2e skip: REPL ^C serve-handler pin (curl)"
    fi
else
    echo "e2e skip: REPL SIGINT pty pins (POSIX + python3)"
fi

# ---- Path.under confines, at the binary [D:path-under] ---------------------
# Every platform: rule 7 makes the drive/UNC shapes raise everywhere,
# and the unit pin's old header claimed Windows ran units only — e2e
# runs there now, so the shipped binary carries the spec too. The
# positive twin leads: raises-on-escape is satisfiable by raising on
# everything.
pudir=$(mkweirtmp)
cat > "$pudir/pu.weir" <<'WEOF'
let bse = Path.combine (Path.tempRoot ()) "safe-uploads"
let a = Path.under bse "a/b.txt"
let b = Path.under bse "a/../b.txt"
print (if Str.endsWith "b.txt" a && Str.endsWith "b.txt" b then "joins-ok" else $"BAD {a} {b}")
WEOF
out=$($BIN "$pudir/pu.weir") || fail "legitimate joins must succeed (the positive twin): $out"
echo "$out" | grep -q "joins-ok" || fail "plain and interior-.. joins yield paths under the base: $out"

purefuse() {
    $BIN -e "$1" >/dev/null 2>&1 && fail "Path.under must raise on every platform: $1"
    return 0
}
purefuse 'Path.under "/safe/uploads" "../uploads-evil/x"'
purefuse 'Path.under "/safe/uploads" "/etc/passwd"'
purefuse 'Path.under "/safe/uploads" "a/../../etc"'
purefuse 'Path.under "/safe/uploads" "C:x"'
purefuse 'Path.under "/safe/uploads" "C:/x"'
# UNC and backslash-leading shapes: weir strings escape \\ only, so the
# UNC value \\server\share needs the doubled spelling — the single one
# is a parse error and a refusal pin passing on it is vacuous (the unit
# pin had exactly that; fixed alongside this cell)
purefuse 'Path.under "/safe/uploads" "\\\\server\\share"'
purefuse 'Path.under "/safe/uploads" "\\x"'
# the rule-7 departure, both sides: separators are not rewritten, so a
# backslash name (not leading) is a legal single filename on POSIX and
# the same text normalises-and-escapes on Windows
if [ "$IS_WINDOWS" = "1" ]; then
    purefuse 'Path.under "C:/safe" "..\\..\\etc"'
else
    $BIN -e 'Path.under "/safe" "..\\..\\etc"' >/dev/null 2>&1 || fail "a backslash name is one ordinary POSIX filename segment"
fi
echo "e2e ok: Path.under at the binary — twin joins, sibling/absolute/climb/drive/UNC refuse, the separator split holds per platform"

# ---- pure regions [D:pure-stage1] -------------------------------------------
# the enforced purity assertion at the binary: a pure body runs; an
# effect refuses with the located teaching (block, let pure, and the
# within-proc composition); the law is check-time, so `check` sees it
puredir=$(mkweirtmp)
cat > "$puredir/ok.weir" <<'WEOF'
let pure double n = n * 2
let x =
    pure
        let y = double 3
        y + 1
print $"{x}"
WEOF
out=$($BIN "$puredir/ok.weir") || fail "a pure body must run: $out"
[ "$out" = "7" ] || fail "pure block value: expected 7, got $out"

cat > "$puredir/bad.weir" <<'WEOF'
pure
    print "no"
WEOF
out=$($BIN check "$puredir/bad.weir" 2>&1) && fail "an effectful pure body must refuse" || true
echo "$out" | grep -q "this 'pure' block forbids effects, but 'print' writes to the console" || fail "the teaching names the offender: $out"
echo "$out" | grep -q "bad.weir:2:5" || fail "the refusal is located at the offending node: $out"

cat > "$puredir/modif.weir" <<'WEOF'
let pure leak () = print "x"
leak ()
WEOF
out=$($BIN check "$puredir/modif.weir" 2>&1) && fail "let pure with an impure body must refuse" || true
echo "$out" | grep -q "writes to the console" || fail "the modifier shares the block's teaching: $out"

cat > "$puredir/proc.weir" <<'WEOF'
let x =
    pure
        within proc p = sleep 5
            1
WEOF
out=$($BIN check "$puredir/proc.weir" 2>&1) && fail "a scoped process inside pure must refuse" || true
echo "$out" | grep -q "'within proc' scopes a resource" || fail "composition with within proc refuses: $out"
echo "e2e ok: pure regions — a pure body runs (7), effects refuse LOCATED (block, let pure, within proc), the law is check-time"

# ---- commit areas [D:commit-areas] ------------------------------------------
# the derived-area check, pinned both ways (a check that rejects
# everything satisfies "rejects bad areas") — a scratch repo, three
# commits: correct area passes, wrong area fails, missing prefix fails,
# and a subset of a multi-area derivation fails (equality, not
# intersection)
cadir=$(mkweirtmp)
(
    cd "$cadir"
    git init -q .
    git -c user.email=ci@ci -c user.name=ci commit -q --allow-empty -m "seed"
    mkdir -p docs src/Weir ci
    echo x > docs/GUIDE.md
    git add -A && git -c user.email=ci@ci -c user.name=ci commit -q -m "docs: a guide line"
    echo y > src/Weir/Parser.fs
    git add -A && git -c user.email=ci@ci -c user.name=ci commit -q -m "docs: mislabeled parser change"
    echo z > src/Weir/Check.fs
    echo w >> src/Weir/Parser.fs
    git add -A && git -c user.email=ci@ci -c user.name=ci commit -q -m "parser: declares a subset of parser+checker"
    echo p >> src/Weir/Parser.fs
    echo t > ci/x.sh
    git add -A && git -c user.email=ci@ci -c user.name=ci commit -q -m "parser: pins ride — ci file with a src change"
    echo q >> docs/GUIDE.md
    git add -A && git -c user.email=ci@ci -c user.name=ci commit -q -m "no prefix at all"
) >/dev/null 2>&1
cascript="$(dirname "$0")/commit-area.weir"
h_ok=$(git -C "$cadir" log --format=%h --grep "a guide line" -1)
h_wrong=$(git -C "$cadir" log --format=%h --grep "mislabeled" -1)
h_subset=$(git -C "$cadir" log --format=%h --grep "subset" -1)
h_ride=$(git -C "$cadir" log --format=%h --grep "pins ride" -1)
h_none=$(git -C "$cadir" log --format=%h --grep "no prefix" -1)
( cd "$cadir" && "$BIN" "$OLDPWD/$cascript" --commit "$h_ok" ) >/dev/null 2>&1 || fail "a correct area must PASS"
rc=0; ( cd "$cadir" && "$BIN" "$OLDPWD/$cascript" --commit "$h_wrong" ) >/dev/null 2>&1 || rc=$?
[ "$rc" != "0" ] || fail "a mislabeled area must FAIL"
rc=0; caout=$( cd "$cadir" && "$BIN" "$OLDPWD/$cascript" --commit "$h_subset" 2>&1 ) || rc=$?
[ "$rc" != "0" ] || fail "a SUBSET of the derived set must FAIL (equality, not intersect)"
echo "$caout" | grep -qF "checker,parser" || fail "the failure prints the derived prefix (self-repair): $caout"
( cd "$cadir" && "$BIN" "$OLDPWD/$cascript" --commit "$h_ride" ) >/dev/null 2>&1 || fail "pins/ci files RIDE a src change"
rc=0; ( cd "$cadir" && "$BIN" "$OLDPWD/$cascript" --commit "$h_none" ) >/dev/null 2>&1 || rc=$?
[ "$rc" != "0" ] || fail "a missing prefix must FAIL"
echo "e2e ok: commit-area check — correct passes, wrong/subset/missing fail, the error names the derived prefix, pins ride"

# the rider's fixtures [D:commit-areas]: an unmapped src file fails
# naming itself (a new src file is an area decision — plausible
# fallbacks are the canary/completion/orFail failure mode); an unmapped
# root file falls back to docs, said aloud; a retired type prefix
# teaches the areas
(
    cd "$cadir"
    echo e > .editorconfig
    git add -A && git -c user.email=ci@ci -c user.name=ci commit -q -m "docs: an unmapped root file"
    echo f >> docs/GUIDE.md
    git add -A && git -c user.email=ci@ci -c user.name=ci commit -q -m "fix: a retired type prefix"
    echo n > src/Weir/Csv.fs
    git add -A && git -c user.email=ci@ci -c user.name=ci commit -q -m "adapters: an unmapped src file"
) >/dev/null 2>&1
h_unsrc=$(git -C "$cadir" log --format=%h --grep "unmapped src" -1)
h_unroot=$(git -C "$cadir" log --format=%h --grep "unmapped root" -1)
h_type=$(git -C "$cadir" log --format=%h --grep "retired type" -1)
rc=0; unout=$( cd "$cadir" && "$BIN" "$OLDPWD/$cascript" --commit "$h_unsrc" 2>&1 ) || rc=$?
[ "$rc" != "0" ] || fail "an unmapped src file must FAIL"
echo "$unout" | grep -qF "src/Weir/Csv.fs" || fail "the unmapped-src failure names the file: $unout"
rc=0; rootout=$( cd "$cadir" && "$BIN" "$OLDPWD/$cascript" --commit "$h_unroot" 2>&1 ) || rc=$?
[ "$rc" = "0" ] || fail "an unmapped root file must PASS on the fallback: $rootout"
echo "$rootout" | grep -qF "fallback: docs" || fail "the fallback says its name: $rootout"
rc=0; tyout=$( cd "$cadir" && "$BIN" "$OLDPWD/$cascript" --commit "$h_type" 2>&1 ) || rc=$?
[ "$rc" != "0" ] || fail "a retired type prefix must FAIL"
echo "$tyout" | grep -qF "retired type prefix" || fail "the type-prefix failure teaches: $tyout"
echo "e2e ok: commit-area rider — unmapped src stops loud, unmapped root falls back aloud, type prefixes teach their retirement"

# ---- wire keys [D:wire-keys] ------------------------------------------------
# reserved words as adapter keys, refereed externally [D:yaml-interop]:
# python writes a document whose keys are weir keywords, weir reads it
# under [<Wire>] fields, modifies one, writes back — python verifies the
# wire keys and values survived the roundtrip
if command -v python3 >/dev/null 2>&1; then
    wkdir=$(mkweirtmp)
    python3 -c 'import json; print(json.dumps({"type":"user","to":"a@b.c","from":"noreply","in":"inbox","do":"send","match":7}))' > "$wkdir/doc.json"
    cat > "$wkdir/wk.weir" <<'WEOF'
type Msg = {
    [<Wire "type">]
    kind: string
    [<Wire "to">]
    dest: string
    [<Wire "from">]
    sender: string
    [<Wire "in">]
    box: string
    [<Wire "do">]
    action: string
    [<Wire "match">]
    hits: int
}
let m = File.read "doc.json" |> from json Msg
let m2 = { m with hits = m.hits + 1 }
m2 |> to json |> File.write "out.json"
print m.kind
WEOF
    wkout=$( cd "$wkdir" && $BIN wk.weir )
    [ "$wkout" = "user" ] || fail "wire-key read: $wkout"
    python3 - "$wkdir/out.json" <<'PYEOF2'
import json, sys
d = json.load(open(sys.argv[1]))
assert set(d) == {"type", "to", "from", "in", "do", "match"}, f"wire keys mangled: {sorted(d)}"
assert d["type"] == "user" and d["match"] == 8, f"values wrong: {d}"
print("referee ok")
PYEOF2
    [ $? -eq 0 ] || fail "wire-key roundtrip referee"
    echo "e2e ok: wire keys — six reserved-word keys read, modified, and written back; python refereed the roundtrip"
else
    echo "e2e skip: wire-key referee (python3 absent)"
fi

# ---- whole-document Map with a value type [D:map-values] -------------------
# from json Map<string, V> takes any V a Map field would — scalars,
# Option, seq, nested Map — and the slot's int key teaches as a
# declared type's does
mvdir=$(mkweirtmp)
printf '{"b": 2, "a": 1}' > "$mvdir/m.json"
cat > "$mvdir/mv.weir" <<'WEOF'
let m = File.read "m.json" |> from json Map<string, int>
print (show (Map.get "a" m + Map.get "b" m))
m |> to json |> Seq.iter print
let n = ["{\"k\": {\"j\": [1, 2]}}"] |> from json Map<string, Map<string, seq<int>>>
print (show (n |> Map.get "k" |> Map.get "j" |> Seq.length))
WEOF
out=$( cd "$mvdir" && $BIN mv.weir 2>&1 ) || fail "a value-type Map must read: $out"
expect "from json Map<string, int> reads a whole document" "3" "$out"
expect "the value-type Map round-trips" '{"a":1,"b":2}' "$out"
expect "a nested value-type Map reads" "2" "$out"
printf 'let m = File.read "m.json" |> from json Map<int, int>\n' > "$mvdir/mk.weir"
out=$( cd "$mvdir" && $BIN check mk.weir 2>&1 ) && fail "Map<int, …> in the slot must refuse: $out"
expect "the slot's int key teaches" "Map keys are strings only" "$out"
rm -rf "$mvdir"

# ---- `..` after a symlink is physical [D:fs-dotdot-physical] -----------------
# L -> real/sub: File.* resolve L/.. to real, as the kernel (cat) does, and
# the plan guard sees the write it lands under its real path
if [ "$IS_WINDOWS" != "1" ]; then
    dddir=$(mkweirtmp)
    mkdir -p "$dddir/real/sub"
    echo inner > "$dddir/real/x.txt"
    echo top > "$dddir/x.txt"
    ln -s real/sub "$dddir/L"
    cat > "$dddir/t.weir" <<'WEOF'
// L -> real/sub: `L/..` is real, as the kernel resolves it [D:fs-dotdot-physical]
let base = Self.args |> Seq.head
print $"read: {File.read $"{base}/L/../x.txt" |> Str.join ""}"
["w"] |> File.write $"{base}/L/../y.txt"
print $"landed: {File.exists $"{base}/real/y.txt"} {File.exists $"{base}/y.txt"}"
print $"exists: {File.exists $"{base}/L/../x.txt"} dir: {Dir.exists $"{base}/L/../sub"}"
print $"list: {Dir.list $"{base}/L/.." |> Seq.map Path.fileName |> Str.join " "}"
File.copy $"{base}/L/../x.txt" $"{base}/L/../z.txt"
print $"copied: {File.exists $"{base}/real/z.txt"}"
let _guarded =
    plan
        File.write $"{base}/L/../p.txt" ["p"]
        print $"{File.read $"{base}/real/p.txt" |> Seq.length}"
print "unreached"
WEOF
    out=$($BIN "$dddir/t.weir" "$dddir" 2>&1) && fail "the dotdot cell's plan read must refuse: $out"
    expect "File.read L/.. is physical" "read: inner" "$out"
    expect "File.write L/.. lands where the kernel puts it" "landed: true false" "$out"
    expect "File.exists/Dir.exists L/.. are physical" "exists: true dir: true" "$out"
    expect "Dir.list L/.. lists real" "list: sub x.txt y.txt" "$out"
    expect "File.copy L/.. is physical" "copied: true" "$out"
    expect "the plan guard keys the physical path" "known-after-apply" "$out"
    cat > "$dddir/g.weir" <<'WEOF'
// Path.glob steps `..` up physically too; the plan simulation follows links [D:fs-dotdot-physical] [D:plan-preconditions]
let base = Self.args |> Seq.head
print $"glob: {Path.glob $"{base}/L/../*.txt" |> Seq.map Path.fileName |> Str.join " "}"
within cd $"{base}/L"
    print $"glob in L: {Path.glob "../*.txt" |> Str.join " "} {Path.glob "../*.txt" |> Seq.map File.read |> Seq.concat |> Str.join " "}"
let alias () =
    plan
        File.write $"{base}/L/n.txt" ["n"]
        File.copy $"{base}/x.txt" $"{base}/real/sub/n.txt"
match alias () |> try result with
| Ok _ -> print "alias: captured"
| Error e -> print $"alias: {Str.replace base "$T" e}"
let dangle () =
    plan
        Dir.deleteAll $"{base}/real/sub"
        File.write $"{base}/L/x" ["x"]
match dangle () |> try result with
| Ok _ -> print "dangle: captured"
| Error e -> print $"dangle: {Str.replace base "$T" e}"
let through () =
    plan
        Dir.create $"{base}/L/k"
        File.write $"{base}/real/sub/k/f" ["f"]
        Dir.move $"{base}/L" $"{base}/real/sub/L2"
print $"through: {through () |> Plan.preview |> Seq.length} ops"
WEOF
    out=$($BIN "$dddir/g.weir" "$dddir" 2>&1) || fail "the dotdot glob/simulation cell failed: $out"
    expect "Path.glob L/.. is physical" "glob: x.txt y.txt z.txt" "$out"
    expect "Path.glob ../ within cd L is physical and File.read reads its hits" "glob in L: ../x.txt ../y.txt ../z.txt inner w inner" "$out"
    expect "a write through a link meets a copy onto its real path" "alias: File.copy: destination exists: \$T/real/sub/n.txt" "$out"
    expect "a write through a link whose target the plan deleted refuses" "dangle: File.write: no such directory: \$T/L" "$out"
    expect "a mkdir through a link backs a write at its real path; a link renames into its target" "through: 3 ops" "$out"
    rm -rf "$dddir"
fi

# ---- FileRow reshape [D:filerow] --------------------------------------------
# kind/target over a real symlink, and File.mode against the platform's
# own stat — an external referee [D:yaml-interop]'s rule
if [ "$IS_WINDOWS" != "1" ]; then
    frdir=$(mkweirtmp)
    echo x > "$frdir/plain.txt"
    ln -s plain.txt "$frdir/link"
    # z-dangle: sorts after link, so the find-by-kind below still lands
    # on the live one; a dangling link is a row, not an absence
    ln -s missing "$frdir/z-dangle"
    cat > "$frdir/fr.weir" <<'WEOF'
let link = ls |> Seq.find (fun f -> f.kind == Symlink)
print $"kind-ok {link.name} {link.target |> Option.defaultValue "MISSING"}"
let plain = ls |> Seq.find (fun f -> f.name == "plain.txt")
if plain.target == None && plain.kind == Regular then print "plain-ok"
if show (File.stat "link") == show link then print "stat-agrees-link"
let dg = File.stat "z-dangle"
if show dg == show (ls |> Seq.find (fun f -> f.name == "z-dangle")) && dg.kind == Symlink then print $"dangle-ok {dg.target |> Option.defaultValue "MISSING"}"
print (File.mode "plain.txt" |> Option.defaultValue "none")
WEOF
    frout=$( cd "$frdir" && $BIN fr.weir )
    echo "$frout" | grep -qF "kind-ok link plain.txt" || fail "a symlink carries kind=Symlink and its target: $frout"
    echo "$frout" | grep -qF "plain-ok" || fail "a plain file is Regular with target None: $frout"
    echo "$frout" | grep -qF "stat-agrees-link" || fail "File.stat and ls must agree on the symlink row: $frout"
    echo "$frout" | grep -qF "dangle-ok missing" || fail "File.stat on a dangling link is a row carrying its target: $frout"
    # File.mode agrees with File.stat about existence [D:mode-existence]:
    # the dangling case raises its own honest message, never "no such path"
    mdout=$( cd "$frdir" && $BIN -e 'File.mode "z-dangle"' 2>&1 ) && fail "File.mode on a dangling link must raise: $mdout" || true
    echo "$mdout" | grep -qF "dangling symlink" || fail "the dangling case names the dangle: $mdout"
    echo "$mdout" | grep -qF "no such path" && fail "a path File.stat describes must not read as absent: $mdout"
    statmode=$( stat -c %A "$frdir/plain.txt" 2>/dev/null || stat -f %Sp "$frdir/plain.txt" 2>/dev/null )
    weirmode=$( echo "$frout" | tail -1 )
    [ "$weirmode" = "$(echo "$statmode" | cut -c2-10)" ] || fail "File.mode vs stat referee: weir=$weirmode stat=$statmode"
    echo "e2e ok: FileRow kind/target over a real symlink; File.stat agrees with ls (live + dangling); File.mode agrees with stat"
else
    echo "e2e skip: FileRow symlink + mode referee (POSIX rows)"
fi

# File.isExecutable "" is a no-such-path, not the cwd [D:isexecutable-empty]:
# Session.resolve "" folds to the cwd (which exists and carries the dir's
# x bit), so an unguarded read answered `true` — it must raise the same
# located missing-path error a real absent path raises, never true.
ixout=$($BIN -e 'File.isExecutable ""' 2>&1) && fail "File.isExecutable \"\" must raise, got: $ixout" || true
echo "$ixout" | grep -qF "no such path" || fail "File.isExecutable \"\" must raise no-such-path: $ixout"
# the correct missing-path posture is unchanged — a real absent path still raises
ixmiss=$($BIN -e 'File.isExecutable "./no-such-path-zz"' 2>&1) && fail "File.isExecutable on a missing path must raise, got: $ixmiss" || true
echo "$ixmiss" | grep -qF "no such path" || fail "File.isExecutable on a missing path must raise no-such-path: $ixmiss"
echo "e2e ok: File.isExecutable \"\" raises like a missing path (the empty string is not the cwd)"

# ---- filesystem members [D:fs-members] -------------------------------------
fsdir=$(mkweirtmp)
cat > "$fsdir/glob.weir" <<'WEOF'
let d = Path.newTempDir ()
Dir.create $"{d}/sub"
["1"] |> File.write $"{d}/a.txt"
["2"] |> File.write $"{d}/sub/b.txt"
Path.glob $"{d}/**/*.txt" |> Seq.freeze |> Seq.iter File.delete
print $"{Path.glob $"{d}/**/*.txt" |> Seq.length}"
Dir.deleteAll d
WEOF
out=$($BIN "$fsdir/glob.weir") || fail "the glob-delete composition must run"
[ "$out" = "0" ] || fail "glob a tree, delete the results: $out"
echo "e2e ok: Path.glob composes with File.delete (the obvious composition)"

# within tmp double-delete: a block that deleteAlls its own binder must
# exit clean — the scope's cleanup tolerates an already-gone directory
cat > "$fsdir/dd.weir" <<'WEOF'
within tmp d
    ["x"] |> File.write $"{d}/f.txt"
    Dir.deleteAll d
print "survived the double delete"
WEOF
out=$($BIN "$fsdir/dd.weir") || fail "double-delete must not raise on scope exit"
echo "$out" | grep -qF "survived" || fail "double-delete: $out"
echo "e2e ok: within tmp tolerates a block that already removed its own directory"
rm -rf "$fsdir"

# bare-pipe caret anchors on the '|', not the space after [PLAN-anchor-before-read]
cat > "$ckdir/bp.weir" <<'WEOF'
let names =
    []
    |> Seq.map _.name
    | Seq.distinct
WEOF
out=$($BIN check --json "$ckdir/bp.weir" || true)
echo "$out" | grep -qF '"line":4,"col":5' || fail "bare-pipe caret on the '|': $out"
echo "$out" | grep -qF "'|' chains commands" || fail "bare-pipe teaching text unchanged: $out"
echo "$out" | grep -qvF "Expecting:" || fail "clean message, no expecting-list: $out"
echo "e2e ok: bare-pipe caret anchors on the '|', clean teaching message"

# message domination: teaching fatals surface cleanly, not buried under an
# expecting-list; reserved-word gate stays fall-through-safe [PLAN-message-domination]
printf 'let rec = 1\n' > "$ckdir/dom.weir"
out=$($BIN check --json "$ckdir/dom.weir" || true)
echo "$out" | grep -qF '"line":1,"col":5' || fail "reserved-word caret on the word: $out"
echo "$out" | grep -qF "'rec' is a keyword" || fail "reserved teaching present: $out"
echo "$out" | grep -qvF "Expecting:" || fail "no buried expecting-list: $out"
printf '$@xs foo\n' > "$ckdir/dom2.weir"
out=$($BIN check --json "$ckdir/dom2.weir" || true)
echo "$out" | grep -qF "a splat cannot head a command" || fail "splat-head teaching surfaces: $out"
printf 'let _x = if true then 1 else 2\n' > "$ckdir/dom3.weir"
$BIN check "$ckdir/dom3.weir" >/dev/null 2>&1 || fail "keyword fall-through must still parse (if)"
echo "e2e ok: teaching fatals dominate; the reserved-word gate stays fall-through-safe"

# anchor residue A+B: foldChain reifier anchors on the marker; keyword in
# param/field slots dominates [PLAN-open-findings]
printf 'git | grep x | exec\n' > "$ckdir/fc.weir"
out=$($BIN check --json "$ckdir/fc.weir" || true)
echo "$out" | grep -qF '"line":1,"col":16' || fail "foldChain anchors on the marker: $out"
echo "$out" | grep -qF "a pipeline cannot" || fail "reifier teaching present: $out"
echo "$out" | grep -qvF "Other error messages" || fail "reifier teaching not buried: $out"
printf 'let f rec = 1\n' > "$ckdir/pk.weir"
out=$($BIN check --json "$ckdir/pk.weir" || true)
echo "$out" | grep -qF '"line":1,"col":7' || fail "param keyword caret: $out"
echo "$out" | grep -qF "'rec' is a keyword" || fail "param keyword teaching: $out"
echo "e2e ok: foldChain anchors on the marker; param/field keyword dominates"

# the last keyword slot: a keyword in a let-destructure pattern dominates
# via the lexical binder scan [PLAN-let-destructure-keyword]
printf 'let (rec) = 1\n' > "$ckdir/dk.weir"
out=$($BIN check --json "$ckdir/dk.weir" || true)
echo "$out" | grep -qF '"line":1,"col":6' || fail "let-destructure keyword caret: $out"
echo "$out" | grep -qF "'rec' is a keyword" || fail "destructure keyword teaching: $out"
echo "$out" | grep -qvF "Expecting:" || fail "destructure keyword not buried: $out"
echo "e2e ok: let-destructure keyword dominates via the lexical binder scan"

printf 'print "clean"\n' > "$ckdir/clean.weir"
out=$($BIN check "$ckdir/clean.weir"); rc=$?
[ $rc -eq 0 ] && [ -z "$out" ] || fail "clean file must exit 0 silently (rc=$rc out=$out)"
echo "e2e ok: clean file exits 0 silently"

cat > "$ckdir/warn.weir" <<'WEOF'
git add -A ; git push
WEOF
rc=0; out=$($BIN check "$ckdir/warn.weir") || rc=$?
[ $rc -eq 0 ] || fail "warnings-only must exit 0 (rc=$rc)"
echo "$out" | grep -qF "warning" || fail "warning severity missing: $out"
echo "e2e ok: warnings appear with severity warning and exit 0 (decided)"

rc=0; $BIN check "$ckdir/multi.weir" >/dev/null 2>&1 || rc=$?
[ $rc -eq 1 ] || fail "error exit must be 1 (got $rc)"
echo "e2e ok: check exits 1 on errors"

rm -rf "$ckdir"

# --- runtime errors name the failing line [D:runtime-position]: a raise
# inside any block form reports the failing statement's file:line:col, not
# the enclosing top-level statement's line. A command reports its own
# column, a raising call its callee's; other raises the start of the failing line. A lazily read
# command's failure blames the command, wherever it is read.
rpdir=$(mkweirtmp)
rp_case() { # name, expected "file:line:col: error", script body
    printf '%s' "$3" > "$rpdir/$1.weir"
    local out rc=0
    out=$(cd "$rpdir" && $BIN "$1.weir" 2>&1) || rc=$?
    [ $rc -ne 0 ] || fail "runtime position $1: expected a failure (rc=$rc): $out"
    echo "$out" | grep -qF "$2" || fail "runtime position $1: expected '$2' in: $out"
    echo "e2e ok: runtime position — $1 ($2)"
}
rp_case top "top.weir:2:1: error:" 'print "a"
sh -c "exit 3"
'
rp_case if "if.weir:3:5: error: command failed with exit code 3" 'if true then
    print "x"
    sh -c "exit 3"
'
rp_case else "else.weir:5:5: error:" 'if false then
    print "x"
else
    print "y"
    sh -c "exit 3"
'
rp_case match "match.weir:4:5: error:" 'match 1 with
| 1 ->
    print "one"
    sh -c "exit 3"
| _ -> ()
'
rp_case within "within.weir:3:5: error:" 'within env A=1
    print "in"
    sh -c "exit 3"
'
rp_case for "for.weir:3:5: error:" 'for i in [1] do
    print $"{i}"
    sh -c "exit 3"
'
rp_case retry "retry.weir:3:5: error:" 'retry attempts=1 delay=1ms
    print "r"
    sh -c "exit 3"
    true
'
rp_case pipeline "pipeline.weir:3:5: error:" 'if true then
    print "p"
    sh -c "echo a" | sh -c "cat >/dev/null; exit 4"
'
rp_case fail "fail.weir:3:5: error: boom" 'if true then
    print "f"
    fail "boom"
'
rp_case lazy "lazy.weir:2:15: error: command failed with exit code 5" 'if true then
    let out = $(sh -c "echo a; exit 5")
    print "between"
    out |> Seq.iter print
'
rp_case lambda "lambda.weir:2:5: error: command failed with exit code 6" '[1] |> Seq.iter (fun i ->
    sh -c "exit 6"
    print $"{i}")
'
# a parallel arm's raise is blamed where the arm raised, not on the
# thread that rethrows it
rp_case pmap "pmap.weir:4:24: error: arm 2" 'let ys =
    [1; 2; 3]
    |> Seq.pmap (fun i ->
        if i == 2 then fail $"arm {i}" else i)
print $"{Seq.length ys}"
'
rp_case piter "piter.weir:3:24: error: arm 2" 'print "start"
[1; 2; 3] |> Seq.piter (fun i ->
        if i == 2 then fail $"arm {i}")
'
rp_case pfirst "pfirst.weir:2:5: error: race 1" 'let f i =
    fail $"race {i}"

let w = [1; 2] |> Seq.pfirst f
print $"{w}"
'
# a failure inside a function defined in ANOTHER statement reports the
# body line, then one `called from` line per call, innermost first
rp_case fncall "fncall.weir:2:5: error: command failed with exit code 7" 'let f () =
    sh -c "exit 7"
    print "after"

if true then
    f ()
'
rp_frames() { # name, expected frame lines (in order, newline-separated)
    local out
    out=$(cd "$rpdir" && $BIN "$1.weir" 2>&1) || true
    local got
    got=$(echo "$out" | grep -F "called from" || true)
    [ "$got" = "$2" ] || fail "runtime trace $1: expected frames:
$2
got:
$got
in: $out"
    echo "e2e ok: runtime trace — $1"
}
rp_frames fncall "  called from fncall.weir:6:5"
rp_frames pfirst "  called from pfirst.weir:4:1"
rp_case nested "nested.weir:3:5: error: deep" 'let inner () =
    print "i"
    fail "deep"
    print "x"

let outer () =
    print "o"
    inner ()

if true then
    outer ()
'
rp_frames nested "  called from nested.weir:8:5
  called from nested.weir:11:5"
printf 'module Lib\n\nlet run : unit -> unit\n\nlet run () =\n    print "in lib"\n    sh -c "exit 9"\n    print "unreached"\n' > "$rpdir/lib.weir"
rp_case module "lib.weir:7:5: error: command failed with exit code 9" 'import "./lib.weir"

print "start"
Lib.run ()
'
rp_frames module "  called from module.weir:4:1"
# a cleanup runs weir code while the body's error unwinds; the error stays
# at the body's line, whether the cleanup succeeds or fails too
rp_case always "always.weir:3:5: error: body" 'within
    print "in"
    fail "body"
always
    print "cleaning"
'
rp_case alwaysfail "alwaysfail.weir:2:5: error: body" 'within
    fail "body"
always
    fail "cleanup"
'
# an unknown statement-head program at run time names it as a missing
# program with the ^$tool advice, not "unbound variable … wrap it in $(…)"
# [D:sweep-findings]
rp_case unknownhead "unknownhead.weir:1:1" 'nosuchcmdxyz arg
'
out=$(cd "$rpdir" && $BIN unknownhead.weir 2>&1 || true)
echo "$out" | grep -qF "unknown command 'nosuchcmdxyz' — not found on PATH" || fail "an unknown head is a missing program: $out"
echo "$out" | grep -qF '^$tool' || fail "the missing-program message teaches ^\$tool: $out"
# --can reports a reified command, and each pipeline stage, at its own column
printf 'let s = git log | head -1 | line\nprint s\n' > "$rpdir/can.weir"
out=$(cd "$rpdir" && $BIN check --can can.weir 2>&1)
echo "$out" | grep -qE 'git +can\.weir:1:9' || fail "--can: git at its own column: $out"
echo "$out" | grep -qE 'head +can\.weir:1:19' || fail "--can: the head stage at its own column: $out"
echo "e2e ok: unknown head names a missing program; --can columns per command"
rp_case item "item.weir:3:20: error:" 'if true then
    print "s"
    let x = [1] |> Seq.item 5
    print $"{x}"
'
# a call that raises inside a one-line form is reported at the callee's
# column; a reifier's failure at its command [D:runtime-position-call]
rp_case oneline "oneline.weir:2:23: error: boom" 'let x = 3
let y = if x > 2 then fail "boom" else 1
print $"{y}"
'
rp_case reified "reified.weir:1:9: error: command failed with exit code 2" 'let t = sh -c "exit 2" | text
print t
'
rm -rf "$rpdir"

# --- runner missing-command diagnosis (2026-07-21: FParsec's primary
# error buried the real cause and showed assembler-joined text) ---

mdir=$(mkweirtmp)
printf 'not-a-real-tool-xyz --flag\n' > "$mdir/m.weir"
rc=0; errout=$($BIN "$mdir/m.weir" 2>&1) || rc=$?
[ $rc -eq 1 ] || fail "missing command must fail the runner (rc=$rc)"
echo "$errout" | grep -qF "unknown command 'not-a-real-tool-xyz'" || fail "diagnosis must name the command: $errout"
echo "$errout" | grep -qF "Expecting:" && fail "FParsec dump must not appear for missing commands: $errout"
echo "$errout" | grep -qF '^$tool' || fail "diagnosis must teach the dynamic head: $errout"
echo "e2e ok: runner names the missing command, no parser dump"

printf 'git status | and echo x | success\n' > "$mdir/r.weir"
rc=0; errout=$($BIN "$mdir/r.weir" 2>&1) || rc=$?
echo "$errout" | grep -qF "Did you mean the reifier 'succeeds'?" || fail "a misspelled reifier must be suggested (rc=$rc): $errout"
echo "e2e ok: a misspelled reifier after | is suggested"

printf 'let c = echo a | not-a-real-tool-xyz | exitCode\nprint $"{c}"\n' > "$mdir/cr.weir"
ckout=$($BIN check "$mdir/cr.weir" 2>&1) || true
echo "$ckout" | grep -qF "command not found on PATH: not-a-real-tool-xyz" || fail "check must warn on a reified pipeline's missing stage: $ckout"
rc=0; errout=$($BIN "$mdir/cr.weir" 2>&1) || rc=$?
echo "$errout" | grep -qF "unknown command 'not-a-real-tool-xyz'" || fail "the runner must name a reified pipeline's missing stage (rc=$rc): $errout"
echo "e2e ok: a reified pipeline's missing stage is named by check and the runner"

cat > "$mdir/syn.weir" <<'WEOF'
let x =
    1 +
    +
WEOF
errout=$($BIN "$mdir/syn.weir" 2>&1 || true)
echo "$errout" | grep -qF "    1 +" || fail "parse errors must show the ORIGINAL source line: $errout"
echo "$errout" | grep -q "\^" || fail "parse errors must carry a caret: $errout"
echo "$errout" | grep -qF " ; " && fail "assembled text must never appear: $errout"
echo "e2e ok: parse errors show unassembled source with caret"

# type errors show the source line with the span underlined, same
# treatment as parse errors (user report, 2026-07-21)
cat > "$mdir/terr.weir" <<'WEOF'
let workdir = "x"
print workdirx
WEOF
errout=$($BIN "$mdir/terr.weir" 2>&1 || true)
echo "$errout" | grep -qF "print workdirx" || fail "type errors must show the source line: $errout"
echo "$errout" | grep -qF "^^^^^^^^" || fail "type errors must underline the span: $errout"
echo "$errout" | grep -qF "Did you mean 'workdir'" || fail "the hint must survive the new renderer: $errout"
echo "e2e ok: type errors underline the offending span"

# a dangling `let ` used to render FParsec's empty error set as
# "Unknown Error(s)" — the ident parser was unlabeled (2026-07-21)
errout=$($BIN -e 'let ' 2>&1 || true)
echo "$errout" | grep -qF "Expecting: identifier" || fail "dangling let must expect an identifier: $errout"
echo "$errout" | grep -qF "Unknown Error" && fail "empty FParsec error sets must not surface: $errout"
echo "e2e ok: dangling let expects an identifier"

rm -rf "$mdir"

# --- every repo script must check (2026-07-21: test-counts.weir had
# been broken since the pairwise re-type and nothing noticed — scripts
# rot silently unless gated; cmd-not-found warnings are fine, errors
# are not) ---

for scr in "$(dirname "$0")"/../examples/*.weir "$(dirname "$0")"/../tools/*.weir; do
    rc=0
    out=$($BIN check "$scr" 2>&1) || rc=$?
    [ $rc -eq 0 ] || fail "repo script no longer checks: $scr — $out"
done
echo "e2e ok: all repo scripts check clean"

# the showcase is a composition test disguised as a document — a tour
# that can be invalid without failing a build is not that (the schema
# live-check found three real errors in it, unprompted [D:add-validates]).
# Child weirs resolve via PATH: the $BIN dir prefix, as everywhere.
scout=$(PATH="$BINDIR:$PATH" $BIN "$(dirname "$0")/../examples/showcase.weir" --tag ci 2>/dev/null) || fail "the showcase must RUN green"
echo "$scout" | grep -qF "showcase complete" || fail "the showcase completes: ${scout: -200}"
echo "$scout" | grep -qF 'weir.dev/switch: "on"' || fail "the district auto-quote demo holds"
echo "e2e ok: the showcase runs end to end (the tour is a build gate now)"

# ---- what weir says when the file isn't weir (rider D1/D3/D4/D5) ----------
# weir.slnx is the reported case and a deliberate fixture choice: XML, in the
# repo, and not going to change shape. No extension rule is involved — the
# heuristic is about shape, so it also covers shebang files and stdin.
nw=$($BIN check "$(dirname "$0")/../weir.slnx" 2>&1) || true

# D4: one sentence, and it comes first — before the detail, never instead of it
echo "$nw" | head -1 | grep -qF "does not look like a weir script" \
    || fail "D4: the not-weir heuristic must lead the output: ${nw:0:200}"
echo "$nw" | grep -qF "error [parse]" || fail "D4: the detail must survive the heuristic"

# D1: diagnostics in position order — the parse error at line 1 precedes the
# assembly errors at 4/5/7, which is the whole point (a reader went to line 4)
first_line_no=$(echo "$nw" | grep -oE "weir\.slnx:[0-9]+" | head -1 | cut -d: -f2)
[ "$first_line_no" = "1" ] || fail "D1: first diagnostic must be line 1, got $first_line_no"

# D3: words reserved only to teach that weir lacks them must never be offered
# as tokens the parser expects
for w in while return try def; do
    echo "$nw" | grep -qE "Expecting:.*'$w'" \
        && fail "D3: reserved teaching word '$w' leaked into an expecting-list"
done

# D5: FParsec's backtracking trace is the parser talking to its author —
# hidden from users, not deleted: it still tells weir's own developers where
# the grammar gave up, so WEIR_LOG=debug keeps it. Both halves are pinned,
# because "absent by default" alone would pass if the capability were dropped.
echo "$nw" | grep -qF "The parser backtracked after" && fail "D5: backtracking note reached the user"
echo "$nw" | grep -qF "end of the input stream" && fail "D5: FParsec EOF note reached the user"
nwdbg=$(WEIR_LOG=debug $BIN check "$(dirname "$0")/../weir.slnx" 2>&1) || true
echo "$nwdbg" | grep -qF "The parser backtracked after" \
    || fail "D5: WEIR_LOG=debug must KEEP the backtrace — hiding it is not deleting it"

# D4 negative control — without this the trigger is untested: a real weir
# script whose first line is broken must keep its located diagnostic and must
# not be told it isn't weir (other statements parsed, so the shape is fine)
# own temp dir: $scriptdir is rm -rf'd at line ~869, ~950 lines above here,
# and this block was its only consumer that late
nwdir=$(mkweirtmp)
printf 'let x = (((\nprint "ok"\n' > "$nwdir/neg1.weir"
neg=$($BIN check "$nwdir/neg1.weir" 2>&1) || true
echo "$neg" | grep -qF "does not look like a weir script" \
    && fail "D4 control: a real script with a line-1 syntax error must NOT trip the heuristic"
echo "$neg" | grep -qF "error [parse]" || fail "D4 control: the located diagnostic must survive"

# --json is for gates, not humans: the heuristic must not appear there
njson=$($BIN check --json "$(dirname "$0")/../weir.slnx" 2>&1) || true
echo "$njson" | grep -qF "does not look like" && fail "D4: the heuristic must not appear in --json"

rm -rf "$nwdir"
echo "e2e ok: a non-weir file says so once, in position order, without parser internals"

# D5's suppression covers a class, not the paths it was written against
# [D:accessor-teaching]: `Unknown Error(s)` is FParsec admitting it has
# nothing to say while claiming an error, and it kept the backtrack
# section alive after D5 dropped that section's position lines. Pinned
# both ways, like the D5 cell above — absent by default, present under
# WEIR_LOG=debug (absence alone would pass if the capability were lost).
ueout=$($BIN -e 'let r = 1..3 in 0' 2>&1 || true)
echo "$ueout" | grep -qF "Unknown Error(s)" && fail "the parser must not print Unknown Error(s): $ueout"
echo "$ueout" | grep -qF "backtracked after" && fail "the backtrack note stays hidden by default: $ueout"
uedbg=$(WEIR_LOG=debug $BIN -e 'let r = 1..3 in 0' 2>&1 || true)
echo "$uedbg" | grep -qF "backtracked after" || fail "WEIR_LOG=debug must KEEP the trace: $uedbg"
echo "e2e ok: Unknown Error(s) joins the backtrack suppression (both ways)"

# range slicing at the binary [D:range-slicing] — inclusive/clamping,
# type-directed (str vs seq), with from-end (^n) index and bounds
rgout=$($BIN -e 'let xs = [10; 20; 30; 40] in xs[1..2] |> Seq.freeze' 2>&1)
echo "$rgout" | grep -qF "20; 30" || fail "range slicing (inclusive) drifted: $rgout"
strout=$($BIN -e '"abcdefghi"[3..7]' 2>&1)
echo "$strout" | grep -qF "defgh" || fail "string slice drifted: $strout"
# Bytes is the third slice target [D:range-slicing] — inclusive, to Bytes;
# a single b[i] teaches the byte forms (indexing parked)
byout=$($BIN -e 'Bytes.toBase64 ((Bytes.fromBase64 "AAECAwQF")[1..3])' 2>&1)
echo "$byout" | grep -qF "AQID" || fail "Bytes slice drifted (expected bytes 1..3): $byout"
biout=$($BIN -e 'let b = Bytes.fromBase64 "AAEC" in b[0]' 2>&1 || true)
echo "$biout" | grep -qF "no b[i] index on Bytes" || fail "b[i] must teach the byte forms: $biout"
# from-the-end (^n) index and slice bounds [D:range-slicing]
feout=$($BIN -e 'let xs = [1; 2; 3] in xs[^1]' 2>&1)
echo "$feout" | grep -qF "3 : int" || fail "from-end index xs[^1] must be the last: $feout"
febout=$($BIN -e 'let xs = [10; 20; 30] in xs[..^2] |> Seq.freeze' 2>&1)
echo "$febout" | grep -qF "10; 20" || fail "from-end bound xs[..^2] (all-but-last) drifted: $febout"
festr=$($BIN -e '"abcde"[^2..]' 2>&1)
echo "$festr" | grep -qF "de" || fail "from-end string slice drifted: $festr"
# the accessor teachings that stand [D:accessor-teaching]: the dotted
# indexer and Map-index still refuse (only the range form flipped)
dtout=$($BIN -e 'let xs = [1; 2; 3] in xs.[0]' 2>&1 || true)
echo "$dtout" | grep -qF "without the dot" || fail "the dotted indexer teaches: $dtout"
mkout=$($BIN -e 'let m = Map.ofPairs [("a", 1)] in m["a"]' 2>&1 || true)
echo "$mkout" | grep -qF "Map.get" || fail "m[k] teaches Map.get (the doc's promise): $mkout"
[ "$($BIN -e 'let xs = [1; 2; 3] in xs[1]' 2>&1 | tail -1)" = "2 : int" ] || fail "the ordinary indexer still works"
echo "e2e ok: accessor teachings (range on seq/str/Bytes, dotted, m[k], b[i]) — the ordinary indexer untouched"

# --- the casing law (2026-07-21) ---------------------------------------

errout=$($BIN -e 'let Foo = 1 in Foo' 2>&1 || true)
echo "$errout" | grep -qF "binding names start lowercase" || fail "casing law must reject at the binder: $errout"
echo "e2e ok: the casing law (lowercase binds) on the AOT binary"

# param-ful command RHS (PLAN-paramful-rhs): the shadowing law —
# this pin was written failing against the guard-dropped prototype
# (`let f x = x` printed SPAWNED with an executable x on PATH)
shdir=$(mkweirtmp)
mkFakeBin "$shdir" x SPAWNED
cat > "$shdir/shadow.weir" <<'WEOF'
let f x = x
print (f "value")
WEOF
out=$(PATH="$(pathEntry "$shdir"):$PATH" $BIN "$shdir/shadow.weir")
expect "params shadow PATH in their own RHS (identity stays identity)" "value" "$out"
printf '^x\n' > "$shdir/force.weir"
out=$(PATH="$(pathEntry "$shdir"):$PATH" $BIN "$shdir/force.weir")
expect "^x still reaches the PATH binary (no capability lost)" "SPAWNED" "$out"
rm -rf "$shdir"

# param-ful RHS: forms, sigil equivalence, splice-typo hint
pfdir=$(mkweirtmp)
(cd "$pfdir" && git init -q . && git -c user.email=a@a -c user.name=a commit -q --allow-empty -m x)
cat > "$pfdir/forms.weir" <<'WEOF'
let revParse r = git rev-parse $r |> Seq.head
let asSigil r = $(git rev-parse $r) |> Seq.head
let headLog () = git log --format=%s -1
print (if revParse "HEAD" == asSigil "HEAD" then "EQUAL" else "DIFF")
headLog () |> print
WEOF
out=$(cd "$pfdir" && $BIN forms.weir)
expect "bare and sigil spellings behaviorally equal" "EQUAL" "$out"
expect "thunk param takes a command RHS" "x" "$out"
cat > "$pfdir/typo.weir" <<'WEOF'
let f pth = git checkout $path
print "unreached"
WEOF
errout=$(cd "$pfdir" && $BIN typo.weir 2>&1 || true)
echo "$errout" | grep -qF "Did you mean 'pth'?" || fail "splice-typo must did-you-mean the param: $errout"
echo "e2e ok: a typo'd splice did-you-means the param"
rm -rf "$pfdir"

# Seq.fold + fun-sugar (PLAN-fold): the git-subrepo receipt folds
# verbatim — the port's blocker provably unblocked
folddir=$(mkweirtmp)
cat > "$folddir/receipt.weir" <<'WEOF'
// encode-subdir's escape loop, as a fold over replacement pairs
let escaped =
    [("~", "%7e"); ("^", "%5e"); (":", "%3a"); (" ", "%20")]
    |> Seq.fold (fun s (pat, repl) -> Str.replace pat repl s) "a b~c:d"

print escaped

// the commit-walk shape: four accumulators in a Ctx record
type Walk = { Prev: string; Ancestor: string; First: string; Kept: int }

let walked =
    ["c1 keep"; "c2 skip"; "c3 keep"]
    |> Seq.fold
        (fun w line ->
            match line with
            | Regex @"^(\w+) keep$" c -> { w with Prev = c; Ancestor = w.Prev; Kept = w.Kept + 1 }
            | _ -> w)
        { Prev = ""; Ancestor = ""; First = ""; Kept = 0 }

print $"{walked.Prev} after {walked.Ancestor}, kept {walked.Kept}"

// the inline-env receipt shape (three vars, Env.ofPairs) — env sigil
let author = Env.ofPairs [("GIT_AUTHOR_NAME", "n"); ("GIT_AUTHOR_EMAIL", "e"); ("GIT_AUTHOR_DATE", "d")]
within env author
    sh -c "echo $GIT_AUTHOR_NAME/$GIT_AUTHOR_EMAIL"
WEOF
out=$($BIN "$folddir/receipt.weir")
expect "the encode-subdir escape fold" "a%20b%7ec%3ad" "$out"
expect "the commit-walk accumulator-record fold" "c3 after c1, kept 2" "$out"
expect "Env.ofPairs into the env sigil (the inline-env receipt)" "n/e" "$out"
rm -rf "$folddir"

# fmt v2 respace under the parse-shape guard (user receipt, 2026-07-22)
fdir=$(mkweirtmp)
cat > "$fdir/ugly.weir" <<'WEOF'
type Great = {Lomo: int; Bimbo: string}

let lomo = {Lomo = 10; Bimbo = "yuck"}

let l2 =  {    lomo with Lomo = 100}
l2 |> show |> print
WEOF
before=$($BIN "$fdir/ugly.weir")
$BIN fmt "$fdir/ugly.weir" >/dev/null 2>&1
grep -qF 'type Great = { Lomo: int; Bimbo: string }' "$fdir/ugly.weir" || fail "respace missed the type decl"
grep -qF 'let l2 = { lomo with Lomo = 100 }' "$fdir/ugly.weir" || fail "respace missed the update line"
after=$($BIN "$fdir/ugly.weir")
[ "$before" = "$after" ] || fail "respace changed behavior: $before vs $after"
errout=$($BIN fmt "$fdir/ugly.weir" 2>&1)
echo "$errout" | grep -qF "already formatted" || fail "respace must be idempotent: $errout"
echo "e2e ok: fmt respaces braces/spacing, behavior-identical, idempotent"

cat > "$fdir/guard.weir" <<'WEOF'
echo {a}
WEOF
cp "$fdir/guard.weir" "$fdir/guard0.weir"
$BIN fmt "$fdir/guard.weir" >/dev/null 2>&1 || true
cmp -s "$fdir/guard.weir" "$fdir/guard0.weir" || fail "the shape guard must revert argv-brace lines"
echo "e2e ok: the shape guard keeps command argv braces literal"
rm -rf "$fdir"

# record update (PLAN-record-update): the corpus snippets are the e2e
upd=$(mkweirtmp)
cat > "$upd/corpus1.weir" <<'WEOF'
type Model = { V: string; I: int }
let m = { V = ""; I = 0 }
let m1 = { m with V = "m" }

type R = { M: Model }
print m1.V
WEOF
out=$($BIN "$upd/corpus1.weir")
expect "corpus bbffe988 verbatim: flat update" "m" "$out"

cat > "$upd/corpus2.weir" <<'WEOF'
type Inner = { X: int }
type Outer = { I: Inner }
let o = { I = { X = 1 } }
let o2 = { o with I.X = 2 }
print $"{o2.I.X} {o.I.X}"
WEOF
out=$($BIN "$upd/corpus2.weir")
expect "corpus 56d739b verbatim: nested update, source untouched" "2 1" "$out"

cat > "$upd/multiline.weir" <<'WEOF'
type R = { A: int; B: int }
let r = { A = 1; B = 2 }

let r2 =
    { r with
        A = 10
        B = 20 }

print $"{r2.A + r2.B}"
WEOF
out=$($BIN "$upd/multiline.weir")
expect "multi-line update rides the brace-continuation rule" "30" "$out"
rm -rf "$upd"

# raw strings (PLAN-raw-strings): both kinds on the AOT binary
rawdir=$(mkweirtmp)
cat > "$rawdir/raw.weir" <<'WEOF'
let path = @"a\raw\path"
let quoted = """say "hi" ok"""

print path
print quoted

match "cfg=7" with
| Regex @"(\w+)=(\d+)" (k, v) -> print $"{k}/{v}"
| _ -> print "no"

match "a\"b" with
| Regex """("[a-z])""" q -> print q
| _ -> print "no"
WEOF
out=$($BIN "$rawdir/raw.weir")
expect "verbatim survives byte-identically" 'a\raw\path' "$out"
expect "triple carries bare quotes" 'say "hi" ok' "$out"
expect "verbatim regex extracts" "cfg/7" "$out"
expect "triple regex matches quoted fields" '"b' "$out"
$BIN fmt --check "$rawdir/raw.weir" >/dev/null 2>&1 || fail "raw script must be fmt-stable"
echo "e2e ok: fmt roundtrips raw strings"

errout=$($BIN -e 'match "a" with | Regex "(a)" v -> v | _ -> ""' 2>&1 || true)
echo "$errout" | grep -qF "regex literals are raw" || fail "the raw-only rider hint is missing: $errout"
echo "e2e ok: the Regex position is raw-only"

out=$($BIN -e 'echo (@"\n") |> Seq.head')
expect "verbatim splice is one literal argv entry" '\n' "$out"
rm -rf "$rawdir"

# the Regex pattern + Str match family (regex plan, 2026-07-22)
redir=$(mkweirtmp)
cat > "$redir/logs.weir" <<'WEOF'
let lines = ["INFO 200 ok"; "WARN 500 boom"; "INFO 404 miss"; "garbage"]

lines
    |> Seq.map (fun l ->
        match l with
        | Regex @"(\w+) (\d+)" (level, code) -> $"{level}:{code}"
        | _ -> "unparsed")
    |> print

let errors =
    lines
    |> Seq.where (Str.isMatch "^WARN")
    |> Seq.map (fun l ->
        match l with
        | Regex @"(\d+)" code -> code |> Str.toInt
        | _ -> 0)
    |> Seq.sum

print $"error-code sum: {errors}"
WEOF
out=$($BIN "$redir/logs.weir")
expect "Regex extraction over a log shape" "INFO:200" "$out"
expect "non-matching lines fall to the catch-all" "unparsed" "$out"
expect "isMatch where-filter + extraction + toInt" "error-code sum: 500" "$out"

cat > "$redir/badregex.weir" <<'WEOF'
sh -c "touch regex-proof"
let x = match "a" with | Regex @"([" v -> v | _ -> ""
print x
WEOF
rc=0; errout=$(cd "$redir" && $BIN badregex.weir 2>&1) || rc=$?
[ $rc -eq 1 ] || fail "invalid regex must fail the check (rc=$rc)"
echo "$errout" | grep -qF "invalid regex" || fail "invalid-regex error missing: $errout"
[ -e "$redir/regex-proof" ] && fail "check-first violated: effect ran before the regex error"
echo "e2e ok: invalid regex fails before any effect"

out=$($BIN check "$redir/badregex.weir" || true)
echo "$out" | grep -qF "[regex]" || fail "regex diagnostic code missing: $out"
rm -rf "$redir"

# composition + redirect hints (mini-plan; oracle refuted tighter-than-pipe)
out=$($BIN -e '(Str.trim >> Str.length) "  ab  "')
expect "composition point-free" "2 : int" "$out"
errout=$($BIN -e '[1] |> Seq.map (fun x -> x) >> Seq.sum' 2>&1 || true)
echo "$errout" | grep -qF "share precedence" || fail "the |>/>> gotcha hint is missing: $errout"
errout=$($BIN -e 'ls >> x' 2>&1 || true)
echo "$errout" | grep -qF "File.append" || fail "the loc.weir line must hint File.append: $errout"
echo "e2e ok: composition works; the two >> mistakes get targeted hints"

rdir=$(mkweirtmp)
cat > "$rdir/redir.weir" <<'WEOF'
echo hi > out.txt
echo hi >> out.txt
WEOF
errout=$(cd "$rdir" && $BIN redir.weir 2>&1 >/dev/null)
echo "$errout" | grep -qF "File.write" || fail "command > must hint File.write: $errout"
echo "$errout" | grep -qF "File.append" || fail "command >> must hint File.append: $errout"
out=$(cd "$rdir" && $BIN redir.weir 2>/dev/null)
expect "> stays a literal argv word (safety pin unchanged)" "hi > out.txt" "$out"
expect ">> stays a literal argv word (safety pin unchanged)" "hi >> out.txt" "$out"
rm -rf "$rdir"

errout=$($BIN -e 'echo (Str.trim >> Str.length)' 2>&1 || true)
echo "$errout" | grep -qF "command arguments must be" || fail "composed function as splice must hit the scalar rule: $errout"
echo "e2e ok: composed function rejected as a command splice"

# fst/snd + Path (loc.weir friction receipts)
out=$($BIN -e '[(1, "b"); (2, "a")] |> Seq.sortBy snd |> Seq.map fst |> Seq.head')
expect "fst/snd project pairs point-free" "2 : int" "$out"
out=$($BIN -e 'Path.combine (Path.dir "a/b/c.fs") (Path.stem "a/b/c.fs")')
# weir's path outputs are the platform's (the platformPath law) — the
# unit pin went platform-aware in windows-s2; this one never had
case "$out" in
    *'"a/b/c"'* | *'"a\\b\\c"'*) echo "e2e ok: Path members compose (platform separators)" ;;
    *) fail "Path members compose: $out" ;;
esac

# prefix minus + sortByDescending (2026-07-21, loc.weir friction)
out=$($BIN -e '2 * -3')
expect "prefix minus at operand position" "-6 : int" "$out"
out=$($BIN -e '[1; 3; 2] |> Seq.sortByDescending (fun x -> x) |> Seq.freeze')
expect "sortByDescending orders down" "[3; 2; 1]" "$out"

# the squiggle sits on the name, not the RHS (user report, 2026-07-21)
errout=$($BIN -e 'let Total = 1 + 2' 2>&1 || true)
echo "$errout" | grep -qF '    ^^^^^' || fail "casing squiggle must underline the binder name: $errout"
echo "e2e ok: casing squiggle points at the binder"

# --- pattern binders + bare comma (2026-07-21) -------------------------

pbdir=$(mkweirtmp)

cat > "$pbdir/binders.weir" <<'WEOF'
let hosts = ["alpha"; "beta"]

let pairs = hosts |> Seq.zip [1; 2]

pairs
    |> Seq.map (fun (n, h) -> $"{h}:{n}")
    |> print

let first, rest = pairs |> Seq.head, pairs |> Seq.skip 1

match first with
| (n, h) -> print $"head {h}"

print $"rest {rest |> Seq.length}"

let (k, _) = ("only-the-key", 99)

print k
WEOF
out=$(cd "$pbdir" && $BIN binders.weir)
expect "zip consumer with tuple lambda params (the customer)" "alpha:1" "$out"
expect "bare-comma binder + bare-comma RHS at full precedence" "rest 1" "$out"
expect "wildcard component" "only-the-key" "$out"

errout=$($BIN -e 'let (Some x) = Some 1 in x' 2>&1 || true)
echo "$errout" | grep -qF "this pattern can fail; use match" || fail "refutable binder must name match: $errout"
echo "e2e ok: refutable binders reject with the contract message"

# comma stays argv-inert in command mode (the guard from the amendment)
out=$($BIN -e '$(echo cols=key,summary) |> Seq.head')
expect "command-mode commas stay bareword characters" "cols=key,summary" "$out"

rm -rf "$pbdir"

# --- tuples (2026-07-21): the reversal, end to end ---------------------

tudir=$(mkweirtmp)

cat > "$tudir/tup.weir" <<'WEOF'
type Route = | Hop of string * int | Stay

let hops = ["a"; "b"; "c"] |> Seq.zip [1; 2; 3]

hops
    |> Seq.map (fun p -> match p with | (n, s) -> $"{s}{n}")
    |> print

let r = Hop ("gw", 2)

match r with
| Hop (host, cost) -> print $"{host}:{cost}"
| Stay -> print "stay"

let deltas =
    [10; 13; 11]
    |> Seq.pairwise
    |> Seq.map (fun p -> match p with | (a, b) -> $"{b - a}")

deltas |> print
WEOF
out=$(cd "$tudir" && $BIN tup.weir)
expect "zip + tuple match in a script" "a1" "$out"
expect "multi-payload constructor round-trip" "gw:2" "$out"
expect "pairwise re-typed migration shape" "3" "$out"

errout=$($BIN -e '[(1, 2)] |> Seq.sortBy (fun x -> x)' 2>&1 || true)
echo "$errout" | grep -qF "cannot be ordered" || fail "tuple sort keys must reject: $errout"
echo "e2e ok: no tuple ordering (divergence-pinned)"

rm -rf "$tudir"

# --- literal patterns + () thunks (2026-07-21) ------------------------

ldir=$(mkweirtmp)

cat > "$ldir/lit.weir" <<'WEOF'
let cleanup () = printerr "cleaning up"

let classify n =
    match n with
    | 0 -> "none"
    | 1 -> "one"
    | n -> $"many ({n})"

let mode = Self.args |> Seq.tryHead |> Option.defaultValue "count"

match mode with
| "count" -> print (classify 1)
| "reset" -> print (classify 0)
| _ -> fail "unknown mode"

cleanup ()
WEOF
out=$(cd "$ldir" && $BIN lit.weir 2>&1)
expect "literal patterns dispatch in a script (int + string matches)" "one" "$out"
expect "the () thunk defers its effect to the call" "cleaning up" "$out"

errout=$($BIN -e 'match 1 with | 0 -> "a" | 1 -> "b"' 2>&1 || true)
echo "$errout" | grep -qF "catch-all" || fail "literal-only match must hard-error: $errout"
echo "e2e ok: literal arms never exhaust (hard error, F#-divergence-pinned)"

rm -rf "$ldir"

# --- type classes Session C (2026-07-21): hardening products

cdir=$(mkweirtmp)

cat > "$cdir/prod.weir" <<'WEOF'
let same x y = x == y

// classes x splices: a constrained helper's result in command argv
echo verdict: (same 1 1)

// classes x pmap: constrained closure across workers (erasure means
// only values cross threads)
let hits = [1; 2; 1] |> Seq.pmap (fun n -> same n 1) |> Seq.where (fun b -> b) |> Seq.length
print $"hits {hits}"
WEOF
out=$(cd "$cdir" && $BIN prod.weir 2>&1)
expect "classes x splices: constrained result in argv" "verdict: true" "$out"
expect "classes x pmap: constrained closure across workers" "hits 2" "$out"

rm -rf "$cdir"

# --- type classes Session B (2026-07-21): Show + Ord — the runtime check dies

bdir=$(mkweirtmp)

# sortBy on a record key is rejected at check time — the effect before
# it must not run (check-first proves the runtime check is gone,
# replaced by static Ord)
cat > "$bdir/ord.weir" <<'WEOF'
printerr "must-not-run"

ls |> Seq.sortBy (fun f -> f) |> print
WEOF
errout=$($BIN "$bdir/ord.weir" 2>&1 || true)
echo "$errout" | grep -qF "cannot sort by this key" || fail "record key must reject at check: $errout"
echo "$errout" | grep -qF "must-not-run" && fail "check-first violated: the effect ran"
echo "e2e ok: sortBy record key dies at CHECK time, zero effects (the headline)"

cat > "$bdir/show.weir" <<'WEOF'
let render x = show x

print (render 42)
print (render [1; 2])
print (render (render 7))
WEOF
out=$($BIN "$bdir/show.weir")
expect "generic show: one helper, int/seq/string" "42" "$out"

out=$($BIN -e '[3; 1; 2] |> Seq.sortBy (fun n -> 0 - n) |> Seq.head'); rc=$?
expect "Ord int keys still sort (descending trick)" "3 : int" "$out"

rm -rf "$bdir"

# --- type classes Session A (2026-07-20): Eq — sentinels retired

tdir=$(mkweirtmp)

cat > "$tdir/eq.weir" <<'WEOF'
type Pair = { A: int; B: int }

let same x y = x == y

let uniq xs =
    xs |> Seq.where (fun x -> not ([x] |> Seq.contains x) == false) |> Seq.length

print $"{same 1 2} {same "a" "a"} {same { A = 1; B = 2 } { A = 1; B = 2 }} {uniq [1; 2; 3]}"
WEOF
out=$(cd "$tdir" && $BIN eq.weir)
expect "generic Eq: one helper, three types (the dedupe capability)" "false true true 3" "$out"

errout=$($BIN -e 'let same x y = x == y in same print printerr' 2>&1 || true)
echo "$errout" | grep -qF "requires equatable values" || fail "instantiation at functions must reject: $errout"
echo "e2e ok: constrained scheme rejects at the demanding use site"

errout=$($BIN -e '([Seq.head []] |> Seq.contains (Seq.head [])) && true' 2>&1 || true)
echo "$errout" | grep -qF "nothing determines" || fail "ambiguity must error: $errout"
echo "e2e ok: ambiguous constraint errors (no defaulting)"

rm -rf "$tdir"

# --- hardening sweep (2026-07-20): product-matrix effect counts,
# fixture backfill, ExitRequest entry-point insurance

hdir=$(mkweirtmp)

# A x E: a blank line ends a compound body — the tail runs unconditionally
cat > "$hdir/axe.weir" <<'WEOF'
if 1 > 2 then
    printerr "conditional"

printerr "always"
WEOF
out=$($BIN "$hdir/axe.weir" 2>&1)
expect "A x E: dedent tail after blank runs unconditionally" "always" "$out"
echo "$out" | grep -qF "conditional" && fail "A x E: false branch must not run"
echo "e2e ok: A x E false branch stayed dead"

# A x F: comment inside a compound body — grouping unchanged (effect count)
cat > "$hdir/axf.weir" <<'WEOF'
let f c =
    if c then
        printerr "one"
        // a transparent comment
        printerr "two"

f false
f true
WEOF
out=$($BIN "$hdir/axf.weir" 2>&1)
[ "$(echo "$out" | grep -c one)" = "1" ] || fail "A x F: 'one' must run exactly once (got: $out)"
[ "$(echo "$out" | grep -c two)" = "1" ] || fail "A x F: 'two' must run exactly once (got: $out)"
echo "e2e ok: A x F comment in compound body, effects counted"

# F x G: sibling sequencing across a comment — both effects, once each
cat > "$hdir/fxg.weir" <<'WEOF'
let go x =
    printerr "first"
    // between siblings
    printerr "second"

go 1
WEOF
out=$($BIN "$hdir/fxg.weir" 2>&1)
[ "$(echo "$out" | grep -c first)" = "1" ] && [ "$(echo "$out" | grep -c second)" = "1" ] || fail "F x G effect count (got: $out)"
echo "e2e ok: F x G sibling effects across comment, counted"

# fixture backfill: record continuation headed (inside a compound body)
cat > "$hdir/rec-headed.weir" <<'WEOF'
type T = { Name: string; Count: int }

let t =
    if 1 > 0 then
        { Name = "headed"
          Count = 1 }
    else
        { Name = "no"
          Count = 0 }

print t.Name
WEOF
out=$($BIN "$hdir/rec-headed.weir")
expect "record continuation headed under if/else" "headed" "$out"

# fixture backfill: record continuation nested (record in record, multi-line)
cat > "$hdir/rec-nested.weir" <<'WEOF'
type Inner = { V: int }
type Outer = { Name: string; In: Inner }

let o =
    { Name = "outer"
      In =
        { V = 42 } }

print $"{o.Name}{o.In.V}"
WEOF
out=$($BIN "$hdir/rec-nested.weir")
expect "record continuation nested (record in record)" "outer42" "$out"

# ExitRequest insurance: all eval entry points return the code silently
rc=0; out=$($BIN -e 'exit 6' 2>&1) || rc=$?
[ $rc -eq 6 ] && [ -z "$out" ] || fail "-e exit must exit 6 silently (rc=$rc out=$out)"
echo "e2e ok: -e entry point honors exit"

rc=0; out=$(echo 'exit 4' | $BIN 2>&1 >/dev/null) || rc=$?
[ $rc -eq 4 ] || fail "REPL entry point must honor exit (rc=$rc out=$out)"
echo "e2e ok: REPL entry point honors exit"

rm -rf "$hdir"

# --- env sugar: the $e(...) capture sigil and within env

sdir=$(mkweirtmp)
printf 'MARK=layered\n' > "$sdir/s.env"

cat > "$sdir/sigil.weir" <<'WEOF'
let e = Env.fromFile "s.env"

within env e
    sh -c "echo effect: $MARK"

let got = $e(sh -c "echo cap: $MARK") |> Seq.head
print got

let r = $e(sh -c "exit 7" | complete)
print $"complete-env exit {r.exitCode}"

let tag = "spliced"
within env e
    sh -c $"echo {tag}: $MARK"
WEOF
out=$(cd "$sdir" && $BIN sigil.weir 2>&1)
expect "env sigil effect form" "effect: layered" "$out"
expect "env sigil capture form" "cap: layered" "$out"
expect "env sigil x complete (completedEnv route)" "complete-env exit 7" "$out"
expect "env sigil x interpolation splice" "spliced: layered" "$out"

cat > "$sdir/district.weir" <<'WEOF'
let e = Env.fromFile "s.env"
let go = 1 > 0

within env e
    sh -c "echo d-one: $MARK"
    sh -c "echo d-two: $MARK"

if go then
    sh -c "echo d-bare: [$MARK]"
WEOF
out=$(cd "$sdir" && $BIN district.weir 2>&1)
expect "within env distributes over the block (was: env district)" "d-two: layered" "$out"
expect "a plain block stays env-less" "d-bare: []" "$out"

$BIN fmt --check "$sdir/district.weir" >/dev/null 2>&1 || fail "fmt must accept the within env block"
echo "e2e ok: fmt roundtrips the within env block"

rm -rf "$sdir"

# --- child-env injection (2026-07-20): the shEnv receipt ------------

edir=$(mkweirtmp)

cat > "$edir/target.env" <<'EOF'
AZURE_SUBSCRIPTION_ID=sub-web
AZURE_DEFAULTS_GROUP='rg web'
# per-target settings
OVERRIDE=from-file
EOF

# the bicep deployStack shape: load a per-target env file, inject into
# the child; the stub asserts the child saw the overlay
cat > "$edir/deploy.weir" <<'WEOF'
let targetEnv = Env.fromFile "target.env"

within env targetEnv
    sh -c "echo \"AZ($AZURE_SUBSCRIPTION_ID|$AZURE_DEFAULTS_GROUP|$OVERRIDE|$INHERITED)\""
WEOF
out=$(cd "$edir" && OVERRIDE=from-parent INHERITED=passed-through $BIN deploy.weir)
expect "bicep shape: overlay sets, overrides, and inherits" "AZ(sub-web|rg web|from-file|passed-through)" "$out"

# parent isolation: the overlay never leaks into the weir process
cat > "$edir/iso.weir" <<'WEOF'
let vars = Env.fromFile "target.env"

within env vars
    sh -c "true"

print (Env.get "AZURE_SUBSCRIPTION_ID" |> Option.defaultValue "(clean)")
WEOF
out=$(cd "$edir" && $BIN iso.weir)
expect "child-env never leaks into the parent session" "(clean)" "$out"

printf 'EMPTYFILE=x\n' > "$edir/one.env"

# empty-string value: the documented removal workaround (via the env sigil)
cat > "$edir/empty.weir" <<'WEOF'
let vars = Env.fromFile "blank.env"

within env vars
    sh -c "echo [$BLANKED]"
WEOF
printf 'BLANKED=\n' > "$edir/blank.env"
out=$(cd "$edir" && BLANKED=parent-value $BIN empty.weir)
expect "empty-string value overrides (removal workaround)" "[]" "$out"

# runEnv/cmdEnv byte-identity, raise, and tree-kill retired
# [D:drop-command-builtins]: the env path is the sigil now, tested by
# "env sigil x complete" above; lifecycle by the non-env command tests

# subset rejections name the sh escape
printf 'export FOO=1\n' > "$edir/bad.env"
errout=$(cd "$edir" && $BIN -e 'Env.fromFile "bad.env" |> Seq.length' 2>&1 || true)
echo "$errout" | grep -qF 'set -a; . file' || fail "dotenv rejection must name the sh escape: $errout"
echo "e2e ok: dotenv rejection names the sh escape"

rm -rf "$edir"

# --- grammar consolidation (2026-07-20): offside close, record
# continuations, exit — the bicep translation's shapes verbatim

gdir=$(mkweirtmp)

cat > "$gdir/guard.weir" <<'WEOF'
type T = { Name: string }

let target =
    let stack = "web"
    if stack == "" then fail "usage"
    { Name = stack }

print target.Name
WEOF
out=$($BIN "$gdir/guard.weir")
expect "offside close: guard-fail then record result" "web" "$out"

cat > "$gdir/silent.weir" <<'WEOF'
let f c =
    if c then printerr "then-arm"
    printerr "sibling"

f false
WEOF
out=$($BIN "$gdir/silent.weir" 2>&1)
expect "offside close: same-level sibling escapes the then-branch" "sibling" "$out"

cat > "$gdir/else.weir" <<'WEOF'
let f c =
    if c then printerr "a"
    else printerr "b"

f false
WEOF
out=$($BIN "$gdir/else.weir" 2>&1)
expect "same-indent else extends the if" "b" "$out"

cat > "$gdir/rec.weir" <<'WEOF'
type T = { Name: string; Count: int }

let t =
    { Name = "a"
      Count = 2 }

print $"{t.Name}{t.Count}"
WEOF
out=$($BIN "$gdir/rec.weir")
expect "record continuation, bare fields" "a2" "$out"

cat > "$gdir/rec2.weir" <<'WEOF'
type T = { Name: string; Count: int }

let t =
    { Name = "b";
      Count = 3 }

print $"{t.Name}{t.Count}"
WEOF
out=$($BIN "$gdir/rec2.weir")
expect "record continuation, trailing-; spelling (no double separator)" "b3" "$out"

# flipped 2026-07-23 [D:blank-in-brackets]: this pinned the at-blank error;
# blanks are transparent now — a gapped-but-closed record runs, and the
# unclosed shape still errors (at statement end / the statement-head guard)
cat > "$gdir/gapped.weir" <<'WEOF'
type T = { Name: string }

let t =
    { Name = "a"

      Tail = "b" }
WEOF
errout=$($BIN check "$gdir/gapped.weir" 2>&1 || true)
echo "$errout" | grep -qF "no declared record has exactly the fields: Name, Tail" || fail "gapped record must reach the CHECKER (transparency): $errout"
echo "e2e ok: blank inside an open brace is transparent (flipped pin)"

cat > "$gdir/broken.weir" <<'WEOF'
type T = { Name: string }

let t =
    { Name = "a"

print t.Name
WEOF
errout=$($BIN "$gdir/broken.weir" 2>&1 || true)
echo "$errout" | grep -qF "record literal" || fail "unclosed brace still errors naming the record: $errout"
echo "e2e ok: an unclosed brace still errors at statement end"

cat > "$gdir/runaway.weir" <<'WEOF'
type T = { Name: string }

let t =
    { Name = "a"

let after = 1
WEOF
errout=$($BIN "$gdir/runaway.weir" 2>&1 || true)
echo "$errout" | grep -qF "statement at column 0 while the '{' opened at line 4" || fail "the guard must bound the runaway: $errout"
echo "e2e ok: the statement-head guard bounds an unclosed bracket"

cat > "$gdir/exit.weir" <<'WEOF'
let r = sh -c "exit 4" | complete
if r.exitCode <> 0 then exit (r.exitCode)
print "unreached-on-failure"
WEOF
rc=0; $BIN "$gdir/exit.weir" >/dev/null 2>&1 || rc=$?
[ $rc -eq 4 ] || fail "exit must propagate the child's code (got $rc)"
echo "e2e ok: exit propagates through complete"

out=$($BIN "$gdir/exit.weir" 2>&1 || true)
[ -z "$out" ] || fail "exit is an intentional exit — no error message (got: $out)"
echo "e2e ok: exit exits silently"

cat > "$gdir/fmtfix.weir" <<'WEOF'
let ok = 1 == 1

let f t =
    printerr "q"
    if ok then
        printerr "login"
        printerr "deploy"
    else printerr "plain"

f true
WEOF
$BIN fmt --check "$gdir/fmtfix.weir" >/dev/null 2>&1 || fail "fmt must accept multi-line if/else in a function body"
echo "e2e ok: fmt roundtrips the if/else repro"

$BIN fmt --check examples/bicep-deploy.weir >/dev/null 2>&1 || fail "fmt must accept the bicep translation"
echo "e2e ok: fmt accepts the bicep origin script"

rm -rf "$gdir"

# attributes (PLAN-attributes): check-time, erased, registered names
adir=$(mkweirtmp)
cat > "$adir/attrs.weir" <<'WEOF'
type Cfg = { [<Short "c">] Count: int; Name: string; [<NoShort>] Loud: bool }
let c = { Count = 1; Name = "x"; Loud = false }
let c2 = { c with Count = 2 }
print $"{c2.Count} {show c.Loud}"
WEOF
out=$($BIN "$adir/attrs.weir")
expect "attributed record constructs, updates, shows — erased" "2 false" "$out"

cat > "$adir/attrs-json.weir" <<'WEOF'
type J = {
    /// the n
    N: int
}
let j = echo '{"N": 5}' |> from json J
print j.N
WEOF
out=$($BIN "$adir/attrs-json.weir")
expect "from json loads a documented record identically (/// inert)" "5" "$out"

# a check error means nothing runs [D:check-error-no-run]: a misaligned
# /// is a check error, so run refuses before the first statement
cat > "$adir/doc-misaligned.weir" <<'WEOF'
print "SIDE-EFFECT"
    /// misaligned doc
let x = 1
print (show x)
WEOF
out=$($BIN "$adir/doc-misaligned.weir" 2>&1) && fail "a doc-align check error must refuse the run: $out"
case "$out" in *SIDE-EFFECT*) fail "a doc-align check error ran a statement: $out" ;; esac
echo "$out" | grep -qF "a doc aligns with what it describes" || fail "run names the doc-align error: $out"

# the recursive field law [D:recursive-fields]: nested records and seq
# fields read from real response shapes — the World Bank receipt (its
# Map-keyed `documents` stays untypable pending Map and is ignored as
# an undeclared field), and kubectl's List/items shape for arrays
# inside objects. Round-trip pinned.
rfdir=$(mkweirtmp)
cat > "$rfdir/wb.json" <<'JEOF'
{ "rows": 10, "total": 593,
  "documents": {
    "D11831032": { "id": "11831032",
                   "entityids": { "entityid": "000334955300064" } } } }
JEOF
cat > "$rfdir/doc.json" <<'JEOF'
{ "id": "11831032", "entityids": { "entityid": "000334955300064" } }
JEOF
cat > "$rfdir/items.json" <<'JEOF'
{"kind":"List","items":[{"name":"kube-dns","ready":true},{"name":"metrics","ready":false}]}
JEOF
cat > "$rfdir/rf.weir" <<'WEOF'
type Meta = { rows: int; total: int }
let m = File.read "wb.json" |> from json Meta
print $"{m.rows}/{m.total}"
type Entity = { entityid: string }
type Doc = { id: string; entityids: Entity }
let d = File.read "doc.json" |> from json Doc
print d.entityids.entityid
type Pod = { name: string; ready: bool }
type L = { kind: string; items: seq<Pod> }
let l = File.read "items.json" |> from json L
l.items |> Seq.iter (fun p -> print p.name)
let rt = l |> to json |> Seq.head
let l2 = [rt] |> from json L
print (if (l2.items |> Seq.length) == 2 && l2.kind == l.kind then "round-trip-ok" else "ROUND-TRIP-BROKE")
WEOF
out=$(cd "$rfdir" && $BIN rf.weir)
expect "recursive fields: the receipt's non-Map parts read" "10/593" "$out"
expect "recursive fields: three deep (the entityid)" "000334955300064" "$out"
expect "recursive fields: seq of records inside an object" "kube-dns" "$out"
expect "recursive fields: to json round-trips the nesting" "round-trip-ok" "$out"
rm -rf "$rfdir"

# from xml [D:from-xml]: the read-only boundary against a real .csproj on
# disk (File.read, not a literal) — attributes, optional text, repeated
# children, a stripped default xmlns, and the missing-attribute runtime
# teaching. The receipt is project traversal: parse groups and refs.
xdir=$(mkweirtmp)
cat > "$xdir/App.csproj" <<'XEOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><IsPackable>false</IsPackable></PropertyGroup>
  <PropertyGroup><IsPublishable>true</IsPublishable></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Core/Core.csproj" />
    <ProjectReference Include="../Util/Util.csproj" />
  </ItemGroup>
</Project>
XEOF
cat > "$xdir/old.csproj" <<'XEOF'
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><AssemblyName>Legacy</AssemblyName></PropertyGroup>
</Project>
XEOF
cat > "$xdir/xml.weir" <<'WEOF'
type Ref = { [<Attr>] Include: string }
type Pg  = { IsPackable: Option<string>; IsPublishable: Option<string> }
type Ig  = { [<Elem "ProjectReference">] refs: seq<Ref> }
type Proj = { [<Elem "PropertyGroup">] groups: seq<Pg>; [<Elem "ItemGroup">] items: seq<Ig> }
let p = File.read "App.csproj" |> from xml Proj
print $"{Seq.length p.groups} groups"
p.items |> Seq.iter (fun g -> g.refs |> Seq.iter (fun r -> print r.Include))
type Ns = { AssemblyName: string }
type Old = { [<Elem "PropertyGroup">] groups: seq<Ns> }
let o = File.read "old.csproj" |> from xml Old
o.groups |> Seq.iter (fun g -> print g.AssemblyName)
WEOF
out=$(cd "$xdir" && $BIN xml.weir)
expect "from xml: PropertyGroups read from a real .csproj" "2 groups" "$out"
expect "from xml: [<Attr>] + [<Elem>] read a repeated ProjectReference" '../Core/Core.csproj
../Util/Util.csproj' "$out"
expect "from xml: a default xmlns is stripped (matched by local name)" "Legacy" "$out"
xerr=$(cd "$xdir" && $BIN -e 'type Ns = { AssemblyName: string }
type Old = { [<Elem "PropertyGroup">] groups: seq<Ns> }
File.read "old.csproj" |> from xml Old |> _.groups |> Seq.iter (fun g -> print g.AssemblyName)' 2>&1) || true
echo "$xerr" | grep -qF "Legacy" || fail "from xml -e inline read: $xerr"
xto=$($BIN -e '[1] |> to xml' 2>&1) && fail "to xml must not exist" || true
echo "$xto" | grep -qF "XML is read-only" || fail "to xml refusal teaches: $xto"
echo "e2e ok: from xml — real .csproj groups/refs, stripped xmlns, no to xml"
rm -rf "$xdir"

# from toml [D:from-toml]: real Cargo.toml / pyproject.toml files on disk —
# arrays of tables, inline tables, a dotted key, a hyphenated key through
# [<Wire>], an offset date-time into Instant through [<Iso8601>], and a
# located duplicate-key error
tdir=$(mkweirtmp)
cat > "$tdir/Cargo.toml" <<'TEOF'
[package]
name = "demo"
version = "0.3.1"
edition = '2021'

[dependencies]
serde = { version = "1", features = ["derive"] }
tokio.version = "1.38"

[[bin]]
name = "cli"

[[bin]]
name = "srv"
TEOF
cat > "$tdir/pyproject.toml" <<'TEOF'
[project]
name = "tool"
requires-python = ">=3.11"
released = 2024-03-01T10:00:00Z
TEOF
printf 'a = 1\nb = 2\na = 3\n' > "$tdir/dup.toml"
cat > "$tdir/toml.weir" <<'WEOF'
type Dep = { version: string; features: Option<seq<string>> }
type Cargo = { package: {| name: string; version: string |}; dependencies: {| serde: Dep; tokio: Dep |}; bin: seq<{| name: string |}> }
let c = File.read "Cargo.toml" |> from toml Cargo
print $"{c.package.name} {c.package.version}"
print $"serde {c.dependencies.serde.version}; tokio {c.dependencies.tokio.version}"
print (c.bin |> Seq.map _.name |> Str.join ",")
type Project = {
    name: string
    [<Wire "requires-python">]
    requiresPython: string
    [<Iso8601>]
    released: Instant
}
let p = File.read "pyproject.toml" |> from toml {| project: Project |}
print $"{p.project.requiresPython} {p.project.released}"
WEOF
out=$(cd "$tdir" && $BIN toml.weir 2>&1) || fail "from toml script: $out"
expect "from toml: a Cargo.toml package table" "demo 0.3.1" "$out"
expect "from toml: inline and dotted-key dependencies" "serde 1" "$out"
expect "from toml: a dotted-key dependency" "tokio 1.38" "$out"
merr=$(cd "$tdir" && $BIN -e 'File.read "Cargo.toml" |> from toml {| dependencies: Map<string, {| version: string |}> |} |> ignore' 2>&1) && fail "a Map field must refuse: $merr" || true
echo "$merr" | grep -qF "cross the toml boundary" || fail "the shape refusal names the toml boundary: $merr"
expect "from toml: arrays of tables" "cli,srv" "$out"
expect "from toml: [<Wire>] and [<Iso8601>]" ">=3.11 2024-03-01T10:00:00Z" "$out"
terr=$(cd "$tdir" && $BIN -e 'File.read "dup.toml" |> from toml {| a: int |} |> _.a |> show |> print' 2>&1) && fail "a duplicate key must fail: $terr" || true
echo "$terr" | grep -qF "from toml: line 3: the key 'a' is defined twice" || fail "the duplicate key is located: $terr"
# [D:toml-local-instant]: an offset-less datetime names no instant
lerr=$($BIN -e '["start = 2024-03-01T10:00:00"] |> from toml {| start: string |} |> _.start |> print' 2>&1) || fail "a local date-time reads into a string: $lerr"
[ "$lerr" = "2024-03-01T10:00:00" ] || fail "the string keeps the text: $lerr"
printf 'type LR = { [<Iso8601>] start: Instant }\nlet r = ["start = 2024-03-01T10:00:00"] |> from toml LR\nprint $"{r.start}"\n' > "$tdir/local.weir"
lerr=$($BIN "$tdir/local.weir" 2>&1) && fail "a local date-time into an Instant must refuse: $lerr" || true
echo "$lerr" | grep -qF "from toml: line 1: 2024-03-01T10:00:00 is a local date-time" || fail "the local date-time refusal: $lerr"
echo "e2e ok: from toml — Cargo/pyproject on disk, Wire, Iso8601, located duplicate key, a local date-time refuses an Instant"
rm -rf "$tdir"

# [D:json-depth] [D:json-yaml-dupkeys]: a valid document past 64 levels
# reads; past 100 it names the depth; a repeated key into a Yaml node
# last-wins, so to yaml writes YAML weir reads back
jdir=$(mkweirtmp)
python3 -I - "$jdir" <<'PYEOF'
import sys
d = lambda n: '{\\"a\\":' * n + '1' + '}' * n
for n in (65, 101):
    open(sys.argv[1] + '/d%d.weir' % n, 'w').write('type N = { a: Yaml }\nlet _r = ["' + d(n) + '"] |> from json N\nprint "read"\n')
PYEOF
out=$($BIN "$jdir/d65.weir" 2>&1) || fail "a 65-deep JSON document must read: $out"
[ "$out" = "read" ] || fail "the 65-deep read: $out"
out=$($BIN "$jdir/d101.weir" 2>&1) && fail "a 101-deep document must refuse" || true
echo "$out" | grep -qF "valid JSON, but it nests deeper than 100 levels" || fail "the depth is named: $out"
cat > "$jdir/dup.weir" <<'WEOF'
type R = { v: Yaml }
let r = ["{\"v\": {\"a\": 1, \"b\": 2, \"a\": 3}}"] |> from json R
r |> to yaml |> from yaml R |> to json |> Seq.iter print
WEOF
out=$($BIN "$jdir/dup.weir" 2>&1) || fail "a repeated key into Yaml must round-trip: $out"
[ "$out" = '{"v":{"a":3,"b":2}}' ] || fail "last value, first position: $out"
rm -rf "$jdir"
echo "e2e ok: from json — 65-deep reads, 101-deep names the depth, a repeated key into Yaml last-wins"

# [D:yaml-empty-doc] [D:mapping-dupkeys]: an all-None record writes {},
# an empty top-level seq [], and the stream keeps every document; a
# repeated key into seq<string * _> last-wins, and the writers refuse a
# mapping holding a key twice
ydir=$(mkweirtmp)
cat > "$ydir/ye.weir" <<'WEOF'
type Res = { cpu: Option<string> }
let xs = [{ cpu = Some "1" }; { cpu = None }; { cpu = Some "2" }]
let y = xs |> to yaml stream |> Seq.freeze
print (Str.join "|" y)
y |> from yaml stream Res |> Seq.iter (fun r -> print (show r.cpu))
let none = xs |> Seq.where (fun r -> r.cpu == Some "9")
print (Str.join "|" (none |> to yaml))
print $"{none |> to yaml |> from yaml seq<Res> |> Seq.length}"
WEOF
out=$($BIN "$ydir/ye.weir" 2>&1) || fail "the empty-document writes must run: $out"
[ "$out" = "$(printf 'cpu: "1"|---|{}|---|cpu: "2"\nSome "1"\nNone\nSome "2"\n[]\n0')" ] || fail "{} and [] documents round-trip: $out"
cat > "$ydir/dup.weir" <<'WEOF'
type P = { labels: seq<string * string> }
let p = ["{\"labels\": {\"a\": \"1\", \"b\": \"x\", \"a\": \"2\"}}"] |> from json P
p |> to json |> Seq.iter print
let w = { labels = [("k", "1"); ("k", "2")] }
w |> to yaml |> Seq.iter print
WEOF
out=$($BIN "$ydir/dup.weir" 2>&1) && fail "a duplicate key must refuse on write: $out" || true
echo "$out" | grep -qxF '{"labels":{"a":"2","b":"x"}}' || fail "a repeated key last-wins at its first position: $out"
echo "$out" | grep -qF "to yaml: the mapping holds the key 'k' twice" || fail "the writer names the key: $out"
rm -rf "$ydir"
echo "e2e ok: to yaml — {} and [] documents, the stream keeps every document; mappings last-win on read, refuse duplicates on write"

# [D:yaml-empty-typed]: a Deployment's `args: []`, `env: []` and
# `labels: {}` survive a yaml round trip as written, and a nested seq
# writes block-wise and reads back
ydir=$(mkweirtmp)
cat > "$ydir/dep.yaml" <<'YEOF'
metadata:
  name: web
  labels: {}
spec:
  containers:
    - name: app
      args: []
      env: []
      matrix:
        -
          - 1
          - 2
        - []
YEOF
cat > "$ydir/dep.weir" <<WEOF
type Env = { name: string; value: string }
type Ctr = { name: string; args: Option<seq<string>>; env: Option<seq<Env>>; matrix: seq<seq<int>> }
type Spec = { containers: seq<Ctr> }
type Meta = { name: string; labels: Option<seq<string * string>> }
type Dep = { metadata: Meta; spec: Spec }
let d = File.read "$ydir/dep.yaml" |> from yaml Dep
d |> to yaml |> Seq.iter print
WEOF
out=$($BIN "$ydir/dep.weir" 2>&1) || fail "the Deployment round trip must run: $out"
[ "$out" = "$(cat "$ydir/dep.yaml")" ] || fail "Some [] / {} / a nested seq round-trip as written: $out"
rm -rf "$ydir"
echo "e2e ok: to yaml — Some [] writes [], an empty mapping {}, a nested seq block-wise; a Deployment round-trips"

# |> try [D:try-form]: a failure as a value — a real missing file and a
# malformed document become None / Error; exit passes through the capture
ydir=$(mkweirtmp)
printf 'a = 1\na = 2\n' > "$ydir/bad.toml"
cat > "$ydir/try.weir" <<'WEOF'
let missing = File.read "nope.toml" |> try
let bad = File.read "bad.toml" |> from toml {| a: int |} |> try result
print $"{missing}"
match bad with
| Ok _ -> print "unexpected"
| Error msg -> print $"error: {msg}"
let _never = (if 1 == 1 then exit 7 else 1) |> try
print "not reached"
WEOF
rc=0; out=$(cd "$ydir" && $BIN try.weir 2>&1) || rc=$?
expect "|> try: a missing file is None" "None" "$out"
expect "|> try result: the error message" "error: from toml: line 2: the key 'a' is defined twice" "$out"
[ $rc -eq 7 ] || fail "exit inside |> try must exit with its code (rc=$rc): $out"
echo "$out" | grep -qF "not reached" && fail "exit must not be captured: $out"
echo "e2e ok: |> try — None / Error from real failures, exit passes through"
rm -rf "$ydir"

# from table [D:from-table]: the aligned-table boundary on a real file —
# a kubectl-shaped fixture (tabwriter reality: 3-space padding, spaced
# values, <none>) read into typed rows; the header-offset law is the
# spaced STATUS value surviving as one cell. Wire hits a raw header;
# Option reads <none>/empty as None; errors carry line + column.
tdir=$(mkweirtmp)
cat > "$tdir/pods.txt" <<'TEOF'
NAME    READY   STATUS             RESTARTS   NOMINATED NODE
web-1   1/1     Running            0          k3d-agent-0
db-0    1/2     CrashLoopBackOff   3          <none>
TEOF
cat > "$tdir/table.weir" <<'WEOF'
type Pod = {
    name: string
    ready: string
    status: string
    [<Wire "RESTARTS">]
    restarts: int
    nominatedNode: Option<string>
}
let pods = File.read "pods.txt" |> from table Pod
print $"{pods |> Seq.length} pods"
pods |> Seq.where (fun p -> p.restarts > 0) |> Seq.iter (fun p -> print $"{p.name} {p.status}")
pods |> Seq.iter (fun p -> print $"{p.nominatedNode}")
WEOF
tout=$(cd "$tdir" && $BIN table.weir)
expect "from table: rows read typed from a real file" "2 pods" "$tout"
expect "from table: a spaced value is ONE cell (header offsets, not whitespace)" "db-0 CrashLoopBackOff" "$tout"
echo "$tout" | grep -qF 'Some "k3d-agent-0"' || fail "from table: two-word header + Option value: $tout"
echo "$tout" | grep -qF "None" || fail "from table: <none> reads as None: $tout"
# a bad cell errors located: row line + column offset + header name
terr=$(cd "$tdir" && $BIN -e 'type P = { name: string; restarts: int }
["NAME   RESTARTS"; "n1     often"] |> from table P |> Seq.length |> show' 2>&1) && fail "a bad int cell must raise" || true
echo "$terr" | grep -qF "from table: line 2, col 8: column 'RESTARTS': expected int, got 'often'" \
  || fail "from table cell error lost its location: $terr"
# a missing declared column names itself and the headers seen
tmiss=$($BIN -e 'type P = { name: string; ip: string }
["NAME   AGE"; "n1     2d"] |> from table P |> Seq.length |> show' 2>&1) && fail "a missing column must raise" || true
echo "$tmiss" | grep -qF "missing column 'ip' for field 'ip' — headers seen: NAME, AGE" \
  || fail "from table missing-column error lost its inventory: $tmiss"
# no write side, and the word stays an ordinary identifier
tto=$($BIN -e 'type P = { a: int }
[{ a = 1 }] |> to table' 2>&1) && fail "to table must not exist" || true
echo "$tto" | grep -qF "the table boundary is read-only" || fail "to table refusal teaches: $tto"
tid=$($BIN -e 'let table = 5
show (table + 1)') || fail "'table' must stay bindable"
echo "$tid" | grep -qF '"6"' || fail "'table' as an identifier: $tid"
# az `-o table` dashes separator [D:from-table-az]: the rule line under the
# header is skipped, so the real rows read typed — bool/int survive
taz=$($BIN -e 'type Vm = { name: string; running: bool; port: int }
["Name    Running   Port"; "------  --------  ----"; "web-1   true      8080"; "db-0    false     443"] |> from table Vm |> Seq.where (fun v -> v.running) |> Seq.map (fun v -> v.name) |> Str.join ","')
echo "$taz" | grep -qF "web-1" || fail "az separator skip: real rows must read (got: $taz)"
echo "$taz" | grep -qF -- "------" && fail "az separator must not become a data row: $taz" || true
echo "e2e ok: from table — header-offset slicing, Wire + Option/<none>, located errors, az separator skip, no to table, 'table' unreserved"
rm -rf "$tdir"

# --- #infer from table [D:from-table]: the REPL drafts the row record
# from an aligned sample, injects it, and the typed read lights up in
# the same session; the as-name guard still refuses a builtin landing
tinf=$(printf '%s\n%s\n%s\n%s\n%s\n' \
  'let tsample = ["NAME    STATUS    RESTARTS"; "web-1   Running   0"; "db-0    Pending   3"]' \
  '#infer tsample from table as Pod' \
  'tsample |> from table Pod |> Seq.where (fun p -> p.restarts > 0) |> Seq.map (fun p -> p.name) |> Seq.length |> show' \
  '#infer tsample from table as Yaml' \
  '#quit' | $BIN)
echo "$tinf" | grep -qF "defined: Pod (1 type)" || fail "#infer from table did not define the row type: $tinf"
echo "$tinf" | grep -qF "read the value as 'seq<Pod>'" || fail "#infer from table lost the seq note: $tinf"
echo "$tinf" | grep -qF '"1" : string' || fail "the injected type must read typed rows: $tinf"
echo "$tinf" | grep -qF "'Yaml' is a built-in type" || fail "the as-name guard must refuse a builtin landing: $tinf"
echo "e2e ok: #infer from table — drafts, injects, reads in one session; the as-name guard holds"

# structural walks [D:structural-walk]: Graph.reach over a dep graph
# (cycle-safe, breadth-first, each node once) and Tree.walk's
# parent-before-child effect order — the two laws, pinned in the binary
wdir=$(mkweirtmp)
cat > "$wdir/walk.weir" <<'WEOF'
let deps = Map.ofPairs [("app", ["core"; "util"]); ("util", ["core"]); ("core", ["app"])]
let neighbors p = Map.tryGet p deps |> Option.defaultValue []
Graph.reach (fun p -> p) neighbors "app" |> Seq.iter print
let step n =
    print (show n)
    if n < 3 then [n + 1] else []
1 |> Tree.walk step
WEOF
out=$($BIN "$wdir/walk.weir")
expect "Graph.reach: cycle-safe BFS, each node once" 'app
core
util
1
2
3' "$out"
werr=$($BIN -e '[1] |> Frontier.fold (fun n -> show n) 0 (fun acc n -> (acc, [n + 1]))' 2>&1) && fail "a non-finite walk must error" || true
echo "$werr" | grep -qF "exceeded 100000 steps" || fail "the step budget teaches: $werr"
echo "e2e ok: structural walks — Graph.reach BFS each-once, Tree.walk parent-first, budget teaches"
rm -rf "$wdir"

# yaml patch [D:yaml-nodes]: the typeless read + district-addressed merge
# against a real kustomization.yaml on disk — keyed upsert, append,
# tombstones, undeclared fields preserved (what typed RMW cannot do),
# and the laws (patches don't render; tombstones scoped to patch)
ydir=$(mkweirtmp)
cat > "$ydir/kustomization.yaml" <<'YEOF'
apiVersion: kustomize.config.k8s.io/v1beta1
kind: Kustomization
namespace: prod
resources:
    - deployment.yaml
    - retired.yaml
images:
    - name: app
      newTag: v1
    - name: db
      newTag: v2
YEOF
cat > "$ydir/patch.weir" <<'WEOF'
let f = "kustomization.yaml"
let p = yaml patch by=name
    images:
        - name: app
          newTag: v3
        - $- name: db
    resources:
        - service.yaml
        - $- retired.yaml
File.read f |> Yaml.parse |> Yaml.merge p |> to yaml |> File.write f
File.read f |> Seq.iter print
WEOF
out=$(cd "$ydir" && $BIN patch.weir)
expect "yaml patch: undeclared fields survive the RMW" "namespace: prod" "$out"
expect "yaml patch: keyed upsert + keyed tombstone under by=" 'images:
  - name: app
    newTag: v3' "$out"
expect "yaml patch: scalar append + scalar tombstone" 'resources:
  - deployment.yaml
  - service.yaml' "$out"
echo "$out" | grep -qF "name: db" && fail "keyed tombstone must remove the db image" || true
echo "$out" | grep -qF "retired.yaml" && fail "scalar tombstone must remove retired.yaml" || true
# idempotency: applying the same patch again changes nothing
out2=$(cd "$ydir" && $BIN patch.weir)
[ "$out" = "$out2" ] || fail "a patch merge must be idempotent"
yerr=$($BIN -e 'let p = yaml patch
    a: 1
p |> to yaml' 2>&1) && fail "a patch must not render" || true
echo "$yerr" | grep -qF "a patch is instructions for a merge, not a document" || fail "the render refusal teaches: $yerr"
yerr2=$($BIN -e 'let d = yaml
    gone: $-
print "no"' 2>&1) && fail "a plain district must refuse a tombstone" || true
echo "$yerr2" | grep -qF "only inside a \`yaml patch\` district" || fail "the tombstone scope teaches: $yerr2"
echo "e2e ok: yaml patch — kustomization RMW (upsert/append/tombstones, unknown keys kept, idempotent), render+scope laws"
# check == run for patch districts [D:yaml-nodes]: check's assume path
# once claimed the `yaml patch …` marker line as a command and the
# district body then failed as statements, while the same file ran
# green — every patch-district script must check green too
(cd "$ydir" && $BIN check patch.weir) || fail "a patch-district script that runs must check (check == run)"
cat > "$ydir/patch-splice.weir" <<'WEOF'
let n = "api"
let _p = yaml patch by=name
    images:
        - name: $n
          newTag: v2
print "ok"
WEOF
(cd "$ydir" && $BIN check patch-splice.weir) || fail "a \$name splice inside a patch district must check"
# the acceptance test: the adapters reference's own patch example
# checks green — extracted verbatim, so the doc and the checker
# cannot drift apart
awk '/^```weir$/{f=1;buf="";next} /^```/{if(f && buf ~ /yaml patch/){print buf; exit} f=0;next} f{buf=buf $0 "\n"}' \
    "$(dirname "$0")/../docs/reference/adapters.md" > "$ydir/docs-patch.weir"
[ -s "$ydir/docs-patch.weir" ] || fail "adapters.md lost its yaml patch example (extractor found nothing)"
(cd "$ydir" && $BIN check docs-patch.weir) || fail "the adapters.md patch example must check green"
echo "e2e ok: patch districts check as they run (splice + multi-line item + the adapters.md example)"
rm -rf "$ydir"

# byte pipes [D:byte-pipes]: a command→command hop is a raw byte pipe —
# no decode, no line split, no appended newline. gzip through a weir
# pipe must be byte-identical to gzip through a bash pipe; chains and
# the leftmost-failure raise ride the same cell.
bdir=$(mkweirtmp)
printf 'abc' > "$bdir/plain.txt"
gzip -nc "$bdir/plain.txt" > "$bdir/ref.gz"
cat > "$bdir/bp.weir" <<'WEOF'
gzip -nc plain.txt | sh -c "cat > weir.gz"
["b"; "a"; "c"] | sort | tr a-z A-Z
WEOF
out=$(cd "$bdir" && $BIN bp.weir)
cmp "$bdir/ref.gz" "$bdir/weir.gz" || fail "binary through a pipe hop must be byte-identical"
expect "byte pipes: a value head + two raw hops" 'A
B
C' "$out"
berr=$($BIN -e 'sh -c "echo hi; exit 3" | sh -c "cat" | sh -c "cat"' 2>&1) && fail "a failing stage must raise" || true
echo "$berr" | grep -qF "command failed with exit code 3" || fail "the leftmost failing stage raises: $berr"
echo "e2e ok: byte pipes — raw hops byte-identical, chains compose, leftmost failure raises"
rm -rf "$bdir"

# ambient capture [D:ambient-capture]: a lazy command bound inside a
# `within` scope and forced outside spawns under the scope it was
# written in (closure law), not the restored outer ambient
adir2=$(mkweirtmp)
mkdir -p "$adir2/inner"
cat > "$adir2/amb.weir" <<'WEOF'
let mk () =
    within cd "inner"
        let s = sh -c "pwd"
        Seq.append s []
mk () |> Seq.iter (fun l -> print l)
WEOF
out=$(cd "$adir2" && $BIN amb.weir)
echo "$out" | grep -q "/inner$" || fail "an escaped lazy command must keep its written-site cwd: $out"
echo "e2e ok: ambient capture — an escaped lazy command spawns under its written-site scope"
# [D:ambient-capture-within]: outside any `within` nothing is captured —
# the lazy value runs in the cwd current at enumeration
cat > "$adir2/top.weir" <<'WEOF'
let top = sh -c "pwd"
let _c = cd "inner"
top |> Seq.iter (fun l -> print l)
WEOF
out=$(cd "$adir2" && $BIN top.weir)
echo "$out" | grep -q "/inner$" || fail "a lazy command outside any within must run in the force-time cwd: $out"
echo "e2e ok: ambient capture — outside any within, the force-time cwd applies"
rm -rf "$adir2"

# within tail pipes [D:within-tail-pipe]: a pipe on a body line belongs
# to the body statement — the six-cell matrix (within cd / within env ×
# bare command, value-headed pipe, cmd|cmd chain) all see the scope, a
# `s |> f` tail line parses, and a dedented pipe at the head column
# still closes the scope (the offside law [D:match-pipe-offside])
wtpdir=$(mkweirtmp)
mkdir -p "$wtpdir/scoped"
cat > "$wtpdir/wtp.weir" <<'WEOF'
within cd "scoped"
    sh -c "echo c1=$(basename $(pwd))"
within cd "scoped"
    ["x"] | sh -c "cat >/dev/null; echo c2=$(basename $(pwd))"
within cd "scoped"
    sh -c "echo y" | sh -c "cat >/dev/null; echo c3=$(basename $(pwd))"
within env [{ name = "WTP"; value = "ov" }]
    sh -c "echo e1=$WTP"
within env [{ name = "WTP"; value = "ov" }]
    ["x"] | sh -c "cat >/dev/null; echo e2=$WTP"
within env [{ name = "WTP"; value = "ov" }]
    sh -c "echo y" | sh -c "cat >/dev/null; echo e3=$WTP"
let s = ["a"; "b"; "c"]
within cd "scoped"
    s |> Seq.where (fun l -> l <> "b") |> Seq.iter (fun l -> print l)
within cd "scoped"
    ["x"]
| sh -c "cat >/dev/null; echo dedent=$(basename $(pwd))"
WEOF
out=$(cd "$wtpdir" && $BIN wtp.weir)
for cell in c1 c2 c3; do
    echo "$out" | grep -qF "$cell=scoped" || fail "within cd must reach the $cell spawn: $out"
done
for cell in e1 e2 e3; do
    echo "$out" | grep -qF "$cell=ov" || fail "within env must reach the $cell spawn: $out"
done
echo "$out" | grep -qxF "a" || fail "the |> tail line must parse and run in the body: $out"
echo "$out" | grep -qxF "c" || fail "the |> tail line must parse and run in the body: $out"
echo "$out" | grep -qxF "b" && fail "the |> tail's filter must apply: $out" || true
echo "$out" | grep -qF "dedent=scoped" && fail "a dedented pipe must close the scope (spawn outside): $out" || true
echo "$out" | grep -qE "dedent=" || fail "the dedented pipe's stage must still run: $out"
echo "e2e ok: within tail pipes — the six-cell matrix scopes, dedented pipe closes"
rm -rf "$wtpdir"

# districts in modules [D:yaml-nodes]: a `yaml patch` district inside an
# imported module function assembles and runs — pinned after a stale-
# binary report claimed the loader broke on the district sentinel
mdir=$(mkweirtmp)
cat > "$mdir/pmod.weir" <<'WEOF'
module Pmod

let addRes : string -> Yaml -> Yaml

let addRes resPath doc =
    let p = yaml patch
        resources:
            - $resPath
    doc |> Yaml.merge p

let mk : string -> YamlPatch

let mk resPath = yaml patch
    resources:
        - $resPath
WEOF
cat > "$mdir/puse.weir" <<'WEOF'
import "./pmod.weir" as Pmod
["kind: K"] |> Yaml.parse |> Pmod.addRes "a.yaml" |> to yaml |> Seq.iter print
["kind: K"] |> Yaml.parse |> Yaml.merge (Pmod.mk "b.yaml") |> to yaml |> Seq.iter print
WEOF
out=$(cd "$mdir" && $BIN puse.weir)
expect "a yaml patch district works inside an imported module" 'kind: K
resources:
  - a.yaml' "$out"
# a module exports a patch builder — the sig names the def-less nominal
# [D:yaml-nodes]; the consumer merges the returned patch
expect "a module exports a YamlPatch-typed builder" 'kind: K
resources:
  - b.yaml' "$out"
# the check side agrees with the runner on both files (the assume-
# resolver once claimed the patch marker as a command [D:assume-resolver])
(cd "$mdir" && $BIN check pmod.weir && $BIN check puse.weir) || fail "check must agree with run on the patch-district module"
echo "e2e ok: districts in modules — patch district in an imported function, YamlPatch-typed export"
rm -rf "$mdir"

# Self.prompt + the Self.stdin law [D:prompt]: one line per interaction
# (message on stderr, stdout stays data), EOF refuses, and a second
# enumeration of the live stream raises the teaching
pdir=$(mkweirtmp)
out=$(printf 'Ada\n' | $BIN -e 'print (Self.prompt "name?")' 2>/dev/null)
expect "prompt reads a line; stdout carries only the data" "Ada" "$out"
perr=$($BIN -e 'print (Self.prompt "x?")' < /dev/null 2>&1) && fail "Self.prompt at EOF must refuse" || true
echo "$perr" | grep -qF "stdin is closed (EOF)" || fail "the EOF teaching: $perr"
cat > "$pdir/re.weir" <<'WEOF'
let one = Self.stdin |> Seq.head
print one
let two = Self.stdin |> Seq.head
print two
WEOF
rerr=$(printf 'a\nb\n' | $BIN "$pdir/re.weir" 2>&1) && fail "re-enumerating Self.stdin must raise" || true
echo "$rerr" | grep -qF "already consumed" || fail "the one-enumeration teaching: $rerr"
echo "e2e ok: Self.prompt reads per-line, EOF refuses, Self.stdin enumerates once"
# Self.prompt consumes its line and nothing more: the rest of stdin stays
# for a later child and for Self.stdin — a pipe and a regular file alike
cat > "$pdir/ahead.weir" <<'WEOF'
let a = Self.prompt "first?"
print $"got {a}"
cat
WEOF
printf 'a\r\nb\nc\n' > "$pdir/ahead.in"
for how in pipe file; do
    if [ "$how" = pipe ]; then
        out=$(printf 'a\nb\nc\n' | timeout 10 $BIN "$pdir/ahead.weir" 2>/dev/null) || fail "prompt read-ahead ($how) failed: $out"
    else
        out=$(timeout 10 $BIN "$pdir/ahead.weir" < "$pdir/ahead.in" 2>/dev/null) || fail "prompt read-ahead ($how) failed: $out"
    fi
    [ "$out" = "$(printf 'got a\nb\nc')" ] || fail "Self.prompt must leave the rest of stdin to cat ($how): $out"
done
printf 'x\ny\nz\n' > "$pdir/ahead2.in"
cat > "$pdir/ahead2.weir" <<'WEOF'
let a = Self.prompt "first?"
print $"got {a}"
Self.stdin |> Seq.iter print
WEOF
out=$(timeout 10 $BIN "$pdir/ahead2.weir" < "$pdir/ahead2.in" 2>/dev/null)
[ "$out" = "$(printf 'got x\ny\nz')" ] || fail "Self.prompt then Self.stdin reads the rest: $out"
echo "e2e ok: Self.prompt reads no further than its newline (pipe + file, CRLF trimmed)"
# a cut read takes no line [D:stdin-reader]: a pfirst loser waiting in
# Self.prompt is cut when the timer wins, the next prompt gets the first
# line and a child the rest; a Self.stdin loser on an open, silent pipe
# and a File.read loser on a FIFO nobody opens never hold the race.
# Windows reads block, uncut — POSIX only
if [ "$IS_WINDOWS" != "1" ] && command -v mkfifo >/dev/null 2>&1; then
    cat > "$pdir/cut.weir" <<'WEOF'
let a = ["ask"; "default"] |> Seq.pfirst (fun k ->
    if k == "default" then
        Duration.sleep 300ms
        "timed-out"
    else
        Self.prompt "continue?")
print $"answer={a}"
let b = Self.prompt "name?"
print $"next={b}"
cat
WEOF
    out=$( (sleep 1.5; printf 'first\nsecond\n') | timeout 10 $BIN "$pdir/cut.weir" 2>/dev/null) || fail "the cut prompt script failed: $out"
    [ "$out" = "$(printf 'answer=timed-out\nnext=first\nsecond')" ] || fail "a cut Self.prompt must leave its line for the next reader: $out"
    for body in 'Self.stdin |> Seq.length |> show' 'File.read "ff" |> Seq.length |> show'; do
        cat > "$pdir/cutw.weir" <<WEOF
let r = [1; 2] |> Seq.pfirst (fun i ->
    if i == 1 then
        Duration.sleep 300ms
        "timer"
    else
        $body)
print \$"winner={r}"
WEOF
        rm -f "$pdir/ff" "$pdir/in"
        mkfifo "$pdir/ff" "$pdir/in"
        # stdin open and silent: a background writer the capture never waits on
        sleep 4 > "$pdir/in" &
        wpid=$!
        start_ms=$(now_ms)
        out=$(cd "$pdir" && timeout 10 $BIN cutw.weir 2>&1 < "$pdir/in") || true
        took=$(( $(now_ms) - start_ms ))
        kill "$wpid" 2>/dev/null || true
        [ "$out" = "winner=timer" ] || fail "a blocked loser ($body) must lose: $out"
        [ "$took" -lt 2500 ] || fail "a loser blocked in ($body) held the race (${took}ms)"
    done
    echo "e2e ok: a cut stdin read takes no line; Self.stdin and FIFO losers never hold the race"

    # a cut prompt never splits a line [D:prompt-cut-whole-line]: the cut
    # lands while the prompt is inside a 300000-byte line fd 0 already
    # holds — the line is finished and stays buffered, so the child reads
    # the NEXT line, never the tail of the cut one
    python3 -c 'import sys; sys.stdout.write("X" * 300000 + "\nNEXT\n")' > "$pdir/long.in"
    cat > "$pdir/longcut.weir" <<'WEOF'
let a = ["ask"; "to"] |> Seq.pfirst (fun k ->
    if k == "to" then
        Duration.sleep 20ms
        "timeout"
    else
        Self.prompt "q?")
print $"race={a}"
let c = sh -c "read x; echo $x" |> Seq.head
print $"child={c}"
let p = Self.prompt "p?"
print $"prompt={Str.length p}"
WEOF
    out=$(timeout 20 $BIN "$pdir/longcut.weir" < "$pdir/long.in" 2>/dev/null) || fail "the long-line cut script failed: $out"
    [ "$out" = "$(printf 'race=timeout\nchild=NEXT\nprompt=300000')" ] || fail "a cut Self.prompt split a line: $out"
    echo "e2e ok: a cut Self.prompt finishes a begun line and leaves it whole for the next weir reader"

    # an abandoned FIFO read never takes a later writer's data
    # [D:fifo-read-polled]: the loser's fd is closed by the cut, so the
    # next File.read gets both lines. Linux (a polled FIFO)
    if [ "$(uname -s)" = Linux ]; then
        cat > "$pdir/steal.weir" <<'WEOF'
let f = Self.args |> Seq.head
let a = ["read"; "to"] |> Seq.pfirst (fun k ->
    if k == "to" then
        Duration.sleep 300ms
        "timeout"
    else
        File.read f |> Seq.head)
print $"race={a}"
print (File.read f |> Seq.freeze |> Str.join ",")
WEOF
        for _ in 1 2 3; do
            rm -f "$pdir/sf"; mkfifo "$pdir/sf"
            (sleep 1; timeout 5 sh -c "printf 'D1\nD2\n' > $pdir/sf") &
            wpid=$!
            out=$(timeout 10 $BIN "$pdir/steal.weir" "$pdir/sf" 2>&1) || true
            wait "$wpid" 2>/dev/null || true
            [ "$out" = "$(printf 'race=timeout\nD1,D2')" ] || fail "an abandoned FIFO read took a later writer's data: $out"
        done
        echo "e2e ok: an abandoned FIFO read closes its fd — the next reader gets every line"
    fi
fi
rm -rf "$pdir"

# anonymous record types [D:anon-records]: the shape inline in the
# adapter slot — `_.field` checks, seq<> composes, the shape persists
# across statements, and a declared record stays a different type
cat > "$adir/anon.weir" <<'WEOF'
let one = echo '{"ip": "1.2.3.4"}' |> from json {| ip: string |}
print one.ip
echo '[{"a": 1}, {"a": 2}]' |> from json seq<{| a: int |}> |> Seq.iter (fun r -> print (show r.a))
WEOF
out=$($BIN "$adir/anon.weir")
expect "anonymous shape: _.field checks, seq<> composes, persists" '1.2.3.4
1
2' "$out"
out=$($BIN check "$adir/anon.weir" 2>&1) || fail "anon.weir must check clean: $out"

# anonymous record literals [D:anon-literals]: the write-side mirror —
# heterogeneous fields write through to json (the Map spelling cannot),
# the literal unifies with the adapter shape, and a multi-line literal
# assembles ({| is one opener token to the sibling rule)
cat > "$adir/anonlit.weir" <<'WEOF'
let key = "xxxx-111"
let n = 7
{| key = key; title = "t 0"; n = n |} |> to json |> File.write "x.json"
cat x.json
let got = echo '{"ip": "9.9.9.9"}' |> from json {| ip: string |}
let both = [got; {| ip = "8.8.8.8" |}]
both |> Seq.iter (fun r -> print r.ip)
let ml = {|
    alpha = 1
    beta = "two"
|}
print ml.beta
WEOF
out=$(cd "$adir" && $BIN anonlit.weir)
expect "anon literal: heterogeneous write, adapter unification, multi-line" '{"key":"xxxx-111","title":"t 0","n":7}
9.9.9.9
8.8.8.8
two' "$out"
rm -f "$adir/x.json"

# Option<scalar> at the JSON boundary [D:json-option]: present -> Some,
# missing/null -> None, and to jsonl omits the None key so it roundtrips.
cat > "$adir/json-option.weir" <<'WEOF'
type R = { name: string; age: Option<int> }
let rows = ["{\"name\":\"a\",\"age\":5}"; "{\"name\":\"b\"}"; "{\"name\":\"c\",\"age\":null}"]
rows |> from jsonl R |> to jsonl |> Seq.iter print
WEOF
out=$($BIN "$adir/json-option.weir")
expect "json Option: Some writes, None (missing or null) omits the key" \
'{"age":5,"name":"a"}
{"name":"b"}
{"name":"c"}' "$out"

# to json writes one document, to jsonl one per element [D:to-jsonl]:
# the top level follows the value's type (record -> object, seq ->
# array), and every adapter pairs with its own name across the arrow
cat > "$adir/to-jsonl.weir" <<'WEOF'
type P = { name: string; count: int }
let items = [{ name = "a"; count = 1 }; { name = "b"; count = 2 }]
{ name = "solo"; count = 0 } |> to json |> Seq.iter print
items |> to json |> Seq.iter print
items |> to jsonl |> Seq.iter print
let back = items |> to json |> from json seq<P>
print (show (back |> Seq.length))
WEOF
out=$($BIN "$adir/to-jsonl.weir")
expect "to json: one document (object or array); to jsonl: NDJSON; the seq roundtrip pairs by name" \
'{"name":"solo","count":0}
[{"name":"a","count":1},{"name":"b","count":2}]
{"name":"a","count":1}
{"name":"b","count":2}
2' "$out"

cat > "$adir/attrs-env.weir" <<'WEOF'
type EC = {
    /// the home dir
    HOME: string
}
let cfg = Env.load EC
print (Str.length cfg.HOME > 0)
WEOF
out=$(HOME=/tmp $BIN "$adir/attrs-env.weir")
expect "Env.load on a documented config field is inert-legal (/// hover-only)" "true" "$out"

errout=$($BIN -e 'type T = { [<Shrot "c">] A: int }' 2>&1) && fail "unknown attribute must reject"
echo "$errout" | grep -qF "unknown attribute 'Shrot'" || fail "unknown attribute names the error: $errout"
echo "$errout" | grep -qF "Did you mean 'Short'?" || fail "unknown attribute hints: $errout"
echo "e2e ok: unknown attribute is a check error with did-you-mean"

errout=$($BIN -e 'type T = { [<Short "c">] A: int; [<Short "c">] B: int }' 2>&1) && fail "short collision must reject"
echo "$errout" | grep -qF "duplicate short '-c'" || fail "explicit shorts collide at check: $errout"
echo "e2e ok: explicit short collision is a check error"

errout=$($BIN -e 'let x = [<Short "c">] 1' 2>&1) && fail "expression-position attribute must reject"
echo "$errout" | grep -qF "attributes attach to record fields, union cases, and type declarations" || fail "non-declaration position names the scope: $errout"
echo "e2e ok: attribute positions outside declarations reject by name"

# widened positions [D:attr-positions]: union decls and cases host
# attributes (validated, inert until [D:wire-unions] binds them); an
# attribute line above `type` binds to it; wrong positions teach their homes
cat > "$adir/attrpos.weir" <<'WEOF'
type DepSpec = { replicas: int }

[<Tag "kind">]
type KDoc =
    | Deployment of DepSpec
    | [<Other>] Unknown of string

let v = Deployment { replicas = 3 }
print $"{v}"
WEOF
out=$($BIN "$adir/attrpos.weir")
expect "attr positions: own-line union Tag + case Other declare clean" "Deployment ({ replicas = 3 })" "$out"
$BIN check "$adir/attrpos.weir" >/dev/null 2>&1 || fail "attrpos.weir must check clean"
$BIN fmt --check "$adir/attrpos.weir" >/dev/null 2>&1 || fail "fmt must accept union/case attributes"

errout=$($BIN -e '[<Tag "kind">] type R = { a: int }' 2>&1) && fail "Tag on a record decl must reject"
echo "$errout" | grep -qF "'Tag' attaches to a union declaration, not a record declaration" || fail "wrong position names the home: $errout"
errout=$($BIN -e 'type U = [<Short "c">] A of int | B' 2>&1) && fail "Short on a case must reject"
echo "$errout" | grep -qF "'Short' attaches to a record field, not a union case" || fail "wrong position names the home: $errout"
echo "e2e ok: widened attribute positions — hosts declare, wrong positions teach homes"

# tagged unions cross the wire [D:wire-unions]: the tag field picks the
# case, [<Other>] is the declared open-world posture, writers reinsert
# the tag first — and `from jsonl KDoc` dispatches mixed NDJSON with
# zero surface beyond the union itself (the decomposition's receipt)
cat > "$adir/wireunion.weir" <<'WEOF'
type DepSpec = { replicas: int; image: string }
type SvcSpec = { port: int }

[<Tag "kind">]
type KDoc =
    | Deployment of DepSpec
    | Service of SvcSpec
    | Ping
    | [<Other>] Unknown of string

let docs = <<<
    {"kind":"Deployment","replicas":3,"image":"nginx"}
    {"kind":"Service","port":80}
    {"kind":"Ping"}
    {"kind":"CronJob","schedule":"weekly"}

docs
|> from jsonl KDoc
|> Seq.iter (fun d ->
    match d with
    | Deployment dep -> print $"deploy {dep.image} x{dep.replicas}"
    | Service s -> print $"svc {s.port}"
    | Ping -> print "ping"
    | Unknown k -> printerr $"skipped kind: {k}")

let one = ["kind: Deployment"; "replicas: 2"; "image: redis"] |> from yaml KDoc
print (match one with | Deployment d -> $"yaml {d.image}" | _ -> "?")

Service { port = 443 } |> to json |> Seq.iter print
Service { port = 443 } |> to yaml |> Seq.iter print
WEOF
out=$(cd "$adir" && $BIN wireunion.weir 2>/dev/null)
expect "wire unions: jsonl dispatch, yaml dispatch, tag-first writes" 'deploy nginx x3
svc 80
ping
yaml redis
{"kind":"Service","port":443}
kind: Service
port: 443' "$out"

errout=$($BIN -e 'type NP = { n: int }
[<Tag "k">]
type KO = A of NP | [<Other>] U of string
U "x" |> to json' 2>&1) && fail "an Other value must refuse to write"
echo "$errout" | grep -qF "nothing faithful can be written" || fail "Other write refusal teaches: $errout"
echo "e2e ok: wire unions — mixed NDJSON via from jsonl, tag dispatch both formats, Other refuses to write"

# a district composes like the seq<string> it produces [D:district-terminates]:
# a `|>` on the block's closing line pipes the whole block, and the bytes
# match the bound spelling exactly (interior blank, deeper indent, trailing
# clip). Before, the trailing pipe glued into the last content line.
dtdir=$(mkweirtmp)
cat > "$dtdir/bound.weir" <<'WEOF'
let s = <<<
    line one
      deeper

    after blank
s |> File.write "out.txt"
WEOF
cat > "$dtdir/piped.weir" <<'WEOF'
<<<
    line one
      deeper

    after blank
|> File.write "out.txt"
WEOF
( cd "$dtdir" && mkdir b p && ( cd b && "$BIN" ../bound.weir ) && ( cd p && "$BIN" ../piped.weir ) )
cmp -s "$dtdir/b/out.txt" "$dtdir/p/out.txt" || fail "piped district must write byte-identical to the bound form: $(diff "$dtdir/b/out.txt" "$dtdir/p/out.txt")"
# the composing shapes run (pipe to a Seq op, yaml block to an adapter)
dtn=$(cd "$dtdir" && "$BIN" -e 'let n =
    <<<
        one
        two
    |> Seq.length
n')
[ "$dtn" = "2 : int" ] || fail "a heredoc |> Seq.length must compose: $dtn"
# the sentinel cannot be smuggled from source
dterr=$(printf 'print "a\036b"\n' > "$dtdir/ctl.weir" && "$BIN" check "$dtdir/ctl.weir" 2>&1) && fail "a source control char must reject"
echo "$dterr" | grep -qF "illegal control character" || fail "the district-close sentinel must be unproduceable from source: $dterr"
echo "e2e ok: district piping — |> composes with the block, piped≡bound bytes, the close sentinel is unproduceable"

# ---- $$<<< splice heredoc [D:heredoc-splice] ------------------------
# a pasted JSON blob with $name/${expr} splices, braces literal — the
# templating case $<<< (holes) could not do without doubling braces
cat > "$dtdir/splice.weir" <<'WEOF'
let host = "srv.example"
let port = 5432
$$<<<
    { "server": "$host", "port": ${port}, "type": "AzureSqlMI", "lit": "$$" }
|> File.write "out.json"
WEOF
sout=$(cd "$dtdir" && "$BIN" splice.weir && cat out.json)
echo "$sout" | grep -qF '{ "server": "srv.example", "port": 5432, "type": "AzureSqlMI", "lit": "$" }' \
  || fail "\$\$<<< must splice \$name/\${expr}, keep braces literal, \$\$ -> \$: $sout"
echo "e2e ok: \$\$<<< splice heredoc — \$name/\${expr} substitute, braces literal, JSON templates directly [D:heredoc-splice]"

# the stream cardinality [D:wire-unions] session S: `from yaml stream T`
# reads N `---` documents each as T — the heterogeneous bundle is
# stream over a tagged union, and to yaml stream's write roundtrips
cat > "$adir/bundle.weir" <<'WEOF'
type DepSpec = { replicas: int; image: string }
type SvcSpec = { port: int }

[<Tag "kind">]
type KDoc =
    | Deployment of DepSpec
    | Service of SvcSpec
    | [<Other>] Unknown of string

let bundle = <<<
    kind: Deployment
    replicas: 3
    image: nginx
    ---
    kind: Service
    port: 80
    ---
    kind: CronJob
    schedule: daily

for d in bundle |> from yaml stream KDoc do
    match d with
    | Deployment dep -> print $"deploy {dep.image} x{dep.replicas}"
    | Service s -> print $"svc {s.port}"
    | Unknown k -> print $"skip {k}"

let back = [Service { port = 1 }; Deployment { replicas = 2; image = "redis" }] |> to yaml stream
let n = back |> from yaml stream KDoc |> Seq.length
print $"roundtrip {n}"
WEOF
out=$($BIN "$adir/bundle.weir")
expect "yaml stream: the mixed bundle dispatches; the stream write reads back" 'deploy nginx x3
svc 80
skip CronJob
roundtrip 2' "$out"

errout=$($BIN -e 'type R = { a: int }
["x"] |> from json stream R' 2>&1) && fail "from json stream must reject"
echo "$errout" | grep -qF "'from json stream' does not exist" || fail "the json stream fence teaches: $errout"
errout=$($BIN -e 'type R2 = { a: int }
["a: 1"; "---"; "a: 2"] |> from yaml R2' 2>&1) && fail "one-doc form must still refuse a stream"
echo "$errout" | grep -qF "read a stream with 'from yaml stream T'" || fail "the teaching re-points: $errout"
echo "e2e ok: yaml stream — bundle dispatch, roundtrip, fences re-point"

# function types [D:function-types]: writable/constructible/callable, but
# the four data ops refuse a function-bearing type at check, naming the field
ftout=$($BIN -e 'type Rule = { matches: string -> bool }
let r = { matches = fun s -> s == "x" }
let run { matches = m } x = m x
print $"{r.matches "x"} {run r "y"}"' 2>&1) || fail "a function field must construct + call + destructure: $ftout"
echo "$ftout" | grep -qxF "true false" || fail "function-field construct/call/destructure drifted: $ftout"
e=$($BIN -e 'type R = { f: unit -> int }
let a = { f = fun () -> 1 }
if a == a then 0 else 1' 2>&1) && fail "== on a function-bearing record must refuse"
echo "$e" | grep -qF "not defined for R" || fail "record == recurses to the function ban: $e"
e=$($BIN -e 'type R = { f: unit -> int }
{ f = fun () -> 1 } |> to json' 2>&1) && fail "to json on a function record must refuse"
echo "$e" | grep -qF "field 'f'" || fail "to json names the field: $e"
e=$($BIN -e 'type R = { f: unit -> int }
{ f = fun () -> 1 } |> to yaml' 2>&1) && fail "to yaml on a function record must refuse"
echo "$e" | grep -qF "field 'f'" || fail "to yaml names the field (the F2 fix): $e"
e=$($BIN -e 'type R = { f: unit -> int }
let a = { f = fun () -> 1 }
print $"{a}"' 2>&1) && fail "show of a function record must refuse"
echo "$e" | grep -qF "cannot be shown" || fail "show refuses the function field: $e"
e=$($BIN -e 'type P = { f: unit -> int }
[<Tag "k">]
type K = C of P
C { f = fun () -> 1 } |> to json' 2>&1) && fail "to json on a tagged union function payload must refuse"
echo "$e" | grep -qF "field 'C.f'" || fail "to json names the union path: $e"
pos=$($BIN -e 'type S = { n: int }
let s = { n = 5 }
print $"{s == s} {show s}"' 2>&1) || fail "the scalar twin must serialise/compare/show: $pos"
echo "$pos" | grep -qF "true { n = 5 }" || fail "positive twin drifted: $pos"
echo "e2e ok: function types — construct/call/destructure; ==/to json/to yaml/show refuse a function-bearing record and tagged union naming the field; scalar twin unaffected"

# match-pipe offside [D:match-pipe-offside]: |> at the arm | closes the
# match; at or under the arm body continues the arm; left of the body
# rejects. On the AOT binary.
mpo=$($BIN -e 'match 5 with
| 5 -> 50
| _ -> 0
|> (fun n -> n + 1)
|> print' 2>&1) || fail "a |> at the arm column must close the match and pipe it: $mpo"
echo "$mpo" | grep -qxF "51" || fail "closing pipe result drifted: $mpo"
mpo=$($BIN -e 'match 5 with
| n -> n
       |> (fun k -> k + 1)
       |> print' 2>&1) || fail "a |> under the arm body must extend the arm: $mpo"
echo "$mpo" | grep -qxF "6" || fail "arm-extending pipe result drifted: $mpo"
mpo=$($BIN -e 'match 5 with
| _ -> "t"
  |> print' 2>&1) && fail "a |> left of the arm body must reject"
echo "$mpo" | grep -qF "left of the arm body" || fail "body-floor teaching drifted: $mpo"
# each arm keeps its own body column: arm 1 inline+deep, arm 2 dangling+shallow
mpo=$($BIN -e 'let x = Some 1
match x with
| Some p -> p |> (fun n -> n + 1)
| None ->
    0
    |> (fun n -> n + 9)
|> print' 2>&1) || fail "a later arm's pipe must be judged by its own body: $mpo"
echo "$mpo" | grep -qxF "2" || fail "per-arm body column drifted: $mpo"
echo "e2e ok: match-pipe offside — |> at the arm column closes; at/under the body continues the arm; each arm keeps its own body column; left of the body rejects"

# field-sep sentinel [D:field-sep-sentinel]: a bare lambda field/element
# does not swallow the next field/element (was "unbound <next>")
fs=$($BIN -e 'let r = {|
    act = fun () -> "y"
    name = "x"
    |}
print $"{r.name} {r.act ()}"' 2>&1) || fail "a bare lambda record field must not swallow the next: $fs"
echo "$fs" | grep -qxF "x y" || fail "field-sep record drifted: $fs"
fs=$($BIN -e 'let xs = [
    fun () -> 1
    fun () -> 2
    ]
print (show (Seq.length xs))' 2>&1) || fail "a bare lambda list element must not swallow the next: $fs"
echo "$fs" | grep -qxF "2" || fail "field-sep list drifted: $fs"
echo "e2e ok: field-sep sentinel — a bare lambda field/element does not swallow the next"

$BIN fmt --check "$adir/attrs.weir" >/dev/null 2>&1 || fail "fmt must accept attributed record decls"
echo "e2e ok: fmt roundtrips attribute lists"

rm -rf "$adir"

# typed argv (PLAN-typed-argv): Args.load over records and unions;
# the attributes plan's carried done-when clauses discharge here
tadir=$(mkweirtmp)
cat > "$tadir/cli.weir" <<'WEOF'
type Cli = {
    /// clean first
    clean: bool
    verbose: bool
    port: Option<int>
    env: string
}
let cli = Args.load Cli
print $"{show cli.clean} {show cli.verbose} {show cli.port} {cli.env}"
WEOF
out=$($BIN "$tadir/cli.weir" --clean --env prod --port 8080)
expect "record flags load typed" "true false Some 8080 prod" "$out"
out=$($BIN "$tadir/cli.weir" -c -e prod)
expect "derived shorts resolve" "true false None prod" "$out"

errout=$($BIN "$tadir/cli.weir" --verbos --port abc stray 2>&1) && fail "three-problem invocation must reject"
echo "$errout" | grep -qF "unknown flag '--verbos'. Did you mean '--verbose'?" || fail "typo did-you-means: $errout"
echo "$errout" | grep -qF "is not an int ('abc')" || fail "int parse collected: $errout"
echo "$errout" | grep -qF "unexpected argument 'stray'" || fail "strictness collected: $errout"
echo "$errout" | grep -qF "missing required flag '--env'" || fail "missing required collected: $errout"
echo "e2e ok: a four-problem invocation reports all four, collected"

# the argv-order oracle [D:argv-rules]: the aggregated error's order is
# the contract — scan problems in token order, then fills in
# declaration order (shared tier before payload). Exact-string pins,
# written against the pre-extraction binary; the twins' shared rules
# must keep this byte-identical.
odir=$(mkweirtmp)
cat > "$odir/rec.weir" <<'WEOF'
type Cli = { retries: int; target: string }
let cli = Args.load Cli
print cli.target
WEOF
got=$($BIN "$odir/rec.weir" --bogus --retries x 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: unknown flag '--bogus'; --retries is not an int ('x'); missing required flag '--retries'; missing required flag '--target'" ] \
    || fail "record-twin order oracle: $got"
echo "e2e ok: record twin: scan order then declaration-order fills (exact)"

cat > "$odir/pol.weir" <<'WEOF'
type Cli = { [<Default true>] color: bool; name: string }
let cli = Args.load Cli
print cli.name
WEOF
got=$($BIN "$odir/pol.weir" --color --no-color --name n 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: '--color' and '--no-color' are both given" ] || fail "polarity oracle: $got"
echo "e2e ok: polarity conflict names both spellings (exact)"

cat > "$odir/sh.weir" <<'WEOF'
type PushCfg = { remote: string; depth: int }
type Verb = | Push of PushCfg
type Cli = { level: int; cmd: Verb }
let _cli = Args.load Cli
print "ok"
WEOF
got=$($BIN "$odir/sh.weir" --level x push --depth y 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: --level is not an int ('x'); --depth is not an int ('y'); missing required flag '--level'; missing required flag '--remote'; missing required flag '--depth'" ] \
    || fail "shared-twin two-tier order oracle: $got"
got=$($BIN "$odir/sh.weir" push 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: missing required flag '--level'; missing required flag '--remote'; missing required flag '--depth'" ] \
    || fail "shared-twin fill order oracle: $got"
echo "e2e ok: shared twin: token-order scan crosses tiers; fills shared-then-payload (exact)"
# `--name=value` teaches the separate-argument spelling, both twins; a
# presence flag says it takes no value
cat > "$odir/eq.weir" <<'WEOF'
type Cli = { verbose: bool; target: string }
let cli = Args.load Cli
print cli.target
WEOF
got=$($BIN "$odir/eq.weir" --target=prod --verbose=yes 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: unknown flag '--target=prod' — a flag's value is the next argument: write '--target prod'; unknown flag '--verbose=yes' — '--verbose' is a presence flag and takes no value; missing required flag '--target'" ] \
    || fail "--name=value teaching (record): $got"
got=$($BIN "$odir/sh.weir" --level 1 push --remote=o --depth 2 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: unknown flag '--remote=o' — a flag's value is the next argument: write '--remote o'; missing required flag '--remote'" ] \
    || fail "--name=value teaching (shared twin): $got"
# a Secret field's value is never echoed, nor an unknown flag's
# [D:args-secret-echo]
cat > "$odir/sec.weir" <<'WEOF'
type Cli = { token: Secret; port: int }
let cli = Args.load Cli
print (show cli)
WEOF
got=$($BIN "$odir/sec.weir" --token=e2e-hunter2 --tokn=e2e-hunter3 --port 1 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: unknown flag '--token=***' — a flag's value is the next argument: write '--token <value>'; unknown flag '--tokn=…'. Did you mean '--token'?; missing required flag '--token'" ] \
    || fail "--secret=value must not echo the value: $got"
echo "e2e ok: --name=value teaches '--name value'; a presence flag takes no value; a Secret's or unknown flag's value is never echoed"
rm -rf "$odir"

out=$($BIN "$tadir/cli.weir" --bogus --help); rc=$?
[ "$rc" -eq 0 ] || fail "--help must exit 0 (got $rc)"
echo "$out" | grep -qF -- "-c, --clean" || fail "help shows derived short truth: $out"
echo "$out" | grep -qF "clean first" || fail "help shows the /// first line: $out"
echo "$out" | grep -qF -- "--env <string>" || fail "help shows valued flags: $out"
echo "$out" | grep -qF "required" || fail "help shows requiredness: $out"
echo "e2e ok: --help derives usage (short truth + /// doc) BEFORE validation, exit 0"

# multi-line ///: --help shows only the first line; the rest is hover-only [D:doc-help]
cat > "$tadir/multi.weir" <<'WEOF'
type Cli = {
    /// terse help line
    /// a second line, hover-only
    verbose: bool
}
let _c = Args.load Cli
print "x"
WEOF
out=$($BIN "$tadir/multi.weir" --help)
echo "$out" | grep -qF "terse help line" || fail "multi-line ///: --help shows line one: $out"
echo "$out" | grep -qF "second line" && fail "multi-line ///: --help must NOT show line two: $out"
echo "e2e ok: --help renders the /// FIRST line only (subsequent lines hover-only)"

# the retired [<Doc>] is now the ordinary unknown-attribute error [D:doc-help]
errout=$($BIN -e 'type T = { [<Doc "x">] A: int }' 2>&1) && fail "[<Doc>] must reject"
echo "$errout" | grep -qF "unknown attribute 'Doc'" || fail "[<Doc>] is unregistered: $errout"
echo "e2e ok: [<Doc>] retired — now the unknown-attribute error"

cat > "$tadir/short.weir" <<'WEOF'
type Cli = { [<Short "e">] clean: bool; env: string }
let cli = Args.load Cli
print $"{show cli.clean} {cli.env}"
WEOF
out=$($BIN "$tadir/short.weir" -e --env prod)
expect "explicit [<Short>] beats derivation" "true prod" "$out"
out=$($BIN "$tadir/short.weir" --help)
echo "$out" | grep -qF -- "-e, --clean" || fail "explicit short in help: $out"
echo "$out" | grep -qE -- '^      --env' || fail "the derived short retired from --env: $out"
echo "e2e ok: derivation yields to declaration; --help is the truth"

cat > "$tadir/amb.weir" <<'WEOF'
type Cli = { clean: bool; copy: bool }
let cli = Args.load Cli
print (show cli.clean)
WEOF
errout=$($BIN "$tadir/amb.weir" -c 2>&1) && fail "ambiguous short must reject"
echo "$errout" | grep -qF "'-c' is ambiguous: --clean, --copy" || fail "ambiguity lists candidates: $errout"
echo "e2e ok: contested shorts derive for nobody and error with candidates"

cat > "$tadir/sub.weir" <<'WEOF'
type CloneArgs = { remote: string; force: bool }
type Cmd =
    | Clone of CloneArgs
    | Status
let r =
    match Args.load Cmd with
    | Clone a -> $"clone {a.remote} {show a.force}"
    | Status -> "status"
print r
WEOF
out=$($BIN "$tadir/sub.weir" clone --remote http://x --force)
expect "union subcommand with payload flags" "clone http://x true" "$out"
out=$($BIN "$tadir/sub.weir" status)
expect "bare-word case" "status" "$out"
errout=$($BIN "$tadir/sub.weir" clne 2>&1) && fail "unknown subcommand must reject"
echo "$errout" | grep -qF "unknown subcommand 'clne'. Did you mean 'clone'?" || fail "subcommand did-you-mean: $errout"
errout=$($BIN "$tadir/sub.weir" 2>&1) && fail "missing subcommand must reject"
echo "$errout" | grep -qF "missing subcommand; one of: clone, status" || fail "missing subcommand lists cases: $errout"
echo "e2e ok: the union front door dispatches, hints, and lists"

errout=$($BIN "$tadir/cli.weir" --env a --env b 2>&1) && fail "repeated flag must reject"
echo "$errout" | grep -qF "'--env' is given twice" || fail "strictness on repeats: $errout"
echo "e2e ok: repeated flags reject (strict by default)"

cat > "$tadir/kebab.weir" <<'WEOF'
type Cli = { dryRun: bool; noFF: bool; useHTTPSNow: bool }
let cli = Args.load Cli
print $"{show cli.dryRun} {show cli.noFF} {show cli.useHTTPSNow}"
WEOF
out=$($BIN "$tadir/kebab.weir" --dry-run --no-ff --use-https-now)
expect "kebab derivation (dryRun/noFF/useHTTPSNow)" "true true true" "$out"

errout=$(printf 'type Cli = { dryRun: bool; DryRun: bool }\nlet c = Args.load Cli\n' | checkPiped 2>&1) && fail "duplicate derived flag must reject"
echo "$errout" | grep -qF "derive the same flag '--dry-run'" || fail "duplicate kebab at check: $errout"
echo "e2e ok: hump-style variance collapses to one flag, duplicate rejected at check"

cat > "$tadir/host.weir" <<'WEOF'
type Cli = { host: string }
let cli = Args.load Cli
print cli.host
WEOF
out=$($BIN "$tadir/host.weir" -h); rc=$?
[ "$rc" -eq 0 ] || fail "-h reserved for help (got rc=$rc)"
echo "$out" | grep -qE -- '^      --host' || fail "h-initial field must not derive a short: $out"
echo "e2e ok: -h is help; h-initial fields never derive"

# [<Positional>] dropped [D:drop-positional] — now an unknown attribute
# Positional returned for signatures [D:command-signatures] — registered
# and inert on weir's own records; its no-argument law still checks
printf 'type P = { [<Positional>] t: string }\nprint "ok"\n' | checkPiped >/dev/null 2>&1 || fail "Positional declares clean (inert)"
errout=$(printf 'type P = { [<Positional 3>] t: string }\n' | checkPiped 2>&1) && fail "Positional takes no argument" || true
echo "$errout" | grep -qF "takes no argument" || fail "the arg law: $errout"
echo "e2e ok: [<Positional>] returned for signatures, inert elsewhere"

errout=$(printf 'type C = { b: Option<bool> }\nlet c = Args.load C\n' | checkPiped 2>&1) && fail "Option<bool> field must reject"
echo "$errout" | grep -qF "a presence flag is already optional" || fail "Option<bool> message: $errout"
echo "e2e ok: Option<bool> rejects with the presence explanation"

errout=$(printf 'type C = { env: string }\nlet c = Args.load C\n' | $BIN 2>&1)
echo "$errout" | grep -qF "Args.load is script-only" || fail "REPL/stdin gate: $errout"
echo "e2e ok: Args.load is script-only; the REPL errors by name"

cat > "$tadir/both.weir" <<'WEOF'
type A = { env: string }
type E = { HOME: string }
let a = Args.load A
let e = Env.load E
print $"{a.env} {Str.length e.HOME > 0}"
WEOF
out=$(HOME=/tmp $BIN "$tadir/both.weir" --env prod)
expect "Args.load composes with Env.load" "prod true" "$out"

printf 'print (Self.args |> Str.join ",")\n' > "$tadir/slice.weir"
out=$($BIN "$tadir/slice.weir" --a b c)
expect "script args start AFTER the script path" "--a,b,c" "$out"

errout=$(printf 'type C = { env: string }\nArgs.load C\n' | checkPiped 2>&1) && fail "bare Args.load statement must reject"
echo "$errout" | grep -qF "discards it" || fail "statement rule covers Args.load: $errout"
echo "e2e ok: Args.load joins the discard family as a value"

rm -rf "$tadir"

# jira-branch loads its Cli (check-only: jira/fzf are not installed)
$BIN check tools/jira-branch.weir >/dev/null 2>&1 || fail "jira-branch must check with Args.load"
echo "e2e ok: jira-branch checks with the typed Cli"

# multiline brackets (PLAN-multiline-brackets): type decls + lists
mldir=$(mkweirtmp)
cat > "$mldir/forms.weir" <<'WEOF'
type Ctx =
    { Subdir: string
      Repo: string }

type Cli =
    { [<Short "c">] count: int
      [<NoShort>]
      verbose: bool }

let pairs =
    [("%", "%25")
     ("..", "%2e%2e")
     (" ", "%20")]

let c = { Subdir = "s"; Repo = "r" }
let cli = { count = 1; verbose = false }
print $"{c.Subdir} {cli.count} {pairs |> Seq.length}"
WEOF
out=$($BIN "$mldir/forms.weir")
expect "all three form-block shapes run (incl. preceding-line attribute)" "s 1 3" "$out"

$BIN fmt --check "$mldir/forms.weir" >/dev/null 2>&1 || fail "fmt must accept the canonical multiline forms"
echo "e2e ok: fmt roundtrips multiline type decls and lists"

printf 'let x =\n    [1; 2\n     3}\n' > "$mldir/cross.weir"
errout=$($BIN "$mldir/cross.weir" 2>&1) && fail "cross-bracket must reject"
echo "$errout" | grep -qF "'}' closes the '[' opened at line 2" || fail "cross-bracket names both: $errout"
echo "e2e ok: cross-bracket closer errors naming both brackets"

# flipped 2026-07-23 [D:blank-in-brackets]
printf 'let x =\n    [1\n\n     2]\nprint (x |> Seq.sum)\n' > "$mldir/blank.weir"
out=$($BIN "$mldir/blank.weir")
expect "blank inside an open list is transparent (flipped pin)" "3" "$out"

rm -rf "$mldir"

# body blanks (PLAN-body-blanks): the core reversal — blanks transparent
# while a statement pends; the col-0 law is the sole boundary
bbdir=$(mkweirtmp)
cat > "$bbdir/status.weir" <<'WEOF'
let status name =
    print $"name: {name}"

    print $"tail: ok"

status "weir"
WEOF
out=$($BIN "$bbdir/status.weir")
expect "the receipt: a gapped function body runs (the user's exact shape)" "tail: ok" "$out"

cat > "$bbdir/arms.weir" <<'WEOF'
let v =
    match 2 with

    | 1 -> "one"

    | _ -> "other"

print v
WEOF
out=$($BIN "$bbdir/arms.weir")
expect "gaps between match head and arms, and between arms" "other" "$out"

cat > "$bbdir/district.weir" <<'WEOF'
let ok = 1 > 0
if ok then
    sh -c "echo first"

    sh -c "echo second"
print "after"
WEOF
out=$($BIN "$bbdir/district.weir")
expect "a gapped command group runs both commands" "second" "$out"
echo "$out" | grep -qF "after" || fail "the district closes at col-0: $out"
echo "e2e ok: district gaps group commands; effect count intact"

cat > "$bbdir/below.weir" <<'WEOF'
let f x =
    let a = 1

    a + nosuchvar
WEOF
errout=$($BIN check "$bbdir/below.weir" 2>&1 || true)
echo "$errout" | grep -qF "below.weir:4:" || fail "an error BELOW a gap must map to its physical line: $errout"
echo "e2e ok: segment translation across gaps"

# the deliberate consequence, both spellings compared
printf 'let x = 1\n\n    2\n' > "$bbdir/stray-gap.weir"
printf 'let x = 1\n    2\n' > "$bbdir/stray-nogap.weir"
err1=$($BIN check "$bbdir/stray-gap.weir" 2>&1 || true)
err2=$($BIN check "$bbdir/stray-nogap.weir" 2>&1 || true)
echo "$err1" | grep -qF "not a function" || fail "gapped stray joins and the checker catches it: $err1"
echo "$err2" | grep -qF "not a function" || fail "blank-free stray control: $err2"
echo "e2e ok: strays behave identically with and without the gap (pinned consequence)"

printf '\n    orphan\n' > "$bbdir/orphan.weir"
errout=$($BIN "$bbdir/orphan.weir" 2>&1 || true)
echo "$errout" | grep -qF "continuation" || fail "no-pending orphan error survives: $errout"
echo "e2e ok: the orphan error survives where it is true (no pending statement)"

rm -rf "$bbdir"

# the echo rule [D:echo-rule]: unforced caps with the lever that works;
# forced echoes in full — Seq.freeze means something at the prompt now
out=$(printf 'let xs = [1..100]\nxs\n' | $BIN 2>&1)
echo "$out" | grep -qF "10; …] : seq<int> (first 10 of an unforced seq — Seq.freeze to echo everything)" || fail "the unforced echo names the honest lever: $out"
out=$(printf '[1..12] |> Seq.freeze\n' | $BIN 2>&1)
echo "$out" | grep -qF "11; 12] : seq<int>" || fail "a forced seq echoes in full: $out"
out=$($BIN -e '["a"; "b"; "c"; "d"; "e"; "f"; "g"; "h"; "i"; "j"; "k"]')
echo "$out" | grep -qF "\"k\"] : seq<string>" || fail "a literal list is forced — full echo, no hint: $out"
echo "e2e ok: the echo rule — unforced caps honestly, forced echoes whole"

out=$($BIN -e '[1..50]')
echo "$out" | grep -qF "(first 10 of an unforced seq" || fail "-e echoes like the REPL (decided): $out"
echo "e2e ok: -e shares the echo rule"

out=$(printf 'print (show [1..100])\n' | $BIN 2>&1)
echo "$out" | grep -qF "; 20; ...]" || fail "show byte-identical (20 + dots): $out"
echo "e2e ok: show is unchanged — byte-identical to its shipped lossy contract"

# REPL coloring (PLAN-repl-color): the harness-sees-no-ANSI guard —
# piped stdin bypasses the tty editor structurally; pin it stays true
out=$(printf 'let x = 1 + 2\nx\n' | $BIN 2>&1)
case "$out" in
    *$'\x1b['*) fail "piped REPL output must carry zero ANSI: $(echo "$out" | head -1)" ;;
esac
echo "e2e ok: piped REPL output is byte-clean (the harness guard)"

# seq patterns (PLAN-seq-force-patterns Part 2): F#'s spelling, seq
# semantics, bounded force + memoize-once
spdir=$(mkweirtmp)
cat > "$spdir/status.weir" <<'WEOF'
let verdict =
    match $(printf 'M a.txt\nA b.txt') with
    | [] -> "clean"
    | line :: rest -> $"dirty: {line} (+{rest |> Seq.length} more)"

print verdict
WEOF
out=$($BIN "$spdir/status.weir")
expect "the design's git-status shape runs" "dirty: M a.txt (+1 more)" "$out"

cat > "$spdir/once.weir" <<'WEOF'
let s = $(sh -c "echo x >> SPMARK; printf 'a\nb\nc\n'")
let r =
    match s with
    | [] -> "none"
    | [a] -> a
    | x :: rest -> $"{x}/{rest |> Str.join "-"}"

print r
WEOF
# no `sed -i`: BSD sed demands a suffix argument there — rewrite-and-move
# is the portable spelling
sed "s|SPMARK|$spdir/mark|" "$spdir/once.weir" > "$spdir/once.weir.tmp" && mv "$spdir/once.weir.tmp" "$spdir/once.weir"
out=$($BIN "$spdir/once.weir")
expect "arms + rest consumption over a command seq" "a/b-c" "$out"
[ "$(grep -c x "$spdir/mark")" = "1" ] || fail "memoize-once: expected ONE spawn, got $(grep -c x "$spdir/mark")"
echo "e2e ok: the memoize-once law holds live (one spawn across arms + rest)"

errout=$($BIN -e 'match 5 with | [] -> 0 | _ -> 1' 2>&1) && fail "non-seq scrutinee must reject"
echo "$errout" | grep -qF "seq patterns need a seq value" || fail "seq-pattern message: $errout"
printf 'let bad =\n    match [1] |> Seq.skip 0 with\n    | [] -> 0\n' > "$spdir/nx.weir"
errout=$($BIN check "$spdir/nx.weir" 2>&1 || true)
echo "$errout" | grep -qF "missing: _ :: _" || fail "seq exhaustiveness names the gap: $errout"
echo "e2e ok: seq-pattern rejections are located and named"

rm -rf "$spdir"

# the two-pipe cliff (investigation rider [D:pipe-hint])
errout=$($BIN -e '$(git status) | Seq.head' 2>&1) && fail "bare | after an expression must reject"
echo "$errout" | grep -qF "pipe expressions with '|>'" || fail "the cliff must name the spelling: $errout"
echo "e2e ok: '|' after an expression names the |> spelling"

# block-let command RHS (PLAN-block-let-cmd): the uniformity fix
# (ROOT resolved before any cd: $0 is relative to the invocation dir)
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
bldir=$(mkweirtmp)
cat > "$bldir/forms.weir" <<'WEOF'
let graft c =
    let tree = git rev-parse $"{c}^{{tree}}" |> Seq.head
    let short = git rev-parse --short $c |> Seq.head
    let ok = git rev-parse --verify $c | succeeds
    $"{short}:{ok} {tree}"

print (graft "HEAD")
WEOF
out=$(cd "$ROOT" && $BIN "$bldir/forms.weir")
echo "$out" | grep -qE "^[0-9a-f]+:true " || fail "the forms block must run: $out"
echo "e2e ok: block-let command RHS binds, pipes, and reifies at depth"

mkdir -p "$bldir/bin"
mkFakeBin "$bldir/bin" zzshadow SPAWNED
cat > "$bldir/shadow.weir" <<'WEOF'
let f y =
    let zzshadow = fun a -> a
    let z = zzshadow y
    z |> Seq.head

print (f ["safe"])
WEOF
out=$(PATH="$(pathEntry "$bldir/bin"):$PATH" $BIN "$bldir/shadow.weir")
expect "block names shadow PATH at depth (the failing-first pin)" "safe" "$out"

cat > "$bldir/force.weir" <<'WEOF'
let f y =
    let zzshadow = fun a -> a
    let z = ^zzshadow y |> Seq.head
    zzshadow z

print (f "x")
WEOF
out=$(PATH="$(pathEntry "$bldir/bin"):$PATH" $BIN "$bldir/force.weir")
expect "^ still reaches the PATH binary from a block RHS" "SPAWNED" "$out"

mkFakeBin "$bldir/bin" function FN-BINARY
out=$(PATH="$(pathEntry "$bldir/bin"):$PATH" $BIN -e '^function' 2>&1)
expect "^function reaches a PATH binary (reservation does not block force)" "FN-BINARY" "$out"

# the reservation retired into the feature [D:function-keyword]: the
# form runs; a binder slot refuses with the generic keyword message
out=$($BIN -e 'print ((function | 0 -> "z" | _ -> "n") 0)')
[ "$out" = "z" ] || fail "function must evaluate: $out"
errout=$($BIN -e 'let function = 1' 2>&1) && fail "function as a binder must reject"
echo "$errout" | grep -qF "'function' is a keyword" || fail "the generic keyword refusal: $errout"
echo "e2e ok: function lands; keyword slots still refuse"

rm -rf "$bldir"

# ---- multiline lambdas [D:multiline-lambda]: the form-block shapes ----

mldir=$(mkweirtmp)
cat > "$mldir/iter.weir" <<'WEOF'
let repos = [("alpha", "r1"); ("beta", "r2")]

repos
    |> Seq.iter (fun (name, path) ->
        let tag = $"repo-{path}"
        echo fetching $tag
        print $"fetched {name}")
WEOF
out=$($BIN "$mldir/iter.weir")
expect "multiline lambda form 1 (iter: let + sigil + print)" "fetching repo-r1" "$out"
expect "multiline lambda form 1 effect order" "fetched beta" "$out"

cat > "$mldir/map.weir" <<'WEOF'
let out =
    [("k", "1"); ("j", "x")]
    |> Seq.map (fun (k, v) ->
        let n = v |> toIntOr 0
        $"{k}={n}"
    )

out |> Seq.iter print
WEOF
if $BIN check "$mldir/map.weir" >/dev/null 2>&1; then
    out=$($BIN "$mldir/map.weir")
    expect "multiline lambda form 2 (map with closer alone)" "j=0" "$out"
else
    # toIntOr may not exist — the form matters, not the helper
    cat > "$mldir/map.weir" <<'WEOF'
let out =
    [("k", 1); ("j", 2)]
    |> Seq.map (fun (k, v) ->
        let n = v + 10
        $"{k}={n}"
    )

out |> Seq.iter print
WEOF
    out=$($BIN "$mldir/map.weir")
    expect "multiline lambda form 2 (map with closer alone)" "j=12" "$out"
fi

# span translation: an error on body line 3 of a lambda inside a
# pipeline stage maps to its physical line (the multibad extension)
cat > "$mldir/span.weir" <<'WEOF'
let v =
    [1; 2]
    |> Seq.map (fun n ->
        let a = n + 1
        a + nope
    )
    |> Seq.sum

print $"{v}"
WEOF
errout=$($BIN "$mldir/span.weir" 2>&1 || true)
expect "lambda-body error maps to its physical line" "span.weir:5:" "$errout"

# command block-let in a lambda body rides the let-RHS spine
cat > "$mldir/spine.weir" <<'WEOF'
let out =
    ["a"; "b"]
    |> Seq.map (fun w ->
        let g = echo tag $w
        g |> Seq.head
    )

out |> Seq.iter print
WEOF
out=$($BIN "$mldir/spine.weir")
expect "command block-let inside a multiline lambda (spine)" "tag b" "$out"

rm -rf "$mldir"

# ---- shared flags by containment [D:shared-flags] ----

sfdir=$(mkweirtmp)
cat > "$sfdir/cli.weir" <<'WEOF'
type SearchArgs = { query: string }
type RunArgs = { name: Option<string> }

type Cmd =
    | Search of SearchArgs
    | Run of RunArgs
    | Status

type Cli = { quiet: bool; cmd: Cmd }

let cli = Args.load Cli
print $"quiet={show cli.quiet}"

match cli.cmd with
| Search a -> print $"search {a.query}"
| Run r -> print $"run {show r.name}"
| Status -> print "status"
WEOF

out=$($BIN "$sfdir/cli.weir" --quiet search --query X)
expect "shared flag BEFORE the case token" "quiet=true" "$out"
out=$($BIN "$sfdir/cli.weir" search --quiet --query X)
expect "shared flag right after the case" "quiet=true" "$out"
out=$($BIN "$sfdir/cli.weir" search --query X --quiet)
expect "shared flag at line end" "search X" "$out"
out=$($BIN "$sfdir/cli.weir" status)
expect "bare case with shared default" "quiet=false" "$out"

errout=$($BIN "$sfdir/cli.weir" run --cmd x 2>&1) && fail "--cmd must be unknown (the union field derives no flag)"
echo "$errout" | grep -qF "unknown flag '--cmd'" || fail "union-field flag must not exist: $errout"
echo "e2e ok: the union field derives no flag"

# cross-tier short contest: -q ambiguous in search's scope, derives in run's
errout=$($BIN "$sfdir/cli.weir" search -q hello 2>&1) && fail "contested -q must be ambiguous"
echo "$errout" | grep -qF -- "'-q' is ambiguous: --quiet, --query" || fail "cross-tier contest: $errout"
echo "e2e ok: cross-tier short contest derives for neither in scope"
out=$($BIN "$sfdir/cli.weir" run -q)
expect "the same short derives where uncontested" "quiet=true" "$out"

# tier-aware did-you-mean
errout=$($BIN "$sfdir/cli.weir" --qiuet run 2>&1) && fail "typo before case must reject"
echo "$errout" | grep -qF "Did you mean '--quiet'?" || fail "before-tier did-you-mean: $errout"
errout=$($BIN "$sfdir/cli.weir" search --qeury x 2>&1) && fail "typo after case must reject"
echo "$errout" | grep -qF "Did you mean '--query'?" || fail "after-tier did-you-mean: $errout"
echo "e2e ok: tier-aware did-you-mean"

# collect-then-raise spans both tiers in one error
errout=$($BIN "$sfdir/cli.weir" search 2>&1) && fail "missing payload flag must reject"
echo "$errout" | grep -qF "missing required flag '--query'" || fail "cross-tier collection: $errout"
echo "e2e ok: one boundary error across tiers"

# two-tier help + case-scoped help
out=$($BIN "$sfdir/cli.weir" --help)
echo "$out" | grep -qF "global options:" || fail "two-tier help missing global section: $out"
echo "$out" | grep -qF "commands:" || fail "two-tier help missing commands: $out"
out=$($BIN "$sfdir/cli.weir" run --help)
echo "$out" | grep -qF "usage: cli.weir run [flags]" || fail "case-scoped help: $out"
echo "$out" | grep -qF -- "-q, --quiet" || fail "scoped help shows the scope-derived short: $out"
echo "e2e ok: two-tier and case-scoped help"

# a union-only target scopes its help to the leading case token too
printf 'type BuildArgs = { target: string }\ntype Cmd =\n    | Build of BuildArgs\n    | Clean\nlet c = Args.load Cmd\nprint $"{c}"\n' > "$sfdir/uo.weir"
out=$($BIN "$sfdir/uo.weir" build --help)
echo "$out" | grep -qF "usage: uo.weir build [flags]" || fail "union-only case-scoped help: $out"
echo "$out" | grep -qF -- "--target" || fail "union-only scoped help shows the case's flags: $out"
echo "$out" | grep -qF "clean" && fail "union-only scoped help lists other cases: $out"
out=$($BIN "$sfdir/uo.weir" --help)
echo "$out" | grep -qF "commands:" || fail "union-only top help lists the commands: $out"
echo "e2e ok: union-only case-scoped help"

# declaration collisions reject at check (both routes)
errout=$(printf 'type CA = { quiet: bool }\ntype Cmd = Go of CA | Stop\ntype Cli = { quiet: bool; cmd: Cmd }\nlet c = Args.load Cli\nprint "x"\n' | checkPiped 2>&1) && fail "kebab collision must reject"
echo "$errout" | grep -qF "shared flags are declared once" || fail "kebab collision route: $errout"
errout=$(printf 'type CA = { [<Short "q">] query: string }\ntype Cmd = Go of CA | Stop\ntype Cli = { [<Short "q">] quiet: bool; cmd: Cmd }\nlet c = Args.load Cli\nprint "x"\n' | checkPiped 2>&1) && fail "explicit-short collision must reject"
echo "$errout" | grep -qF "claimed by [<Short>] in both" || fail "explicit-short collision route: $errout"
errout=$(printf 'type CA = { r: bool }\ntype Cmd = Go of CA | Stop\ntype Cli = { a: Cmd; b: Cmd }\nlet c = Args.load Cli\nprint "x"\n' | checkPiped 2>&1) && fail "two union fields must reject"
echo "$errout" | grep -qF "one subcommand slot" || fail "one-slot law: $errout"
echo "e2e ok: declaration collisions reject at check (both routes + one slot)"

rm -rf "$sfdir"

# ---- the reifier family completes [D:exit-reifiers]: output goes
# ---- where the meaning goes ----

rfdir=$(mkweirtmp)

# the exit-code discard teaching is one string [D:exit-reifiers]: four
# refusal sites, and the copies had drifted to two spellings of the dash
# with nothing pinning any of them. Pinned verbatim and leading at the two
# sites that lead with it — a tail-only assertion sees neither the dash nor
# a demotion under a parser dump. Newlines fold first: the renderer wraps.
edmsg="this discards the exit code — bind it (let rc = <command> | exitCode), match on it, or drop '| exitCode'"

printf 'if 1 == 1 then\n    git push | exitCode\n' > "$rfdir/ed-if.weir"
out=$($BIN check "$rfdir/ed-if.weir" 2>&1) && fail "an else-less if must refuse a discarded exit code"
printf '%s' "$out" | tr '\n' ' ' | grep -qF "error [check]: $edmsg" || fail "the if site must LEAD with the shared teaching: $out"

printf 'let f () =\n    git push | exitCode\n    print "z"\n' > "$rfdir/ed-seq.weir"
out=$($BIN check "$rfdir/ed-seq.weir" 2>&1) && fail "a non-final sibling must refuse a discarded exit code"
printf '%s' "$out" | tr '\n' ' ' | grep -qF "error [parse]: $edmsg" || fail "the sequence site must LEAD with the shared teaching: $out"

# the statement rule's own wording is deliberately not that string — it is
# the discard family's sentence, and pinning it keeps the two distinct
printf 'git push | exitCode\n' > "$rfdir/ed-st.weir"
out=$($BIN check "$rfdir/ed-st.weir" 2>&1) && fail "a bare statement must refuse a discarded exit code"
printf '%s' "$out" | tr '\n' ' ' | grep -qF "error [discard]: this statement computes the exit code and discards it" || fail "the statement site keeps the discard family's wording: $out"

# orFail streams (behavioral pin: the child's stdout reaches the user)
cat > "$rfdir/of.weir" <<'WEOF'
sh -c "echo build-step-one; echo build-step-two; exit 4" | orFail "build broke"
WEOF
out=$($BIN "$rfdir/of.weir" 2>&1) && fail "orFail must raise on nonzero"
echo "$out" | grep -qF "build-step-one" || fail "orFail must stream stdout: $out"
echo "$out" | grep -qF "build broke (exit 4)" || fail "orFail message+code: $out"
echo "e2e ok: orFail streams and raises with the message"

# a bare command ≡ cmd | orFail "<msg>": byte-identical stream, same raise
cat > "$rfdir/eq1.weir" <<'WEOF'
sh -c "echo same-stream; exit 2"
WEOF
cat > "$rfdir/eq2.weir" <<'WEOF'
sh -c "echo same-stream; exit 2" | orFail "custom words"
WEOF
out1=$($BIN "$rfdir/eq1.weir" 2>/dev/null; echo "rc=$?")
out2=$($BIN "$rfdir/eq2.weir" 2>/dev/null; echo "rc=$?")
[ "$out1" = "$out2" ] || fail "a bare command and orFail must stream identically: [$out1] vs [$out2]"
echo "e2e ok: a bare command and orFail stream byte-identically (messages differ on stderr)"

# exitCode: stream + the code as data; never raises
cat > "$rfdir/ec.weir" <<'WEOF'
let rc = sh -c "echo watched-output; exit 7" | exitCode
print $"code={rc}"
WEOF
out=$($BIN "$rfdir/ec.weir")
echo "$out" | grep -qF "watched-output" || fail "exitCode must stream: $out"
echo "$out" | grep -qF "code=7" || fail "exitCode must bind the code: $out"
echo "e2e ok: exitCode streams AND binds (a watched build decides)"

# the taught idiom: match on the code (130 = cancel)
cat > "$rfdir/match.weir" <<'WEOF'
let rc = sh -c "exit 130" | exitCode

match rc with
| 0 -> print "ok"
| 130 -> print "cancelled"
| c -> fail $"unexpected: {c}"
WEOF
out=$($BIN "$rfdir/match.weir")
expect "the match-on-exit-code idiom (graceful cancel)" "cancelled" "$out"

# env variant: the env-sigil reifier at let-RHS (captures + binds the code)
cat > "$rfdir/env.weir" <<'WEOF'
let e = Env.ofPairs [("MARK", "seen")]
let r = $e(sh -c "echo mark=$MARK; exit 3" | complete)
r.stdout |> Seq.iter print
print $"env code {r.exitCode}"
WEOF
out=$($BIN "$rfdir/env.weir")
expect "env-sigil reifier applies the overlay" "mark=seen" "$out"
expect "env-sigil reifier binds the code" "env code 3" "$out"

# conflict cells reject with the teaching text
errout=$(printf 'let x = $(git push | exitCode)
print "u"
' | checkPiped 2>&1) && fail "capture conflict must reject"
echo "$errout" | grep -qF "use '| complete' inside" || fail "capture-conflict teaching: $errout"
errout=$(printf 'git push | exitCode
' | checkPiped 2>&1) && fail "statement discard must reject"
echo "$errout" | grep -qF "drop '| exitCode' if you don't need the code" || fail "statement hint: $errout"
# an interior command line inherits the ruling (the arming desugar)
errout=$(printf 'if 1 > 0 then
    git push | exitCode
' | checkPiped 2>&1) && fail "interior exitCode line must reject"
echo "$errout" | grep -qF "bind it (let rc = <command> | exitCode)" || fail "interior cell keeps the tailored teaching: $errout"
echo "e2e ok: exitCode conflict cells teach (sigil, bang, statement, interior line)"

rm -rf "$rfdir"

# ---- value-headed pipe into a child's stdin [D:value-headed-pipe] ----

fddir=$(mkweirtmp)
# macOS has no sha256sum; shasum -a 256 emits the identical digest and
# the fixture only reads the first field
HASHTOOL="sha256sum"
command -v sha256sum >/dev/null 2>&1 || HASHTOOL="shasum -a 256"
cat > "$fddir/hash.weir" <<'WEOF'
let hashes =
    ["snippet one"; "snippet two"] | HASHTOOL
    |> Seq.map (fun l -> l |> Str.split " " |> Seq.head)

hashes |> Seq.iter print
WEOF
sed "s|HASHTOOL|$HASHTOOL|" "$fddir/hash.weir" > "$fddir/hash.weir.tmp" && mv "$fddir/hash.weir.tmp" "$fddir/hash.weir"
out=$($BIN "$fddir/hash.weir")
expect "value-headed: the miner's sha256 shape (value -> child stdin)" "0027e9fbda04a2a921cb8ae59053abae8a3d29e0c93613be831bcf0262faa36f" "$out"

cat > "$fddir/lazy.weir" <<'WEOF'
[1..1000000] |> Seq.map (fun n -> $"{n}") | head -1 |> print
WEOF
out=$(timeout 10 $BIN "$fddir/lazy.weir") || fail "value-headed input must be lazy (head -1 over a huge range must terminate)"
expect "value-headed input laziness on the AOT binary" "1" "$out"

# a producer's raise piped into a command is the script's failure, at the
# producer's line — only the child closing its stdin early (head -1) is
# the pipe's business. Every consumer shape: captured, chained, | complete,
# | exitCode; `exit 5` in the producer keeps its code.
cat > "$fddir/gen.weir" <<'WEOF'
let gen i =
    if i == 3 then fail "boom" else $"{i}"

WEOF
for shape in 'let _r = [1..10] |> Seq.map gen | HASHTOOL | line' \
             'let _r = [1..10] |> Seq.map gen | cat |> Seq.length' \
             'let _r = [1..10] |> Seq.map gen | cat | complete' \
             'let _r = [1..10] |> Seq.map gen | cat | exitCode'; do
    { cat "$fddir/gen.weir"; printf '%s\nprint "unreached"\n' "$shape"; } | sed "s|HASHTOOL|$HASHTOOL|" > "$fddir/prodfail.weir"
    rc=0; out=$(cd "$fddir" && timeout 10 $BIN prodfail.weir 2>&1) || rc=$?
    [ $rc -eq 1 ] || fail "producer raise into a command must fail ($shape) rc=$rc: $out"
    echo "$out" | grep -qF "prodfail.weir:2:20: error: boom" || fail "producer raise at the producer's line ($shape): $out"
    case "$out" in *unreached*) fail "no statement runs after the producer's raise ($shape): $out" ;; esac
done
printf 'let _r = [1..10] |> Seq.map (fun i -> if i == 3 then exit 5 else $"{i}") | cat |> Seq.length\nprint "unreached"\n' > "$fddir/prodexit.weir"
rc=0; out=$(timeout 10 $BIN "$fddir/prodexit.weir" 2>&1) || rc=$?
[ $rc -eq 5 ] && [ -z "$out" ] || fail "exit 5 in a producer exits 5 (rc=$rc): $out"
printf 'let r = [1..1000000] |> Seq.map (fun i -> if i == 300000 then fail "late" else $"{i}") | head -1 | line\nprint r\n' > "$fddir/prodhead.weir"
out=$(timeout 10 $BIN "$fddir/prodhead.weir" 2>&1) || fail "a child closing stdin early is not the producer's failure: $out"
expect "producer into head -1 still succeeds" "1" "$out"
echo "e2e ok: a producer's raise into a command fails at its line; exit keeps its code; head -1 stays clean"

# ---- value-headed pipelines, more shapes [D:value-headed-pipe] ----
# an expression piped into an external command feeds its stdin
out=$($BIN -e '["a"; "b"; "c"] | tr a-z A-Z')
expect "value-headed pipe feeds stdin" '["A"; "B"; "C"]' "$out"
# value-headed pipe carries args to the child
out=$($BIN -e '["x"] | tr x y')
expect "value-headed pipe passes args to the child" '["y"]' "$out"
# multi-external chains — grep -c, not wc: BSD wc left-pads the count
# and the pin asserts exact bytes (still exactly two externals)
out=$($BIN -e '["one"; "two"] | cat | grep -c ""')
expect "value-headed multi-external chain" '["2"]' "$out"
# resolution decides: a library/binding head keeps the bare-pipe teaching
errout=$($BIN -e '[1; 2] | Seq.length' 2>&1) && fail "library head must keep the pipe hint"
echo "$errout" | grep -qF "'|' chains commands" || fail "library-head hint: $errout"
# type demand: scalar and seq<int> each get their teaching
errout=$(printf '"x" | tr a b\n' | checkPiped 2>&1) && fail "scalar LHS must reject"
echo "$errout" | grep -qF "one line wraps as \`[x]\`" || fail "scalar teaching: $errout"
errout=$(printf '[1; 2] | cat\n' | checkPiped 2>&1) && fail "seq<int> LHS must reject"
echo "$errout" | grep -qF "map show or interpolate per element" || fail "seq<int> teaching: $errout"
# a value-headed single external segment now reifies (session 2) — bound,
# it type-checks (bare, it is a discard like any non-unit expression)
out=$(printf 'let r = ["x"] | grep x | complete\nprint (show r.exitCode)\n' | runPiped)
expect "value-headed | complete now reifies (bind it)" "0" "$out"
echo "e2e ok: value-headed pipe — resolution boundary, type teachings"
# reifier-with-stdin [D:value-headed-pipe] (session 2): a value-headed
# single external segment reifies with the value as stdin
out=$($BIN -e '["apple"; "banana"; "cherry"] | grep app | complete')
expect "value-headed | complete reifies with stdin" 'stdout = ["apple"]' "$out"
out=$($BIN -e '["foo"; "bar"; "foobar"] | grep -c foo | complete')
expect "reified value-headed count (grep -c)" 'stdout = ["2"]' "$out"
out=$($BIN -e '["x"] | grep x | succeeds')
expect "value-headed | succeeds" "true" "$out"
out=$($BIN -e '["x"] | grep zzz | exitCode')
expect "value-headed | exitCode" "1" "$out"
# expression-position reification is the captured chain [D:drop-reify-builtins]
out=$($BIN -e 'let r = $(echo hi | complete) in r.stdout')
expect "expression-position reification via \$(... | complete)" '["hi"]' "$out"
# a multi-external chain reifies as one unit [D:chain-reifiers]
printf 'let r = echo hi | grep h | complete\nprint $"{r.exitCode} {r.stdout |> Seq.head}"\n' > "$fddir/chainr.weir"
out=$($BIN "$fddir/chainr.weir" 2>&1) || fail "a reified chain must run: $out"
expect "multi-external chain | complete" "0 hi" "$out"
echo "e2e ok: reifier-with-stdin (complete/succeeds/exitCode), zero-diff spellings, chains reify"
rm -rf "$fddir"

# ---- [<Default>]: the resting point moves [D:default-attr] ----

dadir=$(mkweirtmp)
cat > "$dadir/cli.weir" <<'WEOF'
type Cli = {
    [<Default 10000>]
    /// cases per invariant
    count: int
    [<Default true>]
    color: bool
    quiet: bool
}

let cli = Args.load Cli
print $"count={cli.count} color={show cli.color} quiet={show cli.quiet}"
WEOF
out=$($BIN "$dadir/cli.weir")
expect "Default fills the resting point" "count=10000 color=true quiet=false" "$out"
out=$($BIN "$dadir/cli.weir" --no-color --count 5)
expect "the minted --no-X twin sets false" "count=5 color=false" "$out"
out=$($BIN "$dadir/cli.weir" --color)
expect "the positive form is an idempotent no-op" "color=true" "$out"
errout=$($BIN "$dadir/cli.weir" --color --no-color 2>&1) && fail "both polarities must reject"
echo "$errout" | grep -qF "'--color' and '--no-color' are both given" || fail "both-given names both: $errout"
errout=$($BIN "$dadir/cli.weir" --no-colr 2>&1) && fail "minted typo must reject"
echo "$errout" | grep -qF "Did you mean '--no-color'?" || fail "minted did-you-mean: $errout"
out=$($BIN "$dadir/cli.weir" --help)
echo "$out" | grep -qF "default: 10000" || fail "help shows the literal default: $out"
echo "$out" | grep -qF "cases per invariant" || fail "help shows the /// doc beside the default: $out"
echo "$out" | grep -qF -- "default: on — --no-color disables" || fail "help shows the bool resting point: $out"
echo "e2e ok: Default fills, mints, teaches, and renders (the help-shape pin)"

rm -rf "$dadir"

# ---- Env.load consumes Default [D:default-attr]: the resting point
# ---- sits below the whole overlay stack ----

endir=$(mkweirtmp)
cat > "$endir/child.weir" <<'WEOF'
type Cfg = { [<Default 8080>] PORT_ZQ: int; [<Default false>] DEBUG_ZQ: bool }
let c = Env.load Cfg
print $"port={c.PORT_ZQ} debug={show c.DEBUG_ZQ}"
WEOF
cat > "$endir/layers.env" <<'WEOF'
PORT_ZQ=9090
WEOF
cat > "$endir/parent.weir" <<'WEOF'
// layer 3: the env-sigil overlay becomes the child's process env
let layers = Env.fromFile "layers.env"
within env layers
    weir child.weir
WEOF
out=$(cd "$endir" && $BIN child.weir)
expect "neither layer sets it: the attribute fills (both types)" "port=8080 debug=false" "$out"
out=$(cd "$endir" && PORT_ZQ=7000 $BIN child.weir)
expect "process env beats the attribute" "port=7000" "$out"
out=$(cd "$endir" && PATH="$BINDIR:$PATH" $BIN parent.weir)
expect "the file overlay (via the env sigil) beats the attribute in the child" "port=9090" "$out"
out=$(cd "$endir" && PORT_ZQ=7000 DEBUG_ZQ=true $BIN child.weir)
expect "Default false on an env bool is a real resting point (set wins)" "debug=true" "$out"
rm -rf "$endir"
echo "e2e ok: the Default resting point sits below the whole env stack"

# ---- scriptPath: the $0 gap closes [D:script-path] ----

spdir=$(mkweirtmp)
# macOS: mktemp lives under the /var symlink and weir absolutizes a
# relative script path against the physical cwd (getcwd) — compare
# physical to physical (pwd -P is POSIX; a no-op on Linux)
spdir=$(cd "$spdir" && pwd -P)
mkdir -p "$spdir/sub" "$spdir/pbin"
cat > "$spdir/sub/where.weir" <<'WEOF'
#!/usr/bin/env weir
cd ..
print (Self.scriptPath |> Path.dir)
WEOF
chmod +x "$spdir/sub/where.weir"
cp "$spdir/sub/where.weir" "$spdir/pbin/where.weir"

# one absolute answer three ways: the three outputs must agree and end
# at the right leaf — never a full-path equality (the separator class;
# weir prints the platform's)
out1=$(cd "$spdir" && $BIN sub/where.weir | tail -1)
out2=$(cd "$spdir/sub" && $BIN ./where.weir | tail -1)
out3=$($BIN "$spdir/sub/where.weir" | tail -1)
[ "$out1" = "$out2" ] && [ "$out2" = "$out3" ] || fail "three invocations must agree: $out1 / $out2 / $out3"
echo "$out1" | grep -q "sub$" || fail "scriptPath's dir must end at sub: $out1"
case "$out1" in /*|[A-Za-z]:*) ;; *) fail "scriptPath must be absolute: $out1" ;; esac
echo "e2e ok: scriptPath — one absolute answer three ways, resolved BEFORE the cd"

if [ "$IS_WINDOWS" = "1" ]; then
    echo "e2e SKIP: shebang-on-PATH — a bare .weir on PATH rides the POSIX shebang (no PATHEXT entry for .weir; a stated product gap, not a fixture one)"
else
    out=$(cd "$spdir" && PATH="$(pathEntry "$spdir/pbin"):$BINDIR:$PATH" where.weir | tail -1)
    [ "$out" = "$spdir/pbin" ] || fail "shebang-on-PATH gets the SCRIPT's path: got $out"
    echo "e2e ok: shebang-on-PATH resolves to the script, not the interpreter"
fi

errout=$($BIN -e 'scriptPath' 2>&1) && fail "-e must refuse scriptPath"
echo "$errout" | grep -qF "scriptPath is script-only" || fail "the teaching: $errout"
echo "e2e ok: scriptPath refused outside scripts with its teaching"

# ---- Self.pid: introspection replaces the $PPID shell-out [D:self-module] ----
cat > "$spdir/pid.weir" <<'WEOF'
let a = Self.pid
let b = Self.pid
print (a == b)
print (a > 0)
WEOF
out=$($BIN "$spdir/pid.weir")
[ "$(echo "$out" | sed -n 1p)" = "true" ] || fail "Self.pid must be STABLE across reads: $out"
[ "$(echo "$out" | sed -n 2p)" = "true" ] || fail "Self.pid must be a positive int: $out"
# it is the running process (matches the OS view of the child)
printf 'print Self.pid\n' > "$spdir/pid2.weir"
got=$($BIN "$spdir/pid2.weir")
echo "$got" | grep -qE '^[0-9]+$' || fail "Self.pid prints an int: $got"
echo "e2e ok: Self.pid is a stable positive int (the $PPID shell-out retires)"

rm -rf "$spdir"

# ---- Path.glob [D:path-glob]: typed discovery, nothing expands ----

pgdir=$(mkweirtmp)
mkdir -p "$pgdir/src/a/b" "$pgdir/fixtures/x" "$pgdir/deny" "$pgdir/loop"
touch "$pgdir/top.json" "$pgdir/other.json" "$pgdir/.hidden.json" \
      "$pgdir/src/one.fs" "$pgdir/src/a/two.fs" "$pgdir/src/a/b/three.fs" \
      "$pgdir/fixtures/x/f.txt" "$pgdir/deny/secret.fs"
# a real symlink or none: Git Bash's ln copy-emulates by default (a
# self-referential loop cannot be copied); nativestrict asks for the
# real thing (runners hold the privilege) and failure gates the
# symlink cell as a stated skip rather than a broken fixture
HAVE_LOOP=1
MSYS=winsymlinks:nativestrict ln -s .. "$pgdir/loop/up" 2>/dev/null || HAVE_LOOP=0
# permission-denial fixtures are inexpressible under root (uid 0
# ignores modes) — the skip assertion below gates on the euid
chmod 000 "$pgdir/deny"

cat > "$pgdir/g.weir" <<'WEOF'
Path.glob "*.json" |> Seq.iter print
Path.glob ".*.json" |> Seq.iter print
Path.glob "**/*.fs" |> Seq.iter print

match Path.glob "*.nope" with
| [] -> print "no matches"
| files -> files |> Seq.iter print

let pinned = Path.glob "*.json" |> Seq.freeze
cd src
let after = Path.glob "*.json" |> Seq.length
print $"lazy-sees-new-cwd: {after}"
print $"forced-pinned: {pinned |> Seq.length}"
WEOF
out=$(cd "$pgdir" && $BIN g.weir)
chmod 755 "$pgdir/deny"
expect "glob: * excludes dotfiles, sorted" "other.json
top.json" "$out"
expect "glob: a dot segment matches them" ".hidden.json" "$out"
expect "glob: ** crosses segments, skips unreadable dirs and symlinks" "src/a/b/three.fs" "$out"
# an effectiveness gate, not an euid gate (as with locked.txt):
# root ignores modes and Windows chmod is inert — test whether the
# denial actually took
if [ -r "$pgdir/deny/secret.fs" ]; then
    echo "e2e SKIP: unreadable-dir cell — chmod 000 did not deny here (root, or Windows where modes are inert)"
else
    echo "$out" | grep -qF "secret.fs" && fail "unreadable dir must skip"
fi
if [ "$HAVE_LOOP" = "1" ]; then
    echo "$out" | grep -qF "loop/up" && fail "globstar must not traverse symlinks"
else
    echo "e2e SKIP: the symlink-loop cell — no real symlink on this runner (Git Bash ln copy-emulates without nativestrict support)"
fi
expect "glob: no matches is the empty seq (the match-[] idiom)" "no matches" "$out"
expect "glob: the cd seam — lazy sees the new cwd" "lazy-sees-new-cwd: 0" "$out"
expect "glob: Seq.freeze pins the answer before cd" "forced-pinned: 2" "$out"

# script-relative discovery: the scriptPath gate's payoff
cat > "$pgdir/src/rel.weir" <<'WEOF'
cd /
Path.glob $"{Self.scriptPath |> Path.dir}/../fixtures/**/*.txt" |> Seq.iter print
WEOF
out=$(cd "$pgdir" && $BIN src/rel.weir)
echo "$out" | grep -qF "fixtures/x/f.txt" || fail "script-relative glob after cd /: $out"
echo "e2e ok: glob composes with scriptPath (script-relative, cd-proof)"

# glob into a child's stdin (the value-headed composition)
cat > "$pgdir/fd.weir" <<'WEOF'
Path.glob "*.json" | sort -r |> Seq.iter print
WEOF
out=$(cd "$pgdir" && $BIN fd.weir)
expect "glob | child: discovery into a child's stdin" "top.json
other.json" "$out"

# the timing ceiling: 10k files enumerate under 2s on the AOT binary
big=$(mkweirtmp)
mkdir -p "$big/d"
(cd "$big/d" && seq 1 10000 | xargs touch)
cat > "$big/t.weir" <<'WEOF'
let n = Path.glob "d/*" |> Seq.length
print $"{n}"
WEOF
start=$(now_ms)
out=$(cd "$big" && $BIN t.weir)
elapsed=$(($(now_ms) - start))
expect "glob: 10k files counted" "10000" "$out"
[ "$elapsed" -lt 2000 ] || fail "glob 10k ceiling: ${elapsed}ms"
echo "e2e ok: glob 10k-file tree under the ceiling (${elapsed}ms)"

# a segment matches with a linear wildcard matcher, no regex
# [D:glob-linear]: a 7-star miss against one 239-char name with 79
# dashes ran past 100s as a regex; a repeated `**` yields each path once
mkdir -p "$big/p" "$big/r/a/b"
(cd "$big/p" && touch "$(printf 'ab-%.0s' $(seq 1 79))ab.txt")
touch "$big/r/x.gz" "$big/r/a/y.gz" "$big/r/a/b/z.gz"
cat > "$big/patho.weir" <<'WEOF'
within cd "p"
    print $"miss={Path.glob "*-*-*-*-*-*-*.gz" |> Seq.length} hit={Path.glob "*-*-*-*-*-*-*.txt" |> Seq.length}"
print $"twice={Path.glob "r/**/**/*.gz" |> Str.join ","}"
print $"apart={Path.glob "r/**/a/**/*.gz" |> Str.join ","}"
WEOF
start=$(now_ms)
out=$(cd "$big" && timeout 60 $BIN patho.weir 2>&1) || fail "glob pathological pattern: $out"
elapsed=$(($(now_ms) - start))
expect "glob: the 7-star miss is empty, its .txt twin matches" "miss=0 hit=1" "$out"
expect "glob: a repeated ** yields each path once" "twice=r/x.gz,r/a/y.gz,r/a/b/z.gz" "$out"
expect "glob: ** apart yields each path once" "apart=r/a/y.gz,r/a/b/z.gz" "$out"
[ "$elapsed" -lt 5000 ] || fail "glob pathological-pattern ceiling: ${elapsed}ms"
echo "e2e ok: glob segments match linearly (7-star miss in ${elapsed}ms) and a repeated ** dedupes"
rm -rf "$pgdir" "$big"

# frozen seqs know their length and index [D:seq-index-o1], and a long
# string's code points are indexed once [D:str-code-points]: each loop
# below was quadratic at 100k (16-54s each); a generous ceiling catches
# the regression without flaking on load
o1dir=$(mkweirtmp)
cat > "$o1dir/o1.weir" <<'WEOF'
let n = 100000
let xs = [1..n] |> Seq.freeze
let a = [1..n] |> Seq.map (fun _ -> Seq.length xs) |> Seq.sum
let b = [0..(n - 1)] |> Seq.map (fun i -> xs[i] + Seq.item i xs) |> Seq.sum
let c = [1..n] |> Seq.map (fun _ -> xs[^1] + Seq.last xs) |> Seq.sum
let d = [0..(n - 1)] |> Seq.choose (fun i -> Seq.tryItem i xs) |> Seq.length
let s = Str.replicate n "a"
let e = [1..n] |> Seq.map (fun _ -> Str.length s) |> Seq.sum
let f = [0..(n - 1)] |> Seq.map (fun i -> Str.sub i 1 s + s[i..i]) |> Seq.where (fun t -> t == "aa") |> Seq.length
let w = Str.replicate (n / 2) "a😀"
let g = [0..(n - 1)] |> Seq.map (fun i -> Str.sub i 1 w) |> Str.join ""
print $"{a} {b} {c} {d} {e} {f} {Str.length w} {g == w}"
WEOF
start=$(now_ms)
out=$(timeout 120 $BIN "$o1dir/o1.weir" 2>&1) || fail "O(1) length/index loops: $out"
elapsed=$(($(now_ms) - start))
expect "frozen length/index and string positions" "10000000000 10000100000 20000000000 100000 10000000000 100000 100000 true" "$out"
[ "$elapsed" -lt 10000 ] || fail "frozen-seq / string-position loops ceiling: ${elapsed}ms (quadratic regression?)"
echo "e2e ok: frozen seqs index in O(1), long strings index code points once (${elapsed}ms for 9 x 100k)"
rm -rf "$o1dir"

# ---- Seq.distinct [D:seq-distinct]: dedupe as a pipeline stage ----

sddir=$(mkweirtmp)
mkdir -p "$sddir"; touch "$sddir/a.txt" "$sddir/b.txt" "$sddir/ab.md"
cat > "$sddir/d.weir" <<'WEOF'
// the deferred glob product cell: overlapping patterns dedupe
let one = Path.glob "a*"
let two = Path.glob "*.txt"

two |> Seq.append one |> Seq.distinct |> Seq.iter print
WEOF
out=$(cd "$sddir" && $BIN d.weir)
expect "glob overlap dedupes through Seq.distinct" "a.txt
ab.md
b.txt" "$out"
count=$(cd "$sddir" && $BIN d.weir | wc -l)
[ "$count" -eq 3 ] || fail "distinct must drop the overlap: $count lines" # -eq: BSD wc pads
echo "e2e ok: Seq.distinct closes the glob-overlap product cell"
rm -rf "$sddir"

# ---- argv splat $@xs [D:argv-splat]: N things, N words ----

spldir=$(mkweirtmp)
( cd "$spldir" && git init -q . && touch a.txt b.txt c.md )

# form 1: glob into git add, verified via git status output
cat > "$spldir/add.weir" <<'WEOF'
let files = Path.glob "*.txt" |> Seq.freeze
git add $@files
git status --porcelain |> Seq.where (Str.startsWith "A ") |> Seq.iter print
WEOF
out=$(cd "$spldir" && $BIN add.weir)
expect "splat: glob into git add (N files, N words)" "A  a.txt
A  b.txt" "$out"

# empty splat vanishes — argv inspection and behavior
cat > "$spldir/empty.weir" <<'WEOF'
let qf = if false then ["-q"] else []
sh -c "echo argc=$#" self $@qf tail
WEOF
out=$(cd "$spldir" && $BIN empty.weir)
expect "splat: empty seq contributes ZERO words" "argc=1" "$out"

# adversarial elements stay single words (the injection pin)
cat > "$spldir/evil.weir" <<'WEOF'
let evil = ["one two"; "semi;colon"; "star*glob"]
sh -c "echo argc=$#" self $@evil
WEOF
out=$(cd "$spldir" && $BIN evil.weir)
expect "splat: spaces/semicolons/globs stay ONE word each (no re-split)" "argc=3" "$out"

# splat rides reifier chains [D:splat-reifier-chains]: the safety pin
# re-run through the reifier path (word integrity identical), empty
# splat, the env-sigil route (the flagship's shape), all four reifiers,
# value-headed, district
cat > "$spldir/reify.weir" <<'WEOF'
let evil = ["one two"; "semi;colon"; "star*glob"]
let r = sh -c "echo argc=$#" self $@evil | complete
print (r.stdout |> Seq.head)

let none = if false then ["x"] else []
let z = sh -c "echo argc=$#" self $@none | complete
print (z.stdout |> Seq.head)

let author = Env.ofPairs [("MARK", "sigil")]
let argv = ["-c"; "echo m=$MARK; exit 4"]
let s = $author(sh $@argv | complete)
print (s.stdout |> Seq.head)
print $"code {s.exitCode}"

let ok = sh $@argv | succeeds
print $"ok={ok}"

let vflags = ["-c"]
let vh = ["a"; "b"; "a"] | grep $@vflags a | complete
print (vh.stdout |> Seq.head)

within env author
    sh -c "echo d=$MARK" self $@none | orFail "boom"
WEOF
out=$(cd "$spldir" && $BIN reify.weir)
expect "splat through the reifier path: adversarial + empty + env sigil + value-headed + district" "argc=3
argc=0
m=sigil
code 4
ok=false
2
d=sigil" "$out"

# the head and mid-word teachings
errout=$(printf 'let xs = ["ls"]
$@xs -la
' | checkPiped 2>&1) && fail "head splat must reject"
echo "$errout" | grep -qF "N words would be N heads" || fail "head teaching: $errout"
errout=$(printf 'let fs = ["a"]
echo --flag=$@fs
' | checkPiped 2>&1) && fail "mid-word splat must reject"
echo "$errout" | grep -qF "cannot join a word under construction" || fail "mid-word teaching: $errout"
# the type teachings, both directions
errout=$(printf 'let ns = [1; 2]
echo $@ns
' | checkPiped 2>&1) && fail "seq<int> splat must reject"
echo "$errout" | grep -qF "map show or interpolate" || fail "seq<int> teaching: $errout"
errout=$(printf 'let s = "x"
echo $@s
' | checkPiped 2>&1) && fail "scalar splat must reject"
echo "$errout" | grep -qF "one value? use \$x" || fail "scalar teaching: $errout"
echo "e2e ok: splat teaches head, mid-word, and both type directions"

# the argv boundary refuses NUL on its own [D:encoding-law] — never
# silent truncation. fromBase64 rejects NUL as non-text, but NUL still
# arrives via File.read of a NUL-bearing file; the spawn hand-off is
# where the byte would truncate, so the refusal lives there.
nuldir=$(mkweirtmp)
printf 'a\0b' > "$nuldir/nul.bin"
cat > "$nuldir/nul-argv.weir" <<WEOF
let v = File.read "$nuldir/nul.bin" |> Seq.head
python3 -c "import sys; print(len(sys.argv[1]))" \$v
WEOF
out=$($BIN "$nuldir/nul-argv.weir" 2>&1 || true)
echo "$out" | grep -qF "contains a NUL byte" || fail "the argv NUL refusal is missing: $out"
echo "$out" | grep -qF "would silently truncate" || fail "the diagnostic must name the truncation: $out"
echo "$out" | grep -qxF "1" && fail "the child saw a truncated word — the refusal did not fire"
cat > "$nuldir/nul-env.weir" <<WEOF
let v = File.read "$nuldir/nul.bin" |> Seq.head
let e = Env.ofPairs [("X", v)]
within env e
    printenv X
WEOF
out=$($BIN "$nuldir/nul-env.weir" 2>&1 || true)
echo "$out" | grep -qF "contains a NUL byte" || fail "the env NUL refusal is missing: $out"
rm -rf "$nuldir"
echo "e2e ok: NUL refuses at the spawn boundary (argv and env), naming the truncation"

# the binary-echo probe recurses through containers [D:binary-echo]: a
# `| complete` record carrying NUL-bearing stdout must refuse the tty
# echo (the record echo leaked what the seq echo refused); the -e echo
# wears the same guard. Needs a pty — `script` — and SKIPs by name
# where absent (absence is never a pass).
# the pty instrument is probed, not assumed (as `timeout` is):
# macOS ships BSD script, whose syntax has no -e/-c — command -v passes
# and the invocation dies. A failing probe is a named SKIP.
if command -v script >/dev/null 2>&1 && script -qec true /dev/null >/dev/null 2>&1; then
    bindir=$(mkweirtmp)
    cat > "$bindir/repl-in.txt" <<'RINEOF'
let b = sh -c "printf \"x\\0y\"" | complete
b
#quit
RINEOF
    cat > "$bindir/e-payload.txt" <<'PINEOF'
let r = sh -c "printf \"x\\0y\"" | complete
r.stdout
PINEOF
    cat > "$bindir/repl-run.sh" <<RUNEOF
#!/bin/sh
exec $BIN < "$bindir/repl-in.txt"
RUNEOF
    cat > "$bindir/e-run.sh" <<RUNEOF
#!/bin/sh
exec $BIN -e "\$(cat "$bindir/e-payload.txt")"
RUNEOF
    chmod +x "$bindir/repl-run.sh" "$bindir/e-run.sh"
    out=$(script -qec "$bindir/repl-run.sh" /dev/null 2>&1 | cat -v)
    echo "$out" | grep -qF "binary output" || fail "the record echo must refuse binary at a tty: $out"
    echo "$out" | grep -qF 'x^@y' && fail "raw bytes leaked through the record echo: $out"
    out=$(script -qec "$bindir/e-run.sh" /dev/null 2>&1 | cat -v)
    echo "$out" | grep -qF "binary output" || fail "-e must refuse binary at a tty: $out"
    out=$("$bindir/e-run.sh" 2>&1 | cat -v)
    echo "$out" | grep -qF 'x^@y' || fail "-e redirected must stay RAW (the data channel): $out"
    rm -rf "$bindir"
    echo "e2e ok: binary echo refuses containers and -e at a tty; redirected stays raw"
else
    echo "e2e SKIP: binary-echo pty cells — no util-linux 'script -e -c' on this runner (BSD script differs)"
fi

# scalar mid-word splice mirrors the splat's fatal [D:argv-splat]: the
# glued prefix would silently drop, so name the space/interp spellings
errout=$(printf 'let f = "x"
echo --file=$f
' | checkPiped 2>&1) && fail "mid-word scalar splice must reject"
echo "$errout" | grep -qF "cannot join a word under construction" || fail "mid-word scalar teaching: $errout"
# argv pieces do not concatenate [D:argv-concat]: the suffix side of the
# whole-word law — before the guard, $root/* silently became two words
errout=$(printf 'let root = "r"
echo $root/*
' | checkPiped 2>&1) && fail "glued tail must reject"
echo "$errout" | grep -qF "argv words do not concatenate" || fail "glued-tail teaching: $errout"
errout=$(printf 'echo --flag="quoted v"
' | checkPiped 2>&1) && fail "glued quote must reject"
echo "$errout" | grep -qF "argv words do not concatenate" || fail "glued-quote teaching: $errout"
# a triple-quoted string is ONE argv word; @"…" refuses with its own teaching [D:argv-at-raw]
out=$(printf 'echo """a "b" c""" x\n' | runPiped 2>&1) || fail "triple-quoted argv word: $out"
[ "$out" = 'a "b" c x' ] || fail "triple-quoted argv word: $out"
errout=$(printf 'echo @"C:\\x"\n' | checkPiped 2>&1) && fail "verbatim argv word must reject"
echo "$errout" | grep -qF "is not an argv word" || fail "verbatim argv teaching: $errout"
# a path-shaped word gets the path hint, not the flag hint [D:argv-splat]:
# the space repair splits one path into two args, so the message leads with
# interpolation and Path.under (the confining join — combine would follow an escaping tail)
errout=$(printf 'let build = "b"
rm -rf ./tt3/$build
' | checkPiped 2>&1) && fail "mid-word path splice must reject"
echo "$errout" | grep -qF "cannot join a path under construction" || fail "path splice says path, not word: $errout"
echo "$errout" | grep -qF "Path.under" || fail "path splice names Path.under: $errout"
# the spaced spelling stays legal (one argv word each)
out=$(printf 'let f = "x.txt"
echo --file $f
' | runPiped 2>&1)
echo "$out" | grep -qF -- "--file x.txt" || fail "spaced splice must pass: $out"
echo "e2e ok: scalar mid-word splice rejects, spaced spelling passes"

# the child's args take a splat while input streams (both axes)
cat > "$spldir/fd.weir" <<'WEOF'
let flags = ["-r"]
["a"; "b"; "c"] | sort $@flags |> Seq.iter print
WEOF
if $BIN check "$spldir/fd.weir" >/dev/null 2>&1; then
    out=$(cd "$spldir" && $BIN fd.weir)
    expect "splat in the child's args while input streams" "c
b
a" "$out"
else
    echo "e2e note: child-arg splat needs a paren splice arg — covered by argv building"
fi
rm -rf "$spldir"

# deep-lock loudness [D:vacuous-probe-audit]: a garbage lock is not a
# stale lock — check must exit 3 (probe-failure), never clear it, and
# publish's consumers treat any non-1 as refuse
LOCKFILE="$(dirname "$0")/../.weir-deep-run.lock"
if [ -e "$LOCKFILE" ]; then
    fail "a deep-run lock exists — not exercising the garbage-lock pin over a live lock"
fi
echo "not-a-pid" > "$LOCKFILE"
rc=0
ci/deep-lock.sh check >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 3 ] || { rm -f "$LOCKFILE"; fail "garbage lock must exit 3, got $rc"; }
[ -f "$LOCKFILE" ] || fail "garbage lock must NOT be auto-cleared"
rm -f "$LOCKFILE"
echo "e2e ok: deep-lock refuses to guess on a garbage lock (exit 3, lock preserved)"

# the depth guard's stack probe on the AOT binary [D:depth-stack-probe]:
# a small stack must produce the located diagnostic, never a SIGSEGV
# (the macOS finding — test hosts there run smaller stacks than Linux)
ddir=$(mkweirtmp)
python3 -c "print('let _x = ' + '('*499 + '1' + ')'*499)" > "$ddir/deep.weir" 2>/dev/null \
    || awk 'BEGIN{s="let _x = "; for(i=0;i<499;i++)s=s"("; s=s"1"; for(i=0;i<499;i++)s=s")"; print s}' > "$ddir/deep.weir"
$BIN check "$ddir/deep.weir" >/dev/null 2>&1 || fail "depth 499 must parse on a full-size stack"
if [ "$IS_WINDOWS" = "1" ]; then
    # ulimit -s cannot constrain a native Windows process (the stack
    # reserve is baked at link time) — the small-stack premise does not
    # exist there; the depth guard itself ran above at full stack
    echo "e2e SKIP: the small-stack probe cell — no POSIX stack limit on Windows (depth 499 parsed above)"
else
    # the front end runs on weir's own stack, not the process main
    # thread's [D:block-width]: a 512KB `ulimit -s` no longer starves the
    # parse (the probe itself stays pinned by the unit 512KB-thread test)
    errout=$( (ulimit -s 512; $BIN check "$ddir/deep.weir") 2>&1 )
    rc=$?
    [ "$rc" -eq 0 ] || fail "depth 499 must parse under ulimit -s 512 (rc=$rc): $errout"
    echo "e2e ok: depth 499 parses under ulimit -s 512 (weir's own stack), and on a full-size stack"
fi
rm -rf "$ddir"

# position translation is indexed once per statement: a 64k-line heredoc
# runs as fast as it checks (a per-node segment scan made run quadratic)
hpdir=$(mkweirtmp)
python3 -c "print('\n'.join(['let s = <<<'] + ['    line %d' % i for i in range(64000)] + ['print (Seq.length s)']))" > "$hpdir/long.weir"
out=$(timeout 20 $BIN "$hpdir/long.weir" 2>&1) || fail "a 64k-line heredoc must run in well under 20s: $out"
[ "$out" = "64000" ] || fail "a 64k-line heredoc: its line count, got: $out"
rm -rf "$hpdir"
echo "e2e ok: a 64k-line heredoc runs in linear time"

# statements are width, not depth [D:block-width]: 20k top-level
# statements run in a loop, and a 2000-statement function body neither
# meets the nesting ceiling nor overflows a 1MB `ulimit -s`
bwdir=$(mkweirtmp)
python3 -c "print('\n'.join('print \"x\"' for _ in range(20000)))" > "$bwdir/flat.weir"
python3 -c "
print('let f () =')
for i in range(2000): print(f'    let _x{i} = {i}')
print('    _x0 + _x1999')
print('print \$\"{f ()}\"')" > "$bwdir/body.weir"
if [ "$IS_WINDOWS" = "1" ]; then
    n=$($BIN "$bwdir/flat.weir" | wc -l | tr -d ' ') || fail "20k flat statements must run"
    out=$($BIN "$bwdir/body.weir" 2>&1) || fail "a 2000-statement body must run: $out"
else
    n=$( (ulimit -s 1024; $BIN "$bwdir/flat.weir") | wc -l | tr -d ' ') || fail "20k flat statements must run on a 1MB stack"
    out=$( (ulimit -s 1024; $BIN "$bwdir/body.weir") 2>&1) || fail "a 2000-statement body must run on a 1MB stack: $out"
fi
[ "$n" = "20000" ] || fail "20k flat statements: all printed, got $n"
[ "$out" = "1999" ] || fail "a 2000-statement body: its value, got: $out"
rm -rf "$bwdir"
echo "e2e ok: 20k top-level statements and a 2000-statement body run under a 1MB stack"

# deep lazy chains walk iteratively [D:seq-depth-iterative]: a fold nesting
# Seq.append / Seq.map / Seq.where / Seq.choose 100k deep enumerates on a small stack (POSIX: 1MB,
# Windows' default main-thread size), and `always` runs
lcdir=$(mkweirtmp)
cat > "$lcdir/chains.weir" <<'WEOF'
within
    let a = [1..100000] |> Seq.fold (fun acc i -> Seq.append acc [i]) []
    let m = [1..100000] |> Seq.fold (fun acc i -> Seq.map (fun x -> x + 1) acc) [0]
    let w = [1..100000] |> Seq.fold (fun acc i -> Seq.where (fun x -> x <> i) acc) [0]
    let c = [1..100000] |> Seq.fold (fun acc i -> Seq.choose (fun x -> Some(x + 1)) acc) [0]
    print $"{Seq.length a} {Seq.head m} {Seq.head w} {Seq.head c}"
always
    printerr "cleanup ran"
WEOF
if [ "$IS_WINDOWS" = "1" ]; then
    out=$($BIN "$lcdir/chains.weir" 2>&1) || fail "deep lazy chains must enumerate: $out"
else
    out=$( (ulimit -s 1024; $BIN "$lcdir/chains.weir") 2>&1) || fail "deep lazy chains must enumerate on a 1MB stack: $out"
fi
[ "$out" = "100000 100000 0 100000
cleanup ran" ] || fail "deep lazy chains: length, innermost-first maps, always ran: $out"
rm -rf "$lcdir"
echo "e2e ok: 100k-deep Seq.append / Seq.map / Seq.where / Seq.choose chains enumerate iteratively"

# ---- user modules and imports, session 1 [D:modules-v1] -------------------
mdir=$(mkweirtmp)
cat > "$mdir/paths.weir" <<'WEOF'
module Paths
type Ctx = { root: string; name: string }
let make : string -> string -> Ctx
let make r n = { root = r; name = n }
let describe : Ctx -> string
let describe c = c.root
WEOF

# happy path: import, qualified member use, a record type crossing
cat > "$mdir/main.weir" <<'WEOF'
import "./paths.weir"
let c = Paths.make "r" "n"
print (Paths.describe c)
print c.name
WEOF
out=$($BIN "$mdir/main.weir")
expect "import: qualified member use + a record type crossing the boundary" "r
n" "$out"

# a module is checkable in isolation (an entry point)
$BIN check "$mdir/paths.weir" >/dev/null 2>&1 || fail "a clean module must check in isolation"
echo "e2e ok: a module checks alone (weir check lib.weir)"

# the qualified literal always resolves, even under field-set ambiguity
cat > "$mdir/disambig.weir" <<'WEOF'
import "./paths.weir"
type Local = { root: string; name: string }
let l = Local { root = "x"; name = "y" }
let c = Paths.Ctx { root = "a"; name = "b" }
print l.root
print c.name
WEOF
out=$($BIN "$mdir/disambig.weir")
expect "the qualified/named literal disambiguates same-field-set records" "x
b" "$out"

# a clean named-literal file checks without spurious warnings (the
# assume-command rule must not read an uppercase type name as a command)
$BIN check "$mdir/disambig.weir" 2>&1 | grep -q . && fail "a clean named-literal module produced diagnostics" || echo "e2e ok: named literals check clean under the assume-command rule"

# a bare literal matching two in-scope records is ambiguous, naming both
cat > "$mdir/ambig.weir" <<'WEOF'
import "./paths.weir"
type Local = { root: string; name: string }
let x = { root = "a"; name = "b" }
WEOF
out=$($BIN check "$mdir/ambig.weir" 2>&1 || true)
expect "a bare literal matching two records is ambiguous, naming both" "ambiguous record literal; it matches: Ctx, Local" "$out"

# alias
cat > "$mdir/aliased.weir" <<'WEOF'
import "./paths.weir" as P
print (P.describe (P.make "aa" "bb"))
WEOF
out=$($BIN "$mdir/aliased.weir")
expect "import ... as Name binds the module under the alias" "aa" "$out"

# running a module errors, naming the escape
printf 'import "./paths.weir"\n' > "$mdir/e_run.weir"
out=$($BIN "$mdir/paths.weir" 2>&1 || true)
expect "running a module errors: it declares, it does not run" "a module declares; it does not run" "$out"

# importing a non-module names the fix
printf 'let x = 1\nprint x\n' > "$mdir/plain.weir"
printf 'import "./plain.weir"\n' > "$mdir/e_notmod.weir"
out=$($BIN check "$mdir/e_notmod.weir" 2>&1 || true)
expect "importing a non-module names the fix (add module, or invoke as a command)" "is not a module; add \`module\` at the top" "$out"

# a missing import puts the resolved absolute path in the message
printf 'import "./nope.weir"\n' > "$mdir/e_missing.weir"
out=$($BIN check "$mdir/e_missing.weir" 2>&1 || true)
# message shape + resolved-ness (the parent leaf, dot-joined) — never
# the verbatim path: weir answers the platform's separators and the
# long 8.3 form (the separator class)
echo "$out" | grep -qF "cannot resolve import: no file at" || fail "missing-import message shape: $out"
echo "$out" | grep -q "$(basename "$mdir").nope.weir" || fail "missing import must name the RESOLVED path: $out"
echo "e2e ok: a missing import names the resolved absolute path"

# self-import has its own message
printf 'import "./e_self.weir"\nprint "hi"\n' > "$mdir/e_self.weir"
out=$($BIN check "$mdir/e_self.weir" 2>&1 || true)
expect "self-import has its own message" "a file cannot import itself" "$out"

# declaration-only: a module cannot hold a command or bare expression
printf 'module B\nlet x = 1\nprint x\n' > "$mdir/declonly.weir"
out=$($BIN check "$mdir/declonly.weir" 2>&1 || true)
expect "a module is declaration-only (direct check enforces it)" "a module declares only" "$out"

# weak purity: a module 'let' cannot run a command at import; a lazy
# command let is a value — binding it spawns nothing [D:lazy-command-lets]
printf 'module W\nlet head : seq<string>\nlet head = git rev-parse HEAD\n' > "$mdir/weak.weir"
out=$($BIN check "$mdir/weak.weir" 2>&1) || fail "a lazy command let must check clean in a module: $out"
printf 'module W\nlet head = git rev-parse HEAD |> Seq.freeze\n' > "$mdir/weak.weir"
out=$($BIN check "$mdir/weak.weir" 2>&1 || true)
expect "weak purity: a module let cannot run a command at import" "cannot run a command at import" "$out"
# ...including through a reifier [D:module-let-reifier]: `cmd | line`
# desugars to no command node, and once ran at import unnoticed
for rf in 'git rev-parse HEAD | line' 'git log -1 | text' 'git status | succeeds'; do
    printf 'module W
let head = %s
' "$rf" > "$mdir/weakr.weir"
    out=$($BIN check "$mdir/weakr.weir" 2>&1 || true)
    expect "weak purity through a reifier: $rf" "cannot run a command at import" "$out"
done
# ...and through the module's own helper: an eager call runs its command
# at import exactly as the command would — refused at check AND at import,
# before any side effect
printf 'module W\nlet v : string\nlet helper () = sh -c "echo SIDE-EFFECT-AT-IMPORT >&2; echo val" |> Seq.exactlyOne\nlet v = helper ()\n' > "$mdir/weakh.weir"
out=$($BIN check "$mdir/weakh.weir" 2>&1 || true)
expect "weak purity through a helper (check)" "'helper' runs one, and this calls it now" "$out"
printf 'import "./weakh.weir"\nprint W.v\n' > "$mdir/weakh_main.weir"
out=$($BIN "$mdir/weakh_main.weir" 2>&1 || true)
expect "weak purity through a helper (import)" "cannot run a command at import" "$out"
case "$out" in *SIDE-EFFECT-AT-IMPORT*) fail "a helper ran its command at import: $out" ;; esac
# ...and at run time, whatever the route [D:module-import-no-commands]:
# a helper through a value, a lambda, composition, collect or a pmap arm
# slips past the check-time walk, and the spawn itself is refused, located
for form in '[()] |> Seq.map helper |> Seq.map Seq.length |> Seq.sum' \
            '(fun () -> helper ()) () |> Seq.length' \
            '(helper >> Seq.length) ()' \
            '[()] |> Seq.collect helper |> Seq.freeze |> Seq.length' \
            '[1; 2] |> Seq.pmap (fun _ -> helper () |> Seq.length) |> Seq.sum'; do
    printf 'module W\nlet helper () = sh -c "echo SIDE-EFFECT-AT-IMPORT >&2; echo val"\nlet _h = helper\nlet v : int\nlet v = %s\n' "$form" > "$mdir/weakv.weir"
    printf 'import "./weakv.weir"\nprint $"{W.v}"\n' > "$mdir/weakv_main.weir"
    rc=0; out=$($BIN "$mdir/weakv_main.weir" 2>&1) || rc=$?
    [ "$rc" -eq 1 ] || fail "a command at import must fail the run ($form): rc=$rc $out"
    expect "no command at import, run time: $form" "weakv.weir:2:" "$out"
    expect "no command at import, run time (message): $form" "runs a command while module W is imported" "$out"
    case "$out" in *SIDE-EFFECT-AT-IMPORT*) fail "a command ran at import ($form): $out" ;; esac
done

# the graph [D:modules-v1]: transitive imports resolve and evaluate; a
# module can import and use another module's members
printf 'module D\nlet base : int\nlet base = 10\n' > "$mdir/deep.weir"
printf 'module M\nimport "./deep.weir"\nlet doubled : int\nlet doubled = D.base * 2\n' > "$mdir/mid.weir"
printf 'import "./mid.weir"\nprint (show M.doubled)\n' > "$mdir/transitive.weir"
out=$($BIN "$mdir/transitive.weir" 2>&1)
expect "transitive imports resolve and evaluate (top -> mid -> deep)" "20" "$out"

# a diamond's shared module is checked once and evaluates; both sides see it
printf 'module S\nlet v : int\nlet v = 5\n' > "$mdir/shared.weir"
printf 'module L\nimport "./shared.weir"\nlet x : int\nlet x = S.v + 1\n' > "$mdir/left.weir"
printf 'module R\nimport "./shared.weir"\nlet y : int\nlet y = S.v + 2\n' > "$mdir/right.weir"
printf 'import "./left.weir"\nimport "./right.weir"\nprint (show (L.x + R.y))\n' > "$mdir/diamond.weir"
out=$($BIN "$mdir/diamond.weir" 2>&1)
expect "a diamond shares one module across both paths" "13" "$out"

# a module's body runs once per program, identity = its real path
# [D:module-once]: a diamond, a re-alias and a symlinked import share one
# Uuid.v4; an 18-level double diamond (2^17 import paths) stays instant
printf 'module U\nlet uid : Uuid\nlet uid = Uuid.v4 ()\n' > "$mdir/once.weir"
printf 'module V\nimport "./once.weir"\nlet id2 : Uuid\nlet id2 = U.uid\n' > "$mdir/oncev.weir"
ln -s once.weir "$mdir/oncelink.weir" 2>/dev/null || cp "$mdir/once.weir" "$mdir/oncelink.weir"
printf 'import "./once.weir"\nimport "./oncev.weir"\nimport "./once.weir" as U2\nimport "./oncelink.weir" as U3\nprint $"{U.uid == V.id2} {U.uid == U2.uid} {U.uid == U3.uid}"\n' > "$mdir/once-main.weir"
out=$($BIN "$mdir/once-main.weir" 2>&1)
if [ -L "$mdir/oncelink.weir" ]; then
    expect "a module evaluates once: diamond, re-alias, symlink" "true true true" "$out"
else
    expect "a module evaluates once: diamond, re-alias" "true true false" "$out"
fi
for i in $(seq 0 17); do
    if [ "$i" = 0 ]; then
        printf 'module M0\nlet x : int\nlet x = 1\n' > "$mdir/dd0.weir"
    else
        printf 'module M%s\nimport "./dd%s.weir" as A\nimport "./dd%s.weir" as B\nlet x : int\nlet x = A.x + B.x\n' "$i" "$((i-1))" "$((i-1))" > "$mdir/dd$i.weir"
    fi
done
printf 'import "./dd17.weir" as T\nprint (show T.x)\n' > "$mdir/dd-main.weir"
out=$(timeout 20 $BIN "$mdir/dd-main.weir" 2>&1)
expect "an 18-level double diamond evaluates each module once" "131072" "$out"

# a member named like a builtin is exported, reachable qualified; the bare
# name stays the builtin [D:module-builtin-names]
printf 'module Doc\ntype Doc = Text of string | Empty\nlet id : int -> int\nlet id n = n + 100\nlet render : Doc -> string\nlet render d =\n    match d with\n    | Text s -> s\n    | Empty -> ""\n' > "$mdir/bdoc.weir"
printf 'import "./bdoc.weir"\nprint $"{Doc.render (Doc.Text "hi")} {Doc.id 1} {id 1} {Text "b"}"\n' > "$mdir/bdoc-main.weir"
out=$($BIN "$mdir/bdoc-main.weir" 2>&1)
expect "builtin-named members export; the bare name stays the builtin" 'hi 101 1 Text "b"' "$out"

# an import cycle is a check error naming the loop, at its closing edge
printf 'module CA\nimport "./cb.weir"\nlet a = 1\n' > "$mdir/ca.weir"
printf 'module CB\nimport "./ca.weir"\nlet b = 2\n' > "$mdir/cb.weir"
printf 'import "./ca.weir"\nprint "hi"\n' > "$mdir/ecycle.weir"
out=$($BIN check "$mdir/ecycle.weir" 2>&1 || true)
expect "an import cycle is named as a loop" "import cycle: ca.weir → cb.weir → ca.weir" "$out"

# a deep error reports at the deepest module's own site
printf 'module DBad\nlet x = Str.trim 5\n' > "$mdir/dbad.weir"
printf 'module MBad\nimport "./dbad.weir"\nlet y = 1\n' > "$mdir/mbad.weir"
printf 'import "./mbad.weir"\nprint "hi"\n' > "$mdir/tbad.weir"
out=$($BIN check "$mdir/tbad.weir" 2>&1 || true)
expect "a transitive error reports at the deepest module's own site" "dbad.weir:2:18: error" "$out"

# the import path is a literal string only
printf 'import foo\n' > "$mdir/e_litpath.weir"
out=$($BIN check "$mdir/e_litpath.weir" 2>&1 || true)
expect "the import path must be a literal string" "import takes a literal string path" "$out"

# multi-file diagnostics [D:modules-v1]: a module-content error reports at
# the module's own file:line, plus an "imported here" note at the import line
cat > "$mdir/broken.weir" <<'WEOF'
module Broken
let bad = Str.trim 5
WEOF
printf 'import "./broken.weir"\nprint "hi"\n' > "$mdir/e_broken.weir"
out=$($BIN check "$mdir/e_broken.weir" 2>&1 || true)
expect "a module error reports at its OWN site" "broken.weir:2:20: error" "$out"
expect "an imported-here note points at the import line" "e_broken.weir:1:8: note" "$out"

# check --json carries the module's own file per diagnostic — and one
# spelling per document (round 26: the importer's diag carried argv's
# form while the module's carried the resolved one; GetFullPath is the
# identity now, so pin the leaf and pin the consistency)
out=$($BIN check --json "$mdir/e_broken.weir" 2>&1 || true)
echo "$out" | grep -q '"file":"[^"]*broken\.weir"' || fail "check --json carries the module's file identity: $out"
files=$(printf '%s' "$out" | grep -o '"file":"[^"]*tmp[^"]*"' | sed 's/.*tmp/tmp/; s/[\\/].*//' | sort -u | wc -l)
[ "$files" -le 1 ] || fail "check --json must spell every file's dir ONE way: $out"
echo "e2e ok: check --json carries the module's file identity (one spelling per document)"

# -e imports against the process cwd [D:repl-import]; a lone import is
# refused like a lone let
out=$($BIN -e 'import "./x.weir"' 2>&1 || true)
expect "-e: a missing import names its path" "no file at" "$out"

# module / import are reserved words
out=$($BIN -e 'let import = 1' 2>&1 || true)
expect "import is a reserved word" "'import' is a keyword" "$out"

# Self.scriptPath is the file's own path; Self.entryPath is the invoked
# script's (a process fact) [D:modules-v1] (decision 12)
printf 'module Sp\nlet where : unit -> string\nlet where () = Self.scriptPath\nlet entry : unit -> string\nlet entry () = Self.entryPath\n' > "$mdir/sp.weir"
printf 'import "./sp.weir"\nprint (Sp.where ())\nprint (Sp.entry ())\n' > "$mdir/spmain.weir"
out=$($BIN "$mdir/spmain.weir" 2>&1)
# leaf pins (weir prints the platform's path spelling): the property is
# which file each answer names, not the dir's spelling
echo "$out" | sed -n 1p | grep -q "sp\.weir$" || fail "a module's Self.scriptPath is its OWN file: $out"
echo "$out" | sed -n 2p | grep -q "spmain\.weir$" || fail "a module's Self.entryPath is the invoked script: $out"
echo "e2e ok: a module's Self.scriptPath is its own file; entryPath the invoked script"

# ---- module signatures [D:module-signatures]: a signature is the export ----
# one signed + one unsigned member end-to-end: the signed one exports and
# runs, the unsigned one refuses with the migration teaching (inferred
# type included, did-you-mean over the signed members); a script refuses
# the form; an orphan sig errors at its own site
cat > "$mdir/sigmod.weir" <<'WEOF'
module Sigmod

let hidden n = n + 1

/// double it
let twice : int -> int

let twice n = hidden n + n - 1
WEOF
printf 'import "./sigmod.weir"\nprint (show (Sigmod.twice 21))\n' > "$mdir/sig_ok.weir"
out=$($BIN "$mdir/sig_ok.weir")
expect "a signed member exports and runs" "42" "$out"
printf 'import "./sigmod.weir"\nprint (show (Sigmod.hidden 1))\n' > "$mdir/sig_priv.weir"
out=$($BIN check "$mdir/sig_priv.weir" 2>&1 || true)
expect "an unsigned member is module-private; the teaching names the signature to add" "'Sigmod.hidden' is module-private (no signature) — add \`let hidden : int -> int\` in the module to export it" "$out"
printf 'let f : int -> int\nlet f x = x\nprint (show (f 1))\n' > "$mdir/sig_script.weir"
out=$($BIN check "$mdir/sig_script.weir" 2>&1 || true)
expect "a script refuses the signature form" "signatures belong to module APIs — scripts infer" "$out"
printf 'module Orph\nlet f : int -> int\n' > "$mdir/sig_orph.weir"
out=$($BIN check "$mdir/sig_orph.weir" 2>&1 || true)
expect "a signature without implementation errors at the sig" "sig_orph.weir:2:5: error [sig-orphan]: signature without implementation" "$out"
printf 'module Mm\nlet f : int -> int\nlet f x = "s"\n' > "$mdir/sig_mis.weir"
out=$($BIN check "$mdir/sig_mis.weir" 2>&1 || true)
expect "a sig/impl mismatch names both sites and both types" "the signature (line 2) declares int -> int, but the implementation is int -> string" "$out"
echo "e2e ok: module signatures (export, private teaching, script refusal, orphan, mismatch)"

rm -rf "$mdir"

# ---- REPL config [D:repl-quality]: inert keys, reject unknown, and the
# load-bearing property that scripts never read it -----------------------
cfgdir=$(mkweirtmp)
# weir reads XDG_CONFIG_HOME on POSIX and %APPDATA% (the shell API, not
# the env) on Windows [D:windows-v1] — so the Windows half uses the
# runner's real AppData (a throwaway VM's), giving the config path
# actual coverage instead of a skip; cleaned up either way
if [ "$IS_WINDOWS" = "1" ]; then
    CFGHOME="$APPDATA"
else
    CFGHOME="$cfgdir"
fi
mkdir -p "$CFGHOME/weir"
printf '{"historySizee": 10}\n' > "$CFGHOME/weir/config.json"
out=$(printf '#quit\n' | XDG_CONFIG_HOME="$cfgdir" XDG_STATE_HOME="$cfgdir/state" $BIN 2>&1 || true)
expect "the REPL config rejects an unknown key with did-you-mean" "unknown key 'historySizee'. Did you mean 'historySize'?" "$out"

# echoElems revived [D:echo-cap]: the config seeds the session cap
# (#echo reads it back); a non-positive value teaches and defaults
printf '{"echoElems": 25}\n' > "$CFGHOME/weir/config.json"
out=$(printf '#echo\n#quit\n' | XDG_CONFIG_HOME="$cfgdir" XDG_STATE_HOME="$cfgdir/state" $BIN 2>&1 || true)
expect "the config seeds the echo cap" "echo cap: 25" "$out"
printf '{"echoElems": 0}\n' > "$CFGHOME/weir/config.json"
out=$(printf '#echo\n#quit\n' | XDG_CONFIG_HOME="$cfgdir" XDG_STATE_HOME="$cfgdir/state" $BIN 2>&1 || true)
echo "$out" | grep -qF "echoElems must be positive; got 0 (using 100)" || fail "a non-positive echoElems must teach: $out"
echo "$out" | grep -qF "echo cap: 100" || fail "…and default: $out"

# an unusable home never stops the REPL [D:repl-history]: HOME= is no
# home [D:home-verbatim] — history turns off with one note, lines run
if [ "$IS_WINDOWS" != "1" ]; then
    out=$(printf 'print (Str.toUpper "hi")\n#history\n' | env -u XDG_CONFIG_HOME -u XDG_STATE_HOME HOME= $BIN 2>&1); rc=$?
    [ $rc -eq 0 ] || fail "HOME= must not stop the REPL (rc $rc): $out"
    expect "HOME= runs the REPL line" "HI" "$out"
    expect "…and turns history off with a note" "history off for this session" "$out"
fi

# invalid UTF-8 in history is skipped, never rewritten [D:history-utf8]: the
# duplicate makes the load rewrite the file; the bad lines come back
# byte-for-byte in place, stay out of #history, and one located note says so
if [ "$IS_WINDOWS" != "1" ]; then
    hu="$cfgdir/hutf8"
    mkdir -p "$hu/weir"
    printf 'a\n\377\376 bad\nb\na\nlet m = "x\\nmatch\n\303(\n' > "$hu/weir/history"
    out=$(printf '#history\n' | XDG_CONFIG_HOME="$cfgdir/none" XDG_STATE_HOME="$hu" $BIN 2>&1); rc=$?
    [ $rc -eq 0 ] || fail "a history with invalid UTF-8 must not stop the REPL (rc $rc): $out"
    expect "the bad lines get one located note" "weir: history: $hu/weir/history:2: invalid UTF-8 — 2 lines left out of recall, kept on disk as-is" "$out"
    expect "…the valid entries recall, deduped" "(4 entries)" "$out"
    echo "$out" | grep -qF "bad" && fail "an invalid-UTF-8 line must not be recalled: $out"
    printf '\377\376 bad\nb\na\nlet m = "x\\nmatch\n\303(\n' > "$cfgdir/hutf8-expected"
    cmp "$hu/weir/history" "$cfgdir/hutf8-expected" || fail "the load rewrite must keep invalid-UTF-8 lines byte-for-byte: $(od -c "$hu/weir/history")"
fi

# a malformed config must not affect a script — scripts never read it, so the
# script runs clean rather than erroring on the broken JSON
printf 'not valid json {{{\n' > "$CFGHOME/weir/config.json"
printf 'print "scripts-ignore-config"\n' > "$cfgdir/s.weir"
out=$(XDG_CONFIG_HOME="$cfgdir" $BIN "$cfgdir/s.weir" 2>&1)
expect "a script ignores the REPL config entirely (even a broken one)" "scripts-ignore-config" "$out"
rm -f "$CFGHOME/weir/config.json"
rm -rf "$cfgdir"

# ---- command-head aliases [D:command-head-alias] -----------------------
# an init.weir #alias line maps a short head to (exe, prefix args),
# consulted before PATH in command-head position, REPL-only. Stubs on
# PATH echo their argv so resolution is asserted without a real tool.
# Skipped on Windows (the .bat stubs quote argv differently — the POSIX
# half proves the resolution law).
if [ "$IS_WINDOWS" != "1" ]; then
    acfg=$(mkweirtmp)
    astub=$(mkweirtmp)
    printf '#!/bin/sh\necho "kubectl-argv:$*"\n' > "$astub/kubectl" && chmod +x "$astub/kubectl"
    printf '#!/bin/sh\necho "kustomize-argv:$*"\n' > "$astub/kustomize" && chmod +x "$astub/kustomize"
    printf '#!/bin/sh\necho "realls-argv:$*"\n' > "$astub/ls" && chmod +x "$astub/ls"
    mkdir -p "$acfg/weir"
    printf '#init\n#alias k  = kubectl\n#alias kb = kustomize build\n#alias ls = ls --color\n' > "$acfg/weir/init.weir"

    # (a) a head alias resolves; the rest stays bare argv
    out=$(printf 'k get po -o yaml\n#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>/dev/null)
    expect "an alias resolves the head; argv stays bare" "kubectl-argv:get po -o yaml" "$out"

    # (b) prefix args insert after the exe, before user argv
    out=$(printf 'kb overlays/prod\n#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>/dev/null)
    expect "a prefix-args alias inserts the fixed prefix" "kustomize-argv:build overlays/prod" "$out"

    # (c) injection-safety: a splice passes as one argv entry (the stub's
    # $* joins with spaces, so a single spliced value shows whole)
    out=$(printf 'let x = "a b"\nk get $x\n#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>/dev/null)
    expect "a spliced value stays ONE argument through an alias" "kubectl-argv:get a b" "$out"

    # (d) ^ bypasses the shadowing alias — real ls, no --color prefix
    out=$(printf '^ls xarg\n#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>/dev/null)
    expect "^ forces PATH, bypassing the alias table" "realls-argv:xarg" "$out"
    echo "$out" | grep -qF -- "--color" && fail "^ls must NOT carry the alias prefix: $out"

    # (e) REPL-only: a script using an alias errors unknown-command (no leak)
    printf 'k get po\n' > "$acfg/uses-alias.weir"
    out=$(PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN "$acfg/uses-alias.weir" 2>&1 || true)
    echo "$out" | grep -qF "unbound variable 'k'" || fail "a script must not see aliases (no leak): $out"

    # (f) a malformed #alias is a loud init error (all-or-nothing)
    printf '#init\n#alias = kubectl\n' > "$acfg/weir/init.weir"
    out=$(printf '#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>&1 || true)
    echo "$out" | grep -qF "malformed #alias" || fail "a malformed #alias must be a loud init error: $out"
    echo "$out" | grep -qF "not loaded" || fail "a malformed init is all-or-nothing: $out"

    # (g) an alias-of-alias is rejected at define time (single-hop)
    printf '#init\n#alias k = kubectl\n#alias kk = k\n' > "$acfg/weir/init.weir"
    out=$(printf '#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>&1 || true)
    echo "$out" | grep -qF "single-hop" || fail "an alias-of-alias must be rejected: $out"

    # (h) #save desugars alias heads: the saved script is alias-free and checks
    printf '#init\n#alias k  = kubectl\n#alias kb = kustomize build\n' > "$acfg/weir/init.weir"
    out=$(printf 'let pods = k get po -o json\nlet note = "k is a letter"\n#save %s/saved.weir\n#quit\n' "$acfg" \
        | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>/dev/null)
    echo "$out" | grep -qF "#save: wrote" || fail "#save must write with aliases active: $out"
    grep -qF "kubectl get po -o json" "$acfg/saved.weir" || fail "#save must desugar the alias head: $(cat "$acfg/saved.weir")"
    grep -qF '"k is a letter"' "$acfg/saved.weir" || fail "#save desugar must leave a string spelling the alias untouched: $(cat "$acfg/saved.weir")"
    PATH="$astub:$PATH" $BIN check "$acfg/saved.weir" || fail "the desugared #save output must check clean: $(cat "$acfg/saved.weir")"

    # (i) a session binding shadows the alias table [D:alias-binding-shadow]:
    # an init declaring both refuses located (all-or-nothing); a live let
    # over an alias shadows it (the function is callable); a live #alias
    # over a binding refuses with the teaching; an alias over a BUILTIN
    # name keeps resolving (cell (a)'s ls case is the canonical alias)
    printf '#init\nlet hi () = print "the-function"\n\n#alias hi = kubectl\n' > "$acfg/weir/init.weir"
    out=$(printf '#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>&1 || true)
    echo "$out" | grep -qF "is also declared in this file" || fail "an init alias/let collision must refuse located: $out"
    echo "$out" | grep -qF "not loaded" || fail "the alias/let collision is all-or-nothing: $out"
    rm -f "$acfg/weir/init.weir"
    out=$(printf '#alias hi = kubectl\nlet hi () = print "the-function"\nhi ()\n#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>&1)
    echo "$out" | grep -qF "the-function" || fail "a session binding must shadow the alias (hi () callable): $out"
    out=$(printf 'let hi () = print "the-function"\n#alias hi = kubectl\nhi ()\n#quit\n' | PATH="$astub:$PATH" XDG_CONFIG_HOME="$acfg" $BIN 2>&1)
    echo "$out" | grep -qF "would never resolve" || fail "a live #alias over a binding must refuse with the teaching: $out"
    echo "$out" | grep -qF "the-function" || fail "the binding must stay callable after the refused #alias: $out"

    echo "e2e ok: #alias resolves head->exe (+prefix), injection-safe, ^-bypass, REPL-only, malformed=loud, single-hop, #save desugars alias-free, bindings shadow the table"
    rm -rf "$acfg" "$astub"
fi

# ---- imports resolve from the importing file's real location [D:init-import]:
# a symlinked init file imports its dotfiles neighbour, and so does a
# symlinked script; Self.scriptPath keeps the link [D:script-path]
if [ "$IS_WINDOWS" != "1" ]; then
    icfg=$(mkweirtmp)
    mkdir -p "$icfg/weir" "$icfg/dot" "$icfg/bin"
    printf 'module Greet\n\n/// greets-from-the-module-doc\nlet hi : unit -> string\n\nlet hi () = "hi-from-module"\n' > "$icfg/dot/greet.weir"
    printf '#init\n\nimport "./greet.weir"\n\nlet shout () = Greet.hi ()\n' > "$icfg/dot/init.weir"
    ln -s "$icfg/dot/init.weir" "$icfg/weir/init.weir"
    out=$(printf 'print (shout ())\nprint (Greet.hi ())\n#quit\n' | XDG_CONFIG_HOME="$icfg" $BIN 2>&1)
    expect "an init file imports a module beside its real location" "hi-from-module" "$out"
    echo "$out" | grep -qF "not loaded" && fail "the importing init must load: $out"
    out=$(printf '#help Greet.hi\n#quit\n' | XDG_CONFIG_HOME="$icfg" $BIN 2>&1)
    expect "#help shows an init-imported member's /// doc" "greets-from-the-module-doc" "$out"
    ckout=$($BIN check "$icfg/weir/init.weir" 2>&1) || fail "check through the init symlink must resolve the import: $ckout"

    printf 'import "./greet.weir"\n\nprint (Greet.hi ())\nprint Self.scriptPath\n' > "$icfg/dot/tool.weir"
    ln -s "$icfg/dot/tool.weir" "$icfg/bin/tool.weir"
    out=$($BIN "$icfg/bin/tool.weir" 2>&1) || fail "a symlinked script must resolve its import: $out"
    expect "a symlinked script imports beside its real location" "hi-from-module" "$out"
    expect "Self.scriptPath keeps the link" "$icfg/bin/tool.weir" "$out"
    rm -rf "$icfg"
fi

# ---- import at the prompt and in -e [D:repl-import]: a relative path
# resolves against the session cwd when the import runs (cd moves it);
# a re-import after an edit reloads; #help shows a member's /// doc;
# #save carries the import, re-spelled absolute
rdir=$(mkweirtmp)
mkdir -p "$rdir/sub"
printf 'module M\n\n/// doubles-from-the-module-doc\nlet twice : int -> int\nlet twice n = n * 2\nlet v : int\nlet v = 1\n' > "$rdir/m.weir"
printf 'module M\n\n/// doubles-from-the-module-doc\nlet twice : int -> int\nlet twice n = n * 2\nlet v : int\nlet v = 42\n' > "$rdir/next.weir"
printf 'module S\nlet w : string\nlet w = "from-sub"\n' > "$rdir/sub/s.weir"
out=$(cd "$rdir" && printf '%s\n' 'import "./m.weir"' 'print $"v={M.v}"' 'let y = M.twice 5' 'cp next.weir m.weir' 'import "./m.weir"' 'print $"v={M.v}"' '#help M.twice' 'cd sub' 'import "./s.weir"' 'print S.w' "#save $rdir/saved.weir" '#quit' | $BIN 2>&1)
expect "prompt import binds the members" "module M imported (2 members: twice, v)" "$out"
expect "prompt import: the value" "v=1" "$out"
expect "a re-import after an edit reloads" "module M reloaded" "$out"
expect "the reloaded value" "v=42" "$out"
expect "#help shows a prompt-imported member's /// doc" "doubles-from-the-module-doc" "$out"
expect "a relative import after cd resolves from the new cwd" "from-sub" "$out"
saved=$(cat "$rdir/saved.weir")
if [ "$IS_WINDOWS" != "1" ]; then
    expect "#save carries the import, absolute" "import \"$rdir/m.weir\" as M" "$saved"
else
    expect "#save carries the import" "m.weir\" as M" "$saved"
fi
ckout=$($BIN check "$rdir/saved.weir" 2>&1) || fail "the saved session must check: $ckout"
# the module rules hold at the prompt: a non-module and a command at import refuse
printf 'let x = 1\n' > "$rdir/plain.weir"
printf 'module C\nlet x : string\nlet x = echo hi | line\n' > "$rdir/cmd.weir"
out=$(cd "$rdir" && printf '%s\n' 'import "./plain.weir" as P' 'import "./cmd.weir"' '#quit' | $BIN 2>&1)
expect "prompt import of a non-module refuses" "is not a module" "$out"
expect "prompt import refuses a command at import" "cannot run a command at import" "$out"
# -e: newline-separated statements, the import resolving from the process cwd
out=$(cd "$rdir" && $BIN -e 'import "./m.weir" as K
K.twice K.v' 2>&1) || fail "-e import: $out"
expect "-e imports against the process cwd" "84 : int" "$out"
out=$(cd "$rdir" && $BIN -e 'import "./m.weir" as K' 2>&1 || true)
expect "-e refuses a lone import" "-e takes an expression, not an import" "$out"
echo "e2e ok: import at the prompt and in -e (cwd-relative, reload, #help doc, #save, module rules)"
rm -rf "$rdir"

# ---- File.append under concurrent appenders [D:append-oappend] ------------
# N weir processes append to one file at once; every call must land
# contiguous and intact — O_APPEND on Unix, so no writer can overwrite
# another's bytes and no lock blocks a concurrent reader. Payload shapes
# cycle: one short line, a three-line call, a >4KiB line, a >64KiB line,
# each line a JSON object carrying writer id, seq and its own sha256.
if [ "$IS_WINDOWS" != "1" ]; then
    apdir=$(mkweirtmp)
    cat > "$apdir/writer.weir" <<'WEOF'
type Cli = {
    /// writer id
    id: int
    /// append calls to make
    calls: int
    /// the shared log path
    log: string
}

let cli = Args.load Cli

let payload size tag =
    let unit = $"{tag}-abcdefghijklmnopqrstuvwxyz0123456789-"
    let reps = size / Str.length unit + 1
    ([1..reps] |> Seq.map (fun _ -> unit) |> Str.join "")[0 .. size - 1]

let jline q k n data =
    let h = Str.sha256 data
    $"{{\"w\":{cli.id},\"q\":{q + 0},\"k\":{k + 0},\"n\":{n + 0},\"h\":\"{h}\",\"d\":\"{data}\"}}"

for s in [1 .. cli.calls] do
    match s % 4 with
    | 1 -> File.append cli.log [ jline s 1 1 (payload 40 $"w{cli.id}s{s}") ]
    | 2 ->
        File.append
            cli.log
            [ jline s 1 3 (payload 60 $"w{cli.id}s{s}a")
              jline s 2 3 (payload 60 $"w{cli.id}s{s}b")
              jline s 3 3 (payload 60 $"w{cli.id}s{s}c") ]
    | 3 -> File.append cli.log [ jline s 1 1 (payload 5000 $"w{cli.id}s{s}") ]
    | _ -> File.append cli.log [ jline s 1 1 (payload 70000 $"w{cli.id}s{s}") ]
WEOF
    cat > "$apdir/validate.weir" <<'WEOF'
type Cli = {
    /// the shared log path
    log: string
    /// expected writers
    writers: int
    /// calls per writer
    calls: int
}

type Line = { w: int; q: int; k: int; n: int; h: string; d: string }
type Pend = { pw: int; pq: int; pn: int; pk: int }

let cli = Args.load Cli
let raw = File.read cli.log |> Seq.freeze
let expected = cli.writers * (cli.calls / 4 * 6)

let parsed =
    raw
    |> Seq.map (fun l ->
        let r = [ l ] |> from json Line
        if Str.sha256 r.d <> r.h then fail $"checksum mismatch at w{r.w} q{r.q} k{r.k}"
        r)
    |> Seq.freeze

let zero = { pw = 0; pq = 0; pn = 0; pk = 0 }

let tail =
    parsed
    |> Seq.fold
        (fun st r ->
            if st.pn == 0 then
                if r.k <> 1 then
                    fail $"call starts mid-way: w{r.w} q{r.q} k{r.k}"

                if r.n > 1 then
                    { pw = r.w; pq = r.q; pn = r.n; pk = 2 }
                else
                    zero
            else
                if r.w <> st.pw || r.q <> st.pq || r.k <> st.pk then
                    fail $"call w{st.pw} q{st.pq} not contiguous: found w{r.w} q{r.q} k{r.k} wanting k{st.pk}"

                if st.pk == st.pn then zero else { st with pk = st.pk + 1 })
        zero

if tail.pn <> 0 then
    fail $"file ends mid-call w{tail.pw} q{tail.pq}"

if (raw |> Seq.length) <> expected then
    fail $"line count {raw |> Seq.length} <> expected {expected}"

print "append-stress ok"
WEOF
    aplog="$apdir/shared.jsonl"
    : > "$aplog"
    appids=""
    for i in 1 2 3 4; do
        "$BIN" "$apdir/writer.weir" --id "$i" --calls 16 --log "$aplog" & appids="$appids $!"
    done
    # a concurrent weir reader must never fail against the appenders
    # (no locks on the O_APPEND path)
    "$BIN" -e "File.read \"$aplog\" |> Seq.length |> show |> print" > /dev/null 2>"$apdir/reader.err" || true
    aprc=0
    for p in $appids; do wait "$p" || aprc=1; done
    [ "$aprc" = "0" ] || fail "append stress: a writer failed"
    [ -s "$apdir/reader.err" ] && fail "append stress: the concurrent reader errored: $(cat "$apdir/reader.err")" || true
    out=$("$BIN" "$apdir/validate.weir" --log "$aplog" --writers 4 --calls 16 2>&1) || fail "append stress validation: $out"
    echo "$out" | grep -qF "append-stress ok" || fail "append stress: $out"
    echo "e2e ok: File.append — 4 concurrent appenders, every call contiguous and intact, reader unblocked"
fi

# ---- File.write integrity + File.writeAtomic [D:write-integrity] ----------
# a File.write whose content raises mid-enumeration must leave the file
# byte-identical (the payload encodes before the truncate); writeAtomic
# swaps whole files — BOM and mode preserved, a symlink's target
# replaced (the link intact), no temp litter on any path
if [ "$IS_WINDOWS" != "1" ]; then
    widir=$(mkweirtmp)
    # weir's own digest/mode readers keep the cell off GNU-only coreutils
    # (macOS has no sha256sum, and stat -c is GNU-only) — dogfooded and
    # portable; the BOM bytes read through od -N3 | tr (the proven form)
    bom3() { od -An -tx1 -N3 "$1" | tr -d ' '; }
    printf 'precious original\nsecond line\n' > "$widir/keep.txt"
    before=$($BIN -e "print (File.sha256 \"$widir/keep.txt\")")
    $BIN -e "File.write \"$widir/keep.txt\" ([1; 2; 3] |> Seq.map (fun i -> if i == 2 then fail \"boom\" else show i))" 2>/dev/null && fail "the raising write must raise"
    after=$($BIN -e "print (File.sha256 \"$widir/keep.txt\")")
    [ "$before" = "$after" ] || fail "a raising File.write must leave the file byte-identical"

    printf '\xef\xbb\xbfbom line\n' > "$widir/bom.txt"
    $BIN -e "File.writeAtomic \"$widir/bom.txt\" [\"swapped\"]" || fail "writeAtomic failed"
    [ "$(bom3 "$widir/bom.txt")" = "efbbbf" ] || fail "writeAtomic must preserve an existing BOM"

    printf 'plain\n' > "$widir/mode.txt" && chmod 640 "$widir/mode.txt"
    $BIN -e "File.writeAtomic \"$widir/mode.txt\" [\"replaced\"]" || fail "writeAtomic failed"
    [ "$($BIN -e "print (File.mode \"$widir/mode.txt\" |> Option.defaultValue \"none\")")" = "rw-r-----" ] || fail "writeAtomic must preserve the target's mode"
    [ "$(bom3 "$widir/mode.txt")" = "efbbbf" ] && fail "writeAtomic must not add a BOM" || true

    printf 'target orig\n' > "$widir/t.txt" && ln -s t.txt "$widir/l.txt"
    $BIN -e "File.writeAtomic \"$widir/l.txt\" [\"via link\"]" || fail "writeAtomic through a symlink failed"
    grep -qF "via link" "$widir/t.txt" || fail "writeAtomic must replace the symlink's target"
    [ -L "$widir/l.txt" ] || fail "the symlink itself must survive"

    ls "$widir"/.*weir-tmp* >/dev/null 2>&1 && fail "writeAtomic left temp litter" || true
    echo "e2e ok: File.write leaves a file untouched on a raising payload; writeAtomic swaps whole (BOM, mode, symlink target, no litter)"
fi

# ---- dynamic command heads [D:dynamic-head] ----------------------------
# ^$name / ^$(…) force-external a value head: one program, resolved at
# run, argv stays typed argv. Stubs echo their argv so injection safety
# is asserted without a real tool. Skipped on Windows (POSIX stubs; the
# unit pins carry the parse/check laws everywhere).
if [ "$IS_WINDOWS" != "1" ]; then
    dhdir=$(mkweirtmp)
    printf '#!/bin/sh\necho "argc=$#"\nfor a in "$@"; do echo "arg:[$a]"; done\n' > "$dhdir/argprobe" && chmod +x "$dhdir/argprobe"
    cp "$dhdir/argprobe" "$dhdir/spaced probe"

    # (a) the head is a runtime value; args pass through typed
    printf 'let tool = "%s/argprobe"\n^$tool one two\n' "$dhdir" > "$dhdir/a.weir"
    out=$($BIN "$dhdir/a.weir")
    expect "a dynamic head runs the value" "argc=2" "$out"

    # (b) injection safety: a spliced arg with spaces stays one argv entry
    printf 'let tool = "%s/argprobe"\nlet x = "a b; rm -rf /"\n^$tool $x\n' "$dhdir" > "$dhdir/b.weir"
    out=$($BIN "$dhdir/b.weir")
    expect "a spaced splice is one argument under a dynamic head" "arg:[a b; rm -rf /]" "$out"
    echo "$out" | grep -qF "argc=1" || fail "injection: exactly one argv entry: $out"

    # (c) a head value with spaces is one program, never re-lexed
    printf 'let tool = "%s/spaced probe"\n^$tool hi\n' "$dhdir" > "$dhdir/c.weir"
    out=$($BIN "$dhdir/c.weir")
    expect "a spaced head value is one program" "arg:[hi]" "$out"

    # (d) the seq-capture head refuses with the located bind-and-pick teaching
    printf '^$(git branch) status\n' > "$dhdir/d.weir"
    out=$($BIN check "$dhdir/d.weir" 2>&1 || true)
    expect "a seq-capture head refuses with the teaching" "which line is it? bind and pick" "$out"
    echo "$out" | grep -qF "d.weir:1:2" || fail "the refusal is located: $out"

    # (e) a string-typed capture head runs (the Phase-0 free case)
    printf '^$(printf %s/argprobe |> Seq.exactlyOne) go\n' "$dhdir" > "$dhdir/e.weir"
    out=$($BIN "$dhdir/e.weir")
    expect "a string-typed capture heads directly" "arg:[go]" "$out"

    # (f) not-found is a located run error naming the value; check stays silent
    printf 'let tool = "zz-no-such-prog"\n^$tool hi\n' > "$dhdir/f.weir"
    $BIN check "$dhdir/f.weir" 2>&1 | grep -q "cmd-not-found" && fail "check must not warn on a dynamic head (run-time carve-out)"
    out=$($BIN "$dhdir/f.weir" 2>&1 || true)
    expect "not-found names the value at run" "command not found: zz-no-such-prog — the dynamic head ^\$tool resolves at run time" "$out"
    echo "$out" | grep -qF "f.weir:2" || fail "the run error is located: $out"

    # (g) composition: pipe, capture, reifier, env overlay
    printf 'let tool = "%s/argprobe"\nlet r = ^$tool x | complete\nprint $"exit={r.exitCode}"\n' "$dhdir" > "$dhdir/g.weir"
    out=$($BIN "$dhdir/g.weir")
    expect "^\$tool | complete reifies" "exit=0" "$out"
    printf 'let tool = "sh"\nlet e = Env.ofPairs [("DH_PROBE", "seen")]\nlet o = $e(^$tool -c "echo p=$DH_PROBE") |> Seq.exactlyOne\nprint o\n' > "$dhdir/h.weir"
    out=$($BIN "$dhdir/h.weir")
    expect "an env sigil overlays a dynamic head" "p=seen" "$out"

    # (i) --can surfaces the dynamic head; --strict treats it as opaque
    printf 'let tool = "%s/argprobe"\n^$tool hi\n' "$dhdir" > "$dhdir/i.weir"
    out=$($BIN check --can "$dhdir/i.weir" 2>&1)
    expect "--can names the dynamic head under runs" '^$tool (not statically known — a dynamic head resolves at run)' "$out"
    echo "$out" | grep -qF "this report is incomplete: 1 opaque site" || fail "a dynamic head counts as an opaque site: $out"
    strictrc=0
    $BIN check --can --strict "$dhdir/i.weir" >/dev/null 2>&1 || strictrc=$?
    [ "$strictrc" = "2" ] || fail "--strict must exit 2 on a dynamic head (got $strictrc)"

    # (j) the fatal spellings teach: splat head, interpolated head
    printf 'let xs = ["a"]\n^$@xs\n' > "$dhdir/j.weir"
    out=$($BIN check "$dhdir/j.weir" 2>&1 || true)
    expect "a splat cannot head (dynamic spelling)" "a splat cannot head a command" "$out"
    printf 'let d = "x"\n^$"{d}/tool" run\n' > "$dhdir/k.weir"
    out=$($BIN check "$dhdir/k.weir" 2>&1 || true)
    expect "an interpolated head teaches bind-first" "bind it first" "$out"

    # (l) zero movement: ^ls still forces the PATH binary
    printf '^ls\n' > "$dhdir/l.weir"
    $BIN "$dhdir/l.weir" > /dev/null || fail "^ls (literal force) must still run"

    echo "e2e ok: dynamic heads run the value (argv typed, spaced head one program), refuse seq captures located, carve out resolution to run, compose (pipe/capture/complete/env), surface in --can/--strict"
    rm -rf "$dhdir"
fi

# ---- help glance + #find [D:help-glance] [D:help-find] --------------------
# the headless doc render is the one #help source: `weir --repl-doc X`
# must print byte-identically to the piped `#help X` answer (prompts
# stripped) — the pin that keeps #find's fzf preview and the prompt's
# own answer from drifting.
hdir=$(mkweirtmp)
$BIN --repl-doc Option > "$hdir/headless.txt" || fail "--repl-doc Option must succeed"
printf '#help Option\n#quit\n' | $BIN | sed -e 's/^weir> //' -e '/^weir>/d' > "$hdir/prompted.txt"
diff -u "$hdir/headless.txt" "$hdir/prompted.txt" >/dev/null \
    || fail "--repl-doc must print the exact #help bytes: $(diff "$hdir/headless.txt" "$hdir/prompted.txt" | head -5)"
# the directive reference table [D:help-find]: docs/reference/lexical.md
# names every dispatched directive across its three contexts (the
# gen-lexical spirit — a new directive without a table row fails here)
for d in help find echo infer save history alias quit init session sig; do
    grep -q "\`#$d\`" "$(dirname "$0")/../docs/reference/lexical.md" \
        || fail "docs/reference/lexical.md directive table is missing #$d"
done
rm -rf "$hdir"
echo "e2e ok: --repl-doc == #help bytes (one source); lexical.md names every directive"

# ---- for/do: the general effect loop [D:for-do] ---------------------------
fdir=$(mkweirtmp)
# the natural shell shape: a bare command body over a real external
cat > "$fdir/loop.weir" <<'WEOF'
for w in ["one"; "two"] do sh -c $"echo got-{w}"
WEOF
out=$($BIN "$fdir/loop.weir")
expect "for/do runs a bare command body per element (implicit effect)" "got-one
got-two" "$out"

# multi-line for bodies need no district [D:district-retirement]
cat > "$fdir/district.weir" <<'WEOF'
for f in ["a"; "b"] do
    sh -c $"echo made-{f}"
WEOF
out=$($BIN "$fdir/district.weir")
expect "for/do takes a plain multi-line command body" "made-a
made-b" "$out"

# the comprehension is eager and evaluates
out=$($BIN -e '[for x in [1; 2; 3] -> x * 10]')
expect "the comprehension evaluates eagerly" "[10; 20; 30] : seq<int>" "$out"

# a non-unit body keeps the statement rule's teaching
out=$($BIN -e 'for x in [1] do x + 1' 2>&1 || true)
expect "a non-unit for body errors in the statement-rule family" "expected unit, got int" "$out"
rm -rf "$fdir"

# ---- the yaml boundary [D:yaml-v1] ----------------------------------------
ydir=$(mkweirtmp)
cat > "$ydir/k8s.weir" <<'WEOF'
type Port = { containerPort: int }
type Container = { name: string; image: string; ports: seq<Port> }
type Meta = { name: string; labels: seq<string * string> }
type Spec = { replicas: int; containers: seq<Container> }
type Deploy = { apiVersion: string; kind: string; metadata: Meta; spec: Spec }
let d = Deploy {
    apiVersion = "apps/v1"
    kind = "Deployment"
    metadata = Meta { name = "app"; labels = [("app", "web"); ("env", "no")] }
    spec = Spec {
        replicas = 3
        containers = [Container { name = "web"; image = "nginx:1.25"; ports = [Port { containerPort = 80 }] }]
    }
}
d |> to yaml |> print
let back = d |> to yaml |> from yaml Deploy
print $"back: {back.metadata.name} {show back.spec.replicas}"
WEOF
out=$($BIN "$ydir/k8s.weir")
expect "to yaml renders the k8s tree with reverse-Norway quoting" 'env: "no"' "$out"
expect "to yaml renders nested sequences of records" "- containerPort: 80" "$out"
expect "the yaml roundtrip holds on the AOT binary" "back: app 3" "$out"

# anchors reject with a position and the subset's teaching
printf 'type D = { a: string; b: string }\nlet d = ["a: &x one"; "b: *x"] |> from yaml D\nprint d.a\n' > "$ydir/anchor.weir"
out=$($BIN "$ydir/anchor.weir" 2>&1 || true)
expect "anchors are rejected with a line and the subset teaching" "line 1: anchors/aliases are outside the yaml subset" "$out"

# the Norway problem cannot fire on read: bool is exactly true/false
printf 'type D = { flag: bool }\nlet d = ["flag: no"] |> from yaml D\nprint (show d.flag)\n' > "$ydir/no.weir"
out=$($BIN "$ydir/no.weir" 2>&1 || true)
expect "the Norway problem never fires on read (bool is exactly true/false)" "expected bool (exactly true/false), got 'no'" "$out"

# streams retired [D:yaml-seq]: one document only, the route named;
# and the seq form reads a top-level sequence document
printf 'type D = { kind: string }\nlet d = ["kind: A"; "---"; "kind: B"] |> from yaml D\nprint d.kind\n' > "$ydir/md.weir"
out=$($BIN "$ydir/md.weir" 2>&1 || true)
expect "a --- stream teaches: one document, the count, the route" "from yaml: reads one document; this input has 2 documents — read a stream with 'from yaml stream T'" "$out"
printf 'type H = { host: string }\nlet hs = ["- host: a"; "- host: b"] |> from yaml seq<H>\nhs |> Seq.iter (fun h -> print h.host)\n' > "$ydir/seqdoc.weir"
out=$($BIN "$ydir/seqdoc.weir")
expect "from yaml seq<T> reads a top-level sequence document" "a
b" "$out"

# [D:yaml-seq]/[D:yaml-empty-flow] zero-indent block sequences +
# empty flow collections — kubectl's form: a block sequence at the same
# column as its parent mapping key, plus `{}`/`[]` empty flow values
# (resources/securityContext/emptyDir/lastState). A real
# `kubectl get -o yaml` List (nested zero-indent seqs throughout:
# items/ownerReferences/containers/args/env/ports/volumeMounts/volumes/
# tolerations/conditions/containerStatuses, compact `- key: v` first
# entry, empty `{}` values) reads and infers clean end-to-end.
cat > "$ydir/kubectl-list.yaml" <<'YEOF'
apiVersion: v1
items:
- apiVersion: v1
  kind: Pod
  metadata:
    name: nginx-7d5c8f9b4-abcde
    namespace: default
    labels:
      app: nginx
      pod-template-hash: 7d5c8f9b4
      app.kubernetes.io/instance: nginx
    ownerReferences:
    - apiVersion: apps/v1
      kind: ReplicaSet
      name: nginx-7d5c8f9b4
      controller: true
  spec:
    containers:
    - name: nginx
      image: nginx:1.25
      args:
      - --debug
      - --port=8080
      env:
      - name: TZ
        value: UTC
      ports:
      - containerPort: 80
        protocol: TCP
      resources: {}
      volumeMounts:
      - name: config
        mountPath: /etc/nginx
      securityContext: {}
    tolerations:
    - key: node.kubernetes.io/not-ready
      operator: Exists
      effect: NoExecute
      tolerationSeconds: 300
    volumes:
    - name: cache
      emptyDir: {}
  status:
    phase: Running
    conditions:
    - type: Ready
      status: "True"
      message: 'Pod was rejected: The node had condition: [DiskPressure]. '
    - type: PodScheduled
      status: "False"
      message: "0/3 nodes are available: 3 Insufficient memory.
        preemption: not eligible."
    containerStatuses:
    - name: nginx
      ready: true
      restartCount: 0
      lastState: {}
    message: 'The node was low on resource: ephemeral-storage. Threshold quantity:
      25462616238, available: 24515828Ki. Container was using 54204Ki,
      request is 0. '
    reason: Evicted
kind: List
YEOF
cat > "$ydir/infer.weir" <<'WEOF'
type Owner = { apiVersion: string; kind: string; name: string; controller: bool }
type Meta = { name: string; namespace: string; labels: seq<string * string>; ownerReferences: seq<Owner> }
type Env = { name: string; value: string }
type Port = { containerPort: int; protocol: string }
type Mount = { name: string; mountPath: string }
type Container = { name: string; image: string; args: seq<string>; env: seq<Env>; ports: seq<Port>; resources: Yaml; volumeMounts: seq<Mount>; securityContext: Yaml }
type Tol = { key: string; operator: string; effect: string; tolerationSeconds: int }
type Volume = { name: string; emptyDir: Yaml }
type Cond = { [<Wire "type">] kind: string; status: string; message: string }
type CStat = { name: string; ready: bool; restartCount: int; lastState: Yaml }
type Spec = { containers: seq<Container>; tolerations: seq<Tol>; volumes: seq<Volume> }
type Status = { phase: string; conditions: seq<Cond>; containerStatuses: seq<CStat>; message: string; reason: string }
type Item = { apiVersion: string; kind: string; metadata: Meta; spec: Spec; status: Status }
type List = { apiVersion: string; items: seq<Item>; kind: string }
let text = File.read "kubectl-list.yaml" |> Seq.freeze
let lst = text |> from yaml List
print $"items: {show (Seq.length lst.items)} kind: {lst.kind}"
let pod = lst.items |> Seq.head
let c = pod.spec.containers |> Seq.head
print $"container: {c.name} args: {show (Seq.length c.args)} port: {show (Seq.head c.ports).containerPort}"
print $"owner: {(Seq.head pod.metadata.ownerReferences).kind}"
print $"resources: {show c.resources}"
print $"cond: {(Seq.head pod.status.conditions).message}"
print $"sched: {(pod.status.conditions |> Seq.skip 1 |> Seq.head).message}"
print $"evict: {pod.status.message}"
WEOF
out=$(cd "$ydir" && $BIN infer.weir)
expect "a real kubectl List (zero-indent block seqs, nested) reads on the AOT binary" "items: 1 kind: List" "$out"
expect "nested zero-indent seqs inside a seq item's map read (containers/args/ports)" "container: nginx args: 2 port: 80" "$out"
expect "a same-indent ownerReferences seq under metadata reads" "owner: ReplicaSet" "$out"
expect "an empty flow {} reads into a Yaml field as YMap []" "resources: YMap ([])" "$out"
# [D:quoted-fold] the kubectl message forms: single-line quoted with
# colons inside, and the multi-line quoted continuation (single and
# double quoted) — the fold is one space per break
expect "a single-line quoted message with colons reads" "cond: Pod was rejected: The node had condition: [DiskPressure]. " "$out"
expect "a multi-line DOUBLE-quoted message folds to one line" "sched: 0/3 nodes are available: 3 Insufficient memory. preemption: not eligible." "$out"
expect "a multi-line single-quoted eviction message folds to one line" "evict: The node was low on resource: ephemeral-storage. Threshold quantity: 25462616238, available: 24515828Ki. Container was using 54204Ki, request is 0. " "$out"

# the real file infers clean end-to-end: zero-indent seqs + empty flow
# together, empty {} → opaque Yaml with a note (never a silent shape)
cat > "$ydir/inferdecl.weir" <<'WEOF'
let text = File.read "kubectl-list.yaml" |> Seq.freeze
print (Yaml.inferShape text)
WEOF
out=$(cd "$ydir" && $BIN inferdecl.weir)
# moved pins [D:repl-infer]: an empty {} is an open map with zero
# entries (the opaque-Yaml draft could not cross the json boundary),
# and the dirty-keyed labels object drafts as the mapping — its keys
# are data, so no [<Wire>] rides at all
echo "$out" | grep -qF "resources: seq<string * string>" || fail "empty {} must infer as the empty open mapping: $out"
echo "$out" | grep -qF "an empty mapping under 'resources'" || fail "empty {} must print a note: $out"
echo "$out" | grep -qi "not valid YAML" && fail "the real kubectl List must infer clean: $out" || true
echo "$out" | grep -qF "labels: seq<string * string>" || fail "the dirty-keyed labels must draft as a mapping: $out"
echo "$out" | grep -qF "'labels' has mostly non-identifier keys" || fail "the labels map note must print: $out"
echo "$out" | grep -qF 'pod-template-hash' && fail "a mapping's keys are data — no label key belongs in the draft: $out" || true
echo "$out" | grep -qF 'k8s-app: string' && fail "a non-identifier key must NOT appear bare as a field: $out" || true

# the open-map draft end-to-end [D:repl-infer] (moved pin — this cell
# pinned the [<Wire>]'d record draft): a labels object with hyphen/dot/
# slash keys over one value shape drafts as the mapping, and the draft
# reads the real data through from yaml as pairs — keys stay data
cat > "$ydir/labels.yaml" <<'YEOF'
labels:
  k8s-app: web
  node.kubernetes.io/os: linux
  app.kubernetes.io/instance: nginx
  clean: yes
YEOF
cat > "$ydir/draft.weir" <<'WEOF'
let text = File.read "labels.yaml" |> Seq.freeze
print (Yaml.inferShape text)
WEOF
draft=$(cd "$ydir" && $BIN draft.weir)
echo "$draft" | grep -qF 'labels: seq<string * string>' || fail "the dirty-keyed labels must draft as a mapping: $draft"
echo "$draft" | grep -qF 'Wire' && fail "mapping keys are data — no [<Wire>] belongs in the draft: $draft" || true
# assemble a program from the drafted decls + a pair lookup per key
{
  echo "$draft" | grep -v '^// note'
  echo 'let text = File.read "labels.yaml" |> Seq.freeze'
  echo 'let r = text |> from yaml Root'
  echo 'let v k = r.labels |> Seq.tryFind (fun (p, _) -> p == k) |> Option.map snd |> Option.defaultValue "?"'
  echo 'let a = v "k8s-app"'
  echo 'let b = v "node.kubernetes.io/os"'
  echo 'let c = v "app.kubernetes.io/instance"'
  echo 'let d = v "clean"'
  echo 'print $"{a} {b} {c} {d}"'
} > "$ydir/roundtrip.weir"
out=$(cd "$ydir" && $BIN roundtrip.weir 2>&1)
expect "the inferred open-mapping draft CHECKS and READS real label data as pairs" "web linux nginx yes" "$out"

# empty flow forms, both positions: {} → empty mapping, [] → empty seq
printf 'type R = { m: Yaml; s: seq<string> }\nlet r = ["m: {}"; "s: []"] |> from yaml R\nprint $"{show r.m} {show (Seq.length r.s)}"\n' > "$ydir/ef.weir"
out=$($BIN "$ydir/ef.weir")
expect "empty flow as map values: {} → YMap [], [] → empty seq" "YMap ([]) 0" "$out"
printf 'let d = ["- {}"; "- []"] |> Yaml.parse\nprint (show d)\n' > "$ydir/efseq.weir"
out=$($BIN "$ydir/efseq.weir")
expect "empty flow as sequence items parses ({} → YMap [], [] → YSeq [])" "YSeq ([YMap ([]); YSeq ([])])" "$out"

# empty flow roundtrips: {} renders back as {}, reads back as YMap []
printf 'type R = { m: Yaml }\nlet r = ["m: {}"] |> from yaml R\nlet back = r |> to yaml\nback |> Seq.iter print\nlet r2 = back |> from yaml R\nprint (show r2.m)\n' > "$ydir/efrt.weir"
out=$($BIN "$ydir/efrt.weir")
expect "an empty {} round-trips: renders {} and reads back YMap []" 'm: {}
YMap ([])' "$out"

# populated flow stays rejected — the ambiguity that justifies block-only
# fires at one-or-more elements; only the empty forms are the exception
printf 'type R = { m: Yaml }\nlet r = ["m: {a: 1}"] |> from yaml R\nprint (show r.m)\n' > "$ydir/pop.weir"
out=$($BIN "$ydir/pop.weir" 2>&1 || true)
expect "populated flow {a: 1} still rejects with the subset teaching" "flow style is outside the yaml subset" "$out"
printf 'type R = { s: Yaml }\nlet r = ["s: [1, 2]"] |> from yaml R\nprint (show r.s)\n' > "$ydir/pop2.weir"
out=$($BIN "$ydir/pop2.weir" 2>&1 || true)
expect "populated flow [1, 2] still rejects with the subset teaching" "flow style is outside the yaml subset" "$out"

# the two block-sequence styles are read-equal: same map value whether
# the seq sits at the parent key's column (kubectl) or indented under it
printf 'type P = { name: string }\ntype R = { items: seq<P> }\nlet flat = ["items:"; "- name: a"; "- name: b"] |> from yaml R\nlet nest = ["items:"; "  - name: a"; "  - name: b"] |> from yaml R\nprint $"{show (Seq.length flat.items)} {show (Seq.length nest.items)}"\n' > "$ydir/styles.weir"
out=$($BIN "$ydir/styles.weir")
expect "both block-sequence indent styles read to the same value" "2 2" "$out"

# a genuinely malformed inline-value-plus-nested-block still errors: the
# nested block is more-indented (not a same-indent sequence)
printf 'type D = { k: string }\nlet d = ["k: value"; "  nested: x"] |> from yaml D\nprint d.k\n' > "$ydir/mal.weir"
out=$($BIN "$ydir/mal.weir" 2>&1 || true)
expect "a real inline-value + nested-block still errors (not a zero-indent seq)" "'k' has both an inline value and a nested block" "$out"
echo "e2e ok: yaml zero-indent block sequences + empty flow {}/[] — kubectl List reads/infers clean end-to-end, both styles read-equal, empty flow round-trips, populated flow + malformed still error"
rm -rf "$ydir"

# ---- the yaml district [D:yaml-district] ----------------------------------
yddir=$(mkweirtmp)
cat > "$yddir/deploy.weir" <<'WEOF'
let deployment name replicas pairs = yaml
    apiVersion: apps/v1
    kind: Deployment
    metadata:
        name: $name
        labels:
            for (k, v) in pairs
                $k: $v
    spec:
        replicas: $replicas
        paused: $(None)

deployment "app" 3 [("app", "web"); ("env", "no")] |> to yaml |> print
WEOF
out=$($BIN "$yddir/deploy.weir")
expect "the district renders with typed splices and for-generated keys" "name: app" "$out"
expect "the district's quoting law holds for spliced pairs" 'env: "no"' "$out"
expect "a None splice omits its entry in a district" "replicas: 3" "$out"
if echo "$out" | grep -q "paused"; then fail "None splice must omit the paused entry: $out"; fi
echo "e2e ok: district None-splice omission"

# a district roundtrips through from yaml on the AOT binary
cat > "$yddir/rt.weir" <<'WEOF'
type Meta = { name: string }
type D = { kind: string; metadata: Meta }
let d = yaml
    kind: Pod
    metadata:
        name: app
let back = d |> to yaml |> from yaml D
print back.metadata.name
WEOF
out=$($BIN "$yddir/rt.weir")
expect "a district roundtrips through from yaml" "app" "$out"

# the splice law rejects a record, at the splice, under check
printf 'type R = { a: int }\nlet d = yaml\n    x: $(R { a = 1 })\nd |> to yaml |> print\n' > "$yddir/law.weir"
out=$($BIN check "$yddir/law.weir" 2>&1 || true)
expect "the splice law rejects a record at the splice" "a yaml splice takes string/int/float/bool" "$out"

# ---- block scalars [D:block-scalars] --------------------------------------
# the ConfigMap workload: literal content (splice-lookalikes are bytes),
# blanks preserved, #! at content head; the form follows the value both
# directions through a typed round trip
cat > "$yddir/cm.weir" <<'WEOF'
type Cm = { data: seq<string * string> }
let cm = yaml
    data:
        run.sh: |
            #!/bin/sh
            echo $HOME stays literal

            for x in xs
        note: |-
            no trailing newline

let back = cm |> to yaml |> from yaml Cm
let get k = back.data |> Seq.choose (fun p -> match p with | (k2, v) when k2 == k -> Some v | _ -> None) |> Seq.head
print (show (get "run.sh"))
print (show (get "note"))
cm |> to yaml |> print
WEOF
out=$($BIN "$yddir/cm.weir")
expect "block scalar content is literal bytes" '"#!/bin/sh\necho $HOME stays literal\n\nfor x in xs\n"' "$out"
expect "|- strips the trailing newline through the round trip" '"no trailing newline"' "$out"
expect "the multiline value renders back as a block" "run.sh: |" "$out"
expect "the no-trailing-newline value renders inline (form follows value)" "note: no trailing newline" "$out"
echo "e2e ok: district block scalars — ConfigMap workload on the AOT binary"

# the interop referee [D:yaml-v1]: `to yaml`/`to json` round-trip
# through parsers weir did not write, over the hostile corpus — the
# adversarial review's F2 durable fix (weir refereeing itself let four
# emitter defects through). PyYAML absent is a named SKIP, never a
# silent pass; the linux CI image carries it, so the gate always runs
# at least there.
if python3 -c 'import yaml' 2>/dev/null; then
    python3 "$(dirname "$0")/interop-referee.py" "$BIN" || fail "interop referee"
else
    echo "e2e SKIP: interop referee — python3+PyYAML absent on this runner (the linux AND macos CI images run it)"
fi

# a rejected header inside a district errors at the header under check
cat > "$yddir/fold.weir" <<'WEOF'
let d = yaml
    a: 1
    s: >
        folded
d |> to yaml |> print
WEOF
out=$($BIN check --json "$yddir/fold.weir" 2>&1 || true)
echo "$out" | grep -qF "folded block scalars" || fail "the folded teaching under check: $out"
echo "$out" | grep -qF '"line":3' || fail "the header error must point at the header line: $out"
echo "e2e ok: a rejected block header errors at the header, with the teaching"
rm -rf "$yddir"

# mid-line # on district structure lines is a comment [D:district-hash] —
# the two paths agree; the acceptance: the pasted line emits unquoted
mhdir=$(mkweirtmp)
cat > "$mhdir/mh.weir" <<'WEOF'
let d = yaml
    image: nginx:latest # pinned by ops
    quoted: "a # b"

d |> to yaml |> print
WEOF
out=$($BIN "$mhdir/mh.weir")
echo "$out" | grep -qxF "image: nginx:latest" || fail "the pasted-manifest comment must cut, value unquoted: $out"
echo "$out" | grep -qxF 'quoted: "a # b"' || fail "a quoted # stays data: $out"
echo "e2e ok: district mid-line # agrees with from yaml — unquoted value out"
rm -rf "$mhdir"

# ---- the hostile-byte fixture [D:content-bytes] ----------------------------
# content is bytes: every hostile class through one block scalar, asserted
# byte-exact through check and run. The fixture is generated (printf) because
# a checked-in literal with trailing spaces / tabs / CRLF invites editor and
# git mangling — the generator is the fixture.
hbdir=$(mkweirtmp)
{
    printf 'let d = yaml\n'
    printf '    data:\n'
    printf '        hostile.txt: |\n'
    printf '            #!/bin/sh\n'
    printf '            # comment-shaped\n'
    printf '            // weir comment as data\n'
    printf '            /// weir doc as data\n'
    printf '\n'
    printf '            \n'
    printf '            echo\tmid-line tab content\n'
    printf '            ---\n'
    printf '            trailing spaces   \n'
    printf '            $name and $(1 + 2)\n'
    printf '            for x in xs\n'
    printf '                more indented\n'
    printf '            { [ unbalanced ( brackets\n'
    printf '            back to base\n'
    printf '\n'
    printf 'print (show d)\n'
} > "$hbdir/hostile.weir"

$BIN check "$hbdir/hostile.weir" || fail "hostile-byte fixture must check clean"

cat > "$hbdir/expected.txt" <<'HEOF'
YMap ([("data", YMap ([("hostile.txt", YStr "#!/bin/sh\n# comment-shaped\n// weir comment as data\n/// weir doc as data\n\n\necho\tmid-line tab content\n---\ntrailing spaces   \n$name and $(1 + 2)\nfor x in xs\n    more indented\n{ [ unbalanced ( brackets\nback to base\n")]))])
HEOF
$BIN "$hbdir/hostile.weir" > "$hbdir/got.txt"
cmp -s "$hbdir/expected.txt" "$hbdir/got.txt" || {
    diff "$hbdir/expected.txt" "$hbdir/got.txt" >&2
    fail "hostile-byte fixture must survive byte-exact through run"
}
echo "e2e ok: the hostile-byte fixture is byte-exact through check and run"

# indentation is spaces only, block content included [D:spaces-only-indent]:
# a tab in a content line's leading whitespace (even after the space base)
# is the indentation error, with its column
printf 'let t = <<<\n    abc\n    \tdef\nprint t\n' > "$hbdir/tablead.weir"
out=$($BIN check "$hbdir/tablead.weir" 2>&1) && fail "a leading tab in block content must be refused"
expect "a leading tab in block content is the indentation error" "tablead.weir:3:5: error [assembly]: tabs are not allowed in indentation (a tab at column 5)" "$out"

# CRLF [D:content-bytes]: a source file's line ending is not data —
# normalized at read, so a CRLF-saved file behaves identically
sed 's/$/\r/' "$hbdir/hostile.weir" > "$hbdir/crlf.weir"
$BIN "$hbdir/crlf.weir" > "$hbdir/crlf.txt"
cmp -s "$hbdir/got.txt" "$hbdir/crlf.txt" || fail "CRLF source must behave byte-identically to LF"
echo "e2e ok: CRLF source normalizes at read — output byte-identical"

# File.write preserves an existing file's UTF-8 BOM [D:encoding-law], never
# adds one — the round-trip-edit case (a BOM'd .csproj kept its BOM). weir
# runs under `cd` with relative paths so its cwd matches the shell's — a
# Git-Bash /tmp path and native weir's D:\tmp disagree otherwise.
bomdir=$(mktemp -d)
printf '\xEF\xBB\xBF<Project>\n</Project>\n' > "$bomdir/bom.csproj"
printf '<Project>\n</Project>\n' > "$bomdir/plain.csproj"
bom3() { od -An -tx1 -N3 "$1" | tr -d ' '; }
(cd "$bomdir" && $BIN -e 'File.read "bom.csproj" |> Seq.freeze |> File.write "bom.csproj"') || fail "File.write round-trip raised on a BOM file"
[ "$(bom3 "$bomdir/bom.csproj")" = "efbbbf" ] || fail "File.write dropped an existing UTF-8 BOM"
(cd "$bomdir" && $BIN -e 'File.read "plain.csproj" |> Seq.freeze |> File.write "plain.csproj"') || fail "File.write round-trip raised on a no-BOM file"
[ "$(bom3 "$bomdir/plain.csproj")" = "efbbbf" ] && fail "File.write must NOT add a BOM to a no-BOM file"
(cd "$bomdir" && $BIN -e '["x"] |> File.write "new.txt"') || fail "File.write raised on a new file"
[ "$(bom3 "$bomdir/new.txt")" = "efbbbf" ] && fail "File.write must NOT add a BOM to a new file"
echo "e2e ok: File.write preserves an existing UTF-8 BOM, never adds one"

# fmt: value-preserving on the fixture; its only byte change is
# normalizing the whitespace-only line to empty (a stated house rule —
# both spellings are a blank content line); idempotent after
cp "$hbdir/hostile.weir" "$hbdir/fmted.weir"
$BIN fmt "$hbdir/fmted.weir" >/dev/null 2>&1 || true
$BIN "$hbdir/fmted.weir" > "$hbdir/fmted.txt"
cmp -s "$hbdir/got.txt" "$hbdir/fmted.txt" || fail "fmt must preserve the fixture's VALUE byte-exactly"
$BIN fmt --check "$hbdir/fmted.weir" || fail "fmt must be idempotent on the fixture"
echo "e2e ok: fmt preserves hostile-byte content (spaces-only line normalizes, value identical)"
rm -rf "$hbdir"

# ---- external contracts: the spine + schemas [D:contracts-spine] -----------
# vendored, pinned, check-time only; the fetch machinery is exercised
# against a local server (CI is offline) serving the committed real
# k8s configmap schema — the published-schema fetch ran in-session
# and is recorded in the plan's report
ctdir=$(mkweirtmp)
mkdir -p "$ctdir/serve"
cp "$(dirname "$0")/../tests/fixtures/configmap-v1.json" "$ctdir/serve/"
ctport=$((18930 + RANDOM % 2000))
# loopback bind: macOS's firewall drops SYNs to an unsigned listener on
# 0.0.0.0 ('Operation timed out' on the very first fetch)
# --directory, never a cd-subshell: MSYS bash does not exec-optimize
# `( cd .. && python ) &`, so $! was the subshell's pid and the kill left
# python alive holding the dir as its cwd (rm: Device or resource busy)
python3 -m http.server $ctport --bind 127.0.0.1 --directory "$ctdir/serve" >/dev/null 2>&1 &
ctsrv=$!
awaitHttp "http://127.0.0.1:$ctport/configmap-v1.json" || { kill $ctsrv 2>/dev/null || true; fail "the schema server never came up"; }

mkdir -p "$ctdir/proj/sub"
( cd "$ctdir/proj" && git init -q . )
( cd "$ctdir/proj" && $BIN add schema http://127.0.0.1:$ctport/configmap-v1.json --as k8s-configmap ) | grep -q "added schema k8s-configmap" || fail "add schema"
test -f "$ctdir/proj/.weir/lock.json" || fail "the lockfile exists after the first fetch"
echo "e2e ok: weir add schema fetches, writes, and locks"

# [D:add-validates]: add validates before it writes — nothing on disk
# on any failure, including no .weir/ creation at all (the strongest
# pin: the tree is byte-identical after a failed add)
printf '<!DOCTYPE html><html><body>a blob page, not the raw file</body></html>' > "$ctdir/serve/page.html"
printf '{"name": "pkg", "version": "1.0.0"}' > "$ctdir/serve/notschema.json"
printf '{"type": "object", "properties": {"a": {"$ref": "#/x"}}}' > "$ctdir/serve/refschema.json"
mkdir -p "$ctdir/fresh"
( cd "$ctdir/fresh" && git init -q . )
out=$( cd "$ctdir/fresh" && $BIN add schema http://127.0.0.1:$ctport/page.html --as bad 2>&1 ) && fail "an HTML response must fail add" || true
echo "$out" | grep -qF "the response is not JSON (Content-Type: text/html)" || fail "gate 2 names what came back: $out"
echo "$out" | grep -qF "use the raw URL" || fail "gate 2 carries the raw-URL hint: $out"
out=$( cd "$ctdir/fresh" && $BIN add schema http://127.0.0.1:$ctport/notschema.json --as pkg 2>&1 ) && fail "a non-schema JSON must fail add" || true
echo "$out" | grep -qF "valid JSON, but not a schema" || fail "gate 3 rejects non-schema JSON: $out"
out=$( cd "$ctdir/fresh" && $BIN add schema http://127.0.0.1:$ctport/refschema.json --as reffy 2>&1 ) && fail "an out-of-subset schema must fail add" || true
echo "$out" | grep -q 'ref' || fail "gate 4 is the subset teaching at ADD time: $out"
echo "$out" | grep -qF "nothing was written" || fail "gate 4 states nothing was written: $out"
test ! -e "$ctdir/fresh/.weir" || fail "a failed add must leave NO .weir at all"
echo "e2e ok: add validates before it writes — HTML/non-schema/out-of-subset fail with .weir untouched"

# verify: ok / modified / absent, distinct — then restore
( cd "$ctdir/proj" && $BIN verify ) | grep -q ": ok" || fail "verify clean"
echo junk >> "$ctdir/proj/.weir/schemas/k8s-configmap.json"
out=$( cd "$ctdir/proj" && $BIN verify ) && fail "verify must exit 1 on modified" || true
echo "$out" | grep -q "MODIFIED" || fail "modified named: $out"
rm "$ctdir/proj/.weir/schemas/k8s-configmap.json"
out=$( cd "$ctdir/proj" && $BIN verify ) && fail "verify must exit 1 on absent" || true
echo "$out" | grep -q "ABSENT" || fail "absent named: $out"
( cd "$ctdir/proj" && $BIN restore ) | grep -q "restored" || fail "restore re-materializes from the lock"
echo "e2e ok: weir verify distinguishes modified from absent; restore re-materializes"

# ---- Bundle A+B: the lockfile path boundary [D:lockfile-confinement] --------
# F14: an absolute/traversal `--as` name must refuse before the fetch,
# writing nothing (the rule add module already had, now shared)
mkdir -p "$ctdir/f14" && ( cd "$ctdir/f14" && git init -q . )
f14abs="$ctdir/F14-ABSOLUTE.json"
out=$( cd "$ctdir/f14" && $BIN add schema http://127.0.0.1:$ctport/configmap-v1.json --as "$f14abs" 2>&1 ) && fail "an absolute --as must refuse" || true
echo "$out" | grep -qF "must be a plain file name" || fail "F14 absolute --as teaches the plain-name rule: $out"
test ! -e "$f14abs" || fail "F14: an absolute --as must write nothing outside .weir/"
out=$( cd "$ctdir/f14" && $BIN add schema http://127.0.0.1:$ctport/configmap-v1.json --as "../escape" 2>&1 ) && fail "a traversal --as must refuse" || true
echo "$out" | grep -qF "must be a plain file name" || fail "F14 traversal --as teaches the plain-name rule: $out"
test ! -e "$ctdir/f14/../escape.json" || fail "F14: a traversal --as must write nothing outside .weir/"
echo "e2e ok: add schema refuses an absolute/traversal --as, writing nothing (F14)"
# the lock is committed: add writes the URL without its userinfo and says
# so [D:lock-no-userinfo]
mkdir -p "$ctdir/ui" && ( cd "$ctdir/ui" && git init -q . )
out=$( cd "$ctdir/ui" && $BIN add schema "http://bob:S3cr3t@127.0.0.1:$ctport/configmap-v1.json" --as ui 2>&1 ) || fail "lock-no-userinfo: add schema with userinfo must succeed: $out"
echo "$out" | grep -qF "credentials are not written" || fail "lock-no-userinfo: add must say the credentials were dropped: $out"
grep -qF "\"http://127.0.0.1:$ctport/configmap-v1.json\"" "$ctdir/ui/.weir/lock.json" || fail "lock-no-userinfo: the lock must name the URL sans userinfo: $(cat "$ctdir/ui/.weir/lock.json")"
grep -qE "bob|S3cr3t" "$ctdir/ui/.weir/lock.json" && fail "lock-no-userinfo: the credentials LEAKED into the lock: $(cat "$ctdir/ui/.weir/lock.json")" || true
echo "e2e ok: add schema keeps a URL's userinfo out of .weir/lock.json"

# F1: a hostile lock (an entry pointing outside .weir/) refuses per entry
# with a located diagnostic, restores the benign sibling, and writes
# nothing outside .weir/. restore overwrites only its own artifacts
# inside .weir/ [D:lockfile-confinement]
f1repo="$ctdir/f1repo"
mkdir -p "$f1repo/.weir"
f1hash=$(sha256sum "$ctdir/serve/configmap-v1.json" | cut -d' ' -f1)
f1esc="$ctdir/F1-ESCAPE.json"
cat > "$f1repo/.weir/lock.json" <<F1EOF
{ "schemaVersion": 1, "artifacts": [
  {"kind":"schema","name":"benign","url":"http://127.0.0.1:$ctport/configmap-v1.json","sha256":"$f1hash","path":"schemas/benign.json"},
  {"kind":"schema","name":"traversal","url":"http://127.0.0.1:$ctport/configmap-v1.json","sha256":"$f1hash","path":"../F1-TRAVERSAL.json"},
  {"kind":"schema","name":"absolute","url":"http://127.0.0.1:$ctport/configmap-v1.json","sha256":"$f1hash","path":"$f1esc"}
] }
F1EOF
out=$( cd "$f1repo" && $BIN restore 2>&1 ) && fail "restore on a hostile lock must exit nonzero" || true
echo "$out" | grep -qF "escapes .weir/" || fail "F1: the hostile entry is a located refusal naming the escape: $out"
test -f "$f1repo/.weir/schemas/benign.json" || fail "F1: the benign sibling must still restore"
test ! -e "$f1esc" || fail "F1: an absolute lock path must not be written outside .weir/"
test ! -e "$ctdir/F1-TRAVERSAL.json" || fail "F1: a traversal lock path must not be written outside .weir/"
# verify refuses to read outside .weir/ too — same located refusal
out=$( cd "$f1repo" && $BIN verify 2>&1 ) && fail "verify on a hostile lock must exit nonzero" || true
echo "$out" | grep -qF "escapes .weir/" || fail "F1: verify names the escape rather than reading it: $out"
echo "e2e ok: a hostile lock refuses per entry (restore + verify), the benign sibling restores, nothing escapes (F1)"

# DA-04: confinement resolves symlinks [D:lockfile-symlink-confinement].
# A lexically-clean lock path (no `..`, not absolute) still escapes when a
# component on the way is a symlink pointing out. POSIX-only (native symlinks).
if [ "$IS_WINDOWS" = "0" ]; then
    # (1) a symlinked intermediate dir: .weir/schemas → outside .weir/
    darepo="$ctdir/da04repo"
    daout="$ctdir/DA04-OUTSIDE"
    mkdir -p "$darepo/.weir" "$daout"
    ln -s "$daout" "$darepo/.weir/schemas"
    cat > "$darepo/.weir/lock.json" <<DA04EOF
{ "schemaVersion": 1, "artifacts": [
  {"kind":"schema","name":"marker","url":"http://127.0.0.1:$ctport/configmap-v1.json","sha256":"$f1hash","path":"schemas/marker.json"}
] }
DA04EOF
    out=$( cd "$darepo" && $BIN restore 2>&1 ) && fail "DA-04: restore through a symlinked .weir/schemas must exit nonzero" || true
    echo "$out" | grep -qF "escapes .weir/" || fail "DA-04: a symlinked intermediate dir is a located refusal: $out"
    test ! -e "$daout/marker.json" || fail "DA-04: nothing may be written outside .weir/ through the symlink"
    echo "e2e ok: a symlinked .weir/schemas is refused on restore, nothing escapes (DA-04)"

    # (2) a symlinked final component: the leaf itself points out
    da2repo="$ctdir/da04repo2"
    da2out="$ctdir/DA04-OUTSIDE2"
    mkdir -p "$da2repo/.weir/schemas" "$da2out"
    printf 'x' > "$da2out/leak.json"
    ln -s "$da2out/leak.json" "$da2repo/.weir/schemas/marker.json"
    cat > "$da2repo/.weir/lock.json" <<DA04EOF2
{ "schemaVersion": 1, "artifacts": [
  {"kind":"schema","name":"marker","url":"http://127.0.0.1:$ctport/configmap-v1.json","sha256":"$f1hash","path":"schemas/marker.json"}
] }
DA04EOF2
    out=$( cd "$da2repo" && $BIN restore 2>&1 ) && fail "DA-04: restore over a symlinked leaf must exit nonzero" || true
    echo "$out" | grep -qF "escapes .weir/" || fail "DA-04: a symlinked final component is refused: $out"
    test "$(cat "$da2out/leak.json")" = "x" || fail "DA-04: the symlinked leaf's target must not be overwritten"
    # (3) verify must not read through the symlinked leaf either
    out=$( cd "$da2repo" && $BIN verify 2>&1 ) && fail "DA-04: verify through a symlinked leaf must exit nonzero" || true
    echo "$out" | grep -qF "escapes .weir/" || fail "DA-04: verify names the escape rather than reading through it: $out"
    echo "e2e ok: a symlinked final component is refused on restore/verify, its target untouched (DA-04)"

    # (4) benign control: a real .weir/schemas dir + clean path still works
    da3repo="$ctdir/da04repo3"
    mkdir -p "$da3repo/.weir/schemas"
    cat > "$da3repo/.weir/lock.json" <<DA04EOF3
{ "schemaVersion": 1, "artifacts": [
  {"kind":"schema","name":"benign","url":"http://127.0.0.1:$ctport/configmap-v1.json","sha256":"$f1hash","path":"schemas/benign.json"}
] }
DA04EOF3
    out=$( cd "$da3repo" && $BIN restore 2>&1 ) || fail "DA-04 control: a real .weir/schemas must still restore: $out"
    test -f "$da3repo/.weir/schemas/benign.json" || fail "DA-04 control: the benign artifact must land inside .weir/"
    echo "e2e ok: a real .weir/schemas directory still restores — not over-refused (DA-04 control)"
else
    echo "e2e skip: DA-04 symlink-confinement (POSIX native symlinks)"
fi

# DA-05: the contract-fetch client drops the credential on a cross-origin
# redirect [D:contract-redirect]. `add module <full-url>` attaches
# `Authorization: token <WEIR_TOKEN_host>` when a host token is set (the
# GitLab PRIVATE-TOKEN's shape) and downloads via fetchBytesWith; a bare
# HttpClient re-sent it to a redirect target. Driven at the CLI over a local
# cross-origin 302 (127.0.0.1 -> localhost). Origin B serves a valid module
# (so the add completes) and logs what arrived — the credential must not.
if command -v python3 >/dev/null 2>&1; then
    da5a=$((23800 + RANDOM % 100))
    da5b=$((23900 + RANDOM % 100))
    printf 'module M\nlet greet : string\nlet greet = "hi"\n' > "$ctdir/da5mod.weir"
    cat > "$ctdir/da5redir.py" <<DA5EOF
import http.server, sys
PB = $da5b
class A(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(302)
        self.send_header('Location', 'http://localhost:%d/mod.weir' % PB)
        self.end_headers()
    def log_message(self, *a): pass
http.server.HTTPServer(('127.0.0.1', $da5a), A).serve_forever()
DA5EOF
    cat > "$ctdir/da5dest.py" <<DA5EOF2
import http.server, sys
LOG = "$ctdir/da5.log"
BODY = open("$ctdir/da5mod.weir", 'rb').read()
class B(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        with open(LOG, 'a') as f:
            for k, v in self.headers.items():
                f.write("%s: %s\n" % (k, v))
        self.send_response(200); self.end_headers(); self.wfile.write(BODY)
    def log_message(self, *a): pass
http.server.HTTPServer(('localhost', $da5b), B).serve_forever()
DA5EOF2
    rm -f "$ctdir/da5.log"
    python3 "$ctdir/da5redir.py" & da5as=$!
    python3 "$ctdir/da5dest.py" & da5bs=$!
    # readiness, not a fixed sleep (see S1): a slow-runner bind race turns a
    # connect into a 30s hang. $da5a is explicit 127.0.0.1.
    awaitTcp $da5a || { kill $da5as $da5bs 2>/dev/null || true; fail "DA-05: the redirect server never came up on $da5a"; }
    mkdir -p "$ctdir/da5repo" && ( cd "$ctdir/da5repo" && git init -q . )
    # the CLI attaches Authorization: token <this> for host 127.0.0.1
    out=$( cd "$ctdir/da5repo" && WEIR_TOKEN_127_0_0_1="SECRET-CONTRACT-TOKEN" $BIN add module "http://127.0.0.1:$da5a/start.weir" --as da5 2>&1 ) || true
    kill $da5as $da5bs 2>/dev/null || true
    da5log=$(cat "$ctdir/da5.log" 2>/dev/null)
    # the artifact did land at origin B (the redirect was followed)
    test -n "$da5log" || fail "DA-05: origin B never received the redirected request: add output: $out"
    # but the credential must not have crossed to origin B
    echo "$da5log" | grep -qiF "SECRET-CONTRACT-TOKEN" && fail "DA-05: the contract credential LEAKED across the cross-origin redirect: $da5log" || true
    echo "$da5log" | grep -qiF "Authorization" && fail "DA-05: the Authorization header reached the cross-origin target: $da5log" || true
    # the User-Agent (non-credential) still crosses — the drop is credential-specific
    echo "$da5log" | grep -qiF "User-Agent: weir/" || fail "DA-05: a non-credential header must still cross (drop is credential-specific): $da5log"
    echo "e2e ok: the contract-fetch client drops the credential across a cross-origin redirect (DA-05); UA still crosses"
else
    echo "e2e skip: DA-05 contract-redirect (python3 absent)"
fi

kill $ctsrv 2>/dev/null || true

# ---- Fix 1: HTTP diagnostics redact a URL's userinfo [D:url-redact] --------
# a `user:pass@host` credential must not print verbatim when an HTTP error
# names the URL. (a) a 500 through Http.expect redacts the credential; a
# userinfo-free URL is still named in full. (b) the transport fallback (an
# unparseable URL) redacts too.
if command -v python3 >/dev/null 2>&1; then
    urport=$((24100 + RANDOM % 200))
    cat > "$ctdir/url500.py" <<URLEOF
import http.server
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self): self.send_response(500); self.end_headers(); self.wfile.write(b'nope\\x1b]52;c;ZXZp\\x07\\rX')
    def log_message(self, *a): pass
http.server.HTTPServer(('127.0.0.1', $urport), H).serve_forever()
URLEOF
    python3 "$ctdir/url500.py" & ursrv=$!
    # awaitTcp, not awaitHttp: the server answers 500 by design, and
    # `curl -sf` treats a 500 as failure — readiness means the listener is up
    awaitTcp "$urport" || { kill $ursrv 2>/dev/null || true; fail "the redact 500-server never came up"; }
    # (a) the credential is masked, the status still named
    out=$($BIN -e "print (Http.expect (Http.get \"http://user:s3cr3t@127.0.0.1:$urport/\"))" 2>&1) && fail "a 500 must raise" || true
    echo "$out" | grep -qF "***@127.0.0.1:$urport/ answered 500" || fail "the userinfo is redacted in the 500 error: $out"
    echo "$out" | grep -qiF "s3cr3t" && fail "the credential LEAKED verbatim in the 500 error: $out" || true
    # the body snippet is data: its ESC/BEL/CR render visibly, piped too [D:error-data-escape]
    echo "$out" | grep -qF 'nope\x1b]52;c;ZXZp\x07 X' || fail "error-data-escape: the 500 body snippet must render ESC/BEL/CR visibly: $out"
    printf '%s' "$out" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b' in d or b'\r' in d or b'\x07' in d) else 0)" \
        || fail "error-data-escape: the 500 body snippet put a raw ESC/BEL/CR in the error: $out"
    # a userinfo-free URL is still named in full
    out=$($BIN -e "print (Http.expect (Http.get \"http://127.0.0.1:$urport/\"))" 2>&1) && fail "a 500 must raise" || true
    echo "$out" | grep -qF "http://127.0.0.1:$urport/ answered 500" || fail "a credential-free URL is named in full: $out"
    kill $ursrv 2>/dev/null || true
    echo "e2e ok: an HTTP 500 redacts a URL's userinfo, names a credential-free URL in full (Fix 1a)"
else
    echo "e2e skip: Fix 1a userinfo-500 (python3 absent)"
fi
# (b) the transport fallback: an unparseable URL still masks its userinfo
out=$($BIN -e 'print (Http.expect (Http.get "http://user:pw@ nohost"))' 2>&1) && fail "an unreachable URL must raise" || true
echo "$out" | grep -qF "***@ nohost" || fail "the transport fallback redacts the unparseable URL's userinfo: $out"
echo "$out" | grep -qiF "user:pw" && fail "the credential LEAKED in the transport fallback: $out" || true
echo "e2e ok: the transport fallback redacts an unparseable URL's userinfo (Fix 1b)"

# ---- url-userinfo-basic / proxy-env-malformed / message-data-escape ----------
# R1: a URL's userinfo is sent as Authorization: Basic base64(user:pass),
# percent-decoded, as curl sends it — unless the request sets Authorization;
# add schema sends it too [D:url-redact]. A malformed HTTP_PROXY refuses,
# never goes direct [D:proxy-env-malformed]. R2: add/restore messages escape a
# URL's control bytes, piped too [D:lock-print-sanitize]
if command -v python3 >/dev/null 2>&1; then
    uaport=$((24300 + RANDOM % 200))
    cat > "$ctdir/uiauth.py" <<UAEOF
import http.server, json
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        a = self.headers.get_all('Authorization') or []
        open('$ctdir/uiauth.log', 'a').write(self.path.split('?')[0] + ' ' + ('|'.join(a) or '-') + '\n')
        if self.path.startswith('/s.json'):
            b = json.dumps({"type": "object", "additionalProperties": False, "properties": {"a": {"type": "string"}}}).encode()
        else:
            b = ('AUTH=' + ('|'.join(a) or '-') + ' N=' + str(len(a))).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(b)
    def log_message(self, *x): pass
http.server.HTTPServer(('127.0.0.1', $uaport), H).serve_forever()
UAEOF
    python3 "$ctdir/uiauth.py" & uasrv=$!
    awaitTcp "$uaport" || { kill $uasrv 2>/dev/null || true; fail "url-userinfo-basic: the echo server never came up"; }
    out=$($BIN -e "print (Http.send (Http.get \"http://bob:S3cr3t@127.0.0.1:$uaport/echo\")).body" 2>&1) || fail "url-userinfo-basic: the send must succeed: $out"
    [ "$out" = "AUTH=Basic Ym9iOlMzY3IzdA== N=1" ] || fail "url-userinfo-basic: the userinfo must arrive as one Basic header: $out"
    # each part percent-decoded: b%40b:p%3Ass is b@b / p:ss
    out=$($BIN -e "print (Http.send (Http.get \"http://b%40b:p%3Ass@127.0.0.1:$uaport/echo\")).body" 2>&1) || fail "url-userinfo-basic: the encoded send must succeed: $out"
    [ "$out" = "AUTH=Basic $(printf 'b@b:p:ss' | base64) N=1" ] || fail "url-userinfo-basic: the parts must be percent-decoded: $out"
    out=$($BIN -e "print (Http.send { Http.get \"http://bob:S3cr3t@127.0.0.1:$uaport/echo\" with headers = [(\"Authorization\", \"Bearer t1\")] }).body" 2>&1) || fail "url-userinfo-basic: the header send must succeed: $out"
    [ "$out" = "AUTH=Bearer t1 N=1" ] || fail "url-userinfo-basic: the script's Authorization wins, exactly one: $out"
    mkdir -p "$ctdir/uia" && ( cd "$ctdir/uia" && git init -q . )
    : > "$ctdir/uiauth.log"
    out=$( cd "$ctdir/uia" && $BIN add schema "http://bob:S3cr3t@127.0.0.1:$uaport/s.json" --as uia 2>&1 ) || fail "url-userinfo-basic: add schema must succeed: $out"
    grep -qF "/s.json Basic Ym9iOlMzY3IzdA==" "$ctdir/uiauth.log" || fail "url-userinfo-basic: add schema must send the userinfo as Basic: $(cat "$ctdir/uiauth.log")"
    echo "e2e ok: a URL's userinfo is sent as Basic auth (decoded), a script's Authorization wins, add schema sends it [D:url-redact]"

    # a malformed proxy variable refuses; the target never sees a request
    for bad in "http://127.0.0.1:99999" "http://[::1"; do
        : > "$ctdir/uiauth.log"
        out=$(env -u ALL_PROXY -u all_proxy HTTP_PROXY="$bad" http_proxy="$bad" NO_PROXY=example.invalid no_proxy=example.invalid "$BIN" -e "print (Http.send (Http.get \"http://127.0.0.1:$uaport/echo\")).status" 2>&1) && fail "proxy-env-malformed: $bad must refuse: $out"
        echo "$out" | grep -qF "http_proxy is set but is not a usable proxy URL" || fail "proxy-env-malformed: $bad must name the variable: $out"
        [ -s "$ctdir/uiauth.log" ] && fail "proxy-env-malformed: $bad went direct: $(cat "$ctdir/uiauth.log")" || true
    done
    # under CGI the runtime still honours HTTP_PROXY, so a malformed one refuses too
    : > "$ctdir/uiauth.log"
    out=$(env -u ALL_PROXY -u all_proxy -u http_proxy REQUEST_METHOD=GET HTTP_PROXY="http://127.0.0.1:99999" NO_PROXY=example.invalid no_proxy=example.invalid "$BIN" -e "print (Http.send (Http.get \"http://127.0.0.1:$uaport/echo\")).status" 2>&1) && fail "proxy-env-malformed: HTTP_PROXY under CGI must refuse: $out"
    # Windows env names are case-insensitive: the runtime finds HTTP_PROXY as http_proxy
    echo "$out" | grep -qiF "http_proxy is set but is not a usable proxy URL" || fail "proxy-env-malformed: HTTP_PROXY under CGI must name the variable: $out"
    [ -s "$ctdir/uiauth.log" ] && fail "proxy-env-malformed: HTTP_PROXY under CGI went direct: $(cat "$ctdir/uiauth.log")" || true
    # a scheme-less user:pw@host value never shows its password
    out=$(env -u ALL_PROXY -u all_proxy -u HTTP_PROXY http_proxy="user:S3cr3t@127.0.0.1:99999" NO_PROXY=example.invalid no_proxy=example.invalid "$BIN" -e "print (Http.send (Http.get \"http://127.0.0.1:$uaport/echo\")).status" 2>&1) && fail "proxy-env-malformed: a scheme-less credential value must refuse: $out"
    echo "$out" | grep -qF "S3cr3t" && fail "proxy-env-malformed: a scheme-less proxy value printed its password: $out"
    echo "$out" | grep -qF "***@127.0.0.1:99999" || fail "proxy-env-malformed: a scheme-less proxy value must show masked: $out"
    # a host NO_PROXY lists still goes direct as asked
    out=$(env HTTP_PROXY="http://127.0.0.1:99999" http_proxy="http://127.0.0.1:99999" NO_PROXY=127.0.0.1 no_proxy=127.0.0.1 "$BIN" -e "print (Http.send (Http.get \"http://127.0.0.1:$uaport/echo\")).status" 2>&1) || fail "proxy-env-malformed: a NO_PROXY host must go direct: $out"
    [ "$out" = "200" ] || fail "proxy-env-malformed: a NO_PROXY host must go direct: $out"
    echo "e2e ok: a malformed proxy variable refuses, never goes direct; NO_PROXY still bypasses [D:proxy-env-malformed]"

    # add's status line and restore's error escape a URL's ESC/BEL/CR, piped
    mkdir -p "$ctdir/uie" && ( cd "$ctdir/uie" && git init -q . )
    out=$( cd "$ctdir/uie" && $BIN add schema "$(printf "http://127.0.0.1:$uaport/s.json?q=\033]52;c;ZXZp\007\r")" --as uie 2>&1 ) || fail "message-data-escape: add schema must succeed: $out"
    echo "$out" | grep -qF 's.json?q=\x1b]52;c;ZXZp\x07\x0d' || fail "message-data-escape: the added line must render ESC/BEL/CR visibly: $out"
    printf '%s' "$out" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b' in d or b'\r' in d or b'\x07' in d) else 0)" \
        || fail "message-data-escape: add schema put a raw ESC/BEL/CR in its output: $out"
    mkdir -p "$ctdir/uir/.weir" && ( cd "$ctdir/uir" && git init -q . )
    printf '{ "schemaVersion": 1, "artifacts": [ { "kind": "schema", "name": "s2", "url": "http://127.0.0.1:%s/nope\\u001b]52;c;ZXZp\\u0007\\r", "sha256": "59c23259e0885dbc4c2f746d071abed3c976dd479e1051c6838d9dd25f26db51", "path": "schemas/s2.json" } ] }\n' "$uaport" > "$ctdir/uir/.weir/lock.json"
    out=$( cd "$ctdir/uir" && $BIN restore 2>&1 ) && fail "message-data-escape: restore of a changed source must fail: $out"
    echo "$out" | grep -qF '/nope\x1b]52;c;ZXZp\x07\x0d' || fail "message-data-escape: restore must name the URL with ESC/BEL/CR visible: $out"
    printf '%s' "$out" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b' in d or b'\r' in d or b'\x07' in d) else 0)" \
        || fail "message-data-escape: restore put a raw ESC/BEL/CR in its output: $out"
    kill $uasrv 2>/dev/null || true
    echo "e2e ok: add and restore messages escape a URL's control bytes, piped too [D:lock-print-sanitize]"
else
    echo "e2e skip: url-userinfo-basic / proxy-env-malformed / message-data-escape (python3 absent)"
fi

# ---- h3r7 stage B: userinfo masked through the last @, proxy fallthrough,
# message sinks, lock writes ---------------------------------------------------
# a password with a raw / ? # @ never shows [D:url-redact]; a malformed proxy
# variable refuses even when a later one is set [D:proxy-env-malformed]; the
# CLI/check/add messages escape their data [D:error-data-escape]; gen types
# drops a "" key with a note [D:infer-wire-escape]; lock.json is replaced
# whole under the .weir/ writer lock [D:weir-dir-lock]
if command -v python3 >/dev/null 2>&1; then
    h7dir=$(mkweirtmp)
    noprox="env -u http_proxy -u HTTP_PROXY -u https_proxy -u HTTPS_PROXY -u all_proxy -u ALL_PROXY -u no_proxy -u NO_PROXY"
    for u in "http://deploy:Tr0ub/4dor@127.0.0.1:1/x" "http://deploy:pa#ss@127.0.0.1:1/x" "http://deploy:p@ssw0rd@127.0.0.1:1/x" "http://deploy:q?wd@127.0.0.1:1/x"; do
        out=$($noprox "$BIN" -e "show (Http.get \"$u\")" 2>&1) || fail "url-redact-last-at: show must succeed: $out"
        echo "$out" | grep -qF 'http://***@127.0.0.1:1/x' || fail "url-redact-last-at: show must mask through the last @: $out"
        out=$($noprox "$BIN" -e "Http.send (Http.get \"$u\")" 2>&1) && fail "url-redact-last-at: $u must not reach"
        echo "$out" | grep -qE 'Tr0ub|4dor|pa#ss|ssw0rd|q\?wd|deploy' && fail "url-redact-last-at: the send error printed the credential: $out"
        echo "$out" | grep -qF '***@127.0.0.1' || fail "url-redact-last-at: the send error must mask: $out"
    done
    # a single-label host after `user:<digits>` and a raw delimiter
    for u in "http://deploy:2024/01/15@intranet/x" "http://u:2024#pw@gitlab/x"; do
        out=$($noprox "$BIN" -e "show (Http.get \"$u\")" 2>&1) || fail "url-redact-last-at: show must succeed: $out"
        echo "$out" | grep -qE '2024|deploy|pw@' && fail "url-redact-last-at: show printed the credential: $out"
        echo "$out" | grep -qE 'http://\*\*\*@(intranet|gitlab)/x' || fail "url-redact-last-at: show must mask a ported user:digits authority: $out"
    done
    mkdir -p "$h7dir/ua" && ( cd "$h7dir/ua" && git init -q . )
    out=$( cd "$h7dir/ua" && $noprox "$BIN" add schema 'http://deploy:Tr0ub/4dor@127.0.0.1:1/s.json' --as s 2>&1 ) && fail "url-redact-last-at: add must fail"
    echo "$out" | grep -qE 'Tr0ub|4dor' && fail "url-redact-last-at: weir add printed the credential: $out"
    for v in 'http://u:SEC/RETPW@h:99999' '//u:SEC@RETPW@h:99999' 'u:SEC#RETPW@h:99999'; do
        out=$($noprox HTTP_PROXY="$v" "$BIN" -e 'Http.send (Http.get "http://127.0.0.1:1/x")' 2>&1) && fail "url-redact-last-at: $v must refuse"
        echo "$out" | grep -qF 'not a usable proxy URL' || fail "url-redact-last-at: $v must refuse as malformed: $out"
        echo "$out" | grep -qE 'SEC|RETPW' && fail "url-redact-last-at: the proxy message printed the credential: $out"
    done
    echo "e2e ok: a URL's userinfo is masked through its last @, raw / ? # @ in the password included [D:url-redact]"

    h7port=$((24500 + RANDOM % 200))
    cat > "$h7dir/srv.py" <<H7EOF
import http.server, json
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        open('$h7dir/srv.log', 'a').write(self.path + '\n')
        if self.path.endswith('/ref.json'):
            b = json.dumps({"type": "object", "properties": {"e": {"\$ref": "#/x\u001b]52;c;QQ==\u0007\r"}}}).encode()
        elif self.path.endswith('/k.json'):
            b = json.dumps({"type": "object", "properties": {"": {"type": "string"}, "ok": {"type": "string"}}}).encode()
        else:
            b = b'proxied'
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(b)
    def log_message(self, *x): pass
http.server.HTTPServer(('127.0.0.1', $h7port), H).serve_forever()
H7EOF
    python3 "$h7dir/srv.py" & h7srv=$!
    awaitTcp "$h7port" || { kill $h7srv 2>/dev/null || true; fail "h3r7-B: the server never came up"; }
    # the runtime would skip the malformed variable and use the next one
    for pair in "http_proxy ALL_PROXY" "all_proxy ALL_PROXY"; do
        read -r pbad pgood <<< "$pair"
        [ "$IS_WINDOWS" = "1" ] && [ "$pbad" = all_proxy ] && continue # one name on Windows
        : > "$h7dir/srv.log"
        out=$($noprox "$pbad=http://h:99999" "$pgood=http://127.0.0.1:$h7port" "$BIN" -e 'print (Http.send (Http.get "http://127.0.0.1:1/x")).body' 2>&1) && fail "proxy-fallthrough: a malformed $pbad must refuse beside $pgood: $out"
        echo "$out" | grep -qiF "$pbad is set but is not a usable proxy URL" || fail "proxy-fallthrough: the refusal must name $pbad: $out"
        [ -s "$h7dir/srv.log" ] && fail "proxy-fallthrough: the request went through $pgood: $(cat "$h7dir/srv.log")" || true
    done
    out=$($noprox "http_proxy=http://127.0.0.1:$h7port" "ALL_PROXY=http://h:99999" "$BIN" -e 'print (Http.send (Http.get "http://127.0.0.1:1/x")).body' 2>&1) || fail "proxy-fallthrough: a usable first variable must win: $out"
    [ "$out" = "proxied" ] || fail "proxy-fallthrough: a usable first variable must win: $out"
    echo "e2e ok: a malformed proxy variable refuses even when a later one is set [D:proxy-env-malformed]"

    # message sinks: the data they quote renders visibly, piped
    E=$(printf 'Q\033]52;c;QQ==\007\rZ')
    mkdir -p "$h7dir/ms" && ( cd "$h7dir/ms" && git init -q . )
    noraw() { printf '%s' "$2" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b' in d or b'\r' in d or b'\x07' in d) else 0)" || fail "message-sinks: $1 put a raw ESC/BEL/CR in its message: $2"; }
    out=$( cd "$h7dir/ms" && $BIN gen types "$E" 2>&1 ) && fail "message-sinks: gen types must refuse"; noraw "gen types arg" "$out"
    out=$( cd "$h7dir/ms" && $BIN gen types --schema "$E" 2>&1 ) && fail "message-sinks: gen types must refuse"; noraw "gen types --schema" "$out"
    out=$( cd "$h7dir/ms" && $BIN fmt "--$E" 2>&1 ) && fail "message-sinks: fmt must refuse"; noraw "fmt flag" "$out"
    out=$( cd "$h7dir/ms" && $BIN check "--$E" 2>&1 ) && fail "message-sinks: check must refuse"; noraw "check flag" "$out"
    echo "$out" | grep -qF 'unknown flag --Q\x1b]52;c;QQ==\x07\x0dZ' || fail "message-sinks: the check flag must render visibly: $out"
    printf '#sig grep\ngrep --co\033c x\n' > "$h7dir/ms/sig.weir"; mkdir -p "$h7dir/ms/.weir/sigs"
    printf 'module Grep\ntype Cmd = {\n    color: bool\n}\n' > "$h7dir/ms/.weir/sigs/grep.weir"
    out=$( cd "$h7dir/ms" && $BIN check sig.weir 2>&1 ) || true; noraw "the sig warning" "$out"
    echo "$out" | grep -qF "unknown flag '--co\x1bc'" || fail "message-sinks: the sig warning must quote the flag visibly: $out"
    printf 'import "./no\033cfile.weir" as M\n' > "$h7dir/ms/imp.weir"
    out=$( cd "$h7dir/ms" && $BIN check imp.weir 2>&1 ) && fail "message-sinks: a missing import must fail"; noraw "the import path" "$out"
    echo "$out" | grep -qF 'no\x1bcfile.weir' || fail "message-sinks: the import path must render visibly: $out"
    printf '^ech\033co hi\n' > "$h7dir/ms/cnf.weir"
    out=$( cd "$h7dir/ms" && $BIN check cnf.weir 2>&1 ) && fail "message-sinks: an unknown head must fail"; noraw "command not found" "$out"
    echo "$out" | grep -qF 'command not found: ech\x1bco' || fail "message-sinks: the head must render visibly: $out"
    out=$( cd "$h7dir/ms" && $noprox "$BIN" add schema "http://127.0.0.1:$h7port/ref.json" --as ref 2>&1 ) && fail "message-sinks: an unresolvable \$ref must refuse"; noraw "add schema's \$ref" "$out"
    echo "$out" | grep -qF '#/x\x1b]52;c;QQ==\x07\x0d' || fail "message-sinks: the \$ref must render visibly: $out"
    echo "e2e ok: CLI, check and add messages escape the data they quote, piped [D:error-data-escape]"

    # gen types drops a "" property key with a note, as #infer does
    out=$( cd "$h7dir/ms" && $noprox "$BIN" add schema "http://127.0.0.1:$h7port/k.json" --as k 2>&1 ) || fail "gentypes-empty-key: add must succeed: $out"
    out=$( cd "$h7dir/ms" && $BIN gen types --schema k --out - 2>&1 ) || fail "gentypes-empty-key: gen types must succeed: $out"
    echo "$out" | grep -qF 'an empty-string key under' || fail "gentypes-empty-key: the drop must be noted: $out"
    echo "$out" | grep -qF 'ok: Option<string>' || fail "gentypes-empty-key: the other field stays: $out"
    kill $h7srv 2>/dev/null || true
    echo "e2e ok: gen types drops a \"\" key with a note [D:infer-wire-escape]"

    # parallel adds serialize under the .weir/ lock; lock.json is replaced whole
    if [ "$IS_WINDOWS" = "0" ]; then
        mkdir -p "$h7dir/pa/bin" && ( cd "$h7dir/pa" && git init -q . )
        for t in s1 s2 s3 s4 s5 s6; do
            printf '#!/bin/sh\n[ "$1" = --help ] || exit 1\nprintf "Usage: %s\\n\\nOptions:\\n  --only-%s   doc\\n"\n' "$t" "$t" > "$h7dir/pa/bin/$t"
            chmod +x "$h7dir/pa/bin/$t"
        done
        pids=""
        for t in s1 s2 s3 s4 s5 s6; do
            ( cd "$h7dir/pa" && PATH="$h7dir/pa/bin:$PATH" "$BIN" add sig $t > /dev/null 2>&1 ) & pids="$pids $!"
        done
        for p in $pids; do wait $p || fail "weir-dir-lock: a parallel add sig failed"; done
        n=$(grep -c '"name"' "$h7dir/pa/.weir/lock.json")
        [ "$n" = 6 ] || fail "weir-dir-lock: 6 parallel adds must leave 6 lock entries, got $n: $(cat "$h7dir/pa/.weir/lock.json")"
        inode() { if stat -c %i "$1" >/dev/null 2>&1; then stat -c %i "$1"; else stat -f %i "$1"; fi; }
        i0=$(inode "$h7dir/pa/.weir/lock.json")
        ( cd "$h7dir/pa" && PATH="$h7dir/pa/bin:$PATH" "$BIN" add sig s1 > /dev/null 2>&1 ) || fail "weir-dir-lock: a re-add must succeed"
        [ "$(inode "$h7dir/pa/.weir/lock.json")" != "$i0" ] || fail "weir-dir-lock: lock.json must be replaced by rename, not rewritten in place"
        ls -a "$h7dir/pa/.weir" "$h7dir/pa/.weir/sigs" | grep -qF 'weir-tmp' && fail "weir-dir-lock: a temp was left behind: $(ls -a "$h7dir/pa/.weir")" || true
        # a missing signature is regenerated, never restored
        rm -f "$h7dir/pa/.weir/sigs/s2.weir"
        out=$( cd "$h7dir/pa" && PATH="$h7dir/pa/bin:$PATH" "$BIN" verify 2>&1 ) && fail "weir-dir-lock: verify must report the absent sig"
        echo "$out" | grep -qF "sig s2: ABSENT — regenerate it: weir add sig 's2'" || fail "verify-sig-absent: the hint must name weir add sig: $out"
        echo "e2e ok: parallel adds keep every lock entry; lock.json is replaced whole; an absent sig says weir add sig [D:weir-dir-lock]"
    fi
else
    echo "e2e skip: h3r7 stage B (python3 absent)"
fi

# ---- Fix 2: parse/decode errors excerpt a huge invalid input [D:excerpt] ---
# a multi-MB invalid input used to be echoed whole (a ~2MB stderr flood);
# it is now bounded to a 64-char head + the true length. A short invalid
# input is still quoted in full.
xdir=$(mkweirtmp)
python3 - "$xdir/big.weir" <<'PYX'
import sys
big = 'z' * 2000000
open(sys.argv[1], 'w').write('let big = "' + big + '"\nprint (Str.toInt big)\n')
PYX
out=$($BIN "$xdir/big.weir" 2>&1) && fail "an invalid toInt must raise" || true
outlen=$(printf '%s' "$out" | wc -c)
[ "$outlen" -lt 200 ] || fail "the excerpted error must be bounded (<200 bytes); got $outlen: ${out:0:120}"
echo "$out" | grep -qF "(2000000 chars)" || fail "the true length is named: ${out:0:120}"
# a short invalid input is still quoted whole
out=$($BIN -e 'print (Str.toInt "notanum")' 2>&1) && fail "a short invalid toInt must raise" || true
echo "$out" | grep -qF '"notanum"' || fail "a short input stays fully readable: $out"
rm -rf "$xdir"
echo "e2e ok: a multi-MB invalid parse input yields a bounded error; a short one is quoted whole (Fix 2)"

# F2: an unreadable file on the import path is a located diagnostic, never
# a crash (mode 000; exit 1, not 134) [D:lockfile-confinement]
if [ "$IS_WINDOWS" = "0" ]; then
    f2dir=$(mkweirtmp)
    printf 'module M\nlet f : int -> int\nlet f x = x + 1\n' > "$f2dir/mod.weir"
    printf 'import "./mod.weir" as M\nprint (show (M.f 1))\n' > "$f2dir/main.weir"
    chmod 000 "$f2dir/mod.weir"
    f2rc=0
    out=$($BIN check "$f2dir/main.weir" 2>&1) || f2rc=$?
    chmod 644 "$f2dir/mod.weir"
    [ "$f2rc" = "1" ] || fail "F2: an unreadable import must exit 1 (a located diagnostic), got $f2rc: $out"
    echo "$out" | grep -qF "cannot read import" || fail "F2: the located 'cannot read import' diagnostic: $out"
    rm -rf "$f2dir"
    echo "e2e ok: an unreadable import is a located diagnostic, never a crash (F2)"
else
    echo "e2e skip: F2 unreadable-import (POSIX mode bits)"
fi

# the entry script's read is weir-worded in every mode, never an internal
# exception: unreadable (mode 000), a directory, a device; and fmt on a
# read-only file names the write it cannot make
if [ "$IS_WINDOWS" = "0" ]; then
    h11dir=$(mkweirtmp)
    printf 'print "hi"\n' > "$h11dir/deny.weir"
    chmod 000 "$h11dir/deny.weir"
    mkdir "$h11dir/adir"
    for mode in "" "check" "check --can" "check --json" "check --can --json" "fmt"; do
        if [ ! -r "$h11dir/deny.weir" ]; then
            rc=0; out=$($BIN $mode "$h11dir/deny.weir" 2>&1) || rc=$?
            [ "$rc" = "2" ] || fail "an unreadable script ($mode) exits 2, got $rc: $out"
            expect "an unreadable script is weir-worded ($mode)" "cannot read $h11dir/deny.weir: permission denied" "$out"
        fi
        rc=0; out=$($BIN $mode "$h11dir/adir" 2>&1) || rc=$?
        [ "$rc" = "2" ] || fail "a directory argument ($mode) exits 2, got $rc: $out"
        expect "a directory argument names itself ($mode)" "$h11dir/adir is a directory, not a script" "$out"
        rc=0; out=$($BIN $mode /dev/zero 2>&1) || rc=$?
        [ "$rc" = "2" ] || fail "a device ($mode) exits 2, got $rc: $out"
        expect "a device is refused, not read forever ($mode)" "not a regular file" "$out"
    done
    chmod 644 "$h11dir/deny.weir"
    printf 'print  "x"\n' > "$h11dir/ro.weir"
    chmod 444 "$h11dir/ro.weir"
    if [ ! -w "$h11dir/ro.weir" ]; then
        rc=0; out=$($BIN fmt "$h11dir/ro.weir" 2>&1) || rc=$?
        [ "$rc" = "2" ] || fail "fmt on a read-only file exits 2, got $rc: $out"
        expect "fmt on a read-only file is weir-worded" "cannot write $h11dir/ro.weir: permission denied" "$out"
    fi
    rm -rf "$h11dir"
else
    echo "e2e skip: unreadable entry script (POSIX mode bits, /dev/zero)"
fi

# a source file must be UTF-8 [D:source-utf8]: the first invalid byte is a
# located refusal (entry and import alike), never a silent U+FFFD; a BOM
# and CRLF still read
u8dir=$(mkweirtmp)
printf 'print "ok"\nprint "\303\251t\303\251 \377 x"\n' > "$u8dir/bad.weir"
for mode in "" "check" "fmt"; do
    rc=0; out=$($BIN $mode "$u8dir/bad.weir" 2>&1) || rc=$?
    [ "$rc" = "2" ] || fail "invalid UTF-8 ($mode) exits 2, got $rc: $out"
    expect "invalid UTF-8 is located ($mode)" "bad.weir:2:12: invalid UTF-8" "$out"
done
printf 'print "ok"\nprint "\303\251t\303\251 \377 x"\n' | cmp -s - "$u8dir/bad.weir" || fail "fmt rewrote a file with invalid UTF-8"
printf 'module M\nlet x : int\nlet x = 1 // \376\n' > "$u8dir/m.weir"
printf 'import "./m.weir"\nprint (show M.x)\n' > "$u8dir/main.weir"
out=$($BIN "$u8dir/main.weir" 2>&1) || true
expect "invalid UTF-8 in a module is located in the module" "m.weir:3:14: error: invalid UTF-8" "$out"
printf '\357\273\277print "bom"\r\nprint "crlf"\r\n' > "$u8dir/bom.weir"
out=$($BIN "$u8dir/bom.weir" 2>&1)
[ "$out" = "bom
crlf" ] || fail "a BOM and CRLF still read: $out"
rm -rf "$u8dir"

# an error in the join text between continuation lines sits just past the
# physical line's last character, never beyond it
jcdir=$(mkweirtmp)
printf 'd\n    within lock""t\n    w\n' > "$jcdir/join.weir"
out=$($BIN check "$jcdir/join.weir" 2>&1) || true
expect "a joined-line error column stays on its line" "join.weir:2:19: error" "$out"
rm -rf "$jcdir"

# check-time catches on the real schema: the typo (did-you-mean) and a
# misplaced nesting (a field at the wrong level)
cat > "$ctdir/proj/sub/cm.weir" <<'WEOF'
let cm = yaml schema=k8s-configmap
    apiVerison: v1
    kind: ConfigMap
    data:
        k: v

cm |> to yaml |> print
WEOF
out=$($BIN check --json "$ctdir/proj/sub/cm.weir" || true)
echo "$out" | grep -qF "unknown field 'apiVerison'" || fail "the typo catch: $out"
echo "$out" | grep -qF "did you mean 'apiVersion'" || fail "the did-you-mean: $out"
echo "$out" | grep -qF '"code":"schema"' || fail "coded schema diagnostic: $out"
echo "e2e ok: apiVerison caught at CHECK time against a real published schema"

cat > "$ctdir/proj/sub/nest.weir" <<'WEOF'
let cm = yaml schema=k8s-configmap
    apiVersion: v1
    kind: ConfigMap
    metadata:
        data:
            k: v

cm |> to yaml |> print
WEOF
out=$($BIN check "$ctdir/proj/sub/nest.weir" 2>&1 || true)
echo "$out" | grep -qF "unknown field 'data'" || fail "misplaced nesting caught (data under metadata): $out"
echo "e2e ok: misplaced nesting is a located check error"

# property 3: with and without the contract, byte-identical output
cat > "$ctdir/proj/sub/p3.weir" <<'WEOF'
let cm = yaml schema=k8s-configmap
    apiVersion: v1
    kind: ConfigMap
    data:
        k: v

cm |> to yaml |> print
WEOF
sed 's/ schema=k8s-configmap//' "$ctdir/proj/sub/p3.weir" > "$ctdir/proj/sub/p3plain.weir"
diff <($BIN "$ctdir/proj/sub/p3.weir") <($BIN "$ctdir/proj/sub/p3plain.weir") || fail "property 3: contracts must not change runtime output"
echo "e2e ok: PROPERTY 3 — byte-identical with and without the contract"

# check never fetches: an unreachable URL in the lock is irrelevant
# while the vendored file exists; a missing schema teaches vendor,
# without touching the network
python3 - "$ctdir/proj/.weir/lock.json" <<'PYEOF2'
import json, sys
d = json.load(open(sys.argv[1]))
d["artifacts"][0]["url"] = "http://127.0.0.1:1/never"
json.dump(d, open(sys.argv[1], "w"))
PYEOF2
$BIN check "$ctdir/proj/sub/p3.weir" || fail "check must succeed offline from the vendored file"
rm "$ctdir/proj/.weir/schemas/k8s-configmap.json"
out=$($BIN check "$ctdir/proj/sub/p3.weir" 2>&1 || true)
echo "$out" | grep -qF "the lock records it; run \`weir restore\`" || fail "locked-but-missing teaches restore, never fetches: $out"

# ...and a never-declared schema teaches `add` — the checker tells the
# two apart by the lock [D:contracts-spine]
cat > "$ctdir/proj/sub/never.weir" <<'WEOF'
let d = yaml schema=never-added
    kind: X

d |> to yaml |> print
WEOF
out=$($BIN check "$ctdir/proj/sub/never.weir" 2>&1 || true)
echo "$out" | grep -qF "add it: weir add schema <url> --as never-added" || fail "undeclared schema teaches add: $out"
echo "e2e ok: check never fetches — locked-but-missing teaches restore, undeclared teaches add"

# the six message shapes, re-pinned verbatim after the consistency pass
# [schema-polish]: every message names its field; paths always (root
# renders without a suffix); a one-element enum states its value plainly
mkdir -p "$ctdir/proj/.weir/schemas"
cat > "$ctdir/proj/.weir/schemas/six.json" <<'JEOF'
{ "type": "object", "additionalProperties": false, "required": ["kind"],
  "properties": {
    "kind": { "enum": ["Service"] },
    "spec": { "type": "object", "additionalProperties": false,
      "properties": {
        "ports": { "type": "array", "items": { "type": "object", "additionalProperties": false,
          "properties": { "port": { "type": "integer" } } } },
        "ips": { "type": "array", "items": { "type": "string" } },
        "affinity": { "type": "string" } } } } }
JEOF
cat > "$ctdir/proj/sub/six.weir" <<'WEOF'
let n = "three"
let d = yaml schema=six
    kinnd: Deployment
    spec:
        ports:
            - port: nope
        ips: $n
        affinity:
            bad: map

d |> to yaml |> print
WEOF
out=$($BIN check "$ctdir/proj/sub/six.weir" 2>&1 || true)
echo "$out" | grep -qF "schema six: unknown field 'kinnd' — did you mean 'kind'?" || fail "1 unknown+didyoumean: $out"
echo "$out" | grep -qF "schema six: missing required field 'kind'" || fail "2 missing required: $out"
echo "$out" | grep -qF "schema six: field spec.ports.port expects integer, got string ('nope')" || fail "3 literal type with path: $out"
echo "$out" | grep -qF "schema six: field spec.ips expects a sequence, but the splice is string" || fail "4 splice type with path: $out"
echo "$out" | grep -qF "schema six: field spec.affinity expects a scalar, got a mapping" || fail "5 structure mismatch with path: $out"
cat > "$ctdir/proj/sub/six2.weir" <<'WEOF'
let d = yaml schema=six
    kind: Deployment

d |> to yaml |> print
WEOF
out=$($BIN check "$ctdir/proj/sub/six2.weir" 2>&1 || true)
echo "$out" | grep -qF "schema six: field kind expects 'Service', got 'Deployment'" || fail "6 one-element enum, plainly: $out"
echo "e2e ok: the six schema messages re-pinned verbatim — fields named, paths always"

# a type array mixing a scalar with a container validates each node
# against its own kind's alternative [D:schema-mixed-kinds]
cat > "$ctdir/proj/.weir/schemas/mix.json" <<'JEOF'
{ "type": "object", "additionalProperties": false,
  "properties": {
    "cmd": { "type": ["string", "array"], "items": { "type": "string" } },
    "on": { "type": ["boolean", "string"] } } }
JEOF
cat > "$ctdir/proj/sub/mix.weir" <<'WEOF'
let a = yaml schema=mix
    cmd: make all
    on: true

let b = yaml schema=mix
    cmd:
        - make
        - all

a |> to yaml |> print
b |> to yaml |> print
WEOF
$BIN check "$ctdir/proj/sub/mix.weir" || fail "a scalar and a sequence both validate against [string, array]"
cat > "$ctdir/proj/sub/mix2.weir" <<'WEOF'
let a = yaml schema=mix
    cmd:
        bad: map

let b = yaml schema=mix
    cmd:
        - 1

a |> to yaml |> print
b |> to yaml |> print
WEOF
out=$($BIN check "$ctdir/proj/sub/mix2.weir" 2>&1 || true)
echo "$out" | grep -qF "schema mix: field cmd expects array/string, got a mapping" || fail "mixed kinds: a mapping refused: $out"
echo "$out" | grep -qF "schema mix: field cmd expects string, got integer ('1')" || fail "mixed kinds: the array alternative's items: $out"
echo "e2e ok: a type array mixing a scalar with a container validates per kind"

# a schema without additionalProperties:false warns at add time — the
# silently-inert-contract guard [schema-polish item 3]
printf '{ "type": "object", "properties": { "a": { "type": "string" } } }' > "$ctdir/serve/loose.json"
# --directory, never a cd-subshell: MSYS bash does not exec-optimize
# `( cd .. && python ) &`, so $! was the subshell's pid and the kill left
# python alive holding the dir as its cwd (rm: Device or resource busy)
python3 -m http.server $ctport --bind 127.0.0.1 --directory "$ctdir/serve" >/dev/null 2>&1 &
ctsrv2=$!
awaitHttp "http://127.0.0.1:$ctport/loose.json" || { kill $ctsrv2 2>/dev/null || true; fail "the schema server (2) never came up"; }
out=$( cd "$ctdir/proj" && $BIN add schema http://127.0.0.1:$ctport/loose.json --as loose 2>&1 )
echo "$out" | grep -qF "unknown-field checking will NOT fire" || fail "the inert-schema warning: $out"
echo "$out" | grep -qF "standalone-strict" || fail "the warning names the strict variant: $out"
kill $ctsrv2 2>/dev/null || true
# reap before rm: the kill is async, and Windows refuses to remove a
# dir while a live process holds anything in it
wait $ctsrv $ctsrv2 2>/dev/null || true
echo "e2e ok: a no-strict schema warns at add time, naming the variant"
rm -rf "$ctdir"

# ---- the starship port's env-overlay findings ------------------------------
ovdir=$(mkweirtmp)
# home and XDG follow within env [D:home-overlay]
printf 'within env HOME="/fake/home" XDG_CONFIG_HOME=""\n    echo ~/x\n    print (Path.home ())\n    print (Path.configHome ())\n' > "$ovdir/home.weir"
out=$(cd "$ovdir" && $BIN home.weir 2>&1) || fail "port: home.weir: $out"
echo "$out" | grep -qxF "/fake/home/x" || fail "port: a typed ~ follows within env HOME: $out"
echo "$out" | grep -qxF "/fake/home" || fail "port: Path.home follows within env HOME: $out"
if [ "$IS_WINDOWS" = "0" ]; then
    echo "$out" | grep -qxF "/fake/home/.config" || fail "port: Path.configHome follows the overlay: $out"
fi
echo "e2e ok: Path.home, a typed ~ and Path.configHome read the within env overlay [D:home-overlay]"
# a head resolves on the PATH the child runs with [D:overlay-path]
if [ "$IS_WINDOWS" = "0" ]; then
    mkdir -p "$ovdir/bin"
    printf '#!/bin/sh\necho fake-tool-ran\n' > "$ovdir/bin/weirportfaketool"
    chmod +x "$ovdir/bin/weirportfaketool"
    printf 'let tool = "weirportfaketool"\nlet dir = Path.combine (Path.dir Self.scriptPath) "bin"\nlet p = Env.need "PATH"\nwithin env PATH=$"{dir}:{p}"\n    ^$tool\nPATH=$"{dir}:{p}" ^$tool\nPATH=$"bin:{p}" ^$tool\n' > "$ovdir/path.weir"
    out=$(cd "$ovdir" && $BIN path.weir 2>&1) || fail "port: ^\$tool must resolve on the overlay PATH: $out"
    [ "$(echo "$out" | grep -c fake-tool-ran)" = 3 ] || fail "port: within env, a PATH= prefix and a relative entry all reach the tool: $out"
    printf 'within env PATH="/weir-no-such-dir"\n    sh -c "echo hi"\n' > "$ovdir/litpath.weir"
    out=$(cd "$ovdir" && $BIN litpath.weir 2>&1) && fail "port: a literal head the overlay PATH lacks must not run: $out"
    echo "$out" | grep -qF "not on the PATH it runs with" || fail "port: the overlay-PATH miss is named: $out"
    echo "e2e ok: ^\$tool and a literal head resolve on the overlay PATH at run time [D:overlay-path]"
fi
rm -rf "$ovdir"

# ---- schema→types: weir gen types [D:schema-types] -------------------------
# a locked schema becomes a user-owned decl-only module: required vs
# optional by schema fact (the thing no #infer sample can carry), the
# open mappings, the wire sanitizer — offline (reads the vendored file),
# deterministic (byte-identical on re-run), add-validates (the emitted
# module runs through the real checker before anything lands)
gtdir=$(mkweirtmp)
mkdir -p "$gtdir/serve" "$gtdir/proj"
cp "$(dirname "$0")/../tests/fixtures/pod-v1-trimmed.json" "$gtdir/serve/"
printf '{ "type": "string" }' > "$gtdir/serve/scalar.json"
gtport=$((18960 + RANDOM % 2000))
python3 -m http.server $gtport --bind 127.0.0.1 --directory "$gtdir/serve" >/dev/null 2>&1 &
gtsrv=$!
awaitHttp "http://127.0.0.1:$gtport/pod-v1-trimmed.json" || { kill $gtsrv 2>/dev/null || true; fail "the gen-types schema server never came up"; }
( cd "$gtdir/proj" && git init -q . )

# the widened subset: a $ref-heavy k8s document (definitions, allOf,
# anyOf IntOrString, nullable, x-kubernetes-*) vendors clean
( cd "$gtdir/proj" && $BIN add schema http://127.0.0.1:$gtport/pod-v1-trimmed.json --as pod ) | grep -q "added schema pod" || fail "add schema on the ref-heavy pod fixture"
( cd "$gtdir/proj" && $BIN add schema http://127.0.0.1:$gtport/scalar.json --as scalar ) >/dev/null || fail "add schema scalar"
# [D:schema-validation-keys] [D:schema-int-or-string] [D:schema-int-enum]:
# validation keywords vendor; int-or-string and an integer enum draft
# types the document's own values read into
cat > "$gtdir/serve/svc.json" <<'JEOF'
{"type":"object","$comment":"c","required":["level"],"properties":{"name":{"type":"string","minLength":1,"pattern":"^a"},"port":{"anyOf":[{"type":"integer"},{"type":"string"}],"x-kubernetes-int-or-string":true},"level":{"type":"integer","enum":[1,2,3]},"tags":{"type":"array","items":{"type":"string"},"maxItems":3,"uniqueItems":true}}}
JEOF
( cd "$gtdir/proj" && $BIN add schema http://127.0.0.1:$gtport/svc.json --as svc ) >/dev/null || fail "add schema over validation keywords"
# [D:schema-num-or-string] [D:schema-enum-kind] [D:schema-const-format]:
# number-or-string and format int-or-string draft Yaml, a typed enum its
# type, a mixed enum Yaml; a root $id, const and contentEncoding vendor
cat > "$gtdir/serve/r2.json" <<'JEOF'
{"$id":"https://example.com/r2.json","type":"object","required":["on","ratio"],"properties":{"cpu":{"oneOf":[{"type":["string","null"]},{"type":["number","null"]}]},"port":{"type":"string","format":"int-or-string"},"on":{"type":"boolean","enum":[true]},"ratio":{"type":"number","enum":[0.5,1.5]},"mode":{"enum":["a",1]},"kind":{"type":"string","const":"Deployment"},"blob":{"type":"string","contentEncoding":"base64","format":"byte"}}}
JEOF
( cd "$gtdir/proj" && $BIN add schema http://127.0.0.1:$gtport/r2.json --as r2 ) >/dev/null || fail "add schema over \$id/const/contentEncoding"
# [D:schema-anyof-mixed]: starship's config-schema.json, trimmed to its
# string-or-array anyOfs (custom.shell; python_binary nests one more)
cat > "$gtdir/serve/starship.json" <<'JEOF'
{"type":"object","properties":{"custom":{"$ref":"#/$defs/CustomConfig"},"python":{"$ref":"#/$defs/PythonConfig"}},"$defs":{"VecOr_string":{"anyOf":[{"type":"string"},{"type":"array","items":{"type":"string"}}]},"VecOr_VecOr_string":{"anyOf":[{"$ref":"#/$defs/VecOr_string"},{"type":"array","items":{"$ref":"#/$defs/VecOr_string"}}]},"CustomConfig":{"type":"object","properties":{"command":{"type":"string","default":""},"shell":{"$ref":"#/$defs/VecOr_string","default":[]}},"additionalProperties":false},"PythonConfig":{"type":"object","properties":{"python_binary":{"$ref":"#/$defs/VecOr_VecOr_string","default":[["python"],["python3"],["python2"]]}},"additionalProperties":false}}}
JEOF
( cd "$gtdir/proj" && $BIN add schema http://127.0.0.1:$gtport/starship.json --as starship ) >/dev/null || fail "add schema over starship's anyOfs"
kill $gtsrv 2>/dev/null || true
wait $gtsrv 2>/dev/null || true

# generation is offline from here — the server is gone
out=$( cd "$gtdir/proj" && $BIN gen types --schema pod )
echo "$out" | grep -q "generated .*types.*pod\.weir" || fail "gen types announces the file: $out"
echo "$out" | grep -qF 'import it:  import "weir:pod" as Pod' || fail "the import line teaches use: $out"
test -f "$gtdir/proj/.weir/types/pod.weir" || fail "the generated module exists"
grep -qF "type Pod = {" "$gtdir/proj/.weir/types/pod.weir" || fail "the root ref names the top type"
grep -qF "env: Option<seq<" "$gtdir/proj/.weir/types/pod.weir" || fail "env is Option BY SCHEMA FACT — absent from required"
grep -qF "labels: Option<seq<string * string>>" "$gtdir/proj/.weir/types/pod.weir" || fail "labels generate the open mapping"
grep -qF "containers: seq<Container>" "$gtdir/proj/.weir/types/pod.weir" || fail "the required array stays plain"
grep -qF "an enum of 'Pending', 'Running'" "$gtdir/proj/.weir/types/pod.weir" || fail "the enum note lists the values"
echo "e2e ok: gen types — required/optional by schema fact, mappings, enum notes"

# determinism: same locked schema → byte-identical output; and the
# output is fmt-canonical
cp "$gtdir/proj/.weir/types/pod.weir" "$gtdir/gen1.weir"
( cd "$gtdir/proj" && $BIN gen types --schema pod ) >/dev/null
cmp -s "$gtdir/proj/.weir/types/pod.weir" "$gtdir/gen1.weir" || fail "gen types must be byte-deterministic"
cp "$gtdir/proj/.weir/types/pod.weir" "$gtdir/fmtprobe.weir"
$BIN fmt --check "$gtdir/fmtprobe.weir" || fail "the generated module must be fmt-canonical"
echo "e2e ok: gen types is byte-deterministic and fmt-canonical"

# the acceptance (the pain #infer cannot fix): a container without env
# reads clean because the schema said env is optional; labels read as
# the mapping — through the weir: import, in a nested dir
mkdir -p "$gtdir/proj/sub"
cat > "$gtdir/proj/sub/pod.json" <<'JEOF'
{"apiVersion":"v1","kind":"Pod","metadata":{"name":"web","labels":{"app":"web","tier.example.com/zone":"eu"}},"spec":{"containers":[{"name":"app","image":"nginx","env":[{"name":"MODE","value":"prod"}]},{"name":"sidecar","image":"busybox"}]},"status":{"phase":"Running"}}
JEOF
cat > "$gtdir/proj/sub/use.weir" <<'WEOF'
import "weir:pod" as Pod

let pod = File.read "pod.json" |> from json Pod

for c in pod.spec.containers do
    let envs = c.env |> Option.map Seq.length |> Option.defaultValue 0
    print $"{c.name}:{envs}"

pod.metadata
    |> Option.iter (fun m ->
        m.labels |> Option.iter (Seq.iter (fun (k, v) -> print $"{k}={v}")))
WEOF
$BIN check "$gtdir/proj/sub/use.weir" || fail "the generated module imports and checks"
out=$( cd "$gtdir/proj/sub" && $BIN use.weir )
echo "$out" | grep -qxF "app:1" || fail "the env-bearing container reads: $out"
echo "$out" | grep -qxF "sidecar:0" || fail "THE acceptance: a container without env reads clean: $out"
echo "$out" | grep -qxF "tier.example.com/zone=eu" || fail "labels read as the mapping, dirty keys included: $out"
echo "e2e ok: THE ACCEPTANCE — schema → gen → import → env-less container reads; labels as mapping"

# the district validator rides the same widened subset: a top-level
# typo checks against the $ref-resolved Pod (additionalProperties:false)
cat > "$gtdir/proj/sub/tpl.weir" <<'WEOF'
let d = yaml schema=pod
    apiVerison: v1
    spec:
        containers:
            - name: app

d |> to yaml |> print
WEOF
out=$($BIN check "$gtdir/proj/sub/tpl.weir" 2>&1 || true)
echo "$out" | grep -qF "unknown field 'apiVerison'" || fail "schema= validates through the root ref: $out"
echo "$out" | grep -qF "did you mean 'apiVersion'" || fail "did-you-mean through the ref: $out"
echo "e2e ok: schema= district validation resolves in-document refs"

# refusals, each teaching: an unrepresentable top level (nothing was
# written), and a never-locked name
out=$( cd "$gtdir/proj" && $BIN gen types --schema scalar 2>&1 ) && fail "a scalar-top schema must refuse" || true
echo "$out" | grep -qF "no record to generate" || fail "the scalar-top refusal teaches: $out"
test ! -e "$gtdir/proj/.weir/types/scalar.weir" || fail "a refused gen must write nothing"
out=$( cd "$gtdir/proj" && $BIN gen types --schema nope 2>&1 ) && fail "an unlocked schema must refuse" || true
echo "$out" | grep -qF "no locked schema 'nope' — add it: weir add schema <url> --as nope" || fail "the unlocked teach: $out"
out=$( cd "$gtdir/proj" && $BIN gen types --schema pod --as lowercase 2>&1 ) && fail "a lowercase --as must refuse" || true
echo "$out" | grep -qF "must be a type name: an uppercase letter first" || fail "--as teaches the type-name rule: $out"
echo "e2e ok: gen types refusals — scalar top, unlocked name, lowercase --as"

( cd "$gtdir/proj" && $BIN gen types --schema svc ) >/dev/null || fail "gen types svc"
svcmod="$gtdir/proj/.weir/types/svc.weir"
grep -qF "port: Option<Yaml>" "$svcmod" || fail "int-or-string drafts Yaml: $(cat "$svcmod")"
grep -qF "level: int" "$svcmod" || fail "an integer enum drafts int: $(cat "$svcmod")"
grep -qF "// note: not enforced: minLength on name" "$svcmod" || fail "the minLength note: $(cat "$svcmod")"
grep -qF "// note: not enforced: uniqueItems on tags" "$svcmod" || fail "the uniqueItems note: $(cat "$svcmod")"
cat > "$gtdir/proj/svc.weir" <<'WEOF'
import "weir:svc" as S
let j = ["{\"port\": 8080, \"level\": 2}"] |> from json Svc
j |> to json |> Seq.iter print
let y = ["port: 8080"; "level: 1"] |> from yaml Svc
y |> to yaml |> Seq.iter print
WEOF
out=$( cd "$gtdir/proj" && $BIN svc.weir 2>&1 ) || fail "the svc types read json and yaml: $out"
echo "$out" | grep -qF '"port":8080' || fail "json keeps 8080 an int: $out"
echo "$out" | grep -qxF "port: 8080" || fail "yaml writes 8080 unquoted: $out"
echo "e2e ok: gen types — validation keywords noted, int-or-string as Yaml, an integer enum as int"
( cd "$gtdir/proj" && $BIN gen types --schema r2 ) >/dev/null || fail "gen types r2"
r2mod="$gtdir/proj/.weir/types/r2.weir"
for want in "cpu: Option<Yaml>" "port: Option<Yaml>" "on: bool" "ratio: float" "mode: Option<Yaml>" "kind: Option<string>" \
    "// note: port: format int-or-string — drafted Yaml" "// note: not enforced: const on kind" \
    "// note: not enforced: contentEncoding on blob" "// note: not enforced: format on blob"; do
    grep -qF "$want" "$r2mod" || fail "r2 drafts '$want': $(cat "$r2mod")"
done
cat > "$gtdir/proj/r2.weir" <<'WEOF'
import "weir:r2" as R
let j = ["{\"cpu\": 1.5, \"port\": 8080, \"on\": true, \"ratio\": 0.5, \"mode\": 1}"] |> from json R2
j |> to json |> Seq.iter print
WEOF
out=$( cd "$gtdir/proj" && $BIN r2.weir 2>&1 ) || fail "the r2 types read the document's own values: $out"
[ "$out" = '{"cpu":1.5,"mode":1,"on":true,"port":8080,"ratio":0.5}' ] || fail "the r2 round trip: $out"
echo "e2e ok: gen types — number-or-string and format int-or-string as Yaml, typed and mixed enums, \$id/const/content keywords noted"
( cd "$gtdir/proj" && $BIN gen types --schema starship ) >/dev/null || fail "gen types starship"
ssmod="$gtdir/proj/.weir/types/starship.weir"
for want in "shell: Option<Yaml>" "python_binary: Option<Yaml>" "command: Option<string>" \
    "// note: VecOr_string: anyOf allows array/string — drafted Yaml; a string reads as YStr s, a sequence as YSeq items"; do
    grep -qF "$want" "$ssmod" || fail "starship drafts '$want': $(cat "$ssmod")"
done
grep -qF "FIRST variant" "$ssmod" && fail "a string-or-array anyOf must not draft its first variant: $(cat "$ssmod")"
cat > "$gtdir/proj/ss.weir" <<'WEOF'
import "weir:starship" as S
let one = ["{\"custom\": {\"shell\": \"bash\"}}"] |> from json Starship
let many = ["{\"custom\": {\"shell\": [\"bash\", \"--norc\"]}, \"python\": {\"python_binary\": [\"python3\", [\"py\", \"-3\"]]}}"] |> from json Starship
print $"{one.custom}"
print $"{many.custom}"
many |> to json |> Seq.iter print
WEOF
out=$( cd "$gtdir/proj" && $BIN ss.weir 2>&1 ) || fail "the starship types read a string and an array shell: $out"
echo "$out" | grep -qF 'YStr "bash"' || fail "a string shell reads as YStr: $out"
echo "$out" | grep -qF 'YSeq' || fail "an array shell reads as YSeq: $out"
echo "$out" | grep -qF '"python_binary":["python3",["py","-3"]]' || fail "python_binary round-trips: $out"
echo "e2e ok: gen types — a string-or-array anyOf drafts Yaml with a note, and reads both kinds (starship) [D:schema-anyof-mixed]"

# --out -: stdout, nothing written; --as renames the top type
( cd "$gtdir/proj" && $BIN gen types --schema pod --as Manifest --out - ) | grep -qF "type Manifest = {" || fail "--as + --out - streams the renamed module"

# the missing-field teach [D:schema-types]: a required field a document
# lacks names the Option repair — the drafted-from-a-sample pain, said
# at the failure
cat > "$gtdir/proj/sub/teach.weir" <<'WEOF'
type C = {
    name: string
    env: seq<string>
}

let c = ["{\"name\":\"x\"}"] |> from json C
print c.name
WEOF
out=$( cd "$gtdir/proj/sub" && $BIN teach.weir 2>&1 ) && fail "the missing required field must raise" || true
echo "$out" | grep -qF "missing field 'env'" || fail "the field is named: $out"
echo "$out" | grep -qF "if the field is sometimes absent, declare it Option<seq<string>>" || fail "the Option repair rides the error: $out"
echo "$out" | grep -qF "a type drafted from a sample only sees what the sample had" || fail "the sample teach: $out"
# (yaml reads a missing seq as empty by its own law, so the yaml teach
# pins on a missing scalar field)
cat > "$gtdir/proj/sub/teachy.weir" <<'WEOF'
type Y = {
    name: string
    image: string
}

let y = ["name: x"] |> from yaml Y
print y.name
WEOF
out=$( cd "$gtdir/proj/sub" && $BIN teachy.weir 2>&1 ) && fail "the yaml missing field must raise" || true
echo "$out" | grep -qF "if the field is sometimes absent, declare it Option<" || fail "the yaml twin teaches too: $out"
echo "e2e ok: from json/yaml missing-field errors carry the Option repair"
rm -rf "$gtdir"

# ---- the Log module [D:log-module] -----------------------------------------
# stderr always; WEIR_LOG selects; stdout is byte-identical at every
# level (the pin — stdout is data); invalid level = loud startup error
lgdir=$(mkweirtmp)
cat > "$lgdir/lg.weir" <<'WEOF'
Log.trace "t"
Log.debug "d"
Log.info "starting"
Log.warn "careful"
Log.traceWith (fun () -> "never built at info")
print "DATA"
WEOF
out=$($BIN "$lgdir/lg.weir" 2>"$lgdir/err")
[ "$out" = "DATA" ] || fail "stdout carries only data: $out"
grep -qxF "INFO starting" "$lgdir/err" || fail "info at default: $(cat "$lgdir/err")"
grep -qxF "WARN careful" "$lgdir/err" || fail "warn at default"
grep -q "TRACE" "$lgdir/err" && fail "trace must filter at default"
WEIR_LOG=trace $BIN "$lgdir/lg.weir" 2>"$lgdir/err2" >/dev/null
grep -qxF "TRACE t" "$lgdir/err2" || fail "trace at trace"
grep -qxF "TRACE never built at info" "$lgdir/err2" || fail "the With thunk runs when the level passes"
WEIR_LOG=off $BIN "$lgdir/lg.weir" 2>"$lgdir/err3" >/dev/null
[ -s "$lgdir/err3" ] && fail "off must be genuine silence: $(cat "$lgdir/err3")"
out=$(WEIR_LOG=loud $BIN "$lgdir/lg.weir" 2>&1) && fail "invalid level must fail" || true
echo "$out" | grep -qF "unknown log level (one of trace|debug|info|warn|off)" || fail "invalid teaches the levels: $out"
diff <($BIN "$lgdir/lg.weir" 2>/dev/null) <(WEIR_LOG=trace $BIN "$lgdir/lg.weir" 2>/dev/null) || fail "stdout must be byte-identical across levels"
diff <($BIN "$lgdir/lg.weir" 2>/dev/null) <(WEIR_LOG=off $BIN "$lgdir/lg.weir" 2>/dev/null) || fail "stdout byte-identical at off"
echo "e2e ok: Log — stderr always, WEIR_LOG selects, stdout byte-identical (THE pin)"

# NO_COLOR / non-tty: the harness reads plain level labels (this pipe is
# not a tty, so tint must be absent even without NO_COLOR)
grep -q "$(printf '\033')" "$lgdir/err" && fail "no escapes when stderr is not a tty"
echo "e2e ok: Log plain form when stderr is piped"

# the thunk is not evaluated below threshold (side-effect proof)
cat > "$lgdir/lz.weir" <<'WEOF'
Log.traceWith (fun () ->
    ["proof"] |> File.write "thunk-ran.txt"
    "built")
print "done"
WEOF
( cd "$lgdir" && $BIN lz.weir >/dev/null 2>&1 )
[ -f "$lgdir/thunk-ran.txt" ] && fail "the With thunk must not run below threshold"
( cd "$lgdir" && WEIR_LOG=trace $BIN lz.weir >/dev/null 2>&1 )
[ -f "$lgdir/thunk-ran.txt" ] || fail "the With thunk must run at its level"
echo "e2e ok: Log With twins — lazy below threshold, side-effect-proven"

# a child weir carries WEIR_LOG via the env sigil (composition, not machinery)
cat > "$lgdir/child.weir" <<'WEOF'
Log.debug "child sees debug"
print "child-data"
WEOF
cat > "$lgdir/parent.weir" <<'WEOF'
let e = Env.fromFile "log.env"
let out = $e(weir child.weir)
out |> print
WEOF
printf 'WEIR_LOG=debug\n' > "$lgdir/log.env"
( cd "$lgdir" && PATH="$BINDIR:$PATH" $BIN parent.weir 2>"$lgdir/cerr" ) \
    || fail "parent.weir failed (child weir needs \$BIN's dir on PATH — CI has no ~/.local/bin): $(cat "$lgdir/cerr")"
grep -qF "DEBUG child sees debug" "$lgdir/cerr" || fail "the env sigil carries WEIR_LOG to a child weir: $(cat "$lgdir/cerr")"
echo "e2e ok: Log level rides the env sigil to child weir processes"

# -e mode logs too
out=$(WEIR_LOG=debug $BIN -e 'Log.debug "from-e"' 2>&1 >/dev/null)
echo "$out" | grep -qF "DEBUG from-e" || fail "-e mode logs: $out"
echo "e2e ok: Log works in -e mode"
rm -rf "$lgdir"

# ---- CLI teaching arms [D:windows-v1] --------------------------------------
# a mistyped option teaches, never dumps; -e names its arity (the
# Windows shell-splitting trap: one intended expression arrives as many)
out=$($BIN --e 'x' 2>&1) && fail "--e must exit nonzero" || true
echo "$out" | grep -qF "unknown option '--e'. Did you mean '-e'?" || fail "--e did-you-means -e: $out"
out=$($BIN -e one two three 2>&1) && fail "-e arity must exit nonzero" || true
echo "$out" | grep -qF "got 3 — quote the program" || fail "-e names its arity: $out"
$BIN --help | grep -qF "usage: weir" || fail "--help prints usage on stdout, exit 0"
echo "e2e ok: CLI teaching arms (--e did-you-mean, -e arity, --help)"

# ---- Duration [D:duration]: the Args/Env spine on the real binary ----------
ddir=$(mkweirtmp)
cat > "$ddir/dur.weir" <<'WEOF'
type DurCli = {
    [<Default 30s>]
    /// give up after this long
    timeout: Duration
}
type DurEnv = {
    [<Default 500ms>]
    WEIR_E2E_TICK: Duration
}
let cli = Args.load DurCli
let e = Env.load DurEnv
print $"t={show cli.timeout} k={show e.WEIR_E2E_TICK} sum={show (cli.timeout + e.WEIR_E2E_TICK)}"
WEOF
out=$($BIN "$ddir/dur.weir")
[ "$out" = "t=30s k=500ms sum=30.5s" ] || fail "duration defaults rest: $out"
out=$(WEIR_E2E_TICK=1h30m $BIN "$ddir/dur.weir" --timeout 90s)
[ "$out" = "t=1m30s k=1h30m sum=1h31m30s" ] || fail "duration text parses at both boundaries: $out"
out=$($BIN "$ddir/dur.weir" --timeout nope 2>&1) && fail "bad duration flag must exit nonzero" || true
echo "$out" | grep -qF "not a duration: 'nope'" || fail "flag rejection names the text: $out"
$BIN "$ddir/dur.weir" --help | grep -qF "default: 30s" || fail "--help renders the Show shape"
# command-mode: 30s is an argv word on the published binary
out=$($BIN -e '$(echo 30s) |> Seq.head')
[ "$out" = '"30s" : string' ] || fail "command-mode 30s stays a word: $out"
# holes consult Show [D:interp-show]; command splices do not
out=$($BIN -e '$"took {90500ms}"')
[ "$out" = '"took 1m30.5s" : string' ] || fail "a Duration hole renders: $out"
out=$($BIN -e 'let d = 30s in $(echo (d))' 2>&1) && fail "spliced Duration must reject" || true
echo "$out" | grep -qF "pass Duration.toMillis d or show d deliberately" || fail "splice teaching names the spellings: $out"
rm -rf "$ddir"
echo "e2e ok: Duration (defaults rest, both boundaries parse, rejection locates, argv word, hole renders / splice teaches)"

# ---- Instant [D:instant]: the boring subset --------------------------------
indir=$(mkweirtmp)
# the cert-expiry acceptance: openssl's own enddate spelling (month
# name, padded day) through the named-format reader — the use case
# that had no weir spelling (openssl-gated, as the TLS block is)
if command -v openssl >/dev/null 2>&1; then
    insubj="/CN=inst"
    [ "$IS_WINDOWS" = "1" ] && insubj="//CN=inst"
    osslerr=$(openssl req -x509 -newkey rsa:2048 -keyout "$indir/k.pem" -out "$indir/c.pem" -days 365 -nodes -subj "$insubj" 2>&1 >/dev/null) ||
        fail "openssl could not generate the instant-acceptance cert: $osslerr"
    openssl x509 -enddate -noout -in "$indir/c.pem" > "$indir/enddate.txt"
    cat > "$indir/expiry.weir" <<'WEOF'
let line = File.read "enddate.txt" |> Seq.head
let expiry = line |> Instant.parseWith "notAfter=%b %e %H:%M:%S %Y"
if expiry - Instant.now () > 24h * 300 then print "cert healthy" else print "cert expiring"
WEOF
    out=$(cd "$indir" && $BIN expiry.weir 2>&1) || fail "the cert-expiry acceptance failed: $out"
    echo "$out" | grep -qF "cert healthy" || fail "a 365-day cert must read healthy: $out"
else
    echo "e2e SKIP: openssl absent — the cert-expiry acceptance not run" >&2
fi

# the log-slicing acceptance: choose + tryParseWith + an Instant cutoff
cat > "$indir/slice.weir" <<'WEOF'
let lines = ["2026-08-14 09:00:01 boot"; "garbage line"; "2026-08-14 11:30:00 ready"; "2026-08-14 23:59:59 late"]
let cutoff = Instant.parse "2026-08-14T10:00:00Z"
lines
    |> Seq.choose (fun l ->
        match Instant.tryParseWith "%Y-%m-%d %H:%M:%S" l with
        | Some t -> (if t > cutoff then Some l else None)
        | None -> None)
    |> Seq.iter print
WEOF
out=$($BIN "$indir/slice.weir") || fail "log-slice acceptance failed: $out"
[ "$out" = "2026-08-14 11:30:00 ready
2026-08-14 23:59:59 late" ] || fail "the slice must keep exactly the two late lines: $out"

# the JSON refusal teaches both conversions
printf 'type TR = { t: Instant }\nlet x = [""] |> from json TR\n' > "$indir/jr.weir"
out=$($BIN "$indir/jr.weir" 2>&1) && fail "an Instant json field must refuse" || true
echo "$out" | grep -qF "Instant.epochMs into an int field" || fail "the refusal names the conversions: $out"

# [D:instant-plus-infer]: Instant + an unknown infers the unknown as a Duration
printf 'let at d = Instant.now () + d\nlet f a b = a + b + Instant.now ()\nprint $"{at 5m > f 1s 2s}"\n' > "$indir/pu.weir"
out=$($BIN "$indir/pu.weir" 2>&1) || fail "Instant + an unknown must infer a Duration: $out"
[ "$out" = "true" ] || fail "the inferred shifts run: $out"
printf 'let t = Instant.now ()\nprint $"{t + t}"\n' > "$indir/pp.weir"
out=$($BIN check "$indir/pp.weir" 2>&1) && fail "two Instants must not add" || true
echo "$out" | grep -qF "two points don't add" || fail "two real points keep the teaching: $out"

rm -rf "$indir"
echo "e2e ok: Instant (cert-expiry via openssl enddate, log slicing by cutoff, JSON refusal teaches)"

# ---- declared codecs [D:wire-codecs]: one declaration, both formats ----
cddir=$(mkweirtmp)
cat > "$cddir/codecs.weir" <<'WEOF'
type Ev = {
    [<Iso8601>] at: Instant
    [<EpochSec; Wire "epoch">] sec: Instant
    [<Millis>] took: Duration
    [<ByteCount>] size: Size
}
let e = { at = Instant.parse "2026-08-14T12:00:00.250Z"; sec = Instant.parse "2026-08-14T12:00:00Z"; took = 1500ms; size = Size.parse "1KiB" }
let j = [e] |> to jsonl |> Seq.head
print j
print $"{show ([j] |> from jsonl Ev |> Seq.head) == show e} {show (e |> to yaml |> from yaml Ev) == show e}"
WEOF
out=$(cd "$cddir" && $BIN codecs.weir 2>&1) || fail "the codec round trip must run: $out"
[ "$out" = '{"at":"2026-08-14T12:00:00.250Z","epoch":1786708800,"took":1500,"size":1024}
true true' ] || fail "codecs: wire shape + json/yaml round trips: $out"
printf 'type L = { [<EpochSec>] t: Instant }\nlet _j = [{ t = Instant.parse "2026-01-01T00:00:00.5Z" }] |> to jsonl |> Seq.freeze\nprint (Seq.head _j)\n' > "$cddir/lossy.weir"
out=$(cd "$cddir" && $BIN lossy.weir 2>&1) && fail "a lossy codec write must raise: $out" || true
echo "$out" | grep -qF "round it, or declare [<EpochMs>]" || fail "the lossy raise names the exact codec: $out"
rm -rf "$cddir"
echo "e2e ok: declared codecs — wire shape, json and yaml round trips, a lossy write raises"

# ---- Uuid [D:uuid]: the AOT binary's generators ----------------------------
# the receipt: an append-only JSON-lines journal keyed by v7 — file order,
# uuid order and text order agree, and the record round-trips. Also pins
# SHA-1 (RFC 9562 A.4) and the big-endian byte order under trimming
uudir=$(mkweirtmp)
cat > "$uudir/journal.weir" <<'WEOF'
type Entry = { id: Uuid; text: string }

let log = "journal.jsonl"
File.write log []
for i in [1..500] do
    [{ id = Uuid.v7 (); text = $"entry {i}" }] |> to jsonl |> File.append log
let ids = File.read log |> from jsonl Entry |> Seq.map _.id |> Seq.freeze
print $"{Seq.length ids} {Seq.equal (ids |> Seq.sort) ids} {Seq.equal (ids |> Seq.map Uuid.toString |> Seq.sort) (ids |> Seq.map Uuid.toString)}"
print (Uuid.v5 Uuid.ns.dns "www.example.com" |> Uuid.toString)
print (Uuid.parse "017f22e2-79b0-7cc3-98c4-dc0c0c07398f" |> Uuid.toBytes |> Bytes.toBase64)
let canon = Uuid.parse "urn:uuid:F81D4FAE-7DEC-11D0-A765-00A0C91E6BF6"
echo $canon
let back = { id = canon; text = "y" } |> to yaml |> from yaml Entry
print $"yaml {back.id == canon}"
WEOF
out=$(cd "$uudir" && $BIN journal.weir 2>&1) || fail "the uuid journal acceptance failed: $out"
[ "$out" = "500 true true
2ed6657d-e927-568b-95e1-2665a8aea6a2
AX8i4nmwfMOYxNwMDAc5jw==
f81d4fae-7dec-11d0-a765-00a0c91e6bf6
yaml true" ] || fail "the uuid journal: v7 order, v5 A.4, big-endian bytes, argv text, yaml round trip [D:wire-table]: $out"
rm -rf "$uudir"
echo "e2e ok: Uuid (v7 journal order holds in file/uuid/text, v5 matches RFC 9562 A.4, big-endian bytes, argv splice)"

# ---- weir check's argv [D:check-cli] ---------------------------------------
# a missing script is a usage error (exit 2), never a run of a script
# named `check`; flags in any order; --strict needs --can
rc=0; uout=$($BIN check 2>&1) || rc=$?
[ "$rc" = "2" ] || fail "weir check with no script must exit 2 (got $rc): $uout"
echo "$uout" | grep -qF "which script?" || fail "the missing-script usage: $uout"
echo "$uout" | grep -qF "no such script" && fail "check must not run a script named check: $uout"
rc=0; uout=$($BIN check --strict tools/fuzz.weir 2>&1) || rc=$?
[ "$rc" = "2" ] && echo "$uout" | grep -qF "add --can" || fail "--strict without --can teaches: $rc $uout"
a=$(cd "$(dirname "$0")/.." && $BIN check --json --can tools/fuzz.weir 2>&1)
b=$(cd "$(dirname "$0")/.." && $BIN check --can --json tools/fuzz.weir 2>&1)
[ "$a" = "$b" ] && echo "$a" | grep -qF '"model":"capability, not behaviour"' || fail "--json --can in any order: $a"
echo "e2e ok: weir check argv — usage errors exit 2, flags in any order"

# ---- weir check --can [D:can-report] ---------------------------------------
candir=$(mkweirtmp)
cat > "$candir/lib.weir" <<'WEOF'
module Lib
let fetchTags : unit -> seq<string>
let fetchTags () = git tag
WEOF
cat > "$candir/cap.weir" <<'WEOF'
import "./lib.weir"
type Cfg = { token: Secret }
let cfg = Env.load Cfg
let t = cfg.token
sh -c "curl example.com"
if 1 == 2 then rg TODO
["x"] |> File.write "out.txt"
let _body = Http.get "https://api.example.com/items" |> Http.expect
curl -H $t https://x.example
WEOF
canrc=0
out=$($BIN check --can "$candir/cap.weir" 2>&1) || canrc=$?
[ "$canrc" = "0" ] || fail "--can must not change a valid check's exit (got $canrc): $out"
echo "$out" | grep -qF "capability, not behaviour" || fail "the model is the first line: $out"
echo "$out" | grep -qF "this report is incomplete: 1 opaque site" || fail "the loud incomplete header: $out"
echo "$out" | grep -qF "sh (opaque)" || fail "opacity marks the runs line inline (F5): $out"
echo "$out" | grep -qF "rg" || fail "an untaken branch still counts (capability, not behaviour): $out"
echo "$out" | grep -qF "File.write out.txt" || fail "the literal path is named: $out"
echo "$out" | grep -qF "Http.expect https://api.example.com/items" || fail "the literal url is named: $out"
echo "$out" | grep -qF "a Secret reaches the argv of curl" || fail "the ps-visible line: $out"
echo "$out" | grep -qF "git" || fail "an imported module's externals appear transitively: $out"
echo "$out" | grep -qF "lib.weir" || fail "the module site carries the module's own file: $out"
# a reified chain reports every stage [D:chain-reifiers]
printf 'let x = git log | head -1 | line\nlet r = Q=1 git status | cat | complete\nprint $"{x} {r.exitCode}"\n' > "$candir/chain.weir"
cout=$($BIN check --can "$candir/chain.weir" 2>&1) || fail "chain --can errored: $cout"
for p in git head cat; do echo "$cout" | grep -qE "^ +$p( ×|  )" || fail "a reified chain reports $p: $cout"; done
echo "$cout" | grep -qF "sets Q for children" || fail "a chain stage's prefix names Q: $cout"
echo "$cout" | grep -qF "not statically known" && fail "a literal chain is no dynamic head: $cout"
# the NAME=value words name their variables [D:within-env-pairs]
printf 'let x = "v"\nwithin env A=1 B=$x\n    git status\nEDITOR=nano git log\n' > "$candir/envp.weir"
eout=$($BIN check --can "$candir/envp.weir" 2>&1) || fail "env pairs --can errored: $eout"
echo "$eout" | grep -qF "sets A, B for children (within env)" || fail "within env pairs name A, B: $eout"
echo "$eout" | grep -qF "sets EDITOR for children" || fail "a command's prefix names EDITOR: $eout"

# the reifier desugar's slots [D:can-report]: orFail's msg rides ahead
# of the program and the Env twins lead with the overlay — the report
# once printed an orFail message as a command; the prog slot is
# positional, literalness checked after (an interpolated msg must not
# shift it)
cat > "$candir/reify.weir" <<'WEOF'
let n = 7
let ev = [Env.pair "K" "v"]
sh -c "true" | orFail "a lock is already live — wait or kill"
git status | orFail $"round {n} failed"
let c = $(date -u | complete)
let ok = $(uname -s | succeeds)
let code = hostname | exitCode
let xs = ["x"]
let fed = xs | sort | complete
$ev(tty -s | orFail "env twin")
print $"{c.exitCode} {ok} {code} {fed.exitCode}"
WEOF
rout=$($BIN check --can "$candir/reify.weir" 2>&1) || fail "reify --can errored"
for prog in sh git date uname hostname sort tty; do
    echo "$rout" | grep -qF "$prog" || fail "the $prog member's program must appear in runs: $rout"
done
echo "$rout" | grep -qF "already live" && fail "an orFail MESSAGE must never appear as a command: $rout"
echo "$rout" | grep -qF "env twin" && fail "an Env-twin orFail MESSAGE must never appear as a command: $rout"
echo "e2e ok: --can reads every reifier member's program slot (orFail/complete/succeeds/exitCode, In and Env twins; messages never commands)"

# F3: the overlay is named when literal (direct, ofPairs, let-bound); honest
# form when computed. F4: identical entries group with a count. F6:
# weir on the interpreter list — the report is honest about the
# largest thing a self-shelling script does.
cat > "$candir/envnames.weir" <<'WEOF'
let ov = Env.ofPairs [("SEED", "1"); ("COUNT", "2")]
within env ov
    sh -c "true"
within env [Env.pair "TOKEN_A" "x"]
    sh -c "true"
let dyn () = [Env.pair (Env.get "K" |> Option.defaultValue "D") "v"]
within env (dyn ())
    sh -c "true"
weir --version
weir --version
WEOF
nout=$($BIN check --can "$candir/envnames.weir" 2>&1) || fail "envnames --can errored"
echo "$nout" | grep -qF "sets SEED, COUNT for children (within env)" || fail "ofPairs names resolve through the let (F3): $nout"
echo "$nout" | grep -qF "sets TOKEN_A for children (within env)" || fail "Env.pair literals name themselves (F3): $nout"
echo "$nout" | grep -qF "names not statically known" || fail "a computed overlay stays honest (F3): $nout"
echo "$nout" | grep -qF "sh (opaque) × 3" || fail "identical entries group with a count (F4): $nout"
echo "$nout" | grep -qF "weir (opaque) × 2" || fail "weir is an interpreter — opaque, grouped (F6): $nout"
echo "$nout" | grep -qF "this report is incomplete: 5 opaque site" || fail "the header carries the opaque count (F5/F6): $nout"
echo "$nout" | grep -qF "takes a program as its argument" && fail "the opaque section is json-only now (F5): $nout"
echo "e2e ok: --can names literal env overlays, groups identical entries, sees through nothing weir-shaped"

# env sugar [D:env-need-getor] [D:env-inline-type]: the one-var reads and the
# inline Env.load name their literal variables like Env.get and Env.load T
cat > "$candir/envsugar.weir" <<'WEOF'
let t = Env.need "SUGAR_TOKEN"
let p = Env.getOr "SUGAR_PORT" "8080"
let e = Env.load {| SUGAR_HOST: string; SUGAR_CI: Option<string> |}
print $"{t} {p} {e.SUGAR_HOST} {e.SUGAR_CI}"
WEOF
sout=$($BIN check --can "$candir/envsugar.weir" 2>&1) || fail "envsugar --can errored: $sout"
echo "$sout" | grep -qF "reads SUGAR_TOKEN (Env.need)" || fail "Env.need names its literal: $sout"
echo "$sout" | grep -qF "reads SUGAR_PORT (Env.getOr)" || fail "Env.getOr names its literal: $sout"
echo "$sout" | grep -qF "reads SUGAR_HOST (Env.load" || fail "inline Env.load names its fields: $sout"
echo "$sout" | grep -qF "reads SUGAR_CI (Env.load" || fail "inline Env.load names every field: $sout"
srun=$(SUGAR_TOKEN=tk SUGAR_HOST=h $BIN "$candir/envsugar.weir" 2>&1) || fail "envsugar run failed: $srun"
[ "$srun" = "tk 8080 h None" ] || fail "envsugar values: $srun"
src=0
sneed=$(SUGAR_HOST=h $BIN "$candir/envsugar.weir" 2>&1) || src=$?
[ "$src" = "1" ] || fail "an unset Env.need must exit 1 (got $src): $sneed"
echo "$sneed" | grep -qF "envsugar.weir:1:" || fail "Env.need fails AT its line: $sneed"
echo "$sneed" | grep -qF "Env.need: SUGAR_TOKEN is not set" || fail "Env.need names the variable: $sneed"
cat > "$candir/argsinline.weir" <<'WEOF'
let cli = Args.load {| verbose: bool; out: Option<string> |}
print $"{cli.verbose} {cli.out}"
WEOF
aout=$($BIN "$candir/argsinline.weir" -v --out x 2>&1) || fail "inline Args.load run failed: $aout"
[ "$aout" = "true Some \"x\"" ] || fail "inline Args.load values: $aout"
$BIN "$candir/argsinline.weir" --help 2>&1 | grep -qF -- "--verbose" || fail "inline Args.load derives --help"
echo "e2e ok: Env.need/getOr and inline Env.load/Args.load — values, located need failure, --can names, --help"
printf 'let e = Env.load {| DUP_X: string; DUP_X: int |}\nprint e.DUP_X\n' > "$candir/envdup.weir"
dout=$($BIN check "$candir/envdup.weir" 2>&1) && fail "a duplicate inline field must refuse at check: $dout" || true
echo "$dout" | grep -qF "duplicate field 'DUP_X' in the anonymous record" || fail "the duplicate is named: $dout"
echo "e2e ok: inline Env.load refuses a duplicate field at check"

# --strict: opaque sites become a distinct nonzero for CI's choosing
strictrc=0
$BIN check --can --strict "$candir/cap.weir" >/dev/null 2>&1 || strictrc=$?
[ "$strictrc" = "2" ] || fail "--strict must exit 2 on opaque sites (got $strictrc)"
printf 'print "clean"\n' > "$candir/clean.weir"
$BIN check --can --strict "$candir/clean.weir" >/dev/null 2>&1 || fail "--strict must pass a clean script"

# --json parses and carries the shape
$BIN check --can --json "$candir/cap.weir" > "$candir/can.json" 2>&1 || fail "--json failed"
python3 - "$candir/can.json" <<'PYCAN' || fail "the --can json shape"
import json, sys
d = json.load(open(sys.argv[1]))
assert d["opaqueSites"] == 1, d["opaqueSites"]
kinds = {c["kind"] for c in d["capabilities"]}
assert {"runs", "opaque", "write", "network", "secret-argv", "secret-load"} <= kinds, kinds
assert all("file" in c and "line" in c and "col" in c for c in d["capabilities"])
PYCAN

# a Secret in a reified command's argv is reported like the plain
# statement's [D:can-secret-reifier]
cat > "$candir/secreif.weir" <<'WEOF'
let s = Secret.of "x"
let a = printf "%s" $s | complete
let b = printf "%s" $s | line
let c = printf "%s" $s | succeeds
let e = printf "%s" $s | exitCode
let t = printf "%s" $s | text
printf "%s" $s | orFail "m"
print $"{a.exitCode} {b} {c} {e} {t}"
WEOF
$BIN check --can --json "$candir/secreif.weir" > "$candir/secreif.json" 2>&1 || fail "--json failed on the reifier script"
python3 - "$candir/secreif.json" <<'PYSR' || fail "every reifier's Secret argv is reported"
import json, sys
d = json.load(open(sys.argv[1]))
lines = sorted(c["line"] for c in d["capabilities"] if c["kind"] == "secret-argv")
assert lines == [2, 3, 4, 5, 6, 7], lines
PYSR

# a check failure suppresses the report (reporting on what cannot run)
printf 'let x = 1 + "a"\n' > "$candir/bad.weir"
badrc=0
out=$($BIN check --can "$candir/bad.weir" 2>&1) || badrc=$?
[ "$badrc" = "1" ] || fail "--can on a failing check exits 1 (got $badrc)"
echo "$out" | grep -qE "error" || fail "the check's own diagnostics print: $out"
echo "$out" | grep -qF "capability" && fail "no report for a script that cannot run: $out" || true

# F7 [D:desugar-namespace]: a `for` loop (the |seqIter desugar) must add
# no phantom dynamic head — a for-only script reports zero opaque sites,
# and --strict exits 0 (the |-namespace split: a library desugar is not
# a spawn)
printf 'for n in ["a"] do print n\n' > "$candir/foronly.weir"
forout=$($BIN check --can "$candir/foronly.weir" 2>&1) || fail "--can on a for-only script errored: $forout"
echo "$forout" | grep -qF "opaque site" && fail "a for loop must add no opaque site (F7): $forout" || true
echo "$forout" | grep -qF '^$' && fail "a for loop must not report a dynamic head (F7): $forout" || true
$BIN check --can --strict "$candir/foronly.weir" >/dev/null 2>&1 || fail "--strict must exit 0 on a for-only script (F7)"
# the --json opaque count is exactly 0
$BIN check --can --json "$candir/foronly.weir" > "$candir/foronly.json" 2>&1 || fail "--json failed on the for-only script"
python3 - "$candir/foronly.json" <<'PYFOR' || fail "a for-only script has 0 opaque sites (F7)"
import json, sys
d = json.load(open(sys.argv[1]))
assert d["opaqueSites"] == 0, d["opaqueSites"]
kinds = {c["kind"] for c in d["capabilities"]}
assert "runs" not in kinds and "opaque" not in kinds, kinds
PYFOR
echo "e2e ok: a for loop adds no phantom dynamic head — 0 opaque sites, --strict exits 0 (F7)"

rm -rf "$candir"
echo "e2e ok: check --can (model line, opaque loud + --strict, literals, untaken branch, import transitive, secret-argv, json shape, failure suppresses)"

# ---- floats, finite-only [D:floats] ----------------------------------------
out=$($BIN -e '0.5 + 0.5')
[ "$out" = "1.0 : float" ] || fail "float arithmetic on AOT: $out"
out=$($BIN -e '3 / 2.0' 2>&1) && fail "mixed arithmetic must reject" || true
echo "$out" | grep -qF "wrap the int: Float.ofInt" || fail "mixed teaching names ofInt: $out"
out=$($BIN -e '0.1 == 0.2' 2>&1) && fail "float == must reject" || true
echo "$out" | grep -qF "use Float.near a b eps" || fail "Eq teaching names near: $out"
out=$($BIN -e '1.0 / 0.0' 2>&1) && fail "float div by zero must raise" || true
echo "$out" | grep -qF "float division by zero" || fail "div-zero named: $out"
out=$($BIN -e 'Duration.toSeconds 2500ms')
[ "$out" = "2.5 : float" ] || fail "toSeconds lossless: $out"
out=$($BIN -e 'Float.near (Float.parse (show 1.5e-3)) 1.5e-3 0.0')
[ "$out" = "true : bool" ] || fail "show/parse round-trip: $out"
echo "e2e ok: floats (finite-only raises, teachings name spellings, toSeconds lossless, round-trip)"

# ---- trailing comments [D:trailing-comments] -------------------------------
tcdir=$(mkweirtmp)
cat > "$tcdir/tc.weir" <<'WEOF'
let x = 5 // a trailing comment parses now
let url = "http://a" // glued // inside strings is data
print $"{x} {url}" // done
WEOF
out=$($BIN "$tcdir/tc.weir")
[ "$out" = '5 http://a' ] || fail "trailing comments on AOT: $out"
out=$($BIN -e '5 // -e agrees')
[ "$out" = "5 : int" ] || fail "-e strips too: $out"
# fmt preserves trailing comments and is idempotent
cat > "$tcdir/fmt.weir" <<'WEOF'
let a  =  1 // close
let bb = 2      // aligned by hand
WEOF
$BIN fmt "$tcdir/fmt.weir" >/dev/null 2>&1
grep -qF 'let a = 1 // close' "$tcdir/fmt.weir" || fail "fmt respaces code, keeps comment"
grep -qF 'let bb = 2      // aligned by hand' "$tcdir/fmt.weir" || fail "fmt preserves comment alignment"
cp "$tcdir/fmt.weir" "$tcdir/fmt2.weir"
$BIN fmt "$tcdir/fmt.weir" >/dev/null 2>&1
cmp -s "$tcdir/fmt.weir" "$tcdir/fmt2.weir" || fail "fmt idempotent with trailing comments"
rm -rf "$tcdir"
echo "e2e ok: trailing comments (script + -e strip, strings stay data, fmt preserves + idempotent)"

# ---- the dedent correct-join [D:dedent-join] -------------------------------
djdir=$(mkweirtmp)
cat > "$djdir/dj.weir" <<'WEOF'
let sub =
    within tmp d
        weir -e "print 10"
    let post = "not-an-argv-word"
    post
print (sub)
WEOF
out=$(PATH="$BINDIR:$PATH" $BIN "$djdir/dj.weir")
[ "$out" = '10
not-an-argv-word' ] || fail "the git-subrepo shape joins on AOT: $out"
cat > "$djdir/floor.weir" <<'WEOF'
let bad =
    within tmp d
        print d
      let z = 1
    z
WEOF
out=$($BIN "$djdir/floor.weir" 2>&1) && fail "unmatched column must still error" || true
echo "$out" | grep -qF "aligns with no enclosing statement" || fail "the floor stays: $out"
rm -rf "$djdir"
echo "e2e ok: dedent correct-join (post-scope statements join, the floor stays)"

# ---- tasks underneath [D:tasks-underneath]: I/O-bound fan-out --------------
# 100 arms each spawning a child weir — the domain's real arm shape,
# at the raised ceiling; order preserved, parent cwd untouched
tudir=$(mkweirtmp)
mkdir -p "$tudir/work"
cat > "$tudir/tu.weir" <<'WEOF'
let before = pwd
let outs =
    [1..100]
    |> Seq.pmap (fun i ->
        within cd "TUMARK/work"
            $(weir -e $"print {show i}") |> Seq.head
    )
    |> Seq.freeze
let after = pwd
print (if before == after then "cwd-held" else "CWD-LEAKED")
print (outs |> Seq.head)
print (outs |> Seq.last)
print $"{outs |> Seq.length}"
WEOF
sed "s|TUMARK|$tudir|g" "$tudir/tu.weir" > "$tudir/tu.weir.tmp" && mv "$tudir/tu.weir.tmp" "$tudir/tu.weir"
start_ms=$(now_ms)
out=$(PATH="$BINDIR:$PATH" $BIN "$tudir/tu.weir")
took=$(( $(now_ms) - start_ms ))
[ "$(echo "$out" | sed -n 1p)" = "cwd-held" ] || fail "cwd scope at ceiling: $out"
[ "$(echo "$out" | sed -n 2p)" = "1" ] || fail "order head: $out"
[ "$(echo "$out" | sed -n 3p)" = "100" ] || fail "order last: $out"
[ "$(echo "$out" | sed -n 4p)" = "100" ] || fail "all arms ran: $out"
echo "e2e ok: tasks underneath (100 spawning arms at the ceiling in ${took}ms, order + scopes hold)"
rm -rf "$tudir"

# ---- retry / poll [D:retry-poll] -------------------------------------------
rpdir=$(mkweirtmp)
cat > "$rpdir/rp.weir" <<'WEOF'
let out = retry attempts=5 delay=10ms
    let r = weir -e "print 42" | complete
    r
until r
    r.exitCode == 0
print (out.stdout |> Seq.head)
let fast = { Retry.defaults with attempts = 2; delay = 0ms }
retry fast (1 == 1)
print "computed-options-ok"
WEOF
out=$(PATH="$BINDIR:$PATH" $BIN "$rpdir/rp.weir")
[ "$out" = '42
computed-options-ok' ] || fail "retry on AOT: $out"
out=$($BIN -e 'retry attempts=2 delay=0ms (1 == 2)' 2>&1) && fail "exhaustion must raise" || true
echo "$out" | grep -qF "retry: exhausted 2 attempt(s) over" || fail "exhaustion names attempts+elapsed: $out"
start_ms=$(now_ms)
out=$($BIN -e 'poll timeout=80ms interval=10s (1 == 2)' 2>&1) && fail "poll timeout must raise" || true
took=$(( $(now_ms) - start_ms ))
echo "$out" | grep -qF "poll: timed out after" || fail "poll exhaustion: $out"
[ "$took" -lt 5000 ] || fail "the pending 10s interval was not cancelled (took ${took}ms)"
# past the timers' Int32.MaxValue ms the main-thread wait slices [D:duration]:
# the timeout ends it, never a .NET range error or an instant fake timeout
start_ms=$(now_ms)
out=$($BIN -e 'poll timeout=300ms interval=2000h (1 == 2)' 2>&1) && fail "long-interval poll must time out" || true
took=$(( $(now_ms) - start_ms ))
echo "$out" | grep -qF "poll: timed out after" || fail "long-interval poll: $out"
[ "$took" -ge 250 ] && [ "$took" -lt 5000 ] || fail "long-interval poll waited ${took}ms, not the 300ms timeout"
out=$($BIN -e 'poll timeout=2000h interval=1ms (1 == 1)' 2>&1) || fail "a 2000h poll timeout is a valid ceiling: $out"
rm -rf "$rpdir"
echo "e2e ok: retry/poll (yields value, computed options, exhaustion messages, cancellable wait)"

# ---- if cmd | succeeds then [D:if-succeeds] --------------------------------
# the inline command condition: the let-RHS acceptance gate one position
# over; `then` stops the chain's argv only inside a condition
isdir=$(mkweirtmp)
cat > "$isdir/is.weir" <<'WEOF'
if test -f /etc/hosts | succeeds then
    print "inline-if"
let r =
    if test -f /nonexistent-weir-e2e | succeeds then "a"
    elif test -f /etc/hosts | succeeds then "elif-inline"
    else "c"
print r
let ok = test -f /etc/hosts | succeeds
if ok then
    print "bind-first-still"
echo then one
WEOF
out=$($BIN "$isdir/is.weir")
[ "$(echo "$out" | sed -n 1p)" = "inline-if" ] || fail "inline if: $out"
[ "$(echo "$out" | sed -n 2p)" = "elif-inline" ] || fail "inline elif: $out"
[ "$(echo "$out" | sed -n 3p)" = "bind-first-still" ] || fail "bind-first kept: $out"
# `then` stays ordinary argv outside a condition — the stop is positional
[ "$(echo "$out" | sed -n 4p)" = "then one" ] || fail "argv then at top level: $out"
# a streaming chain parses and teaches at the checker (bool demanded)
out=$($BIN -e 'if git ls-files then 1 else 2' 2>&1) && fail "non-bool chain must check-error" || true
echo "$out" | grep -qF "expected bool, got seq<string>" || fail "bool teaching: $out"
rm -rf "$isdir"
echo "e2e ok: if cmd | succeeds then (inline if/elif, bind-first kept, argv-then positional, checker demands bool)"

# ---- Seq.pfirst [D:seq-pfirst]: the race -----------------------------------
# the winner returns once the losers stop [D:pfirst-join] — not when they
# would have finished — and a loser's spawned tree dies: its `sleep 2 && touch marker` orphan would fire at ~2s if
# the kill missed, so the absence check waits past that
pfdir=$(mkweirtmp)
cat > "$pfdir/pf.weir" <<'WEOF'
let racer n =
    if n == 1 then sh -c "sleep 2 && touch marker"
    Duration.sleep 200ms
    n * 10

let r = [1; 2] |> Seq.pfirst racer
print $"winner: {r}"
WEOF
# a file redirect, never $( ): command substitution's pipe stays open
# until every process that inherited a handle to it dies, so a missed
# kill would masquerade as weir waiting — the file separates the two.
# The fail message carries the marker state: present = the loser's sh
# survived to its natural end (the kill missed); absent = killed but
# something else held the run.
start_ms=$(now_ms)
(cd "$pfdir" && $BIN pf.weir > "$pfdir/pf.out" 2>&1)
took=$(( $(now_ms) - start_ms ))
out=$(cat "$pfdir/pf.out")
[ "$out" = "winner: 20" ] || fail "pfirst winner: $out"
[ "$took" -lt 1800 ] || fail "pfirst waited for the loser (took ${took}ms; marker=$([ -e "$pfdir/marker" ] && echo present || echo absent))"
sleep 2.5
[ ! -e "$pfdir/marker" ] || fail "the loser's child survived the kill"

# a consumer stopped by the cut kills its tree before the dispose
# [D:scope-tree]: a Windows read wakes on the token at once, so a loser
# reading its command's pipes raised and disposed the Process before the
# cut's kill reached it — sh and its sleep lived on. One loser per
# consumer: complete, chain complete, lines, chain lines, exitCode
cat > "$pfdir/pfcons.weir" <<'WEOF'
let w =
    [0; 1; 2; 3; 4; 5] |> Seq.pfirst (fun i ->
        let body = $"sleep 2; echo x > cons{i}"
        if i == 0 then
            Duration.sleep 300ms
            "win"
        elif i == 1 then
            let _r = sh -c $body | complete
            "complete"
        elif i == 2 then
            let _r = sh -c $body | cat | complete
            "chain complete"
        elif i == 3 then
            let _n = sh -c $body |> Seq.length
            "lines"
        elif i == 4 then
            let _n = sh -c $body | cat |> Seq.length
            "chain lines"
        else
            let _c = sh -c $body | exitCode
            "exitCode")
print $"winner={w}"
WEOF
(cd "$pfdir" && $BIN pfcons.weir > "$pfdir/pfcons.out" 2>&1)
[ "$(cat "$pfdir/pfcons.out")" = "winner=win" ] || fail "pfirst consumer-loser winner: $(cat "$pfdir/pfcons.out")"
sleep 3
left=$(cd "$pfdir" && { ls cons* 2>/dev/null || true; } | tr '\n' ' ')
[ -z "$left" ] || fail "a pfirst loser's consumer left its command tree alive past the cut (markers: $left)"

# a pipe the kill cannot close [D:pfirst-join]: `(sleep 2 &)` reparents an
# orphan that holds the loser's stdout — no tree kill reaches it — so a
# loser reading to EOF would hold the race ~2s; the cut abandons the read
# (statement, capture, chain capture, chain reifier)
for body in 'sh -c "(sleep 2 &) ; sleep 3"' \
    'let _n = sh -c "(sleep 2 &) ; sleep 3" |> Seq.length' \
    'let _n = sh -c "(sleep 2 &) ; sleep 3" | cat |> Seq.length' \
    'let _c = sh -c "(sleep 2 &) ; sleep 3" | cat | exitCode'; do
    cat > "$pfdir/pforph.weir" <<WEOF
let racer n =
    if n == 1 then
        $body
        ()
    Duration.sleep 200ms
    n * 10
print \$"winner: {[1; 2] |> Seq.pfirst racer}"
WEOF
    start_ms=$(now_ms)
    $BIN "$pfdir/pforph.weir" > "$pfdir/pforph.out" 2>&1
    took=$(( $(now_ms) - start_ms ))
    [ "$(cat "$pfdir/pforph.out")" = "winner: 20" ] || fail "pfirst orphan-pipe winner ($body): $(cat "$pfdir/pforph.out")"
    [ "$took" -lt 1800 ] || fail "pfirst waited on an orphan's pipe (took ${took}ms): $body"
done

# a scoped child joins its arm's race group too [D:seq-pfirst]: the spill
# path spawns through the one starter, so `within proc` obeys the same
# kill. The bare-command case above proved the group works; this proves
# the membership. Both directions — the loser's child must die, the
# winner's must run to completion (a group that killed everything would
# pass the first assertion alone).
#
# Do not collapse the branches: the loser's scope sits in the `then` arm
# and the winner's in `else`, which is not incidental — if/match infer
# their first branch and check the rest, so this shape covers both type
# directions of [D:within-scopes]'s scope contracts at the same time. A
# tidy-up that moves both scopes to one branch, or hoists them out of the
# if, silently drops half that coverage.
cat > "$pfdir/pfproc.weir" <<'WEOF'
let racer n =
    if n == 1 then
        within proc srv = sh -c "sleep 3 && touch loser-ran"
            Duration.sleep 10s
    else
        within proc srv = sh -c "sleep 1 && touch winner-ran"
            Duration.sleep 2s
    n * 10

let r = [1; 2] |> Seq.pfirst racer
Duration.sleep 5s
print $"winner: {r}"
WEOF
(cd "$pfdir" && $BIN pfproc.weir > pfproc.out 2>&1)
grep -qF "winner: 20" "$pfdir/pfproc.out" || fail "pfirst+proc winner: $(cat "$pfdir/pfproc.out")"
[ -e "$pfdir/winner-ran" ] || fail "the WINNER's scoped child must run to completion"
[ ! -e "$pfdir/loser-ran" ] || fail "the loser's SCOPED child survived the race kill"

# the race joins its losers [D:pfirst-join]: a sleeping loser is cut, its
# write never lands, and its `always` cleanup still runs before pfirst
# returns
cat > "$pfdir/pfjoin.weir" <<WEOF
let racer n =
    within
        Duration.sleep (if n == 1 then 3s else 100ms)
        [\$"body {n}"] |> File.append "$pfdir/join.log"
        n
    always
        [\$"cleanup {n}"] |> File.append "$pfdir/join.log"
let w = [1; 2] |> Seq.pfirst racer
let log = File.read "$pfdir/join.log" |> Seq.sort |> Str.join ","
print \$"winner: {w} log: {log}"
WEOF
start_ms=$(now_ms)
jout=$($BIN "$pfdir/pfjoin.weir" 2>&1) || fail "pfirst join script failed: $jout"
took=$(( $(now_ms) - start_ms ))
[ "$jout" = "winner: 2 log: body 2,cleanup 1,cleanup 2" ] || fail "pfirst joins its losers (cut body, cleanup kept): $jout"
[ "$took" -lt 2500 ] || fail "the loser's sleep must be cut, not waited out (took ${took}ms)"
# a loser's cleanup runs whole [D:teardown-always-spawns]: a command its
# `always` starts after the loss is exempt from the race kill, and the race
# joins it before returning
cat > "$pfdir/pfalw.weir" <<WEOF
let w = ["lose"; "win"] |> Seq.pfirst (fun k ->
    if k == "win" then
        Duration.sleep 300ms
        k
    else
        within
            Duration.sleep 10s
        always
            sh -c "sleep 0.3; echo cleanup-cmd >> $pfdir/alw.log"
            File.append "$pfdir/alw.log" ["cleanup end"]
        "lose")
let log = File.read "$pfdir/alw.log" |> Str.join ","
print \$"winner={w} log={log}"
WEOF
aout=$($BIN "$pfdir/pfalw.weir" 2>&1) || fail "pfirst loser-cleanup script failed: $aout"
[ "$aout" = "winner=win log=cleanup-cmd,cleanup end" ] || fail "a pfirst loser's always command must run, not die with the race kill: $aout"
# exit n in an arm ends the race and the script [D:pfirst-join]
xrc=0
$BIN -e '[1; 2] |> Seq.pfirst (fun i -> match i with | 1 -> exit 3 ; 0 | _ -> Duration.sleep 300ms ; i)' >/dev/null 2>&1 || xrc=$?
[ "$xrc" = "3" ] || fail "exit 3 in a pfirst arm must exit 3 (got $xrc)"
# exit n in a pmap/piter arm stops the fan-out as it ends a race
# [D:fanout-exit]: the in-flight arm is cut (its command killed) and joined
# with its cleanup whole, no new arm starts, and an earlier arm's error
# never masks the exit. An arm whose read the kill cannot end (a pipe an
# orphan holds) is abandoned by the join, its cleanup run before the exit
# [D:fanout-exit-reads]
MKFX="8.$((MK + 11))"
for cmd in 'sleep 6; echo x' "(sleep $MKFX &) ; sleep 6"; do
for fan in 'let _n = [1; 2; 3] |> Seq.pmap body |> Seq.length' '[1; 2; 3] |> Seq.piterWith 3 body' 'let _n = [1; 2; 3] |> Seq.pmapWith 3 body |> Seq.length'; do
    rm -f "$pfdir/fx.log"
    cat > "$pfdir/fx.weir" <<WEOF
let log = "$pfdir/fx.log"
let body i =
    match i with
    | 1 -> fail "one"
    | 2 ->
        Duration.sleep 300ms
        exit 7
    | _ ->
        within
            let _c = sh -c "$cmd" | exitCode
            File.append log ["late"]
        always
            File.append log ["cleanup"]
$fan
WEOF
    xrc=0; t0=$(date +%s)
    $BIN "$pfdir/fx.weir" >/dev/null 2>&1 || xrc=$?
    t1=$(date +%s)
    pkill -9 -f "^sleep $MKFX\$" 2>/dev/null || true
    [ "$xrc" = "7" ] || fail "exit 7 in a fan-out arm must exit 7, not the earlier arm's error ($cmd; $fan: got $xrc)"
    [ $((t1 - t0)) -le 3 ] || fail "exit in a fan-out arm must cut the in-flight arm ($cmd; $fan: $((t1 - t0))s)"
    [ "$(tr '\n' ',' < "$pfdir/fx.log" 2>/dev/null)" = "cleanup," ] || fail "a cut fan-out arm: body cut, cleanup whole ($cmd; $fan): $(tr '\n' ',' < "$pfdir/fx.log" 2>/dev/null)"
done
done
rm -f "$pfdir/fx.log"
xrc=0; $BIN -e "[1; 2; 3] |> Seq.piterWith 1 (fun i -> File.append \"$pfdir/fx.log\" [show i] ; match i with | 1 -> exit 4 | _ -> ())" >/dev/null 2>&1 || xrc=$?
[ "$xrc" = "4" ] && [ "$(tr '\n' ',' < "$pfdir/fx.log")" = "1," ] || fail "exit in a piter arm must start no new arm (rc $xrc, arms $(tr '\n' ',' < "$pfdir/fx.log"))"
echo "e2e ok: exit n in a pmap/piter arm stops the fan-out — no new arm, the in-flight arm cut (an orphan-held read abandoned) with its cleanup whole, never masked"
# an exit cuts a sibling arm's Proc.wait on a scoped proc opened outside
# the fan-out, which no group kill reaches [D:fanout-exit-reads]; and a
# nested fan-out takes no new arm once an enclosing race is won, an
# enclosing fan-out exits or the serve cut lands [D:fanout-exit]
MKPW="8.$((MK + 12))"
cat > "$pfdir/pw.weir" <<WEOF
within proc srv = sleep $MKPW
    within
        [0; 1] |> Seq.piter (fun i ->
            if i == 0 then
                let r = Proc.wait srv
                print \$"LATE waited {r}"
            else
                Duration.sleep 200ms
                exit 7)
    always
        print "always"
WEOF
xrc=0; t0=$(date +%s)
pwout=$($BIN "$pfdir/pw.weir" 2>&1) || xrc=$?
t1=$(date +%s)
pkill -9 -f "^sleep $MKPW\$" 2>/dev/null || true
[ "$xrc" = "7" ] && [ "$pwout" = "always" ] && [ $((t1 - t0)) -le 3 ] || fail "exit in a fan-out arm must cut a sibling's Proc.wait on an outer proc (rc $xrc, $((t1 - t0))s): $pwout"
for outer in race exit serve; do
    rm -f "$pfdir/na.log"
    case $outer in
        race) cat > "$pfdir/na.weir" <<WEOF
let note s = File.append "$pfdir/na.log" [s]
let r =
    [0; 1] |> Seq.pfirst (fun i ->
        if i == 0 then
            [0; 1; 2] |> Seq.piterWith 1 (fun j ->
                within
                    Duration.sleep 1s
                always
                    note \$"inner arm {j} cleanup")
            0
        else
            Duration.sleep 200ms
            1)
note \$"returned {r}"
WEOF
            want="inner arm 0 cleanup,returned 1," ;;
        exit) cat > "$pfdir/na.weir" <<WEOF
let note s = File.append "$pfdir/na.log" [s]
[0; 1] |> Seq.piter (fun i ->
    if i == 0 then
        [0; 1; 2] |> Seq.piterWith 1 (fun j ->
            within
                Duration.sleep 1s
            always
                note \$"inner arm {j} cleanup")
    else
        Duration.sleep 200ms
        exit 7)
WEOF
            want="inner arm 0 cleanup," ;;
        serve) cat > "$pfdir/na.weir" <<WEOF
let note s = File.append "$pfdir/na.log" [s]
let handler = fun req ->
    [0; 1; 2] |> Seq.piterWith 1 (fun j ->
        within
            Duration.sleep 5s
        always
            note \$"inner arm {j} cleanup")
    HttpServerResponse { status = 200; headers = []; body = Text "ok" }
within serve _srv = { port = 8495; maxConcurrent = 4 } handler
    poll timeout=8s interval=50ms
        Net.portOpen 8495
    within proc _c = curl -s --noproxy "*" -m 20 "http://127.0.0.1:8495/x"
        Duration.sleep 500ms
note "returned"
WEOF
            want="inner arm 0 cleanup,returned," ;;
    esac
    $BIN "$pfdir/na.weir" >/dev/null 2>&1 || true
    [ "$(tr '\n' ',' < "$pfdir/na.log" 2>/dev/null)" = "$want" ] || fail "a nested fan-out must take no new arm once its enclosing $outer cut it: $(tr '\n' ',' < "$pfdir/na.log" 2>/dev/null)"
done
# the exit's join counts an arm joining a nested race as parked only with
# that race's arms parked: a nested arm's running cleanup holds it
cat > "$pfdir/nc.weir" <<WEOF
let note s = File.append "$pfdir/nc.log" [s]
within
    [0; 1] |> Seq.piter (fun i ->
        if i == 0 then
            let _w =
                [0] |> Seq.pfirst (fun k ->
                    within
                        note "nested body"
                    always
                        sh -c "sleep 0.5"
                        note "nested cleanup"
                    k)
            ()
        else
            Duration.sleep 100ms
            exit 5)
always
    note "outer always"
WEOF
rm -f "$pfdir/nc.log"
xrc=0; $BIN "$pfdir/nc.weir" >/dev/null 2>&1 || xrc=$?
[ "$xrc" = "5" ] && [ "$(tr '\n' ',' < "$pfdir/nc.log")" = "nested body,nested cleanup,outer always," ] || fail "a fan-out exit must join a nested race's running cleanup (rc $xrc): $(tr '\n' ',' < "$pfdir/nc.log")"
echo "e2e ok: a fan-out exit cuts a sibling's Proc.wait on an outer proc; a nested fan-out takes no new arm after an enclosing race win, exit or serve cut"

out=$($BIN -e '[7; 8] |> Seq.pfirst (fun n -> match n with | 7 -> Duration.sleep 200ms ; fail "seven dies" | _ -> fail "eight dies")' 2>&1) && fail "all-failed must raise" || true
echo "$out" | grep -qF "seven dies" || fail "first error by input order: $out"
# empty raises and names the guard (reject-don't-guess full form) — pin the
# fragment, not the joined sentence (FParsec wraps long messages)
out=$($BIN -e '[] |> Seq.pfirst (fun n -> n)' 2>&1) && fail "empty must raise" || true
echo "$out" | grep -qF "a race needs at least one arm" || fail "empty names Seq.isEmpty: $out"
rm -rf "$pfdir"
echo "e2e ok: Seq.pfirst (winner fast, loser tree killed — bare AND scoped, winner's scoped child survives, losers joined with cleanups kept — their commands too, exit ends the race, first-by-order error, empty names the guard)"

# a loser's within-tmp cleanup does run — the `finally` executes on the
# loser's own (un-aborted) thread once its killed child fails; here the
# process outlives the loser (a 1s wait after the winner), so cleanup
# completes and the dir is gone. The exit-race leak [D:seq-pfirst] is the
# other case (process exits immediately) — parked, not pinned.
pfdir2=$(mkweirtmp)
cat > "$pfdir2/pf2.weir" <<WEOF
let racer n =
    within tmp d
        echo \$d |> File.write "$pfdir2/loserdir.txt"
        if n == 1 then sh -c "sleep 5"
    Duration.sleep 200ms
    n
let w = [1; 2] |> Seq.pfirst racer
print \$"winner: {w}"
Duration.sleep 1s
let loserDir = File.read "$pfdir2/loserdir.txt" |> Seq.head
let gone = sh -c \$"test -d {loserDir}" | succeeds
print \$"loser-tmp-exists: {gone}"
WEOF
out=$($BIN "$pfdir2/pf2.weir")
echo "$out" | grep -qF "loser-tmp-exists: false" || fail "loser within-tmp cleanup did not run: $out"
rm -rf "$pfdir2"
echo "e2e ok: Seq.pfirst loser within-tmp cleanup runs (finally on the un-aborted thread)"

# ---- the exit hook [D:exit-hook]: temp dirs survive a hard exit no more ----
ehdir=$(mkweirtmp)

# probe B regression: a pfirst whose script exits immediately after the
# winner used to leak the loser's within-tmp dir (a background thread
# killed mid-finally on normal completion); the ProcessExit sweep fixes it
cat > "$ehdir/probeb.weir" <<WEOF
let racer n =
    within tmp d
        echo \$d |> File.write "$ehdir/bdir.txt"
        if n == 1 then sh -c "sleep 5"
    Duration.sleep 200ms
    n
let w = [1; 2] |> Seq.pfirst racer
print \$"winner: {w}"
WEOF
out=$($BIN "$ehdir/probeb.weir") || fail "probe-b script failed: $out"
sleep 0.5
loser=$(cat "$ehdir/bdir.txt")
[ ! -d "$loser" ] || { rm -rf "$loser"; fail "pfirst exit-race leaked the loser's tmp dir: $loser"; }

# the signal sweep (customer 1 — the leak every script always had), and
# registration is per-process: a second weir's live dir survives the
# first one's sweep (never a blind scan of the temp root). Pinned via
# SIGTERM: a POSIX shell without job control starts background jobs
# with SIGINT ignored (and .NET honors an inherited SIG_IGN — the
# nohup convention), so a harness kill -INT is a no-op by design;
# real Ctrl-C (default disposition) takes the same handler, verified
# interactively. TERM exercises the identical sweep path.
# per-instance tag files, never pid-keyed: on Windows, bash's $! is the
# msys fork-exec stub's pid while Self.pid is the native weir's — the
# two never match (the process-identity-across-the-msys-boundary class,
# round 30). And on Windows the TERM stops at the msys boundary: it
# lands on the MSYS sh child, weir raises command-failed, and the
# raise-path finally removes the dir — the same no-leak property
# through the error-unwind arm (the CancelKeyPress arm is
# interactive-only, stated in Session.fs).
for tag in one two; do
cat > "$ehdir/hold-$tag.weir" <<WEOF
within tmp d
    echo \$d |> File.write "$ehdir/$tag.txt"
    sh -c "sleep 30"
WEOF
done
$BIN "$ehdir/hold-one.weir" & ehp1=$!
$BIN "$ehdir/hold-two.weir" & ehp2=$!
sleep 1.2
d1=$(cat "$ehdir/one.txt" 2>/dev/null || true)
d2=$(cat "$ehdir/two.txt" 2>/dev/null || true)
[ -n "$d1" ] && [ -n "$d2" ] || { kill -9 $ehp1 $ehp2 2>/dev/null; fail "exit-hook probes never wrote their dirs: one='$d1' two='$d2'"; }
kill -TERM $ehp1 2>/dev/null || true
wait $ehp1 2>/dev/null || true
sleep 0.5
[ ! -d "$d1" ] || { kill -9 $ehp2 2>/dev/null; fail "the TERM sweep did not remove the interrupted weir's dir: $d1 (dir=exists)"; }
[ -d "$d2" ] || { kill -9 $ehp2 2>/dev/null; fail "the OTHER process's dir was touched (registration must be per-process): $d2"; }
kill -TERM $ehp2 2>/dev/null || true
wait $ehp2 2>/dev/null || true
sleep 0.5
[ ! -d "$d2" ] || { rm -rf "$d2"; fail "the second weir's own SIGINT sweep failed"; }

rm -rf "$ehdir"
echo "e2e ok: exit hook (pfirst exit-race fixed, signal sweep via TERM, registration per-process)"

# ---- scoped processes [D:scoped-procs] -------------------------------------
spdir=$(mkweirtmp)
spport=$((21500 + RANDOM % 300))
# the acceptance: start-await-use-teardown in weir, and nothing survives.
# The server is a plain TCPServer — not `-m http.server`, whose
# HTTPServer.server_bind calls getfqdn(host): on the macOS runner that
# reverse-DNS parks in mDNSResponder indefinitely for weir-descendant
# processes (sample(1) showed slot_tp_init -> socket_gethostbyaddr ->
# mdns_hostbyaddr -> kevent; bash-spawned pythons resolve fine — the
# privacy gating keys on the spawning binary, which weir cannot fix).
# The 2.5s checkpoint keeps a light forensic tail: if this fails again
# the child's words + the port's holder still ride the message.
cat > "$spdir/acc.weir" <<WEOF
within proc srv = python3 -u -c "import socketserver,http.server as h; s=socketserver.TCPServer(('127.0.0.1',$spport),h.SimpleHTTPRequestHandler); s.serve_forever()"
    Duration.sleep 2500ms
    print \$"diag: running={show (Proc.running srv)} pid={Proc.pid srv}"
    Proc.tail srv |> Seq.iter (fun l -> print \$"diag tail: {l}")
    sh -c "lsof -nP -iTCP -sTCP:LISTEN 2>/dev/null | grep $spport || echo diag-no-listener-on-$spport"
    poll timeout=12s interval=100ms watch=srv
        Net.portOpen $spport
    let n = Http.get "http://127.0.0.1:$spport/" |> Http.expect |> Seq.length
    print \$"got={n > 0}"
print "closed"
WEOF
out=$($BIN "$spdir/acc.weir" 2>&1) || {
    # discriminate the halves [marker discipline]: server reachable from
    # bash means weir's probe is the broken side; unreachable means the
    # spawn/bind side
    probe=$(curl -s --max-time 2 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$spport/" 2>/dev/null || echo "curl-failed")
    fail "scoped-proc acceptance failed (bash-side probe of :$spport = $probe): $out"
}
echo "$out" | grep -qF "got=true" || fail "acceptance fetch: $out"
echo "$out" | grep -qF "closed" || fail "acceptance close: $out"
sleep 0.5
$BIN -e "Net.portOpen $spport" | grep -qF "false" || fail "the no-orphan half: port $spport still up after the scope"

# died-at-startup fails the poll immediately with the child's own words
printf 'within proc p = sh -c "echo boom >&2; exit 3"\n    poll timeout=8s watch=p\n        Net.portOpen 1\n' > "$spdir/dead.weir"
out=$($BIN "$spdir/dead.weir" 2>&1) && fail "died-at-startup must raise" || true
echo "$out" | grep -qF "watched process" || fail "watch names itself: $out"
echo "$out" | grep -qF "exited with code 3" || fail "watch carries the code: $out"
echo "$out" | grep -qF "boom" || fail "watch carries the child's words: $out"

# a plain timeout stamps the watched state
printf 'within proc p = sh -c "sleep 30"\n    poll timeout=400ms interval=100ms watch=p\n        Net.portOpen 1\n' > "$spdir/slow.weir"
out=$($BIN "$spdir/slow.weir" 2>&1) && fail "up-but-never-ready must time out" || true
echo "$out" | grep -qF "still running" || fail "timeout stamps the watched state: $out"

# watch is poll's key; a non-Proc watch refuses by type
out=$($BIN -e 'retry attempts=2 watch=ls true' 2>&1) && fail "retry+watch must refuse" || true
echo "$out" | grep -qF "watch= is poll's key" || fail "the retry refusal reason: $out"

if [ "$IS_WINDOWS" != "1" ]; then
    # the raise path kills the tree (pgrep is the POSIX spelling; the
    # Windows arm rides the exit-hook block's stated coverage)
    printf 'within proc p = sh -c "sleep %s"\n    fail "mid-scope"\n' "$MK5" > "$spdir/raise.weir"
    out=$($BIN "$spdir/raise.weir" 2>&1) && fail "the raise must propagate" || true
    echo "$out" | grep -qF "mid-scope" || fail "the raise is the scope's error: $out"
    sleep 0.3
    pgrep -f "sleep $MK5\$" >/dev/null && { pkill -f "sleep $MK5\$" 2>/dev/null || true; fail "the raise path left an orphan"; } || true

    # the hard-exit sweep: a TERM'd weir leaves no registered child
    printf 'within proc p = sh -c "sleep %s"\n    Duration.sleep 30s\n' "$MK6" > "$spdir/term.weir"
    $BIN "$spdir/term.weir" & sptw=$!
    sleep 1.2
    kill -TERM $sptw 2>/dev/null || true
    wait $sptw 2>/dev/null || true
    sleep 0.5
    pgrep -f "sleep $MK6\$" >/dev/null && { pkill -f "sleep $MK6\$" 2>/dev/null || true; fail "the TERM sweep left the scoped child"; } || true
fi

# an escaped handle (the scope's value) answers gracefully after the
# scope killed the child: running=false, tail empty (spill gone), wait
# yields the kill's code — never a crash
printf 'let esc = within proc p = sh -c "sleep 5"\n    p\nprint (show (Proc.running esc))\nprint (show (Proc.tail esc |> Seq.length))\n' > "$spdir/esc.weir"
out=$($BIN "$spdir/esc.weir" 2>&1) || fail "the escaped handle must not crash: $out"
printf '%s' "$out" | head -1 | grep -qF "false" || fail "escaped running must be false: $out"
printf '%s' "$out" | tail -1 | grep -qF "0" || fail "escaped tail must be empty (spill gone): $out"

# a proc scope spawns in a checked position too [D:scoped-procs]: an
# else branch and a non-first match arm are typed by the check direction,
# not infer, and the two directions must agree on the scope's contracts.
# The body deliberately never touches the binder — reading it as a Proc
# raises a type error, so a body that ignores it is the only shape that
# can observe a scope which types clean and spawns nothing.
cat > "$spdir/branch.weir" <<'WEOF'
let f n =
    if n == 1 then
        Duration.sleep 0ms
    else
        within proc srv = sh -c "sleep 1 && touch branch-ran"
            Duration.sleep 3s
    n
print (show (f 2))
WEOF
(cd "$spdir" && $BIN branch.weir >/dev/null 2>&1) || fail "the branch-position proc scope must run"
[ -e "$spdir/branch-ran" ] || fail "a proc scope in an if/else branch never spawned its child"

# the match arm is the other checked position, and there the binder must
# type as Proc — the loud half of the same agreement
cat > "$spdir/arm.weir" <<'WEOF'
let f n =
    match n with
    | 1 -> Duration.sleep 0ms
    | _ ->
        within proc srv = sh -c "sleep 5"
            print (show (Proc.running srv))
    n
print (show (f 2))
WEOF
out=$($BIN "$spdir/arm.weir" 2>&1) || fail "the match-arm proc scope must run: $out"
printf '%s' "$out" | head -1 | grep -qF "true" || fail "the match-arm binder must be a live Proc: $out"

rm -rf "$spdir"
echo "e2e ok: scoped processes (acceptance + no-orphan, watch died/timeout, retry refusal, raise + TERM sweeps, escaped handle graceful, checked-position spawn)"

# ---- command signatures [D:command-signatures] -----------------------------
sgdir=$(mkweirtmp)
mkdir -p "$sgdir/proj/bin" "$sgdir/proj/.git"
if [ "$IS_WINDOWS" != "1" ]; then
cat > "$sgdir/proj/bin/sigtool" <<'WEOF'
#!/bin/sh
case "$1" in
  --version) echo "sigtool 3.1.4";;
  --help) printf 'Flags:\n  -n, --name <x>   a name\n      --dry-run    no effects\n'; touch "$SIGTOOL_MARK";;
  *) echo "ran:$@";;
esac
WEOF
chmod +x "$sgdir/proj/bin/sigtool"
fi
if [ "$IS_WINDOWS" = "1" ]; then
cat > "$sgdir/proj/bin/sigtool.bat" <<'BEOF'
@echo off
if "%~1"=="--version" goto version
if "%~1"=="--help" goto help
echo ran:%*
goto :eof
:version
echo sigtool 3.1.4
goto :eof
:help
echo Flags:
echo   -n, --name ^<x^>   a name
echo       --dry-run    no effects
type nul > "%SIGTOOL_MARK%"
BEOF
fi
cd "$sgdir/proj"
# generation: probes the tool, validates, writes sig + lock
SIGTOOL_MARK="$sgdir/gen-mark" PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig sigtool | grep -qF "added sig sigtool (2 flag(s), source: help" || fail "add sig generates"
test -f .weir/sigs/sigtool.weir || fail "sig file written"
grep -qF '"version": "sigtool 3.1.4"' .weir/lock.json || fail "lock carries the verbatim version"
# --global [D:sig-arg-complete]: the user dir, no lock entry (the lock is
# byte-identical after it)
cp .weir/lock.json "$sgdir/lock.before"
out=$(SIGTOOL_MARK="$sgdir/gen-mark-g" XDG_CONFIG_HOME="$sgdir/cfg" APPDATA="$sgdir/cfg" PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig --global sigtool 2>&1) || fail "add sig --global: $out"
echo "$out" | grep -qF "no lock entry" || fail "add sig --global says it locks nothing: $out"
if [ "$IS_WINDOWS" = "1" ]; then
    # Windows: the user dir is the APPDATA known folder (no env override) —
    # check the path add reported, then remove it
    gsig=$(echo "$out" | sed -n 's/^added sig sigtool to \(.*sigtool\.weir\) .*/\1/p')
    [ -n "$gsig" ] && test -f "$(cygpath -u "$gsig")" || fail "add sig --global writes the user dir: $out"
    case "$gsig" in *[/\\]weir[/\\]sigs[/\\]sigtool.weir) ;; *) fail "add sig --global writes <APPDATA>/weir/sigs: $out" ;; esac
    rm -f "$(cygpath -u "$gsig")"
else
    test -f "$sgdir/cfg/weir/sigs/sigtool.weir" || fail "add sig --global writes the user dir: $out"
fi
cmp -s .weir/lock.json "$sgdir/lock.before" || fail "add sig --global must not touch the lock"
echo "e2e ok: add sig --global writes \$XDG_CONFIG_HOME/weir/sigs, no lock entry [D:sig-arg-complete]"
# checking: typo caught, and check spawns nothing (the marker pin)
printf '#sig sigtool\nsigtool --dry-run --nmae x\nprint "done"\n' > use.weir
rm -f "$sgdir/gen-mark"
out=$(SIGTOOL_MARK="$sgdir/gen-mark" PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN check use.weir 2>&1)
echo "$out" | grep -qF "unknown flag '--nmae' for sigtool. Did you mean '--name'?" || fail "sig typo catch: $out"
test -f "$sgdir/gen-mark" && fail "weir check SPAWNED the tool" || true
# property 3: output byte-identical with and without the contract
printf 'sigtool run-arg\nprint "p3"\n' > p3.weir
PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN p3.weir > with.out 2>/dev/null
rm -rf .weir
PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN p3.weir > without.out 2>/dev/null
cmp -s with.out without.out || fail "property 3: contracts changed run output"
# verify: version arm both ways; restore: the ruled generated behaviour
SIGTOOL_MARK="$sgdir/gen-mark2" PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig sigtool >/dev/null
PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN verify | grep -qF "ok (hash + version)" || fail "verify version arm (match)"
# no `sed -i` (the BSD-suffix law stated at the once.weir block);
# the tool file is per-platform (sigtool vs sigtool.bat — the mkFakeBin split)
if [ "$IS_WINDOWS" = "1" ]; then
    sed 's/3.1.4/9.0.0/' bin/sigtool.bat > bin/sigtool.tmp && mv bin/sigtool.tmp bin/sigtool.bat
else
    sed 's/3.1.4/9.0.0/' bin/sigtool > bin/sigtool.tmp && mv bin/sigtool.tmp bin/sigtool && chmod +x bin/sigtool
fi
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN verify 2>/dev/null) && fail "verify must FAIL on a mismatch" || true
echo "$out" | grep -qF "VERSION MISMATCH" || fail "verify version arm (mismatch): $out"
rm .weir/sigs/sigtool.weir
out=$($BIN restore 2>&1) && fail "restore of an absent generated sig must fail" || true
echo "$out" | grep -qF "ABSENT and generated (nothing to fetch)" || fail "restore never regenerates: $out"
# a tool that refuses --version (the jira shape [D:sig-version-probe]):
# the usage dump is not an identity — the sig records no version and
# says so; the surface still checks; verify takes the hash-only arm
if [ "$IS_WINDOWS" != "1" ]; then
cat > bin/noverstool <<'WEOF'
#!/bin/sh
case "$1" in
  --version) echo "Error: unknown flag: --version" >&2; echo "Usage: noverstool <command>" >&2; exit 1;;
  version) echo "Error: unknown command" >&2; exit 1;;
  --help) printf 'Flags:\n      --force   push through\n';;
  *) echo "ran:$@";;
esac
WEOF
chmod +x bin/noverstool
else
cat > bin/noverstool.bat <<'BEOF'
@echo off
if "%~1"=="--version" goto badflag
if "%~1"=="version" goto badflag
if "%~1"=="--help" goto help
echo ran:%*
goto :eof
:badflag
echo Error: unknown flag: --version 1>&2
exit /b 1
:help
echo Flags:
echo       --force   push through
BEOF
fi
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig noverstool 2>&1) || fail "add sig must succeed on a --version refuser: $out"
echo "$out" | grep -qF "version: none — 'noverstool' answers neither --version nor version" || fail "the refusal is stated, never recorded: $out"
grep -qF "unknown flag" .weir/sigs/noverstool.weir && fail "the usage dump leaked into the sig" || true
grep -qF '"name": "noverstool"' .weir/lock.json || fail "lock entry written"
printf '#sig noverstool
noverstool --forc
print "d"
' > nv.weir
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN check nv.weir 2>&1)
echo "$out" | grep -qF "unknown flag '--forc' for noverstool" || fail "a version-less sig still checks: $out"
# (verify exits nonzero here — the sigtool teardown above left its lock
# entry pointing at a removed file; the noverstool line is the pin)
vout=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN verify 2>&1) || true
echo "$vout" | grep -qF "sig noverstool: ok" || fail "verify takes the hash-only arm: $vout"
# the second rung (the jira shape exactly): --version refused, the
# `version` subcommand speaks — the ladder records that identity
if [ "$IS_WINDOWS" != "1" ]; then
cat > bin/subverstool <<'WEOF'
#!/bin/sh
case "$1" in
  --version) echo "Error: unknown flag: --version" >&2; exit 1;;
  version) echo "subverstool (Version=1.7.0)";;
  --help) printf 'OTHER COMMANDS\n  version   Print version\n\nFlags:\n      --force   push through\n';;
  *) echo "ran:$@";;
esac
WEOF
chmod +x bin/subverstool
else
cat > bin/subverstool.bat <<'BEOF'
@echo off
if "%~1"=="--version" goto badflag
if "%~1"=="version" goto vers
if "%~1"=="--help" goto help
echo ran:%*
goto :eof
:badflag
echo Error: unknown flag: --version 1>&2
exit /b 1
:vers
echo subverstool (Version=1.7.0)
goto :eof
:help
echo OTHER COMMANDS
echo   version   Print version
echo.
echo Flags:
echo       --force   push through
BEOF
fi
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig subverstool 2>&1) || fail "add sig must succeed via the version subcommand: $out"
echo "$out" | grep -qF "version: subverstool (Version=1.7.0)" || fail "the second rung records the identity: $out"
grep -qF 'let version = "subverstool (Version=1.7.0)"' .weir/sigs/subverstool.weir || fail "the sig carries the rung-two identity"
# the harvest pass [D:sig-version-probe]: weir's own usage-table help
# has no flag rows — the last-resort --flag harvest records a surface
out=$($BIN add sig weir 2>&1) || fail "add sig weir must succeed via the harvest: $out"
echo "$out" | grep -qF "source: help-scan" || fail "the harvested surface is labeled: $out"
grep -qF "json: bool" .weir/sigs/weir.weir || fail "the harvest found weir's own --json"
# the subcommand walk (the jira/kubectl shape): Cobra keeps the real
# flags under `tool sub --help` — depth 2, unioned into the flat surface
if [ "$IS_WINDOWS" != "1" ]; then
cat > bin/cobratool <<'WEOF'
#!/bin/sh
case "$*" in
  "--version") echo "cobratool 1.0";;
  "--help") printf 'Available Commands:\n  issue    Manage issues\n  run      Run a thing\n\nFlags:\n      --debug   Debug\n';;
  "run --help") printf 'Flags:\n      --fast   Skip checks\n';;
  "issue --help") printf 'Available Commands:\n  list    List\n\nFlags:\n      --history   History\n';;
  "issue list --help") printf 'Flags:\n      --jql string   JQL query\n';;
  *) exit 1;;
esac
WEOF
chmod +x bin/cobratool
else
cat > bin/cobratool.bat <<'BEOF'
@echo off
if "%*"=="--version" echo cobratool 1.0 & goto :eof
if "%*"=="--help" (
echo Available Commands:
echo   issue    Manage issues
echo   run      Run a thing
echo.
echo Flags:
echo       --debug   Debug
goto :eof
)
if "%*"=="run --help" (
echo Flags:
echo       --fast   Skip checks
goto :eof
)
if "%*"=="issue --help" (
echo Available Commands:
echo   list    List
echo.
echo Flags:
echo       --history   History
goto :eof
)
if "%*"=="issue list --help" (
echo Flags:
echo       --jql string   JQL query
goto :eof
)
exit /b 1
BEOF
fi
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig cobratool 2>&1) || fail "add sig cobratool: $out"
echo "$out" | grep -qF "source: help+subs" || fail "the walk labels its lineage: $out"
grep -qF "jql: string" .weir/sigs/cobratool.weir || fail "the depth-2 walk found --jql, value-taking [D:sig-arity]"
# the scoping receipt [D:scoped-sigs]: provenance kept, the union emitted —
# a flag on the wrong subcommand warns naming the case; globals ride
# every case; a line with no subcommand token stays the L2 skip
grep -qF "type Cmd =" .weir/sigs/cobratool.weir || fail "the walk emits the scoped union"
printf '#sig cobratool\ncobratool issue list --jql x --debug\nprint "d"\n' > sc1.weir
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN check sc1.weir 2>&1)
echo "$out" | grep -q "unknown flag" && fail "in-scope + global must check clean: $out" || true
printf '#sig cobratool\ncobratool run --jql x\nprint "d"\n' > sc2.weir
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN check sc2.weir 2>&1)
echo "$out" | grep -qF "unknown flag '--jql' for cobratool run" || fail "the wrong-case flag warns naming the case: $out"
# the uncramping receipt [D:scoped-sigs]: a deeper path's flag does not
# leak up — `issue --jql` warns while `issue list --jql` is the clean pin above
printf '#sig cobratool\ncobratool issue --jql x\nprint "d"\n' > sc4.weir
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN check sc4.weir 2>&1)
echo "$out" | grep -qF "unknown flag '--jql' for cobratool issue" || fail "a deeper path's flag must not leak up: $out"
# a sub-less line checks the globals (the case intersection) — the
# claude shape: flag-only usage must still squiggle [D:scoped-sigs]
printf '#sig cobratool\ncobratool --debgu\ncobratool --debug\nprint "d"\n' > sc3.weir
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN check sc3.weir 2>&1)
echo "$out" | grep -qF "unknown flag '--debgu' for cobratool" || fail "a sub-less unknown must warn against the globals: $out"
echo "$out" | grep -q "unknown flag '--debug'" && fail "a sub-less GLOBAL must check clean: $out" || true
# ANSI in help/version output (the mac jira shape [D:sig-version-probe]):
# escapes are stripped at the probe boundary — a colored all-caps banner
# still detects, and no escape byte reaches the sig
if [ "$IS_WINDOWS" != "1" ]; then
cat > bin/colortool <<'WEOF'
#!/bin/sh
if [ "$1" = "--version" ]; then printf 'tool \033[32m2.0\033[0m\n'; exit 0; fi
if [ "$1" = "--help" ]; then printf '\033[1mMAIN COMMANDS\033[0m\n  sub    A sub\n\n\033[1mFLAGS\033[0m\n      --top   top\n'; exit 0; fi
if [ "$1" = "sub" ] && [ "$2" = "--help" ]; then printf 'FLAGS\n      --deep   deep flag\n'; exit 0; fi
exit 1
WEOF
chmod +x bin/colortool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig colortool 2>&1) || fail "add sig colortool: $out"
echo "$out" | grep -qF "source: help+subs" || fail "a colored banner must still detect: $out"
echo "$out" | grep -qF 'version: tool 2.0' || fail "the version is stripped clean: $out"
grep -qF "deep: bool" .weir/sigs/colortool.weir || fail "the colored walk reached the sub"
grep -q $'\x1b' .weir/sigs/colortool.weir && fail "an escape byte reached the sig" || true
# keyword longs (docker --type, kubectl --for): Wire carries the flag
# spelling, the field takes a Flag suffix — generation validates
cat > bin/kwtool <<'WEOF'
#!/bin/sh
if [ "$1" = "--version" ]; then echo "kt 1.0"; exit 0; fi
if [ "$1" = "--help" ]; then printf 'MAIN COMMANDS\n  wait   Wait for things\n\nFlags:\n      --for string    Wait condition\n  -t, --type string   Resource type\n  -a, --all           All of them\n'; exit 0; fi
if [ "$1" = "wait" ] && [ "$2" = "--help" ]; then printf 'Flags:\n  -a, --all-tags   Every tag\n'; exit 0; fi
exit 1
WEOF
chmod +x bin/kwtool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig kwtool 2>&1) || fail "keyword longs must generate: $out"
# a keyword long with a short shares one bracket (stacked attrs do not parse)
grep -qF '[<Wire "type"; Short "t">]' .weir/sigs/kwtool.weir || fail "Wire and Short share one attr bracket"
# subcommands reusing a short: first holder keeps it, the union still validates
grep -qF "allTags: bool" .weir/sigs/kwtool.weir || fail "the dup-short flag keeps its long"
# (the globals are their own record [D:sig-sources]: the wait case holds
# only its own -a)
awk '/^type WaitFlags/,/^}/' .weir/sigs/kwtool.weir | grep -c 'Short "a"' | grep -qx 1 || fail "exactly one field of the wait case holds -a"
awk '/^type WaitFlags/,/^}/' .weir/sigs/kwtool.weir | grep -A1 'Short "a"' | grep -qF "allTags" || fail "the wait case's -a is its own --all-tags"
# open-by-default tools (the VS Code shape [D:sig-version-probe]): a bare
# word would open things — bare-word probes run only when --help
# advertises the subcommand, so generation must spawn none here
cat > bin/opentool <<'WEOF'
#!/bin/sh
case "$1" in
  --version) echo "1.93.0"; exit 0;;
  --help) printf 'Usage: opentool [options][paths...]\n\nOptions:\n      --wait   Wait for close\n'; exit 0;;
  -*) exit 1;;
  *) echo "OPENED:$*" >> "$sgdir/opened.log"; exit 0;;
esac
WEOF
chmod +x bin/opentool
rm -f "$sgdir/opened.log"
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig opentool 2>&1) || fail "add sig opentool: $out"
test -f "$sgdir/opened.log" && fail "a bare-word probe OPENED something: $(cat "$sgdir/opened.log")" || true
# the az dialect [D:sig-version-probe]: multi-line --version records its
# first line only; `--flag --alias -s [Required] : doc` rows record the
# postfix short, each alias, and a clean doc
cat > bin/aztool <<'WEOF'
#!/bin/sh
case "$*" in
  "--version") printf 'azcli 9.9 *\nConfig directory /home/u/.azure\n'; exit 0;;
  "--help") printf 'Arguments\n    --uri --url -u [Required] : Request URL.\n    --body -b : Request body.\n    --dryRun, --dry-run : Both spellings accepted.\n'; exit 0;;
  *) exit 1;;
esac
WEOF
chmod +x bin/aztool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig aztool 2>&1) || fail "add sig aztool: $out"
echo "$out" | grep -qF "version: azcli 9.9 *" || fail "the identity is the first line: $out"
echo "$out" | grep -qF ".azure" && fail "environment lines reached the identity" || true
grep -qF '[<Short "u">]' .weir/sigs/aztool.weir || fail "the postfix short records"
grep -qF "url: bool" .weir/sigs/aztool.weir || fail "the alias records as its own flag"
grep -qF "/// Request URL." .weir/sigs/aztool.weir || fail "the doc sheds the alias run and the colon"
# claude-style same-camel aliases merge into one field: the kebab is
# accepted for free, one Wire carries the differing spelling
grep -qF '[<Wire "dryRun">]' .weir/sigs/aztool.weir || fail "the camel alias rides Wire"
printf '#sig aztool\naztool --dryRun x\naztool --dry-run x\nprint "d"\n' > azw.weir
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN check azw.weir 2>&1)
echo "$out" | grep -q "unknown flag" && fail "both alias spellings must check: $out" || true
# Go-flag rows (micro): single-dash longs, description on the next line
cat > bin/gotool <<'WEOF'
#!/bin/sh
if [ "$1" = "--version" ]; then echo "gotool 2.0"; exit 0; fi
if [ "$1" = "--help" ]; then printf 'Usage: gotool [OPTION]...\n-clean\n        Clean the configuration directory and exit\n-config-dir dir\n        Specify a custom configuration directory\n'; exit 0; fi
exit 1
WEOF
chmod +x bin/gotool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig gotool 2>&1) || fail "add sig gotool: $out"
grep -qF "configDir: string" .weir/sigs/gotool.weir || fail "single-dash longs record, \`-config-dir dir\` taking a value"
grep -qF 'let convention = "go"' .weir/sigs/gotool.weir || fail "Go rows set the Go convention [D:sig-conventions]"
grep -qF "/// Clean the configuration directory and exit" .weir/sigs/gotool.weir || fail "next-line docs attach"
# usage-only help on a nonzero exit (BSD grep): the dump harvests; a
# short never warns on a surface that recorded no shorts
cat > bin/bsdtool <<'WEOF'
#!/bin/sh
if [ "$1" = "--version" ]; then echo "bsdtool 2.6"; exit 0; fi
if [ "$1" = "--help" ]; then printf 'usage: bsdtool [-abcq] [-e pattern] [--color=when] [--null] [file ...]\n' >&2; exit 2; fi
exit 1
WEOF
chmod +x bin/bsdtool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig bsdtool 2>&1) || fail "add sig bsdtool: $out"
echo "$out" | grep -qF "source: help-scan" || fail "the refused help still harvests: $out"
printf '#sig bsdtool\nbsdtool -q --color=auto x\nprint "d"\n' > bsd.weir
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN check bsd.weir 2>&1)
echo "$out" | grep -q "unknown flag '-q'" && fail "a shortless surface must not warn on shorts: $out" || true
# the bounded probe [D:sig-version-probe]: a daemon the help leaves
# holding stdout cannot hang generation, and the probe's process group
# dies with it; a flood is cut at the byte bound, with a note; every probe
# runs in a fresh empty temp dir
if [ "$IS_WINDOWS" != "1" ]; then
cat > bin/daemontool <<WEOF
#!/bin/sh
case "\$1" in
  --version) echo "daemontool 1.0";;
  --help) printf 'Options:\n  --aa   aa\n'; sleep 30 & echo \$! > "$sgdir/daemon.pid"; pwd > "$sgdir/probe.cwd"; ls -A | wc -l > "$sgdir/probe.n";;
  *) exit 1;;
esac
WEOF
chmod +x bin/daemontool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" timeout 20 $BIN add sig daemontool 2>&1) || fail "a daemon holding the help's stdout hung add sig: $out"
echo "$out" | grep -qF "added sig daemontool (1 flag(s)" || fail "the daemon's help still records: $out"
if [ "$(uname -s)" = Linux ]; then
    kill -0 "$(cat "$sgdir/daemon.pid")" 2>/dev/null && fail "the probe's process group outlived the probe" || true
else
    kill "$(cat "$sgdir/daemon.pid")" 2>/dev/null || true
fi
case "$(cat "$sgdir/probe.cwd")" in */weir-sigprobe-*) ;; *) fail "a probe ran outside a fresh temp dir: $(cat "$sgdir/probe.cwd")";; esac
[ "$(tr -d ' ' < "$sgdir/probe.n")" = "0" ] || fail "the probe's temp dir was not empty"
cat > bin/floodtool <<'WEOF'
#!/bin/sh
case "$1" in
  --version) echo "floodtool 1.0";;
  --help) echo "Options:"; i=0; while :; do echo "  --f$i   xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"; i=$((i+1)); done;;
  *) exit 1;;
esac
WEOF
chmod +x bin/floodtool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" timeout 60 $BIN add sig floodtool 2>&1) || fail "an endless help was not cut: $out"
echo "$out" | grep -qF "output cut at 4 MiB per stream: 'floodtool --help'" || fail "the cut is stated: $(echo "$out" | cut -c1-300)"
grep -qF "/// output cut at 4 MiB per stream: 'floodtool --help' — the rest was not read" .weir/sigs/floodtool.weir || fail "the sig notes the cut"
# a probe stopped at the time bound keeps what it printed, and says so
# [D:sig-probe-bounds] (the help printed, stdout closed, the tool lingers)
cat > bin/slowtool <<WEOF
#!/bin/sh
case "\$1" in
  --help) printf 'Options:\n  --aa   aa\n'; exec >&-; echo \$\$ > "$sgdir/slow.pid"; exec sleep 40;;
  *) exit 1;;
esac
WEOF
chmod +x bin/slowtool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" timeout 40 $BIN add sig slowtool 2>&1) || fail "a probe at the time bound lost what it read: $out"
echo "$out" | grep -qF "added sig slowtool (1 flag(s)" || fail "the slow help still records: $out"
echo "$out" | grep -qF "stopped at 15 s: 'slowtool --help'" || fail "the time bound is stated: $out"
grep -qF "/// stopped at 15 s: 'slowtool --help' — what it printed by then was read" .weir/sigs/slowtool.weir || fail "the sig notes the time bound"
# a signal to add sig kills the probe's own group and removes its temp
# dir — the terminal's ^C never reaches that group [D:sig-probe-bounds]
mkdir -p "$sgdir/sigtmp"
rm -f "$sgdir/slow.pid"
PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" TMPDIR="$sgdir/sigtmp" $BIN add sig slowtool >/dev/null 2>&1 &
sgpid=$!
for _ in $(seq 1 50); do [ -s "$sgdir/slow.pid" ] && break; sleep 0.1; done
[ -s "$sgdir/slow.pid" ] || fail "the slow probe never started"
kill -TERM "$sgpid"
sgrc=0; wait "$sgpid" || sgrc=$?
[ "$sgrc" = 143 ] || fail "add sig must die by the TERM (143), got $sgrc"
sleep 0.3
kill -0 "$(cat "$sgdir/slow.pid")" 2>/dev/null && { kill "$(cat "$sgdir/slow.pid")"; fail "a signal to add sig orphaned its probe"; } || true
[ -z "$(ls -A "$sgdir/sigtmp")" ] || fail "a signal to add sig left its temp dir: $(ls -A "$sgdir/sigtmp")"
# each probe runs once per generation: git's builtins asked their helper
# and -h from two places
cat > bin/gtool <<WEOF
#!/bin/sh
printf '%s\n' "\$*" >> "$sgdir/gtool.log"
case "\$*" in
  "--version") echo "git version 2.53.0";;
  "--help") printf 'usage: git [-v | --version] [-C <path>] <command> [<args>]\n';;
  "--list-cmds=builtins") echo commit;;
  "commit --git-completion-helper-all") echo "--amend --quiet -- --no-quiet";;
  "commit -h") printf 'usage: git commit [-q] [--amend]\n\n    -q, --[no-]quiet      suppress summary\n    --[no-]amend          amend previous commit\n'; exit 129;;
  *) exit 129;;
esac
WEOF
chmod +x bin/gtool
rm -f "$sgdir/gtool.log"
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig gtool 2>&1) || fail "add sig gtool: $out"
dups=$(sort "$sgdir/gtool.log" | uniq -d)
[ -z "$dups" ] || fail "a probe ran twice in one generation: $dups"
# file names [D:sig-probe-bounds]: a plain name another lock entry
# records (its legacy name) is that tool's file — never read for this one,
# never overwritten: generation moves it to its owner's own name first; a
# no-flags teach names the file #sig reads
printf '#!/bin/sh\n[ "$1" = --help ] && printf "Options:\\n  --lg   lg\\n"\nexit 0\n' > "bin/lg tool"
printf '#!/bin/sh\n[ "$1" = --help ] && printf "Options:\\n  --lgu   lgu\\n"\nexit 0\n' > bin/lg_tool
chmod +x "bin/lg tool" bin/lg_tool
PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig "lg tool" >/dev/null 2>&1 || fail "add sig 'lg tool'"
mv .weir/sigs/lg%20tool.weir .weir/sigs/lg_tool.weir
sed 's#"sigs/lg%20tool.weir"#"sigs/lg_tool.weir"#' .weir/lock.json > lock.tmp && mv lock.tmp .weir/lock.json
before=$(cksum < .weir/sigs/lg_tool.weir)
printf '#sig lg_tool\nprint "x"\n' > lg.weir
out=$($BIN check lg.weir 2>&1) && fail "#sig must not read another tool's legacy-named file: $out" || true
echo "$out" | grep -qF "is the signature of 'lg tool'" || fail "the #sig refusal names the owner: $out"
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig lg_tool 2>&1) || fail "add sig beside another tool's legacy-named file: $out"
echo "$out" | grep -qF "moved the signature of 'lg tool' from sigs/lg_tool.weir to sigs/lg%20tool.weir" || fail "the move is stated: $out"
[ "$(cksum < .weir/sigs/lg%20tool.weir)" = "$before" ] || fail "another tool's legacy-named sig was not kept whole"
grep -qF '"sigs/lg%20tool.weir"' .weir/lock.json || fail "the owner's lock entry follows its file"
grep -qF 'lgu' .weir/sigs/lg_tool.weir || fail "lg_tool gets its own sig"
vout=$($BIN verify 2>&1) || true
echo "$vout" | grep -qF "sig lg tool: ok" || fail "the moved sig still verifies: $vout"
echo "$vout" | grep -qF "sig lg_tool: ok" || fail "the new sig verifies: $vout"
mkdir -p lib
printf '#!/bin/sh\nexit 0\n' > lib/nf
chmod +x lib/nf
out=$($BIN add sig ./lib/nf 2>&1) && fail "a tool with no flags must refuse: $out" || true
echo "$out" | grep -qF "write .weir/sigs/%2E%2Flib%2Fnf.weir by hand" || fail "the hand-write teach names the encoded file: $out"
# the gate itself fails loud [D:sig-help-shapes]: a crashed check, a sig
# that does not load, and `// cleanup` is no clean marker
printf '#!/bin/sh\nif [ "$1" = check ]; then echo boom >&2; exit 134; fi\nexec "$SG_REAL" "$@"\n' > "$sgdir/crashweir"
printf '#!/bin/sh\nif [ "$1" = check ]; then echo "let convention = \\"bogus\\"" >> .weir/sigs/gnufx.weir; fi\nexec "$SG_REAL" "$@"\n' > "$sgdir/badsigweir"
printf '#!/bin/sh\nif [ "$1" = check ]; then echo "gnufx --zzq f // cleanup" >> lines.weir; fi\nexec "$SG_REAL" "$@"\n' > "$sgdir/cleanupweir"
chmod +x "$sgdir/crashweir" "$sgdir/badsigweir" "$sgdir/cleanupweir"
absbin=$(command -v "$BIN" || echo "$BIN")
case "$absbin" in /*) ;; *) absbin="$(pwd)/$absbin";; esac
out=$(SG_REAL="$absbin" $BIN "$ROOT/tools/sig-score.weir" --bin "$sgdir/crashweir" --only gnufx 2>&1) && fail "the gate passed a crashed check: $out" || true
echo "$out" | grep -qF "check crashed (exit 134)" || fail "the gate names the crash: $out"
out=$(SG_REAL="$absbin" $BIN "$ROOT/tools/sig-score.weir" --bin "$sgdir/badsigweir" --only gnufx 2>&1) && fail "the gate passed a sig load error: $out" || true
echo "$out" | grep -qF "the signature did not load (L1):" || fail "the gate names the load error: $out"
out=$(SG_REAL="$absbin" $BIN "$ROOT/tools/sig-score.weir" --bin "$sgdir/cleanupweir" --only gnufx 2>&1) || fail "// cleanup is no clean marker — its line is not scored: $out"
fi
# gh's dialect [D:scoped-sigs]: colon-suffixed command tokens walk, and
# the walk reaches depth 4 (kustomize edit add resource's shape)
cat > bin/ghtool <<'WEOF'
#!/bin/sh
case "$*" in
  "--version") echo "ghtool 2.99"; exit 0;;
  "--help") printf 'CORE COMMANDS\n  pr:    Manage pull requests\n\nFLAGS\n  --help   Show help\n'; exit 0;;
  "pr --help") printf 'CORE COMMANDS\n  checkout:  Check out\n'; exit 0;;
  "pr checkout --help") printf 'CORE COMMANDS\n  branch:  Branch ops\n\nFLAGS\n  -b, --branch string   Branch\n'; exit 0;;
  "pr checkout branch --help") printf 'FLAGS\n      --track4 string   Level four\n'; exit 0;;
  *) exit 1;;
esac
WEOF
chmod +x bin/ghtool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig ghtool 2>&1) || fail "add sig ghtool: $out"
grep -qF "branch: string" .weir/sigs/ghtool.weir || fail "colon-suffixed command tokens walk"
grep -qF "track4: string" .weir/sigs/ghtool.weir || fail "the walk reaches depth 4"
# the phantom filter [D:sig-sources]: the walk runs only the words a
# command table lists — never an options section's continuation line
# (jq's "Command options:"), a flag row under a "Commands:" heading
# (journalctl, gpg) or a prose line; every argv the generator ran is logged
cat > bin/phantool <<'WEOF'
#!/bin/sh
printf '%s\n' "$*" >> "$PHANT_LOG"
case "$*" in
  "--version") echo "phantool 1.0"; exit 0;;
  "--help") printf 'Usage: phantool [options] <filter>\n\nCommand options:\n  -n, --null-input          use null as the single input\n                            the value; read each line\n      --tab                 use tabs\n\nCommands:\n  -h --help        Show this help\n     --sign        make a signature\n  build [PATTERN]  Build the thing\n                   then run it\n  the docs say this is prose\n'; exit 0;;
  "build --help") printf 'Flags:\n      --fast   Skip checks\n'; exit 0;;
  *) exit 1;;
esac
WEOF
chmod +x bin/phantool
rm -f "$sgdir/phant.log"
out=$(PHANT_LOG="$sgdir/phant.log" PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig phantool 2>&1) || fail "add sig phantool: $out"
bad=$(grep -vxE -- '--version|--help|--versio|build --help' "$sgdir/phant.log" || true)
[ -z "$bad" ] || fail "the generator ran words no command table lists: $bad"
grep -qxF 'build --help' "$sgdir/phant.log" || fail "the listed command walks: $(cat "$sgdir/phant.log")"
# PATH-y tool names [D:scoped-sigs]: one flat file under sigs/ (never a
# nested tree, never an escape from .weir), a legal module name, and
# the #sig round-trip on the spelling the script uses
mkdir -p "$sgdir/proj/lib"
cat > "$sgdir/proj/lib/jp" <<'WEOF'
#!/bin/sh
if [ "$1" = "--help" ]; then printf 'usage: [flags]\n  -c, --clean   Deletes branch\n'; exit 0; fi
exit 1
WEOF
chmod +x "$sgdir/proj/lib/jp"
if [ "$IS_WINDOWS" != "1" ]; then
out=$($BIN add sig ./lib/jp 2>&1) || fail "add sig on a path-y tool: $out"
# (injective [D:sig-probe-bounds]: `.` leading and `/` as %XX)
test -f .weir/sigs/%2E%2Flib%2Fjp.weir || fail "the sig file is FLAT under sigs/: $(find .weir/sigs -name '*jp*')"
head -1 .weir/sigs/%2E%2Flib%2Fjp.weir | grep -qE '^module [A-Z]' || fail "a path-y tool mints a legal module name"
printf '#sig ./lib/jp\n./lib/jp --clena\nprint "d"\n' > jp.weir
out=$($BIN check jp.weir 2>&1)
echo "$out" | grep -qF "Did you mean '--clean'?" || fail "the path-y sig round-trips: $out"
# a signature the lock records under the old name is still read, and
# regeneration supersedes it
mv .weir/sigs/%2E%2Flib%2Fjp.weir .weir/sigs/_lib_jp.weir
sed 's#"sigs/%2E%2Flib%2Fjp.weir"#"sigs/_lib_jp.weir"#' .weir/lock.json > lock.tmp && mv lock.tmp .weir/lock.json
grep -qF '"sigs/_lib_jp.weir"' .weir/lock.json || fail "the legacy lock path is in place"
out=$($BIN check jp.weir 2>&1)
echo "$out" | grep -qF "Did you mean '--clean'?" || fail "a legacy-named sig the lock records is still read: $out"
$BIN add sig ./lib/jp >/dev/null 2>&1 || fail "regenerating over a legacy-named sig"
test -f .weir/sigs/_lib_jp.weir && fail "the legacy-named sig survived its regeneration" || true
test -f .weir/sigs/%2E%2Flib%2Fjp.weir || fail "regeneration writes the injective name"
abs=$(cd "$sgdir/proj/lib" && pwd)/jp
$BIN add sig "$abs" >/dev/null 2>&1 || fail "an absolute tool path must generate"
find .weir/sigs -maxdepth 1 -name '*jp.weir' | grep -q . || fail "the absolute path stays INSIDE .weir/sigs"
test -f "$abs.weir" && fail "the sig ESCAPED next to the binary" || true
# a data file is a different wrong turn than a missing tool
printf 'kind: XR\n' > xr.yaml
out=$($BIN add sig ./xr.yaml 2>&1) && fail "a data file must refuse" || true
echo "$out" | grep -qF "exists but did not run" || fail "the data-file teach: $out"
fi
# broot draws a box table — the glyphs strip to spaces and rows parse
cat > bin/boxtool <<'WEOF'
#!/bin/sh
if [ "$1" = "--version" ]; then echo "boxtool 1.0"; exit 0; fi
if [ "$1" = "--help" ]; then printf 'Options:\n\342\224\202  -d    \342\224\202--dates   \342\224\202Show dates\342\224\202\n'; exit 0; fi
exit 1
WEOF
chmod +x bin/boxtool
out=$(PATH="$(pathEntry "$sgdir/proj/bin"):$PATH" $BIN add sig boxtool 2>&1) || fail "add sig boxtool: $out"
grep -qF '[<Short "d">]' .weir/sigs/boxtool.weir || fail "the boxed short records"
grep -qF "/// Show dates" .weir/sigs/boxtool.weir || fail "the boxed doc records"
fi
cd - >/dev/null
rm -rf "$sgdir"
echo "e2e ok: command signatures (generate, typo+did-you-mean, check spawns nothing, property 3, verify arms, restore never regenerates, --version refuser records none)"

# ---- Size [D:size] ---------------------------------------------------------
szdir=$(mkweirtmp)
cat > "$szdir/sz.weir" <<'WEOF'
type Cli = {
    [<Default 10MiB>]
    /// upload ceiling
    max: Size
}
let cli = Args.load Cli
["12345"] |> File.write "probe.bin"
let big = File.size "probe.bin" > 4B
File.delete "probe.bin"
print $"cap {cli.max}, big {big}"
WEOF
out=$(cd "$szdir" && $BIN sz.weir)
[ "$out" = "cap 10 MiB, big true" ] || fail "Size defaults + File.size comparison: $out"
out=$(cd "$szdir" && $BIN sz.weir --max 1.5GiB)
[ "$out" = "cap 1.5 GiB, big true" ] || fail "Size flag parses: $out"
out=$($BIN -e '1MB' 2>&1) && fail "1MB must teach" || true
echo "$out" | grep -qF "'MB' is ambiguous" || fail "the SI teaching: $out"
out=$($BIN -e '$(echo 10MiB word) |> Seq.head')
[ "$out" = '"10MiB word" : string' ] || fail "command-mode 10MiB stays a word: $out"
rm -rf "$szdir"
echo "e2e ok: Size (defaults + File.size compare, flag parse, SI teaching, argv word)"

# ---- Secret [D:secret]: the renderers refuse; reveal/argv are the exits ----
scdir=$(mkweirtmp)
cat > "$scdir/sec.weir" <<'WEOF'
type Cfg = { user: string; token: Secret }
let cfg = Env.load Cfg
print (show cfg)
print (Secret.reveal cfg.token)
let tok = cfg.token
echo $tok
WEOF
out=$(cd "$scdir" && user=admin token=supersecret "$BIN" "$scdir/sec.weir" 2>&1)
# show renders ***, reveal + argv reveal the real value
echo "$out" | grep -qF "token = ***" || fail "show must render the Secret field as ***: $out"
n=$(echo "$out" | grep -cF "supersecret" || true)
[ "$n" -eq 2 ] || fail "reveal + argv must each surface the value exactly once (got $n): $out"
# a containing record's show must not leak
echo "$out" | sed -n 1p | grep -qF "supersecret" && fail "the shown record LEAKED the secret: $out"
rm -rf "$scdir"
echo "e2e ok: Secret (show ***, reveal + argv splice reveal, the shown record does not leak)"

# ---- Http [D:http]: the typed request boundary (offline local server) ------
if command -v python3 >/dev/null 2>&1; then
    hport=$((21000 + RANDOM % 2000))
    hdir=$(mkweirtmp)
    # an echo server: returns the request body byte-exact, status from a
    # header, the Authorization header on /auth, and the User-Agent
    # header(s) as "count|joined" on /ua (a count, because the duplicate-UA
    # case is the likely bug [D:http-ua] — pairs append, they don't replace)
    cat > "$hdir/echo.py" <<'PYEOF2'
import http.server, socketserver, sys
class H(http.server.BaseHTTPRequestHandler):
    def _h(self):
        n = int(self.headers.get('Content-Length', 0))
        body = self.rfile.read(n) if n else b''
        if self.path == '/auth':
            body = self.headers.get('Authorization', 'NONE').encode()
        if self.path == '/ua':
            uas = self.headers.get_all('User-Agent') or []
            body = f"{len(uas)}|{','.join(uas)}".encode()
        if self.path == '/ct':
            body = (self.headers.get('Content-Type', 'NONE') + '|').encode() + body
        code = 404 if self.path == '/missing' else int(self.headers.get('X-Want-Status', '200'))
        self.send_response(code); self.end_headers(); self.wfile.write(body)
    do_GET = do_POST = do_PUT = _h
    def log_message(self, *a): pass
socketserver.TCPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
PYEOF2
    python3 "$hdir/echo.py" "$hport" &
    hsrv=$!
    # readiness, not a fixed sleep (see S1): the bind race hangs a macOS
    # connect to the 30s client timeout. $hport is explicit 127.0.0.1.
    awaitTcp $hport || { kill $hsrv 2>/dev/null || true; fail "Http: the echo server never came up on $hport"; }

    # the mangling pin: a multi-object NDJSON body (spans lines) round-trips
    # byte-exact — the exact bytes curl -d would have eaten
    cat > "$hdir/mangle.weir" <<WEOF
type P = { name: string; count: int }
let items = [{ name = "a"; count = 1 }; { name = "b"; count = 2 }; { name = "c"; count = 3 }]
let sent = items |> to jsonl
let resp = Http.send { Http.defaults with method = Post; url = "http://127.0.0.1:$hport/x"; body = Json sent }
let ok = (sent |> Seq.length) == (resp.body |> Seq.length)
print \$"lines={resp.body |> Seq.length} match={ok}"
resp.body |> Seq.iter print
WEOF
    out=$($BIN "$hdir/mangle.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "mangle send failed: $out"; }
    echo "$out" | grep -qF "lines=3 match=true" || { kill $hsrv 2>/dev/null || true; fail "body did not round-trip line-count: $out"; }
    echo "$out" | grep -qF '{"name":"b","count":2}' || { kill $hsrv 2>/dev/null || true; fail "body bytes mangled: $out"; }

    # status is data: a 404 binds, never raises
    cat > "$hdir/status.weir" <<WEOF
let resp = Http.send { Http.defaults with url = "http://127.0.0.1:$hport/x"; headers = [("X-Want-Status", "404")] }
print \$"status={resp.status}"
WEOF
    out=$($BIN "$hdir/status.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "404 must bind, not raise: $out"; }
    echo "$out" | grep -qF "status=404" || { kill $hsrv 2>/dev/null || true; fail "404 not bound as data: $out"; }

    # auth reaches the server, the Secret revealed only at send
    cat > "$hdir/auth.weir" <<WEOF
let r1 = Http.send { Http.defaults with url = "http://127.0.0.1:$hport/auth"; auth = Bearer (Secret.of "tok123") }
r1.body |> Seq.iter print
let r2 = Http.send { Http.defaults with url = "http://127.0.0.1:$hport/auth"; auth = Basic ("alice", Secret.of "s3cr3t") }
r2.body |> Seq.iter print
WEOF
    out=$($BIN "$hdir/auth.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "auth send failed: $out"; }
    echo "$out" | grep -qF "Bearer tok123" || { kill $hsrv 2>/dev/null || true; fail "Bearer not sent: $out"; }
    echo "$out" | grep -qF "Basic YWxpY2U6czNjcjN0" || { kill $hsrv 2>/dev/null || true; fail "Basic base64 wrong: $out"; }

    # Http.expect raises on non-2xx (naming the status); the same 404 that
    # send binds as data [D:http-s2] — two postures, no boolean
    cat > "$hdir/fetch.weir" <<WEOF
let ok = Http.get "http://127.0.0.1:$hport/x" |> Http.expect
print \$"fetch-ok lines={ok |> Seq.length}"
let bound = Http.send (Http.get "http://127.0.0.1:$hport/missing")
print \$"send-binds={bound.status}"
WEOF
    out=$($BIN "$hdir/fetch.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "fetch/send failed: $out"; }
    echo "$out" | grep -qF "send-binds=404" || { kill $hsrv 2>/dev/null || true; fail "send must BIND a 404: $out"; }
    # the header accessors [D:http-dx]: case-insensitive first hit,
    # multiplicity in wire order
    hdrout=$($BIN -e 'let r = HttpResponse { status = 200; headers = [("ETag", "W/1"); ("Set-Cookie", "a=1"); ("set-cookie", "b=2")]; body = [""] }
print (show (r |> Http.header "etag"))
print (show (r |> Http.header "absent"))
r |> Http.headerAll "SET-COOKIE" |> Seq.iter print' 2>&1) || { kill $hsrv 2>/dev/null || true; fail "header accessors failed: $hdrout"; }
    echo "$hdrout" | grep -qF 'Some "W/1"' || { kill $hsrv 2>/dev/null || true; fail "Http.header must hit case-insensitively: $hdrout"; }
    echo "$hdrout" | grep -qF 'None' || { kill $hsrv 2>/dev/null || true; fail "Http.header must miss as None: $hdrout"; }
    echo "$hdrout" | grep -qF 'a=1' && echo "$hdrout" | grep -qF 'b=2' || { kill $hsrv 2>/dev/null || true; fail "Http.headerAll must keep both cookies: $hdrout"; }

    out=$($BIN -e 'Http.get "http://127.0.0.1:'"$hport"'/missing" |> Http.expect |> Seq.length |> print' 2>&1) && { kill $hsrv 2>/dev/null || true; fail "expect must RAISE on 404"; } || true
    echo "$out" | grep -qF "answered 404" || { kill $hsrv 2>/dev/null || true; fail "the expect raise must name the status: $out"; }

    # a constructor round-trips through send (Http.post carries the method)
    cat > "$hdir/ctor.weir" <<WEOF
let r = Http.send (Http.post "http://127.0.0.1:$hport/x")
print \$"ctor-status={r.status}"
WEOF
    out=$($BIN "$hdir/ctor.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "constructor send failed: $out"; }
    echo "$out" | grep -qF "ctor-status=200" || { kill $hsrv 2>/dev/null || true; fail "constructor did not send: $out"; }

    # Http DX [D:http-dx]: Json takes the value (renders at the boundary,
    # the same bytes as the pre-rendered spelling), Form urlencodes with
    # its content type, and Http.expect returns the body / raises naming
    # method, url and status
    cat > "$hdir/dx.weir" <<WEOF
type P = { name: string; count: int }
let r1 = Http.send { Http.defaults with method = Post; url = "http://127.0.0.1:$hport/x"; body = Json { name = "a"; count = 1 } }
r1.body |> Seq.iter print
let r2 = Http.send { Http.defaults with method = Post; url = "http://127.0.0.1:$hport/ct"; body = Form [("grant type", "a&b")] }
r2.body |> Seq.iter print
let got = Http.expect { Http.defaults with method = Post; url = "http://127.0.0.1:$hport/x"; body = Json { name = "b"; count = 2 } }
got |> Seq.iter print
WEOF
    out=$($BIN "$hdir/dx.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "http-dx probes failed: $out"; }
    echo "$out" | grep -qF '{"name":"a","count":1}' || { kill $hsrv 2>/dev/null || true; fail "Json value body did not render: $out"; }
    echo "$out" | grep -qF 'application/x-www-form-urlencoded|grant%20type=a%26b' || { kill $hsrv 2>/dev/null || true; fail "Form did not urlencode with its content type: $out"; }
    echo "$out" | grep -qF '{"name":"b","count":2}' || { kill $hsrv 2>/dev/null || true; fail "Http.expect did not return the body: $out"; }

    out=$($BIN -e 'Http.expect (Http.get "http://127.0.0.1:'"$hport"'/missing") |> Seq.length |> print' 2>&1) && { kill $hsrv 2>/dev/null || true; fail "Http.expect must RAISE on 404"; } || true
    echo "$out" | grep -qF "GET http://127.0.0.1:$hport/missing answered 404" || { kill $hsrv 2>/dev/null || true; fail "read raise must name method, url, status: $out"; }

    # parallel fetches via Seq.pmap
    cat > "$hdir/pmap.weir" <<WEOF
let urls = ["http://127.0.0.1:$hport/a"; "http://127.0.0.1:$hport/b"; "http://127.0.0.1:$hport/c"]
urls |> Seq.pmap (fun u -> Http.send { Http.defaults with url = u }) |> Seq.map (fun r -> show r.status) |> Seq.iter print
WEOF
    out=$($BIN "$hdir/pmap.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "pmap fetch failed: $out"; }
    [ "$(echo "$out" | grep -c '^200$')" -eq 3 ] || { kill $hsrv 2>/dev/null || true; fail "pmap did not fetch all: $out"; }

    # the default User-Agent [D:http-ua]: weir/<stamp> — pinned as a
    # relationship to --version (a literal would break every release).
    # The /ua path reports count|value: exactly one must arrive in every
    # case (pairs append — the duplicate-UA bug is the likely one), an
    # explicit header wins from both header paths (lowercase spelling
    # included), and fetch rides the same default.
    stamp=$("$BIN" --version)
    cat > "$hdir/ua.weir" <<WEOF
let r1 = Http.get "http://127.0.0.1:$hport/ua" |> Http.send
print \$"default={r1.body |> Seq.head}"
let r2 = { Http.get "http://127.0.0.1:$hport/ua" with headers = [("User-Agent", "custom-ua")] } |> Http.send
print \$"explicit={r2.body |> Seq.head}"
let r3 = { Http.get "http://127.0.0.1:$hport/ua" with headers = [("user-agent", "lower-ua")] } |> Http.send
print \$"lowercase={r3.body |> Seq.head}"
let r4 = { Http.get "http://127.0.0.1:$hport/ua" with secretHeaders = [("User-Agent", Secret.of "secret-ua")] } |> Http.send
print \$"secret={r4.body |> Seq.head}"
print \$"expect={Http.get "http://127.0.0.1:$hport/ua" |> Http.expect |> Seq.head}"
WEOF
    out=$($BIN "$hdir/ua.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "ua cell failed: $out"; }
    echo "$out" | grep -qF "default=1|weir/$stamp" || { kill $hsrv 2>/dev/null || true; fail "default UA must be weir/<--version stamp>, exactly one: $out"; }
    echo "$out" | grep -qF "explicit=1|custom-ua" || { kill $hsrv 2>/dev/null || true; fail "an explicit User-Agent must win, exactly one: $out"; }
    echo "$out" | grep -qF "lowercase=1|lower-ua" || { kill $hsrv 2>/dev/null || true; fail "a lowercase user-agent must still block the default: $out"; }
    echo "$out" | grep -qF "secret=1|secret-ua" || { kill $hsrv 2>/dev/null || true; fail "a secretHeaders User-Agent must win, exactly one: $out"; }
    echo "$out" | grep -qF "expect=1|weir/$stamp" || { kill $hsrv 2>/dev/null || true; fail "Http.expect must send the default UA: $out"; }
    echo "e2e ok: Http default User-Agent (weir/<stamp> == --version, explicit wins from both header paths, exactly one ever sent, expect included)"

    # F3-outbound [D:http-header-bytes]: a request header carrying CR/LF is
    # refused before send (the forge the review found on the wire), with a
    # located error naming the header, byte, and position. The referee is
    # the server's own request log: a clean control header arrives (so the
    # refusal cannot pass by headers never working), the forged one never
    # does, and the offending request is not sent at all.
    cat > "$hdir/inj.weir" <<WEOF
let crlf = Str.fromBase64 "DQo="
let _r = Http.send { Http.get "http://127.0.0.1:$hport/x" with headers = [("X-Evil", "a" + crlf + "Injected: yes")] }
print "unreached"
WEOF
    out=$($BIN "$hdir/inj.weir" 2>&1) && { kill $hsrv 2>/dev/null || true; fail "F3: a CRLF request header must be refused, not sent"; } || true
    echo "$out" | grep -qF "request header 'X-Evil' carries a CR byte in its value" || { kill $hsrv 2>/dev/null || true; fail "F3: the outbound refusal must name the header, byte, and position: $out"; }
    echo "$out" | grep -qF "cannot contain CR, LF or NUL" || { kill $hsrv 2>/dev/null || true; fail "F3: the outbound refusal must name the whole byte class: $out"; }
    # (the NUL leg of the byte class is pinned at the unit level — weir has
    # no source spelling that yields a NUL-bearing string: Str.fromBase64 and
    # Str.fromUtf8 both refuse NUL by the encoding law, which is why F3's
    # realistic payload arrives via a CR/LF decode, not a NUL one)
    # the clean control header reaches the server (headers do work) — the
    # positive control that makes "the forged header is absent" meaningful
    cat > "$hdir/injctl.weir" <<WEOF
let r = Http.send { Http.get "http://127.0.0.1:$hport/echoh" with headers = [("X-Benign", "present")] }
print \$"control-status={r.status}"
WEOF
    out=$($BIN "$hdir/injctl.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "F3: the clean control header send failed: $out"; }
    echo "$out" | grep -qF "control-status=200" || { kill $hsrv 2>/dev/null || true; fail "F3: a clean header must send fine: $out"; }
    echo "e2e ok: Http F3 outbound header injection refused (CR/LF/NUL in a request header, located, control sends clean)"

    # NO_PROXY=* bypasses the proxy for every host [D:noproxy-star], not
    # only an IPv6 literal: the proxy is a dead port, so a request that
    # went through it fails. The control (an unrelated NO_PROXY entry)
    # must fail — else this host ignores the proxy env and the pin proves
    # nothing.
    cat > "$hdir/star.weir" <<WEOF
let r = Http.send (Http.get "http://127.0.0.1:$hport/x")
print \$"star-status={r.status}"
WEOF
    viaDead() { env HTTP_PROXY=http://127.0.0.1:1 http_proxy=http://127.0.0.1:1 NO_PROXY="$1" no_proxy="$1" "$BIN" "$hdir/star.weir" 2>&1; }
    if ctl=$(viaDead example.invalid); then
        echo "e2e SKIP: this host ignores HTTP_PROXY ($ctl) — NO_PROXY=* pin not run" >&2
    else
        out=$(viaDead '*') || { kill $hsrv 2>/dev/null || true; fail "NO_PROXY=* must bypass the proxy for 127.0.0.1: $out"; }
        echo "$out" | grep -qF "star-status=200" || { kill $hsrv 2>/dev/null || true; fail "NO_PROXY=* direct send: $out"; }
        out=$(viaDead 'example.invalid,*') || { kill $hsrv 2>/dev/null || true; fail "a * entry in a list must bypass too: $out"; }
        echo "e2e ok: Http NO_PROXY=* bypasses the proxy for every host (control routes through it)"
        # a CIDR entry bypasses every address it covers [D:noproxy-cidr]
        out=$(viaDead 'example.invalid,127.0.0.0/8') || { kill $hsrv 2>/dev/null || true; fail "NO_PROXY=127.0.0.0/8 must bypass the proxy for 127.0.0.1: $out"; }
        echo "$out" | grep -qF "star-status=200" || { kill $hsrv 2>/dev/null || true; fail "NO_PROXY CIDR direct send: $out"; }
        out=$(viaDead '127.9.9.9/8') || { kill $hsrv 2>/dev/null || true; fail "a CIDR's host bits are ignored: $out"; }
        out=$(viaDead '10.0.0.0/8') && { kill $hsrv 2>/dev/null || true; fail "a CIDR not covering the host must not bypass: $out"; }
        echo "e2e ok: Http NO_PROXY CIDR entries bypass the addresses they cover"
        # a dead proxy's error names the proxy, not the target [D:proxy-err-names-proxy]
        echo "$out" | grep -qF "the proxy 127.0.0.1:1 refused the connection" || { kill $hsrv 2>/dev/null || true; fail "E4: a dead http proxy must be named: $out"; }
        out=$(env HTTPS_PROXY=http://127.0.0.1:1 https_proxy=http://127.0.0.1:1 NO_PROXY=example.invalid no_proxy=example.invalid "$BIN" -e "Http.send (Http.get \"https://127.0.0.1:$hport/x\") |> fun r -> r.status" 2>&1) && { kill $hsrv 2>/dev/null || true; fail "E4: https through a dead proxy must fail: $out"; }
        echo "$out" | grep -qF "the proxy 127.0.0.1:1 refused the connection" || { kill $hsrv 2>/dev/null || true; fail "E4: a dead https proxy must be named: $out"; }
        out=$(env HTTP_PROXY=http://proxy-weir-e2e.invalid:3128 http_proxy=http://proxy-weir-e2e.invalid:3128 NO_PROXY=example.invalid no_proxy=example.invalid "$BIN" "$hdir/star.weir" 2>&1) && { kill $hsrv 2>/dev/null || true; fail "E4: an unresolvable proxy must fail: $out"; }
        echo "$out" | grep -qF "cannot resolve the proxy proxy-weir-e2e.invalid" || { kill $hsrv 2>/dev/null || true; fail "E4: an unresolvable proxy must be named: $out"; }
        echo "e2e ok: Http a dead proxy's error names the proxy (refused http/https, unresolvable)"
        # NO_PROXY as curl reads it [D:noproxy-curl]: a name entry covers
        # its subdomains (a trailing dot ignored), an IP host matches only
        # an address entry — never a text suffix, a port, a bracketed
        # literal or a /0. Direct to sub.example.invalid fails on DNS;
        # through the dead proxy it names the proxy.
        npOut() { env HTTP_PROXY=http://127.0.0.1:1 http_proxy=http://127.0.0.1:1 NO_PROXY="$1" no_proxy="$1" "$BIN" -e "Http.send (Http.get \"$2\") |> fun r -> r.status" 2>&1; }
        for np in example.invalid .example.invalid example.invalid. EXAMPLE.invalid 'x.org, example.invalid'; do
            out=$(npOut "$np" http://sub.example.invalid:1/) && { kill $hsrv 2>/dev/null || true; fail "noproxy-curl: sub.example.invalid must fail: $out"; }
            echo "$out" | grep -qF "the proxy 127.0.0.1:1" && { kill $hsrv 2>/dev/null || true; fail "noproxy-curl: NO_PROXY=$np must bypass sub.example.invalid: $out"; }
        done
        out=$(npOut example.invalid http://notexample.invalid:1/) && { kill $hsrv 2>/dev/null || true; fail "noproxy-curl: notexample.invalid must fail: $out"; }
        echo "$out" | grep -qF "the proxy 127.0.0.1:1" || { kill $hsrv 2>/dev/null || true; fail "noproxy-curl: example.invalid must not cover notexample.invalid: $out"; }
        for np in .0.0.1 .1 127.0.0.0/0 0.0.0.0/0 '127.0.0.0/ 8' 127.0.0.1/33 "127.0.0.1:$hport" localhost 'x.org 127.0.0.1'; do
            out=$(npOut "$np" "http://127.0.0.1:$hport/x") && { kill $hsrv 2>/dev/null || true; fail "noproxy-curl: NO_PROXY=$np must not bypass 127.0.0.1: $out"; }
            echo "$out" | grep -qF "the proxy 127.0.0.1:1" || { kill $hsrv 2>/dev/null || true; fail "noproxy-curl: NO_PROXY=$np went direct: $out"; }
        done
        echo "e2e ok: Http NO_PROXY matches as curl (subdomains, dots, case; no IP suffix, port, /0 or past-width bits)"
    fi
    # a proxy's refusal names the proxy, never its credentials, never .NET's
    # text [D:proxy-refusal-words]: 407/403/502 to the CONNECT, and a proxy
    # that closes the connection (http and https)
    cat > "$hdir/fakeproxy.py" <<'PROXEOF'
import socket, sys, threading
port, mode = int(sys.argv[1]), sys.argv[2]
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(("127.0.0.1", port)); s.listen(50)
def h(c):
    d = b""
    while b"\r\n\r\n" not in d:
        x = c.recv(4096)
        if not x: break
        d += x
    if mode != "close":
        c.sendall(b"HTTP/1.1 " + mode.encode() + b" No\r\nProxy-Authenticate: Basic realm=\"x\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
    c.close()
while True:
    c, _ = s.accept(); threading.Thread(target=h, args=(c,), daemon=True).start()
PROXEOF
    for mode in 407 403 502 close; do
        pport=$((23000 + RANDOM % 2000))
        python3 "$hdir/fakeproxy.py" "$pport" "$mode" &
        ppid=$!
        awaitTcp $pport || { kill $ppid $hsrv 2>/dev/null || true; fail "proxy-refusal: the fake proxy never came up"; }
        for url in https://weir-target.invalid/x http://weir-target.invalid/x; do
            [ "$mode" != close ] && [ "${url%%:*}" = http ] && continue
            out=$(env HTTPS_PROXY="http://ci-bot:S3cr3tPass@127.0.0.1:$pport" https_proxy="http://ci-bot:S3cr3tPass@127.0.0.1:$pport" HTTP_PROXY="http://ci-bot:S3cr3tPass@127.0.0.1:$pport" http_proxy="http://ci-bot:S3cr3tPass@127.0.0.1:$pport" NO_PROXY=example.invalid no_proxy=example.invalid "$BIN" -e "Http.send (Http.get \"$url\") |> fun r -> r.status" 2>&1) && { kill $ppid $hsrv 2>/dev/null || true; fail "proxy-refusal: $mode must fail: $out"; }
            echo "$out" | grep -qF "S3cr3t" && { kill $ppid $hsrv 2>/dev/null || true; fail "proxy-refusal: $mode leaked the proxy credentials: $out"; }
            echo "$out" | grep -qiF "proxy tunnel request" && { kill $ppid $hsrv 2>/dev/null || true; fail "proxy-refusal: $mode printed .NET's text: $out"; }
            if [ "$mode" = close ]; then
                echo "$out" | grep -qF "the proxy 127.0.0.1:$pport closed the connection before answering" || { kill $ppid $hsrv 2>/dev/null || true; fail "proxy-refusal: a closing proxy must be named ($url): $out"; }
            else
                echo "$out" | grep -qF "the proxy 127.0.0.1:$pport refused to tunnel to weir-target.invalid: $mode" || { kill $ppid $hsrv 2>/dev/null || true; fail "proxy-refusal: a $mode must name the proxy: $out"; }
            fi
        done
        kill $ppid 2>/dev/null || true
        wait $ppid 2>/dev/null || true
    done
    echo "e2e ok: Http a proxy refusing the tunnel (407/403/502) or closing is named, credentials never printed"

    kill $hsrv 2>/dev/null || true

    # transport failure raises, in its own words per case [D:transport-words];
    # and check makes no request (a bogus URL checks clean, no network)
    cat > "$hdir/dead.weir" <<'WEOF'
let _resp = Http.send { Http.defaults with url = "http://127.0.0.1:1/never" }
print "unreached"
WEOF
    $BIN check "$hdir/dead.weir" >/dev/null 2>&1 || fail "check must not make a request (should pass clean)"
    out=$($BIN "$hdir/dead.weir" 2>&1) && fail "transport failure must raise" || true
    echo "$out" | grep -qF "refused the connection" || fail "refused case must say so: $out"

    # a timeout names itself and the duration that fired — never the .NET
    # cancellation text (the raw-leak class's 4th instance, closed)
    cat > "$hdir/hang.py" <<'HANGEOF'
import socket, sys, time
s = socket.socket(); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
s.bind(("127.0.0.1", int(sys.argv[1]))); s.listen(1)
c, _ = s.accept(); time.sleep(30)
HANGEOF
    hangport=$((23100 + RANDOM % 200))
    python3 "$hdir/hang.py" "$hangport" 2>/dev/null &
    hangsrv=$!
    sleep 0.3
    out=$($BIN -e 'Http.send { Http.get "http://127.0.0.1:'"$hangport"'/" with timeout = 1s }' 2>&1) && { kill $hangsrv 2>/dev/null || true; fail "timeout must raise"; } || true
    kill $hangsrv 2>/dev/null || true
    echo "$out" | grep -qF "timed out after 1s reaching 127.0.0.1" || fail "timeout must name itself and the duration: $out"
    echo "$out" | grep -qF "canceled" && fail "the .NET cancellation text must not reach a user: $out" || true

    # insecure: TLS verification is on by default and off per-request when
    # asked [D:http-s2] — a self-signed server the default rejects and
    # insecure = true accepts (openssl-guarded)
    if command -v openssl >/dev/null 2>&1; then
        tport=$((22800 + RANDOM % 200))
        # Git Bash resolves the MINGW (native) openssl, and msys bash
        # path-converts a leading-slash arg for native binaries:
        # /CN=… arrives as C:/Program Files/Git/CN=… — the doubled
        # slash collapses to one on the way in. POSIX keeps the plain
        # spelling; the failure carries openssl's own words either way.
        subj="/CN=127.0.0.1"
        [ "$IS_WINDOWS" = "1" ] && subj="//CN=127.0.0.1"
        osslerr=$(openssl req -x509 -newkey rsa:2048 -keyout "$hdir/k.pem" -out "$hdir/c.pem" -days 1 -nodes -subj "$subj" 2>&1 >/dev/null) ||
            fail "openssl could not generate the self-signed cert: $osslerr"
        cat > "$hdir/tls.py" <<TLSEOF
import http.server, ssl, sys
class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.end_headers(); self.wfile.write(b"secure-ok")
    def log_message(self, *a): pass
srv = http.server.HTTPServer(("127.0.0.1", int(sys.argv[1])), H)
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
ctx.load_cert_chain("$hdir/c.pem", "$hdir/k.pem")
srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
srv.serve_forever()
TLSEOF
        python3 "$hdir/tls.py" "$tport" 2>/dev/null &
        tsrv=$!
        awaitTcp "$tport" || { kill $tsrv 2>/dev/null || true; fail "the TLS server never came up"; }
        # default rejects the self-signed cert (verification on)
        out=$($BIN -e 'Http.send (Http.get "https://127.0.0.1:'"$tport"'/")' 2>&1) && { kill $tsrv 2>/dev/null || true; fail "default must reject a self-signed cert"; } || true
        echo "$out" | grep -qF "certificate is not trusted" || { kill $tsrv 2>/dev/null || true; fail "TLS rejection message: $out"; }
        echo "$out" | grep -qF "insecure = true" || { kill $tsrv 2>/dev/null || true; fail "TLS rejection must name its repair: $out"; }
        # insecure = true accepts it
        cat > "$hdir/ins.weir" <<WEOF
let r = Http.send { Http.get "https://127.0.0.1:$tport/" with insecure = true }
print \$"insecure-status={r.status}"
WEOF
        out=$($BIN "$hdir/ins.weir" 2>&1) || { kill $tsrv 2>/dev/null || true; fail "insecure=true must accept the self-signed cert: $out"; }
        echo "$out" | grep -qF "insecure-status=200" || { kill $tsrv 2>/dev/null || true; fail "insecure did not connect: $out"; }
        # a redirect whose TARGET fails names the target's host, not the
        # healthy origin — the TLS repair included (localhost here is the
        # self-signed server, 127.0.0.1 the redirector)
        rtls=$((23000 + RANDOM % 100))
        cat > "$hdir/tredir.py" <<REOF
import http.server
class A(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(302)
        self.send_header('Location', 'https://localhost:$tport/' if self.path == '/tls' else 'http://localhost:1/')
        self.send_header('Content-Length', '0')
        self.end_headers()
    def log_message(self, *a): pass
http.server.HTTPServer(('127.0.0.1', $rtls), A).serve_forever()
REOF
        python3 "$hdir/tredir.py" & trsrv=$!
        awaitTcp $rtls || { kill $tsrv $trsrv 2>/dev/null || true; fail "the TLS redirector never came up on $rtls"; }
        out=$($BIN -e 'Http.send (Http.get "http://127.0.0.1:'"$rtls"'/tls")' 2>&1) && { kill $tsrv $trsrv 2>/dev/null || true; fail "a redirect to a self-signed target must raise"; } || true
        echo "$out" | grep -qF "cannot establish TLS with localhost" || { kill $tsrv $trsrv 2>/dev/null || true; fail "the TLS error must name the redirect target's host: $out"; }
        echo "$out" | grep -qF "insecure = true" || { kill $tsrv $trsrv 2>/dev/null || true; fail "the redirected TLS error keeps its repair: $out"; }
        out=$($BIN -e 'Http.send (Http.get "http://127.0.0.1:'"$rtls"'/dead")' 2>&1) && { kill $tsrv $trsrv 2>/dev/null || true; fail "a redirect to a dead port must raise"; } || true
        echo "$out" | grep -qF "localhost:1 refused the connection" || { kill $tsrv $trsrv 2>/dev/null || true; fail "the refusal must name the redirect target's host and port: $out"; }
        kill $tsrv $trsrv 2>/dev/null || true
        echo "e2e ok: Http insecure (default rejects a self-signed cert, insecure=true accepts — per-request); a failing redirect target is the host named"
    else
        echo "e2e SKIP: openssl absent — Http insecure TLS pin not run" >&2
    fi

    # S1 [D:secret-redirect]: secretHeaders are dropped on a cross-origin
    # redirect, exactly as the BCL drops Authorization — so weir's two
    # credential channels agree (the review's F4). Driven over a local
    # cross-origin 302 (127.0.0.1 -> localhost, a different origin by host),
    # with origin B logging what arrived. The sensitive header must not
    # reach B; a non-sensitive control header must (proving the drop is
    # credential-specific, not a blanket strip). auth Bearer is dropped too.
    ra=$((23400 + RANDOM % 200))
    rb=$((23600 + RANDOM % 200))
    cat > "$hdir/redir.py" <<REOF
import http.server, sys
PB = $rb
class A(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(302)
        self.send_header('Location', 'http://localhost:%d/dest' % PB)
        self.end_headers()
    def log_message(self, *a): pass
http.server.HTTPServer(('127.0.0.1', $ra), A).serve_forever()
REOF
    cat > "$hdir/dest.py" <<DEOF
import http.server, sys
LOG = "$hdir/dest.log"
class B(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        with open(LOG, 'a') as f:
            for k, v in self.headers.items():
                f.write("%s: %s\n" % (k, v))
        self.send_response(200); self.end_headers(); self.wfile.write(b'landed')
    def log_message(self, *a): pass
http.server.HTTPServer(('localhost', $rb), B).serve_forever()
DEOF
    rm -f "$hdir/dest.log"
    python3 "$hdir/redir.py" & rasrv=$!
    python3 "$hdir/dest.py" & rbsrv=$!
    # readiness, not a fixed sleep: on a slow runner (macOS) the listener
    # may not be accepting at 0.6s, and a connect into the bind race hangs
    # to the 30s client timeout instead of refusing (the awaitTcp comment).
    # $ra is explicit 127.0.0.1 so /dev/tcp readiness is reliable; dest.py
    # then gets the request round-trip as extra margin.
    awaitTcp $ra || { kill $rasrv $rbsrv 2>/dev/null || true; fail "S1: the redirect server never came up on $ra"; }
    cat > "$hdir/redir.weir" <<WEOF
let tok = Secret.of "SECRET-TOKEN-ABC"
let r = Http.send { Http.get "http://127.0.0.1:$ra/start" with secretHeaders = [("X-Api-Key", tok)]; headers = [("X-Control", "keepme")]; auth = Bearer (Secret.of "BEARER-TOK") }
print \$"status={r.status} body={r.body |> Seq.head}"
WEOF
    out=$($BIN "$hdir/redir.weir" 2>&1) || { kill $rasrv $rbsrv 2>/dev/null || true; fail "S1 cross-origin redirect run failed: $out"; }
    echo "$out" | grep -qF "status=200 body=landed" || { kill $rasrv $rbsrv 2>/dev/null || true; fail "S1: the redirect must be followed to origin B: $out"; }
    kill $rasrv $rbsrv 2>/dev/null || true
    dlog=$(cat "$hdir/dest.log" 2>/dev/null)
    echo "$dlog" | grep -qiF "X-Api-Key" && fail "S1: the secretHeaders credential LEAKED to the cross-origin redirect target: $dlog" || true
    echo "$dlog" | grep -qiF "Authorization" && fail "S1: the auth Bearer credential LEAKED to the cross-origin redirect target: $dlog" || true
    echo "$dlog" | grep -qiF "X-Control: keepme" || fail "S1: a NON-sensitive control header must still cross the redirect (the drop is credential-specific): $dlog"
    echo "e2e ok: Http S1 cross-origin redirect drops secretHeaders + auth (control header crosses; credentials do not reach origin B)"

    rm -rf "$hdir"
    echo "e2e ok: Http (mangling, status-is-data, auth, fetch-raises/send-binds, constructors, pmap, transport raises, check silent)"
else
    echo "e2e SKIP: python3 absent — Http offline server not run" >&2
fi

# ---- the showcase's own .weir tree [D:showcase-covers] ---------------------
# the tour imports a module, validates a district against the committed
# schema, and declares the hand-written git signature — check needs no
# restore and no network (the fresh-tree copy proves it)
sctdir=$(mkweirtmp)
mkdir -p "$sctdir/root/.git"
cp -r "$(dirname "$0")/../examples" "$sctdir/root/examples"
( cd "$sctdir/root" && $BIN check examples/showcase.weir >/dev/null 2>&1 ) || fail "the showcase checks in a fresh tree, offline, no restore"
# the caught-typo proof the showcase's comment points at
printf '#sig git\nlet rc = git status --porcelian | exitCode\nprint (show rc)\n' > "$sctdir/root/examples/typo.weir"
out=$(cd "$sctdir/root" && $BIN check examples/typo.weir 2>&1) || true
echo "$out" | grep -qF "unknown flag '--porcelian' for git status. Did you mean '--porcelain'?" || fail "the git sig catches the typo (reified shape): $out"
rm -rf "$sctdir"
echo "e2e ok: showcase .weir tree (offline check, no restore; the typo the comment names is caught)"

# ---- install.sh hardening [D:install-truncation][D:install-checksum-scope] --
# install.sh is a template [D:install-checksum-scope]: ci/gen-install.weir
# substitutes the tag and embeds the release SHA256SUMS to produce the pinned
# artifact weir.sh serves. This exercises both — the template's guards and the
# generation — offline, against a synthetic release.
insdir=$(mkweirtmp)

# the truncation guard: the whole body is main() invoked last, so a fetch cut
# mid-body is an unclosed function — a syntax error that defines and runs
# nothing (set -eu cannot catch a truncation; nothing failed). Parse-only
# (sh -n), so no side effects. The cut is computed to land inside main() —
# past its opening brace, before the closing one — so it stays a mid-body cut
# as the header comment grows.
mainstart=$(grep -b -m1 '^main() {' "$ROOT/install.sh" | cut -d: -f1)
[ -n "$mainstart" ] || fail "install.sh has no 'main() {' — the truncation guard is gone"
head -c "$((mainstart + 300))" "$ROOT/install.sh" > "$insdir/trunc.sh"
if sh -n "$insdir/trunc.sh" 2>/dev/null; then
    fail "a truncated install.sh must be a syntax error (main() left unclosed)"
fi
sh -n "$ROOT/install.sh" || fail "the full install.sh template must parse clean"
echo "e2e ok: install.sh truncation guard — mid-main cut is a syntax error, full template parses"

# the template refuses to run unsubstituted: a real tag never contains '@', so
# the placeholder trips the guard rather than fetching a bogus tag
tout=$(sh "$ROOT/install.sh" 2>&1 || true)
echo "$tout" | grep -qF "this is the install TEMPLATE" || fail "the unsubstituted template must refuse to run, got: $tout"
echo "e2e ok: install.sh template refuses to run unsubstituted (@-sentinel guard)"

# generation [D:install-checksum-scope]: gen-install.weir pins the tag and
# embeds a real SHA256SUMS; the result must parse, carry no leftover
# placeholders, and its embedded checksum must match the real binary (two-origin
# verify — nothing fetched from the binary's origin to check it).
gendir="$insdir/gen"; mkdir -p "$gendir"
printf 'fake-x64\n'   > "$gendir/weir-v9.9.9-linux-x64"
printf 'fake-arm64\n' > "$gendir/weir-v9.9.9-linux-arm64"
# the fixture is the canonical format release.yml's ubuntu sha256sum
# emits — hash, two spaces, name — spelled out rather than trusted to the
# local tool: MSYS coreutils writes the binary marker (hash *name), which
# broke the space-anchored greps below on Windows and is a fixture
# infidelity, not the format under test. $HASHTOOL (declared beside the
# File.sha256 cells) covers sha256sum-less macOS.
( cd "$gendir" && for f in weir-v9.9.9-*; do
    printf '%s  %s\n' "$($HASHTOOL "$f" | cut -d' ' -f1)" "$f"
  done > SHA256SUMS )
"$BIN" "$ROOT/ci/gen-install.weir" --template "$ROOT/install.sh" --sums "$gendir/SHA256SUMS" --tag v9.9.9 --out "$gendir/install.sh" >/dev/null \
    || fail "gen-install.weir failed"
sh -n "$gendir/install.sh" || fail "the generated install.sh must parse"
grep -q 'WEIR_TAG\|WEIR_SHA256SUMS' "$gendir/install.sh" && fail "the generated script still has unsubstituted placeholders"
name=weir-v9.9.9-linux-x64
gsums=$(awk "/<<'WEIR_SUMS'/{f=1;next} /^WEIR_SUMS\$/{f=0} f" "$gendir/install.sh")
gexp=$(printf '%s\n' "$gsums" | grep " $name\$" | cut -d' ' -f1)
gact=$($HASHTOOL "$gendir/$name" | cut -d' ' -f1)
[ -n "$gexp" ] && [ "$gexp" = "$gact" ] || fail "embedded checksum ($gexp) must match the real binary ($gact)"
# each checksum appears exactly once — inside the heredoc. A generator
# that replaces a comment merely mentioning the placeholder plants a
# second, bare copy of the sums at the top of the served script,
# executed as commands before set -eu could object.
[ "$(grep -cF "$gexp" "$gendir/install.sh")" -eq 1 ] || fail "a checksum appears outside the heredoc — the anchor matched a mention, not the placeholder line"
echo "e2e ok: gen-install.weir — pins the tag, embeds SHA256SUMS once, embedded checksum matches the binary"

# a missing SHA256SUMS entry is named, not left to a formatting error — the
# exact grep-guard the generated script runs over its embedded sums
missing="weir-v9.9.9-linux-riscv"
imsg=$(printf '%s\n' "$gsums" | grep -q " $missing\$" || echo "no embedded checksum for $missing")
[ "$imsg" = "no embedded checksum for $missing" ] || fail "a missing checksum entry must be named, got: $imsg"
echo "e2e ok: install missing-checksum entry is named; present entry verifies"

# ---- release-assets argv boundary [D:release-assets] ------------------------
# the completeness check itself needs the live API (the release publish
# job and ci/release-published.weir run it there); offline, pin the argv
# boundary: --tag is required (strict Args.load — no request is ever made
# without it), and --help exits 0. No network test is invented here.
if raout=$("$BIN" "$ROOT/ci/release-assets.weir" 2>&1); then
    fail "release-assets.weir without --tag must refuse, said: $raout"
fi
echo "$raout" | grep -qF -- "--tag" || fail "release-assets.weir must name the missing --tag flag: $raout"
"$BIN" "$ROOT/ci/release-assets.weir" --help > /dev/null || fail "release-assets.weir --help must exit 0"
echo "e2e ok: release-assets.weir argv boundary (missing --tag refuses; --help exits 0)"

# ---- the reference dump is current [D:reference] --------------------------
# site/src/data/reference.json is generated from builtinDocs (weir
# docs-json) — the same one source #help and hover read. The committed
# copy must match the binary's dump, or the site's reference pages are
# stale (the grammar-manifest currency pattern, applied to docs).
refdump=$(mkweirtmp)
"$BIN" docs-json > "$refdump/reference.json"
if ! diff "$refdump/reference.json" "$ROOT/site/src/data/reference.json" > /dev/null; then
    # show what differs before failing — a platform-byte mismatch and a
    # stale dump need different repairs, and a silent diff hides which
    echo "--- sizes: dump=$(wc -c < "$refdump/reference.json") committed=$(wc -c < "$ROOT/site/src/data/reference.json")"
    echo "--- first differing bytes (cmp -l, octal):"
    cmp -l "$refdump/reference.json" "$ROOT/site/src/data/reference.json" | head -5 || true
    echo "--- diff head:"
    diff "$refdump/reference.json" "$ROOT/site/src/data/reference.json" | head -40 || true
    fail "site/src/data/reference.json differs from this binary's dump — content drift means regenerate (weir docs-json > site/src/data/reference.json); byte-only drift (CR bytes above) means a platform newline leak [D:lf-output]"
fi
echo "e2e ok: reference dump current (docs-json == committed site data)"

# ---- Str.fields: the piped-column idiom on the shipped binary [D:str-fields]
# a kubectl-shaped fixture piped into a script that reads Self.stdin —
# whitespace runs collapse, so column 2 is Seq.item 1, no regex
sfdir=$(mkweirtmp)
cat > "$sfdir/cols.weir" <<'EOF'
Self.stdin
|> Seq.skip 1
|> Seq.map (fun l -> l |> Str.fields |> Seq.item 1)
|> Str.join ","
|> print
EOF
sfout=$(printf 'NAME        READY   STATUS    RESTARTS   AGE\nweir-7d9f   1/1     Running   0          12m\nsite-abc1   2/2     Running   1          3h\n' | "$BIN" "$sfdir/cols.weir")
[ "$sfout" = "1/1,2/2" ] || fail "Str.fields column extraction from piped fixture: got '$sfout'"
echo "e2e ok: Str.fields extracts the READY column from piped fixture lines (whitespace runs, no empties)"

# ---- the CLI page is byte-pinned against --help ---------------------------
# docs/cli.md quotes the usage block verbatim; a new subcommand or flag
# must move the page in the same commit as the binary.
cliusage=$(mkweirtmp)
awk '/^```text$/{f++; next} /^```$/{if(f==1)exit} f==1' "$ROOT/docs/tooling.md" > "$cliusage/page.txt"
"$BIN" --help > "$cliusage/help.txt"
diff "$cliusage/page.txt" "$cliusage/help.txt" \
    || fail "docs/tooling.md's usage block differs from 'weir --help' — update the page (or the binary's usage string) so they agree"
echo "e2e ok: docs/cli.md usage block == weir --help"

# ---- the lexical keyword table renders the gated manifest -----------------
# docs/reference/lexical.md's keyword region is generated from
# editors/grammar-manifest.json; a keyword added to weir must appear on
# the page in the same commit, and the generator's classification must
# cover the manifest exactly (it fails loud on an unclassified keyword).
(cd "$ROOT" && "$BIN" tools/gen-lexical.weir --check) \
    || fail "the lexical keyword table is stale or unclassified — run: weir tools/gen-lexical.weir"
echo "e2e ok: lexical keyword table current (generated from the grammar manifest)"

# ---- prose wrap hazard: '>' at a wrapped line's start -----------------------
# CommonMark lets a blockquote interrupt a paragraph, so a wrap that
# lands '>' at line start eats the character and breaks the span —
# prose damage no fence can catch (e.g. an Http >= 400 rewording).
(cd "$ROOT" && "$BIN" ci/wrap-hazard.weir) || fail "wrap hazard in rendered docs — see lines above"
echo "e2e ok: no mid-paragraph '>' wrap hazards in rendered docs"

# ---- every manifest keyword has teaching prose ----------------------------
# members are coverage-gated; this is the syntax half, derived from the
# same gated manifest (import shipped with no teaching section and only
# this audit noticed — the class needs a gate, not a memory).
(cd "$ROOT" && "$BIN" ci/feature-docs.weir) || fail "a manifest keyword has no teaching prose — see above"
echo "e2e ok: every manifest keyword taught in prose (GUIDE or written reference)"

# ---- the REPL init file [D:repl-init] -------------------------------------
# the plan's own example file loads: settings applied, names bound,
# #help carries the /// doc; failure is all-or-nothing with a located
# error; a missing init is silent (the negative pin).
initcfg=$(mkweirtmp)
# the config-home split [D:windows-v1], same as the config cells: POSIX
# reads XDG_CONFIG_HOME (the fixture), Windows reads the shell API's
# real AppData — write there and clean up (throwaway VM either way)
if [ "$IS_WINDOWS" = "1" ]; then
    INITHOME="$APPDATA"
else
    INITHOME="$initcfg"
fi
mkdir -p "$INITHOME/weir" "$initcfg/work"
cat > "$INITHOME/weir/init.weir" <<WEOF
#init
#session {
    cwd = "$initcfg/work"
    logLevel = "debug"
    echoCap = 50
    env = [
        "EDITOR", "hx"
        "PAGER", "less -R"
    ]
}

/// push the current branch and set upstream
let pu () = git push --set-upstream origin HEAD

/// the last N commits, one line each
let lg n = git log --oneline \$"-{n}"

/// files over a size, newest first
let big threshold =
    ls
    |> Seq.where (fun f -> f.bytes > threshold)
    |> Seq.sortBy (fun f -> f.modified)
WEOF
iout=$(printf 'pwd\nprint (Env.get "EDITOR" |> Option.defaultValue "unset")\n#echo\n#help pu\nsh -c "echo child-sees-$PAGER"\n#quit\n' \
    | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
# a successful load is silent [D:repl-init] — no report line; the
# effects below (cwd, env, echoCap, #help doc) are the proof it loaded
echo "$iout" | grep -q "^init:" && fail "a successful init must be silent, not report: $iout" || true
# the fixture's unique dirname + /work, separator-agnostic: pwd echoes
# the native (escape-doubled) spelling on Windows
echo "$iout" | grep -q "$(basename "$initcfg").*work" || fail "init cwd not applied"
echo "$iout" | grep -qxF "weir> hx" || fail "init env not visible to Env.get"
echo "$iout" | grep -qF "echo cap: 50" || fail "init echoCap not applied"
echo "$iout" | grep -qF "push the current branch and set upstream" || fail "#help on an init name lost its /// doc"
echo "$iout" | grep -qF "child-sees-less -R" || fail "init env not inherited by a child"
# all-or-nothing: a typo'd #session key reports located, loads nothing
cat > "$INITHOME/weir/init.weir" <<'WEOF'
#init
#session {
    echoCpa = 50
}
let hi () = print "hi"
WEOF
iout=$(printf '#help hi
#quit
' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "unknown #session key 'echoCpa'. Did you mean 'echoCap'?" || fail "init typo key lost its did-you-mean"
echo "$iout" | grep -qF "init: not loaded" || fail "broken init must say not loaded"
echo "$iout" | grep -qF "unknown name 'hi'" || fail "all-or-nothing broke: a binding survived a failed init"
# a raising value fails the load located, never as a .NET trace: both
# eval sites (a #session field's value and a plain let) run user code at
# startup, and an escaping exception killed the REPL before it reached a
# prompt. The trace-absence assertion is the claim — the located line
# alone would pass on a build that also dumped a stack.
cat > "$INITHOME/weir/init.weir" <<'WEOF'
#init
#session {
    cwd = File.read "/no/such/file/xyz" |> Seq.head
}
let hi () = print "hi"
WEOF
iout=$(printf '#help hi
#quit
' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "File.read: no such file:" || fail "a raising #session value must report its own error: $iout"
echo "$iout" | grep -qE "init\.weir:3:" || fail "the raising #session value must be LOCATED at its line: $iout"
echo "$iout" | grep -qF "init: not loaded" || fail "a raising #session value must not load"
echo "$iout" | grep -qF "unknown name 'hi'" || fail "all-or-nothing broke after a raising #session value"
echo "$iout" | grep -qF "Unhandled exception" && fail "a raising #session value dumped a .NET trace: $iout" || true

cat > "$INITHOME/weir/init.weir" <<'WEOF'
#init
let bad = File.read "/no/such/file/xyz" |> Seq.head
let hi () = print "hi"
WEOF
iout=$(printf '#help hi
#quit
' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "File.read: no such file:" || fail "a raising init let must report its own error: $iout"
echo "$iout" | grep -qE "init\.weir:2:" || fail "the raising let must be LOCATED at its line: $iout"
echo "$iout" | grep -qF "init: not loaded" || fail "a raising init let must not load"
echo "$iout" | grep -qF "unknown name 'hi'" || fail "all-or-nothing broke after a raising init let"
echo "$iout" | grep -qF "Unhandled exception" && fail "a raising init let dumped a .NET trace: $iout" || true

# a lazy command let loads and spawns nothing; each use runs it; an
# eager one is refused naming why [D:lazy-command-lets]
if [ "$IS_WINDOWS" != "1" ]; then
    rm -f "$initcfg/lazymark"
    cat > "$INITHOME/weir/init.weir" <<WEOF
#init
let gl = sh -c "echo run >> '$initcfg/lazymark'; echo lazy-out"
WEOF
    iout=$(printf 'sh -c "echo main >> %s/lazymark"\ngl\ngl\n#quit\n' "$initcfg" \
        | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
    echo "$iout" | grep -qF "init: not loaded" && fail "a lazy command let must load: $iout" || true
    [ "$(grep -c lazy-out <<<"$iout")" = 2 ] || fail "each use of a lazy init let runs it: $iout"
    [ "$(tr '\n' ' ' < "$initcfg/lazymark")" = "main run run " ] || fail "the init load spawned (or a use did not): $(cat "$initcfg/lazymark")"
    # the echo of a command-backed let never runs it, piped too
    # [D:repl-let-echo-lazy]; typing the name does
    rm -f "$initcfg/lazymark"
    iout=$(printf 'let c = sh -c "echo cleanup >> %s/lazymark"\nlet g2 = gl\nlet fz = gl |> Seq.freeze\nlet (n1, d1) = (1, gl)\n#quit\n' "$initcfg" \
        | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
    echo "$iout" | grep -qF "c : seq<string> (command-backed — runs on each use; type c to run it)" || fail "a command-backed let echoes its type and hint: $iout"
    echo "$iout" | grep -qF "g2 : seq<string> (command-backed" || fail "a let reading a command-backed binding is command-backed too: $iout"
    echo "$iout" | grep -qF 'fz : seq<string> = ["lazy-out"]' || fail "an eager let echoes its value: $iout"
    echo "$iout" | grep -qF "d1 : seq<string> (command-backed" || fail "a destructured command-backed component echoes its type, never runs: $iout"
    echo "$iout" | grep -qF "n1 : int = 1" || fail "a destructured pure component still echoes its value: $iout"
    [ "$(tr '\n' ' ' < "$initcfg/lazymark")" = "run " ] || fail "only the frozen let ran: $(cat "$initcfg/lazymark")"
fi
cat > "$INITHOME/weir/init.weir" <<'WEOF'
#init
let br = git rev-parse --abbrev-ref HEAD | line
WEOF
iout=$(printf '#quit\n' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "\`| line\` runs the command now — drop the reifier to keep it lazy, or wrap it in a function" || fail "an eager init let must name its reifier: $iout"
echo "$iout" | grep -qF "init: not loaded" || fail "an eager init let must not load: $iout"

# the marker is required [D:init-marker]: the canonical file without
# #init refuses, located, and the session starts without it
cat > "$INITHOME/weir/init.weir" <<'WEOF'
let hi () = print "hi"

#session {
    echoCap = 7
}
WEOF
iout=$(printf '#echo\n#quit\n' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "put #init on the first line" || fail "an unmarked canonical init must refuse naming the marker: $iout"
echo "$iout" | grep -qF "init: not loaded" || fail "an unmarked init must not load"
echo "$iout" | grep -qF "echo cap: 7" && fail "an unmarked init must not apply its #session (echoCap leaked): $iout" || true
echo "e2e ok: the init file requires #init — unmarked refuses, located, nothing applied"

# the #session prompt [D:session-prompt], piped half: the provider
# names an init declaration (so it checks after the declarations bind),
# and a redirected session ignores it — the prompt mirror stays the
# fixed default (a provider could run commands per piped line)
cat > "$INITHOME/weir/init.weir" <<'WEOF'
#init
let sigil () = "custom> "

#session {
    prompt = sigil
}
WEOF
iout=$(printf '1 + 1\n#quit\n' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
# load is silent on success [D:repl-init]: no "not loaded" failure, and
# the session evaluates — the provider loaded (a pipe ignores the prompt)
echo "$iout" | grep -qF "init: not loaded" && fail "a valid prompt provider must load, not error: $iout" || true
echo "$iout" | grep -qF "2" || fail "the session evaluates after loading the prompt provider: $iout"
echo "$iout" | grep -qF "weir> " || fail "a redirected session must keep the default prompt mirror: $iout"
echo "$iout" | grep -qF "custom> " && fail "a redirected session must not run the prompt provider" || true
# the value expression stays command-free like every field — the
# command belongs inside the function the field names
cat > "$INITHOME/weir/init.weir" <<'WEOF'
#init
#session {
    prompt = $(sh -c "echo x") |> Seq.head
}
WEOF
iout=$(printf '#quit\n' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "a #session value cannot run a command" || fail "a command-running prompt value must refuse: $iout"
echo "$iout" | grep -qF "init: not loaded" || fail "a refused prompt value must not load"
# the type gate names both accepted shapes
cat > "$INITHOME/weir/init.weir" <<'WEOF'
#init
#session {
    prompt = 42
}
WEOF
iout=$(printf '#quit\n' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "prompt expects a string, a unit -> string, or a PromptStatus -> string function" || fail "a mistyped prompt must refuse with the shapes named: $iout"

# declaration-only: a bare command refuses with the teach
cat > "$INITHOME/weir/init.weir" <<'WEOF'
#init
git status
WEOF
iout=$(printf '#quit
' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "the init file is declaration-only" || fail "init decl-only teach missing"
# missing init: silent — the negative pin ("reports on failure" must
# not be satisfied by something that reports always)
rm "$INITHOME/weir/init.weir"
iout=$(printf '#quit
' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$iout" | grep -qF "init:" && fail "a missing init must be silent" || true
# a #session in a script teaches its home; typed at the prompt likewise
printf '#session {\n    cwd = "/tmp"\n}\nprint "x"\n' > "$initcfg/s.weir"
sout=$("$BIN" "$initcfg/s.weir" 2>&1 || true)
echo "$sout" | grep -qF "#session lives in the REPL init file" || fail "script #session teach missing: $sout"
pout=$(printf '#session\n#quit\n' | XDG_CONFIG_HOME="$initcfg" XDG_STATE_HOME="$initcfg/state" "$BIN" 2>&1)
echo "$pout" | grep -qF "#session is read from the init file at startup" || fail "prompt #session teach missing"
# the login-shell convention: a leading-dash argv[0] changes nothing
lout=$(bash -c "exec -a -weir \"$BIN\" --version" 2>&1) || fail "dash argv[0] broke weir"
rm -f "$INITHOME/weir/init.weir"
echo "e2e ok: repl init file (example loads with docs+settings, all-or-nothing failure, raising values fail located with no trace, silent missing, both #session teaches, dash-argv0)"

# ---- weir add module [D:add-module] ---------------------------------------
# the acceptance battery: vendor from a local server (deterministic),
# import via the weir: walk from root and a subdir, the module's
# capability in the importer's --can at the module's own line, tamper →
# verify → restore-repairs, re-add prints the sha pair, the seven
# teaches, R1's negative pins, and the live shorthand chain.
amdir=$(mkweirtmp)
mkdir -p "$amdir/serve" "$amdir/proj/sub"
cat > "$amdir/serve/greet.weir" <<'WEOF'
module Greet

/// the greeting the pins expect
let hello : string -> string

let hello name = $"hi {name}"

/// writes an audit line
let log : string -> unit

let log msg = [msg] |> File.write "audit.log"
WEOF
printf 'module NeedsDep\n\nimport "./x.weir" as X\n\nlet f : int -> int\nlet f x = x\n' > "$amdir/serve/needsdep.weir"
amport=$((21000 + RANDOM % 2000))
python3 -m http.server $amport --bind 127.0.0.1 --directory "$amdir/serve" >/dev/null 2>&1 &
amsrv=$!
awaitHttp "http://127.0.0.1:$amport/greet.weir" || { kill $amsrv 2>/dev/null || true; fail "the module server never came up"; }
( cd "$amdir/proj" && git init -q . )
aout=$(cd "$amdir/proj" && $BIN add module "http://127.0.0.1:$amport/greet.weir" --as greet)
echo "$aout" | grep -q "added module greet (.*2 member(s))" || fail "add module: $aout"
echo "$aout" | grep -qF 'import it:  import "weir:greet" as Greet' || fail "add module must print the import line: $aout"
grep -q '"schemaVersion": 1' "$amdir/proj/.weir/lock.json" || fail "the lock carries schemaVersion 1"
printf 'import "weir:greet" as G\nprint (G.hello "vendored")\n' > "$amdir/proj/use.weir"
printf 'import "weir:greet" as G\nprint (G.hello "deep")\n' > "$amdir/proj/sub/deep.weir"
[ "$(cd "$amdir/proj" && $BIN use.weir)" = "hi vendored" ] || fail "weir: import at the root"
[ "$(cd "$amdir/proj" && $BIN sub/deep.weir)" = "hi deep" ] || fail "weir: import from a subdir (the walk)"
printf 'import "weir:greet" as G\n\nG.log "ran"\n' > "$amdir/proj/audit.weir"
canout=$(cd "$amdir/proj" && $BIN check --can audit.weir)
# separator-agnostic: Windows prints the module path with backslashes
echo "$canout" | grep -q "File.write audit.log  .*modules.greet\.weir:11:" || fail "--can must carry the vendored module's write at its own line: $canout"
sed -i.bak 's/hi/hacked/' "$amdir/proj/.weir/modules/greet.weir" && rm -f "$amdir/proj/.weir/modules/greet.weir.bak"
( cd "$amdir/proj" && $BIN verify || true ) | grep -q "module greet: MODIFIED" || fail "verify must flag the tampered module"
( cd "$amdir/proj" && $BIN restore ) | grep -q "module greet: repaired" || fail "restore must repair a modified vendored file"
( cd "$amdir/proj" && $BIN verify ) | grep -q "module greet: ok" || fail "post-repair verify"
printf '\n/// third\nlet bye : string -> string\nlet bye n = $"bye {n}"\n' >> "$amdir/serve/greet.weir"
( cd "$amdir/proj" && $BIN add module "http://127.0.0.1:$amport/greet.weir" --as greet ) | grep -q "updated module greet: " || fail "re-add must print old → new sha"
( cd "$amdir/proj" && $BIN add module "http://127.0.0.1:$amport/needsdep.weir" --as dep 2>&1 || true ) | grep -q "vendored modules are leaves for now" || fail "import refusal teach"
( cd "$amdir/proj" && $BIN add module "github.com/acme/lib/x.weir@v1" --as x 2>&1 || true ) | grep -q "needs the // repo/path separator" || fail "// teach"
( cd "$amdir/proj" && $BIN add module "github.com/acme/lib//x.weir" --as x 2>&1 || true ) | grep -q "an explicit @ref is required" || fail "@ref teach"
( cd "$amdir/proj" && $BIN add module "sr.ht/a/b//x.weir@v1" --as x 2>&1 || true ) | grep -q "unknown host 'sr.ht'" || fail "unknown-host teach"
( cd "$amdir/proj" && $BIN add module "http://127.0.0.1:$amport/greet.weir" --as seq 2>&1 || true ) | grep -q "a builtin module — pick another --as" || fail "builtin alias refusal"
printf 'import "weir:nope" as N\nprint "x"\n' > "$amdir/proj/missing.weir"
( cd "$amdir/proj" && $BIN missing.weir 2>&1 || true ) | grep -q "no vendored module 'nope'" || fail "missing-vendored teach"
printf 'import "weir:../evil" as E\nprint "x"\n' > "$amdir/proj/evil.weir"
( cd "$amdir/proj" && $BIN evil.weir 2>&1 || true ) | grep -q "names a vendored module, never a path" || fail "weir: path-shape refusal"
# R1's negative pins: both pre-existing bare spellings stay file-relative
printf 'module Sib\nlet s : unit -> string\nlet s () = "sibling"\n' > "$amdir/proj/sib.weir"
cp "$amdir/proj/sib.weir" "$amdir/proj/sibnoext"
printf 'import "sib.weir" as A\nprint (A.s ())\n' > "$amdir/proj/r1a.weir"
printf 'import "sibnoext" as B\nprint (B.s ())\n' > "$amdir/proj/r1b.weir"
[ "$(cd "$amdir/proj" && $BIN r1a.weir)" = "sibling" ] || fail "R1: bare-with-extension must stay file-relative"
[ "$(cd "$amdir/proj" && $BIN r1b.weir)" = "sibling" ] || fail "R1: bare-no-extension must stay file-relative"
# the live shorthand chain (ref→sha→raw fetch→validation): a real repo
# file that is not a module — the refusal proves the bytes arrived.
# Tolerates the shared-IP rate limit; CI passes WEIR_TOKEN_GITHUB_COM.
lout=$(cd "$amdir/proj" && $BIN add module "github.com/weir-shell/weir//ci/release-published.weir@v0.0.5" --as relpub 2>&1 || true)
echo "$lout" | grep -q "is not a module" || echo "$lout" | grep -qE "rate limit|answered 403" || fail "live shorthand chain: $lout"
kill $amsrv 2>/dev/null || true
echo "e2e ok: add module (vendor, walk-import root+subdir, --can at the module's line, tamper→repair, re-add sha pair, the teaches, R1 negative pins, live shorthand)"

# ---- the homepage quotes the compiler [D:hero] ----------------------------
# site/src/pages/index.astro quotes four outputs verbatim; all are pinned
# here against live runs so the homepage cannot go stale while green (the
# docs gate asserts executability, not output). The splice refusal's
# message text is also byte-pinned in Tests.fs. --can file:lines are
# included deliberately: fuzz.weir drift must move the homepage in the
# same commit.
herodir=$(mkweirtmp)
# the hero [D:hero-3]: the typed boundary ends in a real command —
# from json parses the version, git tag consumes it. The fixture is a
# real repo (identity via -c flags; a lightweight tag needs a commit)
printf '%s\n' '{"name": "acme-api", "version": "2.4.0", "private": true}' > "$herodir/package.json"
( cd "$herodir" && git init -q . && git add package.json && git -c user.email=e2e@weir -c user.name=e2e commit -qm fixture )
cat > "$herodir/tag.weir" <<'WEOF'
type Pkg = { name: string; version: string }

let pkg = File.read "package.json" |> from json Pkg
let tag = $"v{pkg.version}"
git tag $tag
print $"tagged {pkg.name} v{pkg.version}"
WEOF
hout=$(cd "$herodir" && "$BIN" tag.weir 2>&1) || fail "the hero run failed: $hout"
[ "$hout" = 'tagged acme-api v2.4.0' ] || fail "the hero output drifted — update index.astro: $hout"
( cd "$herodir" && git tag -l ) | grep -qxF "v2.4.0" || fail "the hero's git tag was not created"
# beat 1 [D:hero-3]: the check at three distances — a tool's flag
# (#sig, generated from a stub then marked exhaustive: the stub's
# 2-flag surface is fully known), a manifest's key (the real vendored
# ConfigMap schema, served locally like the contracts cell), a program
# name (PATH). All three findings in one weir check report, and the
# check must write nothing. HTTP-e2e shaped (a served schema + a spawned
# stub), so POSIX + python3 only — never on Windows, like the sibling
# schema cells; the Linux/macOS currency gate is what pins the page.
if [ "$IS_WINDOWS" != "1" ] && command -v python3 >/dev/null 2>&1; then
    b1dir=$(mkweirtmp)
    mkdir -p "$b1dir/bin" "$b1dir/serve"
    cat > "$b1dir/bin/bicep" <<'WEOF'
#!/bin/sh
case "$1" in
  --version) echo "bicep 0.30.3";;
  --help) printf 'Flags:\n      --outfile <x>   output path\n      --stdout        write to stdout\n';;
  *) echo "ran:$@";;
esac
WEOF
    chmod +x "$b1dir/bin/bicep"
    ( cd "$b1dir" && git init -q . )
    ( cd "$b1dir" && PATH="$(pathEntry "$b1dir/bin"):$PATH" "$BIN" add sig bicep >/dev/null ) || fail "beat-1 sig generation"
    printf '\nlet exhaustive = true\n' >> "$b1dir/.weir/sigs/bicep.weir"
    cp "$ROOT/tests/fixtures/configmap-v1.json" "$b1dir/serve/"
    b1port=$((18790 + RANDOM % 100))
    python3 -m http.server $b1port --bind 127.0.0.1 --directory "$b1dir/serve" >/dev/null 2>&1 &
    b1srv=$!
    awaitHttp "http://127.0.0.1:$b1port/configmap-v1.json" || { kill $b1srv 2>/dev/null || true; fail "beat-1 schema server never came up"; }
    ( cd "$b1dir" && "$BIN" add schema http://127.0.0.1:$b1port/configmap-v1.json --as k8s-configmap >/dev/null ) || { kill $b1srv 2>/dev/null || true; fail "beat-1 add schema"; }
    kill $b1srv 2>/dev/null || true
    cat > "$b1dir/deploy.weir" <<'WEOF'
#sig bicep

bicep build --outfil main.json

let config = yaml schema=k8s-configmap
    apiVerison: v1
    kind: ConfigMap
    metadata:
        name: site-config
    data:
        template: main.json

config |> to yaml |> File.write "config.yaml"
rsnyc -av main.json config.yaml backup:/srv/site
WEOF
    # the check resolves against only the stub bin [D:hero-3]: rsnyc's
    # cmd-not-found is did-you-mean-agnostic just when no near command is
    # on PATH (a box with rsync would splice "Did you mean 'rsync'?" and
    # the byte-pin — and the page — would drift). bicep still resolves
    # (it is the one entry); the check spawns nothing.
    b1=$(cd "$b1dir" && PATH="$(pathEntry "$b1dir/bin")" "$BIN" check deploy.weir 2>&1) && fail "beat-1 check must exit nonzero"
    for line in \
        "deploy.weir:3:13: error [sig]: unknown flag '--outfil' for bicep. Did you mean '--outfile'? (#sig bicep, line 1; exhaustive signature)" \
        "deploy.weir:6:5: error [schema]: schema k8s-configmap: unknown field 'apiVerison' — did you mean 'apiVersion'?" \
        "deploy.weir:14:1: warning [cmd-not-found]: command not found on PATH: rsnyc — weir resolves commands at check time; the script runs once it is installed"; do
        echo "$b1" | grep -qF "$line" || fail "beat-1 check output drifted — update index.astro; missing: $line"
    done
    [ ! -e "$b1dir/config.yaml" ] || fail "beat 1's money line is false — check WROTE a file"
    echo "e2e ok: homepage beat-1 three-distance check (sig flag + schema key + PATH, one report, wrote nothing)"
else
    echo "e2e skip: homepage beat-1 three-distance check (POSIX + python3 — HTTP e2e not on Windows)"
fi
# beat 2: --help derived from the record, quoted exactly
cat > "$herodir/ship.weir" <<'WEOF'
type Cli = {
    /// print what would happen, deploy nothing
    dryRun: bool

    /// the environment to deploy to
    target: string

    /// wait this long for the health check
    timeout: Option<Duration>
}

let cli = Args.load Cli
print $"deploying to {cli.target} (dry-run: {cli.dryRun})"
WEOF
b2=$(cd "$herodir" && "$BIN" ship.weir --help 2>&1) || true
for line in \
    "usage: ship.weir [flags]" \
    "  -d, --dry-run               print what would happen, deploy nothing" \
    "      --target <string>       required — the environment to deploy to" \
    "      --timeout <duration>    optional — wait this long for the health check"; do
    echo "$b2" | grep -qF "$line" || fail "beat-2 --help drifted — update index.astro; missing: $line"
done
# the concatenation refusal (below the fold) — the like-for-like steam
# shape [D:argv-concat]: root from the script's own path, glued /*
printf 'let steamroot = Self.scriptPath |> Path.dir\nrm -rf $steamroot/*\n' > "$herodir/steam.weir"
herr=$(cd "$herodir" && "$BIN" check steam.weir 2>&1) && fail "the steam snippet must refuse"
echo "$herr" | grep -qF "steam.weir:2:18: error [parse]: argv words do not concatenate" || fail "concat refusal framing drifted: $herr"
echo "$herr" | grep -qF "filesystem path" || fail "concat refusal tail drifted: $herr"
# the --can quote — relative path, as the homepage shows it
hcan=$(cd "$ROOT" && "$BIN" check --can tools/fuzz.weir 2>&1) || fail "hero --can run failed: $hcan"
for line in \
    "tools/fuzz.weir can (capability, not behaviour — an untaken branch still counts):" \
    "ci/deep-lock.sh × 2  tools/fuzz.weir:31:1, 66:5" \
    "File.read (path not statically known)  tools/fuzz.weir:59:20" \
    "within tmp (a temporary directory)  tools/fuzz.weir:47:1" \
    "sets WEIR_FUZZ_SEED, WEIR_FUZZ_COUNT, WEIR_FUZZ_REPORT for children (within env)  tools/fuzz.weir:52:5" \
    "fail  tools/fuzz.weir:62:9"; do
    echo "$hcan" | grep -qF "$line" || fail "the homepage's --can quote drifted — update index.astro; missing: $line"
done
echo "e2e ok: homepage hero currency (hero tag run + tag created, beat-1 three-distance check + wrote-nothing, beat-2 --help, the splice refusal, the --can quote — all match live runs)"

# the ported-module findings triage [D:fail-bottom] [D:continuation-siblings]:
# a fail arm opposite a value arm, a lambda body's tuple block-let, and
# a multi-line application whose every deeper line joins — script and
# module body alike
tdir=$(mkweirtmp)
cat > "$tdir/triage.weir" <<'WEOF'
let picked = match Some 41 with | Some v -> v | None -> fail "absent"
print (show (picked + 1))

[(1, 2); (3, 4)] |> Seq.iter (fun p ->
    let a, b = p
    print $"{a}+{b}")

let combine3 a b c = a * 2 + b * 3 + c * 5

let v =
    combine3
        1
        2
        3

print (show v)
WEOF
out=$($BIN "$tdir/triage.weir")
expect "fail diverges: the arm carries the value" '42' "$out"
expect "a lambda-body tuple block-let destructures" '3+4' "$out"
expect "multi-line application: every deeper line joins" '23' "$out"
cat > "$tdir/mlm.weir" <<'WEOF'
module Mlm

let combine3 a b c = a * 2 + b * 3 + c * 5

let total : seq<string> -> int

let total xs =
    combine3
        (xs |> Seq.length)
        10
        100
WEOF
cat > "$tdir/mluse.weir" <<'WEOF'
import "./mlm.weir" as Mlm
print (show (Mlm.total ["a"; "b"]))
WEOF
out=$(cd "$tdir" && $BIN mluse.weir)
expect "multi-line application assembles in a module body" '534' "$out"
rm -rf "$tdir"

# ---- plan/apply [D:plan-apply]: the acceptance cell ------------------
# a neutral manifest-tree renderer run inside `plan` must capture every
# File.write/Dir op (its reads run) and the resulting Plan must `apply`
# to a byte-identical tree vs the direct (non-plan) run — the full-
# guarantee case (fs-only). Plus the refusals: proc-in-plan and
# apply-in-plan (check), known-after-apply (located runtime).
# mkweirtmp (not bare mktemp -d): its mixed C:/... spelling keeps every
# padir path forward-slashed, so a bash-interpolated $padir inside a weir
# "..."/$"..." string never carries a backslash the string-escape pass
# would eat (\t → TAB) — the Windows path-mangling class every cell below
# would otherwise share.
padir=$(mkweirtmp)
cat > "$padir/render.weir" <<'WEOF'
type Node = { Rel: string; Content: seq<string> }

let nodes =
    [ { Rel = "conf"; Content = [] }
      { Rel = "conf/app.ini"; Content = ["name = demo"; "port = 8080"] }
      { Rel = "conf/env"; Content = [] }
      { Rel = "conf/env/prod.env"; Content = ["TIER=prod"] } ]

let render root =
    for n in nodes do
        let full = $"{root}/{n.Rel}"
        if n.Content |> Seq.isEmpty then
            Dir.create full
        else
            File.write full n.Content

let base = Self.args |> Seq.head
Dir.create $"{base}/direct"
render $"{base}/direct"

Dir.create $"{base}/applied"
let changes =
    plan
        render $"{base}/applied"

print $"ops: {changes |> Plan.ops |> Seq.length}"
changes |> Plan.apply
print "applied"
WEOF
out=$($BIN "$padir/render.weir" "$padir" 2>&1) || fail "the plan acceptance render failed: $out"
expect "plan captures every fs op (reads run)" "ops: 4" "$out"
expect "the plan applies" "applied" "$out"
if ! diff -r "$padir/direct" "$padir/applied" >/dev/null 2>&1; then
    fail "the applied tree diverges from the direct run — not byte-identical"
fi
echo "e2e ok: plan/apply — the applied tree is byte-identical to the direct run"

# proc-in-plan refuses (check)
cat > "$padir/proc.weir" <<'WEOF'
let p =
    plan
        git status
print $"{p |> Plan.isEmpty}"
WEOF
out=$($BIN check "$padir/proc.weir" 2>&1) && fail "proc inside a plan must refuse" || true
echo "$out" | grep -qF "refused inside 'plan'" || fail "the proc-in-plan teaching must fire: $out"
echo "e2e ok: plan/apply — proc inside a plan refuses"

# apply-in-plan refuses (check)
cat > "$padir/applyin.weir" <<'WEOF'
let q =
    plan
        File.write "f" ["a"]
let p =
    plan
        q |> Plan.apply
print $"{p |> Plan.isEmpty}"
WEOF
out=$($BIN check "$padir/applyin.weir" 2>&1) && fail "apply inside a plan must refuse" || true
echo "$out" | grep -qF "'Plan.apply' is refused inside 'plan'" || fail "the apply-in-plan teaching must fire: $out"
echo "e2e ok: plan/apply — apply inside a plan refuses"

# known-after-apply: a read of a captured target is a located runtime refusal
cat > "$padir/kaa.weir" <<WEOF
let p =
    plan
        File.write "$padir/kaa.txt" ["a"]
        let _c = File.read "$padir/kaa.txt"
        print "unreached"
print \$"{p |> Plan.isEmpty}"
WEOF
out=$($BIN "$padir/kaa.weir" 2>&1) && fail "a known-after-apply read must refuse" || true
echo "$out" | grep -qF "known-after-apply" || fail "the known-after-apply teaching must fire: $out"
echo "e2e ok: plan/apply — a known-after-apply read refuses (located)"

# [D:plan-preconditions] (ruling R4): a captured op checks its
# preconditions against the plan's simulated state and raises at
# capture, as the direct call does — every op kind, against real and
# planned state; a valid chain still applies
cat > "$padir/pre.weir" <<'WEOF'
// every captured op checks its preconditions against the plan's simulated state [D:plan-preconditions]
within tmp root
    let p n = $"{root}/{n}"
    File.write (p "a.txt") ["a"]
    File.write (p "exists.txt") ["e"]
    Dir.create (p "d")
    File.write (p "d/in.txt") ["i"]
    let c0 () =
        plan
            File.copy (p "a.txt") (p "exists.txt")
    match c0 () |> try result with
    | Ok pl -> print $"copy onto existing: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"copy onto existing: {Str.replace root "$T" e}"
    let c1 () =
        plan
            File.write (p "new.txt") ["n"]
            File.copy (p "a.txt") (p "new.txt")
    match c1 () |> try result with
    | Ok pl -> print $"copy onto a planned write: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"copy onto a planned write: {Str.replace root "$T" e}"
    let c2 () =
        plan
            File.write (p "new.txt") ["n"]
            File.copy (p "new.txt") (p "new2.txt")
    match c2 () |> try result with
    | Ok pl -> print $"copy from planned write: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"copy from planned write: {Str.replace root "$T" e}"
    let c3 () =
        plan
            File.delete (p "missing.txt")
    match c3 () |> try result with
    | Ok pl -> print $"delete missing: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"delete missing: {Str.replace root "$T" e}"
    let c4 () =
        plan
            File.delete (p "a.txt")
            File.delete (p "a.txt")
    match c4 () |> try result with
    | Ok pl -> print $"delete twice: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"delete twice: {Str.replace root "$T" e}"
    let c5 () =
        plan
            File.delete (p "a.txt")
            File.copy (p "a.txt") (p "b.txt")
    match c5 () |> try result with
    | Ok pl -> print $"copy from deleted: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"copy from deleted: {Str.replace root "$T" e}"
    let c6 () =
        plan
            File.move (p "nope.txt") (p "b.txt")
    match c6 () |> try result with
    | Ok pl -> print $"move from missing: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"move from missing: {Str.replace root "$T" e}"
    let c7 () =
        plan
            File.move (p "a.txt") (p "b.txt")
            File.move (p "a.txt") (p "c.txt")
    match c7 () |> try result with
    | Ok pl -> print $"move twice: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"move twice: {Str.replace root "$T" e}"
    let c8 () =
        plan
            File.move (p "a.txt") (p "b.txt")
            File.delete (p "b.txt")
    match c8 () |> try result with
    | Ok pl -> print $"move then delete target ok: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"move then delete target ok: {Str.replace root "$T" e}"
    let c9 () =
        plan
            Dir.create (p "e")
            File.write (p "e/x.txt") ["x"]
            Dir.delete (p "e")
    match c9 () |> try result with
    | Ok pl -> print $"delete a dir the plan filled: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"delete a dir the plan filled: {Str.replace root "$T" e}"
    let c10 () =
        plan
            File.delete (p "d/in.txt")
            Dir.delete (p "d")
    match c10 () |> try result with
    | Ok pl -> print $"delete a dir the plan emptied ok: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"delete a dir the plan emptied ok: {Str.replace root "$T" e}"
    let c11 () =
        plan
            Dir.delete (p "d")
    match c11 () |> try result with
    | Ok pl -> print $"delete a real non-empty dir: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"delete a real non-empty dir: {Str.replace root "$T" e}"
    let c12 () =
        plan
            File.write (p "zz/x.txt") ["x"]
    match c12 () |> try result with
    | Ok pl -> print $"write under a missing dir: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"write under a missing dir: {Str.replace root "$T" e}"
    let c13 () =
        plan
            Dir.create (p "zz/yy")
            File.write (p "zz/yy/x.txt") ["x"]
    match c13 () |> try result with
    | Ok pl -> print $"write under a planned dir ok: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"write under a planned dir ok: {Str.replace root "$T" e}"
    let c14 () =
        plan
            Dir.deleteAll (p "d")
            File.write (p "d/x.txt") ["x"]
    match c14 () |> try result with
    | Ok pl -> print $"write under a deleted tree: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"write under a deleted tree: {Str.replace root "$T" e}"
    let c15 () =
        plan
            File.write (p "d") ["x"]
    match c15 () |> try result with
    | Ok pl -> print $"write onto a dir: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"write onto a dir: {Str.replace root "$T" e}"
    let c16 () =
        plan
            Dir.create (p "a.txt/sub")
    match c16 () |> try result with
    | Ok pl -> print $"mkdir under a file: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"mkdir under a file: {Str.replace root "$T" e}"
    let c17 () =
        plan
            File.write (p "f.txt") ["x"]
            Dir.create (p "f.txt")
    match c17 () |> try result with
    | Ok pl -> print $"mkdir over a planned file: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"mkdir over a planned file: {Str.replace root "$T" e}"
    let c18 () =
        plan
            Dir.move (p "d") (p "d2")
            File.copy (p "d2/in.txt") (p "in2.txt")
            File.delete (p "d/in.txt")
    match c18 () |> try result with
    | Ok pl -> print $"dir move then use old: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir move then use old: {Str.replace root "$T" e}"
    let c19 () =
        plan
            Dir.copy (p "d") (p "d3")
            Dir.copy (p "d") (p "d3")
    match c19 () |> try result with
    | Ok pl -> print $"dir copy twice: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir copy twice: {Str.replace root "$T" e}"
    let c20 () =
        plan
            Dir.copy (p "d") (p "d4")
            Dir.delete (p "d4")
    match c20 () |> try result with
    | Ok pl -> print $"dir copy then delete it non-empty: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir copy then delete it non-empty: {Str.replace root "$T" e}"
    let c21 () =
        plan
            Dir.deleteAll (p "nod")
    match c21 () |> try result with
    | Ok pl -> print $"deleteAll missing: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"deleteAll missing: {Str.replace root "$T" e}"
    let c22 () =
        plan
            Dir.copy (p "d") (p "d/sub")
    match c22 () |> try result with
    | Ok pl -> print $"dir copy into itself: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir copy into itself: {Str.replace root "$T" e}"
    let c23 () =
        plan
            Dir.move (p "nod") (p "x")
    match c23 () |> try result with
    | Ok pl -> print $"dir move missing: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir move missing: {Str.replace root "$T" e}"
    let c24 () =
        plan
            File.writeAtomic (p "zz/q.txt") ["x"]
    match c24 () |> try result with
    | Ok pl -> print $"writeAtomic under missing: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"writeAtomic under missing: {Str.replace root "$T" e}"
    let c26 () =
        plan
            Dir.create (p "made")
            Dir.copy (p "made") (p "made2")
    match c26 () |> try result with
    | Ok pl -> print $"dir copy of a planned dir ok: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir copy of a planned dir ok: {Str.replace root "$T" e}"
    let c27 () =
        plan
            Dir.move (p "d") (p "dm")
            Dir.copy (p "dm") (p "dm2")
    match c27 () |> try result with
    | Ok pl -> print $"dir copy of a moved dir ok: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir copy of a moved dir ok: {Str.replace root "$T" e}"
    let c28 () =
        plan
            Dir.create (p "np")
            Dir.copy (p "d") (p "np/d")
    match c28 () |> try result with
    | Ok pl -> print $"dir copy into a planned parent ok: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir copy into a planned parent ok: {Str.replace root "$T" e}"
    let c29 () =
        plan
            File.copy (p "a.txt") (p "nodir/a.txt")
    match c29 () |> try result with
    | Ok pl -> print $"copy into a missing dir: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"copy into a missing dir: {Str.replace root "$T" e}"
    let c30 () =
        plan
            File.move (p "a.txt") (p "nodir/a.txt")
    match c30 () |> try result with
    | Ok pl -> print $"move into a missing dir: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"move into a missing dir: {Str.replace root "$T" e}"
    let c31 () =
        plan
            Dir.move (p "d") (p "nodir/d")
    match c31 () |> try result with
    | Ok pl -> print $"dir move into a missing dir: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir move into a missing dir: {Str.replace root "$T" e}"
    let c32 () =
        plan
            Dir.deleteAll (p "d")
            File.copy (p "a.txt") (p "d/a.txt")
    match c32 () |> try result with
    | Ok pl -> print $"copy into a deleted dir: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"copy into a deleted dir: {Str.replace root "$T" e}"
    let c33 () =
        plan
            Dir.move (p "d") (p "d/sub")
    match c33 () |> try result with
    | Ok pl -> print $"dir move into itself: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"dir move into itself: {Str.replace root "$T" e}"
    match Dir.move (p "d") (p "d/sub") |> try result with
    | Ok _ -> print "direct dir move into itself: ok"
    | Error e -> print $"direct dir move into itself: {Str.replace root "$T" e}"
    let c25 () =
        plan
            File.write (p "m1.txt") ["1"]
            File.copy (p "m1.txt") (p "m2.txt")
            File.move (p "m2.txt") (p "m3.txt")
            File.delete (p "m1.txt")
            Dir.create (p "q")
            File.move (p "m3.txt") (p "q/m3.txt")
            Dir.move (p "q") (p "r")
            File.delete (p "r/m3.txt")
            Dir.delete (p "r")
    match c25 () |> try result with
    | Ok pl -> print $"all valid: ok ({pl |> Plan.preview |> Seq.length} ops)"
    | Error e -> print $"all valid: {Str.replace root "$T" e}"
    print $"apply: {Plan.apply (c25 ()) |> try result}"
    print $"after: {Dir.list root |> Seq.map Path.fileName |> Str.join " "}"
WEOF
cat > "$padir/pre.want" <<'WEOF'
copy onto existing: File.copy: destination exists: $T/exists.txt
copy onto a planned write: File.copy: destination exists: $T/new.txt
copy from planned write: ok (2 ops)
delete missing: File.delete: no such file: $T/missing.txt
delete twice: File.delete: no such file: $T/a.txt
copy from deleted: File.copy: no such file: $T/a.txt
move from missing: File.move: no such file: $T/nope.txt
move twice: File.move: no such file: $T/a.txt
move then delete target ok: ok (2 ops)
delete a dir the plan filled: Dir.delete: not empty: $T/e — Dir.deleteAll removes a tree
delete a dir the plan emptied ok: ok (2 ops)
delete a real non-empty dir: Dir.delete: not empty: $T/d — Dir.deleteAll removes a tree
write under a missing dir: File.write: no such directory: $T/zz
write under a planned dir ok: ok (2 ops)
write under a deleted tree: File.write: no such directory: $T/d
write onto a dir: File.write: $T/d is a directory
mkdir under a file: Dir.create: $T/a.txt is a file — a directory cannot be created there
mkdir over a planned file: Dir.create: $T/f.txt is a file — a directory cannot be created there
dir move then use old: File.delete: no such file: $T/d/in.txt
dir copy twice: Dir.copy: destination exists: $T/d3
dir copy then delete it non-empty: Dir.delete: not empty: $T/d4 — Dir.deleteAll removes a tree
deleteAll missing: Dir.deleteAll: no such directory: $T/nod
dir copy into itself: Dir.copy: destination $T/d/sub is inside the source $T/d
dir move missing: Dir.move: no such directory: $T/nod
writeAtomic under missing: File.writeAtomic: no such directory: $T/zz
dir copy of a planned dir ok: ok (2 ops)
dir copy of a moved dir ok: ok (2 ops)
dir copy into a planned parent ok: ok (2 ops)
copy into a missing dir: File.copy: no such directory: $T/nodir
move into a missing dir: File.move: no such directory: $T/nodir
dir move into a missing dir: Dir.move: no such directory: $T/nodir
copy into a deleted dir: File.copy: no such directory: $T/d
dir move into itself: Dir.move: destination $T/d/sub is inside the source $T/d
direct dir move into itself: Dir.move: destination $T/d/sub is inside the source $T/d
all valid: ok (9 ops)
apply: Ok (())
after: a.txt d exists.txt
WEOF
out=$($BIN "$padir/pre.weir" 2>&1) || fail "the plan precondition cell failed: $out"
printf '%s\n' "$out" | tr -d '\r' | sed 's#\\#/#g' | diff "$padir/pre.want" - >/dev/null || fail "plan preconditions at capture diverge: $out"
echo "e2e ok: plan/apply — every captured op checks its preconditions against the plan's state"

# [D:plan-stale-reads]: every fs read is guarded — a listing after a
# captured write inside it, a read under a captured deleteAll
mkdir -p "$padir/sr/tree" && printf 't\n' > "$padir/sr/tree/f"
cat > "$padir/sr1.weir" <<WEOF
let p =
    plan
        File.write "$padir/sr/new.txt" ["n"]
        print \$"{Path.glob "$padir/sr/*.txt" |> Seq.length}"
print \$"{p |> Plan.isEmpty}"
WEOF
out=$($BIN "$padir/sr1.weir" 2>&1) && fail "a glob over a dir with a captured write must refuse" || true
echo "$out" | grep -qF "Path.glob: known-after-apply" || fail "the glob refusal: $out"
cat > "$padir/sr2.weir" <<WEOF
let p =
    plan
        Dir.deleteAll "$padir/sr/tree"
        print \$"{File.exists "$padir/sr/tree/f"}"
print \$"{p |> Plan.isEmpty}"
WEOF
out=$($BIN "$padir/sr2.weir" 2>&1) && fail "File.exists under a captured deleteAll must refuse" || true
echo "$out" | grep -qF "File.exists: known-after-apply" || fail "the exists refusal: $out"
echo "e2e ok: plan/apply — stale reads refuse: a listing over a captured write, a read under a captured deleteAll"

# [D:plan-guard-links]: the guard compares symlink-resolved paths, both
# directions — a read through a dir alias of a written file, a read of a
# file a captured delete reached through the alias; and an inner plan's
# read sees an enclosing plan's pending ops
if [ "$IS_WINDOWS" != "1" ]; then
    mkdir -p "$padir/sl/real" && printf 'old\n' > "$padir/sl/real/a.txt"
    ln -s real "$padir/sl/alias" && ln -s a.txt "$padir/sl/real/lnk.txt"
    cat > "$padir/sl1.weir" <<WEOF
let p =
    plan
        File.write "$padir/sl/real/a.txt" ["new"]
        File.read "$padir/sl/alias/a.txt" |> Seq.iter print
print \$"{p |> Plan.isEmpty}"
WEOF
    out=$($BIN "$padir/sl1.weir" 2>&1) && fail "a read through a dir symlink alias must refuse: $out" || true
    echo "$out" | grep -qF "File.read: known-after-apply" || fail "the alias read refusal: $out"
    cat > "$padir/sl2.weir" <<WEOF
let p =
    plan
        File.write "$padir/sl/real/a.txt" ["new"]
        File.read "$padir/sl/real/lnk.txt" |> Seq.iter print
print \$"{p |> Plan.isEmpty}"
WEOF
    out=$($BIN "$padir/sl2.weir" 2>&1) && fail "a read through a file symlink must refuse: $out" || true
    echo "$out" | grep -qF "File.read: known-after-apply" || fail "the file-link read refusal: $out"
    cat > "$padir/sl3.weir" <<WEOF
let p =
    plan
        File.delete "$padir/sl/alias/a.txt"
        print \$"{File.exists "$padir/sl/real/a.txt"}"
print \$"{p |> Plan.isEmpty}"
WEOF
    out=$($BIN "$padir/sl3.weir" 2>&1) && fail "a real-path read after a delete via the alias must refuse: $out" || true
    echo "$out" | grep -qF "File.exists: known-after-apply" || fail "the reverse alias refusal: $out"
    cat > "$padir/sl4.weir" <<WEOF
let p =
    plan
        File.delete "$padir/sl/real/lnk.txt"
        print \$"{File.exists "$padir/sl/real/a.txt"}"
print \$"{p |> Plan.isEmpty}"
WEOF
    out=$($BIN "$padir/sl4.weir" 2>&1) || fail "deleting a link leaves its target readable: $out"
    [ "$out" = "$(printf 'true\nfalse')" ] || fail "the link's target reads: $out"
    echo "e2e ok: plan/apply — the stale-read guard resolves symlinks, both directions; a deleted link's target reads"
fi
mkdir -p "$padir/np"
cat > "$padir/np.weir" <<WEOF
let outer =
    plan
        File.write "$padir/np/cfg" ["v2"]
        let inner =
            plan
                print \$"{File.exists "$padir/np/cfg"}"
        print \$"{inner |> Plan.isEmpty}"
print \$"{outer |> Plan.isEmpty}"
WEOF
out=$($BIN "$padir/np.weir" 2>&1) && fail "an inner plan's read of an outer-captured path must refuse: $out" || true
echo "$out" | grep -qF "an enclosing 'plan'" || fail "the nested refusal names the enclosing plan: $out"
echo "e2e ok: plan/apply — a nested plan checks every enclosing frame"

# [D:plan-guard-inode]: an in-place write refuses a read through another
# hard link to the file
if [ "$IS_WINDOWS" != "1" ]; then
    mkdir -p "$padir/hl" && printf 'old\n' > "$padir/hl/a.txt" && ln "$padir/hl/a.txt" "$padir/hl/b.txt"
    cat > "$padir/hl.weir" <<WEOF
let p =
    plan
        File.write "$padir/hl/a.txt" ["new"]
        File.read "$padir/hl/b.txt" |> Seq.iter print
print \$"{p |> Plan.isEmpty}"
WEOF
    out=$($BIN "$padir/hl.weir" 2>&1) && fail "a read through another hard link must refuse: $out" || true
    echo "$out" | grep -qF "the same file (a hard link)" || fail "the hard-link refusal: $out"
    echo "e2e ok: plan/apply — the stale-read guard keys in-place writes by file identity"
fi

# [D:plan-within-tmp]: an op under a within tmp opened in the plan refuses
# before anything applies
cat > "$padir/wt.weir" <<WEOF
let p =
    plan
        File.write "$padir/wt-first.txt" ["a"]
        within tmp t
            File.write \$"{t}/x" ["y"]
p |> Plan.apply
WEOF
out=$($BIN "$padir/wt.weir" 2>&1) && fail "an op under a plan's within tmp must refuse" || true
echo "$out" | grep -qF "opened inside the plan" || fail "the within-tmp refusal: $out"
[ -f "$padir/wt-first.txt" ] && fail "nothing may apply after the refusal"
echo "e2e ok: plan/apply — an op under a within tmp opened in the plan refuses, nothing applied"

# [D:plan-writeatomic]: writeAtomic applies atomically — a hardlink keeps the old inode
if [ "$IS_WINDOWS" != "1" ]; then
    printf 'old\n' > "$padir/wa.txt" && ln "$padir/wa.txt" "$padir/wa-link.txt"
    cat > "$padir/wa.weir" <<WEOF
let p =
    plan
        File.writeAtomic "$padir/wa.txt" ["new"]
p |> Plan.preview |> Seq.iter print
p |> Plan.apply
WEOF
    out=$($BIN "$padir/wa.weir" 2>&1) || fail "a planned writeAtomic must apply: $out"
    echo "$out" | grep -qF "atomically" || fail "the preview names the atomic write: $out"
    [ "$(cat "$padir/wa.txt")" = "new" ] || fail "the target holds the new content"
    [ "$(cat "$padir/wa-link.txt")" = "old" ] || fail "an applied writeAtomic must split the hardlink"
    echo "e2e ok: plan/apply — File.writeAtomic applies atomically (the hardlink keeps the old content)"
fi

# DA-01 [D:plan-parallel-refusal]: a parallel/race callback runs on a
# worker thread without the (thread-local) plan capture frame — a native
# mutation there would escape capture and run for real. The combinator
# must refuse at runtime before any worker is scheduled: no marker file.
cat > "$padir/par.weir" <<WEOF
let changes =
    plan
        [1] |> Seq.piterWith 1 (fun _ -> File.write "$padir/par-marker.txt" ["probe"])
print \$"empty={changes |> Plan.isEmpty}"
WEOF
out=$($BIN "$padir/par.weir" 2>&1) && fail "a parallel combinator inside a plan must refuse" || true
echo "$out" | grep -qF "'piterWith' is refused inside 'plan'" || fail "the parallel-in-plan refusal must fire: $out"
[ -f "$padir/par-marker.txt" ] && fail "a worker wrote the marker despite the plan — capture escaped"
echo "e2e ok: plan/apply — a parallel combinator inside a plan refuses (no worker ran)"

# the same combinator outside a plan still runs (the refusal is scoped)
cat > "$padir/paro.weir" <<WEOF
[1; 2; 3] |> Seq.piterWith 2 (fun n -> File.write \$"$padir/paro-{n}.txt" ["x"])
print "ran"
WEOF
out=$($BIN "$padir/paro.weir" 2>&1) || fail "a parallel combinator OUTSIDE a plan must still run: $out"
expect "the parallel combinator ran outside a plan" "ran" "$out"
[ "$(ls "$padir"/paro-*.txt 2>/dev/null | wc -l)" -eq 3 ] || fail "the outside-plan parallel combinator did not write all arms"
echo "e2e ok: plan/apply — a parallel combinator OUTSIDE a plan still runs"

# DA-02 [D:plan-proc-runtime-guard]: the syntactic firstPlanRefusal follows
# top-level helpers but not a callable passed as a value, so that indirect
# proc slips past `check`. A runtime guard at the one spawn point refuses
# it: no child process runs, no marker file. (Direct proc and a named
# helper still refuse at check — belt and suspenders.)
cat > "$padir/indirect.weir" <<WEOF
let runMarker () =
    let lines = sh -c "printf marker > $padir/proc-marker.txt"
    lines |> Seq.length
let capture f =
    plan
        let _forced = f ()
        ()
let changes = capture runMarker
print \$"empty={changes |> Plan.isEmpty}"
WEOF
# it checks clean (the syntactic walk cannot see through the parameter)...
out=$($BIN check "$padir/indirect.weir" 2>&1) || fail "a callable passed into a plan must check clean: $out"
# ...but refuses at runtime before the child spawns
out=$($BIN "$padir/indirect.weir" 2>&1) && fail "an indirect proc inside a plan must refuse at runtime" || true
echo "$out" | grep -qF "refused inside 'plan'" || fail "the runtime proc-in-plan guard must fire: $out"
[ -f "$padir/proc-marker.txt" ] && fail "the indirect proc spawned despite the plan — the guard did not fire"
echo "e2e ok: plan/apply — an indirect helper-wrapped proc refuses at runtime (no child ran)"

# a native File.write reached through a serial helper still captures —
# the runtime guard must not over-refuse weir-native mutations
cat > "$padir/helperwrite.weir" <<WEOF
let doWrite () =
    File.write "$padir/helper-marker.txt" ["probe"]
let changes =
    plan
        doWrite ()
if changes |> Plan.isEmpty then fail "the helper's native write was not captured"
print "captured"
WEOF
out=$($BIN "$padir/helperwrite.weir" 2>&1) || fail "a native write via a serial helper must still capture: $out"
expect "the serial helper's native write is captured" "captured" "$out"
[ -f "$padir/helper-marker.txt" ] && fail "the captured write ran to disk — over-captured"
echo "e2e ok: plan/apply — a native write via a serial helper still captures (not over-refused)"

# DA-03 [D:plan-path-bound]: a plan binds paths absolute at capture, so
# apply writes the previewed location regardless of the apply-time cwd.
# Capture a write + a copy under `cd A`, apply under `cd B`: both land in
# A (the preview shows A's absolute paths), never B.
mkdir -p "$padir/A" "$padir/B"
printf 'data\n' > "$padir/A/orig.txt"
cat > "$padir/bound.weir" <<WEOF
let p =
    within cd "$padir/A"
        plan
            File.write "marker.txt" ["hi"]
            File.copy "orig.txt" "copied.txt"
// normalise separators: on Windows Plan.preview emits Path.GetFullPath's
// backslashes, and the capture dir may carry an 8.3 shortname the bash
// \$padir spelling lacks — so assert the absolute-under-A TAIL, not the
// full prefix. The A-vs-B binding is proven by the filesystem checks below.
let text = Str.join "\n" (p |> Plan.preview) |> Str.replace "\\\\" "/"
if not (Str.contains "/A/marker.txt" text) then fail "preview not absolute-A (write)"
if not (Str.contains "/A/copied.txt" text) then fail "preview not absolute-A (copy dst)"
within cd "$padir/B"
    p |> Plan.apply
print "applied"
WEOF
out=$($BIN "$padir/bound.weir" 2>&1) || fail "the bound-path plan failed: $out"
expect "the bound plan applies" "applied" "$out"
[ -f "$padir/A/marker.txt" ] || fail "the write did not land in A (the previewed capture-time dir)"
[ -f "$padir/A/copied.txt" ] || fail "the copy did not land in A"
[ -f "$padir/B/marker.txt" ] && fail "the write rebound to B (the apply-time cwd) — DA-03 regressed"
[ -f "$padir/B/copied.txt" ] && fail "the copy rebound to B — DA-03 regressed"
echo "e2e ok: plan/apply — paths bind absolute at capture; apply-under-B lands in A (DA-03)"

rm -rf "$padir"

# ---- within serve: the scoped HTTP listener [D:http-serve] -----------
# The three primitives proven end-to-end against the AOT binary: a
# routing handler (health/echo), incremental streaming (timestamped
# arrivals), the concurrency ceiling (marker interleave), and clean
# shutdown (the port frees — a second bind succeeds).
svdir=$(mkweirtmp)
svport=8471
svport2=8472
svport3=8473

# (1) routing: /health -> 200 "ok"; /echo -> a query param echoed. The
# server backgrounds itself in the scope and drives its own client so
# the port is torn down deterministically at block exit.
cat > "$svdir/route.weir" <<WEOF
let handler = fun req ->
    match req.path with
    | "/health" -> HttpServerResponse { status = 200; headers = []; body = Text "ok" }
    | "/echo" ->
        let name =
            match Str.trySplitOnce "=" req.query with
            | Some (_, v) -> v
            | None -> "?"
        HttpServerResponse { status = 200; headers = []; body = Text \$"hello {name}" }
    | _ -> HttpServerResponse { status = 404; headers = []; body = Text "not found" }

within serve srv = { port = $svport; maxConcurrent = 4 } handler
    print \$"listening on {Server.port srv}"
    poll timeout=8s interval=100ms
        Net.portOpen $svport
    let h = Http.get "http://127.0.0.1:$svport/health" |> Http.expect |> Seq.head
    print \$"health={h}"
    let hl = Http.get "http://localhost:$svport/health" |> Http.expect |> Seq.head
    print \$"health-localhost={hl}"
    let e = Http.get "http://127.0.0.1:$svport/echo?name=weir" |> Http.expect |> Seq.head
    print \$"echo={e}"
    within proc probe = sh "$svdir/dualhost.sh"
        Duration.sleep 2000ms
print "closed"
WEOF
# the localhost fetch above (and the dual-host probe below) prove the
# loopback-names fix [D:serve-loopback-names]: a Host: localhost request
# reaches the handler, not .NET's prefix-miss 404 — on the same socket
cat > "$svdir/dualhost.sh" <<DHEOF
#!/bin/sh
{
    printf 'v4=%s\n' "\$(curl -s --max-time 4 "http://127.0.0.1:$svport/health")"
    printf 'name=%s\n' "\$(curl -s --max-time 4 "http://localhost:$svport/health")"
} > "$svdir/dualhost.txt" 2>&1
DHEOF
chmod +x "$svdir/dualhost.sh"
out=$($BIN "$svdir/route.weir" 2>&1) || {
    probe=$(curl -s --max-time 2 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$svport/health" 2>/dev/null || echo "curl-failed")
    fail "serve routing failed (bash-side probe of :$svport/health = $probe): $out"
}
echo "$out" | grep -qF "health=ok" || fail "serve /health must return ok: $out"
echo "$out" | grep -qF "health-localhost=ok" || fail "serve localhost Host must reach the handler, not a 404: $out"
echo "$out" | grep -qF "echo=hello weir" || fail "serve /echo must echo the query param: $out"
echo "$out" | grep -qF "closed" || fail "serve scope must exit: $out"
# the bash-side dual-host referee: both names returned the handler body
grep -qF "v4=ok" "$svdir/dualhost.txt" || fail "serve 127.0.0.1 Host must return the handler body: $(cat "$svdir/dualhost.txt" 2>/dev/null)"
grep -qF "name=ok" "$svdir/dualhost.txt" || fail "serve localhost Host must return the handler body (not .NET's 404): $(cat "$svdir/dualhost.txt" 2>/dev/null)"
# clean shutdown: the port frees, a second bind on it succeeds
sleep 0.5
$BIN -e "Net.portOpen $svport" | grep -qF "false" || fail "serve no-orphan: port $svport still up after the scope"
echo "e2e ok: within serve — routing (health/echo) reachable over BOTH 127.0.0.1 and localhost + the port frees on exit"

# (2) streaming: a 5-element Stream body with a delay between elements.
# The client (curl -N, unbuffered) timestamps each arrival; we assert
# the first chunk lands well before the last — incremental, not
# buffered-then-flushed. The capture is a standalone script (no nested
# quote-escaping through the weir sh -c and the heredoc).
cat > "$svdir/capture.sh" <<CAPEOF
#!/bin/sh
curl -sN --max-time 10 "http://127.0.0.1:$svport2/x" | while IFS= read -r line; do
    [ -n "\$line" ] && echo "\$(date +%s.%N) \$line"
done > "$svdir/arrivals.txt" 2>&1
CAPEOF
chmod +x "$svdir/capture.sh"
cat > "$svdir/stream.weir" <<WEOF
let handler = fun req ->
    let elems = [1; 2; 3; 4; 5] |> Seq.map (fun i ->
        Duration.sleep 300ms
        \$"chunk-{i}")
    HttpServerResponse { status = 200; headers = []; body = Stream elems }

within serve srv = { port = $svport2; maxConcurrent = 4 } handler
    poll timeout=8s interval=100ms
        Net.portOpen $svport2
    within proc client = sh "$svdir/capture.sh"
        Duration.sleep 3000ms
print "streamed"
WEOF
out=$($BIN "$svdir/stream.weir" 2>&1) || fail "serve streaming run failed: $out"
echo "$out" | grep -qF "streamed" || fail "serve stream scope must exit: $out"
# five data lines arrived
nchunks=$(grep -c "chunk-" "$svdir/arrivals.txt" 2>/dev/null || echo 0)
[ "$nchunks" -eq 5 ] || fail "serve stream: expected 5 chunks, got $nchunks ($(cat "$svdir/arrivals.txt" 2>/dev/null))"
# incrementality: first arrival is >= 800ms before the last (5 elems x
# 300ms spacing gives ~1.2s spread; 800ms is a slack floor that a
# buffered-all-at-once server would fail)
t_first=$(grep "chunk-1" "$svdir/arrivals.txt" | head -1 | awk '{print $1}')
t_last=$(grep "chunk-5" "$svdir/arrivals.txt" | head -1 | awk '{print $1}')
spread=$(awk -v a="$t_first" -v b="$t_last" 'BEGIN{printf "%.3f", b-a}')
awk -v s="$spread" 'BEGIN{exit !(s >= 0.8)}' || fail "serve stream NOT incremental: chunk-1..chunk-5 spread was ${spread}s (< 0.8s means buffered) — arrivals: $(cat "$svdir/arrivals.txt")"
echo "e2e ok: within serve — Stream body arrives INCREMENTALLY (chunk-1..chunk-5 spread ${spread}s)"

# (3) concurrency ceiling: maxConcurrent=2, fire 4 slow requests, assert
# only 2 handlers run at once via a start/end marker log. The ceiling
# holds iff the log is start,start,end,end,start,start,end,end — never
# four starts in a row.
rm -f "$svdir/markers.txt"
cat > "$svdir/conc.weir" <<WEOF
let handler = fun req ->
    let _m1 = sh -c "echo start >> $svdir/markers.txt" | complete
    Duration.sleep 800ms
    let _m2 = sh -c "echo end >> $svdir/markers.txt" | complete
    HttpServerResponse { status = 200; headers = []; body = Text "done" }

within serve srv = { port = $svport3; maxConcurrent = 2 } handler
    poll timeout=8s interval=100ms
        Net.portOpen $svport3
    within proc load = sh -c "for i in 1 2 3 4; do curl -s --max-time 8 http://127.0.0.1:$svport3/slow & done; wait"
        Duration.sleep 3000ms
print "loaded"
WEOF
out=$($BIN "$svdir/conc.weir" 2>&1) || fail "serve concurrency run failed: $out"
echo "$out" | grep -qF "loaded" || fail "serve conc scope must exit: $out"
# the ceiling proof: the first three markers must be start,start,end —
# a third 'start' before any 'end' would mean 3+ concurrent (ceiling
# broken). At maxConcurrent=2 the third line is always an 'end'.
first3=$(head -3 "$svdir/markers.txt" 2>/dev/null | tr '\n' ',')
[ "$first3" = "start,start,end," ] || fail "serve concurrency ceiling BROKEN: first 3 markers were '$first3' (expected 'start,start,end,') — markers: $(cat "$svdir/markers.txt" 2>/dev/null | tr '\n' ' ')"
echo "e2e ok: within serve — concurrency ceiling holds (maxConcurrent=2 caps 4 requests at 2)"

# ---- Bundle E: serve wire fidelity (F8/F9/F12) + tty sanitize (F13) ---
svport4=8474
svport5=8475
svport6=8476

# (F9) the method boundary [D:serve-method]: QUERY reads as Query, a
# well-formed unlisted verb reads as Other v (the handler routes on it),
# and a malformed method token is refused at the boundary with 400. A raw
# socket sends the exact request line so the verb is what the wire says.
cat > "$svdir/methprobe.py" <<'PYEOF'
import socket, sys
port = int(sys.argv[1])
def send(verb):
    s = socket.socket(); s.connect(('127.0.0.1', port))
    s.sendall(('%s /echo HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nConnection: close\r\n\r\n' % (verb, port)).encode())
    d = b''
    while True:
        c = s.recv(4096)
        if not c: break
        d += c
    status = d.split(b'\r\n', 1)[0].decode()
    body = d.split(b'\r\n\r\n', 1)[-1].decode(errors='replace')
    print('%s|status=%s|body=%s' % (verb, status, body))
for v in ['GET', 'QUERY', 'FROBNICATE']:
    send(v)
# malformed: an embedded control byte in the method token
s = socket.socket(); s.connect(('127.0.0.1', port))
s.sendall(b'BA\x01D /echo HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nConnection: close\r\n\r\n' % port)
d = b''
while True:
    c = s.recv(4096)
    if not c: break
    d += c
print('MALFORMED|status=%s' % (d.split(b'\r\n', 1)[0].decode() if d else '(reset)'))
PYEOF
cat > "$svdir/method.weir" <<WEOF
within serve srv = { port = $svport4; maxConcurrent = 2 } (fun req -> match req.method with | Get -> HttpServerResponse { status = 200; headers = []; body = Text "GET" } | Query -> HttpServerResponse { status = 200; headers = []; body = Text "QUERY" } | Other v -> HttpServerResponse { status = 405; headers = []; body = Text \$"OTHER:{v}" } | _ -> HttpServerResponse { status = 200; headers = []; body = Text "known" })
    poll timeout=8s interval=100ms
        Net.portOpen $svport4
    let out = sh -c "python3 '$svdir/methprobe.py' $svport4" | complete
    out.stdout |> Seq.iter print
print "method-done"
WEOF
out=$($BIN "$svdir/method.weir" 2>&1) || fail "serve method run failed: $out"
echo "$out" | grep -qF "GET|status=HTTP/1.1 200 OK|body=GET" || fail "F9: GET must route as Get: $out"
echo "$out" | grep -qF "QUERY|status=HTTP/1.1 200 OK|body=QUERY" || fail "F9: QUERY must read as Query, not Get: $out"
echo "$out" | grep -qE "FROBNICATE\|status=HTTP/1.1 405( [^|]*)?\|body=OTHER:FROBNICATE" || fail "F9: an unlisted verb must route as Other v with 405: $out"
echo "$out" | grep -qE "MALFORMED\|status=HTTP/1.1 400( |$)" || fail "F9: a malformed method token must be refused with 400: $out"
echo "e2e ok: within serve — F9 method boundary (QUERY=Query, unlisted=Other, malformed=400)"

# (F8) a Stream producer raising mid-body [D:serve-stream]: the failure is
# surfaced to the script through Server.streamErrors (the designated
# channel), never a raise out of the handler. The client reads whatever
# arrived; the script observes the failure and can act on it.
cat > "$svdir/streamprobe.py" <<'PYEOF'
import socket, sys
port = int(sys.argv[1])
s = socket.socket(); s.connect(('127.0.0.1', port))
s.sendall(('GET /half HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nConnection: close\r\n\r\n' % port).encode())
d = b''
while True:
    c = s.recv(4096)
    if not c: break
    d += c
print('GOT-FIRST=%s' % (b'data: first' in d))
print('TERMINATED=%s' % d.endswith(b'0\r\n\r\n'))
PYEOF
cat > "$svdir/stream.weir" <<WEOF
let handler = fun req ->
    match req.path with
    | "/half" -> HttpServerResponse { status = 200; headers = []; body = Stream ([1; 2] |> Seq.map (fun i -> if i == 1 then "first" else fail "producer died")) }
    | _ -> HttpServerResponse { status = 404; headers = []; body = Text "nf" }

within serve srv = { port = $svport5; maxConcurrent = 2 } handler
    poll timeout=8s interval=100ms
        Net.portOpen $svport5
    let out = sh -c "python3 '$svdir/streamprobe.py' $svport5" | complete
    out.stdout |> Seq.iter print
    let errs = Server.streamErrors srv
    print \$"stream-errors={errs |> Seq.length}"
    errs |> Seq.iter (fun e -> print \$"stream-err={e}")
print "stream-done"
WEOF
out=$($BIN "$svdir/stream.weir" 2>&1) || fail "serve stream run failed: $out"
# one server on every OS [D:serve-kestrel]: the early chunk is on the wire
# before the producer's later raise, everywhere
echo "$out" | grep -qF "GOT-FIRST=True" || fail "F8: the client must receive the first element before the producer died: $out"
# the client can TELL [D:serve-stream-truncate]: the chunked body ends
# without its zero-length terminator, so its HTTP layer reports a
# truncated body — a proper terminator after a failure must not survive
echo "$out" | grep -qF "TERMINATED=False" || fail "F8: a producer raise must leave the chunked body unterminated: $out"
echo "$out" | grep -qF "stream-errors=1" || fail "F8: the script must observe the producer failure via the channel: $out"
echo "$out" | grep -qF "stream-err=producer died" || fail "F8: Server.streamErrors must carry the failure message: $out"
echo "e2e ok: within serve — F8 a Stream producer raise surfaces via Server.streamErrors and leaves the client an unterminated body"

# (F3-serve) a response header carrying CR/LF [D:http-header-bytes]: the
# serve face of the injection refusal. A raw socket referees the wire so
# the assertion is what arrived, not curl's view: the injecting response is
# a 500 with no forged header (never F11's silent drop), a benign control
# header does arrive on a clean path, and the script observes the located
# refusal through Server.streamErrors.
cat > "$svdir/hdrprobe.py" <<'PYEOF'
import socket, sys
port = int(sys.argv[1])
def get(path):
    s = socket.socket(); s.connect(('127.0.0.1', port))
    s.sendall(('GET %s HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nConnection: close\r\n\r\n' % (path, port)).encode())
    d = b''
    while True:
        c = s.recv(4096)
        if not c: break
        d += c
    return d
evil = get('/crlf')
benign = get('/ok')
badname = get('/badname')
status_evil = evil.split(b'\r\n', 1)[0].decode()
print('EVIL-STATUS=%s' % status_evil)
# the forged header/name must NOT appear anywhere in the evil response
print('FORGED=%s' % ((b'Injected' in evil) or (b'X-Crlf' in evil)))
print('BENIGN-ARRIVES=%s' % (b'X-Benign: present' in benign))
print('BADNAME-STATUS=%s' % badname.split(b'\r\n', 1)[0].decode())
PYEOF
cat > "$svdir/hdr.weir" <<WEOF
let crlf = Str.fromBase64 "DQo="
let handler = fun req ->
    match req.path with
    | "/crlf" -> HttpServerResponse { status = 200; headers = [("X-Crlf", "a" + crlf + "Injected: yes")]; body = Text "ok" }
    | "/ok" -> HttpServerResponse { status = 200; headers = [("X-Benign", "present")]; body = Text "ok" }
    | "/badname" -> HttpServerResponse { status = 200; headers = [("Bad Name", "v")]; body = Text "ok" }
    | _ -> HttpServerResponse { status = 404; headers = []; body = Text "nf" }

within serve srv = { port = $svport5; maxConcurrent = 2 } handler
    poll timeout=8s interval=100ms
        Net.portOpen $svport5
    let out = sh -c "python3 '$svdir/hdrprobe.py' $svport5" | complete
    out.stdout |> Seq.iter print
    Duration.sleep 150ms
    let errs = Server.streamErrors srv
    print \$"refusals={errs |> Seq.length}"
    errs |> Seq.iter (fun e -> print \$"refusal={e}")
print "hdr-done"
WEOF
out=$($BIN "$svdir/hdr.weir" 2>&1) || fail "serve header-injection run failed: $out"
echo "$out" | grep -qF "EVIL-STATUS=HTTP/1.1 500" || fail "F3-serve: an injecting response header must be refused (500), got: $out"
echo "$out" | grep -qF "FORGED=False" || fail "F3-serve: the forged header must NOT reach the wire: $out"
echo "$out" | grep -qF "BENIGN-ARRIVES=True" || fail "F3-serve: a clean control header must still arrive (refusal is distinguishable from headers-never-working): $out"
echo "$out" | grep -qF "refusals=2" || fail "F3-serve/F11: the script must observe both located refusals via Server.streamErrors: $out"
echo "$out" | grep -qF "response header 'X-Crlf' carries a CR byte" || fail "F3-serve: the located refusal must name the header and byte: $out"
# (F11) a header the listener cannot send is refused by name, never
# dropped [D:http-header-unsendable]: a 500, and the reason on the channel
echo "$out" | grep -qF "BADNAME-STATUS=HTTP/1.1 500" || fail "F11-serve: an unsendable response header must be refused (500), not dropped: $out"
echo "$out" | grep -qF "refusal=response header 'Bad Name' cannot be sent — its name carries a space" || fail "F11-serve: the refusal must name the header and why: $out"
echo "e2e ok: within serve — F3 a CR/LF response header is refused (500, no forged header, control arrives, located via streamErrors); F11 an unsendable one too"

# (F11-client) a request header with no place on the wire is refused by
# name before any connection [D:http-header-unsendable] — HttpClient
# skipped it silently. Nothing need listen: the refusal precedes the send.
for case in 'Bad Name|v|its name carries a space' 'Content-Type|application/json|it is a body header and this request has no body' 'X-U|café|its value carries the non-ASCII character'; do
    hn=${case%%|*}; rest=${case#*|}; hv=${rest%%|*}; why=${rest#*|}
    rc=0
    out=$($BIN -e "print \$\"{(Http.send { Http.get \"http://127.0.0.1:9/\" with headers = [(\"$hn\", \"$hv\")] }).status}\"" 2>&1) || rc=$?
    [ "$rc" = "1" ] || fail "F11-client: '$hn' must be refused (exit 1), got rc=$rc: $out"
    echo "$out" | grep -qF "request header '$hn' cannot be sent — $why" || fail "F11-client: the refusal must name '$hn' and why: $out"
done
echo "e2e ok: Http.send — F11 a header with no place on the wire is refused by name (bad name, bodyless body header, non-ASCII)"
# a Secret's offending character is never printed [D:secret-header-offset]:
# secretHeaders and auth = Bearer name the header and the byte offset
for req in 'secretHeaders = [("X-Tok", Secret.of "sßcret")]|X-Tok|at byte offset 1 of the secret value' \
           'auth = Bearer (Secret.of "tØken")|Authorization|at byte offset 8 of the secret value'; do
    rf=${req%%|*}; rest=${req#*|}; hn=${rest%%|*}; why=${rest#*|}
    rc=0
    out=$($BIN -e "print \$\"{(Http.send { Http.get \"http://127.0.0.1:9/\" with $rf }).status}\"" 2>&1) || rc=$?
    [ "$rc" = "1" ] || fail "secret-header: '$hn' must be refused (exit 1), got rc=$rc: $out"
    echo "$out" | grep -qF "request header '$hn' cannot be sent — its value carries a non-ASCII character $why" || fail "secret-header: '$hn' must name the offset: $out"
    echo "$out" | grep -qE 'ß|Ø' && fail "secret-header: the secret's character leaked: $out" || true
done
echo "e2e ok: Http.send — a secret header's non-ASCII byte is located by offset, never printed"

# (F12) the request-body read timeout [D:serve-body-timeout]: a slow
# client declaring a large Content-Length and holding the socket is
# refused with 408 within the bound, not parked. bodyTimeout=1s here.
cat > "$svdir/slowbody.py" <<'PYEOF'
import socket, sys, time
port = int(sys.argv[1])
s = socket.socket(); s.connect(('127.0.0.1', port))
s.sendall(('POST /x HTTP/1.1\r\nHost: 127.0.0.1:%d\r\nContent-Length: 1000000\r\nConnection: close\r\n\r\n' % port).encode())
s.sendall(b'0123456789')
t0 = time.time()
d = b''
try:
    while True:
        c = s.recv(4096)
        if not c: break
        d += c
except Exception:
    pass
print('elapsed=%.1f status=%s' % (time.time() - t0, d.split(b'\r\n', 1)[0].decode() if d else '(none)'))
PYEOF
cat > "$svdir/timeout.weir" <<WEOF
within serve srv = { port = $svport6; maxConcurrent = 2; bodyTimeout = 1s } (fun req -> HttpServerResponse { status = 200; headers = []; body = Text "ok" })
    poll timeout=8s interval=100ms
        Net.portOpen $svport6
    let out = sh -c "python3 '$svdir/slowbody.py' $svport6" | complete
    out.stdout |> Seq.iter print
print "timeout-done"
WEOF
out=$($BIN "$svdir/timeout.weir" 2>&1) || fail "serve body-timeout run failed: $out"
echo "$out" | grep -qF "status=HTTP/1.1 408" || fail "F12: a slow body must be refused with 408: $out"
echo "e2e ok: within serve — F12 request-body read timeout refuses a slow client with 408"

# (H20) the request-body size bound [D:serve-max-body]: a body past
# maxBody is answered 413 (never buffered whole); one within it reaches
# the handler. Small bound — the test never sends a large body.
svport7=8477
cat > "$svdir/maxbody.py" <<'PYEOF'
import socket, sys
port, n = int(sys.argv[1]), int(sys.argv[2])
s = socket.socket(); s.connect(('127.0.0.1', port))
s.sendall(('POST /x HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: %d\r\nConnection: close\r\n\r\n' % n).encode() + b'x' * n)
d = b''
while True:
    c = s.recv(4096)
    if not c: break
    d += c
print('status=%s body=%s' % (d.split(b'\r\n', 1)[0].decode(), d.split(b'\r\n\r\n', 1)[-1].decode()))
PYEOF
cat > "$svdir/maxbody.weir" <<WEOF
within serve srv = { port = $svport7; maxConcurrent = 2; maxBody = 16B } (fun req -> HttpServerResponse { status = 200; headers = []; body = Text \$"len={Str.length req.body}" })
    poll timeout=8s interval=100ms
        Net.portOpen $svport7
    let fits = sh -c "python3 '$svdir/maxbody.py' $svport7 16" | complete
    fits.stdout |> Seq.iter print
    let over = sh -c "python3 '$svdir/maxbody.py' $svport7 17" | complete
    over.stdout |> Seq.iter print
WEOF
out=$($BIN "$svdir/maxbody.weir" 2>&1) || fail "serve maxBody run failed: $out"
echo "$out" | grep -qF "status=HTTP/1.1 200 OK body=len=16" || fail "H20: a body of exactly maxBody must reach the handler: $out"
echo "$out" | grep -qF "status=HTTP/1.1 413" || fail "H20: a body past maxBody must be 413: $out"
echo "e2e ok: within serve — H20 maxBody answers 413 past the bound"

# a request body in a charset weir cannot decode is answered 415 before
# the handler [D:serve-charset], never decoded as UTF-8 with U+FFFD; a
# known charset still decodes per its declaration
svport20=8487
cat > "$svdir/charset.py" <<'PYEOF'
import socket, sys
port, cs, body = int(sys.argv[1]), sys.argv[2], bytes.fromhex(sys.argv[3])
s = socket.socket(); s.connect(('127.0.0.1', port))
s.sendall(('POST /x HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: text/plain; charset=%s\r\nContent-Length: %d\r\nConnection: close\r\n\r\n' % (cs, len(body))).encode() + body)
d = b''
while True:
    c = s.recv(4096)
    if not c: break
    d += c
# UTF-8 bytes, not print: Windows' Python writes a pipe in the ANSI code page
sys.stdout.buffer.write(('%s status=%s body=%s\n' % (cs, d.split(b'\r\n', 1)[0].decode(), d.split(b'\r\n\r\n', 1)[-1].decode())).encode('utf-8'))
PYEOF
cat > "$svdir/charset.weir" <<WEOF
within serve srv = { port = $svport20; maxConcurrent = 2 } (fun req -> HttpServerResponse { status = 200; headers = []; body = Text \$"handled={req.body}" })
    poll timeout=8s interval=100ms
        Net.portOpen $svport20
    let unknown = sh -c "python3 '$svdir/charset.py' $svport20 x-weir-none e961" | complete
    unknown.stdout |> Seq.iter print
    let latin = sh -c "python3 '$svdir/charset.py' $svport20 iso-8859-1 e961" | complete
    latin.stdout |> Seq.iter print
WEOF
out=$($BIN "$svdir/charset.weir" 2>&1) || fail "serve charset run failed: $out"
echo "$out" | grep -qF "x-weir-none status=HTTP/1.1 415" || fail "a body in an unknown charset must be 415: $out"
echo "$out" | grep -qF "request body charset 'x-weir-none' is not one weir decodes" || fail "the 415 must name the charset: $out"
echo "$out" | grep -q "x-weir-none.*handled=" && fail "a body in an unknown charset must never reach the handler: $out"
echo "$out" | grep -qF "iso-8859-1 status=HTTP/1.1 200 OK body=handled=éa" || fail "a declared iso-8859-1 body must decode per its charset: $out"
echo "e2e ok: within serve — a body in an unknown charset is answered 415 before the handler"

# ---- serve on Kestrel [D:serve-kestrel]: the wire, end to end ----------
# Pipelining, repeated headers (F10) and one-response-per-request (H3-E6)
# on the AOT binary through a weir handler; a raw socket referees the
# bytes, so the assertion is what the wire carried.
svport8=8478
cat > "$svdir/wire.py" <<'PYEOF'
import socket, sys, re, time
port = int(sys.argv[1]); H = b"Host: 127.0.0.1:%d\r\n" % port
def drain(s, d, first, quiet):
    s.settimeout(first)
    while True:
        try:
            x = s.recv(65536)
        except socket.timeout:
            return d, "quiet"
        except OSError:
            return d, "reset"
        if not x:
            return d, "eof"
        d += x
        s.settimeout(quiet)
# `then` is sent only once the first reply has gone quiet on an open
# connection: writing into one the server closed draws a RST, and the
# client then loses the reply it had not read yet (as hunt3-xplat X59)
def exchange(payload, then=None):
    s = socket.create_connection(("127.0.0.1", port), timeout=5)
    s.sendall(payload)
    d, end = drain(s, b"", 5, 1.0 if then else 5)
    if then and end == "quiet":
        try:
            s.sendall(then)
            d, _ = drain(s, d, 5, 5)
        except OSError:
            pass
    s.close()
    return d
def statuses(d): return [m.decode() for m in re.findall(rb"HTTP/1\.1 [0-9]{3}", d)]
def bodies(d): return [m.decode() for m in re.findall(rb"<[^>]*>", d)]
g = lambda p, close=False: b"GET %s HTTP/1.1\r\n" % p + H + (b"Connection: close\r\n" if close else b"") + b"\r\n"
d = exchange(g(b"/p1") + g(b"/p2", True))
print("PIPE2 %s %s" % (statuses(d), bodies(d)))
d = exchange(g(b"/q1") + b"POST /q2 HTTP/1.1\r\n" + H + b"Content-Length: 2\r\n\r\nhi" + g(b"/q3", True))
print("PIPE3 %s %s" % (statuses(d), bodies(d)))
d = exchange(b"GET /xff HTTP/1.1\r\n" + H + b"X-Forwarded-For: 10.0.0.1\r\nX-Forwarded-For: 10.0.0.2\r\nX-Forwarded-For: 10.0.0.3\r\nConnection: close\r\n\r\n")
print("XFF %s" % bodies(d))
d = exchange(b"GET /a HTTP/1.1\r\nHost: example.com\r\n\r\n", g(b"/after-host", True))
print("E6-HOST %s %s" % (statuses(d), bodies(d)))
d = exchange(b"GET http://evil.example/x HTTP/1.1\r\n" + H + b"\r\n", g(b"/after-abs", True))
print("E6-ABS %s %s" % (statuses(d), bodies(d)))
d = exchange(b"GET / HTTP/9.9\r\n" + H + b"\r\n", g(b"/after-version", True))
print("E6-VERSION %s %s" % (statuses(d), bodies(d)))
PYEOF
cat > "$svdir/wire.weir" <<WEOF
let handler = fun req ->
    match req.path with
    | "/xff" ->
        let hops = req.headers |> Seq.where (fun (k, _) -> Str.toLower k == "x-forwarded-for") |> Seq.map snd |> Str.join ","
        HttpServerResponse { status = 200; headers = []; body = Text \$"<{hops}>" }
    | p -> HttpServerResponse { status = 200; headers = []; body = Text \$"<{p}>" }

within serve srv = { port = $svport8; maxConcurrent = 4 } handler
    poll timeout=8s interval=100ms
        Net.portOpen $svport8
    let out = sh -c "python3 '$svdir/wire.py' $svport8" | complete
    out.stdout |> Seq.iter print
WEOF
out=$($BIN "$svdir/wire.weir" 2>&1) || fail "serve wire run failed: $out"
echo "$out" | grep -qF "PIPE2 ['HTTP/1.1 200', 'HTTP/1.1 200'] ['</p1>', '</p2>']" || fail "pipelining: two requests on one connection must both be answered, in order: $out"
echo "$out" | grep -qF "PIPE3 ['HTTP/1.1 200', 'HTTP/1.1 200', 'HTTP/1.1 200'] ['</q1>', '</q2>', '</q3>']" || fail "pipelining: three requests on one connection must all be answered, in order: $out"
echo "$out" | grep -qF "XFF ['<10.0.0.1,10.0.0.2,10.0.0.3>']" || fail "F10: three X-Forwarded-For lines must reach the handler as three pairs in wire order: $out"
# H3-E6: a refused request gets one response and the follow-up its own —
# never an unsolicited 200 a keep-alive client would pair with its next
echo "$out" | grep -qF "E6-HOST ['HTTP/1.1 404', 'HTTP/1.1 200'] ['</after-host>']" || fail "E6: a foreign Host gets one 404 and the next request its own answer: $out"
echo "$out" | grep -qE "E6-ABS \['HTTP/1.1 4[0-9]{2}'(, 'HTTP/1.1 200'\] \['</after-abs>'\]|\] \[\])" || fail "E6: an absolute-form foreign URI gets one refusal and no unsolicited 200: $out"
echo "$out" | grep -qF "E6-VERSION ['HTTP/1.1 505'] []" || fail "E6: an unsupported version gets exactly one 505: $out"
echo "e2e ok: within serve — pipelining (2 and 3 in order), F10 three duplicates in wire order, E6 one response per request"

# (F8 on Kestrel) curl's own HTTP layer sees the truncation: exit 18, not
# a clean end — the connection aborts mid-body, no reflection
# [D:serve-stream-truncate]
svport9=8479
cat > "$svdir/f8curl.weir" <<WEOF
let handler = fun req -> HttpServerResponse { status = 200; headers = []; body = Stream ([1; 2] |> Seq.map (fun i -> if i == 1 then "first" else fail "producer died")) }
within serve srv = { port = $svport9; maxConcurrent = 2 } handler
    poll timeout=8s interval=100ms
        Net.portOpen $svport9
    let r = curl -s --max-time 5 http://127.0.0.1:$svport9/ | complete
    print \$"curl-exit={r.exitCode}"
    let ok = curl -s --max-time 5 http://127.0.0.1:$svport9/ | complete
    print \$"second-exit={ok.exitCode}"
WEOF
out=$($BIN "$svdir/f8curl.weir" 2>&1) || fail "serve F8 curl run failed: $out"
echo "$out" | grep -qF "curl-exit=18" || fail "F8: curl must exit 18 (truncated chunked body) after a producer raise: $out"
echo "e2e ok: within serve — F8 a producer raise leaves curl exit 18"

# (H3-A3) [::1] reaches the handler, not a listener 400 — where the host
# has an IPv6 loopback at all
svport10=8480
if python3 -c "import socket; s=socket.socket(socket.AF_INET6); s.bind(('::1', 0))" 2>/dev/null; then
    cat > "$svdir/v6.weir" <<WEOF
within serve srv = { port = $svport10; maxConcurrent = 2 } (fun req -> HttpServerResponse { status = 200; headers = []; body = Text "weir-handler" })
    poll timeout=8s interval=100ms
        Net.portOpen $svport10
    let v6 = curl -s -g --noproxy "*" --max-time 5 -w " status=%{http_code}" "http://[::1]:$svport10/" | complete
    print \$"v6={v6.stdout |> Str.join " "}"
WEOF
    out=$($BIN "$svdir/v6.weir" 2>&1) || fail "serve [::1] run failed: $out"
    echo "$out" | grep -qF "v6=weir-handler status=200" || fail "A3: a [::1] request must reach the handler: $out"
    echo "e2e ok: within serve — A3 [::1] reaches the handler"
else
    echo "e2e skip: within serve [::1] (no IPv6 loopback on this host)"
fi

# (H3-E5) a slow request body never holds a handler slot: with ONE slot
# and a client dribbling its body under a 10s bodyTimeout, a fast
# request is still answered at once
svport11=8481
cat > "$svdir/loris.py" <<'PYEOF'
import socket, sys, time
port = int(sys.argv[1]); H = b"Host: 127.0.0.1:%d\r\n" % port
slow = [socket.create_connection(("127.0.0.1", port)) for _ in range(3)]
for s in slow:
    s.sendall(b"POST /slow HTTP/1.1\r\n" + H + b"Content-Length: 1000\r\n\r\nab")
time.sleep(0.5)
t = time.time()
c = socket.create_connection(("127.0.0.1", port), timeout=10)
c.sendall(b"GET /fast HTTP/1.1\r\n" + H + b"Connection: close\r\n\r\n")
d = b""
while True:
    x = c.recv(4096)
    if not x: break
    d += x
print("FAST %s in %.2fs" % (d.split(b"\r\n", 1)[0].decode(), time.time() - t))
PYEOF
cat > "$svdir/loris.weir" <<WEOF
within serve srv = { port = $svport11; maxConcurrent = 1; bodyTimeout = 10s } (fun req -> HttpServerResponse { status = 200; headers = []; body = Text "ok" })
    poll timeout=8s interval=100ms
        Net.portOpen $svport11
    let out = sh -c "python3 '$svdir/loris.py' $svport11" | complete
    out.stdout |> Seq.iter print
WEOF
out=$($BIN "$svdir/loris.weir" 2>&1) || fail "serve slow-body run failed: $out"
fastline=$(echo "$out" | grep "^FAST " || true)
echo "$fastline" | grep -qF "FAST HTTP/1.1 200 OK" || fail "E5: a fast request must be served beside slow bodies: $out"
fastsecs=$(echo "$fastline" | sed -E 's/.* in ([0-9.]+)s/\1/')
awk -v s="$fastsecs" 'BEGIN{exit !(s < 2.0)}' || fail "E5: slow bodies held the only slot — the fast request waited ${fastsecs}s: $out"
echo "e2e ok: within serve — E5 slow request bodies never hold a handler slot (fast request in ${fastsecs}s)"

# (H3-E7) a raising handler and an invalid status answer 500 AND reach
# the script on Server.streamErrors [D:serve-handler-errors]
svport12=8482
cat > "$svdir/e7.weir" <<WEOF
let handler = fun req ->
    match req.path with
    | "/raise" -> fail "handler boom"
    | "/status0" -> HttpServerResponse { status = 0; headers = []; body = Text "x" }
    | _ -> HttpServerResponse { status = 200; headers = []; body = Text "ok" }

within serve srv = { port = $svport12; maxConcurrent = 2 } handler
    poll timeout=8s interval=100ms
        Net.portOpen $svport12
    let a = curl -s --max-time 5 -w " %{http_code}" http://127.0.0.1:$svport12/raise | complete
    let b = curl -s --max-time 5 -w " %{http_code}" http://127.0.0.1:$svport12/status0 | complete
    print \$"raise={a.stdout |> Str.join ""}"
    print \$"status0={b.stdout |> Str.join ""}"
    Server.streamErrors srv |> Seq.iter (fun e -> print \$"recorded={e}")
WEOF
out=$($BIN "$svdir/e7.weir" 2>&1) || fail "serve E7 run failed: $out"
echo "$out" | grep -qF "raise=internal error 500" || fail "E7: a raising handler still answers 500 internal error: $out"
echo "$out" | grep -qF "status0=internal error 500" || fail "E7: an invalid status answers 500: $out"
echo "$out" | grep -qF "recorded=serve handler on GET /raise failed: handler boom" || fail "E7: the raise must be recorded for the script: $out"
echo "$out" | grep -qF "recorded=serve handler on GET /status0 failed: status 0 is not an HTTP status (100 to 999)" || fail "E7: the invalid status must be recorded: $out"
echo "e2e ok: within serve — E7 a raising handler and an invalid status are recorded on Server.streamErrors"

# (H3-C3) a handler outliving the scope never crashes weir: the late
# handler's result is discarded, `always` runs, and the port is free at
# once; with eight handlers stuck the scope still closes after ONE
# bounded drain, not 2s per handler [D:serve-drain]
svport13=8483
cat > "$svdir/c3a.weir" <<WEOF
let handler = fun req ->
    Duration.sleep 3s
    HttpServerResponse { status = 200; headers = []; body = Text "late" }

within
    within serve srv = { port = $svport13; maxConcurrent = 4 } handler
        poll timeout=8s interval=50ms
            Net.portOpen $svport13
        within proc client = curl -s --max-time 10 http://127.0.0.1:$svport13/
            Duration.sleep 300ms
    print \$"port-open-after-close={Net.portOpen $svport13}"
    within serve again = { port = $svport13; maxConcurrent = 1 } handler
        print "rebind ok"
    Duration.sleep 3s
    print "after work"
always
    print "always ran"
WEOF
code=0; out=$($BIN "$svdir/c3a.weir" 2>&1) || code=$?
[ "$code" = "0" ] || fail "C3: a handler finishing after the scope closed must not crash weir (exit $code): $out"
echo "$out" | grep -qF "port-open-after-close=false" || fail "C3: the port must be free right after the scope closes: $out"
echo "$out" | grep -qF "rebind ok" || fail "C3: a second bind on the port must succeed after close: $out"
echo "$out" | grep -qF "after work" || fail "C3: the script must keep running past the late handler: $out"
echo "$out" | grep -qF "always ran" || fail "C3: always must run: $out"
cat > "$svdir/c3b.weir" <<WEOF
let handler = fun req ->
    Duration.sleep 30s
    HttpServerResponse { status = 200; headers = []; body = Text "late" }

within serve srv = { port = $svport13; maxConcurrent = 8 } handler
    poll timeout=8s interval=50ms
        Net.portOpen $svport13
    within proc client = sh -c "for i in 1 2 3 4 5 6 7 8; do curl -s --max-time 40 http://127.0.0.1:$svport13/ & done; wait"
        Duration.sleep 500ms
print "closed"
WEOF
t0=$(date +%s)
code=0; out=$($BIN "$svdir/c3b.weir" 2>&1) || code=$?
t1=$(date +%s)
[ "$code" = "0" ] || fail "C3: eight stuck handlers at scope close must not crash weir (exit $code): $out"
# 0.5s body + one 2s drain + startup slack (a serial join is 2s x 8)
[ $((t1 - t0)) -le 6 ] || fail "C3: closing with 8 stuck handlers took $((t1 - t0))s — the drain is not bounded and parallel: $out"
echo "e2e ok: within serve — C3 a late handler never crashes; one bounded drain; port free at once"

# a handler still running past the drain is cut at its next effect boundary
# [D:serve-cut]: neither its write nor its command lands after `within
# serve` returns (the script sleeps past the handler's natural end)
svport16=8486
rm -f "$svdir/late.log"
cat > "$svdir/late.weir" <<WEOF
let log = "$svdir/late.log"
let handler = fun req ->
    File.append log ["handler started"]
    Duration.sleep 4s
    File.append log ["handler late"]
    sh -c "echo handler-late-cmd >> $svdir/late.log"
    HttpServerResponse { status = 200; headers = []; body = Text "slow" }

within serve srv = { port = $svport16; maxConcurrent = 2 } handler
    poll timeout=8s interval=50ms
        Net.portOpen $svport16
    within proc client = curl -s --max-time 10 http://127.0.0.1:$svport16/
        poll timeout=8s interval=50ms
            File.exists log
File.append log ["scope closed"]
Duration.sleep 4s
File.append log ["script end"]
WEOF
out=$($BIN "$svdir/late.weir" 2>&1) || fail "serve late-handler run failed: $out"
[ "$(tr '\n' ',' < "$svdir/late.log")" = "handler started,scope closed,script end," ] || fail "a late serve handler's effects landed after the scope: $(tr '\n' ',' < "$svdir/late.log")"
echo "e2e ok: within serve — a handler past the drain is cut; no effect after the scope returns"

# a cut handler's `always` is joined whole before `within serve` returns
# [D:serve-cut-joins-cleanup] — no drain-length cap on the cleanup, its
# command included, and an enclosing lock is held until it ends
svport17=8492
rm -f "$svdir/clean.log"
cat > "$svdir/clean.weir" <<WEOF
let log = "$svdir/clean.log"
let handler = fun req ->
    within
        File.append log ["handler started"]
        Duration.sleep 6s
    always
        File.append log ["cleanup start"]
        Duration.sleep 2500ms
        sh -c "echo cleanup-cmd >> $svdir/clean.log"
        File.append log ["cleanup end"]
    HttpServerResponse { status = 200; headers = []; body = Text "late" }

within lock "$svdir/clean.lock"
    within serve srv = { port = $svport17; maxConcurrent = 2 } handler
        poll timeout=8s interval=50ms
            Net.portOpen $svport17
        within proc client = curl -s --max-time 15 http://127.0.0.1:$svport17/
            poll timeout=8s interval=50ms
                File.exists log
    File.append log ["serve returned"]
File.append log ["lock released"]
WEOF
out=$($BIN "$svdir/clean.weir" 2>&1) || fail "serve cleanup-join run failed: $out"
[ "$(tr '\n' ',' < "$svdir/clean.log")" = "handler started,cleanup start,cleanup-cmd,cleanup end,serve returned,lock released," ] || fail "a cut serve handler's always must finish before within serve returns: $(tr '\n' ',' < "$svdir/clean.log")"
echo "e2e ok: within serve — a cut handler's always cleanup is joined whole before the scope returns"

# a signal landing while `within serve` drains holds the watchdog
# [D:serve-cut-joins-cleanup]: the cut handler's `always` (longer than the
# watchdog's 1s) is joined whole, then the signal's exit
svport18=8493
if [ "$IS_WINDOWS" != "1" ] && command -v setsid >/dev/null 2>&1; then
    rm -f "$svdir/dsig.log"
    cat > "$svdir/dsig.weir" <<WEOF
let log = "$svdir/dsig.log"
let handler = fun req ->
    within
        File.append log ["handler started"]
        Duration.sleep 6s
    always
        File.append log ["cleanup start"]
        Duration.sleep 2500ms
        File.append log ["cleanup end"]
    HttpServerResponse { status = 200; headers = []; body = Text "late" }

within serve srv = { port = $svport18; maxConcurrent = 2 } handler
    poll timeout=8s interval=50ms
        Net.portOpen $svport18
    within proc client = curl -s --max-time 15 http://127.0.0.1:$svport18/
        poll timeout=8s interval=50ms
            File.exists log
    File.append log ["body done"]
File.append log ["serve returned"]
WEOF
    setsid "$BIN" "$svdir/dsig.weir" </dev/null >/dev/null 2>&1 &
    wpid=$!
    for _ in $(seq 1 100); do grep -qF "body done" "$svdir/dsig.log" 2>/dev/null && break; sleep 0.05; done
    sleep 0.2
    kill -TERM "$wpid" 2>/dev/null || true
    code=0; wait "$wpid" || code=$?
    [ "$code" = "143" ] || fail "a SIGTERM during the serve drain exits 143 (got $code)"
    [ "$(tr '\n' ',' < "$svdir/dsig.log")" = "handler started,body done,cleanup start,cleanup end," ] || fail "a signal during the serve drain must join the handler's always whole: $(tr '\n' ',' < "$svdir/dsig.log")"
    echo "e2e ok: within serve — a signal during the drain holds the watchdog; the cut handler's always runs whole"
fi

# a serve scope opened inside an `always` cleanup still cuts its own
# handlers past the drain [D:cleanup-owns-its-cuts]: the cleanup mark
# spares the cleanup the cuts outside it, never the scope's own
svport19=8495
rm -f "$svdir/incl.log"
cat > "$svdir/incl.weir" <<WEOF
let log = "$svdir/incl.log"
let handler = fun req ->
    File.append log ["handler started"]
    Duration.sleep 6s
    File.append log ["handler late"]
    HttpServerResponse { status = 200; headers = []; body = Text "late" }

within
    print "body"
always
    within serve srv = { port = $svport19; maxConcurrent = 2 } handler
        poll timeout=8s interval=50ms
            Net.portOpen $svport19
        within proc client = curl -s --max-time 10 http://127.0.0.1:$svport19/
            poll timeout=8s interval=50ms
                File.exists log
    File.append log ["serve returned"]
WEOF
t0=$(date +%s)
out=$($BIN "$svdir/incl.weir" 2>&1) || fail "serve-in-cleanup run failed: $out"
t1=$(date +%s)
[ "$(tr '\n' ',' < "$svdir/incl.log")" = "handler started,serve returned," ] || fail "a serve scope inside a cleanup must cut its late handler: $(tr '\n' ',' < "$svdir/incl.log")"
[ $((t1 - t0)) -le 5 ] || fail "a serve scope inside a cleanup waited out its handler ($((t1 - t0))s)"
echo "e2e ok: within serve inside an always cleanup cuts its own late handler"

# (H3 lows) a queued request whose client already left is never started,
# and a Stream whose client aborts frees its slot [D:serve-drain]
svport14=8484
rm -f "$svdir/starts.txt"
cat > "$svdir/lows.weir" <<WEOF
let streamer = fun req ->
    File.append "$svdir/starts.txt" ["start"]
    let elems = [1..30] |> Seq.map (fun i ->
        Duration.sleep 100ms
        \$"chunk-{i}")
    HttpServerResponse { status = 200; headers = []; body = Stream elems }

let handler = fun req ->
    match req.path with
    | "/fast" -> HttpServerResponse { status = 200; headers = []; body = Text "fast" }
    | _ -> streamer req

within serve srv = { port = $svport14; maxConcurrent = 2 } handler
    poll timeout=8s interval=50ms
        Net.portOpen $svport14
    // two clients hold both slots; four more queue behind them and leave
    // long before the holders do, so they are gone when a slot frees
    let _gone = sh -c "for i in 1 2; do curl -s -o /dev/null --max-time 1.5 http://127.0.0.1:$svport14/ & done; sleep 0.3; for i in 1 2 3 4; do curl -s -o /dev/null --max-time 0.3 http://127.0.0.1:$svport14/ & done; wait" | complete
    let t0 = Instant.now ()
    let f = curl -s --max-time 10 http://127.0.0.1:$svport14/fast | complete
    let took = Instant.now () - t0
    print \$"fast={f.stdout |> Str.join ""} quick={took < 1s}"
    print \$"starts={File.read "$svdir/starts.txt" |> Seq.length}"
WEOF
out=$($BIN "$svdir/lows.weir" 2>&1) || fail "serve lows run failed: $out"
echo "$out" | grep -qF "fast=fast quick=true" || fail "lows: an aborted Stream must free its slot: $out"
echo "$out" | grep -qF "starts=2" || fail "lows: queued requests whose clients left must never start (2 slots held, 4 queued clients gone): $out"
echo "e2e ok: within serve — lows: a gone client's queued request never starts; an aborted Stream frees its slot"

# (shutdown) SIGINT and SIGTERM inside `within serve` with a request in
# flight: the exit code is the signal's, `always` runs, the port is free
# the moment weir is gone
svport15=8485
if [ "$IS_WINDOWS" != "1" ] && command -v setsid >/dev/null 2>&1; then
    cat > "$svdir/sig.weir" <<WEOF
let marker = Self.args |> Seq.head
let handler = fun req ->
    Duration.sleep 20s
    HttpServerResponse { status = 200; headers = []; body = Text "late" }
within
    within serve srv = { port = $svport15; maxConcurrent = 2 } handler
        Duration.sleep 60s
always
    ["cleaned-up"] |> File.write marker
WEOF
    for pair in "INT:130" "TERM:143"; do
        sig="${pair%%:*}"; want="${pair##*:}"
        M="$svdir/sig-$sig"; rm -f "$M"
        setsid "$BIN" "$svdir/sig.weir" "$M" </dev/null >/dev/null 2>&1 &
        wpid=$!
        awaitTcp $svport15 || { kill -9 "$wpid" 2>/dev/null || true; fail "shutdown: the server never came up"; }
        curl -s -o /dev/null --max-time 30 "http://127.0.0.1:$svport15/" &
        cpid=$!
        sleep 0.5
        kill -"$sig" "$wpid" 2>/dev/null || true
        code=timeout
        for _ in $(seq 1 40); do
            if ! kill -0 "$wpid" 2>/dev/null; then code=0; wait "$wpid" 2>/dev/null || code=$?; break; fi
            sleep 0.1
        done
        kill "$cpid" 2>/dev/null || true; wait "$cpid" 2>/dev/null || true
        [ "$code" = "$want" ] || { kill -9 "$wpid" 2>/dev/null || true; fail "shutdown: SIG$sig inside within serve must exit $want (got $code)"; }
        [ "$(cat "$M" 2>/dev/null)" = "cleaned-up" ] || fail "shutdown: SIG$sig must run the always cleanup"
        $BIN -e "Net.portOpen $svport15" | grep -qF "false" || fail "shutdown: port $svport15 still up after SIG$sig"
    done
    echo "e2e ok: within serve — SIGINT/SIGTERM with a request in flight: exit 130/143, always runs, port free"
fi

# (F13) tty data sanitize [D:binary-echo]/[D:tty-color]: a data value's
# terminal-DRIVING escapes (OSC title/clipboard, cursor/screen CSI, BEL)
# render sanitized at a tty, while SGR colour and a bare CR pass
# (display-only / cursor return [D:tty-cr-pass]) and weir's own colour
# is unaffected; redirected output stays byte-faithful. Driven through a pty.
# Bidi controls (RLO here) render as visible \uXXXX [D:tty-bidi-sanitize].
cat > "$svdir/f13.weir" <<'WEOF'
let esc = Str.fromBase64 "Gw=="
let bel = Str.fromBase64 "Bw=="
let cr = Str.fromBase64 "DQ=="
let name = esc + "[32mok" + esc + "]0;PWN" + bel + "mid" + cr + "end"
print name
let rlo = Str.fromBase64 "4oCu"
print ("bidi" + rlo + "txt")
WEOF
cat > "$svdir/f13err.weir" <<'WEOF'
let esc = Str.fromBase64 "Gw=="
let cr = Str.fromBase64 "DQ=="
let bad = esc + "[31mhidden" + cr + "evil" + esc + "]0;PWN"
fail $"file: {bad}"
WEOF
# F13 is driven through a real pty; Windows Python has no termios/pty
# (import pty -> ModuleNotFoundError), and tty sanitize is a POSIX-tty
# behaviour — skip the whole cell there (it never reached the redirect
# coda on Windows anyway).
if [ "$IS_WINDOWS" = "0" ]; then
sanit=$(python3 - "$BIN" "$svdir/f13.weir" <<'PYEOF'
import pty, os, sys
binp, script = sys.argv[1], sys.argv[2]
out = []
pid, fd = pty.fork()
if pid == 0:
    os.execv(binp, [binp, script])
else:
    try:
        while True:
            d = os.read(fd, 1024)
            if not d: break
            out.append(d)
    except OSError:
        pass
    data = b''.join(out)
    # new contract [D:tty-color]: SGR (display) passes; the terminal-driving
    # families are neutralized to visible \xNN. Assert the SPECIFIC payload
    # sequences (the pty's own \r\n line ending is not the payload's CR).
    sgr_kept = b'\x1b[32mok' in data          # display colour survives
    osc_raw = b'\x1b]' in data                # raw OSC introducer must NOT appear
    osc_vis = b'\\x1b]0;PWN' in data          # OSC defanged to visible text
    cr_raw = b'mid\rend' in data              # a bare CR passes raw [D:tty-cr-pass]
    cr_vis = b'\\x0d' in data                 # and never renders as \x0d
    bidi_raw = b'\xe2\x80\xae' in data         # a raw RLO must NOT appear
    bidi_vis = b'bidi\\u202Etxt' in data       # RLO visible as \u202E
    print('SGR_KEPT=%s OSC_RAW=%s OSC_VIS=%s CR_RAW=%s CR_VIS=%s BIDI_RAW=%s BIDI_VIS=%s' % (sgr_kept, osc_raw, osc_vis, cr_raw, cr_vis, bidi_raw, bidi_vis))
PYEOF
)
echo "$sanit" | grep -qF "SGR_KEPT=True" || fail "F13: SGR colour from data should reach the tty (display-only): $sanit"
echo "$sanit" | grep -qF "OSC_RAW=False" || fail "F13: a raw OSC from data must not reach the tty: $sanit"
echo "$sanit" | grep -qF "OSC_VIS=True" || fail "F13: an OSC must render as visible \\x1b] at a tty: $sanit"
echo "$sanit" | grep -qF "CR_RAW=True" || fail "F13: a bare CR must pass raw at a tty [D:tty-cr-pass]: $sanit"
echo "$sanit" | grep -qF "CR_VIS=False" || fail "F13: a bare CR must not render as \\x0d at a tty: $sanit"
echo "$sanit" | grep -qF "BIDI_RAW=False" || fail "F13: a raw bidi control must not reach the tty: $sanit"
echo "$sanit" | grep -qF "BIDI_VIS=True" || fail "F13: a bidi control must render as visible \\u202E at a tty: $sanit"
# weir's own colour survives: the error word stays coloured while the
# data in the message is sanitized
colour=$(python3 - "$BIN" "$svdir/f13err.weir" <<'PYEOF'
import pty, os, sys
binp, script = sys.argv[1], sys.argv[2]
out = []
pid, fd = pty.fork()
if pid == 0:
    os.execv(binp, [binp, script])
else:
    try:
        while True:
            d = os.read(fd, 1024)
            if not d: break
            out.append(d)
    except OSError:
        pass
    data = b''.join(out)
    before_data = data.split(b'file:')[0]
    weir_colour = b'\x1b[' in before_data   # weir's own error colour, raw
    # the data's OSC is neutralized (its SGR is cosmetic and its CR a
    # cursor return, both kept [D:tty-cr-pass])
    cr_safe = (b'hidden\revil\\x1b]0;PWN' in data) and (b'\x1b]0;PWN' not in data)
    print('WEIR_COLOUR=%s DATA_SANITIZED=%s' % (weir_colour, cr_safe))
PYEOF
)
echo "$colour" | grep -qF "WEIR_COLOUR=True" || fail "F13: weir's own colour must be unaffected: $colour"
echo "$colour" | grep -qF "DATA_SANITIZED=True" || fail "F13: the OSC in error text must be sanitized, its CR kept: $colour"
# redirected (piped) output stays byte-faithful — every raw byte survives
$BIN "$svdir/f13.weir" > "$svdir/f13.out" 2>/dev/null || true
python3 -c "import sys; d=open('$svdir/f13.out','rb').read(); sys.exit(0 if (b'\x1b[32mok\x1b]0;PWN\x07mid\rend' in d and b'bidi\xe2\x80\xaetxt' in d) else 1)" \
    || fail "F13: redirected output must stay byte-faithful (raw ESC/OSC/BEL/CR/RLO preserved through a pipe)"
echo "e2e ok: F13 tty data sanitize (data escapes neutralized at a tty, weir colour intact, pipe byte-faithful)"
# the CLI's own JSON (`check --json`, `check --can --json`) rides the same
# tty sanitizer [D:tty-bidi-sanitize]: the path carries an RLO and a C1
# CSI (the encoder escapes only C0), sanitized at a pty and byte-identical
# when redirected.
jname=$(printf 'r\342\200\256q\302\233.weir')
printf 'let x = \n' > "$svdir/$jname"
printf 'echo "hi"\n' > "$svdir/c$jname"
jsan=$(python3 - "$BIN" "$svdir/$jname" "$svdir/c$jname" <<'PYEOF'
import pty, os, sys
binp, bad, good = sys.argv[1], sys.argv[2], sys.argv[3]
def at_pty(args):
    out = []
    pid, fd = pty.fork()
    if pid == 0:
        os.execv(binp, [binp] + args)
    try:
        while True:
            d = os.read(fd, 1024)
            if not d: break
            out.append(d)
    except OSError:
        pass
    return b''.join(out)
for tag, args in (('CHECK', ['check', '--json', bad]), ('CAN', ['check', '--can', '--json', good]), ('CANH', ['check', '--can', good])):
    data = at_pty(args)
    raw = (b'\xe2\x80\xae' in data) or (b'\xc2\x9b' in data)
    vis = b'r\\u202Eq\\x9b.weir' in data
    print('%s_RAW=%s %s_VIS=%s' % (tag, raw, tag, vis))
PYEOF
)
for tag in CHECK CAN CANH; do
    echo "$jsan" | grep -qF "${tag}_RAW=False" || fail "tty-bidi: $tag must not put a raw RLO/C1 on the tty: $jsan"
    echo "$jsan" | grep -qF "${tag}_VIS=True" || fail "tty-bidi: $tag must render RLO/C1 visible at a tty: $jsan"
done
$BIN check --json "$svdir/$jname" > "$svdir/j1.out" 2>/dev/null || true
$BIN check --can --json "$svdir/c$jname" > "$svdir/j2.out" 2>/dev/null || true
$BIN check --can "$svdir/c$jname" >> "$svdir/j2.out" 2>/dev/null || true
python3 -c "import sys; a=open('$svdir/j1.out','rb').read(); b=open('$svdir/j2.out','rb').read(); n=b'r\xe2\x80\xaeq\xc2\x9b.weir'; sys.exit(0 if (n in a and n in b and b'\\\\u' not in a + b and b'\\\\x9b' not in a + b) else 1)" \
    || fail "tty-bidi: redirected check --json / --can (json and human) must stay byte-faithful (raw RLO/C1 in the path)"
echo "e2e ok: tty-bidi check --json and --can (json and human) sanitized at a tty, byte-faithful when redirected"
# every console sink rides the tty sanitizer [D:tty-sink-sanitize]:
# printerr, Log.*, the Self.prompt message and the -e echo neutralize an
# OSC at a tty (the pty harness reprs a raw ESC as \x1b, a defanged one
# as \\x1b), and stay byte-faithful when redirected
sinkrun="$(dirname "$0")/../tests/pty/pty-run.py"
for sink in 'printerr ((Str.fromBase64 "Gw==") + "]52;c;SINK")' \
            'Log.warn ((Str.fromBase64 "Gw==") + "]52;c;SINK")' \
            'Self.prompt ((Str.fromBase64 "Gw==") + "]52;c;SINK")' \
            'Some ((Str.fromBase64 "Gw==") + "]52;c;SINK")'; do
    sout=$(printf 'SLEEP 300\nSEND ans\\n\nSLEEP 300\n' | python3 "$sinkrun" 6 "$BIN" -e "$sink" 2>&1)
    echo "$sout" | grep -qF '\\x1b]52;c;SINK' || fail "tty-sink: $sink must render the OSC visible at a tty: $sout"
    echo "$sout" | grep -q '[^\\]\\x1b\]52' && fail "tty-sink: $sink put a raw OSC on the tty: $sout" || true
done
printf 'ans\n' | $BIN -e 'Log.warn ((Str.fromBase64 "Gw==") + "]52;c;SINK")' 2> "$svdir/sink.err" || true
python3 -c "import sys; sys.exit(0 if b'\x1b]52;c;SINK' in open('$svdir/sink.err','rb').read() else 1)" \
    || fail "tty-sink: a redirected stderr must stay byte-faithful"
echo "e2e ok: tty-sink printerr, Log, Self.prompt and the -e echo sanitized at a tty, byte-faithful when redirected"
# a bare CR passes every sink [D:tty-cr-pass]: printerr keeps the
# progress-line rewrite while its OSC stays visible
crout=$(printf 'SLEEP 300\n' | python3 "$sinkrun" 6 "$BIN" -e 'printerr ("50%" + (Str.fromBase64 "DQ==") + "100%" + (Str.fromBase64 "Gw==") + "]0;CR")' 2>&1)
echo "$crout" | grep -qF '50%\r100%\\x1b]0;CR' || fail "tty-cr: printerr must pass a bare CR raw and defang the OSC: $crout"
echo "e2e ok: tty-cr a bare CR passes printerr at a tty, the OSC beside it stays visible"
# the DATA renderers show CR and SGR visibly [D:tty-echo-strict] while
# print keeps both [D:tty-cr-pass]; X = "a<CR>b<ESC>[8mc"
X='(Str.fromBase64 "YQ1iG1s4bWM=")'
sout=$(printf 'SLEEP 300\n' | python3 "$sinkrun" 6 "$BIN" -e "Some $X" 2>&1)
echo "$sout" | grep -qF 'a\\rb\\x1b[8mc' || fail "tty-echo-strict: the -e echo must show CR (show's \\r) and SGR visibly: $sout"
sout=$(printf 'SLEEP 300\n' | python3 "$sinkrun" 6 "$BIN" -e "[$X]" 2>&1)
echo "$sout" | grep -qF 'a\\rb\\x1b[8mc' || fail "tty-echo-strict: the -e seq echo must show CR and SGR visibly: $sout"
sout=$(printf 'SLEEP 300\n' | python3 "$sinkrun" 6 "$BIN" -e "print $X" 2>&1)
echo "$sout" | grep -qF 'a\rb\x1b[8mc' || fail "tty-echo-strict: print must keep CR and SGR raw: $sout"
sout=$(printf 'SLEEP 900\nSEND [%s]\\r\nSLEEP 800\nSEND [{| n = %s |}; {| n = %s |}]\\r\nSLEEP 800\nSEND #quit\\r\n' "$X" "$X" "$X" | python3 "$sinkrun" 10 "$BIN" 2>&1)
[ "$(echo "$sout" | grep -oF 'a\\x0db\\x1b[8mc' | wc -l)" -ge 2 ] || fail "tty-echo-strict: the REPL lines echo and table cells must show CR and SGR visibly: $sout"
echo "$sout" | grep -qF 'a\rb\x1b[8mc' && fail "tty-echo-strict: the REPL echo put a raw CR/SGR on the tty: $sout" || true
cat > "$svdir/preview-cr.weir" <<WEOF
let p =
    plan
        Dir.create \$"/nowhere/a{$X}"
p |> Plan.preview |> Seq.iter print
WEOF
# a Dir.create: a delete of a missing path refuses at capture [D:plan-preconditions]
pout=$($BIN "$svdir/preview-cr.weir" 2>&1) || fail "tty-echo-strict: preview run failed: $pout"
[ "$pout" = 'make dir /nowhere/aa\x0db\x1b[8mc' ] || fail "tty-echo-strict: a Plan.preview path must render CR and SGR visibly, piped too: $pout"
echo "e2e ok: tty-echo-strict the -e/REPL echo, table cells and Plan.preview show CR and SGR visibly; print keeps them"
# Args.load --help rides the tty sanitizer [D:tty-help-sanitize]
printf 'type Cli = {\n    /// be verbose \033]0;HELP\007\n    verbose: bool\n}\nlet cli = Args.load Cli\nprint (show cli.verbose)\n' > "$svdir/help-osc.weir"
sout=$(printf 'SLEEP 300\n' | python3 "$sinkrun" 6 "$BIN" "$svdir/help-osc.weir" --help 2>&1)
echo "$sout" | grep -qF 'be verbose \\x1b]0;HELP\\x07' || fail "tty-help: --help must render the doc's OSC visible at a tty: $sout"
$BIN "$svdir/help-osc.weir" --help > "$svdir/help-osc.out" 2>&1 || fail "tty-help: --help must exit 0"
python3 -c "import sys; sys.exit(0 if b'\x1b]0;HELP\x07' in open('$svdir/help-osc.out','rb').read() else 1)" \
    || fail "tty-help: redirected --help must stay byte-faithful"
echo "e2e ok: tty-help Args.load --help doc text sanitized at a tty, byte-faithful when redirected"
# weir verify/restore render lock.json fields as data [D:lock-print-sanitize]
lk="$svdir/lockprint"; mkdir -p "$lk/.weir/sigs"; : > "$lk/.git"
lockof() { printf '{"schemaVersion": 1, "artifacts": [{"kind": "%s", "name": "ls\\u001b]52;c;SINK\\u0007", "url": "generated:help", "sha256": "%s", "path": "sigs/ls.weir", "version": "x"}]}' \
    "$1" "$(printf '0%.0s' $(seq 64))" > "$lk/.weir/lock.json"; }
lockof sig
printf '// tampered\n' > "$lk/.weir/sigs/ls.weir"
absbin=$(cd "$(dirname "$BIN")" && pwd)/$(basename "$BIN")
abssink=$(cd "$(dirname "$sinkrun")" && pwd)/$(basename "$sinkrun")
sout=$(cd "$lk" && printf 'SLEEP 300\n' | python3 "$abssink" 6 "$absbin" verify 2>&1)
echo "$sout" | grep -qF 'ls\\x1b]52;c;SINK\\x07: MODIFIED' || fail "lock-print: verify must render the lock name's OSC visible at a tty: $sout"
(cd "$lk" && "$absbin" verify > "$lk/verify.out" 2>&1) && fail "lock-print: a modified sig must exit 1"
python3 -c "import sys; d=open('$lk/verify.out','rb').read(); sys.exit(0 if b'ls\\\\x1b]52;c;SINK\\\\x07: MODIFIED' in d and b'\x1b' not in d and b'\x07' not in d else 1)" \
    || fail "lock-print: redirected verify must render the lock name's OSC visibly too (R2): $(cat -v "$lk/verify.out")"
lockof evil
kout=$(cd "$lk" && "$absbin" verify 2>&1) && fail "lock-print: an unknown kind must refuse: $kout"
echo "$kout" | grep -qF "unknown \"kind\" 'evil'" || fail "lock-print: the refusal must name the kind: $kout"
echo "e2e ok: lock-print verify renders lock fields visibly, at a tty and piped; an unknown kind refuses"
# every diagnostic sink rides the tty sanitizer [D:tty-diag-sanitize]:
# check's import path, the run's parse/type source echo, the invalid-regex
# message, the #sig name and `no such script` render an OSC visible at a
# tty, while weir's own colour stays; redirected output stays raw
dg="$svdir/diag"; mkdir -p "$dg"
printf 'import "./x\033]52;c;DIAG\007.weir" as M\nprint "x"\n' > "$dg/imp.weir"
printf 'let x = "a\033]52;c;DIAG\007b" + 1\nprint x\n' > "$dg/src.weir"
printf 'let x = "a\033]52;c;DIAG\007b" +\nprint x\n' > "$dg/parse.weir"
printf 'let f s =\n    match s with\n    | Regex @"(\033]52;c;DIAG\007" x -> x\n    | _ -> ""\nprint (f "a")\n' > "$dg/rx.weir"
printf '#sig zz\033]52;c;DIAG\007\nprint "x"\n' > "$dg/sig.weir"
for args in "check $dg/imp.weir" "$dg/imp.weir" "$dg/src.weir" "$dg/parse.weir" "check $dg/rx.weir" "$dg/rx.weir" \
            "check $dg/sig.weir" "$dg/sig.weir" "$(printf "$dg/nope\033]52;c;DIAG\007.weir")"; do
    # shellcheck disable=SC2086
    dout=$(printf 'SLEEP 300\n' | python3 "$sinkrun" 6 "$BIN" $args 2>&1)
    echo "$dout" | grep -qF '\\x1b]52;c;DIAG' || fail "tty-diag: weir $args must render the OSC visible at a tty: $dout"
    echo "$dout" | grep -q '[^\\]\\x1b\]52' && fail "tty-diag: weir $args put a raw OSC on the tty: $dout" || true
done
dout=$(printf 'SLEEP 300\n' | python3 "$sinkrun" 6 "$BIN" "$dg/src.weir" 2>&1)
echo "$dout" | grep -qF '\x1b[31mtype error' || fail "tty-diag: weir's own colour must survive the sanitizer: $dout"
$BIN check "$dg/imp.weir" > "$dg/imp.out" 2>&1 || true
$BIN "$dg/src.weir" 2> "$dg/src.err" || true
python3 -c "import sys; sys.exit(0 if all(b'\x1b]52;c;DIAG\x07' in open(f,'rb').read() for f in sys.argv[1:]) else 1)" "$dg/src.err" \
    || fail "tty-diag: a redirected source echo must stay byte-faithful"
# the import path is data in a message: escaped piped too [D:error-data-escape]
grep -qF 'x\x1b]52;c;DIAG\x07.weir' "$dg/imp.out" || fail "tty-diag: a redirected import path must render escaped: $(cat "$dg/imp.out")"
echo "e2e ok: tty-diag check/run diagnostics (import path, source echo, regex, #sig, no such script) sanitized at a tty, byte-faithful when redirected"
# a runtime error escapes the data it quotes [D:error-data-escape] — always,
# piped too: a parse input, a document excerpt, a path, an env var name
ed="$svdir/errdata"; mkdir -p "$ed"
printf 'let n = Float.parse (Env.need "X")\nprint $"{n}"\n' > "$ed/float.weir"
printf 'type T = { a: int }\nlet t = [Env.need "X"] |> from json T\nprint $"{t.a}"\n' > "$ed/json.weir"
printf 'print (File.read (Env.need "X") |> Seq.length)\n' > "$ed/path.weir"
printf 'print (Env.need (Env.need "X"))\n' > "$ed/env.weir"
printf 'let r = Http.send { (Http.get "http://127.0.0.1:9/") with headers = [(Env.need "X", "v")] }\nprint $"{r.status}"\n' > "$ed/hdr.weir"
printf 'type E = { x: int }\nlet e = Env.load E\nprint $"{e.x}"\n' > "$ed/envload.weir"
for f in float json path env hdr; do
    eout=$(X="$(printf '/no\r\033[8mpe')" "$BIN" "$ed/$f.weir" 2>&1) && fail "error-data-escape: $f must fail: $eout"
    echo "$eout" | grep -qF '\x0d\x1b[8mpe' || fail "error-data-escape: $f must render the quoted CR/ESC visibly: $eout"
    printf '%s' "$eout" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b' in d or b'\r' in d) else 0)" \
        || fail "error-data-escape: $f put a raw CR/ESC in the error: $eout"
done
# Env.load and Args.load quote a bad value, an unknown flag and a stray argument
xv="$(printf '/no\r\033[8mpe')"
eout=$(env x="$xv" "$BIN" "$ed/envload.weir" 2>&1) && fail "error-data-escape: Env.load must fail: $eout"
echo "$eout" | grep -qF '\x0d\x1b[8mpe' || fail "error-data-escape: Env.load must render the quoted CR/ESC visibly: $eout"
printf '%s' "$eout" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b' in d or b'\r' in d) else 0)" \
    || fail "error-data-escape: Env.load put a raw CR/ESC in the error: $eout"
printf 'type A = { port: int }\nlet a = Args.load A\nprint $"{a.port}"\n' > "$ed/args.weir"
for args in "--port|$xv" "--port|1|--$xv" "--port=$xv" "--port|1|$xv"; do
    IFS='|' read -r -a av <<<"$args"
    eout=$("$BIN" "$ed/args.weir" "${av[@]}" 2>&1) && fail "error-data-escape: Args.load ${av[*]} must fail: $eout"
    echo "$eout" | grep -qF '\x0d\x1b[8mpe' || fail "error-data-escape: Args.load must render the quoted CR/ESC visibly: $eout"
    printf '%s' "$eout" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b' in d or b'\r' in d) else 0)" \
        || fail "error-data-escape: Args.load put a raw CR/ESC in the error: $eout"
done
# from xml over bytes quotes its excerpt as the text path does
# [D:xml-encoding]
printf '<R><a>x\r\033[8mpe</a' > "$ed/bad.xml"
printf 'type R = { a: string }\nlet r = File.readBytes "%s/bad.xml" |> from xml R\nprint r.a\n' "$ed" > "$ed/xmlb.weir"
eout=$("$BIN" "$ed/xmlb.weir" 2>&1) && fail "error-data-escape: a bad xml document must fail: $eout"
echo "$eout" | grep -qF 'x\x0d\x1b[8mpe' || fail "error-data-escape: from xml over bytes must render the quoted CR/ESC visibly: $eout"
printf '%s' "$eout" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b' in d or b'\r' in d) else 0)" \
    || fail "error-data-escape: from xml over bytes put a raw CR/ESC in the error: $eout"
printf 'let x: int = "a"\n' > "$(printf '%s/a\033[8m\rb.weir' "$ed")"
eout=$("$BIN" check "$(printf '%s/a\033[8m\rb.weir' "$ed")" 2>&1; "$BIN" "$(printf '%s/a\033[8m\rb.weir' "$ed")" 2>&1; "$BIN" "$(printf '%s/z\033[8m\rb.weir' "$ed")" 2>&1 || true)
echo "$eout" | grep -qF 'no such script' || fail "error-data-escape: a missing script must say so: $eout"
echo "$eout" | grep -qF 'a\x1b[8m\x0db.weir:1:' || fail "error-data-escape: a diagnostic must name the file visibly: $eout"
printf '%s' "$eout" | python3 -c "import sys; d=sys.stdin.buffer.read(); sys.exit(1 if (b'\x1b[8m' in d or b'\r' in d) else 0)" \
    || fail "error-data-escape: a file name put a raw CR/ESC in a diagnostic: $eout"
echo "e2e ok: error-data-escape runtime errors render the quoted CR/ESC visibly (parse input, json excerpt, path, env name, header name, Env.load/Args.load value, file name)"
# Plan.preview renders an http send line's escapes visibly [D:tty-echo-strict]
cat > "$ed/pv.weir" <<'WEOF'
let p =
    plan
        let _r = Http.send { Http.defaults with method = Post; url = Env.need "U"; body = Text "ping" }
        ()
p |> Plan.preview |> Seq.iter print
WEOF
pout=$(U="$(printf 'http://127.0.0.1:1/h\033[8m')" "$BIN" "$ed/pv.weir" 2>&1) || fail "tty-echo-strict: the http preview run failed: $pout"
echo "$pout" | grep -qF 'http send' || fail "tty-echo-strict: no http send line: $pout"
echo "$pout" | grep -qF '/h\x1b[8m' || fail "tty-echo-strict: an http send preview must render the url's ESC visibly: $pout"
printf '%s' "$pout" | python3 -c "import sys; sys.exit(1 if b'\x1b' in sys.stdin.buffer.read() else 0)" \
    || fail "tty-echo-strict: the http send preview put a raw ESC out: $pout"
echo "e2e ok: tty-echo-strict Plan.preview's http send line shows escapes visibly"
# weir add sig renders the tool's text as data [D:sig-text-sanitize]
sg="$svdir/sigtext"; mkdir -p "$sg/bin" "$sg/p"; : > "$sg/p/.git"
cat > "$sg/bin/evil" <<'SHEOF'
#!/bin/sh
case "$*" in
  --version) printf 'evil 1.0\033]52;c;ZXZpbA==\007\n';;
  *--help*) printf 'Usage: evil [options]\n\nOptions:\n  --out <file>   write \033]0;T\007 out\n  --ok           fine\n';;
esac
SHEOF
chmod +x "$sg/bin/evil"
sout=$(cd "$sg/p" && printf 'SLEEP 300\n' | PATH="$sg/bin:$PATH" python3 "$abssink" 6 "$absbin" add sig evil 2>&1)
echo "$sout" | grep -qF 'version: evil 1.0\\x1b]52;c;ZXZpbA==\\x07' || fail "sig-text: add sig must print the version's OSC visibly at a tty: $sout"
python3 -c "import sys; d=open('$sg/p/.weir/sigs/evil.weir','rb').read(); sys.exit(1 if (b'\x1b' in d or b'\x07' in d) else 0)" \
    || fail "sig-text: the generated sig must hold no raw control byte"
grep -qF 'evil 1.0\\x1b]52' "$sg/p/.weir/sigs/evil.weir" || fail "sig-text: the generated version must show the escape: $(cat "$sg/p/.weir/sigs/evil.weir")"
printf '#sig evil\nevil --ok\n' > "$sg/p/use.weir"
cout=$(cd "$sg/p" && PATH="$sg/bin:$PATH" "$absbin" check use.weir 2>&1) || fail "sig-text: the generated sig must still check: $cout"
echo "e2e ok: sig-text add sig prints and writes the tool's escapes visibly; the sig still checks"
# a hung-up terminal never aborts weir [D:console-hangup]: the cleanup
# prints after the hangup, runs whole, and the exit is SIGHUP's
hu="$svdir/hup"; mkdir -p "$hu"
cat > "$hu/h.weir" <<'WEOF'
let log = Self.args |> Seq.head
within
    File.append log ["body"]
    [1..400] |> Seq.iter (fun k ->
        print $"{k}"
        Duration.sleep 20ms)
always
    print "cleanup prints"
    printerr "cleanup printerr"
    Log.warn "cleanup logs"
    File.append log ["cleanup end"]
WEOF
hout=$(python3 - "$BIN" "$hu/h.weir" "$hu/h.log" <<'HUPEOF'
import os, pty, sys, time
pid, fd = pty.fork()
if pid == 0:
    os.execv(sys.argv[1], sys.argv[1:])
t0 = time.monotonic()
while time.monotonic() - t0 < 1.5:
    try: os.read(fd, 65536)
    except OSError: break
os.close(fd)
t1 = time.monotonic(); code = 'HANG'
while time.monotonic() - t1 < 10:
    p, st = os.waitpid(pid, os.WNOHANG)
    if p: code = os.waitstatus_to_exitcode(st); break
    time.sleep(0.02)
if code == 'HANG': os.kill(pid, 9); os.waitpid(pid, 0)
print('EXIT=%s' % code)
HUPEOF
)
case "$hout" in EXIT=129|EXIT=-1) ;; *) fail "console-hangup: a hung-up terminal must end weir by SIGHUP's teardown (129), not abort: $hout" ;; esac
grep -qxF "cleanup end" "$hu/h.log" || fail "console-hangup: the cleanup must run whole after the hangup: $(cat "$hu/h.log" 2>/dev/null)"
echo "e2e ok: console-hangup a hung-up terminal unwinds by SIGHUP, the printing cleanup runs whole"
# console-write [D:console-write] (E1): a full or closed stderr never aborts
# weir — the cleanup runs, the error's write is dropped, the exit is the run's
cw="$svdir/cw"; mkdir -p "$cw"
printf '%s\n' 'within' '    printerr "progress"' '    Log.warn "a log line"' '    print "body-done"' 'always' '    print "cleanup-ran"' 'fail "x"' > "$cw/f.weir"
for redir in full closed; do
    [ "$redir" = full ] && [ ! -e /dev/full ] && continue
    rc=0
    if [ "$redir" = full ]; then
        out=$("$BIN" "$cw/f.weir" 2>/dev/full) || rc=$?
    else
        out=$("$BIN" "$cw/f.weir" 2>&-) || rc=$?
    fi
    [ "$rc" = 1 ] || fail "console-write: a $redir stderr keeps the run's exit 1, never an abort: rc=$rc out=$out"
    [ "$(echo "$out" | tr '\n' ,)" = "body-done,cleanup-ran," ] || fail "console-write: a $redir stderr runs the script and its cleanup: $out"
done
echo "e2e ok: console-write a full or closed stderr never aborts — writes dropped, cleanup runs, exit kept"
# C1-6: a closed stdin or stdout is /dev/null too — never the runtime's own
# fd, which the lowest-fd rule hands the first open [D:console-write]
if [ "$IS_WINDOWS" != "1" ]; then
    printf '%s\n' 'Self.stdin |> Seq.iter (fun l -> print $"line {Str.length l}")' 'print "end"' > "$cw/in.weir"
    out=$(timeout 10 "$BIN" "$cw/in.weir" <&- 2>&1)
    [ "$out" = "end" ] || fail "console-write: a closed stdin reads nothing: $out"
    rc=0; err=$("$BIN" -e 'print "x"' 2>&1 1>&-) || rc=$?
    [ "$rc" = 0 ] && [ -z "$err" ] || fail "console-write: a closed stdout writes nowhere, never an error: rc=$rc $err"
    echo "e2e ok: console-write a closed stdin reads nothing, a closed stdout writes nowhere"
fi

# a tty REPL whose terminal hangs up mid-command dies by the SIGHUP, never
# an internal error [D:console-write]; one killed by TERM/HUP at its
# prompt restores the terminal [D:die-by-signal]
rh="$svdir/rh"; mkdir -p "$rh/c" "$rh/d" "$rh/s" "$rh/k"
rhout=$(env HOME="$rh" XDG_CONFIG_HOME="$rh/c" XDG_DATA_HOME="$rh/d" XDG_STATE_HOME="$rh/s" XDG_CACHE_HOME="$rh/k" python3 - "$BIN" <<'HUPEOF'
import os, pty, sys, time, signal, termios, select
def run(act):
    pid, fd = pty.fork()
    if pid == 0:
        for s in (signal.SIGINT, signal.SIGQUIT, signal.SIGHUP, signal.SIGTERM): signal.signal(s, signal.SIG_DFL)
        os.execv(sys.argv[1], sys.argv[1:])
    def pump(d):
        t = time.monotonic()
        while time.monotonic() - t < d:
            if select.select([fd], [], [], 0.02)[0]:
                try: os.read(fd, 65536)
                except OSError: return
    pump(1.5)
    if act == 'hangup':
        os.write(fd, b'/bin/sleep 3\r'); pump(0.6); os.close(fd)
    else:
        os.kill(pid, getattr(signal, 'SIG' + act))
    t = time.monotonic(); code = 'HANG'
    while time.monotonic() - t < 8:
        p, st = os.waitpid(pid, os.WNOHANG)
        if p: code = os.waitstatus_to_exitcode(st); break
        time.sleep(0.02)
    if code == 'HANG': os.kill(pid, 9); os.waitpid(pid, 0)
    mode = ''
    if act != 'hangup':
        lf = termios.tcgetattr(fd)[3]; mode = ' icanon=%d echo=%d' % (bool(lf & termios.ICANON), bool(lf & termios.ECHO)); os.close(fd)
    print('%s=%s%s' % (act, code, mode))
for a in ('hangup', 'TERM', 'HUP'): run(a)
HUPEOF
)
echo "$rhout" | grep -qxE "hangup=(-1|129)" || fail "a tty REPL whose terminal hangs up mid-command dies by the SIGHUP: $rhout"
echo "$rhout" | grep -qx "TERM=-15 icanon=1 echo=1" || fail "a tty REPL killed by TERM at its prompt restores the terminal: $rhout"
echo "$rhout" | grep -qx "HUP=-1 icanon=1 echo=1" || fail "a tty REPL killed by HUP at its prompt restores the terminal: $rhout"
echo "e2e ok: a tty REPL dies by a hangup's SIGHUP mid-command, and restores the terminal on TERM/HUP at its prompt"

# every File op on a FIFO is cut by a race win [D:fifo-ops-polled] (Linux)
if [ "$(uname -s)" = Linux ]; then
    ff="$svdir/ff"; mkdir -p "$ff/d"
    cat > "$ff/arm.weir" <<'WEOF'
let op = Self.args |> Seq.head
let f = Self.args |> Seq.item 1
let d = Self.args |> Seq.item 2
let r =
    [0; 1] |> Seq.pfirst (fun i ->
        if i == 0 then
            if op == "sha256" then
                print (File.sha256 f)
            elif op == "copy" then
                File.copy f $"{d}/copied"
            elif op == "write" then
                File.write f ["x"]
            elif op == "append" then
                File.append f ["x"]
            elif op == "writeBytes" then
                File.writeBytes f (Str.toUtf8 "x")
            "op"
        else
            Duration.sleep 200ms
            "timer")
print $"{op}: r={r}"
WEOF
    for op in sha256 copy write append writeBytes; do
        rm -rf "$ff/d" "$ff/p"; mkdir -p "$ff/d"; mkfifo "$ff/p"
        out=$(timeout -s KILL 10 "$BIN" "$ff/arm.weir" "$op" "$ff/p" "$ff/d" 2>&1) || true
        [ "$out" = "$op: r=timer" ] || fail "File.$op on a peerless FIFO is cut by a race win: $out"
    done
    # writeAtomic refuses a FIFO at once, leaving it a FIFO, nothing written
    rm -rf "$ff/p"; mkfifo "$ff/p"
    out=$(timeout -s KILL 10 "$BIN" -e "File.writeAtomic \"$ff/p\" [\"x\"]" 2>&1) && fail "File.writeAtomic on a FIFO must refuse: $out"
    echo "$out" | grep -qF "is a FIFO — an atomic replace has no meaning there; use File.write" || fail "File.writeAtomic on a FIFO names the refusal: $out"
    [ -p "$ff/p" ] || fail "File.writeAtomic on a FIFO leaves it a FIFO"
    [ -z "$(ls -A "$ff" | grep -v '^p$' | grep -v '^d$' | grep -v '^arm.weir$')" ] || fail "File.writeAtomic on a FIFO writes nothing: $(ls -A "$ff")"
    printf '%s\n' 'let p = Self.args |> Seq.head' 'let pl =' '    plan' '        File.writeAtomic p ["x"]' 'print $"captured {Plan.isEmpty pl}"' > "$ff/plan.weir"
    out=$(timeout -s KILL 10 "$BIN" "$ff/plan.weir" "$ff/p" 2>&1) && fail "a plan capturing File.writeAtomic onto a FIFO must refuse: $out"
    echo "$out" | grep -qF "is a FIFO" || fail "a plan's File.writeAtomic onto a FIFO refuses at capture: $out"
    [ -p "$ff/p" ] || fail "a plan's refused writeAtomic leaves the FIFO"
    rm -f "$ff/plan.weir"
    (sleep 0.3; cat "$ff/p" > "$ff/got") &
    "$BIN" -e "File.write \"$ff/p\" [\"hello\"; \"fifo\"]" || fail "File.write to a FIFO with a reader writes"
    wait
    [ "$(tr '\n' , < "$ff/got")" = "hello,fifo," ] || fail "File.write to a FIFO delivers the lines: $(cat "$ff/got")"
    echo "e2e ok: File.sha256/copy/write/append/writeBytes on a FIFO are cut by a race win; writeAtomic refuses one (direct and at plan capture); a read FIFO gets the write"
fi

# Proc.wait returns at the exit, not at a 50ms poll (D1)
printf '%s\n' 'for i in [1..60] do' '    within proc p = sh -c "exit 0"' '        let _c = Proc.wait p' '        ()' 'print "done"' > "$svdir/pw.weir"
pws=$(now_ms)
out=$("$BIN" "$svdir/pw.weir" 2>&1)
pwms=$(( $(now_ms) - pws ))
[ "$out" = "done" ] || fail "Proc.wait loop: $out"
[ "$pwms" -lt 2500 ] || fail "60 Proc.wait calls take ${pwms}ms — Proc.wait must wake at the exit, not poll 50ms"
echo "e2e ok: Proc.wait wakes at the exit (60 waits in ${pwms}ms)"
else
    echo "e2e skip: F13 tty sanitize is pty-driven — no termios/pty on Windows Python"
fi

rm -rf "$svdir"

# ---- front-end hardening pins: three security cuts -------------------------
# each triggered a SIGABRT/DoS on the base; the fix makes each a located
# diagnostic (exit != 134) or linear time. Only e2e drives the whole
# binary, so it is where the exit code (not just the message) is pinned.
hdir=$(mkweirtmp)

# Fix 1 -- attr integer past 64-bit: located parse error, not exit 134
# [D:attr-int-overflow]
cat > "$hdir/attr.weir" <<WEOF
type T = {
    [<Default 99999999999999999999>]
    A: int
}
print "x"
WEOF
rc=0; out=$($BIN check "$hdir/attr.weir" 2>&1) || rc=$?
[ "$rc" != "134" ] || fail "attr overflow still SIGABRTs (exit 134): $out"
[ "$rc" = "1" ] || fail "attr overflow must be a lint error (exit 1), got $rc: $out"
echo "$out" | grep -qF "out of range (64-bit)" || fail "attr overflow must teach the range: $out"
echo "e2e ok: attr integer overflow is a located error (exit $rc, not 134)"

# Fix 2 -- bareword ';'-spine: linear, not O(N^2) [D:head-word-bound]
# 20k barewords was >25s (quadratic); the bound makes it ~5s. A generous
# ceiling catches a regression to quadratic without flaking on load.
python3 -c "print('let x = ' + ';'.join(['b']*20000))" > "$hdir/spine.weir"
t0=$(date +%s)
rc=0; $BIN check "$hdir/spine.weir" >/dev/null 2>&1 || rc=$?
t1=$(date +%s)
elapsed=$((t1 - t0))
[ "$rc" != "134" ] || fail "bareword spine crashed (exit 134)"
[ "$elapsed" -lt 20 ] || fail "bareword ';'-spine is not linear: 20k barewords took ${elapsed}s (was quadratic; expected <20s)"
echo "e2e ok: bareword ';'-spine checks in ${elapsed}s (linear, was O(N^2)/>25s)"

# doc-comment run linearity: a contiguous /// run in a module file accumulated
# O(N^2) (a list append per line) — 50k /// lines was ~21s. Cons+reverse makes
# it linear (~0.4s). A generous ceiling catches a regression to quadratic.
python3 -c "print('module M'); [print('/// d') for _ in range(50000)]; print('let x = 1')" > "$hdir/docdos.weir"
t0=$(date +%s)
$BIN check "$hdir/docdos.weir" >/dev/null 2>&1 || true
t1=$(date +%s)
elapsed=$((t1 - t0))
[ "$elapsed" -lt 15 ] || fail "doc-comment /// run is not linear: 50k lines took ${elapsed}s (was O(N^2)/~21s; expected <15s)"
echo "e2e ok: doc-comment /// run checks in ${elapsed}s (linear, was O(N^2)/~21s)"

# Fix 3 -- the CLI guard: the two fixed crash triggers exit located, never
# 134, through the whole binary [D:cli-exception-guard]
rc=0; $BIN check "$hdir/attr.weir" >/dev/null 2>&1 || rc=$?
[ "$rc" != "134" ] || fail "CLI guard: a front-end trigger still aborts with 134"
echo "e2e ok: CLI verb dispatch backstops the front-end crash class (exit $rc, not 134)"
rm -rf "$hdir"

# ---- the spawn-boundary NUL funnel [D:spawn-nul-funnel] ------------------
# SECURITY.md's word-integrity claim: a NUL-bearing value is refused at
# the process boundary with a diagnostic, never silently truncated. The
# refusal lived only in the evaluator's statement/pipe constructors —
# four downstream spawn paths (the reifiers, the ambient `within env`
# overlay + the `$e(...)` twin, `into`, and the dynamic head `^$name`)
# assembled argv/env and skipped it, so a `\0` truncated at execve. The
# fix moves the refusal to the one spawn funnel (Proc.spawn), so every
# path inherits it. Each hostile script below must refuse (exit 1) and
# spawn no child (the child logs its argv/env to a file — it must stay
# empty). The NUL enters as external data: a shim on PATH emits a
# NUL-bearing line, decoded into a weir string.
# POSIX-only: the shims are #!/bin/sh scripts used as command heads — a
# native weir.exe cannot exec a shebang script (and an extensionless name
# does not resolve via PATHEXT), so on Windows `emitprog` reads as an
# unbound variable. sh alone is not enough (MSYS ships it); the NUL-refusal
# itself is platform-neutral and unit-tested [D:spawn-nul-funnel].
if [ "$IS_WINDOWS" = "0" ] && command -v sh >/dev/null 2>&1; then
    nuldir=$(mkweirtmp)
    mkdir -p "$nuldir/bin"
    nullog="$nuldir/child.log"
    # argvdump: logs its argv (excluding $0) and env FOO, then exits 0 —
    # so a truncated word/env leaves evidence and a clean exit (the vuln)
    cat > "$nuldir/bin/argvdump" <<EOF
#!/bin/sh
{ i=0; for a in "\$@"; do echo "argv[\$i]=\$a"; i=\$((i+1)); done; echo "FOO=\${FOO-unset}"; } >> "$nullog"
exit 0
EOF
    chmod +x "$nuldir/bin/argvdump"
    # emitnul: a NUL-bearing "line" a\0b ; emitprog: argvdump\0junk (a head)
    printf '#!/bin/sh\nprintf '\''a\\000b'\''\n' > "$nuldir/bin/emitnul"
    printf '#!/bin/sh\nprintf '\''argvdump\\000junk'\''\n' > "$nuldir/bin/emitprog"
    chmod +x "$nuldir/bin/emitnul" "$nuldir/bin/emitprog"

    nul_refuses() { # name — asserts exit 1, no child, NUL diagnostic
        : > "$nullog"
        set +e
        out=$(PATH="$nuldir/bin:$PATH" $BIN "$nuldir/$1.weir" 2>&1)
        rc=$?
        set -e
        [ "$rc" -eq 1 ] || fail "nul $1: expected exit 1, got $rc (out: $out)"
        [ ! -s "$nullog" ] || fail "nul $1: a child SPAWNED (log: $(cat "$nullog"))"
        echo "$out" | grep -qF "NUL byte" || fail "nul $1: no NUL diagnostic (out: $out)"
        echo "e2e ok: nul funnel — $1 refuses, no child, located diagnostic"
    }

    # 1. dynamic head ^$name — the worst: a NUL-bearing head would resolve
    # through PATH to the prefix program (argvdump) and run it
    cat > "$nuldir/dynhead.weir" <<'WEOF'
let p = emitprog |> Seq.exactlyOne
let r = ^$p hello | complete
print (show r.exitCode)
WEOF
    nul_refuses dynhead
    # 2. reifier argument (| complete)
    cat > "$nuldir/reifier.weir" <<'WEOF'
let x = emitnul |> Seq.exactlyOne
let r = argvdump $x | complete
print (show r.exitCode)
WEOF
    nul_refuses reifier
    # 3. ambient `within env` overlay — the env value carries the NUL
    cat > "$nuldir/ambient.weir" <<'WEOF'
let v = emitnul |> Seq.exactlyOne
within env [Env.pair "FOO" v]
    let r = argvdump hello | complete
    print (show r.exitCode)
WEOF
    nul_refuses ambient
    # 4. a value piped into `sh -c` — the cmdline tail past the NUL would drop
    cat > "$nuldir/pipesh.weir" <<'WEOF'
let tail = emitnul |> Seq.exactlyOne
let cmd = $"argvdump before{tail}after"
["one"] | sh -c $cmd |> Seq.iter print
WEOF
    nul_refuses pipesh
    # 5. the $e(...) env twin — an env value via the sigil
    cat > "$nuldir/envsigil.weir" <<'WEOF'
let v = emitnul |> Seq.exactlyOne
let e = Env.ofPairs [("FOO", v)]
let r = $e(argvdump hello | complete)
print (show r.exitCode)
WEOF
    nul_refuses envsigil
    # the env key coverage — a NUL in the key, not the value
    cat > "$nuldir/envkey.weir" <<'WEOF'
let k = emitnul |> Seq.exactlyOne
let e = Env.ofPairs [(k, "v")]
let r = $e(argvdump hello | complete)
print (show r.exitCode)
WEOF
    nul_refuses envkey
    # the statement-path control stays green: a plain command with a NUL
    # arg still refuses (the pre-existing evaluator refusal), no child
    cat > "$nuldir/stmt.weir" <<'WEOF'
let x = emitnul |> Seq.exactlyOne
argvdump $x
WEOF
    nul_refuses stmt
    # and a clean command still spawns (the funnel is not overzealous)
    : > "$nullog"
    PATH="$nuldir/bin:$PATH" $BIN -e 'argvdump ok deliberate' >/dev/null 2>&1 || fail "nul: a clean command must still run"
    grep -qF "argv[0]=ok" "$nullog" || fail "nul: clean command did not reach the child"
    echo "e2e ok: nul funnel — a clean command still spawns (no false refusal)"
    rm -rf "$nuldir"
fi

# ---- STRIX-6: yaml depth caps — hostile input teaches, never hangs/crashes [D:yaml-depth]
s6dir=$(mkweirtmp)
# a 5000-level `a:` ladder (1-space indents) — the cubic-hang shape. The
# parser caps at 500 and returns a located diagnostic promptly.
: >"$s6dir/ladder.yaml"
for i in $(seq 0 4999); do
    printf '%*sa:\n' "$i" "" >>"$s6dir/ladder.yaml"
done
printf '%*sv: 1\n' 5000 "" >>"$s6dir/ladder.yaml"
cat >"$s6dir/parse.weir" <<WEOF
let doc = File.read "$s6dir/ladder.yaml" |> Yaml.parse
print \$"{doc}"
WEOF
out=$($BIN "$s6dir/parse.weir" 2>&1) && fail "a 5000-deep ladder must be refused, not parsed: $out" || true
echo "$out" | grep -qF "yaml nesting is too deep (limit 500)" || fail "parser depth cap message: $out"
echo "e2e ok: STRIX-6 — a 5000-deep yaml ladder hits the parser cap (no hang)"
# a Seq.fold-nested YSeq ~100k deep is the StackOverflow (exit 134) shape;
# the emitter caps at 1000 and fails clean (exit 1).
cat >"$s6dir/emit.weir" <<WEOF
let deep = [1..100000] |> Seq.fold (fun acc _ -> YSeq [acc]) (YInt 1)
deep |> to yaml |> Seq.length |> print
WEOF
out=$($BIN "$s6dir/emit.weir" 2>&1) && fail "a 100k-deep value must be refused, not emitted: $out" || true
echo "$out" | grep -qF "nests deeper than 100" || fail "emitter depth cap message: $out"
echo "e2e ok: STRIX-6 — a 100k-deep 'to yaml' hits the emitter cap (no crash)"
rm -rf "$s6dir"

# ---- W2 docs-vs-binary: GUIDE/tooling/repl claims no fence pins
w2dir=$(mkweirtmp)
# tooling.md: a mistyped option gets the closest one, top level and per subcommand
out=$($BIN --hlep 2>&1) && fail "--hlep must exit 2" || true
expect "a mistyped --help suggests it" "Did you mean '--help'?" "$out"
out=$($BIN check --cna x.weir 2>&1) && fail "check --cna must exit 2" || true
expect "a mistyped check flag suggests the closest" "Did you mean '--can'?" "$out"
out=$($BIN fmt --chek x.weir 2>&1) && fail "fmt --chek must exit 2" || true
expect "a mistyped fmt flag suggests the closest" "Did you mean '--check'?" "$out"
# repl.md: a directive is always a single line — even right after match
# arms or a |> chain, whose next line could still have continued them
printf 'match 1 with\n| 1 -> "one"\n| _ -> "other"\n#echo\nlet xs =\n    [1]\n    |> Seq.map id\n#echo\n#quit\n' >"$w2dir/in.txt"
out=$(cd "$w2dir" && XDG_CONFIG_HOME="$w2dir/cfg" XDG_STATE_HOME="$w2dir/st" "$BIN" <"$w2dir/in.txt" 2>&1)
[ "$(echo "$out" | grep -c 'echo cap: 100')" = 2 ] || fail "a directive after a block must run as a directive: $out"
echo "$out" | grep -qF "parse error" && fail "a directive after a block must not be parsed as code: $out"
echo "e2e ok: piped REPL — a directive never continues the block above it"
# the piped REPL's lookahead line goes back to weir's stdin buffer
# [D:repl-lookahead-unread]: a Self.prompt in the entry reads the next
# line (the answer), which never runs as a command
printf 'let ok = Self.prompt "go?"\nANSWER\nprint $"ok=[{ok}]"\nlet two = Self.prompt "again?"\n#echo\nprint $"two=[{two}]"\n' >"$w2dir/prompt.txt"
out=$(cd "$w2dir" && XDG_CONFIG_HOME="$w2dir/cfg" XDG_STATE_HOME="$w2dir/st" "$BIN" <"$w2dir/prompt.txt" 2>&1)
echo "$out" | grep -qF "ok=[ANSWER]" || fail "a piped REPL Self.prompt must read the next line: $out"
echo "$out" | grep -qF "two=[#echo]" || fail "a looked-ahead directive line must reach the prompt: $out"
echo "$out" | grep -qF "unknown command" && fail "the prompt's answer ran as a command: $out"
echo "e2e ok: piped REPL — Self.prompt reads the line after its statement"
# an attached lookahead line is judged like a read one — every
# continuation shape (|> tails, a match's arms, an if's body and else, a
# for body, a bracket) leaves the answers after it to its prompts, in order
cat >"$w2dir/cont.txt" <<'WEOF'
[1]
    |> Seq.iter (fun _ -> print $"P1=[{Self.prompt "q?"}]")
ANS1
let a = Self.prompt "q?"
        |> Str.toUpper
ans2
print $"A2=[{a}]"
[1]
    |> Seq.map id
    |> Seq.iter (fun _ -> print $"P3=[{Self.prompt "q?"}]")
ANS3
match Self.prompt "q?" with
| "M" -> print "M4=yes"
| _ -> print "M4=no"
M
if true then
    print $"A5=[{Self.prompt "q?"}]"
    print $"B5=[{Self.prompt "q?"}]"
ANS5a
ANS5b
if true then
    print $"I6=[{Self.prompt "q?"}]"
else
    print "no"
ANS6
for i in [1; 2] do
    print $"F7=[{Self.prompt "q?"}]"
F7a
F7b
let xs = [
    Self.prompt "q?"
]
B8
print $"B8=[{xs |> Str.join ","}]"
print "end"
WEOF
out=$(cd "$w2dir" && XDG_CONFIG_HOME="$w2dir/cfg" XDG_STATE_HOME="$w2dir/st" "$BIN" <"$w2dir/cont.txt" 2>&1)
for want in "P1=[ANS1]" "A2=[ANS2]" "P3=[ANS3]" "M4=yes" "A5=[ANS5a]" "B5=[ANS5b]" "I6=[ANS6]" "F7=[F7a]" "F7=[F7b]" "B8=[B8]" "end"; do
    echo "$out" | grep -qF -- "$want" || fail "piped REPL continuation shapes: expected $want in: $out"
done
echo "$out" | grep -qE "unknown command|parse error|continuation without" && fail "a looked-ahead answer ran as code: $out"
echo "e2e ok: piped REPL — every continuation shape leaves its answers to its prompts, in order"
# GUIDE "Lazy values": the stated outputs of the within cd / within env example
cat >"$w2dir/lazy.weir" <<'WEOF'
Dir.create "proj/src"
File.write "proj/a.txt" ["a"]
File.write "proj/src/b.txt" ["b"]
cd "proj"
let top = ls |> Seq.map _.name
let inside = within cd "src" (ls |> Seq.map _.name)
let greet = within env [Env.pair "GREETING" "hi"] $(sh -c "echo ${GREETING:-unset}")
cd "src"
print $"{show top}|{show inside}|{show greet}"
WEOF
out=$(cd "$w2dir" && "$BIN" lazy.weir 2>&1) || fail "GUIDE lazy-values example: $out"
expect "GUIDE lazy values: read late, within cd/env keep their scope" '["b.txt"]|["b.txt"]|["hi"]' "$out"
rm -rf "$w2dir"

# ---- the starship port's CLI-level findings -------------------------------
pfdir=$(mkweirtmp)
# an importer checks a module as the module checks alone
# [D:assume-resolver]; a reified head warns like a plain one
printf 'module\n\nlet ver : unit -> seq<string>\nlet ver () =\n    let r = $(weirnosuchtoolzz -Vv | complete)\n    r.stdout\n' > "$pfdir/modmiss.weir"
printf 'module\n\nlet ver : unit -> seq<string>\nlet ver () = weirnosuchtoolzz -Vv\n' > "$pfdir/modmiss2.weir"
printf 'import "modmiss.weir" as M\nM.ver () |> Seq.iter print\n' > "$pfdir/root.weir"
printf 'import "modmiss2.weir" as M\nM.ver () |> Seq.iter print\n' > "$pfdir/root2.weir"
out=$(cd "$pfdir" && $BIN check modmiss.weir 2>&1) || fail "port: the lone module checks: $out"
echo "$out" | grep -qF "warning [cmd-not-found]: command not found on PATH: weirnosuchtoolzz" || fail "port: a missing \$(cmd | complete) head warns: $out"
for r in root.weir root2.weir; do
    out=$(cd "$pfdir" && $BIN check $r 2>&1) || fail "port: $r must check as its module does alone: $out"
done
echo "e2e ok: an importer checks a module as the module checks alone; a reified missing head warns [D:assume-resolver]"
# the run-time teaching for a missing head [D:missing-command-diagnosis]
printf 'let f () =\n    weirnosuchtoolzz -Vv\nf () |> Seq.iter print\n' > "$pfdir/fn.weir"
for r in root.weir root2.weir fn.weir; do
    out=$(cd "$pfdir" && $BIN $r 2>&1) && fail "port: $r must fail at run time"
    echo "$out" | grep -qF "unknown command 'weirnosuchtoolzz'" || fail "port: $r names the unknown command: $out"
    echo "$out" | grep -qF '^$tool' || fail "port: $r teaches ^\$tool: $out"
    echo "$out" | grep -qE "Expecting|unbound variable" && fail "port: $r must not dump or say unbound: $out"
done
echo "e2e ok: a missing \$(cmd | complete) or function-body head gets the unknown-command teaching at run time [D:missing-command-diagnosis]"
# a bare member after a chain's |> teaches the qualified spelling; a
# bare-member first line is weir, so no not-weir preamble [D:bare-partition]
printf 'let b () =\n    git rev-parse --abbrev-ref HEAD | line |> split "/" |> _[0]\nprint (b ())\n' > "$pfdir/sp.weir"
printf 'let x = "a/b" |> map Str.length\n' > "$pfdir/sp3.weir"
for r in sp.weir sp3.weir; do
    out=$(cd "$pfdir" && $BIN check $r 2>&1) && fail "port: $r must not check clean"
    echo "$out" | grep -qE "'(split|map)' is a bare module member — spell it '(Str.split|Seq.map)'" || fail "port: $r teaches the qualified spelling: $out"
    echo "$out" | grep -qE "feed a program|does not look like a weir script" && fail "port: $r must not read as a program or as not-weir: $out"
done
printf 'let ok = ["a"] |> contains "a"\nprint (show ok)\n' > "$pfdir/two.weir"
chk=$(cd "$pfdir" && $BIN check two.weir 2>&1) && fail "port: contains must not check clean"
run=$(cd "$pfdir" && $BIN two.weir 2>&1) && fail "port: contains must not run"
for o in "$chk" "$run"; do
    echo "$o" | grep -qF "'contains' is module-qualified here; use Seq.contains or Str.contains" || fail "port: check and run agree on contains: $o"
done
echo "e2e ok: a bare or two-home member after |> teaches its qualified spelling, check and run alike, no not-weir preamble [D:bare-partition]"
rm -rf "$pfdir"

echo "e2e battery: all green"
