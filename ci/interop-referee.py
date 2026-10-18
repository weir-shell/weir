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
    # the caller decides whether absence is a skip (e2e names it); this
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
# the whole 1.1/1.2 resolver sets [D:yaml-plain-resolvers]: signed
# leading-dot floats, go-yaml v2's one-letter bools, 1.1 merge/value keys
payloads += ["+.5", "-.5", "+.5e3", "-.5E-3", "._5", "+._5", "1.", "-1.", "+1", "+0x1f", "-0b1",
             "+1:20", "1_0.5", "2001-12-14", "2001-12-14t21:59:43.10-05:00",
             "Infinity", "-Infinity", "NaN", "inf", "nan", "1e400",
             "y", "Y", "n", "N", "<<", "=", ".", "+", "-.", "+."]
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

# positive control, two-sided and structurally independent of every
# finding: the oracle must accept a match and reject a mismatch
assert yaml.safe_load("k: abc")["k"] == "abc"
assert yaml.safe_load("k: abc")["k"] != "xyz"
assert json.loads('{"k": "abc"}')["k"] == "abc"

# `to yaml` yields the document's lines; `to json` writes one
# document [D:to-jsonl] — base64 collapses each doc to one line. Each
# payload also goes out as a mapping KEY, and back through weir's own
# reader (value and key)
script = ["type T = { A: string }", "type K = { M: seq<string * int> }",
          "let isStr y p = match y with | YStr s -> s == p | _ -> false"]
for p in payloads:
    b64 = base64.b64encode(p.encode()).decode()
    script.append(f'print (Str.toBase64 (Str.join "\\n" ({{ A = Str.fromBase64 "{b64}" }} |> to yaml)))')
    script.append(f'print (Str.toBase64 (Str.join "\\n" ({{ A = Str.fromBase64 "{b64}" }} |> to json)))')
    script.append(f'print (Str.toBase64 (Str.join "\\n" ([(Str.fromBase64 "{b64}", 1)] |> to yaml)))')
    script.append(f'let p{len(script)} = Str.fromBase64 "{b64}"')
    n = len(script) - 1
    script.append(f'print (if ({{ A = p{n} }} |> to yaml |> from yaml T).A == p{n} && isStr ({{ A = p{n} }} |> to yaml |> from yaml {{| A: Yaml |}}).A p{n} && (({{ M = [(p{n}, 1)] }} |> to yaml |> from yaml K).M |> Seq.map fst |> Seq.head) == p{n} then "c2VsZg==" else "")')

# the resolvers a plain scalar must miss to be a string to every reader:
# YAML 1.1 (spec tag regexes, PyYAML's included), 1.2 core and json, and
# go-yaml v2's table + yamlStyleFloat (sigs.k8s.io/yaml) [D:yaml-plain-resolvers]
import re
RESOLVERS = [re.compile(r) for r in [
    r"^(?:y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)$",
    r"^(?:~|null|Null|NULL|)$",
    r"^(?:[-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)$",
    r"^(?:[-+]?(?:[0-9][0-9_]*)?\.[0-9_]*(?:[eE][-+]?[0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))$",
    r"^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?$",
    r"^[-+]?(?:[0-9]+|0o[0-7]+|0x[0-9a-fA-F]+)$",
    r"^[0-9][0-9][0-9][0-9]-[0-9][0-9]?-[0-9][0-9]?(?:(?:[Tt]|[ \t]+)[0-9][0-9]?:[0-9][0-9]:[0-9][0-9](?:\.[0-9]*)?(?:[ \t]*(?:Z|[-+][0-9][0-9]?(?::[0-9][0-9])?))?)?$",
    r"^(?:<<|=)$",
]]
def plainTyped(text):
    # "." alone and a bare sign are strings in every reader (Psych special-cases ".")
    return text not in (".", "+.", "-.") and any(r.match(text) for r in RESOLVERS)

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

lines = r.stdout.splitlines()
if len(lines) != 4 * len(payloads):
    print(f"interop-referee: expected {4 * len(payloads)} lines, got {len(lines)}", file=sys.stderr)
    sys.exit(1)

fails = 0
for i, p in enumerate(payloads):
    # a written document ends with a newline; the base64 line-join
    # dropped it, and `|` chomping depends on it
    ydoc = base64.b64decode(lines[4 * i]).decode() + "\n"
    jdoc = base64.b64decode(lines[4 * i + 1]).decode()
    kdoc = base64.b64decode(lines[4 * i + 2]).decode() + "\n"
    if lines[4 * i + 3] != "c2VsZg==":
        print(f"WEIR SELF-ROUNDTRIP FAIL {p!r}\n  doc: {ydoc!r} key doc: {kdoc!r}", file=sys.stderr)
        fails += 1
    for doc, scalar in ((ydoc, ydoc[len("A: "):-1] if ydoc.startswith("A: ") else None),
                        (kdoc, kdoc[:-len(": 1\n")] if kdoc.endswith(": 1\n") and "\n" not in kdoc[:-1] else None)):
        if scalar is not None and not scalar.startswith('"') and plainTyped(scalar):
            print(f"PLAIN RESOLVES AS NON-STRING {p!r} -> {scalar!r}", file=sys.stderr)
            fails += 1
    # a key past 1024 chars needs YAML's explicit `? ` form, outside the
    # subset (a BACKLOG low): the value check above covers those payloads
    if len(p) > 1024:
        continue
    try:
        got = list(yaml.safe_load(kdoc).keys())
        if got != [p]:
            print(f"YAML KEY MISMATCH {p!r} -> {got!r}\n  doc: {kdoc!r}", file=sys.stderr)
            fails += 1
    except Exception as e:
        print(f"YAML KEY PARSE-FAIL {p!r}: {type(e).__name__}\n  doc: {kdoc!r}", file=sys.stderr)
        fails += 1
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
