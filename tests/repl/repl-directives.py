#!/usr/bin/env python3
# Session directives [D:repl-directives]: '#' is the prefix for
# everything addressed to the tooling — #help's three forms from the
# one hover source, #quit (Ctrl+D still works), and comment-only
# lines as silent no-ops. The :q teaching arm retired 2026-08-14.
import os
import pty
import re
import select
import subprocess
import sys
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

# a name the CHECKER would refuse must not get a confident hover: #help and
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

# --- #quit quits; :q is fully retired (user 2026-08-14) ---------------
t = piped("#quit\nprint \"unreached\"\n")
if "unreached" in t:
    failures.append("#quit must leave the REPL")

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

# --- a FILE directive at the REPL gets its own redirect, not the flood
t = piped("#sig\n#quit\n")
if "file directive" not in t or "no effect in the REPL" not in t:
    failures.append(f"#sig at the REPL must redirect (file directive): {t[-200:]!r}")
t = piped("#schema\n#quit\n")
if "file directive" not in t:
    failures.append(f"#schema at the REPL must redirect (file directive): {t[-200:]!r}")

# --- #alias [D:command-head-alias] is a RECOGNIZED directive ----------
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

# --- piped bytes are UNMOVED by the session cap [D:echo-cap]: the piped
# echo keeps its pinned constant even after #echo changes the session's
t = piped('#echo 3\n[1; 2; 3; 4; 5; 6; 7; 8; 9; 10; 11; 12] |> Seq.map (fun x -> x)\n#quit\n')
if "first 10 of an unforced seq" not in t:
    failures.append(f"the piped echo must keep its pinned cap of 10: {t[-300:]!r}")

# --- the cap at a tty: default 100 covers command-sized output; #echo
# moves it live; all uncaps (the acceptance rides the lines form) ------
def pty_session(lines, settle=0.6):
    pid, fd = pty.fork()
    if pid == 0:
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
# Seq.force, no clip sentence ("l29" is a RESULT spelling; the typed
# line never contains it)
if "l29" not in segs[0] or "first" in segs[0]:
    failures.append(f"29 lines must echo whole under the default cap: {segs[0][-300:]!r}")
if "l5" not in segs[2] or "l6" in segs[2] or "first 5 of an unforced seq" not in segs[2]:
    failures.append(f"#echo 5 must clip the tty echo at 5 with the live-cap sentence: {segs[2][-300:]!r}")
if "l29" not in segs[4] or "first" in segs[4]:
    failures.append(f"#echo all must uncap the tty echo: {segs[4][-300:]!r}")

# --- the bare statement is the CHILD's own write [D:colour-inherit] ---
# stdout inherits at a tty (isatty true — colour works), so weir has no
# guard on this path: the bytes, NUL included, are the child's choice
# (bash's posture). The VALUE echo keeps its refusal — pinned by the
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
    # LISTS on the second (readline convention) — taps controls it
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

# `Wr` at a head has a single completion and it is NOT the WriteFile
# constructor — Tab either does nothing visible or completes a function;
# WriteFile must not be the offered head
t = pty_tab("Wr")
if "WriteFile" in t:
    failures.append(f"a constructor (WriteFile) must not complete at a statement head: {t[-300:]!r}")

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

print("repl-directives: #help x3 (one source), glance rendering (member + module blurbs), #find fallback (substring/usage/no-match), #quit + Ctrl+D, :q retired, comments no-op, #echo cap (report/set/all/teach, tty live, piped pinned), unknown-directive message trimmed (#sig/#schema redirect), #alias recognized (list/add/single-hop/help), empty-prompt Tab offers directives, constructor not a head")
