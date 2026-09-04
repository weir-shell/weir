#!/usr/bin/env python3
# The interop referee [D:yaml-v1]: `to yaml` and `to json` round-trip
# through parsers weir did not write (PyYAML, Python's json), over a
# hostile corpus. The adversarial review's F2 found the yaml pair had
# no external referee — weir validated its own output (`to yaml |> from
# yaml`), so four emitter defects survived every green run. This gate
# is the durable fix: a fifth instance fails CI, not a review.
#
# Usage: interop-referee.py <weir-binary>
# Exit 0 = every payload round-tripped through both foreign parsers.
import base64, json, subprocess, sys, tempfile, os

try:
    import yaml
except ImportError:
    # the CALLER decides whether absence is a skip (e2e names it); this
    # script itself never passes without the oracle
    print("interop-referee: PyYAML is not importable", file=sys.stderr)
    sys.exit(2)

BIN = sys.argv[1]

# the hostile corpus: 1.1/1.2 boolean and null forms, number-alikes
# (incl. sexagesimal), float specials, structural sigils, whitespace,
# every line-break flavor, controls, emoji, long lines, block shapes
payloads = []
payloads += ["yes", "no", "on", "off", "true", "false", "null", "~",
             "Yes", "No", "On", "Off", "True", "False", "Null",
             "YES", "NO", "ON", "OFF", "TRUE", "FALSE", "NULL"]
payloads += ["007", "1e5", "1.5", "-3", "+3", ".5", "0x1f", "0o17",
             "1_000", "1:2", "12:34:56", "0b101", "1e-5", "-0"]
payloads += [".inf", "-.inf", "+.inf", ".Inf", ".INF", "-.INF", ".nan", ".NaN", ".NAN"]
payloads += ["- x", "? y", ": z", "[a]", "{a: b}", "#c", "a: b", "a #b",
             "&anchor", "*alias", "!tag", "|", "|-", ">", "%YAML", "---",
             "...", "@x", "`x", "'a'", '"a"', "a'b", 'a"b', "\\", "\\n", "a:"]
payloads += [" lead", "trail ", "  both  ", "a\n b", "\ta", "a\t",
             "a\n   ", "a\n \n", " \n x", "\n", "\n\n", "a\n\n", "a\nb",
             "a\nb\n", "a\nb\n\n", "  a\nb", " lead\nrest"]
payloads += ["a\rb", "a\r\nb", "a\u0085b", "a\u2028b", "a\u2029b", "\r", "\u2028"]
payloads += ["a\x01b", "\x1b[31mred\x1b[0m", "\x7f", "a\x1fb"]
payloads += ["\U0001F600", "a\U0001F600b\u2028\U0001F600"]
payloads += ["x" * 5000, ("ab " * 2000).strip()]

# positive control, TWO-SIDED and structurally independent of every
# finding: the oracle must accept a match and reject a mismatch
assert yaml.safe_load("k: abc")["k"] == "abc"
assert yaml.safe_load("k: abc")["k"] != "xyz"
assert json.loads('{"k": "abc"}')["k"] == "abc"

# `to yaml` yields the document's lines; `to json` writes ONE
# document [D:to-jsonl] — base64 collapses each doc to one line
script = ["type T = { A: string }"]
for p in payloads:
    b64 = base64.b64encode(p.encode()).decode()
    script.append(f'print (Str.toBase64 (Str.join "\\n" ({{ A = Str.fromBase64 "{b64}" }} |> to yaml)))')
    script.append(f'print (Str.toBase64 (Str.join "\\n" ({{ A = Str.fromBase64 "{b64}" }} |> to json)))')

with tempfile.NamedTemporaryFile("w", suffix=".weir", delete=False, encoding="utf-8") as f:
    f.write("\n".join(script) + "\n")
    path = f.name

try:
    r = subprocess.run([BIN, path], capture_output=True, text=True, timeout=300)
finally:
    os.unlink(path)

if r.returncode != 0:
    print(f"interop-referee: weir exited {r.returncode}:\n{r.stderr[:2000]}", file=sys.stderr)
    sys.exit(1)

lines = [l for l in r.stdout.splitlines() if l.strip()]
if len(lines) != 2 * len(payloads):
    print(f"interop-referee: expected {2 * len(payloads)} documents, got {len(lines)}", file=sys.stderr)
    sys.exit(1)

fails = 0
for i, p in enumerate(payloads):
    # a written document ends with a newline; the base64 line-join
    # dropped it, and `|` chomping depends on it
    ydoc = base64.b64decode(lines[2 * i]).decode() + "\n"
    jdoc = base64.b64decode(lines[2 * i + 1]).decode()
    try:
        got = yaml.safe_load(ydoc)["A"]
        if got != p:
            print(f"YAML MISMATCH {p!r} -> {got!r}\n  doc: {ydoc!r}", file=sys.stderr)
            fails += 1
    except Exception as e:
        print(f"YAML PARSE-FAIL {p!r}: {type(e).__name__}\n  doc: {ydoc!r}", file=sys.stderr)
        fails += 1
    try:
        got = json.loads(jdoc)["A"]
        if got != p:
            print(f"JSON MISMATCH {p!r} -> {got!r}\n  doc: {jdoc!r}", file=sys.stderr)
            fails += 1
    except Exception as e:
        print(f"JSON PARSE-FAIL {p!r}: {type(e).__name__}\n  doc: {jdoc!r}", file=sys.stderr)
        fails += 1

if fails:
    print(f"interop-referee: {fails} failure(s) over {len(payloads)} payloads", file=sys.stderr)
    sys.exit(1)

print(f"interop-referee ok: {len(payloads)} payloads round-trip through PyYAML and json")
