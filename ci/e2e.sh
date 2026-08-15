#!/usr/bin/env bash

# KNOWN NOISE, not a failure (Windows, round 31's run): bash lines like
#   dofork: child -1 - forked process N died unexpectedly ... 0xC0000142
#   ./ci/e2e.sh: fork: retry: Resource temporarily unavailable
# are the msys2/Git-Bash fork flake — the forked bash CHILD fails DLL
# init (cygwin address-space collision on loaded runners), and bash
# retries with backoff (0/1/3/7s) and recovers. Every battery-owned
# background job is killed AND reaped at block end, so a live-leak
# diagnosis is wrong by construction; only a fork that exhausts all
# retries fails the run, and that is the runner's weather, not ours.

# temp dirs weir can SEE on every platform: Git Bash's /tmp is
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
# the TLS twin polls TCP-ACCEPT only (curl's TLS stack disagrees with
# the python server on some platforms; readiness needs the LISTENER,
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
# an UNGUARDED nonzero command under set -e used to kill the battery
# SILENTLY (round 33 — the run ended after a green line with no FAIL at
# all); the ERR trap names its own line and command. Deliberately NO
# `set -E`: with it the trap reaches into command-substitution
# subshells, where several pins run commands whose failure is the
# point ($(cmd; echo rc=$?)) — top level is where the silent class
# lives, and top level is what fires it.
trap 'echo "e2e FAIL: unguarded command failed at line $LINENO: $BASH_COMMAND" >&2' ERR

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"

# POSIX-only harnesses (os.fork, pty, zombies) get STATED skips on
# Windows — a skip echoes its reason, never silence
IS_WINDOWS=0
case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) IS_WINDOWS=1 ;; esac
# python subprocess must NOT resolve `bash` itself on Windows — the
# native PATH finds System32's WSL stub first (the sh-never-bash class,
# python axis): hand the harnesses THIS bash, native-form
if [ "$IS_WINDOWS" = "1" ]; then
    WEIR_BASH=$(cygpath -m "$(command -v bash)")
    export WEIR_BASH
fi

# a PATH ENTRY must be POSIX-form: mkweirtmp's mixed (C:/...) spelling
# and a Windows-form dirname both carry a drive colon that reads as a
# PATH separator — round 5's class, every prefix site (identity on POSIX)
pathEntry() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -u "$1"; else printf '%s\n' "$1"; fi
}
# a fake PATH binary every platform can SPAWN — PER-PLATFORM, never
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

# HARD stale-binary gate [D:masking-mechanized] — the ONE shared gate
# (stamp == HEAD, no .fs newer than the binary), so stale results are
# impossible rather than catchable.
"$(dirname "$0")/check-fresh.sh" "$BIN"

fail() {
    echo "e2e FAIL: $1" >&2
    exit 1
}

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

# ---- pins-walk: three runtime messages only e2e can see [D:pins-walk] ------
pwdir=$(mkweirtmp)
# THE set-e analogue's WORDS were never asserted (the raise itself was)
cat > "$pwdir/x.weir" <<'WEOF'
sh -c "exit 3"
print "unreached"
WEOF
out=$($BIN "$pwdir/x.weir" 2>&1) && fail "nonzero exit must raise" || true
echo "$out" | grep -qF "command failed with exit code 3" || fail "the exit-raise names code+command: $out"
# the '>' redirect WARNING names the File.write spelling
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

# macOS ships no GNU timeout — a background-watchdog polyfill (same
# contract: run the command, kill it after N seconds, nonzero on kill)
if ! command -v timeout >/dev/null 2>&1; then
    timeout() {
        local secs=$1
        shift
        "$@" &
        local pid=$!
        (
            sleep "$secs"
            kill -9 "$pid" 2>/dev/null || true
        ) &
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

out=$($BIN -e 'ls |> where (fun f -> f.bytes > 1MiB) |> first 5' 2>&1); rc=$?
[ $rc -eq 0 ] || fail "flagship pipeline must run measure-free: $out"
echo "e2e ok: flagship pipeline, bare bytes"

if $BIN -e '1<mb> + 2<mb>' 2>/dev/null; then
    fail "old measure literal should be rejected"
fi
errout=$($BIN -e '1<mb>' 2>&1 || true)
echo "$errout" | grep -qF "units of measure are not supported" || fail "transition message missing: $errout"
echo "e2e ok: measure transition error"

# the witness is a WEIR child: native on every platform, so it cannot
# glob for us — an MSYS echo.exe expands args from a native parent,
# convicting the witness, not weir
awdir=$(mkweirtmp)
printf 'Self.args |> print\n' > "$awdir/args.weir"
out=$(PATH="$BINDIR:$PATH" $BIN -e "\$(weir \"$awdir/args.weir\" \"*\")")
expect "argv stays literal" '["*"]' "$out"

out=$($BIN -e 'echo hi (40 + 2) |> first 1')
expect "command mode with splice" '["hi 42"]' "$out"

out=$($BIN -e '[1..5] |> Seq.length')
expect "range literal on the AOT binary" "5 : int" "$out"

out=$(timeout 5 $BIN -e '[1..1000000] |> first 3') || fail "huge range under first must terminate (laziness)"
expect "ranges are lazy generators" '[1; 2; 3]' "$out"

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

out=$($BIN -e 'echo $"n={40 + 2}" |> first 1')
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

out=$($BIN -e 'yes hi | cat |> first 2')
expect "external pipes into external stdin" '["hi"; "hi"]' "$out"

# /etc/hosts, not /etc/hostname: the latter doesn't exist on macOS
# (grep exit 2 = error, not the no-match 1 this pin reifies)
out=$($BIN -e 'grep nomatch /etc/hosts | complete |> _.exitCode')
expect "complete reifies nonzero exit as data" "1 : int" "$out"

# sh, NEVER bash: a native weir resolving `bash` on a Windows runner
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
names |> Seq.first 1 |> print
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

cat > "$scriptdir/loose.weir" <<'WEOF'
#loose
[2; 1] |> where (fun x -> x > 0) |> map (fun x -> x * 3) |> first 1 |> sum |> print
WEOF
$BIN fmt --qualify "$scriptdir/loose.weir" 2>/dev/null
out=$($BIN "$scriptdir/loose.weir")
expect "fmt --qualify graduates loose to strict-clean" "6" "$out"
grep -q "Seq.map" "$scriptdir/loose.weir" || fail "fmt did not qualify"
if grep -q "#loose" "$scriptdir/loose.weir"; then fail "fmt left the #loose directive"; fi
echo "e2e ok: fmt --qualify roundtrip"
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
    |> Seq.first 1

rows |> Seq.iter (fun f -> print (show f))
WEOF
out=$(cd "$scriptdir" && $BIN show.weir)
echo "$out" | grep -qF "bytes = " || fail "show must render the builtin row: $out"
echo "e2e ok: show renders typed rows on the AOT binary"

start=$(now_ms)
$BIN -e '[1; 2; 3; 4] |> Seq.piter (fun n -> if ($(sh -c "sleep 0.3" | complete)).exitCode > 99 then print "never")' >/dev/null 2>&1 || fail "piter probe failed"
elapsed_ms=$(($(now_ms) - start))
[ "$elapsed_ms" -lt 900 ] || fail "piter must run workers in parallel (4x300ms took ${elapsed_ms}ms)"
echo "e2e ok: piter parallelism (4x300ms in ${elapsed_ms}ms)"

# temp-dir fixture, leaf pins: /tmp and /etc are POSIX-isms a NATIVE
# weir resolves drive-relative on Windows (D:\tmp), and pwd's separator
# is the platform's — assert the LEAF, never the path
forkdir=$(mkweirtmp)
mkdir -p "$forkdir/home" "$forkdir/wa" "$forkdir/wb"
cat > "$forkdir/fork.weir" <<'WEOF'
let a = cd "FORKMARK/home"

let workers =
    ["FORKMARK/wa"; "FORKMARK/wb"]
    |> Seq.pmap (fun d ->
        let x = cd d
        pwd |> Seq.head)

let ws = workers |> Seq.force
print (if ws |> Seq.head |> Str.endsWith "wa" then "w1-ok" else "w1-wrong")
print (if ws |> Seq.last |> Str.endsWith "wb" then "w2-ok" else "w2-wrong")
print (if pwd |> Seq.head |> Str.endsWith "home" then "parent-held" else "parent-moved")
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

let steps =
    if go then
        !(sh -c "echo one")
        !(sh -c "echo two")
        print "three"

let skipped =
    if 1 > 2 then
        !(sh -c "echo never")
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
    !(sh -c "echo eff-one")
    !(sh -c "echo eff-two")

if 1 > 2 then
    !(sh -c "echo never-a")
    !(sh -c "echo never-b")

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

if $BIN -e '!(weir-no-such-program-zz)' 2>/dev/null; then
    fail "typo'd program inside a sigil must fail at check time"
fi
echo "e2e ok: sigil heads resolve at check time"
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

# the retirement TEACHES [D:district-retirement]
cat > "$distdir/old.weir" <<'WEOF'
if go then !
    sh -c "echo x"
WEOF
rerr=$($BIN check "$distdir/old.weir" 2>&1) && fail "the retired spelling must error" || true
echo "$rerr" | grep -qF "district retired" || fail "retirement teaching: $rerr"
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

let cfg = Env.load Config
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

# the overlay stack resolves BEFORE the enum conversion: a dotenv file
# feeds the child env, the child's Env.load sees the layered value
cat > "$envdir/lvl.env" <<'WEOF'
WEIR_E2E_LVL=info
WEOF
cat > "$envdir/outer.weir" <<'WEOF'
let e = Env.fromFile "lvl.env"
!e(weir enum.weir)
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
# pattern message says "scrutinee". Pin the FRAGMENTS (FParsec wraps).
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

# unreachable arms are HARD errors, coverage's dual (2026-07-21: the
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
rm -rf "$fmtdir"
echo "e2e ok: fmt canonicalizes, --check gates, output runs"

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

rm -rf "$scriptdir"

out=$($BIN -e 'print "visible"')
[ "$out" = "visible" ] || fail "print in -e must emit exactly the line, no unit trailer (got: $out)"
echo "e2e ok: unit is invisible in -e"

out=$(printf 'print "hi"\nlet u = ()\nu\n#quit\n' | $BIN)
echo "$out" | grep -qF "hi" || fail "REPL print lost its output"
if echo "$out" | grep -qF "() : unit"; then fail "unit leaked into REPL display"; fi
echo "e2e ok: unit is invisible in the REPL"

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
# captured output is LF on EVERY platform [D:lf-output] — Windows
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

# a near-miss BINDING bridges the verdict split: check's command
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
    # tmLanguage repository keys — add to BOTH or neither.
    # LIMITATION on record: this proves rule PRESENCE, not regex
    # semantics — a wrong skip/end inside a matching rule name is
    # invisible here; per-kind escape laws are verified by eye on the
    # showcase (its @-verbatim and triple-quote lines are the canary,
    # and a stale INSTALLED syntax copy shows there too)
    # the drift rule, AMENDED for engine capability [D:micro-exempt]: add
    # to both or neither, UNLESS a grammar's engine cannot express it —
    # then the shortfall is STATED in that grammar's header
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

    # the grammar MANIFEST [D:ts-split]: the generated contract the
    # split tree-sitter repo checks itself against at its pinned ref —
    # the currency gate here proves the committed file matches the
    # source, so the cross-repo half can trust it
    python3 "$(dirname "$0")/grammar-manifest.py" --check || fail "grammar manifest stale"

    # within-kind inventory [D:within-kinds]: the kinds are a CLOSED SET
    # in ONE table (src/Weir/Ast.fs withinKinds); the IN-REPO grammars
    # hard-code the same set by necessity — this pins them together
    # (the tree-sitter third checks itself in ITS repo, against the
    # manifest [D:ts-split])
    python3 - "$(dirname "$0")/.." <<'PYWK' || fail "within-kind inventories diverge from Ast.withinKinds"
import re, sys
root = sys.argv[1]
ast = open(f"{root}/src/Weir/Ast.fs").read()
# the table block: Name = "..." entries inside withinKinds
tbl_block = re.search(r"let withinKinds[^=]*=\n(.*?)\n\n", ast, re.S).group(1)
table = set(re.findall(r'Name = "(\w+)"', tbl_block))
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
    echo "e2e ok: within-kind inventory (Ast.withinKinds == micro == tmLanguage; tree-sitter via the manifest)"

    # adapter inventory [D:form-word-hover]: the from/to adapters are a
    # CLOSED SET whose SOURCE is the builtinDocs keys (`from X`/`to X`) —
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
    echo "e2e ok: adapter inventory (builtinDocs == micro == tmLanguage; tree-sitter via the manifest)"

    # the Zed highlight drift guard RETIRED with the split [D:ts-split]:
    # the canonical queries live in weir-shell/tree-sitter-weir now, so
    # this repo cannot diff against them. The replacement is a RITUAL,
    # stated where the guard stood: bumping the Zed grammar rev and
    # refreshing languages/weir/highlights.scm are ONE motion — the
    # extension update copies the queries from the SAME grammar commit
    # the rev pins. A CI re-join needs the extension in the grammar's
    # repo or its own; deferred with the split's release-gate question.

    # --- REPL line editor under a pty (2026-07-21) ---------------------
    if [ "$IS_WINDOWS" = "1" ]; then
        echo "e2e SKIP: the six pty REPL harnesses — python has no pty on Windows (the REPL itself is exercised by the Windows hand-run checklist)"
    else
    python3 "$(dirname "$0")/../tests/repl/repl-wordnav.py" "$BIN" || fail "repl word navigation"
    echo "e2e ok: repl Ctrl+Left/Right word navigation"

    python3 "$(dirname "$0")/../tests/repl/repl-color.py" "$BIN" || fail "repl coloring"
    echo "e2e ok: repl lexical coloring, head verdicts, NO_COLOR"

    python3 "$(dirname "$0")/../tests/repl/repl-quality.py" "$BIN" || fail "repl quality (history/Ctrl+R)"
    echo "e2e ok: repl history (XDG/dedup/0600), Ctrl+R fzf-stub + fallback"

    python3 "$(dirname "$0")/../tests/repl/waiting-indicator.py" "$BIN" || fail "waiting indicator"
    echo "e2e ok: repl waiting indicator (grace, erase, fast/piped/child-owned silent)"

    python3 "$(dirname "$0")/../tests/repl/cooked-trap.py" "$BIN" || fail "cooked trap / echo-once"
    echo "e2e ok: repl cooked-trap (one child run per echo, Enter survives a slow child)"

    python3 "$(dirname "$0")/../tests/repl/repl-directives.py" "$BIN" || fail "repl directives"
    echo "e2e ok: repl directives (#help x3, #quit, :q retired, comments no-op, #echo cap)"

    python3 "$(dirname "$0")/../tests/repl/repl-multiline.py" "$BIN" || fail "repl multiline editor"
    fi
    echo "e2e ok: repl 2D buffer, Enter-completeness, whole-entry history, wrap at two widths"
else
    # no silent caps: name what was skipped
    echo "e2e SKIP: python3 absent — lsp + repl pty probes NOT run" >&2
fi

# --- weir check [--json] (2026-07-21, LSP chain 2/3) -------------------

ckdir=$(mkweirtmp)

cat > "$ckdir/multi.weir" <<'WEOF'
let Foo = 1

nats == nats
WEOF
out=$($BIN check "$ckdir/multi.weir") && fail "check must exit 1 on errors" || true
echo "$out" | grep -qF "[casing-law]" || fail "casing code missing: $out"
echo "$out" | grep -qF "[eq]" || fail "second independent error missing (recovery): $out"
echo "e2e ok: weir check reports ALL errors, located and coded"

out=$($BIN check --json "$ckdir/multi.weir" || true)
echo "$out" | grep -qF '"code":"casing-law"' || fail "json code field: $out"
echo "$out" | grep -qF '"line":1' || fail "json line field: $out"
echo "e2e ok: weir check --json carries file/line/col/code"

# cmd-not-found squiggles the WHOLE head word [PLAN-diagnostics-arc A4]
printf 'nosuchzz foo bar\n' > "$ckdir/word.weir"
out=$($BIN check --json "$ckdir/word.weir" || true)
echo "$out" | grep -qF '"code":"cmd-not-found"' || fail "cmd-not-found json: $out"
echo "$out" | grep -qF '"endCol":9' || fail "full-word endCol (nosuchzz = cols 1-8): $out"
echo "e2e ok: cmd-not-found spans the full head word"

# row provenance: cross-statement no-field errors point at the ACCESS,
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

# FLIPPED by [D:interior-arming]: a command-first body now CHECKS —
# the interior command ARMS as an effect instead of seq-unit-erroring.
# The at-the-head/no-EOF-dump quality property moves to a NON-command
# fixture (the statement rule is unchanged for those); the old
# rejection this pin asserted is the rule's named flip.
cat > "$ckdir/sib.weir" <<'WEOF'
let f t =
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

# raise timings [D:interior-arming]: ARMED raises immediately (the tail
# never runs); CAPTURE raises only at force (existing law, re-pinned)
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
let x = sh -c "exit 3"
print "bound-fine"
WEOF
out=$(cd "$iadir" && $BIN cap.weir 2>&1) || fail "an unforced capture must not raise: $out"
echo "$out" | grep -qF "bound-fine" || fail "capture binds without raising"
echo "e2e ok: armed raises immediately; capture raises at force (both timings)"

# a MULTI-LINE for body needs no district now [D:interior-arming] —
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

# statement position: a NON-command non-unit block still hits the
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
        print (pwd |> Seq.head)
print (pwd |> Seq.head)
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
# resolved-ness = the PARENT rode into the message; the leaf pair with
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
        pwd |> Seq.head
    p)
outs |> Seq.iter print
print (pwd |> Seq.head)
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

cat > "$widir/envval.weir" <<'WEOF'
let vars = [Env.pair "WQ" "carried"]
let got = within env vars
    $(sh -c "echo $WQ") |> Seq.head
print got
WEOF
vout=$( cd "$widir" && $BIN envval.weir )
[ "$vout" = "carried" ] || fail "env value case captures under the overlay: $vout"
echo "e2e ok: within env expression position yields a captured value"

# ---- filesystem members [D:fs-members] -------------------------------------
fsdir=$(mkweirtmp)
cat > "$fsdir/glob.weir" <<'WEOF'
let d = Path.newTempDir ()
Dir.create $"{d}/sub"
["1"] |> File.write $"{d}/a.txt"
["2"] |> File.write $"{d}/sub/b.txt"
Path.glob $"{d}/**/*.txt" |> Seq.force |> Seq.iter File.delete
print $"{Path.glob $"{d}/**/*.txt" |> Seq.length}"
Dir.deleteAll d
WEOF
out=$($BIN "$fsdir/glob.weir") || fail "the glob-delete composition must run"
[ "$out" = "0" ] || fail "glob a tree, delete the results: $out"
echo "e2e ok: Path.glob composes with File.delete (the obvious composition)"

# within tmp DOUBLE-DELETE: a block that deleteAlls its own binder must
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

# bare-pipe caret anchors ON the '|', not the space after [PLAN-anchor-before-read]
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
printf 'let x = if true then 1 else 2\n' > "$ckdir/dom3.weir"
$BIN check "$ckdir/dom3.weir" >/dev/null 2>&1 || fail "keyword fall-through must still parse (if)"
echo "e2e ok: teaching fatals dominate; the reserved-word gate stays fall-through-safe"

# anchor residue A+B: foldChain reifier anchors on the marker; keyword in
# param/field slots dominates [PLAN-open-findings]
printf 'git | grep x | complete\n' > "$ckdir/fc.weir"
out=$($BIN check --json "$ckdir/fc.weir" || true)
echo "$out" | grep -qF '"line":1,"col":16' || fail "foldChain anchors on the marker: $out"
echo "$out" | grep -qF "must directly follow a single external command" || fail "reifier teaching present: $out"
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

# --- runner missing-command diagnosis (2026-07-21: FParsec's primary
# error buried the real cause and showed assembler-joined text) ---

mdir=$(mkweirtmp)
printf 'not-a-real-tool-xyz --flag\n' > "$mdir/m.weir"
rc=0; errout=$($BIN "$mdir/m.weir" 2>&1) || rc=$?
[ $rc -eq 1 ] || fail "missing command must fail the runner (rc=$rc)"
echo "$errout" | grep -qF "unknown command 'not-a-real-tool-xyz'" || fail "diagnosis must name the command: $errout"
echo "$errout" | grep -qF "Expecting:" && fail "FParsec dump must not appear for missing commands: $errout"
echo "e2e ok: runner names the missing command, no parser dump"

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

# type errors show the source line with the SPAN underlined, same
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

# --- every repo script must CHECK (2026-07-21: test-counts.weir had
# been broken since the pairwise re-type and nothing noticed — scripts
# rot silently unless gated; cmd-not-found warnings are fine, errors
# are not) ---

for scr in "$(dirname "$0")"/../examples/*.weir "$(dirname "$0")"/../tools/*.weir; do
    rc=0
    out=$($BIN check "$scr" 2>&1) || rc=$?
    [ $rc -eq 0 ] || fail "repo script no longer checks: $scr — $out"
done
echo "e2e ok: all repo scripts check clean"

# the showcase is a COMPOSITION TEST disguised as a document — a tour
# that can be invalid without failing a build is not that (the schema
# live-check found three real errors in it, unprompted [D:add-validates]).
# Child weirs resolve via PATH: the $BIN dir prefix, as everywhere.
scout=$(PATH="$BINDIR:$PATH" $BIN "$(dirname "$0")/../examples/showcase.weir" --tag ci 2>/dev/null) || fail "the showcase must RUN green"
echo "$scout" | grep -qF "showcase complete" || fail "the showcase completes: ${scout: -200}"
echo "$scout" | grep -qF 'weir.dev/switch: "on"' || fail "the district auto-quote demo holds"
echo "e2e ok: the showcase runs end to end (the tour is a build gate now)"

# --- the casing law (2026-07-21) ---------------------------------------

errout=$($BIN -e 'let Foo = 1 in Foo' 2>&1 || true)
echo "$errout" | grep -qF "binding names start lowercase" || fail "casing law must reject at the binder: $errout"
echo "e2e ok: the casing law (lowercase binds) on the AOT binary"

# param-ful command RHS (PLAN-paramful-rhs): the shadowing law —
# this pin was written FAILING against the guard-dropped prototype
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
!author(sh -c "echo $GIT_AUTHOR_NAME/$GIT_AUTHOR_EMAIL")
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

# record update (PLAN-record-update): the corpus snippets ARE the e2e
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
# weir's path outputs are the PLATFORM'S (the platformPath law) — the
# unit pin went platform-aware in windows-s2; this one never had
case "$out" in
    *'"a/b/c"'* | *'"a\\b\\c"'*) echo "e2e ok: Path members compose (platform separators)" ;;
    *) fail "Path members compose: $out" ;;
esac

# prefix minus + sortByDescending (2026-07-21, loc.weir friction)
out=$($BIN -e '2 * -3')
expect "prefix minus at operand position" "-6 : int" "$out"
out=$($BIN -e '[1; 3; 2] |> Seq.sortByDescending (fun x -> x) |> Seq.force')
expect "sortByDescending orders down" "[3; 2; 1]" "$out"

# the squiggle sits ON the name, not the RHS (user report, 2026-07-21)
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

# THE HEADLINE: sortBy-on-record-key is rejected at CHECK time — the
# effect before it must NOT run (check-first proves the runtime check
# is gone, replaced by static Ord)
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

# A x E: a blank line ends a compound body — the tail runs UNCONDITIONALLY
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

# fixture backfill: record continuation HEADED (inside a compound body)
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

# fixture backfill: record continuation NESTED (record in record, multi-line)
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

# --- env sugar layers 1+2 (2026-07-20): $e(...) / !e(...) and the !e district

sdir=$(mkweirtmp)
printf 'MARK=layered\n' > "$sdir/s.env"

cat > "$sdir/sigil.weir" <<'WEOF'
let e = Env.fromFile "s.env"

!e(sh -c "echo effect: $MARK")

let got = $e(sh -c "echo cap: $MARK") |> Seq.head
print got

let r = $e(sh -c "exit 7" | complete)
print $"complete-env exit {r.exitCode}"

let tag = "spliced"
!e(sh -c $"echo {tag}: $MARK")
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

!targetEnv(sh -c "echo \"AZ($AZURE_SUBSCRIPTION_ID|$AZURE_DEFAULTS_GROUP|$OVERRIDE|$INHERITED)\"")
WEOF
out=$(cd "$edir" && OVERRIDE=from-parent INHERITED=passed-through $BIN deploy.weir)
expect "bicep shape: overlay sets, overrides, and inherits" "AZ(sub-web|rg web|from-file|passed-through)" "$out"

# parent isolation: the overlay never leaks into the weir process
cat > "$edir/iso.weir" <<'WEOF'
let vars = Env.fromFile "target.env"

!vars(sh -c "true")

print (Env.get "AZURE_SUBSCRIPTION_ID" |> Option.defaultValue "(clean)")
WEOF
out=$(cd "$edir" && $BIN iso.weir)
expect "child-env never leaks into the parent session" "(clean)" "$out"

printf 'EMPTYFILE=x\n' > "$edir/one.env"

# empty-string value: the documented removal workaround (via the env sigil)
cat > "$edir/empty.weir" <<'WEOF'
let vars = Env.fromFile "blank.env"

!vars(sh -c "echo [$BLANKED]")
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

# FLIPPED 2026-07-23 [D:blank-in-brackets]: this pinned the AT-BLANK error;
# blanks are transparent now — a gapped-but-closed record RUNS, and the
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

# the recursive field law [D:recursive-fields]: nested records and seq
# fields read from REAL response shapes — the World Bank receipt
# (verbatim from the live sitting that forced the law; its Map-keyed
# `documents` stays untypable pending Map and is IGNORED as an
# undeclared field), and kubectl's List/items shape for arrays-inside-
# objects. Round-trip pinned.
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
let rt = [l] |> to json |> Seq.head
let l2 = [rt] |> from json L
print (if (l2.items |> Seq.length) == 2 && l2.kind == l.kind then "round-trip-ok" else "ROUND-TRIP-BROKE")
WEOF
out=$(cd "$rfdir" && $BIN rf.weir)
expect "recursive fields: the receipt's non-Map parts read" "10/593" "$out"
expect "recursive fields: three deep (the entityid)" "000334955300064" "$out"
expect "recursive fields: seq of records inside an object" "kube-dns" "$out"
expect "recursive fields: to json round-trips the nesting" "round-trip-ok" "$out"
rm -rf "$rfdir"

# anonymous record types [D:anon-records]: the shape inline in the
# adapter slot — `_.field` checks, seq<> composes, the shape persists
# across statements, and a declared record stays a DIFFERENT type
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

# Option<scalar> at the JSON boundary [D:json-option]: present -> Some,
# missing/null -> None, and to json OMITS the None key so it roundtrips.
cat > "$adir/json-option.weir" <<'WEOF'
type R = { name: string; age: Option<int> }
let rows = ["{\"name\":\"a\",\"age\":5}"; "{\"name\":\"b\"}"; "{\"name\":\"c\",\"age\":null}"]
rows |> from jsonl R |> to json |> Seq.iter print
WEOF
out=$($BIN "$adir/json-option.weir")
expect "json Option: Some writes, None (missing or null) omits the key" \
'{"age":5,"name":"a"}
{"name":"b"}
{"name":"c"}' "$out"

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
echo "$errout" | grep -qF "attributes attach to record fields" || fail "non-field position names the scope: $errout"
echo "e2e ok: attribute positions outside record fields reject by name"

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

# the argv-order oracle [D:argv-rules]: the aggregated error's ORDER is
# the contract — scan problems in TOKEN order, then fills in
# DECLARATION order (shared tier before payload). Exact-string pins,
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
let cli = Args.load Cli
print "ok"
WEOF
got=$($BIN "$odir/sh.weir" --level x push --depth y 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: --level is not an int ('x'); --depth is not an int ('y'); missing required flag '--level'; missing required flag '--remote'; missing required flag '--depth'" ] \
    || fail "shared-twin two-tier order oracle: $got"
got=$($BIN "$odir/sh.weir" push 2>&1 | tail -1 | sed 's/^.*error: //' || true)
[ "$got" = "Args.load Cli: missing required flag '--level'; missing required flag '--remote'; missing required flag '--depth'" ] \
    || fail "shared-twin fill order oracle: $got"
echo "e2e ok: shared twin: token-order scan crosses tiers; fills shared-then-payload (exact)"
rm -rf "$odir"

out=$($BIN "$tadir/cli.weir" --bogus --help); rc=$?
[ "$rc" -eq 0 ] || fail "--help must exit 0 (got $rc)"
echo "$out" | grep -qF -- "-c, --clean" || fail "help shows derived short truth: $out"
echo "$out" | grep -qF "clean first" || fail "help shows the /// first line: $out"
echo "$out" | grep -qF -- "--env <string>" || fail "help shows valued flags: $out"
echo "$out" | grep -qF "required" || fail "help shows requiredness: $out"
echo "e2e ok: --help derives usage (short truth + /// doc) BEFORE validation, exit 0"

# multi-line ///: --help shows ONLY the first line; the rest is hover-only [D:doc-help]
cat > "$tadir/multi.weir" <<'WEOF'
type Cli = {
    /// terse help line
    /// a second line, hover-only
    verbose: bool
}
let c = Args.load Cli
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

# [<Positional>] DROPPED [D:drop-positional] — now an unknown attribute
# Positional RETURNED for signatures [D:command-signatures] — registered
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

# FLIPPED 2026-07-23 [D:blank-in-brackets]
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
# forced echoes in full — Seq.force means something at the prompt now
out=$(printf 'let xs = [1..100]\nxs\n' | $BIN 2>&1)
echo "$out" | grep -qF "10; …] : seq<int> (first 10 of an unforced seq — Seq.force to echo everything)" || fail "the unforced echo names the honest lever: $out"
out=$(printf '[1..12] |> Seq.force\n' | $BIN 2>&1)
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
# (ROOT resolved BEFORE any cd: $0 is relative to the invocation dir)
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
    z

print (f "x")
WEOF
out=$(PATH="$(pathEntry "$bldir/bin"):$PATH" $BIN "$bldir/force.weir")
expect "^ still reaches the PATH binary from a block RHS" "SPAWNED" "$out"

mkFakeBin "$bldir/bin" function FN-BINARY
out=$(PATH="$(pathEntry "$bldir/bin"):$PATH" $BIN -e '^function' 2>&1)
expect "^function reaches a PATH binary (reservation does not block force)" "FN-BINARY" "$out"

# the reservation retired into the FEATURE [D:function-keyword]: the
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
        !(echo fetching $tag)
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

# collect-then-raise spans both tiers in ONE error
errout=$($BIN "$sfdir/cli.weir" search 2>&1) && fail "missing payload flag must reject"
echo "$errout" | grep -qF "missing required flag '--query'" || fail "cross-tier collection: $errout"
echo "e2e ok: one boundary error across tiers"

# two-tier help + case-scoped help
out=$($BIN "$sfdir/cli.weir" --help)
echo "$out" | grep -qF "global options:" || fail "two-tier help missing global section: $out"
echo "$out" | grep -qF "commands:" || fail "two-tier help missing commands: $out"
out=$($BIN "$sfdir/cli.weir" run --help)
echo "$out" | grep -qF "usage: run [flags]" || fail "case-scoped help: $out"
echo "$out" | grep -qF -- "-q, --quiet" || fail "scoped help shows the scope-derived short: $out"
echo "e2e ok: two-tier and case-scoped help"

# declaration collisions reject at CHECK (both routes)
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

# orFail STREAMS (behavioral pin: the child's stdout reaches the user)
cat > "$rfdir/of.weir" <<'WEOF'
sh -c "echo build-step-one; echo build-step-two; exit 4" | orFail "build broke"
WEOF
out=$($BIN "$rfdir/of.weir" 2>&1) && fail "orFail must raise on nonzero"
echo "$out" | grep -qF "build-step-one" || fail "orFail must stream stdout: $out"
echo "$out" | grep -qF "build broke (exit 4)" || fail "orFail message+code: $out"
echo "e2e ok: orFail streams and raises with the message"

# bare !(cmd) ≡ cmd | orFail "<msg>": byte-identical stream, same raise
cat > "$rfdir/eq1.weir" <<'WEOF'
!(sh -c "echo same-stream; exit 2")
WEOF
cat > "$rfdir/eq2.weir" <<'WEOF'
sh -c "echo same-stream; exit 2" | orFail "custom words"
WEOF
out1=$($BIN "$rfdir/eq1.weir" 2>/dev/null; echo "rc=$?")
out2=$($BIN "$rfdir/eq2.weir" 2>/dev/null; echo "rc=$?")
[ "$out1" = "$out2" ] || fail "!() and orFail must stream identically: [$out1] vs [$out2]"
echo "e2e ok: bare !() and orFail stream byte-identically (messages differ on stderr)"

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
errout=$(printf '!(git push | exitCode)
' | checkPiped 2>&1) && fail "discard conflict must reject"
echo "$errout" | grep -qF "bind it (let rc = <command> | exitCode)" || fail "discard-conflict teaching: $errout"
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

# ---- value-headed pipelines, more shapes [D:value-headed-pipe] ----
# an EXPRESSION piped into an external command feeds its stdin
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
# single external segment reifies WITH the value as stdin
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
# multi-external reifier still rejects (no new law)
errout=$(printf 'echo hi | grep h | complete\n' | checkPiped 2>&1) && fail "multi-external reifier must reject"
echo "$errout" | grep -qF "single external command segment" || fail "multi-external rule changed: $errout"
# the sigil-interior teaching names the value-headed spelling
# (retargeted from the retired district [D:district-retirement])
errout=$(printf '!(["x"] | cat)\n' | checkPiped 2>&1) && fail "value-headed in a sigil interior must reject"
echo "$errout" | grep -qF "value-headed pipeline bound outside" || fail "sigil-interior teaching: $errout"
echo "e2e ok: reifier-with-stdin (complete/succeeds/exitCode), zero-diff spellings, sigil-interior teaching"
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
!layers(weir child.weir)
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
# relative script path against the PHYSICAL cwd (getcwd) — compare
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

# one absolute answer three ways: the three outputs must AGREE and end
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
# it IS the running process (matches the OS view of the child)
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
# a REAL symlink or none: Git Bash's ln copy-emulates by default (a
# self-referential loop cannot be copied); nativestrict asks for the
# real thing (runners hold the privilege) and failure gates the
# symlink CELL as a stated skip rather than a broken fixture
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

let pinned = Path.glob "*.json" |> Seq.force
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
# EFFECTIVENESS gate, not an euid gate (the locked.txt precedent):
# root ignores modes AND Windows chmod is inert — test whether the
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
expect "glob: Seq.force pins the answer before cd" "forced-pinned: 2" "$out"

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
rm -rf "$pgdir" "$big"

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
let files = Path.glob "*.txt" |> Seq.force
git add $@files
git status --porcelain |> Seq.where (Str.startsWith "A ") |> Seq.iter print
WEOF
out=$(cd "$spldir" && $BIN add.weir)
expect "splat: glob into git add (N files, N words)" "A  a.txt
A  b.txt" "$out"

# empty splat vanishes — argv inspection AND behavior
cat > "$spldir/empty.weir" <<'WEOF'
let qf = if false then ["-q"] else []
sh -c "echo argc=$#" self $@qf tail
WEOF
out=$(cd "$spldir" && $BIN empty.weir)
expect "splat: empty seq contributes ZERO words" "argc=1" "$out"

# adversarial elements stay single words (THE injection pin)
cat > "$spldir/evil.weir" <<'WEOF'
let evil = ["one two"; "semi;colon"; "star*glob"]
sh -c "echo argc=$#" self $@evil
WEOF
out=$(cd "$spldir" && $BIN evil.weir)
expect "splat: spaces/semicolons/globs stay ONE word each (no re-split)" "argc=3" "$out"

# splat rides reifier chains [D:splat-reifier-chains]: THE safety pin
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

!author(sh -c "echo d=$MARK" self $@none | orFail "boom")
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

# scalar mid-word splice mirrors the splat's fatal [D:argv-splat]: the
# glued prefix would silently drop, so name the space/interp spellings
errout=$(printf 'let f = "x"
echo --file=$f
' | checkPiped 2>&1) && fail "mid-word scalar splice must reject"
echo "$errout" | grep -qF "cannot join a word under construction" || fail "mid-word scalar teaching: $errout"
# the spaced spelling stays legal (one argv word each)
out=$(printf 'let f = "x.txt"
echo --file $f
' | runPiped 2>&1)
echo "$out" | grep -qF -- "--file x.txt" || fail "spaced splice must pass: $out"
echo "e2e ok: scalar mid-word splice rejects, spaced spelling passes"

# the child's ARGS take a splat while input streams (both axes)
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

# deep-lock loudness [D:vacuous-probe-audit]: a garbage lock is NOT a
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
python3 -c "print('let x = ' + '('*499 + '1' + ')'*499)" > "$ddir/deep.weir" 2>/dev/null \
    || awk 'BEGIN{s="let x = "; for(i=0;i<499;i++)s=s"("; s=s"1"; for(i=0;i<499;i++)s=s")"; print s}' > "$ddir/deep.weir"
$BIN check "$ddir/deep.weir" >/dev/null 2>&1 || fail "depth 499 must parse on a full-size stack"
if [ "$IS_WINDOWS" = "1" ]; then
    # ulimit -s cannot constrain a native Windows process (the stack
    # reserve is baked at link time) — the small-stack PREMISE does not
    # exist there; the depth guard itself ran above at full stack
    echo "e2e SKIP: the small-stack probe cell — no POSIX stack limit on Windows (depth 499 parsed above)"
else
    errout=$( (ulimit -s 512; $BIN check "$ddir/deep.weir") 2>&1 ) && fail "small-stack deep parse must diagnose"
    rc=$?
    [ "$rc" -lt 128 ] || fail "small-stack deep parse crashed (rc=$rc) — the probe must fire first"
    echo "$errout" | grep -qF "nested too deeply" || fail "probe diagnostic missing: $errout"
    echo "e2e ok: depth probe diagnoses on a 512KB stack (no SIGSEGV), full stack still parses 499"
fi
rm -rf "$ddir"

# ---- user modules and imports, session 1 [D:modules-v1] -------------------
mdir=$(mkweirtmp)
cat > "$mdir/paths.weir" <<'WEOF'
module Paths
type Ctx = { root: string; name: string }
let make r n = { root = r; name = n }
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

# a bare literal matching TWO in-scope records is ambiguous, naming both
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

# a missing import puts the RESOLVED ABSOLUTE path in the message
printf 'import "./nope.weir"\n' > "$mdir/e_missing.weir"
out=$($BIN check "$mdir/e_missing.weir" 2>&1 || true)
# message SHAPE + resolved-ness (the parent leaf, dot-joined) — never
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

# weak purity: a module 'let' cannot run a command at import
printf 'module W\nlet head = git rev-parse HEAD\n' > "$mdir/weak.weir"
out=$($BIN check "$mdir/weak.weir" 2>&1 || true)
expect "weak purity: a module let cannot run a command at import" "cannot run a command at import" "$out"

# the graph [D:modules-v1]: transitive imports resolve and evaluate; a
# module can import and use another module's members
printf 'module D\nlet base = 10\n' > "$mdir/deep.weir"
printf 'module M\nimport "./deep.weir"\nlet doubled = D.base * 2\n' > "$mdir/mid.weir"
printf 'import "./mid.weir"\nprint (show M.doubled)\n' > "$mdir/transitive.weir"
out=$($BIN "$mdir/transitive.weir" 2>&1)
expect "transitive imports resolve and evaluate (top -> mid -> deep)" "20" "$out"

# a diamond's shared module is checked once and evaluates; both sides see it
printf 'module S\nlet v = 5\n' > "$mdir/shared.weir"
printf 'module L\nimport "./shared.weir"\nlet x = S.v + 1\n' > "$mdir/left.weir"
printf 'module R\nimport "./shared.weir"\nlet y = S.v + 2\n' > "$mdir/right.weir"
printf 'import "./left.weir"\nimport "./right.weir"\nprint (show (L.x + R.y))\n' > "$mdir/diamond.weir"
out=$($BIN "$mdir/diamond.weir" 2>&1)
expect "a diamond shares one module across both paths" "13" "$out"

# an import cycle is a check error naming the loop, at its closing edge
printf 'module CA\nimport "./cb.weir"\nlet a = 1\n' > "$mdir/ca.weir"
printf 'module CB\nimport "./ca.weir"\nlet b = 2\n' > "$mdir/cb.weir"
printf 'import "./ca.weir"\nprint "hi"\n' > "$mdir/ecycle.weir"
out=$($BIN check "$mdir/ecycle.weir" 2>&1 || true)
expect "an import cycle is named as a loop" "import cycle: ca.weir → cb.weir → ca.weir" "$out"

# a deep error reports at the deepest module's OWN site
printf 'module DBad\nlet x = Str.trim 5\n' > "$mdir/dbad.weir"
printf 'module MBad\nimport "./dbad.weir"\nlet y = 1\n' > "$mdir/mbad.weir"
printf 'import "./mbad.weir"\nprint "hi"\n' > "$mdir/tbad.weir"
out=$($BIN check "$mdir/tbad.weir" 2>&1 || true)
expect "a transitive error reports at the deepest module's own site" "dbad.weir:2:18: error" "$out"

# the import path is a literal string only
printf 'import foo\n' > "$mdir/e_litpath.weir"
out=$($BIN check "$mdir/e_litpath.weir" 2>&1 || true)
expect "the import path must be a literal string" "import takes a literal string path" "$out"

# multi-file diagnostics [D:modules-v1]: a module-CONTENT error reports at
# the module's OWN file:line, PLUS an "imported here" note at the import line
cat > "$mdir/broken.weir" <<'WEOF'
module Broken
let bad = Str.trim 5
WEOF
printf 'import "./broken.weir"\nprint "hi"\n' > "$mdir/e_broken.weir"
out=$($BIN check "$mdir/e_broken.weir" 2>&1 || true)
expect "a module error reports at its OWN site" "broken.weir:2:20: error" "$out"
expect "an imported-here note points at the import line" "e_broken.weir:1:8: note" "$out"

# check --json carries the module's own file per diagnostic — and ONE
# spelling per document (round 26: the importer's diag carried argv's
# form while the module's carried the resolved one; GetFullPath is the
# identity now, so pin the LEAF and pin the consistency)
out=$($BIN check --json "$mdir/e_broken.weir" 2>&1 || true)
echo "$out" | grep -q '"file":"[^"]*broken\.weir"' || fail "check --json carries the module's file identity: $out"
files=$(printf '%s' "$out" | grep -o '"file":"[^"]*tmp[^"]*"' | sed 's/.*tmp/tmp/; s/[\\/].*//' | sort -u | wc -l)
[ "$files" -le 1 ] || fail "check --json must spell every file's dir ONE way: $out"
echo "e2e ok: check --json carries the module's file identity (one spelling per document)"

# import is script-only (-e has no file to resolve against)
out=$($BIN -e 'import "./x.weir"' 2>&1 || true)
expect "import is script-only (-e rejects it)" "import is script-only" "$out"

# module / import are reserved words
out=$($BIN -e 'let import = 1' 2>&1 || true)
expect "import is a reserved word" "'import' is a keyword" "$out"

# Self.scriptPath is the FILE's own path; Self.entryPath is the invoked
# script's (a process fact) [D:modules-v1] (decision 12)
printf 'module Sp\nlet where () = Self.scriptPath\nlet entry () = Self.entryPath\n' > "$mdir/sp.weir"
printf 'import "./sp.weir"\nprint (Sp.where ())\nprint (Sp.entry ())\n' > "$mdir/spmain.weir"
out=$($BIN "$mdir/spmain.weir" 2>&1)
# leaf pins (weir prints the platform's path spelling): the property is
# WHICH file each answer names, not the dir's spelling
echo "$out" | sed -n 1p | grep -q "sp\.weir$" || fail "a module's Self.scriptPath is its OWN file: $out"
echo "$out" | sed -n 2p | grep -q "spmain\.weir$" || fail "a module's Self.entryPath is the invoked script: $out"
echo "e2e ok: a module's Self.scriptPath is its own file; entryPath the invoked script"

rm -rf "$mdir"

# ---- REPL config [D:repl-quality]: inert keys, reject unknown, and the
# load-bearing property that SCRIPTS never read it -----------------------
cfgdir=$(mkweirtmp)
# weir reads XDG_CONFIG_HOME on POSIX and %APPDATA% (the shell API, not
# the env) on Windows [D:windows-v1] — so the Windows half uses the
# runner's REAL AppData (a throwaway VM's), giving the config path
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

# echoElems REVIVED [D:echo-cap]: the config seeds the session cap
# (#echo reads it back); a non-positive value teaches and defaults
printf '{"echoElems": 25}\n' > "$CFGHOME/weir/config.json"
out=$(printf '#echo\n#quit\n' | XDG_CONFIG_HOME="$cfgdir" XDG_STATE_HOME="$cfgdir/state" $BIN 2>&1 || true)
expect "the config seeds the echo cap" "echo cap: 25" "$out"
printf '{"echoElems": 0}\n' > "$CFGHOME/weir/config.json"
out=$(printf '#echo\n#quit\n' | XDG_CONFIG_HOME="$cfgdir" XDG_STATE_HOME="$cfgdir/state" $BIN 2>&1 || true)
echo "$out" | grep -qF "echoElems must be positive; got 0 (using 100)" || fail "a non-positive echoElems must teach: $out"
echo "$out" | grep -qF "echo cap: 100" || fail "…and default: $out"

# a MALFORMED config must not affect a SCRIPT — scripts never read it, so the
# script runs clean rather than erroring on the broken JSON
printf 'not valid json {{{\n' > "$CFGHOME/weir/config.json"
printf 'print "scripts-ignore-config"\n' > "$cfgdir/s.weir"
out=$(XDG_CONFIG_HOME="$cfgdir" $BIN "$cfgdir/s.weir" 2>&1)
expect "a script ignores the REPL config entirely (even a broken one)" "scripts-ignore-config" "$out"
rm -f "$CFGHOME/weir/config.json"
rm -rf "$cfgdir"

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
expect "a --- stream teaches: one document, the count, the route" "from yaml: reads one document; this input has 2 documents — split on '---' and parse each" "$out"
printf 'type H = { host: string }\nlet hs = ["- host: a"; "- host: b"] |> from yaml seq<H>\nhs |> Seq.iter (fun h -> print h.host)\n' > "$ydir/seqdoc.weir"
out=$($BIN "$ydir/seqdoc.weir")
expect "from yaml seq<T> reads a top-level sequence document" "a
b" "$out"
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

# a rejected header inside a district errors AT THE HEADER under check
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
# the two paths agree; the acceptance: the pasted line emits UNQUOTED
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
# content is BYTES: every hostile class through one block scalar, asserted
# byte-exact through check AND run. The fixture is GENERATED (printf) because
# a checked-in literal with trailing spaces / tabs / CRLF invites editor and
# git mangling — the generator IS the fixture.
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
    printf '            \techo tab-first content\n'
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
YMap ([("data", YMap ([("hostile.txt", YStr "#!/bin/sh\n# comment-shaped\n// weir comment as data\n/// weir doc as data\n\n\n\techo tab-first content\n---\ntrailing spaces   \n$name and $(1 + 2)\nfor x in xs\n    more indented\n{ [ unbalanced ( brackets\nback to base\n")]))])
HEOF
$BIN "$hbdir/hostile.weir" > "$hbdir/got.txt"
cmp -s "$hbdir/expected.txt" "$hbdir/got.txt" || {
    diff "$hbdir/expected.txt" "$hbdir/got.txt" >&2
    fail "hostile-byte fixture must survive byte-exact through run"
}
echo "e2e ok: the hostile-byte fixture is byte-exact through check and run"

# CRLF [D:content-bytes]: a source file's line ending is not data —
# normalized at read, so a CRLF-saved file behaves identically
sed 's/$/\r/' "$hbdir/hostile.weir" > "$hbdir/crlf.weir"
$BIN "$hbdir/crlf.weir" > "$hbdir/crlf.txt"
cmp -s "$hbdir/got.txt" "$hbdir/crlf.txt" || fail "CRLF source must behave byte-identically to LF"
echo "e2e ok: CRLF source normalizes at read — output byte-identical"

# fmt: value-preserving on the fixture; its ONLY byte change is
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
# against a LOCAL server (CI is offline) serving the committed REAL
# k8s configmap schema — the published-schema fetch ran in-session
# and is recorded in the plan's report
ctdir=$(mkweirtmp)
mkdir -p "$ctdir/serve"
cp "$(dirname "$0")/../tests/fixtures/configmap-v1.json" "$ctdir/serve/"
ctport=$((18930 + RANDOM % 2000))
# loopback bind: macOS's firewall drops SYNs to an unsigned listener on
# 0.0.0.0 ('Operation timed out' on the very first fetch)
# --directory, never a cd-subshell: MSYS bash does not exec-optimize
# `( cd .. && python ) &`, so $! was the SUBSHELL and the kill left
# python alive holding the dir as its cwd (rm: Device or resource busy)
python3 -m http.server $ctport --bind 127.0.0.1 --directory "$ctdir/serve" >/dev/null 2>&1 &
ctsrv=$!
awaitHttp "http://127.0.0.1:$ctport/configmap-v1.json" || { kill $ctsrv 2>/dev/null || true; fail "the schema server never came up"; }

mkdir -p "$ctdir/proj/sub"
( cd "$ctdir/proj" && git init -q . )
( cd "$ctdir/proj" && $BIN add schema http://127.0.0.1:$ctport/configmap-v1.json --as k8s-configmap ) | grep -q "added schema k8s-configmap" || fail "add schema"
test -f "$ctdir/proj/.weir/lock.json" || fail "the lockfile exists after the first fetch"
echo "e2e ok: weir add schema fetches, writes, and locks"

# [D:add-validates]: add validates BEFORE it writes — nothing on disk
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
kill $ctsrv 2>/dev/null || true

# check-time catches on the REAL schema: the typo (did-you-mean) and a
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

# check NEVER fetches: an unreachable URL in the lock is irrelevant
# while the vendored file exists; a MISSING schema teaches vendor,
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

# ...and a NEVER-declared schema teaches `add` — the checker tells the
# two apart by the lock [D:contracts-spine]
cat > "$ctdir/proj/sub/never.weir" <<'WEOF'
let d = yaml schema=never-added
    kind: X

d |> to yaml |> print
WEOF
out=$($BIN check "$ctdir/proj/sub/never.weir" 2>&1 || true)
echo "$out" | grep -qF "add it: weir add schema <url> --as never-added" || fail "undeclared schema teaches add: $out"
echo "e2e ok: check never fetches — locked-but-missing teaches restore, undeclared teaches add"

# the six message shapes, re-pinned VERBATIM after the consistency pass
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

# a schema with NO additionalProperties:false warns at ADD time — the
# silently-inert-contract guard [schema-polish item 3]
printf '{ "type": "object", "properties": { "a": { "type": "string" } } }' > "$ctdir/serve/loose.json"
# --directory, never a cd-subshell: MSYS bash does not exec-optimize
# `( cd .. && python ) &`, so $! was the SUBSHELL and the kill left
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

# ---- the Log module [D:log-module] -----------------------------------------
# stderr always; WEIR_LOG selects; stdout is BYTE-IDENTICAL at every
# level (THE pin — stdout is data); invalid level = loud startup error
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

# NO_COLOR / non-tty: the harness reads PLAIN level labels (this pipe is
# not a tty, so tint must be absent even without NO_COLOR)
grep -q "$(printf '\033')" "$lgdir/err" && fail "no escapes when stderr is not a tty"
echo "e2e ok: Log plain form when stderr is piped"

# the thunk is NOT evaluated below threshold (side-effect proof)
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
# Windows shell-splitting trap: ONE intended expression arrives as many)
out=$($BIN --e 'x' 2>&1) && fail "--e must exit nonzero" || true
echo "$out" | grep -qF "unknown option '--e'. Did you mean '-e'?" || fail "--e did-you-means -e: $out"
out=$($BIN -e one two three 2>&1) && fail "-e arity must exit nonzero" || true
echo "$out" | grep -qF "got 3 — quote the expression" || fail "-e names its arity: $out"
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
# command-mode: 30s is an argv WORD on the published binary
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
# that had NO weir spelling (openssl-gated, the TLS block's precedent)
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

rm -rf "$indir"
echo "e2e ok: Instant (cert-expiry via openssl enddate, log slicing by cutoff, JSON refusal teaches)"

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
        !(weir -e "print 10")
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
# 100 arms each SPAWNING a child weir — the domain's real arm shape,
# at the raised ceiling; order preserved, parent cwd untouched
tudir=$(mkweirtmp)
mkdir -p "$tudir/work"
cat > "$tudir/tu.weir" <<'WEOF'
let before = pwd |> Seq.head
let outs =
    [1..100]
    |> Seq.pmap (fun i ->
        within cd "TUMARK/work"
            $(weir -e $"print {show i}") |> Seq.head
    )
    |> Seq.force
let after = pwd |> Seq.head
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
rm -rf "$rpdir"
echo "e2e ok: retry/poll (yields value, computed options, exhaustion messages, cancellable wait)"

# ---- if cmd | succeeds then [D:if-succeeds] --------------------------------
# the inline command condition: the let-RHS acceptance gate one position
# over; `then` stops the chain's argv ONLY inside a condition
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
# `then` stays ordinary argv OUTSIDE a condition — the stop is positional
[ "$(echo "$out" | sed -n 4p)" = "then one" ] || fail "argv then at top level: $out"
# a streaming chain parses and teaches at the CHECKER (bool demanded)
out=$($BIN -e 'if git ls-files then 1 else 2' 2>&1) && fail "non-bool chain must check-error" || true
echo "$out" | grep -qF "expected bool, got seq<string>" || fail "bool teaching: $out"
rm -rf "$isdir"
echo "e2e ok: if cmd | succeeds then (inline if/elif, bind-first kept, argv-then positional, checker demands bool)"

# ---- Seq.pfirst [D:seq-pfirst]: the race -----------------------------------
# the winner returns without joining the losers, and a loser's spawned
# TREE dies: its `sleep 2 && touch marker` orphan would fire at ~2s if
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
# FILE redirect, never $( ): command substitution's pipe stays open
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
out=$($BIN -e '[7; 8] |> Seq.pfirst (fun n -> match n with | 7 -> Duration.sleep 200ms ; fail "seven dies" | _ -> fail "eight dies")' 2>&1) && fail "all-failed must raise" || true
echo "$out" | grep -qF "seven dies" || fail "first error by input order: $out"
# empty raises AND names the guard (reject-don't-guess full form) — pin the
# FRAGMENT, not the joined sentence (FParsec wraps long messages)
out=$($BIN -e '[] |> Seq.pfirst (fun n -> n)' 2>&1) && fail "empty must raise" || true
echo "$out" | grep -qF "a race needs at least one arm" || fail "empty names Seq.isEmpty: $out"
rm -rf "$pfdir"
echo "e2e ok: Seq.pfirst (winner in ${took}ms, loser tree killed, first-by-order error, empty names the guard)"

# a loser's within-tmp cleanup DOES run — the `finally` executes on the
# loser's own (un-aborted) thread once its killed child fails; here the
# process OUTLIVES the loser (a 1s wait after the winner), so cleanup
# completes and the dir is gone. The exit-race leak [D:seq-pfirst] is the
# OTHER case (process exits immediately) — parked, not pinned.
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

# PROBE B REGRESSION: a pfirst whose script exits immediately after the
# winner used to leak the loser's within-tmp dir (a background thread
# killed mid-finally on NORMAL completion); the ProcessExit sweep fixes it
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

# the SIGNAL sweep (customer 1 — the leak every script always had) and
# REGISTRATION IS PER-PROCESS: a second weir's live dir survives the
# first one's sweep (never a blind scan of the temp root). Pinned via
# SIGTERM: a POSIX shell without job control starts background jobs
# with SIGINT IGNORED (and .NET honors an inherited SIG_IGN — the
# nohup convention), so a harness kill -INT is a no-op by DESIGN;
# real Ctrl-C (default disposition) takes the same handler, verified
# interactively. TERM exercises the identical sweep path.
# per-instance TAG files, never pid-keyed: on Windows, bash's $! is the
# msys fork-exec STUB's pid while Self.pid is the native weir's — the
# two never match (the process-identity-across-the-msys-boundary class,
# round 30). And on Windows the TERM stops at the msys boundary: it
# lands on the MSYS sh child, weir raises command-failed, and the
# RAISE-PATH finally removes the dir — the same no-leak property
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
# the acceptance: start-await-use-teardown in weir, and NOTHING survives.
# The server is a plain TCPServer — NOT `-m http.server`, whose
# HTTPServer.server_bind calls getfqdn(host): on the macOS runner that
# reverse-DNS parks in mDNSResponder FOREVER for weir-descendant
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
    let n = Http.fetch "http://127.0.0.1:$spport/" |> Seq.length
    print \$"got={n > 0}"
print "closed"
WEOF
out=$($BIN "$spdir/acc.weir" 2>&1) || {
    # discriminate the halves [marker discipline]: server reachable from
    # BASH means weir's probe is the broken side; unreachable means the
    # spawn/bind side
    probe=$(curl -s --max-time 2 -o /dev/null -w '%{http_code}' "http://127.0.0.1:$spport/" 2>/dev/null || echo "curl-failed")
    fail "scoped-proc acceptance failed (bash-side probe of :$spport = $probe): $out"
}
echo "$out" | grep -qF "got=true" || fail "acceptance fetch: $out"
echo "$out" | grep -qF "closed" || fail "acceptance close: $out"
sleep 0.5
$BIN -e "Net.portOpen $spport" | grep -qF "false" || fail "the no-orphan half: port $spport still up after the scope"

# died-at-startup fails the poll IMMEDIATELY with the child's own words
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
    printf 'within proc p = sh -c "sleep 297"\n    fail "mid-scope"\n' > "$spdir/raise.weir"
    out=$($BIN "$spdir/raise.weir" 2>&1) && fail "the raise must propagate" || true
    echo "$out" | grep -qF "mid-scope" || fail "the raise is the scope's error: $out"
    sleep 0.3
    pgrep -f "sleep 297" >/dev/null && { pkill -f "sleep 297" 2>/dev/null || true; fail "the raise path left an orphan"; } || true

    # the hard-exit sweep: a TERM'd weir leaves no registered child
    printf 'within proc p = sh -c "sleep 298"\n    Duration.sleep 30s\n' > "$spdir/term.weir"
    $BIN "$spdir/term.weir" & sptw=$!
    sleep 1.2
    kill -TERM $sptw 2>/dev/null || true
    wait $sptw 2>/dev/null || true
    sleep 0.5
    pgrep -f "sleep 298" >/dev/null && { pkill -f "sleep 298" 2>/dev/null || true; fail "the TERM sweep left the scoped child"; } || true
fi

# an ESCAPED handle (the scope's value) answers gracefully after the
# scope killed the child: running=false, tail empty (spill gone), wait
# yields the kill's code — never a crash
printf 'let esc = within proc p = sh -c "sleep 5"\n    p\nprint (show (Proc.running esc))\nprint (show (Proc.tail esc |> Seq.length))\n' > "$spdir/esc.weir"
out=$($BIN "$spdir/esc.weir" 2>&1) || fail "the escaped handle must not crash: $out"
printf '%s' "$out" | head -1 | grep -qF "false" || fail "escaped running must be false: $out"
printf '%s' "$out" | tail -1 | grep -qF "0" || fail "escaped tail must be empty (spill gone): $out"

rm -rf "$spdir"
echo "e2e ok: scoped processes (acceptance + no-orphan, watch died/timeout, retry refusal, raise + TERM sweeps, escaped handle graceful)"

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
# checking: typo caught, and CHECK SPAWNS NOTHING (the marker pin)
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
cd - >/dev/null
rm -rf "$sgdir"
echo "e2e ok: command signatures (generate, typo+did-you-mean, check spawns nothing, property 3, verify arms, restore never regenerates)"

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

# ---- Http [D:http]: the typed request boundary (OFFLINE local server) ------
if command -v python3 >/dev/null 2>&1; then
    hport=$((21000 + RANDOM % 2000))
    hdir=$(mkweirtmp)
    # an echo server: returns the request body byte-exact, status from a
    # header, and the Authorization header on /auth
    cat > "$hdir/echo.py" <<'PYEOF2'
import http.server, socketserver, sys
class H(http.server.BaseHTTPRequestHandler):
    def _h(self):
        n = int(self.headers.get('Content-Length', 0))
        body = self.rfile.read(n) if n else b''
        if self.path == '/auth':
            body = self.headers.get('Authorization', 'NONE').encode()
        code = 404 if self.path == '/missing' else int(self.headers.get('X-Want-Status', '200'))
        self.send_response(code); self.end_headers(); self.wfile.write(body)
    do_GET = do_POST = do_PUT = _h
    def log_message(self, *a): pass
socketserver.TCPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
PYEOF2
    python3 "$hdir/echo.py" "$hport" &
    hsrv=$!
    sleep 0.6

    # THE MANGLING PIN: a multi-object NDJSON body (spans lines) round-trips
    # BYTE-EXACT — the exact bytes curl -d would have eaten
    cat > "$hdir/mangle.weir" <<WEOF
type P = { name: string; count: int }
let items = [{ name = "a"; count = 1 }; { name = "b"; count = 2 }; { name = "c"; count = 3 }]
let sent = items |> to json
let resp = Http.send { Http.defaults with method = Post; url = "http://127.0.0.1:$hport/x"; body = Json sent }
let ok = (sent |> Seq.length) == (resp.body |> Seq.length)
print \$"lines={resp.body |> Seq.length} match={ok}"
resp.body |> Seq.iter print
WEOF
    out=$($BIN "$hdir/mangle.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "mangle send failed: $out"; }
    echo "$out" | grep -qF "lines=3 match=true" || { kill $hsrv 2>/dev/null || true; fail "body did not round-trip line-count: $out"; }
    echo "$out" | grep -qF '{"count":2,"name":"b"}' || { kill $hsrv 2>/dev/null || true; fail "body bytes mangled: $out"; }

    # STATUS IS DATA: a 404 binds, never raises
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

    # Http.fetch RAISES on non-2xx (naming the status); the SAME 404 that
    # send BINDS as data [D:http-s2] — two names, no boolean
    cat > "$hdir/fetch.weir" <<WEOF
let ok = Http.fetch "http://127.0.0.1:$hport/x"
print \$"fetch-ok lines={ok |> Seq.length}"
let bound = Http.send (Http.get "http://127.0.0.1:$hport/missing")
print \$"send-binds={bound.status}"
WEOF
    out=$($BIN "$hdir/fetch.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "fetch/send failed: $out"; }
    echo "$out" | grep -qF "send-binds=404" || { kill $hsrv 2>/dev/null || true; fail "send must BIND a 404: $out"; }
    out=$($BIN -e 'Http.fetch "http://127.0.0.1:'"$hport"'/missing" |> Seq.length |> print' 2>&1) && { kill $hsrv 2>/dev/null || true; fail "fetch must RAISE on 404"; } || true
    echo "$out" | grep -qF "answered 404" || { kill $hsrv 2>/dev/null || true; fail "fetch raise must name the status: $out"; }

    # a constructor round-trips through send (Http.post carries the method)
    cat > "$hdir/ctor.weir" <<WEOF
let r = Http.send (Http.post "http://127.0.0.1:$hport/x")
print \$"ctor-status={r.status}"
WEOF
    out=$($BIN "$hdir/ctor.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "constructor send failed: $out"; }
    echo "$out" | grep -qF "ctor-status=200" || { kill $hsrv 2>/dev/null || true; fail "constructor did not send: $out"; }

    # parallel fetches via Seq.pmap
    cat > "$hdir/pmap.weir" <<WEOF
let urls = ["http://127.0.0.1:$hport/a"; "http://127.0.0.1:$hport/b"; "http://127.0.0.1:$hport/c"]
urls |> Seq.pmap (fun u -> Http.send { Http.defaults with url = u }) |> Seq.map (fun r -> show r.status) |> Seq.iter print
WEOF
    out=$($BIN "$hdir/pmap.weir" 2>&1) || { kill $hsrv 2>/dev/null || true; fail "pmap fetch failed: $out"; }
    [ "$(echo "$out" | grep -c '^200$')" -eq 3 ] || { kill $hsrv 2>/dev/null || true; fail "pmap did not fetch all: $out"; }

    kill $hsrv 2>/dev/null || true

    # TRANSPORT failure raises, in its OWN words per case [D:transport-words];
    # and CHECK makes NO request (a bogus URL checks clean, no network)
    cat > "$hdir/dead.weir" <<'WEOF'
let resp = Http.send { Http.defaults with url = "http://127.0.0.1:1/never" }
print "unreached"
WEOF
    $BIN check "$hdir/dead.weir" >/dev/null 2>&1 || fail "check must not make a request (should pass clean)"
    out=$($BIN "$hdir/dead.weir" 2>&1) && fail "transport failure must raise" || true
    echo "$out" | grep -qF "refused the connection" || fail "refused case must say so: $out"

    # a TIMEOUT names itself and the duration that fired — never the .NET
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

    # insecure: TLS verification is ON by default and OFF per-request when
    # asked [D:http-s2] — a self-signed server the default REJECTS and
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
        # default REJECTS the self-signed cert (verification on)
        out=$($BIN -e 'Http.send (Http.get "https://127.0.0.1:'"$tport"'/")' 2>&1) && { kill $tsrv 2>/dev/null || true; fail "default must reject a self-signed cert"; } || true
        echo "$out" | grep -qF "certificate is not trusted" || { kill $tsrv 2>/dev/null || true; fail "TLS rejection message: $out"; }
        echo "$out" | grep -qF "insecure = true" || { kill $tsrv 2>/dev/null || true; fail "TLS rejection must name its repair: $out"; }
        # insecure = true ACCEPTS it
        cat > "$hdir/ins.weir" <<WEOF
let r = Http.send { Http.get "https://127.0.0.1:$tport/" with insecure = true }
print \$"insecure-status={r.status}"
WEOF
        out=$($BIN "$hdir/ins.weir" 2>&1) || { kill $tsrv 2>/dev/null || true; fail "insecure=true must accept the self-signed cert: $out"; }
        echo "$out" | grep -qF "insecure-status=200" || { kill $tsrv 2>/dev/null || true; fail "insecure did not connect: $out"; }
        kill $tsrv 2>/dev/null || true
        echo "e2e ok: Http insecure (default rejects a self-signed cert, insecure=true accepts — per-request)"
    else
        echo "e2e SKIP: openssl absent — Http insecure TLS pin not run" >&2
    fi

    rm -rf "$hdir"
    echo "e2e ok: Http (mangling, status-is-data, auth, fetch-raises/send-binds, constructors, pmap, transport raises, check silent)"
else
    echo "e2e SKIP: python3 absent — Http offline server not run" >&2
fi

# ---- the showcase's own .weir tree [D:showcase-covers] ---------------------
# the tour imports a module, validates a district against the COMMITTED
# schema, and declares the hand-written git signature — check needs no
# restore and no network (the fresh-tree copy proves it)
sctdir=$(mkweirtmp)
mkdir -p "$sctdir/root/.git"
cp -r "$(dirname "$0")/../examples" "$sctdir/root/examples"
( cd "$sctdir/root" && $BIN check examples/showcase.weir >/dev/null 2>&1 ) || fail "the showcase checks in a fresh tree, offline, no restore"
# the caught-typo proof the showcase's comment points at
printf '#sig git\nlet rc = git status --porcelian | exitCode\nprint (show rc)\n' > "$sctdir/root/examples/typo.weir"
out=$(cd "$sctdir/root" && $BIN check examples/typo.weir 2>&1) || true
echo "$out" | grep -qF "unknown flag '--porcelian' for git. Did you mean '--porcelain'?" || fail "the git sig catches the typo (reified shape): $out"
rm -rf "$sctdir"
echo "e2e ok: showcase .weir tree (offline check, no restore; the typo the comment names is caught)"

echo "e2e battery: all green"
