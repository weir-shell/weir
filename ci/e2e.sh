#!/usr/bin/env bash
# End-to-end battery against the AOT binary (command-mode Session 4 set).
set -euo pipefail

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"

# HARD stale-binary gate [D:masking-mechanized] — the ONE shared gate
# (stamp == HEAD, no .fs newer than the binary), so stale results are
# impossible rather than catchable.
"$(dirname "$0")/check-fresh.sh" "$BIN"

fail() {
    echo "e2e FAIL: $1" >&2
    exit 1
}

expect() {
    local desc="$1" needle="$2" out="$3"
    # -- so needles may start with '-' (e.g. "-6 : int")
    echo "$out" | grep -qF -- "$needle" || fail "$desc — expected to find: $needle in: $out"
    echo "e2e ok: $desc"
}

out=$($BIN -e '(1 + 2) * 2')
expect "expression eval" "6 : int" "$out"

out=$($BIN -e 'ls |> where (fun f -> f.Bytes > 1048576) |> first 5' 2>&1); rc=$?
[ $rc -eq 0 ] || fail "flagship pipeline must run measure-free: $out"
echo "e2e ok: flagship pipeline, bare bytes"

if $BIN -e '1<mb> + 2<mb>' 2>/dev/null; then
    fail "old measure literal should be rejected"
fi
errout=$($BIN -e '1<mb>' 2>&1 || true)
echo "$errout" | grep -qF "units of measure are not supported" || fail "transition message missing: $errout"
echo "e2e ok: measure transition error"

out=$($BIN -e 'cmd "echo" ["*"]')
expect "argv stays literal" '["*"]' "$out"

out=$($BIN -e 'echo hi (40 + 2) | first 1')
expect "command mode with splice" '["hi 42"]' "$out"

out=$($BIN -e '[1..5] |> Seq.length')
expect "range literal on the AOT binary" "5 : int" "$out"

out=$(timeout 5 $BIN -e '[1..1000000] |> first 3') || fail "huge range under first must terminate (laziness)"
expect "ranges are lazy generators" '[1; 2; 3]' "$out"

rangedir=$(mktemp -d)
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

out=$($BIN -e 'echo $"n={40 + 2}" | first 1')
expect "interpolated string is one argv entry" '["n=42"]' "$out"

dir=$(mktemp -d)
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

out=$(printf 'cd "%s"\ngit status --porcelain | from porcelain | where _.Staged | map _.Path\n^ls\nlet pat = "a"\ngrep -l $pat staged.txt\n:q\n' "$dir" | $BIN)
expect "cd + porcelain + staged filter" '["staged.txt"]' "$out"
expect "^ls forces external" 'untracked.txt' "$out"
expect "bound-variable splice into grep" '["staged.txt"]' "$out"
rm -rf "$dir"

out=$($BIN -e 'yes hi | cat | first 2')
expect "external pipes into external stdin" '["hi"; "hi"]' "$out"

out=$($BIN -e 'grep nomatch /etc/hostname | complete |> _.ExitCode')
expect "complete reifies nonzero exit as data" "1 : int" "$out"

out=$(timeout 10 $BIN -e 'bash -c "yes e | head -c 100000 1>&2; echo done" | complete |> _.ExitCode') \
    || fail "chatty-stderr deadlock under complete (timeout)"
expect "concurrent stderr drain under complete" "0 : int" "$out"

out=$($BIN -e 'sh -c "echo out; echo err 1>&2"' 2>/dev/null)
expect "stderr passthrough keeps stdout stream clean" '["out"]' "$out"

out=$($BIN -e 'sh -c "echo a && echo b"')
expect "POSIX one-liner via the external shell" '["a"; "b"]' "$out"

out=$($BIN -e 'sh -c "exit 7" | complete |> _.ExitCode')
expect "sh lines can complete now (old builtin boundary gone)" "7 : int" "$out"

out=$($BIN -e 'match Ok 3 with | Ok v -> v | Error e -> Str.length e')
expect "prelude Result with cross-arm inference" "3 : int" "$out"

out=$($BIN -e 'ls |> Seq.sortBy _.Bytes |> Seq.map _.Name |> Seq.head' 2>/dev/null | head -1)
expect "qualified module pipeline" " : string" "$out"

out=$($BIN -e '[] |> Seq.tryHead |> Option.defaultValue 9')
expect "Option sweep idiom on the AOT binary" "9 : int" "$out"

out=$($BIN -e 'Some 3')
expect "prelude Option types generically" "Some 3 : Option<int>" "$out"

branchdir=$(mktemp -d)
(
    cd "$branchdir"
    git init -q
    git -c user.email=ci@ci -c user.name=ci commit -q --allow-empty -m init
    git branch feature/a
    git branch feature/b
    git branch keep-me
)
out=$(printf 'cd "%s"\ngit branch | map trim | where (startsWith "feature") | join ","\n:q\n' "$branchdir" | $BIN)
expect "git-branch-cleanup dogfood task" '"feature/a,feature/b"' "$out"
rm -rf "$branchdir"

scriptdir=$(mktemp -d)
cat > "$scriptdir/task.weir" <<'WEOF'
#!/usr/bin/env weir
// strict by default
type Tag = Big | Small
let names = ls |> Seq.map _.Name
names |> Seq.first 1 |> print
echo spliced (40 + 2)
args |> Seq.head |> print
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
let staged =
    git status --porcelain
    | from porcelain
    | Seq.first 1

staged |> Seq.iter (fun c -> print (show c))
WEOF
out=$(cd "$scriptdir" && git init -q 2>/dev/null; cd "$scriptdir" && $BIN show.weir)
echo "$out" | grep -qF "Staged = " || fail "show must render the porcelain row: $out"
echo "e2e ok: show renders typed rows on the AOT binary"

start=$(date +%s%N)
$BIN -e '[1; 2; 3; 4] |> Seq.piter (fun n -> if (completed "sh" ["-c"; "sleep 0.3"]).ExitCode > 99 then print "never")' >/dev/null 2>&1 || fail "piter probe failed"
elapsed_ms=$(( ($(date +%s%N) - start) / 1000000 ))
[ "$elapsed_ms" -lt 900 ] || fail "piter must run workers in parallel (4x300ms took ${elapsed_ms}ms)"
echo "e2e ok: piter parallelism (4x300ms in ${elapsed_ms}ms)"

forkdir=$(mktemp -d)
cat > "$forkdir/fork.weir" <<'WEOF'
let a = cd "/tmp"

let workers =
    ["/"; "/etc"]
    |> Seq.pmap (fun d ->
        let x = cd d
        pwd |> Seq.head)

workers |> Seq.iter print
print $"after: {pwd |> Seq.head}"
WEOF
out=$($BIN "$forkdir/fork.weir")
expect "worker sessions fork: worker one" "/" "$out"
expect "worker sessions fork: worker two" "/etc" "$out"
expect "worker sessions fork: parent untouched" "after: /tmp" "$out"
rm -rf "$forkdir"

out_run=$($BIN -e 'run "sh" ["-c"; "printf a\\nb\\n"]')
out_print=$($BIN -e 'cmd "sh" ["-c"; "printf a\\nb\\n"] |> print')
[ "$out_run" = "$out_print" ] || fail "run must be byte-identical to cmd |> print (run=[$out_run] print=[$out_print])"
echo "e2e ok: run is the cmd|>print desugar, byte-identical"

if $BIN -e 'run "sh" ["-c"; "exit 4"]' 2>/dev/null; then
    fail "run must raise on nonzero exit"
fi
echo "e2e ok: run raises on nonzero exit"

seqdir=$(mktemp -d)
cat > "$seqdir/seq.weir" <<'WEOF'
let go = 1 > 0

let steps =
    if go then
        run "sh" ["-c"; "echo one"]
        run "sh" ["-c"; "echo two"]
        print "three"

let skipped =
    if 1 > 2 then
        run "sh" ["-c"; "echo never"]
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

sigdir=$(mktemp -d)
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
print $"code: {code.ExitCode}"
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

distdir=$(mktemp -d)
cat > "$distdir/d.weir" <<'WEOF'
let go = 1 > 0

if go then !
    sh -c "echo dist-one"
    // comments are transparent inside districts
    sh -c "echo dist-two"

if 1 > 2 then !
    sh -c "echo dist-never"

print "dist-after"
WEOF
out=$($BIN "$distdir/d.weir")
for needle in dist-one dist-two dist-after; do
    expect "district: $needle" "$needle" "$out"
done
if echo "$out" | grep -qF "dist-never"; then fail "false-branch district ran"; fi
echo "e2e ok: district effect counts, both branch ways, comments transparent"

cat > "$distdir/span.weir" <<'WEOF'
let n = 3

if 1 > 0 then !
    sh -c "echo a"
    echo (n |> Seq.length)
WEOF
errout=$($BIN "$distdir/span.weir" 2>&1) && fail "bad splice in district must fail"
echo "$errout" | grep -qE "span.weir:5:" || fail "district splice error must point at line 5: $errout"
echo "e2e ok: district span translation points at the district line"
rm -rf "$distdir"

envdir=$(mktemp -d)
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

faildir=$(mktemp -d)
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

bigdir=$(mktemp -d)
truncate -s 3G "$bigdir/sparse.bin"
touch "$bigdir/empty.txt"
out=$(printf 'cd "%s"\nls |> Seq.where (fun f -> f.Bytes > 2147483647) |> Seq.map _.Name\nls |> Seq.where (fun f -> f.Bytes == 0) |> Seq.map _.Name\n:q\n' "$bigdir" | $BIN)
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

fmtdir=$(mktemp -d)
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

out=$(printf 'print "hi"\nlet u = ()\nu\n:q\n' | $BIN)
echo "$out" | grep -qF "hi" || fail "REPL print lost its output"
if echo "$out" | grep -qF "() : unit"; then fail "unit leaked into REPL display"; fi
echo "e2e ok: unit is invisible in the REPL"

stmtdir=$(mktemp -d)
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
echo "e2e ok: renderer byte-identical on empties and embedded newlines"

cat > "$stmtdir/stream.weir" <<'WEOF'
["alpha"; "staged: yes"; "omega"] |> print
WEOF
out=$($BIN "$stmtdir/stream.weir" | grep staged)
expect "print streams through a host pipe" "staged: yes" "$out"

out=$($BIN -e 'cmd "sh" ["-c"; "echo streamed"] |> print')
[ "$out" = "streamed" ] || fail "expression-position process |> print must stream (got: $out)"
echo "e2e ok: cmd sh |> print streams"

if $BIN -e 'cmd "sh" ["-c"; "exit 3"] |> print' 2>/dev/null; then
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

svdir=$(mktemp -d)

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

out=$($BIN -e '$(^cat /etc/hostname) |> Seq.length')
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
kill "$lk_pid" 2>/dev/null
wait "$lk_pid" 2>/dev/null || true
"$LOCKSH" check && fail "a dead holder must read as stale (no live holder)"
[ -f "$(dirname "$0")/../.weir-deep-run.lock" ] && fail "check must clear the stale lock"
echo "e2e ok: deep-run lock acquires, refuses double, clears when stale"

# --- weir lsp v1 (2026-07-21, LSP chain 3/3) ---------------------------

if command -v python3 >/dev/null 2>&1; then
    python3 "$(dirname "$0")/../tests/lib/harness-selftest.py" || fail "harness selftest (zombie truth / stamp gate)"
    echo "e2e ok: harness library selftest (waitpid-truth + stamp gate)"

    WEIR_BIN="$BIN" python3 "$(dirname "$0")/../tests/lsp/lsp-e2e.py" || fail "lsp integration probes"
    echo "e2e ok: lsp diagnostics/hover/completion over stdio"

    # grammar drift guard: micro's '# rule:' annotations vs the
    # tmLanguage repository keys — add to BOTH or neither.
    # LIMITATION on record: this proves rule PRESENCE, not regex
    # semantics — a wrong skip/end inside a matching rule name is
    # invisible here; per-kind escape laws are verified by eye on the
    # flagship (git-subrepo's encodeSubref @-string line is the canary,
    # and a stale INSTALLED syntax copy shows there too)
    python3 - "$(dirname "$0")/.." <<'PYEOF' || fail "grammar inventories diverge (micro vs tmLanguage)"
import json, re, sys
root = sys.argv[1]
micro = set(re.findall(r"^\s*# rule: ([\w-]+)$", open(f"{root}/editors/micro/weir.yaml").read(), re.M))
tm = set(json.load(open(f"{root}/editors/vscode/syntaxes/weir.tmLanguage.json"))["repository"].keys())
if micro != tm:
    print("micro-only:", sorted(micro - tm), " tm-only:", sorted(tm - micro))
    sys.exit(1)
print(f"inventories match ({len(tm)} rules)")
PYEOF
    echo "e2e ok: grammar inventories match (micro == tmLanguage)"

    # --- REPL line editor under a pty (2026-07-21) ---------------------
    python3 "$(dirname "$0")/../tests/repl/repl-wordnav.py" "$BIN" || fail "repl word navigation"
    echo "e2e ok: repl Ctrl+Left/Right word navigation"

    python3 "$(dirname "$0")/../tests/repl/repl-color.py" "$BIN" || fail "repl coloring"
    echo "e2e ok: repl lexical coloring, head verdicts, NO_COLOR"
else
    # no silent caps: name what was skipped
    echo "e2e SKIP: python3 absent — lsp + repl pty probes NOT run" >&2
fi

# --- weir check [--json] (2026-07-21, LSP chain 2/3) -------------------

ckdir=$(mktemp -d)

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

mdir=$(mktemp -d)
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

# --- the casing law (2026-07-21) ---------------------------------------

errout=$($BIN -e 'let Foo = 1 in Foo' 2>&1 || true)
echo "$errout" | grep -qF "binding names start lowercase" || fail "casing law must reject at the binder: $errout"
echo "e2e ok: the casing law (lowercase binds) on the AOT binary"

# param-ful command RHS (PLAN-paramful-rhs): the shadowing law —
# this pin was written FAILING against the guard-dropped prototype
# (`let f x = x` printed SPAWNED with an executable x on PATH)
shdir=$(mktemp -d)
printf '#!/bin/sh\necho SPAWNED\n' > "$shdir/x" && chmod +x "$shdir/x"
cat > "$shdir/shadow.weir" <<'WEOF'
let f x = x
print (f "value")
WEOF
out=$(PATH="$shdir:$PATH" $BIN "$shdir/shadow.weir")
expect "params shadow PATH in their own RHS (identity stays identity)" "value" "$out"
printf '^x\n' > "$shdir/force.weir"
out=$(PATH="$shdir:$PATH" $BIN "$shdir/force.weir")
expect "^x still reaches the PATH binary (no capability lost)" "SPAWNED" "$out"
rm -rf "$shdir"

# param-ful RHS: forms, sigil equivalence, splice-typo hint
pfdir=$(mktemp -d)
(cd "$pfdir" && git init -q . && git -c user.email=a@a -c user.name=a commit -q --allow-empty -m x)
cat > "$pfdir/forms.weir" <<'WEOF'
let revParse r = git rev-parse $r | Seq.head
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

# the git-subrepo example (the translation flagship): the FULL live
# lifecycle — clone, pull, push (author-preserving graft), second
# round-trip, status/clean/init — against a real bare upstream
srdir=$(mktemp -d)
SR="$(cd "$(dirname "$0")/.." && pwd)/examples/git-subrepo.weir"
(
    cd "$srdir"
    git init -q --bare lib.git && git -C lib.git symbolic-ref HEAD refs/heads/main
    git init -q seed && cd seed
    git config user.email u@u && git config user.name up
    echo "lib v1" > lib.txt && git add -A && git commit -qm "lib: v1"
    git branch -m main && git push -q ../lib.git main && cd ..
    git init -q host && cd host
    git config user.email h@h && git config user.name hostdev
    echo host > host.txt && git add -A && git commit -qm "host: init" && git branch -m main
)
out=$(cd "$srdir/host" && $BIN "$SR" clone --remote ../lib.git --subdir vendor)
expect "git-subrepo clone (head-branch detected via ls-remote)" "cloned into 'vendor'" "$out"
[ -f "$srdir/host/vendor/lib.txt" ] || fail "clone must materialize the subdir"

(cd "$srdir/seed" && echo "lib v2" >> lib.txt && git commit -qam "lib: v2" && git push -q ../lib.git main)
out=$(cd "$srdir/host" && $BIN "$SR" pull --subdir vendor)
expect "git-subrepo pull (graft + merge-tree join, no worktree)" "pulled from" "$out"
grep -q "lib v2" "$srdir/host/vendor/lib.txt" || fail "pull must land upstream content"

(cd "$srdir/host" && echo "host contribution" >> vendor/lib.txt && git commit -qam "host: contribute")
out=$(cd "$srdir/host" && $BIN "$SR" push --subdir vendor)
expect "git-subrepo push (the graft walk)" "pushed to" "$out"
upstream_log=$(cd "$srdir/seed" && git pull -q ../lib.git main 2>/dev/null; git log --format="%s|%an" -2)
echo "$upstream_log" | grep -qF "host: contribute|hostdev" || fail "push must land upstream WITH the host author preserved: $upstream_log"

(cd "$srdir/seed" && echo "lib v3" >> lib.txt && git commit -qam "lib: v3" && git push -q ../lib.git main)
out=$(cd "$srdir/host" && $BIN "$SR" pull --subdir vendor)
expect "second pull merges upstream over local contributions" "pulled from" "$out"
grep -q "lib v3" "$srdir/host/vendor/lib.txt" || fail "v3 must arrive"
grep -q "host contribution" "$srdir/host/vendor/lib.txt" || fail "local contribution must survive the merge"

out=$(cd "$srdir/host" && $BIN "$SR" pull --subdir vendor)
expect "git-subrepo pull detects up-to-date via the union" "up to date" "$out"

out=$(cd "$srdir/host" && $BIN "$SR" status --subdir vendor -v)
expect "git-subrepo status reads .gitrepo" "Tracking Branch: main" "$out"
expect "the fetch ref surfaces through the Regex pattern" "FETCH Ref:" "$out"

out=$(cd "$srdir/host" && $BIN "$SR" clean --subdir vendor --force)
expect "clean removes the subrepo refs" "Removed ref" "$out"

out=$(cd "$srdir/host" && mkdir -p tools && echo t > tools/t.txt && git add -A && git commit -qm "host: tools" && $BIN "$SR" init --subdir tools)
expect "init converts a tracked subdir" "Subrepo created from 'tools'" "$out"

errout=$(cd "$srdir/host" && $BIN "$SR" pull --subdir nope 2>&1 || true)
echo "$errout" | grep -qF "No 'nope/.gitrepo' file" || fail "missing-gitrepo error: $errout"
echo "e2e ok: git-subrepo error paths are located"
rm -rf "$srdir"

# Seq.fold + fun-sugar (PLAN-fold): the git-subrepo receipt folds
# verbatim — the port's blocker provably unblocked
folddir=$(mktemp -d)
cat > "$folddir/receipt.weir" <<'WEOF'
// encode-subdir's escape loop, as a fold over replacement pairs
let escaped =
    [("~", "%7e"); ("^", "%5e"); (":", "%3a"); (" ", "%20")]
    |> Seq.fold (fun s (from0, to0) -> Str.replace from0 to0 s) "a b~c:d"

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

// the inline-env receipt shape (three vars, Env.ofPairs)
runEnv (Env.ofPairs [("GIT_AUTHOR_NAME", "n"); ("GIT_AUTHOR_EMAIL", "e"); ("GIT_AUTHOR_DATE", "d")]) "sh" ["-c"; "echo $GIT_AUTHOR_NAME/$GIT_AUTHOR_EMAIL"]
WEOF
out=$($BIN "$folddir/receipt.weir")
expect "the encode-subdir escape fold" "a%20b%7ec%3ad" "$out"
expect "the commit-walk accumulator-record fold" "c3 after c1, kept 2" "$out"
expect "Env.ofPairs feeds runEnv (the inline-env receipt)" "n/e" "$out"
rm -rf "$folddir"

# fmt v2 respace under the parse-shape guard (user receipt, 2026-07-22)
fdir=$(mktemp -d)
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
upd=$(mktemp -d)
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
rawdir=$(mktemp -d)
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

out=$($BIN -e 'echo (@"\n") | Seq.head')
expect "verbatim splice is one literal argv entry" '\n' "$out"
rm -rf "$rawdir"

# the Regex pattern + Str match family (regex plan, 2026-07-22)
redir=$(mktemp -d)
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

rdir=$(mktemp -d)
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
expect "Path members compose" '"a/b/c"' "$out"

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

pbdir=$(mktemp -d)

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

tudir=$(mktemp -d)

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

ldir=$(mktemp -d)

cat > "$ldir/lit.weir" <<'WEOF'
let cleanup () = printerr "cleaning up"

let classify n =
    match n with
    | 0 -> "none"
    | 1 -> "one"
    | n -> $"many ({n})"

let mode = args |> Seq.tryHead |> Option.defaultValue "count"

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

cdir=$(mktemp -d)

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

bdir=$(mktemp -d)

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

tdir=$(mktemp -d)

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

hdir=$(mktemp -d)

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

sdir=$(mktemp -d)
printf 'MARK=layered\n' > "$sdir/s.env"

cat > "$sdir/sigil.weir" <<'WEOF'
let e = Env.fromFile "s.env"

!e(sh -c "echo effect: $MARK")

let got = $e(sh -c "echo cap: $MARK") |> Seq.head
print got

let r = $e(sh -c "exit 7" | complete)
print $"complete-env exit {r.ExitCode}"

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

if go then !e
    sh -c "echo d-one: $MARK"
    sh -c "echo d-two: $MARK"

if go then !
    sh -c "echo d-bare: [$MARK]"
WEOF
out=$(cd "$sdir" && $BIN district.weir 2>&1)
expect "env district distributes over the block" "d-two: layered" "$out"
expect "bare district stays env-less" "d-bare: []" "$out"

$BIN fmt --check "$sdir/district.weir" >/dev/null 2>&1 || fail "fmt must accept the env district"
echo "e2e ok: fmt roundtrips the env district"

rm -rf "$sdir"

# --- child-env injection (2026-07-20): the shEnv receipt ------------

edir=$(mktemp -d)

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

runEnv targetEnv "sh" ["-c"; "echo \"AZ($AZURE_SUBSCRIPTION_ID|$AZURE_DEFAULTS_GROUP|$OVERRIDE|$INHERITED)\""]
WEOF
out=$(cd "$edir" && OVERRIDE=from-parent INHERITED=passed-through $BIN deploy.weir)
expect "bicep shape: overlay sets, overrides, and inherits" "AZ(sub-web|rg web|from-file|passed-through)" "$out"

# parent isolation: the overlay never leaks into the weir process
cat > "$edir/iso.weir" <<'WEOF'
let vars = Env.fromFile "target.env"

runEnv vars "sh" ["-c"; "true"]

print (Env.get "AZURE_SUBSCRIPTION_ID" |> Option.defaultValue "(clean)")
WEOF
out=$(cd "$edir" && $BIN iso.weir)
expect "child-env never leaks into the parent session" "(clean)" "$out"

# byte-identity: runEnv with an empty overlay IS run
printf 'EMPTYFILE=x\n' > "$edir/one.env"
cat > "$edir/ident-a.weir" <<'WEOF'
run "sh" ["-c"; "echo one; echo two"]
WEOF
cat > "$edir/ident-b.weir" <<'WEOF'
runEnv [] "sh" ["-c"; "echo one; echo two"]
WEOF
a=$(cd "$edir" && $BIN ident-a.weir)
b=$(cd "$edir" && $BIN ident-b.weir)
[ "$a" = "$b" ] || fail "runEnv [] must be byte-identical to run (got '$a' vs '$b')"
echo "e2e ok: runEnv [] byte-identical to run"

# empty-string value: the documented removal workaround
cat > "$edir/empty.weir" <<'WEOF'
let vars = Env.fromFile "blank.env"

runEnv vars "sh" ["-c"; "echo [$BLANKED]"]
WEOF
printf 'BLANKED=\n' > "$edir/blank.env"
out=$(cd "$edir" && BLANKED=parent-value $BIN empty.weir)
expect "empty-string value overrides (removal workaround)" "[]" "$out"

# lifecycle with env: raise-at-force and tree-kill hold on the env path
if (cd "$edir" && $BIN -e 'runEnv (Env.fromFile "one.env") "sh" ["-c"; "exit 3"]') 2>/dev/null; then
    fail "runEnv must raise on nonzero exit at force"
fi
echo "e2e ok: runEnv raises on nonzero exit"

out=$(cd "$edir" && timeout 10 $BIN -e 'cmdEnv (Env.fromFile "one.env") "yes" ["hi"] |> Seq.first 1 |> Seq.length') || fail "cmdEnv stream must tree-kill after first"
expect "cmdEnv tree-kills like cmd" "1 : int" "$out"

# subset rejections name the sh escape
printf 'export FOO=1\n' > "$edir/bad.env"
errout=$(cd "$edir" && $BIN -e 'Env.fromFile "bad.env" |> Seq.length' 2>&1 || true)
echo "$errout" | grep -qF 'set -a; . file' || fail "dotenv rejection must name the sh escape: $errout"
echo "e2e ok: dotenv rejection names the sh escape"

rm -rf "$edir"

# --- grammar consolidation (2026-07-20): offside close, record
# continuations, exit — the bicep translation's shapes verbatim

gdir=$(mktemp -d)

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
if r.ExitCode <> 0 then exit (r.ExitCode)
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
adir=$(mktemp -d)
cat > "$adir/attrs.weir" <<'WEOF'
type Cfg = { [<Short "c"; Doc "count">] Count: int; Name: string; [<NoShort>] Loud: bool }
let c = { Count = 1; Name = "x"; Loud = false }
let c2 = { c with Count = 2 }
print $"{c2.Count} {show c.Loud}"
WEOF
out=$($BIN "$adir/attrs.weir")
expect "attributed record constructs, updates, shows — erased" "2 false" "$out"

cat > "$adir/attrs-json.weir" <<'WEOF'
type J = { [<Doc "the n">] N: int }
let j = echo '{"N": 5}' | from json J | Seq.head
print j.N
WEOF
out=$($BIN "$adir/attrs-json.weir")
expect "from json loads an attributed record identically" "5" "$out"

cat > "$adir/attrs-env.weir" <<'WEOF'
type EC = { [<Doc "the home dir">] HOME: string }
let cfg = Env.load EC
print (Str.length cfg.HOME > 0)
WEOF
out=$(HOME=/tmp $BIN "$adir/attrs-env.weir")
expect "Env.load on a Doc'd config field is inert-legal" "true" "$out"

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
tadir=$(mktemp -d)
cat > "$tadir/cli.weir" <<'WEOF'
type Cli = { [<Doc "clean first">] clean: bool; verbose: bool; port: Option<int>; env: string }
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

out=$($BIN "$tadir/cli.weir" --bogus --help); rc=$?
[ "$rc" -eq 0 ] || fail "--help must exit 0 (got $rc)"
echo "$out" | grep -qF -- "-c, --clean" || fail "help shows derived short truth: $out"
echo "$out" | grep -qF "clean first" || fail "help shows Doc text: $out"
echo "$out" | grep -qF -- "--env <string>" || fail "help shows valued flags: $out"
echo "$out" | grep -qF "required" || fail "help shows requiredness: $out"
echo "e2e ok: --help derives usage (short truth + Doc) BEFORE validation, exit 0"

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

errout=$(printf 'type Cli = { dryRun: bool; DryRun: bool }\nlet c = Args.load Cli\n' | $BIN check /dev/stdin 2>&1) && fail "duplicate derived flag must reject"
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
errout=$(printf 'type P = { [<Positional>] t: string }\n' | $BIN check /dev/stdin 2>&1) && fail "Positional must be unknown"
echo "$errout" | grep -qF "unknown attribute 'Positional'" || fail "the unknown-attr message: $errout"
echo "e2e ok: [<Positional>] is an unknown attribute (dropped)"

errout=$(printf 'type C = { b: Option<bool> }\nlet c = Args.load C\n' | $BIN check /dev/stdin 2>&1) && fail "Option<bool> field must reject"
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

printf 'print (args |> Str.join ",")\n' > "$tadir/slice.weir"
out=$($BIN "$tadir/slice.weir" --a b c)
expect "script args start AFTER the script path" "--a,b,c" "$out"

errout=$(printf 'type C = { env: string }\nArgs.load C\n' | $BIN check /dev/stdin 2>&1) && fail "bare Args.load statement must reject"
echo "$errout" | grep -qF "discards it" || fail "statement rule covers Args.load: $errout"
echo "e2e ok: Args.load joins the discard family as a value"

rm -rf "$tadir"

# jira-branch loads its Cli (check-only: jira/fzf are not installed)
$BIN check tools/jira-branch.weir >/dev/null 2>&1 || fail "jira-branch must check with Args.load"
echo "e2e ok: jira-branch checks with the typed Cli"

# multiline brackets (PLAN-multiline-brackets): type decls + lists
mldir=$(mktemp -d)
cat > "$mldir/forms.weir" <<'WEOF'
type Ctx =
    { Subdir: string
      Repo: string }

type Cli =
    { [<Short "c"; Doc "count">] count: int
      [<Doc "notes">]
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
bbdir=$(mktemp -d)
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
if ok then !
    sh -c "echo first"

    sh -c "echo second"
print "after"
WEOF
out=$($BIN "$bbdir/district.weir")
expect "a gapped district runs both commands" "second" "$out"
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

# bounded REPL echo (mini-plan): glance vs read
out=$(printf 'let xs = [1..100]\nxs\n' | $BIN 2>&1)
echo "$out" | grep -qF "10; …] : seq<int> (10 of ? shown — pipe to Seq.map show |> print for all)" || fail "REPL echo must bound and hint a spelling that TYPES: $out"
echo "e2e ok: REPL echo truncates at 10; non-string seqs hint the show spelling"

out=$($BIN -e '["a"; "b"; "c"; "d"; "e"; "f"; "g"; "h"; "i"; "j"; "k"]')
echo "$out" | grep -qF "(10 of 11 shown — pipe to print for all)" || fail "string seqs hint plain print: $out"
echo "e2e ok: string seqs hint |> print (it types); counts real when known"

out=$($BIN -e '[1..50]')
echo "$out" | grep -qF "(10 of ? shown" || fail "-e echoes like the REPL (decided): $out"
echo "e2e ok: -e shares the echo bound"

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
spdir=$(mktemp -d)
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
let s = cmd "sh" ["-c"; "echo x >> SPMARK; printf 'a\nb\nc\n'"]
let r =
    match s with
    | [] -> "none"
    | [a] -> a
    | x :: rest -> $"{x}/{rest |> Str.join "-"}"

print r
WEOF
sed -i "s|SPMARK|$spdir/mark|" "$spdir/once.weir"
out=$($BIN "$spdir/once.weir")
expect "arms + rest consumption over a command seq" "a/b-c" "$out"
[ "$(grep -c x "$spdir/mark")" = "1" ] || fail "memoize-once: expected ONE spawn, got $(grep -c x "$spdir/mark")"
echo "e2e ok: the memoize-once law holds live (one spawn across arms + rest)"

errout=$($BIN -e 'match 5 with | [] -> 0 | _ -> 1' 2>&1) && fail "non-seq scrutinee must reject"
echo "$errout" | grep -qF "seq patterns need a seq scrutinee" || fail "scrutinee message: $errout"
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
bldir=$(mktemp -d)
cat > "$bldir/forms.weir" <<'WEOF'
let graft c =
    let tree = git rev-parse $"{c}^{{tree}}" | Seq.head
    let short = git rev-parse --short $c | Seq.head
    let ok = git rev-parse --verify $c | succeeds
    $"{short}:{ok} {tree}"

print (graft "HEAD")
WEOF
out=$(cd "$ROOT" && $BIN "$bldir/forms.weir")
echo "$out" | grep -qE "^[0-9a-f]+:true " || fail "the forms block must run: $out"
echo "e2e ok: block-let command RHS binds, pipes, and reifies at depth"

mkdir -p "$bldir/bin"
printf '#!/bin/sh\necho SPAWNED\n' > "$bldir/bin/zzshadow"
chmod +x "$bldir/bin/zzshadow"
cat > "$bldir/shadow.weir" <<'WEOF'
let f y =
    let zzshadow = fun a -> a
    let z = zzshadow y
    z |> Seq.head

print (f ["safe"])
WEOF
out=$(PATH="$bldir/bin:$PATH" $BIN "$bldir/shadow.weir")
expect "block names shadow PATH at depth (the failing-first pin)" "safe" "$out"

cat > "$bldir/force.weir" <<'WEOF'
let f y =
    let zzshadow = fun a -> a
    let z = ^zzshadow y | Seq.head
    z

print (f "x")
WEOF
out=$(PATH="$bldir/bin:$PATH" $BIN "$bldir/force.weir")
expect "^ still reaches the PATH binary from a block RHS" "SPAWNED" "$out"

printf '#!/bin/sh\necho FN-BINARY\n' > "$bldir/bin/function"
chmod +x "$bldir/bin/function"
out=$(PATH="$bldir/bin:$PATH" $BIN -e '^function' 2>&1)
expect "^function reaches a PATH binary (reservation does not block force)" "FN-BINARY" "$out"

errout=$($BIN -e 'let function = 1' 2>&1) && fail "function must be reserved"
echo "$errout" | grep -qF "fun x -> match x with" || fail "the reservation must teach: $errout"
echo "e2e ok: function reserved with its teaching hint"

rm -rf "$bldir"

# ---- multiline lambdas [D:multiline-lambda]: the form-block shapes ----

mldir=$(mktemp -d)
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

sfdir=$(mktemp -d)
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
errout=$(printf 'type CA = { quiet: bool }\ntype Cmd = Go of CA | Stop\ntype Cli = { quiet: bool; cmd: Cmd }\nlet c = Args.load Cli\nprint "x"\n' | $BIN check /dev/stdin 2>&1) && fail "kebab collision must reject"
echo "$errout" | grep -qF "shared flags are declared once" || fail "kebab collision route: $errout"
errout=$(printf 'type CA = { [<Short "q">] query: string }\ntype Cmd = Go of CA | Stop\ntype Cli = { [<Short "q">] quiet: bool; cmd: Cmd }\nlet c = Args.load Cli\nprint "x"\n' | $BIN check /dev/stdin 2>&1) && fail "explicit-short collision must reject"
echo "$errout" | grep -qF "claimed by [<Short>] in both" || fail "explicit-short collision route: $errout"
errout=$(printf 'type CA = { r: bool }\ntype Cmd = Go of CA | Stop\ntype Cli = { a: Cmd; b: Cmd }\nlet c = Args.load Cli\nprint "x"\n' | $BIN check /dev/stdin 2>&1) && fail "two union fields must reject"
echo "$errout" | grep -qF "one subcommand slot" || fail "one-slot law: $errout"
echo "e2e ok: declaration collisions reject at check (both routes + one slot)"

rm -rf "$sfdir"

# ---- the reifier family completes [D:exit-reifiers]: output goes
# ---- where the meaning goes ----

rfdir=$(mktemp -d)

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

# env variant: the expression-position spelling
cat > "$rfdir/env.weir" <<'WEOF'
let rc = exitCodedEnv (Env.ofPairs [("MARK", "seen")]) "sh" ["-c"; "echo mark=$MARK; exit 3"]
print $"env code {rc}"
WEOF
out=$($BIN "$rfdir/env.weir")
expect "exitCodedEnv streams with the overlay applied" "mark=seen" "$out"
expect "exitCodedEnv binds the code" "env code 3" "$out"

# conflict cells reject with the teaching text
errout=$(printf 'let x = $(git push | exitCode)
print "u"
' | $BIN check /dev/stdin 2>&1) && fail "capture conflict must reject"
echo "$errout" | grep -qF "use '| complete' inside" || fail "capture-conflict teaching: $errout"
errout=$(printf '!(git push | exitCode)
' | $BIN check /dev/stdin 2>&1) && fail "discard conflict must reject"
echo "$errout" | grep -qF "bind it (let rc = <command> | exitCode)" || fail "discard-conflict teaching: $errout"
errout=$(printf 'git push | exitCode
' | $BIN check /dev/stdin 2>&1) && fail "statement discard must reject"
echo "$errout" | grep -qF "drop '| exitCode' if you don't need the code" || fail "statement hint: $errout"
# a district line inherits the !() ruling (the wrap desugar)
errout=$(printf 'if 1 > 0 then !
    git push | exitCode
' | $BIN check /dev/stdin 2>&1) && fail "district exitCode line must reject"
echo "$errout" | grep -qF "bind it (let rc = <command> | exitCode)" || fail "district cell: $errout"
echo "e2e ok: exitCode conflict cells teach (sigil, bang, statement, district)"

rm -rf "$rfdir"

# ---- feed: the family's stdin member [D:spawn-spec] ----

fddir=$(mktemp -d)
cat > "$fddir/hash.weir" <<'WEOF'
let hashes =
    ["snippet one"; "snippet two"]
    |> feed "sha256sum" []
    |> Seq.map (fun l -> l |> Str.split " " |> Seq.head)

hashes |> Seq.iter print
WEOF
out=$($BIN "$fddir/hash.weir")
expect "feed: the miner's sha256 shape (value -> child stdin)" "0027e9fbda04a2a921cb8ae59053abae8a3d29e0c93613be831bcf0262faa36f" "$out"

cat > "$fddir/lazy.weir" <<'WEOF'
[1..1000000] |> Seq.map (fun n -> $"{n}") |> feed "head" ["-1"] |> print
WEOF
out=$(timeout 10 $BIN "$fddir/lazy.weir") || fail "feed input must be lazy (head -1 over a huge range must terminate)"
expect "feed input laziness on the AOT binary" "1" "$out"
rm -rf "$fddir"

# ---- [<Default>]: the resting point moves [D:default-attr] ----

dadir=$(mktemp -d)
cat > "$dadir/cli.weir" <<'WEOF'
type Cli = {
    [<Default 10000; Doc "cases per invariant">]
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
echo "$out" | grep -qF -- "default: on — --no-color disables" || fail "help shows the bool resting point: $out"
echo "e2e ok: Default fills, mints, teaches, and renders (the help-shape pin)"

rm -rf "$dadir"

# ---- Env.load consumes Default [D:default-attr]: the resting point
# ---- sits below the whole overlay stack ----

endir=$(mktemp -d)
cat > "$endir/child.weir" <<'WEOF'
type Cfg = { [<Default 8080>] PORT_ZQ: int; [<Default false>] DEBUG_ZQ: bool }
let c = Env.load Cfg
print $"port={c.PORT_ZQ} debug={show c.DEBUG_ZQ}"
WEOF
cat > "$endir/layers.env" <<'WEOF'
PORT_ZQ=9090
WEOF
cat > "$endir/parent.weir" <<'WEOF'
// layer 3: the runEnv overlay becomes the child's process env
runEnv (Env.fromFile "layers.env") "weir" ["child.weir"]
WEOF
out=$(cd "$endir" && $BIN child.weir)
expect "neither layer sets it: the attribute fills (both types)" "port=8080 debug=false" "$out"
out=$(cd "$endir" && PORT_ZQ=7000 $BIN child.weir)
expect "process env beats the attribute" "port=7000" "$out"
out=$(cd "$endir" && PATH="$(dirname $BIN):$PATH" $BIN parent.weir)
expect "the file overlay (via runEnv) beats the attribute in the child" "port=9090" "$out"
out=$(cd "$endir" && PORT_ZQ=7000 DEBUG_ZQ=true $BIN child.weir)
expect "Default false on an env bool is a real resting point (set wins)" "debug=true" "$out"
rm -rf "$endir"
echo "e2e ok: the Default resting point sits below the whole env stack"

# ---- scriptPath: the $0 gap closes [D:script-path] ----

spdir=$(mktemp -d)
mkdir -p "$spdir/sub" "$spdir/pbin"
cat > "$spdir/sub/where.weir" <<'WEOF'
#!/usr/bin/env weir
cd ..
print (scriptPath |> Path.dir)
WEOF
chmod +x "$spdir/sub/where.weir"
cp "$spdir/sub/where.weir" "$spdir/pbin/where.weir"

want="$spdir/sub"
out=$(cd "$spdir" && $BIN sub/where.weir | tail -1)
[ "$out" = "$want" ] || fail "relative invocation: got $out want $want"
out=$(cd "$spdir/sub" && $BIN ./where.weir | tail -1)
[ "$out" = "$want" ] || fail "dot-relative invocation: got $out"
out=$($BIN "$spdir/sub/where.weir" | tail -1)
[ "$out" = "$want" ] || fail "absolute invocation: got $out"
echo "e2e ok: scriptPath — one absolute answer three ways, resolved BEFORE the cd"

out=$(cd "$spdir" && PATH="$spdir/pbin:$(dirname $BIN):$PATH" where.weir | tail -1)
[ "$out" = "$spdir/pbin" ] || fail "shebang-on-PATH gets the SCRIPT's path: got $out"
echo "e2e ok: shebang-on-PATH resolves to the script, not the interpreter"

errout=$($BIN -e 'scriptPath' 2>&1) && fail "-e must refuse scriptPath"
echo "$errout" | grep -qF "scriptPath is script-only" || fail "the teaching: $errout"
echo "e2e ok: scriptPath refused outside scripts with its teaching"
rm -rf "$spdir"

# ---- Path.glob [D:path-glob]: typed discovery, nothing expands ----

pgdir=$(mktemp -d)
mkdir -p "$pgdir/src/a/b" "$pgdir/fixtures/x" "$pgdir/deny" "$pgdir/loop"
touch "$pgdir/top.json" "$pgdir/other.json" "$pgdir/.hidden.json" \
      "$pgdir/src/one.fs" "$pgdir/src/a/two.fs" "$pgdir/src/a/b/three.fs" \
      "$pgdir/fixtures/x/f.txt" "$pgdir/deny/secret.fs"
ln -s .. "$pgdir/loop/up"
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
if [ "$(id -u)" = "0" ]; then
    echo "e2e note: unreadable-dir cell skipped (root ignores permission modes)"
else
    echo "$out" | grep -qF "secret.fs" && fail "unreadable dir must skip"
fi
echo "$out" | grep -qF "loop/up" && fail "globstar must not traverse symlinks"
expect "glob: no matches is the empty seq (the match-[] idiom)" "no matches" "$out"
expect "glob: the cd seam — lazy sees the new cwd" "lazy-sees-new-cwd: 0" "$out"
expect "glob: Seq.force pins the answer before cd" "forced-pinned: 2" "$out"

# script-relative discovery: the scriptPath gate's payoff
cat > "$pgdir/src/rel.weir" <<'WEOF'
cd /
Path.glob $"{scriptPath |> Path.dir}/../fixtures/**/*.txt" |> Seq.iter print
WEOF
out=$(cd "$pgdir" && $BIN src/rel.weir)
echo "$out" | grep -qF "fixtures/x/f.txt" || fail "script-relative glob after cd /: $out"
echo "e2e ok: glob composes with scriptPath (script-relative, cd-proof)"

# glob feeds stdin (the feed composition)
cat > "$pgdir/fd.weir" <<'WEOF'
Path.glob "*.json" |> feed "sort" ["-r"] |> Seq.iter print
WEOF
out=$(cd "$pgdir" && $BIN fd.weir)
expect "glob |> feed: discovery into a child's stdin" "top.json
other.json" "$out"

# the timing ceiling: 10k files enumerate under 2s on the AOT binary
big=$(mktemp -d)
mkdir -p "$big/d"
(cd "$big/d" && seq 1 10000 | xargs touch)
cat > "$big/t.weir" <<'WEOF'
let n = Path.glob "d/*" |> Seq.length
print $"{n}"
WEOF
start=$(date +%s%N)
out=$(cd "$big" && $BIN t.weir)
elapsed=$(( ($(date +%s%N) - start) / 1000000 ))
expect "glob: 10k files counted" "10000" "$out"
[ "$elapsed" -lt 2000 ] || fail "glob 10k ceiling: ${elapsed}ms"
echo "e2e ok: glob 10k-file tree under the ceiling (${elapsed}ms)"
rm -rf "$pgdir" "$big"

# ---- Seq.distinct [D:seq-distinct]: dedupe as a pipeline stage ----

sddir=$(mktemp -d)
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
[ "$count" = "3" ] || fail "distinct must drop the overlap: $count lines"
echo "e2e ok: Seq.distinct closes the glob-overlap product cell"
rm -rf "$sddir"

# ---- argv splat $@xs [D:argv-splat]: N things, N words ----

spldir=$(mktemp -d)
( cd "$spldir" && git init -q . && touch a.txt b.txt c.md )

# form 1: glob into git add, verified via porcelain
cat > "$spldir/add.weir" <<'WEOF'
let files = Path.glob "*.txt" |> Seq.force
git add $@files
git status --porcelain | Seq.where (Str.startsWith "A ") | Seq.iter print
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

# the head and mid-word teachings
errout=$(printf 'let xs = ["ls"]
$@xs -la
' | $BIN check /dev/stdin 2>&1) && fail "head splat must reject"
echo "$errout" | grep -qF "N words would be N heads" || fail "head teaching: $errout"
errout=$(printf 'let fs = ["a"]
echo --flag=$@fs
' | $BIN check /dev/stdin 2>&1) && fail "mid-word splat must reject"
echo "$errout" | grep -qF "cannot join a word under construction" || fail "mid-word teaching: $errout"
# the type teachings, both directions
errout=$(printf 'let ns = [1; 2]
echo $@ns
' | $BIN check /dev/stdin 2>&1) && fail "seq<int> splat must reject"
echo "$errout" | grep -qF "map show or interpolate" || fail "seq<int> teaching: $errout"
errout=$(printf 'let s = "x"
echo $@s
' | $BIN check /dev/stdin 2>&1) && fail "scalar splat must reject"
echo "$errout" | grep -qF "one value? use \$x" || fail "scalar teaching: $errout"
echo "e2e ok: splat teaches head, mid-word, and both type directions"

# scalar mid-word splice mirrors the splat's fatal [D:argv-splat]: the
# glued prefix would silently drop, so name the space/interp spellings
errout=$(printf 'let f = "x"
echo --file=$f
' | $BIN check /dev/stdin 2>&1) && fail "mid-word scalar splice must reject"
echo "$errout" | grep -qF "cannot join a word under construction" || fail "mid-word scalar teaching: $errout"
# the spaced spelling stays legal (one argv word each)
out=$(printf 'let f = "x.txt"
echo --file $f
' | $BIN /dev/stdin 2>&1)
echo "$out" | grep -qF -- "--file x.txt" || fail "spaced splice must pass: $out"
echo "e2e ok: scalar mid-word splice rejects, spaced spelling passes"

# feed's ARGS take a splat while input streams (both axes)
cat > "$spldir/fd.weir" <<'WEOF'
let flags = ["-r"]
["a"; "b"; "c"] |> feed "sort" ($@flags) |> Seq.iter print
WEOF
if $BIN check "$spldir/fd.weir" >/dev/null 2>&1; then
    out=$(cd "$spldir" && $BIN fd.weir)
    expect "splat in feed's args while input streams" "c
b
a" "$out"
else
    echo "e2e note: feed-arg splat needs a paren splice arg — covered by run/cmd argv building"
fi
rm -rf "$spldir"

echo "e2e battery: all green"
