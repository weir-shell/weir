#!/usr/bin/env bash
# End-to-end battery against the AOT binary (command-mode Session 4 set).
set -euo pipefail

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"

# stale-binary guard: the third time someone chased a phantom failure from
# an outdated ~/.local/bin/weir earned this check
if [ -d "$(dirname "$0")/../src/Weir" ] && [ -f "$BIN" ]; then
    newer=$(find "$(dirname "$0")/../src/Weir" -path '*/obj' -prune -o -path '*/bin' -prune -o -name '*.fs' -newer "$BIN" -print -quit)
    if [ -n "$newer" ]; then
        echo "WARNING: $BIN is older than $newer — run ./publish.sh first" >&2
    fi
fi

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

out=$($BIN -e '[] |> Seq.tryHead |> Option.defaultTo 9')
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

# --- weir lsp v1 (2026-07-21, LSP chain 3/3) ---------------------------

if command -v python3 >/dev/null 2>&1; then
    WEIR_BIN="$BIN" python3 "$(dirname "$0")/../tests/lsp/lsp-e2e.py" || fail "lsp integration probes"
    echo "e2e ok: lsp diagnostics/hover/completion over stdio"

    # --- REPL line editor under a pty (2026-07-21) ---------------------
    python3 "$(dirname "$0")/../tests/repl/repl-wordnav.py" "$BIN" || fail "repl word navigation"
    echo "e2e ok: repl Ctrl+Left/Right word navigation"
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
out=$($BIN -e '[1; 3; 2] |> Seq.sortByDescending (fun x -> x) |> Seq.toList')
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

let mode = args |> Seq.tryHead |> Option.defaultTo "count"

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

print (Env.get "AZURE_SUBSCRIPTION_ID" |> Option.defaultTo "(clean)")
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

cat > "$gdir/broken.weir" <<'WEOF'
type T = { Name: string }

let t =
    { Name = "a"

print t.Name
WEOF
errout=$($BIN "$gdir/broken.weir" 2>&1 || true)
echo "$errout" | grep -qF "record literal" || fail "open-brace error must name the record: $errout"
echo "e2e ok: blank inside an open brace errors, naming the brace"

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

echo "e2e battery: all green"
