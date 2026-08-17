#!/usr/bin/env bash
# Commit areas, DERIVED rather than remembered [D:commit-areas]: the
# area prefix on a commit subject is checked against the areas its DIFF
# actually touches — a fact about the commit, not an assertion by the
# author. The path map lives HERE (one copy, consumed by this check;
# dev/PROCESS.md documents it for humans and cites this file as the
# source of truth).
#
#   ci/commit-area.sh <commit>...        check specific commits
#   ci/commit-area.sh --range A..B       check every commit in a range
#
# Subject shape:  area: subject   |   area,area: subject
# The declared set must EQUAL the derived set (a subset would let a
# commit declare `docs` while changing the parser — the exact failure
# this check exists to prevent). On mismatch the error PRINTS the
# derived prefix, so the ceremony repairs itself.
#
# THE RIDING RULE (what keeps feature commits one-line): if any
# src/Weir file moved, the derived set is the src areas ONLY — tests,
# pins (e2e/skill-doc rows), docs, and ledger entries RIDE the feature
# they verify or record. A commit with no src changes derives from the
# non-src map (ci / docs / ledger / tests).
set -euo pipefail

area_of_src() {
    case "$1" in
        src/Weir/Parser.fs|src/Weir/Script.fs|src/Weir/Ast.fs) echo parser ;;
        src/Weir/Check.fs|src/Weir/Types.fs) echo checker ;;
        src/Weir/Eval.fs|src/Weir/Session.fs|src/Weir/Proc.fs) echo eval ;;
        src/Weir/Builtins.fs|src/Weir/Prelude.fs) echo builtins ;;
        src/Weir/Repl.fs|src/Weir/Complete.fs) echo repl ;;
        src/Weir/Lsp.fs) echo lsp ;;
        src/Weir/Fmt.fs) echo fmt ;;
        src/Weir/Yaml.fs|src/Weir/Http.fs) echo adapters ;;
        src/Weir/*) echo cli ;;   # Program/Can/Argv/Extern/Contracts/Diagnose/fsproj — the command surface
        *) echo "" ;;
    esac
}

area_of_other() {
    case "$1" in
        docs/DECISIONS.md|dev/*) echo ledger ;;
        ci/*|.github/*|tools/*|tests/pty/*|publish.*|install.*|weir.slnx|.gitignore|.dockerignore) echo ci ;;
        tests/*) echo tests ;;
        docs/*|skills/*|examples/*|editors/*|README.md|SECURITY.md|NOTICE|LICENSE|CONTRIBUTING.md|CODE_OF_CONDUCT.md|THIRD-PARTY-NOTICES.txt|CLAUDE.md) echo docs ;;
        *) echo docs ;;   # anything new at the root reads as docs until the map learns it
    esac
}

check_commit() {
    local c="$1"
    local subject files derived declared src_areas other_areas
    subject=$(git log -1 --format=%s "$c")
    files=$(git show --format= --name-only "$c")

    src_areas=""
    other_areas=""
    while IFS= read -r f; do
        [ -z "$f" ] && continue
        a=$(area_of_src "$f")
        if [ -n "$a" ]; then
            src_areas="$src_areas $a"
        else
            other_areas="$other_areas $(area_of_other "$f")"
        fi
    done <<< "$files"

    if [ -n "${src_areas// /}" ]; then
        derived=$(echo "$src_areas" | tr ' ' '\n' | grep -v '^$' | sort -u | paste -sd, -)
    else
        derived=$(echo "$other_areas" | tr ' ' '\n' | grep -v '^$' | sort -u | paste -sd, -)
    fi

    if [ -z "$derived" ]; then
        echo "commit-area SKIP: $c has no files ($subject)"
        return 0
    fi

    declared=$(echo "$subject" | sed -n 's/^\([a-z][a-z,]*\): .*/\1/p')

    if [ -z "$declared" ]; then
        echo "commit-area FAIL: $c has no area prefix — subject should start '$derived: ' ($subject)" >&2
        return 1
    fi

    declared=$(echo "$declared" | tr ',' '\n' | sort -u | paste -sd, -)

    if [ "$declared" != "$derived" ]; then
        echo "commit-area FAIL: $c declares '$declared' but the diff derives '$derived' ($subject)" >&2
        return 1
    fi

    echo "commit-area ok: $c $declared"
}

rc=0
if [ "${1:-}" = "--range" ]; then
    for c in $(git rev-list "$2"); do
        check_commit "$c" || rc=1
    done
else
    for c in "$@"; do
        check_commit "$c" || rc=1
    done
fi
exit $rc
