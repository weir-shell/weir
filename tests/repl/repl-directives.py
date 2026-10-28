#!/usr/bin/env python3
# Session directives [D:repl-directives]: '#' is the prefix for
# everything addressed to the tooling — #help's three forms from the
# one hover source, #quit (Ctrl+D still works), and comment-only
# lines as silent no-ops. The :q teaching arm is retired.
import os
import pty
import re
import select
import subprocess
import sys
import tempfile
import time

WEIR = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/bin/weir")
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "lib"))
from harness import assert_fresh
assert_fresh(WEIR, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))

failures = []


def piped(text):
    p = subprocess.run([WEIR], input=text, capture_output=True, text=True, timeout=30)
    return p.stdout


# --- #help's three forms (piped drives dispatch identically) ---------
t = piped("#help\n#quit\n")
if "Directives:" not in t or "#quit" not in t:
    failures.append(f"#help must list the directives: {t[-200:]!r}")
if "Modules:" not in t or "Seq" not in t or "Http" not in t:
    failures.append(f"#help must list the modules (derived): {t[-200:]!r}")

t = piped("#help Seq\n#quit\n")
if "collect" not in t or "members" not in t:
    failures.append(f"#help Seq must list the module's members: {t[-200:]!r}")

# --- the glance [D:help-glance]: one member per line with its doc's
# first line; bare #help gives every module a blurb line ----------------
t = piped("#help Option\n#quit\n")
lines = t.splitlines()
if not any(l.strip().startswith("bind") and "Apply a function" in l for l in lines):
    failures.append(f"#help Option must glance bind's doc on its own line: {t[-400:]!r}")
if not any(l.strip().startswith("defaultValue") and "The Some value" in l for l in lines):
    failures.append(f"#help Option must glance defaultValue's doc: {t[-400:]!r}")

t = piped("#help\n#quit\n")
if not any("Seq" in l and "lazy sequence" in l for l in t.splitlines()):
    failures.append(f"bare #help must blurb each module (Seq — lazy sequence …): {t[-400:]!r}")
if "#find" not in t:
    failures.append(f"#help must list #find: {t[-400:]!r}")

# --- #find [D:help-find]: piped sessions take the deterministic
# fallback (substring, case-insensitive); bare #find teaches usage -----
t = piped("#find sha256\n#quit\n")
for want in ("Str.sha256", "File.sha256", "Bytes.sha256"):
    if want not in t:
        failures.append(f"#find sha256 (piped fallback) must list {want}: {t[-400:]!r}")
t = piped("#find\n#quit\n")
if "#find <query>" not in t:
    failures.append(f"bare #find must teach usage (never an install-fzf message): {t[-300:]!r}")
if "install" in t.lower():
    failures.append(f"#find must NEVER say to install fzf: {t[-300:]!r}")
t = piped("#find zzznotathing\n#quit\n")
if "no matches" not in t:
    failures.append(f"#find with no hits must say so: {t[-300:]!r}")

t = piped("#help Seq.collect\n#quit\n")
if "Seq.collect (f:" not in t:
    failures.append(f"#help member must render the annotated signature: {t[-200:]!r}")
if "flatten" not in t:
    failures.append(f"#help member must render the hover doc text: {t[-200:]!r}")

# piped #help is the pinned byte surface [D:help-tint]: literal
# backticks, zero ANSI — the tty tint must not move these bytes
t = piped("#help Yaml.merge\n#quit\n")
if "`yaml patch`" not in t:
    failures.append(f"piped #help must keep the literal backticks: {t[-300:]!r}")
if "\x1b[" in t:
    failures.append("piped #help must carry zero ANSI")

# the signature line and example block are part of that pinned surface
# [D:help-tint]: piped they stay the plain spelling, zero ANSI
t = piped("#help Yaml.inferShape\n#quit\n")
if "Yaml.inferShape (lines: seq<string>) : string" not in t:
    failures.append(f"piped #help must keep the plain signature: {t[-300:]!r}")
if "print (Yaml.inferShape sample)" not in t:
    failures.append(f"piped #help must keep the plain example: {t[-300:]!r}")
if "\x1b[" in t:
    failures.append("piped #help signature/example must carry zero ANSI")

# a name the checker would refuse must not get a confident hover: #help and
# the checker read one ownership function [D:ambiguous-ctor]
t = piped("type B = C\ntype Z = C\n#help C\n#quit\n")
if "ambiguous constructor" not in t or "B, Z" not in t:
    failures.append(f"#help must refuse an ambiguous case, naming both: {t[-200:]!r}")

t = piped("type Z = C\n#help C\n#quit\n")
if "C : Z" not in t:
    failures.append(f"#help still answers an UNambiguous case: {t[-200:]!r}")

t = piped("#help Seq.colect\n#quit\n")
if "no member 'colect'" not in t or "collect" not in t:
    failures.append(f"a dotted typo must did-you-mean in its module: {t[-200:]!r}")

# --- #quit quits; :q is fully retired ---------------------------------
t = piped("#quit\nprint \"unreached\"\n")
if "unreached" in t:
    failures.append("#quit must leave the REPL")

# a keyword can never head a command — #alias refuses it
t = piped("#alias let = echo\n#quit\n")
if "keyword" not in t or "alias let = echo" in t:
    failures.append(f"#alias let = … must be refused as a keyword: {t[-200:]!r}")

# piped #history reads the history file and lists this session's lines
# (which are not saved — piped input is not the user's history file)
home = tempfile.mkdtemp(prefix="weir-hist-piped-")
os.makedirs(os.path.join(home, "state", "weir"))
with open(os.path.join(home, "state", "weir", "history"), "w") as f:
    f.write("let fromfile = 1\n")
p = subprocess.run([WEIR], input="let zz = 41\n#history\n#quit\n", capture_output=True, text=True,
                   timeout=30, env=dict(os.environ, XDG_STATE_HOME=os.path.join(home, "state")))
if "let fromfile = 1" not in p.stdout or "let zz = 41" not in p.stdout or "(empty)" in p.stdout:
    failures.append(f"piped #history must read the file and this session: {p.stdout[-300:]!r}")
with open(os.path.join(home, "state", "weir", "history")) as f:
    if "zz" in f.read():
        failures.append("a piped session must not write the history file")

# an argument to #quit teaches its form — never "did you mean '#quit'"
t = piped("#quit now\nprint \"still-here\"\n#quit\n")
if "takes no argument" not in t or "Did you mean" in t:
    failures.append(f"#quit now must teach the bare form: {t[-200:]!r}")
if "still-here" not in t:
    failures.append("#quit now must not quit")

t = piped(":q\nprint \"still-here\"\n#quit\n")
if "`:q` is now" in t:
    failures.append("the :q teaching arm should be gone")
if "still-here" not in t:
    failures.append(":q (now an ordinary error) must not quit the session")

# --- unknown directive names the family ------------------------------
t = piped("#time\n#quit\n")
if "unknown directive '#time'" not in t:
    failures.append(f"an unknown directive must say so: {t[-200:]!r}")
# the #sig/#schema parenthetical is noise for an unrelated typo — gone
if "#sig" in t or "#schema" in t:
    failures.append(f"an unrelated typo must NOT carry the #sig/#schema tail: {t[-200:]!r}")

# --- a file directive at the REPL gets its own redirect, not the flood
t = piped("#sig\n#quit\n")
if "file directive" not in t or "no effect in the REPL" not in t:
    failures.append(f"#sig at the REPL must redirect (file directive): {t[-200:]!r}")
# #schema never existed (a yaml block names its schema with `yaml schema=`):
# it is an unknown directive like any other [D:surface-gate]
t = piped("#schema\n#quit\n")
if "unknown directive '#schema'" not in t:
    failures.append(f"#schema is not a directive — it must read as unknown: {t[-200:]!r}")

# --- #alias [D:command-head-alias] is a recognized directive ----------
# bare #alias lists (empty session -> the teaching line); a live add
# echoes; #help lists it; the single-hop rule rejects an alias-of-alias
t = piped("#alias\n#quit\n")
if "unknown directive" in t:
    failures.append(f"#alias must be a recognized directive: {t[-200:]!r}")
t = piped("#alias k = kubectl\n#alias\n#quit\n")
if "alias k = kubectl" not in t:
    failures.append(f"a live #alias must add and a bare #alias must list it: {t[-200:]!r}")
t = piped("#alias k = kubectl\n#alias kk = k\n#quit\n")
if "single-hop" not in t:
    failures.append(f"a live alias-of-alias must be rejected (single-hop): {t[-200:]!r}")
t = piped("#help\n#quit\n")
if "#alias" not in t:
    failures.append(f"#help must list #alias: {t[-300:]!r}")

# --- comment-only lines: silent no-ops; trailing comments work --------
t = piped("//just a comment\n/// a doc\nprint \"after\"\n#quit\n")
if "Expecting" in t or "error" in t:
    failures.append(f"comment-only lines must be silent no-ops: {t[-300:]!r}")
if "after" not in t:
    failures.append("the session must continue past comment-only lines")

t = piped("let x = 5 // trailing\nx\n#quit\n")
if "5 : int" not in t:
    failures.append(f"a trailing comment on a statement must evaluate: {t[-200:]!r}")

# --- the echo cap [D:echo-cap]: report / set / all / teach ------------
t = piped("#echo\n#echo 25\n#echo\n#echo all\n#echo\n#echo nope\n#echo 0\n#quit\n")
if "echo cap: 100" not in t:
    failures.append(f"#echo bare must report the default: {t[-200:]!r}")
if t.count("echo cap: 25") < 2:
    failures.append(f"#echo 25 must set AND the next bare #echo report it: {t[-300:]!r}")
if t.count("echo cap: all") < 2:
    failures.append(f"#echo all must uncap and report: {t[-300:]!r}")
if t.count("positive count or 'all'") != 2:
    failures.append(f"invalid arguments (nope, 0) must both teach: {t[-300:]!r}")

t = piped("#help\n#quit\n")
if "#echo" not in t:
    failures.append(f"#help must list #echo: {t[-300:]!r}")
if "hang" not in t:
    failures.append(f"#help's #echo line must carry the all-hangs warning: {t[-300:]!r}")

# --- one cap everywhere [D:echo-cap-100]: the piped echo follows the
# session cap, so #echo moves it and its report tells the truth
t = piped('#echo 3\n[1; 2; 3; 4; 5; 6; 7; 8; 9; 10; 11; 12] |> Seq.map (fun x -> x)\n#quit\n')
if "first 3 of an unforced seq" not in t:
    failures.append(f"the piped echo must follow #echo: {t[-300:]!r}")

# --- the cap at a tty: default 100 covers command-sized output; #echo
# moves it live; all uncaps (the acceptance rides the lines form) ------
def pty_session(lines, settle=0.6, env=None):
    pid, fd = pty.fork()
    if pid == 0:
        if env is None:
            os.execv(WEIR, ["weir"])
        else:
            os.execve(WEIR, ["weir"], env)
    time.sleep(0.8)
    out = b""

    def drain(t):
        nonlocal out
        deadline = time.time() + t
        while time.time() < deadline:
            r, _, _ = select.select([fd], [], [], 0.1)
            if r:
                try:
                    out += os.read(fd, 65536)
                except OSError:
                    return

    segs = []
    for l in lines:
        start = len(out)
        os.write(fd, (l + "\r").encode())
        drain(settle)
        segs.append(re.sub(r"\x1b\[[0-9;]*[A-Za-z]|\x1b=", "", out[start:].decode(errors="replace")))
    os.write(fd, b"\x04")
    deadline = time.time() + 10
    reaped = False
    while time.time() < deadline:
        r, _, _ = select.select([fd], [], [], 0.2)
        if r:
            try:
                out += os.read(fd, 65536)
            except OSError:
                pass
        done, _ = os.waitpid(pid, os.WNOHANG)
        if done:
            reaped = True
            break
    os.close(fd)
    if not reaped:
        os.kill(pid, 9)
        os.waitpid(pid, 0)
        failures.append("the #echo pty session did not exit on ^D")
    return segs

segs = pty_session(
    [
        'let xs = [1..29] |> Seq.map (fun i -> $"l{i}")',
        "#echo 5",
        "xs",
        "#echo all",
        "xs",
    ]
)
# the acceptance: 29 unforced lines fit under the default cap — no
# Seq.freeze, no clip sentence ("l29" is a result spelling; the typed
# line never contains it)
if "l29" not in segs[0] or "first" in segs[0]:
    failures.append(f"29 lines must echo whole under the default cap: {segs[0][-300:]!r}")
if "l5" not in segs[2] or "l6" in segs[2] or "first 5 of an unforced seq" not in segs[2]:
    failures.append(f"#echo 5 must clip the tty echo at 5 with the live-cap sentence: {segs[2][-300:]!r}")
if "l29" not in segs[4] or "first" in segs[4]:
    failures.append(f"#echo all must uncap the tty echo: {segs[4][-300:]!r}")

# --- the type echo [D:repl-types]: off by default at a tty, #types on
# restores the typed echo byte for byte, bare reports, a bad value
# teaches; the config key seeds it and the directive overrides it -----
def types_env(cfg):
    d = tempfile.mkdtemp()
    if cfg is not None:
        os.makedirs(os.path.join(d, "weir"))
        with open(os.path.join(d, "weir", "config.json"), "w") as f:
            f.write(cfg)
    return dict(os.environ, XDG_CONFIG_HOME=d, XDG_STATE_HOME=os.path.join(d, "state"))

types_lines = [
    "2",
    "let x = 5",
    'let ys = [1..3] |> Seq.map (fun i -> $"y{i}") |> Seq.freeze',
    'Seq.initInfinite (fun i -> $"l{i}")',
    'print "a"',
]

def last_lines(seg, n):
    out = [l.strip() for l in seg.replace("\r", "").split("\n")]
    return [l for l in out if l and "weir>" not in l][-n:]

segs = pty_session(["#types"] + types_lines, env=types_env(None))
if "types: off" not in segs[0]:
    failures.append(f"bare #types must report the default off: {segs[0][-200:]!r}")
if last_lines(segs[1], 1) != ["2"]:
    failures.append(f"types off: a bare expression echoes the value alone: {segs[1][-200:]!r}")
if last_lines(segs[2], 1) != ["x = 5"]:
    failures.append(f"types off: a let echoes name = value: {segs[2][-200:]!r}")
if last_lines(segs[3], 2) != ["y3", "ys (frozen)"]:
    failures.append(f"types off: a let's seq meta keeps the name and state, no type: {segs[3][-200:]!r}")
if last_lines(segs[4], 2) != ["l99", "(first 100 of an unforced seq — Seq.freeze to echo everything)"]:
    failures.append(f"types off: a truncated seq keeps its tail alone: {segs[4][-300:]!r}")
if last_lines(segs[5], 1) != ["a"] or "unit" in segs[5]:
    failures.append(f"types off: a unit statement prints nothing after its output: {segs[5][-200:]!r}")

segs = pty_session(["#types on"] + types_lines + ["#types off", "2", "#types maybe", "#types"], env=types_env(None))
if "types: on" not in segs[0]:
    failures.append(f"#types on must set and report: {segs[0][-200:]!r}")
if last_lines(segs[1], 1) != ["2 : int"]:
    failures.append(f"types on: today's bare echo: {segs[1][-200:]!r}")
if last_lines(segs[2], 1) != ["x : int = 5"]:
    failures.append(f"types on: today's let echo: {segs[2][-200:]!r}")
if last_lines(segs[3], 2) != ["y3", "ys : seq<string> (frozen)"]:
    failures.append(f"types on: today's let seq meta: {segs[3][-200:]!r}")
if last_lines(segs[4], 2) != ["l99", ": seq<string> (first 100 of an unforced seq — Seq.freeze to echo everything)"]:
    failures.append(f"types on: today's truncated meta: {segs[4][-300:]!r}")
if last_lines(segs[5], 2) != ["a", "() : unit"]:
    failures.append(f"types on: today's unit echo: {segs[5][-200:]!r}")
if "types: off" not in segs[6] or last_lines(segs[7], 1) != ["2"]:
    failures.append(f"#types off must set, report and drop the type: {segs[6][-200:]!r} {segs[7][-200:]!r}")
if "#types takes 'on' or 'off'" not in segs[8]:
    failures.append(f"#types with a bad value must teach: {segs[8][-200:]!r}")
if "types: off" not in segs[9]:
    failures.append(f"a bad value must leave the setting alone: {segs[9][-200:]!r}")

segs = pty_session(["#types", "2", "#types off", "2"], env=types_env('{"types": "on"}'))
if "types: on" not in segs[0] or last_lines(segs[1], 1) != ["2 : int"]:
    failures.append(f"the config's types key must seed the session: {segs[0][-200:]!r} {segs[1][-200:]!r}")
if last_lines(segs[3], 1) != ["2"]:
    failures.append(f"#types off must override the config: {segs[3][-200:]!r}")

# a config value that is not "on"/"off" teaches and keeps the default
p = subprocess.run([WEIR], input="#types\n#quit\n", capture_output=True, text=True, timeout=30, env=types_env('{"types": true}'))
if 'types takes "on" or "off"; got true (using "off")' not in p.stderr or "types: off" not in p.stdout:
    failures.append(f"a bad config types value must teach and default: {p.stderr[-200:]!r} {p.stdout[-200:]!r}")

# the piped surface keeps its types whatever the setting — its bytes are
# pinned, as with the echo cap [D:echo-cap]
t = piped("2\nlet x = 5\n#types off\n2\n#quit\n")
if "2 : int" not in t or "x : int = 5" not in t or t.count("2 : int") != 2:
    failures.append(f"the piped echo must keep its types under #types off: {t[-300:]!r}")

t = piped("#help\n#quit\n")
if "#types [on | off]" not in t:
    failures.append(f"#help must list #types: {t[-300:]!r}")

# --- the bare statement is the child's own write [D:colour-inherit] ---
# stdout inherits at a tty (isatty true — colour works), so weir has no
# guard on this path: the bytes, NUL included, are the child's choice
# (bash's posture). The value echo keeps its refusal — pinned by the
# [D:binary-echo] cells and the echoBinary units.
segs = pty_session(["sh -c 'printf \"x\\0y\\n\"'"])
if "binary output" in segs[0]:
    failures.append(f"the statement path has no guard to refuse with: {segs[0][-200:]!r}")
if "\x00" not in segs[0]:
    failures.append(f"the child's bytes reach the terminal raw: {segs[0][-200:]!r}")

# --- Tab completion: an empty prompt teaches the directives, not the
# flood [D:empty-prompt-directives]; a constructor is not a statement
# head [D:constructors-not-heads] ---------------------------------------
def pty_tab(prefix, taps=1, settle=0.6):
    # type <prefix> then Tab(s), capture the paint, then ^C + ^D to leave.
    # a set of candidates sharing a prefix extends on the first Tab and
    # lists on the second (readline convention) — taps controls it
    pid, fd = pty.fork()
    if pid == 0:
        os.execv(WEIR, ["weir"])
    time.sleep(0.8)
    out = b""
    if prefix:
        os.write(fd, prefix.encode())
        time.sleep(0.2)
    def drain(t):
        nonlocal out
        deadline = time.time() + t
        while time.time() < deadline:
            r, _, _ = select.select([fd], [], [], 0.1)
            if r:
                try:
                    out += os.read(fd, 65536)
                except OSError:
                    return
    for _ in range(taps):
        os.write(fd, b"\t")
        drain(0.35)
    drain(settle)
    os.write(fd, b"\x03")  # Ctrl+C: abandon the line
    os.write(fd, b"\x04")  # Ctrl+D: leave
    time.sleep(0.3)
    try:
        while True:
            r, _, _ = select.select([fd], [], [], 0.2)
            if not r:
                break
            c = os.read(fd, 65536)
            if not c:
                break
            out += c
    except OSError:
        pass
    try:
        os.close(fd)
    except OSError:
        pass
    os.waitpid(pid, 0)
    return re.sub(r"\x1b\[[0-9;]*[A-Za-z]|\x1b=|\x1b\][^\x07]*\x07", "", out.decode(errors="replace"))

# an empty prompt: the 5 directives share the '#' prefix, so the first
# Tab extends the line to '#' and the second lists the directive names
# (the '#'-slot takes over once '#' is present) — never the 954-PATH
# flood. Both halves prove the fix: the line becomes '#', and the menu
# is the closed directive set.
t = pty_tab("", taps=2)
if "weir> #" not in t:
    failures.append(f"an empty-prompt Tab must extend to '#' (the directives' shared prefix): {t[-400:]!r}")
if "help" not in t or "infer" not in t or "save" not in t:
    failures.append(f"an empty-prompt Tab must offer the session directives: {t[-400:]!r}")

# a continuation line is not an entry's first line: Tab there offers no
# directive, and a typed '#' there completes none [D:repl-directives]
t = pty_tab("let tabx =\r", taps=2)
if re.search(r"\u2026\s+#", t) or re.search(r"\bhelp\s+history\b", t):
    failures.append(f"a continuation-line Tab must not offer directives: {t[-400:]!r}")
t = pty_tab("let tabx =\r#", taps=2)
if re.search(r"\bhelp\s+history\b", t) or re.search(r"\u2026\s+#help", t):
    failures.append(f"a continuation-line '#' must not complete directives: {t[-400:]!r}")

# `Wr` at a head has a single completion and it is not the WriteFile
# constructor — Tab either does nothing visible or completes a function;
# WriteFile must not be the offered head
t = pty_tab("Wr")
if "WriteFile" in t:
    failures.append(f"a constructor (WriteFile) must not complete at a statement head: {t[-300:]!r}")

# --- #history [D:repl-history]: shows the entries with the file path in
# the header (a user cannot cat what they cannot find; `~` never expands),
# bare = all, <n> = the last n. Only the TTY path records (piped input is
# not the user's history), so this must be a pty session; a fresh HOME per
# session makes the count deterministic. A tty records every submitted
# line, the `#history` directive included (bash-style), so the count and
# the tail account for the directive line itself. ---------------------
def pty_history(lines, settle=0.6):
    home = tempfile.mkdtemp(prefix="weir-hist-")
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["HOME"] = home
        # scrub XDG so stateHome resolves under the clean HOME
        for k in ("XDG_STATE_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME"):
            os.environ.pop(k, None)
        os.execv(WEIR, ["weir"])
    time.sleep(0.8)
    out = b""

    def drain(t):
        nonlocal out
        deadline = time.time() + t
        while time.time() < deadline:
            r, _, _ = select.select([fd], [], [], 0.1)
            if r:
                try:
                    out += os.read(fd, 65536)
                except OSError:
                    return

    def clean(b):
        return re.sub(r"\x1b\[[0-9;]*[A-Za-z]|\x1b=", "", b.decode(errors="replace"))

    segs = []
    for l in lines:
        start = len(out)
        os.write(fd, (l + "\r").encode())
        drain(settle)
        # a slow runner answers after the settle: keep reading until the
        # prompt is back after the submitted line (bounded), so a late
        # reply is waited for instead of read as missing
        deadline = time.time() + 10
        while time.time() < deadline and not re.search(r"\n\r?weir> ", clean(out[start:])):
            drain(0.2)
        segs.append(clean(out[start:]))
    os.write(fd, b"\x04")
    time.sleep(0.3)
    try:
        os.close(fd)
    except OSError:
        pass
    os.waitpid(pid, 0)
    return segs


# bare #history: the three lets plus the `#history` line itself (4 entries)
allseg = pty_history(["let ha = 1", "let hb = 2", "let hc = 3", "#history"])[3]
if "history at " not in allseg or "/weir/history" not in allseg:
    failures.append(f"#history must print the file path in its header: {allseg[-300:]!r}")
if "(4 entries)" not in allseg:
    failures.append(f"#history header must count the entries (3 lets + the directive): {allseg[-300:]!r}")
for want in ("let ha = 1", "let hb = 2", "let hc = 3"):
    if want not in allseg:
        failures.append(f"bare #history must dump every entry ({want}): {allseg[-400:]!r}")

# #history 3 is the tail of [ha, hb, hc, "#history 3"]: hb, hc, the
# directive line — the oldest (ha) is excluded
tailseg = pty_history(["let ha = 1", "let hb = 2", "let hc = 3", "#history 3"])[3]
if "let hb = 2" not in tailseg or "let hc = 3" not in tailseg:
    failures.append(f"#history 3 must show the recent entries: {tailseg[-300:]!r}")
if "let ha = 1" in tailseg:
    failures.append(f"#history 3 must NOT show the oldest entry (tail): {tailseg[-300:]!r}")

t = piped("#help\n#quit\n")
if "#history" not in t:
    failures.append(f"#help must list #history: {t[-300:]!r}")
t = piped("#hisory\n#quit\n")
if "did you mean" not in t.lower() or "#history" not in t:
    failures.append(f"a #history typo must did-you-mean it (dispatch reads sessionDirectives): {t[-200:]!r}")

# --- #keys [D:repl-keys]: the editor's bindings, listed by #help near
# the top, plain when piped; a typo did-you-means it ------------------
t = piped("#help\n#quit\n")
if "#keys" not in "\n".join(t.splitlines()[:4]):
    failures.append(f"#help must list #keys in its first lines: {t[:300]!r}")
t = piped("#keys\n#quit\n")
for key in ("Ctrl+Y", "Ctrl+R", "Alt+Enter"):
    if not any(l.strip().startswith(key) for l in t.splitlines()):
        failures.append(f"#keys must list {key} on its own line: {t[-600:]!r}")
if "\x1b" in t:
    failures.append(f"piped #keys must be plain: {t[-300:]!r}")
t = piped("#kyes\n#quit\n")
if "did you mean" not in t.lower() or "#keys" not in t:
    failures.append(f"a #keys typo must did-you-mean it: {t[-200:]!r}")

# --- Ctrl+D still leaves (the pty half) -------------------------------
pid, fd = pty.fork()
if pid == 0:
    os.execv(WEIR, ["weir"])
time.sleep(0.8)
os.write(fd, b"\x04")
deadline = time.time() + 10
reaped = False
while time.time() < deadline:
    r, _, _ = select.select([fd], [], [], 0.2)
    if r:
        try:
            os.read(fd, 4096)
        except OSError:
            pass
    done, _ = os.waitpid(pid, os.WNOHANG)
    if done:
        reaped = True
        break
os.close(fd)
if not reaped:
    os.kill(pid, 9)
    os.waitpid(pid, 0)
    failures.append("Ctrl+D must still leave the REPL")

if failures:
    for f in failures:
        print("repl-directives FAIL:", f)
    sys.exit(1)

print("repl-directives: #help x3 (one source), glance rendering (member + module blurbs), #find fallback (substring/usage/no-match), #quit + Ctrl+D, :q retired, comments no-op, #echo cap (report/set/all/teach, tty live, piped pinned), #types (default off, on restores, report, teach, config seed + override, piped pinned), #history (path header, count, bare-all, tail-n, help-listed, typo did-you-mean), unknown-directive message trimmed (#sig redirect, #schema unknown), #alias recognized (list/add/single-hop/help), #keys (help-listed, Ctrl+Y/Ctrl+R/Alt+Enter, piped plain, typo did-you-mean), empty-prompt Tab offers directives (continuation lines none), constructor not a head")
