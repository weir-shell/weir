#!/usr/bin/env bash
# End-to-end battery against the AOT binary (command-mode Session 4 set).
set -euo pipefail

BIN="${WEIR_BIN:-$HOME/.local/bin/weir}"

fail() {
    echo "e2e FAIL: $1" >&2
    exit 1
}

expect() {
    local desc="$1" needle="$2" out="$3"
    echo "$out" | grep -qF "$needle" || fail "$desc — expected to find: $needle in: $out"
    echo "e2e ok: $desc"
}

out=$($BIN -e '1 + 2 |> double')
expect "expression eval" "6 : int" "$out"

out=$($BIN -e 'cmd "echo" ["*"]')
expect "argv stays literal" '["*"]' "$out"

out=$($BIN -e 'echo hi (40 + 2) | first 1')
expect "command mode with splice" '["hi 42"]' "$out"

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

out=$($BIN -e 'sh "echo out; echo err 1>&2"' 2>/dev/null)
expect "stderr passthrough keeps stdout stream clean" '["out"]' "$out"

out=$($BIN -e 'match Ok 3 with | Ok v -> v | Error e -> Str.length e')
expect "prelude Result with cross-arm inference" "3 : int" "$out"

out=$($BIN -e 'ls |> Seq.sortBy _.Size |> Seq.map _.Name |> Seq.head' 2>/dev/null | head -1)
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

if $BIN -e 'yes hi | grep hi | complete' 2>/dev/null; then
    fail "multi-segment | complete should be a parse error"
fi
echo "e2e ok: multi-segment complete rejected"

if $BIN -e '^weir-definitely-not-a-command' 2>/dev/null; then
    fail "forced unknown command should not succeed"
fi
echo "e2e ok: forced unknown command rejected"

echo "e2e battery: all green"
