#!/usr/bin/env python3
# The corpus miner (PLAN-corpus-remine, 2026-07-22) — THE COMMITTED
# ARTIFACT the first mine never left behind (its filter lived and died
# in session state; only corpus-report-5928e91.md survived). This
# reconstruction is calibrated to the first mine's published
# denominator (4256 extracted -> 78 weir-plausible) and makes the
# reject-lists explicit so the filter DIFF is readable as language
# growth: WAVE_REJECTS are the shapes the four feature waves shipped
# (tuples, literal patterns, composition, raw strings) — present in
# BASE mode (the first mine's world), lifted in WIDE mode (today's).
#
# Usage: corpus-mine.py <componenttests-dir> <out-dir> [--wide]
import hashlib
import re
import sys
from pathlib import Path

TRIPLE = re.compile(r'"""(.*?)"""', re.DOTALL)

# Constructs weir does not have (or bounds out) regardless of wave —
# one entry per family, substring or regex match on the whole snippet.
BASE_REJECTS = [
    "module ", "namespace", "open ", "[<", "printfn", "printf", "sprintf",
    "failwith", "raise", "try", "exception", "member", "interface",
    "abstract", "override", "inherit", "static ", "struct", "delegate",
    "class", "new ", "mutable", "<-", ":=", "let rec", "and ",
    "seq {", "async", "task {", "query", "lazy", "yield", "while ",
    "for ", "do ", "use ", "downto", ":>", ":?", "byref", "nativeptr",
    "voidptr", "inline", "^", "%", "#", "`", "'",  # generics/ops/quots/chars
    "float", "decimal", "byte", "uint", "int8", "int16", "int64",
    "nativeint", "System.", "List.", "Array.", "Map.", "Set.",
    "Option.", "Seq.", "ignore", "box", "unbox", "typeof", "nameof",
    "|>",   # F# pipelines lean on stdlib calls weir spells differently
    "..",   # ranges: weir-legal but F# range shapes are stdlib-heavy
    "$",    # interpolation: hole typing diverges (fn-valued holes row)
    "\\",   # escape-heavy strings: encoding families weir bounds out
    "()",   # unit-heavy shapes skew to do-blocks
    " :: ", "[]",  # lists: weir seqs are not F# lists (e8686993 class)
]
BASE_REJECT_RES = [
    re.compile(r"let\s+\w+\s*:"),      # annotated lets (weir: no annotations)
    re.compile(r"\(\s*\|"),            # active patterns
    re.compile(r'"B'),                 # byte-string literals
]

# The four waves: rejected in the first mine's world, ADMITTED now.
WAVE_REJECTS = [
    ",",                                # tuples / bare comma / multi-payload
    ">>", "<<",                        # composition
    '@"',                               # verbatim raw strings
    " * ",                              # tuple types / of a * b payloads
]
WAVE_REJECT_RES = [
    re.compile(r"\|\s*\d"),            # int literal-pattern arms
    re.compile(r'\|\s*"'),             # string literal-pattern arms
    re.compile(r"let\s*\("),           # destructuring let heads
]


def plausible(src: str, wide: bool) -> bool:
    lines = [l for l in src.splitlines() if l.strip()]
    if not lines or len(lines) > 8:
        return False
    if not any(l.lstrip().startswith("let ") for l in lines):
        return False  # weir scripts are let-shaped; bare exprs skew to fsi
    rejects = list(BASE_REJECTS)
    reject_res = list(BASE_REJECT_RES)
    if not wide:
        rejects += WAVE_REJECTS
        reject_res += WAVE_REJECT_RES
    for tok in rejects:
        if tok in src:
            return False
    for rx in reject_res:
        if rx.search(src):
            return False
    return True


def main():
    root, out = Path(sys.argv[1]), Path(sys.argv[2])
    wide = "--wide" in sys.argv
    out.mkdir(parents=True, exist_ok=True)
    extracted = 0
    kept = 0
    seen = set()
    for f in sorted(root.rglob("*.fs")):
        text = f.read_text(errors="replace")
        for m in TRIPLE.finditer(text):
            src = m.group(1).strip("\n")
            extracted += 1
            h = hashlib.sha256(src.encode()).hexdigest()[:12]
            if h in seen:
                continue
            seen.add(h)
            if plausible(src, wide):
                kept += 1
                (out / f"{h}.snippet").write_text(src + "\n")
    mode = "wide" if wide else "base"
    print(f"{mode}: extracted={extracted} unique={len(seen)} kept={kept}")


if __name__ == "__main__":
    main()
